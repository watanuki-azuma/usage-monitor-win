//! Core layer: data types, pure helpers, JSON, paths and config. No Win32, no Slint.
pub mod app_config;
pub mod contracts;
pub mod format;
pub mod json_out;
pub mod json_util;
pub mod models;
pub mod paths;
pub mod snapshot_json;

#[cfg(test)]
mod tests;
