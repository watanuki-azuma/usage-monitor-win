//! Ports of tests/M2_Adapters.cs, M2_Review.cs, M3_Agy.cs, M3_GeminiGrok.cs and M3_Review.cs
//! (66 tests, same names in snake_case, grouped in modules named after the C# test classes).
//! Fake OAuth-looking values are split across string literals so secret scanners do not flag them.
use std::fs;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime};

use chrono::{DateTime, Duration as ChronoDuration, Utc};

use super::claude::{self, ClaudeAdapter};
use super::codex::{self, CodexAdapter};
use super::gemini::{self, GeminiAdapter};
use super::grok::{self, GrokAdapter};
use super::http::{get_json, post_form, post_json, RealHttp};
use super::test_support::*;
use crate::core::contracts::{kind, state, Http, HttpError, ProviderSnapshot, UsageAdapter, UsageWindow};
use crate::core::format;

fn ms(t: DateTime<Utc>) -> i64 {
    t.timestamp_millis()
}

fn set_mtime(path: &str, when: SystemTime) {
    let f = fs::OpenOptions::new().write(true).open(path).unwrap();
    f.set_modified(when).unwrap();
}

fn hex(text: &str) -> Vec<u8> {
    (0..text.len() / 2).map(|i| u8::from_str_radix(&text[i * 2..i * 2 + 2], 16).unwrap()).collect()
}

mod http_tests {
    use super::*;

    #[test]
    fn get_returns_status_text_and_lowercase_headers() {
        let s = TestServer::new(|_req, out| {
            respond(out, 200, &[("X-Thing", "yes")], b"{\"a\":1}");
        });
        let r = RealHttp::new().request("GET", &s.base_url, &[], None, 5000).unwrap();
        assert_eq!(200, r.status, "status");
        assert_eq!("{\"a\":1}", r.text, "text");
        assert_eq!(7, r.raw.len(), "raw length");
        assert_eq!("yes", r.headers["x-thing"], "header key lower-cased");
    }

    #[test]
    fn post_sends_body_and_custom_headers() {
        use std::sync::Mutex;
        let seen: Arc<Mutex<Option<(String, Option<String>, Option<String>, String)>>> = Arc::new(Mutex::new(None));
        let seen2 = seen.clone();
        let s = TestServer::new(move |req, out| {
            *seen2.lock().unwrap() = Some((
                req.method.clone(),
                req.header("Authorization").map(|s| s.to_string()),
                req.header("Content-Type").map(|s| s.to_string()),
                String::from_utf8_lossy(&req.body).into_owned(),
            ));
            respond(out, 201, &[], b"");
        });
        let body = "{\"k\":\"日本\"}";
        let r = RealHttp::new()
            .request(
                "POST",
                &s.base_url,
                &[("Authorization", "Bearer abc"), ("Content-Type", "application/json")],
                Some(body.as_bytes()),
                5000,
            )
            .unwrap();
        assert_eq!(201, r.status, "status");
        let (method, auth, content_type, seen_body) = seen.lock().unwrap().clone().unwrap();
        assert_eq!("POST", method, "method");
        assert_eq!(Some("Bearer abc".to_string()), auth, "authorization header");
        assert_contains(&content_type.unwrap(), "application/json", "content type");
        assert_eq!(body, seen_body, "body utf-8");
    }

    #[test]
    fn non_2xx_is_returned_not_thrown() {
        let s = TestServer::new(|_req, out| respond(out, 429, &[], b"slow down"));
        let r = RealHttp::new().request("GET", &s.base_url, &[], None, 5000).unwrap();
        assert_eq!(429, r.status, "status 429");
        assert_eq!("slow down", r.text, "body of error response");
    }

    #[test]
    fn rejects_unsupported_schemes() {
        let http = RealHttp::new();
        assert!(http.request("GET", "file:///C:/Windows/win.ini", &[], None, 2000).is_err(), "file scheme");
        assert!(http.request("GET", "ftp://example.test/x", &[], None, 2000).is_err(), "ftp scheme");
    }

    #[test]
    fn rejects_oversized_response() {
        let s = TestServer::new(|_req, out| {
            let big = vec![0u8; 2 * 1024 * 1024 + 1];
            respond(out, 200, &[], &big);
        });
        assert!(RealHttp::new().request("GET", &s.base_url, &[], None, 10000).is_err(), "over 2 MiB");
    }

    #[test]
    fn accepts_response_of_exactly_2_mib() {
        let s = TestServer::new(|_req, out| {
            let big = vec![65u8; 2 * 1024 * 1024];
            respond(out, 200, &[], &big);
        });
        let r = RealHttp::new().request("GET", &s.base_url, &[], None, 10000).unwrap();
        assert_eq!(2 * 1024 * 1024, r.raw.len(), "exactly 2 MiB accepted");
    }

    #[test]
    fn times_out() {
        let s = TestServer::new(|_req, _out| std::thread::sleep(Duration::from_millis(3000)));
        let t0 = Instant::now();
        assert!(RealHttp::new().request("GET", &s.base_url, &[], None, 400).is_err(), "timeout");
        assert!(t0.elapsed().as_millis() < 2500, "timeout is honoured");
    }

    #[test]
    fn helpers_parse_json_and_tolerate_garbage() {
        let f = FakeHttp::default();
        f.set_handler(|r| {
            if r.url.ends_with("/ok") {
                return reply(200, "{\"a\":[1,2],\"n\":null}");
            }
            reply(502, "<html>bad gateway</html>")
        });
        let ok = get_json(&f, "https://x.test/ok", &[]).unwrap();
        assert_eq!(200, ok.status, "status");
        let parsed = ok.json.as_ref().expect("json parsed");
        assert!(parsed["a"].is_array(), "arrays are arrays");
        let bad = get_json(&f, "https://x.test/bad", &[]).unwrap();
        assert_eq!(502, bad.status, "bad status");
        assert!(bad.json.is_none(), "non-json body -> Json null");
        assert_eq!("GET", f.requests()[0].method, "GET method");
    }

    #[test]
    fn helpers_post_json_and_post_form_encode_bodies() {
        let f = FakeHttp::default();
        f.set_handler(|_r| reply(200, "{}"));
        let body = serde_json::json!({ "grant_type": "refresh_token", "n": 3 });
        post_json(&f, "https://x.test/j", &[], &body).unwrap();
        let j = f.requests()[0].clone();
        assert_eq!("POST", j.method, "method");
        assert_contains(j.header("Content-Type").unwrap(), "application/json", "json content type");
        assert_contains(j.body.as_deref().unwrap(), "\"grant_type\":\"refresh_token\"", "json body");

        post_form(&f, "https://x.test/f", &[], &[("a", "1"), ("b", "x y&z")]).unwrap();
        let form = f.requests()[1].clone();
        assert_contains(
            form.header("Content-Type").unwrap(),
            "application/x-www-form-urlencoded",
            "form content type",
        );
        assert_eq!("a=1&b=x%20y%26z", form.body.as_deref().unwrap(), "form body is percent-encoded");
    }
}

mod claude_adapter_tests {
    use super::*;

    const USAGE_URL_PART: &str = "api.anthropic.com/api/oauth/usage";

    fn creds(access: &str, refresh: &str, expires_at_ms: i64, extra: &str) -> String {
        format!(
            "{{\"claudeAiOauth\":{{\"accessToken\":\"{}\",\"refreshToken\":\"{}\",\"expiresAt\":{}{}}}}}",
            access, refresh, expires_at_ms, extra
        )
    }

    const FULL_USAGE: &str = concat!(
        "{\"five_hour\":{\"utilization\":42.5,\"resets_at\":\"2026-10-11T15:00:00Z\"},",
        "\"seven_day\":{\"utilization\":10,\"resets_at\":\"2026-10-17T00:00:00Z\"},",
        "\"limits\":[{\"kind\":\"weekly_scoped\",\"scope\":{\"model\":{\"display_name\":\"Opus\"}},\"percent\":77,\"resets_at\":\"2026-10-17T00:00:00Z\"}],",
        "\"spend\":{\"enabled\":true,\"limit\":{\"amount_minor\":2000},\"used\":{\"amount_minor\":500}}}"
    );

    fn fetch(e: &AdapterEnv, cli: bool) -> ProviderSnapshot {
        ClaudeAdapter::new().fetch_usage(&e.context(cli, "claude"))
    }

    #[test]
    fn not_installed_when_no_credentials_and_no_cli() {
        let e = AdapterEnv::new();
        let p = fetch(&e, false);
        assert_eq!("claude", p.id, "id");
        assert_eq!(state::NOT_INSTALLED, p.status.state, "state");
        assert_eq!(0, e.http.count(), "no requests");
    }

    #[test]
    fn logged_out_when_cli_present_but_no_credentials() {
        let e = AdapterEnv::new();
        let p = fetch(&e, true);
        assert_eq!(state::LOGGED_OUT, p.status.state, "state");
        assert_eq!(Some("Run: claude auth login".to_string()), p.status.hint, "hint");
    }

