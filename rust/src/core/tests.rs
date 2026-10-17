//! Ports of tests/M1_Core.cs (27 tests, same names in snake_case).
use chrono::{DateTime, Duration, FixedOffset, TimeZone, Utc};
use serde_json::{json, Value};

use super::contracts::{kind, state, ProviderSnapshot, ProviderStatus, Snapshot, UsageWindow};
use super::{app_config::AppConfig, format, models, paths::Paths, snapshot_json};

fn utc(y: i32, mo: u32, d: u32, h: u32, mi: u32, s: u32) -> DateTime<Utc> {
    Utc.with_ymd_and_hms(y, mo, d, h, mi, s).unwrap()
}

fn now() -> DateTime<Utc> {
    utc(2026, 10, 11, 12, 0, 0)
}

fn w(kind: &str, used: Option<f64>) -> UsageWindow {
    wl(kind, kind, used)
}

fn wl(kind: &str, label: &str, used: Option<f64>) -> UsageWindow {
    UsageWindow { kind: kind.to_string(), label: label.to_string(), used_pct: used, ..Default::default() }
}

fn provider(windows: Vec<UsageWindow>) -> ProviderSnapshot {
    let mut p = ProviderSnapshot::new("claude", now());
    p.display_name = Some("Claude Code".to_string());
    p.status = ProviderStatus { state: state::OK.to_string(), ..Default::default() };
    p.windows = windows;
    p.source = "api".to_string();
    p
}

mod models_tests {
    use super::*;

    #[test]
    fn percentage_rejects_invalid_values() {
        // NaN / Infinity cannot be expressed in JSON; serde_json turns them into null.
        let bad = vec![
            Value::Null,
            json!(f64::NAN),
            json!(f64::INFINITY),
            json!(f64::NEG_INFINITY),
            json!(""),
            json!(" "),
            json!("bad"),
            json!({}),
            json!(true),
            json!([]),
        ];
        for v in &bad {
            assert_eq!(None, models::percentage(v), "Percentage({})", v);
            assert_eq!(None, models::remaining_pct(v), "RemainingPct invalid");
        }
        // Strings that parse to non-finite numbers are rejected as well.
        assert_eq!(None, models::percentage(&json!("NaN")));
        assert_eq!(None, models::percentage(&json!("Infinity")));
    }

    #[test]
    fn percentage_accepts_numbers_and_numeric_strings() {
        assert_eq!(Some(12.5), models::percentage(&json!("12.5")), "string 12.5");
        assert_eq!(Some(150.0), models::percentage(&json!(150)), "int 150 preserved (overage)");
        assert_eq!(Some(0.0), models::percentage(&json!(-10)), "negative clamps to 0");
        assert_eq!(Some(42.0), models::percentage(&json!(42u64)), "long");
        assert_eq!(Some(7.25), models::percentage(&json!(7.25)), "decimal");
        assert_eq!(Some(0.0), models::percentage(&json!(0)), "zero");
        assert_eq!(Some(100.0), models::remaining_pct(&json!(0)), "remaining of 0");
        assert_eq!(Some(0.0), models::remaining_pct(&json!(150)), "remaining never negative");
        assert_eq!(Some(87.5), models::remaining_pct(&json!("12.5")), "remaining of 12.5");
    }

    #[test]
    fn window_of_normalises_used_remaining_and_reset() {
        let a = models::window_of(kind::DAILY, "Pro", &json!(-10), Some("invalid"));
        assert_eq!(Some(0.0), a.used_pct, "used clamped");
        assert_eq!(Some(100.0), a.remaining_pct, "remaining");
        assert_eq!(None, a.resets_at, "invalid reset -> null");
        assert_eq!("Pro", a.label, "label");
        assert_eq!(kind::DAILY, a.kind, "kind");

        let b = models::window_of(kind::FIVE_HOUR, "5h", &json!(150), Some("2030-01-01T00:00:00Z"));
        assert_eq!(Some(150.0), b.used_pct, "overage preserved");
        assert_eq!(Some(0.0), b.remaining_pct, "no negative remaining");
        assert_eq!(Some(utc(2030, 1, 1, 0, 0, 0)), b.resets_at, "reset parsed as UTC");

        let c = models::window_of(kind::WEEKLY, "Weekly", &Value::Null, Some("2030-01-01T09:00:00+09:00"));
        assert_eq!(None, c.used_pct, "null used stays null");
        assert_eq!(None, c.remaining_pct, "null remaining");
        assert_eq!(Some(utc(2030, 1, 1, 0, 0, 0)), c.resets_at, "offset converted to UTC");
        // "kind is Utc" holds by construction: the type is DateTime<Utc>.
    }

