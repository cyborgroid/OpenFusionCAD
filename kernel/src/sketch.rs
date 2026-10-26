//! 스케치 엔티티 → 닫힌 영역(프로파일) 검출.
//!
//! 끝점을 공유하는 선/호를 이어 닫힌 루프를 찾고, 루프 포함 관계로 바깥 경계와 구멍을
//! 나눈다. 바깥 경계 하나 + 그 바로 안쪽 구멍들 = 프로파일 하나 (Fusion 의 "프로파일" 클릭 단위).

use crate::model::{Entity, V2};

const EPS: f64 = 1e-6;

/// 2D 곡선 조각
#[derive(Debug, Clone, Copy)]
pub enum Seg {
    Line { a: V2, b: V2 },
    Arc { a: V2, m: V2, b: V2 },
    Circle { c: V2, r: f64 },
}

impl Seg {
    fn ends(&self) -> Option<(V2, V2)> {
        match *self {
            Seg::Line { a, b } | Seg::Arc { a, b, .. } => Some((a, b)),
            Seg::Circle { .. } => None,
        }
    }
    fn reversed(&self) -> Seg {
        match *self {
            Seg::Line { a, b } => Seg::Line { a: b, b: a },
            Seg::Arc { a, m, b } => Seg::Arc { a: b, m, b: a },
            c => c,
        }
    }
    /// 다각형 근사 (시작점 포함, 끝점 제외)
    pub fn polyline(&self, out: &mut Vec<V2>) {
        match *self {
            Seg::Line { a, .. } => out.push(a),
            Seg::Arc { a, m, b } => {
                if let Some((c, r, t0, sweep)) = arc_params(a, m, b) {
                    let n = ((sweep.abs() / (std::f64::consts::PI / 24.0)).ceil() as usize).max(4);
                    for i in 0..n {
                        let t = t0 + sweep * i as f64 / n as f64;
                        out.push([c[0] + r * t.cos(), c[1] + r * t.sin()]);
                    }
                } else {
                    out.push(a);
                }
            }
            Seg::Circle { c, r } => {
                for i in 0..64 {
                    let t = std::f64::consts::TAU * i as f64 / 64.0;
                    out.push([c[0] + r * t.cos(), c[1] + r * t.sin()]);
                }
            }
        }
    }
}

/// 3점 호 → (중심, 반지름, 시작각, 부호 있는 회전각)
pub fn arc_params(a: V2, m: V2, b: V2) -> Option<(V2, f64, f64, f64)> {
    let d = 2.0 * (a[0] * (m[1] - b[1]) + m[0] * (b[1] - a[1]) + b[0] * (a[1] - m[1]));
    if d.abs() < 1e-12 {
        return None;
    }
    let sq = |p: V2| p[0] * p[0] + p[1] * p[1];
    let ux = (sq(a) * (m[1] - b[1]) + sq(m) * (b[1] - a[1]) + sq(b) * (a[1] - m[1])) / d;
    let uy = (sq(a) * (b[0] - m[0]) + sq(m) * (a[0] - b[0]) + sq(b) * (m[0] - a[0])) / d;
    let c = [ux, uy];
    let r = ((a[0] - ux).powi(2) + (a[1] - uy).powi(2)).sqrt();
    let ang = |p: V2| (p[1] - uy).atan2(p[0] - ux);
    let (ta, tm, tb) = (ang(a), ang(m), ang(b));
    let norm = |x: f64| x.rem_euclid(std::f64::consts::TAU);
    // a → b 를 반시계로 갈 때 m 을 지나면 양의 방향
    let ccw = norm(tm - ta) < norm(tb - ta);
    let sweep = if ccw { norm(tb - ta) } else { -norm(ta - tb) };
    Some((c, r, ta, sweep))
}

/// 엔티티를 곡선 조각으로 분해 (작도선 제외)
pub fn segments(entities: &[Entity]) -> Vec<Seg> {
    let mut out = vec![];
    for e in entities {
        match *e {
            Entity::Line { a, b } => out.push(Seg::Line { a, b }),
            Entity::Rect { a, b } => {
                let p = [a, [b[0], a[1]], b, [a[0], b[1]]];
                for i in 0..4 {
                    out.push(Seg::Line { a: p[i], b: p[(i + 1) % 4] });
                }
            }
            Entity::Circle { c, r } if r > EPS => out.push(Seg::Circle { c, r }),
            Entity::Arc { a, m, b } => out.push(Seg::Arc { a, m, b }),
            Entity::Construction { .. } | Entity::Point { .. } | Entity::Circle { .. } => {}
        }
    }
    out
}

fn same(p: V2, q: V2) -> bool {
    (p[0] - q[0]).abs() < 1e-5 && (p[1] - q[1]).abs() < 1e-5
}

/// 닫힌 루프 (순서대로 이어진 조각들)
#[derive(Debug, Clone)]
pub struct Loop {
    pub segs: Vec<Seg>,
    pub poly: Vec<V2>,
}

