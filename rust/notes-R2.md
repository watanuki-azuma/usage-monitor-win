# R2 (adapters) — results and open points

## Result

- `powershell -NoProfile -ExecutionPolicy Bypass -File rust\build.ps1` ends with
  `RESULT: 93 passed, 0 failed` (27 core + 66 adapters) and
  `APP: ...\rust\target\release\UsageMonitor.exe (8,157,696 bytes)` (still the placeholder window).
- All 66 tests of `tests/M2_Adapters.cs`, `M2_Review.cs`, `M3_Agy.cs`, `M3_GeminiGrok.cs`,
  `M3_Review.cs` are ported to `src/adapters/tests.rs` (same names in snake_case, modules named after the
  C# test classes). Fakes: `src/adapters/test_support.rs` (`FakeHttp`, `FakeCred`, `AdapterEnv`, `TestServer`).
- No dependency added, `Cargo.lock` unchanged.

## Files

- `adapters/http.rs` `RealHttp` (ureq 3, native-tls/SChannel, `RootCerts::PlatformVerifier`) + `get_json` /
  `post_json` / `post_form`; `util.rs`; `claude.rs`; `codex.rs`; `gemini.rs`; `grok.rs`; `wincred.rs`.
- `core/json_out.rs`: added `indented_json(&Value)` (generic pretty writer, 5.1b style) and
  `JsonWriter::unsigned`.

## Design notes

- `RealHttp`: a fresh `ureq::Agent` per request (no pooling, like `KeepAlive = false`); `timeout_global` =
  whole exchange; redirects off (`max_redirects(0)`, 3xx returned as a status); `http_status_as_error(false)`;
  body cap 2 MiB (read with limit 2 MiB + 1, error when exceeded); gzip responses are decoded by ureq.
  Unlike .NET it sends `Accept-Encoding: gzip` (ureq `gzip` feature, requested in Cargo.toml).
- The `RealHttp` tests (8 of the 66) run against a loopback `127.0.0.1` server written with
  `std::net::TcpListener` (the C# used `HttpListener`). No traffic leaves the machine.
- Regexes of the C# code (`FiveHourPattern`, `ModelFlashPattern`, `VersionFamilyPattern`, the group-name
  separator replace) are matched by hand: no `regex` dependency.
- `GeminiAdapter` keeps its per-instance cache (refreshed credentials, extracted OAuth client, 10-minute
  negative cache) behind a `Mutex` (trait is `Send + Sync`); the lock is never held across I/O.
- Token-file CAS rewrite: mtime compared as `SystemTime`, `<file>.tmp` + `std::fs::rename` (atomic replace
  on Windows), tmp removed on any failure; Claude/Grok keep other keys and key order (`serde_json`
  `preserve_order`).
- Env overrides (`AGY_CLIENT_ID`, `ANTIGRAVITY_*` ...) are read from the real process environment, like C#.

## Open points

- `std::fs::rename` over a file another process holds open without `FILE_SHARE_DELETE` fails and leaves the
  original untouched (same outcome as `File.Replace`), but the C# test for that case never actually forces a
  refresh while the file is held (its own comment says so); the Rust port has the same weakness.
- ureq reads proxy settings from the environment by default (`HTTP_PROXY` etc.); SPEC §3 says "system default
  is enough". If a system (WinHTTP/IE) proxy is needed, that is a follow-up.
- `HttpError` messages are ureq's texts, not .NET `WebException` texts; they only appear in
  `status.message` of a `fetch_failed` provider.
- `F2` formatting of the Grok prepaid balance uses Rust `{:.2}` (exact decimal rounding), .NET Framework
  rounds half away from zero on the 15-digit form; only differs for values like `x.xx5`.
- Fake HTTP `HttpResult` of tests carries no `json`; the helpers (`get_json` ...) parse the text, like C#.
- `wincred::WinCredReader` is compiled but not exercised by tests (as in C#).

## Review (Codex Sol xhigh, 2026-10-18) — all 4 confirmed in the code and fixed (see below)

- **Credential replacements lose Windows permissions** — [util.rs:232](E:/claude-projects/usage-monitor-app/native/.claude/worktrees/agent-a8f61b11d8b54e266/rust/src/adapters/util.rs:232), also `grok.rs:741`. If a credential file has a restrictive explicit ACL but its directory grants broader access, refreshing replaces it with the temporary file’s broader permissions. C# uses `File.Replace` (`ClaudeAdapter.cs:260`, `GrokAdapter.cs:728`), which preserves the destination’s DACL; Rust uses `rename`. Fix both replacement paths to preserve the original security metadata, using [ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).

- **Windows proxy settings are ignored** — [http.rs:76](E:/claude-projects/usage-monitor-app/native/.claude/worktrees/agent-a8f61b11d8b54e266/rust/src/adapters/http.rs:76). With an Internet Options proxy configured and no proxy environment variables, C# requests use that proxy; Rust connects directly because ureq’s `win-system-proxy` feature is disabled. On a network requiring the proxy, every provider fails. Fix by configuring ureq from Windows proxy settings; enabling its existing feature covers a simple static proxy. [C# proxy behavior](https://learn.microsoft.com/en-us/dotnet/api/system.net.httpwebrequest.proxy?view=netframework-4.8.1).

- **Previously readable credential files become logged out** — [util.rs:208](E:/claude-projects/usage-monitor-app/native/.claude/worktrees/agent-a8f61b11d8b54e266/rust/src/adapters/util.rs:208). A valid Codex `auth.json` saved as UTF-16LE with a BOM works in C# (`CodexAdapter.cs:224`), but Rust decodes it as UTF-8, fails JSON parsing, and returns `logged_out`. C#’s `File.ReadAllText` [detects UTF-16 and UTF-32 BOMs](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.readalltext?view=netframework-4.8.1). Fix `read_text` to recognize those encodings.

- **Prepaid-credit labels round differently** — [grok.rs:85](E:/claude-projects/usage-monitor-app/native/.claude/worktrees/agent-a8f61b11d8b54e266/rust/src/adapters/grok.rs:85). For `{"prepaidBalance":{"val":1.125}}`, C# (`GrokAdapter.cs:84`) displays `Credits $1.13`; Rust displays `Credits $1.12` because fixed-precision formatting uses [half-to-even rounding](https://doc.rust-lang.org/std/fmt/). Fix by matching .NET Framework’s numeric formatting before constructing the label.

### Fixes (parent, 2026-10-18)

- ACL: `util::replace_file` = `ReplaceFileW` (keeps the destination DACL/owner/attributes, like
  `File.Replace`); used by Claude (`write_replace`) and Grok. Claude now removes the `.tmp` only while
  the credentials file still exists (same guard as Grok; the C# Claude code deletes it unconditionally).
- Proxy: ureq `win-system-proxy` (adds pure-Rust `winreg`): env vars first, then the Internet Options
  static proxy. Loopback hosts always go direct. Not covered: PAC/auto-detect, per-protocol
  `ProxyServer` strings (`http=..;https=..`), `ProxyOverride` bypass list.
- BOM: `util::decode_text` detects UTF-32 LE/BE, UTF-16 LE/BE and UTF-8 BOMs (.NET order).
- F2: `util::format_f2` (15 significant digits, then half away from zero), used for the Grok label.
- 3 tests added (`r2_review_tests`); `build.ps1 -SkipRelease`: 96 passed, 0 failed.
