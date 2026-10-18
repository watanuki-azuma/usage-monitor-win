pub mod dpi;
pub mod menu;
pub mod registry;
pub mod single_instance;
pub mod taskbar;
pub mod tray;
pub mod window_style;
pub mod wincred;
pub fn wide(s:&str)->Vec<u16> { s.encode_utf16().chain(Some(0)).collect() }
