//! HKCU Run, same value and quoted executable path as Native.cs. Only called on worker.
use super::wide;
use windows_sys::Win32::System::Registry::*;
pub const RUN_KEY_PATH: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
pub const RUN_VALUE_NAME: &str = "Usage Monitor";
pub fn apply(enabled: bool) -> bool {
    unsafe {
        let mut key = std::ptr::null_mut();
        if RegOpenKeyExW(
            HKEY_CURRENT_USER,
            wide(RUN_KEY_PATH).as_ptr(),
            0,
            KEY_SET_VALUE | KEY_QUERY_VALUE,
            &mut key,
        ) != 0
        {
            return false;
        }
        let exe = std::env::current_exe()
            .ok()
            .map(|p| p.to_string_lossy().into_owned())
            .unwrap_or_default();
        let name = wide(RUN_VALUE_NAME);
        let ok = if enabled {
            let value = wide(&format!("\"{}\"", exe));
            RegSetValueExW(
                key,
                name.as_ptr(),
                0,
                REG_SZ,
                value.as_ptr() as *const u8,
                (value.len() * 2) as u32,
            ) == 0
        } else {
            RegDeleteValueW(key, name.as_ptr());
            true
        };
        let mut buf = vec![0u16; 32768];
        let mut len = (buf.len() * 2) as u32;
        let mut ty = 0;
        let rc = RegQueryValueExW(
            key,
            name.as_ptr(),
            std::ptr::null(),
            &mut ty,
            buf.as_mut_ptr() as *mut u8,
            &mut len,
        );
        RegCloseKey(key);
        ok && enabled
            && rc == 0
            && ty == REG_SZ
            && String::from_utf16_lossy(&buf[..((len / 2) as usize).saturating_sub(1)])
                .trim_matches('"')
                == exe
    }
}
