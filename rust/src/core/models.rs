//! Core domain helpers (port of src/Models.cs).
use chrono::{DateTime, Duration, NaiveDate, Utc};
use serde_json::Value;

use super::contracts::{kind, state, ProviderSnapshot, ProviderStatus, Snapshot, UsageWindow};

pub const ALERT_USED_PCT: i32 = 80;

pub const PROVIDER_IDS: [&str; 4] = ["claude", "codex", "gemini", "grok"];

/// percentage(): numbers and numeric strings. Rejects null, bool, arrays/objects, blank and
/// non-numeric strings, NaN and +/-Infinity. Negatives clamp to 0; values above 100 are preserved.
pub fn percentage(value: &Value) -> Option<f64> {
    match value {
        Value::String(text) => percentage_of_string(text),
        Value::Number(n) => clamp_finite(n.as_f64()?),
        _ => None,
    }
}

fn clamp_finite(mut n: f64) -> Option<f64> {
    if n.is_nan() || n.is_infinite() {
        return None;
    }
    if n < 0.0 {
        n = 0.0;
    }
    Some(n)
}

fn percentage_of_string(text: &str) -> Option<f64> {
    // .NET NumberStyles.Float whitespace only (not Unicode spaces such as U+00A0)
    let t = text.trim_matches(|c: char| matches!(c, '\t'..='\r' | ' '));
    if t.is_empty() {
        return None;
    }
    let n: f64 = t.parse().ok()?;
    clamp_finite(n)
}

fn remaining_of(used: f64) -> f64 {
    let left = 100.0 - used;
    if left < 0.0 {
        0.0
    } else {
        left
    }
}

pub fn remaining_pct(used: &Value) -> Option<f64> {
    percentage(used).map(remaining_of)
}

/// windowOf(): normalises a raw provider window. Remaining is derived, never trusted.
pub fn window_of(kind: &str, label: &str, used_pct: &Value, resets_at: Option<&str>) -> UsageWindow {
    let used = percentage(used_pct);
    UsageWindow {
        kind: kind.to_string(),
        label: label.to_string(),
        used_pct: used,
        remaining_pct: used.map(remaining_of),
        resets_at: resets_at.and_then(parse_iso),
    }
}

pub fn empty_provider(id: &str, display_name: &str, status: ProviderStatus, now_utc: DateTime<Utc>) -> ProviderSnapshot {
    let mut p = ProviderSnapshot::new(id, now_utc);
    p.display_name = Some(display_name.to_string());
    p.status = status;
    p.plan = None;
    p.source = "none".to_string();
    p
}

fn window_rank(k: &str) -> i32 {
    if k == kind::FIVE_HOUR {
        1
    } else if k == kind::DAILY {
        2
    } else {
        3
    }
}

/// currentWindow(): five_hour wins, then daily, then anything; within the preferred group the
/// highest usage wins (the first of a tie). Returns a reference into `p.windows`.
pub fn current_window(p: &ProviderSnapshot) -> Option<&UsageWindow> {
    let mut best: Option<&UsageWindow> = None;
    let mut best_rank = 0;
    for w in &p.windows {
        let used = match w.used_pct {
            Some(u) => u,
            None => continue,
        };
        let rank = window_rank(&w.kind);
        match best {
            None => {
                best = Some(w);
                best_rank = rank;
            }
            Some(b) => {
                if rank < best_rank {
                    best = Some(w);
                    best_rank = rank;
                } else if rank == best_rank && used > b.used_pct.unwrap_or(0.0) {
                    best = Some(w);
                }
            }
        }
    }
    best
}

pub fn is_alerting(w: &UsageWindow) -> bool {
    matches!(w.used_pct, Some(u) if u >= ALERT_USED_PCT as f64)
}

/// applyLocalResets(): the 1 s UI tick. Never mutates the input. Returns (new snapshot, changed).
pub fn apply_local_resets(s: &Snapshot, now_utc: DateTime<Utc>) -> (Snapshot, bool) {
    let mut changed = false;
    let mut providers = Vec::with_capacity(s.providers.len());
    for p in &s.providers {
        let mut expired = false;
        let mut windows = Vec::with_capacity(p.windows.len());
        for w in &p.windows {
            match w.resets_at {
                Some(r) if r <= now_utc => {
                    expired = true;
                    let mut copy = w.clone();
                    copy.used_pct = None;
                    copy.remaining_pct = None;
                    copy.resets_at = None;
                    windows.push(copy);
                }
                _ => windows.push(w.clone()),
            }
        }
        let mut clone = p.clone();
        clone.windows = windows;
        if expired {
            clone.status.state = state::STALE.to_string();
            changed = true;
        }
        providers.push(clone);
    }
    let result = Snapshot {
        generated_at: s.generated_at,
        poll_interval_secs: s.poll_interval_secs,
        providers,
    };
    (result, changed)
}

