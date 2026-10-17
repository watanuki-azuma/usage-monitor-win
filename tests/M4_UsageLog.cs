using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UsageMonitor.Tests
{
    public static class UsageLogTests
    {
        static Snapshot Snap(double claudeUsed, string state)
        {
            Snapshot s = new Snapshot();
            s.PollIntervalSecs = 60;
            ProviderSnapshot c = new ProviderSnapshot();
            c.Id = "claude"; c.DisplayName = "Claude Code"; c.Plan = "Pro"; c.Source = "api";
            c.FetchedAt = Dates.Utc(2026, 10, 11, 12, 0, 0);
            c.Status = new ProviderStatus(); c.Status.State = state;
            UsageWindow w = new UsageWindow();
            w.Kind = Kind.FiveHour; w.Label = "5h"; w.UsedPct = claudeUsed; w.RemainingPct = 100 - claudeUsed;
            w.ResetsAt = Dates.Utc(2026, 10, 11, 15, 0, 0);
            c.Windows.Add(w);
            s.Providers.Add(c);
            ProviderSnapshot g = new ProviderSnapshot();
            g.Id = "grok"; g.DisplayName = "Grok Build"; g.Source = "none"; g.FetchedAt = c.FetchedAt;
            g.Status = new ProviderStatus(); g.Status.State = State.NotInstalled; g.Status.Hint = "secret-looking hint must not matter";
            s.Providers.Add(g);
            return s;
        }

        static string[] Lines(string path)
        {
            return File.ReadAllLines(path, Encoding.UTF8);
        }

        public static void Test_append_writes_one_json_line_with_usage_only()
        {
            string dir = TempDir.Create();
            string path = Path.Combine(dir, "usage-log.jsonl");
            UsageLog log = new UsageLog(path);
            DateTime now = Dates.Utc(2026, 10, 11, 12, 0, 5);
            Assert.True(log.Append(Snap(40, State.Ok), now), "written");
            string[] lines = Lines(path);
            Assert.Equal(1, lines.Length, "one line");
            object parsed = new JsonHelper().Parse(lines[0]);
            IDictionary<string, object> root = (IDictionary<string, object>)parsed;
            Assert.Equal("2026-10-11T12:00:05.000Z", (string)root["t"], "timestamp");
            object[] providers = (object[])root["providers"];
            Assert.Equal(2, providers.Length, "providers");
            IDictionary<string, object> claude = (IDictionary<string, object>)providers[0];
            Assert.Equal("claude", (string)claude["id"], "id");
            Assert.Equal("ok", (string)claude["state"], "state");
            Assert.Equal("Pro", (string)claude["plan"], "plan");
            object[] windows = (object[])claude["windows"];
            IDictionary<string, object> w = (IDictionary<string, object>)windows[0];
            Assert.Equal("five_hour", (string)w["kind"], "kind");
            Assert.Equal(40.0, Convert.ToDouble(w["used_pct"]), "used");
            Assert.Equal("2026-10-11T15:00:00.000Z", (string)w["resets_at"], "reset");
            Assert.True(!lines[0].Contains("secret-looking"), "hints/messages are not logged");
        }

        public static void Test_unchanged_snapshots_are_skipped_until_the_heartbeat()
        {
            string dir = TempDir.Create();
            string path = Path.Combine(dir, "usage-log.jsonl");
            UsageLog log = new UsageLog(path);
            DateTime t0 = Dates.Utc(2026, 10, 11, 12, 0, 0);
            Assert.True(log.Append(Snap(40, State.Ok), t0), "first");
            Assert.True(!log.Append(Snap(40, State.Ok), t0.AddSeconds(60)), "same values skipped");
            Assert.True(log.Append(Snap(41, State.Ok), t0.AddSeconds(120)), "changed value written");
            Assert.True(!log.Append(Snap(41, State.Ok), t0.AddMinutes(5)), "still skipped");
            Assert.True(log.Append(Snap(41, State.Ok), t0.AddMinutes(13)), "heartbeat after 10 minutes");
            Assert.True(log.Append(Snap(41, State.Stale), t0.AddMinutes(14)), "state change written");
            Assert.Equal(4, Lines(path).Length, "lines");
        }

        public static void Test_log_rotates_when_it_grows_past_the_limit_and_never_throws()
        {
            string dir = TempDir.Create();
            string path = Path.Combine(dir, "usage-log.jsonl");
            File.WriteAllText(path, new string('x', 5 * 1024 * 1024));
            UsageLog log = new UsageLog(path);
            Assert.True(log.Append(Snap(10, State.Ok), Dates.Utc(2026, 10, 11, 12, 0, 0)), "written after rotation");
            Assert.True(File.Exists(path + ".1"), "old file kept as .1");
            Assert.Equal(1, Lines(path).Length, "fresh file has the new line only");

            // unwritable location: returns false, no exception
            UsageLog bad = new UsageLog(Path.Combine(dir, "no-such-dir", "deeper", "x:", "usage-log.jsonl"));
            bool ok = bad.Append(Snap(10, State.Ok), Dates.Utc(2026, 10, 11, 12, 0, 0));
            Assert.True(!ok, "failure is reported, not thrown");
        }
    }
}
