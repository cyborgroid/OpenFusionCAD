//! 네트워크 계층 (netlib.py / resilient_url_open.py / retry_algorithm.py 대응)
//!
//! - 시스템 CA 저장소 사용 (원본은 동봉된 certs.pem 사용)
//! - http(s)_proxy 환경 변수 자동 인식 + 407 응답 시 프록시 자격 증명 요청
//! - Range 요청 기반 이어받기 + 지수 백오프 재시도

use crate::events::{Event, Ui};
use crate::i18n::tr;
use sha1::{Digest, Sha1};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, RwLock};
use std::time::Duration;

const USER_AGENT: &str = concat!("OpenFusionCAD-Streamer/", env!("CARGO_PKG_VERSION"), " (Linux)");
const MAX_RETRIES: u32 = 6;

#[derive(Clone)]
pub struct Net {
    agent: Arc<RwLock<ureq::Agent>>,
    proxy_url: Option<String>,
    ui: Ui,
}

fn env_proxy() -> Option<String> {
    for v in ["https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY", "http_proxy", "HTTP_PROXY"] {
        if let Ok(p) = std::env::var(v) {
            if !p.trim().is_empty() {
                return Some(p.trim().to_string());
            }
        }
    }
    None
}

fn build_agent(proxy: Option<&str>) -> ureq::Agent {
    let mut b = ureq::AgentBuilder::new()
        .user_agent(USER_AGENT)
        .timeout_connect(Duration::from_secs(20))
        .timeout_read(Duration::from_secs(60))
        .redirects(5);
    if let Some(p) = proxy {
        match ureq::Proxy::new(p) {
            Ok(px) => b = b.proxy(px),
            Err(e) => log::warn!("invalid proxy '{p}': {e}"),
        }
    }
    b.build()
}

#[derive(Debug)]
pub enum NetError {
    NotFound(String),
    Cancelled,
    Other(String),
}

impl std::fmt::Display for NetError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            NetError::NotFound(u) => write!(f, "not found: {u}"),
            NetError::Cancelled => write!(f, "cancelled"),
            NetError::Other(s) => write!(f, "{s}"),
        }
    }
}

impl Net {
    pub fn new(ui: Ui) -> Net {
        let proxy = env_proxy();
        if let Some(p) = &proxy {
            log::info!("ProxyMode: USE_PROXY({p})");
        } else {
            log::info!("ProxyMode: NO_PROXY");
        }
        Net { agent: Arc::new(RwLock::new(build_agent(proxy.as_deref()))), proxy_url: proxy, ui }
    }

    fn agent(&self) -> ureq::Agent {
        self.agent.read().unwrap().clone()
    }

    /// 407 응답 시 원본 `provideproxycreds` 대화상자와 같은 방식으로 자격 증명을 받는다.
    fn ask_proxy_creds(&self) -> bool {
        let Some(proxy) = &self.proxy_url else { return false };
        let resp = self.ui.send(Event::ProvideProxyCreds {
            summary: tr("Proxy authentication required"),
            message: tr("Enter the user name and password for the proxy server."),
        });
        let Ok(v) = serde_json::from_str::<serde_json::Value>(&resp) else { return false };
        let (Some(u), Some(p)) = (v["username"].as_str(), v["password"].as_str()) else { return false };
        let (scheme, rest) = proxy.split_once("://").unwrap_or(("http", proxy.as_str()));
        let host = rest.rsplit('@').next().unwrap_or(rest);
        let url = format!("{scheme}://{}:{}@{host}", urlencode(u), urlencode(p));
        *self.agent.write().unwrap() = build_agent(Some(&url));
        true
    }

    fn get(&self, url: &str, range_from: u64) -> Result<ureq::Response, NetError> {
        let mut attempt = 0;
        loop {
            if self.ui.cancelled() {
                return Err(NetError::Cancelled);
            }
            let mut req = self.agent().get(url);
            if range_from > 0 {
                req = req.set("Range", &format!("bytes={range_from}-"));
            }
            if let Ok(tok) = std::env::var("NEUTRON_STREAMER_ENABLE_TOKEN") {
                // 원본과 동일: Artifactory 토큰 접근 (사내 미러 등)
                req = req.set("Authorization", &format!("Bearer {tok}"));
            }
            match req.call() {
                Ok(r) => return Ok(r),
                Err(ureq::Error::Status(407, _)) if self.ask_proxy_creds() => continue,
                // CDN 은 없는 객체에 대해 302 → NotFound 페이지 → 403 을 돌려준다
                Err(ureq::Error::Status(code @ (403 | 404), r)) => {
                    log::warn!("{url} → HTTP {code} ({})", r.get_url());
                    return Err(NetError::NotFound(url.to_string()));
                }
                Err(e) => {
                    attempt += 1;
                    if attempt > MAX_RETRIES {
                        return Err(NetError::Other(format!("{e}, url:{url}")));
                    }
                    let wait = Duration::from_millis(500 * (1 << attempt.min(6)));
                    log::warn!("Failed to request {url} ({e}). Retrying in {wait:?}");
                    std::thread::sleep(wait);
                }
            }
        }
    }

    pub fn get_string(&self, url: &str) -> Result<String, NetError> {
        self.get_with_etag(url).map(|(s, _)| s)
    }

    /// 본문과 ETag 를 함께 반환 (WRITE_STREAMER_OPTIONS 의 ETag 기록용)
    pub fn get_with_etag(&self, url: &str) -> Result<(String, Option<String>), NetError> {
        log::info!("Opening manifest at: {url}");
        let r = self.get(url, 0)?;
        let final_url = r.get_url().to_string();
        let etag = r.header("ETag").map(String::from);
        let s = r.into_string().map_err(|e| NetError::Other(e.to_string()))?;
        if final_url.contains("/message/NotFound") {
            return Err(NetError::NotFound(url.into()));
        }
        Ok((s, etag))
    }