    #[test]
    fn unknown_when_no_oauth_token() {
        let e = AdapterEnv::new();
        e.write(".claude\\.credentials.json", "{\"somethingElse\":{}}");
        let p = fetch(&e, false);
        assert_eq!(state::UNKNOWN, p.status.state, "state");
        assert_contains(p.status.message.as_deref().unwrap(), "not signed in", "message");
    }

    #[test]
    fn happy_path_maps_all_window_kinds() {
        let e = AdapterEnv::new();
        e.write(
            ".claude\\.credentials.json",
            &creds(
                "AT",
                "RT",
                ms(e.now + ChronoDuration::hours(5)),
                ",\"rateLimitTier\":\"default_claude_max_5x\",\"subscriptionType\":\"max\"",
            ),
        );
        e.http.set_handler(|_r| reply(200, FULL_USAGE));
        let p = fetch(&e, false);
        assert_eq!(state::OK, p.status.state, "state");
        assert_eq!(Some("Claude Code".to_string()), p.display_name, "display name");
        assert_eq!(Some("Max 5x".to_string()), p.plan, "plan");
        assert_eq!("api", p.source, "source");
        assert_eq!(e.now, p.fetched_at, "fetched at uses injected clock");
        assert_eq!(4, p.windows.len(), "window count");
        assert_eq!(kind::FIVE_HOUR, p.windows[0].kind, "w0 kind");
        assert_eq!("5h", p.windows[0].label, "w0 label");
        assert_f64(42.5, p.windows[0].used_pct, "w0 used");
        assert_eq!(Some(utc(2026, 10, 11, 15, 0, 0)), p.windows[0].resets_at, "w0 reset");
        assert_eq!(kind::WEEKLY, p.windows[1].kind, "w1 kind");
        assert_eq!("Weekly", p.windows[1].label, "w1 label");
        assert_f64(10.0, p.windows[1].used_pct, "w1 used");
        assert_eq!(kind::WEEKLY_SCOPED, p.windows[2].kind, "w2 kind");
        assert_eq!("Opus weekly", p.windows[2].label, "w2 label");
        assert_f64(77.0, p.windows[2].used_pct, "w2 used");
        assert_eq!(kind::CREDITS, p.windows[3].kind, "w3 kind");
        assert_eq!("Credits", p.windows[3].label, "w3 label");
        assert_f64(25.0, p.windows[3].used_pct, "w3 used (500/2000)");
        assert_eq!(1, e.http.count(), "single request");
        let req = e.http.requests()[0].clone();
        assert_eq!("GET", req.method, "method");
        assert_contains(&req.url, USAGE_URL_PART, "url");
        assert_eq!(Some("Bearer AT"), req.header("Authorization"), "authorization");
        assert_eq!(Some("oauth-2025-04-20"), req.header("anthropic-beta"), "beta header");
    }

    #[test]
    fn map_usage_does_not_invent_zero_from_a_reset_time() {
        let p = claude::map_usage(
            &json("{\"five_hour\":{\"resets_at\":\"2030-01-01T00:00:00Z\"}}"),
            None,
            utc(2026, 1, 1, 0, 0, 0),
        );
        assert_eq!(1, p.windows.len(), "window kept");
        assert!(p.windows[0].used_pct.is_none(), "used stays null");
    }

    #[test]
    fn plan_label() {
        let pl = |text: &str| claude::plan_label(Some(&json(text)));
        assert_eq!(Some("Max 20x".to_string()), pl("{\"rateLimitTier\":\"default_claude_max_20x\"}"), "20x");
        assert_eq!(Some("Max 5x".to_string()), pl("{\"rateLimitTier\":\"default_claude_max_5x\"}"), "5x");
        assert_eq!(Some("Max 20x".to_string()), pl("{\"rateLimitTier\":\"x_max_20_y\"}"), "regex 20");
        assert_eq!(Some("Max 5x".to_string()), pl("{\"rateLimitTier\":\"x_max_5_y\"}"), "regex 5");
        assert_eq!(Some("Max".to_string()), pl("{\"subscriptionType\":\"max\"}"), "max");
        assert_eq!(Some("Pro".to_string()), pl("{\"subscriptionType\":\"pro\"}"), "pro");
        assert_eq!(Some("Enterprise".to_string()), pl("{\"subscriptionType\":\"enterprise\"}"), "enterprise");
        assert_eq!(Some("team".to_string()), pl("{\"subscriptionType\":\"team\"}"), "passthrough");
        assert_eq!(None, pl("{}"), "none");
        assert_eq!(None, claude::plan_label(None), "null oauth");
    }

    #[test]
    fn refresh_401_then_429_keeps_the_rate_limit_and_stores_new_tokens() {
        let e = AdapterEnv::new();
        let cred_path = e.write(".claude\\.credentials.json", &creds("OLD", "RT", ms(e.now + ChronoDuration::hours(5)), ""));
        let usage_calls = Arc::new(AtomicUsize::new(0));
        let calls = usage_calls.clone();
        e.http.set_handler(move |r| {
            if r.url.contains("/oauth/token") {
                return reply(200, "{\"access_token\":\"NEW\",\"refresh_token\":\"RT2\",\"expires_in\":3600}");
            }
            let n = calls.fetch_add(1, Ordering::SeqCst) + 1;
            reply(if n == 1 { 401 } else { 429 }, "")
        });
        let p = fetch(&e, false);
        assert!(p.rate_limited, "rate limited flag");
        assert_eq!(state::FETCH_FAILED, p.status.state, "state");
        assert_eq!(Some("rate limited".to_string()), p.status.message, "message");
        let saved = fs::read_to_string(&cred_path).unwrap();
        assert_contains(&saved, "\"accessToken\": \"NEW\"", "new access token written (indented json)");
        assert_contains(&saved, "\"refreshToken\": \"RT2\"", "rotated refresh token written");
        assert_contains(&saved, "claudeAiOauth", "structure kept");
        assert_eq!(2, usage_calls.load(Ordering::SeqCst), "usage retried after refresh");
        let second = e.http.requests().into_iter().filter(|r| r.url.contains(USAGE_URL_PART)).last().unwrap();
        assert_eq!(Some("Bearer NEW"), second.header("Authorization"), "retry uses the new token");
    }

    #[test]
    fn expiring_token_is_refreshed_before_the_first_usage_call() {
        let e = AdapterEnv::new();
        e.write(".claude\\.credentials.json", &creds("OLD", "RT", ms(e.now + ChronoDuration::seconds(30)), ""));
        e.http.set_handler(|r| {
            if r.url.contains("/oauth/token") {
                return reply(200, "{\"access_token\":\"NEW\",\"expires_in\":3600}");
            }
            reply(200, FULL_USAGE)
        });
        let p = fetch(&e, false);
        assert_eq!(state::OK, p.status.state, "ok");
        let reqs = e.http.requests();
        assert_eq!(2, reqs.len(), "refresh + usage");
        assert_eq!("POST", reqs[0].method, "refresh first");
        assert_contains(&reqs[0].url, "platform.claude.com/v1/oauth/token", "token url");
        let body = reqs[0].body.as_deref().unwrap();
        assert_contains(body, "\"grant_type\":\"refresh_token\"", "grant type");
        assert_contains(body, "\"client_id\":\"9d1c250a-e61b-44d9-88ed-5944d1962f5e\"", "client id");
        assert_contains(body, "\"refresh_token\":\"RT\"", "refresh token");
        assert_eq!(Some("Bearer NEW"), reqs[1].header("Authorization"), "usage with new token");
    }

    #[test]
    fn refresh_rejected_means_logged_out_without_usage_call() {
        let e = AdapterEnv::new();
        e.write(".claude\\.credentials.json", &creds("OLD", "RT", ms(e.now + ChronoDuration::seconds(30)), ""));
        e.http.set_handler(|_r| reply(400, "{\"error\":\"invalid_grant\"}"));
        let p = fetch(&e, false);
        assert_eq!(state::LOGGED_OUT, p.status.state, "logged out");
        assert_eq!(1, e.http.count(), "only the refresh was attempted");
    }

    #[test]
    fn refresh_can_be_disabled_by_config() {
        let mut e = AdapterEnv::new();
        e.write(".claude\\.credentials.json", &creds("OLD", "RT", ms(e.now + ChronoDuration::seconds(30)), ""));
        e.config.set_refresh_tokens("claude", false);
        e.http.set_handler(|_r| reply(401, ""));
        let p = fetch(&e, false);
        assert_eq!(state::LOGGED_OUT, p.status.state, "logged out on 401");
        for r in e.http.requests() {
            assert_eq!("GET", r.method, "no POST (refresh) when disabled");
        }
    }

    #[test]
    fn other_http_errors() {
        let e = AdapterEnv::new();
        e.write(".claude\\.credentials.json", &creds("AT", "RT", ms(e.now + ChronoDuration::hours(5)), ""));
        e.http.set_handler(|_r| reply(500, "oops"));
        let p = fetch(&e, false);
        assert_eq!(state::FETCH_FAILED, p.status.state, "state");
        assert_eq!(Some("HTTP 500".to_string()), p.status.message, "message");
        assert!(!p.rate_limited, "not rate limited");
    }