    #[test]
    fn empty_provider_shape() {
        let st = ProviderStatus {
            state: state::LOGGED_OUT.to_string(),
            hint: Some("Run: claude auth login".to_string()),
            ..Default::default()
        };
        let p = models::empty_provider("claude", "Claude Code", st, now());
        assert_eq!("claude", p.id, "id");
        assert_eq!(Some("Claude Code".to_string()), p.display_name, "name");
        assert_eq!(state::LOGGED_OUT, p.status.state, "state");
        assert_eq!(0, p.windows.len(), "no windows");
        assert_eq!(now(), p.fetched_at, "fetched at");
        assert_eq!("none", p.source, "source");
        assert_eq!(None, p.plan, "plan");
    }

    #[test]
    fn current_window_prefers_five_hour_then_daily_then_highest() {
        let is = |p: &ProviderSnapshot, idx: usize| -> bool {
            models::current_window(p).map_or(false, |c| std::ptr::eq(c, &p.windows[idx]))
        };
        let five = w(kind::FIVE_HOUR, Some(5.0));
        let weekly = w(kind::WEEKLY, Some(100.0));
        assert!(is(&provider(vec![five.clone(), weekly.clone()]), 0), "five before weekly");
        assert!(is(&provider(vec![weekly.clone(), five.clone()]), 1), "order independent");
        assert!(is(&provider(vec![w(kind::FIVE_HOUR, None), weekly.clone()]), 1), "unknown five skipped");
        assert!(models::current_window(&provider(vec![w(kind::DAILY, None)])).is_none(), "nothing numeric");

        let daily = w(kind::DAILY, Some(20.0));
        assert!(is(&provider(vec![weekly.clone(), daily]), 1), "daily fallback before weekly");
        assert!(is(&provider(vec![weekly]), 0), "weekly only");

        let lo = w(kind::FIVE_HOUR, Some(10.0));
        let hi = w(kind::FIVE_HOUR, Some(60.0));
        assert!(is(&provider(vec![lo, hi]), 1), "highest among preferred");
    }

    #[test]
    fn is_alerting_threshold_is_exactly_80() {
        assert!(models::is_alerting(&w(kind::DAILY, Some(80.0))), "80 alerts");
        assert!(!models::is_alerting(&w(kind::DAILY, Some(79.99))), "79.99 does not");
        assert!(!models::is_alerting(&w(kind::DAILY, None)), "unknown does not");
        assert!(models::is_alerting(&w(kind::DAILY, Some(100.0))), "100 alerts");
    }

    #[test]
    fn apply_local_resets_expires_windows_without_mutating_input() {
        let mut p = provider(vec![]);
        let expired = models::window_of(kind::DAILY, "Pro", &json!(99), Some("1970-01-01T00:00:00.000Z"));
        let bad_reset = models::window_of(kind::QUOTA, "Q", &json!(40), Some("bad"));
        let future = models::window_of(kind::FIVE_HOUR, "5h", &json!(30), Some("2099-01-01T00:00:00Z"));
        p.windows.push(expired);
        p.windows.push(bad_reset);
        p.windows.push(future);
        let mut s = Snapshot::new(now());
        s.providers.push(p.clone());

        let (r, changed) = models::apply_local_resets(&s, now());
        assert!(changed, "changed");
        let rp = &r.providers[0];
        assert_eq!(None, rp.windows[0].used_pct, "expired used null");
        assert_eq!(None, rp.windows[0].remaining_pct, "expired remaining null");
        assert_eq!(None, rp.windows[0].resets_at, "expired reset null");
        assert_eq!(kind::DAILY, rp.windows[0].kind, "kind kept");
        assert_eq!("Pro", rp.windows[0].label, "label kept");
        assert_eq!(Some(40.0), rp.windows[1].used_pct, "window without reset untouched");
        assert_eq!(Some(30.0), rp.windows[2].used_pct, "future window untouched");
        assert_eq!(state::STALE, rp.status.state, "provider becomes stale");

        assert_eq!(Some(99.0), s.providers[0].windows[0].used_pct, "input window not mutated");
        assert_eq!(state::OK, s.providers[0].status.state, "input status not mutated");
        assert!(s.providers[0].windows[0].resets_at.is_some(), "input reset kept");
        assert_eq!(p, s.providers[0]);

        let (_, again) = models::apply_local_resets(&r, now());
        assert!(!again, "second application is a no-op");
    }

