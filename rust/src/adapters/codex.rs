//! Codex (ChatGPT usage) adapter (port of src/Adapters/CodexAdapter.cs).
//! OAuth tokens live in ~/.codex/auth.json; usage comes from the wham/usage endpoint.
use chrono::{DateTime, Utc};
use serde_json::Value;

use super::http::get_json;
use super::util::{self, get, has_text, list_of, map_of, num, str_of};
use crate::core::contracts::{kind, state, AdapterContext, ProviderSnapshot, UsageAdapter, UsageWindow};
use crate::core::json_util;
use crate::core::models;

pub const ADAPTER_ID: &str = "codex";
pub const DISPLAY_NAME: &str = "Codex";

const USAGE_URL: &str = "https://chatgpt.com/backend-api/wham/usage";

#[derive(Default)]
pub struct CodexAdapter;

impl CodexAdapter {
    pub fn new() -> CodexAdapter {
        CodexAdapter
    }
}

fn empty(state: &str, message: Option<&str>, hint: Option<&str>, now: DateTime<Utc>) -> ProviderSnapshot {
    util::empty(ADAPTER_ID, DISPLAY_NAME, state, message, hint, now)
}

// classifyWindow() from codex.js: the window length picks the kind, the provider name (when there
// is one) is prefixed to the label, and the reset comes from reset_at (epoch seconds) or
// reset_after_seconds (relative to now).
fn classify_window(
    used_pct: &Value,
    seconds: Option<&Value>,
    reset_at: Option<&Value>,
    reset_after_seconds: Option<&Value>,
    fallback_label: &str,
    now: DateTime<Utc>,
) -> UsageWindow {
    let mut kind_name = kind::WEEKLY_SCOPED;
    let mut label = fallback_label.to_string();

    let window = num(seconds).unwrap_or(f64::NAN);
    if (14400.0..=21600.0).contains(&window) {
        kind_name = kind::FIVE_HOUR;
        label = "5h".to_string();
    } else if (518400.0..=691200.0).contains(&window) {
        kind_name = kind::WEEKLY;
        label = "Weekly".to_string();
    } else if (82800.0..=90000.0).contains(&window) {
        kind_name = kind::DAILY;
        label = "Daily".to_string();
    } else if label.is_empty() {
        let hours = util::round_i32(window / 3600.0);
        if hours >= 24 {
            label = format!("{}d", util::round_i32(hours as f64 / 24.0));
        } else {
            label = format!("{}h", hours);
        }
    }

    // The provider name is only kept for the "additional_rate_limits" entries.
    if !fallback_label.is_empty() && fallback_label != "5h" && fallback_label != "Weekly" {
        let tail = if label == fallback_label { "" } else { label.as_str() };
        label = format!("{} {}", fallback_label, tail).trim().to_string();
    }

    // Number(reset_at) * 1000 when present, Date.now() + Number(reset_after_seconds) * 1000
    // otherwise, and None when neither yields a finite date.
    let reset_at_num = num(reset_at);
    let reset_after = num(reset_after_seconds);
    let resets_at = if let Some(r) = reset_at_num {
        util::from_epoch_ms(r * 1000.0)
    } else if let Some(a) = reset_after {
        // Range-checked: an unusable reset_after_seconds yields "unknown", it must never lose the
        // whole provider.
        util::from_epoch_ms(util::to_epoch_ms(now) + a * 1000.0)
    } else {
        None
    };

    let used = models::percentage(used_pct);
    UsageWindow {
        kind: kind_name.to_string(),
        label,
        used_pct: used,
        remaining_pct: models::remaining_pct(used_pct),
        resets_at,
    }
}

// used_percent wins, remaining_percent is converted, and the window is skipped when neither is
// present (exactly like codex.js).
fn window_used(window: &Value) -> Option<Value> {
    if let Some(used) = get(Some(window), "used_percent") {
        return Some(used.clone());
    }
    let remaining = num(get(Some(window), "remaining_percent"))?;
    Some(util::f64_value(Some(100.0 - remaining)))
}

