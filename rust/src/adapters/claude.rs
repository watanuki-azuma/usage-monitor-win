//! Claude Code usage adapter (port of src/Adapters/ClaudeAdapter.cs).
//! OAuth tokens live in ~/.claude/.credentials.json; usage comes from the Anthropic OAuth endpoint.
use std::time::SystemTime;

use chrono::{DateTime, Utc};
use serde_json::{json, Map, Value};

use super::http::{get_json, post_json};
use super::util::{self, f64_value, get, get_path, list_of, map_of, or_null, str_of, str_path};
use crate::core::contracts::{kind, state, AdapterContext, HttpResult, ProviderSnapshot, UsageAdapter};
use crate::core::json_out::indented_json;
use crate::core::json_util;
use crate::core::models::window_of;
use crate::core::paths::Paths;

pub const ADAPTER_ID: &str = "claude";
pub const DISPLAY_NAME: &str = "Claude Code";

const CLIENT_ID: &str = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
const USAGE_URL: &str = "https://api.anthropic.com/api/oauth/usage";
const TOKEN_URL: &str = "https://platform.claude.com/v1/oauth/token";
const TOKEN_URL_LEGACY: &str = "https://console.anthropic.com/v1/oauth/token";

/// A token that expires within this window is refreshed before it is used.
const REFRESH_SKEW_MS: f64 = 60000.0;

#[derive(Clone, Copy, PartialEq, Eq)]
enum Outcome {
    NotRefreshed,
    Refreshed,
    Rejected,
}

/// The parsed credential file: the whole document, its `claudeAiOauth` object and the mtime
/// captured before the read (the value the compare-and-swap compares against).
struct Creds {
    root: Map<String, Value>,
    oauth: Option<Value>,
    mtime: Option<SystemTime>,
}

#[derive(Default)]
pub struct ClaudeAdapter;

impl ClaudeAdapter {
    pub fn new() -> ClaudeAdapter {
        ClaudeAdapter
    }
}

fn cli_on_path(paths: &Paths) -> bool {
    paths.find_on_path("claude").is_some()
}

fn creds_present(paths: &Paths) -> bool {
    util::file_exists(&paths.claude_credentials())
}

fn empty(state: &str, message: Option<&str>, hint: Option<&str>, now: DateTime<Utc>) -> ProviderSnapshot {
    util::empty(ADAPTER_ID, DISPLAY_NAME, state, message, hint, now)
}

/// Read + parse the credential file, remembering its mtime for the compare-and-swap write.
fn read_creds_file(path: &str) -> Option<Creds> {
    if !util::file_exists(path) {
        return None;
    }
    // statSync comes first in claude.js: the mtime captured here is the value the compare-and-swap
    // compares against, so it must be read before the contents.
    let mtime = util::mtime(path);
    let text = util::read_text(path).ok()?;
    let parsed = json_util::parse(Some(&text))?;
    let root = match parsed {
        Value::Object(m) => m,
        _ => return None,
    };
    let oauth = root.get("claudeAiOauth").filter(|v| v.is_object()).cloned();
    Some(Creds { root, oauth, mtime })
}

pub fn plan_label(oauth: Option<&Value>) -> Option<String> {
    let oauth = oauth.filter(|v| v.is_object())?;
    if let Some(tier) = str_of(Some(oauth), "rateLimitTier") {
        if !tier.is_empty() {
            if tier == "default_claude_max_20x" {
                return Some("Max 20x".to_string());
            }
            if tier == "default_claude_max_5x" {
                return Some("Max 5x".to_string());
            }
            if tier.contains("max_20") {
                return Some("Max 20x".to_string());
            }
            if tier.contains("max_5") {
                return Some("Max 5x".to_string());
            }
        }
    }
    let subscription = str_of(Some(oauth), "subscriptionType")?;
    match subscription.as_str() {
        "max" => Some("Max".to_string()),
        "pro" => Some("Pro".to_string()),
        "enterprise" => Some("Enterprise".to_string()),
        "" => None,
        _ => Some(subscription),
    }
}

