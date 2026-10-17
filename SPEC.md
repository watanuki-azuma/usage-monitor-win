# Usage Monitor — native rewrite (SPEC)

Goal: replace the Electron app ("Usage Monitor", 4 BrowserWindows + GPU process, ~250 MB exe) with a small native
Windows app that shows the same usage meters (Claude Code / Codex / Gemini / Grok Build) with **no Chromium**.

## 1. Hard constraints

- Language: **C# 5**, .NET Framework 4.x, compiled with the in-box `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.
  Forbidden syntax: `?.`  `?[`  `$"..."`  `nameof`  expression-bodied members (`=>` on methods/properties)
  auto-property initializers (`{ get; set; } = x`)  `out var`  tuples  `using static`  pattern matching  `is T x`.
  (`async/await`, LINQ, lambdas, `Task` are fine but prefer plain threads/locks.)
- No NuGet, no extra DLLs. Only framework assemblies: `System`, `System.Core`, `System.Windows.Forms`, `System.Drawing`,
  `System.Web.Extensions` (JSON via `JavaScriptSerializer`). One `.exe`.
- JSON gotcha: `JavaScriptSerializer.Deserialize<Dictionary<string,object>>` yields `ArrayList` for arrays, while
  `JavaScriptSerializer.DeserializeObject` yields `object[]`. Numbers arrive as `int`/`long`/`decimal`/`double`.
  Always go through `DeserializeObject` and handle `IList`, `IDictionary<string,object>` and every numeric type.
- All time values are UTC internally (`DateTimeKind.Utc`), serialized as ISO-8601 with milliseconds and `Z`
  (`2030-01-01T00:00:00.000Z`).
- Never make a real network call or read real credential files from tests or from code you run while developing.
  Tests inject `IHttp` / `ICredentialReader` fakes and point `Paths` at temp directories.
- Do not edit `src/Contracts.cs` or anything under `tests/`, `fixtures/`, `reference/`. Do not add files outside `src/`
  (plus `eval/notes-<task>.md` if you want to leave notes).

## 2. Architecture (why it is lighter)

| Electron app                                   | Native app                                                      |
|------------------------------------------------|-----------------------------------------------------------------|
| 1 main + GPU + utility + 4 renderer processes  | **1 process**, 1 UI thread + 1 poll thread                      |
| HTML/CSS windows (flyout, chips, peek, clickaway) | owner-drawn GDI+ `Form`s: **chips** and **flyout** only        |
| IPC + preload bridge                           | direct calls on the UI thread (`Control.BeginInvoke` from poller) |
| PowerShell spawn to read Credential Manager    | P/Invoke `CredRead` in-process                                  |
| PowerShell/UIA spawn for taskbar rect          | `FindWindow("Shell_TrayWnd")` / `GetWindowRect` in-process      |
| 1 s `setInterval` re-render of the DOM         | repaint only when the displayed text changes                    |

Layers (dependencies point downward only; layers 1–4 must not reference `System.Windows.Forms`):

1. `Models`, `Format`, `SnapshotJson`, `Paths`, `AppConfig`        (pure logic)
2. `RealHttp` + adapters (`ClaudeAdapter`, `CodexAdapter`, `GeminiAdapter`, `GrokAdapter`), `WinCred`
3. `PollEngine`, `AlertEngine`, `FileSnapshotStore`, `FileAlertStore`
4. `Ui\*` (WinForms), `Program`

Data compatibility (must stay byte-compatible in *shape* so `usage.py` and the model-router keep working):
`snapshot.json`, `config.json`, `alert-state.json` use the same keys as the Electron app
(see `reference/js/cache.js`, `reference/js/config.js`, `reference/js/models.js`).
`usage.py` reads `providers[].id/status.state/status.reason/plan/fetched_at/windows[].label/kind/used_pct/resets_at`.

## 3. Milestones and files