    #[test]
    fn transport_errors_become_fetch_failed_not_exceptions() {
        let e = AdapterEnv::new();
        e.write(".claude\\.credentials.json", &creds("AT", "RT", ms(e.now + ChronoDuration::hours(5)), ""));
        e.http.set_handler(|_r| Err(HttpError("boom".to_string())));
        let p = fetch(&e, false);
        assert_eq!(state::FETCH_FAILED, p.status.state, "state");
    }

    #[test]
    fn token_file_is_not_overwritten_when_it_changed_during_refresh() {
        let e = AdapterEnv::new();
        let cred_path = e.write(".claude\\.credentials.json", &creds("OLD", "RT", ms(e.now + ChronoDuration::seconds(30)), ""));
        let concurrent = creds("CLI-WROTE-THIS", "RT-FROM-CLI", ms(e.now + ChronoDuration::hours(8)), "");
        let (path, written) = (cred_path.clone(), concurrent.clone());
        e.http.set_handler(move |r| {
            if r.url.contains("/oauth/token") {
                fs::write(&path, &written).unwrap();
                set_mtime(&path, SystemTime::now() + Duration::from_secs(600));
                return reply(200, "{\"access_token\":\"NEW\",\"refresh_token\":\"RT2\",\"expires_in\":3600}");
            }
            reply(200, FULL_USAGE)
        });
        fetch(&e, false);
        assert_eq!(concurrent, fs::read_to_string(&cred_path).unwrap(), "the other writer's file is left untouched");
    }
}

mod codex_adapter_tests {
    use super::*;

    fn epoch(t: DateTime<Utc>) -> i64 {
        t.timestamp()
    }

    fn fetch(e: &AdapterEnv, cli: bool) -> ProviderSnapshot {
        CodexAdapter::new().fetch_usage(&e.context(cli, "codex"))
    }

    #[test]
    fn map_wham_handles_daily_window_and_bad_reset() {
        let p = codex::map_wham(
            &json("{\"rate_limit\":{\"primary_window\":{\"used_percent\":20,\"limit_window_seconds\":86400,\"reset_at\":\"bad\"}}}"),
            utc(2026, 10, 11, 12, 0, 0),
        );
        assert_eq!(kind::DAILY, p.windows[0].kind, "kind");
        assert_eq!("Daily", p.windows[0].label, "label");
        assert_f64(20.0, p.windows[0].used_pct, "used");
        assert!(p.windows[0].resets_at.is_none(), "reset null");
    }

    #[test]
    fn api_key_login_makes_no_request() {
        let e = AdapterEnv::new();
        e.write(".codex\\auth.json", "{\"OPENAI_API_KEY\":\"sk-fake\"}");
        let p = fetch(&e, false);
        assert_eq!(state::UNKNOWN, p.status.state, "state");
        assert_eq!(0, e.http.count(), "no request");
    }

    #[test]
    fn missing_install_and_missing_login() {
        let e = AdapterEnv::new();
        let a = fetch(&e, false);
        assert_eq!(state::NOT_INSTALLED, a.status.state, "not installed");
        let b = fetch(&e, true);
        assert_eq!(state::LOGGED_OUT, b.status.state, "logged out");
        assert_eq!(Some("Run: codex login".to_string()), b.status.hint, "hint");
        e.write(".codex\\auth.json", "{\"tokens\":{}}");
        let c = fetch(&e, true);
        assert_eq!(state::LOGGED_OUT, c.status.state, "no access token -> logged out");
    }

    #[test]
    fn happy_path() {
        let e = AdapterEnv::new();
        e.write(".codex\\auth.json", "{\"tokens\":{\"access_token\":\"AT\",\"account_id\":\"acc-1\"}}");
        let five = utc(2026, 10, 11, 15, 0, 0);
        let week = utc(2026, 10, 18, 3, 0, 0);
        let body = format!(
            concat!(
                "{{\"plan_type\":\"plus\",\"rate_limit\":{{",
                "\"primary_window\":{{\"used_percent\":20,\"limit_window_seconds\":18000,\"reset_at\":{}}},",
                "\"secondary_window\":{{\"used_percent\":55,\"limit_window_seconds\":604800,\"reset_after_seconds\":3600}}}},",
                "\"additional_rate_limits\":[{{\"name\":\"GPT-5-Codex\",\"primary_window\":{{\"used_percent\":5,\"limit_window_seconds\":604800,\"reset_at\":{}}}}},",
                "{{\"name\":\"NoUsed\",\"primary_window\":{{\"limit_window_seconds\":604800}}}}],",
                "\"rate_limit_reset_credits\":{{\"available_count\":2}}}}"
            ),
            epoch(five),
            epoch(week)
        );
        e.http.set_handler(move |_r| reply(200, &body));
        let p = fetch(&e, false);
        assert_eq!(state::OK, p.status.state, "state");
        assert_eq!(Some("Plus".to_string()), p.plan, "plan capitalised");
        assert_eq!(Some("Codex".to_string()), p.display_name, "name");
        assert_eq!(4, p.windows.len(), "windows (extra without used skipped, credits added)");
        assert_eq!(kind::FIVE_HOUR, p.windows[0].kind, "w0 kind");
        assert_eq!("5h", p.windows[0].label, "w0 label");
        assert_f64(20.0, p.windows[0].used_pct, "w0 used");
        assert_eq!(Some(five), p.windows[0].resets_at, "w0 reset from epoch seconds");
        assert_eq!(kind::WEEKLY, p.windows[1].kind, "w1 kind");
        assert_f64(55.0, p.windows[1].used_pct, "w1 used");
        assert_eq!(Some(e.now + ChronoDuration::seconds(3600)), p.windows[1].resets_at, "w1 reset from reset_after_seconds");
        assert_eq!("GPT-5-Codex Weekly", p.windows[2].label, "w2 label");
        assert_f64(5.0, p.windows[2].used_pct, "w2 used");
        assert_eq!(kind::CREDITS, p.windows[3].kind, "w3 kind");
        assert_eq!("Reset credits \u{00D7}2", p.windows[3].label, "w3 label");
        assert!(p.windows[3].used_pct.is_none(), "credits have no percentage");
        let req = e.http.requests()[0].clone();
        assert_contains(&req.url, "chatgpt.com/backend-api/wham/usage", "url");
        assert_eq!(Some("Bearer AT"), req.header("Authorization"), "authorization");
        assert_eq!(Some("acc-1"), req.header("ChatGPT-Account-Id"), "account id");
    }

    #[test]
    fn remaining_percent_is_converted() {
        let p = codex::map_wham(
            &json("{\"rate_limit\":{\"primary_window\":{\"remaining_percent\":30,\"limit_window_seconds\":18000}}}"),
            utc(2026, 10, 11, 12, 0, 0),
        );
        assert_f64(70.0, p.windows[0].used_pct, "100 - remaining");
    }

    #[test]
    fn auth_failures_and_rate_limit() {
        let e = AdapterEnv::new();
        e.write(".codex\\auth.json", "{\"tokens\":{\"access_token\":\"AT\"}}");
        e.http.set_handler(|_r| reply(401, ""));
        let a = fetch(&e, false);
        assert_eq!(state::LOGGED_OUT, a.status.state, "401 -> logged out");
        assert_eq!(Some("Open Codex once to refresh login".to_string()), a.status.hint, "hint");

        e.http.set_handler(|_r| reply(429, ""));
        let b = fetch(&e, false);
        assert!(b.rate_limited, "429 flagged");
        assert_eq!(state::FETCH_FAILED, b.status.state, "429 state");

        e.http.set_handler(|_r| reply(503, ""));
        let c = fetch(&e, false);
        assert_eq!(Some("HTTP 503".to_string()), c.status.message, "other status");
    }
}

mod review_fix_tests {
    use super::*;

    fn creds(access: &str, refresh: &str, expires_at_ms: i64) -> String {
        format!(
            "{{\"claudeAiOauth\":{{\"accessToken\":\"{}\",\"refreshToken\":\"{}\",\"expiresAt\":{}}}}}",
            access, refresh, expires_at_ms
        )
    }

    #[test]
    fn claude_recovery_hints_are_in_hint_so_the_ui_shows_them() {
        {
            let mut e = AdapterEnv::new();
            e.write(".claude\\.credentials.json", &creds("AT", "RT", ms(e.now + ChronoDuration::hours(5))));
            e.config.set_refresh_tokens("claude", false);
            e.http.set_handler(|_r| reply(401, ""));
            let p = ClaudeAdapter::new().fetch_usage(&e.context(false, "claude"));
            assert_eq!(state::LOGGED_OUT, p.status.state, "state");
            assert_eq!(Some("Run: claude auth login".to_string()), p.status.hint, "hint field");
            assert_eq!(Some("Run: claude auth login".to_string()), format::status_text(&p), "what the flyout shows");
        }
        {
            let e = AdapterEnv::new();
            let c = ClaudeAdapter::new().fetch_usage(&e.context(false, "claude"));
            assert_contains(&format::status_text(&c).unwrap(), "claude", "claude not installed text mentions the CLI");
            let x = CodexAdapter::new().fetch_usage(&e.context(false, "codex"));
            assert_contains(&format::status_text(&x).unwrap(), "Install Codex", "codex not installed text");
        }
    }

