use std::{cell::RefCell,rc::Rc};
use windows_sys::Win32::{Foundation::*,Graphics::Gdi::*,System::LibraryLoader::GetModuleHandleW,UI::{Shell::*,WindowsAndMessaging::*}};
use super::wide;
const WM_TRAY:u32=WM_APP+1;
thread_local!{static ACTION:RefCell<Option<Rc<dyn Fn(String)>>>=RefCell::new(None);}
unsafe extern "system" fn proc(hwnd:HWND,msg:u32,wp:WPARAM,lp:LPARAM)->LRESULT {
    if msg==WM_DISPLAYCHANGE {
        let _=slint::invoke_from_event_loop(||crate::ui::app_controller::dispatch("display".into()));
    }
    if msg==WM_TRAY {
        let action=match lp as u32 {WM_LBUTTONUP=>Some("tray-flyout"),WM_RBUTTONUP=>Some("menu"),NIN_BALLOONUSERCLICK=>Some("show"),_=>None};
        if let Some(a)=action {let f=ACTION.with(|f|f.borrow().clone());if let Some(f)=f{f(a.into());}}return 0;
    }
    if msg==RegisterWindowMessageW(wide("TaskbarCreated").as_ptr()) {
        let f=ACTION.with(|f|f.borrow().clone());if let Some(f)=f{f("explorer".into());}
    }
    DefWindowProcW(hwnd,msg,wp,lp)
}
pub struct Tray {pub hwnd:HWND,broadcast:HWND,icon:HICON}
impl Tray {
    pub fn menu_owner(&self)->HWND {self.broadcast}
    pub fn new(action:Rc<dyn Fn(String)>)->Self {unsafe{
        ACTION.with(|f|*f.borrow_mut()=Some(action));
        let instance=GetModuleHandleW(std::ptr::null());let class=wide("UsageMonitorRustTray");
        let mut wc:WNDCLASSW=std::mem::zeroed();wc.lpfnWndProc=Some(proc);wc.hInstance=instance;wc.lpszClassName=class.as_ptr();RegisterClassW(&wc);
        let create=|parent|CreateWindowExW(0,class.as_ptr(),class.as_ptr(),0,0,0,0,0,parent,std::ptr::null_mut(),instance,std::ptr::null());
        // Broadcasts do not reach HWND_MESSAGE; this invisible top-level sibling receives Explorer restart.
        let hwnd=create(HWND_MESSAGE);let broadcast=create(std::ptr::null_mut());
        let icon=badge_icon();let t=Self{hwnd,broadcast,icon};t.add();t
    }}
    fn data(&self)->NOTIFYICONDATAW {let mut d:NOTIFYICONDATAW=unsafe{std::mem::zeroed()};d.cbSize=std::mem::size_of::<NOTIFYICONDATAW>() as u32;d.hWnd=self.hwnd;d.uID=1;d}
    pub fn add(&self){unsafe{let mut d=self.data();d.uFlags=NIF_ICON|NIF_MESSAGE|NIF_TIP;d.uCallbackMessage=WM_TRAY;d.hIcon=self.icon;copy(&mut d.szTip,"Usage Monitor");Shell_NotifyIconW(NIM_ADD,&d);}}
    pub fn balloon(&self,title:&str,body:&str){unsafe{let mut d=self.data();d.uFlags=NIF_INFO;d.dwInfoFlags=NIIF_NONE;d.Anonymous.uTimeout=10000;copy(&mut d.szInfoTitle,title);copy(&mut d.szInfo,body);Shell_NotifyIconW(NIM_MODIFY,&d);}}
}
fn copy<const N:usize>(buf:&mut [u16;N],text:&str){for (b,c) in buf.iter_mut().take(N-1).zip(text.encode_utf16()){*b=c;}}
unsafe fn badge_icon()->HICON {
    let dc=CreateCompatibleDC(std::ptr::null_mut());let screen=GetDC(std::ptr::null_mut());
    let bitmap=CreateCompatibleBitmap(screen,16,16);ReleaseDC(std::ptr::null_mut(),screen);let old=SelectObject(dc,bitmap);
    let bg=CreateSolidBrush(0x00271811);let accent=CreateSolidBrush(0x005f7ae0);let pen=CreatePen(PS_NULL,0,0);let oldpen=SelectObject(dc,pen);
    let mut rect=RECT{left:0,top:0,right:16,bottom:16};FillRect(dc,&rect,bg);let oldbrush=SelectObject(dc,accent);Ellipse(dc,2,2,14,14);
    let font=CreateFontW(-9,0,0,0,700,0,0,0,1,0,0,0,0,wide("Segoe UI").as_ptr());let oldfont=SelectObject(dc,font);SetBkMode(dc,TRANSPARENT as i32);SetTextColor(dc,0x00271811);
    DrawTextW(dc,wide("C").as_ptr(),1,&mut rect,DT_CENTER|DT_VCENTER|DT_SINGLELINE|DT_NOPREFIX);
    SelectObject(dc,oldfont);SelectObject(dc,oldbrush);SelectObject(dc,oldpen);SelectObject(dc,old);DeleteDC(dc);DeleteObject(font);DeleteObject(bg);DeleteObject(accent);DeleteObject(pen);
    let mask=CreateBitmap(16,16,1,1,[0u8;32].as_ptr() as *const _);let info=ICONINFO{fIcon:1,xHotspot:0,yHotspot:0,hbmMask:mask,hbmColor:bitmap};let icon=CreateIconIndirect(&info);DeleteObject(mask);DeleteObject(bitmap);icon
}
impl Drop for Tray{fn drop(&mut self){unsafe{Shell_NotifyIconW(NIM_DELETE,&self.data());DestroyWindow(self.hwnd);DestroyWindow(self.broadcast);DestroyIcon(self.icon);}ACTION.with(|f|*f.borrow_mut()=None);}}
