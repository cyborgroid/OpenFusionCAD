//! Wine 실행 환경.
//!
//! Fusion 본체는 Windows PE 바이너리로만 배포되므로 Linux 에서는 전용 Wine prefix
//! 위에서 실행한다. 다운로더/런처 자체는 Wine 없이 네이티브로 동작한다.
//! prefix 는 다운로더 전용(~/.local/share/openfusioncad/wineprefix)이라 사용자의
//! 다른 Wine 설정과 섞이지 않는다.

use crate::config::{Paths, WineOptions};
use crate::net::Net;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

/// 원본 Windows 환경에서만 의미가 있거나 Linux 에서 문제를 일으키는 구성요소 비활성화.
/// (커뮤니티에서 검증된 Fusion-on-Wine 설정)
const DLL_OVERRIDES: &[(&str, &str)] = &[
    // Autodesk Desktop Platform 분석(텔레메트리) 서비스 – Wine 에서 멈춤 유발, 비활성화
    ("adpclientservice.exe", ""),
    // CEF 기반 내장 브라우저는 Wine 내장 구현 사용
    ("AdCefWebBrowser.exe", "builtin"),
    // Fusion 이 MSVCRT 패키지로 직접 배포하는 런타임을 우선 사용
    ("msvcp140", "native"),
    ("mfc140u", "native"),
    // Windows 언어 설정 API – Wine 미구현으로 충돌 유발
    ("bcp47langs", ""),
];

#[derive(Clone)]
pub struct Wine {
    pub bin: PathBuf,
    pub server: PathBuf,
    pub prefix: PathBuf,
    opts: WineOptions,
}

pub fn which(name: &str) -> Option<PathBuf> {
    if name.contains('/') {
        let p = PathBuf::from(name);
        return p.is_file().then_some(p);
    }
    std::env::var_os("PATH").and_then(|paths| {
        std::env::split_paths(&paths).map(|d| d.join(name)).find(|p| p.is_file())
    })
}

/// `wine --version` → (major, minor)
pub fn version(bin: &Path) -> Option<(u32, u32)> {
    let out = Command::new(bin).arg("--version").stderr(Stdio::null()).output().ok()?;
    let s = String::from_utf8_lossy(&out.stdout);
    let v = s.trim().trim_start_matches("wine-");
    let mut it = v.split(|c: char| !c.is_ascii_digit());
    let major = it.next()?.parse().ok()?;
    let minor = it.next().and_then(|m| m.parse().ok()).unwrap_or(0);
    Some((major, minor))
}

impl Wine {
    pub fn locate(paths: &Paths, opts: &WineOptions) -> Option<Wine> {
        let bin = which(&opts.wine_bin).or_else(|| which("wine64"))?;
        let server = bin.parent().map(|d| d.join("wineserver")).filter(|p| p.is_file()).or_else(|| which("wineserver"))?;
        Some(Wine { bin, server, prefix: paths.prefix.clone(), opts: opts.clone() })
    }

    pub fn command(&self, exe: &str) -> Command {
        let mut c = Command::new(&self.bin);
        c.arg(exe);
        self.env(&mut c);
        c
    }

    pub fn env(&self, c: &mut Command) {
        c.env("WINEPREFIX", &self.prefix).env("WINEARCH", "win64").env("WINEDEBUG", "-all");
        if self.opts.suppress_mono_gecko {
            // wine-mono/wine-gecko 설치 프롬프트를 띄우지 않는다
            c.env("WINEDLLOVERRIDES", "mscoree=;mshtml=");
        }
    }

    fn run_quiet(&self, args: &[&str]) -> bool {
        let mut c = Command::new(&self.bin);
        c.args(args);
        self.env(&mut c);
        match c.stdout(Stdio::null()).stderr(Stdio::null()).status() {
            Ok(s) => s.success(),
            Err(e) => {
                log::warn!("wine {:?} failed: {e}", args);
                false
            }
        }
    }

    pub fn wait_server(&self) {
        let mut c = Command::new(&self.server);
        c.arg("-w").env("WINEPREFIX", &self.prefix);
        let _ = c.status();
    }

    /// 이 prefix 의 모든 Windows 프로세스 종료 (TERMINATE_PROCESSES 대응)
    pub fn kill_all(&self) {
        let mut c = Command::new(&self.server);
        c.arg("-k").env("WINEPREFIX", &self.prefix);
        let _ = c.status();
    }

    pub fn is_initialized(&self) -> bool {
        self.prefix.join("system.reg").exists()
    }

    /// prefix 생성 및 기본 설정
    pub fn init_prefix(&self) -> Result<(), String> {
        std::fs::create_dir_all(&self.prefix).map_err(|e| e.to_string())?;
        if !self.is_initialized() {
            log::info!("Creating Wine prefix at {}", self.prefix.display());
            if !self.run_quiet(&["wineboot", "--init"]) {
                return Err("wineboot failed".into());
            }
            self.wait_server();
        }
        // Fusion 매니페스트의 required-os(Windows 10 build 17763) 를 만족하도록 보고 버전 지정
        self.reg_add("HKCU\\Software\\Wine", "Version", "REG_SZ", "win10");
        for (dll, mode) in DLL_OVERRIDES {
            self.reg_add("HKCU\\Software\\Wine\\DllOverrides", dll, "REG_SZ", mode);
        }
        // 글꼴 안티앨리어싱 (subpixel RGB)
        self.reg_add("HKCU\\Control Panel\\Desktop", "FontSmoothing", "REG_SZ", "2");
        self.reg_add("HKCU\\Control Panel\\Desktop", "FontSmoothingType", "REG_DWORD", "2");
        self.reg_add("HKCU\\Control Panel\\Desktop", "FontSmoothingGamma", "REG_DWORD", "1400");
        self.reg_add("HKCU\\Control Panel\\Desktop", "FontSmoothingOrientation", "REG_DWORD", "1");
        // https:// 등 외부 링크는 Wine 의 winebrowser → xdg-open 으로 Linux 기본 브라우저에서 연다
        self.wait_server();
        Ok(())
    }

