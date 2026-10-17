using System;
using System.Collections.Generic;
using System.IO;

namespace UsageMonitor.Tests
{
    public static class ModelsTests
    {
        static readonly DateTime Now = Dates.Utc(2026, 10, 11, 12, 0, 0);

        static ProviderSnapshot Provider(params UsageWindow[] windows)
        {
            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = "claude";
            p.DisplayName = "Claude Code";
            p.Status = new ProviderStatus();
            p.Status.State = State.Ok;
            p.Windows.AddRange(windows);
            p.FetchedAt = Now;
            p.Source = "api";
            return p;
        }

        static UsageWindow W(string kind, double? used)
        {
            UsageWindow w = new UsageWindow();
            w.Kind = kind;
            w.Label = kind;
            w.UsedPct = used;
            return w;
        }

        public static void Test_Percentage_rejects_invalid_values()
        {
            object[] bad = new object[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, "", " ", "bad", new object(), true, new int[0] };
            foreach (object v in bad)
            {
                Assert.Null(Models.Percentage(v), "Percentage(" + (v == null ? "null" : v.GetType().Name + ":" + v) + ")");
                Assert.Null(Models.RemainingPct(v), "RemainingPct invalid");
            }
        }

        public static void Test_Percentage_accepts_numbers_and_numeric_strings()
        {
            Assert.Equal(12.5, Models.Percentage("12.5"), "string 12.5");
            Assert.Equal(150.0, Models.Percentage(150), "int 150 preserved (overage)");
            Assert.Equal(0.0, Models.Percentage(-10), "negative clamps to 0");
            Assert.Equal(42.0, Models.Percentage(42L), "long");
            Assert.Equal(7.25, Models.Percentage(7.25m), "decimal");
            Assert.Equal(0.0, Models.Percentage(0), "zero");
            Assert.Equal(100.0, Models.RemainingPct(0), "remaining of 0");
            Assert.Equal(0.0, Models.RemainingPct(150), "remaining never negative");
            Assert.Equal(87.5, Models.RemainingPct("12.5"), "remaining of 12.5");
        }

        public static void Test_WindowOf_normalises_used_remaining_and_reset()
        {
            UsageWindow a = Models.WindowOf(Kind.Daily, "Pro", -10, "invalid");
            Assert.Equal(0.0, a.UsedPct, "used clamped");
            Assert.Equal(100.0, a.RemainingPct, "remaining");
            Assert.Null(a.ResetsAt, "invalid reset -> null");
            Assert.Equal("Pro", a.Label, "label");
            Assert.Equal(Kind.Daily, a.Kind, "kind");

            UsageWindow b = Models.WindowOf(Kind.FiveHour, "5h", 150, "2030-01-01T00:00:00Z");
            Assert.Equal(150.0, b.UsedPct, "overage preserved");
            Assert.Equal(0.0, b.RemainingPct, "no negative remaining");
            Assert.Equal(Dates.Utc(2030, 1, 1, 0, 0, 0), b.ResetsAt, "reset parsed as UTC");

            UsageWindow c = Models.WindowOf(Kind.Weekly, "Weekly", null, "2030-01-01T09:00:00+09:00");
            Assert.Null(c.UsedPct, "null used stays null");
            Assert.Null(c.RemainingPct, "null remaining");
            Assert.Equal(Dates.Utc(2030, 1, 1, 0, 0, 0), c.ResetsAt, "offset converted to UTC");
            Assert.Equal(DateTimeKind.Utc, c.ResetsAt.Value.Kind, "kind is Utc");
        }

        public static void Test_EmptyProvider_shape()
        {
            ProviderStatus st = new ProviderStatus();
            st.State = State.LoggedOut;
            st.Hint = "Run: claude auth login";
            ProviderSnapshot p = Models.EmptyProvider("claude", "Claude Code", st, Now);
            Assert.Equal("claude", p.Id, "id");
            Assert.Equal("Claude Code", p.DisplayName, "name");
            Assert.Equal(State.LoggedOut, p.Status.State, "state");
            Assert.NotNull(p.Windows, "windows list");
            Assert.Equal(0, p.Windows.Count, "no windows");
            Assert.Equal(Now, p.FetchedAt, "fetched at");
            Assert.Equal("none", p.Source, "source");
            Assert.Null(p.Plan, "plan");
        }

