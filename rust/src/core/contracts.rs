//! Shared data types and traits (port of src/Contracts.cs).
//! All times are UTC (`DateTime<Utc>`). Trait objects are `Send + Sync` so they can be shared with
//! the polling worker thread through `Arc`.
use std::collections::HashMap;
use std::sync::Arc;

use chrono::{DateTime, Utc};

use super::app_config::AppConfig;
use super::paths::Paths;

/// Window kinds (strings, as written to snapshot.json).
pub mod kind {
    pub const FIVE_HOUR: &str = "five_hour";
    pub const WEEKLY: &str = "weekly";
    pub const WEEKLY_SCOPED: &str = "weekly_scoped";
    pub const DAILY: &str = "daily";
    pub const MONTHLY: &str = "monthly";
    pub const QUOTA: &str = "quota";
    pub const CREDITS: &str = "credits";
}

/// Provider states (strings, as written to snapshot.json).
pub mod state {
    pub const OK: &str = "ok";
    pub const STALE: &str = "stale";
    pub const LOGGED_OUT: &str = "logged_out";
    pub const NOT_INSTALLED: &str = "not_installed";
    pub const FETCH_FAILED: &str = "fetch_failed";
    pub const UNKNOWN: &str = "unknown";
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct UsageWindow {
    pub kind: String,
    pub label: String,
    /// `None` = unknown.
    pub used_pct: Option<f64>,
    /// max(0, 100 - used_pct); `None` when `used_pct` is `None`.
    pub remaining_pct: Option<f64>,
    pub resets_at: Option<DateTime<Utc>>,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct ProviderStatus {
    /// One of `state::*`.
    pub state: String,
    /// logged_out / not_installed.
    pub hint: Option<String>,
    /// fetch_failed / unknown.
    pub message: Option<String>,
    /// "rate_limited" when backing off.
    pub reason: Option<String>,
    pub retry_at: Option<DateTime<Utc>>,
    /// stale only.
    pub age_secs: Option<i32>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ProviderSnapshot {
    /// claude | codex | gemini | grok
    pub id: String,
    pub display_name: Option<String>,
    pub status: ProviderStatus,
    pub plan: Option<String>,
    pub windows: Vec<UsageWindow>,
    /// May be empty.
    pub footnotes: Vec<String>,
    pub fetched_at: DateTime<Utc>,
    /// "api" | "cache" | "none"
    pub source: String,
    /// In-memory only (JS: _rateLimited); NOT serialized.
    pub rate_limited: bool,
}

impl ProviderSnapshot {
    pub fn new(id: &str, fetched_at: DateTime<Utc>) -> ProviderSnapshot {
        ProviderSnapshot {
            id: id.to_string(),
            display_name: None,
            status: ProviderStatus::default(),
            plan: None,
            windows: Vec::new(),
            footnotes: Vec::new(),
            fetched_at,
            source: String::new(),
            rate_limited: false,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct Snapshot {
    pub generated_at: DateTime<Utc>,
    pub poll_interval_secs: i32,
    pub providers: Vec<ProviderSnapshot>,
}

impl Snapshot {
    pub fn new(generated_at: DateTime<Utc>) -> Snapshot {
        Snapshot { generated_at, poll_interval_secs: 0, providers: Vec::new() }
    }
}

// ---- HTTP ---------------------------------------------------------------------------------

#[derive(Clone, Debug, Default)]
pub struct HttpResult {
    /// HTTP status; transport errors are returned as `Err(HttpError)`, never as status 0.
    pub status: u16,
    /// UTF-8 decoded body ("" when empty).
    pub text: String,
    pub raw: Vec<u8>,
    /// Keys lower-cased.
    pub headers: HashMap<String, String>,
    /// Parsed body, `None` when not JSON.
    pub json: Option<serde_json::Value>,
}

/// Transport error, timeout, unsupported scheme (only http/https) or body > 2 MiB.
#[derive(Clone, Debug, PartialEq)]
pub struct HttpError(pub String);

impl std::fmt::Display for HttpError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for HttpError {}

pub trait Http: Send + Sync {
    fn request(
        &self,
        method: &str,
        url: &str,
        headers: &[(&str, &str)],
        body: Option<&[u8]>,
        timeout_ms: u32,
    ) -> Result<HttpResult, HttpError>;
}

pub trait CredentialReader: Send + Sync {
    /// Reads a Windows generic credential blob decoded as UTF-8, or `None` when absent.
    fn read_generic_utf8(&self, target: &str) -> Option<String>;
}

// ---- Adapters -----------------------------------------------------------------------------

#[derive(Clone)]
pub struct AdapterContext {
    pub http: Arc<dyn Http>,
    pub paths: Paths,
    pub credentials: Arc<dyn CredentialReader>,
    pub config: AppConfig,
    pub utc_now: Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

pub trait UsageAdapter: Send + Sync {
    fn id(&self) -> &str;
    fn display_name(&self) -> &str;
    /// Blocking. Never fails for expected failures (returns a provider with state fetch_failed /
    /// logged_out ...).
    fn fetch_usage(&self, ctx: &AdapterContext) -> ProviderSnapshot;
}

// ---- Persistence --------------------------------------------------------------------------

pub trait SnapshotStore: Send + Sync {
    /// `None` when missing/invalid.
    fn load(&self) -> Option<Snapshot>;
    /// Atomic (tmp + replace); may fail.
    fn save(&self, snapshot: &Snapshot) -> std::io::Result<()>;
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct AlertEntry {
    pub crossed_80: bool,
    pub crossed_100: bool,
    /// UTC; `None` = keep until pruned never.
    pub until: Option<DateTime<Utc>>,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct AlertState {
    pub fired: FiredMap,
}

/// Insertion-ordered map for `alert-state.json` (the C# Dictionary keeps insertion order, so the
/// file keeps its key order). Linear lookups: the map holds a handful of entries.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct FiredMap {
    items: Vec<(String, AlertEntry)>,
}

impl FiredMap {
    pub fn new() -> FiredMap {
        FiredMap::default()
    }
    pub fn len(&self) -> usize {
        self.items.len()
    }
    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }
    pub fn contains_key(&self, key: &str) -> bool {
        self.items.iter().any(|(k, _)| k == key)
    }
    pub fn get(&self, key: &str) -> Option<&AlertEntry> {
        self.items.iter().find(|(k, _)| k == key).map(|(_, v)| v)
    }
    /// Replaces in place (position kept) or appends.
    pub fn insert(&mut self, key: String, value: AlertEntry) {
        match self.items.iter_mut().find(|(k, _)| *k == key) {
            Some(slot) => slot.1 = value,
            None => self.items.push((key, value)),
        }
    }
    /// Keeps the order of the remaining entries.
    pub fn remove(&mut self, key: &str) -> Option<AlertEntry> {
        let pos = self.items.iter().position(|(k, _)| k == key)?;
        Some(self.items.remove(pos).1)
    }
    pub fn iter(&self) -> impl Iterator<Item = (&String, &AlertEntry)> {
        self.items.iter().map(|(k, v)| (k, v))
    }
    pub fn retain<F: FnMut(&str, &AlertEntry) -> bool>(&mut self, mut f: F) {
        self.items.retain(|(k, v)| f(k, v));
    }
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Toast {
    pub title: String,
    pub body: String,
}
