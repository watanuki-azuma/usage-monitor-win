# R5 report (2026-10-18)

Status: **done**. The `rpg`, `fluent`, and `terminal` skins are implemented in Slint alongside `classic`, live skin switching is wired from the tray menu "Design", `--render-test --skin S` renders each skin `S` to matching `chips.png` and `flyout.png`, and all 140 tests pass.

## Files changed

- `rust/ui/common.slint`: extended shared `ChipData`, `LineData`, `CardData`, added `HeaderButtonData`, and added `in property <string> skin: "classic"` on `WindowBase`.
- `rust/ui/classic/app.slint`: kept classic `ClassicChips` and `ClassicFlyout` untouched and routed the single `Chips` and `Flyout` windows by `root.skin` (`"classic"`, `"rpg"`, `"fluent"`, `"terminal"`).
- `rust/ui/rpg/app.slint`: added `RpgChips` and `RpgFlyout` (FF7-style double bevel borders, deep blue vertical gradients, crossed-swords header crest, pointing hand cursor, gold-bordered initial badges, `HP`/`MP`/`SP` gauges with specular highlight, `SLOW`/`STALE` status pill, `KO`/`REVIVE` ghost gauge row, and diamond/triangle glyphs).
- `rust/ui/fluent/app.slint`: added `FluentChips` and `FluentFlyout` (Windows 11 Mica/Acrylic dark surfaces, header chart glyph, brand-tinted top card glow, gradient 3D badges, rounded pill meters with top highlight, status/credit/command pills).
- `rust/ui/terminal/app.slint`: added `TerminalChips` and `TerminalFlyout` (CRT phosphor-green frame with scanlines, `usage@monitor:~$ watch -n 5 █ [pts/0]` prompt header, `[ 4 MONITORS ]` / `[ SYS HEALTH: ALERT ]` divider, box-drawn pane corners/ticks, `[###...]` ASCII bars, and `Consolas` monospace typography).
- `rust/src/ui/chip_model.rs`: added `rpg_for_provider` (computes remaining `NN/100` while preserving `color_class` and `stale` from used%).
- `rust/src/ui/skins.rs`: updated `component_skin` to return the normalized skin ID (`"classic"`, `"rpg"`, `"fluent"`, `"terminal"`) and added `chips_radius` / `flyout_radius` per skin.
- `rust/src/ui/render_test.rs`: added per-skin model builders (`update_classic`, `update_rpg`, `update_fluent`, `update_terminal`) and dispatched `update` and `run` by `skins::component_skin`.
- `rust/src/ui/app_controller.rs`: bound `chips.set_skin` and `flyout.set_skin` in `render()`, updated window rounding radii per skin, and made `skin:<id>` live-switch the active Slint skin, re-place/round windows while preserving visibility and docking/position state, and persist `config.json`.
- `rust/src/ui/tests.rs`: updated `test_registry_creates_every_skin_and_unknown_is_classic`, added `test_rpg_chips_show_remaining_while_colour_follows_used`, and updated `test_each_skin_measures_and_paints_chips_and_flyout` to set and verify each skin at 100%, 150%, and 200% scales.
- `rust/NOTES.md`, `rust/notes-R5.md`: added R5 notes and report.

## Commands run and results

- `powershell -NoProfile -ExecutionPolicy Bypass -File rust/build.ps1`
  - `RESULT: 140 passed, 0 failed`
  - `APP: E:\claude-projects\usage-monitor-app\native\.claude\worktrees\r5\rust\target\release\UsageMonitor.exe (9,956,864 bytes)`
- `rust\target\release\UsageMonitor.exe --render-test rust\target\r5\classic --skin classic --data-dir rust\reference-renders\data`
  - Exit code: `0`
- `rust\target\release\UsageMonitor.exe --render-test rust\target\r5\rpg --skin rpg --data-dir rust\reference-renders\data`
  - Exit code: `0`
- `rust\target\release\UsageMonitor.exe --render-test rust\target\r5\fluent --skin fluent --data-dir rust\reference-renders\data`
  - Exit code: `0`
- `rust\target\release\UsageMonitor.exe --render-test rust\target\r5\terminal --skin terminal --data-dir rust\reference-renders\data`
  - Exit code: `0`

## Per-skin render comparison vs. C# reference renders (`rust/reference-renders/<skin>/`)

