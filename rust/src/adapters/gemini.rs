//! Gemini / Antigravity usage adapter (port of src/Adapters/GeminiAdapter.cs).
//!
//! Credentials come from three places, in order: the Windows Credential Manager
//! ("gemini:antigravity", injected as `ctx.credentials`), the on-disk token files under ~/.gemini,
//! or the ANTIGRAVITY_* environment variables. Usage comes from the v1internal:* endpoints of the
//! two API hosts; the quota summary is tried first and the quota / models endpoints are the
//! fallbacks.
//!
//! The OAuth client id/secret are extracted at runtime from the agy binary — never hard-coded
//! (with AGY_CLIENT_ID / AGY_CLIENT_SECRET and GEMINI_OAUTH_CLIENT_ID / GEMINI_OAUTH_CLIENT_SECRET
//! as environment overrides). No regex crate is used: the patterns of the C# code are matched by
//! hand.
use std::io::Read;
use std::sync::Mutex;

use chrono::{DateTime, Utc};
use serde_json::{json, Map, Value};

use super::http::{post_form, post_json};
use super::util::{self, f64_value, first_text, get, list_of, map_of, str_of};
use crate::core::contracts::{
    kind, state, AdapterContext, HttpError, HttpResult, ProviderSnapshot, UsageAdapter, UsageWindow,
};
use crate::core::json_util;
use crate::core::models::{parse_iso, window_of};

pub const ADAPTER_ID: &str = "gemini";
pub const DISPLAY_NAME: &str = "Gemini";

const WIN_CRED_TARGET: &str = "gemini:antigravity";
const TOKEN_URL: &str = "https://oauth2.googleapis.com/token";

const API_HOSTS: [&str; 2] = [
    "https://daily-cloudcode-pa.googleapis.com", // agy itself talks to daily; prod reports a separate, unused pool
    "https://cloudcode-pa.googleapis.com",
];

const USER_AGENT: &str = "antigravity/windows/amd64";

/// An access token that expires within this window is refreshed before it is used.
const REFRESH_SKEW_SECONDS: f64 = 60.0;

// ----------------------------------------------------------------------------------------------
// Mapping helpers. They take whatever the JSON parser produced and never panic on odd shapes.
// ----------------------------------------------------------------------------------------------

/// usedFromFraction() — a remaining fraction (0..1) becomes a used percentage, clamped to 0..100.
/// Non-numeric input is `None`, exactly like the JS Number.isFinite() guard.
pub fn used_from_fraction(remaining: Option<&Value>) -> Option<f64> {
    let frac = json_util::loose_num(remaining)?;
    let used = (1.0 - frac) * 100.0;
    Some(used.clamp(0.0, 100.0))
}

/// expiryEpoch() — accepts epoch seconds, epoch millis or an ISO-8601 string and always answers
/// in epoch seconds (the millis form is divided down when it is obviously millis).
pub fn expiry_epoch(value: Option<&Value>) -> Option<f64> {
    let value = value.filter(|v| !v.is_null())?;
    match value {
        Value::String(text) => {
            if text.is_empty() {
                return None;
            }
            if let Some(parsed) = json_util::loose_num(Some(value)) {
                return Some(if parsed > 1e12 { parsed / 1000.0 } else { parsed });
            }
            parse_iso(text).map(|iso| util::to_epoch_ms(iso) / 1000.0)
        }
        other => {
            let number = json_util::loose_num(Some(other))?;
            Some(if number > 1e12 { number / 1000.0 } else { number })
        }
    }
}

/// mapSummary() — only the groups that belong to Gemini are shown, so one provider's quota never
/// leaks into another's row.
pub fn map_summary(data: &Value, _now: DateTime<Utc>) -> Vec<UsageWindow> {
    let mut windows = Vec::new();
    let raw = match list_of(Some(data), "groups") {
        Some(r) => r,
        None => return windows,
    };

    let groups: Vec<&Value> = raw.iter().filter(|g| g.is_object() && is_gemini_group(g)).collect();

    for group in &groups {
        let buckets = match list_of(Some(group), "buckets") {
            Some(b) => b,
            None => continue,
        };
        for bucket in buckets {
            if !bucket.is_object() {
                continue;
            }
            if get(Some(bucket), "remainingFraction").is_none() {
                continue;
            }
            let used = match used_from_fraction(get(Some(bucket), "remainingFraction")) {
                Some(u) => u,
                None => continue,
            };

            let (k, mut label) = classify_bucket(bucket);
            if groups.len() > 1 {
                label = prefix_group_label(group, &label);
            }
            windows.push(window_of(k, &label, &f64_value(Some(used)), str_of(Some(bucket), "resetTime").as_deref()));
        }
    }
    windows
}

/// The non-Gemini groups of the summary (AGY also sells "Claude and GPT models" with their own
/// 5-hour and weekly limits). They get their own rows, labelled by group, and the kind Quota so
/// nothing that reads snapshot.json takes them for Gemini's own five_hour/weekly.
pub fn map_other_groups(data: &Value) -> Vec<UsageWindow> {
    let mut windows = Vec::new();
    let raw = match list_of(Some(data), "groups") {
        Some(r) => r,
        None => return windows,
    };

    for group in raw {
        if !group.is_object() || is_gemini_group(group) {
            continue;
        }
        let buckets = match list_of(Some(group), "buckets") {
            Some(b) => b,
            None => continue,
        };
        let prefix = short_group(group);
        let mut group_rows: Vec<UsageWindow> = Vec::new();

        for bucket in buckets {
            if !bucket.is_object() {
                continue;
            }
            let used = match used_from_fraction(get(Some(bucket), "remainingFraction")) {
                Some(u) => u,
                None => continue,
            };

            let (k, label) = classify_bucket(bucket);
            let suffix = if k == kind::FIVE_HOUR {
                "5h".to_string()
            } else if k == kind::WEEKLY {
                "Wk".to_string()
            } else {
                label
            };
            let text = if prefix.is_empty() { suffix } else { format!("{} {}", prefix, suffix) };
            let win = window_of(kind::QUOTA, &text, &f64_value(Some(used)), str_of(Some(bucket), "resetTime").as_deref());
            if k == kind::FIVE_HOUR {
                group_rows.insert(0, win);
            } else {
                group_rows.push(win);
            }
        }
        windows.extend(group_rows);
    }
    windows
}