    #[test]
    fn apply_local_resets_boundary_is_inclusive() {
        let p = provider(vec![models::window_of(kind::DAILY, "Pro", &json!(50), Some("2026-10-11T12:00:00Z"))]);
        let mut s = Snapshot::new(now());
        s.providers.push(p);
        let (r, changed) = models::apply_local_resets(&s, now());
        assert!(changed, "reset time equal to now counts as expired");
        assert_eq!(None, r.providers[0].windows[0].used_pct, "used null");
    }
}

mod format_tests {
    use super::*;

    fn tz_utc() -> Utc {
        Utc
    }

    #[test]
    fn format_used_total() {
        assert_eq!("—", format::format_used_total(None, true), "null compact");
        assert_eq!("—/100%", format::format_used_total(None, false), "null full");
        assert_eq!("—", format::format_used_total(Some(&wl(kind::DAILY, "x", None)), true), "unknown compact");
        assert_eq!("42/100", format::format_used_total(Some(&wl(kind::DAILY, "x", Some(42.4))), true), "compact");
        assert_eq!("42/100%", format::format_used_total(Some(&wl(kind::DAILY, "x", Some(42.4))), false), "full");
        assert_eq!("43/100", format::format_used_total(Some(&wl(kind::DAILY, "x", Some(42.6))), true), "rounds");
        assert_eq!("100/100%", format::format_used_total(Some(&wl(kind::DAILY, "x", Some(100.0))), false), "100");
    }

    #[test]
    fn format_eta_relative() {
        let n = now();
        let tz = tz_utc();
        let eta = |t: Option<DateTime<Utc>>| format::format_eta(t, n, &tz);
        assert_eq!("", eta(None), "null");
        assert_eq!("now", eta(Some(n - Duration::minutes(1))), "past");
        assert_eq!("now", eta(Some(n)), "equal");
        assert_eq!("45m", eta(Some(n + Duration::minutes(45))), "45m");
        assert_eq!("1h 30m", eta(Some(n + Duration::minutes(90))), "1h 30m");
        assert_eq!("2h 00m", eta(Some(n + Duration::hours(2))), "2h 00m");
        assert_eq!("1h 05m", eta(Some(n + Duration::minutes(65))), "minutes zero padded");
        assert_eq!("23h 59m", eta(Some(n + Duration::minutes(23 * 60 + 59))), "just under a day");
    }

    #[test]
    fn format_eta_rounds_half_up_like_javascript() {
        let n = now();
        let tz = tz_utc();
        let eta = |s: i64| format::format_eta(Some(n + Duration::seconds(s)), n, &tz);
        assert_eq!("1m", eta(30), "0.5 min -> 1m");
        assert_eq!("2m", eta(90), "1.5 min -> 2m");
        assert_eq!("1m", eta(89), "1.48 min -> 1m");
    }

    #[test]
    fn format_eta_absolute_for_a_day_or_more() {
        let n = now();
        let tz = tz_utc();
        assert_eq!("Wed 3:05 PM", format::format_eta(Some(utc(2026, 10, 14, 15, 5, 0)), n, &tz), "afternoon");
        assert_eq!("Wed 12:05 AM", format::format_eta(Some(utc(2026, 10, 14, 0, 5, 0)), n, &tz), "just after midnight");
        assert_eq!("Wed 12:30 PM", format::format_eta(Some(utc(2026, 10, 14, 12, 30, 0)), n, &tz), "noon hour");
        let jst = FixedOffset::east_opt(9 * 3600).unwrap();
        assert_eq!(
            "Thu 12:05 AM",
            format::format_eta(Some(utc(2026, 10, 14, 15, 5, 0)), n, &jst),
            "converted to the supplied zone"
        );
        assert_eq!("Mon 12:00 PM", format::format_eta(Some(n + Duration::days(1)), n, &tz), "exactly 24h is absolute");
    }

