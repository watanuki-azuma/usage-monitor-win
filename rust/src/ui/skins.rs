//! Skin registry: maps normalized skin IDs to display names, active Slint skin, and window radii.
pub use crate::core::app_config::{normalize_skin, SKIN_IDS};
pub const NAMES: [&str; 4] = ["Classic", "RPG", "Fluent", "Terminal"];
pub fn name_of(id: Option<&str>) -> &'static str {
    NAMES[SKIN_IDS.iter().position(|v| *v == normalize_skin(id)).unwrap()]
}
pub fn component_skin(id: Option<&str>) -> &'static str {
    normalize_skin(id)
}
pub fn chips_radius(id: Option<&str>) -> f32 {
    match normalize_skin(id) {
        "fluent" => 8.0,
        _ => 6.0,
    }
}
pub fn flyout_radius(id: Option<&str>) -> f32 {
    match normalize_skin(id) {
        "fluent" => 8.0,
        "terminal" => 10.0,
        _ => 12.0,
    }
}
