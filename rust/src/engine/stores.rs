//! File-backed stores (port of src/Stores.cs): the directory is created on demand, data goes to
//! `<path>.tmp` first and is then renamed over the destination, so no reader ever sees a half-written
//! file and no .tmp is left behind. UTF-8 without BOM. Missing or corrupt files load as "nothing".
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::core::contracts::{AlertState, Snapshot, SnapshotStore};
use crate::core::snapshot_json;

use super::alert_engine;

/// UI config persistence uses the same complete-temp-file replacement as the engine stores.
/// Call on a worker thread, like snapshot/alert saves.
pub fn save_config(path: impl AsRef<Path>, config: &crate::core::app_config::AppConfig) -> io::Result<()> {
    atomic_file::write(path.as_ref(), &config.to_json())
}

pub struct FileSnapshotStore {
    path: PathBuf,
}

impl FileSnapshotStore {
    pub fn new(path: impl Into<PathBuf>) -> FileSnapshotStore {
        FileSnapshotStore { path: path.into() }
    }
}

impl SnapshotStore for FileSnapshotStore {
    /// `None` when missing, unreadable or invalid (cache.js loadSnapshot).
    fn load(&self) -> Option<Snapshot> {
        let text = atomic_file::read(&self.path)?;
        snapshot_json::parse(Some(&text))
    }

    /// Atomic (tmp + replace); fails only when the filesystem itself refuses the write.
    fn save(&self, snapshot: &Snapshot) -> io::Result<()> {
        atomic_file::write(&self.path, &snapshot_json::serialize(snapshot))
    }
}

pub struct FileAlertStore {
    path: PathBuf,
}

impl FileAlertStore {
    pub fn new(path: impl Into<PathBuf>) -> FileAlertStore {
        FileAlertStore { path: path.into() }
    }

    /// Missing or corrupt files yield an empty state.
    pub fn load(&self) -> AlertState {
        match atomic_file::read(&self.path) {
            None => AlertState::default(),
            Some(text) => alert_engine::parse_state(Some(&text)),
        }
    }

    pub fn save(&self, state: &AlertState) -> io::Result<()> {
        atomic_file::write(&self.path, &alert_engine::serialize_state(state))
    }
}

/// Shared tmp-file write used by both stores.
pub(crate) mod atomic_file {
    use super::*;

    /// `None` when the file is absent or cannot be read. Like .NET `File.ReadAllText`: a UTF-8 BOM is
    /// dropped and invalid bytes become U+FFFD.
    pub fn read(path: &Path) -> Option<String> {
        if path.as_os_str().is_empty() || !path.is_file() {
            return None;
        }
        let bytes = fs::read(path).ok()?;
        let text = String::from_utf8_lossy(&bytes).into_owned();
        Some(match text.strip_prefix('\u{feff}') {
            Some(rest) => rest.to_string(),
            None => text,
        })
    }

    pub fn write(path: &Path, content: &str) -> io::Result<()> {
        if path.as_os_str().is_empty() {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "path is required"));
        }
        if let Some(dir) = path.parent() {
            if !dir.as_os_str().is_empty() {
                fs::create_dir_all(dir)?;
            }
        }

        // Unique temp file per save (pid + counter + nanos, create_new): two concurrent saves never
        // share a handle, so one can never write through into the other's renamed file.
        static COUNTER: AtomicUsize = AtomicUsize::new(0);
        let mut tmp = PathBuf::new();
        let mut file = None;
        for _ in 0..16 {
            let nanos = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0);
            let mut name = path.as_os_str().to_os_string();
            name.push(format!(".{}.{}.{}.tmp", std::process::id(), COUNTER.fetch_add(1, Ordering::SeqCst), nanos));
            let candidate = PathBuf::from(name);
            match OpenOptions::new().write(true).create_new(true).open(&candidate) {
                Ok(f) => {
                    tmp = candidate;
                    file = Some(f);
                    break;
                }
                Err(e) if e.kind() == io::ErrorKind::AlreadyExists => continue,
                Err(e) => return Err(e),
            }
        }
        let mut file = file.ok_or_else(|| io::Error::new(io::ErrorKind::AlreadyExists, "no free temp file name"))?;

        // The temp file has to be complete first: if it cannot be written it is cleaned up and the
        // error propagates, because a partial .tmp must never reach the destination.
        let written = file.write_all(content.as_bytes()).and_then(|_| file.flush());
        drop(file);
        if let Err(e) = written {
            let _ = fs::remove_file(&tmp);
            return Err(e);
        }

        // On Windows `rename` replaces an existing destination.
        if let Err(rename_err) = fs::rename(&tmp, path) {
            // Plain overwrite as the fallback for the replace step, only with a complete temp file.
            let recovered = fs::copy(&tmp, path).is_ok();
            let _ = fs::remove_file(&tmp);
            if !recovered {
                return Err(rename_err);
            }
        }
        Ok(())
    }
}