    #[test]
    fn flyout_label() {
        let l = |k: &str, label: &str, used: Option<f64>| format::flyout_label(&wl(k, label, used));
        assert_eq!("Pro", l(kind::DAILY, "Pro", Some(1.0)), "custom label wins");
        assert_eq!("5h", l(kind::FIVE_HOUR, "5h", Some(1.0)), "five hour");
        assert_eq!("Wk", l(kind::WEEKLY, "Weekly", Some(1.0)), "weekly");
        assert_eq!("Daily", l(kind::DAILY, "Daily", Some(1.0)), "daily keeps label");
        assert_eq!("Credits", l(kind::CREDITS, "Credits", None), "credits");
        assert_eq!("Weekly", l(kind::WEEKLY_SCOPED, "Weekly", Some(1.0)), "scoped falls back to label");
        assert_eq!("Opus weekly", l(kind::WEEKLY_SCOPED, "Opus weekly", Some(1.0)), "scoped custom");
    }

    fn p(st: &str, hint: Option<&str>, message: Option<&str>) -> ProviderSnapshot {
        let mut p = ProviderSnapshot::new("x", now());
        p.status = ProviderStatus {
            state: st.to_string(),
            hint: hint.map(|s| s.to_string()),
            message: message.map(|s| s.to_string()),
            ..Default::default()
        };
        p
    }

    #[test]
    fn status_text() {
        let t = |x: &ProviderSnapshot| format::status_text(x);
        assert_eq!(Some("Run: claude auth login".to_string()), t(&p(state::LOGGED_OUT, Some("Run: claude auth login"), None)), "logged out hint");
        assert_eq!(Some("Sign in".to_string()), t(&p(state::LOGGED_OUT, None, None)), "logged out default");
        assert_eq!(Some("Not installed".to_string()), t(&p(state::NOT_INSTALLED, None, None)), "not installed default");
        assert_eq!(Some("Install Codex".to_string()), t(&p(state::NOT_INSTALLED, Some("Install Codex"), None)), "not installed hint");
        assert_eq!(Some("HTTP 500".to_string()), t(&p(state::FETCH_FAILED, None, Some("HTTP 500"))), "failed message");
        assert_eq!(Some("Error".to_string()), t(&p(state::FETCH_FAILED, None, None)), "failed default");
        assert_eq!(Some("n/a".to_string()), t(&p(state::UNKNOWN, None, None)), "unknown default");
        assert_eq!(None, t(&p(state::OK, None, None)), "ok -> null");
        assert_eq!(None, t(&p(state::STALE, None, None)), "stale -> null");
    }

    #[test]
    fn stale_text() {
        let n = now();
        let tz = tz_utc();
        let mut x = p(state::STALE, None, None);
        assert_eq!("stale", format::stale_text(&x, n, &tz), "plain stale");
        x.status.reason = Some("rate_limited".to_string());
        assert_eq!("rate limited", format::stale_text(&x, n, &tz), "no retry time");
        x.status.retry_at = Some(n + Duration::minutes(90));
        assert_eq!("rate limited \u{00B7} retry in 1h 30m", format::stale_text(&x, n, &tz), "with retry");
        x.status.retry_at = Some(n - Duration::minutes(5));
        assert_eq!("rate limited", format::stale_text(&x, n, &tz), "retry in the past");
    }
}

mod snapshot_json_tests {
    use super::*;

