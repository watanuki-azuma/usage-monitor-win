//! Small helpers shared by the adapters (port of src/Adapters/AdapterUtil.cs).
//!
//! The C# helpers take "whatever the JSON parser produced" (object) and answer null for anything
//! odd. Here the same shapes are `Option<&Value>`: a JSON `null` counts as absent, exactly like a
//! null in the C# dictionaries.
use std::io;
use std::path::Path;
use std::time::SystemTime;

use chrono::{DateTime, Utc};
use serde_json::{Map, Value};

use crate::core::contracts::{HttpError, ProviderSnapshot, ProviderStatus};
use crate::core::json_util;
use crate::core::models;

/// A JSON null to hand to `models::window_of` when a value is absent.
pub static NULL: Value = Value::Null;

pub fn or_null(value: Option<&Value>) -> &Value {
    value.unwrap_or(&NULL)
}

/// `Value` for an optional double (`None` and non-finite numbers become null).
pub fn f64_value(value: Option<f64>) -> Value {
    match value {
        Some(d) => Value::from(d),
        None => Value::Null,
    }
}

/// The value at `key` of an object, `None` when missing, JSON null or not an object.
pub fn get<'a>(container: Option<&'a Value>, key: &str) -> Option<&'a Value> {
    container?.as_object()?.get(key).filter(|v| !v.is_null())
}

/// The object at `key`.
pub fn map_of<'a>(container: Option<&'a Value>, key: &str) -> Option<&'a Value> {
    get(container, key).filter(|v| v.is_object())
}

/// The array at `key`.
pub fn list_of<'a>(container: Option<&'a Value>, key: &str) -> Option<&'a Vec<Value>> {
    get(container, key)?.as_array()
}

pub fn get_path<'a>(container: Option<&'a Value>, first: &str, second: &str) -> Option<&'a Value> {
    get(map_of(container, first), second)
}

/// A string value (any string, also ""), else `None`.
pub fn str_of(container: Option<&Value>, key: &str) -> Option<String> {
    get(container, key)?.as_str().map(|s| s.to_string())
}

pub fn str_path(container: Option<&Value>, first: &str, second: &str) -> Option<String> {
    str_of(map_of(container, first), second)
}

pub fn has(container: Option<&Value>, key: &str) -> bool {
    get(container, key).is_some()
}

/// Truthy string check: "" counts as absent, exactly like JavaScript's `if (x.y)`.
pub fn has_text(container: Option<&Value>, key: &str) -> bool {
    matches!(str_of(container, key), Some(s) if !s.is_empty())
}

/// `JsonUtil.LooseNum`: numbers and numeric strings.
pub fn num(value: Option<&Value>) -> Option<f64> {
    json_util::loose_num(value)
}

/// First non-empty string among `keys` (the C# `FirstText`).
pub fn first_text(container: Option<&Value>, keys: &[&str]) -> Option<String> {
    for key in keys {
        if let Some(s) = str_of(container, key) {
            if !s.is_empty() {
                return Some(s);
            }
        }
    }
    None
}

/// Used for plan capitalisation and fallback labels.
pub fn capitalize(text: &str) -> String {
    let mut chars = text.chars();
    match chars.next() {
        None => String::new(),
        Some(first) => {
            let mut out: String = first.to_uppercase().collect();
            out.push_str(chars.as_str());
            out
        }
    }
}

/// JS Math.round() ties away from zero (.NET's default ties to even); saturates, NaN -> i32::MIN
/// (what the C# unchecked cast yields).
pub fn round_i32(value: f64) -> i32 {
    if value.is_nan() {
        return i32::MIN;
    }
    let rounded = value.round();
    if rounded < i32::MIN as f64 {
        return i32::MIN;
    }
    if rounded > i32::MAX as f64 {
        return i32::MAX;
    }
    rounded as i32
}

