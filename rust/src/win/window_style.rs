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
        // winit rewrites both styles on every show/hide (adding the caption and dropping
        // WS_EX_TOOLWINDOW); keep ours instead of re-applying them with a hide/show.
        WM_STYLECHANGING => {
            let change = &mut *(lp as *mut STYLESTRUCT);
            let chips = GetPropW(hwnd, wide(IS_FLYOUT).as_ptr()).is_null();
            if wp as i32 == GWL_STYLE {
                change.styleNew = popup_style(change.styleNew);
            } else if wp as i32 == GWL_EXSTYLE {
                change.styleNew = tool_ex_style(change.styleNew, chips);
            }
            CallWindowProcW(old, hwnd, msg, wp, lp)
        }
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
fn popup_style(style: u32) -> u32 {
    (style & !(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX))
        | WS_POPUP
}
fn tool_ex_style(ex: u32, chips: bool) -> u32 {
    let ex = (ex | WS_EX_TOOLWINDOW | WS_EX_TOPMOST) & !WS_EX_APPWINDOW;
    if chips {
        ex | WS_EX_NOACTIVATE
    } else {
        ex & !WS_EX_NOACTIVATE
    }
}
/// Subclasses the window once and applies the tool-window styles in place. The window is created
/// without a taskbar button (winit `skip_taskbar`), so no hide/show is needed and nothing flickers.
pub fn tool(hwnd: HWND, chips: bool) {
    unsafe {
        if hwnd.is_null() {
            return;
        }
        if GetPropW(hwnd, wide(ORIGINAL_PROC).as_ptr()).is_null() {
            if !chips {
                SetPropW(hwnd, wide(IS_FLYOUT).as_ptr(), 1 as _);
            }
            let old = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, proc as *const () as isize);
            SetPropW(hwnd, wide(ORIGINAL_PROC).as_ptr(), old as _);
        }
        frameless(hwnd);
        let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
        let next = tool_ex_style(ex, chips);
        if next != ex {
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, next as isize);
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
        let next = popup_style(style);
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
