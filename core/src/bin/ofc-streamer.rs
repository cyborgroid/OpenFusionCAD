//! ofc-streamer – 원본 streamer.exe 와 같은 명령행 인터페이스의 헤드리스/CLI 실행기.
//!
//!   ofc-streamer -p deploy -a 73e72ada57b7480280f7a6f4a289729f -s production -c https://dl.appstreaming.autodesk.com/production/
//!   ofc-streamer launch [fusion360://...] [-- 추가 인자]
//!   ofc-streamer handle-uri adskidmgr:/login?code=...     (Autodesk 계정 로그인 콜백)

use clap::{Parser, Subcommand};
use ofc_core::config::{ProcessKind, Request, WineOptions};
use ofc_core::constants::{self, FUSION_URL_SCHEME, IDENTITY_URL_SCHEME};
use ofc_core::events::{Event, Sink};
use std::io::{BufRead, IsTerminal, Write};
use std::path::PathBuf;
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

#[derive(Parser, Debug)]
#[command(name = "ofc-streamer", version, about = "Deploys and updates Autodesk Fusion on Linux (Wine)")]
struct Cli {
    #[command(subcommand)]
    cmd: Option<Cmd>,
    /// deploy | update | launch | uninstall | reinstall | cleancache | query | pull
    #[arg(short = 'p', long = "process", default_value = "deploy")]
    process: String,
    #[arg(short = 'a', long = "appid", default_value = constants::FUSION_APP_ID)]
    app_id: String,
    #[arg(short = 's', long = "stream", default_value = constants::DEFAULT_STREAM)]
    stream: String,
    /// 매니페스트 루트 URL (-s 보다 우선)
    #[arg(short = 'c', long = "conn")]
    conn: Option<String>,
    #[arg(long = "lang")]
    lang: Option<String>,
    #[arg(long = "threadscount", default_value_t = 5)]
    threads: usize,
    #[arg(long = "no-auto-launch", visible_alias = "nal")]
    no_auto_launch: bool,
    #[arg(long = "quiet")]
    quiet: bool,
    #[arg(long = "full-deploy")]
    full_deploy: bool,
    #[arg(long = "installlocation")]
    install_location: Option<String>,
    #[arg(long = "resources")]
    resources: Option<String>,
    #[arg(short = 'r', long = "purge-incomplete")]
    purge: bool,
    #[arg(long = "silent")]
    silent: bool,
    #[arg(long = "cleancacheall")]
    cleancacheall: bool,
    #[arg(long = "removeuserdata")]
    removeuserdata: bool,
    #[arg(long = "removeusercred")]
    removeusercred: bool,
    #[arg(long = "deep_clean")]
    deep_clean: bool,
    #[arg(long = "pull-dir")]
    pull_dir: Option<String>,
    /// 사용할 wine 실행 파일
    #[arg(long = "wine", default_value = "wine")]
    wine: String,
    /// DXVK 설치 생략 (Wine 기본 wined3d 사용)
    #[arg(long = "no-dxvk")]
    no_dxvk: bool,
    /// Microsoft WebView2 런타임 설치 (기본: 제외)
    #[arg(long = "with-webview2")]
    with_webview2: bool,
    #[arg(long = "no-desktop-shortcut")]
    no_desktop_shortcut: bool,
    /// 로그를 표준 오류로도 출력
    #[arg(short = 'v', long = "verbose")]
    verbose: bool,
    /// 원본 호환용으로 받아들이고 무시하는 인자 (-g, --ppid 등)
    #[arg(short = 'g', long = "globalinstall", hide = true)]
    _global: bool,
    #[arg(long = "ppid", hide = true)]
    _ppid: Option<String>,
    #[arg(long = "install-origin", hide = true)]
    _origin: Option<String>,
}

#[derive(Subcommand, Debug)]
enum Cmd {
    /// Fusion 실행 (실행 전 업데이트 확인)
    Launch {
        /// fusion360:// URL (선택)
        uri: Option<String>,
        /// Fusion360.exe 에 전달할 추가 인자
        #[arg(last = true)]
        args: Vec<String>,
        #[arg(long)]
        no_update_check: bool,
    },
    /// URL 스킴 처리기 (adskidmgr:// → Identity Manager, fusion360:// → Fusion)
    HandleUri { uri: String },
    /// 설치 진행 상황과 아카이브별 다운로드/설치 과정을 표로 실시간 표시
    Monitor {
        /// 한 번만 출력하고 종료
        #[arg(long)]
        once: bool,
        /// 화면 높이와 관계없이 모든 행 표시
        #[arg(long)]
        all: bool,
        /// 작업이 끝나도 계속 대기 (다음 작업까지 감시)
        #[arg(long)]
        follow: bool,
        /// 갱신 주기(ms)
        #[arg(long, default_value_t = 1000)]
        interval: u64,
    },
    /// .ico → .png 변환 (패키징 스크립트용)
    #[command(hide = true)]
    ExportIcon { ico: PathBuf, png: PathBuf },
}

