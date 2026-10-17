using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace UsageMonitor.Tests
{
    public class FakeAdapter : IUsageAdapter
    {
        string id;
        public int Calls;
        public Func<AdapterContext, ProviderSnapshot> Fetch;

        public FakeAdapter(string id)
        {
            this.id = id;
        }

        public string Id { get { return id; } }
        public string DisplayName { get { return id.ToUpperInvariant(); } }

        public ProviderSnapshot FetchUsage(AdapterContext ctx)
        {
            Interlocked.Increment(ref Calls);
            return Fetch(ctx);
        }
    }

    public class MemoryStore : ISnapshotStore
    {
        public Snapshot Saved;
        public bool ThrowOnSave;
        public int Saves;

        public Snapshot Load() { return Saved; }

        public void Save(Snapshot snapshot)
        {
            Saves++;
            if (ThrowOnSave) throw new IOException("disk full");
            Saved = snapshot;
        }
    }

    public class Clock
    {
        public DateTime Now = Dates.Utc(2026, 10, 11, 12, 0, 0);
        public DateTime Get() { return Now; }
    }

    public static class PollEngineTests
    {
        static ProviderSnapshot OkProvider(string id, double used, DateTime now)
        {
            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = id;
            p.DisplayName = id.ToUpperInvariant();
            p.Status = new ProviderStatus();
            p.Status.State = State.Ok;
            p.Windows.Add(Models.WindowOf(Kind.Daily, "Pro", used, now.AddHours(3).ToString("o")));
            p.FetchedAt = now;
            p.Source = "api";
            return p;
        }

        static ProviderSnapshot RateLimited(string id, DateTime now)
        {
            ProviderStatus st = new ProviderStatus();
            st.State = State.FetchFailed;
            st.Message = "rate limited";
            ProviderSnapshot p = Models.EmptyProvider(id, id.ToUpperInvariant(), st, now);
            p.RateLimited = true;
            return p;
        }

        class Rig
        {
            public Clock Clock = new Clock();
            public MemoryStore Store = new MemoryStore();
            public List<FakeAdapter> Adapters = new List<FakeAdapter>();
            public PollEngine Engine;
            public AdapterContext Ctx = new AdapterContext();

            public Rig(int timeoutMs)
            {
                foreach (string id in Models.ProviderIds)
                {
                    FakeAdapter a = new FakeAdapter(id);
                    string captured = id;
                    a.Fetch = delegate(AdapterContext c) { return OkProvider(captured, 50, Clock.Now); };
                    Adapters.Add(a);
                }
                List<IUsageAdapter> list = new List<IUsageAdapter>();
                foreach (FakeAdapter a in Adapters) list.Add(a);
                Engine = new PollEngine(list, Store, Clock.Get, timeoutMs);
                Ctx.Config = AppConfig.Default();
                Ctx.UtcNow = Clock.Get;
            }

            public int TotalCalls()
            {
                int n = 0;
                foreach (FakeAdapter a in Adapters) n += a.Calls;
                return n;
            }
        }

        public static void Test_poll_returns_every_provider_in_order_and_saves()
        {
            Rig r = new Rig(5000);
            Snapshot s = r.Engine.PollOnce(r.Ctx, 30);
            Assert.Equal(4, s.Providers.Count, "providers");
            Assert.Equal("claude", s.Providers[0].Id, "order 0");
            Assert.Equal("grok", s.Providers[3].Id, "order 3");
            Assert.Equal(30, s.PollIntervalSecs, "interval carried");
            Assert.Equal(r.Clock.Now, s.GeneratedAt, "generated at");
            Assert.Equal(4, r.TotalCalls(), "each adapter once");
            Assert.NotNull(r.Store.Saved, "snapshot persisted");
            Assert.Equal(1, r.Store.Saves, "saved once");
        }

        public static void Test_save_failure_does_not_lose_fresh_data()
        {
            Rig r = new Rig(5000);
            r.Store.ThrowOnSave = true;
            Snapshot s = r.Engine.PollOnce(r.Ctx, 5);
            Assert.Equal(4, s.Providers.Count, "fresh data delivered");
            Assert.Equal(State.Ok, s.Providers[0].Status.State, "state ok");
        }

        public static void Test_concurrent_polls_coalesce()
        {
            Rig r = new Rig(5000);
            foreach (FakeAdapter a in r.Adapters)
            {
                FakeAdapter self = a;
                string id = a.Id;
                a.Fetch = delegate(AdapterContext c) { Thread.Sleep(300); return OkProvider(id, 10, r.Clock.Now); };
            }
            Snapshot s1 = null, s2 = null;
            Thread t1 = new Thread(delegate() { s1 = r.Engine.PollOnce(r.Ctx, 5); });
            Thread t2 = new Thread(delegate() { Thread.Sleep(50); s2 = r.Engine.PollOnce(r.Ctx, 5); });
            t1.Start(); t2.Start();
            t1.Join(); t2.Join();
            Assert.Equal(4, r.TotalCalls(), "adapters called once in total");
            Assert.True(object.ReferenceEquals(s1, s2), "both callers get the same snapshot");
        }

        public static void Test_timed_out_adapter_is_never_started_twice_and_late_result_is_used()
        {
            Rig r = new Rig(150);
            ManualResetEvent gate = new ManualResetEvent(false);
            FakeAdapter claude = r.Adapters[0];
            claude.Fetch = delegate(AdapterContext c)
            {
                gate.WaitOne(10000);
                return OkProvider("claude", 93, r.Clock.Now);
            };
            Snapshot first = r.Engine.PollOnce(r.Ctx, 5);
            Assert.Equal(State.FetchFailed, first.Providers[0].Status.State, "timed out without cache -> fetch_failed");
            Assert.Equal(1, claude.Calls, "started once");
            r.Engine.PollOnce(r.Ctx, 5);
            Assert.Equal(1, claude.Calls, "not started again while the first is still pending");
            gate.Set();
            Thread.Sleep(100);
            Snapshot third = r.Engine.PollOnce(r.Ctx, 5);
            Assert.Equal(1, claude.Calls, "late result consumed instead of re-fetching");
            Assert.Equal(93.0, third.Providers[0].Windows[0].UsedPct, "late value shown");
        }

        public static void Test_rate_limit_backoff_doubles_up_to_30_minutes()
        {
            Rig r = new Rig(5000);
            FakeAdapter codex = r.Adapters[1];
            codex.Fetch = delegate(AdapterContext c) { return RateLimited("codex", r.Clock.Now); };
            int[] minutes = new int[] { 2, 4, 8, 16, 30, 30 };
            int expectedCalls = 0;
            foreach (int m in minutes)
            {
                r.Engine.PollOnce(r.Ctx, 5);
                expectedCalls++;
                Assert.Equal(expectedCalls, codex.Calls, "poll at start of backoff step " + m + " calls the adapter");
                r.Clock.Now = r.Clock.Now.AddSeconds(m * 60 - 1);
                r.Engine.PollOnce(r.Ctx, 5);
                Assert.Equal(expectedCalls, codex.Calls, "1s before " + m + " min: still backing off");
                r.Clock.Now = r.Clock.Now.AddSeconds(2);
            }
            r.Engine.PollOnce(r.Ctx, 5);
            Assert.Equal(expectedCalls + 1, codex.Calls, "after the last backoff the adapter runs again");
        }

        public static void Test_rate_limited_provider_with_cache_is_stale_with_reason_and_retry_time()
        {
            Rig r = new Rig(5000);
            FakeAdapter claude = r.Adapters[0];
            Snapshot good = r.Engine.PollOnce(r.Ctx, 5);
            Assert.Equal(State.Ok, good.Providers[0].Status.State, "first ok");
            r.Clock.Now = r.Clock.Now.AddMinutes(10);
            claude.Fetch = delegate(AdapterContext c) { return RateLimited("claude", r.Clock.Now); };
            Snapshot s = r.Engine.PollOnce(r.Ctx, 5);
            ProviderSnapshot p = s.Providers[0];
            Assert.Equal(State.Stale, p.Status.State, "stale");
            Assert.Equal("rate_limited", p.Status.Reason, "reason");
            Assert.Equal(r.Clock.Now.AddMinutes(2), p.Status.RetryAt, "retry in 2 minutes");
            Assert.Equal(50.0, p.Windows[0].UsedPct, "cached value shown");
            Assert.Equal("cache", p.Source, "source cache");
        }

        public static void Test_claude_is_polled_at_most_every_5_minutes()
        {
            Rig r = new Rig(5000);
            r.Engine.PollOnce(r.Ctx, 5);
            r.Clock.Now = r.Clock.Now.AddSeconds(60);
            r.Engine.PollOnce(r.Ctx, 5);
            Assert.Equal(1, r.Adapters[0].Calls, "claude skipped inside 5 minutes");
            Assert.Equal(2, r.Adapters[1].Calls, "codex polled every time");
            r.Clock.Now = r.Clock.Now.AddSeconds(5 * 60);
            r.Engine.PollOnce(r.Ctx, 5);
            Assert.Equal(2, r.Adapters[0].Calls, "claude polled again after 5 minutes");
        }

        public static void Test_adapter_exception_becomes_fetch_failed_for_that_provider_only()
        {
            Rig r = new Rig(5000);
            r.Adapters[2].Fetch = delegate(AdapterContext c) { throw new InvalidOperationException("kaput"); };
            Snapshot s = r.Engine.PollOnce(r.Ctx, 5);
            Assert.Equal(State.FetchFailed, s.Providers[2].Status.State, "failed provider");
            Assert.Contains(s.Providers[2].Status.Message, "kaput", "message");
            Assert.Equal(State.Ok, s.Providers[0].Status.State, "others unaffected");
            Assert.Equal(State.Ok, s.Providers[3].Status.State, "others unaffected");
        }

        public static void Test_expired_windows_are_reset_in_the_result()
        {
            Rig r = new Rig(5000);
            r.Adapters[0].Fetch = delegate(AdapterContext c)
            {
                ProviderSnapshot p = OkProvider("claude", 77, r.Clock.Now);
                p.Windows[0].ResetsAt = r.Clock.Now.AddMinutes(-1);
                return p;
            };
            Snapshot s = r.Engine.PollOnce(r.Ctx, 5);
            Assert.Null(s.Providers[0].Windows[0].UsedPct, "expired window unknown");
            Assert.Equal(State.Stale, s.Providers[0].Status.State, "provider stale");
        }

        public static void Test_MergeWithCache()
        {
            DateTime now = Dates.Utc(2026, 10, 11, 12, 0, 0);
            ProviderSnapshot cached = OkProvider("claude", 88, now.AddMinutes(-3));

            ProviderStatus failed = new ProviderStatus();
            failed.State = State.FetchFailed;
            ProviderSnapshot fresh = Models.EmptyProvider("claude", "Claude Code", failed, now);
            ProviderSnapshot merged = PollEngine.MergeWithCache(fresh, cached, now);
            Assert.Equal(State.Stale, merged.Status.State, "stale");
            Assert.Equal(88.0, merged.Windows[0].UsedPct, "cached windows");
            Assert.Equal(cached.FetchedAt, merged.FetchedAt, "original timestamp");
            Assert.Equal("cache", merged.Source, "source");
            Assert.Equal(180, merged.Status.AgeSecs, "age in seconds");

            ProviderStatus lo = new ProviderStatus();
            lo.State = State.LoggedOut;
            ProviderSnapshot loggedOut = Models.EmptyProvider("claude", "Claude Code", lo, now);
            Assert.True(object.ReferenceEquals(PollEngine.MergeWithCache(loggedOut, cached, now), loggedOut), "logged_out is not masked by cache");

            ProviderSnapshot ok = OkProvider("claude", 10, now);
            Assert.True(object.ReferenceEquals(PollEngine.MergeWithCache(ok, cached, now), ok), "ok wins");
            Assert.True(object.ReferenceEquals(PollEngine.MergeWithCache(fresh, null, now), fresh), "no cache -> fresh");

            ProviderSnapshot emptyCache = Models.EmptyProvider("claude", "Claude Code", failed, now.AddMinutes(-1));
            Assert.True(object.ReferenceEquals(PollEngine.MergeWithCache(fresh, emptyCache, now), fresh), "cache without windows is useless");
        }
    }

    public static class AlertEngineTests
    {
        static readonly DateTime Now = Dates.Utc(2026, 10, 11, 12, 0, 0);

        static ProviderSnapshot Gemini(string state, params UsageWindow[] windows)
        {
            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = "gemini";
            p.DisplayName = "Gemini";
            p.Status = new ProviderStatus();
            p.Status.State = state;
            p.Windows.AddRange(windows);
            p.FetchedAt = Now;
            return p;
        }

        static UsageWindow W(string kind, string label, double? used, DateTime? reset)
        {
            UsageWindow w = new UsageWindow();
            w.Kind = kind;
            w.Label = label;
            w.UsedPct = used;
            w.ResetsAt = reset;
            return w;
        }

        static Snapshot Snap(ProviderSnapshot p)
        {
            Snapshot s = new Snapshot();
            s.Providers.Add(p);
            return s;
        }

        public static void Test_limit_reached_and_80_percent_toasts_are_deduplicated()
        {
            AlertState state = new AlertState();
            DateTime reset = Now.AddHours(3);
            ProviderSnapshot p = Gemini(State.Ok, W(Kind.Daily, "Pro", 100, reset), W(Kind.Daily, "Flash", 85, reset));
            List<Toast> t = AlertEngine.Evaluate(Snap(p), state, true, Now);
            Assert.Equal(2, t.Count, "two toasts");
            Assert.Equal("Usage Monitor — Gemini", t[0].Title, "title");
            Assert.Equal("Pro limit reached", t[0].Body, "limit body");
            Assert.Equal("Flash 85/100% used", t[1].Body, "80% body");
            Assert.Equal(0, AlertEngine.Evaluate(Snap(p), state, true, Now).Count, "no repeat");

            ProviderSnapshot stale = Gemini(State.Stale, W(Kind.Daily, "Pro", 100, reset), W(Kind.Daily, "Other", 90, reset));
            Assert.Equal(0, AlertEngine.Evaluate(Snap(stale), state, true, Now).Count, "stale providers never alert");
        }

        public static void Test_without_notify_on_limit_100_percent_is_a_normal_80_toast()
        {
            AlertState state = new AlertState();
            ProviderSnapshot p = Gemini(State.Ok, W(Kind.FiveHour, "5h", 100, Now.AddHours(1)));
            List<Toast> t = AlertEngine.Evaluate(Snap(p), state, false, Now);
            Assert.Equal(1, t.Count, "one toast");
            Assert.Equal("5h 100/100% used", t[0].Body, "body");
            Assert.Equal(0, AlertEngine.Evaluate(Snap(p), state, false, Now).Count, "once only");
        }

        public static void Test_alert_rearms_after_usage_drops_below_80()
        {
            AlertState state = new AlertState();
            DateTime reset = Now.AddHours(2);
            ProviderSnapshot hi = Gemini(State.Ok, W(Kind.FiveHour, "5h", 85, reset));
            ProviderSnapshot lo = Gemini(State.Ok, W(Kind.FiveHour, "5h", 40, reset));
            Assert.Equal(1, AlertEngine.Evaluate(Snap(hi), state, true, Now).Count, "first crossing");
            Assert.Equal(0, AlertEngine.Evaluate(Snap(lo), state, true, Now).Count, "below threshold");
            Assert.Equal(1, AlertEngine.Evaluate(Snap(hi), state, true, Now).Count, "second crossing alerts again");
        }

        public static void Test_new_reset_period_alerts_again_and_expired_or_unknown_windows_are_ignored()
        {
            AlertState state = new AlertState();
            ProviderSnapshot a = Gemini(State.Ok, W(Kind.FiveHour, "5h", 90, Now.AddHours(2)));
            Assert.Equal(1, AlertEngine.Evaluate(Snap(a), state, true, Now).Count, "period 1");
            ProviderSnapshot b = Gemini(State.Ok, W(Kind.FiveHour, "5h", 90, Now.AddHours(7)));
            Assert.Equal(1, AlertEngine.Evaluate(Snap(b), state, true, Now).Count, "period 2 is a different key");
            ProviderSnapshot c = Gemini(State.Ok, W(Kind.FiveHour, "5h", 99, Now.AddMinutes(-1)), W(Kind.Daily, "Pro", null, Now.AddHours(1)));
            Assert.Equal(0, AlertEngine.Evaluate(Snap(c), new AlertState(), true, Now).Count, "expired / unknown ignored");
        }

        public static void Test_state_is_pruned_after_the_window_reset_time()
        {
            AlertState state = new AlertState();
            AlertEntry old = new AlertEntry();
            old.Crossed80 = true;
            old.Until = Now.AddMinutes(-1);
            state.Fired["gemini|daily|Old|0"] = old;
            AlertEntry keep = new AlertEntry();
            keep.Crossed80 = true;
            keep.Until = Now.AddHours(1);
            state.Fired["gemini|daily|Keep|0"] = keep;
            AlertEngine.Evaluate(Snap(Gemini(State.Ok)), state, true, Now);
            Assert.False(state.Fired.ContainsKey("gemini|daily|Old|0"), "expired entry removed");
            Assert.True(state.Fired.ContainsKey("gemini|daily|Keep|0"), "live entry kept");
        }

        public static void Test_state_json_roundtrip_and_tolerance()
        {
            AlertState s = new AlertState();
            AlertEntry e = new AlertEntry();
            e.Crossed80 = true;
            e.Crossed100 = false;
            e.Until = Dates.Utc(2026, 10, 12, 0, 0, 0);
            s.Fired["claude|five_hour|5h|123"] = e;
            string json = AlertEngine.SerializeState(s);
            Assert.Contains(json, "\"fired\"", "fired key");
            Assert.Contains(json, "\"crossed80\": true", "crossed80 key");
            AlertState back = AlertEngine.ParseState(json);
            Assert.True(back.Fired["claude|five_hour|5h|123"].Crossed80, "crossed80 survives");
            Assert.Equal(Dates.Utc(2026, 10, 12, 0, 0, 0), back.Fired["claude|five_hour|5h|123"].Until, "until survives");

            Assert.Equal(0, AlertEngine.ParseState("bad").Fired.Count, "garbage");
            Assert.Equal(0, AlertEngine.ParseState(null).Fired.Count, "null");
            Assert.Equal(0, AlertEngine.ParseState("{\"fired\":[]}").Fired.Count, "array fired");
            Assert.Equal(0, AlertEngine.ParseState("{\"fired\":{\"invalid\":null}}").Fired.Count, "null entry skipped");
        }
    }

    public static class StoreTests
    {
        public static void Test_snapshot_store_roundtrip_atomic_and_tolerant()
        {
            string dir = TempDir.Create();
            try
            {
                string path = Path.Combine(dir, "sub", "snapshot.json");
                FileSnapshotStore store = new FileSnapshotStore(path);
                Assert.Null(store.Load(), "missing file");
                Snapshot s = new Snapshot();
                s.GeneratedAt = Dates.Utc(2026, 10, 11, 12, 0, 0);
                s.PollIntervalSecs = 15;
                ProviderSnapshot p = new ProviderSnapshot();
                p.Id = "claude";
                p.DisplayName = "Claude Code";
                p.Status = new ProviderStatus();
                p.Status.State = State.Ok;
                p.Windows.Add(Models.WindowOf(Kind.FiveHour, "5h", 12.5, "2026-10-11T15:00:00Z"));
                p.FetchedAt = Dates.Utc(2026, 10, 11, 11, 59, 0);
                p.Source = "api";
                s.Providers.Add(p);
                store.Save(s);
                Assert.True(File.Exists(path), "file written (directory created)");
                Assert.False(File.Exists(path + ".tmp"), "no temp file left");
                Snapshot back = store.Load();
                Assert.NotNull(back, "loaded");
                Assert.Equal(12.5, back.Providers[0].Windows[0].UsedPct, "value");
                File.WriteAllText(path, "{ truncated");
                Assert.Null(store.Load(), "corrupt file -> null");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        public static void Test_alert_store_roundtrip()
        {
            string dir = TempDir.Create();
            try
            {
                string path = Path.Combine(dir, "alert-state.json");
                FileAlertStore store = new FileAlertStore(path);
                Assert.Equal(0, store.Load().Fired.Count, "missing -> empty");
                AlertState s = new AlertState();
                AlertEntry e = new AlertEntry();
                e.Crossed100 = true;
                s.Fired["k"] = e;
                store.Save(s);
                Assert.True(store.Load().Fired["k"].Crossed100, "roundtrip");
                File.WriteAllText(path, "nonsense");
                Assert.Equal(0, store.Load().Fired.Count, "corrupt -> empty");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