struct QuotaEntry {
    used: f64,
    name: Option<String>,
    id: String,
    reset_time: Option<String>,
}

fn model_version(id: &str) -> f64 {
    match match_version_family(id.trim()) {
        Some((Some(version), _, _)) => version.parse::<f64>().unwrap_or(0.0),
        _ => 0.0,
    }
}

fn is_lite(id: &str) -> bool {
    id.to_lowercase().contains("lite")
}

/// "Claude and GPT models" -> "Claude/GPT".
fn short_group(group: &Value) -> String {
    let label = group_label(group);
    let mut name: &str = label.trim();
    if name.to_lowercase().ends_with(" models") {
        // " models" is 7 ASCII characters
        let cut = name.char_indices().rev().nth(6).map(|(i, _)| i).unwrap_or(0);
        name = &name[..cut];
    }
    let joined = replace_group_separators(name);
    if joined.chars().count() > 14 {
        joined.chars().take(14).collect()
    } else {
        joined
    }
}

/// Regex.Replace(name, @"\s+and\s+|\s*&\s*|\s*,\s*", "/", IgnoreCase).
fn replace_group_separators(name: &str) -> String {
    let chars: Vec<char> = name.chars().collect();
    let mut out = String::new();
    let mut i = 0;
    while i < chars.len() {
        match separator_end(&chars, i) {
            Some(end) if end > i => {
                out.push('/');
                i = end;
            }
            _ => {
                out.push(chars[i]);
                i += 1;
            }
        }
    }
    out
}

fn separator_end(chars: &[char], start: usize) -> Option<usize> {
    let skip_ws = |mut j: usize| {
        while j < chars.len() && chars[j].is_whitespace() {
            j += 1;
        }
        j
    };
    // \s+and\s+
    let after_ws = skip_ws(start);
    if after_ws > start
        && after_ws + 3 <= chars.len()
        && chars[after_ws..after_ws + 3].iter().collect::<String>().eq_ignore_ascii_case("and")
    {
        let after_and = after_ws + 3;
        let end = skip_ws(after_and);
        if end > after_and {
            return Some(end);
        }
    }
    // \s*&\s*  and  \s*,\s*
    if after_ws < chars.len() && (chars[after_ws] == '&' || chars[after_ws] == ',') {
        return Some(skip_ws(after_ws + 1));
    }
    None
}

/// mapModels() — the fetchAvailableModels response: an array or an id-keyed object, only the
/// Gemini models, the two highest-usage quotas.
pub fn map_models(data: &Value, _now: DateTime<Utc>) -> Vec<UsageWindow> {
    let mut models: Vec<Value> = Vec::new();
    if data.is_object() {
        let raw = get(Some(data), "models");
        match raw {
            Some(Value::Array(list)) => {
                for item in list {
                    if item.is_object() {
                        models.push(item.clone());
                    }
                }
            }
            Some(Value::Object(keyed)) => {
                // Object.entries(): the key becomes the model id when the entry has none.
                for (k, v) in keyed {
                    let mut merged = match v.as_object() {
                        Some(m) => m.clone(),
                        None => continue,
                    };
                    let has_model = matches!(merged.get("model"), Some(Value::String(s)) if !s.is_empty());
                    if !has_model {
                        merged.insert("model".to_string(), Value::String(k.clone()));
                    }
                    models.push(Value::Object(merged));
                }
            }
            _ => {}
        }
    }

    let mut scored: Vec<QuotaEntry> = Vec::new();
    for model in &models {
        let quota = match map_of(Some(model), "quotaInfo") {
            Some(q) => q,
            None => continue,
        };
        if get(Some(quota), "remainingFraction").is_none() {
            continue;
        }
        let used = match used_from_fraction(get(Some(quota), "remainingFraction")) {
            Some(u) => u,
            None => continue,
        };
        scored.push(QuotaEntry {
            used,
            name: first_text(Some(model), &["displayName", "label", "model", "name"]),
            id: str_of(Some(model), "model").or_else(|| str_of(Some(model), "name")).unwrap_or_default(),
            reset_time: str_of(Some(quota), "resetTime"),
        });
    }

    // stable, highest usage first
    scored.sort_by(|a, b| b.used.partial_cmp(&a.used).unwrap_or(std::cmp::Ordering::Equal));

    let mut picked: Vec<&QuotaEntry> = Vec::new();
    for q in &scored {
        let name = q.name.as_deref().unwrap_or("");
        if !name.to_lowercase().contains("gemini") && !q.id.to_lowercase().contains("gemini") {
            continue;
        }
        picked.push(q);
        if picked.len() == 2 {
            break;
        }
    }

    picked
        .iter()
        .map(|q| window_of(kind::QUOTA, &short_model(q.name.as_deref()), &f64_value(Some(q.used)), q.reset_time.as_deref()))
        .collect()
}

