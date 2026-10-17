//! Grok Build usage adapter (port of src/Adapters/GrokAdapter.cs).
//!
//! Tokens live in ~/.grok/auth.json (one entry per signed-in account; the newest one wins). Usage
//! comes from the REST billing endpoint; when the REST response has no percentage (proto3 omits a
//! zero) the grpc-web GetGrokCreditsConfig call fills it in. The protobuf and grpc-web frame
//! parsing is hand written so no protobuf dependency is needed.
use std::time::SystemTime;

use chrono::{DateTime, Utc};
use serde_json::{Map, Value};

use super::http::{get_json, post_form};
use super::util::{self, f64_value, get, has, list_of, map_of, str_of, str_path};
use crate::core::contracts::{kind, state, AdapterContext, HttpError, ProviderSnapshot, UsageAdapter, UsageWindow};
use crate::core::json_out::indented_json;
use crate::core::json_util;
use crate::core::models::{parse_iso, to_iso, window_of};

pub const ADAPTER_ID: &str = "grok";
pub const DISPLAY_NAME: &str = "Grok Build";

const BILLING_URL: &str = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
const GRPC_CREDITS_URL: &str = "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";
const GRPC_TIMEOUT_MS: u32 = 4000;

/// GetGrokCreditsConfigRequest { bool exclude_legacy_monthly_usage = 1; } as a grpc-web frame.
const GRPC_REQUEST: [u8; 7] = [0, 0, 0, 0, 2, 0x08, 0x00];

/// A token that expires within this window is refreshed before it is used.
const REFRESH_SKEW_MS: f64 = 60000.0;

const DEFAULT_ISSUER: &str = "https://auth.x.ai";

#[derive(Default)]
pub struct GrokAdapter;

impl GrokAdapter {
    pub fn new() -> GrokAdapter {
        GrokAdapter
    }
}

fn empty(state: &str, message: Option<&str>, hint: Option<&str>, now: DateTime<Utc>) -> ProviderSnapshot {
    util::empty(ADAPTER_ID, DISPLAY_NAME, state, message, hint, now)
}

// ----------------------------------------------------------------------------------------------
// mapping (body -> snapshot). Takes whatever the JSON parser produced, never panics.
// ----------------------------------------------------------------------------------------------

pub fn map_billing(body: &Value, now: DateTime<Utc>) -> ProviderSnapshot {
    let map = Some(body).filter(|v| v.is_object());
    let none_cfg = Value::Object(Map::new());
    let cfg: &Value = match map_of(map, "config") {
        Some(c) => c,
        None => map.unwrap_or(&none_cfg),
    };
    let cfg = Some(cfg);

    let mut windows: Vec<UsageWindow> = Vec::new();
    let mut pct = weekly_pct(cfg);
    let from_credits = has(cfg, "creditUsagePercent") || !product_percents(cfg).is_empty();

    // proto3 JSON omits a 0.0 creditUsagePercent. Grok's /usage still draws a 0% bar when the
    // period is live, so the zero is only invented for a live period.
    if pct.is_none() && has_active_period(cfg, now) {
        pct = Some(0.0);
    }

    if let Some(p) = pct {
        let (k, label) = period_meta(cfg, from_credits);
        windows.push(window_of(k, label, &f64_value(Some(p)), period_end(cfg).as_deref()));
    }

    let cap = field_val(cfg, "onDemandCap");
    let prepaid = field_val(cfg, "prepaidBalance");
    let used = field_val(cfg, "onDemandUsed");

    if let Some(c) = cap.filter(|c| *c > 0.0) {
        let used_val = used.unwrap_or(0.0);
        windows.push(window_of(kind::CREDITS, "Credits", &f64_value(Some(used_val / c * 100.0)), None));
    } else if let Some(pb) = prepaid.filter(|p| *p > 0.0) {
        windows.push(UsageWindow {
            kind: kind::CREDITS.to_string(),
            label: format!("Credits ${:.2}", pb),
            used_pct: None,
            remaining_pct: None,
            resets_at: None,
        });
    }

    let mut footnotes = Vec::new();
    if let Some(products) = list_of(cfg, "productUsage") {
        for item in products {
            if !item.is_object() {
                continue;
            }
            let product = match str_of(Some(item), "product") {
                Some(p) if !p.is_empty() => p,
                _ => continue,
            };
            if get(Some(item), "usagePercent").is_none() {
                continue;
            }
            let name = product.strip_prefix("Grok").unwrap_or(&product);
            footnotes.push(format!("{} {}% of pool", name, util::num_text(get(Some(item), "usagePercent"))));
        }
    }

    let mut p = util::new_ok(ADAPTER_ID, DISPLAY_NAME, now);
    p.plan = Some("SuperGrok".to_string());
    p.windows = windows;
    p.footnotes = footnotes;
    p
}

