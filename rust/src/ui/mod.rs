#[cfg(windows)]
pub mod app_controller;
pub mod chip_model;
pub mod render_test;
pub mod skins;
pub mod taskbar_math;

slint::slint! {
    export { Chips, Flyout, ChipData, CardData, LineData, HeaderButtonData } from "ui/classic/app.slint";
}

#[cfg(test)]
mod tests;