/// mapQuotaBuckets() — the retrieveUserQuota response (buckets per model). Live, every Gemini
/// model reports the same remainingFraction and resetTime: they share one 5-hour pool, so it is
/// shown as a single five_hour row (the highest-use bucket, worst case). No weekly Gemini bucket
/// exists here (prod's "gemini-weekly" never moves with agy use).
fn map_quota_buckets(data: &Value) -> Vec<UsageWindow> {
    let mut windows = Vec::new();
    let raw = match list_of(Some(data), "buckets") {
        Some(r) => r,
        None => return windows,
    };

    let mut scored: Vec<QuotaEntry> = Vec::new();
    for bucket in raw {
        if !bucket.is_object() {
            continue;
        }
        if get(Some(bucket), "remainingFraction").is_none() {
            continue;
        }
        let used = match used_from_fraction(get(Some(bucket), "remainingFraction")) {
            Some(u) => u,
            None => continue,
        };
        let name = str_of(Some(bucket), "modelId").or_else(|| str_of(Some(bucket), "id"));
        // Only buckets of a Gemini model are candidates (a bucket that belongs to another
        // provider must never take the row), before sorting by used%.
        if !is_gemini_bucket(bucket) {
            continue;
        }
        scored.push(QuotaEntry {
            used,
            id: name.clone().unwrap_or_default(),
            name,
            reset_time: str_of(Some(bucket), "resetTime"),
        });
    }

    // Ties (typically every model at the same value) go to the newest model, non-Lite first;
    // the sort is stable, so the original order decides the rest.
    scored.sort_by(|a, b| {
        let an = a.name.as_deref().unwrap_or("");
        let bn = b.name.as_deref().unwrap_or("");
        b.used
            .partial_cmp(&a.used)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| model_version(bn).partial_cmp(&model_version(an)).unwrap_or(std::cmp::Ordering::Equal))
            .then_with(|| is_lite(an).cmp(&is_lite(bn)))
    });
    if let Some(first) = scored.first() {
        windows.push(window_of(kind::FIVE_HOUR, "5h", &f64_value(Some(first.used)), first.reset_time.as_deref()));
    }
    windows
}

/// isGeminiBucket(): the retrieveUserQuota fallback is shared across providers, so a bucket only
/// counts as a candidate when its model id (modelId / name / id) mentions "gemini".
fn is_gemini_bucket(bucket: &Value) -> bool {
    ["modelId", "name", "id"]
        .iter()
        .any(|f| matches!(str_of(Some(bucket), f), Some(v) if v.to_lowercase().contains("gemini")))
}

// ----------------------------------------------------------------------------------------------
// classification / labels
// ----------------------------------------------------------------------------------------------

fn is_gemini_group(group: &Value) -> bool {
    matches!(first_text(Some(group), &["displayName", "name", "groupId"]), Some(n) if n.to_lowercase().contains("gemini"))
}

fn group_label(group: &Value) -> String {
    first_text(Some(group), &["displayName", "name", "groupId"]).unwrap_or_default()
}

fn prefix_group_label(group: &Value, label: &str) -> String {
    let prefix = group_label(group);
    if prefix.is_empty() {
        return label.to_string();
    }
    format!("{} {}", prefix, label)
}

fn is_sep_char(c: u8) -> bool {
    matches!(c, b' ' | b'_' | b'.' | b'-')
}

/// /five[ _.-]?hour|5[ _.-]?h/i on an already lower-cased string.
fn matches_five_hour(lower: &str) -> bool {
    let b = lower.as_bytes();
    for i in 0..b.len() {
        let rest = &b[i..];
        if rest.starts_with(b"five") {
            let after = &rest[4..];
            if after.starts_with(b"hour") || (!after.is_empty() && is_sep_char(after[0]) && after[1..].starts_with(b"hour")) {
                return true;
            }
        }
        if rest[0] == b'5' {
            let after = &rest[1..];
            if after.first() == Some(&b'h') || (!after.is_empty() && is_sep_char(after[0]) && after.get(1) == Some(&b'h')) {
                return true;
            }
        }
    }
    false
}

// classifyBucket(): the bucket name (and window field) decide the kind. The reference regex is
// /five.?hour|5h|FIVE_HOUR/i, which does not match the "5 hour limit" style the API actually
// returns, so the separator is optional here as well.
fn classify_bucket(bucket: &Value) -> (&'static str, String) {
    let bucket_label = first_text(Some(bucket), &["displayName", "bucketId"]).unwrap_or_default();
    let window = str_of(Some(bucket), "window").unwrap_or_default();
    let joined = format!("{} {}", bucket_label, window).to_lowercase();

    if matches_five_hour(&joined) {
        return (kind::FIVE_HOUR, "5h".to_string());
    }
    if joined.contains("week") {
        return (kind::WEEKLY, "Weekly".to_string());
    }
    if joined.contains("day") || joined.contains("daily") || joined.contains("rpd") {
        return (kind::DAILY, "Daily".to_string());
    }
    let label = if bucket_label.is_empty() { "Quota".to_string() } else { bucket_label };
    (kind::WEEKLY_SCOPED, label)
}

