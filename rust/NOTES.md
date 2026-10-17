# Rust + Slint rewrite — notes

## R0 (2026-10-18)

- `build.ps1`: RESULT 0 passed, 0 failed; APP `target\release\UsageMonitor.exe` 8,157,696 bytes
  (placeholder window only).
- Toolchain findings (also in SLINT-SPEC.md §2): no `slint-build` (build script crashes), tests in
  `--release` (debug binaries linking Slint crash at start-up).
- `cargo tree`: no `cc` / `ring` / `aws-lc-sys` / `openssl-sys`; TLS = `ureq` → `native-tls` (SChannel).

## R1 (2026-10-18)

- `core` ported: contracts, models, format, snapshot_json, paths, app_config, json_out (writer),
  json_util (tolerant readers, the C# `JsonUtil`). Tests: `src/core/tests.rs`, all 27 tests of
  `tests/M1_Core.cs` with the same names in snake_case.
- `build.ps1`: `RESULT: 27 passed, 0 failed`; APP 8,157,696 bytes (still the placeholder window).
- Design: times are `DateTime<Utc>`; `format_eta`/`stale_text` take any chrono `TimeZone` (tests use
  `Utc` / `FixedOffset`; the app can pass `Local`). C# `object` JSON values = `serde_json::Value`.
  `Http`, `CredentialReader`, `UsageAdapter`, `SnapshotStore` are `Send + Sync` (share via `Arc`);
  `Http::request` returns `Result<HttpResult, HttpError>` (transport errors are `Err`).
  `ProviderSnapshot.status` is not optional (C# allowed null; never produced by the code).
- `Paths` only reads env vars (like C#); the "exe folder is the default data dir" rule stays in
  `main.rs` via `USAGE_MONITOR_DATA_DIR` (`--data-dir`), as in `Program.cs`.

## Open points

- `SkinRegistry` (ids + `Normalize`) lives in `core::app_config` (`SKIN_IDS`, `normalize_skin`) for now;
  `ui::skins` can reuse it. No separate "skin round-trip" test added (not in M1_Core.cs).
- `AlertState.fired` is a `HashMap` (no order); the engine milestone must decide if `alert-state.json`
  key order matters (use an ordered map then).
- Numbers: `json_out` prints doubles with Rust's shortest form without exponent and `-0` as `0`
  (C# `"R"` could print `1E+16`; irrelevant for percentages).
- JSON input is parsed by serde_json (strict); a UTF-8 BOM in a file is not stripped (neither in C#
  `Trim()`), so callers reading files should decide on BOM handling in R2/R3.
- The JSON writer covers snapshot/config only; adapters (token file rewrite, usage log) may need a
  generic `Value` -> pretty-string function in R2/R3.
- R1 review (Codex Sol high), handled:
  - numeric strings: trim only .NET `NumberStyles.Float` whitespace (U+0009–U+000D, space) — fixed;
  - ISO dates whose offset moves them outside years 1..=9999 are invalid, as in C# — fixed;
  - C# writes tiny doubles as `1E-05` (`"R"` format); Rust writes `0.00001`. Kept: SPEC.md §5.1b asks
    for numbers without exponent (Node style) and every reader (Python json, serde) accepts both;
  - `USERPROFILE` unset: C# falls back to the known folder (`GetFolderPath`); Rust uses `"."`.
    Not fixed in `core` (no Win32 there); `main.rs` can pass the known folder in R4 if needed.

## Handoff (2026-10-18, paused for quota)

State of the `slint` branch: R0, R1 (core, 27 tests), R3 (engine, 27 tests incl. 2 added after review)
merged and reviewed. `build.ps1`: RESULT 54 passed, 0 failed.

- R2 (adapters, 66 tests, 93 total with core) is done by the worker but **not merged**: branch
  `slint-r2-wip` (based on R1, without R3). Its Codex Sol xhigh review result is in `rust/notes-R2.md`
  on that branch once recorded; fix the confirmed findings, then merge into `slint`
  (no file overlap with R3 except `core/json_out.rs` additions; R3 changed `core/contracts.rs`
  `AlertState.fired` to `FiredMap`).
- Next: R4 (windows integration + classic skin + render-test). Routing plan: tricky-impl -> Codex Sol
  high (its workspace-write sandbox can run `build.ps1`, checked on 2026-10-18), review Sol xhigh.
- Worker notes per milestone: `notes-R3.md` (and `notes-R2.md` on the R2 branch).