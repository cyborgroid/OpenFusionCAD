//! ofc-kernel – GUI 가 띄우는 형상 커널 프로세스 (stdin JSON 요청 → stdout 이진 응답)

use std::io::{BufRead, Write};

fn main() {
    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    let mut out = std::io::BufWriter::with_capacity(1 << 20, stdout.lock());
    for line in stdin.lock().lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        if ofc_kernel::protocol::handle(&line, &mut out).is_err() {
            break;
        }
        if out.flush().is_err() {
            break;
        }
    }
}
