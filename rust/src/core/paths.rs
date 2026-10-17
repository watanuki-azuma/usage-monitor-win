//! Every path derived from environment variables (port of src/Paths.cs).
//! The "default data dir = folder of the exe" rule lives in `main.rs` (it sets
//! `USAGE_MONITOR_DATA_DIR` from `--data-dir`), exactly like `Program.cs`.
use std::path::{Path, PathBuf};
use std::sync::Arc;

type GetEnv = Arc<dyn Fn(&str) -> Option<String> + Send + Sync>;

/// Windows only; paths.js picks .exe/.cmd/.bat/"" on win32.
const PATH_EXTENSIONS: [&str; 4] = [".exe", ".cmd", ".bat", ""];

#[derive(Clone)]
pub struct Paths {
    get_env: GetEnv,
    home: String,
    app_data_dir: String,
    local_app_data_dir: String,
    data_dir: Option<String>,
    agy_binary_candidates: Vec<String>,
}

/// Like .NET `Path.Combine` (a rooted later part wins).
fn combine(parts: &[&str]) -> String {
    let mut p = PathBuf::new();
    for part in parts {
        p.push(part);
    }
    p.to_string_lossy().into_owned()
}

fn non_empty(v: Option<String>) -> Option<String> {
    v.filter(|s| !s.is_empty())
}

impl Paths {
    pub fn new<F>(get_env: F) -> Paths
    where
        F: Fn(&str) -> Option<String> + Send + Sync + 'static,
    {
        let get_env: GetEnv = Arc::new(get_env);

        // JavaScript's `a || b`: an empty variable counts as unset.
        let home = non_empty(get_env("USERPROFILE")).unwrap_or_else(fallback_home);
        let app_data_dir = non_empty(get_env("APPDATA")).unwrap_or_else(|| combine(&[&home, "AppData", "Roaming"]));
        let local_app_data_dir =
            non_empty(get_env("LOCALAPPDATA")).unwrap_or_else(|| combine(&[&home, "AppData", "Local"]));
        // Packaged builds keep config and cache next to the exe; source runs use AppData.
        let data_dir = non_empty(get_env("USAGE_MONITOR_DATA_DIR"));

        let agy_binary_candidates = vec![
            combine(&[&local_app_data_dir, "agy", "bin", "agy.exe"]),
            combine(&[&local_app_data_dir, "agy", "bin", "agy"]),
        ];

        Paths { get_env, home, app_data_dir, local_app_data_dir, data_dir, agy_binary_candidates }
    }

    /// Reads the real process environment.
    pub fn from_process_env() -> Paths {
        Paths::new(|k| std::env::var(k).ok())
    }

    fn env(&self, name: &str) -> Option<String> {
        (self.get_env)(name)
    }

    fn dir_or(&self, var: &str, default_leaf: &str) -> String {
        non_empty(self.env(var)).unwrap_or_else(|| combine(&[&self.home, default_leaf]))
    }

    pub fn home(&self) -> &str {
        &self.home
    }

    pub fn claude_config_dir(&self) -> String {
        self.dir_or("CLAUDE_CONFIG_DIR", ".claude")
    }

    pub fn claude_credentials(&self) -> String {
        combine(&[&self.claude_config_dir(), ".credentials.json"])
    }

    pub fn codex_home(&self) -> String {
        self.dir_or("CODEX_HOME", ".codex")
    }

    pub fn codex_auth(&self) -> String {
        combine(&[&self.codex_home(), "auth.json"])
    }

    pub fn grok_home(&self) -> String {
        self.dir_or("GROK_HOME", ".grok")
    }

    pub fn grok_auth(&self) -> String {
        combine(&[&self.grok_home(), "auth.json"])
    }

    pub fn gemini_home(&self) -> String {
        self.dir_or("GEMINI_HOME", ".gemini")
    }

    pub fn gemini_oauth(&self) -> String {
        combine(&[&self.gemini_home(), "oauth_creds.json"])
    }

    pub fn antigravity_token(&self) -> String {
        combine(&[&self.gemini_home(), "antigravity-cli", "antigravity-oauth-token"])
    }

    pub fn agy_binary_candidates(&self) -> &[String] {
        &self.agy_binary_candidates
    }

    pub fn config_dir(&self) -> String {
        match &self.data_dir {
            Some(d) => d.clone(),
            None => combine(&[&self.app_data_dir, "UsageMonitor"]),
        }
    }

    pub fn cache_dir(&self) -> String {
        match &self.data_dir {
            Some(d) => d.clone(),
            None => combine(&[&self.local_app_data_dir, "UsageMonitor", "cache"]),
        }
    }

    pub fn config_path(&self) -> String {
        combine(&[&self.config_dir(), "config.json"])
    }

    pub fn snapshot_path(&self) -> String {
        combine(&[&self.cache_dir(), "snapshot.json"])
    }

    pub fn alert_state_path(&self) -> String {
        combine(&[&self.cache_dir(), "alert-state.json"])
    }

    /// cliPath(): first match wins, .exe before .cmd before .bat before the bare name.
    pub fn find_on_path(&self, name: &str) -> Option<String> {
        if name.is_empty() {
            return None;
        }
        for dir in self.path_entries() {
            for ext in PATH_EXTENSIONS.iter() {
                let candidate = Path::new(&dir).join(format!("{}{}", name, ext));
                if candidate.is_file() {
                    return Some(candidate.to_string_lossy().into_owned());
                }
            }
        }
        None
    }

    fn path_entries(&self) -> Vec<String> {
        let path = match non_empty(self.env("PATH")) {
            Some(p) => p,
            None => return Vec::new(),
        };
        let mut entries = Vec::new();
        for raw in path.split(';') {
            let mut entry = raw.trim().to_string();
            if entry.len() >= 2 && entry.starts_with('"') && entry.ends_with('"') {
                entry = entry[1..entry.len() - 1].trim().to_string();
            }
            if !entry.is_empty() {
                entries.push(entry);
            }
        }
        entries
    }
}

fn fallback_home() -> String {
    match std::env::var("USERPROFILE") {
        Ok(d) if !d.is_empty() => d,
        _ => ".".to_string(),
    }
}