        public static void Test_CurrentWindow_prefers_five_hour_then_daily_then_highest()
        {
            UsageWindow five = W(Kind.FiveHour, 5);
            UsageWindow weekly = W(Kind.Weekly, 100);
            Assert.True(object.ReferenceEquals(Models.CurrentWindow(Provider(five, weekly)), five), "five before weekly");
            Assert.True(object.ReferenceEquals(Models.CurrentWindow(Provider(weekly, five)), five), "order independent");
            Assert.True(object.ReferenceEquals(Models.CurrentWindow(Provider(W(Kind.FiveHour, null), weekly)), weekly), "unknown five skipped");
            Assert.Null(Models.CurrentWindow(Provider(W(Kind.Daily, null))), "nothing numeric");

            UsageWindow daily = W(Kind.Daily, 20);
            Assert.True(object.ReferenceEquals(Models.CurrentWindow(Provider(weekly, daily)), daily), "daily fallback before weekly");
            Assert.True(object.ReferenceEquals(Models.CurrentWindow(Provider(weekly)), weekly), "weekly only");

            UsageWindow lo = W(Kind.FiveHour, 10);
            UsageWindow hi = W(Kind.FiveHour, 60);
            Assert.True(object.ReferenceEquals(Models.CurrentWindow(Provider(lo, hi)), hi), "highest among preferred");
        }

        public static void Test_IsAlerting_threshold_is_exactly_80()
        {
            Assert.True(Models.IsAlerting(W(Kind.Daily, 80)), "80 alerts");
            Assert.False(Models.IsAlerting(W(Kind.Daily, 79.99)), "79.99 does not");
            Assert.False(Models.IsAlerting(W(Kind.Daily, null)), "unknown does not");
            Assert.True(Models.IsAlerting(W(Kind.Daily, 100)), "100 alerts");
        }

        public static void Test_ApplyLocalResets_expires_windows_without_mutating_input()
        {
            ProviderSnapshot p = Provider();
            UsageWindow expired = Models.WindowOf(Kind.Daily, "Pro", 99, "1970-01-01T00:00:00.000Z");
            UsageWindow badReset = Models.WindowOf(Kind.Quota, "Q", 40, "bad");
            UsageWindow future = Models.WindowOf(Kind.FiveHour, "5h", 30, "2099-01-01T00:00:00Z");
            p.Windows.Add(expired);
            p.Windows.Add(badReset);
            p.Windows.Add(future);
            Snapshot s = new Snapshot();
            s.Providers.Add(p);

            bool changed;
            Snapshot r = Models.ApplyLocalResets(s, Now, out changed);
            Assert.True(changed, "changed");
            ProviderSnapshot rp = r.Providers[0];
            Assert.Null(rp.Windows[0].UsedPct, "expired used null");
            Assert.Null(rp.Windows[0].RemainingPct, "expired remaining null");
            Assert.Null(rp.Windows[0].ResetsAt, "expired reset null");
            Assert.Equal(Kind.Daily, rp.Windows[0].Kind, "kind kept");
            Assert.Equal("Pro", rp.Windows[0].Label, "label kept");
            Assert.Equal(40.0, rp.Windows[1].UsedPct, "window without reset untouched");
            Assert.Equal(30.0, rp.Windows[2].UsedPct, "future window untouched");
            Assert.Equal(State.Stale, rp.Status.State, "provider becomes stale");

            Assert.Equal(99.0, p.Windows[0].UsedPct, "input window not mutated");
            Assert.Equal(State.Ok, p.Status.State, "input status not mutated");
            Assert.True(p.Windows[0].ResetsAt.HasValue, "input reset kept");

            bool again;
            Models.ApplyLocalResets(r, Now, out again);
            Assert.False(again, "second application is a no-op");
        }

