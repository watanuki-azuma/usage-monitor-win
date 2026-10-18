#![windows_subsystem = "windows"]

use std::{io::Write, path::PathBuf};
use usage_monitor::{ui, win};
fn main() {
    if let Err(error) = start() {
        // start() has returned: no UI event loop is running while fatal errors are logged.
        if let Some(dir) = std::env::var_os("USAGE_MONITOR_DATA_DIR") {
            if let Ok(mut file) = std::fs::OpenOptions::new().create(true).append(true)
                .open(PathBuf::from(dir).join("usage-monitor.log"))
            {
                let _ = writeln!(file, "{} [error] fatal: {}", chrono::Local::now().format("%Y-%m-%dT%H:%M:%S"), error);
            }
        }
        eprintln!("{}", error);
        std::process::exit(1);
    }
}
fn start() -> Result<(), Box<dyn std::error::Error>> {
    let (mut data, mut render, mut skin) = (None, None, None);
    let mut no_poll = false;
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--smoke" => return Ok(()),
            "--data-dir" => if let Some(value) = args.next() { data = Some(value); },
            "--render-test" => if let Some(value) = args.next() { render = Some(value); },
            "--skin" => if let Some(value) = args.next() { skin = Some(value); },
            "--no-poll" => no_poll = true,
            _ => {
                if let Some(s) = arg.strip_prefix("--data-dir=") { data = Some(s.into()); }
                else if let Some(s) = arg.strip_prefix("--render-test=") { render = Some(s.into()); }
                else if let Some(s) = arg.strip_prefix("--skin=") { skin = Some(s.into()); }
            }
        }
    }
    win::dpi::init();
    let exe = std::env::current_exe()?;
    let exe_dir = exe.parent().unwrap();
    let data = data.filter(|s| !s.is_empty()).map(PathBuf::from).unwrap_or_else(|| exe_dir.into());
    let data = std::path::absolute(data).unwrap_or_else(|_| exe_dir.into());
    std::env::set_var("USAGE_MONITOR_DATA_DIR", &data);
    if let Some(out) = render {
        let out = PathBuf::from(out);
        let result = ui::render_test::run(&out, &data, exe_dir, skin.as_deref());
        if let Err(e) = &result {
            let _ = std::fs::write(out.join("render-test-error.txt"), e.to_string());
        }
        return result;
    }
    let Some(single) = win::single_instance::SingleInstance::acquire()? else { return Ok(()); };
    ui::app_controller::run(data, no_poll, single)
}
