//! `ofc-streamer monitor` – 설치 진행 상황과 아카이브별 다운로드/설치 과정을 표로 실시간 표시.
//!
//! 엔진이 기록하는 `<data>/status.json` 을 읽는다. 상태 파일이 없는 이전 버전 엔진이
//! 실행 중이면 다운로드 캐시(.part / .tar.xz)와 로그의 마지막 진행 이벤트로 대신 표시한다.

use crate::config::Paths;
use crate::status::{self, Row, Snapshot, State};
use std::collections::HashMap;
use std::fmt::Write as _;
use std::io::Write;
use std::time::{Duration, Instant};

pub struct Options {
    pub once: bool,
    pub all: bool,
    pub follow: bool,
    pub interval_ms: u64,
}

const RESET: &str = "\x1b[0m";
const BOLD: &str = "\x1b[1m";
const DIM: &str = "\x1b[2m";

fn term_size() -> (usize, usize) {
    let mut ws: libc::winsize = unsafe { std::mem::zeroed() };
    // SAFETY: TIOCGWINSZ 는 winsize 구조체를 채운다
    let ok = unsafe { libc::ioctl(libc::STDOUT_FILENO, libc::TIOCGWINSZ, &mut ws) } == 0;
    if ok && ws.ws_col > 0 {
        (ws.ws_col as usize, ws.ws_row as usize)
    } else {
        (120, 40)
    }
}

/// 한글 등 전각 문자를 2칸으로 계산
fn width(s: &str) -> usize {
    s.chars()
        .map(|c| {
            let u = c as u32;
            if (0x1100..=0x115F).contains(&u) || (0x2E80..=0xA4CF).contains(&u) || (0xAC00..=0xD7A3).contains(&u) || (0xF900..=0xFAFF).contains(&u) || (0xFF00..=0xFF60).contains(&u) {
                2
            } else {
                1
            }
        })
        .sum()
}

fn fit(s: &str, w: usize) -> String {
    let mut out = String::new();
    let mut cur = 0;
    for c in s.chars() {
        let cw = width(&c.to_string());
        if cur + cw > w {
            if w > 0 && cur >= w {
                out.pop();
            }
            out.push('…');
            cur = w;
            break;
        }
        out.push(c);
        cur += cw;
    }
    out + &" ".repeat(w.saturating_sub(cur))
}

fn rfit(s: &str, w: usize) -> String {
    let cw = width(s);
    if cw >= w {
        fit(s, w)
    } else {
        " ".repeat(w - cw) + s
    }
}

fn mb(b: u64) -> String {
    if b >= 1 << 30 {
        format!("{:.2} GB", b as f64 / (1u64 << 30) as f64)
    } else {
        format!("{:.1} MB", b as f64 / 1048576.0)
    }
}

fn rate(bps: f64) -> String {
    if bps < 1.0 {
        "-".into()
    } else if bps >= 1048576.0 {
        format!("{:.1} MB/s", bps / 1048576.0)
    } else {
        format!("{:.0} KB/s", bps / 1024.0)
    }
}

fn bar(frac: f64, w: usize) -> String {
    let frac = frac.clamp(0.0, 1.0);
    let full = (frac * w as f64 * 8.0).round() as usize;
    let parts = ["", "▏", "▎", "▍", "▌", "▋", "▊", "▉"];
    let mut s = "█".repeat(full / 8);
    if full / 8 < w {
        s.push_str(parts[full % 8]);
        let used = full / 8 + usize::from(full % 8 > 0);
        s.push_str(&"░".repeat(w - used));
    }
    s
}

fn hms(secs: u64) -> String {
    if secs >= 3600 {
        format!("{}:{:02}:{:02}", secs / 3600, secs / 60 % 60, secs % 60)
    } else {
        format!("{:02}:{:02}", secs / 60, secs % 60)
    }
}

