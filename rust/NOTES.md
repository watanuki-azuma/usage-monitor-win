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

## R4 (2026-10-18)

- Windows integration + classic skin + `--render-test` (Codex Sol high; review Sol xhigh: 5 findings,
  all fixed by the worker; details in `notes-R4.md`). `build.ps1` (outside the Codex sandbox, which denies
  `ReplaceFileW`): RESULT 139 passed, 0 failed; APP 9,311,232 bytes.
- Render-test classic: exit 0 in ~0.3 s, no window; matches the C# render (layout, colours, texts); fonts,
  antialiasing and dash phase differ slightly. Brand marks are letters, as in C# `Theme.Initial()`.
- Measured on this machine (release exe before the review fixes, `--no-poll`, copy of the C# data dir,
  chips shown, flyout closed): working set 25.4 MB, private 4.7 MB, 9 threads, CPU 0 ms over 30 s.
  Chips placed at the saved undocked position (118,1098), same as the C# config.
- Single instance: launching the Rust exe while the C# app runs exits in 150 ms (shared mutex/event).
- Not yet checked by hand: tray menu items, docking on the vertical taskbar, flyout focus/Esc, toasts,
  Explorer restart, autostart (R6 checklist).

## R5 (2026-10-18)

- Added `rpg`, `fluent`, and `terminal` Slint skins (`ui/rpg/app.slint`, `ui/fluent/app.slint`,
  `ui/terminal/app.slint`) routed from the single `Chips` and `Flyout` windows in `ui/classic/app.slint`
  via `in property <string> skin` on `WindowBase` (`ui/common.slint`).
- `ui::chip_model::rpg_for_provider` computes remaining `NN/100` while preserving `color_class` and
  `stale` from used%. Ported `test_rpg_chips_show_remaining_while_colour_follows_used` from `M5_Skins.cs`
  and updated `test_registry_creates_every_skin_and_unknown_is_classic` and
  `test_each_skin_measures_and_paints_chips_and_flyout`.
- Live skin switching (`skin:<id>` in `ui::app_controller`) updates `config.skin`, switches the Slint
  `skin` property on the existing `Chips` and `Flyout` windows, updates window sizes/placement/rounding
  while keeping visibility and docking state intact, and persists `config.json`.
- `build.ps1`: `RESULT: 140 passed, 0 failed`; `APP: target\release\UsageMonitor.exe (9,956,864 bytes)`.
- `--render-test` for `classic`, `rpg`, `fluent`, and `terminal` all exit 0 and match the C# reference
  renders in `rust/reference-renders/<skin>/` in dimensions, layout, colours, fonts, and texts (details
  in `notes-R5.md`).


## R6 parity checklist (2026-10-18, release exe, this machine: 2560x1440, 96 DPI, vertical taskbar left 62 px)

Run as the real app (C# app stopped, `--data-dir` = copy of the C# data dir, polling on), driven by a
script (tray messages, keyboard on the popup menu, window rects, config.json, HKCU Run).

| check | result |
|---|---|
| first poll with the real credentials | ok: claude/codex/gemini `ok`, grok `not_installed`; snapshot.json written |
| tray menu items, order, check marks | ok: same as `TrayController.cs` (Refresh now, Refresh every ▸ 5/15/30/60 [60 checked], Hide chips, Open flyout, Snap flyout/chips to taskbar, Start with Windows, Design ▸ 4 skins [current checked], Quit) |
| tray left click → flyout, foreground; Esc closes it | ok (flyout at x=62, beside the vertical taskbar) |
| single instance: 2nd launch exits 0 and shows the 1st one's flyout | ok (also against the C# app, R4) |
| snap / unsnap chips | ok; docked chips at (0,1121) 288x30 — **same as the C# app** with the same config (the "x=62" in SPEC §6 is the undocked position) |
| live skin switch rpg / fluent / classic | ok: sizes follow the skin (rpg 328 / fluent 302 / classic 288 px chips), position and visibility kept, `skin` saved |
| Start with Windows on / off | ok: HKCU Run `Usage Monitor` = quoted exe path, then removed (state restored) |
| `TaskbarCreated` (simulated Explorer restart) | ok: process alive, icon re-added, menu still opens. A real Explorer restart was not done |
| Quit from the menu | ok: process exits, tray window gone |
| idle | working set 31.9–32.3 MB, private 7.5–8.1 MB, 10 threads, CPU 156 ms over 60 s **including one real 60 s poll** |

Not checked live: toast balloons (needs a window crossing 80 %/100 %; texts are covered by the engine
tests), a real Explorer restart, a horizontal taskbar, DPI other than 100 %.
Code: `src/ui`, `src/win`, `src/main.rs` reformatted with rustfmt (R4 dense style).
