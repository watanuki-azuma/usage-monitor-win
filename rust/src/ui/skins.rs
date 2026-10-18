//! R4 registry: choices are retained; all four use classic components until R5.
pub use crate::core::app_config::{normalize_skin, SKIN_IDS};
pub const NAMES: [&str; 4] = ["Classic", "RPG", "Fluent", "Terminal"];
pub fn name_of(id: Option<&str>) -> &'static str {
    NAMES[SKIN_IDS.iter().position(|v| *v == normalize_skin(id)).unwrap()]
}
pub fn component_skin(_id: Option<&str>) -> &'static str { "classic" }
