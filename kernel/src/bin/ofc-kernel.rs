//! ofc-kernel – GUI 가 띄우는 형상 커널 프로세스 (stdin JSON 요청 → stdout 이진 응답)

use std::io::{BufRead, Write};
use std::os::fd::FromRawFd;

fn main() {
    // OCCT(STEP 작성기 등)는 진단 메시지를 표준 출력에 직접 쓴다. 그대로 두면 이진 프로토콜
    // 스트림이 깨지므로, 원래 stdout 을 프로토콜 전용 fd 로 복제하고 fd 1 은 stderr 로 돌린다.
    // SAFETY: 시작 시 한 번, 다른 스레드가 없을 때 fd 를 조작한다.
    let proto = unsafe {
        let fd = libc::dup(1);
        assert!(fd >= 0, "dup(stdout) failed");
        libc::dup2(2, 1);
        std::fs::File::from_raw_fd(fd)
    };
    let mut out = std::io::BufWriter::with_capacity(1 << 20, proto);
    let stdin = std::io::stdin();
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
