//! snapshot.json serialisation (port of src/SnapshotJson.cs). Same shape as the Electron/C# file so
//! usage.py and the model-router keep reading it.
use chrono::Utc;

use super::contracts::{ProviderSnapshot, ProviderStatus, Snapshot};
use super::json_out::JsonWriter;
use super::json_util as ju;
use super::models;

fn non_empty(v: &Option<String>) -> Option<&str> {
    match v {
        Some(s) if !s.is_empty() => Some(s.as_str()),
        _ => None,
    }
}

pub fn serialize(s: &Snapshot) -> String {
    let mut w = JsonWriter::new();
    w.begin_root_object();
    w.key("generated_at");
    w.string(Some(&models::to_iso(s.generated_at)));
    w.key("poll_interval_secs");
    w.number(s.poll_interval_secs as i64);

    w.key("providers");
    w.begin_array();
    for p in &s.providers {
        w.begin_object();

        w.key("id");
        w.string(Some(&p.id));
        w.key("display_name");
        w.string(p.display_name.as_deref());

        w.key("status");
        w.begin_object();
        let st = &p.status;
        w.key("state");
        w.string(Some(&st.state));
        // undefined in JavaScript => the key is left out of the file entirely.
        if let Some(h) = non_empty(&st.hint) {
            w.key("hint");
            w.string(Some(h));
        }
        if let Some(m) = non_empty(&st.message) {
            w.key("message");
            w.string(Some(m));
        }
        if let Some(r) = non_empty(&st.reason) {
            w.key("reason");
            w.string(Some(r));
        }
        if let Some(t) = st.retry_at {
            w.key("retry_at");
            w.string(Some(&models::to_iso(t)));
        }
        if let Some(a) = st.age_secs {
            w.key("age_secs");
            w.number(a as i64);
        }
        w.end_object();

        w.key("plan");
        w.string(p.plan.as_deref());

        w.key("windows");
        w.begin_array();
        for win in &p.windows {
            w.begin_object();
            w.key("kind");
            w.string(Some(&win.kind));
            w.key("label");
            w.string(Some(&win.label));
            w.key("used_pct");
            w.double(win.used_pct);
            w.key("remaining_pct");
            w.double(win.remaining_pct);
            w.key("resets_at");
            let iso = win.resets_at.map(models::to_iso);
            w.string(iso.as_deref());
            w.end_object();
        }
        w.end_array();

        if !p.footnotes.is_empty() {
            w.key("footnotes");
            w.begin_array();
            for f in &p.footnotes {
                w.string(Some(f));
            }
            w.end_array();
        }

        w.key("fetched_at");
        w.string(Some(&models::to_iso(p.fetched_at)));
        w.key("source");
        w.string(Some(&p.source));

        w.end_object();
    }
    w.end_array();
    w.end_object();
    w.into_string()
}

/// Tolerant like cache.js loadSnapshot(): a provider is only kept when it has a known id, an array
/// of windows, a status with a string state and a parseable fetched_at. Anything else yields `None`.
pub fn parse(json: Option<&str>) -> Option<Snapshot> {
    let root = ju::parse(json)?;
    let data = ju::as_map(&root)?;
    let providers = ju::as_list(ju::get(data, "providers")?)?;

    let mut kept: Vec<ProviderSnapshot> = Vec::new();
    for item in providers {
        let po = match ju::as_map(item) {
            Some(m) => m,
            None => continue,
        };
        let id = match ju::str_of(po, "id") {
            Some(id) if models::PROVIDER_IDS.contains(&id.as_str()) => id,
            _ => continue,
        };
        let windows = match ju::get(po, "windows").and_then(ju::as_list) {
            Some(l) => l,
            None => continue,
        };
        let so = match ju::get(po, "status").and_then(ju::as_map) {
            Some(m) => m,
            None => continue,
        };
        let state = match ju::str_of(so, "state") {
            Some(s) => s,
            None => continue,
        };
        let fetched = match ju::str_of(po, "fetched_at").and_then(|t| models::parse_iso(&t)) {
            Some(f) => f,
            None => continue,
        };

        let mut p = ProviderSnapshot::new(&id, fetched);
        p.display_name = ju::str_of(po, "display_name");
        p.status = ProviderStatus {
            state,
            hint: ju::str_of(so, "hint"),
            message: ju::str_of(so, "message"),
            reason: ju::str_of(so, "reason"),
            retry_at: ju::str_of(so, "retry_at").and_then(|t| models::parse_iso(&t)),
            age_secs: ju::int(ju::get(so, "age_secs")),
        };
        p.plan = ju::str_of(po, "plan");

        for wo in windows {
            let wm = match ju::as_map(wo) {
                Some(m) => m,
                None => continue,
            };
            let (kind, label) = match (ju::str_of(wm, "kind"), ju::str_of(wm, "label")) {
                (Some(k), Some(l)) => (k, l),
                _ => continue,
            };
            let null = serde_json::Value::Null;
            let used = ju::get(wm, "used_pct").unwrap_or(&null);
            let resets = ju::str_of(wm, "resets_at");
            p.windows.push(models::window_of(&kind, &label, used, resets.as_deref()));
        }

        p.footnotes = ju::str_list(ju::get(po, "footnotes"));
        p.source = ju::str_of(po, "source").unwrap_or_else(|| "none".to_string());
        kept.push(p);
    }

    if kept.is_empty() {
        return None;
    }

    let generated = ju::str_of(data, "generated_at").and_then(|t| models::parse_iso(&t));
    Some(Snapshot {
        generated_at: generated.unwrap_or_else(Utc::now),
        poll_interval_secs: ju::int(ju::get(data, "poll_interval_secs")).unwrap_or(0),
        providers: kept,
    })
}