fn digits(s: &[char], start: usize, count: usize) -> Option<i32> {
    if start + count > s.len() {
        return None;
    }
    let mut value = 0i32;
    for c in &s[start..start + count] {
        if !c.is_ascii_digit() {
            return None;
        }
        value = value * 10 + (*c as i32 - '0' as i32);
    }
    Some(value)
}

/// ISO-8601 parsing: "YYYY-MM-DD[T| ]HH:MM[:SS[.f...]][Z|+HH[:MM]|-HH:MM]" -> UTC, else `None`.
pub fn parse_iso(text: &str) -> Option<DateTime<Utc>> {
    let t = text.trim();
    let s: Vec<char> = t.chars().collect();
    if s.len() < 10 {
        return None;
    }

    let year = digits(&s, 0, 4)?;
    if s[4] != '-' {
        return None;
    }
    let month = digits(&s, 5, 2)?;
    if s[7] != '-' {
        return None;
    }
    let day = digits(&s, 8, 2)?;

    let (mut hour, mut minute, mut second, mut frac_ticks, mut offset_minutes) = (0, 0, 0, 0i64, 0i64);
    let mut pos = 10;
    if pos < s.len() {
        let sep = s[pos];
        if sep != 'T' && sep != 't' && sep != ' ' {
            return None;
        }
        pos += 1;
        hour = digits(&s, pos, 2)?;
        pos += 2;
        if pos < s.len() && s[pos] != ':' {
            return None;
        }
        pos += 1;
        minute = digits(&s, pos, 2)?;
        pos += 2;
        if pos < s.len() && s[pos] == ':' {
            pos += 1;
            second = digits(&s, pos, 2)?;
            pos += 2;
        }
        if pos < s.len() && s[pos] == '.' {
            pos += 1;
            let mut n = 0;
            let mut value = 0i64;
            while pos < s.len() {
                let c = s[pos];
                if !c.is_ascii_digit() {
                    break;
                }
                if n < 7 {
                    value = value * 10 + (c as i64 - '0' as i64);
                    n += 1;
                }
                pos += 1;
            }
            if n == 0 {
                return None;
            }
            while n < 7 {
                value *= 10;
                n += 1;
            }
            frac_ticks = value;
        }
        if pos < s.len() {
            let c = s[pos];
            pos += 1;
            let sign: i64 = match c {
                'Z' | 'z' => 0,
                '+' => 1,
                '-' => -1,
                _ => return None,
            };
            if sign != 0 {
                let oh = digits(&s, pos, 2)?;
                pos += 2;
                let mut om = 0;
                if pos < s.len() && s[pos] == ':' {
                    pos += 1;
                    if pos + 2 > s.len() {
                        return None;
                    }
                    om = digits(&s, pos, 2)?;
                    pos += 2;
                }
                offset_minutes = sign * (oh as i64 * 60 + om as i64);
            }
        }
        if pos != s.len() {
            return None;
        }
    }

    if year < 1 {
        return None;
    }
    let date = NaiveDate::from_ymd_opt(year, month as u32, day as u32)?;
    let naive = date.and_hms_opt(hour as u32, minute as u32, second as u32)?;
    let utc = naive
        .checked_sub_signed(Duration::minutes(offset_minutes))?
        .checked_add_signed(Duration::nanoseconds(frac_ticks * 100))?;
    // C# DateTime range: an offset that moves the instant outside years 1..=9999 is invalid
    if !(1..=9999).contains(&chrono::Datelike::year(&utc)) {
        return None;
    }
    Some(DateTime::<Utc>::from_naive_utc_and_offset(utc, Utc))
}

/// ISO-8601 with milliseconds and a trailing Z, exactly like JavaScript's toISOString().
pub fn to_iso(value: DateTime<Utc>) -> String {
    use chrono::Timelike;
    format!(
        "{}.{:03}Z",
        value.format("%Y-%m-%dT%H:%M:%S"),
        value.nanosecond() / 1_000_000 % 1000
    )
}
