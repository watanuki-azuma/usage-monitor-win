//! Adapters layer: HTTP transport, Windows credential reader and the four provider adapters
//! (claude, codex, gemini, grok). Port of src/Http.cs, src/WinCred.cs and src/Adapters/*.cs.
//! Win32 is used only in `wincred` (CredReadW/CredFree); no Slint.
pub mod claude;
pub mod codex;
pub mod gemini;
pub mod grok;
pub mod http;
pub mod util;
pub mod wincred;

#[cfg(test)]
mod test_support;
#[cfg(test)]
mod tests;
