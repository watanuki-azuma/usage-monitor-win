// Based on microsoft/windows-rs windows-link 0.2.1 (MIT OR Apache-2.0).
// Only GNU linking differs: use the existing windows-targets import library instead of emitting
// separate raw-dylib IAT fragments from every crate. Keep upstream behavior on other targets.
#![no_std]
#[doc(hidden)]
pub use windows_targets as targets;

#[cfg(all(windows, target_env = "gnu"))]
#[macro_export]
macro_rules! link {
    ($library:literal $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        $crate::targets::link!($library $abi $($link_name)? fn $($function)*);
    )
}

#[cfg(all(windows, not(target_env = "gnu"), target_arch = "x86"))]
#[macro_export]
macro_rules! link {
    ($library:literal $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = $library, kind = "raw-dylib", modifiers = "+verbatim", import_name_type = "undecorated")]
        extern $abi { $(#[link_name=$link_name])? pub fn $($function)*; }
    )
}

#[cfg(all(windows, not(target_env = "gnu"), not(target_arch = "x86")))]
#[macro_export]
macro_rules! link {
    ($library:literal $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = $library, kind = "raw-dylib", modifiers = "+verbatim")]
        extern $abi { $(#[link_name=$link_name])? pub fn $($function)*; }
    )
}

#[cfg(not(windows))]
#[macro_export]
macro_rules! link {
    ($library:literal $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        extern $abi { pub fn $($function)*; }
    )
}