        public static void Test_ApplyLocalResets_boundary_is_inclusive()
        {
            ProviderSnapshot p = Provider(Models.WindowOf(Kind.Daily, "Pro", 50, "2026-10-11T12:00:00Z"));
            Snapshot s = new Snapshot();
            s.Providers.Add(p);
            bool changed;
            Snapshot r = Models.ApplyLocalResets(s, Now, out changed);
            Assert.True(changed, "reset time equal to now counts as expired");
            Assert.Null(r.Providers[0].Windows[0].UsedPct, "used null");
        }
    }

    public static class FormatTests
    {
        static readonly DateTime Now = Dates.Utc(2026, 10, 11, 12, 0, 0);
        static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

        static UsageWindow W(string kind, string label, double? used)
        {
            UsageWindow w = new UsageWindow();
            w.Kind = kind;
            w.Label = label;
            w.UsedPct = used;
            return w;
        }

        public static void Test_FormatUsedTotal()
        {
            Assert.Equal("—", Format.FormatUsedTotal(null, true), "null compact");
            Assert.Equal("—/100%", Format.FormatUsedTotal(null, false), "null full");
            Assert.Equal("—", Format.FormatUsedTotal(W(Kind.Daily, "x", null), true), "unknown compact");
            Assert.Equal("42/100", Format.FormatUsedTotal(W(Kind.Daily, "x", 42.4), true), "compact");
            Assert.Equal("42/100%", Format.FormatUsedTotal(W(Kind.Daily, "x", 42.4), false), "full");
            Assert.Equal("43/100", Format.FormatUsedTotal(W(Kind.Daily, "x", 42.6), true), "rounds");
            Assert.Equal("100/100%", Format.FormatUsedTotal(W(Kind.Daily, "x", 100), false), "100");
        }

        public static void Test_FormatEta_relative()
        {
            Assert.Equal("", Format.FormatEta(null, Now, Utc), "null");
            Assert.Equal("now", Format.FormatEta(Now.AddMinutes(-1), Now, Utc), "past");
            Assert.Equal("now", Format.FormatEta(Now, Now, Utc), "equal");
            Assert.Equal("45m", Format.FormatEta(Now.AddMinutes(45), Now, Utc), "45m");
            Assert.Equal("1h 30m", Format.FormatEta(Now.AddMinutes(90), Now, Utc), "1h 30m");
            Assert.Equal("2h 00m", Format.FormatEta(Now.AddHours(2), Now, Utc), "2h 00m");
            Assert.Equal("1h 05m", Format.FormatEta(Now.AddMinutes(65), Now, Utc), "minutes zero padded");
            Assert.Equal("23h 59m", Format.FormatEta(Now.AddMinutes(23 * 60 + 59), Now, Utc), "just under a day");
        }

        public static void Test_FormatEta_rounds_half_up_like_javascript()
        {
            Assert.Equal("1m", Format.FormatEta(Now.AddSeconds(30), Now, Utc), "0.5 min -> 1m");
            Assert.Equal("2m", Format.FormatEta(Now.AddSeconds(90), Now, Utc), "1.5 min -> 2m");
            Assert.Equal("1m", Format.FormatEta(Now.AddSeconds(89), Now, Utc), "1.48 min -> 1m");
        }

        public static void Test_FormatEta_absolute_for_a_day_or_more()
        {
            Assert.Equal("Wed 3:05 PM", Format.FormatEta(Dates.Utc(2026, 10, 14, 15, 5, 0), Now, Utc), "afternoon");
            Assert.Equal("Wed 12:05 AM", Format.FormatEta(Dates.Utc(2026, 10, 14, 0, 5, 0), Now, Utc), "just after midnight");
            Assert.Equal("Wed 12:30 PM", Format.FormatEta(Dates.Utc(2026, 10, 14, 12, 30, 0), Now, Utc), "noon hour");
            TimeZoneInfo jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
            Assert.Equal("Thu 12:05 AM", Format.FormatEta(Dates.Utc(2026, 10, 14, 15, 5, 0), Now, jst), "converted to the supplied zone");
            Assert.Equal("Mon 12:00 PM", Format.FormatEta(Now.AddDays(1), Now, Utc), "exactly 24h is absolute");
        }