| M | Files (you create)                                                                 | Port from `reference/js/…`                  |
|---|-------------------------------------------------------------------------------------|---------------------------------------------|
| 1 | `src/Models.cs` `src/Format.cs` `src/SnapshotJson.cs` `src/Paths.cs` `src/AppConfig.cs` | `models.js` `ui/format.js` `cache.js` `paths.js` `config.js` |
| 2 | `src/Http.cs` `src/Adapters/ClaudeAdapter.cs` `src/Adapters/CodexAdapter.cs`        | `http.js` `adapters/claude.js` `adapters/codex.js` |
| 3 | `src/WinCred.cs` `src/Adapters/GeminiAdapter.cs` `src/Adapters/GrokAdapter.cs`      | `wincred.js` `adapters/gemini.js` `adapters/grok.js` |
| 4 | `src/PollEngine.cs` `src/AlertEngine.cs` `src/Stores.cs`                            | `poller.js` `alerts.js` `cache.js`          |
| 5 | `src/Ui/*.cs` `src/Program.cs`                                                      | `main.js` `ui/*` `taskbarLayout.js` `autostart.js` |

## 4. Public API (exact signatures — tests compile against these)

All in `namespace UsageMonitor`. Types in `src/Contracts.cs` already exist.

### M1

```csharp
public static class Models {
    public const int AlertUsedPct = 80;
    public static readonly string[] ProviderIds;                 // { "claude", "codex", "gemini", "grok" }
    public static double? Percentage(object value);              // see §5.1
    public static double? RemainingPct(object used);             // max(0, 100 - Percentage(used)); null if invalid
    public static UsageWindow WindowOf(string kind, string label, object usedPct, string resetsAt);
    public static ProviderSnapshot EmptyProvider(string id, string displayName, ProviderStatus status, DateTime nowUtc);
    public static UsageWindow CurrentWindow(ProviderSnapshot p); // see §5.1
    public static bool IsAlerting(UsageWindow w);                // UsedPct >= 80
    public static Snapshot ApplyLocalResets(Snapshot s, DateTime nowUtc, out bool changed);
}
public static class Format {
    public static string FormatEta(DateTime? resetsAtUtc, DateTime nowUtc, TimeZoneInfo tz);
    public static string FormatUsedTotal(UsageWindow w, bool compact);
    public static string FlyoutLabel(UsageWindow w);
    public static string StatusText(ProviderSnapshot p);         // null when the provider is ok/stale
    public static string StaleText(ProviderSnapshot p, DateTime nowUtc, TimeZoneInfo tz);
}
public static class SnapshotJson {
    public static string Serialize(Snapshot s);                  // indented, same keys as the Electron snapshot.json
    public static Snapshot Parse(string json);                   // null when invalid / no usable providers
}
public class Paths {
    public Paths(Func<string, string> getEnv);                   // getEnv(name) returns null when unset
    public string Home { get; }                                  // USERPROFILE
    public string ClaudeConfigDir { get; }                       // CLAUDE_CONFIG_DIR or Home\.claude
    public string ClaudeCredentials { get; }                     // ClaudeConfigDir\.credentials.json
    public string CodexHome { get; }                             // CODEX_HOME or Home\.codex
    public string CodexAuth { get; }                             // CodexHome\auth.json
    public string GrokHome { get; }                              // GROK_HOME or Home\.grok
    public string GrokAuth { get; }                              // GrokHome\auth.json
    public string GeminiHome { get; }                            // GEMINI_HOME or Home\.gemini
    public string GeminiOAuth { get; }                           // GeminiHome\oauth_creds.json
    public string AntigravityToken { get; }                      // GeminiHome\antigravity-cli\antigravity-oauth-token
    public string[] AgyBinaryCandidates { get; }                 // LOCALAPPDATA\agy\bin\agy.exe, ...\agy
    public string ConfigDir { get; }                             // USAGE_MONITOR_DATA_DIR or APPDATA\UsageMonitor
    public string CacheDir { get; }                              // USAGE_MONITOR_DATA_DIR or LOCALAPPDATA\UsageMonitor\cache
    public string ConfigPath { get; }                            // ConfigDir\config.json
    public string SnapshotPath { get; }                          // CacheDir\snapshot.json
    public string AlertStatePath { get; }                        // CacheDir\alert-state.json
    public string FindOnPath(string name);                       // searches getEnv("PATH"); tries .exe .cmd .bat and bare; null if none
}
public class AppConfig {
    public static readonly int[] AllowedIntervals;               // { 5, 15, 30, 60 }
    public int PollIntervalSecs { get; set; }                    // default 5
    public int ConfigVersion { get; set; }                       // default 5
    public bool ChipsDocked { get; set; }                        // default true
    public bool ChipsHidden { get; set; }                        // default false
    public int? ChipsX { get; set; } public int? ChipsY { get; set; }
    public int? ChipsDockX { get; set; } public int? ChipsDockY { get; set; }
    public bool FlyoutDocked { get; set; }                       // default false
    public bool FlyoutPinned { get; set; }                       // default false
    public int? FlyoutX { get; set; } public int? FlyoutY { get; set; }
    public int? FlyoutDockX { get; set; } public int? FlyoutDockY { get; set; }
    public bool Autostart { get; set; }                          // default true
    public bool CheckUpdatesOnStartup { get; set; }              // default true (kept for file compatibility, unused)
    public bool NotifyOnLimitReached { get; set; }               // default true
    public bool RefreshTokens(string adapterId);                 // claude|gemini|grok; default true; false only if explicitly false
    public void SetRefreshTokens(string adapterId, bool value);
    public static AppConfig Default();
    public static AppConfig Parse(string json);                  // never null; tolerant (§5.2)
    public string ToJson();                                      // indented; same keys as Electron config.json
}
```

