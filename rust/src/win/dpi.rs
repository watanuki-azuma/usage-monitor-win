use windows_sys::Win32::UI::HiDpi::*;
use windows_sys::Win32::UI::WindowsAndMessaging::SetProcessDPIAware;
pub fn init() {
    unsafe { SetProcessDPIAware(); }
    // Winit honors this on creation/recreation and ignores later monitor DPI events.
    // Set it before any backend or worker exists: C# uses one fixed system design scale.
    std::env::set_var("SLINT_SCALE_FACTOR",scale().to_string());
}
pub fn design_scale(dpi:i32)->f32 {
    if dpi<72 {1.} else {dpi.min(192) as f32/96.}
}
pub fn scale()->f32 {
    static SCALE:std::sync::OnceLock<f32>=std::sync::OnceLock::new();
    *SCALE.get_or_init(||unsafe{
    use windows_sys::Win32::Graphics::Gdi::*;
    let mut dpi=GetDpiForSystem() as i32;
    if !(72..=480).contains(&dpi) {
        let dc=GetDC(std::ptr::null_mut());
        if !dc.is_null(){dpi=GetDeviceCaps(dc,LOGPIXELSY as i32);ReleaseDC(std::ptr::null_mut(),dc);}
    }
    design_scale(dpi)
    })
}
#[cfg(test)]
mod tests {
    #[test] fn system_design_scale_has_csharp_fallback_and_cap() {
        for (dpi,expected) in [(0,1.),(71,1.),(72,0.75),(96,1.),(144,1.5),(192,2.),(288,2.),(480,2.)] {
            assert_eq!(super::design_scale(dpi),expected);
        }
    }
}
