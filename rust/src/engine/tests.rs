//! Ports of tests/M4_Engine.cs, M4_Review.cs and M4_UsageLog.cs (25 tests, same names in snake_case).
use std::sync::atomic::Ordering;
use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration as StdDuration;

use chrono::{DateTime, Duration, Utc};
use serde_json::{json, Value};

use super::alert_engine;
use super::poll_engine::PollEngine;
use super::stores::{FileAlertStore, FileSnapshotStore};
use super::test_support::*;
use super::usage_log::UsageLog;
use crate::core::contracts::{
    kind, state, AdapterContext, AlertEntry, AlertState, ProviderSnapshot, ProviderStatus, Snapshot,
    SnapshotStore, UsageAdapter, UsageWindow,
};
use crate::core::models;

fn sleep_ms(ms: u64) {
    std::thread::sleep(StdDuration::from_millis(ms));
}

fn ok_provider(id: &str, used: f64, now: DateTime<Utc>) -> ProviderSnapshot {
    let mut p = ProviderSnapshot::new(id, now);
    p.display_name = Some(id.to_uppercase());
    p.status = ProviderStatus { state: state::OK.to_string(), ..Default::default() };
    p.windows.push(models::window_of(
        kind::DAILY,
        "Pro",
        &json!(used),
        Some(&models::to_iso(now + Duration::hours(3))),
    ));
    p.source = "api".to_string();
    p
}

fn rate_limited(id: &str, now: DateTime<Utc>) -> ProviderSnapshot {
    let st = ProviderStatus {
        state: state::FETCH_FAILED.to_string(),
        message: Some("rate limited".to_string()),
        ..Default::default()
    };
    let mut p = models::empty_provider(id, &id.to_uppercase(), st, now);
    p.rate_limited = true;
    p
}

struct Rig {
    clock: TestClock,
    store: Arc<MemoryStore>,
    adapters: Vec<Arc<FakeAdapter>>,
    engine: Arc<PollEngine>,
    ctx: AdapterContext,
}

impl Rig {
    fn new(timeout_ms: u64) -> Rig {
        let clock = TestClock::new();
        let store = Arc::new(MemoryStore::default());
        let mut adapters = Vec::new();
        for id in models::PROVIDER_IDS.iter() {
            let c = clock.clone();
            let captured = id.to_string();
            adapters.push(Arc::new(FakeAdapter::new(
                id,
                Arc::new(move |_| ok_provider(&captured, 50.0, c.now())),
            )));
        }
        let list: Vec<Arc<dyn UsageAdapter>> = adapters.iter().map(|a| a.clone() as Arc<dyn UsageAdapter>).collect();
        let engine = Arc::new(PollEngine::new(list, Some(store.clone() as Arc<dyn SnapshotStore>), clock.as_fn(), timeout_ms));
        let ctx = test_context(&clock);
        Rig { clock, store, adapters, engine, ctx }
    }

    fn total_calls(&self) -> usize {
        self.adapters.iter().map(|a| a.calls()).sum()
    }

    fn poll(&self, interval: i32) -> Arc<Snapshot> {
        self.engine.poll_once(&self.ctx, interval)
    }
}

// ---- PollEngineTests ---------------------------------------------------------------------------

#[test]
fn poll_returns_every_provider_in_order_and_saves() {
    let r = Rig::new(5000);
    let s = r.poll(30);
    assert_eq!(4, s.providers.len(), "providers");
    assert_eq!("claude", s.providers[0].id, "order 0");
    assert_eq!("grok", s.providers[3].id, "order 3");
    assert_eq!(30, s.poll_interval_secs, "interval carried");
    assert_eq!(r.clock.now(), s.generated_at, "generated at");
    assert_eq!(4, r.total_calls(), "each adapter once");
    assert!(r.store.saved.lock().unwrap().is_some(), "snapshot persisted");
    assert_eq!(1, r.store.saves.load(Ordering::SeqCst), "saved once");
}

#[test]
fn save_failure_does_not_lose_fresh_data() {
    let r = Rig::new(5000);
    r.store.throw_on_save.store(true, Ordering::SeqCst);
    let s = r.poll(5);
    assert_eq!(4, s.providers.len(), "fresh data delivered");
    assert_eq!(state::OK, s.providers[0].status.state, "state ok");
}

