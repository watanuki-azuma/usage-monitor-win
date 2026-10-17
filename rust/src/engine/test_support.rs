//! Fakes shared by the engine tests (the ones of tests/Harness.cs and tests/M4_Engine.cs): fake
//! adapter, in-memory store, controllable clock, temp directories.
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use chrono::{DateTime, TimeZone, Utc};

use crate::core::app_config::AppConfig;
use crate::core::contracts::{
    AdapterContext, CredentialReader, Http, HttpError, HttpResult, ProviderSnapshot, Snapshot, SnapshotStore,
    UsageAdapter,
};
use crate::core::paths::Paths;

pub fn utc(y: i32, mo: u32, d: u32, h: u32, mi: u32, s: u32) -> DateTime<Utc> {
    Utc.with_ymd_and_hms(y, mo, d, h, mi, s).unwrap()
}

pub type FetchFn = Arc<dyn Fn(&AdapterContext) -> ProviderSnapshot + Send + Sync>;

pub struct FakeAdapter {
    id: String,
    name: String,
    pub calls: AtomicUsize,
    fetch: Mutex<FetchFn>,
}

impl FakeAdapter {
    pub fn new(id: &str, fetch: FetchFn) -> FakeAdapter {
        FakeAdapter { id: id.to_string(), name: id.to_uppercase(), calls: AtomicUsize::new(0), fetch: Mutex::new(fetch) }
    }

    pub fn set_fetch(&self, fetch: FetchFn) {
        *self.fetch.lock().unwrap() = fetch;
    }

    pub fn calls(&self) -> usize {
        self.calls.load(Ordering::SeqCst)
    }
}

impl UsageAdapter for FakeAdapter {
    fn id(&self) -> &str {
        &self.id
    }
    fn display_name(&self) -> &str {
        &self.name
    }
    fn fetch_usage(&self, ctx: &AdapterContext) -> ProviderSnapshot {
        self.calls.fetch_add(1, Ordering::SeqCst);
        let f = self.fetch.lock().unwrap().clone(); // never hold the lock while fetching
        f(ctx)
    }
}

#[derive(Default)]
pub struct MemoryStore {
    pub saved: Mutex<Option<Snapshot>>,
    pub throw_on_save: AtomicBool,
    pub saves: AtomicUsize,
}

impl SnapshotStore for MemoryStore {
    fn load(&self) -> Option<Snapshot> {
        self.saved.lock().unwrap().clone()
    }

    fn save(&self, snapshot: &Snapshot) -> std::io::Result<()> {
        self.saves.fetch_add(1, Ordering::SeqCst);
        if self.throw_on_save.load(Ordering::SeqCst) {
            return Err(std::io::Error::new(std::io::ErrorKind::Other, "disk full"));
        }
        *self.saved.lock().unwrap() = Some(snapshot.clone());
        Ok(())
    }
}

/// Controllable clock.
#[derive(Clone)]
pub struct TestClock(pub Arc<Mutex<DateTime<Utc>>>);

impl TestClock {
    pub fn new() -> TestClock {
        TestClock(Arc::new(Mutex::new(utc(2026, 10, 11, 12, 0, 0))))
    }
    pub fn now(&self) -> DateTime<Utc> {
        *self.0.lock().unwrap()
    }
    pub fn advance(&self, d: chrono::Duration) {
        let mut g = self.0.lock().unwrap();
        *g = *g + d;
    }
    pub fn as_fn(&self) -> Arc<dyn Fn() -> DateTime<Utc> + Send + Sync> {
        let c = self.clone();
        Arc::new(move || c.now())
    }
}

struct NoHttp;
impl Http for NoHttp {
    fn request(&self, _: &str, _: &str, _: &[(&str, &str)], _: Option<&[u8]>, _: u32) -> Result<HttpResult, HttpError> {
        Err(HttpError("no network in engine tests".to_string()))
    }
}

struct NoCredentials;
impl CredentialReader for NoCredentials {
    fn read_generic_utf8(&self, _: &str) -> Option<String> {
        None
    }
}

pub fn test_context(clock: &TestClock) -> AdapterContext {
    AdapterContext {
        http: Arc::new(NoHttp),
        paths: Paths::new(|_| None),
        credentials: Arc::new(NoCredentials),
        config: AppConfig::default(),
        utc_now: clock.as_fn(),
    }
}

/// Unique temp directory, removed on drop.
pub struct TempDir(PathBuf);

impl TempDir {
    pub fn create() -> TempDir {
        static COUNTER: AtomicUsize = AtomicUsize::new(0);
        let n = COUNTER.fetch_add(1, Ordering::SeqCst);
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        let p = std::env::temp_dir().join(format!("um-test-{}-{}-{}", std::process::id(), n, nanos));
        std::fs::create_dir_all(&p).unwrap();
        TempDir(p)
    }

    pub fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