fn usage_headers(access_token: &str) -> Vec<(String, String)> {
    vec![
        ("Authorization".to_string(), format!("Bearer {}", access_token)),
        ("anthropic-beta".to_string(), "oauth-2025-04-20".to_string()),
        ("anthropic-version".to_string(), "2023-06-01".to_string()),
        ("x-app".to_string(), "cli".to_string()),
        ("User-Agent".to_string(), "claude-cli/2.1.201 (external, cli)".to_string()),
        ("anthropic-dangerous-direct-browser-access".to_string(), "true".to_string()),
    ]
}

fn as_refs(headers: &[(String, String)]) -> Vec<(&str, &str)> {
    headers.iter().map(|(k, v)| (k.as_str(), v.as_str())).collect()
}

fn fetch_usage_response(ctx: &AdapterContext, access_token: &str) -> Result<HttpResult, crate::core::contracts::HttpError> {
    let headers = usage_headers(access_token);
    get_json(&*ctx.http, USAGE_URL, &as_refs(&headers))
}

impl UsageAdapter for ClaudeAdapter {
    fn id(&self) -> &str {
        ADAPTER_ID
    }

    fn display_name(&self) -> &str {
        DISPLAY_NAME
    }

    fn fetch_usage(&self, ctx: &AdapterContext) -> ProviderSnapshot {
        let now = (ctx.utc_now)();
        let paths = &ctx.paths;

        if !creds_present(paths) && !cli_on_path(paths) {
            return empty(state::NOT_INSTALLED, None, Some("Install Claude Code, then run: claude auth login"), now);
        }
        if !creds_present(paths) {
            return empty(state::LOGGED_OUT, None, Some("Run: claude auth login"), now);
        }

        let cred_path = paths.claude_credentials();
        let mut creds = match read_creds_file(&cred_path) {
            Some(c) => c,
            None => return empty(state::LOGGED_OUT, None, Some("Run: claude auth login"), now),
        };

        let mut access_token = str_of(creds.oauth.as_ref(), "accessToken");
        let mut refresh_token = str_of(creds.oauth.as_ref(), "refreshToken");

        let access = match access_token.as_deref() {
            Some(a) if !a.is_empty() => a.to_string(),
            _ => {
                return empty(
                    state::UNKNOWN,
                    Some("Claude is not signed in with a subscription (no OAuth token)"),
                    None,
                    now,
                )
            }
        };
        let mut access = access;

        let expires_at = util::num(get(creds.oauth.as_ref(), "expiresAt"));
        let refresh_enabled = ctx.config.refresh_tokens(ADAPTER_ID);
        let has_refresh = matches!(refresh_token.as_deref(), Some(r) if !r.is_empty());

        // A token expiring within the skew window is rotated before it is used at all.
        let mut refreshed = false;
        if refresh_enabled && has_refresh {
            if let Some(exp) = expires_at {
                if exp != 0.0 {
                    let remaining = exp - util::to_epoch_ms(now);
                    if remaining < REFRESH_SKEW_MS {
                        // A transport failure here must not cost us the usage request: we keep
                        // going with the token we already have.
                        let outcome = refresh_now(ctx, &creds, now).unwrap_or(Outcome::NotRefreshed);
                        refreshed = outcome == Outcome::Refreshed;
                        // A rejected grant means the login is gone; do not even call the usage endpoint.
                        if outcome == Outcome::Rejected {
                            return empty(state::LOGGED_OUT, None, Some("Run: claude auth login"), now);
                        }
                        if refreshed {
                            reload(&cred_path, &mut creds, &mut access_token, &mut refresh_token);
                            access = access_token.clone().unwrap_or_default();
                        }
                    }
                }
            }
        }

        let mut res = match fetch_usage_response(ctx, &access) {
            Ok(r) => r,
            Err(e) => return util::fetch_failed(ADAPTER_ID, DISPLAY_NAME, &e, now),
        };

        // 401/403: rotate the token once, then retry with the new one.
        if (res.status == 401 || res.status == 403) && refresh_enabled && !refreshed && has_refresh {
            if let Ok(Outcome::Refreshed) = refresh_now(ctx, &creds, now) {
                reload(&cred_path, &mut creds, &mut access_token, &mut refresh_token);
                let fresh = access_token.clone().unwrap_or_default();
                if !fresh.is_empty() {
                    match fetch_usage_response(ctx, &fresh) {
                        Ok(r) => res = r,
                        Err(e) => return util::fetch_failed(ADAPTER_ID, DISPLAY_NAME, &e, now),
                    }
                }
            }
        }

        from_usage_response(&res, creds.oauth.as_ref(), now)
    }
}