fn state_label(r: &Row) -> (&'static str, &'static str) {
    match r.state {
        State::Pending => ("대기", "\x1b[90m"),
        State::Downloading if r.size > 0 && r.got >= r.size => ("검증", "\x1b[35m"),
        State::Downloading => ("다운로드", "\x1b[36m"),
        State::Verifying => ("검증", "\x1b[35m"),
        State::Extracting => ("설치(해제)", "\x1b[33m"),
        State::Done => ("완료", "\x1b[32m"),
        State::Failed => ("실패", "\x1b[31m"),
        State::Installed => ("기설치", "\x1b[34m"),
    }
}

/// 아카이브별 속도 계산 (지수 이동 평균)
#[derive(Default)]
struct Speed {
    last: HashMap<String, (u64, Instant)>,
    rate: HashMap<String, f64>,
    total: Option<(u64, Instant)>,
    total_rate: f64,
}

impl Speed {
    fn update(&mut self, rows: &[Row]) {
        let now = Instant::now();
        for r in rows {
            if let Some((got, t)) = self.last.get(&r.archive) {
                let dt = now.duration_since(*t).as_secs_f64();
                if dt > 0.05 {
                    let inst = r.got.saturating_sub(*got) as f64 / dt;
                    let e = self.rate.entry(r.archive.clone()).or_insert(inst);
                    *e = *e * 0.6 + inst * 0.4;
                }
            }
            self.last.insert(r.archive.clone(), (r.got, now));
        }
        let got: u64 = rows.iter().filter(|r| r.state != State::Installed).map(|r| r.got).sum();
        if let Some((g, t)) = self.total {
            let dt = now.duration_since(t).as_secs_f64();
            if dt > 0.05 {
                let inst = got.saturating_sub(g) as f64 / dt;
                self.total_rate = if self.total_rate == 0.0 { inst } else { self.total_rate * 0.7 + inst * 0.3 };
            }
        }
        self.total = Some((got, now));
    }
}

/// 이전 엔진(상태 파일 없음)용: 캐시 디렉터리와 로그로 근사 스냅숏 구성
fn legacy_snapshot(paths: &Paths) -> Option<Snapshot> {
    let mut rows = vec![];
    let dir = paths.cache.join("packages");
    for e in std::fs::read_dir(&dir).ok()?.filter_map(Result::ok) {
        let name = e.file_name().to_string_lossy().into_owned();
        let len = e.metadata().map(|m| m.len()).unwrap_or(0);
        let (archive, state) = if let Some(a) = name.strip_suffix(".tar.part") {
            (a.to_string(), State::Downloading)
        } else if let Some(a) = name.strip_suffix(".tar.xz") {
            (a.to_string(), State::Extracting)
        } else {
            continue;
        };
        rows.push(Row { package: "?".into(), archive, size: 0, got: len, files: 0, state, changed: 0 });
    }
    if rows.is_empty() {
        // 받는 중인 파일이 없으면 이전 엔진 작업도 없는 것으로 본다
        return None;
    }
    let log = std::fs::read_dir(&paths.logs)
        .ok()?
        .filter_map(Result::ok)
        .filter(|e| e.file_name().to_string_lossy().starts_with("streamer_"))
        .max_by_key(|e| e.metadata().and_then(|m| m.modified()).ok())?;
    let text = std::fs::read_to_string(log.path()).ok()?;
    let mut phase = String::new();
    let mut pct = 0.0;
    for line in text.lines().rev().take(400) {
        if let Some(i) = line.find("UI event: ") {
            if let Ok(v) = serde_json::from_str::<serde_json::Value>(&line[i + 10..]) {
                if v["type"] == "progress" {
                    phase = v["description"].as_str().unwrap_or("").to_string();
                    pct = v["percentage"].as_f64().unwrap_or(0.0);
                    break;
                }
            }
        }
    }
    let modified = log.metadata().ok()?.modified().ok()?.duration_since(std::time::UNIX_EPOCH).ok()?.as_secs();
    Some(Snapshot {
        pid: 0,
        process: "deploy (이전 엔진 – 근사 표시)".into(),
        version: String::new(),
        phase,
        percentage: pct,
        started: 0,
        updated: modified,
        finished: status::now().saturating_sub(modified) > 30 && rows.is_empty(),
        ok: None,
        rows,
    })
}