#[test]
fn concurrent_polls_coalesce() {
    let r = Rig::new(5000);
    for a in &r.adapters {
        let id = a.id().to_string();
        let c = r.clock.clone();
        a.set_fetch(Arc::new(move |_| {
            sleep_ms(300);
            ok_provider(&id, 10.0, c.now())
        }));
    }
    // the engine is shared with two threads
    let engine = r.engine.clone();
    let (e1, c1) = (engine.clone(), r.ctx.clone());
    let (e2, c2) = (engine.clone(), r.ctx.clone());
    let t1 = std::thread::spawn(move || e1.poll_once(&c1, 5));
    let t2 = std::thread::spawn(move || {
        sleep_ms(50);
        e2.poll_once(&c2, 5)
    });
    let s1 = t1.join().unwrap();
    let s2 = t2.join().unwrap();
    assert_eq!(4, r.total_calls(), "adapters called once in total");
    assert!(Arc::ptr_eq(&s1, &s2), "both callers get the same snapshot");
}

#[test]
fn timed_out_adapter_is_never_started_twice_and_late_result_is_used() {
    let r = Rig::new(150);
    let gate = Arc::new((Mutex::new(false), Condvar::new()));
    let claude = r.adapters[0].clone();
    {
        let gate = gate.clone();
        let c = r.clock.clone();
        claude.set_fetch(Arc::new(move |_| {
            let (m, cv) = &*gate;
            let g = m.lock().unwrap();
            let _ = cv.wait_timeout_while(g, StdDuration::from_secs(10), |open| !*open).unwrap();
            ok_provider("claude", 93.0, c.now())
        }));
    }
    let first = r.poll(5);
    assert_eq!(state::FETCH_FAILED, first.providers[0].status.state, "timed out without cache -> fetch_failed");
    assert_eq!(1, claude.calls(), "started once");
    r.poll(5);
    assert_eq!(1, claude.calls(), "not started again while the first is still pending");
    {
        let (m, cv) = &*gate;
        *m.lock().unwrap() = true;
        cv.notify_all();
    }
    sleep_ms(100);
    let third = r.poll(5);
    assert_eq!(1, claude.calls(), "late result consumed instead of re-fetching");
    assert_eq!(Some(93.0), third.providers[0].windows[0].used_pct, "late value shown");
}

#[test]
fn rate_limit_backoff_doubles_up_to_30_minutes() {
    let r = Rig::new(5000);
    let codex = r.adapters[1].clone();
    {
        let c = r.clock.clone();
        codex.set_fetch(Arc::new(move |_| rate_limited("codex", c.now())));
    }
    let minutes = [2i64, 4, 8, 16, 30, 30];
    let mut expected_calls = 0;
    for m in minutes.iter() {
        r.poll(5);
        expected_calls += 1;
        assert_eq!(expected_calls, codex.calls(), "poll at start of backoff step {} calls the adapter", m);
        r.clock.advance(Duration::seconds(m * 60 - 1));
        r.poll(5);
        assert_eq!(expected_calls, codex.calls(), "1s before {} min: still backing off", m);
        r.clock.advance(Duration::seconds(2));
    }
    r.poll(5);
    assert_eq!(expected_calls + 1, codex.calls(), "after the last backoff the adapter runs again");
}

#[test]
fn rate_limited_provider_with_cache_is_stale_with_reason_and_retry_time() {
    let r = Rig::new(5000);
    let claude = r.adapters[0].clone();
    let good = r.poll(5);
    assert_eq!(state::OK, good.providers[0].status.state, "first ok");
    r.clock.advance(Duration::minutes(10));
    {
        let c = r.clock.clone();
        claude.set_fetch(Arc::new(move |_| rate_limited("claude", c.now())));
    }
    let s = r.poll(5);
    let p = &s.providers[0];
    assert_eq!(state::STALE, p.status.state, "stale");
    assert_eq!(Some("rate_limited".to_string()), p.status.reason, "reason");
    assert_eq!(Some(r.clock.now() + Duration::minutes(2)), p.status.retry_at, "retry in 2 minutes");
    assert_eq!(Some(50.0), p.windows[0].used_pct, "cached value shown");
    assert_eq!("cache", p.source, "source cache");
}

