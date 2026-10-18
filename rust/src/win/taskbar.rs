use windows_sys::Win32::{Foundation::*,Graphics::Gdi::*,UI::WindowsAndMessaging::*};
use crate::ui::taskbar_math::{self as math,RectI};
use super::wide;
pub fn rect(hwnd:HWND)->Option<RectI> {unsafe{let mut r=std::mem::zeroed(); if hwnd.is_null()||GetWindowRect(hwnd,&mut r)==0 {None}else{Some(from(r))}}}
fn from(r:RECT)->RectI {RectI::new(r.left,r.top,r.right-r.left,r.bottom-r.top)}
pub fn geometry()->(RectI,RectI) {unsafe{
    let h=FindWindowW(wide("Shell_TrayWnd").as_ptr(),std::ptr::null());
    let t=rect(h).unwrap_or_else(||monitor(0,0,false));
    let n=FindWindowExW(h,std::ptr::null_mut(),wide("TrayNotifyWnd").as_ptr(),std::ptr::null());
    (t,rect(n).unwrap_or_default())
}}
pub fn monitor(x:i32,y:i32,work:bool)->RectI {unsafe{
    let h=MonitorFromPoint(POINT{x,y},MONITOR_DEFAULTTONEAREST);
    let mut info:MONITORINFO=std::mem::zeroed();info.cbSize=std::mem::size_of::<MONITORINFO>() as u32;
    GetMonitorInfoW(h,&mut info);from(if work {info.rcWork}else{info.rcMonitor})
}}
pub fn point_on_screen(x:i32,y:i32)->bool {unsafe{!MonitorFromPoint(POINT{x,y},MONITOR_DEFAULTTONULL).is_null()}}
pub fn dock(w:i32,h:i32,gap:i32)->RectI {
    let (t,n)=geometry();let mut p=math::dock_before_tray(t,n,w,h,gap);
    let fits=if t.w>=t.h {n.x-t.x>=w+gap}else{n.y-t.y>=h+gap};
    if !fits {p=if t.w>=t.h {RectI::new(n.x-gap-w,t.y-gap-h,w,h)}else{RectI::new(t.x+t.w+gap,n.y-gap-h,w,h)};}
    math::clamp(p,monitor(t.x,t.y,false))
}
pub fn move_to(hwnd:HWND,p:RectI) {unsafe{if !hwnd.is_null(){SetWindowPos(hwnd,HWND_TOPMOST,p.x,p.y,p.w,p.h,SWP_NOACTIVATE);}}}
pub fn cursor()->(i32,i32) {unsafe{let mut p=POINT{x:0,y:0};GetCursorPos(&mut p);(p.x,p.y)}}