fn render(s: &Snapshot, sp: &Speed, opts: &Options) -> String {
    let (cols, lines) = term_size();
    let cols = cols.max(80);
    let mut o = String::new();
    let now = status::now();
    let elapsed = if s.started > 0 { hms(now.saturating_sub(s.started)) } else { "-".into() };
    let alive = status::writer_alive(s);
    let run_state = if s.finished {
        match s.ok {
            Some(true) => "\x1b[32m완료\x1b[0m",
            Some(false) => "\x1b[31m실패/취소\x1b[0m",
            None => "종료",
        }
    } else if s.pid != 0 && !alive {
        "\x1b[31m중단됨(프로세스 없음)\x1b[0m"
    } else {
        "\x1b[36m진행 중\x1b[0m"
    };
    let _ = writeln!(o, "{BOLD} Autodesk Fusion 설치 모니터{RESET}  ─  작업: {}  ·  상태: {run_state}  ·  경과 {elapsed}  {DIM}(PID {}){RESET}", s.process, s.pid);
    let _ = writeln!(o, " 단계: {}", s.phase);

    let work: Vec<&Row> = s.rows.iter().filter(|r| r.state != State::Installed && r.archive != "-").collect();
    let total: u64 = work.iter().map(|r| r.size.max(r.got)).sum();
    let got: u64 = work.iter().map(|r| r.got.min(r.size.max(r.got))).sum();
    let remain = total.saturating_sub(got);
    let eta = if sp.total_rate > 1.0 && remain > 0 { hms((remain as f64 / sp.total_rate) as u64) } else { "-".into() };
    let bw = (cols / 3).clamp(20, 50);
    let _ = writeln!(
        o,
        " 전체 {} {:5.1}%   전송 {} / {}   속도 {}   남은 시간 {}",
        bar(s.percentage / 100.0, bw),
        s.percentage,
        mb(got),
        mb(total),
        rate(sp.total_rate),
        eta
    );
    let count = |st: &[State]| s.rows.iter().filter(|r| st.contains(&r.state)).count();
    let files: usize = s.rows.iter().map(|r| r.files).sum();
    let _ = writeln!(
        o,
        " 아카이브: \x1b[32m완료 {}\x1b[0m · \x1b[36m진행 {}\x1b[0m · 대기 {} · \x1b[34m기설치 {}\x1b[0m · \x1b[31m실패 {}\x1b[0m   설치된 파일 {}개   {DIM}갱신 {}초 전{RESET}",
        count(&[State::Done]),
        count(&[State::Downloading, State::Verifying, State::Extracting]),
        count(&[State::Pending]),
        count(&[State::Installed]),
        count(&[State::Failed]),
        files,
        now.saturating_sub(s.updated)
    );
    let _ = writeln!(o);

    // 열 폭
    let w_no = 3;
    let w_size = 10;
    let w_got = 10;
    let w_speed = 10;
    let w_state = 10;
    let w_files = 6;
    let w_arch = 10;
    let w_pbar = 12;
    let fixed = w_no + w_arch + w_size + w_got + w_pbar + 7 + w_speed + w_state + w_files + 10;
    let w_pkg = cols.saturating_sub(fixed + 2).clamp(12, 40);
    let header = format!(
        " {} {} {} {} {} {} {} {} {}",
        rfit("#", w_no),
        fit("패키지", w_pkg),
        fit("아카이브", w_arch),
        rfit("크기", w_size),
        rfit("받은 양", w_got),
        fit("진행률", w_pbar + 7),
        rfit("속도", w_speed),
        fit(" 상태", w_state),
        rfit("파일", w_files)
    );
    let _ = writeln!(o, "{BOLD}{header}{RESET}");
    let _ = writeln!(o, "{DIM} {}{RESET}", "─".repeat(width(&header).saturating_sub(1).min(cols - 1)));

    // 정렬: 진행 중 → 실패 → 대기 → 완료(최근 순) → 기설치
    let mut idx: Vec<usize> = (0..s.rows.len()).collect();
    let rank = |r: &Row| match r.state {
        State::Downloading | State::Verifying | State::Extracting => 0,
        State::Failed => 1,
        State::Pending => 2,
        State::Done => 3,
        State::Installed => 4,
    };
    idx.sort_by(|&a, &b| {
        let (ra, rb) = (&s.rows[a], &s.rows[b]);
        rank(ra).cmp(&rank(rb)).then_with(|| if rank(ra) == 3 { rb.changed.cmp(&ra.changed) } else { a.cmp(&b) })
    });
    let avail = if opts.all || opts.once { usize::MAX } else { lines.saturating_sub(9).max(5) };
    let shown = idx.len().min(avail);
    for &i in idx.iter().take(shown) {
        let r = &s.rows[i];
        let size = r.size.max(r.got);
        let frac = match r.state {
            State::Done | State::Installed => 1.0,
            _ if size > 0 => r.got as f64 / size as f64,
            _ => 0.0,
        };
        let (label, color) = state_label(r);
        let speed = if matches!(r.state, State::Downloading) { rate(*sp.rate.get(&r.archive).unwrap_or(&0.0)) } else { "-".into() };
        let arch = if r.archive.len() > 8 { format!("{}…", &r.archive[..8]) } else { r.archive.clone() };
        let _ = writeln!(
            o,
            " {} {} {} {} {} {}{} {:5.1}%{RESET} {} {color}{}{RESET} {}",
            rfit(&(i + 1).to_string(), w_no),
            fit(&r.package, w_pkg),
            fit(&arch, w_arch),
            rfit(&if r.size > 0 { mb(r.size) } else { "?".into() }, w_size),
            rfit(&mb(r.got), w_got),
            color,
            bar(frac, w_pbar),
            frac * 100.0,
            rfit(&speed, w_speed),
            fit(&format!(" {label}"), w_state),
            rfit(&if r.files > 0 { r.files.to_string() } else { "-".into() }, w_files)
        );
    }
    if shown < idx.len() {
        let _ = writeln!(o, "{DIM}   … 외 {}개 (전체 보기: ofc-streamer monitor --all){RESET}", idx.len() - shown);
    }
    if !opts.once {
        let _ = writeln!(o, "{DIM}\n Ctrl+C 로 모니터 종료 (설치는 계속 진행됨){RESET}");
    }
    o
}

