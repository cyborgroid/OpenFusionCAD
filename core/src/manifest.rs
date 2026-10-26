//! 매니페스트 모델 (adsk.dls.streamer.manifests 대응)
//!
//! - 애플리케이션: `{root}{platform}/{app_id}/full.json`
//! - 패키지:       `{root}packages/{checksum}.json`
//! - 아카이브:     `{root}packages/{non-patched checksum}.tar.xz` (SHA-1 = 파일명)

use serde::Deserialize;
use serde_json::{Map, Value};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Deserialize)]
pub struct PackageRef {
    pub checksum: String,
    #[serde(rename = "compressed-size", default)]
    pub compressed_size: u64,
    #[serde(default)]
    pub size: u64,
}

#[derive(Debug, Clone, Deserialize, Default)]
pub struct StreamerInfo {
    #[serde(default)]
    pub checksum: String,
    #[serde(rename = "feature-version", default)]
    pub feature_version: String,
    #[serde(rename = "release-id", default)]
    pub release_id: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct AppManifest {
    #[serde(rename = "build-version", default)]
    pub build_version: String,
    #[serde(rename = "release-version", default)]
    pub release_version: String,
    #[serde(rename = "launcher-path", default)]
    pub launcher_path: String,
    #[serde(default)]
    pub packages: Vec<PackageRef>,
    #[serde(rename = "pre-install-tasks", default)]
    pub pre_install_tasks: Vec<String>,
    #[serde(rename = "initial-install-delete-paths", default)]
    pub initial_install_delete_paths: Vec<String>,
    #[serde(default)]
    pub properties: Map<String, Value>,
    #[serde(default)]
    pub streamer: StreamerInfo,
    #[serde(rename = "full-checksum", default)]
    pub full_checksum: String,
    #[serde(skip)]
    pub raw: String,
}

impl AppManifest {
    pub fn parse(raw: &str) -> Result<Self, String> {
        let mut m: AppManifest = serde_json::from_str(raw).map_err(|e| format!("invalid app manifest: {e}"))?;
        m.raw = raw.to_string();
        Ok(m)
    }
    pub fn display_name(&self) -> String {
        self.prop_str("display-name").unwrap_or_else(|| "Autodesk Fusion".into())
    }
    pub fn exec_name(&self) -> String {
        self.prop_str("exec-name").unwrap_or_else(|| "Fusion360.exe".into())
    }
    pub fn session_file_name(&self) -> Option<String> {
        self.prop_str("session-file-name")
    }
    pub fn sub_applications(&self) -> Vec<String> {
        self.properties
            .get("sub-applications")
            .and_then(Value::as_array)
            .map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect())
            .unwrap_or_default()
    }
    fn prop_str(&self, k: &str) -> Option<String> {
        self.properties.get(k).and_then(Value::as_str).map(String::from)
    }
    pub fn version(&self) -> String {
        format!("{} ({})", self.build_version, self.release_version)
    }
}

/// 패키지 매니페스트의 파일/디렉터리 트리 노드
#[derive(Debug, Clone, Deserialize, Default)]
pub struct DirNode {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub destination: Option<String>,
    #[serde(default)]
    pub files: Vec<FileNode>,
    #[serde(default)]
    pub directories: Vec<DirNode>,
    #[serde(default)]
    pub properties: Map<String, Value>,
}

#[derive(Debug, Clone, Deserialize, Default)]
pub struct FileNode {
    pub name: String,
    #[serde(default)]
    pub properties: Map<String, Value>,
}

impl FileNode {
    pub fn id(&self) -> Option<&str> {
        self.properties.get("id").and_then(Value::as_str)
    }
}

