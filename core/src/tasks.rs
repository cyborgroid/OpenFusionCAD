//! 패키지 특수 동작(SpecialActionIds) – factory.register_special_actions 대응.
//!
//! 모든 태스크는 Windows 원본과 같은 파일 결과를 Wine prefix 안에 만들거나,
//! Windows 전용(레지스트리, COM, MSI, icacls, RunOnce, Explorer 아이콘 캐시)이면
//! Linux 대응 동작으로 대체/생략한다. 생략 사유는 로그와 docs/RESTRICTIONS.md 참고.

use crate::config::{Paths, Request};
use crate::constants::{action, IDSDK_CONFIG_FILE_ID, WEBVIEW2_INSTALLER_FILE_ID};
use crate::desktop;
use crate::manifest::{expand, expand_win, stream_tag_suffix, AppManifest, PackageManifest};
use crate::wine::Wine;
use serde_json::Value;
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

pub struct Ctx<'a> {
    pub paths: &'a Paths,
    pub req: &'a Request,
    pub wine: Option<&'a Wine>,
    pub app: &'a AppManifest,
    pub packages: &'a [PackageManifest],
    /// Application.fileidmap – 모든 패키지의 파일/디렉터리 ID → 경로
    pub idmap: HashMap<String, PathBuf>,
    pub manifest_url: String,
    pub etag: Option<String>,
}

impl<'a> Ctx<'a> {
    pub fn new(paths: &'a Paths, req: &'a Request, wine: Option<&'a Wine>, app: &'a AppManifest, packages: &'a [PackageManifest], manifest_url: String, etag: Option<String>) -> Self {
        let mut idmap = HashMap::new();
        for p in packages {
            if let Some(dest) = p.destination() {
                for (id, e) in p.id_map(&paths.destination(dest)) {
                    idmap.insert(id, e.path);
                }
            }
        }
        Ctx { paths, req, wine, app, packages, idmap, manifest_url, etag }
    }

    pub fn pkg_root(&self, pkg: &PackageManifest) -> Option<PathBuf> {
        pkg.destination().map(|d| self.paths.destination(d))
    }

    /// 메인 실행 파일(Fusion360.exe) – 앱 UUID 와 같은 파일 ID
    pub fn main_exe(&self) -> Option<PathBuf> {
        self.idmap.get(&self.req.app_id).cloned().filter(|p| p.is_file())
    }
}

fn prop<'v>(m: &'v serde_json::Map<String, Value>, k: &str) -> Option<&'v Value> {
    m.get(k)
}

fn is_true(m: &serde_json::Map<String, Value>, k: &str) -> bool {
    matches!(m.get(k), Some(Value::String(s)) if s.eq_ignore_ascii_case("true")) || matches!(m.get(k), Some(Value::Bool(true)))
}

/// 패키지 하나의 post-deploy 태스크 실행. 실패는 경고로만 기록한다(원본과 동일하게
/// 개별 태스크 실패가 설치 전체를 중단시키지 않도록).
pub fn run_post_deploy(ctx: &Ctx, pkg: &PackageManifest) {
    let tasks: Vec<String> = pkg.post_deploy_tasks.iter().chain(&pkg.post_procedure_tasks).map(|t| action::normalize(t)).collect();
    for t in tasks {
        let r = match t.as_str() {
            action::ACTIVATE_CONFIG => activate_config(ctx, pkg),
            action::PROCESS_TEMPLATE => process_template(ctx, pkg),
            action::WRITE_STREAMER_OPTIONS => write_streamer_options(ctx, pkg),
            action::WRITE_LAUNCHER_INI => write_launcher_ini(ctx, pkg),
            action::UPDATE_PINNABLE_LAUNCHER => update_pinnable_launcher(ctx, pkg),
            action::FILESYSTEM_ACTIONS => filesystem_actions(ctx, pkg),
            action::EXTERNAL_INSTALLS => external_installs(ctx, pkg, false),
            action::UPDATE_IDSDK_CONFIG => update_idsdk_config(ctx, pkg),
            action::INSTALL_WEBVIEW2 => install_webview2(ctx),
            action::CREATE_PACKAGE_LINK => create_package_links(ctx, pkg),
            action::REMOVE_STREAMING_SESSION => remove_streaming_session(ctx, pkg),
            // 아래는 프로세스 끝에서 한 번만 수행 (process.rs: finish_install)
            action::CREATE_LAUNCH_ITEMS | action::AUTO_LAUNCH_ITEMS | action::REGISTER_UNINSTALL | action::FILE_ASSOCIATIONS | action::DELIVER_ICON => Ok(()),
            // 체크섬은 다운로드 단계에서 아카이브마다 SHA-1 로 검증 완료
            action::FULL_CHECKSUM_VALIDATION => Ok(()),
            action::ADD_REGISTRY_INFO | action::REGISTER_COM | action::REMOVE_WININSTALLER_PRODUCT => {
                log::info!("skip {t}: Windows registry/COM/MSI integration is not applicable on Linux");
                Ok(())
            }
            action::WRITE_GA_APPNAME => {
                log::info!("skip {t}: analytics session file (telemetry) is not written");
                Ok(())
            }
            action::REGISTER_SAFE_MODE | action::COMPILE_PYTHON_BYTECODE | action::LAUNCH_SERVICES_REGISTRATION => {
                log::info!("skip {t}: platform-specific (Windows Explorer / macOS)");
                Ok(())
            }
            other => {
                log::info!("Special action {other} not in registry");
                Ok(())
            }
        };
        if let Err(e) = r {
            log::warn!("special action {t} failed for package {}: {e}", pkg.source_id());
        }
    }
}