pub fn run(paths: &Paths, opts: &Options) -> i32 {
    let status_path = paths.data.join("status.json");
    let mut sp = Speed::default();
    let mut out = std::io::stdout();
    let mut finished_shown = 0;
    loop {
        let snap = status::read(&status_path)
            .filter(|s| s.finished || status::writer_alive(s) || opts.once)
            .or_else(|| legacy_snapshot(paths))
            .or_else(|| status::read(&status_path));
        let frame = match &snap {
            Some(s) => {
                sp.update(&s.rows);
                render(s, &sp, opts)
            }
            None => " 실행 중이거나 기록된 설치 작업이 없습니다. (상태 파일: ".to_string() + &status_path.to_string_lossy() + ")\n",
        };
        if opts.once {
            let _ = out.write_all(frame.as_bytes());
            return 0;
        }
        let _ = out.write_all(format!("\x1b[H\x1b[2J{frame}").as_bytes());
        let _ = out.flush();
        let done = snap.as_ref().map(|s| s.finished || (s.pid != 0 && !status::writer_alive(s))).unwrap_or(false);
        if done && !opts.follow {
            finished_shown += 1;
            if finished_shown > 1 {
                return 0;
            }
        }
        std::thread::sleep(Duration::from_millis(opts.interval_ms.max(200)));
    }
}
