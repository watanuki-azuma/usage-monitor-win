//! UI state lives on the event loop; all filesystem, credential, HTTP and registry work is on worker.
use super::{
    render_test,
    taskbar_math::{self as math, RectI},
    Chips, Flyout,
};
use crate::{
    adapters::{self, http::RealHttp, wincred::WinCredReader},
    core::{app_config::AppConfig, contracts::*, models, paths::Paths},
    engine::{
        alert_engine,
        poll_engine::PollEngine,
        stores::{FileAlertStore, FileSnapshotStore},
        usage_log::UsageLog,
    },
    win::{self, menu, single_instance::SingleInstance, taskbar, tray::Tray, window_style},
};
use chrono::Utc;
use slint::ComponentHandle;
use std::{
    cell::RefCell,
    path::PathBuf,
    rc::Rc,
    sync::{mpsc, Arc},
    time::{Duration, Instant},
};

enum Work {
    Start,
    Config(AppConfig),
    Autostart(AppConfig),
    Snapshot(Arc<Snapshot>, bool),
    Refresh,
    Stop,
}
fn forward_poll(tx: &Option<mpsc::Sender<Work>>, command: Work) {
    if let Some(tx) = tx {
        let _ = tx.send(command);
    }
}
// Consume a whole pending batch before polling, like C#'s single wakePoll signal.
fn merge_poll_commands(
    commands: impl Iterator<Item = Work>,
    config: &mut AppConfig,
    due: &mut Instant,
) -> bool {
    for command in commands {
        match command {
            Work::Config(c) => {
                if c.poll_interval_secs != config.poll_interval_secs {
                    *due = Instant::now();
                }
                *config = c;
            }
            Work::Refresh => *due = Instant::now(),
            Work::Stop => return false,
            _ => {}
        }
    }
    true
}
fn interval_commands(config: &mut AppConfig, selection: &str) -> [Work; 2] {
    config.poll_interval_secs = selection
        .parse::<i32>()
        .ok()
        .filter(|s| crate::core::app_config::ALLOWED_INTERVALS.contains(s))
        .unwrap_or(5);
    [Work::Config(config.clone()), Work::Refresh]
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn refresh_burst_during_poll_coalesces_and_uses_latest_config() {
        let (tx, rx) = mpsc::channel();
        let mut config = AppConfig::default();
        let mut next = config.clone();
        next.poll_interval_secs = 15;
        tx.send(Work::Refresh).unwrap();
        tx.send(Work::Config(next)).unwrap();
        for _ in 0..8 {
            tx.send(Work::Refresh).unwrap();
        }
        let mut due = Instant::now() + Duration::from_secs(60);
        assert!(merge_poll_commands(rx.try_iter(), &mut config, &mut due));
        assert!(due <= Instant::now());
        assert_eq!(config.poll_interval_secs, 15);
        // The follow-up poll finished; no queued click should trigger another poll.
        due = Instant::now() + Duration::from_secs(15);
        let scheduled = due;
        assert!(merge_poll_commands(rx.try_iter(), &mut config, &mut due));
        assert_eq!(due, scheduled);
    }
    #[test]
    fn stop_preempts_a_pending_refresh() {
        let mut config = AppConfig::default();
        let mut due = Instant::now();
        assert!(!merge_poll_commands(
            [Work::Refresh, Work::Stop].into_iter(),
            &mut config,
            &mut due
        ));
    }
    #[test]
    fn every_interval_selection_saves_then_refreshes_even_if_unchanged() {
        let mut config = AppConfig::default();
        for selection in ["5", "5", "15", "15", "invalid"] {
            let expected = if selection == "15" { 15 } else { 5 };
            let [save, refresh] = interval_commands(&mut config, selection);
            assert!(matches!(save,Work::Config(c) if c.poll_interval_secs==expected));
            assert!(matches!(refresh, Work::Refresh));
            assert_eq!(config.poll_interval_secs, expected);
        }
    }
    #[test]
    fn no_poll_forwarding_needs_no_receiver() {
        for _ in 0..100 {
            forward_poll(&None, Work::Refresh);
            forward_poll(&None, Work::Config(AppConfig::default()));
        }
        let (tx, rx) = mpsc::channel();
        forward_poll(&Some(tx), Work::Refresh);
        assert!(matches!(rx.try_recv(), Ok(Work::Refresh)));
    }
}
thread_local! { static APP:RefCell<Option<Rc<RefCell<AppController>>>>=RefCell::new(None); }
pub struct AppController {
    config: AppConfig,
    current: Option<Snapshot>,
    chips: Chips,
    flyout: Flyout,
    tray: Option<Tray>,
    single: SingleInstance,
    worker: mpsc::Sender<Work>,
    tick: slint::Timer,
    setup: slint::Timer,
    chips_hwnd: windows_sys::Win32::Foundation::HWND,
    flyout_hwnd: windows_sys::Win32::Foundation::HWND,
    chips_rect: RectI,
    last_geometry: Option<(RectI, RectI)>,
    menu_open: bool,
    drag_start: Option<(i32, i32, RectI, bool)>,
    drag_moved: bool,
    flyout_shown: Instant,
}
fn post(f: impl FnOnce() + Send + 'static) {
    let _ = slint::invoke_from_event_loop(f);
}
fn with_app(f: impl FnOnce(&mut AppController)) {
    let app = APP.with(|a| a.borrow().clone());
    if let Some(app) = app {
        f(&mut app.borrow_mut());
    }
}
pub fn dispatch(action: String) {
    if action == "menu" || action == "interval-menu" {
        let info = APP.with(|a| {
            a.borrow().as_ref().and_then(|a| {
                let mut a = a.borrow_mut();
                if a.menu_open {
                    return None;
                }
                a.menu_open = true;
                let visible = a.flyout.window().is_visible();
                let hwnd = if visible && !a.flyout_hwnd.is_null() {
                    a.flyout_hwnd
                } else if !a.config.chips_hidden && !a.chips_hwnd.is_null() {
                    a.chips_hwnd
                } else {
                    a.tray.as_ref().unwrap().menu_owner()
                };
                Some((hwnd, a.config.clone(), visible))
            })
        });
        if let Some((hwnd, c, v)) = info {
            let next = menu::show(hwnd, &c, v, action == "interval-menu");
            with_app(|a| {
                a.menu_open = false;
                a.flyout_shown = Instant::now();
            });
            if let Some(next) = next {
                dispatch(next);
            }
        }
        return;
    }
    with_app(|a| a.action(&action));
}
fn save_config(dir: &std::path::Path, c: &AppConfig) {
    if let Err(error) = crate::engine::stores::save_config(dir.join("config.json"), c) {
        use std::io::Write;
        if let Ok(mut file) = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(dir.join("usage-monitor.log"))
        {
            let _ = writeln!(
                file,
                "{} [warn] config write failed: {}",
                Utc::now().to_rfc3339(),
                error
            );
        }
    }
}
fn worker(
    dir: PathBuf,
    no_poll: bool,
    rx: mpsc::Receiver<Work>,
    results: mpsc::Sender<Work>,
    ready: mpsc::SyncSender<(AppConfig, Option<Snapshot>)>,
) {
    let json = std::fs::read(dir.join("config.json"))
        .ok()
        .map(|bytes| adapters::util::decode_text(&bytes));
    let mut config = AppConfig::parse(json.as_deref());
    let store = Arc::new(FileSnapshotStore::new(dir.join("snapshot.json")));
    let cached = store.load();
    config.autostart = win::registry::apply(config.autostart);
    save_config(&dir, &config);
    let _ = ready.send((config.clone(), cached));
    let (poll_tx, mut poll_rx) = if no_poll {
        (None, None)
    } else {
        let (tx, rx) = mpsc::channel();
        (Some(tx), Some(rx))
    };
    let alert_store = FileAlertStore::new(dir.join("alert-state.json"));
    let mut alerts = alert_store.load();
    // Persistence stays responsive while a provider is taking its full 25-second timeout.
    while let Ok(command) = rx.recv() {
        match command {
            Work::Start => {
                if !no_poll {
                    if let Some(rx) = poll_rx.take() {
                        let dir = dir.clone();
                        let config = config.clone();
                        let results = results.clone();
                        std::thread::Builder::new()
                            .name("usage-monitor-poll".into())
                            .spawn(move || poll_loop(dir, config, rx, results))
                            .ok();
                    }
                }
            }
            Work::Config(c) => {
                let actual_autostart = config.autostart;
                config = c;
                config.autostart = actual_autostart;
                save_config(&dir, &config);
                forward_poll(&poll_tx, Work::Config(config.clone()));
            }
            Work::Autostart(c) => {
                config = c;
                config.autostart = win::registry::apply(config.autostart);
                save_config(&dir, &config);
                let actual = config.autostart;
                post(move || with_app(|a| a.config.autostart = actual));
                forward_poll(&poll_tx, Work::Config(config.clone()));
            }
            Work::Refresh => forward_poll(&poll_tx, Work::Refresh),
            Work::Snapshot(snapshot, polled) => {
                let toasts = alert_engine::evaluate(
                    &snapshot,
                    &mut alerts,
                    config.notify_on_limit_reached,
                    Utc::now(),
                );
                let _ = alert_store.save(&alerts);
                post(move || {
                    with_app(|a| {
                        if polled {
                            a.current = Some((*snapshot).clone());
                            a.render();
                        }
                        if let Some(t) = &a.tray {
                            for toast in toasts {
                                t.balloon(&toast.title, &toast.body);
                            }
                        }
                    })
                });
            }
            Work::Stop => {
                forward_poll(&poll_tx, Work::Stop);
                break;
            }
        }
    }
}
fn poll_loop(
    dir: PathBuf,
    mut config: AppConfig,
    rx: mpsc::Receiver<Work>,
    results: mpsc::Sender<Work>,
) {
    let store = Arc::new(FileSnapshotStore::new(dir.join("snapshot.json")));
    let log = UsageLog::new(dir.join("usage-log.jsonl"));
    let engine = PollEngine::new(
        vec![
            Arc::new(adapters::claude::ClaudeAdapter::new()),
            Arc::new(adapters::codex::CodexAdapter::new()),
            Arc::new(adapters::gemini::GeminiAdapter::new()),
            Arc::new(adapters::grok::GrokAdapter::new()),
        ],
        Some(store),
        Arc::new(Utc::now),
        25000,
    );
    let paths = Paths::from_process_env();
    let http = Arc::new(RealHttp::new());
    let credentials = Arc::new(WinCredReader::new());
    let mut due = Instant::now();
    let mut failures = std::collections::HashMap::new();
    loop {
        if !merge_poll_commands(rx.try_iter(), &mut config, &mut due) {
            break;
        }
        if Instant::now() >= due {
            let ctx = AdapterContext {
                paths: paths.clone(),
                http: http.clone(),
                credentials: credentials.clone(),
                config: config.clone(),
                utc_now: Arc::new(Utc::now),
            };
            let snapshot = engine.poll_once(&ctx, config.poll_interval_secs);
            log.append(&snapshot, Utc::now());
            for p in &snapshot.providers {
                if matches!(
                    p.status.state.as_str(),
                    state::FETCH_FAILED | state::LOGGED_OUT | state::NOT_INSTALLED | state::UNKNOWN
                ) {
                    let failure = format!(
                        "{} {}",
                        p.status.state,
                        p.status.message.as_deref().unwrap_or("")
                    );
                    if failures.get(&p.id) != Some(&failure) {
                        use std::io::Write;
                        if let Ok(mut file) = std::fs::OpenOptions::new()
                            .create(true)
                            .append(true)
                            .open(dir.join("usage-monitor.log"))
                        {
                            let _ = writeln!(
                                file,
                                "{} [warn] provider {}: {}",
                                Utc::now().to_rfc3339(),
                                p.id,
                                failure
                            );
                        }
                        failures.insert(p.id.clone(), failure);
                    }
                } else {
                    failures.remove(&p.id);
                }
            }
            if results.send(Work::Snapshot(snapshot, true)).is_err() {
                break;
            }
            due = Instant::now() + Duration::from_secs(config.poll_interval_secs as u64);
            continue;
        }
        let command = match rx.recv_timeout(due.saturating_duration_since(Instant::now())) {
            Ok(c) => c,
            Err(mpsc::RecvTimeoutError::Timeout) => continue,
            Err(_) => break,
        };
        if !merge_poll_commands(
            std::iter::once(command).chain(rx.try_iter()),
            &mut config,
            &mut due,
        ) {
            break;
        }
    }
}
/// Before the first show there is no HWND yet; giving Slint the position makes the window appear
/// in place instead of at the default spot and jumping there 50 ms later.
fn preset_position(window: &slint::Window, p: RectI) {
    window.set_position(slint::PhysicalPosition::new(p.x, p.y));
}
pub fn run(
    dir: PathBuf,
    no_poll: bool,
    single: SingleInstance,
) -> Result<(), Box<dyn std::error::Error>> {
    let (tx, rx) = mpsc::channel();
    let (ready, initial) = mpsc::sync_channel(1);
    // Startup reads finish before any window or UI event loop exists.
    let results = tx.clone();
    let io_worker = std::thread::Builder::new()
        .name("usage-monitor-worker".into())
        .spawn(move || worker(dir, no_poll, rx, results, ready))?;
    let (config, current) = initial.recv()?;
    // Created without a taskbar button, so the windows never need a hide/show to drop one.
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .with_winit_window_attributes_hook(|attributes| {
            use slint::winit_030::winit::platform::windows::WindowAttributesExtWindows;
            attributes.with_skip_taskbar(true)
        })
        .select()?;
    let chips = Chips::new()?;
    let flyout = Flyout::new()?;
    let app = Rc::new(RefCell::new(AppController {
        config,
        current,
        chips,
        flyout,
        tray: None,
        single,
        worker: tx,
        tick: slint::Timer::default(),
        setup: slint::Timer::default(),
        chips_hwnd: std::ptr::null_mut(),
        flyout_hwnd: std::ptr::null_mut(),
        chips_rect: RectI::default(),
        last_geometry: None,
        menu_open: false,
        drag_start: None,
        drag_moved: false,
        flyout_shown: Instant::now(),
    }));
    APP.with(|a| *a.borrow_mut() = Some(app.clone()));
    {
        let mut a = app.borrow_mut();
        a.chips.on_action(|s| dispatch(s.into()));
        a.flyout.on_action(|s| dispatch(s.into()));
        a.chips
            .on_drag(|x, y, phase| with_app(|a| a.drag(false, x, y, phase)));
        a.flyout
            .on_drag(|x, y, phase| with_app(|a| a.drag(true, x, y, phase)));
        a.chips.window().on_close_requested(|| {
            dispatch("chips".into());
            slint::CloseRequestResponse::KeepWindowShown
        });
        a.flyout.window().on_close_requested(|| {
            dispatch("escape".into());
            slint::CloseRequestResponse::KeepWindowShown
        });
        a.render();
        a.place();
        if !a.config.chips_hidden {
            a.chips.show()?;
        }
        if a.config.flyout_docked || a.config.flyout_pinned {
            a.flyout.show()?;
        }
        a.tray = Some(Tray::new(Rc::new(dispatch)));
        a.begin_setup();
        a.tick
            .start(slint::TimerMode::Repeated, Duration::from_secs(1), || {
                with_app(|a| a.tick())
            });
        let _ = a.worker.send(Work::Start);
    }
    let result = slint::run_event_loop_until_quit();
    {
        let mut a = app.borrow_mut();
        a.tick.stop();
        a.setup.stop();
        a.tray.take();
        let _ = a.worker.send(Work::Stop);
    }
    APP.with(|a| a.borrow_mut().take());
    // The loop has ended and tray is removed; drain pending config writes before process exit.
    let _ = io_worker.join();
    result.map_err(Into::into)
}
impl AppController {
    fn persist(&self) {
        let _ = self.worker.send(Work::Config(self.config.clone()));
    }
    fn render(&mut self) {
        let old = (
            self.chips.get_content_width(),
            self.chips.get_content_height(),
            self.flyout.get_content_width(),
            self.flyout.get_content_height(),
        );
        let skin = super::skins::component_skin(Some(&self.config.skin));
        self.chips.set_skin(skin.into());
        self.flyout.set_skin(skin.into());
        self.flyout.set_interval(self.config.poll_interval_secs);
        self.flyout.set_docked(self.config.flyout_docked);
        self.flyout.set_pinned(self.config.flyout_pinned);
        render_test::update(&self.chips, &self.flyout, self.current.as_ref(), Utc::now());
        let scale = win::dpi::scale();
        if old.0 != self.chips.get_content_width() || old.1 != self.chips.get_content_height() {
            if self.config.chips_docked || self.chips_hwnd.is_null() {
                self.place_chips();
            } else {
                self.chips_rect.w = (self.chips.get_content_width() * scale).round() as i32;
                self.chips_rect.h = (self.chips.get_content_height() * scale).round() as i32;
                taskbar::move_to(self.chips_hwnd, self.chips_rect);
                let cr =
                    (super::skins::chips_radius(Some(&self.config.skin)) * scale).round() as i32;
                window_style::round(self.chips_hwnd, self.chips_rect.w, self.chips_rect.h, cr);
            }
        }
        if old.2 != self.flyout.get_content_width() || old.3 != self.flyout.get_content_height() {
            if self.config.flyout_docked || self.flyout_hwnd.is_null() {
                self.place_flyout();
            } else if let Some(mut p) = taskbar::rect(self.flyout_hwnd) {
                p.w = (self.flyout.get_content_width() * scale).round() as i32;
                p.h = (self.flyout.get_content_height() * scale).round() as i32;
                p = math::clamp(p, taskbar::monitor(p.x, p.y, true));
                self.move_flyout(p, scale);
            }
        }
    }
    fn begin_setup(&self) {
        self.setup.start(
            slint::TimerMode::Repeated,
            Duration::from_millis(50),
            || with_app(|a| a.setup_windows()),
        );
    }
    fn setup_windows(&mut self) {
        let c = window_style::find("UsageMonitor-Rust-Chips");
        let f = window_style::find("UsageMonitor-Rust-Flyout");
        let mut done = true;
        if self.chips.window().is_visible() {
            if c.is_null() {
                done = false;
            } else if c != self.chips_hwnd {
                self.chips_hwnd = c;
                window_style::tool(c, true);
            }
        }
        if self.flyout.window().is_visible() {
            if f.is_null() {
                done = false;
            } else if f != self.flyout_hwnd {
                self.flyout_hwnd = f;
                window_style::tool(f, false);
                unsafe {
                    windows_sys::Win32::UI::WindowsAndMessaging::SetForegroundWindow(f);
                }
            }
        }
        self.place();
        if done {
            self.setup.stop();
        }
    }
    fn place(&mut self) {
        self.place_chips();
        self.place_flyout();
    }
    fn place_chips(&mut self) {
        let s = win::dpi::scale();
        let (w, h) = (
            (self.chips.get_content_width() * s).round() as i32,
            (self.chips.get_content_height().max(30.0) * s).round() as i32,
        );
        self.chips_rect = if self.config.chips_docked {
            taskbar::dock(w, h, (4.0 * s).round() as i32)
        } else {
            let stored = self.config.chips_x.is_some() && self.config.chips_y.is_some();
            let (x, y) = if stored {
                (self.config.chips_x.unwrap(), self.config.chips_y.unwrap())
            } else {
                (0, 0)
            };
            let area = taskbar::monitor(x, y, true);
            let target = if stored {
                RectI::new(x, y, w, h)
            } else {
                RectI::new(
                    area.x + area.w - w - (12.0 * s).round() as i32,
                    area.y + area.h - h - (8.0 * s).round() as i32,
                    w,
                    h,
                )
            };
            let p = math::clamp(target, area);
            if !stored || !taskbar::point_on_screen(x, y) {
                self.config.chips_x = Some(p.x);
                self.config.chips_y = Some(p.y);
                self.persist();
            }
            p
        };
        let cr = (super::skins::chips_radius(Some(&self.config.skin)) * s).round() as i32;
        if self.chips_hwnd.is_null() {
            preset_position(self.chips.window(), self.chips_rect);
        } else if taskbar::rect(self.chips_hwnd) != Some(self.chips_rect) {
            taskbar::move_to(self.chips_hwnd, self.chips_rect);
            window_style::round(self.chips_hwnd, w, h, cr);
        }
        if self.config.chips_docked
            && (self.config.chips_dock_x != Some(self.chips_rect.x)
                || self.config.chips_dock_y != Some(self.chips_rect.y))
        {
            self.config.chips_dock_x = Some(self.chips_rect.x);
            self.config.chips_dock_y = Some(self.chips_rect.y);
            self.persist();
        }
    }
    fn place_flyout(&self) {
        let s = win::dpi::scale();
        let c = self.chips_rect;
        let (fw, fh) = (
            (self.flyout.get_content_width() * s).round() as i32,
            (self.flyout.get_content_height() * s).round() as i32,
        );
        let p = if !self.config.flyout_docked
            && self.config.flyout_pinned
            && self.config.flyout_x.is_some()
            && self.config.flyout_y.is_some()
        {
            let (x, y) = (self.config.flyout_x.unwrap(), self.config.flyout_y.unwrap());
            math::clamp(RectI::new(x, y, fw, fh), taskbar::monitor(x, y, true))
        } else {
            math::flyout_anchor(
                c,
                taskbar::monitor(c.x + c.w / 2, c.y + c.h / 2, true),
                fw,
                fh,
                (8.0 * s).round() as i32,
            )
        };
        self.move_flyout(p, s);
    }
    fn move_flyout(&self, p: RectI, scale: f32) {
        if self.flyout_hwnd.is_null() {
            preset_position(self.flyout.window(), p);
        } else if taskbar::rect(self.flyout_hwnd) != Some(p) {
            taskbar::move_to(self.flyout_hwnd, p);
            let fr = (super::skins::flyout_radius(Some(&self.config.skin)) * scale).round() as i32;
            window_style::round(self.flyout_hwnd, p.w, p.h, fr);
        }
    }
    fn show(&mut self) {
        self.render();
        if self.flyout_hwnd.is_null() {
            self.place_flyout();
        }
        let _ = self.flyout.show();
        self.flyout_shown = Instant::now();
        // Slint keeps the HWND across hide/show; the styles survive because the subclass filters
        // winit's style rewrites, so only focus is needed here.
        if !self.flyout_hwnd.is_null() {
            unsafe {
                windows_sys::Win32::UI::WindowsAndMessaging::SetForegroundWindow(self.flyout_hwnd);
            }
        }
        self.begin_setup();
    }
    fn tick(&mut self) {
        if self.single.activated() {
            self.show();
        }
        let geometry = taskbar::geometry();
        if self.last_geometry != Some(geometry) {
            self.last_geometry = Some(geometry);
            if self.config.chips_docked {
                self.place_chips();
            }
            if self.config.flyout_docked && self.flyout.window().is_visible() {
                self.place_flyout();
            }
        }
        if self.config.chips_docked && !self.config.chips_hidden {
            window_style::topmost(self.chips_hwnd);
        }
        window_style::frameless(self.chips_hwnd);
        window_style::frameless(self.flyout_hwnd);
        if let Some(s) = &self.current {
            let (next, changed) = models::apply_local_resets(s, Utc::now());
            if changed {
                let _ = self
                    .worker
                    .send(Work::Snapshot(Arc::new(next.clone()), false));
                self.current = Some(next);
                let _ = self.worker.send(Work::Refresh);
                self.render();
            } else if self.flyout.window().is_visible() {
                self.render();
            }
        }
    }
    fn action(&mut self, action: &str) {
        match action {
            "quit" => {
                self.persist();
                let _ = slint::quit_event_loop();
            }
            "refresh" => {
                let _ = self.worker.send(Work::Refresh);
            }
            "show" => self.show(),
            "flyout" => {
                if self.flyout.window().is_visible() && !self.config.flyout_docked {
                    let _ = self.flyout.hide();
                } else {
                    self.show();
                }
            }
            "tray-flyout" => {
                if self.flyout.window().is_visible() {
                    if !self.config.flyout_docked {
                        let _ = self.flyout.hide();
                    }
                } else {
                    self.show();
                }
            }
            "menu-flyout" => {
                if self.flyout.window().is_visible() {
                    let _ = self.flyout.hide();
                } else {
                    self.show();
                }
            }
            "escape" => {
                let _ = self.flyout.hide();
            }
            "deactivate" => {
                if !self.menu_open {
                    slint::Timer::single_shot(Duration::from_millis(180), || {
                        dispatch("blur".into())
                    });
                }
            }
            "blur" => {
                if !self.config.flyout_pinned
                    && !self.config.flyout_docked
                    && !self.menu_open
                    && self.flyout_shown.elapsed() >= Duration::from_millis(180)
                {
                    unsafe {
                        if !self.flyout_hwnd.is_null()
                            && windows_sys::Win32::UI::WindowsAndMessaging::GetForegroundWindow()
                                != self.flyout_hwnd
                        {
                            let _ = self.flyout.hide();
                        }
                    }
                }
            }
            "chips" => {
                self.config.chips_hidden = !self.config.chips_hidden;
                if self.config.chips_hidden {
                    let _ = self.chips.hide();
                } else {
                    let _ = self.chips.show();
                    self.begin_setup();
                }
                self.persist();
            }
            "chips-dock" => {
                self.config.chips_docked = !self.config.chips_docked;
                if !self.config.chips_docked {
                    self.config.chips_x = Some(self.chips_rect.x);
                    self.config.chips_y = Some(self.chips_rect.y);
                } else {
                    self.place_chips();
                }
                self.persist();
            }
            "pin" => {
                self.config.flyout_pinned = !self.config.flyout_pinned;
                if !self.config.flyout_pinned {
                    self.config.flyout_docked = false;
                }
                self.render();
                self.persist();
            }
            "flyout-dock" | "menu-flyout-dock" => {
                self.config.flyout_docked = !self.config.flyout_docked;
                if self.config.flyout_docked {
                    self.config.flyout_pinned = true;
                } else if let Some(p) = taskbar::rect(self.flyout_hwnd) {
                    self.config.flyout_x = Some(p.x);
                    self.config.flyout_y = Some(p.y);
                }
                self.place();
                self.render();
                self.persist();
                if action == "menu-flyout-dock" && self.config.flyout_docked {
                    self.show();
                }
            }
            "autostart" => {
                self.config.autostart = !self.config.autostart;
                let _ = self.worker.send(Work::Autostart(self.config.clone()));
            }
            "explorer" => {
                if let Some(t) = &self.tray {
                    t.add();
                }
                self.last_geometry = None;
            }
            "display" => self.last_geometry = None,
            _ => {
                if let Some(s) = action.strip_prefix("skin:") {
                    let next = super::skins::normalize_skin(Some(s));
                    if self.config.skin != next {
                        self.config.skin = next.into();
                        self.render();
                        let scale = win::dpi::scale();
                        if !self.chips_hwnd.is_null() {
                            let cr = (super::skins::chips_radius(Some(&self.config.skin)) * scale)
                                .round() as i32;
                            window_style::round(
                                self.chips_hwnd,
                                self.chips_rect.w,
                                self.chips_rect.h,
                                cr,
                            );
                        }
                        if !self.flyout_hwnd.is_null() {
                            if let Some(p) = taskbar::rect(self.flyout_hwnd) {
                                let fr = (super::skins::flyout_radius(Some(&self.config.skin))
                                    * scale)
                                    .round() as i32;
                                window_style::round(self.flyout_hwnd, p.w, p.h, fr);
                            }
                        }
                    }
                    self.persist();
                }
                if let Some(s) = action.strip_prefix("interval:") {
                    for command in interval_commands(&mut self.config, s) {
                        let _ = self.worker.send(command);
                    }
                    self.render();
                }
                if let Some(id) = action.strip_prefix("usage:") {
                    let url = match id {
                        "claude" => "https://claude.ai/settings/usage",
                        "codex" => "https://chatgpt.com/codex/settings/usage",
                        "gemini" => "https://antigravity.google",
                        "grok" => "https://grok.com",
                        _ => return,
                    };
                    let url = url.to_string();
                    std::thread::spawn(move || unsafe {
                        windows_sys::Win32::UI::Shell::ShellExecuteW(
                            std::ptr::null_mut(),
                            win::wide("open").as_ptr(),
                            win::wide(&url).as_ptr(),
                            std::ptr::null(),
                            std::ptr::null(),
                            1,
                        );
                    });
                }
            }
        }
    }
    fn drag(&mut self, flyout: bool, _x: f32, _y: f32, phase: i32) {
        if flyout && self.config.flyout_docked {
            return;
        }
        let hwnd = if flyout {
            self.flyout_hwnd
        } else {
            self.chips_hwnd
        };
        let (x, y) = taskbar::cursor();
        if phase == 0 {
            if let Some(p) = taskbar::rect(hwnd) {
                self.drag_start = Some((x, y, p, flyout));
                self.drag_moved = false;
            }
            return;
        }
        if let Some((sx, sy, p, f)) = self.drag_start {
            if f != flyout {
                return;
            }
            if phase == 1 && ((x - sx).abs() >= 3 || (y - sy).abs() >= 3 || self.drag_moved) {
                self.drag_moved = true;
                taskbar::move_to(hwnd, RectI::new(p.x + x - sx, p.y + y - sy, p.w, p.h));
            }
            if phase == 2 {
                self.drag_start = None;
                if self.drag_moved {
                    if let Some(p) = taskbar::rect(hwnd) {
                        if flyout {
                            self.config.flyout_x = Some(p.x);
                            self.config.flyout_y = Some(p.y);
                        } else {
                            self.config.chips_x = Some(p.x);
                            self.config.chips_y = Some(p.y);
                            self.config.chips_docked =
                                math::is_over_tray(p, Some(taskbar::geometry().1));
                            self.chips_rect = p;
                            self.place_chips();
                        }
                        self.persist();
                    }
                } else if !flyout {
                    self.action("flyout");
                }
            }
        }
    }
}
