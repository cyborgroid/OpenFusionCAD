//! 데스크톱 통합 (freedesktop.org 표준)
//!
//! 원본 Windows 동작 → Linux 대응
//! - CREATE_LAUNCH_ITEMS (바탕화면/시작 메뉴 .lnk, 작업표시줄 고정) → `.desktop` 런처
//! - WRITE_LAUNCHER_INI / UPDATE_PINNABLE_LAUNCHER / DELIVER_ICON → `.desktop` + hicolor 아이콘
//! - REGISTER_UNINSTALL (HKCU\...\Uninstall 레지스트리) → "Autodesk Fusion 설치제거" 런처
//! - FILE_ASSOCIATIONS (HKCU\Software\Classes\fusion360) → `x-scheme-handler/fusion360`
//! - 계정 로그인 콜백 (HKCU\Software\Classes\adskidmgr) → `x-scheme-handler/adskidmgr`

use crate::config::Paths;
use crate::constants::{FUSION_URL_SCHEME, IDENTITY_URL_SCHEME};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

pub const MAIN_DESKTOP: &str = "openfusioncad-fusion.desktop";
pub const UNINSTALL_DESKTOP: &str = "openfusioncad-fusion-uninstall.desktop";
pub const IDMGR_DESKTOP: &str = "openfusioncad-adskidmgr.desktop";
pub const ICON_NAME: &str = "openfusioncad-fusion";

fn app_dir() -> Option<PathBuf> {
    std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf))
}

/// CLI(ofc-streamer) 경로. GUI 에서 FFI 로 호출될 때도 같은 디렉터리에서 찾는다.
pub fn streamer_bin() -> PathBuf {
    if let Ok(p) = std::env::var("OFC_STREAMER_BIN") {
        return PathBuf::from(p);
    }
    if let Some(d) = app_dir() {
        for cand in [d.join("ofc-streamer"), d.join("../ofc-streamer")] {
            if cand.is_file() {
                return cand.canonicalize().unwrap_or(cand);
            }
        }
    }
    crate::wine::which("ofc-streamer").unwrap_or_else(|| PathBuf::from("/opt/openfusioncad/ofc-streamer"))
}

/// GUI(FusionClientDownloader) 경로 – 설치제거 런처에서 사용
pub fn gui_bin() -> Option<PathBuf> {
    if let Ok(p) = std::env::var("OFC_GUI_BIN") {
        return Some(PathBuf::from(p));
    }
    let d = streamer_bin().parent()?.to_path_buf();
    let p = d.join("FusionClientDownloader");
    p.is_file().then_some(p)
}

fn quote(p: &Path) -> String {
    // Desktop Entry Spec: 공백/특수문자가 있는 인자는 큰따옴표로 감싸고 \ " ` $ 이스케이프
    let s = p.to_string_lossy();
    let esc: String = s.chars().flat_map(|c| if matches!(c, '"' | '`' | '$' | '\\') { vec!['\\', c] } else { vec![c] }).collect();
    format!("\"{esc}\"")
}

fn write_desktop(dir: &Path, name: &str, body: &str) -> std::io::Result<PathBuf> {
    fs::create_dir_all(dir)?;
    let p = dir.join(name);
    fs::write(&p, body)?;
    use std::os::unix::fs::PermissionsExt;
    fs::set_permissions(&p, fs::Permissions::from_mode(0o755))?;
    Ok(p)
}

/// .ico 에서 가장 큰 이미지를 PNG 로 저장
pub fn ico_to_png(ico_path: &Path, png_path: &Path) -> Result<(), String> {
    let f = fs::File::open(ico_path).map_err(|e| e.to_string())?;
    let dir = ico::IconDir::read(f).map_err(|e| e.to_string())?;
    let best = dir.entries().iter().max_by_key(|e| (e.width(), e.bits_per_pixel())).ok_or("empty icon")?;
    let img = best.decode().map_err(|e| e.to_string())?;
    if let Some(parent) = png_path.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let out = fs::File::create(png_path).map_err(|e| e.to_string())?;
    img.write_png(out).map_err(|e| e.to_string())
}

pub struct LaunchItem<'a> {
    pub name: &'a str,
    pub icon_ico: Option<&'a Path>,
}