        public static void Test_FlyoutLabel()
        {
            Assert.Equal("Pro", Format.FlyoutLabel(W(Kind.Daily, "Pro", 1)), "custom label wins");
            Assert.Equal("5h", Format.FlyoutLabel(W(Kind.FiveHour, "5h", 1)), "five hour");
            Assert.Equal("Wk", Format.FlyoutLabel(W(Kind.Weekly, "Weekly", 1)), "weekly");
            Assert.Equal("Daily", Format.FlyoutLabel(W(Kind.Daily, "Daily", 1)), "daily keeps label");
            Assert.Equal("Credits", Format.FlyoutLabel(W(Kind.Credits, "Credits", null)), "credits");
            Assert.Equal("Weekly", Format.FlyoutLabel(W(Kind.WeeklyScoped, "Weekly", 1)), "scoped falls back to label");
            Assert.Equal("Opus weekly", Format.FlyoutLabel(W(Kind.WeeklyScoped, "Opus weekly", 1)), "scoped custom");
        }

        static ProviderSnapshot P(string state, string hint, string message)
        {
            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = "x";
            p.Status = new ProviderStatus();
            p.Status.State = state;
            p.Status.Hint = hint;
            p.Status.Message = message;
            return p;
        }

        public static void Test_StatusText()
        {
            Assert.Equal("Run: claude auth login", Format.StatusText(P(State.LoggedOut, "Run: claude auth login", null)), "logged out hint");
            Assert.Equal("Sign in", Format.StatusText(P(State.LoggedOut, null, null)), "logged out default");
            Assert.Equal("Not installed", Format.StatusText(P(State.NotInstalled, null, null)), "not installed default");
            Assert.Equal("Install Codex", Format.StatusText(P(State.NotInstalled, "Install Codex", null)), "not installed hint");
            Assert.Equal("HTTP 500", Format.StatusText(P(State.FetchFailed, null, "HTTP 500")), "failed message");
            Assert.Equal("Error", Format.StatusText(P(State.FetchFailed, null, null)), "failed default");
            Assert.Equal("n/a", Format.StatusText(P(State.Unknown, null, null)), "unknown default");
            Assert.Null(Format.StatusText(P(State.Ok, null, null)), "ok -> null");
            Assert.Null(Format.StatusText(P(State.Stale, null, null)), "stale -> null");
        }

        public static void Test_StaleText()
        {
            ProviderSnapshot p = P(State.Stale, null, null);
            Assert.Equal("stale", Format.StaleText(p, Now, Utc), "plain stale");
            p.Status.Reason = "rate_limited";
            Assert.Equal("rate limited", Format.StaleText(p, Now, Utc), "no retry time");
            p.Status.RetryAt = Now.AddMinutes(90);
            Assert.Equal("rate limited \u00B7 retry in 1h 30m", Format.StaleText(p, Now, Utc), "with retry");
            p.Status.RetryAt = Now.AddMinutes(-5);
            Assert.Equal("rate limited", Format.StaleText(p, Now, Utc), "retry in the past");
        }
    }

    public static class SnapshotJsonTests
    {
        static readonly DateTime Now = Dates.Utc(2026, 10, 11, 12, 0, 0);

        static string Sample()
        {
            return "{\"generated_at\":\"2026-10-11T05:43:53.022Z\",\"poll_interval_secs\":60,\"providers\":["
                + "{\"id\":\"claude\",\"display_name\":\"Claude Code\",\"status\":{\"state\":\"ok\"},\"plan\":\"Pro\",\"windows\":["
                + "{\"kind\":\"five_hour\",\"label\":\"5h\",\"used_pct\":98,\"remaining_pct\":2,\"resets_at\":\"2026-10-11T08:19:59.648Z\"},"
                + "{\"kind\":\"weekly\",\"label\":\"Weekly\",\"used_pct\":16.5,\"remaining_pct\":83.5,\"resets_at\":\"2026-10-17T16:59:59.648Z\"}],"
                + "\"fetched_at\":\"2026-10-11T05:40:50.830Z\",\"source\":\"api\"},"
                + "{\"id\":\"codex\",\"display_name\":\"Codex\",\"status\":{\"state\":\"stale\",\"age_secs\":12,\"reason\":\"rate_limited\",\"retry_at\":\"2026-10-11T06:00:00.000Z\"},"
                + "\"plan\":\"Plus\",\"windows\":[{\"kind\":\"credits\",\"label\":\"Reset credits x1\",\"used_pct\":null,\"remaining_pct\":null,\"resets_at\":null}],"
                + "\"footnotes\":[\"a & b\",\"c\"],\"fetched_at\":\"2026-10-11T05:43:50.982Z\",\"source\":\"cache\"}]}";
        }

