use super::wide;
use windows_sys::Win32::{Foundation::HWND, UI::WindowsAndMessaging::*};
const ORIGINAL_PROC: &str = "UsageMonitorRustOriginalProc";
const IS_FLYOUT: &str = "UsageMonitorRustIsFlyout";
unsafe extern "system" fn proc(hwnd: HWND, msg: u32, wp: usize, lp: isize) -> isize {
    let old: WNDPROC = std::mem::transmute(GetPropW(hwnd, wide(ORIGINAL_PROC).as_ptr()));
    if msg == WM_ACTIVATE
        && wp & 0xffff == WA_INACTIVE as usize
        && !GetPropW(hwnd, wide(IS_FLYOUT).as_ptr()).is_null()
    {
        let _ = slint::invoke_from_event_loop(|| {
            crate::ui::app_controller::dispatch("deactivate".into())
        });
    }
    match msg {
        // DefWindowProc paints an old-style title bar over the client area on activation changes
        // (opening the tray menu, showing the flyout); lParam -1 keeps the activation bookkeeping
        // but skips that repaint.
        WM_NCACTIVATE => CallWindowProcW(old, hwnd, msg, wp, -1),
        // Same for title/icon changes (Slint sets the title on every property update): run them
        // with WS_VISIBLE cleared so nothing is painted, like Chromium's redraw lock.
        WM_SETTEXT | WM_SETICON => {
            let style = GetWindowLongPtrW(hwnd, GWL_STYLE);
            let visible = style & WS_VISIBLE as isize != 0;
            if visible {
                SetWindowLongPtrW(hwnd, GWL_STYLE, style & !(WS_VISIBLE as isize));
            }
            let result = CallWindowProcW(old, hwnd, msg, wp, lp);
            if visible {
                SetWindowLongPtrW(hwnd, GWL_STYLE, style);
            }
            result
        }
        _ => CallWindowProcW(old, hwnd, msg, wp, lp),
    }
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
        if GetPropW(hwnd, wide(ORIGINAL_PROC).as_ptr()).is_null() {
            if !chips {
                SetPropW(hwnd, wide(IS_FLYOUT).as_ptr(), 1 as _);
            }
            let old = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, proc as *const () as isize);
            SetPropW(hwnd, wide(ORIGINAL_PROC).as_ptr(), old as _);
        }
        ShowWindow(hwnd, SW_HIDE);
        frameless(hwnd);
        let mut ex = (ex | WS_EX_TOOLWINDOW | WS_EX_TOPMOST) & !WS_EX_APPWINDOW;
        if chips {
            ex |= WS_EX_NOACTIVATE;
        } else {
            ex &= !WS_EX_NOACTIVATE;
        }
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex as isize);
        ShowWindow(hwnd, if chips { SW_SHOWNOACTIVATE } else { SW_SHOW });
        repaint_all(hwnd);
    }
}
/// The software renderer only redraws what it knows changed, so after the hide/show above the
/// window stayed empty. A 1 px size change and back makes Slint repaint the whole window; the two
/// steps run from timers so Slint sees each size (both in one pass cancel out).
fn repaint_all(hwnd: HWND) {
    fn resize(hwnd: HWND, w: i32, h: i32) {
        unsafe {
            if IsWindow(hwnd) != 0 {
                let flags = SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE;
                SetWindowPos(hwnd, std::ptr::null_mut(), 0, 0, w, h, flags);
            }
        }
    }
    let mut r = unsafe { std::mem::zeroed::<windows_sys::Win32::Foundation::RECT>() };
    if unsafe { GetWindowRect(hwnd, &mut r) } == 0 {
        return;
    }
    let (w, h) = (r.right - r.left, r.bottom - r.top);
    let id = hwnd as isize;
    let step = std::time::Duration::from_millis(40);
    slint::Timer::single_shot(step, move || resize(id as HWND, w, h + 1));
    slint::Timer::single_shot(step * 2, move || resize(id as HWND, w, h));
}
/// Makes the window a plain popup without a caption. winit keeps `WS_CAPTION` on undecorated
/// windows and hides it by answering `WM_NCCALCSIZE`; when that does not happen the windows showed
/// a bare title bar (and a caption window cannot be smaller than the system minimum height).
/// Cheap when the style is already right, so the 1 s tick can call it as a watchdog.
pub fn frameless(hwnd: HWND) {
    unsafe {
        if hwnd.is_null() {
            return;
        }
        let style = GetWindowLongPtrW(hwnd, GWL_STYLE) as u32;
        let next = (style
            & !(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX))
            | WS_POPUP;
        if next != style {
            SetWindowLongPtrW(hwnd, GWL_STYLE, next as isize);
            SetWindowPos(
                hwnd,
                std::ptr::null_mut(),
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED,
            );
        }
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