// weeklyPct(): the explicit credit percentage wins, then the sum of the product pools, then
// used / monthlyLimit.
fn weekly_pct(cfg: Option<&Value>) -> Option<f64> {
    cfg?;
    if let Some(explicit) = util::num(get(cfg, "creditUsagePercent")) {
        return Some(explicit);
    }

    let products = product_percents(cfg);
    if !products.is_empty() {
        return Some(products.iter().sum());
    }

    let used = field_val(cfg, "used");
    let limit = field_val(cfg, "monthlyLimit");
    match (used, limit) {
        (Some(u), Some(l)) if l > 0.0 => Some(u / l * 100.0),
        _ => None,
    }
}

fn product_percents(cfg: Option<&Value>) -> Vec<f64> {
    let mut values = Vec::new();
    if let Some(list) = list_of(cfg, "productUsage") {
        for item in list {
            if !item.is_object() {
                continue;
            }
            if !matches!(str_of(Some(item), "product"), Some(p) if !p.is_empty()) {
                continue;
            }
            if let Some(n) = util::num(get(Some(item), "usagePercent")) {
                values.push(n);
            }
        }
    }
    values
}

/// The .val wrapper the billing config uses for money/usage numbers.
fn field_val(cfg: Option<&Value>, key: &str) -> Option<f64> {
    cfg?;
    util::num(get(map_of(cfg, key), "val"))
}

fn period_end(cfg: Option<&Value>) -> Option<String> {
    cfg?;
    if let Some(end) = util::str_path(cfg, "currentPeriod", "end") {
        if !end.is_empty() {
            return Some(end);
        }
    }
    str_of(cfg, "billingPeriodEnd")
}

fn has_active_period(cfg: Option<&Value>, now: DateTime<Utc>) -> bool {
    if cfg.is_none() {
        return false;
    }

    let mut start = str_path(cfg, "currentPeriod", "start");
    if matches!(start.as_deref(), None | Some("")) {
        start = str_of(cfg, "billingPeriodStart");
    }
    let end = period_end(cfg);
    let blank = |s: &Option<String>| matches!(s.as_deref(), None | Some(""));
    if blank(&start) && blank(&end) {
        return false;
    }

    let start_ms = start.as_deref().and_then(parse_iso).map(util::to_epoch_ms);
    let end_ms = end.as_deref().and_then(parse_iso).map(util::to_epoch_ms);
    let now_ms = util::to_epoch_ms(now);

    match (start_ms, end_ms) {
        (Some(s), Some(e)) => s <= now_ms && now_ms < e,
        (None, Some(e)) => now_ms < e,
        (Some(s), None) => s <= now_ms,
        (None, None) => false,
    }
}

fn period_meta(cfg: Option<&Value>, from_credits_percent: bool) -> (&'static str, &'static str) {
    let period_type = str_of(map_of(cfg, "currentPeriod"), "type").unwrap_or_default().to_ascii_uppercase();

    if period_type.contains("WEEKLY") {
        return (kind::WEEKLY, "Weekly");
    }
    if period_type.contains("MONTHLY") {
        return (kind::MONTHLY, "Monthly");
    }
    if from_credits_percent {
        return (kind::WEEKLY, "Weekly");
    }
    if matches!(field_val(cfg, "monthlyLimit"), Some(l) if l > 0.0) {
        return (kind::MONTHLY, "Monthly");
    }
    (kind::WEEKLY, "Weekly")
}