1. **`classic`** (exit code `0`; `chips.png` 294×30, `flyout.png` 640×262):
   - Unchanged from R4. Matches C# reference layout, dimensions, colours, and texts; minor differences remain limited to Slint software font rasterization vs. GDI ClearType, corner antialiasing, and stale dash phase.
2. **`rpg`** (exit code `0`; `chips.png` 320×30, `flyout.png` 710×274):
   - Matches C# reference dimensions (320×30 and 710×274), FF7 double-bevel frames, deep blue vertical gradients, crossed-swords header crest, pointing hand cursor on the first card, `LV. ...` / `KO` badges, `HP`/`MP`/`SP` remaining-percentage gauges (`2/100`, `84/100`, `59/100`, `65/100`, `100/100`, `0/100 REVIVE`), `SLOW` pill, and `ITEM: Reset credits ×1`.
   - Visible differences vs. C# render: Slint software font hinting/antialiasing vs. GDI ClearType, minor stroke antialiasing on the vector crossed-swords/hand cursor paths, and sampled dash phase on the stale chip border.
3. **`fluent`** (exit code `0`; `chips.png` 314×30, `flyout.png` 680×334):
   - Matches C# reference dimensions (314×30 and 680×334), dark Mica/Acrylic card surfaces, brand-tinted top card glow gradients, header bar-chart badge, `Segoe UI Semibold` typography, gradient 3D brand badges, rounded pill progress bars with top specular highlight, amber stale banner pill, credit pill (`◆ Reset credits ×1`), and command pill (`> Run: grok login` + `CLI`).
   - Visible differences vs. C# render: Slint software font rasterization vs. GDI ClearType, top card glow approximated as a smooth vertical gradient band rather than a 2D elliptical radial brush (Slint software renderer supports linear gradients), and slight corner antialiasing differences.
4. **`terminal`** (exit code `0`; `chips.png` 310×30, `flyout.png` 700×286):
   - Matches C# reference dimensions (310×30 and 700×286), CRT phosphor frame with scanline overlay, `usage@monitor:~$ watch -n 5 █ [pts/0]` prompt and bracketed header buttons (`[PIN]`, `[DOCK]`, `[5s]`, `[REFRESH]`), `[ 4 MONITORS ]` / `[ SYS HEALTH: ALERT ]` divider, box-drawn pane borders with left accent bar, `[##########.]` (12-slot) and `[######.............]` (19-slot) ASCII bars, `[!] rate limited · retry in Sun 9:30 PM`, `> Reset credits ×1`, and `$ Run: grok login`.
   - Visible differences vs. C# render: Slint software `Consolas` glyph rasterization vs. GDI ClearType and subtle alpha blending differences on the 1px scanline overlay.

## Tests added / updated

- Added `ui::tests::test_rpg_chips_show_remaining_while_colour_follows_used` (ported from `tests/M5_Skins.cs`: verifies `10%` used → `"90/100"` `"green"`, `85%` used → `"15/100"` `"red"`, logged-out missing window → `"—"` `"gray"`).
- Updated `ui::tests::test_registry_creates_every_skin_and_unknown_is_classic` to verify that `skins::component_skin` maps all four `SKIN_IDS` (`classic`, `rpg`, `fluent`, `terminal`) to their respective skin and normalizes unknown/None to `"classic"`.
- Updated `ui::tests::test_each_skin_measures_and_paints_chips_and_flyout` to set `c.set_skin` / `f.set_skin` for each skin in `SKIN_IDS` and verify headless measurement and software rendering at 1.0×, 1.5×, and 2.0× scale factors.

## Open points

- Interactive desktop checks (live tray menu clicking, vertical taskbar docking, focus/Esc dismissal, Explorer restart, and idle memory/CPU measurement across skins) remain for the R6 manual/integration checklist, as the app was only run with `--render-test`.

## Review (Sonnet route-review, 2026-10-18) and fixes (parent)

- Skin switch called `place()` unconditionally, which moved an unpinned, undocked flyout back to its
  anchor: removed; `render()` already follows size changes (and re-docks docked windows).
- RPG/Fluent/Terminal replaced the header-buttons model on every render (every second while the flyout
  is shown): now set only when the buttons changed.
- `build.ps1`: RESULT 140 passed, 0 failed; renders unchanged.
