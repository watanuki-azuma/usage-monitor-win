use windows_sys::Win32::{Foundation::*,System::Threading::*};
use super::wide;
pub const MUTEX_NAME:&str="UsageMonitor-SingleInstance-4d1f9c07";
pub const ACTIVATE_EVENT_NAME:&str="UsageMonitor-Activate-4d1f9c07";
pub struct SingleInstance { mutex:HANDLE, event:HANDLE }
impl SingleInstance {
    pub fn acquire()->Result<Option<Self>,std::io::Error> { unsafe {
        let mutex=CreateMutexW(std::ptr::null(),1,wide(MUTEX_NAME).as_ptr());
        if mutex.is_null() {return Err(std::io::Error::last_os_error());}
        if GetLastError()==ERROR_ALREADY_EXISTS {
            let event=OpenEventW(EVENT_MODIFY_STATE,0,wide(ACTIVATE_EVENT_NAME).as_ptr());
            if !event.is_null() {SetEvent(event);CloseHandle(event);} CloseHandle(mutex);return Ok(None);
        }
        let event=CreateEventW(std::ptr::null(),0,0,wide(ACTIVATE_EVENT_NAME).as_ptr());
        if event.is_null() {CloseHandle(mutex);return Err(std::io::Error::last_os_error());}
        Ok(Some(Self{mutex,event}))
    } }
    pub fn activated(&self)->bool { unsafe {WaitForSingleObject(self.event,0)==WAIT_OBJECT_0} }
}
impl Drop for SingleInstance { fn drop(&mut self) {unsafe{CloseHandle(self.event);ReleaseMutex(self.mutex);CloseHandle(self.mutex);}} }