#[test]
fn claude_is_polled_at_most_every_5_minutes() {
    let r = Rig::new(5000);
    r.poll(5);
    r.clock.advance(Duration::seconds(60));
    r.poll(5);
    assert_eq!(1, r.adapters[0].calls(), "claude skipped inside 5 minutes");
    assert_eq!(2, r.adapters[1].calls(), "codex polled every time");
    r.clock.advance(Duration::seconds(5 * 60));
    r.poll(5);
    assert_eq!(2, r.adapters[0].calls(), "claude polled again after 5 minutes");
}

#[test]
fn adapter_exception_becomes_fetch_failed_for_that_provider_only() {
    let r = Rig::new(5000);
    r.adapters[2].set_fetch(Arc::new(|_| panic!("kaput")));
    let s = r.poll(5);
    assert_eq!(state::FETCH_FAILED, s.providers[2].status.state, "failed provider");
    assert!(s.providers[2].status.message.as_deref().unwrap_or("").contains("kaput"), "message");
    assert_eq!(state::OK, s.providers[0].status.state, "others unaffected");
    assert_eq!(state::OK, s.providers[3].status.state, "others unaffected");
}

#[test]
fn expired_windows_are_reset_in_the_result() {
    let r = Rig::new(5000);
    let c = r.clock.clone();
    r.adapters[0].set_fetch(Arc::new(move |_| {
        let mut p = ok_provider("claude", 77.0, c.now());
        p.windows[0].resets_at = Some(c.now() - Duration::minutes(1));
        p
    }));
    let s = r.poll(5);
    assert_eq!(None, s.providers[0].windows[0].used_pct, "expired window unknown");
    assert_eq!(state::STALE, s.providers[0].status.state, "provider stale");
}

#[test]
fn merge_with_cache() {
    let now = utc(2026, 10, 11, 12, 0, 0);
    let cached = ok_provider("claude", 88.0, now - Duration::minutes(3));

    let failed = ProviderStatus { state: state::FETCH_FAILED.to_string(), ..Default::default() };
    let fresh = models::empty_provider("claude", "Claude Code", failed.clone(), now);
    let merged = PollEngine::merge_with_cache(fresh.clone(), Some(&cached), now);
    assert_eq!(state::STALE, merged.status.state, "stale");
    assert_eq!(Some(88.0), merged.windows[0].used_pct, "cached windows");
    assert_eq!(cached.fetched_at, merged.fetched_at, "original timestamp");
    assert_eq!("cache", merged.source, "source");
    assert_eq!(Some(180), merged.status.age_secs, "age in seconds");

    // C# asserts reference identity; values are moved in Rust, so "returned unchanged" = equality
    // with the input and difference from the cached provider.
    let lo = ProviderStatus { state: state::LOGGED_OUT.to_string(), ..Default::default() };
    let logged_out = models::empty_provider("claude", "Claude Code", lo, now);
    let r = PollEngine::merge_with_cache(logged_out.clone(), Some(&cached), now);
    assert!(r == logged_out && r != cached, "logged_out is not masked by cache");

    let ok = ok_provider("claude", 10.0, now);
    let r = PollEngine::merge_with_cache(ok.clone(), Some(&cached), now);
    assert!(r == ok && r != cached, "ok wins");
    assert!(PollEngine::merge_with_cache(fresh.clone(), None, now) == fresh, "no cache -> fresh");

    let empty_cache = models::empty_provider("claude", "Claude Code", failed, now - Duration::minutes(1));
    assert!(
        PollEngine::merge_with_cache(fresh.clone(), Some(&empty_cache), now) == fresh,
        "cache without windows is useless"
    );
}

// ---- AlertEngineTests --------------------------------------------------------------------------

fn alert_now() -> DateTime<Utc> {
    utc(2026, 10, 11, 12, 0, 0)
}

fn gemini(st: &str, windows: Vec<UsageWindow>) -> ProviderSnapshot {
    let mut p = ProviderSnapshot::new("gemini", alert_now());
    p.display_name = Some("Gemini".to_string());
    p.status = ProviderStatus { state: st.to_string(), ..Default::default() };
    p.windows = windows;
    p
}

fn win(k: &str, label: &str, used: Option<f64>, reset: Option<DateTime<Utc>>) -> UsageWindow {
    UsageWindow { kind: k.to_string(), label: label.to_string(), used_pct: used, resets_at: reset, ..Default::default() }
}

fn snap(p: ProviderSnapshot) -> Snapshot {
    let mut s = Snapshot::new(alert_now());
    s.providers.push(p);
    s
}

