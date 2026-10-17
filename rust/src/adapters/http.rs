//! Real HTTP transport (port of src/Http.cs) on `ureq` 3 with native-tls (SChannel).
//!
//! Rules (same as the C# version):
//!   * only http/https, anything else is rejected up front
//!   * `timeout_ms` covers the whole exchange (connect, request, headers, body)
//!   * a response body is capped at 2 MiB (an error when exceeded)
//!   * non-2xx statuses are *returned*, never turned into errors
//!   * header names are lower-cased in `HttpResult.headers`
//!   * redirects are never followed, so credentials can never travel to another host
//!   * a fresh agent per request (no connection pooling, like `KeepAlive = false`)
use std::collections::HashMap;
use std::time::Duration;

use serde_json::Value;
use ureq::tls::{RootCerts, TlsConfig, TlsProvider};

use crate::core::contracts::{Http, HttpError, HttpResult};
use crate::core::json_util;

pub const DEFAULT_TIMEOUT_MS: u32 = 8000;
pub const MAX_BODY_BYTES: usize = 2 * 1024 * 1024;

#[derive(Clone, Copy, Debug, Default)]
pub struct RealHttp;

impl RealHttp {
    pub fn new() -> RealHttp {
        RealHttp
    }
}

fn err(text: impl Into<String>) -> HttpError {
    HttpError(text.into())
}

fn parse_url(url: &str) -> Result<String, HttpError> {
    let trimmed = url.trim();
    if trimmed.is_empty() {
        return Err(err("url is empty"));
    }
    let lower = trimmed.to_ascii_lowercase();
    let scheme = match lower.find("://") {
        Some(i) => &lower[..i],
        None => return Err(err(format!("not an absolute URL: {}", url))),
    };
    if scheme != "http" && scheme != "https" {
        return Err(err(format!("unsupported protocol: {}", scheme)));
    }
    Ok(trimmed.to_string())
}

impl Http for RealHttp {
    fn request(
        &self,
        method: &str,
        url: &str,
        headers: &[(&str, &str)],
        body: Option<&[u8]>,
        timeout_ms: u32,
    ) -> Result<HttpResult, HttpError> {
        let timeout = if timeout_ms > 0 { timeout_ms } else { DEFAULT_TIMEOUT_MS };
        let url = parse_url(url)?;
        let verb = {
            let m = method.trim().to_ascii_uppercase();
            if m.is_empty() {
                "GET".to_string()
            } else {
                m
            }
        };

        let tls = TlsConfig::builder()
            .provider(TlsProvider::NativeTls)
            .root_certs(RootCerts::PlatformVerifier)
            .build();
        let config = ureq::Agent::config_builder()
            .http_status_as_error(false)
            .max_redirects(0)
            .timeout_global(Some(Duration::from_millis(timeout as u64)))
            .tls_config(tls)
            .build();
        let agent: ureq::Agent = config.into();

        let mut builder = ureq::http::Request::builder().method(verb.as_str()).uri(url.as_str());
        for (name, value) in headers {
            let name = name.trim();
            if name.is_empty() {
                continue;
            }
            builder = builder.header(name, *value);
        }

        let response = match body {
            Some(bytes) if !bytes.is_empty() => {
                let request = builder.body(bytes.to_vec()).map_err(|e| err(e.to_string()))?;
                agent.run(request)
            }
            _ => {
                let request = builder.body(()).map_err(|e| err(e.to_string()))?;
                agent.run(request)
            }
        };
        let mut response = response.map_err(|e| err(e.to_string()))?;

        let status = response.status().as_u16();
        let mut header_map: HashMap<String, String> = HashMap::new();
        for (name, value) in response.headers().iter() {
            let key = name.as_str().to_ascii_lowercase();
            let text = String::from_utf8_lossy(value.as_bytes()).into_owned();
            // Multi-valued headers arrive comma-joined, like Node's headers map.
            match header_map.get_mut(&key) {
                Some(existing) => {
                    existing.push(',');
                    existing.push_str(&text);
                }
                None => {
                    header_map.insert(key, text);
                }
            }
        }

        let raw = response
            .body_mut()
            .with_config()
            .limit(MAX_BODY_BYTES as u64 + 1)
            .read_to_vec()
            .map_err(|e| err(e.to_string()))?;
        if raw.len() > MAX_BODY_BYTES {
            return Err(err(format!("response body exceeds {} bytes", MAX_BODY_BYTES)));
        }
        let text = if raw.is_empty() { String::new() } else { String::from_utf8_lossy(&raw).into_owned() };
        let json = json_util::parse(Some(&text));
        Ok(HttpResult { status, text, raw, headers: header_map, json })
    }
}

// ----------------------------------------------------------------------------------------------
// Convenience wrappers around Http::request (reference/js/http.js getJson / postJson / postForm).
// `json` is parsed from the response text when it is valid JSON and left `None` otherwise.
// ----------------------------------------------------------------------------------------------

pub fn get_json(http: &dyn Http, url: &str, headers: &[(&str, &str)]) -> Result<HttpResult, HttpError> {
    let result = http.request("GET", url, headers, None, DEFAULT_TIMEOUT_MS)?;
    Ok(with_json(result))
}

pub fn post_json(http: &dyn Http, url: &str, headers: &[(&str, &str)], body: &Value) -> Result<HttpResult, HttpError> {
    let merged = merge(headers, "Content-Type", "application/json");
    let payload = serde_json::to_vec(body).map_err(|e| err(e.to_string()))?;
    let refs: Vec<(&str, &str)> = merged.iter().map(|(k, v)| (k.as_str(), v.as_str())).collect();
    let result = http.request("POST", url, &refs, Some(&payload), DEFAULT_TIMEOUT_MS)?;
    Ok(with_json(result))
}

pub fn post_form(
    http: &dyn Http,
    url: &str,
    headers: &[(&str, &str)],
    fields: &[(&str, &str)],
) -> Result<HttpResult, HttpError> {
    let merged = merge(headers, "Content-Type", "application/x-www-form-urlencoded");
    let payload = form_body(fields);
    let refs: Vec<(&str, &str)> = merged.iter().map(|(k, v)| (k.as_str(), v.as_str())).collect();
    let result = http.request("POST", url, &refs, Some(payload.as_bytes()), DEFAULT_TIMEOUT_MS)?;
    Ok(with_json(result))
}

/// Caller headers win over the default (same key, ordinal compare), like a dictionary assignment.
fn merge(headers: &[(&str, &str)], name: &str, default_value: &str) -> Vec<(String, String)> {
    let mut merged: Vec<(String, String)> = vec![(name.to_string(), default_value.to_string())];
    for (k, v) in headers {
        match merged.iter_mut().find(|(mk, _)| mk == k) {
            Some(slot) => slot.1 = (*v).to_string(),
            None => merged.push(((*k).to_string(), (*v).to_string())),
        }
    }
    merged
}

fn with_json(mut result: HttpResult) -> HttpResult {
    if result.json.is_none() {
        result.json = json_util::parse(Some(&result.text));
    }
    result
}

/// application/x-www-form-urlencoded as `Uri.EscapeDataString` does it (RFC 3986 unreserved only).
fn form_body(fields: &[(&str, &str)]) -> String {
    let mut out = String::new();
    for (i, (k, v)) in fields.iter().enumerate() {
        if i > 0 {
            out.push('&');
        }
        escape_data(k, &mut out);
        out.push('=');
        escape_data(v, &mut out);
    }
    out
}

fn escape_data(text: &str, out: &mut String) {
    for b in text.bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{:02X}", b));
        }
    }
}
