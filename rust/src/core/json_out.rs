//! JSON writer reproducing Node's `JSON.stringify(value, null, 2)` (port of the C# `JsonWriter`):
//! two spaces, `"key": value`, no HTML escaping, no escaped solidus, non-ASCII as-is, shortest
//! round-trip numbers without exponent, `null` for unknown. Output has no trailing newline.
use std::fmt::Write;

#[derive(Default)]
pub struct JsonWriter {
    sb: String,
    /// One flag per open container: has anything been written into it yet.
    has_content: Vec<bool>,
    /// A key has been written, so the next value needs no separator of its own.
    pending_key: bool,
}

impl JsonWriter {
    pub fn new() -> JsonWriter {
        JsonWriter::default()
    }

    pub fn into_string(self) -> String {
        self.sb
    }

    pub fn as_str(&self) -> &str {
        &self.sb
    }

    fn indent(&mut self) {
        for _ in 0..self.has_content.len() {
            self.sb.push_str("  ");
        }
    }

    fn separator(&mut self) {
        if self.has_content.is_empty() {
            return;
        }
        if *self.has_content.last().unwrap() {
            self.sb.push(',');
        }
        self.sb.push('\n');
        self.indent();
    }

    fn before_value(&mut self) {
        if !self.pending_key {
            self.separator();
        }
        self.pending_key = false;
    }

    fn after_value(&mut self) {
        self.pending_key = false;
        if let Some(last) = self.has_content.last_mut() {
            *last = true;
        }
    }

    pub fn begin_root_object(&mut self) {
        self.sb.push('{');
        self.has_content.push(false);
    }

    pub fn begin_object(&mut self) {
        self.before_value();
        self.sb.push('{');
        self.has_content.push(false);
    }

    pub fn begin_array(&mut self) {
        self.before_value();
        self.sb.push('[');
        self.has_content.push(false);
    }

    fn close_container(&mut self, end: char) {
        let had_content = self.has_content.pop().unwrap_or(false);
        self.pending_key = false;
        if had_content {
            self.sb.push('\n');
            self.indent();
        }
        self.sb.push(end);
        if let Some(last) = self.has_content.last_mut() {
            *last = true;
        }
    }

    pub fn end_object(&mut self) {
        self.close_container('}');
    }

    pub fn end_array(&mut self) {
        self.close_container(']');
    }

    pub fn key(&mut self, name: &str) {
        self.separator();
        self.sb.push('"');
        self.escape(name);
        self.sb.push_str("\": ");
        self.pending_key = true;
    }

    /// `None` writes `null`.
    pub fn string(&mut self, value: Option<&str>) {
        self.before_value();
        match value {
            None => self.sb.push_str("null"),
            Some(v) => {
                self.sb.push('"');
                self.escape(v);
                self.sb.push('"');
            }
        }
        self.after_value();
    }

    pub fn number(&mut self, value: i64) {
        self.before_value();
        let _ = write!(self.sb, "{}", value);
        self.after_value();
    }

    /// Coordinates and other optional integers.
    pub fn nullable_number(&mut self, value: Option<i64>) {
        match value {
            Some(v) => self.number(v),
            None => {
                self.before_value();
                self.sb.push_str("null");
                self.after_value();
            }
        }
    }

    pub fn bool(&mut self, value: bool) {
        self.before_value();
        self.sb.push_str(if value { "true" } else { "false" });
        self.after_value();
    }

    /// NaN / Infinity / `None` print as `null` (like JavaScript); -0 prints as `0`.
    pub fn double(&mut self, value: Option<f64>) {
        self.before_value();
        match value {
            Some(d) if d.is_finite() => {
                if d == 0.0 {
                    self.sb.push('0');
                } else {
                    let _ = write!(self.sb, "{}", d);
                }
            }
            _ => self.sb.push_str("null"),
        }
        self.after_value();
    }

    /// An unsigned integer that does not fit `number()`.
    pub fn unsigned(&mut self, value: u64) {
        self.before_value();
        let _ = write!(self.sb, "{}", value);
        self.after_value();
    }

    /// JavaScript escaping: control characters escaped, `& < > /` and non-ASCII as-is.
    fn escape(&mut self, text: &str) {
        for c in text.chars() {
            match c {
                '"' => self.sb.push_str("\\\""),
                '\\' => self.sb.push_str("\\\\"),
                '\n' => self.sb.push_str("\\n"),
                '\r' => self.sb.push_str("\\r"),
                '\t' => self.sb.push_str("\\t"),
                '\u{8}' => self.sb.push_str("\\b"),
                '\u{c}' => self.sb.push_str("\\f"),
                c if (c as u32) < 0x20 => {
                    let _ = write!(self.sb, "\\u{:04x}", c as u32);
                }
                c => self.sb.push(c),
            }
        }
    }
}

/// `JSON.stringify(value, null, 2)` for any `serde_json::Value` (port of `AdapterUtil.IndentedJson`):
/// used to rewrite credential files without dropping the keys we do not understand.
pub fn indented_json(value: &serde_json::Value) -> String {
    let mut w = JsonWriter::new();
    write_value(&mut w, value);
    w.into_string()
}

fn write_value(w: &mut JsonWriter, value: &serde_json::Value) {
    use serde_json::Value;
    match value {
        Value::Null => w.string(None),
        Value::Bool(b) => w.bool(*b),
        Value::String(s) => w.string(Some(s)),
        Value::Number(n) => {
            if let Some(i) = n.as_i64() {
                w.number(i);
            } else if let Some(u) = n.as_u64() {
                w.unsigned(u);
            } else {
                w.double(n.as_f64());
            }
        }
        Value::Array(items) => {
            w.begin_array();
            for item in items {
                write_value(w, item);
            }
            w.end_array();
        }
        Value::Object(map) => {
            w.begin_object();
            for (k, v) in map {
                w.key(k);
                write_value(w, v);
            }
            w.end_object();
        }
    }
}
