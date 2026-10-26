//! 문서 모델 – 파라메트릭 피처 타임라인.
//!
//! GUI(C#)가 문서를 소유하고 JSON 으로 보내면 커널이 타임라인을 처음부터 재생성한다.
//! 같은 JSON 이 `.ofcd` 파일 형식이기도 하다. 단위는 mm, 각도는 도(degree).

use serde::{Deserialize, Serialize};

pub type V2 = [f64; 2];
pub type V3 = [f64; 3];

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Document {
    #[serde(default = "one")]
    pub version: u32,
    #[serde(default)]
    pub features: Vec<Feature>,
    /// 타임라인 마커: 앞에서부터 이 개수만큼만 계산 (None = 전부)
    #[serde(default)]
    pub marker: Option<usize>,
}

fn one() -> u32 {
    1
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
pub struct Plane {
    pub origin: V3,
    pub normal: V3,
    pub xdir: V3,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "lowercase")]
pub enum Entity {
    Line { a: V2, b: V2 },
    /// 대각 꼭짓점 a, b 로 정의되는 축 정렬 사각형
    Rect { a: V2, b: V2 },
    Circle { c: V2, r: f64 },
    /// 시작점 a, 경유점 m, 끝점 b 를 지나는 3점 호
    Arc { a: V2, m: V2, b: V2 },
    /// 작도선 – 프로파일에서 제외, 회전축 등으로 사용
    Construction { a: V2, b: V2 },
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq, Default)]
#[serde(rename_all = "lowercase")]
pub enum Operation {
    #[default]
    New,
    Join,
    Cut,
    Intersect,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq, Default)]
#[serde(rename_all = "lowercase")]
pub enum Direction {
    #[default]
    One,
    Symmetric,
    Two,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum Axis {
    /// 스케치의 직선/작도선 엔티티
    Line { entity: usize },
    /// 스케치 평면의 X / Y 축
    X,
    Y,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "lowercase")]
pub enum FeatureKind {
    Sketch {
        plane: Plane,
        #[serde(default)]
        entities: Vec<Entity>,
    },
    Extrude {
        sketch: String,
        /// 사용할 프로파일 인덱스 (None = 모든 닫힌 영역)
        #[serde(default)]
        profiles: Option<Vec<usize>>,
        distance: f64,
        #[serde(default)]
        distance2: f64,
        #[serde(default)]
        direction: Direction,
        #[serde(default)]
        operation: Operation,
        #[serde(default)]
        target: Option<usize>,
    },
    Revolve {
        sketch: String,
        #[serde(default)]
        profiles: Option<Vec<usize>>,
        axis: Axis,
        #[serde(default = "full_turn")]
        angle: f64,
        #[serde(default)]
        operation: Operation,
        #[serde(default)]
        target: Option<usize>,
    },
    Fillet { body: usize, edges: Vec<usize>, radius: f64 },
    Chamfer { body: usize, edges: Vec<usize>, distance: f64 },
    Box {
        origin: V3,
        size: V3,
        #[serde(default)]
        operation: Operation,
        #[serde(default)]
        target: Option<usize>,
    },
    Cylinder {
        origin: V3,
        #[serde(default = "z_up")]
        axis: V3,
        radius: f64,
        height: f64,
        #[serde(default)]
        operation: Operation,
        #[serde(default)]
        target: Option<usize>,
    },
    Sphere {
        center: V3,
        radius: f64,
        #[serde(default)]
        operation: Operation,
        #[serde(default)]
        target: Option<usize>,
    },
    /// STEP/IGES/BREP 파일 삽입
    Import {
        path: String,
        #[serde(default)]
        operation: Operation,
        #[serde(default)]
        target: Option<usize>,
    },
}

fn full_turn() -> f64 {
    360.0
}
fn z_up() -> V3 {
    [0.0, 0.0, 1.0]
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Feature {
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub suppressed: bool,
    #[serde(flatten)]
    pub kind: FeatureKind,
}
