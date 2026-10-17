//! Usage history for other tools and models (port of src/UsageLog.cs): one JSON object per line,
//! appended after a poll when something changed (plus a heartbeat). Numbers and states only - never
//! tokens, hints or messages. Rotates at 4 MiB (the previous file is kept as `<name>.1`). Never
//! panics and never reports errors to the poller (`append` just returns false).
//!
//! Line format:
//! `{"t":"<utc iso>","poll_interval_secs":60,"providers":[{"id":"claude","state":"ok","plan":"Pro",
//! "source":"api","windows":[{"kind":"five_hour","label":"5h","used_pct":40,"remaining_pct":60,
//! "resets_at":"<utc iso>"}]}]}`
use std::fmt::Write as _;
use std::fs::{self, OpenOptions};
use std::io::Write as _;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};
use serde_json::{json, Map, Value};

use crate::core::contracts::{state, Snapshot};
use crate::core::models;

pub const MAX_BYTES: u64 = 4 * 1024 * 1024;

pub fn heartbeat() -> Duration {
    Duration::minutes(10)
}

pub struct UsageLog {
    path: PathBuf,
    last: Mutex<Last>,
}

#[derive(Default)]
struct Last {
    fingerprint: Option<String>,
    write_utc: Option<DateTime<Utc>>,
}

/// JavaScriptSerializer writes whole numbers without a fraction (40, not 40.0).
fn number(v: Option<f64>) -> Value {
    match v {
        Some(d) if d.is_finite() => {
            if d.fract() == 0.0 && d.abs() < 1e15 {
                json!(d as i64)
            } else {
                json!(d)
            }
        }
        _ => Value::Null,
    }
}

/// Ticks (100 ns) since 0001-01-01 like .NET `DateTime.Ticks`; only used inside the fingerprint.
fn ticks(t: DateTime<Utc>) -> i128 {
    const UNIX_EPOCH_TICKS: i128 = 621_355_968_000_000_000;
    UNIX_EPOCH_TICKS + t.timestamp() as i128 * 10_000_000 + (t.timestamp_subsec_nanos() / 100) as i128
}

/// JavaScriptSerializer escapes these HTML-sensitive characters inside strings; they can only occur
/// inside strings in compact JSON.
fn escape_like_javascript_serializer(json: &str) -> String {
    let mut out = String::with_capacity(json.len());
    for c in json.chars() {
        match c {
            '<' | '>' | '&' | '\'' => {
                let _ = write!(out, "\\u{:04x}", c as u32);
            }
            c => out.push(c),
        }
    }
    out
}

impl UsageLog {
    pub fn new(path: impl Into<PathBuf>) -> UsageLog {
        UsageLog { path: path.into(), last: Mutex::new(Last::default()) }
    }

    /// Returns true when a line was written.
    pub fn append(&self, snapshot: &Snapshot, now_utc: DateTime<Utc>) -> bool {
        catch_unwind(AssertUnwindSafe(|| self.append_inner(snapshot, now_utc))).unwrap_or(false)
    }

    fn append_inner(&self, snapshot: &Snapshot, now_utc: DateTime<Utc>) -> bool {
        if self.path.as_os_str().is_empty() {
            return false;
        }

        let mut providers = Vec::new();
        let mut fp = String::new();
        for p in &snapshot.providers {
            let st = if p.status.state.is_empty() { state::UNKNOWN } else { p.status.state.as_str() };
            let mut entry = Map::new();
            entry.insert("id".into(), json!(p.id));
            entry.insert("state".into(), json!(st));
            entry.insert("plan".into(), json!(p.plan));
            entry.insert("source".into(), json!(p.source));
            let _ = write!(fp, "{}|{}|{}|", p.id, st, p.plan.as_deref().unwrap_or(""));

            let mut windows = Vec::new();
            for w in &p.windows {
                let mut item = Map::new();
                item.insert("kind".into(), json!(w.kind));
                item.insert("label".into(), json!(w.label));
                item.insert("used_pct".into(), number(w.used_pct));
                item.insert("remaining_pct".into(), number(w.remaining_pct));
                item.insert("resets_at".into(), json!(w.resets_at.map(models::to_iso)));
                windows.push(Value::Object(item));
                let used = w.used_pct.map(|u| format!("{:?}", u)).unwrap_or_else(|| "-".to_string());
                let reset = w.resets_at.map(|r| ticks(r).to_string()).unwrap_or_else(|| "-".to_string());
                let _ = write!(fp, "{}:{}:{}:{};", w.kind, w.label, used, reset);
            }
            entry.insert("windows".into(), Value::Array(windows));
            fp.push('\n');
            providers.push(Value::Object(entry));
        }

        let mut last = match self.last.lock() {
            Ok(g) => g,
            Err(poisoned) => poisoned.into_inner(),
        };
        let unchanged = last.fingerprint.as_deref() == Some(fp.as_str());
        if unchanged {
            if let Some(w) = last.write_utc {
                if now_utc - w < heartbeat() {
                    return false;
                }
            }
        }

        let mut root = Map::new();
        root.insert("t".into(), json!(models::to_iso(now_utc)));
        root.insert("poll_interval_secs".into(), json!(snapshot.poll_interval_secs));
        root.insert("providers".into(), Value::Array(providers));
        let line = match serde_json::to_string(&Value::Object(root)) {
            Ok(l) => escape_like_javascript_serializer(&l),
            Err(_) => return false,
        };

        if let Some(dir) = self.path.parent() {
            if !dir.as_os_str().is_empty() && fs::create_dir_all(dir).is_err() {
                return false;
            }
        }
        if rotate(&self.path).is_err() {
            return false;
        }
        let written = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)
            .and_then(|mut f| f.write_all(format!("{}\n", line).as_bytes()));
        if written.is_err() {
            return false;
        }

        last.fingerprint = Some(fp);
        last.write_utc = Some(now_utc);
        true
    }
}

fn rotate(path: &Path) -> std::io::Result<()> {
    let len = match fs::metadata(path) {
        Ok(m) if m.is_file() => m.len(),
        _ => return Ok(()),
    };
    if len <= MAX_BYTES {
        return Ok(());
    }
    let mut old = path.as_os_str().to_os_string();
    old.push(".1");
    let old = PathBuf::from(old);
    if old.exists() {
        fs::remove_file(&old)?;
    }
    fs::rename(path, &old)
}