#[test]
fn limit_reached_and_80_percent_toasts_are_deduplicated() {
    let now = alert_now();
    let mut st = AlertState::default();
    let reset = now + Duration::hours(3);
    let p = gemini(
        state::OK,
        vec![win(kind::DAILY, "Pro", Some(100.0), Some(reset)), win(kind::DAILY, "Flash", Some(85.0), Some(reset))],
    );
    let t = alert_engine::evaluate(&snap(p.clone()), &mut st, true, now);
    assert_eq!(2, t.len(), "two toasts");
    assert_eq!("Usage Monitor \u{2014} Gemini", t[0].title, "title");
    assert_eq!("Pro limit reached", t[0].body, "limit body");
    assert_eq!("Flash 85/100% used", t[1].body, "80% body");
    assert_eq!(0, alert_engine::evaluate(&snap(p), &mut st, true, now).len(), "no repeat");

    let stale = gemini(
        state::STALE,
        vec![win(kind::DAILY, "Pro", Some(100.0), Some(reset)), win(kind::DAILY, "Other", Some(90.0), Some(reset))],
    );
    assert_eq!(0, alert_engine::evaluate(&snap(stale), &mut st, true, now).len(), "stale providers never alert");
}

#[test]
fn without_notify_on_limit_100_percent_is_a_normal_80_toast() {
    let now = alert_now();
    let mut st = AlertState::default();
    let p = gemini(state::OK, vec![win(kind::FIVE_HOUR, "5h", Some(100.0), Some(now + Duration::hours(1)))]);
    let t = alert_engine::evaluate(&snap(p.clone()), &mut st, false, now);
    assert_eq!(1, t.len(), "one toast");
    assert_eq!("5h 100/100% used", t[0].body, "body");
    assert_eq!(0, alert_engine::evaluate(&snap(p), &mut st, false, now).len(), "once only");
}

#[test]
fn alert_rearms_after_usage_drops_below_80() {
    let now = alert_now();
    let mut st = AlertState::default();
    let reset = now + Duration::hours(2);
    let hi = gemini(state::OK, vec![win(kind::FIVE_HOUR, "5h", Some(85.0), Some(reset))]);
    let lo = gemini(state::OK, vec![win(kind::FIVE_HOUR, "5h", Some(40.0), Some(reset))]);
    assert_eq!(1, alert_engine::evaluate(&snap(hi.clone()), &mut st, true, now).len(), "first crossing");
    assert_eq!(0, alert_engine::evaluate(&snap(lo), &mut st, true, now).len(), "below threshold");
    assert_eq!(1, alert_engine::evaluate(&snap(hi), &mut st, true, now).len(), "second crossing alerts again");
}

#[test]
fn new_reset_period_alerts_again_and_expired_or_unknown_windows_are_ignored() {
    let now = alert_now();
    let mut st = AlertState::default();
    let a = gemini(state::OK, vec![win(kind::FIVE_HOUR, "5h", Some(90.0), Some(now + Duration::hours(2)))]);
    assert_eq!(1, alert_engine::evaluate(&snap(a), &mut st, true, now).len(), "period 1");
    let b = gemini(state::OK, vec![win(kind::FIVE_HOUR, "5h", Some(90.0), Some(now + Duration::hours(7)))]);
    assert_eq!(1, alert_engine::evaluate(&snap(b), &mut st, true, now).len(), "period 2 is a different key");
    let c = gemini(
        state::OK,
        vec![
            win(kind::FIVE_HOUR, "5h", Some(99.0), Some(now - Duration::minutes(1))),
            win(kind::DAILY, "Pro", None, Some(now + Duration::hours(1))),
        ],
    );
    let mut fresh_state = AlertState::default();
    assert_eq!(0, alert_engine::evaluate(&snap(c), &mut fresh_state, true, now).len(), "expired / unknown ignored");
}

#[test]
fn state_is_pruned_after_the_window_reset_time() {
    let now = alert_now();
    let mut st = AlertState::default();
    st.fired.insert(
        "gemini|daily|Old|0".to_string(),
        AlertEntry { crossed_80: true, until: Some(now - Duration::minutes(1)), ..Default::default() },
    );
    st.fired.insert(
        "gemini|daily|Keep|0".to_string(),
        AlertEntry { crossed_80: true, until: Some(now + Duration::hours(1)), ..Default::default() },
    );
    alert_engine::evaluate(&snap(gemini(state::OK, vec![])), &mut st, true, now);
    assert!(!st.fired.contains_key("gemini|daily|Old|0"), "expired entry removed");
    assert!(st.fired.contains_key("gemini|daily|Keep|0"), "live entry kept");
}