struct TermSink {
    interactive: bool,
    silent_yes: bool,
}

fn ask(q: &str, default: bool) -> bool {
    eprint!("\n{q} [{}] ", if default { "Y/n" } else { "y/N" });
    let _ = std::io::stderr().flush();
    let mut s = String::new();
    if std::io::stdin().lock().read_line(&mut s).is_err() {
        return default;
    }
    match s.trim().to_ascii_lowercase().as_str() {
        "" => default,
        x => x.starts_with('y') || x.starts_with('예') || x == "ㅛ",
    }
}

impl Sink for TermSink {
    fn send(&self, ev: &Event) -> String {
        match ev {
            Event::Progress { description, percentage, .. } => {
                let w = 30usize;
                let fill = ((percentage / 100.0) * w as f64) as usize;
                eprint!("\r\x1b[2K[{}{}] {:>3.0}%  {}", "#".repeat(fill), "-".repeat(w - fill.min(w)), percentage, description);
                let _ = std::io::stderr().flush();
                String::new()
            }
            Event::ReportError { title, what, detail, todo, log_file, .. } => {
                eprintln!("\n\n== {title} ==\n{what}\n  {detail}\n{todo}\nlog: {log_file}");
                "OK".into()
            }
            Event::PromptUser { title, body, .. } => {
                eprintln!("\n\n== {title} ==\n{body}");
                if self.interactive && ask("", false) { "Yes".into() } else { "No".into() }
            }
            Event::InformUser { title, body, .. } => {
                eprintln!("\n\n== {title} ==\n{body}");
                "Yes".into()
            }
            Event::PrecheckFailure { paragraph, items, .. } => {
                eprintln!("\n\n{paragraph}");
                for i in items {
                    eprintln!("  ✗ {}\n    {}", i.description, i.url);
                }
                if self.interactive && ask("Re-Test?", true) { "retest".into() } else { "cancel".into() }
            }
            Event::ProvideProxyCreds { summary, message } => {
                if !self.interactive {
                    return "Cancel".into();
                }
                eprintln!("\n\n== {summary} ==\n{message}");
                eprint!("Username: ");
                let mut u = String::new();
                let _ = std::io::stdin().lock().read_line(&mut u);
                eprint!("Password: ");
                let mut p = String::new();
                let _ = std::io::stdin().lock().read_line(&mut p);
                serde_json::json!({"username": u.trim(), "password": p.trim()}).to_string()
            }
            Event::AbortUninstall { title, .. } => {
                eprintln!("\n\n== {title} ==");
                if self.silent_yes || (self.interactive && ask("Uninstall Autodesk Fusion?", false)) { "Yes".into() } else { "No".into() }
            }
            Event::Complete { ok, message } => {
                eprintln!();
                if *ok {
                    println!("{message}");
                } else {
                    eprintln!("failed: {message}");
                }
                String::new()
            }
        }
    }
}

fn build_request(cli: &Cli) -> Result<Request, String> {
    Ok(Request {
        process: cli.process.parse::<ProcessKind>()?,
        app_id: cli.app_id.clone(),
        stream: cli.stream.clone(),
        conn: cli.conn.clone(),
        lang: cli.lang.clone(),
        threads: cli.threads,
        no_auto_launch: cli.no_auto_launch,
        quiet: cli.quiet,
        full_deploy: cli.full_deploy,
        install_location: cli.install_location.clone(),
        resources_dir: cli.resources.clone(),
        purge: cli.purge,
        silent: cli.silent,
        cleancacheall: cli.cleancacheall,
        removeuserdata: cli.removeuserdata,
        removeusercred: cli.removeusercred,
        deep_clean: cli.deep_clean,
        pull_dir: cli.pull_dir.clone(),
        launch_args: vec![],
        wine: WineOptions { wine_bin: cli.wine.clone(), dxvk: !cli.no_dxvk, webview2: cli.with_webview2, ..Default::default() },
        desktop_shortcut: !cli.no_desktop_shortcut,
    })
}

fn sink(cli: &Cli) -> Arc<dyn Sink> {
    Arc::new(TermSink { interactive: std::io::stdin().is_terminal() && !cli.quiet, silent_yes: cli.silent })
}

fn run(req: Request, cli: &Cli) -> i32 {
    let o = ofc_core::run_request(req, sink(cli), Arc::new(AtomicBool::new(false)), cli.verbose);
    if o.ok { 0 } else { 1 }
}

