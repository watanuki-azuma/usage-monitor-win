//! Poll engine (port of src/PollEngine.cs, itself a port of poller.js).
//!
//! * every adapter runs in parallel (own thread) behind a per-adapter timeout; a timed-out
//!   operation is *retained* and consumed by a later poll, so the same adapter is never started
//!   twice for one tick of work (token refreshes must never overlap);
//! * concurrent `poll_once` calls coalesce on one in-flight collection and all get the same
//!   `Arc<Snapshot>`;
//! * per-provider backoff 2/4/8/16/30/30... minutes on rate limit (status reason "rate_limited" plus
//!   retry_at); claude is polled at most every 5 minutes;
//! * fetch_failed/stale results merge with the cached provider (stale + cached windows + age_secs);
//! * local resets are applied to the collected result and the snapshot is saved after every poll; a
//!   failing store never loses the fresh data.
//!
//! All time comes from the injected clock. No static mutable state: two engines are independent.
//! Nothing panics out of `poll_once`.
use std::collections::HashMap;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::time::Duration as StdDuration;

use chrono::{DateTime, Duration, Utc};

use crate::core::contracts::{
    state, AdapterContext, ProviderSnapshot, ProviderStatus, Snapshot, SnapshotStore, UsageAdapter,
};
use crate::core::models::{self, PROVIDER_IDS};

const CLAUDE_ID: &str = "claude";
/// Anthropic's OAuth usage endpoint answers 429 when polled every minute (poller.js MIN_INTERVAL_MS).
const CLAUDE_MIN_GAP_MS: f64 = 5.0 * 60.0 * 1000.0;
const MAX_BACKOFF_SECS: f64 = 30.0 * 60.0;
const RATE_LIMITED_REASON: &str = "rate_limited";

pub type Clock = Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>;

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    match m.lock() {
        Ok(g) => g,
        Err(p) => p.into_inner(),
    }
}

/// An adapter call started by some poll; the result is filled in by its thread.
struct FetchSlot {
    result: Mutex<Option<ProviderSnapshot>>,
    done: Condvar,
}

impl FetchSlot {
    fn new() -> FetchSlot {
        FetchSlot { result: Mutex::new(None), done: Condvar::new() }
    }

    fn set(&self, value: ProviderSnapshot) {
        *lock(&self.result) = Some(value);
        self.done.notify_all();
    }

    /// True when the result is available within `timeout_ms`.
    fn wait(&self, timeout_ms: u64) -> bool {
        let guard = lock(&self.result);
        let (guard, _) = match self.done.wait_timeout_while(guard, StdDuration::from_millis(timeout_ms), |r| r.is_none()) {
            Ok(x) => x,
            Err(p) => p.into_inner(),
        };
        guard.is_some()
    }

    fn take_clone(&self) -> Option<ProviderSnapshot> {
        lock(&self.result).clone()
    }
}

/// One in-flight collection: the leader fills it, every concurrent caller waits and gets the
/// identical snapshot back (coalescing).
struct PollOperation {
    result: Mutex<Option<Arc<Snapshot>>>,
    done: Condvar,
}

impl PollOperation {
    fn new() -> PollOperation {
        PollOperation { result: Mutex::new(None), done: Condvar::new() }
    }

    fn run(&self, snapshot: Arc<Snapshot>) {
        *lock(&self.result) = Some(snapshot);
        self.done.notify_all();
    }

    fn result(&self) -> Arc<Snapshot> {
        let mut guard = lock(&self.result);
        loop {
            if let Some(r) = guard.as_ref() {
                return r.clone();
            }
            guard = match self.done.wait(guard) {
                Ok(g) => g,
                Err(p) => p.into_inner(),
            };
        }
    }
}

#[derive(Default)]
struct Inner {
    /// Operations started by an earlier poll that have not been consumed yet (timeout retention).
    pending: HashMap<String, Arc<FetchSlot>>,
    backoff_until: HashMap<String, DateTime<Utc>>,
    rate_limit_hits: HashMap<String, u32>,
    /// The latest snapshot this engine produced. The store is the durable copy; this one keeps the
    /// throttling and the cache merge working while the file cannot be read or written.
    last_snapshot: Option<Snapshot>,
    active: Option<Arc<PollOperation>>,
}

pub struct PollEngine {
    /// ProviderIds order first, then extras.
    adapters: Vec<Arc<dyn UsageAdapter>>,
    store: Option<Arc<dyn SnapshotStore>>,
    utc_now: Clock,
    adapter_timeout_ms: u64,
    gate: Mutex<Inner>,
}