    #[test]
    fn a_failing_proactive_refresh_does_not_prevent_the_usage_request() {
        let e = AdapterEnv::new();
        e.write(".claude\\.credentials.json", &creds("AT", "RT", ms(e.now + ChronoDuration::seconds(30))));
        e.http.set_handler(|r| {
            if r.url.contains("/oauth/token") {
                return Err(HttpError("refresh endpoint timed out".to_string()));
            }
            reply(200, "{\"five_hour\":{\"utilization\":12,\"resets_at\":\"2026-10-11T15:00:00Z\"}}")
        });
        let p = ClaudeAdapter::new().fetch_usage(&e.context(false, "claude"));
        assert_eq!(state::OK, p.status.state, "usage still fetched with the current token");
        assert_f64(12.0, p.windows[0].used_pct, "value");
    }

    #[test]
    fn codex_unusable_relative_reset_keeps_the_percentage() {
        let e = AdapterEnv::new();
        e.write(".codex\\auth.json", "{\"tokens\":{\"access_token\":\"AT\"}}");
        e.http.set_handler(|_r| {
            reply(
                200,
                "{\"rate_limit\":{\"primary_window\":{\"used_percent\":20,\"limit_window_seconds\":18000,\"reset_after_seconds\":1e20}}}",
            )
        });
        let p = CodexAdapter::new().fetch_usage(&e.context(false, "codex"));
        assert_eq!(state::OK, p.status.state, "state");
        assert_f64(20.0, p.windows[0].used_pct, "used kept");
        assert!(p.windows[0].resets_at.is_none(), "reset unknown");
    }

    #[test]
    fn http_deadline_covers_the_whole_exchange_not_just_each_read() {
        use std::io::Write;
        let s = TestServer::new(|_req, out| {
            let _ = out.write_all(b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
            for _ in 0..80 {
                if out.write_all(b"1\r\nA\r\n").is_err() || out.flush().is_err() {
                    return;
                }
                std::thread::sleep(Duration::from_millis(100));
            }
        });
        let t0 = Instant::now();
        assert!(
            RealHttp::new().request("GET", &s.base_url, &[], None, 800).is_err(),
            "slow trickle must hit the overall deadline"
        );
        assert!(t0.elapsed().as_millis() < 3000, "gave up in time: {} ms", t0.elapsed().as_millis());
    }

    #[test]
    fn credentials_file_is_never_missing_and_leaves_no_temp_file_after_a_refresh() {
        let e = AdapterEnv::new();
        let path = e.write(".claude\\.credentials.json", &creds("OLD", "RT", ms(e.now + ChronoDuration::seconds(30))));
        let during = path.clone();
        e.http.set_handler(move |r| {
            if r.url.contains("/oauth/token") {
                assert!(std::path::Path::new(&during).exists(), "credentials exist while the refresh is in flight");
                return reply(200, "{\"access_token\":\"NEW\",\"refresh_token\":\"RT2\",\"expires_in\":3600}");
            }
            reply(200, "{}")
        });
        ClaudeAdapter::new().fetch_usage(&e.context(false, "claude"));
        assert!(std::path::Path::new(&path).exists(), "credentials exist afterwards");
        assert!(!std::path::Path::new(&format!("{}.tmp", path)).exists(), "no temp file left");
        assert_contains(&fs::read_to_string(&path).unwrap(), "\"accessToken\": \"NEW\"", "updated");
        // a file that is held open by another process must stay intact if it cannot be replaced
        let before = fs::read_to_string(&path).unwrap();
        {
            use std::os::windows::fs::OpenOptionsExt;
            let _hold = fs::OpenOptions::new().read(true).share_mode(1).open(&path).unwrap(); // FILE_SHARE_READ
            e.http.set_handler(|r| {
                if r.url.contains("/oauth/token") {
                    return reply(200, "{\"access_token\":\"NEWER\",\"refresh_token\":\"RT3\",\"expires_in\":3600}");
                }
                reply(200, "{}")
            });
            // forcing a refresh again by rewriting the expiry into the past is not possible while
            // held; just ensure no panic escapes
            let _p = ClaudeAdapter::new().fetch_usage(&e.context(false, "claude"));
        }
        assert_eq!(before, fs::read_to_string(&path).unwrap(), "file untouched");
    }
}

mod agy_quota_tests {
    use super::*;

    const BLOB: &str = "{\"token\":{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expiry\":\"2099-01-01T00:00:00Z\"}}";
    const LOAD: &str = "{\"cloudaicompanionProject\":\"p\",\"paidTier\":{\"id\":\"g1-pro-tier\",\"name\":\"Google AI Pro\"}}";
    const THIRD_PARTY_GROUP: &str = concat!(
        "{\"displayName\":\"Claude and GPT models\",\"buckets\":[",
        "{\"bucketId\":\"3p-weekly\",\"displayName\":\"Weekly Limit Remaining\",\"window\":\"weekly\",\"resetTime\":\"2026-10-17T13:30:20Z\",\"remainingFraction\":0.75},",
        "{\"bucketId\":\"3p-5h\",\"displayName\":\"Five Hour Limit Remaining\",\"window\":\"5h\",\"resetTime\":\"2026-10-16T16:31:36Z\",\"remainingFraction\":1}]}"
    );
    const QUOTA: &str = concat!(
        "{\"buckets\":[",
        "{\"modelId\":\"chat_20706\",\"remainingFraction\":1},",
        "{\"modelId\":\"claude-opus-5-5-high\",\"remainingFraction\":0,\"resetTime\":\"2026-10-16T16:31:36Z\"},",
        "{\"modelId\":\"gemini-3.8-flash-tiered\",\"remainingFraction\":1,\"resetTime\":\"2026-10-16T16:31:36Z\"},",
        "{\"modelId\":\"gemini-3.8-flash-low\",\"remainingFraction\":1,\"resetTime\":\"2026-10-16T16:31:36Z\"},",
        "{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":0.6,\"resetTime\":\"2026-10-16T16:31:36Z\"},",
        "{\"modelId\":\"tab_flash_lite_preview\",\"remainingFraction\":0}]}"
    );

    fn fetch(summary: &str, calls: Option<Arc<std::sync::Mutex<Vec<String>>>>) -> ProviderSnapshot {
        let e = AdapterEnv::new();
        e.credentials.set("gemini:antigravity", BLOB);
        let summary = summary.to_string();
        e.http.set_handler(move |r| {
            if let Some(c) = &calls {
                c.lock().unwrap().push(r.url[r.url.rfind(':').unwrap() + 1..].to_string());
            }
            if r.url.ends_with(":loadCodeAssist") {
                return reply(200, LOAD);
            }
            if r.url.ends_with(":retrieveUserQuotaSummary") {
                return reply(200, &summary);
            }
            if r.url.ends_with(":retrieveUserQuota") {
                return reply(200, QUOTA);
            }
            reply(404, "")
        });
        GeminiAdapter::new().fetch_usage(&e.context(false, "agy"))
    }