/// INITIAL_INSTALL_DELETE_FILES – 앱 매니페스트의 initial-install-delete-paths 제거
pub fn initial_install_delete(ctx: &Ctx) {
    for p in &ctx.app.initial_install_delete_paths {
        let path = PathBuf::from(expand(p, ctx.paths));
        let r = if path.is_dir() { fs::remove_dir_all(&path) } else { fs::remove_file(&path) };
        if r.is_ok() {
            log::info!("removed {}", path.display());
        }
    }
}

/// ACTIVATE_CONFIG – 스트림별 설정 원본을 활성 설정 파일로 복사
/// (ApplicationOptions.production.json → "Fusion 360.server.config")
fn activate_config(ctx: &Ctx, pkg: &PackageManifest) -> Result<(), String> {
    let Some(root) = ctx.pkg_root(pkg) else { return Ok(()) };
    // pre-production → production 처럼 접두사 제거
    let tag = match ctx.req.stream.split_once('-') {
        Some((pre, rest)) if pre.chars().all(|c| c.is_ascii_alphabetic()) => rest.to_string(),
        _ => ctx.req.stream.clone(),
    };
    let mut errs = vec![];
    pkg.walk(&root, &mut |_, _| {}, &mut |path, f| {
        let Some(Value::Object(src)) = prop(&f.properties, "config-activation-source") else { return };
        let Some(sid) = src.get(&tag).and_then(Value::as_str) else { return };
        let Some(from) = ctx.idmap.get(sid) else { return };
        if path.exists() {
            let _ = fs::remove_file(path);
        }
        if src.get("mode").and_then(Value::as_str) != Some("remove_only") {
            if let Err(e) = fs::copy(from, path) {
                errs.push(format!("{}: {e}", path.display()));
            } else {
                log::info!("activated config {} -> {}", from.display(), path.display());
            }
        }
    });
    errs.is_empty().then_some(()).ok_or(errs.join("; "))
}

/// PROCESS_TEMPLATE – "process-template" 파일의 ${...} 치환 (Fusion 이 읽으므로 Windows 경로)
fn process_template(ctx: &Ctx, pkg: &PackageManifest) -> Result<(), String> {
    let Some(root) = ctx.pkg_root(pkg) else { return Ok(()) };
    let mut errs = vec![];
    pkg.walk(&root, &mut |_, _| {}, &mut |path, f| {
        if !is_true(&f.properties, "process-template") {
            return;
        }
        match fs::read(path) {
            Ok(bytes) => {
                let text = String::from_utf8_lossy(&bytes);
                let out = expand_win(&text, ctx.paths).trim().to_string();
                if let Err(e) = fs::write(path, out) {
                    errs.push(e.to_string());
                }
            }
            Err(e) => errs.push(format!("{}: {e}", path.display())),
        }
    });
    errs.is_empty().then_some(()).ok_or(errs.join("; "))
}

