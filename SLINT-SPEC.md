# Usage Monitor — Rust + Slint rewrite (SPEC)

Goal: rewrite the Windows tray app in this repository (C# 5 / WinForms, `src/`, `tests/`) in **Rust with
the Slint UI toolkit**. The app must do everything the C# app does and look like it, with the UI written in
`.slint` files instead of hand-written GDI+ painting. Exe size may grow (a few MB is fine); idle cost must
stay near zero (see §9).

**Source of truth.** The C# code and its 129 tests define the behaviour. `SPEC.md` is the original design
document of the C# app and is useful background, but it is partly out of date (Antigravity, the Gemini
5-hour row, the Claude/GPT rows, usage log, skins were added later). **Where `SPEC.md` and the C# code
disagree, the code wins.** Do not change the C# app; you may build and run it to compare.

---

## 1. Ground rules

- Work on the `slint` branch. Rust code lives under `rust/` (new). Do not modify `src/`, `tests/`,
  `fixtures/`, `build.ps1`, `SPEC.md`.
- Never make real network calls or read real credential files from tests or from code you run while
  developing. Tests use fakes (see §7). The real app on this machine is running; do not stop it, do not
  touch `E:\claude-projects\usage-monitor-native\` (deployment folder) or its data files.
- No secrets in the repository. Fake OAuth values in tests must be split across string literals
  (`"GOCS" + "PX-..."`, `".apps.google" + "usercontent.com"`) the same way `tests/M3_Review.cs` does,
  otherwise GitHub push protection rejects the push.
- Keep the dependency list in §3. Anything else needs a short reason in `rust/NOTES.md`; it must be pure
  Rust (no crate that compiles C/C++ code — see §2).
- If something is not decided here, do not invent product behaviour: copy the C# behaviour, and list the
  open point in `rust/NOTES.md`.

## 2. Toolchain and build (this machine)

- Rust stable **x86_64-pc-windows-gnu** (1.99.1), installed under `%USERPROFILE%\.rustup` / `.cargo`.
  `%USERPROFILE%\.cargo\bin` is **not** on PATH; scripts must add it.
- No Visual Studio / MSVC linker and no full MinGW. Two gaps of the rustup GNU self-contained toolchain,
  both solved in the prototype and to be automated by `rust/build.ps1`:
  1. **dlltool**: the bundled `self-contained\dlltool.exe` fails (it needs `as.exe`, not shipped). Use LLVM's
     dlltool instead: `rustup component add llvm-tools` (already installed), then copy
     `<sysroot>\lib\rustlib\x86_64-pc-windows-gnu\bin\llvm-ar.exe` to `rust\.tools\llvm-dlltool.exe` (llvm-ar
     acts as dlltool when its file name says so) together with `libgcc_s_seh-1.dll` and
     `libwinpthread-1.dll` from the same folder, and pass `-C dlltool=<that exe>` in `RUSTFLAGS`.
     `<sysroot>` comes from `rustc --print sysroot`.
  2. **Missing import libraries** (seen: `shlwapi`): the link fails with `cannot find -l<name>`. Generate
     `lib<name>.a` from `C:\Windows\System32\<name>.dll` exports (`llvm-readobj --coff-exports` → `.def` →
     `llvm-dlltool -d <def> -D <name>.dll -l lib<name>.a -m i386:x86-64`) into `rust\.tools\lib` and pass
     `-L native=<that dir>`. The prototype's `rust/proto/mkimplib.ps1` does exactly this.
- `rust/build.ps1` (new): sets PATH, prepares `.tools` (idempotent), sets `RUSTFLAGS`, runs
  `cargo test` then `cargo build --release`, retries the import-library step for any `cannot find -l<name>`,
  and prints `RESULT: <passed> passed, <failed> failed` and `APP: <path> (<bytes> bytes)` like the C#
  `build.ps1`. No absolute user paths committed (`rust/.cargo/config.toml` must not hard-code them;
  `.tools/` and `target/` are git-ignored).
- Because there is no C compiler, **no crate may build C/C++ code** (`cc`, `ring`, `aws-lc-sys`, `skia`,
  `freetype-sys`, `openssl-sys` ...). Check `cargo tree` when adding a dependency.

## 3. Dependencies

| crate | use | notes |
|---|---|---|
| `slint` 1.18 | UI | `default-features = false`, features `std`, `compat-1-2`, `backend-winit`, `renderer-software`, `software-renderer-systemfonts`. No Skia, no FemtoVG. |
| `windows-sys` 0.59 | Win32 | tray icon, menus, window styles, taskbar rect, registry, CredRead, mutex/event, DPI |
| `serde`, `serde_json` | JSON | `serde_json` with `preserve_order` (token files keep their key order) |
| `chrono` | time | UTC internally; local time zone for ETA texts |
| `png` | `--render-test` output | pure Rust |
| HTTPS | see below | must not pull `ring`/`aws-lc` |

HTTPS: use `ureq` with `default-features = false` and its native-tls (SChannel) feature, so TLS comes from
Windows. If that still pulls C code or misbehaves, implement `Http` on WinHTTP through `windows-sys`
(`WinHttpOpen/Connect/OpenRequest/SendRequest/ReceiveResponse/ReadData`). Either way it sits behind the
`Http` trait (§5) so adapters and tests do not care. Proxy: system default is enough.

## 4. Layout

```
rust/
  Cargo.toml  build.ps1  NOTES.md  .gitignore
  proto/                 the feasibility spike (reference only, not built by build.ps1)
  src/
    main.rs              arg parsing, single instance, start-up
    core/                models, format, snapshot_json, paths, app_config, json_out   (no Win32, no Slint)
    adapters/            claude, codex, gemini, grok, util
    engine/              poll_engine, alert_engine, stores, usage_log
    win/                 tray, menu, taskbar, window_style, wincred, registry, single_instance, toast, dpi
    ui/                  app_controller, chip_model, taskbar_math, skins (registry), render_test
  ui/                    .slint files: common.slint + one folder per skin (classic, rpg, fluent, terminal)
  tests/ or #[cfg(test)] modules — ports of the C# tests
