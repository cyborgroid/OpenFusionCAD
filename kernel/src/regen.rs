//! 타임라인 재생성: 피처를 순서대로 적용해 바디(B-rep)를 만들고 표시용 메시로 변환.

use crate::model::*;
use crate::sketch::{self, Profile, Seg};
use glam::DVec3;
use opencascade::angle::Angle;
use opencascade::primitives::{Edge, Face, Shape, Wire};
use serde::Serialize;
use std::collections::HashMap;

pub struct Body {
    pub name: String,
    pub feature: String,
    pub shape: Shape,
}

#[derive(Debug, Clone, Serialize)]
pub struct FeatureStatus {
    pub id: String,
    pub ok: bool,
    pub error: Option<String>,
}

/// GUI 가 그릴 스케치 프로파일 (평면 2D 좌표)
#[derive(Debug, Clone, Serialize)]
pub struct ProfileOut {
    pub outer: Vec<V2>,
    pub holes: Vec<Vec<V2>>,
    pub area: f64,
    /// 영역 채우기용 삼각형 (월드 좌표 x,y,z 를 3개씩)
    pub tris: Vec<f32>,
}

#[derive(Debug, Clone, Serialize)]
pub struct SketchOut {
    pub id: String,
    pub profiles: Vec<ProfileOut>,
}

pub struct State {
    pub bodies: Vec<Body>,
    pub status: Vec<FeatureStatus>,
    pub sketches: Vec<SketchOut>,
}

struct Frame {
    o: DVec3,
    x: DVec3,
    y: DVec3,
    n: DVec3,
}

impl Frame {
    fn new(p: &Plane) -> Result<Frame, String> {
        let n = DVec3::from(p.normal).normalize_or_zero();
        let mut x = DVec3::from(p.xdir);
        x = (x - n * x.dot(n)).normalize_or_zero();
        if n == DVec3::ZERO || x == DVec3::ZERO {
            return Err("invalid sketch plane".into());
        }
        Ok(Frame { o: DVec3::from(p.origin), x, y: n.cross(x), n })
    }
    fn p(&self, q: V2) -> DVec3 {
        self.o + self.x * q[0] + self.y * q[1]
    }
}

/// 루프 → 와이어. `ccw` 가 true 면 평면 법선 기준 반시계 방향으로 만든다.
/// OCCT 는 면의 바깥 경계와 구멍 경계가 서로 반대 방향이어야 구멍으로 인식한다.
fn wire_of(frame: &Frame, l: &sketch::Loop, ccw: bool) -> Wire {
    let reverse = (l.area() >= 0.0) != ccw;
    let mut segs = l.segs.clone();
    if reverse {
        segs.reverse();
    }
    let edges: Vec<Edge> = segs
        .iter()
        .map(|s| match *s {
            Seg::Line { a, b } if reverse => Edge::segment(frame.p(b), frame.p(a)),
            Seg::Line { a, b } => Edge::segment(frame.p(a), frame.p(b)),
            Seg::Arc { a, m, b } if reverse => Edge::arc(frame.p(b), frame.p(m), frame.p(a)),
            Seg::Arc { a, m, b } => Edge::arc(frame.p(a), frame.p(m), frame.p(b)),
            // 원은 법선 방향으로 회전 방향이 정해진다
            Seg::Circle { c, r } => Edge::circle(frame.p(c), if ccw { frame.n } else { -frame.n }, r),
        })
        .collect();
    Wire::from_edges(edges.iter())
}

fn face_of(frame: &Frame, p: &Profile) -> Face {
    let outer = wire_of(frame, &p.outer, true);
    let holes: Vec<Wire> = p.holes.iter().map(|h| wire_of(frame, h, false)).collect();
    if holes.is_empty() {
        Face::from_wire(&outer)
    } else {
        Face::from_wire_with_holes(&outer, &holes)
    }
}

fn pick<'a>(all: &'a [Profile], sel: &Option<Vec<usize>>) -> Result<Vec<&'a Profile>, String> {
    let v: Vec<&Profile> = match sel {
        None => all.iter().collect(),
        Some(ix) => ix.iter().filter_map(|i| all.get(*i)).collect(),
    };
    if v.is_empty() {
        Err("no closed profile in sketch".into())
    } else {
        Ok(v)
    }
}

