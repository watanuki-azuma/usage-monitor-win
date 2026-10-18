# Usage Monitor for Windows

A small native Windows tray app that shows usage meters for Claude Code, Codex,
Gemini (Antigravity) and Grok Build: chips next to the taskbar clock and a
flyout with every 5-hour / weekly window and its reset time.

- Rust + [Slint](https://slint.dev) (software renderer), single `.exe` (~10 MB), no Chromium
- Idle: ~32 MB working set, ~8 MB private memory, CPU ~0
- Four switchable designs from the tray menu: Classic, RPG, Fluent, Terminal
- Data files (`config.json`, `snapshot.json`, `alert-state.json`, `usage-log.jsonl`) live next to the
  exe and keep the format of the earlier C# version, which can still be built from `src/`

## Build (Rust, current)

Needs the Rust GNU toolchain (`x86_64-pc-windows-gnu`, with the `llvm-tools` component).
`rust/build.ps1` works around the gaps of that toolchain (see `SLINT-SPEC.md` §2):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File rust/build.ps1
```

Runs the tests (release) and writes `rust\target\release\UsageMonitor.exe`.

Options: `--data-dir <dir>` (where config/state live), `--no-poll` (cached snapshot only).
Render the UI to PNGs without showing windows:

```powershell
rust\target\release\UsageMonitor.exe --render-test <out-dir> --skin rpg --data-dir <dir with snapshot.json>
```

Notes per milestone: `rust/NOTES.md`.

## Build (C#, previous version)

C# 5 / .NET Framework 4 / WinForms, built with the in-box `csc.exe`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Milestone 5
```

## Credits

Native rewrite of [sb-git-cs/usage-monitor](https://github.com/sb-git-cs/usage-monitor)
(Electron) by Shivam Bajpai. MIT License, see [LICENSE](LICENSE).