// ----------------------------------------------------------------------------------------------
// grpc-web / protobuf parsing (hand written port of the same three functions in grok.js)
// ----------------------------------------------------------------------------------------------

/// Returns the credit usage percent carried by the response, or `None` when there is none.
pub fn parse_credits_grpc_web(frame: &[u8]) -> Option<f64> {
    if frame.is_empty() {
        return None;
    }
    let message = first_grpc_web_data_message(frame)?;

    let mut config: Option<Vec<u8>> = None;
    if !walk_proto(&message, &mut |field, wire, bytes| {
        if field == 1 && wire == 2 {
            config = Some(bytes.to_vec());
        }
    }) {
        return None;
    }
    let config = config.unwrap_or(message);

    let mut percent: Option<f64> = None;
    if !walk_proto(&config, &mut |field, wire, bytes| {
        if field != 1 {
            return;
        }
        if wire == 5 && bytes.len() == 4 {
            let mut b = [0u8; 4];
            b.copy_from_slice(bytes);
            percent = Some(f32::from_le_bytes(b) as f64);
        } else if wire == 1 && bytes.len() == 8 {
            let mut b = [0u8; 8];
            b.copy_from_slice(bytes);
            percent = Some(f64::from_le_bytes(b));
        }
    }) {
        return None;
    }

    percent.filter(|p| p.is_finite())
}

// A grpc-web body is a sequence of 5-byte headers (1 flag byte + 4 length bytes, big endian)
// followed by that many message bytes. The trailers ("grpc-status: 0") are skipped.
fn first_grpc_web_data_message(bytes: &[u8]) -> Option<Vec<u8>> {
    let mut offset = 0usize;
    while offset + 5 <= bytes.len() {
        let compressed = bytes[offset];
        let length = ((bytes[offset + 1] as u32) << 24)
            | ((bytes[offset + 2] as u32) << 16)
            | ((bytes[offset + 3] as u32) << 8)
            | (bytes[offset + 4] as u32);
        offset += 5;

        let end = offset as u64 + length as u64;
        if end > bytes.len() as u64 {
            return None;
        }
        let frame = &bytes[offset..end as usize];
        offset = end as usize;

        if compressed == 0x80 || compressed != 0 {
            continue;
        }
        if frame.len() >= 11 && &frame[..11] == b"grpc-status" {
            continue;
        }
        return Some(frame.to_vec());
    }
    None
}

// Walks the fields of a protobuf message. Wire 2 (length delimited), 5 (fixed32) and 1 (fixed64)
// hand over their bytes; wire 0 (varint) hands over an empty slice (the value is never used).
// Returns false as soon as a byte sequence is malformed: a truncated tag, a length that overruns
// the buffer (or a 32-bit int) or an unknown wire type, so the caller never maps a half decoded
// message.
fn walk_proto(buf: &[u8], on_field: &mut dyn FnMut(i32, i32, &[u8])) -> bool {
    let mut offset = 0usize;
    while offset < buf.len() {
        let (tag, next) = match read_varint(buf, offset) {
            Some(v) => v,
            None => return false,
        };
        let field = (tag >> 3) as i32;
        let wire = (tag & 7) as i32;
        offset = next;

        match wire {
            2 => {
                let (len, next) = match read_varint(buf, offset) {
                    Some(v) => v,
                    None => return false,
                };
                let end = next as u64 + len;
                if end > buf.len() as u64 {
                    return false;
                }
                on_field(field, wire, &buf[next..end as usize]);
                offset = end as usize;
            }
            5 => {
                if offset + 4 > buf.len() {
                    return false;
                }
                on_field(field, wire, &buf[offset..offset + 4]);
                offset += 4;
            }
            1 => {
                if offset + 8 > buf.len() {
                    return false;
                }
                on_field(field, wire, &buf[offset..offset + 8]);
                offset += 8;
            }
            0 => {
                // A wire-0 varint can be a full 64-bit value (up to ten bytes) — wider than the
                // 32 bits read_varint accepts for a tag or a length. Skip the bytes instead of
                // decoding them, so a large unused varint does not discard the message.
                let after = match skip_varint(buf, offset) {
                    Some(a) => a,
                    None => return false,
                };
                on_field(field, wire, &[]);
                offset = after;
            }
            _ => return false,
        }
    }
    true
}