fn combine(shapes: Vec<Shape>) -> Shape {
    let mut it = shapes.into_iter();
    let first = it.next().unwrap_or_else(Shape::empty);
    it.fold(first, |acc, s| acc.union(&s).into())
}

impl State {
    fn apply(&mut self, f: &Feature, shape: Shape, op: Operation, target: Option<usize>) -> Result<(), String> {
        let tgt = target.or_else(|| self.bodies.len().checked_sub(1));
        match (op, tgt) {
            (Operation::New, _) | (_, None) => {
                if matches!(op, Operation::Cut | Operation::Intersect) {
                    return Err("no body to cut/intersect".into());
                }
                let name = format!("Body{}", self.bodies.len() + 1);
                self.bodies.push(Body { name, feature: f.id.clone(), shape });
            }
            (_, Some(t)) => {
                let b = self.bodies.get_mut(t).ok_or("target body does not exist")?;
                b.shape = match op {
                    Operation::Join => b.shape.union(&shape).into(),
                    Operation::Cut => b.shape.subtract(&shape).into(),
                    Operation::Intersect => b.shape.intersect(&shape).into(),
                    Operation::New => unreachable!(),
                };
            }
        }
        Ok(())
    }

    fn feature(&mut self, f: &Feature, sketches: &HashMap<String, (Frame, Vec<Entity>, Vec<Profile>)>) -> Result<(), String> {
        match &f.kind {
            FeatureKind::Sketch { .. } => Ok(()),
            FeatureKind::Extrude { sketch, profiles, distance, distance2, direction, operation, target } => {
                let (frame, _, all) = sketches.get(sketch).ok_or("sketch not found")?;
                if distance.abs() < 1e-9 && *direction != Direction::Two {
                    return Err("distance must not be zero".into());
                }
                let (start, len) = match direction {
                    Direction::One => (0.0, *distance),
                    Direction::Symmetric => (-distance.abs() / 2.0, distance.abs()),
                    Direction::Two => (-distance2, distance + distance2),
                };
                let mut solids = vec![];
                for p in pick(all, profiles)? {
                    let shifted = Frame { o: frame.o + frame.n * start, x: frame.x, y: frame.y, n: frame.n };
                    let face = face_of(&shifted, p);
                    solids.push(Shape::from(face.extrude(frame.n * len)));
                }
                self.apply(f, combine(solids), *operation, *target)
            }
            FeatureKind::Revolve { sketch, profiles, axis, angle, operation, target } => {
                let (frame, ents, all) = sketches.get(sketch).ok_or("sketch not found")?;
                let (ao, ad) = match axis {
                    Axis::X => (frame.o, frame.x),
                    Axis::Y => (frame.o, frame.y),
                    Axis::Line { entity } => match ents.get(*entity) {
                        Some(Entity::Line { a, b }) | Some(Entity::Construction { a, b }) => (frame.p(*a), (frame.p(*b) - frame.p(*a)).normalize()),
                        _ => return Err("revolve axis must be a line".into()),
                    },
                };
                let ang = if angle.abs() >= 360.0 - 1e-9 { None } else { Some(Angle::Degrees(*angle)) };
                let mut solids = vec![];
                for p in pick(all, profiles)? {
                    solids.push(Shape::from(face_of(frame, p).revolve(ao, ad, ang)));
                }
                self.apply(f, combine(solids), *operation, *target)
            }
            FeatureKind::Fillet { body, edges, radius } | FeatureKind::Chamfer { body, edges, distance: radius } => {
                if *radius <= 0.0 {
                    return Err("radius/distance must be positive".into());
                }
                let b = self.bodies.get_mut(*body).ok_or("body does not exist")?;
                let all = unique_edges(&b.shape);
                let chosen: Vec<&Edge> = edges.iter().filter_map(|i| all.get(*i).map(|(e, _)| e)).collect();
                if chosen.is_empty() {
                    return Err("no edges selected".into());
                }
                b.shape = if matches!(f.kind, FeatureKind::Fillet { .. }) {
                    b.shape.fillet_edges(*radius, chosen)
                } else {
                    b.shape.chamfer_edges(*radius, chosen)
                };
                Ok(())
            }
            FeatureKind::Box { origin, size, operation, target } => {
                let o = DVec3::from(*origin);
                let s = Shape::box_from_corners(o, o + DVec3::from(*size));
                self.apply(f, s, *operation, *target)
            }
            FeatureKind::Cylinder { origin, axis, radius, height, operation, target } => {
                let s = Shape::cylinder(DVec3::from(*origin), *radius, DVec3::from(*axis).normalize(), *height);
                self.apply(f, s, *operation, *target)
            }
            FeatureKind::Sphere { center, radius, operation, target } => {
                let s = Shape::sphere(*radius).at(DVec3::from(*center)).build();
                self.apply(f, s, *operation, *target)
            }
            FeatureKind::Import { path, operation, target } => {
                let lower = path.to_lowercase();
                let s = if lower.ends_with(".igs") || lower.ends_with(".iges") {
                    Shape::read_iges(path)
                } else if lower.ends_with(".brep") {
                    Shape::read_brep_text(path)
                } else {
                    Shape::read_step(path)
                }
                .map_err(|e| e.to_string())?;
                self.apply(f, s, *operation, *target)
            }
        }
    }
}