    fn sample() -> String {
        concat!(
            "{\"generated_at\":\"2026-10-11T05:43:53.022Z\",\"poll_interval_secs\":60,\"providers\":[",
            "{\"id\":\"claude\",\"display_name\":\"Claude Code\",\"status\":{\"state\":\"ok\"},\"plan\":\"Pro\",\"windows\":[",
            "{\"kind\":\"five_hour\",\"label\":\"5h\",\"used_pct\":98,\"remaining_pct\":2,\"resets_at\":\"2026-10-11T08:19:59.648Z\"},",
            "{\"kind\":\"weekly\",\"label\":\"Weekly\",\"used_pct\":16.5,\"remaining_pct\":83.5,\"resets_at\":\"2026-10-17T16:59:59.648Z\"}],",
            "\"fetched_at\":\"2026-10-11T05:40:50.830Z\",\"source\":\"api\"},",
            "{\"id\":\"codex\",\"display_name\":\"Codex\",\"status\":{\"state\":\"stale\",\"age_secs\":12,\"reason\":\"rate_limited\",\"retry_at\":\"2026-10-11T06:00:00.000Z\"},",
            "\"plan\":\"Plus\",\"windows\":[{\"kind\":\"credits\",\"label\":\"Reset credits x1\",\"used_pct\":null,\"remaining_pct\":null,\"resets_at\":null}],",
            "\"footnotes\":[\"a & b\",\"c\"],\"fetched_at\":\"2026-10-11T05:43:50.982Z\",\"source\":\"cache\"}]}"
        )
        .to_string()
    }

    #[test]
    fn parse_reads_the_electron_format() {
        let s = snapshot_json::parse(Some(&sample())).expect("parsed");
        assert_eq!(60, s.poll_interval_secs, "interval");
        assert_eq!(utc(2026, 10, 11, 5, 43, 53) + Duration::milliseconds(22), s.generated_at, "generated_at");
        assert_eq!(2, s.providers.len(), "providers");
        let c = &s.providers[0];
        assert_eq!("claude", c.id, "id");
        assert_eq!(Some("Pro".to_string()), c.plan, "plan");
        assert_eq!(state::OK, c.status.state, "state");
        assert_eq!(2, c.windows.len(), "windows");
        assert_eq!(Some(98.0), c.windows[0].used_pct, "used");
        assert_eq!(Some(2.0), c.windows[0].remaining_pct, "remaining");
        assert_eq!(kind::FIVE_HOUR, c.windows[0].kind, "kind");
        assert_eq!(Some(16.5), c.windows[1].used_pct, "fractional used");
        assert_eq!("api", c.source, "source");
        let x = &s.providers[1];
        assert_eq!(state::STALE, x.status.state, "stale state");
        assert_eq!(Some("rate_limited".to_string()), x.status.reason, "reason");
        assert_eq!(Some(utc(2026, 10, 11, 6, 0, 0)), x.status.retry_at, "retry_at");
        assert_eq!(Some(12), x.status.age_secs, "age_secs");
        assert_eq!(None, x.windows[0].used_pct, "credits unknown used");
        assert_eq!(2, x.footnotes.len(), "footnotes");
    }

    #[test]
    fn parse_rejects_or_sanitises_bad_input() {
        assert!(snapshot_json::parse(None).is_none(), "null");
        assert!(snapshot_json::parse(Some("")).is_none(), "empty");
        assert!(snapshot_json::parse(Some("not json")).is_none(), "garbage");
        assert!(snapshot_json::parse(Some("{\"providers\":\"bad\"}")).is_none(), "providers not array");
        assert!(snapshot_json::parse(Some("[]")).is_none(), "array root");
        assert!(snapshot_json::parse(Some("{\"providers\":[]}")).is_none(), "no providers");

        let messy = concat!(
            "{\"providers\":[null,{\"id\":\"nope\",\"status\":{\"state\":\"ok\"},\"windows\":[],\"fetched_at\":\"2026-10-11T00:00:00Z\"},",
            "{\"id\":\"claude\",\"windows\":{}},",
            "{\"id\":\"claude\",\"status\":{\"state\":\"ok\"},\"windows\":[null,{\"kind\":\"daily\"},{\"label\":\"x\"},",
            "{\"kind\":\"daily\",\"label\":\"Pro\",\"used_pct\":\"bad\",\"resets_at\":\"bad\"},",
            "{\"kind\":\"weekly\",\"label\":\"Weekly\",\"used_pct\":\"12.5\"}],\"fetched_at\":\"2026-10-11T00:00:00Z\"},",
            "{\"id\":\"codex\",\"status\":{\"state\":\"ok\"},\"windows\":[],\"fetched_at\":\"garbage\"}]}"
        );
        let s = snapshot_json::parse(Some(messy)).expect("kept the one valid provider");
        assert_eq!(1, s.providers.len(), "only the valid provider remains");
        let p = &s.providers[0];
        assert_eq!(2, p.windows.len(), "invalid windows dropped");
        assert_eq!(None, p.windows[0].used_pct, "bad used -> null");
        assert_eq!(None, p.windows[0].resets_at, "bad reset -> null");
        assert_eq!(Some(12.5), p.windows[1].used_pct, "numeric string accepted");
    }

