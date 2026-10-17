//! config.json (port of src/AppConfig.cs).
use std::collections::HashMap;

use super::json_out::JsonWriter;
use super::json_util as ju;

pub const ALLOWED_INTERVALS: [i32; 4] = [5, 15, 30, 60];

/// Skin ids (SkinRegistry.Ids in the C# app).
pub const SKIN_IDS: [&str; 4] = ["classic", "rpg", "fluent", "terminal"];

/// Only these three adapters have a refresh_tokens switch in the file; anything else still
/// defaults to true, like JS's `!== false`.
const ADAPTER_IDS: [&str; 3] = ["claude", "gemini", "grok"];

/// Unknown, empty or missing ids map to "classic" (SkinRegistry.Normalize).
pub fn normalize_skin(id: Option<&str>) -> &'static str {
    match id {
        Some(i) => SKIN_IDS.iter().copied().find(|s| *s == i).unwrap_or("classic"),
        None => "classic",
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct AppConfig {
    pub poll_interval_secs: i32,
    pub config_version: i32,
    pub chips_docked: bool,
    pub chips_hidden: bool,
    pub chips_x: Option<i32>,
    pub chips_y: Option<i32>,
    pub chips_dock_x: Option<i32>,
    pub chips_dock_y: Option<i32>,
    pub flyout_docked: bool,
    pub flyout_pinned: bool,
    pub flyout_x: Option<i32>,
    pub flyout_y: Option<i32>,
    pub flyout_dock_x: Option<i32>,
    pub flyout_dock_y: Option<i32>,
    pub autostart: bool,
    /// Kept only so the file stays compatible; the native app has no updater.
    pub check_updates_on_startup: bool,
    pub notify_on_limit_reached: bool,
    /// classic, rpg, fluent or terminal.
    pub skin: String,
    adapters: HashMap<String, bool>,
}

impl Default for AppConfig {
    fn default() -> AppConfig {
        let mut adapters = HashMap::new();
        for id in ADAPTER_IDS.iter() {
            adapters.insert(id.to_string(), true);
        }
        AppConfig {
            poll_interval_secs: 5,
            config_version: 5,
            chips_docked: true,
            chips_hidden: false,
            chips_x: None,
            chips_y: None,
            chips_dock_x: None,
            chips_dock_y: None,
            flyout_docked: false,
            flyout_pinned: false,
            flyout_x: None,
            flyout_y: None,
            flyout_dock_x: None,
            flyout_dock_y: None,
            autostart: true,
            check_updates_on_startup: true,
            notify_on_limit_reached: true,
            skin: "classic".to_string(),
            adapters,
        }
    }
}

impl AppConfig {
    pub fn refresh_tokens(&self, adapter_id: &str) -> bool {
        if adapter_id.is_empty() {
            return true;
        }
        self.adapters.get(adapter_id).copied().unwrap_or(true)
    }

    pub fn set_refresh_tokens(&mut self, adapter_id: &str, value: bool) {
        if adapter_id.is_empty() {
            return;
        }
        self.adapters.insert(adapter_id.to_string(), value);
    }

    /// Tolerant like config.js normalize(): junk, arrays, non-objects and wrong-typed values fall
    /// back to the defaults; every call builds a fresh object.
    pub fn parse(json: Option<&str>) -> AppConfig {
        let mut c = AppConfig::default();
        let root = match ju::parse(json) {
            Some(r) => r,
            None => return c,
        };
        let raw = match ju::as_map(&root) {
            Some(m) => m,
            None => return c,
        };

        c.poll_interval_secs = interval_of(raw);
        c.config_version = int_of(raw, "config_version").unwrap_or(5);
        c.chips_docked = bool_of(raw, "chips_docked", true);
        c.chips_hidden = bool_of(raw, "chips_hidden", false);
        c.chips_x = int_of(raw, "chips_x");
        c.chips_y = int_of(raw, "chips_y");
        c.chips_dock_x = int_of(raw, "chips_dock_x");
        c.chips_dock_y = int_of(raw, "chips_dock_y");
        c.flyout_docked = bool_of(raw, "flyout_docked", false);
        c.flyout_pinned = bool_of(raw, "flyout_pinned", false);
        c.flyout_x = int_of(raw, "flyout_x");
        c.flyout_y = int_of(raw, "flyout_y");
        c.flyout_dock_x = int_of(raw, "flyout_dock_x");
        c.flyout_dock_y = int_of(raw, "flyout_dock_y");
        c.autostart = bool_of(raw, "autostart", true);
        c.check_updates_on_startup = bool_of(raw, "check_updates_on_startup", true);
        c.notify_on_limit_reached = bool_of(raw, "notify_on_limit_reached", true);
        c.skin = normalize_skin(raw.get("skin").and_then(|v| v.as_str())).to_string();

        let raw_adapters = ju::get(raw, "adapters").and_then(ju::as_map);
        for id in ADAPTER_IDS.iter() {
            let entry = raw_adapters.and_then(|a| ju::get(a, id)).and_then(ju::as_map);
            let flag = entry.and_then(|e| ju::get(e, "refresh_tokens"));
            // Only a literal false turns refreshing off, exactly like the JS `!== false`.
            c.adapters.insert(id.to_string(), !matches!(flag, Some(serde_json::Value::Bool(false))));
        }
        c
    }

    /// Key order matches the Electron config.json.
    pub fn to_json(&self) -> String {
        let mut w = JsonWriter::new();
        w.begin_root_object();

        w.key("poll_interval_secs");
        w.number(self.poll_interval_secs as i64);
        w.key("config_version");
        w.number(self.config_version as i64);
        w.key("chips_docked");
        w.bool(self.chips_docked);
        w.key("chips_hidden");
        w.bool(self.chips_hidden);
        w.key("chips_x");
        w.nullable_number(self.chips_x.map(i64::from));
        w.key("chips_y");
        w.nullable_number(self.chips_y.map(i64::from));
        w.key("chips_dock_x");
        w.nullable_number(self.chips_dock_x.map(i64::from));
        w.key("chips_dock_y");
        w.nullable_number(self.chips_dock_y.map(i64::from));
        w.key("flyout_docked");
        w.bool(self.flyout_docked);
        w.key("flyout_pinned");
        w.bool(self.flyout_pinned);
        w.key("flyout_x");
        w.nullable_number(self.flyout_x.map(i64::from));
        w.key("flyout_y");
        w.nullable_number(self.flyout_y.map(i64::from));
        w.key("flyout_dock_x");
        w.nullable_number(self.flyout_dock_x.map(i64::from));
        w.key("flyout_dock_y");
        w.nullable_number(self.flyout_dock_y.map(i64::from));
        w.key("autostart");
        w.bool(self.autostart);
        w.key("check_updates_on_startup");
        w.bool(self.check_updates_on_startup);
        w.key("notify_on_limit_reached");
        w.bool(self.notify_on_limit_reached);
        w.key("skin");
        w.string(Some(normalize_skin(Some(&self.skin))));

        w.key("adapters");
        w.begin_object();
        for id in ADAPTER_IDS.iter() {
            w.key(id);
            w.begin_object();
            w.key("refresh_tokens");
            w.bool(self.refresh_tokens(id));
            w.end_object();
        }
        w.end_object();

        w.end_object();
        w.into_string()
    }
}

/// A boolean written as the string "false" (or anything non-boolean) keeps the default.
fn bool_of(map: &ju::JsonMap, key: &str, fallback: bool) -> bool {
    ju::bool_of(ju::get(map, key)).unwrap_or(fallback)
}

/// Finite numbers (numeric strings too), rounded half away from zero; else `None`.
fn int_of(map: &ju::JsonMap, key: &str) -> Option<i32> {
    ju::int(ju::get(map, key))
}

fn interval_of(map: &ju::JsonMap) -> i32 {
    match int_of(map, "poll_interval_secs") {
        Some(c) if ALLOWED_INTERVALS.contains(&c) => c,
        _ => 5,
    }
}
