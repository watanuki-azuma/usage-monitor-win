//! Pure visible chips model, matching ChipModel.cs (timestamps are deliberately excluded).
use crate::core::{
    contracts::{state, ProviderSnapshot, Snapshot},
    format, models,
};

#[derive(Clone, Debug, PartialEq)]
pub struct ChipInfo {
    pub id: String,
    pub text: String,
    pub color_class: &'static str,
    pub stale: bool,
}
pub fn for_provider(p: Option<&ProviderSnapshot>) -> ChipInfo {
    let w = p.and_then(models::current_window);
    ChipInfo {
        id: p.map(|p| p.id.clone()).unwrap_or_default(),
        text: format::format_used_total(w, true),
        color_class: match w {
            Some(w) if w.used_pct.is_some() => {
                if models::is_alerting(w) {
                    "red"
                } else {
                    "green"
                }
            }
            _ => "gray",
        },
        stale: p.is_some_and(|p| p.status.state == state::STALE),
    }
}
pub fn rpg_for_provider(p: Option<&ProviderSnapshot>) -> ChipInfo {
    let mut base = for_provider(p);
    let Some(w) = p.and_then(models::current_window) else {
        return base;
    };
    let Some(used) = w.used_pct else {
        return base;
    };
    let rem = w
        .remaining_pct
        .unwrap_or(100.0 - used)
        .clamp(0.0, 100.0)
        .round() as i64;
    base.text = format!("{rem}/100");
    base
}
pub fn key(s: Option<&Snapshot>) -> String {
    s.map(|s| {
        s.providers
            .iter()
            .map(|p| {
                let c = for_provider(Some(p));
                format!(
                    "{}\u{1}{}\u{1}{}\u{1}{}",
                    c.id,
                    c.text,
                    c.color_class,
                    if c.stale { "1" } else { "0" }
                )
            })
            .collect::<Vec<_>>()
            .join("\u{2}")
    })
    .unwrap_or_default()
}
