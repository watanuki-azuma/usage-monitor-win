# Slint feasibility spike (reference only)

Verified on 2026-10-18 (Windows 10, vertical taskbar on the left):
chips/flyout as tool windows (no taskbar button, no activation, top-most), docking above the tray,
tray icon on a message-only window with a native menu, idle CPU 0 ms / 35 s, working set 16 MB,
release exe 7.9 MB (Slint 1.18.1, winit 0.30, software renderer, x86_64-pc-windows-gnu).

Build needs the toolchain workarounds in `../../SLINT-SPEC.md` §2 (`-C dlltool=...` pointing at a copy of
llvm-ar named `llvm-dlltool.exe`, and `mkimplib.ps1 -Names shlwapi` for the missing import library).