/// "<version>-<family>[-lite]" ids ("gemini-3.8-flash-tiered", "gemini-3.5-flash-lite",
/// "gemini-2.5-pro"); this is the regex
/// `^(?:models/)?(?:gemini[-_ ])?(?:(\d+(?:\.\d+)?)[-_ ])?(flash|pro|ultra|nano)(?:[-_ ](lite))?(?:$|[-_ ])`
/// (case-insensitive) matched by hand. Returns (version, lower-case family, lite).
fn match_version_family(s: &str) -> Option<(Option<String>, String, bool)> {
    let lower = s.to_ascii_lowercase();
    let b = lower.as_bytes();
    let len = b.len();
    let mut i = 0;

    if lower[i..].starts_with("models/") {
        i += 7;
    }
    if lower[i..].starts_with("gemini") && i + 6 < len && is_sep_char(b[i + 6]) {
        i += 7;
    }

    // optional version
    let mut version: Option<String> = None;
    if i < len && b[i].is_ascii_digit() {
        let mut j = i;
        while j < len && b[j].is_ascii_digit() {
            j += 1;
        }
        if j + 1 < len && b[j] == b'.' && b[j + 1].is_ascii_digit() {
            j += 1;
            while j < len && b[j].is_ascii_digit() {
                j += 1;
            }
        }
        if j < len && is_sep_char(b[j]) {
            version = Some(s[i..j].to_string());
            i = j + 1;
        } else {
            return None; // a digit run that is not followed by a separator cannot start a family
        }
    }

    let family = ["flash", "pro", "ultra", "nano"].iter().find(|f| lower[i..].starts_with(**f))?;
    let m = i + family.len();

    // (?:[-_ ](lite))? first, then without it
    if m + 5 <= len && is_sep_char(b[m]) && &b[m + 1..m + 5] == b"lite" && (m + 5 == len || is_sep_char(b[m + 5])) {
        return Some((version, family.to_string(), true));
    }
    if m == len || is_sep_char(b[m]) {
        return Some((version, family.to_string(), false));
    }
    None
}

/// Does `lower` contain `first` followed (later on the same line) by `second`?
fn contains_in_order(lower: &str, first: &str, second: &str) -> bool {
    lower.match_indices(first).any(|(i, m)| {
        let rest = &lower[i + m.len()..];
        let line = rest.split('\n').next().unwrap_or("");
        line.contains(second)
    })
}

/// shortModel(): "gemini-3-pro" -> "3 Pro", "gemini-flash" -> "Flash", anything unknown keeps (at
/// most) the first 14 characters of its id.
fn short_model(model_id: Option<&str>) -> String {
    let s = model_id.unwrap_or("");
    if s.is_empty() {
        return "Model".to_string();
    }
    if let Some((version, family, lite)) = match_version_family(s.trim()) {
        let mut text = util::capitalize(&family);
        if lite {
            text.push_str(" Lite");
        }
        if let Some(v) = version {
            text = format!("{} {}", v, text);
        }
        return text;
    }
    let lower = s.to_lowercase();
    // 3\.?8.*flash | 3-flash | 3\.8
    let flash_38 = contains_in_order(&lower, "3.8", "flash")
        || contains_in_order(&lower, "38", "flash")
        || lower.contains("3-flash")
        || lower.contains("3.8");
    if flash_38 {
        return "3 Flash".to_string();
    }
    if contains_in_order(&lower, "3", "pro") {
        return "3 Pro".to_string();
    }
    if lower.contains("flash") {
        return "Flash".to_string();
    }
    if lower.contains("pro") {
        return "Pro".to_string();
    }

    let stripped = s.strip_prefix("models/").unwrap_or(s);
    let stripped = stripped.strip_prefix("gemini-").unwrap_or(stripped);
    if stripped.chars().count() > 14 {
        stripped.chars().take(14).collect()
    } else {
        stripped.to_string()
    }
}

fn extract_project(data: &Value) -> Option<String> {
    let project = get(Some(data), "cloudaicompanionProject")?;
    if let Some(text) = project.as_str() {
        if !text.is_empty() {
            return Some(text.to_string());
        }
    }
    if project.is_object() {
        return str_of(Some(project), "id");
    }
    None
}

// planLabel(): the paid tier's name wins; otherwise the current tier's, where a tier without a
// name is its id and the free tier is always shown as "Free".
fn plan_label(data: &Value) -> Option<String> {
    if !data.is_object() {
        return None;
    }
    if let Some(paid) = tier_name(get(Some(data), "paidTier")) {
        return Some(paid);
    }

    let current = get(Some(data), "currentTier");
    let current_id = tier_id(current);
    if let Some(current_name) = tier_name(current) {
        if current_id.as_deref() == Some("free-tier") || current_name.to_lowercase().contains("free") {
            return Some("Free".to_string());
        }
        return Some(current_name);
    }
    None
}

// tierName(): a tier's name is its non-empty `name`, otherwise its non-empty `id`.
fn tier_name(raw: Option<&Value>) -> Option<String> {
    let raw = raw?;
    if let Some(text) = raw.as_str() {
        return if text.is_empty() { None } else { Some(text.to_string()) };
    }
    if !raw.is_object() {
        return None;
    }
    let mut name = str_of(Some(raw), "name").unwrap_or_default();
    if name.is_empty() {
        name = str_of(Some(raw), "id").unwrap_or_default();
    }
    if name.is_empty() {
        None
    } else {
        Some(name)
    }
}

// tierId(): the tier's id — the `id` field of an object tier, and the string itself for a plain
// string tier (which counts as both name and id).
fn tier_id(raw: Option<&Value>) -> Option<String> {
    let raw = raw?;
    if let Some(text) = raw.as_str() {
        return if text.is_empty() { None } else { Some(text.to_string()) };
    }
    if !raw.is_object() {
        return None;
    }
    str_of(Some(raw), "id").filter(|s| !s.is_empty())
}

// ----------------------------------------------------------------------------------------------
// adapter plumbing
// ----------------------------------------------------------------------------------------------

#[derive(Clone)]
struct GemCreds {
    access_token: Option<String>,
    refresh_token: Option<String>,
    expiry_epoch: Option<f64>,
    source: String,
}

#[derive(Clone)]
struct OauthClient {
    client_id: String,
    client_secret: String,
}