// Base-128 varint, strictly bounded: at most five bytes (a 32-bit value is all a tag or a length
// needs) and no silent wrap to zero.
fn read_varint(buf: &[u8], offset: usize) -> Option<(u64, usize)> {
    let mut result: u64 = 0;
    let mut shift = 0;
    let mut pos = offset;
    while pos < buf.len() && shift <= 28 {
        let b = buf[pos];
        pos += 1;
        // The fifth byte contributes bits 28..34; anything above bit 31 cannot be a uint.
        if shift == 28 && (b & 0x70) != 0 {
            return None;
        }
        result |= ((b & 0x7f) as u64) << shift;
        if b & 0x80 == 0 {
            return Some((result, pos));
        }
        shift += 7;
    }
    None
}

// Skips a wire-0 (varint) value without decoding it: up to ten bytes, a longer unterminated
// sequence or running off the end of the buffer fails the parse.
fn skip_varint(buf: &[u8], offset: usize) -> Option<usize> {
    let mut pos = offset;
    let mut count = 0;
    while pos < buf.len() {
        let b = buf[pos];
        pos += 1;
        count += 1;
        if b & 0x80 == 0 {
            return Some(pos);
        }
        if count >= 10 {
            return None;
        }
    }
    None
}

// ----------------------------------------------------------------------------------------------
// fetch
// ----------------------------------------------------------------------------------------------

impl UsageAdapter for GrokAdapter {
    fn id(&self) -> &str {
        ADAPTER_ID
    }

    fn display_name(&self) -> &str {
        DISPLAY_NAME
    }

    fn fetch_usage(&self, ctx: &AdapterContext) -> ProviderSnapshot {
        let now = (ctx.utc_now)();
        let paths = &ctx.paths;
        let auth_path = paths.grok_auth();

        let creds_present = util::file_exists(&auth_path);
        let cli_on_path = paths.find_on_path("grok").is_some();

        if !creds_present && !cli_on_path {
            return empty(state::NOT_INSTALLED, None, Some("Install Grok Build, then run: grok login"), now);
        }
        if !creds_present {
            return empty(state::LOGGED_OUT, None, Some("Run: grok login"), now);
        }

        let mtime = util::mtime(&auth_path);
        let json = util::read_text(&auth_path)
            .ok()
            .and_then(|text| json_util::parse(Some(&text)))
            .and_then(|v| match v {
                Value::Object(m) => Some(m),
                _ => None,
            });
        let json = match json {
            Some(j) => j,
            None => return empty(state::LOGGED_OUT, None, Some("Run: grok login"), now),
        };

        let (top_key, entry) = match pick_entry(&json) {
            Some(p) => p,
            None => {
                return empty(
                    state::UNKNOWN,
                    Some("Grok is on an API key \u{2014} SuperGrok weekly % is a subscription meter"),
                    None,
                    now,
                )
            }
        };

        let entry_val = Value::Object(entry);
        let entry_ref = Some(&entry_val);
        let mut token = str_of(entry_ref, "key");
        let entry_key = token.clone();
        let mut refreshed = false;
        let refresh_enabled = ctx.config.refresh_tokens(ADAPTER_ID);
        let now_ms = util::to_epoch_ms(now);
        let exp_ms = expiry_ms(entry_ref, token.as_deref());

        if refresh_enabled {
            if let Some(exp) = exp_ms {
                if exp != 0.0 && exp - now_ms < REFRESH_SKEW_MS {
                    // keep the token we have on any failure
                    if let Ok(Some(next)) =
                        refresh_grok(ctx, entry_ref, &auth_path, mtime, Some(&top_key), entry_key.as_deref(), now)
                    {
                        token = Some(next);
                        refreshed = true;
                    }
                }
            }
        }

        let headers = cli_headers(token.as_deref(), entry_ref);
        let mut res = match get_json(&*ctx.http, BILLING_URL, &refs(&headers)) {
            Ok(r) => r,
            Err(e) => return util::fetch_failed(ADAPTER_ID, DISPLAY_NAME, &e, now),
        };

        // 401/403: rotate once, then retry (a token that was just refreshed is not rotated a
        // second time in the same pass).
        if (res.status == 401 || res.status == 403) && refresh_enabled && !refreshed {
            if let Ok(Some(next)) =
                refresh_grok(ctx, entry_ref, &auth_path, mtime, Some(&top_key), entry_key.as_deref(), now)
            {
                token = Some(next);
                let headers = cli_headers(token.as_deref(), entry_ref);
                if let Ok(r) = get_json(&*ctx.http, BILLING_URL, &refs(&headers)) {
                    res = r;
                }
            }
        }

        let status = res.status;
        if status == 401 || status == 403 {
            return empty(state::LOGGED_OUT, None, Some("Run grok login (token expired)"), now);
        }
        if status == 429 {
            return util::rate_limited(ADAPTER_ID, DISPLAY_NAME, now);
        }
        let mut json = match (res.json.take(), status) {
            (Some(j), 200) => j,
            _ => return empty(state::FETCH_FAILED, Some(&format!("HTTP {}", status)), None, now),
        };

        // The billing config (or the root object) gets the grpc-web percentage when REST has none.
        let billing_in_config = map_of(Some(&json), "config").is_some();
        let needs_credit = {
            let billing = if billing_in_config { map_of(Some(&json), "config") } else { Some(&json).filter(|v| v.is_object()) };
            billing.is_some() && weekly_pct(billing).is_none()
        };
        if needs_credit {
            // the REST period still maps when this fails
            if let Some(credit) = fetch_credits_grpc(ctx, token.as_deref()) {
                let target = if billing_in_config { json.get_mut("config") } else { Some(&mut json) };
                if let Some(Value::Object(map)) = target {
                    map.insert("creditUsagePercent".to_string(), f64_value(Some(credit)));
                }
            }
        }

        map_billing(&json, now)
    }
}