        public static void Test_Parse_reads_the_electron_format()
        {
            Snapshot s = SnapshotJson.Parse(Sample());
            Assert.NotNull(s, "parsed");
            Assert.Equal(60, s.PollIntervalSecs, "interval");
            Assert.Equal(Dates.Utc(2026, 10, 11, 5, 43, 53).AddMilliseconds(22), s.GeneratedAt, "generated_at");
            Assert.Equal(2, s.Providers.Count, "providers");
            ProviderSnapshot c = s.Providers[0];
            Assert.Equal("claude", c.Id, "id");
            Assert.Equal("Pro", c.Plan, "plan");
            Assert.Equal(State.Ok, c.Status.State, "state");
            Assert.Equal(2, c.Windows.Count, "windows");
            Assert.Equal(98.0, c.Windows[0].UsedPct, "used");
            Assert.Equal(2.0, c.Windows[0].RemainingPct, "remaining");
            Assert.Equal(Kind.FiveHour, c.Windows[0].Kind, "kind");
            Assert.Equal(16.5, c.Windows[1].UsedPct, "fractional used");
            Assert.Equal("api", c.Source, "source");
            ProviderSnapshot x = s.Providers[1];
            Assert.Equal(State.Stale, x.Status.State, "stale state");
            Assert.Equal("rate_limited", x.Status.Reason, "reason");
            Assert.Equal(Dates.Utc(2026, 10, 11, 6, 0, 0), x.Status.RetryAt, "retry_at");
            Assert.Equal(12, x.Status.AgeSecs, "age_secs");
            Assert.Null(x.Windows[0].UsedPct, "credits unknown used");
            Assert.Equal(2, x.Footnotes.Count, "footnotes");
        }

        public static void Test_Parse_rejects_or_sanitises_bad_input()
        {
            Assert.Null(SnapshotJson.Parse(null), "null");
            Assert.Null(SnapshotJson.Parse(""), "empty");
            Assert.Null(SnapshotJson.Parse("not json"), "garbage");
            Assert.Null(SnapshotJson.Parse("{\"providers\":\"bad\"}"), "providers not array");
            Assert.Null(SnapshotJson.Parse("[]"), "array root");
            Assert.Null(SnapshotJson.Parse("{\"providers\":[]}"), "no providers");

            string messy = "{\"providers\":[null,{\"id\":\"nope\",\"status\":{\"state\":\"ok\"},\"windows\":[],\"fetched_at\":\"2026-10-11T00:00:00Z\"},"
                + "{\"id\":\"claude\",\"windows\":{}},"
                + "{\"id\":\"claude\",\"status\":{\"state\":\"ok\"},\"windows\":[null,{\"kind\":\"daily\"},{\"label\":\"x\"},"
                + "{\"kind\":\"daily\",\"label\":\"Pro\",\"used_pct\":\"bad\",\"resets_at\":\"bad\"},"
                + "{\"kind\":\"weekly\",\"label\":\"Weekly\",\"used_pct\":\"12.5\"}],\"fetched_at\":\"2026-10-11T00:00:00Z\"},"
                + "{\"id\":\"codex\",\"status\":{\"state\":\"ok\"},\"windows\":[],\"fetched_at\":\"garbage\"}]}";
            Snapshot s = SnapshotJson.Parse(messy);
            Assert.NotNull(s, "kept the one valid provider");
            Assert.Equal(1, s.Providers.Count, "only the valid provider remains");
            ProviderSnapshot p = s.Providers[0];
            Assert.Equal(2, p.Windows.Count, "invalid windows dropped");
            Assert.Null(p.Windows[0].UsedPct, "bad used -> null");
            Assert.Null(p.Windows[0].ResetsAt, "bad reset -> null");
            Assert.Equal(12.5, p.Windows[1].UsedPct, "numeric string accepted");
        }

