use windows_sys::Win32::UI::HiDpi::*;
use windows_sys::Win32::UI::WindowsAndMessaging::SetProcessDPIAware;
pub fn init() { unsafe { SetProcessDPIAware(); } }
pub fn scale()->f32 {unsafe{
    use windows_sys::Win32::Graphics::Gdi::*;
    let mut dpi=GetDpiForSystem() as i32;
    if !(72..=480).contains(&dpi) {
        let dc=GetDC(std::ptr::null_mut());
        if !dc.is_null(){dpi=GetDeviceCaps(dc,LOGPIXELSY as i32);ReleaseDC(std::ptr::null_mut(),dc);}
    }
    if dpi<72{dpi=96;}
    dpi.min(192) as f32/96.
}}