fn profile_tris(frame: &Frame, p: &Profile) -> Vec<f32> {
    let shape: Shape = face_of(frame, p).into();
    let Ok(m) = shape.mesh_with_tolerance(0.05) else { return vec![] };
    let mut out = Vec::with_capacity(m.indices.len() * 3);
    for i in &m.indices {
        let v = m.vertices[*i];
        out.extend([v.x as f32, v.y as f32, v.z as f32]);
    }
    out
}

/// 문서 → 바디 목록 (피처별 상태 포함)
pub fn regenerate(doc: &Document) -> State {
    let mut st = State { bodies: vec![], status: vec![], sketches: vec![] };
    let mut sketches = HashMap::new();
    let n = doc.marker.unwrap_or(doc.features.len()).min(doc.features.len());
    for f in &doc.features[..n] {
        if f.suppressed {
            st.status.push(FeatureStatus { id: f.id.clone(), ok: true, error: None });
            continue;
        }
        if let FeatureKind::Sketch { plane, entities } = &f.kind {
            match Frame::new(plane) {
                Ok(frame) => {
                    let profs = sketch::profiles(entities);
                    st.sketches.push(SketchOut {
                        id: f.id.clone(),
                        profiles: profs
                            .iter()
                            .map(|p| ProfileOut {
                                outer: p.outer.poly.clone(),
                                holes: p.holes.iter().map(|h| h.poly.clone()).collect(),
                                area: p.outer.area().abs(),
                                tris: profile_tris(&frame, p),
                            })
                            .collect(),
                    });
                    sketches.insert(f.id.clone(), (frame, entities.clone(), profs));
                }
                Err(e) => {
                    st.status.push(FeatureStatus { id: f.id.clone(), ok: false, error: Some(e) });
                    continue;
                }
            }
        }
        let r = st.feature(f, &sketches);
        st.status.push(FeatureStatus { id: f.id.clone(), ok: r.is_ok(), error: r.err() });
    }
    st
}

// ---------------------------------------------------------------- 표시용 메시

#[derive(Default)]
pub struct MeshOut {
    pub positions: Vec<f32>,
    pub normals: Vec<f32>,
    pub indices: Vec<u32>,
    /// 모서리 선분 (x1,y1,z1,x2,y2,z2) – Fusion 처럼 검은 외곽선 표시용
    pub edges: Vec<f32>,
    /// 선분별 모서리 번호 (Fillet/Chamfer 의 edges 인덱스와 동일한 순서)
    pub edge_ids: Vec<u32>,
    /// 면별 삼각형 시작 인덱스 (len = faces + 1)
    pub face_start: Vec<u32>,
    /// 면별 [평면 여부, 중심 xyz, 법선 xyz]
    pub face_info: Vec<f32>,
    pub bbox: [f64; 6],
}