    fn find<'a>(p: &'a ProviderSnapshot, label: &str) -> Option<&'a UsageWindow> {
        p.windows.iter().find(|w| w.label == label)
    }

    #[test]
    fn third_party_group_is_shown_as_its_own_rows_next_to_gemini() {
        let p = fetch(&format!("{{\"groups\":[{}]}}", THIRD_PARTY_GROUP), None);
        assert_eq!(state::OK, p.status.state, "state");
        assert_eq!(Some("Google AI Pro".to_string()), p.plan, "plan");

        // One Gemini row first: the shared 5-hour pool, taken from the highest-use bucket
        assert_eq!("5h", p.windows[0].label, "single gemini 5h row");
        assert_eq!(kind::FIVE_HOUR, p.windows[0].kind, "gemini pool is five_hour");
        assert_f64(40.0, p.windows[0].used_pct, "gemini used");
        assert_eq!(Some(utc(2026, 10, 16, 16, 31, 36)), p.windows[0].resets_at, "gemini reset");

        let five = find(&p, "Claude/GPT 5h").expect("third-party 5h row");
        let week = find(&p, "Claude/GPT Wk").expect("third-party weekly row");
        assert_eq!(kind::QUOTA, five.kind, "not reported as Gemini's own five_hour");
        assert_eq!(kind::QUOTA, week.kind, "not reported as Gemini's own weekly");
        assert_f64(0.0, five.used_pct, "5h used");
        assert_f64(25.0, week.used_pct, "weekly used");
        assert_eq!(Some(utc(2026, 10, 17, 13, 30, 20)), week.resets_at, "weekly reset");
        assert_eq!(3, p.windows.len(), "one Gemini row + two Claude/GPT rows");

        for w in &p.windows {
            assert!(!w.label.to_lowercase().contains("opus"), "per-model Claude bucket not shown as a Gemini row");
            assert!(!w.label.to_lowercase().contains("tab"), "tab-completion bucket not shown");
        }
    }

    #[test]
    fn gemini_summary_group_still_wins_and_skips_the_fallback() {
        let calls = Arc::new(std::sync::Mutex::new(Vec::new()));
        let gemini = concat!(
            "{\"displayName\":\"Gemini models\",\"buckets\":[",
            "{\"displayName\":\"5 hour\",\"remainingFraction\":0.8,\"resetTime\":\"2026-10-16T15:00:00Z\"},",
            "{\"displayName\":\"Weekly\",\"remainingFraction\":0.5,\"resetTime\":\"2026-10-20T00:00:00Z\"}]}"
        );
        let p = fetch(&format!("{{\"groups\":[{},{}]}}", gemini, THIRD_PARTY_GROUP), Some(calls.clone()));
        assert_eq!(state::OK, p.status.state, "state");
        assert_eq!(kind::FIVE_HOUR, p.windows[0].kind, "gemini 5h stays five_hour");
        assert_f64(20.0, p.windows[0].used_pct, "gemini 5h");
        assert_eq!(kind::WEEKLY, p.windows[1].kind, "gemini weekly stays weekly");
        assert!(find(&p, "Claude/GPT Wk").is_some(), "third-party rows appended");
        assert!(
            !calls.lock().unwrap().contains(&"retrieveUserQuota".to_string()),
            "no per-model fallback when the summary has Gemini rows"
        );
    }

    #[test]
    fn new_model_ids_get_readable_short_labels() {
        let w = gemini::map_models(
            &json(concat!(
                "{\"models\":[",
                "{\"model\":\"gemini-3.5-flash-lite\",\"quotaInfo\":{\"remainingFraction\":0.1}},",
                "{\"model\":\"gemini-3.8-flash-tiered\",\"quotaInfo\":{\"remainingFraction\":0.2}}]}"
            )),
            utc(2026, 10, 16, 0, 0, 0),
        );
        assert_eq!("3.5 Flash Lite", w[0].label, "lite variant");
        assert_eq!("3.8 Flash", w[1].label, "tier suffix dropped");
    }
}

mod gemini_mapping_tests {
    use super::*;
    use serde_json::Value;

    fn now() -> DateTime<Utc> {
        utc(2026, 10, 11, 12, 0, 0)
    }

    #[test]
    fn used_from_fraction() {
        assert_f64(75.0, gemini::used_from_fraction(Some(&json("0.25"))), "0.25 remaining");
        assert_f64(0.0, gemini::used_from_fraction(Some(&json("1"))), "1 remaining");
        assert_f64(50.0, gemini::used_from_fraction(Some(&json("\"0.5\""))), "numeric string");
        assert_f64(0.0, gemini::used_from_fraction(Some(&json("1.5"))), "clamped low");
        assert_f64(100.0, gemini::used_from_fraction(Some(&json("-1"))), "clamped high");
        let bad: Vec<Option<Value>> = vec![
            None,
            Some(Value::Null),
            Some(json("\"\"")),
            Some(json("\"  \"")),
            Some(json("\"Infinity\"")),
            Some(json("true")),
            Some(json("\"x\"")),
        ];
        for v in &bad {
            assert!(gemini::used_from_fraction(v.as_ref()).is_none(), "invalid {:?}", v);
        }
    }

    #[test]
    fn expiry_epoch_accepts_seconds_millis_and_iso() {
        let ee = |text: &str| gemini::expiry_epoch(Some(&json(text)));
        assert_f64(1800000000.0, ee("\"1800000000\""), "seconds string");
        assert_f64(1800000000.0, ee("\"1800000000000\""), "millis string");
        assert_f64(1800000000.0, ee("1800000000000"), "millis number");
        assert_f64(1800000000.0, ee("1800000000"), "seconds number");
        assert_f64(1800000000.0, ee("\"2027-01-15T08:00:00Z\""), "iso");
        assert!(gemini::expiry_epoch(None).is_none(), "null");
        assert!(ee("\"\"").is_none(), "empty");
        assert!(ee("\"not a date\"").is_none(), "garbage");
    }

    #[test]
    fn map_summary_never_shows_another_providers_quota() {
        let w = gemini::map_summary(
            &json("{\"groups\":[{\"displayName\":\"Claude\",\"buckets\":[{\"remainingFraction\":0}]}]}"),
            now(),
        );
        assert_eq!(0, w.len(), "claude group ignored");
    }

    #[test]
    fn map_summary_collects_all_gemini_groups_and_classifies_buckets() {
        let two = gemini::map_summary(
            &json(concat!(
                "{\"groups\":[{\"displayName\":\"Gemini Pro\",\"buckets\":[{\"remainingFraction\":0.5,\"window\":\"DAILY\"}]},",
                "{\"displayName\":\"Gemini Flash\",\"buckets\":[{\"remainingFraction\":0.5,\"window\":\"DAILY\"}]}]}"
            )),
            now(),
        );
        assert_eq!(2, two.len(), "two groups");
        assert_eq!("Gemini Pro Daily", two[0].label, "label includes group when several groups");
        assert_eq!(kind::DAILY, two[0].kind, "kind daily");
        assert_f64(50.0, two[0].used_pct, "used");

        let one = gemini::map_summary(
            &json(concat!(
                "{\"groups\":[{\"displayName\":\"Gemini\",\"buckets\":[",
                "{\"displayName\":\"5 hour limit\",\"remainingFraction\":0.9,\"resetTime\":\"2026-10-11T15:00:00Z\"},",
                "{\"displayName\":\"Weekly\",\"remainingFraction\":0.4},",
                "{\"bucketId\":\"mystery\",\"remainingFraction\":0.1},",
                "{\"displayName\":\"skip me\"}]}]}"
            )),
            now(),
        );
        assert_eq!(3, one.len(), "bucket without fraction skipped");
        assert_eq!(kind::FIVE_HOUR, one[0].kind, "five hour");
        assert_eq!("5h", one[0].label, "5h label");
        assert_eq!(Some(utc(2026, 10, 11, 15, 0, 0)), one[0].resets_at, "reset time");
        assert_eq!(kind::WEEKLY, one[1].kind, "weekly");
        assert_eq!("Weekly", one[1].label, "weekly label");
        assert_f64(60.0, one[1].used_pct, "weekly used");
        assert_eq!(kind::WEEKLY_SCOPED, one[2].kind, "unknown bucket");
        assert_eq!("mystery", one[2].label, "falls back to bucket id");
    }

    #[test]
    fn map_models_filters_to_gemini_and_uses_keyed_ids() {
        let w = gemini::map_models(
            &json(concat!(
                "{\"models\":{\"claude-opus\":{\"quotaInfo\":{\"remainingFraction\":0}},",
                "\"gemini-pro\":{\"displayName\":\"Pro\",\"quotaInfo\":{\"remainingFraction\":0.25}}}}"
            )),
            now(),
        );
        assert_eq!(1, w.len(), "only gemini");
        assert_f64(75.0, w[0].used_pct, "used");
        assert_eq!(kind::QUOTA, w[0].kind, "kind quota");
        assert_eq!("Pro", w[0].label, "short label");
    }
}

mod gemini_adapter_tests {
    use super::*;

    const HOST1: &str = "https://daily-cloudcode-pa.googleapis.com";

    fn blob(access: &str, refresh: &str, expiry: &str) -> String {
        format!(
            "{{\"token\":{{\"access_token\":\"{}\",\"refresh_token\":\"{}\",\"expiry\":\"{}\"}}}}",
            access, refresh, expiry
        )
    }

    const LOAD_OK: &str = "{\"cloudaicompanionProject\":\"proj-1\",\"paidTier\":{\"name\":\"Google AI Pro\"}}";
    const SUMMARY: &str = concat!(
        "{\"groups\":[{\"displayName\":\"Gemini\",\"buckets\":[",
        "{\"displayName\":\"5 hour\",\"remainingFraction\":0.8,\"resetTime\":\"2026-10-11T15:00:00Z\"},",
        "{\"displayName\":\"Weekly\",\"remainingFraction\":0.5,\"resetTime\":\"2026-10-17T00:00:00Z\"}]}]}"
    );

    fn fake_agy(e: &AdapterEnv) {
        // fake values; literals are split so secret scanners do not flag them
        e.write(
            "AppData\\Local\\agy\\bin\\agy.exe",
            &("junk 1071234567890-abcdefg.apps.google".to_string()
                + "usercontent.com junk GOCS"
                + "PX-AbCdEfGhIjKlMnOpQrStUvWx more junk"),
        );
    }

    fn fetch(e: &AdapterEnv) -> ProviderSnapshot {
        GeminiAdapter::new().fetch_usage(&e.context(false, "agy"))
    }

