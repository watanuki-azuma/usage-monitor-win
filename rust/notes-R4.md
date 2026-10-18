# R4 report (2026-10-18)

Status: **partial**. Windows integration, classic Slint UI, and headless rendering are implemented.
Acceptance is blocked by six existing token-replacement tests: `ReplaceFileW` returns Windows error 5
in this sandbox. C# `File.Replace` on dummy worktree files also throws `UnauthorizedAccessException`.
Adapter behavior and tests remain unchanged; all ten new UI tests pass.

Files changed: `Cargo.toml/lock`, `src/lib.rs`, `src/main.rs`, `src/engine/stores.rs` (additive
`save_config` API), `src/ui/*.rs`, `src/win/*.rs`, `ui/common.slint`, `ui/classic/app.slint`,
`vendor/windows-link/`, `NOTES.md`, this report. No changes to proto, reference renders, C# files,
or build-script workarounds. The existing windows-link dependency patch fixes orphan GNU import
entries; no new crate versions or C compilation. Details and test adaptations/skips are in NOTES.md.

Commands/results:

- `powershell -NoProfile -ExecutionPolicy Bypass -File rust/build.ps1`:
  `RESULT: 127 passed, 6 failed` (133 total).
- Same command with `-SkipTests`: `RESULT: 0 passed, 0 failed`;
  `APP: E:\claude-projects\usage-monitor-app\native\.claude\worktrees\r4\rust\target\release\UsageMonitor.exe (9,307,136 bytes)`.
- Release test executable filtered to `ui::tests::`: `10 passed; 0 failed; 123 filtered out`.
- `cargo tree --manifest-path rust/Cargo.toml --offline`: no prohibited C-building crates.

```powershell
rust\target\release\UsageMonitor.exe --render-test rust\target\r4-renders --skin classic --data-dir rust\reference-renders\data
```

Final render: **exit 0 in 434 ms**, no OS window. Chips 294x30; flyout 640x262.
The `--flag=value` form with terminal fallback also exits 0. Both PNG pairs were inspected and
iterated. Remaining visual differences: font rasterization/weight and small baseline shifts,
rounded-edge antialiasing, stale dash phase. Layout, colors and texts match closely.

Section 9: exe **9,307,136 bytes (8.88 MiB)**. Idle working set/private bytes/CPU/thread count
**not measured**. No normal app process was started or left behind. Read-only desktop query:
2560x1440, 96 DPI; Shell_TrayWnd is inaccessible from this sandbox.

Open points: rerun the full suite with ReplaceFileW permissions; verify native tray/menu, docking,
focus, single-instance interop, autostart, balloons, Explorer restart, winit DPI, and idle budgets
on the interactive desktop. Section 6's observed vertical x=62 is not explained by the checked-in
C# fit/clamp logic (which can yield x=0); this port follows that code pending comparison.
RPG/Fluent/Terminal keep their config ids and use classic until R5; the RPG remaining-value test
is deferred to R5.

## Review fixes

1. **Fixed.** `Native.DpiScale()` / `Theme.Scale` use a fixed, capped system scale. Rust now
   caches that same scale and sets `SLINT_SCALE_FACTOR` before backend/worker creation. The
   checked-in Slint backend honors it on window creation/recreation and ignores subsequent
   winit DPI changes, keeping rendering and native placement consistent (300% becomes 200%).
2. **Fixed.** C#'s `ManualResetEventSlim` coalesces refreshes during a poll. Rust now drains
   pending commands before polling, applies the latest config, merges refreshes, and honors Stop.
3. **Fixed.** C# render sizes use `Theme.Scale`; Rust now applies the capped system scale to
   both headless Slint windows and physical PNG buffers (640 becomes 960 pixels at 150%).
4. **Fixed.** `--no-poll` now creates no poll command channel and skips forwarding to it.
5. **Fixed.** Like C# `SetInterval()`, every interval selection sends Config then Refresh,
   including selection of the already-checked interval.
6. **Not real in this checkout.** `src/Ui/Theme.cs`'s brand-mark section defines `Initial()`
   as C/X/G/K. `ChipsForm.cs:296` and `FlyoutForm.cs:933` draw those strings with
   `DrawTextCenter`; there are no brand-shape paths to port. The supplied reference PNGs are
   consistent with these small letter badges. Retained the source-of-truth behavior.

Tests: added six tests for refresh-burst coalescing/latest config, Stop, repeated interval
selection, disabled forwarding, pure DPI fallback/cap, and physical-size rounding. Extended
the existing headless smoke test to 100%, 150%, and 200%, checking scale, buffers, and content
near the right edge. Tests create no native windows and access no registry or network.

- Final full suite (`build.ps1 -SkipRelease`): `RESULT: 133 passed, 6 failed` (139 total),
  only the six known sandbox `ReplaceFileW` failures; adapter code/tests unchanged.
- Release test executable with exactly those six excluded:
  **`RESULT: 133 passed, 0 failed`** (6 filtered out).
- `build.ps1 -SkipTests`: success; `APP: target\release\UsageMonitor.exe (9,311,232 bytes)`.
- Requested render command: exit 0 in **375 ms**. Personally compared both generated PNGs
  in `target/r4-renders/` with `reference-renders/classic/`: matching dimensions (294x30 chips,
  640x262 flyout at this system's 100% scale), layout, colors, and text. Remaining differences
  are font rasterization/weight/baselines, rounded-edge antialiasing, and stale dash phase.

Only `rust/` changed; no dependencies added, no commit/push, no normal app launch, and no
worktree app process left running. Native interactive DPI behavior remains untested here.