pub fn map_wham(body: &Value, now: DateTime<Utc>) -> ProviderSnapshot {
    let mut windows = Vec::new();
    let map = Some(body).filter(|v| v.is_object());

    if map.is_some() {
        let rate_limit = map_of(map, "rate_limit");

        if let Some(primary) = map_of(rate_limit, "primary_window") {
            if let Some(used) = window_used(primary) {
                windows.push(classify_window(
                    &used,
                    get(Some(primary), "limit_window_seconds"),
                    get(Some(primary), "reset_at"),
                    get(Some(primary), "reset_after_seconds"),
                    "5h",
                    now,
                ));
            }
        }

        if let Some(secondary) = map_of(rate_limit, "secondary_window") {
            if let Some(used) = window_used(secondary) {
                windows.push(classify_window(
                    &used,
                    get(Some(secondary), "limit_window_seconds"),
                    get(Some(secondary), "reset_at"),
                    get(Some(secondary), "reset_after_seconds"),
                    "Weekly",
                    now,
                ));
            }
        }

        if let Some(extra) = list_of(map, "additional_rate_limits") {
            for item in extra {
                if !item.is_object() {
                    continue;
                }
                let window = match map_of(Some(item), "primary_window") {
                    Some(w) => w,
                    None => continue,
                };
                let used = match get(Some(window), "used_percent") {
                    Some(u) => u,
                    None => continue,
                };
                let mut name = str_of(Some(item), "name").unwrap_or_default();
                if name.is_empty() {
                    name = str_of(Some(item), "id").unwrap_or_default();
                }
                if name.is_empty() {
                    name = "extra".to_string();
                }
                windows.push(classify_window(
                    used,
                    get(Some(window), "limit_window_seconds"),
                    get(Some(window), "reset_at"),
                    get(Some(window), "reset_after_seconds"),
                    &name,
                    now,
                ));
            }
        }

        if let Some(credits) = map_of(map, "rate_limit_reset_credits") {
            let available = num(get(Some(credits), "available_count"));
            if matches!(available, Some(a) if a > 0.0) {
                windows.push(UsageWindow {
                    kind: kind::CREDITS.to_string(),
                    label: format!("Reset credits \u{00D7}{}", util::num_text(get(Some(credits), "available_count"))),
                    used_pct: None,
                    remaining_pct: None,
                    resets_at: None,
                });
            }
        }
    }

    let plan = str_of(map, "plan_type").filter(|p| !p.is_empty()).map(|p| util::capitalize(&p));

    let mut p = util::new_ok(ADAPTER_ID, DISPLAY_NAME, now);
    p.plan = plan;
    p.windows = windows;
    p
}

impl UsageAdapter for CodexAdapter {
    fn id(&self) -> &str {
        ADAPTER_ID
    }

    fn display_name(&self) -> &str {
        DISPLAY_NAME
    }

    fn fetch_usage(&self, ctx: &AdapterContext) -> ProviderSnapshot {
        let now = (ctx.utc_now)();
        let paths = &ctx.paths;
        let auth_path = paths.codex_auth();

        let auth_present = util::file_exists(&auth_path);
        let cli_on_path = paths.find_on_path("codex").is_some();
        if !auth_present && !cli_on_path {
            return empty(state::NOT_INSTALLED, None, Some("Install Codex, then run: codex login"), now);
        }
        if !auth_present {
            return empty(state::LOGGED_OUT, None, Some("Run: codex login"), now);
        }

        let auth = util::read_text(&auth_path)
            .ok()
            .and_then(|text| json_util::parse(Some(&text)))
            .filter(|v| v.is_object());
        let auth = match auth {
            Some(a) => a,
            None => return empty(state::LOGGED_OUT, None, Some("Run: codex login"), now),
        };

        // An API-key login has no subscription windows to report.
        let tokens = map_of(Some(&auth), "tokens");
        let has_access_token = matches!(str_of(tokens, "access_token"), Some(t) if !t.is_empty());
        let has_api_key = has_text(Some(&auth), "api_key") || has_text(Some(&auth), "OPENAI_API_KEY");
        if has_api_key && !has_access_token {
            return empty(
                state::UNKNOWN,
                Some("Codex is signed in with an API key \u{2014} subscription windows are unavailable"),
                None,
                now,
            );
        }
        if !has_access_token {
            return empty(state::LOGGED_OUT, None, Some("Run: codex login"), now);
        }

        let access_token = str_of(tokens, "access_token").unwrap_or_default();
        let account_id = str_of(tokens, "account_id").unwrap_or_default();

        let authorization = format!("Bearer {}", access_token);
        let headers = [
            ("Authorization", authorization.as_str()),
            ("ChatGPT-Account-Id", account_id.as_str()),
            ("User-Agent", "Mozilla/5.0 usage-monitor/1.0"),
        ];

        let res = match get_json(&*ctx.http, USAGE_URL, &headers) {
            Ok(r) => r,
            Err(e) => return util::fetch_failed(ADAPTER_ID, DISPLAY_NAME, &e, now),
        };
        let status = res.status;

        if status == 401 || status == 403 {
            return empty(state::LOGGED_OUT, None, Some("Open Codex once to refresh login"), now);
        }
        if status == 429 {
            return util::rate_limited(ADAPTER_ID, DISPLAY_NAME, now);
        }
        match (&res.json, status) {
            (Some(json), 200) => map_wham(json, now),
            _ => empty(state::FETCH_FAILED, Some(&format!("HTTP {}", status)), None, now),
        }
    }
}