### M2

```csharp
public class RealHttp : IHttp { public HttpResult Request(string method, string url, IDictionary<string,string> headers, byte[] body, int timeoutMs); }
public static class HttpHelpers {
    public static HttpResult GetJson(IHttp http, string url, IDictionary<string,string> headers);              // timeout 8000 ms
    public static HttpResult PostJson(IHttp http, string url, IDictionary<string,string> headers, object body); // JSON-serialises body
    public static HttpResult PostForm(IHttp http, string url, IDictionary<string,string> headers, IDictionary<string,string> fields);
}
public class ClaudeAdapter : IUsageAdapter {
    public static ProviderSnapshot MapUsage(object body, object oauth, DateTime nowUtc);
    public static string PlanLabel(object oauth);
}
public class CodexAdapter : IUsageAdapter {
    public static ProviderSnapshot MapWham(object body, DateTime nowUtc);
}
```

### M3

```csharp
public class WinCredReader : ICredentialReader { public string ReadGenericUtf8(string target); }   // P/Invoke advapi32 CredRead/CredFree
public class GeminiAdapter : IUsageAdapter {
    public static double? UsedFromFraction(object remaining);
    public static double? ExpiryEpoch(object value);
    public static List<UsageWindow> MapSummary(object data, DateTime nowUtc);
    public static List<UsageWindow> MapModels(object data, DateTime nowUtc);
}
public class GrokAdapter : IUsageAdapter {
    public static ProviderSnapshot MapBilling(object body, DateTime nowUtc);
    public static double? ParseCreditsGrpcWeb(byte[] frame);      // credit usage percent or null
}
```

### M4

```csharp
public class PollEngine {
    public PollEngine(IList<IUsageAdapter> adapters, ISnapshotStore store, Func<DateTime> utcNow, int adapterTimeoutMs);
    public Snapshot PollOnce(AdapterContext ctx, int pollIntervalSecs);   // blocking; coalesces concurrent calls
    public static ProviderSnapshot MergeWithCache(ProviderSnapshot fresh, ProviderSnapshot cached, DateTime nowUtc);
}
public static class AlertEngine {
    public static List<Toast> Evaluate(Snapshot s, AlertState state, bool notifyOnLimit, DateTime nowUtc);  // mutates state
    public static AlertState ParseState(string json);                  // tolerant, never null
    public static string SerializeState(AlertState state);
}
public class FileSnapshotStore : ISnapshotStore { public FileSnapshotStore(string path); ... }
public class FileAlertStore { public FileAlertStore(string path); public AlertState Load(); public void Save(AlertState s); }
```

### M5 — UI

Pure, unit-tested helpers (no WinForms types; `src/Ui/TaskbarMath.cs`, `src/Ui/ChipModel.cs`):

