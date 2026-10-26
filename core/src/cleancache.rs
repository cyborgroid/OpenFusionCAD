//! 캐시 정리 (cleancache.py / clean_cache_config.json 대응)
//!
//! 원본 설정의 "windows" 항목을 그대로 읽고 환경 변수를 Wine prefix 안의 경로로 매핑한다.
//! `<o2id>`(사용자 Autodesk ID 폴더)는 해당 위치의 모든 하위 디렉터리로 확장한다.

use crate::config::Paths;
use serde_json::Value;
use std::fs;
use std::path::{Path, PathBuf};

pub struct Options {
    pub force_all: bool,
    pub remove_user_data: bool,
    pub remove_user_cred: bool,
}

fn map_env(var: &str, paths: &Paths) -> Option<PathBuf> {
    Some(match var.to_ascii_uppercase().as_str() {
        "LOCALAPPDATA" => paths.local_appdata(),
        "APPDATA" => paths.roaming_appdata(),
        "TMP" | "TEMP" => paths.win_temp(),
        "USERPROFILE" => paths.win_profile(),
        "WINDIR" => paths.prefix.join("drive_c/windows"),
        _ => return None,
    })
}

/// `%LOCALAPPDATA%\Autodesk\...\<o2id>\x` → 실제 경로 목록
fn resolve(template: &str, paths: &Paths) -> Vec<PathBuf> {
    let t = template.replace('\\', "/");
    let Some(rest) = t.strip_prefix('%') else { return vec![] };
    let Some((var, tail)) = rest.split_once('%') else { return vec![] };
    let Some(base) = map_env(var, paths) else { return vec![] };
    let mut cur = vec![base];
    for comp in tail.split('/').filter(|c| !c.is_empty()) {
        let mut next = vec![];
        for c in &cur {
            if comp == "<o2id>" {
                if let Ok(rd) = fs::read_dir(c) {
                    next.extend(rd.filter_map(Result::ok).filter(|e| e.path().is_dir()).map(|e| e.path()));
                }
            } else if comp.contains("<build-number>") {
                let prefix = comp.split("<build-number>").next().unwrap_or("");
                if let Ok(rd) = fs::read_dir(c) {
                    next.extend(rd.filter_map(Result::ok).filter(|e| e.file_name().to_string_lossy().starts_with(prefix)).map(|e| e.path()));
                }
            } else {
                next.push(c.join(comp));
            }
        }
        cur = next;
    }
    cur
}

fn remove(p: &Path, is_file: bool, filter: &str) -> usize {
    if is_file || filter.is_empty() {
        if !p.exists() {
            return 0;
        }
        let r = if p.is_dir() { fs::remove_dir_all(p) } else { fs::remove_file(p) };
        return r.is_ok() as usize;
    }
    // 디렉터리 + 필터: 이름에 필터("7Z|7z" 등) 중 하나를 포함하는 항목만 제거
    let pats: Vec<&str> = filter.split('|').collect();
    let mut n = 0;
    if let Ok(rd) = fs::read_dir(p) {
        for e in rd.filter_map(Result::ok) {
            let name = e.file_name().to_string_lossy().into_owned();
            if pats.iter().any(|f| name.contains(f)) {
                let path = e.path();
                let ok = if path.is_dir() { fs::remove_dir_all(&path) } else { fs::remove_file(&path) };
                n += ok.is_ok() as usize;
            }
        }
    }
    n
}

fn apply(items: &Value, paths: &Paths, force_all: bool) -> usize {
    let mut n = 0;
    for it in items.as_array().into_iter().flatten() {
        let flag = it["CleanFlag"].as_str().unwrap_or("Default");
        if flag == "ForceAll" && !force_all {
            continue;
        }
        let is_file = it["isFile"].as_bool().unwrap_or(false);
        let filter = it["filter"].as_str().unwrap_or("");
        for p in resolve(it["path"].as_str().unwrap_or(""), paths) {
            // 파일 항목의 filter 는 파일명 확인용 – 경로 자체가 대상
            n += remove(&p, is_file, if is_file { "" } else { filter });
        }
    }
    n
}

pub fn run(paths: &Paths, opts: &Options) -> Result<usize, String> {
    let cfg: Value = serde_json::from_str(
        &fs::read_to_string(paths.resources.join("clean_cache_config.json")).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    let win = &cfg["windows"];
    let mut n = apply(&win["Items"], paths, opts.force_all);
    if opts.remove_user_data {
        n += apply(&win["User_Data_Items"]["Offline"], paths, true);
    }
    if opts.remove_user_cred {
        n += apply(&win["User_Data_Items"]["Credential"], paths, true);
    }
    // 다운로더 자체의 패키지 캐시
    if opts.force_all {
        let _ = fs::remove_dir_all(paths.cache.join("packages"));
    }
    log::info!("clean cache removed {n} items");
    Ok(n)
}