#[test]
fn state_json_roundtrip_and_tolerance() {
    let mut s = AlertState::default();
    s.fired.insert(
        "claude|five_hour|5h|123".to_string(),
        AlertEntry { crossed_80: true, crossed_100: false, until: Some(utc(2026, 10, 12, 0, 0, 0)) },
    );
    let json = alert_engine::serialize_state(&s);
    assert!(json.contains("\"fired\""), "fired key");
    assert!(json.contains("\"crossed80\": true"), "crossed80 key");
    let back = alert_engine::parse_state(Some(&json));
    let e = back.fired.get("claude|five_hour|5h|123").unwrap();
    assert!(e.crossed_80, "crossed80 survives");
    assert_eq!(Some(utc(2026, 10, 12, 0, 0, 0)), e.until, "until survives");

    assert_eq!(0, alert_engine::parse_state(Some("bad")).fired.len(), "garbage");
    assert_eq!(0, alert_engine::parse_state(None).fired.len(), "null");
    assert_eq!(0, alert_engine::parse_state(Some("{\"fired\":[]}")).fired.len(), "array fired");
    assert_eq!(0, alert_engine::parse_state(Some("{\"fired\":{\"invalid\":null}}")).fired.len(), "null entry skipped");
}

// ---- StoreTests --------------------------------------------------------------------------------

#[test]
fn snapshot_store_roundtrip_atomic_and_tolerant() {
    let dir = TempDir::create();
    let path = dir.path().join("sub").join("snapshot.json");
    let store = FileSnapshotStore::new(path.clone());
    assert!(store.load().is_none(), "missing file");
    let mut s = Snapshot::new(utc(2026, 10, 11, 12, 0, 0));
    s.poll_interval_secs = 15;
    let mut p = ProviderSnapshot::new("claude", utc(2026, 10, 11, 11, 59, 0));
    p.display_name = Some("Claude Code".to_string());
    p.status = ProviderStatus { state: state::OK.to_string(), ..Default::default() };
    p.windows.push(models::window_of(kind::FIVE_HOUR, "5h", &json!(12.5), Some("2026-10-11T15:00:00Z")));
    p.source = "api".to_string();
    s.providers.push(p);
    store.save(&s).unwrap();
    assert!(path.exists(), "file written (directory created)");
    let leftovers: Vec<_> = std::fs::read_dir(path.parent().unwrap())
        .unwrap()
        .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
        .collect();
    assert_eq!(vec!["snapshot.json".to_string()], leftovers, "no temp file left");
    let back = store.load().expect("loaded");
    assert_eq!(Some(12.5), back.providers[0].windows[0].used_pct, "value");
    std::fs::write(&path, "{ truncated").unwrap();
    assert!(store.load().is_none(), "corrupt file -> null");
}

#[test]
fn alert_store_roundtrip() {
    let dir = TempDir::create();
    let path = dir.path().join("alert-state.json");
    let store = FileAlertStore::new(path.clone());
    assert_eq!(0, store.load().fired.len(), "missing -> empty");
    let mut s = AlertState::default();
    s.fired.insert("k".to_string(), AlertEntry { crossed_100: true, ..Default::default() });
    store.save(&s).unwrap();
    assert!(store.load().fired.get("k").unwrap().crossed_100, "roundtrip");
    std::fs::write(&path, "nonsense").unwrap();
    assert_eq!(0, store.load().fired.len(), "corrupt -> empty");
}

// ---- EngineReviewTests -------------------------------------------------------------------------

#[test]
fn backoff_stays_capped_after_dozens_of_consecutive_rate_limits() {
    let r = Rig::new(5000);
    let codex = r.adapters[1].clone();
    {
        let c = r.clock.clone();
        codex.set_fetch(Arc::new(move |_| rate_limited("codex", c.now())));
    }
    for i in 1..=45usize {
        r.poll(5);
        assert_eq!(i, codex.calls(), "poll {} reaches the adapter", i);
        r.clock.advance(Duration::seconds(1));
        r.poll(5);
        assert_eq!(i, codex.calls(), "poll {} + 1s is still inside the backoff", i);
        r.clock.advance(Duration::minutes(31));
    }
}