/// 업데이트가 있으면 GUI 업데이트 화면으로 넘긴다 (원본 FusionLauncher 의 업데이트 확인 대체)
fn launch(cli: &Cli, uri: Option<String>, args: Vec<String>, no_update_check: bool) -> i32 {
    let mut req = match build_request(cli) {
        Ok(r) => r,
        Err(e) => return fail(&e),
    };
    req.process = ProcessKind::Launch;
    req.launch_args = uri.into_iter().chain(args).collect();
    if !no_update_check && std::env::var_os("OFC_NO_UPDATE_CHECK").is_none() {
        let paths = ofc_core::config::Paths::new(&req);
        ofc_core::i18n::init(&paths.resources, &req.lang.clone().unwrap_or_else(ofc_core::i18n::system_lang));
        let ui = ofc_core::events::Ui::new(sink(cli), Arc::new(AtomicBool::new(false)), true);
        let runner = ofc_core::process::Runner::new(req.clone(), ui, PathBuf::new());
        if let Some(v) = runner.check_update() {
            if let Some(gui) = ofc_core::desktop::gui_bin() {
                eprintln!("update available: {v}");
                let err = {
                    use std::os::unix::process::CommandExt;
                    std::process::Command::new(gui).args(["-p", "update", "-s", &req.stream]).exec()
                };
                eprintln!("failed to start updater: {err}");
            }
        }
    }
    run(req, cli)
}

fn handle_uri(cli: &Cli, uri: &str) -> i32 {
    let scheme = uri.split(':').next().unwrap_or("").to_ascii_lowercase();
    let mut req = match build_request(cli) {
        Ok(r) => r,
        Err(e) => return fail(&e),
    };
    if scheme == FUSION_URL_SCHEME {
        return launch(cli, Some(uri.to_string()), vec![], true);
    }
    if scheme != IDENTITY_URL_SCHEME {
        return fail(&format!("unsupported URI scheme: {scheme}"));
    }
    req.process = ProcessKind::Launch;
    let paths = ofc_core::config::Paths::new(&req);
    let log = ofc_core::init_logging(&paths.logs, cli.verbose);
    let ui = ofc_core::events::Ui::new(sink(cli), Arc::new(AtomicBool::new(false)), true);
    let runner = ofc_core::process::Runner::new(req.clone(), ui, log);
    // IDSDK 패키지의 "Autodesk Identity Manager" 디렉터리에서 실행 파일 탐색
    let exe = runner
        .installed_exe(Some(constants::IDSDK_CONFIG_FILE_ID))
        .and_then(|cfg| cfg.parent().map(|d| d.join("AdskIdentityManager.exe")))
        .filter(|p| p.is_file())
        .or_else(|| find_file(&paths.stream_dir(), "AdskIdentityManager.exe", 4));
    let Some(exe) = exe else { return fail("AdskIdentityManager.exe not found – is Autodesk Fusion installed?") };
    let Some(wine) = ofc_core::wine::Wine::locate(&paths, &req.wine) else { return fail("wine not found") };
    log::info!("forwarding sign-in callback to {}", exe.display());
    match wine.spawn_detached(&exe, &[uri.to_string()], Some(&paths.logs.join("identity-manager.log"))) {
        Ok(()) => 0,
        Err(e) => fail(&e.to_string()),
    }
}

fn find_file(dir: &std::path::Path, name: &str, depth: usize) -> Option<PathBuf> {
    let mut best: Option<(std::time::SystemTime, PathBuf)> = None;
    let mut stack = vec![(dir.to_path_buf(), 0)];
    while let Some((d, lvl)) = stack.pop() {
        let Ok(rd) = std::fs::read_dir(&d) else { continue };
        for e in rd.filter_map(Result::ok) {
            let p = e.path();
            if p.is_dir() && lvl < depth {
                stack.push((p, lvl + 1));
            } else if e.file_name() == name {
                let t = e.metadata().and_then(|m| m.modified()).unwrap_or(std::time::UNIX_EPOCH);
                if best.as_ref().map(|(bt, _)| t > *bt).unwrap_or(true) {
                    best = Some((t, p));
                }
            }
        }
    }
    best.map(|(_, p)| p)
}

fn fail(msg: &str) -> i32 {
    eprintln!("ofc-streamer: {msg}");
    1
}

fn main() {
    let cli = Cli::parse();
    let code = match &cli.cmd {
        Some(Cmd::Launch { uri, args, no_update_check }) => launch(&cli, uri.clone(), args.clone(), *no_update_check),
        Some(Cmd::HandleUri { uri }) => handle_uri(&cli, uri),
        Some(Cmd::Monitor { once, all, follow, interval }) => match build_request(&cli) {
            Ok(req) => ofc_core::monitor::run(
                &ofc_core::config::Paths::new(&req),
                &ofc_core::monitor::Options { once: *once, all: *all, follow: *follow, interval_ms: *interval },
            ),
            Err(e) => fail(&e),
        },
        Some(Cmd::ExportIcon { ico, png }) => match ofc_core::desktop::ico_to_png(ico, png) {
            Ok(()) => 0,
            Err(e) => fail(&e),
        },
        None => match build_request(&cli) {
            Ok(req) if req.process == ProcessKind::Launch => launch(&cli, None, vec![], false),
            Ok(req) => run(req, &cli),
            Err(e) => fail(&e),
        },
    };
    std::process::exit(code);
}
