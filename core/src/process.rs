//! 프로세스 오케스트레이션 (process/{deploy,update,launch,uninstall,reinstall,
//! clean_cache,query,pull}_process.py 대응)

use crate::cleancache;
use crate::config::{Paths, ProcessKind, Request};
use crate::constants::PLATFORM_WINDOWS;
use crate::desktop;
use crate::events::{Event, Ui};
use crate::i18n::{tr, trf};
use crate::manifest::{AppManifest, PackageManifest, PackageRef};
use crate::net::{Net, NetError};
use crate::precheck;
use crate::registry::Registry;
use crate::tasks::{self, Ctx};
use crate::wine::Wine;
use std::collections::{HashSet, VecDeque};
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::Mutex;
use std::time::Duration;

pub struct Outcome {
    pub ok: bool,
    pub message: String,
}

#[derive(Debug)]
pub enum Fail {
    Cancelled,
    Error { what: String, detail: String, todo: String },
}

impl From<NetError> for Fail {
    fn from(e: NetError) -> Self {
        match e {
            NetError::Cancelled => Fail::Cancelled,
            other => Fail::Error {
                what: tr("Network failure.  Unable to recover."),
                detail: other.to_string(),
                todo: tr("Make sure your internet is working normally or switch to a more stable connection, then run the installer again."),
            },
        }
    }
}

fn err(what: &str, detail: impl ToString) -> Fail {
    Fail::Error {
        what: tr(what),
        detail: detail.to_string(),
        todo: trf("If Fusion still does not install, collect the log files and contact us through [[{0}]] for help.", &[])
            .replace("[[{0}]]", "Fusion Support Forum"),
    }
}

pub struct Runner {
    pub req: Request,
    pub paths: Paths,
    pub ui: Ui,
    pub net: Net,
    pub log_file: PathBuf,
}

