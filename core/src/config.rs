//! 실행 옵션과 Linux 경로 배치.
//!
//! Windows 원본의 위치(%LOCALAPPDATA%\Autodesk\webdeploy …)는 전용 Wine prefix
//! 안의 동일한 상대 경로로 매핑한다. Fusion 내부 코드가 기대하는 디렉터리 구조를
//! 그대로 유지하기 위함이다. 레지스트리/로그/캐시 등 다운로더 자체 데이터는
//! XDG 기준 경로를 사용한다.

use crate::constants::{self, location};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum ProcessKind {
    #[default]
    Deploy,
    Update,
    Launch,
    Uninstall,
    Reinstall,
    Cleancache,
    Query,
    Pull,
}

impl std::str::FromStr for ProcessKind {
    type Err = String;
    fn from_str(s: &str) -> Result<Self, String> {
        Ok(match s.to_ascii_lowercase().as_str() {
            "deploy" => Self::Deploy,
            "update" => Self::Update,
            "launch" => Self::Launch,
            "uninstall" => Self::Uninstall,
            "reinstall" => Self::Reinstall,
            "cleancache" => Self::Cleancache,
            "query" => Self::Query,
            "pull" => Self::Pull,
            o => return Err(format!("unknown process: {o}")),
        })
    }
}

/// 원본 streamer.exe 명령행 인자와 같은 의미의 실행 요청.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct Request {
    pub process: ProcessKind,
    pub app_id: String,
    pub stream: String,
    /// `-c/--conn` : 매니페스트 루트 URL. 없으면 stream 의 채널 루트
    pub conn: Option<String>,
    pub lang: Option<String>,
    pub threads: usize,
    pub no_auto_launch: bool,
    pub quiet: bool,
    /// `--full-deploy`: sub-applications 매니페스트까지 받아 설치
    pub full_deploy: bool,
    /// `--installlocation`: 데이터 루트(기본 ~/.local/share/openfusioncad)
    pub install_location: Option<String>,
    pub resources_dir: Option<String>,
    // uninstall
    pub purge: bool,
    pub silent: bool,
    // clean cache / reinstall
    pub cleancacheall: bool,
    pub removeuserdata: bool,
    pub removeusercred: bool,
    pub deep_clean: bool,
    // pull
    pub pull_dir: Option<String>,
    // launch
    pub launch_args: Vec<String>,
    // Linux 전용 옵션
    pub wine: WineOptions,
    pub desktop_shortcut: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct WineOptions {
    /// 사용할 wine 실행 파일 (기본: PATH 의 `wine`)
    pub wine_bin: String,
    /// DXVK(Direct3D→Vulkan, 오픈소스) 를 prefix 에 설치
    pub dxvk: bool,
    /// WebView2(Microsoft 컴포넌트) 설치 – 기본 비활성
    pub webview2: bool,
    /// wine-mono(.NET 대체)/wine-gecko(IE 대체) 자동 설치 프롬프트 억제
    pub suppress_mono_gecko: bool,
}

impl Default for WineOptions {
    fn default() -> Self {
        WineOptions { wine_bin: "wine".into(), dxvk: true, webview2: false, suppress_mono_gecko: true }
    }
}

impl Default for Request {
    fn default() -> Self {
        Request {
            process: ProcessKind::Deploy,
            app_id: constants::FUSION_APP_ID.into(),
            stream: constants::DEFAULT_STREAM.into(),
            conn: None,
            lang: None,
            threads: 5,
            no_auto_launch: false,
            quiet: false,
            full_deploy: false,
            install_location: None,
            resources_dir: None,
            purge: false,
            silent: false,
            cleancacheall: false,
            removeuserdata: false,
            removeusercred: false,
            deep_clean: false,
            pull_dir: None,
            launch_args: vec![],
            wine: WineOptions::default(),
            desktop_shortcut: true,
        }
    }
}

impl Request {
    pub fn root_url(&self) -> String {
        let mut r = self
            .conn
            .clone()
            .or_else(|| constants::channel_root(&self.stream).map(String::from))
            .unwrap_or_else(|| format!("https://dl.appstreaming.autodesk.com/{}/", self.stream));
        if !r.ends_with('/') {
            r.push('/');
        }
        r
    }
}

fn home() -> PathBuf {
    std::env::var_os("HOME").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("/tmp"))
}

fn xdg(var: &str, fallback: &str) -> PathBuf {
    match std::env::var_os(var) {
        Some(v) if !v.is_empty() => PathBuf::from(v),
        _ => home().join(fallback),
    }
}

pub fn linux_user() -> String {
    std::env::var("USER")
        .ok()
        .filter(|u| !u.is_empty())
        .unwrap_or_else(|| {
            // SAFETY: getpwuid 는 정적 버퍼를 반환한다. 즉시 복사한다.
            unsafe {
                let pw = libc::getpwuid(libc::getuid());
                if pw.is_null() {
                    return "user".into();
                }
                std::ffi::CStr::from_ptr((*pw).pw_name).to_string_lossy().into_owned()
            }
        })
}