```csharp
public struct RectI { public int X, Y, W, H; public RectI(int x, int y, int w, int h); }   // value type; fields public
public static class TaskbarMath {
    public static string Edge(RectI screen, RectI workArea);   // "top"|"left"|"right"|"bottom": thickest gap between screen and work area; if the thickest gap is <= 8 px -> "bottom"
    public static RectI DockBeforeTray(RectI taskbar, RectI trayNotify, int w, int h, int gap);
        // horizontal taskbar (taskbar.W >= taskbar.H): X = trayNotify.X - gap - w, Y = taskbar.Y + (taskbar.H - h) / 2
        // vertical taskbar: X = taskbar.X + (taskbar.W - w) / 2, Y = trayNotify.Y - gap - h        (integer division)
    public static RectI Clamp(RectI r, RectI bounds);          // move r inside bounds; if r is larger than bounds on an axis, pin to bounds' origin on that axis
    public static bool IsOverTray(RectI window, RectI? tray);  // centre point of window inside tray (false when tray is null)
    public static RectI FlyoutAnchor(RectI chips, RectI work, int w, int h, int gap);
        // centred horizontally on chips; Y = chips.Y - gap - h (above); if that is < work.Y put it below: chips.Y + chips.H + gap; then Clamp into work
}
public class ChipInfo { public string Id; public string Text; public string ColorClass; public bool Stale; }
public static class ChipModel {
    public static ChipInfo For(ProviderSnapshot p);   // Text = Format.FormatUsedTotal(CurrentWindow, true) ("—" when none); ColorClass "red" (>=80) | "green" | "gray" (no window); Stale = state == "stale"
    public static string Key(Snapshot s);             // equal iff the chips would look identical (ids, text, colour, stale flag, in order)
}
```

Windows code (not unit-tested): `Program.Main` plus `src/Ui/*.cs`.

`Program.Main` plus `src/Ui/*.cs`. Required command-line switches: `--data-dir <dir>` (sets USAGE_MONITOR_DATA_DIR; default data dir = the folder of the exe, like the packaged Electron app),
`--render-test <outdir>` (see §6), `--no-poll` (UI only, uses cached snapshot).

## 5. Behaviour notes

### 5.1 Models
- `Percentage(value)`: accepts `double/float/int/long/decimal/short/byte` and numeric `string`; rejects `null`, blank strings,
  non-numeric strings, `bool`, objects, `NaN`, `±Infinity` (→ `null`). Negative → `0`. Values over 100 are preserved (overage).
- `WindowOf`: `RemainingPct = max(0, 100 - used)`; `ResetsAt` is parsed ISO-8601 (any offset) → UTC, invalid/empty → `null`.
- `CurrentWindow`: among windows with a numeric `UsedPct`, prefer `five_hour`; if none, prefer `daily`; otherwise any.
  Among the preferred group choose the **highest** `UsedPct` (first one wins ties). `null` when no numeric window.
- `ApplyLocalResets`: a window whose `ResetsAt <= nowUtc` becomes `UsedPct=null, RemainingPct=null, ResetsAt=null`
  (kind/label kept), and its provider's status becomes `State = "stale"` (other status fields kept).
  Windows with `null` ResetsAt are untouched. **The input snapshot must not be mutated** (return a deep copy; when nothing
  expired `changed` is false and the result may be the same data). A second call on the result reports `changed == false`.

### 5.1b JSON output format

JavaScriptSerializer cannot indent, so write JSON by hand (a small JsonWriter helper is fine). Output must look like Node's
JSON.stringify(value, null, 2): 2-space indent, `"key": value` (one space after the colon), `null` for unknown,
numbers without exponent, `&` `<` `>` **not** escaped, `/` not escaped, non-ASCII written as-is, files UTF-8 **without BOM**,
and \r\n or \n line endings both fine.

### 5.2 AppConfig.Parse
Tolerant like `config.js normalize`: non-object / malformed JSON → defaults. Booleans of the wrong type (e.g. the string
`"false"`) fall back to the default. Coordinates that are not finite numbers become `null`. `poll_interval_secs` not in
{5,15,30,60} → 5. `adapters.<id>.refresh_tokens` is false only if the value is literally `false`. Each call returns a new
object (no shared mutable defaults). `ConfigVersion` is carried from the file (default 5).
`ToJson()` key order: `poll_interval_secs, config_version, chips_docked, chips_hidden, chips_x, chips_y, chips_dock_x,
chips_dock_y, flyout_docked, flyout_pinned, flyout_x, flyout_y, flyout_dock_x, flyout_dock_y, autostart,
check_updates_on_startup, notify_on_limit_reached, adapters{claude,gemini,grok:{refresh_tokens}}`.