/// `AdapterUtil.NumText`: strings as they are, numbers as `0.####`.
pub fn num_text(value: Option<&Value>) -> String {
    let value = match value {
        None | Some(Value::Null) => return "null".to_string(),
        Some(v) => v,
    };
    if let Value::String(s) = value {
        return s.clone();
    }
    if let Value::Bool(b) = value {
        return if *b { "true" } else { "false" }.to_string();
    }
    match num(Some(value)) {
        Some(d) => {
            let mut text = format!("{:.4}", d);
            if text.contains('.') {
                while text.ends_with('0') {
                    text.pop();
                }
                if text.ends_with('.') {
                    text.pop();
                }
            }
            if text == "-0" {
                text = "0".to_string();
            }
            text
        }
        None => value.to_string(),
    }
}

/// `new Date(ms)`: epoch milliseconds to UTC, `None` when the value is unusable.
pub fn from_epoch_ms(ms: f64) -> Option<DateTime<Utc>> {
    if ms.is_nan() || ms.is_infinite() {
        return None;
    }
    if !(-62135596800000.0..=253402300799999.0).contains(&ms) {
        return None; // outside DateTime's range
    }
    DateTime::<Utc>::from_timestamp_millis(ms.round() as i64)
}

pub fn to_epoch_ms(utc: DateTime<Utc>) -> f64 {
    utc.timestamp_millis() as f64
}

/// A provider with the given status and no windows.
pub fn empty(
    id: &str,
    display_name: &str,
    state: &str,
    message: Option<&str>,
    hint: Option<&str>,
    now: DateTime<Utc>,
) -> ProviderSnapshot {
    let status = ProviderStatus {
        state: state.to_string(),
        message: message.map(|s| s.to_string()),
        hint: hint.map(|s| s.to_string()),
        ..ProviderStatus::default()
    };
    models::empty_provider(id, display_name, status, now)
}

/// fetch_failed from a transport error (message = the error text).
pub fn fetch_failed(id: &str, display_name: &str, error: &HttpError, now: DateTime<Utc>) -> ProviderSnapshot {
    let message = if error.0.is_empty() { "transport error" } else { error.0.as_str() };
    empty(id, display_name, crate::core::contracts::state::FETCH_FAILED, Some(message), None, now)
}

/// fetch_failed + the in-memory rate-limit flag.
pub fn rate_limited(id: &str, display_name: &str, now: DateTime<Utc>) -> ProviderSnapshot {
    let mut p = empty(id, display_name, crate::core::contracts::state::FETCH_FAILED, Some("rate limited"), None, now);
    p.rate_limited = true;
    p
}

pub fn new_ok(id: &str, display_name: &str, now: DateTime<Utc>) -> ProviderSnapshot {
    let mut p = ProviderSnapshot::new(id, now);
    p.display_name = Some(display_name.to_string());
    p.status = ProviderStatus { state: crate::core::contracts::state::OK.to_string(), ..ProviderStatus::default() };
    p.source = "api".to_string();
    p
}

// ----------------------------------------------------------------------------------------------
// files
// ----------------------------------------------------------------------------------------------

/// `File.ReadAllText`: the encoding comes from a byte order mark (UTF-32 LE/BE, UTF-16 LE/BE,
/// UTF-8; UTF-32 LE is checked before UTF-16 LE as in .NET); without one the file is UTF-8.
/// Invalid data is replaced with U+FFFD.
pub fn read_text(path: &str) -> io::Result<String> {
    Ok(decode_text(&std::fs::read(path)?))
}

pub fn decode_text(bytes: &[u8]) -> String {
    fn utf32(rest: &[u8], f: fn([u8; 4]) -> u32) -> String {
        rest.chunks(4)
            .map(|c| <[u8; 4]>::try_from(c).ok().map(f).and_then(char::from_u32).unwrap_or('\u{FFFD}'))
            .collect()
    }
    fn utf16(rest: &[u8], f: fn([u8; 2]) -> u16) -> String {
        let (whole, tail) = rest.split_at(rest.len() & !1);
        let mut out: String = char::decode_utf16(whole.chunks(2).map(|c| f([c[0], c[1]])))
            .map(|r| r.unwrap_or('\u{FFFD}'))
            .collect();
        if !tail.is_empty() {
            out.push('\u{FFFD}');
        }
        out
    }
    if let Some(rest) = bytes.strip_prefix(&[0xFF, 0xFE, 0x00, 0x00][..]) {
        return utf32(rest, u32::from_le_bytes);
    }
    if let Some(rest) = bytes.strip_prefix(&[0x00, 0x00, 0xFE, 0xFF][..]) {
        return utf32(rest, u32::from_be_bytes);
    }
    if let Some(rest) = bytes.strip_prefix(&[0xFF, 0xFE][..]) {
        return utf16(rest, u16::from_le_bytes);
    }
    if let Some(rest) = bytes.strip_prefix(&[0xFE, 0xFF][..]) {
        return utf16(rest, u16::from_be_bytes);
    }
    let bytes = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF][..]).unwrap_or(bytes);
    String::from_utf8_lossy(bytes).into_owned()
}