pub fn mesh_body(shape: &Shape, tol: f64) -> Result<MeshOut, String> {
    let mut m = MeshOut { bbox: [f64::MAX, f64::MAX, f64::MAX, f64::MIN, f64::MIN, f64::MIN], ..Default::default() };
    // 전체를 한 번 삼각분할 → 면별 메시가 같은 분할을 재사용
    shape.mesh_with_tolerance(tol).map_err(|e| e.to_string())?;
    for face in shape.faces() {
        m.face_start.push((m.indices.len() / 3) as u32);
        let fs: Shape = Shape::from(&face);
        let mut fm = match fs.mesh_with_tolerance(tol) {
            Ok(x) => x,
            Err(_) => continue,
        };
        // opencascade-rs 는 뒤집힌 면의 삼각형 순서만 바꾸고 법선은 곡면 기준 그대로 준다.
        // 바깥쪽 법선이 되도록 뒤집는다.
        if face.orientation() != opencascade::primitives::FaceOrientation::Forward {
            for n in fm.normals.iter_mut() {
                *n = -*n;
            }
        }
        let base = (m.positions.len() / 3) as u32;
        // normal_at_center() 는 무게중심을 곡면에 투영하는데, 원통 안쪽 면처럼 중심이 축 위에
        // 있으면 투영 해가 정해지지 않아 OCCT 가 Standard_OutOfRange 로 프로세스를 종료시킨다.
        // 메시 법선(바깥 방향)을 대신 사용한다.
        let n0 = fm.normals.first().copied().unwrap_or(DVec3::Z).normalize_or_zero();
        let mut planar = true;
        for (v, n) in fm.vertices.iter().zip(fm.normals.iter()) {
            m.positions.extend([v.x as f32, v.y as f32, v.z as f32]);
            m.normals.extend([n.x as f32, n.y as f32, n.z as f32]);
            if n.dot(n0).abs() < 0.9999 {
                planar = false;
            }
            for k in 0..3 {
                m.bbox[k] = m.bbox[k].min(v[k]);
                m.bbox[k + 3] = m.bbox[k + 3].max(v[k]);
            }
        }
        m.indices.extend(fm.indices.iter().map(|i| base + *i as u32));
        let c = face.center_of_mass();
        m.face_info.extend([if planar { 1.0 } else { 0.0 }, c.x as f32, c.y as f32, c.z as f32, n0.x as f32, n0.y as f32, n0.z as f32]);
    }
    m.face_start.push((m.indices.len() / 3) as u32);
    for (i, (_, pts)) in unique_edges(shape).iter().enumerate() {
        for w in pts.windows(2) {
            m.edges.extend([w[0].x as f32, w[0].y as f32, w[0].z as f32, w[1].x as f32, w[1].y as f32, w[1].z as f32]);
            m.edge_ids.push(i as u32);
        }
    }
    if m.positions.is_empty() {
        m.bbox = [0.0; 6];
    }
    Ok(m)
}

/// 중복 없는 모서리 목록. TopExp_Explorer 는 두 면이 공유하는 모서리를 두 번 돌려주므로
/// 끝점과 중간 근사점으로 같은 모서리를 걸러낸다. 표시와 Fillet 선택이 같은 순서를 쓴다.
pub fn unique_edges(shape: &Shape) -> Vec<(Edge, Vec<DVec3>)> {
    let key = |p: DVec3| ((p.x * 1e4).round() as i64, (p.y * 1e4).round() as i64, (p.z * 1e4).round() as i64);
    let mut seen = std::collections::HashSet::new();
    let mut out = vec![];
    for e in shape.edges() {
        let pts: Vec<DVec3> = e.approximation_segments().collect();
        if pts.len() < 2 {
            continue;
        }
        let mid = pts[pts.len() / 2];
        let (a, b) = (key(pts[0]), key(*pts.last().unwrap()));
        let k = if a <= b { (a, b, key(mid)) } else { (b, a, key(mid)) };
        if seen.insert(k) {
            out.push((e, pts));
        }
    }
    out
}