/// `<add key="K" value="..."/>` 형식 XML 요소의 value 속성 설정. 요소가 없으면 그대로.
fn xml_set(text: &str, key: &str, value: Option<&str>) -> String {
    let needle = format!("key=\"{key}\"");
    let Some(kpos) = text.find(&needle) else { return text.to_string() };
    let Some(start) = text[..kpos].rfind('<') else { return text.to_string() };
    let Some(end_rel) = text[kpos..].find('>') else { return text.to_string() };
    let end = kpos + end_rel + 1;
    let tag = &text[start..end];
    let new_tag = match value {
        None => String::new(),
        Some(v) => {
            let v = v.replace('&', "&amp;").replace('"', "&quot;").replace('<', "&lt;");
            if let Some(vp) = tag.find("value=\"") {
                let vs = vp + 7;
                let ve = tag[vs..].find('"').map(|i| vs + i).unwrap_or(vs);
                format!("{}{}{}", &tag[..vs], v, &tag[ve..])
            } else {
                tag.replacen(&needle, &format!("{needle} value=\"{v}\""), 1)
            }
        }
    };
    format!("{}{}{}", &text[..start], new_tag, &text[end..])
}

/// WRITE_STREAMER_OPTIONS – "Fusion 360.streamer.config" 에 업데이트 채널 정보 기록
fn write_streamer_options(ctx: &Ctx, pkg: &PackageManifest) -> Result<(), String> {
    let Some(root) = ctx.pkg_root(pkg) else { return Ok(()) };
    let mut errs = vec![];
    pkg.walk(&root, &mut |_, _| {}, &mut |path, f| {
        if !is_true(&f.properties, "write-options") {
            return;
        }
        let Ok(mut text) = fs::read_to_string(path) else {
            errs.push(format!("cannot read {}", path.display()));
            return;
        };
        text = xml_set(&text, "GlobalInstall", Some("0"));
        text = xml_set(&text, "DevBuild", None);
        if let Some(etag) = &ctx.etag {
            text = xml_set(&text, "StreamerRootUrl", Some(&ctx.manifest_url));
            text = xml_set(&text, "ETag", Some(etag));
        }
        text = xml_set(&text, "InstallStream", Some(&ctx.req.stream));
        text = xml_set(&text, "InstallOrigin", Some("Streamer"));
        if let Err(e) = fs::write(path, text) {
            errs.push(e.to_string());
        }
    });
    errs.is_empty().then_some(()).ok_or(errs.join("; "))
}

/// WRITE_LAUNCHER_INI – FusionLauncher.exe.ini (UTF-16, ConfigParser 형식)
fn write_launcher_ini(ctx: &Ctx, pkg: &PackageManifest) -> Result<(), String> {
    for r in &pkg.references {
        if !is_true(&r.properties, "write-ini") {
            continue;
        }
        let Some(ini) = ctx.idmap.get(&r.id) else { continue };
        let cmd_id = r.properties.get("cmd").and_then(Value::as_str).unwrap_or(&ctx.req.app_id);
        let Some(exe) = ctx.idmap.get(cmd_id) else { continue };
        let auid = r.properties.get("auid").and_then(Value::as_str).unwrap_or("AutodeskInc.Fusion360${streamtag}");
        let auid = auid.replace("${streamtag}", &stream_tag_suffix(&ctx.req.stream));
        let body = format!(
            "[Launcher]\r\nstream = {}\r\nauid = {}\r\ncmd = \"\"{}\"\"\r\nglobal = False\r\n\r\n",
            ctx.req.stream,
            auid,
            ctx.paths.to_windows_path(exe)
        );
        let mut bytes = vec![0xFF, 0xFE];
        for u in body.encode_utf16() {
            bytes.extend_from_slice(&u.to_le_bytes());
        }
        if let Some(d) = ini.parent() {
            fs::create_dir_all(d).map_err(|e| e.to_string())?;
        }
        fs::write(ini, bytes).map_err(|e| e.to_string())?;
        log::info!("wrote launcher ini {}", ini.display());
    }
    Ok(())
}