impl PollEngine {
    pub fn new(
        adapters: Vec<Arc<dyn UsageAdapter>>,
        store: Option<Arc<dyn SnapshotStore>>,
        utc_now: Clock,
        adapter_timeout_ms: u64,
    ) -> PollEngine {
        PollEngine {
            adapters: ordered(adapters),
            store,
            utc_now,
            adapter_timeout_ms,
            gate: Mutex::new(Inner::default()),
        }
    }

    /// Blocking: runs one full collection and returns the snapshot. Concurrent calls join the
    /// in-flight collection and receive the very same `Arc<Snapshot>`.
    pub fn poll_once(&self, ctx: &AdapterContext, poll_interval_secs: i32) -> Arc<Snapshot> {
        let (op, leader) = {
            let mut g = lock(&self.gate);
            match &g.active {
                Some(op) => (op.clone(), false),
                None => {
                    let op = Arc::new(PollOperation::new());
                    g.active = Some(op.clone());
                    (op, true)
                }
            }
        };

        if leader {
            let guard = LeaderGuard { engine: self, op: op.clone(), interval: poll_interval_secs };
            if let Ok(s) = catch_unwind(AssertUnwindSafe(|| self.collect(ctx, poll_interval_secs))) {
                op.run(Arc::new(s));
            }
            // on a panic the guard publishes an empty snapshot (the C# code hands out null)
            drop(guard);
        }
        op.result()
    }

    /// mergeWithCache(): a failed/stale fresh result is only masked by the cache when the cache
    /// actually holds windows; the cached timestamp and windows are shown, the state becomes stale,
    /// the source becomes "cache" and the age of the cached data is reported.
    pub fn merge_with_cache(fresh: ProviderSnapshot, cached: Option<&ProviderSnapshot>, now: DateTime<Utc>) -> ProviderSnapshot {
        let cached = match cached {
            Some(c) => c,
            None => return fresh,
        };
        let st = fresh.status.state.as_str();
        if st == state::OK {
            return fresh;
        }
        if (st == state::FETCH_FAILED || st == state::STALE) && !cached.windows.is_empty() {
            let mut merged = cached.clone();
            merged.status = ProviderStatus {
                state: state::STALE.to_string(),
                age_secs: Some(age_seconds(now, cached.fetched_at)),
                ..Default::default()
            };
            merged.source = "cache".to_string();
            return merged;
        }
        fresh
    }

    /// One full collection: adapters in parallel, then the snapshot is reset locally and saved.
    fn collect(&self, ctx: &AdapterContext, poll_interval_secs: i32) -> Snapshot {
        let now = (self.utc_now)();

        let mut previous: Option<Snapshot> = None;
        if let Some(store) = &self.store {
            previous = catch_unwind(AssertUnwindSafe(|| store.load())).unwrap_or(None);
        }
        // The store is the source of truth, but when it has nothing to give (or refuses to be read
        // or written) the last snapshot we produced is the previous one.
        if previous.is_none() {
            previous = lock(&self.gate).last_snapshot.clone();
        }

        let mut prev_by_id: HashMap<String, ProviderSnapshot> = HashMap::new();
        if let Some(prev) = previous {
            for p in prev.providers {
                prev_by_id.entry(p.id.clone()).or_insert(p);
            }
        }

        // Every provider thread blocks at most one adapter timeout, so this returns even while a
        // timed-out adapter is still running in the background.
        let results: Vec<ProviderSnapshot> = std::thread::scope(|scope| {
            let prev_by_id = &prev_by_id;
            let handles: Vec<_> = self
                .adapters
                .iter()
                .map(|adapter| scope.spawn(move || self.collect_provider(adapter, ctx, prev_by_id, now)))
                .collect();
            handles
                .into_iter()
                .zip(self.adapters.iter())
                .map(|(h, a)| {
                    h.join().unwrap_or_else(|_| failed_provider(a.id(), a.display_name(), "poll failed", now))
                })
                .collect()
        });

        let snap = Snapshot { generated_at: now, poll_interval_secs, providers: results };
        let (next, _changed) = models::apply_local_resets(&snap, now);

        lock(&self.gate).last_snapshot = Some(next.clone());

        if let Some(store) = &self.store {
            // a failed write must never lose the fresh data
            let _ = catch_unwind(AssertUnwindSafe(|| store.save(&next)));
        }
        next
    }