    #[test]
    fn serialize_roundtrip_and_key_names() {
        let s = snapshot_json::parse(Some(&sample())).unwrap();
        let json = snapshot_json::serialize(&s);
        for needle in [
            "\"generated_at\"",
            "\"poll_interval_secs\"",
            "\"display_name\"",
            "\"used_pct\"",
            "\"remaining_pct\"",
            "\"resets_at\": \"2026-10-11T08:19:59.648Z\"",
            "\"fetched_at\"",
        ] {
            assert!(json.contains(needle), "missing {}", needle);
        }
        assert!(!json.to_lowercase().contains("ratelimited"), "no in-memory fields leak");
        assert!(!json.to_lowercase().contains("_ratelimited"), "no _rateLimited");

        let again = snapshot_json::parse(Some(&json)).expect("reparse");
        assert_eq!(2, again.providers.len(), "providers");
        assert_eq!(Some(16.5), again.providers[0].windows[1].used_pct, "value survives");
        assert_eq!(s.providers[0].fetched_at, again.providers[0].fetched_at, "fetched_at survives");
        assert_eq!(Some("rate_limited".to_string()), again.providers[1].status.reason, "reason survives");
    }

    #[test]
    fn serialize_is_valid_for_usage_py() {
        let s = snapshot_json::parse(Some(&sample())).unwrap();
        let json = snapshot_json::serialize(&s);
        assert!(!json.contains("NaN"), "no NaN");
        assert!(!json.contains("\\/"), "no escaped slashes needed");
        assert!(json.contains("a & b"), "ampersand is not HTML-escaped");
        // Whole output is valid JSON.
        assert!(serde_json::from_str::<Value>(&json).is_ok());
    }
}

mod paths_tests {
    use super::*;
    use std::collections::HashMap;

    fn env(map: HashMap<String, String>) -> Paths {
        Paths::new(move |k| map.get(k).cloned())
    }

    fn base() -> HashMap<String, String> {
        let mut m = HashMap::new();
        m.insert("USERPROFILE".to_string(), r"C:\Users\u".to_string());
        m.insert("APPDATA".to_string(), r"C:\Users\u\AppData\Roaming".to_string());
        m.insert("LOCALAPPDATA".to_string(), r"C:\Users\u\AppData\Local".to_string());
        m
    }

    #[test]
    fn default_locations() {
        let p = env(base());
        assert_eq!(r"C:\Users\u", p.home(), "home");
        assert_eq!(r"C:\Users\u\.claude\.credentials.json", p.claude_credentials(), "claude");
        assert_eq!(r"C:\Users\u\.codex\auth.json", p.codex_auth(), "codex");
        assert_eq!(r"C:\Users\u\.grok\auth.json", p.grok_auth(), "grok");
        assert_eq!(r"C:\Users\u\.gemini\oauth_creds.json", p.gemini_oauth(), "gemini");
        assert_eq!(r"C:\Users\u\.gemini\antigravity-cli\antigravity-oauth-token", p.antigravity_token(), "antigravity");
        assert_eq!(r"C:\Users\u\AppData\Roaming\UsageMonitor", p.config_dir(), "config dir");
        assert_eq!(r"C:\Users\u\AppData\Roaming\UsageMonitor\config.json", p.config_path(), "config path");
        assert_eq!(r"C:\Users\u\AppData\Local\UsageMonitor\cache", p.cache_dir(), "cache dir");
        assert_eq!(r"C:\Users\u\AppData\Local\UsageMonitor\cache\snapshot.json", p.snapshot_path(), "snapshot path");
        assert_eq!(r"C:\Users\u\AppData\Local\UsageMonitor\cache\alert-state.json", p.alert_state_path(), "alert path");
        assert!(!p.agy_binary_candidates().is_empty(), "agy candidates");
        assert_eq!(r"C:\Users\u\AppData\Local\agy\bin\agy.exe", p.agy_binary_candidates()[0], "first agy candidate");
    }