/// .NET Framework `ToString("F2", InvariantCulture)` for a double: the value is first cut to
/// 15 significant digits, then rounded half away from zero (so 1.125 gives "1.13", where Rust's
/// `{:.2}` gives "1.12").
pub fn format_f2(x: f64) -> String {
    if !x.is_finite() || x.abs() >= 1e15 {
        return format!("{:.2}", x);
    }
    let s = format!("{:.14e}", x.abs()); // d.dddddddddddddde<exp>: 15 significant digits
    let (mantissa, exp) = s.split_once('e').unwrap_or((&s, "0"));
    let digits: u64 = mantissa.replace('.', "").parse().unwrap_or(0);
    let exp: i32 = exp.parse().unwrap_or(0);
    // value = digits * 10^(exp - 14); hundredths = digits * 10^(exp - 12)
    let shift = exp - 12;
    let hundredths: u64 = if shift >= 0 {
        digits * 10u64.pow(shift as u32)
    } else if -shift > 15 {
        0
    } else {
        let div = 10u64.pow((-shift) as u32);
        let (q, r) = (digits / div, digits % div);
        if r * 2 >= div { q + 1 } else { q }
    };
    let sign = if x < 0.0 && hundredths != 0 { "-" } else { "" };
    format!("{}{}.{:02}", sign, hundredths / 100, hundredths % 100)
}

pub fn file_exists(path: &str) -> bool {
    !path.is_empty() && Path::new(path).is_file()
}

/// Last write time, `None` when the file cannot be stat'ed.
pub fn mtime(path: &str) -> Option<SystemTime> {
    std::fs::metadata(path).and_then(|m| m.modified()).ok()
}

pub fn delete_quietly(path: &str) {
    if file_exists(path) {
        let _ = std::fs::remove_file(path);
    }
}

/// Writes `text` to `<path>.tmp` and swaps it in with [`replace_file`]. The caller removes the
/// temp copy on failure, but only while `path` still exists (see `replace_file`).
pub fn write_replace(path: &str, text: &str) -> io::Result<()> {
    let tmp = format!("{}.tmp", path);
    std::fs::write(&tmp, text.as_bytes())?;
    replace_file(&tmp, path)
}

/// `File.Replace(src, dest, null)`: `ReplaceFileW`, which keeps the destination's security
/// descriptor (DACL), owner, attributes and creation time instead of taking the temp file's.
/// The destination must exist. When the call fails after the destination was already unlinked
/// (`ERROR_UNABLE_TO_MOVE_REPLACEMENT`), `src` is the only copy left, so callers must not delete
/// it unless `dest` still exists.
pub fn replace_file(src: &str, dest: &str) -> io::Result<()> {
    use windows_sys::Win32::Storage::FileSystem::ReplaceFileW;
    let wide = |s: &str| s.encode_utf16().chain(std::iter::once(0)).collect::<Vec<u16>>();
    let (src_w, dest_w) = (wide(src), wide(dest));
    // SAFETY: both strings are NUL-terminated and outlive the call; no backup file, no reserved data.
    let ok = unsafe {
        ReplaceFileW(dest_w.as_ptr(), src_w.as_ptr(), std::ptr::null(), 0, std::ptr::null(), std::ptr::null())
    };
    if ok != 0 {
        Ok(())
    } else {
        Err(io::Error::last_os_error())
    }
}

pub fn copy_map(value: Option<&Value>) -> Map<String, Value> {
    match value {
        Some(Value::Object(m)) => m.clone(),
        _ => Map::new(),
    }
}
