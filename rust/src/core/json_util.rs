//! Tolerant JSON reading helpers (port of the C# `JsonUtil`). Values are `serde_json::Value`.
use serde_json::{Map, Value};

pub type JsonMap = Map<String, Value>;

/// Trimmed text parsed as JSON; `None` for null/blank/invalid input.
pub fn parse(json: Option<&str>) -> Option<Value> {
    let text = json?.trim();
    if text.is_empty() {
        return None;
    }
    serde_json::from_str(text).ok()
}

pub fn as_map(value: &Value) -> Option<&JsonMap> {
    value.as_object()
}

pub fn as_list(value: &Value) -> Option<&Vec<Value>> {
    value.as_array()
}

pub fn get<'a>(map: &'a JsonMap, key: &str) -> Option<&'a Value> {
    map.get(key)
}

/// A string value, else `None`.
pub fn str_of(map: &JsonMap, key: &str) -> Option<String> {
    match map.get(key) {
        Some(Value::String(s)) => Some(s.clone()),
        _ => None,
    }
}

pub fn bool_of(value: Option<&Value>) -> Option<bool> {
    match value {
        Some(Value::Bool(b)) => Some(*b),
        _ => None,
    }
}

/// Any JSON number as a finite double. No clamping.
pub fn num(value: Option<&Value>) -> Option<f64> {
    match value {
        Some(Value::Number(n)) => n.as_f64().filter(|d| d.is_finite()),
        _ => None,
    }
}

/// `num()` plus numeric strings, the way JavaScript's Number() reads "poll_interval_secs".
pub fn loose_num(value: Option<&Value>) -> Option<f64> {
    if let Some(d) = num(value) {
        return Some(d);
    }
    match value {
        Some(Value::String(s)) => {
            // .NET NumberStyles.Float whitespace only (not Unicode spaces such as U+00A0)
            let t = s.trim_matches(|c: char| matches!(c, '\t'..='\r' | ' '));
            if t.is_empty() {
                return None;
            }
            let n: f64 = t.parse().ok()?;
            if n.is_finite() {
                Some(n)
            } else {
                None
            }
        }
        _ => None,
    }
}

/// Rounds half away from zero; `None` outside the i32 range.
pub fn int(value: Option<&Value>) -> Option<i32> {
    let d = loose_num(value)?;
    let rounded = d.round();
    if rounded < i32::MIN as f64 || rounded > i32::MAX as f64 {
        return None;
    }
    Some(rounded as i32)
}

/// String items of an array; everything else is dropped. Empty when not an array.
pub fn str_list(value: Option<&Value>) -> Vec<String> {
    match value {
        Some(Value::Array(items)) => items
            .iter()
            .filter_map(|v| v.as_str().map(|s| s.to_string()))
            .collect(),
        _ => Vec::new(),
    }
}