    /// Wine 자체의 레지스트리(.reg 텍스트 파일)를 설정한다. Windows 레지스트리 API 와 무관.
    pub fn reg_add(&self, key: &str, name: &str, ty: &str, data: &str) -> bool {
        self.run_quiet(&["reg", "add", key, "/v", name, "/t", ty, "/d", data, "/f"])
    }

    /// DXVK (Direct3D 9/10/11 → Vulkan, zlib 라이선스 오픈소스) 설치
    pub fn install_dxvk(&self, net: &Net, cache: &Path) -> Result<(), String> {
        let marker = self.prefix.join(".ofc-dxvk");
        if marker.exists() {
            return Ok(());
        }
        let api = "https://api.github.com/repos/doitsujin/dxvk/releases/latest";
        let rel = net.get_string(api).map_err(|e| e.to_string())?;
        let v: serde_json::Value = serde_json::from_str(&rel).map_err(|e| e.to_string())?;
        let asset = v["assets"]
            .as_array()
            .and_then(|a| {
                a.iter().find(|x| {
                    let n = x["name"].as_str().unwrap_or("");
                    n.starts_with("dxvk-") && n.ends_with(".tar.gz") && !n.contains("native")
                })
            })
            .ok_or("DXVK release asset not found")?;
        let url = asset["browser_download_url"].as_str().ok_or("no url")?;
        let name = asset["name"].as_str().unwrap_or("dxvk.tar.gz");
        let tgz = cache.join(name);
        if !tgz.exists() {
            net.download_plain(url, &tgz).map_err(|e| e.to_string())?;
        }
        let tmp = cache.join("dxvk-extract");
        let _ = std::fs::remove_dir_all(&tmp);
        std::fs::create_dir_all(&tmp).map_err(|e| e.to_string())?;
        let ok = Command::new("tar").arg("-xzf").arg(&tgz).arg("-C").arg(&tmp).status().map(|s| s.success()).unwrap_or(false);
        if !ok {
            return Err("failed to extract DXVK".into());
        }
        let root = std::fs::read_dir(&tmp).map_err(|e| e.to_string())?.filter_map(Result::ok).next().ok_or("empty dxvk archive")?.path();
        let sys = self.prefix.join("drive_c/windows");
        for (arch, dst) in [("x64", sys.join("system32")), ("x32", sys.join("syswow64"))] {
            let src = root.join(arch);
            if !src.is_dir() || !dst.is_dir() {
                continue;
            }
            for e in std::fs::read_dir(&src).map_err(|e| e.to_string())?.filter_map(Result::ok) {
                let _ = std::fs::copy(e.path(), dst.join(e.file_name()));
            }
        }
        for dll in ["d3d9", "d3d10core", "d3d11", "dxgi"] {
            self.reg_add("HKCU\\Software\\Wine\\DllOverrides", dll, "REG_SZ", "native,builtin");
        }
        let _ = std::fs::remove_dir_all(&tmp);
        let _ = std::fs::write(&marker, name);
        log::info!("DXVK installed: {name}");
        Ok(())
    }

    /// Windows 실행 파일을 백그라운드로 시작 (Fusion 실행, Identity Manager 콜백 등)
    pub fn spawn_detached(&self, exe: &Path, args: &[String], log_file: Option<&Path>) -> std::io::Result<()> {
        let mut c = self.command(&exe.to_string_lossy());
        c.args(args);
        if let Some(dir) = exe.parent() {
            c.current_dir(dir);
        }
        // 실행 중인 Fusion 에는 mono/gecko 억제가 필요 없다
        c.env_remove("WINEDLLOVERRIDES");
        match log_file.and_then(|p| std::fs::File::create(p).ok()) {
            Some(f) => {
                let f2 = f.try_clone()?;
                c.stdout(f).stderr(f2);
            }
            None => {
                c.stdout(Stdio::null()).stderr(Stdio::null());
            }
        }
        c.stdin(Stdio::null());
        use std::os::unix::process::CommandExt;
        // 다운로더 종료와 무관하게 계속 실행되도록 새 세션으로 분리
        unsafe {
            c.pre_exec(|| {
                libc::setsid();
                Ok(())
            });
        }
        c.spawn().map(|_| ())
    }

    pub fn run_wait(&self, exe: &Path, args: &[String]) -> bool {
        let mut c = self.command(&exe.to_string_lossy());
        c.args(args);
        if let Some(dir) = exe.parent() {
            c.current_dir(dir);
        }
        c.stdout(Stdio::null()).stderr(Stdio::null()).status().map(|s| s.success()).unwrap_or(false)
    }
}