/// 메인 런처 + 서비스 유틸리티 동작 + fusion360:// 스킴 등록
pub fn create_launch_items(paths: &Paths, item: &LaunchItem, desktop_shortcut: bool) -> Result<(), String> {
    let streamer = streamer_bin();
    if let Some(ico) = item.icon_ico {
        if let Err(e) = ico_to_png(ico, &paths.icons.join(format!("{ICON_NAME}.png"))) {
            log::warn!("icon conversion failed: {e}");
        }
    }
    let exec = format!("{} launch", quote(&streamer));
    let body = format!(
        "[Desktop Entry]\nType=Application\nVersion=1.0\nName={name}\nGenericName=3D CAD/CAM/CAE\nComment={name} (Wine)\n\
Exec={exec} %u\nIcon={ICON_NAME}\nTerminal=false\nCategories=Graphics;Engineering;3DGraphics;\n\
MimeType=x-scheme-handler/{FUSION_URL_SCHEME};\nStartupWMClass=fusion360.exe\nStartupNotify=true\n\
Actions=ServiceUtility;\n\n[Desktop Action ServiceUtility]\nName=Autodesk Fusion Service Utility\nExec={exec} -- -serviceUtil\n",
        name = item.name
    );
    let p = write_desktop(&paths.applications, MAIN_DESKTOP, &body).map_err(|e| e.to_string())?;
    if desktop_shortcut {
        if let Some(d) = &paths.desktop_dir {
            let target = d.join(MAIN_DESKTOP);
            if fs::copy(&p, &target).is_ok() {
                // GNOME: 신뢰된 런처로 표시 (실패해도 무시)
                let _ = Command::new("gio")
                    .args(["set", &target.to_string_lossy(), "metadata::trusted", "true"])
                    .stderr(Stdio::null())
                    .status();
            }
        }
    }
    xdg_default(&paths.applications, MAIN_DESKTOP, FUSION_URL_SCHEME);
    refresh(paths);
    Ok(())
}

/// 계정 로그인 콜백(adskidmgr://) 처리기 등록 – 기존 계정 연동 유지의 핵심
pub fn register_identity_handler(paths: &Paths) -> Result<(), String> {
    let body = format!(
        "[Desktop Entry]\nType=Application\nName=Autodesk Identity Manager\nComment=Autodesk account sign-in callback for Fusion (Wine)\n\
Exec={} handle-uri %u\nIcon={ICON_NAME}\nTerminal=false\nNoDisplay=true\nMimeType=x-scheme-handler/{IDENTITY_URL_SCHEME};\n",
        quote(&streamer_bin())
    );
    write_desktop(&paths.applications, IDMGR_DESKTOP, &body).map_err(|e| e.to_string())?;
    xdg_default(&paths.applications, IDMGR_DESKTOP, IDENTITY_URL_SCHEME);
    refresh(paths);
    Ok(())
}

/// REGISTER_UNINSTALL 대응
pub fn register_uninstall(paths: &Paths, name: &str) -> Result<(), String> {
    let (exec, term) = match gui_bin() {
        Some(g) => (format!("{} -p uninstall", quote(&g)), "false"),
        None => (format!("{} -p uninstall", quote(&streamer_bin())), "true"),
    };
    let body = format!(
        "[Desktop Entry]\nType=Application\nName={}\nExec={exec}\nIcon={ICON_NAME}\nTerminal={term}\nCategories=Settings;\nNoDisplay=false\n",
        crate::i18n::trf("Remove {}", &[name])
    );
    write_desktop(&paths.applications, UNINSTALL_DESKTOP, &body).map_err(|e| e.to_string())?;
    refresh(paths);
    Ok(())
}

pub fn remove_all(paths: &Paths) {
    for n in [MAIN_DESKTOP, UNINSTALL_DESKTOP, IDMGR_DESKTOP] {
        let _ = fs::remove_file(paths.applications.join(n));
        if let Some(d) = &paths.desktop_dir {
            let _ = fs::remove_file(d.join(n));
        }
    }
    let _ = fs::remove_file(paths.icons.join(format!("{ICON_NAME}.png")));
    refresh(paths);
}

fn xdg_default(apps: &Path, desktop: &str, scheme: &str) {
    let ok = Command::new("xdg-mime")
        .args(["default", desktop, &format!("x-scheme-handler/{scheme}")])
        .stderr(Stdio::null())
        .status()
        .map(|s| s.success())
        .unwrap_or(false);
    if !ok {
        // xdg-utils 가 없으면 mimeapps.list 를 직접 갱신
        let cfg = std::env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(std::env::var("HOME").unwrap_or_default()).join(".config"));
        let list = cfg.join("mimeapps.list");
        let mut s = fs::read_to_string(&list).unwrap_or_default();
        let line = format!("x-scheme-handler/{scheme}={desktop}");
        if !s.contains(&line) {
            if !s.contains("[Default Applications]") {
                s.push_str("\n[Default Applications]\n");
            }
            s = s.replacen("[Default Applications]\n", &format!("[Default Applications]\n{line}\n"), 1);
            let _ = fs::write(list, s);
        }
        log::warn!("xdg-mime unavailable; wrote mimeapps.list directly ({})", apps.display());
    }
}

fn refresh(paths: &Paths) {
    let _ = Command::new("update-desktop-database").arg(&paths.applications).stderr(Stdio::null()).status();
}