    #[test]
    fn environment_overrides() {
        let mut m = base();
        m.insert("CLAUDE_CONFIG_DIR".to_string(), r"D:\cc".to_string());
        m.insert("CODEX_HOME".to_string(), r"D:\cx".to_string());
        m.insert("GROK_HOME".to_string(), r"D:\gk".to_string());
        m.insert("GEMINI_HOME".to_string(), r"D:\gm".to_string());
        m.insert("USAGE_MONITOR_DATA_DIR".to_string(), r"D:\data".to_string());
        let p = env(m);
        assert_eq!(r"D:\cc\.credentials.json", p.claude_credentials(), "claude override");
        assert_eq!(r"D:\cx\auth.json", p.codex_auth(), "codex override");
        assert_eq!(r"D:\gk\auth.json", p.grok_auth(), "grok override");
        assert_eq!(r"D:\gm\oauth_creds.json", p.gemini_oauth(), "gemini override");
        assert_eq!(r"D:\gm\antigravity-cli\antigravity-oauth-token", p.antigravity_token(), "antigravity follows gemini home");
        assert_eq!(r"D:\data", p.config_dir(), "data dir config");
        assert_eq!(r"D:\data", p.cache_dir(), "data dir cache");
        assert_eq!(r"D:\data\snapshot.json", p.snapshot_path(), "snapshot in data dir");
        assert_eq!(r"D:\data\config.json", p.config_path(), "config in data dir");
    }

    #[test]
    fn find_on_path_tries_extensions_in_order() {
        let dir = std::env::temp_dir().join(format!("um-r1-paths-{}-{}", std::process::id(), line!()));
        std::fs::create_dir_all(&dir).unwrap();
        let result = std::panic::catch_unwind(|| {
            let dir = dir.clone();
            std::fs::write(dir.join("tool.cmd"), "x").unwrap();
            std::fs::write(dir.join("bare"), "x").unwrap();
            let mut m = base();
            m.insert("PATH".to_string(), format!(r"C:\nonexistent;{}", dir.display()));
            let p = env(m);
            let s = |f: &str| Some(dir.join(f).to_string_lossy().into_owned());
            assert_eq!(s("tool.cmd"), p.find_on_path("tool"), "cmd found");
            assert_eq!(s("bare"), p.find_on_path("bare"), "bare name found");
            assert_eq!(None, p.find_on_path("missing"), "missing");
            std::fs::write(dir.join("tool.exe"), "x").unwrap();
            assert_eq!(s("tool.exe"), p.find_on_path("tool"), ".exe preferred over .cmd");
        });
        let _ = std::fs::remove_dir_all(&dir);
        if let Err(e) = result {
            std::panic::resume_unwind(e);
        }
    }
}

mod app_config_tests {
    use super::*;

    #[test]
    fn defaults() {
        let c = AppConfig::default();
        assert_eq!(5, c.poll_interval_secs, "interval");
        assert_eq!(5, c.config_version, "version");
        assert!(c.chips_docked, "docked default");
        assert!(!c.chips_hidden, "hidden default");
        assert!(c.autostart, "autostart default");
        assert!(c.notify_on_limit_reached, "notify default");
        assert_eq!(None, c.chips_x, "x default");
        assert!(c.refresh_tokens("claude"), "claude refresh default");
        assert_eq!(4, super::super::app_config::ALLOWED_INTERVALS.len(), "allowed intervals");
        assert_eq!("classic", c.skin, "skin default");
    }