/// UPDATE_PINNABLE_LAUNCHER – FusionLauncher.exe 를 고정 위치(../6a0c96…/)로 복사
fn update_pinnable_launcher(ctx: &Ctx, pkg: &PackageManifest) -> Result<(), String> {
    let Some(root) = ctx.pkg_root(pkg) else { return Ok(()) };
    let mut errs = vec![];
    pkg.walk(&root, &mut |_, _| {}, &mut |path, f| {
        let Some(rel) = f.properties.get("launcher-relative-location").and_then(Value::as_str) else { return };
        let Some(dir) = path.parent() else { return };
        let target_dir = normalize(&dir.join(rel.replace('\\', "/")));
        let target = target_dir.join(&f.name);
        let newer = match (fs::metadata(path).and_then(|m| m.modified()), fs::metadata(&target).and_then(|m| m.modified())) {
            (Ok(a), Ok(b)) => a > b,
            _ => true,
        };
        if newer && path.exists() {
            let _ = fs::create_dir_all(&target_dir);
            if let Err(e) = fs::copy(path, &target) {
                errs.push(e.to_string());
            }
        }
    });
    errs.is_empty().then_some(()).ok_or(errs.join("; "))
}

fn normalize(p: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for c in p.components() {
        match c {
            std::path::Component::ParentDir => {
                out.pop();
            }
            std::path::Component::CurDir => {}
            o => out.push(o),
        }
    }
    out
}

/// FILESYSTEM_ACTIONS – writable-by-all 디렉터리 생성 (icacls 대신 단일 사용자 권한으로 충분)
fn filesystem_actions(ctx: &Ctx, pkg: &PackageManifest) -> Result<(), String> {
    let Some(root) = ctx.pkg_root(pkg) else { return Ok(()) };
    pkg.walk(
        &root,
        &mut |p, d| {
            if is_true(&d.properties, "writable-by-all") {
                let _ = fs::create_dir_all(p);
            }
        },
        &mut |_, _| {},
    );
    Ok(())
}

/// EXTERNAL_INSTALLS – references 의 installable=exe 를 Wine 으로 실행
/// (예: MaterialLibraryMigrator.exe migratecache "C:\...\Common\Material Library")
pub fn external_installs(ctx: &Ctx, pkg: &PackageManifest, uninstall: bool) -> Result<(), String> {
    let Some(wine) = ctx.wine else { return Ok(()) };
    let (kind_key, params_key) = if uninstall { ("uninstallable", "uninstall-params") } else { ("installable", "install-params") };
    for r in &pkg.references {
        let Some(kind) = r.properties.get(kind_key).and_then(Value::as_str) else { continue };
        if kind != "exe" {
            log::info!("skip external {kind} install for {} (msiexec is not used)", r.id);
            continue;
        }
        let Some(exe) = ctx.idmap.get(&r.id) else {
            log::info!("external install target {} not found, skipping", r.id);
            continue;
        };
        if !exe.is_file() {
            continue;
        }
        let params: Vec<String> = match r.properties.get(params_key) {
            Some(Value::String(s)) => expand_win(s, ctx.paths).split_whitespace().map(String::from).collect(),
            Some(Value::Array(a)) => a.iter().filter_map(Value::as_str).map(|s| expand_win(s, ctx.paths).trim().to_string()).collect(),
            _ => vec![],
        };
        log::info!("external install: {} {:?}", exe.display(), params);
        if !wine.run_wait(exe, &params) {
            log::warn!("external install returned non-zero: {}", exe.display());
        }
    }
    Ok(())
}