#[derive(Default)]
struct HostResult {
    ok: bool,
    auth_failed: bool,
    failed: bool,
    has_status: bool,
    status: u16,
    plan: Option<String>,
    windows: Vec<UsageWindow>,
}

#[derive(Default)]
struct Cache {
    mem_creds: Option<GemCreds>,
    oauth_client: Option<OauthClient>,
    /// When ExtractOauthClient() fails, the UTC time of that failure is remembered here. A
    /// recently failed extraction is not retried for ten minutes, so an expired token does not
    /// rescan the ~190 MB agy binary (up to twice) on every poll. A success clears it again.
    oauth_client_failed: Option<DateTime<Utc>>,
}

/// Refreshed credentials and the extracted OAuth client are cached per instance so two adapters
/// never share state.
#[derive(Default)]
pub struct GeminiAdapter {
    cache: Mutex<Cache>,
}

impl GeminiAdapter {
    pub fn new() -> GeminiAdapter {
        GeminiAdapter::default()
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Cache> {
        self.cache.lock().unwrap_or_else(|e| e.into_inner())
    }
}

fn empty(state: &str, message: Option<&str>, hint: Option<&str>, now: DateTime<Utc>) -> ProviderSnapshot {
    util::empty(ADAPTER_ID, DISPLAY_NAME, state, message, hint, now)
}

fn cli_on_path(ctx: &AdapterContext) -> bool {
    ctx.paths.find_on_path("agy").is_some() || ctx.paths.find_on_path("gemini").is_some()
}

impl UsageAdapter for GeminiAdapter {
    fn id(&self) -> &str {
        ADAPTER_ID
    }

    fn display_name(&self) -> &str {
        DISPLAY_NAME
    }