fn refs(headers: &[(String, String)]) -> Vec<(&str, &str)> {
    headers.iter().map(|(k, v)| (k.as_str(), v.as_str())).collect()
}

// pickEntry(): every top-level value that carries a "key" is an account; the newest create_time
// wins (string compare, exactly like localeCompare on ISO dates).
fn pick_entry(json: &Map<String, Value>) -> Option<(String, Map<String, Value>)> {
    let mut candidates: Vec<(&String, &Map<String, Value>)> = Vec::new();
    for (k, v) in json {
        let map = match v.as_object() {
            Some(m) => m,
            None => continue,
        };
        // JS: values.filter((v) => v && v.key) — a missing, non-string or empty key is not a
        // signed-in account, so it is never selected.
        match map.get("key").and_then(|v| v.as_str()) {
            Some(s) if !s.is_empty() => candidates.push((k, map)),
            _ => {}
        }
    }
    // Stable, descending by create_time (JS sort is stable too).
    candidates.sort_by(|a, b| create_time(b.1).cmp(create_time(a.1)));
    candidates.first().map(|(k, m)| ((*k).clone(), (*m).clone()))
}

fn create_time(entry: &Map<String, Value>) -> &str {
    entry.get("create_time").and_then(|v| v.as_str()).unwrap_or("")
}

fn expiry_ms(entry: Option<&Value>, token: Option<&str>) -> Option<f64> {
    let expires_at = str_of(entry, "expires_at");
    if let Some(parsed) = expires_at.as_deref().and_then(parse_iso) {
        return Some(util::to_epoch_ms(parsed));
    }
    jwt_exp_ms(token)
}

// A JWT's exp claim is the fallback when auth.json carries no expires_at.
fn jwt_exp_ms(token: Option<&str>) -> Option<f64> {
    let token = token.filter(|t| !t.is_empty())?;
    let parts: Vec<&str> = token.split('.').collect();
    if parts.len() < 2 {
        return None;
    }
    let payload = base64_url_decode(parts[1])?;
    let parsed = json_util::parse(Some(&payload))?;
    let exp = util::num(get(Some(&parsed), "exp"))?;
    if exp == 0.0 {
        return None;
    }
    Some(exp * 1000.0)
}