#[test]
fn quiet_windows_do_not_accumulate_alert_state_forever() {
    let mut st = AlertState::default();
    let t0 = utc(2026, 10, 11, 12, 0, 0);
    for period in 0..20 {
        let now = t0 + Duration::hours(period * 6);
        let mut p = ProviderSnapshot::new("claude", now);
        p.display_name = Some("Claude Code".to_string());
        p.status = ProviderStatus { state: state::OK.to_string(), ..Default::default() };
        p.windows.push(win(kind::FIVE_HOUR, "5h", Some(10.0), Some(now + Duration::hours(5))));
        let mut s = Snapshot::new(now);
        s.providers.push(p);
        alert_engine::evaluate(&s, &mut st, true, now);
    }
    assert!(st.fired.len() <= 2, "old reset periods are pruned (entries: {})", st.fired.len());
}

#[test]
fn throttling_survives_a_failing_snapshot_store() {
    let r = Rig::new(5000);
    r.store.throw_on_save.store(true, Ordering::SeqCst);
    r.poll(5);
    r.clock.advance(Duration::seconds(60));
    r.poll(5);
    assert_eq!(1, r.adapters[0].calls(), "claude 5-minute gap is kept from memory when the file cannot be written");
}

#[test]
fn a_cache_dated_in_the_future_does_not_suppress_polling() {
    let r = Rig::new(5000);
    r.poll(5);
    r.clock.advance(Duration::hours(-1));
    r.poll(5);
    assert_eq!(2, r.adapters[0].calls(), "after the clock moved back the cached reading is not trusted");
}

// ---- R3 review regressions (Rust-only) ---------------------------------------------------------

#[test]
fn a_panicking_clock_never_hangs_poll_once() {
    let clock = TestClock::new();
    let adapter: Arc<dyn UsageAdapter> = Arc::new(FakeAdapter::new("claude", Arc::new(|_| panic!("unused"))));
    let engine = Arc::new(PollEngine::new(
        vec![adapter],
        None,
        Arc::new(|| -> DateTime<Utc> { panic!("clock broken") }),
        100,
    ));
    let ctx = test_context(&clock);
    let (tx, rx) = std::sync::mpsc::channel();
    for _ in 0..4 {
        let (e, c, tx) = (engine.clone(), ctx.clone(), tx.clone());
        std::thread::spawn(move || {
            let s = e.poll_once(&c, 7);
            let _ = tx.send(s.poll_interval_secs);
        });
    }
    for _ in 0..4 {
        assert_eq!(Ok(7), rx.recv_timeout(StdDuration::from_secs(10)), "poll_once returned");
    }
    assert_eq!(7, engine.poll_once(&ctx, 7).poll_interval_secs, "later calls still work");
}

#[test]
fn concurrent_saves_never_mix_content() {
    let dir = TempDir::create();
    let path = dir.path().join("snapshot.json");
    let mut handles = Vec::new();
    for n in 0..4 {
        let store = FileSnapshotStore::new(path.clone());
        handles.push(std::thread::spawn(move || {
            for i in 0..25 {
                let mut s = Snapshot::new(utc(2026, 10, 11, 12, 0, 0));
                s.poll_interval_secs = n * 100 + i;
                let mut p = ProviderSnapshot::new("claude", utc(2026, 10, 11, 12, 0, 0));
                p.status = ProviderStatus { state: state::OK.to_string(), ..Default::default() };
                s.providers.push(p);
                let _ = store.save(&s); // a Windows sharing violation on rename may fail a save
            }
        }));
    }
    for h in handles {
        h.join().unwrap();
    }
    assert!(FileSnapshotStore::new(path).load().is_some(), "file is valid JSON");
}

// ---- UsageLogTests -----------------------------------------------------------------------------

fn log_snap(claude_used: f64, st: &str) -> Snapshot {
    let fetched = utc(2026, 10, 11, 12, 0, 0);
    let mut s = Snapshot::new(fetched);
    s.poll_interval_secs = 60;
    let mut c = ProviderSnapshot::new("claude", fetched);
    c.display_name = Some("Claude Code".to_string());
    c.plan = Some("Pro".to_string());
    c.source = "api".to_string();
    c.status = ProviderStatus { state: st.to_string(), ..Default::default() };
    c.windows.push(UsageWindow {
        kind: kind::FIVE_HOUR.to_string(),
        label: "5h".to_string(),
        used_pct: Some(claude_used),
        remaining_pct: Some(100.0 - claude_used),
        resets_at: Some(utc(2026, 10, 11, 15, 0, 0)),
    });
    s.providers.push(c);
    let mut g = ProviderSnapshot::new("grok", fetched);
    g.display_name = Some("Grok Build".to_string());
    g.source = "none".to_string();
    g.status = ProviderStatus {
        state: state::NOT_INSTALLED.to_string(),
        hint: Some("secret-looking hint must not matter".to_string()),
        ..Default::default()
    };
    s.providers.push(g);
    s
}