#[derive(Debug, Clone, Deserialize)]
pub struct Reference {
    pub id: String,
    #[serde(default)]
    pub properties: Map<String, Value>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct PackageManifest {
    #[serde(rename = "non-patched", default)]
    pub non_patched: Vec<String>,
    #[serde(default)]
    pub root: DirNode,
    #[serde(default)]
    pub properties: Map<String, Value>,
    #[serde(default)]
    pub references: Vec<Reference>,
    #[serde(rename = "pre-deploy-tasks", default)]
    pub pre_deploy_tasks: Vec<String>,
    #[serde(rename = "post-deploy-tasks", default)]
    pub post_deploy_tasks: Vec<String>,
    #[serde(rename = "post-procedure-tasks", default)]
    pub post_procedure_tasks: Vec<String>,
    #[serde(rename = "odis-pre-uninstall-tasks", default)]
    pub pre_uninstall_tasks: Vec<String>,
    #[serde(default)]
    pub registries: Vec<Value>,
    #[serde(skip)]
    pub checksum: String,
    #[serde(skip)]
    pub raw: String,
}

/// 파일 ID 맵 항목: (절대 경로, 파일 노드)
#[derive(Debug, Clone)]
pub struct IdEntry {
    pub path: PathBuf,
    pub node: FileNode,
}

impl PackageManifest {
    pub fn parse(checksum: &str, raw: &str) -> Result<Self, String> {
        let mut m: PackageManifest =
            serde_json::from_str(raw).map_err(|e| format!("invalid package manifest {checksum}: {e}"))?;
        m.checksum = checksum.to_string();
        m.raw = raw.to_string();
        Ok(m)
    }

    pub fn source_id(&self) -> String {
        self.properties.get("source-id").and_then(Value::as_str).unwrap_or("").to_string()
    }

    pub fn destination(&self) -> Option<&str> {
        self.root.destination.as_deref()
    }

    pub fn has_task(&self, id: &str) -> bool {
        let id = id.to_ascii_lowercase();
        self.pre_deploy_tasks
            .iter()
            .chain(&self.post_deploy_tasks)
            .chain(&self.post_procedure_tasks)
            .any(|t| t.eq_ignore_ascii_case(&id))
    }

    /// 트리를 순회하며 (디렉터리 경로, 디렉터리 노드) / (파일 경로, 파일 노드) 콜백 호출
    pub fn walk(&self, base: &Path, dir_cb: &mut dyn FnMut(&Path, &DirNode), file_cb: &mut dyn FnMut(&Path, &FileNode)) {
        fn rec(
            p: &Path,
            n: &DirNode,
            dir_cb: &mut dyn FnMut(&Path, &DirNode),
            file_cb: &mut dyn FnMut(&Path, &FileNode),
        ) {
            dir_cb(p, n);
            for f in &n.files {
                file_cb(&p.join(&f.name), f);
            }
            for d in &n.directories {
                rec(&p.join(&d.name), d, dir_cb, file_cb);
            }
        }
        rec(base, &self.root, dir_cb, file_cb);
    }

    /// 파일 ID → 경로 맵 (manifests.Package._IdMapEntry)
    pub fn id_map(&self, base: &Path) -> Vec<(String, IdEntry)> {
        let mut out = vec![];
        self.walk(base, &mut |_, _| {}, &mut |p, f| {
            if let Some(id) = f.id() {
                out.push((id.to_string(), IdEntry { path: p.to_path_buf(), node: f.clone() }));
            }
        });
        out
    }
}

/// 매니페스트 문자열 안의 `${location_<id>}`, `${dirsep}`, `{stream_tag}`, `${streamtag}` 치환
pub fn expand(s: &str, paths: &crate::config::Paths) -> String {
    let mut out = String::with_capacity(s.len());
    let mut rest = s;
    while let Some(i) = rest.find("${") {
        out.push_str(&rest[..i]);
        let after = &rest[i + 2..];
        if let Some(j) = after.find('}') {
            let key = &after[..j];
            let rep = if let Some(id) = key.strip_prefix("location_") {
                paths.location(id).map(|p| p.to_string_lossy().into_owned())
            } else {
                match key {
                    "dirsep" => Some("/".to_string()),
                    // production 스트림은 빈 태그 (Windows AUMID 'AutodeskInc.Fusion360' 와 동일)
                    "streamtag" => Some(stream_tag_suffix(&paths.stream)),
                    _ => None,
                }
            };
            match rep {
                Some(r) => out.push_str(&r),
                None => {
                    out.push_str("${");
                    out.push_str(key);
                    out.push('}');
                }
            }
            rest = &after[j + 1..];
        } else {
            out.push_str(&rest[i..]);
            rest = "";
        }
    }
    out.push_str(rest);
    out.replace("{stream_tag}", &paths.stream).replace('\\', "/")
}

fn stream_tag_suffix(stream: &str) -> String {
    if stream == "production" {
        String::new()
    } else {
        stream.to_string()
    }
}
