# Usage Monitor for Windows

A small native Windows tray app that shows usage meters for Claude Code, Codex,
Gemini (Antigravity) and Grok Build: chips next to the taskbar clock and a
flyout with every 5-hour / weekly window and its reset time.

- Single `.exe` (~240 KB), no Chromium, idle CPU ~0
- C# 5 / .NET Framework 4 / WinForms + GDI+, built with the in-box `csc.exe`
  (no Visual Studio, no NuGet)
- Four switchable designs from the tray menu: Classic, RPG, Fluent, Terminal

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Milestone 5
```

Runs the tests and writes `out\UsageMonitor.exe`.

Render the UI to PNGs without showing windows:

```powershell
out\UsageMonitor.exe --render-test <out-dir> --skin rpg --data-dir <dir with snapshot.json>
```

## Credits

Native rewrite of [sb-git-cs/usage-monitor](https://github.com/sb-git-cs/usage-monitor)
(Electron) by Shivam Bajpai. MIT License, see [LICENSE](LICENSE).