fn base64_url_decode(text: &str) -> Option<String> {
    if text.is_empty() {
        return None;
    }
    let mut chars: Vec<u8> = text
        .bytes()
        .filter(|b| !matches!(b, b' ' | b'\t' | b'\r' | b'\n'))
        .map(|b| match b {
            b'-' => b'+',
            b'_' => b'/',
            other => other,
        })
        .collect();
    match chars.len() % 4 {
        2 => chars.extend_from_slice(b"=="),
        3 => chars.push(b'='),
        _ => {}
    }
    if chars.len() % 4 != 0 {
        return None;
    }
    let mut out: Vec<u8> = Vec::with_capacity(chars.len() / 4 * 3);
    let value = |c: u8| -> Option<u32> {
        match c {
            b'A'..=b'Z' => Some((c - b'A') as u32),
            b'a'..=b'z' => Some((c - b'a') as u32 + 26),
            b'0'..=b'9' => Some((c - b'0') as u32 + 52),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    };
    let groups = chars.len() / 4;
    for (i, quad) in chars.chunks(4).enumerate() {
        let last = i + 1 == groups;
        let pad = quad.iter().rev().take_while(|c| **c == b'=').count();
        if pad > 2 || (pad > 0 && !last) {
            return None;
        }
        let mut n: u32 = 0;
        for (j, c) in quad.iter().enumerate() {
            let v = if j >= 4 - pad { 0 } else { value(*c)? };
            n = (n << 6) | v;
        }
        out.push((n >> 16) as u8);
        if pad < 2 {
            out.push((n >> 8) as u8);
        }
        if pad < 1 {
            out.push(n as u8);
        }
    }
    Some(String::from_utf8_lossy(&out).into_owned())
}

fn cli_headers(token: Option<&str>, entry: Option<&Value>) -> Vec<(String, String)> {
    let mut headers = vec![
        ("Authorization".to_string(), format!("Bearer {}", token.unwrap_or(""))),
        ("Accept".to_string(), "application/json".to_string()),
        ("X-XAI-Token-Auth".to_string(), "xai-grok-cli".to_string()),
        ("User-Agent".to_string(), "grok-cli/1.0".to_string()),
    ];
    if let Some(user_id) = str_of(entry, "user_id") {
        if !user_id.is_empty() {
            headers.push(("x-userid".to_string(), user_id));
        }
    }
    headers
}

// refreshGrok(): OIDC refresh grant, then a compare-and-swap write of the account we actually
// used (so a file the CLI rewrote in the meantime is never clobbered).
fn refresh_grok(
    ctx: &AdapterContext,
    entry: Option<&Value>,
    creds_path: &str,
    mtime: Option<SystemTime>,
    top_key: Option<&String>,
    entry_key: Option<&str>,
    now: DateTime<Utc>,
) -> Result<Option<String>, HttpError> {
    let mut issuer = str_of(entry, "oidc_issuer").filter(|s| !s.is_empty()).unwrap_or_else(|| DEFAULT_ISSUER.to_string());
    while issuer.ends_with('/') {
        issuer.pop();
    }

    let client_id = str_of(entry, "oidc_client_id").unwrap_or_default();
    let refresh_token = str_of(entry, "refresh_token").unwrap_or_default();
    if client_id.is_empty() || refresh_token.is_empty() {
        return Ok(None);
    }

    let mut token_url = format!("{}/oauth/token", issuer);
    // discovery failures use the default token endpoint
    if let Ok(disc) = get_json(&*ctx.http, &format!("{}/.well-known/openid-configuration", issuer), &[("Accept", "application/json")]) {
        if disc.status == 200 {
            if let Some(endpoint) = str_of(disc.json.as_ref(), "token_endpoint") {
                if !endpoint.is_empty() {
                    token_url = endpoint;
                }
            }
        }
    }

    let fields = [
        ("grant_type", "refresh_token"),
        ("refresh_token", refresh_token.as_str()),
        ("client_id", client_id.as_str()),
    ];
    let res = post_form(&*ctx.http, &token_url, &[("Accept", "application/json")], &fields)?;
    if res.status != 200 {
        return Ok(None);
    }
    let granted = match res.json.as_ref().filter(|v| v.is_object()) {
        Some(g) => g,
        None => return Ok(None),
    };
    let access = match str_of(Some(granted), "access_token") {
        Some(a) if !a.is_empty() => a,
        _ => return Ok(None),
    };

    let write = || -> std::io::Result<()> {
        use std::io::{Error, ErrorKind};
        if !util::file_exists(creds_path) || util::mtime(creds_path) != mtime {
            return Ok(());
        }

        let text = util::read_text(creds_path)?;
        let mut root = match json_util::parse(Some(&text)) {
            Some(Value::Object(m)) => m,
            _ => return Ok(()),
        };
        let current = match top_key.and_then(|k| root.get(k.as_str())).and_then(|v| v.as_object()) {
            Some(c) => c,
            None => return Ok(()),
        };

        // JS: if (!current || current.key !== entry.key) return access_token. The account we
        // refreshed must still be the one that sits at that key.
        if current.get("key").and_then(|v| v.as_str()) != entry_key {
            return Ok(());
        }

        let mut updated = current.clone();
        updated.insert("key".to_string(), Value::String(access.clone()));

        if let Some(rotated) = str_of(Some(granted), "refresh_token").filter(|s| !s.is_empty()) {
            updated.insert("refresh_token".to_string(), Value::String(rotated));
        }

        if let Some(expires_in) = util::num(get(Some(granted), "expires_in")).filter(|e| *e != 0.0) {
            let at = util::from_epoch_ms(util::to_epoch_ms(now) + expires_in * 1000.0)
                .ok_or_else(|| Error::new(ErrorKind::InvalidData, "expires_in out of range"))?;
            updated.insert("expires_at".to_string(), Value::String(to_iso(at)));
        }

        root.insert(top_key.cloned().unwrap_or_default(), Value::Object(updated));

        // One atomic replace: the destination is never deleted first, so a failing write in
        // between cannot leave the user's credentials missing. Written without a BOM.
        let tmp = format!("{}.tmp", creds_path);
        std::fs::write(&tmp, indented_json(&Value::Object(root)).as_bytes())?;

        // The check and the swap are kept as close together as the API allows: if the CLI
        // touched the file in the meantime we drop the temp copy instead.
        if util::mtime(creds_path) != mtime {
            util::delete_quietly(&tmp);
            return Ok(());
        }
        std::fs::rename(&tmp, creds_path)
    };
    if write().is_err() {
        // The in-memory token is still usable. Only remove the scratch copy when the credentials
        // file is still there.
        if util::file_exists(creds_path) {
            util::delete_quietly(&format!("{}.tmp", creds_path));
        }
    }

    Ok(Some(access))
}

fn fetch_credits_grpc(ctx: &AdapterContext, token: Option<&str>) -> Option<f64> {
    let authorization = format!("Bearer {}", token.unwrap_or(""));
    let headers = [
        ("Authorization", authorization.as_str()),
        ("Content-Type", "application/grpc-web+proto"),
        ("x-grpc-web", "1"),
        ("X-XAI-Token-Auth", "xai-grok-cli"),
        ("Accept", "application/grpc-web+proto"),
        ("Origin", "https://grok.com"),
        ("Referer", "https://grok.com/?_s=usage"),
        ("User-Agent", "grok-cli/1.0"),
    ];

    let res = ctx.http.request("POST", GRPC_CREDITS_URL, &headers, Some(&GRPC_REQUEST), GRPC_TIMEOUT_MS).ok()?;
    if res.status != 200 {
        return None;
    }
    if let Some(grpc_status) = res.headers.get("grpc-status") {
        if !grpc_status.is_empty() && grpc_status != "0" {
            return None;
        }
    }
    parse_credits_grpc_web(&res.raw)
}
