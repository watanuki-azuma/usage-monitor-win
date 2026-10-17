//! Presentation strings (port of src/Format.cs).
use chrono::{DateTime, Datelike, TimeZone, Timelike, Utc};

use super::contracts::{kind, state, ProviderSnapshot, UsageWindow};

const DAY_NAMES: [&str; 7] = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const EM_DASH: &str = "\u{2014}";

fn pad(n: i64) -> String {
    format!("{:02}", n)
}

/// Relative while it is nearer than a day ("45m", "1h 05m"), then an absolute clock time in `tz`
/// ("Wed 3:05 PM"). Minutes round half up like JavaScript's Math.round.
pub fn format_eta<Tz: TimeZone>(
    resets_at_utc: Option<DateTime<Utc>>,
    now_utc: DateTime<Utc>,
    tz: &Tz,
) -> String {
    let resets = match resets_at_utc {
        Some(r) => r,
        None => return String::new(),
    };
    let left = resets - now_utc;
    if left <= chrono::Duration::zero() {
        return "now".to_string();
    }

    let total_minutes = left.num_nanoseconds().map(|n| n as f64 / 6e10).unwrap_or(left.num_seconds() as f64 / 60.0);
    let minutes = total_minutes.round(); // away from zero
    if minutes < 24.0 * 60.0 {
        let total = minutes as i64;
        let h = total / 60;
        let m = total % 60;
        if h > 0 {
            return format!("{}h {}m", h, pad(m));
        }
        return format!("{}m", m);
    }

    let local = resets.with_timezone(tz);
    let mut hours = local.hour();
    let ampm = if hours >= 12 { "PM" } else { "AM" };
    hours %= 12;
    if hours == 0 {
        hours = 12;
    }
    format!(
        "{} {}:{} {}",
        DAY_NAMES[local.weekday().num_days_from_sunday() as usize],
        hours,
        pad(local.minute() as i64),
        ampm
    )
}

pub fn format_used_total(w: Option<&UsageWindow>, compact: bool) -> String {
    let used = match w.and_then(|w| w.used_pct) {
        Some(u) => u,
        None => {
            return if compact { EM_DASH.to_string() } else { format!("{}/100%", EM_DASH) };
        }
    };
    let used = used.round() as i64;
    format!("{}{}", used, if compact { "/100" } else { "/100%" })
}

/// A label that the taskbar row can show without the adult "Weekly" wording.
pub fn flyout_label(w: &UsageWindow) -> String {
    let label = w.label.as_str();
    let custom = !label.is_empty();
    if custom && label != "5h" && label != "Weekly" && label != "Daily" && label != "Credits" {
        return label.to_string();
    }
    if w.kind == kind::FIVE_HOUR {
        return "5h".to_string();
    }
    if w.kind == kind::WEEKLY {
        return "Wk".to_string();
    }
    if w.kind == kind::DAILY {
        return if custom { label.to_string() } else { "Day".to_string() };
    }
    if w.kind == kind::CREDITS {
        return "Credits".to_string();
    }
    label.to_string()
}

fn or_default(v: &Option<String>, default: &str) -> String {
    match v {
        Some(s) if !s.is_empty() => s.clone(),
        _ => default.to_string(),
    }
}

/// Status line for the flyout header; `None` while the provider is fine (ok/stale draw nothing).
pub fn status_text(p: &ProviderSnapshot) -> Option<String> {
    let s = &p.status;
    match s.state.as_str() {
        x if x == state::LOGGED_OUT => Some(or_default(&s.hint, "Sign in")),
        x if x == state::NOT_INSTALLED => Some(or_default(&s.hint, "Not installed")),
        x if x == state::FETCH_FAILED => Some(or_default(&s.message, "Error")),
        x if x == state::UNKNOWN => Some(or_default(&s.message, "n/a")),
        _ => None,
    }
}

/// What a stale chip shows; rate limiting adds the retry countdown when there is one.
pub fn stale_text<Tz: TimeZone>(p: &ProviderSnapshot, now_utc: DateTime<Utc>, tz: &Tz) -> String {
    let s = &p.status;
    if s.reason.as_deref() != Some("rate_limited") {
        return "stale".to_string();
    }
    let eta = format_eta(s.retry_at, now_utc, tz);
    if eta.is_empty() || eta == "now" {
        return "rate limited".to_string();
    }
    format!("rate limited \u{00B7} retry in {}", eta)
}