    /// 재시도 없이 짧은 타임아웃으로 가져오기 (실행 전 업데이트 확인용 – 오프라인이면 즉시 포기)
    pub fn get_quick(&self, url: &str) -> Option<String> {
        let r = self.agent().get(url).timeout(Duration::from_secs(6)).call().ok()?;
        if r.get_url().contains("/message/NotFound") {
            return None;
        }
        r.into_string().ok()
    }

    /// 간단한 연결 확인 (prechecks.CheckNetworkConnectivity)
    pub fn reachable(&self, url: &str) -> Result<(), String> {
        match self.agent().head(url).timeout(Duration::from_secs(15)).call() {
            Ok(_) | Err(ureq::Error::Status(_, _)) => Ok(()),
            Err(ureq::Error::Transport(t)) => Err(t.to_string()),
        }
    }

    /// 아카이브 다운로드: `.part` 이어받기 → SHA-1 검증 → 최종 파일로 이름 변경.
    /// `counter` 에 받은 바이트 수를 누적한다(진행률 계산용).
    pub fn download_verified(&self, url: &str, dest: &Path, sha1_hex: &str, counter: &AtomicU64) -> Result<(), NetError> {
        if dest.exists() {
            if file_sha1(dest).map(|h| h.eq_ignore_ascii_case(sha1_hex)).unwrap_or(false) {
                counter.fetch_add(fs::metadata(dest).map(|m| m.len()).unwrap_or(0), Ordering::Relaxed);
                return Ok(());
            }
            let _ = fs::remove_file(dest);
        }
        let part = dest.with_extension("part");
        let mut retries = 0;
        loop {
            let have = fs::metadata(&part).map(|m| m.len()).unwrap_or(0);
            counter.fetch_add(have, Ordering::Relaxed);
            let res = self.fetch_into(url, &part, have, counter);
            match res {
                Ok(()) => {
                    let h = file_sha1(&part).map_err(|e| NetError::Other(e.to_string()))?;
                    if h.eq_ignore_ascii_case(sha1_hex) {
                        fs::rename(&part, dest).map_err(|e| NetError::Other(e.to_string()))?;
                        return Ok(());
                    }
                    log::error!("checksum mismatch for {url}: got {h}");
                    let len = fs::metadata(&part).map(|m| m.len()).unwrap_or(0);
                    counter.fetch_sub(len, Ordering::Relaxed);
                    let _ = fs::remove_file(&part);
                    retries += 1;
                    if retries > 2 {
                        return Err(NetError::Other(crate::i18n::trf("Checksum mismatch for package {}", &[sha1_hex])));
                    }
                }
                Err(NetError::Other(e)) => {
                    let len = fs::metadata(&part).map(|m| m.len()).unwrap_or(0);
                    counter.fetch_sub(len, Ordering::Relaxed);
                    retries += 1;
                    if retries > MAX_RETRIES {
                        return Err(NetError::Other(e));
                    }
                    log::warn!("Read operation error: {e}... recovering (reopen from {len} bytes)");
                    std::thread::sleep(Duration::from_secs(retries as u64 * 2));
                }
                Err(e) => return Err(e),
            }
        }
    }

    fn fetch_into(&self, url: &str, part: &Path, from: u64, counter: &AtomicU64) -> Result<(), NetError> {
        let r = self.get(url, from)?;
        let resumed = r.status() == 206;
        let mut f: File = if resumed {
            OpenOptions::new().append(true).open(part)
        } else {
            if from > 0 {
                counter.fetch_sub(from, Ordering::Relaxed);
            }
            File::create(part)
        }
        .map_err(|e| NetError::Other(e.to_string()))?;
        let mut rd = r.into_reader();
        let mut buf = vec![0u8; 256 * 1024];
        loop {
            if self.ui.cancelled() {
                return Err(NetError::Cancelled);
            }
            let n = rd.read(&mut buf).map_err(|e| NetError::Other(e.to_string()))?;
            if n == 0 {
                break;
            }
            f.write_all(&buf[..n]).map_err(|e| NetError::Other(e.to_string()))?;
            counter.fetch_add(n as u64, Ordering::Relaxed);
        }
        f.flush().map_err(|e| NetError::Other(e.to_string()))?;
        Ok(())
    }

    /// 일반 파일 다운로드 (DXVK 등 부가 구성요소)
    pub fn download_plain(&self, url: &str, dest: &Path) -> Result<(), NetError> {
        let r = self.get(url, 0)?;
        let mut f = File::create(dest).map_err(|e| NetError::Other(e.to_string()))?;
        std::io::copy(&mut r.into_reader(), &mut f).map_err(|e| NetError::Other(e.to_string()))?;
        Ok(())
    }
}

pub fn file_sha1(p: &Path) -> std::io::Result<String> {
    let mut f = File::open(p)?;
    let mut h = Sha1::new();
    let mut buf = vec![0u8; 1 << 20];
    loop {
        let n = f.read(&mut buf)?;
        if n == 0 {
            break;
        }
        h.update(&buf[..n]);
    }
    Ok(hex::encode(h.finalize()))
}

fn urlencode(s: &str) -> String {
    s.bytes()
        .map(|b| match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => (b as char).to_string(),
            _ => format!("%{b:02X}"),
        })
        .collect()
}