        public static void Test_Serialize_roundtrip_and_key_names()
        {
            Snapshot s = SnapshotJson.Parse(Sample());
            string json = SnapshotJson.Serialize(s);
            Assert.Contains(json, "\"generated_at\"", "generated_at key");
            Assert.Contains(json, "\"poll_interval_secs\"", "interval key");
            Assert.Contains(json, "\"display_name\"", "display_name key");
            Assert.Contains(json, "\"used_pct\"", "used_pct key");
            Assert.Contains(json, "\"remaining_pct\"", "remaining_pct key");
            Assert.Contains(json, "\"resets_at\": \"2026-10-11T08:19:59.648Z\"", "iso date with millis");
            Assert.Contains(json, "\"fetched_at\"", "fetched_at key");
            Assert.True(json.IndexOf("RateLimited", StringComparison.OrdinalIgnoreCase) < 0, "no in-memory fields leak");
            Assert.True(json.IndexOf("_rateLimited", StringComparison.OrdinalIgnoreCase) < 0, "no _rateLimited");

            Snapshot again = SnapshotJson.Parse(json);
            Assert.NotNull(again, "reparse");
            Assert.Equal(2, again.Providers.Count, "providers");
            Assert.Equal(16.5, again.Providers[0].Windows[1].UsedPct, "value survives");
            Assert.Equal(s.Providers[0].FetchedAt, again.Providers[0].FetchedAt, "fetched_at survives");
            Assert.Equal("rate_limited", again.Providers[1].Status.Reason, "reason survives");
        }

        public static void Test_Serialize_is_valid_for_usage_py()
        {
            // usage.py reads these keys with python's json module; check for stray non-JSON values.
            Snapshot s = SnapshotJson.Parse(Sample());
            string json = SnapshotJson.Serialize(s);
            Assert.True(json.IndexOf("NaN", StringComparison.Ordinal) < 0, "no NaN");
            Assert.True(json.IndexOf("\\/", StringComparison.Ordinal) < 0, "no escaped slashes needed");
            Assert.Contains(json, "a & b", "ampersand is not HTML-escaped");
        }
    }

    public static class PathsTests
    {
        static Func<string, string> Env(Dictionary<string, string> map)
        {
            return delegate(string k) { string v; return map.TryGetValue(k, out v) ? v : null; };
        }

