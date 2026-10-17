//! Alert engine (port of src/AlertEngine.cs): 80% crossing once per window key, 100% "limit
//! reached" once when enabled, only providers in state ok, re-arm below 80, prune expired entries.
use chrono::{DateTime, Duration, Utc};

use crate::core::contracts::{state, AlertEntry, AlertState, ProviderSnapshot, Snapshot, Toast, UsageWindow};
use crate::core::json_out::JsonWriter;
use crate::core::json_util as ju;
use crate::core::models::{self, ALERT_USED_PCT};

/// evaluate(): mutates `alert_state` (prune + crossed flags) and returns the toasts to show now.
pub fn evaluate(s: &Snapshot, alert_state: &mut AlertState, notify_on_limit: bool, now: DateTime<Utc>) -> Vec<Toast> {
    let mut toasts = Vec::new();
    prune(alert_state, now);

    for p in &s.providers {
        if p.status.state != state::OK {
            continue; // stale/logged_out/... never alert
        }
        for w in &p.windows {
            let used = match w.used_pct {
                Some(u) if u.is_finite() => u,
                _ => continue,
            };
            if matches!(w.resets_at, Some(r) if r <= now) {
                continue;
            }

            let key = key_for(p, w);
            let mut prev = alert_state.fired.get(&key).cloned().unwrap_or_default();

            if used >= ALERT_USED_PCT as f64 && !prev.crossed_80 && !(notify_on_limit && used >= 100.0) {
                toasts.push(toast(p, w, &format!("{}/100% used", percent_text(used))));
                prev.crossed_80 = true;
                prev.until = w.resets_at;
            }
            if notify_on_limit && used >= 100.0 && !prev.crossed_100 {
                toasts.push(toast(p, w, "limit reached"));
                prev.crossed_100 = true;
                prev.crossed_80 = true;
                prev.until = w.resets_at;
            }
            if used < ALERT_USED_PCT as f64 {
                prev.crossed_80 = false;
                prev.crossed_100 = false;
            }
            // Every tracked entry gets a deadline, so a window that never alerted is pruned as well
            // (an unknown reset is presumed to recycle within a week).
            prev.until = Some(w.resets_at.unwrap_or_else(|| now + Duration::days(7)));
            alert_state.fired.insert(key, prev);
        }
    }
    toasts
}

fn toast(p: &ProviderSnapshot, w: &UsageWindow, body: &str) -> Toast {
    Toast {
        title: format!("Usage Monitor \u{2014} {}", p.display_name.as_deref().unwrap_or("")),
        body: format!("{} {}", w.label, body),
    }
}

/// JavaScript Math.round: halves go away from zero.
fn percent_text(used: f64) -> String {
    format!("{}", used.round() as i64)
}

/// keyFor(): "<id>|<kind>|<label>|<resetMinute>" - the reset minute makes every period new.
fn key_for(p: &ProviderSnapshot, w: &UsageWindow) -> String {
    let resets = match w.resets_at {
        Some(r) => reset_minute(r).to_string(),
        None => "0".to_string(),
    };
    format!("{}|{}|{}|{}", p.id, w.kind, w.label, resets)
}

/// Minutes since the Unix epoch, floored like JavaScript's Math.floor(t / 60000).
fn reset_minute(resets_at: DateTime<Utc>) -> i64 {
    resets_at.timestamp_millis().div_euclid(60_000)
}

/// prune(): drop entries whose deadline has passed.
fn prune(alert_state: &mut AlertState, now: DateTime<Utc>) {
    alert_state.fired.retain(|_, e| !matches!(e.until, Some(u) if u <= now));
}

/// alert-state.json - tolerant like cache.js loadAlertState(): never fails, garbage ignored.
pub fn parse_state(json: Option<&str>) -> AlertState {
    let mut result = AlertState::default();
    let root = match ju::parse(json) {
        Some(r) => r,
        None => return result,
    };
    let root = match ju::as_map(&root) {
        Some(m) => m,
        None => return result,
    };
    let fired = match ju::get(root, "fired").and_then(ju::as_map) {
        Some(f) => f,
        None => return result,
    };
    for (key, value) in fired {
        let eo = match ju::as_map(value) {
            Some(m) => m,
            None => continue, // null / array / scalar entries are skipped
        };
        let entry = AlertEntry {
            crossed_80: ju::bool_of(ju::get(eo, "crossed80")).unwrap_or(false),
            crossed_100: ju::bool_of(ju::get(eo, "crossed100")).unwrap_or(false),
            until: ju::str_of(eo, "until").and_then(|t| models::parse_iso(&t)),
        };
        result.fired.insert(key.clone(), entry);
    }
    result
}

pub fn serialize_state(alert_state: &AlertState) -> String {
    let mut w = JsonWriter::new();
    w.begin_root_object();
    w.key("fired");
    w.begin_object();
    for (key, e) in alert_state.fired.iter() {
        w.key(key);
        w.begin_object();
        w.key("crossed80");
        w.bool(e.crossed_80);
        w.key("crossed100");
        w.bool(e.crossed_100);
        // undefined in JavaScript => the key is left out of the file entirely.
        if let Some(u) = e.until {
            w.key("until");
            w.string(Some(&models::to_iso(u)));
        }
        w.end_object();
    }
    w.end_object();
    w.end_object();
    w.into_string()
}