/// After a successful rotation the file is read again, so a CLI-written token file wins over our
/// copy (claude.js's `creds = readCreds()`).
fn reload(path: &str, creds: &mut Creds, access: &mut Option<String>, refresh: &mut Option<String>) {
    if let Some(fresh) = read_creds_file(path) {
        if fresh.oauth.is_some() {
            *access = str_of(fresh.oauth.as_ref(), "accessToken");
            *refresh = str_of(fresh.oauth.as_ref(), "refreshToken");
            *creds = fresh;
        }
    }
}

/// POSTs the refresh grant and, on success, rewrites the token file (compare-and-swap, so a file
/// the CLI changed underneath us is never clobbered).
fn refresh_now(ctx: &AdapterContext, creds: &Creds, now: DateTime<Utc>) -> Result<Outcome, crate::core::contracts::HttpError> {
    let body = json!({
        "grant_type": "refresh_token",
        "refresh_token": str_of(creds.oauth.as_ref(), "refreshToken"),
        "client_id": CLIENT_ID,
    });
    let headers = [("Content-Type", "application/json"), ("User-Agent", "usage-monitor/1.0")];

    let mut res = post_json(&*ctx.http, TOKEN_URL, &headers, &body)?;
    if res.status == 404 || res.status == 405 {
        res = post_json(&*ctx.http, TOKEN_URL_LEGACY, &headers, &body)?;
    }
    // A rejected grant: the token is gone, not merely invalid.
    if res.status == 400 || res.status == 401 {
        return Ok(Outcome::Rejected);
    }
    if res.status != 200 {
        return Ok(Outcome::NotRefreshed);
    }

    let tokens = match res.json.as_ref().filter(|v| v.is_object()) {
        Some(t) => t,
        None => return Ok(Outcome::NotRefreshed),
    };
    let granted = match get(Some(tokens), "access_token") {
        Some(g) => g,
        None => return Ok(Outcome::NotRefreshed),
    };

    let token = granted.as_str().filter(|s| !s.is_empty()).map(|s| s.to_string());
    let refresh = str_of(Some(tokens), "refresh_token").filter(|s| !s.is_empty());
    let expires_in = util::num(get(Some(tokens), "expires_in"));

    let cred_path = ctx.paths.claude_credentials();

    // Compare-and-swap: only rewrite when the file has not changed since it was read. This
    // check-then-replace race with the CLI itself cannot be closed without cooperation from the
    // CLI, so at least the original file is never destroyed first.
    let outcome = (|| -> std::io::Result<()> {
        if !util::file_exists(&cred_path) {
            return Ok(());
        }
        if util::mtime(&cred_path) != creds.mtime {
            return Ok(());
        }

        let mut next_oauth = util::copy_map(creds.oauth.as_ref());
        if let Some(t) = &token {
            next_oauth.insert("accessToken".to_string(), Value::String(t.clone()));
        }
        if let Some(r) = &refresh {
            next_oauth.insert("refreshToken".to_string(), Value::String(r.clone()));
        }
        if let Some(e) = expires_in {
            next_oauth.insert("expiresAt".to_string(), f64_value(Some(util::to_epoch_ms(now) + e * 1000.0)));
        }

        let mut root = creds.root.clone();
        root.insert("claudeAiOauth".to_string(), Value::Object(next_oauth));

        // One atomic replace: the destination is never deleted first, so a crash in between
        // cannot leave the user's credentials missing, and a failing replace keeps the original.
        util::write_replace(&cred_path, &indented_json(&Value::Object(root)))
    })();
    // Only remove the scratch copy while the credentials file is still there: a replace that
    // failed after unlinking the destination leaves the .tmp file as the only copy.
    if outcome.is_err() && util::file_exists(&cred_path) {
        util::delete_quietly(&format!("{}.tmp", cred_path));
    }
    Ok(Outcome::Refreshed)
}

