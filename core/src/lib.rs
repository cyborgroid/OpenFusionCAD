//! OpenFusionCAD 스트리밍 엔진.
//!
//! Autodesk "Fusion Client Downloader.exe"(7-Zip SFX + Python 3.14 streamer.exe +
//! NuJSBridge 웹뷰)의 동작을 Linux 네이티브로 재구현한다. GUI(C#/Avalonia)는 이
//! 라이브러리를 C ABI(`ofc_run`)로 호출하고, CLI(`ofc-streamer`)는 직접 링크한다.

pub mod cleancache;
pub mod config;
pub mod constants;
pub mod desktop;
pub mod events;
pub mod i18n;
pub mod manifest;
pub mod net;
pub mod precheck;
pub mod process;
pub mod registry;
pub mod tasks;
pub mod wine;

use config::{Paths, Request};
use events::{Event, Sink, Ui};
use std::ffi::{c_char, c_void, CStr, CString};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

// ---------------------------------------------------------------- logging

struct FileLogger {
    file: Mutex<Option<std::fs::File>>,
    stderr: AtomicBool,
}

static LOGGER: OnceLock<&'static FileLogger> = OnceLock::new();

impl log::Log for FileLogger {
    fn enabled(&self, m: &log::Metadata) -> bool {
        m.level() <= log::Level::Debug && (m.target().starts_with("ofc") || m.level() <= log::Level::Warn)
    }
    fn log(&self, r: &log::Record) {
        if !self.enabled(r.metadata()) {
            return;
        }
        let line = format!("{} {:<5} {}\n", timestamp(), r.level(), r.args());
        if let Some(f) = self.file.lock().unwrap().as_mut() {
            let _ = f.write_all(line.as_bytes());
        }
        if self.stderr.load(Ordering::Relaxed) && r.level() <= log::Level::Info {
            let _ = std::io::stderr().write_all(line.as_bytes());
        }
    }
    fn flush(&self) {
        if let Some(f) = self.file.lock().unwrap().as_mut() {
            let _ = f.flush();
        }
    }
}

fn timestamp() -> String {
    let mut t: libc::time_t = 0;
    let mut tm: libc::tm = unsafe { std::mem::zeroed() };
    // SAFETY: 표준 C 시간 함수, 포인터는 유효
    unsafe {
        libc::time(&mut t);
        libc::localtime_r(&t, &mut tm);
    }
    format!("{:04}-{:02}-{:02} {:02}:{:02}:{:02}", tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday, tm.tm_hour, tm.tm_min, tm.tm_sec)
}

/// 로그 파일 설정 (원본과 같이 실행마다 새 파일). 반환: 로그 파일 경로
pub fn init_logging(logs: &Path, verbose_stderr: bool) -> PathBuf {
    let _ = std::fs::create_dir_all(logs);
    let name = format!("streamer_{}.log", timestamp().replace([' ', ':'], "-"));
    let path = logs.join(name);
    let logger = LOGGER.get_or_init(|| {
        let l: &'static FileLogger = Box::leak(Box::new(FileLogger { file: Mutex::new(None), stderr: AtomicBool::new(false) }));
        let _ = log::set_logger(l);
        log::set_max_level(log::LevelFilter::Debug);
        l
    });
    *logger.file.lock().unwrap() = std::fs::File::create(&path).ok();
    logger.stderr.store(verbose_stderr, Ordering::Relaxed);
    log::info!("OpenFusionCAD streamer {} (log: {})", env!("CARGO_PKG_VERSION"), path.display());
    path
}

