use super::wide;
use windows_sys::Win32::{Foundation::HWND, UI::WindowsAndMessaging::*};
unsafe extern "system" fn proc(hwnd: HWND, msg: u32, wp: usize, lp: isize) -> isize {
    if msg == WM_ACTIVATE && wp & 0xffff == WA_INACTIVE as usize {
        let _ = slint::invoke_from_event_loop(|| {
            crate::ui::app_controller::dispatch("deactivate".into())
        });
    }
    let old = GetPropW(hwnd, wide("UsageMonitorRustOriginalProc").as_ptr());
    CallWindowProcW(std::mem::transmute(old), hwnd, msg, wp, lp)
}
pub fn find(title: &str) -> HWND {
    unsafe { FindWindowW(std::ptr::null(), wide(title).as_ptr()) }
}
pub fn tool(hwnd: HWND, chips: bool) {
    unsafe {
        if hwnd.is_null() {
            return;
        }
        let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
        if !chips && GetPropW(hwnd, wide("UsageMonitorRustOriginalProc").as_ptr()).is_null() {
            let old = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, proc as *const () as isize);
            SetPropW(
                hwnd,
                wide("UsageMonitorRustOriginalProc").as_ptr(),
                old as _,
            );
        }
        ShowWindow(hwnd, SW_HIDE);
        let mut ex = (ex | WS_EX_TOOLWINDOW | WS_EX_TOPMOST) & !WS_EX_APPWINDOW;
        if chips {
            ex |= WS_EX_NOACTIVATE;
        } else {
            ex &= !WS_EX_NOACTIVATE;
        }
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex as isize);
        ShowWindow(hwnd, if chips { SW_SHOWNOACTIVATE } else { SW_SHOW });
    }
}
pub fn round(hwnd: HWND, w: i32, h: i32, r: i32) {
    unsafe {
        if hwnd.is_null() {
            return;
        }
        let region =
            windows_sys::Win32::Graphics::Gdi::CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2);
        if windows_sys::Win32::Graphics::Gdi::SetWindowRgn(hwnd, region, 1) == 0 {
            windows_sys::Win32::Graphics::Gdi::DeleteObject(region);
        }
    }
}
pub fn topmost(hwnd: HWND) {
    unsafe {
        if !hwnd.is_null() && GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32 & WS_EX_TOPMOST == 0 {
            SetWindowPos(
                hwnd,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
            );
        }
    }
}
