//! 설치 레지스트리 (adsk.dls.streamer.registry 대응).
//!
//! 원본은 apsw(SQLite) + resources/scripts/reg/init.sql 스키마를 사용한다.
//! 동일한 스키마(tblSTREAM / tblAPPLICATIONS / tblPACKAGES / tblAPPLICATIONPACKAGES /
//! tblFILES)를 그대로 사용해 원본 도구와 개념적으로 호환되도록 했다.

use rusqlite::{params, Connection, OptionalExtension};
use std::path::Path;

const INIT_SQL: &str = include_str!("../sql/init.sql");

pub struct Registry {
    conn: Connection,
}

#[derive(Debug, Clone)]
pub struct InstalledApp {
    pub uuid: String,
    pub stream: String,
    pub version: String,
    pub manifest: String,
}

impl Registry {
    pub fn open(path: &Path) -> rusqlite::Result<Registry> {
        let fresh = !path.exists();
        let conn = Connection::open(path)?;
        conn.execute_batch("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;")?;
        if fresh {
            log::info!("Execute SQL script: init.sql");
            conn.execute_batch(INIT_SQL)?;
        }
        Ok(Registry { conn })
    }

    pub fn set_stream(&self, tag: &str, root: &str) -> rusqlite::Result<()> {
        let n = self.conn.execute(
            "UPDATE tblSTREAM SET root=?2, last_install=datetime('now') WHERE tag=?1",
            params![tag, root],
        )?;
        if n == 0 {
            self.conn.execute(
                "INSERT INTO tblSTREAM (tag, root, last_install) VALUES (?1, ?2, datetime('now'))",
                params![tag, root],
            )?;
        }
        Ok(())
    }

    pub fn stream_root(&self, tag: &str) -> Option<String> {
        self.conn
            .query_row("SELECT root FROM tblSTREAM WHERE tag=?1", params![tag], |r| r.get(0))
            .optional()
            .ok()
            .flatten()
    }

    pub fn application(&self, uuid: &str, stream: &str) -> Option<InstalledApp> {
        self.conn
            .query_row(
                "SELECT uuid, stream, version, manifest FROM tblAPPLICATIONS WHERE uuid=?1 AND stream=?2",
                params![uuid, stream],
                |r| Ok(InstalledApp { uuid: r.get(0)?, stream: r.get(1)?, version: r.get(2)?, manifest: r.get(3)? }),
            )
            .optional()
            .ok()
            .flatten()
    }

    pub fn applications(&self) -> Vec<InstalledApp> {
        let mut st = match self.conn.prepare("SELECT uuid, stream, version, manifest FROM tblAPPLICATIONS") {
            Ok(s) => s,
            Err(_) => return vec![],
        };
        st.query_map([], |r| Ok(InstalledApp { uuid: r.get(0)?, stream: r.get(1)?, version: r.get(2)?, manifest: r.get(3)? }))
            .map(|it| it.filter_map(Result::ok).collect())
            .unwrap_or_default()
    }

    pub fn has_package(&self, checksum: &str, stream: &str) -> bool {
        self.conn
            .query_row("SELECT 1 FROM tblPACKAGES WHERE checksum=?1 AND stream=?2", params![checksum, stream], |_| Ok(()))
            .optional()
            .ok()
            .flatten()
            .is_some()
    }

    pub fn package_manifest(&self, checksum: &str, stream: &str) -> Option<String> {
        self.conn
            .query_row("SELECT manifest FROM tblPACKAGES WHERE checksum=?1 AND stream=?2", params![checksum, stream], |r| {
                r.get(0)
            })
            .optional()
            .ok()
            .flatten()
    }

    /// 패키지와 그 패키지가 기록한 파일 목록을 한 트랜잭션으로 저장
    pub fn record_package(&mut self, checksum: &str, stream: &str, size: u64, manifest: &str, files: &[String]) -> rusqlite::Result<()> {
        let tx = self.conn.transaction()?;
        tx.execute(
            "INSERT OR REPLACE INTO tblPACKAGES (checksum, stream, install_size, manifest) VALUES (?1, ?2, ?3, ?4)",
            params![checksum, stream, size as i64, manifest],
        )?;
        {
            let mut st = tx.prepare("INSERT OR REPLACE INTO tblFILES (pkgid, stream, taskid, path) VALUES (?1, ?2, NULL, ?3)")?;
            for f in files {
                st.execute(params![checksum, stream, f])?;
            }
        }
        tx.commit()
    }

    pub fn record_application(&mut self, uuid: &str, stream: &str, version: &str, manifest: &str, packages: &[String]) -> rusqlite::Result<()> {
        let tx = self.conn.transaction()?;
        tx.execute("DELETE FROM tblAPPLICATIONPACKAGES WHERE application=?1 AND appstream=?2", params![uuid, stream])?;
        tx.execute(
            "INSERT OR REPLACE INTO tblAPPLICATIONS (uuid, stream, version, manifest) VALUES (?1, ?2, ?3, ?4)",
            params![uuid, stream, version, manifest],
        )?;
        {
            let mut st = tx.prepare(
                "INSERT INTO tblAPPLICATIONPACKAGES (application, package, appstream, pkgstream) VALUES (?1, ?2, ?3, ?3)",
            )?;
            for p in packages {
                st.execute(params![uuid, p, stream])?;
            }
        }
        tx.commit()
    }

    /// 어떤 애플리케이션에서도 참조하지 않는 패키지 (업데이트 후 정리 대상)
    pub fn orphan_packages(&self, stream: &str) -> Vec<String> {
        let mut st = match self.conn.prepare(
            "SELECT checksum FROM tblPACKAGES p WHERE stream=?1 AND NOT EXISTS
               (SELECT 1 FROM tblAPPLICATIONPACKAGES ap WHERE ap.package=p.checksum AND ap.pkgstream=p.stream)",
        ) {
            Ok(s) => s,
            Err(_) => return vec![],
        };
        st.query_map(params![stream], |r| r.get(0)).map(|it| it.filter_map(Result::ok).collect()).unwrap_or_default()
    }

    pub fn package_files(&self, checksum: &str, stream: &str) -> Vec<String> {
        let mut st = match self.conn.prepare("SELECT path FROM tblFILES WHERE pkgid=?1 AND stream=?2") {
            Ok(s) => s,
            Err(_) => return vec![],
        };
        st.query_map(params![checksum, stream], |r| r.get(0)).map(|it| it.filter_map(Result::ok).collect()).unwrap_or_default()
    }

    pub fn remove_package(&mut self, checksum: &str, stream: &str) -> rusqlite::Result<()> {
        let tx = self.conn.transaction()?;
        tx.execute("DELETE FROM tblFILES WHERE pkgid=?1 AND stream=?2", params![checksum, stream])?;
        tx.execute("DELETE FROM tblPACKAGES WHERE checksum=?1 AND stream=?2", params![checksum, stream])?;
        tx.commit()
    }

    pub fn remove_application(&mut self, uuid: &str, stream: &str) -> rusqlite::Result<()> {
        let tx = self.conn.transaction()?;
        tx.execute("DELETE FROM tblAPPLICATIONPACKAGES WHERE application=?1 AND appstream=?2", params![uuid, stream])?;
        tx.execute("DELETE FROM tblAPPLICATIONS WHERE uuid=?1 AND stream=?2", params![uuid, stream])?;
        tx.commit()
    }
}