    fn fetch_usage(&self, ctx: &AdapterContext) -> ProviderSnapshot {
        let now = (ctx.utc_now)();
        let cli = cli_on_path(ctx);

        let mut creds = match self.get_creds(ctx, now) {
            Some(c) => c,
            None => {
                if !cli {
                    return empty(state::NOT_INSTALLED, None, Some("Install Antigravity CLI, then run: agy"), now);
                }
                return empty(state::LOGGED_OUT, None, Some("Run: agy  (sign in with Google)"), now);
            }
        };

        let refresh_enabled = ctx.config.refresh_tokens(ADAPTER_ID);
        let load_body = load_body(&creds, ctx);

        let mut token = creds.access_token.clone();
        let mut last_fail: Option<HostResult> = None;

        for host in API_HOSTS {
            let mut result = match fetch_from_host(ctx, host, token.as_deref(), &load_body, now) {
                Ok(r) => r,
                Err(_) => {
                    last_fail = Some(HostResult { failed: true, ..HostResult::default() });
                    continue;
                }
            };

            if result.auth_failed && refresh_enabled && matches!(creds.refresh_token.as_deref(), Some(r) if !r.is_empty()) {
                if let Some(next) = self.refresh_access(ctx, &creds, now) {
                    if matches!(next.access_token.as_deref(), Some(a) if !a.is_empty()) {
                        token = next.access_token.clone();
                        creds = next;
                        result = match fetch_from_host(ctx, host, token.as_deref(), &load_body, now) {
                            Ok(r) => r,
                            Err(_) => {
                                last_fail = Some(HostResult { failed: true, ..HostResult::default() });
                                continue;
                            }
                        };
                    }
                }
            }

            if result.auth_failed {
                return empty(state::LOGGED_OUT, None, Some("Run: agy  (token expired)"), now);
            }

            if result.ok {
                let mut p = util::new_ok(ADAPTER_ID, DISPLAY_NAME, now);
                p.plan = result.plan;
                p.windows = result.windows;
                return p;
            }

            let stop = result.failed && result.has_status && result.status == 429;
            last_fail = Some(result);
            if stop {
                break;
            }
        }

        if let Some(f) = &last_fail {
            if f.failed && f.has_status && f.status == 429 {
                return util::rate_limited(ADAPTER_ID, DISPLAY_NAME, now);
            }
        }

        let message = match &last_fail {
            Some(f) if f.has_status && f.status != 0 => format!("HTTP {}", f.status),
            _ => "quota unavailable".to_string(),
        };
        empty(state::FETCH_FAILED, Some(&message), None, now)
    }
}

// fetchFromHost(): loadCodeAssist for the project + plan, then the quota summary and its two
// fallbacks. Any 401/403 anywhere means "the token is dead"; 429 everywhere means "back off".
fn fetch_from_host(
    ctx: &AdapterContext,
    host: &str,
    token: Option<&str>,
    load_body: &Value,
    now: DateTime<Utc>,
) -> Result<HostResult, HttpError> {
    let mut r = HostResult::default();

    let load = assist_post(ctx, host, token, "loadCodeAssist", load_body)?;
    if load.status == 401 || load.status == 403 {
        r.auth_failed = true;
        return Ok(r);
    }
    let load_json = match (&load.json, load.status) {
        (Some(j), 200) => j,
        _ => {
            r.failed = true;
            r.status = load.status;
            r.has_status = true;
            return Ok(r);
        }
    };

    let project = extract_project(load_json);
    r.plan = plan_label(load_json);

    let mut quota_body = Map::new();
    if let Some(p) = project.filter(|p| !p.is_empty()) {
        quota_body.insert("project".to_string(), Value::String(p));
    }
    let quota_body = Value::Object(quota_body);

    macro_rules! back_off {
        ($res:expr) => {
            if $res.status == 429 {
                r.failed = true;
                r.status = 429;
                r.has_status = true;
                return Ok(r);
            }
            if $res.status == 401 || $res.status == 403 {
                r.auth_failed = true;
                return Ok(r);
            }
        };
    }

    let summary = assist_post(ctx, host, token, "retrieveUserQuotaSummary", &quota_body)?;
    back_off!(summary);

    // Third-party (Claude/GPT) rows from the summary are appended after the Gemini rows,
    // whichever endpoint the Gemini rows come from.
    let mut others: Vec<UsageWindow> = Vec::new();
    if summary.status == 200 {
        if let Some(json) = &summary.json {
            others = map_other_groups(json);
            let windows = map_summary(json, now);
            if !windows.is_empty() {
                return Ok(done(r, windows, others));
            }
        }
    }

    let quota = assist_post(ctx, host, token, "retrieveUserQuota", &quota_body)?;
    back_off!(quota);
    if quota.status == 200 {
        if let Some(json) = &quota.json {
            let windows = map_quota_buckets(json);
            if !windows.is_empty() {
                return Ok(done(r, windows, others));
            }
        }
    }

    let models = assist_post(ctx, host, token, "fetchAvailableModels", &quota_body)?;
    back_off!(models);
    if models.status == 200 {
        if let Some(json) = &models.json {
            let windows = map_models(json, now);
            if !windows.is_empty() {
                return Ok(done(r, windows, others));
            }
        }
    }

    if !others.is_empty() {
        return Ok(done(r, Vec::new(), others));
    }

    // JS: [models, quota, summary].find((r) => r.status !== 200)?.status
    r.failed = true;
    if models.status != 200 {
        r.status = models.status;
        r.has_status = true;
    } else if quota.status != 200 {
        r.status = quota.status;
        r.has_status = true;
    } else if summary.status != 200 {
        r.status = summary.status;
        r.has_status = true;
    }
    Ok(r)
}

fn done(mut r: HostResult, gemini: Vec<UsageWindow>, others: Vec<UsageWindow>) -> HostResult {
    let mut all = gemini;
    all.extend(others);
    r.ok = true;
    r.windows = all;
    r
}

fn assist_post(
    ctx: &AdapterContext,
    host: &str,
    token: Option<&str>,
    method: &str,
    body: &Value,
) -> Result<HttpResult, HttpError> {
    let authorization = format!("Bearer {}", token.unwrap_or(""));
    let headers = [("Authorization", authorization.as_str()), ("User-Agent", USER_AGENT)];
    post_json(&*ctx.http, &format!("{}/v1internal:{}", host, method), &headers, body)
}

fn load_body(creds: &GemCreds, ctx: &AdapterContext) -> Value {
    let mut ide_type = "ANTIGRAVITY";
    if creds.source == "file" {
        let oauth = util::file_exists(&ctx.paths.gemini_oauth());
        let antigravity = util::file_exists(&ctx.paths.antigravity_token());
        if oauth && !antigravity {
            ide_type = "IDE_UNSPECIFIED";
        }
    }
    json!({ "metadata": { "ideType": ide_type, "platform": "PLATFORM_UNSPECIFIED", "pluginType": "GEMINI" } })
}

// ----------------------------------------------------------------------------------------------
// credentials
// ----------------------------------------------------------------------------------------------

fn access_valid(creds: Option<&GemCreds>, now: DateTime<Utc>) -> bool {
    let creds = match creds {
        Some(c) => c,
        None => return false,
    };
    if !matches!(creds.access_token.as_deref(), Some(a) if !a.is_empty()) {
        return false;
    }
    // JS: `if (!creds.expiry_epoch) return true;` — an absent (or zeroed) expiry is trusted.
    match creds.expiry_epoch {
        None => true,
        Some(e) if e == 0.0 => true,
        Some(e) => e - util::to_epoch_ms(now) / 1000.0 > REFRESH_SKEW_SECONDS,
    }
}

impl GeminiAdapter {
    fn get_creds(&self, ctx: &AdapterContext, now: DateTime<Utc>) -> Option<GemCreds> {
        let refresh_enabled = ctx.config.refresh_tokens(ADAPTER_ID);
        {
            let cache = self.lock();
            if access_valid(cache.mem_creds.as_ref(), now) {
                return cache.mem_creds.clone();
            }
        }

        let creds = read_stored_creds(ctx)?;

        if refresh_enabled && !access_valid(Some(&creds), now) && matches!(creds.refresh_token.as_deref(), Some(r) if !r.is_empty()) {
            if let Some(next) = self.refresh_access(ctx, &creds, now) {
                return Some(next);
            }
        }

        self.lock().mem_creds = Some(creds.clone());
        Some(creds)
    }

    fn refresh_access(&self, ctx: &AdapterContext, creds: &GemCreds, now: DateTime<Utc>) -> Option<GemCreds> {
        let refresh_token = creds.refresh_token.as_deref().filter(|r| !r.is_empty())?;
        let client = self.extract_oauth_client(ctx)?;

        let fields = [
            ("client_id", client.client_id.as_str()),
            ("client_secret", client.client_secret.as_str()),
            ("refresh_token", refresh_token),
            ("grant_type", "refresh_token"),
        ];
        let res = post_form(&*ctx.http, TOKEN_URL, &[("Accept", "application/json")], &fields).ok()?;
        let token = res.json.as_ref().filter(|_| res.status == 200)?;
        let access = str_of(Some(token), "access_token").filter(|a| !a.is_empty())?;

        let refresh = str_of(Some(token), "refresh_token")
            .filter(|r| !r.is_empty())
            .unwrap_or_else(|| refresh_token.to_string());

        let seconds = match util::num(get(Some(token), "expires_in")) {
            Some(e) if e != 0.0 => e,
            _ => 3600.0,
        };

        let next = GemCreds {
            access_token: Some(access),
            refresh_token: Some(refresh),
            expiry_epoch: Some(util::to_epoch_ms(now) / 1000.0 + seconds),
            source: creds.source.clone(),
        };
        self.lock().mem_creds = Some(next.clone());
        Some(next)
    }