### 5.3 Adapters
Port the decision logic of the JS adapters faithfully (state names, hints, messages, plan labels, window kinds/labels,
refresh rules, 429 handling → `RateLimited = true` + `fetch_failed` "rate limited"). Differences from JS:
- Synchronous (`IHttp.Request` blocks). Time via `ctx.UtcNow()`.
- Adapters have a public parameterless constructor (`new ClaudeAdapter()`); `Id` is `claude|codex|gemini|grok`.
- `MapXxx` helpers take the value produced by `JavaScriptSerializer.DeserializeObject` (`Dictionary<string,object>`,
  `object[]`, `string`, numbers, `null`) or `null`; they must never throw on unexpected shapes.
- Transport exceptions from `IHttp.Request` are caught inside `FetchUsage` and become `fetch_failed` with the exception message.
- Token files are rewritten only if their mtime is unchanged since read (compare-and-swap) using tmp file + rename/replace.
  Keep every other top-level key of the file intact and write it in the §5.1b format (indented JSON, `"key": value`).
- The Claude usage endpoint headers are in `claude.js`; keep them.

### 5.4 Poller / alerts
Port `poller.js` / `alerts.js` semantics: per-provider backoff (2, 4, 8, 16, then 30 min doubling on `RateLimited`),
`claude` minimum gap 5 min, adapter timeout, pending-task retention so a timed-out adapter is never started twice,
cache merge (`fetch_failed`/`stale` + cached windows → `stale` with cached data and original `FetchedAt`),
snapshot saved after each poll (save failure must not lose the fresh data), `ApplyLocalResets` applied to the result.
Alerts: 80% crossing once per window key (`id|kind|label|resetMinute`), 100% "limit reached" once when `notifyOnLimit`,
only for providers whose state is `ok`; re-arm when usage drops below 80; prune entries whose `Until` has passed.
Toast text: title `Usage Monitor — <DisplayName>`, body `<label> <rounded>/100% used` or `<label> limit reached`.

### 5.5 UI (M5)
Two owner-drawn borderless top-most tool windows (no taskbar button, `WS_EX_NOACTIVATE` for chips) + `NotifyIcon`.
- **Chips bar**: one rounded pill per provider: brand mark (use the SVG path data in `reference/js/ui/format.js` `MARKS`,
  convert to `GraphicsPath` once at startup — or draw a simple glyph if conversion is too costly) + `NN/100`.
  Colours: green when `< 80`, red when `>= 80`, gray when unknown; dashed border when stale. Background `#111827` ~94% opaque.
  Docked mode: sits immediately left of `TrayNotifyWnd` inside the taskbar rectangle (use `FindWindow("Shell_TrayWnd")`,
  `FindWindowEx(..., "TrayNotifyWnd")`, `GetWindowRect`, per-monitor DPI aware). Dragging undocks; dropping on the tray
  area re-docks. Click toggles flyout, right-click opens the tray menu.
- **Flyout**: 2-column grid of provider cards (name, plan, per-window row: label, meter bar, `NN/100%`, ETA like `1h 05m`),
  refresh button, interval selector, dock/pin buttons; closes on `Esc` or when it loses focus unless pinned/docked.
- **Tray menu**: Show chips / Hide chips, Refresh now, Refresh every {5,15,30,60} seconds, Open flyout, Start with Windows
  (HKCU `Run` key, value = exe path), Quit.
- Repaint only when the rendered text/colour changes; a single 1-second `Timer` updates ETAs and applies local resets.
- Single instance via named `Mutex`; second launch shows the flyout of the first (named `EventWaitHandle`).
- Toasts through `NotifyIcon.ShowBalloonTip`.
- Memory budget: < 40 MB private working set idle; no `Application.EnableVisualStyles` heavy controls — custom paint only.

## 6. Build & verification

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Milestone 1   # compiles src\*.cs + tests up to M1, runs them
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Milestone 5   # also builds out\UsageMonitor.exe
```

`build.ps1` prints compiler errors and a final line `RESULT: <passed> passed, <failed> failed`.
A milestone is done when it compiles with zero errors and `failed == 0`.
UI milestone: `UsageMonitor.exe --render-test <dir>` must write `chips.png` and `flyout.png` rendered from
`fixtures/snapshot-sample.json` (via `Control.DrawToBitmap` or a direct `OnPaint` into a `Bitmap`), exit 0 within 10 s,
and must not show any window.
