//! GNU gettext `.mo` 카탈로그 로더.
//!
//! 원본 설치기의 `resources/<lang>/LC_MESSAGES/messages.mo` 를 그대로 사용하므로
//! 원본과 동일한 번역 문구가 표시된다. Linux 이식으로 새로 생긴 문구는
//! `EXTRA_KO` 표에 보충했다(다른 언어는 영어로 표시).

use std::collections::HashMap;
use std::path::Path;
use std::sync::OnceLock;

static CATALOG: OnceLock<Catalog> = OnceLock::new();

#[derive(Default)]
pub struct Catalog {
    lang: String,
    map: HashMap<String, String>,
}

fn parse_mo(data: &[u8]) -> Option<HashMap<String, String>> {
    if data.len() < 28 {
        return None;
    }
    let magic = u32::from_le_bytes(data[0..4].try_into().ok()?);
    let rd: fn([u8; 4]) -> u32 = match magic {
        0x950412de => u32::from_le_bytes,
        0xde120495 => u32::from_be_bytes,
        _ => return None,
    };
    let word = |off: usize| -> Option<u32> { Some(rd(data.get(off..off + 4)?.try_into().ok()?)) };
    let n = word(8)? as usize;
    let orig = word(12)? as usize;
    let trans = word(16)? as usize;
    let mut map = HashMap::with_capacity(n);
    for i in 0..n {
        let (ol, oo) = (word(orig + i * 8)? as usize, word(orig + i * 8 + 4)? as usize);
        let (tl, to) = (word(trans + i * 8)? as usize, word(trans + i * 8 + 4)? as usize);
        let k = String::from_utf8_lossy(data.get(oo..oo + ol)?).into_owned();
        let v = String::from_utf8_lossy(data.get(to..to + tl)?).into_owned();
        if !k.is_empty() {
            // 복수형 항목은 첫 번째 형태만 사용
            let k = k.split('\0').next().unwrap_or_default().to_string();
            let v = v.split('\0').next().unwrap_or_default().to_string();
            map.insert(k, v);
        }
    }
    Some(map)
}

/// Linux 이식 과정에서 추가된 문구의 한국어 번역
const EXTRA_KO: &[(&str, &str)] = &[
    ("Downloading Autodesk Fusion", "Autodesk Fusion 다운로드 중"),
    ("Installing Autodesk Fusion", "Autodesk Fusion 설치 중"),
    ("Verifying packages", "패키지 확인 중"),
    ("Preparing Wine environment", "Wine 환경 준비 중"),
    ("Configuring Autodesk account sign-in", "Autodesk 계정 로그인 연동 구성 중"),
    ("Creating launchers", "실행 바로 가기 작성 중"),
    ("Finishing installation", "설치 마무리 중"),
    ("Installation complete", "설치 완료"),
    ("Starting Autodesk Fusion", "Autodesk Fusion 시작 중"),
    ("Removing files", "파일 제거 중"),
    ("Cleaning cache", "캐시 정리 중"),
    ("Wine is not installed or too old (9.0 or later is required).", "Wine이 설치되어 있지 않거나 너무 오래되었습니다(9.0 이상 필요)."),
    ("Install Wine with: sudo apt install wine64 wine32", "다음 명령으로 Wine을 설치하십시오: sudo apt install wine64 wine32"),
    ("Learn more..", "자세히 알아보기.."),
    ("The installer could not continue because of the following issues:", "다음 문제로 인해 설치를 계속할 수 없습니다:"),
    ("Autodesk Fusion is not installed.", "Autodesk Fusion이 설치되어 있지 않습니다."),
    ("Uninstall complete", "설치제거 완료"),
    ("Clean cache complete", "캐시 정리 완료"),
    ("Download failed: {}", "다운로드 실패: {}"),
    ("Checksum mismatch for package {}", "패키지 {}의 체크섬이 일치하지 않습니다"),
    ("Proxy authentication required", "프록시 인증 필요"),
    ("Enter the user name and password for the proxy server.", "프록시 서버의 사용자 이름과 암호를 입력하십시오."),
    ("Your Linux user does not have write access to {}.", "현재 Linux 사용자에게 {}에 대한 쓰기 권한이 없습니다."),
];

impl Catalog {
    pub fn load(resources: &Path, lang: &str) -> Catalog {
        let lang = normalize_lang(lang);
        let mut map = HashMap::new();
        let mo = resources.join("locale").join(&lang).join("LC_MESSAGES").join("messages.mo");
        if let Ok(data) = std::fs::read(&mo) {
            if let Some(m) = parse_mo(&data) {
                map = m;
            }
        }
        if lang == "ko" {
            for (k, v) in EXTRA_KO {
                map.entry(k.to_string()).or_insert_with(|| v.to_string());
            }
        }
        Catalog { lang, map }
    }

    pub fn tr(&self, s: &str) -> String {
        self.map.get(s).cloned().unwrap_or_else(|| s.to_string())
    }
}

/// 시스템 로캘(`ko_KR.UTF-8` 등) → 원본 리소스 디렉터리 이름(`ko`)
pub fn normalize_lang(lang: &str) -> String {
    let l = lang.split(['.', '@']).next().unwrap_or("en");
    let l = l.split(['_', '-']).next().unwrap_or("en").to_ascii_lowercase();
    match l.as_str() {
        "ko" | "ja" | "zh" | "de" | "fr" | "it" | "es" | "pt" | "pl" | "tr" | "en" => l,
        _ => "en".into(),
    }
}

pub fn system_lang() -> String {
    for var in ["LC_ALL", "LC_MESSAGES", "LANG"] {
        if let Ok(v) = std::env::var(var) {
            if !v.is_empty() && v != "C" && v != "POSIX" {
                return normalize_lang(&v);
            }
        }
    }
    "en".into()
}

pub fn init(resources: &Path, lang: &str) {
    let _ = CATALOG.set(Catalog::load(resources, lang));
}

pub fn lang() -> String {
    CATALOG.get().map(|c| c.lang.clone()).unwrap_or_else(|| "en".into())
}

/// 번역 함수 (gettext `_()` 대응)
pub fn tr(s: &str) -> String {
    match CATALOG.get() {
        Some(c) => c.tr(s),
        None => s.to_string(),
    }
}

/// `{}` 위치 인자를 순서대로 채우는 원본 Python `str.format` 대응
pub fn trf(s: &str, args: &[&str]) -> String {
    let mut out = tr(s);
    for a in args {
        if let Some(i) = out.find("{}") {
            out.replace_range(i..i + 2, a);
        }
    }
    out
}
