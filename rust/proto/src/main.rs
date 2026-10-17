#![windows_subsystem = "windows"]
// Feasibility spike: Slint (software renderer) windows docked into the Windows taskbar
// area, no taskbar button, no activation, plus a native tray icon and menu.

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use std::cell::RefCell;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Shell::*;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

slint::slint! {
    export component Chips inherits Window {
        title: "um-proto-chips";
        no-frame: true;
        always-on-top: true;
        background: #1f1f1f;
        width: 190px;
        height: 34px;
        in property <string> label: "C 42%  X 10%  G 5%";
        Rectangle {
            border-radius: 6px;
            background: #2b2b2b;
            border-width: 1px;
            border-color: #3c3c3c;
            Text {
                text: root.label;
                color: #e8e8e8;
                font-size: 12px;
                horizontal-alignment: center;
                vertical-alignment: center;
            }
        }
    }
    export component Flyout inherits Window {
        title: "um-proto-flyout";
        no-frame: true;
        always-on-top: true;
        background: #1b1b1b;
        width: 320px;
        height: 180px;
        in property <float> used: 0.42;
        VerticalLayout {
            padding: 16px;
            spacing: 10px;
            Text { text: "Claude Code  5h"; color: #ffffff; font-size: 14px; }
            Rectangle {
                height: 8px;
                border-radius: 4px;
                background: #333;
                Rectangle {
                    x: 0;
                    width: parent.width * root.used;
                    border-radius: 4px;
                    background: @linear-gradient(90deg, #6ab0ff 0%, #b06aff 100%);
                }
            }
            Text { text: "resets in 2h 14m"; color: #a0a0a0; font-size: 12px; }
        }
    }
}

const WM_TRAY: u32 = WM_APP + 1;
const ID_QUIT: usize = 1;

thread_local! {
    static FLYOUT: RefCell<Option<slint::Weak<Flyout>>> = RefCell::new(None);
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn hwnd_of(w: &slint::Window) -> HWND {
    match w.window_handle().window_handle().map(|h| h.as_raw()) {
        Ok(RawWindowHandle::Win32(h)) => h.hwnd.get() as HWND,
        // the winit + software renderer combination reports NotSupported; fall back to
        // the unique window title of this process
        _ => std::ptr::null_mut(),
    }
}

fn hwnd_by_title(title: &str) -> HWND {
    unsafe { FindWindowW(std::ptr::null(), wide(title).as_ptr()) }
}

/// Tool window (no taskbar button), never activated, top-most.
unsafe fn make_tool(hwnd: HWND) {
    let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32;
    let ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST) & !WS_EX_APPWINDOW;
    ShowWindow(hwnd, SW_HIDE);
    SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex as isize);
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
}

unsafe fn rect_of(hwnd: HWND) -> RECT {
    let mut r: RECT = std::mem::zeroed();
    GetWindowRect(hwnd, &mut r);
    r
}

/// Same rule as TaskbarMath.DockBeforeTray in the C# app.
unsafe fn dock_before_tray(hwnd: HWND) {
    let tb = FindWindowW(wide("Shell_TrayWnd").as_ptr(), std::ptr::null());
    if tb.is_null() {
        return;
    }
    let tray = FindWindowExW(tb, std::ptr::null_mut(), wide("TrayNotifyWnd").as_ptr(), std::ptr::null());
    let (t, n, me) = (rect_of(tb), rect_of(tray), rect_of(hwnd));
    let (w, h) = (me.right - me.left, me.bottom - me.top);
    let (x, y) = if t.right - t.left >= t.bottom - t.top {
        (n.left - 8 - w, t.top + (t.bottom - t.top - h) / 2)
    } else {
        (t.left + (t.right - t.left - w) / 2, n.top - 8 - h)
    };
    SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
}

unsafe fn toggle_flyout() {
    FLYOUT.with(|f| {
        let fl = match f.borrow().as_ref().and_then(|w| w.upgrade()) {
            Some(fl) => fl,
            None => return,
        };
        let win = fl.window();
        if win.is_visible() {
            let _ = win.hide();
            return;
        }
        let _ = fl.show();
        let mut h = hwnd_of(win);
        if h.is_null() {
            h = hwnd_by_title("um-proto-flyout");
        }
        make_tool(h);
        let mut p: POINT = std::mem::zeroed();
        GetCursorPos(&mut p);
        let me = rect_of(h);
        SetWindowPos(h, HWND_TOPMOST, p.x - (me.right - me.left), p.y - (me.bottom - me.top) - 16,
                     0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    });
}

unsafe fn tray_menu(hwnd: HWND) {
    let menu = CreatePopupMenu();
    AppendMenuW(menu, MF_STRING, ID_QUIT, wide("Quit").as_ptr());
    let mut p: POINT = std::mem::zeroed();
    GetCursorPos(&mut p);
    SetForegroundWindow(hwnd);
    let cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.x, p.y, 0, hwnd, std::ptr::null());
    DestroyMenu(menu);
    if cmd as usize == ID_QUIT {
        let mut nid: NOTIFYICONDATAW = std::mem::zeroed();
        nid.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
        nid.hWnd = hwnd;
        nid.uID = 1;
        Shell_NotifyIconW(NIM_DELETE, &nid);
        let _ = slint::quit_event_loop();
    }
}

unsafe extern "system" fn tray_proc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if msg == WM_TRAY {
        match lp as u32 {
            WM_LBUTTONUP => toggle_flyout(),
            WM_RBUTTONUP => tray_menu(hwnd),
            _ => {}
        }
        return 0;
    }
    DefWindowProcW(hwnd, msg, wp, lp)
}

unsafe fn add_tray_icon() {
    let inst = GetModuleHandleW(std::ptr::null());
    let class = wide("UmProtoTray");
    let mut wc: WNDCLASSW = std::mem::zeroed();
    wc.lpfnWndProc = Some(tray_proc);
    wc.hInstance = inst;
    wc.lpszClassName = class.as_ptr();
    RegisterClassW(&wc);
    let hwnd = CreateWindowExW(0, class.as_ptr(), class.as_ptr(), 0, 0, 0, 0, 0,
                               HWND_MESSAGE, std::ptr::null_mut(), inst, std::ptr::null());
    let mut nid: NOTIFYICONDATAW = std::mem::zeroed();
    nid.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
    nid.hWnd = hwnd;
    nid.uID = 1;
    nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    nid.uCallbackMessage = WM_TRAY;
    nid.hIcon = LoadIconW(std::ptr::null_mut(), IDI_APPLICATION);
    let tip = wide("Usage Monitor (Slint proto)");
    nid.szTip[..tip.len()].copy_from_slice(&tip);
    Shell_NotifyIconW(NIM_ADD, &nid);
}

fn main() -> Result<(), slint::PlatformError> {
    let chips = Chips::new()?;
    let flyout = Flyout::new()?;
    FLYOUT.with(|f| *f.borrow_mut() = Some(flyout.as_weak()));
    chips.show()?;
    // the winit backend creates the native window once the event loop runs, so the
    // Win32 styling and docking happen on the first loop turn
    let chips_weak = chips.as_weak();
    let ch_cell = std::rc::Rc::new(std::cell::Cell::new(std::ptr::null_mut::<std::ffi::c_void>()));
    let ch_set = ch_cell.clone();
    let setup = std::rc::Rc::new(slint::Timer::default());
    let setup_stop = setup.clone();
    let started = std::time::Instant::now();
    setup.start(slint::TimerMode::Repeated, std::time::Duration::from_millis(50), move || unsafe {
        if !ch_set.get().is_null() {
            return;
        }
        let c = match chips_weak.upgrade() {
            Some(c) => c,
            None => return,
        };
        let mut h = hwnd_of(c.window());
        if h.is_null() {
            h = hwnd_by_title("um-proto-chips");
        }
        if h.is_null() {
            return;
        }
        let _ = std::fs::write(std::env::temp_dir().join("um-proto.log"),
            format!("chips hwnd={:?} after {} ms\n", h, started.elapsed().as_millis()));
        ch_set.set(h);
        make_tool(h);
        dock_before_tray(h);
        add_tray_icon();
        setup_stop.stop();
    });

    // stand-in for the poll update (the C# app polls about once a minute)
    let n = std::cell::Cell::new(42);
    let weak = chips.as_weak();
    let tick = slint::Timer::default();
    tick.start(slint::TimerMode::Repeated, std::time::Duration::from_secs(30), move || {
        n.set((n.get() + 1) % 100);
        if let Some(c) = weak.upgrade() {
            c.set_label(format!("C {}%  X 10%  G 5%", n.get()).into());
        }
    });
    // top-most watchdog like the C# app (a taskbar click can raise the taskbar over us)
    let watch = slint::Timer::default();
    watch.start(slint::TimerMode::Repeated, std::time::Duration::from_secs(2), move || unsafe {
        let ch = ch_cell.get();
        if !ch.is_null() {
            SetWindowPos(ch, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    });
    slint::run_event_loop_until_quit()
}