/// 공통 진입점: 요청 실행
pub fn run_request(mut req: Request, sink: Arc<dyn Sink>, cancel: Arc<AtomicBool>, verbose: bool) -> process::Outcome {
    let paths = Paths::new(&req);
    let lang = req.lang.clone().unwrap_or_else(i18n::system_lang);
    req.lang = Some(lang.clone());
    i18n::init(&paths.resources, &lang);
    let log_file = init_logging(&paths.logs, verbose);
    log::info!("Command-Line Argument Configuration:\n{}", serde_json::to_string_pretty(&req).unwrap_or_default());
    log::info!(">>>>>>>> app id is {} ---- stream is {} ---- global install is False <<<<<<<<", req.app_id, req.stream);
    let ui = Ui::new(sink, cancel, req.quiet);
    process::Runner::new(req, ui, log_file).run()
}

// ---------------------------------------------------------------- C ABI (GUI 용)

/// 이벤트 콜백: (이벤트 JSON, 응답 버퍼, 버퍼 크기, 사용자 포인터) → 응답 길이(바이트)
pub type EventCallback = extern "C" fn(*const c_char, *mut c_char, usize, *mut c_void) -> usize;

struct FfiSink {
    cb: EventCallback,
    user: usize,
}

impl Sink for FfiSink {
    fn send(&self, ev: &Event) -> String {
        let js = CString::new(serde_json::to_string(ev).unwrap_or_default()).unwrap_or_default();
        let mut buf = vec![0u8; 8192];
        let n = (self.cb)(js.as_ptr(), buf.as_mut_ptr() as *mut c_char, buf.len(), self.user as *mut c_void);
        String::from_utf8_lossy(&buf[..n.min(buf.len())]).into_owned()
    }
}

static CANCEL: OnceLock<Arc<AtomicBool>> = OnceLock::new();

fn cancel_flag() -> Arc<AtomicBool> {
    CANCEL.get_or_init(|| Arc::new(AtomicBool::new(false))).clone()
}

/// 요청 JSON 을 실행한다. 블로킹 호출 – GUI 는 작업 스레드에서 호출해야 한다.
/// 반환: 0 성공, 1 실패/취소, 2 잘못된 요청
///
/// # Safety
/// `request_json` 은 NUL 종료 UTF-8 문자열이어야 한다.
#[no_mangle]
pub unsafe extern "C" fn ofc_run(request_json: *const c_char, cb: EventCallback, user: *mut c_void) -> i32 {
    if request_json.is_null() {
        return 2;
    }
    let s = CStr::from_ptr(request_json).to_string_lossy();
    let req: Request = match serde_json::from_str(&s) {
        Ok(r) => r,
        Err(_) => return 2,
    };
    let cancel = cancel_flag();
    cancel.store(false, Ordering::SeqCst);
    let sink = Arc::new(FfiSink { cb, user: user as usize });
    let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| run_request(req, sink, cancel, false)));
    match r {
        Ok(o) if o.ok => 0,
        _ => 1,
    }
}

/// 진행 중인 작업 취소 요청 (창 닫기 등)
#[no_mangle]
pub extern "C" fn ofc_cancel() {
    cancel_flag().store(true, Ordering::SeqCst);
}

/// 엔진 버전 문자열 (정적 수명)
#[no_mangle]
pub extern "C" fn ofc_version() -> *const c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr() as *const c_char
}

/// 로캘에 맞는 번역 문자열을 버퍼에 기록 (GUI 정적 라벨용). 반환: 바이트 길이
///
/// # Safety
/// `resources`, `lang`, `msgid` 는 NUL 종료 문자열, `out` 은 `cap` 바이트 쓰기 가능해야 한다.
#[no_mangle]
pub unsafe extern "C" fn ofc_translate(resources: *const c_char, lang: *const c_char, msgid: *const c_char, out: *mut c_char, cap: usize) -> usize {
    let res = CStr::from_ptr(resources).to_string_lossy().into_owned();
    let lang = CStr::from_ptr(lang).to_string_lossy().into_owned();
    let id = CStr::from_ptr(msgid).to_string_lossy();
    let cat = i18n::Catalog::load(Path::new(&res), &lang);
    let s = cat.tr(&id);
    let n = s.len().min(cap);
    std::ptr::copy_nonoverlapping(s.as_ptr(), out as *mut u8, n);
    n
}
