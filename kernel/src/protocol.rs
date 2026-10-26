//! GUI ↔ 커널 프로세스 프로토콜.
//!
//! 요청: 표준 입력으로 한 줄짜리 JSON
//!   {"cmd":"regen","doc":{...}}
//!   {"cmd":"export","doc":{...},"path":"/x/y.step","format":"step|stl|iges|brep","bodies":[0,1]|null}
//!   {"cmd":"ping"}
//! 응답: 표준 출력으로 [u32 LE 길이][JSON] 다음에, regen 이면 바디마다 아래 배열을
//!   [u32 바이트 길이][리틀엔디언 원소] 형식으로 순서대로 이어 붙인다:
//!   positions(f32) normals(f32) indices(u32) edges(f32) edge_ids(u32) face_start(u32) face_info(f32)

use crate::model::Document;
use crate::regen::{self, MeshOut};
use opencascade::primitives::{Compound, Shape};
use serde::Deserialize;
use serde_json::{json, Value};
use std::io::Write;

#[derive(Deserialize)]
pub struct Request {
    pub cmd: String,
    #[serde(default)]
    pub doc: Option<Document>,
    #[serde(default)]
    pub path: Option<String>,
    #[serde(default)]
    pub format: Option<String>,
    #[serde(default)]
    pub bodies: Option<Vec<usize>>,
    #[serde(default)]
    pub tolerance: Option<f64>,
}

fn frame(out: &mut impl Write, v: &Value) -> std::io::Result<()> {
    let b = serde_json::to_vec(v)?;
    out.write_all(&(b.len() as u32).to_le_bytes())?;
    out.write_all(&b)
}

fn blob_f32(out: &mut impl Write, v: &[f32]) -> std::io::Result<()> {
    out.write_all(&((v.len() * 4) as u32).to_le_bytes())?;
    for x in v {
        out.write_all(&x.to_le_bytes())?;
    }
    Ok(())
}

fn blob_u32(out: &mut impl Write, v: &[u32]) -> std::io::Result<()> {
    out.write_all(&((v.len() * 4) as u32).to_le_bytes())?;
    for x in v {
        out.write_all(&x.to_le_bytes())?;
    }
    Ok(())
}

pub fn handle(line: &str, out: &mut impl Write) -> std::io::Result<()> {
    let req: Request = match serde_json::from_str(line) {
        Ok(r) => r,
        Err(e) => return frame(out, &json!({"ok": false, "error": format!("bad request: {e}")})),
    };
    match req.cmd.as_str() {
        "ping" => frame(out, &json!({"ok": true, "version": env!("CARGO_PKG_VERSION"), "engine": "OpenCASCADE 7.8"})),
        "regen" => {
            let Some(doc) = req.doc else { return frame(out, &json!({"ok": false, "error": "missing doc"})) };
            let st = regen::regenerate(&doc);
            let tol = req.tolerance.unwrap_or(0.02);
            let mut meshes: Vec<(usize, MeshOut)> = vec![];
            let mut bodies = vec![];
            for (i, b) in st.bodies.iter().enumerate() {
                if b.removed {
                    continue;
                }
                match regen::mesh_body(&b.shape, tol) {
                    Ok(m) => {
                        bodies.push(json!({
                            "index": i, "name": b.name, "feature": b.feature, "bbox": m.bbox,
                            "faces": m.face_start.len().saturating_sub(1),
                            "edges": m.edge_ids.last().map(|x| x + 1).unwrap_or(0),
                        }));
                        meshes.push((i, m));
                    }
                    Err(e) => bodies.push(json!({"index": i, "name": b.name, "feature": b.feature, "error": e})),
                }
            }
            let meshed: Vec<usize> = meshes.iter().map(|(i, _)| *i).collect();
            frame(out, &json!({"ok": true, "features": st.status, "sketches": st.sketches, "bodies": bodies, "meshed": meshed}))?;
            for (_, m) in &meshes {
                blob_f32(out, &m.positions)?;
                blob_f32(out, &m.normals)?;
                blob_u32(out, &m.indices)?;
                blob_f32(out, &m.edges)?;
                blob_u32(out, &m.edge_ids)?;
                blob_u32(out, &m.face_start)?;
                blob_f32(out, &m.face_info)?;
            }
            Ok(())
        }
        "export" => {
            let (Some(doc), Some(path)) = (req.doc, req.path) else {
                return frame(out, &json!({"ok": false, "error": "missing doc/path"}));
            };
            let st = regen::regenerate(&doc);
            let shapes: Vec<&Shape> = st
                .bodies
                .iter()
                .enumerate()
                .filter(|(i, b)| !b.removed && req.bodies.as_ref().map(|x| x.contains(i)).unwrap_or(true))
                .map(|(_, b)| &b.shape)
                .collect();
            if shapes.is_empty() {
                return frame(out, &json!({"ok": false, "error": "no bodies to export"}));
            }
            let fmt = req.format.unwrap_or_else(|| path.rsplit('.').next().unwrap_or("step").to_lowercase());
            let r = match fmt.as_str() {
                "step" | "stp" => Shape::write_all_step(shapes, &path).map_err(|e| e.to_string()),
                other => {
                    let comp: Shape = Compound::from_shapes(shapes).into();
                    match other {
                        "stl" => comp.write_stl_with_tolerance(&path, req.tolerance.unwrap_or(0.01)).map_err(|e| e.to_string()),
                        "iges" | "igs" => comp.write_iges(&path).map_err(|e| e.to_string()),
                        "brep" => comp.write_brep_text(&path).map_err(|e| e.to_string()),
                        f => Err(format!("unsupported format: {f}")),
                    }
                }
            };
            match r {
                Ok(()) => frame(out, &json!({"ok": true, "path": path})),
                Err(e) => frame(out, &json!({"ok": false, "error": e})),
            }
        }
        c => frame(out, &json!({"ok": false, "error": format!("unknown cmd {c}")})),
    }
}
