pub mod chip_model;
pub mod taskbar_math;
pub mod skins;
pub mod render_test;
#[cfg(windows)]
pub mod app_controller;

slint::slint! {
    export { Chips, Flyout, ChipData, CardData, LineData, HeaderButtonData } from "ui/classic/app.slint";
}

#[cfg(test)]
mod tests;