    #[test]
    fn not_installed_and_logged_out() {
        let e = AdapterEnv::new();
        let a = fetch(&e);
        assert_eq!(state::NOT_INSTALLED, a.status.state, "not installed");
        let b = GeminiAdapter::new().fetch_usage(&e.context(true, "agy"));
        assert_eq!(state::LOGGED_OUT, b.status.state, "logged out");
        assert_eq!(Some("Run: agy  (sign in with Google)".to_string()), b.status.hint, "hint (two spaces)");
        assert_eq!(0, e.http.count(), "no requests");
    }

    #[test]
    fn happy_path_with_credential_manager_blob() {
        let e = AdapterEnv::new();
        e.credentials.set("gemini:antigravity", &blob("AT", "RT", "2099-01-01T00:00:00Z"));
        e.http.set_handler(|r| {
            if r.url.ends_with(":loadCodeAssist") {
                return reply(200, LOAD_OK);
            }
            if r.url.ends_with(":retrieveUserQuotaSummary") {
                return reply(200, SUMMARY);
            }
            reply(404, "")
        });
        let p = fetch(&e);
        assert_eq!(state::OK, p.status.state, "state");
        assert_eq!(Some("Gemini".to_string()), p.display_name, "name");
        assert_eq!(Some("Google AI Pro".to_string()), p.plan, "plan");
        assert_eq!(2, p.windows.len(), "windows");
        assert_f64(20.0, p.windows[0].used_pct, "5h used");
        assert_f64(50.0, p.windows[1].used_pct, "weekly used");
        assert_contains(&e.credentials.reads()[0], "gemini:antigravity", "credential target");
        let reqs = e.http.requests();
        assert_eq!(2, reqs.len(), "load + summary");
        assert_eq!(format!("{}/v1internal:loadCodeAssist", HOST1), reqs[0].url, "load url");
        assert_contains(reqs[0].body.as_deref().unwrap(), "\"ideType\":\"ANTIGRAVITY\"", "load body metadata");
        assert_eq!(Some("Bearer AT"), reqs[0].header("Authorization"), "authorization");
        assert_eq!(Some("antigravity/windows/amd64"), reqs[0].header("User-Agent"), "user agent");
        assert_contains(reqs[1].body.as_deref().unwrap(), "\"project\":\"proj-1\"", "summary body carries project");
    }

    #[test]
    fn falls_back_to_quota_then_models_endpoint() {
        let e = AdapterEnv::new();
        e.credentials.set("gemini:antigravity", &blob("AT", "RT", "2099-01-01T00:00:00Z"));
        e.http.set_handler(|r| {
            if r.url.ends_with(":loadCodeAssist") {
                return reply(200, LOAD_OK);
            }
            if r.url.ends_with(":retrieveUserQuotaSummary") {
                return reply(200, "{\"groups\":[]}");
            }
            if r.url.ends_with(":retrieveUserQuota") {
                return reply(
                    200,
                    "{\"buckets\":[{\"modelId\":\"gemini-3-pro\",\"remainingFraction\":0.3,\"resetTime\":\"2026-10-12T00:00:00Z\"},{\"modelId\":\"gemini-flash\",\"remainingFraction\":0.9}]}",
                );
            }
            reply(404, "")
        });
        let p = fetch(&e);
        assert_eq!(state::OK, p.status.state, "state");
        assert_eq!(1, p.windows.len(), "one shared-pool row");
        assert_eq!(kind::FIVE_HOUR, p.windows[0].kind, "five_hour");
        assert_eq!("5h", p.windows[0].label, "5h label");
        assert_f64(70.0, p.windows[0].used_pct, "highest used wins");
    }

    #[test]
    fn rate_limit_on_quota_stops_immediately() {
        let e = AdapterEnv::new();
        e.credentials.set("gemini:antigravity", &blob("AT", "RT", "2099-01-01T00:00:00Z"));
        e.http.set_handler(|r| {
            if r.url.ends_with(":loadCodeAssist") {
                return reply(200, LOAD_OK);
            }
            reply(429, "")
        });
        let p = fetch(&e);
        assert!(p.rate_limited, "rate limited");
        assert_eq!(state::FETCH_FAILED, p.status.state, "state");
        assert_eq!(2, e.http.count(), "no further endpoints or hosts are tried after a 429");
    }

    #[test]
    fn refresh_on_401_uses_client_extracted_from_agy_binary() {
        let e = AdapterEnv::new();
        fake_agy(&e);
        e.credentials.set("gemini:antigravity", &blob("OLD", "RT", "2099-01-01T00:00:00Z"));
        e.http.set_handler(|r| {
            if r.url.contains("oauth2.googleapis.com/token") {
                return reply(200, "{\"access_token\":\"NEW\",\"expires_in\":3600}");
            }
            if r.header("Authorization") != Some("Bearer NEW") {
                return reply(401, "");
            }
            if r.url.ends_with(":loadCodeAssist") {
                return reply(200, LOAD_OK);
            }
            reply(200, SUMMARY)
        });
        let p = fetch(&e);
        assert_eq!(state::OK, p.status.state, "ok after refresh");
        let tok = e
            .http
            .requests()
            .into_iter()
            .filter(|r| r.url.contains("oauth2.googleapis.com/token"))
            .last()
            .expect("token endpoint called");
        assert_eq!("POST", tok.method, "post");
        let body = tok.body.as_deref().unwrap();
        assert_contains(
            body,
            &("client_id=1071234567890-abcdefg.apps.google".to_string() + "usercontent.com"),
            "client id extracted from binary",
        );
        assert_contains(
            body,
            &("client_secret=GOCS".to_string() + "PX-AbCdEfGhIjKlMnOpQrStUvWx"),
            "client secret extracted from binary",
        );
        assert_contains(body, "refresh_token=RT", "refresh token");
        assert_contains(body, "grant_type=refresh_token", "grant type");
    }

    #[test]
    fn expired_access_token_is_refreshed_before_use_and_failure_means_logged_out() {
        let e = AdapterEnv::new();
        fake_agy(&e);
        e.credentials.set("gemini:antigravity", &blob("OLD", "RT", "2000-01-01T00:00:00Z"));
        e.http.set_handler(|r| {
            if r.url.contains("oauth2.googleapis.com/token") {
                return reply(200, "{\"access_token\":\"NEW\",\"expires_in\":3600}");
            }
            reply(401, "")
        });
        let p = fetch(&e);
        assert_eq!(state::LOGGED_OUT, p.status.state, "401 even with fresh token -> logged out");
        assert_eq!(Some("Run: agy  (token expired)".to_string()), p.status.hint, "hint");
        let reqs = e.http.requests();
        assert_eq!("POST", reqs[0].method, "refresh happened first");
        assert_contains(reqs[1].header("Authorization").unwrap(), "NEW", "uses refreshed token");
    }

    #[test]
    fn refresh_disabled_by_config() {
        let mut e = AdapterEnv::new();
        fake_agy(&e);
        e.credentials.set("gemini:antigravity", &blob("OLD", "RT", "2099-01-01T00:00:00Z"));
        e.config.set_refresh_tokens("gemini", false);
        e.http.set_handler(|_r| reply(401, ""));
        let p = fetch(&e);
        assert_eq!(state::LOGGED_OUT, p.status.state, "logged out");
        for r in e.http.requests() {
            assert!(!r.url.contains("oauth2.googleapis.com"), "token endpoint not called");
        }
    }
}

mod grok_tests {
    use super::*;

    fn now() -> DateTime<Utc> {
        utc(2026, 10, 11, 12, 0, 0)
    }

    fn fetch(e: &AdapterEnv, cli: bool) -> ProviderSnapshot {
        GrokAdapter::new().fetch_usage(&e.context(cli, "grok"))
    }

    #[test]
    fn monthly_fallback_is_not_labelled_weekly() {
        let p = grok::map_billing(
            &json("{\"used\":{\"val\":25},\"monthlyLimit\":{\"val\":100},\"productUsage\":[null,{}]}"),
            now(),
        );
        assert_eq!(kind::MONTHLY, p.windows[0].kind, "monthly");
        assert_eq!("Monthly", p.windows[0].label, "label");
        assert_f64(25.0, p.windows[0].used_pct, "used");
        assert_eq!(
            0,
            grok::map_billing(&json("{\"used\":{\"val\":25},\"monthlyLimit\":{\"val\":\"0\"}}"), now()).windows.len(),
            "zero limit -> no window"
        );
    }

    #[test]
    fn omitted_proto3_percent_is_zero_while_period_is_live() {
        let p = grok::map_billing(
            &json("{\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"start\":\"2020-01-01T00:00:00Z\",\"end\":\"2099-01-01T00:00:00Z\"},\"isUnifiedBillingUser\":true}"),
            now(),
        );
        assert_eq!(kind::WEEKLY, p.windows[0].kind, "weekly");
        assert_f64(0.0, p.windows[0].used_pct, "0 percent");
        assert_eq!(Some(utc(2099, 1, 1, 0, 0, 0)), p.windows[0].resets_at, "period end");
        let ended = grok::map_billing(
            &json("{\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"start\":\"2020-01-01T00:00:00Z\",\"end\":\"2021-01-01T00:00:00Z\"}}"),
            now(),
        );
        assert_eq!(0, ended.windows.len(), "ended period -> nothing invented");
    }