```

`core`, `adapters`, `engine`, `ui::chip_model`, `ui::taskbar_math` must not depend on Win32 or Slint
(testable on their own), mirroring the C# layering (`SPEC.md` §2).

## 5. Behaviour to port (module by module)

Port the logic of each C# file faithfully, including edge cases its tests cover. Mapping:

| C# | Rust | key points |
|---|---|---|
| `src/Contracts.cs` | `core::contracts` | Snapshot, ProviderSnapshot, ProviderStatus, UsageWindow, AdapterContext, traits `Http`, `CredentialReader`, `UsageAdapter`, `SnapshotStore` |
| `Models.cs` | `core::models` | `percentage`, `remaining_pct`, `window_of`, `current_window`, `is_alerting`, `apply_local_resets` (never mutates its input) |
| `Format.cs` | `core::format` | ETA (`1h 05m` etc.), used/total texts, status/stale texts — exact strings |
| `SnapshotJson.cs` | `core::snapshot_json` | read/write `snapshot.json`; same keys and shape |
| `Paths.cs` | `core::paths` | every path from env vars, `--data-dir`, `USAGE_MONITOR_DATA_DIR`; default data dir = **folder of the exe** |
| `AppConfig.cs` | `core::app_config` | tolerant parse, defaults, key order of `ToJson`, includes `"skin"` (default `classic`, unknown → classic) |
| `Http.cs` | `adapters::http` | GET/POST JSON/form helpers, 8 s timeout, transport errors → error value (no panic) |
| `Adapters/*.cs` | `adapters::*` | states, hints, messages, plan labels, window kinds/labels, token refresh rules, compare-and-swap rewrite of token files (mtime check, tmp + rename, keep other keys), 429 → rate limited |
| `WinCred.cs` | `win::wincred` | `CredReadW` generic credential as UTF-8 |
| `PollEngine.cs` | `engine::poll_engine` | backoff, claude 5-min gap, timeouts, never start a timed-out adapter twice, cache merge → `stale`, save after each poll, local resets |
| `AlertEngine.cs`, `Stores.cs` | `engine::*` | 80 % / 100 % toasts once per window key, re-arm, prune; `alert-state.json` |
| `UsageLog.cs` | `engine::usage_log` | `usage-log.jsonl` in the data dir, same line format, heartbeat, 4 MiB rotation, never fails the poll |
| `Ui/ChipModel.cs`, `Ui/TaskbarMath.cs` | `ui::*` | pure helpers, same results |
| `Ui/AppController.cs`, `TrayController.cs`, `Native.cs`, `Program.cs` | `ui::app_controller`, `win::*`, `main.rs` | see §6 |
| `Ui/Theme.cs`, `ChipsForm.cs`, `FlyoutForm.cs`, `Ui/Skins/**` | `ui/*.slint` | see §8 |

Gemini (current facts, already in the C# code): all Gemini models share **one 5-hour pool**, shown as a
single `five_hour` row labelled `5h`; the "Claude and GPT models" group gives `Claude/GPT 5h` and
`Claude/GPT Wk` rows. There is **no** real Gemini weekly source: do not add a Gemini weekly row (the prod
`gemini-weekly` value does not move with Antigravity usage).

### Data compatibility (must not break)

Other tools read these files (`usage.py` next to the exe, the model-router skill):

- `snapshot.json`, `config.json`, `alert-state.json`, `usage-log.jsonl` — same keys, same shape, same
  locations as the C# app. JSON output style = Node `JSON.stringify(v, null, 2)` (`SPEC.md` §5.1b):
  2-space indent, `"key": value`, no exponent, `&<>/` and non-ASCII unescaped, UTF-8 without BOM.
- Times are UTC ISO-8601 with milliseconds and `Z`.
- `usage.py` reads `providers[].id / status.state / status.reason / plan / fetched_at /
  windows[].label / kind / used_pct / resets_at`.

A Rust build pointed at a C# data dir (`--data-dir`) must read it and keep working, and vice versa.

## 6. Windows integration

Learned from `rust/proto` (it works; reuse it):

- **Native window handle**: with winit + software renderer, Slint's `window_handle()` returns
  `NotSupported`. Give every window a unique `title` and find it with `FindWindowW(NULL, title)`.
- **Windows are created lazily**: the native window appears only after the event loop starts. Do the Win32
  set-up (styles, placement) from a 50 ms repeated `slint::Timer` that stops itself once the window is found.
  Apply the same pattern when a window is shown again after `hide()` if winit recreated it (check).
- **Styles**: chips and flyout are tool windows: add `WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, remove
  `WS_EX_APPWINDOW`, chips also `WS_EX_NOACTIVATE`; hide → set style → show (`SW_SHOWNOACTIVATE`) so no
  taskbar button appears. Flyout must take keyboard focus (Esc closes it) — check which styles the C#
  flyout uses (`Native.cs`, `FlyoutForm.cs`) and match.
- **Top-most watchdog** like the C# app (taskbar clicks can raise the taskbar above the chips).
- **Docking**: same rules as `TaskbarMath` + `AppController` (`Shell_TrayWnd`, `TrayNotifyWnd`,
  `GetWindowRect`), horizontal **and vertical** taskbars. This machine has a **vertical taskbar on the left,
  62 px wide**; the C# chips sit outside it (x = 62) there — reproduce what the C# app does.
- **Tray icon**: `Shell_NotifyIconW` on a message-only window (`HWND_MESSAGE`) created on the UI thread;
  winit's message loop dispatches its messages. Left click toggles the flyout, right click opens the menu.
  Remove the icon on quit (`NIM_DELETE`). Re-add it on `TaskbarCreated` (Explorer restart).
- **Menu**: native popup menu (`CreatePopupMenu` / `TrackPopupMenu`, `SetForegroundWindow` first) with the
  same items, order, check marks and enabled states as `TrayController.cs`: Refresh now, Refresh every
  {5,15,30,60} s, Show/Hide chips, Open/Hide flyout, Snap/Unsnap chips to taskbar, Snap/Unsnap flyout to
  taskbar, Start with Windows, **Design ▸ Classic / RPG / Fluent / Terminal** (check on current), Quit.
- **Single instance**: named mutex + named event, same names as `Program.cs`, so a C# instance and a Rust
  instance also exclude each other; a second launch makes the first show its flyout.
- **Start with Windows**: HKCU `...\Run` value, same value name as the C# app.
- **Toasts**: `Shell_NotifyIconW` `NIF_INFO` balloon (what `NotifyIcon.ShowBalloonTip` does), same texts.
- **DPI**: match the C# choice (system-DPI aware, layout scaled by the system DPI — `Native.cs`). Note that
  winit sets its own DPI awareness; check the result on this 2560×1440 screen and record it in NOTES.md.
- **Threads**: polling runs on a worker thread (blocking HTTP); results go to the UI with
  `slint::invoke_from_event_loop`. Never block the UI thread on I/O.

## 7. Tests

- Port every C# test (`tests/*.cs`, 129 tests) to Rust `#[test]`s with the same names in snake_case and the
  same fixtures (`fixtures/`). Fakes: `FakeHttp` (records requests, scripted replies), `FakeCred`, temp
  dirs for `Paths` — see `tests/Harness.cs`.
- UI tests: chip model, taskbar math, config `skin` round-trip, skin registry creates all four skins, and a
  render smoke test (each skin renders chips + flyout into a buffer without panicking).
- `rust/build.ps1` must end with `RESULT: N passed, 0 failed`.

## 8. UI in Slint

- Two windows: **chips** and **flyout**, frameless, as in the C# app. Same content per skin:
  - chips: one pill per provider (brand mark + number, colour by state, dashed/dim when stale);
  - flyout: provider cards with plan, per-window rows (label, meter, numbers, ETA), refresh, interval,
    dock/pin controls, Esc / focus-loss closing rules.
- **Four skins** — `classic`, `rpg`, `fluent`, `terminal` — each a `.slint` component set implementing the
  same interface (properties + callbacks in `common.slint`), switchable live from the tray menu, saved as
  `"skin"` in `config.json`, unknown → classic. The C# skin code (`src/Ui/Theme.cs`, `ChipsForm.cs`,
  `FlyoutForm.cs`, `src/Ui/Skins/<Skin>/*.cs`) is the visual specification: colours, fonts, sizes, spacing,
  texts, what each skin shows (e.g. RPG shows **remaining** as `NN/100` with "KO" when logged out while its
  colours follow used %).
- Brand marks: take the shapes from the C# code (`Theme.cs` brand-mark section) and express them as Slint
  `Path` commands or small embedded SVG images.
- **Reference renders**: build the C# app (`build.ps1 -Milestone 5`) and run
  `out\UsageMonitor.exe --render-test <dir> --skin <id> --data-dir <dir with snapshot.json>` for each skin
  with `fixtures/snapshot-sample.json` → `chips.png`, `flyout.png`. The Slint renders of the same snapshot
  should look like these (layout, colours, texts). Pixel identity is not required; anti-aliasing and font
  rasterisation will differ.
- `--render-test <dir> [--skin <id>] [--data-dir <dir>]`: render chips + flyout to PNG with Slint's
  software renderer **without showing any window** (use a `slint::platform` software-renderer window that
  renders into a pixel buffer), exit 0 within 10 s. Also `--no-poll` (UI only, cached snapshot) and
  `--data-dir`, as in the C# app.
- Repaint only when something visible changed. A single 1-second UI timer updates ETA texts and applies
  local resets (as the C# app); no animation timers while idle.

## 9. Performance budget (measured on the release exe)

| item | target | C# app today | prototype |
|---|---|---|---|
| idle CPU, chips shown, flyout closed | ~0 (≤ 50 ms per minute) | ~0 | 0 ms / 35 s |
| private memory idle | ≤ 30 MB | 40 MB | 3.7 MB |
| working set idle | ≤ 40 MB | 54 MB | 16 MB |
| exe size | ≤ 15 MB | 0.24 MB | 7.9 MB |
| processes / threads | 1 process | 1 | 1 |

Report these numbers in `rust/NOTES.md` at each milestone that has a runnable exe.

## 10. Milestones

Each milestone: `rust/build.ps1` passes, nothing outside `rust/` changed (except this file if asked), notes
updated. Stop after each milestone and report.

| R | scope | done when |
|---|---|---|
| R0 | `rust/` skeleton, `build.ps1` with the toolchain workarounds, `.gitignore` (prototype is already in `rust/proto`; keep it out of the main package, e.g. `[workspace] exclude = ["proto"]`) | clean clone builds an empty app with `build.ps1` |
| R1 | `core` + its tests (M1_Core) | tests pass |
| R2 | `adapters` + Http + WinCred + tests (M2_*, M3_*) | tests pass, no real network in tests |
| R3 | `engine` + tests (M4_*) | tests pass |
| R4 | windows integration + **classic** skin + render-test | app runs against a copy of the data dir with `--data-dir`; classic render matches the C# render; budget in §9 met |
| R5 | rpg, fluent, terminal skins + live switching | four renders match their C# renders; switching keeps chips/flyout visibility and placement |
| R6 | parity pass | checklist in NOTES.md: every tray menu item, docking on vertical + horizontal taskbars, single instance with the C# app, autostart, toasts, Explorer restart |

## 11. Report after each milestone

Status (done / partial / blocked), files changed, commands run with their result lines (`RESULT:`,
`APP:`), measured numbers (§9), open points. Do not claim a check you did not run.