    /// Per-provider part of collect(): backoff, claude min-gap, pending retention + timeout,
    /// rate-limit bookkeeping and the cache merge. Runs on its own thread, in parallel.
    fn collect_provider(
        &self,
        adapter: &Arc<dyn UsageAdapter>,
        ctx: &AdapterContext,
        prev_by_id: &HashMap<String, ProviderSnapshot>,
        now: DateTime<Utc>,
    ) -> ProviderSnapshot {
        let id = adapter.id().to_string();
        let name = adapter.display_name().to_string();
        match catch_unwind(AssertUnwindSafe(|| self.collect_provider_inner(adapter, &id, &name, ctx, prev_by_id, now))) {
            Ok(p) => p,
            Err(_) => failed_provider(&id, &name, "poll failed", now),
        }
    }

    fn collect_provider_inner(
        &self,
        adapter: &Arc<dyn UsageAdapter>,
        id: &str,
        name: &str,
        ctx: &AdapterContext,
        prev_by_id: &HashMap<String, ProviderSnapshot>,
        now: DateTime<Utc>,
    ) -> ProviderSnapshot {
        let cached = prev_by_id.get(id);

        let slot: Arc<FetchSlot>;
        {
            let mut g = lock(&self.gate);
            let backing = matches!(g.backoff_until.get(id), Some(end) if now < *end);
            if backing {
                if let Some(c) = cached {
                    if !c.windows.is_empty() {
                        return stale_from_cache(&g, c, now, id);
                    }
                    return c.clone();
                }
                // no cache at all -> fall through and try the adapter anyway (like poller.js)
            }

            // A cached fetched_at in the future means the clock moved back, not that the reading
            // is fresh: a negative age is "not recent" and must not suppress the poll.
            if id == CLAUDE_ID {
                if let Some(c) = cached {
                    let age_ms = millis_between(now, c.fetched_at);
                    if (c.status.state == state::OK || c.status.state == state::LOGGED_OUT)
                        && age_ms >= 0.0
                        && age_ms < CLAUDE_MIN_GAP_MS
                    {
                        return c.clone();
                    }
                }
            }

            slot = match g.pending.get(id) {
                Some(s) => s.clone(),
                None => {
                    let s = Arc::new(FetchSlot::new());
                    g.pending.insert(id.to_string(), s.clone());
                    start_fetch(adapter.clone(), ctx.clone(), s.clone(), now);
                    s
                }
            };
        }

        let fresh: ProviderSnapshot;
        if !slot.wait(self.adapter_timeout_ms) {
            // Timed out: the operation stays pending so a later poll consumes the late result.
            fresh = failed_provider(id, name, &format!("{} timeout", id), now);
        } else {
            let fetched = result_of(&slot, id, name, now);
            let mut g = lock(&self.gate);
            if matches!(g.pending.get(id), Some(s) if Arc::ptr_eq(s, &slot)) {
                g.pending.remove(id);
            }
            if fetched.rate_limited {
                // 2, 4, 8, 16, then 30 minutes while the provider keeps rate limiting. The
                // exponent is capped so a very long run of rate limits cannot overflow.
                let hits = g.rate_limit_hits.get(id).copied().unwrap_or(0).saturating_add(1);
                g.rate_limit_hits.insert(id.to_string(), hits);
                let exponent = hits.min(5);
                let wanted = 2f64.powi(exponent as i32) * 60.0;
                let secs = if wanted > MAX_BACKOFF_SECS { MAX_BACKOFF_SECS } else { wanted };
                g.backoff_until.insert(id.to_string(), now + Duration::seconds(secs as i64));
            } else if fetched.status.state == state::OK {
                g.backoff_until.remove(id);
                g.rate_limit_hits.remove(id);
            }
            fresh = fetched;
        }

        let mut merged = PollEngine::merge_with_cache(fresh, cached, now);

        let (has_hits, until) = {
            let g = lock(&self.gate);
            (g.rate_limit_hits.contains_key(id), g.backoff_until.get(id).copied())
        };
        if has_hits && merged.status.state == state::STALE {
            merged.status.reason = Some(RATE_LIMITED_REASON.to_string());
            merged.status.retry_at = Some(until.unwrap_or(now));
        }
        merged
    }
}

/// Backoff branch: show the cached provider as stale, with the rate-limit reason attached.
fn stale_from_cache(g: &Inner, cached: &ProviderSnapshot, now: DateTime<Utc>, id: &str) -> ProviderSnapshot {
    let mut merged = cached.clone();
    let mut st = ProviderStatus {
        state: state::STALE.to_string(),
        age_secs: Some(age_seconds(now, cached.fetched_at)),
        ..Default::default()
    };
    if g.rate_limit_hits.contains_key(id) {
        st.reason = Some(RATE_LIMITED_REASON.to_string());
        st.retry_at = Some(g.backoff_until.get(id).copied().unwrap_or(now));
    }
    merged.status = st;
    merged.source = "cache".to_string();
    merged
}

