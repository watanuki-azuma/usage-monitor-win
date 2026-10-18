use windows_sys::Win32::{Foundation::HWND,UI::WindowsAndMessaging::*};
use crate::{core::app_config::{AppConfig,ALLOWED_INTERVALS,SKIN_IDS},ui::skins::NAMES};
use super::{wide,taskbar};
unsafe fn item(menu:HMENU,id:usize,text:&str,checked:bool) {AppendMenuW(menu,MF_STRING|if checked{MF_CHECKED}else{0},id,wide(text).as_ptr());}
pub fn show(hwnd:HWND,c:&AppConfig,visible:bool,interval_only:bool)->Option<String> {unsafe{
    let m=CreatePopupMenu();let intervals=if interval_only{m}else{CreatePopupMenu()};
    for (i,s) in ALLOWED_INTERVALS.iter().enumerate(){item(intervals,20+i,&format!("{}{} seconds",if interval_only{"Every "}else{""},s),c.poll_interval_secs==*s);}
    if !interval_only {
        item(m,1,"Refresh now",false); AppendMenuW(m,MF_POPUP,intervals as usize,wide("Refresh every").as_ptr());
        AppendMenuW(m,MF_SEPARATOR,0,std::ptr::null());
        item(m,2,if c.chips_hidden{"Show chips"}else{"Hide chips"},false);
        item(m,3,if visible{"Hide flyout"}else{"Open flyout"},false);
        item(m,4,if c.flyout_docked{"Unsnap flyout from taskbar"}else{"Snap flyout to taskbar"},false);
        item(m,5,if c.chips_docked{"Unsnap chips from taskbar"}else{"Snap chips to taskbar"},false);
        item(m,6,"Start with Windows",c.autostart);
        let design=CreatePopupMenu();for (i,id) in SKIN_IDS.iter().enumerate(){item(design,30+i,NAMES[i],c.skin==*id);}
        AppendMenuW(m,MF_POPUP,design as usize,wide("Design").as_ptr());AppendMenuW(m,MF_SEPARATOR,0,std::ptr::null());item(m,7,"Quit",false);
    }
    let (x,y)=taskbar::cursor();SetForegroundWindow(hwnd);
    let cmd=TrackPopupMenu(m,TPM_RETURNCMD|TPM_RIGHTBUTTON,x,y,0,hwnd,std::ptr::null());
    PostMessageW(hwnd,WM_NULL,0,0);DestroyMenu(m);
    match cmd {1=>Some("refresh".into()),2=>Some("chips".into()),3=>Some("menu-flyout".into()),4=>Some("menu-flyout-dock".into()),5=>Some("chips-dock".into()),6=>Some("autostart".into()),7=>Some("quit".into()),20..=23=>Some(format!("interval:{}",ALLOWED_INTERVALS[(cmd-20) as usize])),30..=33=>Some(format!("skin:{}",SKIN_IDS[(cmd-30) as usize])),_=>None}
}}
