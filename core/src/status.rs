//! 아카이브별 진행 상태 공유 (`<data>/status.json`).
//!
//! 설치 엔진이 주기적으로 기록하고 `ofc-streamer monitor` 가 읽어 표로 보여 준다.
//! 원자적 교체(임시 파일 → rename)로 쓰므로 읽는 쪽이 반쯤 쓴 파일을 보지 않는다.

use serde::{Deserialize, Serialize};
use std::path::Path;
use std::sync::atomic::{AtomicU64, AtomicU8, AtomicUsize, Ordering};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
#[repr(u8)]
pub enum State {
    Pending = 0,
    Downloading = 1,
    Verifying = 2,
    Extracting = 3,
    Done = 4,
    Failed = 5,
    /// 이미 같은 체크섬으로 설치되어 있어 건너뜀
    Installed = 6,
}

impl State {
    fn from_u8(v: u8) -> State {
        match v {
            1 => State::Downloading,
            2 => State::Verifying,
            3 => State::Extracting,
            4 => State::Done,
            5 => State::Failed,
            6 => State::Installed,
            _ => State::Pending,
        }
    }
}

/// 작업 스레드가 갱신하는 아카이브 한 개의 상태
pub struct JobStat {
    pub package: String,
    pub archive: String,
    pub size: AtomicU64,
    pub got: AtomicU64,
    pub files: AtomicUsize,
    state: AtomicU8,
    /// 상태 전환 시각(초) – 완료 순서 정렬용
    pub changed: AtomicU64,
}

impl JobStat {
    pub fn new(package: String, archive: String, size: u64, state: State) -> JobStat {
        JobStat {
            package,
            archive,
            size: AtomicU64::new(size),
            got: AtomicU64::new(if state == State::Installed { size } else { 0 }),
            files: AtomicUsize::new(0),
            state: AtomicU8::new(state as u8),
            changed: AtomicU64::new(now()),
        }
    }
    pub fn set(&self, s: State) {
        self.state.store(s as u8, Ordering::Relaxed);
        self.changed.store(now(), Ordering::Relaxed);
    }
    pub fn state(&self) -> State {
        State::from_u8(self.state.load(Ordering::Relaxed))
    }
    fn row(&self) -> Row {
        Row {
            package: self.package.clone(),
            archive: self.archive.clone(),
            size: self.size.load(Ordering::Relaxed),
            got: self.got.load(Ordering::Relaxed),
            files: self.files.load(Ordering::Relaxed),
            state: self.state(),
            changed: self.changed.load(Ordering::Relaxed),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Row {
    pub package: String,
    pub archive: String,
    pub size: u64,
    pub got: u64,
    pub files: usize,
    pub state: State,
    pub changed: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Snapshot {
    pub pid: u32,
    pub process: String,
    pub version: String,
    pub phase: String,
    pub percentage: f64,
    pub started: u64,
    pub updated: u64,
    pub finished: bool,
    pub ok: Option<bool>,
    pub rows: Vec<Row>,
}

pub fn now() -> u64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

pub fn snapshot(stats: &[JobStat]) -> Vec<Row> {
    stats.iter().map(JobStat::row).collect()
}

pub fn write(path: &Path, snap: &Snapshot) {
    let tmp = path.with_extension("json.tmp");
    if let Ok(js) = serde_json::to_vec(snap) {
        if std::fs::write(&tmp, js).is_ok() {
            let _ = std::fs::rename(&tmp, path);
        }
    }
}

pub fn read(path: &Path) -> Option<Snapshot> {
    serde_json::from_slice(&std::fs::read(path).ok()?).ok()
}

/// 기록한 프로세스가 아직 살아 있는지
pub fn writer_alive(s: &Snapshot) -> bool {
    // SAFETY: 시그널 0 은 존재 확인만 한다
    s.pid != 0 && unsafe { libc::kill(s.pid as libc::pid_t, 0) } == 0
}
