//! OpenFusionCAD 형상 커널.
//!
//! OpenCASCADE(B-rep) 위에서 파라메트릭 피처 타임라인을 계산한다. GUI 와는 별도 프로세스
//! (`ofc-kernel`)로 동작해, 형상 연산 중 C++ 예외로 커널이 죽어도 GUI 는 살아 있다.

pub mod model;
pub mod protocol;
pub mod regen;
pub mod sketch;