/// UPDATE_IDSDK_CONFIG – Autodesk Identity Manager 설정의 ssoServer.guid 갱신 (계정 연동)
fn update_idsdk_config(ctx: &Ctx, pkg: &PackageManifest) -> Result<(), String> {
    let Some(root) = ctx.pkg_root(pkg) else { return Ok(()) };
    let mut result = Ok(());
    pkg.walk(&root, &mut |_, _| {}, &mut |path, f| {
        if f.id() != Some(IDSDK_CONFIG_FILE_ID) {
            return;
        }
        log::info!("Found Configuration file : {}", path.display());
        result = (|| {
            let text = fs::read_to_string(path).map_err(|e| e.to_string())?;
            let mut v: Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(|e| e.to_string())?;
            if !v["ssoServer"].is_object() {
                v["ssoServer"] = Value::Object(Default::default());
            }
            v["ssoServer"]["guid"] = Value::String(uuid::Uuid::new_v4().to_string());
            fs::write(path, serde_json::to_string(&v).map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
            log::info!("Configuration updated successfully");
            Ok(())
        })();
    });
    result
}

/// INSTALL_WEBVIEW2 – Microsoft Edge WebView2 런타임. 기본 생략(옵션으로만 설치).
fn install_webview2(ctx: &Ctx) -> Result<(), String> {
    if !ctx.req.wine.webview2 {
        log::info!("skip INSTALL_WEBVIEW2: Microsoft WebView2 runtime is excluded (enable with --with-webview2)");
        return Ok(());
    }
    let (Some(wine), Some(exe)) = (ctx.wine, ctx.idmap.get(WEBVIEW2_INSTALLER_FILE_ID)) else { return Ok(()) };
    let ok = wine.run_wait(exe, &["/silent".into(), "/install".into()]);
    if ok { Ok(()) } else { Err("WebView2 installer failed".into()) }
}

/// CREATE_PACKAGE_LINK – linked-packages 디렉터리에 대상 패키지 파일 하드링크
fn create_package_links(ctx: &Ctx, pkg: &PackageManifest) -> Result<(), String> {
    let Some(root) = ctx.pkg_root(pkg) else { return Ok(()) };
    let mut links: Vec<(PathBuf, Vec<String>)> = vec![];
    pkg.walk(
        &root,
        &mut |p, d| {
            if let Some(Value::Array(a)) = d.properties.get("linked-packages") {
                links.push((p.to_path_buf(), a.iter().filter_map(Value::as_str).map(String::from).collect()));
            }
        },
        &mut |_, _| {},
    );
    for (dir, ids) in links {
        for id in ids {
            let Some(target_pkg) = ctx.packages.iter().find(|p| p.checksum == id || p.source_id() == id) else { continue };
            let Some(mut src) = ctx.pkg_root(target_pkg) else { continue };
            if let Some(sub) = target_pkg.properties.get("link-target-contents").and_then(Value::as_str) {
                src = src.join(sub.replace('\\', "/"));
            }
            hardlink_tree(&src, &dir).map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

fn hardlink_tree(src: &Path, dst: &Path) -> std::io::Result<()> {
    fs::create_dir_all(dst)?;
    for e in fs::read_dir(src)? {
        let e = e?;
        let to = dst.join(e.file_name());
        if e.file_type()?.is_dir() {
            hardlink_tree(&e.path(), &to)?;
        } else {
            let _ = fs::remove_file(&to);
            if fs::hard_link(e.path(), &to).is_err() {
                fs::copy(e.path(), &to)?;
            }
        }
    }
    Ok(())
}

fn remove_streaming_session(ctx: &Ctx, pkg: &PackageManifest) -> Result<(), String> {
    if let (Some(root), Some(name)) = (ctx.pkg_root(pkg), ctx.app.session_file_name()) {
        let _ = fs::remove_file(root.join(name));
    }
    Ok(())
}

/// 설치 완료 후 한 번 수행하는 데스크톱 통합 (CREATE_LAUNCH_ITEMS / REGISTER_UNINSTALL /
/// FILE_ASSOCIATIONS / DELIVER_ICON + 계정 로그인 콜백 등록)
pub fn finish_install(ctx: &Ctx) -> Result<(), String> {
    let icon_id = ctx.app.properties.get("uninstall-icon").and_then(Value::as_str).unwrap_or("cb75314714154bd685b3395fe57655e7");
    let icon = ctx.idmap.get(icon_id).filter(|p| p.is_file()).cloned();
    let name = ctx.app.display_name();
    desktop::create_launch_items(ctx.paths, &desktop::LaunchItem { name: &name, icon_ico: icon.as_deref() }, ctx.req.desktop_shortcut)?;
    desktop::register_identity_handler(ctx.paths)?;
    desktop::register_uninstall(ctx.paths, &name)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::xml_set;

    #[test]
    fn xml_value_update() {
        let x = r#"<appSettings><add key="InstallStream" value="x"/><add key="DevBuild" value="1"/></appSettings>"#;
        let y = xml_set(x, "InstallStream", Some("production"));
        assert!(y.contains(r#"<add key="InstallStream" value="production"/>"#));
        let z = xml_set(&y, "DevBuild", None);
        assert!(!z.contains("DevBuild"));
        assert_eq!(xml_set(x, "Missing", Some("v")), x);
    }
}
