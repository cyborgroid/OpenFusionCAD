//! 원본 JS `ProcessProgressSink` 와 1:1 대응되는 UI 이벤트 프로토콜.
//!
//! 원본은 Python 프로세스가 NuJSBridge(Windows 전용 웹뷰)를 통해
//! `progress / reporterror / promptuser / informuser / precheckfailure /
//! provideproxycreds / abortuninstall` 를 호출하고 `getresult()` 를 폴링했다.
//! 여기서는 같은 의미의 이벤트를 JSON 으로 직렬화해 콜백(Sink)에 전달하고,
//! 응답이 필요한 이벤트는 콜백의 반환 문자열을 결과로 사용한다.

use serde::Serialize;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

#[derive(Debug, Clone, Serialize)]
pub struct PrecheckItem {
    pub description: String,
    pub url: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(tag = "type", rename_all = "lowercase")]
pub enum Event {
    /// 진행 상황. `percentage` 는 0..=100, `update_bg` 가 false 면 배경 전환 생략
    Progress {
        step: u32,
        step_total: u32,
        description: String,
        percentage: f64,
        update_bg: bool,
    },
    /// "Problem installing Fusion" 오류 대화상자. 응답: "OK"
    ReportError {
        title: String,
        what_label: String,
        what: String,
        detail: String,
        todo_label: String,
        todo: String,
        log_dir: String,
        log_file: String,
        primary: String,
        secondary: String,
    },
    /// 예/아니오 질의 대화상자. 응답: "Yes" | "No"
    PromptUser {
        title: String,
        body: String,
        primary: String,
        secondary: String,
    },
    /// 확인 버튼만 있는 안내 대화상자. 응답: "Yes"
    InformUser {
        title: String,
        body: String,
        primary: String,
        link: Option<String>,
    },
    /// 사전 점검 실패 패널. 응답: "retest" | "cancel"
    PrecheckFailure {
        paragraph: String,
        items: Vec<PrecheckItem>,
        hyperlink: String,
        btn_retest: String,
        btn_cancel: String,
    },
    /// 프록시 인증 대화상자. 응답: {"username":..,"password":..} | "Cancel"
    ProvideProxyCreds { summary: String, message: String },
    /// 설치제거 설문 패널. 응답: "Yes"(계속) | "No"(취소)
    AbortUninstall {
        title: String,
        continue_label: String,
        cancel_label: String,
    },
    /// 작업 종료
    Complete { ok: bool, message: String },
}

impl Event {
    pub fn needs_response(&self) -> bool {
        !matches!(self, Event::Progress { .. } | Event::Complete { .. })
    }
}

/// 이벤트 수신자. GUI(C#) 는 FFI 콜백으로, CLI 는 터미널로 구현한다.
pub trait Sink: Send + Sync {
    /// 이벤트를 전달하고, 응답이 필요한 이벤트면 응답 문자열을 반환한다.
    fn send(&self, ev: &Event) -> String;
}

/// 여러 스레드에서 안전하게 이벤트를 보내기 위한 래퍼 + 취소 플래그
#[derive(Clone)]
pub struct Ui {
    sink: Arc<dyn Sink>,
    lock: Arc<Mutex<()>>,
    cancel: Arc<AtomicBool>,
    pub quiet: bool,
}

impl Ui {
    pub fn new(sink: Arc<dyn Sink>, cancel: Arc<AtomicBool>, quiet: bool) -> Self {
        Ui { sink, lock: Arc::new(Mutex::new(())), cancel, quiet }
    }

    pub fn send(&self, ev: Event) -> String {
        let _g = self.lock.lock().unwrap_or_else(|e| e.into_inner());
        if let Ok(js) = serde_json::to_string(&ev) {
            log::debug!("UI event: {}", js);
        }
        let r = self.sink.send(&ev);
        if ev.needs_response() {
            log::info!("UI response: {}", r);
        }
        r
    }

    pub fn progress(&self, step: u32, step_total: u32, description: &str, pct: f64) {
        self.send(Event::Progress {
            step,
            step_total,
            description: description.to_string(),
            percentage: pct.clamp(0.0, 100.0),
            update_bg: true,
        });
    }

    pub fn cancelled(&self) -> bool {
        self.cancel.load(Ordering::SeqCst)
    }

    pub fn cancel_flag(&self) -> Arc<AtomicBool> {
        self.cancel.clone()
    }
}