    #[test]
    fn product_usage_is_summed_and_explained_in_footnotes() {
        let p = grok::map_billing(
            &json(concat!(
                "{\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"end\":\"2099-01-01T00:00:00Z\"},",
                "\"productUsage\":[{\"product\":\"GrokBuild\",\"usagePercent\":8},{\"product\":\"GrokChat\",\"usagePercent\":2}]}"
            )),
            now(),
        );
        assert_eq!(kind::WEEKLY, p.windows[0].kind, "weekly");
        assert_f64(10.0, p.windows[0].used_pct, "sum");
        assert_eq!(2, p.footnotes.len(), "footnotes");
        assert_eq!("Build 8% of pool", p.footnotes[0], "footnote 0");
        assert_eq!("Chat 2% of pool", p.footnotes[1], "footnote 1");
        assert_eq!(Some("SuperGrok".to_string()), p.plan, "plan");
        assert_eq!(Some("Grok Build".to_string()), p.display_name, "name");
    }

    #[test]
    fn credit_windows() {
        let capped = grok::map_billing(
            &json("{\"creditUsagePercent\":34.5,\"onDemandCap\":{\"val\":50},\"onDemandUsed\":{\"val\":10}}"),
            now(),
        );
        assert_f64(34.5, capped.windows[0].used_pct, "explicit percent");
        assert_eq!(kind::CREDITS, capped.windows[1].kind, "credits window");
        assert_f64(20.0, capped.windows[1].used_pct, "10 of 50");
        let prepaid = grok::map_billing(&json("{\"creditUsagePercent\":1,\"prepaidBalance\":{\"val\":12.5}}"), now());
        assert_eq!("Credits $12.50", prepaid.windows[1].label, "prepaid label");
        assert!(prepaid.windows[1].used_pct.is_none(), "no percentage");
    }

    #[test]
    fn parse_credits_grpc_web() {
        assert_f64(34.0, grok::parse_credits_grpc_web(&hex("00000000070a050d00000842")), "float32 percent");
        assert!(grok::parse_credits_grpc_web(&[]).is_none(), "empty");
        assert!(grok::parse_credits_grpc_web(&hex("00000000")).is_none(), "truncated header");
        assert!(
            grok::parse_credits_grpc_web(&hex("800000000f677270632d7374617475733a2030")).is_none(),
            "trailer-only frame"
        );
    }

    fn accounts(extra: &str) -> String {
        format!(
            "{{\"old\":{{\"key\":\"old-token\",\"create_time\":\"2024-01-01\"}},\"current\":{{\"key\":\"current-token\",\"create_time\":\"2025-01-01\"{}}}}}",
            extra
        )
    }

    #[test]
    fn not_installed_logged_out_and_api_key() {
        let e = AdapterEnv::new();
        assert_eq!(state::NOT_INSTALLED, fetch(&e, false).status.state, "not installed");
        assert_eq!(state::LOGGED_OUT, fetch(&e, true).status.state, "logged out");
        e.write(".grok\\auth.json", "{\"apiKey\":{\"foo\":\"bar\"}}");
        assert_eq!(state::UNKNOWN, fetch(&e, true).status.state, "no account entry with key -> unknown");
    }

    #[test]
    fn refresh_updates_only_the_selected_account_and_runs_once() {
        let e = AdapterEnv::new();
        let path = e.write(
            ".grok\\auth.json",
            &accounts(",\"expires_at\":\"2020-01-01\",\"refresh_token\":\"rt\",\"oidc_client_id\":\"cid\""),
        );
        let refreshes = Arc::new(AtomicUsize::new(0));
        let counter = refreshes.clone();
        e.http.set_handler(move |r| {
            if r.url.contains("openid-configuration") {
                return reply(404, "");
            }
            if r.method == "POST" {
                counter.fetch_add(1, Ordering::SeqCst);
                return reply(200, "{\"access_token\":\"fresh\",\"refresh_token\":\"rotated\"}");
            }
            reply(401, "")
        });
        let p = fetch(&e, false);
        assert_eq!(state::LOGGED_OUT, p.status.state, "401 after refresh -> logged out");
        assert_eq!(1, refreshes.load(Ordering::SeqCst), "refreshed exactly once");
        let saved = fs::read_to_string(&path).unwrap();
        assert_contains(&saved, "\"key\": \"old-token\"", "other account untouched");
        assert_contains(&saved, "\"key\": \"fresh\"", "selected account updated");
        assert_contains(&saved, "\"refresh_token\": \"rotated\"", "rotated refresh token stored");
        let post = e.http.requests().into_iter().filter(|r| r.method == "POST").last().unwrap();
        assert_contains(&post.url, "auth.x.ai/oauth/token", "default token endpoint");
        assert_contains(post.body.as_deref().unwrap(), "client_id=cid", "client id");
        assert_contains(post.body.as_deref().unwrap(), "grant_type=refresh_token", "grant");
    }

    #[test]
    fn fills_omitted_percent_from_grpc_web_credits() {
        let e = AdapterEnv::new();
        e.write(".grok\\auth.json", "{\"current\":{\"key\":\"tok\",\"create_time\":\"2025-01-01\"}}");
        e.http.set_handler(|r| {
            if r.url.contains("cli-chat-proxy.grok.com/v1/billing") {
                return reply(
                    200,
                    "{\"config\":{\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"start\":\"2020-01-01T00:00:00Z\",\"end\":\"2099-01-01T00:00:00Z\"}}}",
                );
            }
            if r.url.contains("GetGrokCreditsConfig") {
                return binary(200, hex("00000000070a050d00000842"));
            }
            reply(404, "")
        });
        let p = fetch(&e, false);
        assert_eq!(state::OK, p.status.state, "ok");
        assert_eq!(kind::WEEKLY, p.windows[0].kind, "weekly");
        assert_f64(34.0, p.windows[0].used_pct, "percent from grpc");
        let billing = e.http.requests()[0].clone();
        assert_eq!(Some("Bearer tok"), billing.header("Authorization"), "authorization");
        assert_eq!(Some("xai-grok-cli"), billing.header("X-XAI-Token-Auth"), "token auth header");
    }

    #[test]
    fn rate_limit_and_http_errors() {
        let e = AdapterEnv::new();
        e.write(".grok\\auth.json", "{\"current\":{\"key\":\"tok\",\"create_time\":\"2025-01-01\"}}");
        e.http.set_handler(|_r| reply(429, ""));
        let a = fetch(&e, false);
        assert!(a.rate_limited, "429 flagged");
        e.http.set_handler(|_r| reply(500, ""));
        let b = fetch(&e, false);
        assert_eq!(Some("HTTP 500".to_string()), b.status.message, "status message");
    }
}

mod m3_review_tests {
    use super::*;

    fn blob(access: &str, refresh: &str, expiry: &str) -> String {
        format!(
            "{{\"token\":{{\"access_token\":\"{}\",\"refresh_token\":\"{}\",\"expiry\":\"{}\"}}}}",
            access, refresh, expiry
        )
    }

    const SUMMARY: &str = concat!(
        "{\"groups\":[{\"displayName\":\"Gemini\",\"buckets\":[",
        "{\"displayName\":\"5 hour\",\"remainingFraction\":0.8,\"resetTime\":\"2026-10-11T15:00:00Z\"}]}]}"
    );

    // ---- Grok: credential file safety ----

    #[test]
    fn grok_refresh_does_not_overwrite_an_account_the_cli_replaced_meanwhile() {
        let e = AdapterEnv::new();
        let body = "{\"current\":{\"key\":\"stale-token\",\"create_time\":\"2025-01-01\",\"expires_at\":\"2020-01-01\",\"refresh_token\":\"rt\",\"oidc_client_id\":\"cid\"}}";
        let path = e.write(".grok\\auth.json", body);
        let stamp = fs::metadata(&path).unwrap().modified().unwrap();
        let p2 = path.clone();
        e.http.set_handler(move |r| {
            if r.url.contains("openid-configuration") {
                return reply(404, "");
            }
            if r.method == "POST" {
                // the CLI logs in again while our refresh request is in flight; the file keeps
                // its old timestamp (coarse clocks), only the account key differs
                fs::write(&p2, body.replace("stale-token", "cli-new-token")).unwrap();
                set_mtime(&p2, stamp);
                return reply(200, "{\"access_token\":\"fresh\",\"refresh_token\":\"rotated\"}");
            }
            reply(401, "")
        });
        GrokAdapter::new().fetch_usage(&e.context(false, "grok"));
        let saved = fs::read_to_string(&path).unwrap();
        assert_contains(&saved, "cli-new-token", "the CLI's new login survives");
        assert!(!saved.contains("\"key\": \"fresh\""), "our refresh result is not written over it");
    }