    // extractOauthClient(): the environment overrides win, otherwise the client id and secret are
    // scraped out of the agy binary (that is where the CLI keeps them).
    fn extract_oauth_client(&self, ctx: &AdapterContext) -> Option<OauthClient> {
        let now = (ctx.utc_now)();
        {
            let cache = self.lock();
            if let Some(c) = &cache.oauth_client {
                return Some(c.clone());
            }
            // Negative cache: a recent failed scan (no binary, or no secrets in it) is not
            // retried for ten minutes.
            if let Some(failed) = cache.oauth_client_failed {
                if (now - failed).num_milliseconds() as f64 / 60000.0 < 10.0 {
                    return None;
                }
            }
        }

        let found = extract_oauth_client_uncached(ctx);
        let mut cache = self.lock();
        match found {
            Some(c) => {
                cache.oauth_client = Some(c.clone());
                cache.oauth_client_failed = None; // a success forgets any earlier failure
                Some(c)
            }
            None => {
                cache.oauth_client_failed = Some(now);
                None
            }
        }
    }
}

fn env_non_empty(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|v| !v.is_empty())
}

// readStoredCreds(): wincred first, then the token files, then the environment.
fn read_stored_creds(ctx: &AdapterContext) -> Option<GemCreds> {
    let blob = ctx.credentials.read_generic_utf8(WIN_CRED_TARGET);
    if let Some(parsed) = json_util::parse(blob.as_deref()).filter(|v| v.is_object()) {
        let token = map_of(Some(&parsed), "token");
        if let Some(from_block) = from_token_block(Some(token.unwrap_or(&parsed)), "wincred") {
            return Some(from_block);
        }
    }

    let from_file = from_file(&ctx.paths.antigravity_token()).or_else(|| from_file(&ctx.paths.gemini_oauth()));
    if from_file.is_some() {
        return from_file;
    }

    let env_refresh = env_non_empty("ANTIGRAVITY_REFRESH_TOKEN");
    let env_access = env_non_empty("ANTIGRAVITY_ACCESS_TOKEN");
    if env_refresh.is_some() || env_access.is_some() {
        return Some(GemCreds {
            access_token: env_access,
            refresh_token: env_refresh,
            expiry_epoch: None,
            source: "env".to_string(),
        });
    }
    None
}

fn from_token_block(token: Option<&Value>, source: &str) -> Option<GemCreds> {
    let token = token.filter(|v| v.is_object());
    token?;

    let non_empty = |key: &str| str_of(token, key).filter(|s| !s.is_empty());
    let access = non_empty("access_token").or_else(|| non_empty("accessToken"));
    let refresh = non_empty("refresh_token").or_else(|| non_empty("refreshToken"));
    if access.is_none() && refresh.is_none() {
        return None;
    }

    let expiry = get(token, "expiry").or_else(|| get(token, "expiry_date")).or_else(|| get(token, "expires_at"));
    Some(GemCreds {
        access_token: access,
        refresh_token: refresh,
        expiry_epoch: expiry_epoch(expiry),
        source: source.to_string(),
    })
}

fn from_file(path: &str) -> Option<GemCreds> {
    if !util::file_exists(path) {
        return None;
    }
    let text = util::read_text(path).ok()?;
    let json = json_util::parse(Some(&text)).filter(|v| v.is_object())?;
    let token = map_of(Some(&json), "token");
    from_token_block(Some(token.unwrap_or(&json)), "file")
}

// The actual extraction (environment overrides, then the agy binary).
fn extract_oauth_client_uncached(ctx: &AdapterContext) -> Option<OauthClient> {
    let env_id = env_non_empty("AGY_CLIENT_ID").or_else(|| env_non_empty("GEMINI_OAUTH_CLIENT_ID"));
    let env_secret = env_non_empty("AGY_CLIENT_SECRET").or_else(|| env_non_empty("GEMINI_OAUTH_CLIENT_SECRET"));
    if let (Some(id), Some(secret)) = (&env_id, &env_secret) {
        return Some(OauthClient { client_id: id.clone(), client_secret: secret.clone() });
    }

    let bin_path = find_agy_binary(ctx)?;
    if !util::file_exists(&bin_path) {
        return None;
    }
    let (ids, secrets) = scan_binary(&bin_path).ok()?;
    if ids.is_empty() || secrets.is_empty() {
        return None;
    }

    let client_id = ids.iter().find(|id| id.starts_with("1071")).unwrap_or(&ids[0]).clone();
    Some(OauthClient { client_id, client_secret: secrets[0].clone() })
}

fn find_agy_binary(ctx: &AdapterContext) -> Option<String> {
    if let Some(from_path) = ctx.paths.find_on_path("agy") {
        return Some(from_path);
    }
    ctx.paths
        .agy_binary_candidates()
        .iter()
        .find(|c| !c.is_empty() && util::file_exists(c))
        .cloned()
}

// The two markers a client id and a client secret always sit next to, as raw bytes.
const CLIENT_ID_SUFFIX: &[u8] = b".apps.googleusercontent.com";
const SECRET_PREFIX: &[u8] = b"GOCSPX-";

// Reading the whole binary into one string (and running regexes over it) is quadratic on a long
// digit run, so the file is scanned in chunks and only the candidates are kept, never the text.
const SCAN_CHUNK: usize = 1024 * 1024;
// A candidate can straddle a chunk boundary; 256 bytes of overlap keep even a long one (ten or
// more digits + "-" + the run + the 26-byte suffix) visible in both chunks.
const SCAN_OVERLAP: usize = 256;