impl Loop {
    fn new(segs: Vec<Seg>) -> Loop {
        let mut poly = vec![];
        for s in &segs {
            s.polyline(&mut poly);
        }
        Loop { segs, poly }
    }
    pub fn area(&self) -> f64 {
        let p = &self.poly;
        let mut a = 0.0;
        for i in 0..p.len() {
            let (x1, y1) = (p[i][0], p[i][1]);
            let (x2, y2) = (p[(i + 1) % p.len()][0], p[(i + 1) % p.len()][1]);
            a += x1 * y2 - x2 * y1;
        }
        a / 2.0
    }
    fn contains_point(&self, q: V2) -> bool {
        let p = &self.poly;
        let mut inside = false;
        let mut j = p.len() - 1;
        for i in 0..p.len() {
            if (p[i][1] > q[1]) != (p[j][1] > q[1]) && q[0] < (p[j][0] - p[i][0]) * (q[1] - p[i][1]) / (p[j][1] - p[i][1]) + p[i][0] {
                inside = !inside;
            }
            j = i;
        }
        inside
    }
    /// 다른 루프를 완전히 포함하는지 (꼭짓점 모두 내부)
    fn contains(&self, other: &Loop) -> bool {
        self.area().abs() > other.area().abs() && other.poly.iter().all(|q| self.contains_point(*q))
    }
}

/// 끝점 연결로 닫힌 루프 찾기 (분기 없는 단순 스케치 기준)
pub fn find_loops(segs: &[Seg]) -> Vec<Loop> {
    let mut used = vec![false; segs.len()];
    let mut loops = vec![];
    for i in 0..segs.len() {
        if used[i] {
            continue;
        }
        let Some((start, mut end)) = segs[i].ends() else {
            used[i] = true;
            loops.push(Loop::new(vec![segs[i]]));
            continue;
        };
        let mut chain = vec![segs[i]];
        let mut chain_idx = vec![i];
        used[i] = true;
        let mut closed = same(start, end);
        while !closed {
            let next = (0..segs.len()).find_map(|j| {
                if used[j] {
                    return None;
                }
                let (a, b) = segs[j].ends()?;
                if same(a, end) {
                    Some((j, segs[j]))
                } else if same(b, end) {
                    Some((j, segs[j].reversed()))
                } else {
                    None
                }
            });
            match next {
                Some((j, s)) => {
                    used[j] = true;
                    chain_idx.push(j);
                    end = s.ends().unwrap().1;
                    chain.push(s);
                    closed = same(start, end);
                }
                None => break,
            }
        }
        if closed {
            loops.push(Loop::new(chain));
        } else {
            // 열린 체인: 다른 루프에 쓰일 수 있도록 첫 조각 외에는 반환
            for j in chain_idx.into_iter().skip(1) {
                used[j] = false;
            }
        }
    }
    loops
}

/// 프로파일 = 바깥 루프 + 바로 안쪽 구멍 루프들
#[derive(Debug, Clone)]
pub struct Profile {
    pub outer: Loop,
    pub holes: Vec<Loop>,
}

pub fn profiles(entities: &[Entity]) -> Vec<Profile> {
    let loops = find_loops(&segments(entities));
    // 각 루프의 포함 깊이: 짝수 = 바깥 경계, 홀수 = 구멍
    let depth: Vec<usize> = loops.iter().map(|l| loops.iter().filter(|o| o.contains(l)).count()).collect();
    let mut out = vec![];
    for (i, l) in loops.iter().enumerate() {
        if depth[i] % 2 != 0 {
            continue;
        }
        let holes = loops
            .iter()
            .enumerate()
            .filter(|(j, h)| depth[*j] == depth[i] + 1 && l.contains(h))
            .map(|(_, h)| h.clone())
            .collect();
        out.push(Profile { outer: l.clone(), holes });
    }
    // 큰 영역부터 (선택 인덱스를 안정적으로)
    out.sort_by(|a, b| b.outer.area().abs().partial_cmp(&a.outer.area().abs()).unwrap_or(std::cmp::Ordering::Equal));
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rect_with_hole() {
        let e = vec![Entity::Rect { a: [0.0, 0.0], b: [10.0, 10.0] }, Entity::Circle { c: [5.0, 5.0], r: 2.0 }];
        let p = profiles(&e);
        assert_eq!(p.len(), 1);
        assert_eq!(p[0].holes.len(), 1);
        assert!((p[0].outer.area().abs() - 100.0).abs() < 1e-6);
    }

    #[test]
    fn lines_chain_unordered() {
        let e = vec![
            Entity::Line { a: [0.0, 0.0], b: [5.0, 0.0] },
            Entity::Line { a: [0.0, 5.0], b: [0.0, 0.0] },
            Entity::Line { a: [5.0, 0.0], b: [0.0, 5.0] },
            Entity::Line { a: [20.0, 0.0], b: [30.0, 0.0] }, // 열린 선은 무시
        ];
        let p = profiles(&e);
        assert_eq!(p.len(), 1);
        assert!((p[0].outer.area().abs() - 12.5).abs() < 1e-6);
    }

    #[test]
    fn arc_direction() {
        let (c, r, _, sweep) = arc_params([1.0, 0.0], [0.0, 1.0], [-1.0, 0.0]).unwrap();
        assert!(c[0].abs() < 1e-9 && c[1].abs() < 1e-9 && (r - 1.0).abs() < 1e-9);
        assert!((sweep - std::f64::consts::PI).abs() < 1e-9);
    }
}