fn from_usage_response(res: &HttpResult, oauth: Option<&Value>, now: DateTime<Utc>) -> ProviderSnapshot {
    let status = res.status;

    if status == 401 || status == 403 {
        return empty(state::LOGGED_OUT, None, Some("Run: claude auth login"), now);
    }
    if status == 429 {
        return util::rate_limited(ADAPTER_ID, DISPLAY_NAME, now);
    }
    match (&res.json, status) {
        (Some(json), 200) => map_usage(json, oauth, now),
        _ => empty(state::FETCH_FAILED, Some(&format!("HTTP {}", status)), None, now),
    }
}

// ----------------------------------------------------------------------------------------------
// mapping (body -> snapshot). Must never panic on an unexpected shape.
// ----------------------------------------------------------------------------------------------

pub fn map_usage(body: &Value, oauth: Option<&Value>, now: DateTime<Utc>) -> ProviderSnapshot {
    let mut windows = Vec::new();
    let map = Some(body).filter(|v| v.is_object());

    if let Some(five_hour) = map_of(map, "five_hour") {
        if keeps_window(five_hour) {
            windows.push(window_of(
                kind::FIVE_HOUR,
                "5h",
                or_null(get(Some(five_hour), "utilization")),
                str_of(Some(five_hour), "resets_at").as_deref(),
            ));
        }
    }

    if let Some(seven_day) = map_of(map, "seven_day") {
        if keeps_window(seven_day) {
            windows.push(window_of(
                kind::WEEKLY,
                "Weekly",
                or_null(get(Some(seven_day), "utilization")),
                str_of(Some(seven_day), "resets_at").as_deref(),
            ));
        }
    }

    if map.is_some() {
        if let Some(limits) = list_of(map, "limits") {
            for item in limits {
                if !scope_weekly_is_wanted(item) {
                    continue;
                }
                let scope = map_of(Some(item), "scope");
                let name = match str_path(scope, "model", "display_name") {
                    Some(n) if !n.is_empty() => n,
                    _ => continue,
                };
                let used = or_null(get(Some(item), "percent"));
                let resets_at = str_of(Some(item), "resets_at");
                windows.push(window_of(kind::WEEKLY_SCOPED, &format!("{} weekly", name), used, resets_at.as_deref()));
            }
        }

        if let Some(spend) = map_of(map, "spend") {
            if spend_is_wanted(spend) {
                let used = util::num(get_path(Some(spend), "used", "amount_minor"));
                let limit = util::num(get_path(Some(spend), "limit", "amount_minor"));
                windows.push(window_of(kind::CREDITS, "Credits", &f64_value(spend_pct(used, limit)), None));
            }
        }
    }

    let mut p = util::new_ok(ADAPTER_ID, DISPLAY_NAME, now);
    p.plan = plan_label(oauth);
    p.windows = windows;
    p
}

fn keeps_window(window: &Value) -> bool {
    get(Some(window), "utilization").is_some()
        || matches!(str_of(Some(window), "resets_at"), Some(s) if !s.is_empty())
}

fn scope_weekly_is_wanted(limit: &Value) -> bool {
    if !limit.is_object() {
        return false;
    }
    if str_of(Some(limit), "kind").as_deref() != Some(kind::WEEKLY_SCOPED) {
        return false;
    }
    let scope = match map_of(Some(limit), "scope") {
        Some(s) => s,
        None => return false,
    };
    if map_of(Some(scope), "model").is_none() {
        return false;
    }
    matches!(str_path(Some(scope), "model", "display_name"), Some(n) if !n.is_empty())
}

fn spend_is_wanted(spend: &Value) -> bool {
    if json_util::bool_of(get(Some(spend), "enabled")) != Some(true) {
        return false;
    }
    map_of(Some(spend), "limit").is_some() && map_of(Some(spend), "used").is_some()
}

fn spend_pct(used: Option<f64>, limit: Option<f64>) -> Option<f64> {
    let (used, limit) = (used?, limit?);
    if limit == 0.0 {
        return None; // a falsy denominator is "unknown" in JS, not 0
    }
    Some(used / limit * 100.0)
}
