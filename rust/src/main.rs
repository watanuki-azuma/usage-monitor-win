#![windows_subsystem = "windows"]

// UI comes from ui/*.slint through the slint! macro. Do not switch to slint-build/build.rs: its
// build-time font discovery (DirectWrite) crashes under the windows-gnu toolchain used here.
slint::slint! {
    export { Placeholder } from "ui/app.slint";
}

fn main() -> Result<(), slint::PlatformError> {
    // R0: proves the toolchain builds and links a Slint app; `--smoke` exits right away.
    if std::env::args().any(|a| a == "--smoke") {
        return Ok(());
    }
    Placeholder::new()?.run()
}