        static Dictionary<string, string> Base()
        {
            Dictionary<string, string> m = new Dictionary<string, string>();
            m["USERPROFILE"] = @"C:\Users\u";
            m["APPDATA"] = @"C:\Users\u\AppData\Roaming";
            m["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local";
            return m;
        }

        public static void Test_default_locations()
        {
            Paths p = new Paths(Env(Base()));
            Assert.Equal(@"C:\Users\u", p.Home, "home");
            Assert.Equal(@"C:\Users\u\.claude\.credentials.json", p.ClaudeCredentials, "claude");
            Assert.Equal(@"C:\Users\u\.codex\auth.json", p.CodexAuth, "codex");
            Assert.Equal(@"C:\Users\u\.grok\auth.json", p.GrokAuth, "grok");
            Assert.Equal(@"C:\Users\u\.gemini\oauth_creds.json", p.GeminiOAuth, "gemini");
            Assert.Equal(@"C:\Users\u\.gemini\antigravity-cli\antigravity-oauth-token", p.AntigravityToken, "antigravity");
            Assert.Equal(@"C:\Users\u\AppData\Roaming\UsageMonitor", p.ConfigDir, "config dir");
            Assert.Equal(@"C:\Users\u\AppData\Roaming\UsageMonitor\config.json", p.ConfigPath, "config path");
            Assert.Equal(@"C:\Users\u\AppData\Local\UsageMonitor\cache", p.CacheDir, "cache dir");
            Assert.Equal(@"C:\Users\u\AppData\Local\UsageMonitor\cache\snapshot.json", p.SnapshotPath, "snapshot path");
            Assert.Equal(@"C:\Users\u\AppData\Local\UsageMonitor\cache\alert-state.json", p.AlertStatePath, "alert path");
            Assert.True(p.AgyBinaryCandidates.Length >= 1, "agy candidates");
            Assert.Equal(@"C:\Users\u\AppData\Local\agy\bin\agy.exe", p.AgyBinaryCandidates[0], "first agy candidate");
        }

        public static void Test_environment_overrides()
        {
            Dictionary<string, string> m = Base();
            m["CLAUDE_CONFIG_DIR"] = @"D:\cc";
            m["CODEX_HOME"] = @"D:\cx";
            m["GROK_HOME"] = @"D:\gk";
            m["GEMINI_HOME"] = @"D:\gm";
            m["USAGE_MONITOR_DATA_DIR"] = @"D:\data";
            Paths p = new Paths(Env(m));
            Assert.Equal(@"D:\cc\.credentials.json", p.ClaudeCredentials, "claude override");
            Assert.Equal(@"D:\cx\auth.json", p.CodexAuth, "codex override");
            Assert.Equal(@"D:\gk\auth.json", p.GrokAuth, "grok override");
            Assert.Equal(@"D:\gm\oauth_creds.json", p.GeminiOAuth, "gemini override");
            Assert.Equal(@"D:\gm\antigravity-cli\antigravity-oauth-token", p.AntigravityToken, "antigravity follows gemini home");
            Assert.Equal(@"D:\data", p.ConfigDir, "data dir config");
            Assert.Equal(@"D:\data", p.CacheDir, "data dir cache");
            Assert.Equal(@"D:\data\snapshot.json", p.SnapshotPath, "snapshot in data dir");
            Assert.Equal(@"D:\data\config.json", p.ConfigPath, "config in data dir");
        }

        public static void Test_FindOnPath_tries_extensions_in_order()
        {
            string dir = TempDir.Create();
            try
            {
                File.WriteAllText(Path.Combine(dir, "tool.cmd"), "x");
                File.WriteAllText(Path.Combine(dir, "bare"), "x");
                Dictionary<string, string> m = Base();
                m["PATH"] = @"C:\nonexistent;" + dir;
                Paths p = new Paths(Env(m));
                Assert.Equal(Path.Combine(dir, "tool.cmd"), p.FindOnPath("tool"), "cmd found");
                Assert.Equal(Path.Combine(dir, "bare"), p.FindOnPath("bare"), "bare name found");
                Assert.Null(p.FindOnPath("missing"), "missing");
                File.WriteAllText(Path.Combine(dir, "tool.exe"), "x");
                Assert.Equal(Path.Combine(dir, "tool.exe"), p.FindOnPath("tool"), ".exe preferred over .cmd");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }

    public static class AppConfigTests
    {
        public static void Test_defaults()
        {
            AppConfig c = AppConfig.Default();
            Assert.Equal(5, c.PollIntervalSecs, "interval");
            Assert.Equal(5, c.ConfigVersion, "version");
            Assert.True(c.ChipsDocked, "docked default");
            Assert.False(c.ChipsHidden, "hidden default");
            Assert.True(c.Autostart, "autostart default");
            Assert.True(c.NotifyOnLimitReached, "notify default");
            Assert.Null(c.ChipsX, "x default");
            Assert.True(c.RefreshTokens("claude"), "claude refresh default");
            Assert.Equal(4, AppConfig.AllowedIntervals.Length, "allowed intervals");
        }

        public static void Test_Parse_is_tolerant()
        {
            AppConfig c = AppConfig.Parse("{\"poll_interval_secs\":0,\"chips_hidden\":\"false\",\"chips_x\":\"bad\",\"chips_docked\":false,\"adapters\":{\"claude\":null,\"grok\":{\"refresh_tokens\":false}}}");
            Assert.Equal(5, c.PollIntervalSecs, "invalid interval -> 5");
            Assert.False(c.ChipsHidden, "string boolean -> default false");
            Assert.Null(c.ChipsX, "bad coordinate -> null");
            Assert.False(c.ChipsDocked, "explicit false kept");
            Assert.True(c.RefreshTokens("claude"), "null adapter -> true");
            Assert.False(c.RefreshTokens("grok"), "explicit false kept");
            Assert.True(c.RefreshTokens("gemini"), "missing adapter -> true");

            Assert.Equal(5, AppConfig.Parse(null).PollIntervalSecs, "null -> defaults");
            Assert.Equal(5, AppConfig.Parse("not json").PollIntervalSecs, "garbage -> defaults");
            Assert.Equal(5, AppConfig.Parse("[1,2]").PollIntervalSecs, "array -> defaults");
            Assert.Equal(60, AppConfig.Parse("{\"poll_interval_secs\":60}").PollIntervalSecs, "60 allowed");
            Assert.Equal(15, AppConfig.Parse("{\"poll_interval_secs\":\"15\"}").PollIntervalSecs, "numeric string allowed");
            Assert.Equal(5, AppConfig.Parse("{\"poll_interval_secs\":7}").PollIntervalSecs, "7 not allowed");
        }

        public static void Test_Parse_reads_coordinates_and_flags()
        {
            AppConfig c = AppConfig.Parse("{\"chips_x\":89,\"chips_y\":1030,\"chips_dock_x\":62,\"chips_dock_y\":33,\"flyout_x\":1937,\"flyout_y\":1137,"
                + "\"flyout_docked\":true,\"flyout_pinned\":true,\"autostart\":false,\"config_version\":4,\"notify_on_limit_reached\":false}");
            Assert.Equal(89, c.ChipsX, "chips_x");
            Assert.Equal(1030, c.ChipsY, "chips_y");
            Assert.Equal(62, c.ChipsDockX, "dock x");
            Assert.Equal(33, c.ChipsDockY, "dock y");
            Assert.Equal(1937, c.FlyoutX, "flyout x");
            Assert.True(c.FlyoutDocked, "flyout docked");
            Assert.True(c.FlyoutPinned, "flyout pinned");
            Assert.False(c.Autostart, "autostart false");
            Assert.False(c.NotifyOnLimitReached, "notify false");
            Assert.Equal(4, c.ConfigVersion, "version carried");
        }

        public static void Test_Parse_does_not_share_mutable_state()
        {
            AppConfig a = AppConfig.Parse("{\"adapters\":{\"claude\":null}}");
            a.SetRefreshTokens("claude", false);
            a.PollIntervalSecs = 60;
            AppConfig b = AppConfig.Parse(null);
            Assert.True(b.RefreshTokens("claude"), "fresh object");
            Assert.Equal(5, b.PollIntervalSecs, "fresh interval");
            AppConfig d = AppConfig.Default();
            Assert.True(d.RefreshTokens("claude"), "default object fresh");
        }

        public static void Test_ToJson_roundtrip_and_key_order()
        {
            AppConfig c = AppConfig.Default();
            c.PollIntervalSecs = 30;
            c.ChipsX = 10;
            c.SetRefreshTokens("gemini", false);
            string json = c.ToJson();
            int i1 = json.IndexOf("\"poll_interval_secs\"", StringComparison.Ordinal);
            int i2 = json.IndexOf("\"config_version\"", StringComparison.Ordinal);
            int i3 = json.IndexOf("\"chips_docked\"", StringComparison.Ordinal);
            int i4 = json.IndexOf("\"adapters\"", StringComparison.Ordinal);
            Assert.True(i1 >= 0 && i1 < i2 && i2 < i3 && i3 < i4, "key order");
            Assert.Contains(json, "\"chips_y\": null", "null coordinates written as null");
            AppConfig d = AppConfig.Parse(json);
            Assert.Equal(30, d.PollIntervalSecs, "interval roundtrip");
            Assert.Equal(10, d.ChipsX, "x roundtrip");
            Assert.False(d.RefreshTokens("gemini"), "adapter flag roundtrip");
            Assert.True(d.RefreshTokens("claude"), "other adapter untouched");
        }
    }
}
