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
merged and reviewed.

- R2 (adapters, 69 tests) merged on 2026-10-18 after the 4 Sol xhigh findings were confirmed and fixed
  (ReplaceFileW, Windows static proxy, UTF-16/32 BOMs, F2 rounding; details in `notes-R2.md`).
  `build.ps1`: RESULT 123 passed, 0 failed; APP 8,157,696 bytes (still the placeholder window).
- Next: R4 (windows integration + classic skin + render-test). Routing plan: tricky-impl -> Codex Sol
  high (its workspace-write sandbox can run `build.ps1`, checked on 2026-10-18), review Sol xhigh.
- Worker notes per milestone: `notes-R3.md` (and `notes-R2.md` on the R2 branch).

## R4 (2026-10-18, partial acceptance)

- Added pure `ui::chip_model` and `ui::taskbar_math`; seven M5_Ui tests ported with the same snake_case
  names/cases. Added config skin round-trip, registry, and headless render smoke tests: ten new tests.
- Classic UI lives in `ui/common.slint` and `ui/classic/app.slint`, loaded by `slint!` in `ui/mod.rs`.
  Shared models/properties/callbacks are inherited and exported by both windows. All four registry ids
  currently instantiate classic; selecting another id keeps that id in config.json for R5.
- `Theme.cs` in this checkout uses rounded C/X/G/K initial badges, not custom brand vector shapes;
  those badges are reproduced. Stale outlines use Slint Paths with sampled rounded dashes.
- Headless platform uses `MinimalSoftwareWindow` and RGB buffers/PNG encoding; never shows an OS
  window or reads credentials. Both generated PNG pairs were viewed and iterated against the supplied
  C# references. Layout/content/colors match closely at 294x30 and 640x262. Remaining differences:
  font rasterization/weight and small baseline shifts, rounded-edge antialiasing, stale dash phase.
- Win32 modules cover the named mutex/event (same C# names), tray badge/menu/checkmarks, Explorer
  restart, Run value `Usage Monitor`, balloons, tool/no-activate/topmost styles, rounded window regions,
  taskbar/monitor geometry and DPI initialization. Chips/flyout callbacks wire refresh, interval,
  pin/dock, visibility, drag positions, Esc/Alt+F4, delayed blur, provider usage URLs and skin selection.
- PollEngine runs on its own worker; a separate I/O worker serializes config/alert persistence and
  registry work, keeping saves responsive during slow polls. Results use `invoke_from_event_loop`.
  One 1s UI timer handles local resets, visible ETA changes, activation and taskbar/watchdog checks.
  The 50ms native-handle setup timer stops once visible windows are found; blur uses a temporary 180ms
  single-shot. No animation timers. Unchanged visible data retains its Slint models.
- Only additive engine API: `engine::stores::save_config(path, config)` exposes the existing atomic
  writer for UI worker persistence. No core/adapter behavior changed. `win::wincred` reexports the R2
  reader; its implementation stays unchanged.
- Toolchain finding: linking actual Slint rendering into the release tests exposed orphan raw-dylib
  IAT entries (e.g. GetProcessHeap held an RVA rather than a resolved address), causing startup AVs.
  `vendor/windows-link` patches the existing 0.2.1 package to use the existing windows-targets 0.52.6
  GNU import library. Non-GNU behavior remains upstream. No new crate versions or C compilation;
  build.ps1 and its workarounds are untouched. Release tests and render-test now start successfully.
- Full build: `RESULT: 127 passed, 6 failed` (133 total). All ten UI tests pass. Six existing token-file
  replacement tests fail with Windows error 5. Reproduced in the default temp directory and in this
  worktree; C# File.Replace on dummy worktree files also throws UnauthorizedAccessException. These
  tests were not skipped and ReplaceFileW was not weakened. Details/result lines: `notes-R4.md`.
- M5_Skins adaptations/skips: registry test checks four ids/names/normalization and R4's allowed classic
  fallback rather than WinForms runtime types/namespaces. Each-skin smoke test renders the fallback
  for all four ids without native windows. Only `test_rpg_chips_show_remaining_while_colour_follows_used`
  is deferred wholesale: RPG rendering belongs to R5.
- Section 9: final exe size in `notes-R4.md`; idle working set/private bytes/CPU/thread count not measured.
  No normal app process was started. Read-only query reports 2560x1440 at 96 DPI, but Shell_TrayWnd is
  inaccessible on this sandbox desktop. Interactive winit DPI, docking and integration checks remain
  unverified. In particular §6's observed vertical x=62 conflicts with the checked-in C# fit/clamp
  control flow, which can yield x=0 for a wide bar; this port follows the C# source pending comparison.
- Final release: `APP: target\release\UsageMonitor.exe (9,307,136 bytes)` (8.88 MiB).
  Final classic render-test: exit 0 in 434 ms; 294x30 chips, 640x262 flyout, both inspected after build.
  `--render-test=... --skin=terminal --no-poll --data-dir=...` also exits 0 with classic fallback.
