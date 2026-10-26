//! 사전 점검 (prechecks.py 대응)
//!
//! 원본 점검 항목과 Linux 대응:
//! | 원본                      | Linux                                             |
//! |---------------------------|---------------------------------------------------|
//! | CheckMediaFeaturePack     | 제거 (Windows N 에디션 전용 제한)                 |
//! | CheckRegistryNotFound     | 제거 → CheckWine (Wine ≥ 9.0 존재 여부)           |
//! | CheckFolderAccessibility  | 유지 (데이터/캐시 디렉터리 쓰기 권한)             |
//! | CheckSSLCertificateIssue  | 유지 (시스템 CA 로 TLS 검증)                      |
//! | CheckNetworkConnectivity  | 유지                                              |
//! | DiskSpaceFull             | 유지 (statvfs, 기본 8GB)                          |

use crate::config::Paths;
use crate::events::PrecheckItem;
use crate::i18n::{tr, trf};
use crate::net::Net;
use crate::wine;
use serde_json::Value;
use std::path::Path;

pub struct Failure {
    pub name: &'static str,
    pub item: PrecheckItem,
}

fn cfg_url(cfg: &Value, key: &str, fallback: &str) -> String {
    cfg["prechecks"][key]["url"].as_str().unwrap_or(fallback).to_string()
}

pub fn free_bytes(p: &Path) -> Option<u64> {
    let mut p = p.to_path_buf();
    while !p.exists() {
        p = p.parent()?.to_path_buf();
    }
    let c = std::ffi::CString::new(p.to_string_lossy().as_bytes()).ok()?;
    let mut st: libc::statvfs = unsafe { std::mem::zeroed() };
    // SAFETY: c 는 유효한 C 문자열, st 는 쓰기 가능한 구조체
    if unsafe { libc::statvfs(c.as_ptr(), &mut st) } != 0 {
        return None;
    }
    Some(st.f_bavail as u64 * st.f_frsize as u64)
}

fn writable(dir: &Path) -> bool {
    if std::fs::create_dir_all(dir).is_err() {
        return false;
    }
    let probe = dir.join(".ofc-write-test");
    let ok = std::fs::write(&probe, b"ok").is_ok();
    let _ = std::fs::remove_file(probe);
    ok
}

pub fn run(paths: &Paths, net: &Net, root_url: &str, wine_bin: &str) -> Vec<Failure> {
    let cfg: Value = std::fs::read_to_string(paths.resources.join("precheck_config.json"))
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or(Value::Null);
    let mut out = vec![];

    // Wine (CheckRegistryNotFound / CheckMediaFeaturePack 대체)
    let wine_ok = wine::which(wine_bin)
        .or_else(|| wine::which("wine64"))
        .and_then(|b| wine::version(&b))
        .map(|(maj, _)| maj >= 9)
        .unwrap_or(false);
    if !wine_ok {
        out.push(Failure {
            name: "CheckWine",
            item: PrecheckItem {
                description: format!(
                    "{} {}",
                    tr("Wine is not installed or too old (9.0 or later is required)."),
                    tr("Install Wine with: sudo apt install wine64 wine32")
                ),
                url: "https://gitlab.winehq.org/wine/wine/-/wikis/Debian-Ubuntu".into(),
            },
        });
    }

    // 폴더 접근 권한
    for d in [&paths.data, &paths.cache] {
        if !writable(d) {
            out.push(Failure {
                name: "CheckFolderAccessibility",
                item: PrecheckItem {
                    description: format!("{} {}", tr("Access to install directories failed."), trf("Your Linux user does not have write access to {}.", &[&d.to_string_lossy()])),
                    url: cfg_url(&cfg, "CheckFolderAccessibility", ""),
                },
            });
            break;
        }
    }

    // 디스크 공간
    let need_gb: u64 = cfg["default_size"].as_str().and_then(|s| s.parse().ok()).unwrap_or(crate::constants::DEFAULT_REQUIRED_GB);
    if let Some(free) = free_bytes(&paths.data) {
        if free < need_gb * 1024 * 1024 * 1024 {
            out.push(Failure {
                name: "DiskSpaceFull",
                item: PrecheckItem {
                    description: trf("System is out of disk space. Please create {} GB of space before proceeding.", &[&need_gb.to_string()]),
                    url: cfg_url(&cfg, "DiskSpaceFull", ""),
                },
            });
        }
    }

    // 네트워크 & SSL
    match net.reachable(root_url) {
        Ok(()) => {}
        Err(e) => {
            let ssl = e.to_lowercase().contains("certificate") || e.to_lowercase().contains("tls");
            out.push(if ssl {
                Failure {
                    name: "CheckSSLCertificateIssue",
                    item: PrecheckItem { description: tr("Failed to verify SSL certificate."), url: cfg_url(&cfg, "CheckSSLCertificateIssue", "") },
                }
            } else {
                Failure {
                    name: "CheckNetworkConnectivity",
                    item: PrecheckItem {
                        description: tr("Unable to connect to Network. Please check the connection."),
                        url: cfg_url(&cfg, "CheckNetworkConnectivity", ""),
                    },
                }
            });
        }
    }
    for f in &out {
        log::warn!("precheck failed: {} – {}", f.name, f.item.description);
    }
    out
}