#[derive(Debug, Clone)]
pub struct Paths {
    /// 다운로더 데이터 루트 (~/.local/share/openfusioncad)
    pub data: PathBuf,
    pub prefix: PathBuf,
    pub cache: PathBuf,
    pub logs: PathBuf,
    pub registry_db: PathBuf,
    pub resources: PathBuf,
    /// ~/.local/share/applications
    pub applications: PathBuf,
    /// ~/.local/share/icons/hicolor/256x256/apps
    pub icons: PathBuf,
    pub desktop_dir: Option<PathBuf>,
    pub stream: String,
}

impl Paths {
    pub fn new(req: &Request) -> Paths {
        let data_home = xdg("XDG_DATA_HOME", ".local/share");
        let data = req
            .install_location
            .as_ref()
            .map(PathBuf::from)
            .unwrap_or_else(|| data_home.join("openfusioncad"));
        let cache = xdg("XDG_CACHE_HOME", ".cache").join("openfusioncad");
        let resources = req.resources_dir.as_ref().map(PathBuf::from).unwrap_or_else(default_resources_dir);
        Paths {
            prefix: data.join("wineprefix"),
            logs: data.join("logs"),
            registry_db: data.join("registry.db"),
            cache,
            data,
            resources,
            applications: data_home.join("applications"),
            icons: data_home.join("icons/hicolor/256x256/apps"),
            desktop_dir: desktop_dir(),
            stream: req.stream.clone(),
        }
    }

    /// Wine prefix 안의 사용자 프로필 (C:\users\<user>)
    pub fn win_profile(&self) -> PathBuf {
        self.prefix.join("drive_c/users").join(linux_user())
    }
    /// %LOCALAPPDATA%
    pub fn local_appdata(&self) -> PathBuf {
        self.win_profile().join("AppData/Local")
    }
    /// %APPDATA%
    pub fn roaming_appdata(&self) -> PathBuf {
        self.win_profile().join("AppData/Roaming")
    }
    /// %TEMP%
    pub fn win_temp(&self) -> PathBuf {
        self.local_appdata().join("Temp")
    }

    /// constants.LocationIds → 실제 경로 (windows/platform.py 의 템플릿과 동일한 상대 경로)
    pub fn location(&self, id: &str) -> Option<PathBuf> {
        let la = self.local_appdata().join("Autodesk");
        Some(match id {
            location::WEBDEPLOY => la.join("webdeploy"),
            location::META => la.join("webdeploy/meta"),
            location::COMMON_DATA => la.join("Common"),
            location::SHARED => la.join("webdeploy/shared"),
            location::STREAMER => la.join("webdeploy/meta/streamer"),
            location::REGISTRY => la.join("webdeploy/meta/registry"),
            _ => return None,
        })
    }

    /// 패키지 `root.destination` → 설치 디렉터리.
    /// LocationIds 이면 해당 위치, 그 외(버전 해시)는 `webdeploy/<stream>/<hash>`.
    pub fn destination(&self, dest: &str) -> PathBuf {
        if let Some(p) = self.location(dest) {
            return p;
        }
        self.location(location::WEBDEPLOY).unwrap().join(&self.stream).join(dest)
    }

    pub fn stream_dir(&self) -> PathBuf {
        self.location(location::WEBDEPLOY).unwrap().join(&self.stream)
    }

    /// Linux 경로 → Wine 의 Windows 경로 (C:\...)
    pub fn to_windows_path(&self, p: &Path) -> String {
        let drive_c = self.prefix.join("drive_c");
        match p.strip_prefix(&drive_c) {
            Ok(rel) => format!("C:\\{}", rel.to_string_lossy().replace('/', "\\")),
            Err(_) => format!("Z:{}", p.to_string_lossy().replace('/', "\\")),
        }
    }

    pub fn ensure(&self) -> std::io::Result<()> {
        for d in [&self.data, &self.cache, &self.logs] {
            std::fs::create_dir_all(d)?;
        }
        Ok(())
    }
}

fn desktop_dir() -> Option<PathBuf> {
    // xdg-user-dirs 설정(~/.config/user-dirs.dirs)의 XDG_DESKTOP_DIR 사용
    let cfg = xdg("XDG_CONFIG_HOME", ".config").join("user-dirs.dirs");
    if let Ok(s) = std::fs::read_to_string(cfg) {
        for line in s.lines() {
            if let Some(v) = line.strip_prefix("XDG_DESKTOP_DIR=") {
                let v = v.trim_matches('"').replace("$HOME", &home().to_string_lossy());
                let p = PathBuf::from(v);
                if p.is_dir() {
                    return Some(p);
                }
            }
        }
    }
    let p = home().join("Desktop");
    p.is_dir().then_some(p)
}

/// 실행 파일 위치 기준으로 resources 디렉터리를 찾는다.
pub fn default_resources_dir() -> PathBuf {
    if let Ok(exe) = std::env::current_exe() {
        let mut dir = exe.parent().map(Path::to_path_buf);
        while let Some(d) = dir {
            let cand = d.join("resources");
            if cand.join("precheck_config.json").exists() {
                return cand;
            }
            dir = d.parent().map(Path::to_path_buf);
        }
    }
    PathBuf::from("/opt/openfusioncad/resources")
}