/// Runs the adapter on its own thread; a panicking adapter becomes a fetch_failed provider.
fn start_fetch(adapter: Arc<dyn UsageAdapter>, ctx: AdapterContext, slot: Arc<FetchSlot>, now: DateTime<Utc>) {
    let id = adapter.id().to_string();
    let name = adapter.display_name().to_string();
    let (spawn_id, spawn_name) = (id.clone(), name.clone());
    let slot_for_thread = slot.clone();
    let spawned = std::thread::Builder::new().name(format!("fetch-{}", id)).spawn(move || {
        let r = match catch_unwind(AssertUnwindSafe(|| adapter.fetch_usage(&ctx))) {
            Ok(p) => p,
            Err(payload) => failed_provider(&id, &name, &panic_message(payload.as_ref()), now),
        };
        slot_for_thread.set(r);
    });
    if let Err(e) = spawned {
        slot.set(failed_provider(
            &spawn_id,
            &spawn_name,
            &format!("cannot start adapter thread: {}", e),
            now,
        ));
    }
}

fn panic_message(payload: &(dyn std::any::Any + Send)) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        if !s.is_empty() {
            return s.to_string();
        }
    }
    if let Some(s) = payload.downcast_ref::<String>() {
        if !s.is_empty() {
            return s.clone();
        }
    }
    "adapter failed".to_string()
}

fn result_of(slot: &FetchSlot, id: &str, name: &str, now: DateTime<Utc>) -> ProviderSnapshot {
    slot.take_clone()
        .unwrap_or_else(|| failed_provider(id, name, "adapter produced no snapshot", now))
}

fn failed_provider(id: &str, display_name: &str, message: &str, now: DateTime<Utc>) -> ProviderSnapshot {
    let st = ProviderStatus {
        state: state::FETCH_FAILED.to_string(),
        message: Some(message.to_string()),
        ..Default::default()
    };
    models::empty_provider(id, display_name, st, now)
}

fn millis_between(later: DateTime<Utc>, earlier: DateTime<Utc>) -> f64 {
    (later - earlier).num_microseconds().map(|m| m as f64 / 1000.0).unwrap_or(f64::MAX)
}

fn age_seconds(now: DateTime<Utc>, fetched_at: DateTime<Utc>) -> i32 {
    let age = millis_between(now, fetched_at) / 1000.0;
    if age <= 0.0 {
        return 0;
    }
    if age > i32::MAX as f64 {
        return i32::MAX;
    }
    age.round() as i32
}

/// Guarantees that the leader of a collection publishes a result and clears `active` on every path
/// (including a panic while computing it), so waiting callers can never hang. The fallback
/// timestamp comes from the system clock, never from the injected (user) clock.
struct LeaderGuard<'a> {
    engine: &'a PollEngine,
    op: Arc<PollOperation>,
    interval: i32,
}

impl Drop for LeaderGuard<'_> {
    fn drop(&mut self) {
        {
            let mut r = lock(&self.op.result);
            if r.is_none() {
                let mut s = Snapshot::new(Utc::now());
                s.poll_interval_secs = self.interval;
                *r = Some(Arc::new(s));
                self.op.done.notify_all();
            }
        }
        let mut g = lock(&self.engine.gate);
        if matches!(&g.active, Some(a) if Arc::ptr_eq(a, &self.op)) {
            g.active = None;
        }
    }
}

/// Provider order: PROVIDER_IDS first (claude, codex, gemini, grok), then anything else the caller
/// supplied, in the given order. Duplicates are ignored.
fn ordered(adapters: Vec<Arc<dyn UsageAdapter>>) -> Vec<Arc<dyn UsageAdapter>> {
    let mut list: Vec<Arc<dyn UsageAdapter>> = Vec::new();
    for id in PROVIDER_IDS.iter() {
        if let Some(a) = adapters.iter().find(|a| a.id() == *id) {
            list.push(a.clone());
        }
    }
    for a in &adapters {
        if PROVIDER_IDS.contains(&a.id()) {
            continue;
        }
        if !list.iter().any(|b| std::ptr::eq(Arc::as_ptr(b) as *const u8, Arc::as_ptr(a) as *const u8)) {
            list.push(a.clone());
        }
    }
    list
}