// Scans the binary for client ids and secrets:
//   id     — at every occurrence of ".apps.googleusercontent.com", walk back over the [a-z0-9]+
//            run (at least one), the single "-" and the digit run (at least ten) in front of it:
//            <digits>-[a-z0-9]+.apps.googleusercontent.com.
//   secret — "GOCSPX-" plus the [A-Za-z0-9_]{20,40} run that directly follows it (the first 40
//            bytes when the run is longer).
// The scan stops as soon as a "1071" id and a secret are both known.
fn scan_binary(path: &str) -> std::io::Result<(Vec<String>, Vec<String>)> {
    let mut ids: Vec<String> = Vec::new();
    let mut secrets: Vec<String> = Vec::new();

    let mut buf = vec![0u8; SCAN_CHUNK + SCAN_OVERLAP];
    let mut file = std::fs::File::open(path)?;
    let mut carried = 0usize; // bytes of the previous chunk repeated at the front of buf
    loop {
        let mut read = 0usize;
        while read < SCAN_CHUNK {
            let n = file.read(&mut buf[carried + read..carried + SCAN_CHUNK])?;
            if n == 0 {
                break;
            }
            read += n;
        }
        let limit = carried + read;
        if limit == 0 {
            break;
        }

        // A short fill means the stream is exhausted, so the window ends on the last byte of the
        // file and a candidate cut off by that end is final.
        let at_end = read < SCAN_CHUNK;

        scan_client_ids(&buf, limit, &mut ids);
        scan_secrets(&buf, limit, at_end, &mut secrets);

        // At the end of the file nothing follows the window, so its tail is not repeated.
        if at_end {
            break;
        }
        if ids.iter().any(|id| id.starts_with("1071")) && !secrets.is_empty() {
            break;
        }

        let carry = limit.min(SCAN_OVERLAP);
        buf.copy_within(limit - carry..limit, 0);
        carried = carry;
    }
    Ok((ids, secrets))
}

fn scan_client_ids(buf: &[u8], limit: usize, ids: &mut Vec<String>) {
    let mut from = 0usize;
    while from + CLIENT_ID_SUFFIX.len() <= limit {
        let at = match index_of_bytes(buf, from, limit, CLIENT_ID_SUFFIX) {
            Some(a) => a,
            None => return,
        };

        // walk back over [a-z0-9]+
        let mut run = at as isize - 1;
        while run >= 0 && is_lower_alnum(buf[run as usize]) {
            run -= 1;
        }
        let head = (run + 1) as usize; // first byte of that run
        if head == at || run < 0 || buf[run as usize] != b'-' {
            from = at + 1; // no run, or not "<digits>-"
            continue;
        }

        // walk back over the digit run
        let mut digits = run - 1;
        while digits >= 0 && buf[digits as usize].is_ascii_digit() {
            digits -= 1;
        }
        if run - digits - 1 < 10 {
            // fewer than ten digits: not an id
            from = at + 1;
            continue;
        }

        let start = (digits + 1) as usize;
        ids.push(latin1(&buf[start..at + CLIENT_ID_SUFFIX.len()]));
        from = at + 1;
    }
}

fn scan_secrets(buf: &[u8], limit: usize, at_end: bool, secrets: &mut Vec<String>) {
    let mut from = 0usize;
    while from + SECRET_PREFIX.len() <= limit {
        let at = match index_of_bytes(buf, from, limit, SECRET_PREFIX) {
            Some(a) => a,
            None => return,
        };

        let body = at + SECRET_PREFIX.len();
        let mut run = 0usize; // [A-Za-z0-9_]{20,40}, capped at 40
        // The binary stores secrets back to back (GOCSPX-<28>GOCSPX-<16>...), and the JS
        // reference splits on the marker, so a run also ends where the next marker begins.
        while run < 40 && body + run < limit && is_secret_char(buf[body + run]) && !starts_with_at(buf, body + run, limit, SECRET_PREFIX) {
            run += 1;
        }
        // A run the window cut short (not the cap, not the end of the file) is completed by the
        // next chunk, which repeats the last 256 bytes at its front. Recording the short form
        // here would put a truncated candidate first, so it is skipped and picked up whole next
        // time round.
        let cut = run < 40 && body + run >= limit && !at_end;
        if !cut && run >= 20 {
            secrets.push(latin1(&buf[at..at + SECRET_PREFIX.len() + run]));
        }
        from = at + 1;
    }
}

fn starts_with_at(buf: &[u8], at: usize, limit: usize, needle: &[u8]) -> bool {
    at + needle.len() <= limit && &buf[at..at + needle.len()] == needle
}

fn index_of_bytes(buf: &[u8], from: usize, limit: usize, needle: &[u8]) -> Option<usize> {
    let n = needle.len();
    if limit < n {
        return None;
    }
    let last = limit - n;
    let first = needle[0];
    let mut i = from;
    while i <= last {
        if buf[i] == first && &buf[i + 1..i + n] == &needle[1..] {
            return Some(i);
        }
        i += 1;
    }
    None
}

fn is_lower_alnum(b: u8) -> bool {
    b.is_ascii_digit() || b.is_ascii_lowercase()
}

fn is_secret_char(b: u8) -> bool {
    b.is_ascii_alphanumeric() || b == b'_'
}

/// The latin1 mapping the JS reference reads the binary with: byte -> char.
fn latin1(bytes: &[u8]) -> String {
    bytes.iter().map(|b| *b as char).collect()
}