fn lines(path: &std::path::Path) -> Vec<String> {
    std::fs::read_to_string(path).unwrap().lines().map(|l| l.to_string()).collect()
}

#[test]
fn append_writes_one_json_line_with_usage_only() {
    let dir = TempDir::create();
    let path = dir.path().join("usage-log.jsonl");
    let log = UsageLog::new(path.clone());
    let now = utc(2026, 10, 11, 12, 0, 5);
    assert!(log.append(&log_snap(40.0, state::OK), now), "written");
    let l = lines(&path);
    assert_eq!(1, l.len(), "one line");
    let root: Value = serde_json::from_str(&l[0]).unwrap();
    assert_eq!("2026-10-11T12:00:05.000Z", root["t"].as_str().unwrap(), "timestamp");
    let providers = root["providers"].as_array().unwrap();
    assert_eq!(2, providers.len(), "providers");
    let claude = &providers[0];
    assert_eq!("claude", claude["id"].as_str().unwrap(), "id");
    assert_eq!("ok", claude["state"].as_str().unwrap(), "state");
    assert_eq!("Pro", claude["plan"].as_str().unwrap(), "plan");
    let w = &claude["windows"].as_array().unwrap()[0];
    assert_eq!("five_hour", w["kind"].as_str().unwrap(), "kind");
    assert_eq!(40.0, w["used_pct"].as_f64().unwrap(), "used");
    assert_eq!("2026-10-11T15:00:00.000Z", w["resets_at"].as_str().unwrap(), "reset");
    assert!(!l[0].contains("secret-looking"), "hints/messages are not logged");
    // whole numbers are written without a fraction, like JavaScriptSerializer
    assert!(l[0].contains("\"used_pct\":40,"), "line shape: {}", l[0]);
}

#[test]
fn unchanged_snapshots_are_skipped_until_the_heartbeat() {
    let dir = TempDir::create();
    let path = dir.path().join("usage-log.jsonl");
    let log = UsageLog::new(path.clone());
    let t0 = utc(2026, 10, 11, 12, 0, 0);
    assert!(log.append(&log_snap(40.0, state::OK), t0), "first");
    assert!(!log.append(&log_snap(40.0, state::OK), t0 + Duration::seconds(60)), "same values skipped");
    assert!(log.append(&log_snap(41.0, state::OK), t0 + Duration::seconds(120)), "changed value written");
    assert!(!log.append(&log_snap(41.0, state::OK), t0 + Duration::minutes(5)), "still skipped");
    assert!(log.append(&log_snap(41.0, state::OK), t0 + Duration::minutes(13)), "heartbeat after 10 minutes");
    assert!(log.append(&log_snap(41.0, state::STALE), t0 + Duration::minutes(14)), "state change written");
    assert_eq!(4, lines(&path).len(), "lines");
}

#[test]
fn log_rotates_when_it_grows_past_the_limit_and_never_throws() {
    let dir = TempDir::create();
    let path = dir.path().join("usage-log.jsonl");
    std::fs::write(&path, "x".repeat(5 * 1024 * 1024)).unwrap();
    let log = UsageLog::new(path.clone());
    assert!(log.append(&log_snap(10.0, state::OK), utc(2026, 10, 11, 12, 0, 0)), "written after rotation");
    let mut old = path.as_os_str().to_os_string();
    old.push(".1");
    assert!(std::path::Path::new(&old).exists(), "old file kept as .1");
    assert_eq!(1, lines(&path).len(), "fresh file has the new line only");

    // unwritable location: returns false, no panic
    let bad = UsageLog::new(dir.path().join("no-such-dir").join("deeper").join("x:").join("usage-log.jsonl"));
    let ok = bad.append(&log_snap(10.0, state::OK), utc(2026, 10, 11, 12, 0, 0));
    assert!(!ok, "failure is reported, not thrown");
}
