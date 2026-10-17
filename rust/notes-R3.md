# R3 notes - engine layer

Ported: `engine/poll_engine.rs`, `alert_engine.rs`, `stores.rs`, `usage_log.rs`; tests in
`engine/tests.rs` + `test_support.rs` (all 25 tests of M4_Engine.cs / M4_Review.cs / M4_UsageLog.cs,
snake_case names). build.ps1: `RESULT: 52 passed, 0 failed` (27 core + 25 engine).

## API

- `PollEngine::new(adapters: Vec<Arc<dyn UsageAdapter>>, store: Option<Arc<dyn SnapshotStore>>,
  utc_now: Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>, adapter_timeout_ms: u64)`;
  `poll_once(&self, &AdapterContext, poll_interval_secs) -> Arc<Snapshot>` (concurrent callers get the
  same `Arc`); `PollEngine::merge_with_cache(fresh, Option<&cached>, now)`. Send + Sync.
- `alert_engine::{evaluate(&Snapshot, &mut AlertState, notify_on_limit, now) -> Vec<Toast>,
  parse_state(Option<&str>), serialize_state(&AlertState)}`.
- `FileSnapshotStore` (impl `SnapshotStore`), `FileAlertStore::{load, save}`.
- `UsageLog::new(path)`, `append(&self, &Snapshot, now) -> bool` (interior Mutex, Send + Sync).

## Decisions / differences from C#

- AlertState ordering (open point of R1): `AlertState.fired` is now `contracts::FiredMap`, an
  insertion-ordered Vec-backed map (replace keeps position, remove keeps order) so alert-state.json
  keeps the C# Dictionary key order. Only contracts.rs changed (type of one field + new type).
- C# `PollOnce` can hand out `null` if Collect throws; Rust returns an empty snapshot in that
  (practically unreachable) case.
- `merge_with_cache` takes `fresh` by value; the C# test's ReferenceEquals checks became value
  equality (`r == input && r != cached`).
- Adapter panics (the Rust equivalent of exceptions) are caught on the adapter thread; the panic
  message becomes the fetch_failed message (the "kaput" test).
- File reads strip a UTF-8 BOM and decode lossily (like .NET `File.ReadAllText`) in `stores.rs`
  (answers the R1 BOM open point for the engine's files).
- usage-log: numbers that are whole are written without a fraction (JavaScriptSerializer style,
  `40`), and `< > & '` inside strings are escaped as `<` etc. like JavaScriptSerializer.
- Atomic write uses `fs::rename` (replaces on Windows) with copy fallback, no `File.Replace`.

## Open points

- `PollEngine` runs each adapter on a freshly spawned OS thread (no pool); a timed-out adapter's
  thread keeps running until it finishes (same as the C# task).
- The poll loop / UI wiring that calls `poll_once`, `evaluate`, `UsageLog::append` is R4.
- Timing tests use the C# values (150 ms timeout, 300 ms sleeps); suite ran in 0.43 s.