/// 동시에 하나의 설치기만 실행 (원본: "An installation is already in progress")
struct InstanceLock(#[allow(dead_code)] fs::File);

fn lock(paths: &Paths) -> Option<InstanceLock> {
    let f = fs::OpenOptions::new().create(true).write(true).truncate(false).open(paths.data.join("streamer.lock")).ok()?;
    use std::os::unix::io::AsRawFd;
    // SAFETY: 유효한 fd 에 대한 flock
    let r = unsafe { libc::flock(f.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) };
    (r == 0).then_some(InstanceLock(f))
}

impl Runner {
    pub fn new(req: Request, ui: Ui, log_file: PathBuf) -> Runner {
        let paths = Paths::new(&req);
        let net = Net::new(ui.clone());
        Runner { req, paths, ui, net, log_file }
    }

    fn manifest_url(&self, app_id: &str) -> String {
        format!("{}{}/{}/full.json", self.req.root_url(), PLATFORM_WINDOWS, app_id)
    }

    pub fn run(&self) -> Outcome {
        if let Err(e) = self.paths.ensure() {
            return self.fail(err("Access to install directories failed.", e));
        }
        let needs_lock = !matches!(self.req.process, ProcessKind::Launch | ProcessKind::Query);
        let _lock = if needs_lock {
            match lock(&self.paths) {
                Some(l) => Some(l),
                None => {
                    let ev = Event::InformUser {
                        title: tr("Multiple installers"),
                        body: tr("You have multiple instances of the Fusion installer running at the same time."),
                        primary: tr("OK"),
                        link: None,
                    };
                    if !self.req.quiet {
                        self.ui.send(ev);
                    }
                    return self.done(false, tr("An installation is already in progress."));
                }
            }
        } else {
            None
        };
        let r = match self.req.process {
            ProcessKind::Deploy | ProcessKind::Update => self.deploy(),
            ProcessKind::Launch => self.launch().map(|_| tr("Starting Autodesk Fusion")),
            ProcessKind::Uninstall => self.uninstall(false),
            ProcessKind::Reinstall => self.reinstall(),
            ProcessKind::Cleancache => self.clean_cache(),
            ProcessKind::Query => self.query(),
            ProcessKind::Pull => self.pull(),
        };
        match r {
            Ok(msg) => self.done(true, msg),
            Err(f) => self.fail(f),
        }
    }

    fn done(&self, ok: bool, message: String) -> Outcome {
        self.ui.send(Event::Complete { ok, message: message.clone() });
        Outcome { ok, message }
    }

    fn fail(&self, f: Fail) -> Outcome {
        match f {
            Fail::Cancelled => self.done(false, "cancelled".into()),
            Fail::Error { what, detail, todo } => {
                log::error!("{what}: {detail}");
                if !self.req.quiet {
                    let title = match self.req.process {
                        ProcessKind::Uninstall => tr("Fusion can't be uninstalled."),
                        _ => tr("Problem installing Fusion"),
                    };
                    self.ui.send(Event::ReportError {
                        title,
                        what_label: tr("What's wrong?"),
                        what: what.clone(),
                        detail: detail.clone(),
                        todo_label: tr("What should I do?"),
                        todo,
                        log_dir: self.paths.logs.to_string_lossy().into_owned(),
                        log_file: self.log_file.to_string_lossy().into_owned(),
                        primary: tr("OK"),
                        secondary: tr("Open Log File Location"),
                    });
                }
                self.done(false, format!("{what}: {detail}"))
            }
        }
    }

    fn check_cancel(&self) -> Result<(), Fail> {
        if self.ui.cancelled() {
            Err(Fail::Cancelled)
        } else {
            Ok(())
        }
    }

    fn registry(&self) -> Result<Registry, Fail> {
        Registry::open(&self.paths.registry_db).map_err(|e| err("Install registry is not recognized", e))
    }

    fn wine(&self) -> Option<Wine> {
        Wine::locate(&self.paths, &self.req.wine)
    }

    // ------------------------------------------------------------------ prechecks
    fn prechecks(&self) -> Result<(), Fail> {
        loop {
            self.ui.progress(1, 5, &tr("Running pre-checks"), 0.0);
            let fails = precheck::run(&self.paths, &self.net, &self.req.root_url(), &self.req.wine.wine_bin);
            if fails.is_empty() {
                return Ok(());
            }
            if self.req.quiet {
                let d: Vec<String> = fails.iter().map(|f| f.item.description.clone()).collect();
                return Err(err("Unable to Install", d.join("\n")));
            }
            let r = self.ui.send(Event::PrecheckFailure {
                paragraph: tr("The installer could not continue because of the following issues:"),
                items: fails.into_iter().map(|f| f.item).collect(),
                hyperlink: tr("Learn more.."),
                btn_retest: tr("Re-Test"),
                btn_cancel: tr("Cancel"),
            });
            if r != "retest" {
                return Err(Fail::Cancelled);
            }
        }
    }

    // ------------------------------------------------------------------ manifests
    fn fetch_app(&self, app_id: &str) -> Result<(AppManifest, Option<String>), Fail> {
        let url = self.manifest_url(app_id);
        let (raw, etag) = self.net.get_with_etag(&url)?;
        let m = AppManifest::parse(&raw).map_err(|e| err("Outdated installer", e))?;
        log::info!("Application {} version {}", m.display_name(), m.version());
        Ok((m, etag))
    }

    fn fetch_packages(&self, refs: &[PackageRef]) -> Result<Vec<PackageManifest>, Fail> {
        let root = self.req.root_url();
        let queue = Mutex::new(refs.iter().cloned().enumerate().collect::<VecDeque<_>>());
        let out: Mutex<Vec<(usize, PackageManifest)>> = Mutex::new(vec![]);
        let error: Mutex<Option<Fail>> = Mutex::new(None);
        let done = AtomicUsize::new(0);
        std::thread::scope(|s| {
            for _ in 0..self.req.threads.clamp(1, 16) {
                s.spawn(|| loop {
                    let Some((i, r)) = queue.lock().unwrap().pop_front() else { break };
                    if error.lock().unwrap().is_some() {
                        break;
                    }
                    let res = self
                        .net
                        .get_string(&format!("{root}packages/{}.json", r.checksum))
                        .map_err(Fail::from)
                        .and_then(|raw| PackageManifest::parse(&r.checksum, &raw).map_err(|e| err("Missing files", e)));
                    match res {
                        Ok(m) => out.lock().unwrap().push((i, m)),
                        Err(e) => {
                            *error.lock().unwrap() = Some(e);
                            break;
                        }
                    }
                    let n = done.fetch_add(1, Ordering::Relaxed) + 1;
                    self.ui.progress(2, 5, &tr("Setting Up"), 1.0 + 3.0 * n as f64 / refs.len().max(1) as f64);
                });
            }
        });
        if let Some(e) = error.into_inner().unwrap() {
            return Err(e);
        }
        let mut v = out.into_inner().unwrap();
        v.sort_by_key(|(i, _)| *i);
        Ok(v.into_iter().map(|(_, m)| m).collect())
    }

    // ------------------------------------------------------------------ deploy / update
    fn installed_ok(&self, reg: &Registry, app: &AppManifest) -> bool {
        let Some(inst) = reg.application(&self.req.app_id, &self.req.stream) else { return false };
        let Ok(old) = AppManifest::parse(&inst.manifest) else { return false };
        old.release_version == app.release_version && self.installed_ctx_exe(reg).map(|p| p.is_file()).unwrap_or(false)
    }

    fn deploy(&self) -> Result<String, Fail> {
        self.ui.progress(0, 5, &tr("Initializing..."), 0.0);
        self.prechecks()?;
        self.check_cancel()?;
        self.ui.progress(2, 5, &tr("Setting Up"), 1.0);
        let (app, etag) = self.fetch_app(&self.req.app_id)?;
        let mut reg = self.registry()?;

        if self.installed_ok(&reg, &app) {
            log::info!("No need to update. {} installed", app.version());
            if self.req.process == ProcessKind::Deploy && !self.req.quiet {
                let r = self.ui.send(Event::PromptUser {
                    title: tr("Latest version already installed"),
                    body: trf("You have the latest version of {}.\n\nWould you like to start the application?", &[&app.display_name()]),
                    primary: tr("Yes"),
                    secondary: tr("No"),
                });
                if r == "Yes" {
                    self.launch()?;
                }
            } else if self.req.process == ProcessKind::Update && !self.req.no_auto_launch && !self.req.quiet {
                self.launch()?;
            }
            return Ok(trf("No need to update. {} installed", &[&app.version()]));
        }

        // sub-applications (--full-deploy)
        let mut refs = app.packages.clone();
        if self.req.full_deploy {
            for sub in app.sub_applications() {
                match self.fetch_app(&sub) {
                    Ok((m, _)) => refs.extend(m.packages),
                    Err(_) => log::info!("sub-application {sub} has no streamable manifest; skipping"),
                }
            }
        }
        let mut seen = HashSet::new();
        refs.retain(|r| seen.insert(r.checksum.clone()));

        let packages = self.fetch_packages(&refs)?;
        self.check_cancel()?;

        // Wine prefix
        self.ui.progress(3, 5, &tr("Preparing Wine environment"), 4.0);
        let wine = self.wine().ok_or_else(|| err("Operating system is not supported", tr("Wine is not installed or too old (9.0 or later is required).")))?;
        wine.init_prefix().map_err(|e| err("Unable to Install", e))?;
        if self.req.wine.dxvk {
            if let Err(e) = wine.install_dxvk(&self.net, &self.paths.cache) {
                log::warn!("DXVK installation skipped: {e}");
            }
        }
        self.check_cancel()?;

        let manifest_url = self.manifest_url(&self.req.app_id);
        {
            // pre-install-tasks (INITIAL_INSTALL_DELETE_FILES 등)
            let ctx = Ctx::new(&self.paths, &self.req, Some(&wine), &app, &packages, manifest_url.clone(), etag.clone());
            if !reg.application(&self.req.app_id, &self.req.stream).is_some() {
                tasks::initial_install_delete(&ctx);
            }
        }

        // 다운로드 + 설치
        self.install_packages(&mut reg, &refs, &packages)?;
        self.check_cancel()?;

        // post-deploy tasks
        self.ui.progress(4, 5, &tr("Configuring Autodesk account sign-in"), 96.0);
        let ctx = Ctx::new(&self.paths, &self.req, Some(&wine), &app, &packages, manifest_url, etag);
        for p in &packages {
            tasks::run_post_deploy(&ctx, p);
        }
        self.ui.progress(4, 5, &tr("Creating launchers"), 98.0);
        if let Err(e) = tasks::finish_install(&ctx) {
            log::warn!("desktop integration: {e}");
        }
        let all: Vec<String> = refs.iter().map(|r| r.checksum.clone()).collect();
        reg.record_application(&self.req.app_id, &self.req.stream, &app.release_version, &app.raw, &all)
            .map_err(|e| err("Install registry is not recognized", e))?;
        reg.set_stream(&self.req.stream, &self.req.root_url()).ok();

        self.ui.progress(5, 5, &tr("Finishing installation"), 99.0);
        self.cleanup_old(&mut reg, &packages);
        wine.wait_server();
        self.ui.progress(5, 5, &tr("Installation complete"), 100.0);

        if !self.req.no_auto_launch && !self.req.quiet {
            if let Some(exe) = ctx.main_exe() {
                self.spawn_fusion(&wine, &exe, &[])?;
            }
        }
        Ok(format!("{} {}", app.display_name(), app.version()))
    }

    fn install_packages(&self, reg: &mut Registry, refs: &[PackageRef], packages: &[PackageManifest]) -> Result<(), Fail> {
        struct Job {
            idx: usize,
            archive: String,
            size: u64,
        }
        let stream = self.req.stream.clone();
        // 이미 설치된(같은 체크섬) 패키지는 건너뜀 – 패치 대신 변경된 패키지만 전체 다운로드
        let mut jobs = VecDeque::new();
        let mut remaining_per_pkg = vec![0usize; packages.len()];
        for (i, (r, p)) in refs.iter().zip(packages).enumerate() {
            let dest_ok = p.destination().map(|d| self.paths.destination(d).exists()).unwrap_or(true);
            if reg.has_package(&r.checksum, &stream) && dest_ok {
                continue;
            }
            let archives = p.archives(r.compressed_size);
            let per = if archives.is_empty() { 0 } else { r.compressed_size / archives.len() as u64 };
            remaining_per_pkg[i] = archives.len();
            for a in archives {
                jobs.push_back(Job { idx: i, archive: a, size: per });
            }
            if remaining_per_pkg[i] == 0 {
                // 내용 없는 패키지(디렉터리/특수 동작 전용)
                if let Some(d) = p.destination() {
                    let root = self.paths.destination(d);
                    p.walk(&root, &mut |dir, _| {
                        let _ = fs::create_dir_all(dir);
                    }, &mut |_, _| {});
                }
                reg.record_package(&r.checksum, &stream, r.size, &p.raw, &[]).map_err(|e| err("Install registry is not recognized", e))?;
            }
        }
        let total: u64 = jobs.iter().map(|j| j.size).sum::<u64>().max(1);
        let n_jobs = jobs.len();
        log::info!("{} archives to install ({} bytes compressed)", n_jobs, total);
        if n_jobs == 0 {
            return Ok(());
        }

        let pkg_cache = self.paths.cache.join("packages");
        fs::create_dir_all(&pkg_cache).map_err(|e| err("Access to install directories failed.", e))?;
        let queue = Mutex::new(jobs);
        let downloaded = AtomicU64::new(0);
        let extracted = AtomicU64::new(0);
        let error: Mutex<Option<Fail>> = Mutex::new(None);
        let files_per_pkg: Vec<Mutex<Vec<String>>> = (0..packages.len()).map(|_| Mutex::new(vec![])).collect();
        let remaining: Vec<AtomicUsize> = remaining_per_pkg.iter().map(|n| AtomicUsize::new(*n)).collect();
        let completed: Mutex<Vec<usize>> = Mutex::new(vec![]);
        let finished = std::sync::atomic::AtomicBool::new(false);
        let root = self.req.root_url();

        std::thread::scope(|s| {
            // 진행률 보고 스레드 (원본: 다운로드 5~85%, 설치 ~95%)
            s.spawn(|| {
                while !finished.load(Ordering::Relaxed) {
                    let d = downloaded.load(Ordering::Relaxed).min(total) as f64 / total as f64;
                    let x = extracted.load(Ordering::Relaxed).min(total) as f64 / total as f64;
                    let pct = 5.0 + 90.0 * (0.7 * d + 0.3 * x);
                    let desc = if d < 0.999 {
                        format!(
                            "{}  ({:.0} / {:.0} MB)",
                            tr("Downloading Autodesk Fusion"),
                            downloaded.load(Ordering::Relaxed) as f64 / 1048576.0,
                            total as f64 / 1048576.0
                        )
                    } else {
                        tr("Installing Autodesk Fusion")
                    };
                    self.ui.progress(3, 5, &desc, pct);
                    std::thread::sleep(Duration::from_millis(400));
                }
            });
            let workers: Vec<_> = (0..self.req.threads.clamp(1, 16))
                .map(|_| {
                    s.spawn(|| loop {
                        if self.ui.cancelled() || error.lock().unwrap().is_some() {
                            break;
                        }
                        let Some(job) = queue.lock().unwrap().pop_front() else { break };
                        let pkg = &packages[job.idx];
                        let url = format!("{root}packages/{}.tar.xz", job.archive);
                        let file = pkg_cache.join(format!("{}.tar.xz", job.archive));
                        let res = self
                            .net
                            .download_verified(&url, &file, &job.archive, &downloaded)
                            .map_err(Fail::from)
                            .and_then(|_| {
                                let dest = pkg.destination().map(|d| self.paths.destination(d)).ok_or_else(|| err("Missing files", "package has no destination"))?;
                                let files = extract(&file, &dest).map_err(|e| err("Unable to Install", format!("{}: {e}", file.display())))?;
                                files_per_pkg[job.idx].lock().unwrap().extend(files);
                                if std::env::var_os("OFC_KEEP_PACKAGES").is_none() {
                                    let _ = fs::remove_file(&file);
                                }
                                Ok(())
                            });
                        match res {
                            Ok(()) => {
                                extracted.fetch_add(job.size, Ordering::Relaxed);
                                if remaining[job.idx].fetch_sub(1, Ordering::SeqCst) == 1 {
                                    completed.lock().unwrap().push(job.idx);
                                }
                            }
                            Err(e) => {
                                let mut g = error.lock().unwrap();
                                if g.is_none() {
                                    *g = Some(e);
                                }
                                break;
                            }
                        }
                    })
                })
                .collect();
            for w in workers {
                let _ = w.join();
            }
            finished.store(true, Ordering::Relaxed);
        });

        // 완료된 패키지는 실패 여부와 무관하게 기록 → 재시도 시 이어서 설치
        for idx in completed.into_inner().unwrap() {
            let r = &refs[idx];
            let files = std::mem::take(&mut *files_per_pkg[idx].lock().unwrap());
            reg.record_package(&r.checksum, &stream, r.size, &packages[idx].raw, &files).map_err(|e| err("Install registry is not recognized", e))?;
        }
        if let Some(e) = error.into_inner().unwrap() {
            return Err(e);
        }
        self.check_cancel()
    }

    /// 업데이트 후 이전 버전 정리 (--no_cleanup 이 아니면)
    fn cleanup_old(&self, reg: &mut Registry, current: &[PackageManifest]) {
        let keep_dests: HashSet<PathBuf> = current.iter().filter_map(|p| p.destination()).map(|d| self.paths.destination(d)).collect();
        for sum in reg.orphan_packages(&self.req.stream) {
            if let Some(raw) = reg.package_manifest(&sum, &self.req.stream) {
                if let Ok(p) = PackageManifest::parse(&sum, &raw) {
                    if let Some(d) = p.destination() {
                        let dir = self.paths.destination(d);
                        if !keep_dests.contains(&dir) && dir.starts_with(self.paths.stream_dir()) {
                            log::info!("removing old version directory {}", dir.display());
                            let _ = fs::remove_dir_all(&dir);
                        }
                    }
                }
            }
            let _ = reg.remove_package(&sum, &self.req.stream);
        }
    }

    // ------------------------------------------------------------------ launch
    fn installed_packages(&self, reg: &Registry) -> Option<(AppManifest, Vec<PackageManifest>)> {
        let inst = reg.application(&self.req.app_id, &self.req.stream)?;
        let app = AppManifest::parse(&inst.manifest).ok()?;
        let pkgs = app
            .packages
            .iter()
            .filter_map(|r| reg.package_manifest(&r.checksum, &self.req.stream).and_then(|raw| PackageManifest::parse(&r.checksum, &raw).ok()))
            .collect();
        Some((app, pkgs))
    }

    fn installed_ctx_exe(&self, reg: &Registry) -> Option<PathBuf> {
        let (app, pkgs) = self.installed_packages(reg)?;
        let ctx = Ctx::new(&self.paths, &self.req, None, &app, &pkgs, String::new(), None);
        ctx.main_exe()
    }

    pub fn installed_exe(&self, file_id: Option<&str>) -> Option<PathBuf> {
        let reg = Registry::open(&self.paths.registry_db).ok()?;
        let (app, pkgs) = self.installed_packages(&reg)?;
        let ctx = Ctx::new(&self.paths, &self.req, None, &app, &pkgs, String::new(), None);
        match file_id {
            None => ctx.main_exe(),
            Some(id) => ctx.idmap.get(id).cloned(),
        }
    }

    fn spawn_fusion(&self, wine: &Wine, exe: &Path, args: &[String]) -> Result<(), Fail> {
        self.ui.progress(5, 5, &tr("Starting Autodesk Fusion"), 100.0);
        let log = self.paths.logs.join("fusion-wine.log");
        wine.spawn_detached(exe, args, Some(&log)).map_err(|e| err("Fusion didn't launch for unknown reasons.", e))
    }

    pub fn launch(&self) -> Result<(), Fail> {
        let exe = self.installed_exe(None).ok_or_else(|| err("Could not find an application to launch", tr("Autodesk Fusion is not installed.")))?;
        let wine = self.wine().ok_or_else(|| err("Operating system is not supported", tr("Wine is not installed or too old (9.0 or later is required).")))?;
        self.spawn_fusion(&wine, &exe, &self.req.launch_args)
    }

    /// 실행 전 업데이트 확인 (FusionLauncher 의 업데이트 확인 대체). 새 버전이면 그 버전 문자열.
    pub fn check_update(&self) -> Option<String> {
        let reg = Registry::open(&self.paths.registry_db).ok()?;
        let inst = reg.application(&self.req.app_id, &self.req.stream)?;
        let old = AppManifest::parse(&inst.manifest).ok()?;
        let new = AppManifest::parse(&self.net.get_quick(&self.manifest_url(&self.req.app_id))?).ok()?;
        (new.release_version != old.release_version).then(|| new.version())
    }

    // ------------------------------------------------------------------ uninstall
    fn uninstall(&self, silent: bool) -> Result<String, Fail> {
        let mut reg = self.registry()?;
        let installed = reg.application(&self.req.app_id, &self.req.stream);
        if !silent && !self.req.silent && !self.req.quiet {
            let r = self.ui.send(Event::AbortUninstall {
                title: tr("We are sorry to see you go."),
                continue_label: tr("Uninstall"),
                cancel_label: tr("Cancel"),
            });
            if r != "Yes" {
                return Err(Fail::Cancelled);
            }
        }
        self.ui.progress(1, 3, &tr("Uninstalling"), 5.0);
        if let Some(w) = self.wine() {
            if self.paths.prefix.exists() {
                w.kill_all();
            }
        }
        let pkgs = installed.as_ref().and_then(|_| self.installed_packages(&reg)).map(|(_, p)| p).unwrap_or_default();
        let n = pkgs.len().max(1);
        for (i, p) in pkgs.iter().enumerate() {
            self.check_cancel()?;
            for f in reg.package_files(&p.checksum, &self.req.stream) {
                let _ = fs::remove_file(&f);
            }
            if let Some(d) = p.destination() {
                let dir = self.paths.destination(d);
                if dir.starts_with(self.paths.stream_dir()) {
                    let _ = fs::remove_dir_all(&dir);
                }
            }
            let _ = reg.remove_package(&p.checksum, &self.req.stream);
            self.ui.progress(2, 3, &tr("Removing files"), 5.0 + 85.0 * (i + 1) as f64 / n as f64);
        }
        let _ = reg.remove_application(&self.req.app_id, &self.req.stream);
        let _ = fs::remove_dir_all(self.paths.stream_dir());
        desktop::remove_all(&self.paths);
        if self.req.purge || self.req.deep_clean {
            // Wine prefix 까지 모두 제거 (로그인 정보·캐시 포함)
            drop(reg);
            let _ = fs::remove_dir_all(&self.paths.prefix);
            let _ = fs::remove_file(&self.paths.registry_db);
            let _ = fs::remove_dir_all(&self.paths.cache);
        }
        self.ui.progress(3, 3, &tr("Uninstall complete"), 100.0);
        Ok(tr("Uninstall complete"))
    }

    fn reinstall(&self) -> Result<String, Fail> {
        self.uninstall(true)?;
        if self.req.deep_clean {
            let _ = self.clean_cache_inner(true, true, true);
        }
        self.deploy()
    }

    // ------------------------------------------------------------------ clean cache / query / pull
    fn clean_cache_inner(&self, all: bool, data: bool, cred: bool) -> Result<usize, Fail> {
        cleancache::run(&self.paths, &cleancache::Options { force_all: all, remove_user_data: data, remove_user_cred: cred })
            .map_err(|e| err("Unable to Install", e))
    }

    fn clean_cache(&self) -> Result<String, Fail> {
        self.ui.progress(1, 2, &tr("Cleaning cache"), 30.0);
        if let Some(w) = self.wine() {
            if self.paths.prefix.exists() {
                w.kill_all();
            }
        }
        let n = self.clean_cache_inner(self.req.cleancacheall, self.req.removeuserdata, self.req.removeusercred)?;
        self.ui.progress(2, 2, &tr("Clean cache complete"), 100.0);
        Ok(format!("{} ({n})", tr("Clean cache complete")))
    }

    fn query(&self) -> Result<String, Fail> {
        let reg = self.registry()?;
        let apps: Vec<serde_json::Value> = reg
            .applications()
            .into_iter()
            .map(|a| {
                let m = AppManifest::parse(&a.manifest).ok();
                serde_json::json!({
                    "app_id": a.uuid, "stream": a.stream, "release": a.version,
                    "name": m.as_ref().map(|m| m.display_name()),
                    "build": m.as_ref().map(|m| m.build_version.clone()),
                    "exe": self.installed_exe(None),
                    "prefix": self.paths.prefix,
                })
            })
            .collect();
        Ok(serde_json::to_string_pretty(&apps).unwrap_or_default())
    }

    fn pull(&self) -> Result<String, Fail> {
        let dir = PathBuf::from(self.req.pull_dir.clone().unwrap_or_else(|| self.paths.cache.join("mirror").to_string_lossy().into_owned()));
        fs::create_dir_all(dir.join("packages")).map_err(|e| err("Access to install directories failed.", e))?;
        self.ui.progress(1, 3, &tr("Pulling Installation Mirror"), 1.0);
        let (app, _) = self.fetch_app(&self.req.app_id)?;
        let packages = self.fetch_packages(&app.packages)?;
        let app_dir = dir.join(PLATFORM_WINDOWS).join(&self.req.app_id);
        fs::create_dir_all(&app_dir).map_err(|e| err("Access to install directories failed.", e))?;
        fs::write(app_dir.join("full.json"), &app.raw).map_err(|e| err("Access to install directories failed.", e))?;
        let counter = AtomicU64::new(0);
        let total: u64 = app.packages.iter().map(|p| p.compressed_size).sum::<u64>().max(1);
        let root = self.req.root_url();
        for (r, p) in app.packages.iter().zip(&packages) {
            fs::write(dir.join("packages").join(format!("{}.json", r.checksum)), &p.raw).ok();
            for a in p.archives(r.compressed_size) {
                self.net.download_verified(&format!("{root}packages/{a}.tar.xz"), &dir.join("packages").join(format!("{a}.tar.xz")), &a, &counter)?;
                let pct = 5.0 + 95.0 * counter.load(Ordering::Relaxed) as f64 / total as f64;
                self.ui.progress(2, 3, &tr("Pulling Installation Mirror"), pct);
            }
        }
        Ok(format!("mirror: {} (use --conn file://{}/)", dir.display(), dir.display()))
    }
}

/// tar.xz 를 대상 디렉터리에 풀고 기록할 파일 경로 목록을 반환.
/// `unpack_in` 은 `..`/절대 경로 항목을 거부한다(원본 tarfile filter='data' 와 동일한 안전성).
fn extract(archive: &Path, dest: &Path) -> std::io::Result<Vec<String>> {
    fs::create_dir_all(dest)?;
    let f = fs::File::open(archive)?;
    let xz = xz2::read::XzDecoder::new_multi_decoder(std::io::BufReader::with_capacity(1 << 20, f));
    let mut ar = tar::Archive::new(xz);
    ar.set_preserve_permissions(false);
    ar.set_overwrite(true);
    let mut files = vec![];
    for entry in ar.entries()? {
        let mut entry = entry?;
        let rel = entry.path()?.into_owned();
        let target = dest.join(&rel);
        if entry.header().entry_type().is_file() && target.is_file() {
            // 실행 중이거나 하드링크된 이전 파일을 덮어쓰지 않도록 먼저 제거
            let _ = fs::remove_file(&target);
        }
        if entry.unpack_in(dest)? && entry.header().entry_type().is_file() {
            files.push(target.to_string_lossy().into_owned());
        }
    }
    // 남은 스트림 소비 (멀티 스트림 xz 패딩)
    let mut sink = [0u8; 4096];
    let mut inner = ar.into_inner();
    while inner.read(&mut sink)? > 0 {}
    Ok(files)
}