    #[test]
    fn parse_is_tolerant() {
        let c = AppConfig::parse(Some("{\"poll_interval_secs\":0,\"chips_hidden\":\"false\",\"chips_x\":\"bad\",\"chips_docked\":false,\"adapters\":{\"claude\":null,\"grok\":{\"refresh_tokens\":false}}}"));
        assert_eq!(5, c.poll_interval_secs, "invalid interval -> 5");
        assert!(!c.chips_hidden, "string boolean -> default false");
        assert_eq!(None, c.chips_x, "bad coordinate -> null");
        assert!(!c.chips_docked, "explicit false kept");
        assert!(c.refresh_tokens("claude"), "null adapter -> true");
        assert!(!c.refresh_tokens("grok"), "explicit false kept");
        assert!(c.refresh_tokens("gemini"), "missing adapter -> true");

        assert_eq!(5, AppConfig::parse(None).poll_interval_secs, "null -> defaults");
        assert_eq!(5, AppConfig::parse(Some("not json")).poll_interval_secs, "garbage -> defaults");
        assert_eq!(5, AppConfig::parse(Some("[1,2]")).poll_interval_secs, "array -> defaults");
        assert_eq!(60, AppConfig::parse(Some("{\"poll_interval_secs\":60}")).poll_interval_secs, "60 allowed");
        assert_eq!(15, AppConfig::parse(Some("{\"poll_interval_secs\":\"15\"}")).poll_interval_secs, "numeric string allowed");
        assert_eq!(5, AppConfig::parse(Some("{\"poll_interval_secs\":7}")).poll_interval_secs, "7 not allowed");
    }

    #[test]
    fn parse_reads_coordinates_and_flags() {
        let c = AppConfig::parse(Some(concat!(
            "{\"chips_x\":89,\"chips_y\":1030,\"chips_dock_x\":62,\"chips_dock_y\":33,\"flyout_x\":1937,\"flyout_y\":1137,",
            "\"flyout_docked\":true,\"flyout_pinned\":true,\"autostart\":false,\"config_version\":4,\"notify_on_limit_reached\":false}"
        )));
        assert_eq!(Some(89), c.chips_x, "chips_x");
        assert_eq!(Some(1030), c.chips_y, "chips_y");
        assert_eq!(Some(62), c.chips_dock_x, "dock x");
        assert_eq!(Some(33), c.chips_dock_y, "dock y");
        assert_eq!(Some(1937), c.flyout_x, "flyout x");
        assert!(c.flyout_docked, "flyout docked");
        assert!(c.flyout_pinned, "flyout pinned");
        assert!(!c.autostart, "autostart false");
        assert!(!c.notify_on_limit_reached, "notify false");
        assert_eq!(4, c.config_version, "version carried");
    }

    #[test]
    fn parse_does_not_share_mutable_state() {
        let mut a = AppConfig::parse(Some("{\"adapters\":{\"claude\":null}}"));
        a.set_refresh_tokens("claude", false);
        a.poll_interval_secs = 60;
        let b = AppConfig::parse(None);
        assert!(b.refresh_tokens("claude"), "fresh object");
        assert_eq!(5, b.poll_interval_secs, "fresh interval");
        let d = AppConfig::default();
        assert!(d.refresh_tokens("claude"), "default object fresh");
    }

    #[test]
    fn to_json_roundtrip_and_key_order() {
        let mut c = AppConfig::default();
        c.poll_interval_secs = 30;
        c.chips_x = Some(10);
        c.set_refresh_tokens("gemini", false);
        let json = c.to_json();
        let i1 = json.find("\"poll_interval_secs\"").unwrap();
        let i2 = json.find("\"config_version\"").unwrap();
        let i3 = json.find("\"chips_docked\"").unwrap();
        let i4 = json.find("\"adapters\"").unwrap();
        assert!(i1 < i2 && i2 < i3 && i3 < i4, "key order");
        assert!(json.contains("\"chips_y\": null"), "null coordinates written as null");
        let d = AppConfig::parse(Some(&json));
        assert_eq!(30, d.poll_interval_secs, "interval roundtrip");
        assert_eq!(Some(10), d.chips_x, "x roundtrip");
        assert!(!d.refresh_tokens("gemini"), "adapter flag roundtrip");
        assert!(d.refresh_tokens("claude"), "other adapter untouched");
    }
}