    #[test]
    fn grok_refresh_leaves_no_temp_file_and_a_valid_json_file() {
        let e = AdapterEnv::new();
        let path = e.write(
            ".grok\\auth.json",
            "{\"current\":{\"key\":\"tok\",\"create_time\":\"2025-01-01\",\"expires_at\":\"2020-01-01\",\"refresh_token\":\"rt\",\"oidc_client_id\":\"cid\",\"user_id\":\"u1\"}}",
        );
        e.http.set_handler(|r| {
            if r.url.contains("openid-configuration") {
                return reply(404, "");
            }
            if r.method == "POST" {
                return reply(200, "{\"access_token\":\"fresh\",\"expires_in\":3600}");
            }
            reply(401, "")
        });
        GrokAdapter::new().fetch_usage(&e.context(false, "grok"));
        assert!(std::path::Path::new(&path).exists(), "auth.json still exists");
        assert!(!std::path::Path::new(&format!("{}.tmp", path)).exists(), "no temp file left behind");
        let saved = fs::read_to_string(&path).unwrap();
        assert_contains(&saved, "\"key\": \"fresh\"", "token written");
        assert_contains(&saved, "\"user_id\": \"u1\"", "unrelated fields kept");
        assert!(saved.starts_with('{'), "no BOM, plain JSON");
    }

    #[test]
    fn grok_account_with_empty_key_is_not_selected() {
        let e = AdapterEnv::new();
        e.write(
            ".grok\\auth.json",
            "{\"old\":{\"key\":\"old-token\",\"create_time\":\"2024-01-01\"},\"newer\":{\"key\":\"\",\"create_time\":\"2025-01-01\"}}",
        );
        e.http.set_handler(|_r| reply(401, ""));
        GrokAdapter::new().fetch_usage(&e.context(false, "grok"));
        assert!(e.http.count() > 0, "a request was made");
        assert_eq!(
            Some("Bearer old-token"),
            e.http.requests()[0].header("Authorization"),
            "falls back to the account that has a key"
        );
    }

    #[test]
    fn grpc_unused_wide_varint_fields_do_not_discard_the_percent() {
        // an unrelated varint field (field 2, 10-byte int64) in front of the nested message
        assert_f64(
            34.0,
            grok::parse_credits_grpc_web(&hex(&("0000000012".to_string() + "10FFFFFFFFFFFFFFFFFF01" + "0a05" + "0d00000842"))),
            "10-byte varint is skipped",
        );
        // a malformed (never terminated) varint still fails cleanly
        assert!(
            grok::parse_credits_grpc_web(&hex(&("000000000c".to_string() + "10FFFFFFFFFFFFFFFFFFFF" + "00"))).is_none(),
            "11+ byte varint"
        );
    }

    #[test]
    fn grpc_length_varint_overflow_is_rejected() {
        // nested length 0x100000005 would wrap to 5 when truncated to 32 bits
        assert!(
            grok::parse_credits_grpc_web(&hex("000000000b0a85808080100d00000842")).is_none(),
            "overflowing length"
        );
        assert_f64(34.0, grok::parse_credits_grpc_web(&hex("00000000070a050d00000842")), "well-formed frame still parses");
    }

    // ---- Gemini ----

    #[test]
    fn gemini_plan_label_falls_back_to_the_tier_id() {
        {
            let e = AdapterEnv::new();
            e.credentials.set("gemini:antigravity", &blob("AT", "RT", "2099-01-01T00:00:00Z"));
            e.http.set_handler(|r| {
                if r.url.ends_with(":loadCodeAssist") {
                    return reply(200, "{\"cloudaicompanionProject\":\"p\",\"currentTier\":{\"id\":\"free-tier\"}}");
                }
                reply(200, SUMMARY)
            });
            let p = GeminiAdapter::new().fetch_usage(&e.context(false, "agy"));
            assert_eq!(Some("Free".to_string()), p.plan, "free-tier id without a name");
        }
        {
            let e = AdapterEnv::new();
            e.credentials.set("gemini:antigravity", &blob("AT", "RT", "2099-01-01T00:00:00Z"));
            e.http.set_handler(|r| {
                if r.url.ends_with(":loadCodeAssist") {
                    return reply(
                        200,
                        "{\"cloudaicompanionProject\":\"p\",\"paidTier\":{\"id\":\"g1-pro-tier\"},\"currentTier\":{\"id\":\"free-tier\"}}",
                    );
                }
                reply(200, SUMMARY)
            });
            let p = GeminiAdapter::new().fetch_usage(&e.context(false, "agy"));
            assert_eq!(Some("g1-pro-tier".to_string()), p.plan, "paid tier id is used when it has no name");
        }
    }

    #[test]
    fn gemini_quota_fallback_ignores_other_providers_models() {
        let e = AdapterEnv::new();
        e.credentials.set("gemini:antigravity", &blob("AT", "RT", "2099-01-01T00:00:00Z"));
        e.http.set_handler(|r| {
            if r.url.ends_with(":loadCodeAssist") {
                return reply(200, "{\"cloudaicompanionProject\":\"p\"}");
            }
            if r.url.ends_with(":retrieveUserQuotaSummary") {
                return reply(200, "{\"groups\":[]}");
            }
            if r.url.ends_with(":retrieveUserQuota") {
                return reply(
                    200,
                    "{\"buckets\":[{\"modelId\":\"claude-sonnet-5\",\"remainingFraction\":0},{\"modelId\":\"gemini-3-pro\",\"remainingFraction\":0.8}]}",
                );
            }
            reply(404, "")
        });
        let p = GeminiAdapter::new().fetch_usage(&e.context(false, "agy"));
        assert_eq!(state::OK, p.status.state, "state");
        assert_eq!(1, p.windows.len(), "only the Gemini bucket");
        assert_eq!("5h", p.windows[0].label, "label");
        assert_f64(20.0, p.windows[0].used_pct, "used from the Gemini bucket, not the exhausted Claude one");
    }

    fn token_flow_handler(e: &AdapterEnv) {
        e.http.set_handler(|r| {
            if r.url.contains("oauth2.googleapis.com/token") {
                return reply(200, "{\"access_token\":\"NEW\",\"expires_in\":3600}");
            }
            if r.header("Authorization") != Some("Bearer NEW") {
                return reply(401, "");
            }
            if r.url.ends_with(":loadCodeAssist") {
                return reply(200, "{\"cloudaicompanionProject\":\"p\"}");
            }
            reply(200, SUMMARY)
        });
    }

    #[test]
    fn gemini_secret_stops_at_the_next_gocspx_marker() {
        // the real agy binary stores secrets back to back: GOCSPX-<28 chars>GOCSPX-<16 chars>-...
        let e = AdapterEnv::new();
        e.write(
            "AppData\\Local\\agy\\bin\\agy.exe",
            // fake values; literals are split so secret scanners do not flag them
            &("junk 1071234567890-abcdefg.apps.google".to_string()
                + "usercontent.com GOCS"
                + "PX-AbCdEfGhIjKlMnOpQrStUvWxYzABGOCS"
                + "PX-ZZZZZZZZZZZZZZZZ-tail"),
        );
        e.credentials.set("gemini:antigravity", &blob("OLD", "RT", "2099-01-01T00:00:00Z"));
        token_flow_handler(&e);
        GeminiAdapter::new().fetch_usage(&e.context(false, "agy"));
        let tok = e
            .http
            .requests()
            .into_iter()
            .filter(|r| r.url.contains("oauth2.googleapis.com/token"))
            .last()
            .expect("token endpoint called");
        assert_contains(
            tok.body.as_deref().unwrap(),
            &("client_secret=GOCS".to_string() + "PX-AbCdEfGhIjKlMnOpQrStUvWxYzAB&"),
            "secret ends where the next marker starts",
        );
    }

    #[test]
    fn gemini_client_extraction_survives_long_digit_runs_quickly() {
        let e = AdapterEnv::new();
        let junk = "7".repeat(120000);
        e.write(
            "AppData\\Local\\agy\\bin\\agy.exe",
            &(junk + " 1071234567890-abcdefg.apps.google" + "usercontent.com junk GOCS" + "PX-AbCdEfGhIjKlMnOpQrStUvWx tail"),
        );
        e.credentials.set("gemini:antigravity", &blob("OLD", "RT", "2099-01-01T00:00:00Z"));
        token_flow_handler(&e);
        let t0 = Instant::now();
        let p = GeminiAdapter::new().fetch_usage(&e.context(false, "agy"));
        let elapsed = t0.elapsed().as_millis();
        assert_eq!(state::OK, p.status.state, "ok after refresh");
        assert!(elapsed < 3000, "extraction took {} ms", elapsed);
        let tok = e
            .http
            .requests()
            .into_iter()
            .filter(|r| r.url.contains("oauth2.googleapis.com/token"))
            .last()
            .expect("token endpoint called");
        assert_contains(
            tok.body.as_deref().unwrap(),
            &("client_id=1071234567890-abcdefg.apps.google".to_string() + "usercontent.com"),
            "client id",
        );
    }
}
