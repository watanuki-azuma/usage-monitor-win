//! Engine layer: poll engine, alert engine, file stores, usage log. See SLINT-SPEC.md §5.
pub mod alert_engine;
pub mod poll_engine;
pub mod stores;
pub mod usage_log;

#[cfg(test)]
mod test_support;
#[cfg(test)]
mod tests;
