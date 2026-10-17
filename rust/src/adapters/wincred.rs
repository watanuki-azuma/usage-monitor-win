//! Windows Credential Manager reader (port of src/WinCred.cs).
//!
//! Only CRED_TYPE_GENERIC credentials are read, the blob is decoded as UTF-8, and anything that
//! prevents a read (target absent, empty blob) comes back as `None` so the adapters can fall back
//! to the on-disk token files. Tests inject their own `CredentialReader`; this one talks to the
//! real credential store and is never used by the test suite.
use windows_sys::Win32::Security::Credentials::{CredFree, CredReadW, CREDENTIALW, CRED_TYPE_GENERIC};

use crate::core::contracts::CredentialReader;

#[derive(Clone, Copy, Debug, Default)]
pub struct WinCredReader;

impl WinCredReader {
    pub fn new() -> WinCredReader {
        WinCredReader
    }
}

impl CredentialReader for WinCredReader {
    fn read_generic_utf8(&self, target: &str) -> Option<String> {
        if target.is_empty() {
            return None;
        }
        let wide: Vec<u16> = target.encode_utf16().chain(std::iter::once(0)).collect();
        let mut cred: *mut CREDENTIALW = std::ptr::null_mut();
        // SAFETY: `wide` is a NUL-terminated UTF-16 string that outlives the call; on success
        // `cred` points to a CREDENTIALW allocated by the API that CredFree releases below.
        unsafe {
            if CredReadW(wide.as_ptr(), CRED_TYPE_GENERIC, 0, &mut cred) == 0 || cred.is_null() {
                return None; // ERROR_NOT_FOUND and friends
            }
            let size = (*cred).CredentialBlobSize as usize;
            let blob = (*cred).CredentialBlob;
            let result = if size == 0 || blob.is_null() {
                None
            } else {
                let bytes = std::slice::from_raw_parts(blob as *const u8, size);
                Some(String::from_utf8_lossy(bytes).into_owned())
            };
            CredFree(cred as *const core::ffi::c_void);
            result
        }
    }
}
