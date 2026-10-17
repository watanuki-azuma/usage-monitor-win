// Everything that turns an environment variable into a concrete file location.
// Port of reference/js/paths.js (milestone M1).
// Language level: C# 5 (in-box csc.exe).
using System;
using System.Collections.Generic;
using System.IO;

namespace UsageMonitor
{
    public class Paths
    {
        // Windows only; reference/js/paths.js picks .exe/.cmd/.bat/"" on win32 as well.
        private static readonly string[] PathExtensions = new string[] { ".exe", ".cmd", ".bat", "" };

        private readonly Func<string, string> getEnv;
        private readonly string home;
        private readonly string appDataDir;
        private readonly string localAppDataDir;
        private readonly string dataDir;
        private readonly string[] agyBinaryCandidates;

        public Paths(Func<string, string> getEnv)
        {
            this.getEnv = getEnv;

            // JavaScript's `a || b`: an empty variable counts as unset.
            this.home = Env("USERPROFILE");
            if (string.IsNullOrEmpty(home)) home = FallbackHome();

            this.appDataDir = Env("APPDATA");
            if (string.IsNullOrEmpty(appDataDir)) appDataDir = Path.Combine(home, "AppData", "Roaming");

            this.localAppDataDir = Env("LOCALAPPDATA");
            if (string.IsNullOrEmpty(localAppDataDir)) localAppDataDir = Path.Combine(home, "AppData", "Local");

            // Packaged builds keep config and cache next to the exe; source runs use AppData.
            this.dataDir = Env("USAGE_MONITOR_DATA_DIR");

            this.agyBinaryCandidates = new string[]
            {
                Path.Combine(localAppDataDir, "agy", "bin", "agy.exe"),
                Path.Combine(localAppDataDir, "agy", "bin", "agy"),
            };
        }

        public string Home { get { return home; } }

        public string ClaudeConfigDir
        {
            get
            {
                string dir = Env("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(dir)) dir = Path.Combine(home, ".claude");
                return dir;
            }
        }

        public string ClaudeCredentials { get { return Path.Combine(ClaudeConfigDir, ".credentials.json"); } }

        public string CodexHome
        {
            get
            {
                string dir = Env("CODEX_HOME");
                if (string.IsNullOrEmpty(dir)) dir = Path.Combine(home, ".codex");
                return dir;
            }
        }

        public string CodexAuth { get { return Path.Combine(CodexHome, "auth.json"); } }

        public string GrokHome
        {
            get
            {
                string dir = Env("GROK_HOME");
                if (string.IsNullOrEmpty(dir)) dir = Path.Combine(home, ".grok");
                return dir;
            }
        }

        public string GrokAuth { get { return Path.Combine(GrokHome, "auth.json"); } }

        public string GeminiHome
        {
            get
            {
                string dir = Env("GEMINI_HOME");
                if (string.IsNullOrEmpty(dir)) dir = Path.Combine(home, ".gemini");
                return dir;
            }
        }

        public string GeminiOAuth { get { return Path.Combine(GeminiHome, "oauth_creds.json"); } }

        public string AntigravityToken { get { return Path.Combine(GeminiHome, "antigravity-cli", "antigravity-oauth-token"); } }

        public string[] AgyBinaryCandidates { get { return agyBinaryCandidates; } }

        public string ConfigDir
        {
            get
            {
                if (!string.IsNullOrEmpty(dataDir)) return dataDir;
                return Path.Combine(appDataDir, "UsageMonitor");
            }
        }

        public string CacheDir
        {
            get
            {
                if (!string.IsNullOrEmpty(dataDir)) return dataDir;
                return Path.Combine(localAppDataDir, "UsageMonitor", "cache");
            }
        }

        public string ConfigPath { get { return Path.Combine(ConfigDir, "config.json"); } }
        public string SnapshotPath { get { return Path.Combine(CacheDir, "snapshot.json"); } }
        public string AlertStatePath { get { return Path.Combine(CacheDir, "alert-state.json"); } }

        // cliPath(): first match wins, .exe before .cmd before .bat before the bare name, and a PATH
        // entry without the extension does not satisfy a name that has one elsewhere.
        public string FindOnPath(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            foreach (string dir in PathEntries())
            {
                for (int i = 0; i < PathExtensions.Length; i++)
                {
                    string candidate;
                    try { candidate = Path.Combine(dir, name + PathExtensions[i]); }
                    catch (ArgumentException) { candidate = null; }
                    if (candidate == null) continue;
                    try
                    {
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (IOException) { /* unreadable directory: keep looking */ }
                    catch (UnauthorizedAccessException) { continue; }
                }
            }
            return null;
        }

        private string[] PathEntries()
        {
            string path = getEnv == null ? null : getEnv("PATH");
            if (string.IsNullOrEmpty(path)) return new string[0];

            string[] parts = path.Split(new char[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
            List<string> entries = new List<string>(parts.Length);
            foreach (string raw in parts)
            {
                string entry = raw.Trim();
                if (entry.Length >= 2 && entry.StartsWith("\"", StringComparison.Ordinal) && entry.EndsWith("\"", StringComparison.Ordinal))
                    entry = entry.Substring(1, entry.Length - 2).Trim();
                if (entry.Length > 0) entries.Add(entry);
            }
            return entries.ToArray();
        }

        private string Env(string name)
        {
            if (getEnv == null || name == null) return null;
            return getEnv(name);
        }

        private static string FallbackHome()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(dir)) dir = Environment.ExpandEnvironmentVariables("%USERPROFILE%");
            return string.IsNullOrEmpty(dir) || dir == "%USERPROFILE%" ? "." : dir;
        }
    }
}
