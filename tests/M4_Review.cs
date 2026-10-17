using System;
using System.Collections.Generic;

namespace UsageMonitor.Tests
{
    // Regression tests for defects found in the independent review of M4.
    public static class EngineReviewTests
    {
        static ProviderSnapshot Ok(string id, double used, DateTime now)
        {
            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = id;
            p.DisplayName = id;
            p.Status = new ProviderStatus();
            p.Status.State = State.Ok;
            p.Windows.Add(Models.WindowOf(Kind.Daily, "Pro", used, now.AddHours(3).ToString("o")));
            p.FetchedAt = now;
            p.Source = "api";
            return p;
        }

        static ProviderSnapshot Limited(string id, DateTime now)
        {
            ProviderStatus st = new ProviderStatus();
            st.State = State.FetchFailed;
            st.Message = "rate limited";
            ProviderSnapshot p = Models.EmptyProvider(id, id, st, now);
            p.RateLimited = true;
            return p;
        }

        static void Build(out PollEngine engine, out List<FakeAdapter> adapters, out Clock clock, out MemoryStore store, out AdapterContext ctx)
        {
            Clock c = new Clock();
            clock = c;
            store = new MemoryStore();
            adapters = new List<FakeAdapter>();
            List<IUsageAdapter> list = new List<IUsageAdapter>();
            foreach (string id in Models.ProviderIds)
            {
                FakeAdapter a = new FakeAdapter(id);
                string captured = id;
                a.Fetch = delegate(AdapterContext x) { return Ok(captured, 50, c.Now); };
                adapters.Add(a);
                list.Add(a);
            }
            engine = new PollEngine(list, store, c.Get, 5000);
            ctx = new AdapterContext();
            ctx.Config = AppConfig.Default();
            ctx.UtcNow = c.Get;
        }

        public static void Test_backoff_stays_capped_after_dozens_of_consecutive_rate_limits()
        {
            PollEngine engine; List<FakeAdapter> adapters; Clock clock; MemoryStore store; AdapterContext ctx;
            Build(out engine, out adapters, out clock, out store, out ctx);
            FakeAdapter codex = adapters[1];
            codex.Fetch = delegate(AdapterContext x) { return Limited("codex", clock.Now); };
            for (int i = 1; i <= 45; i++)
            {
                engine.PollOnce(ctx, 5);
                Assert.Equal(i, codex.Calls, "poll " + i + " reaches the adapter");
                clock.Now = clock.Now.AddSeconds(1);
                engine.PollOnce(ctx, 5);
                Assert.Equal(i, codex.Calls, "poll " + i + " + 1s is still inside the backoff");
                clock.Now = clock.Now.AddMinutes(31);
            }
        }

        public static void Test_quiet_windows_do_not_accumulate_alert_state_forever()
        {
            AlertState state = new AlertState();
            DateTime t0 = Dates.Utc(2026, 10, 11, 12, 0, 0);
            for (int period = 0; period < 20; period++)
            {
                DateTime now = t0.AddHours(period * 6);
                ProviderSnapshot p = new ProviderSnapshot();
                p.Id = "claude";
                p.DisplayName = "Claude Code";
                p.Status = new ProviderStatus();
                p.Status.State = State.Ok;
                UsageWindow w = new UsageWindow();
                w.Kind = Kind.FiveHour;
                w.Label = "5h";
                w.UsedPct = 10;
                w.ResetsAt = now.AddHours(5);
                p.Windows.Add(w);
                Snapshot s = new Snapshot();
                s.Providers.Add(p);
                AlertEngine.Evaluate(s, state, true, now);
            }
            Assert.True(state.Fired.Count <= 2, "old reset periods are pruned (entries: " + state.Fired.Count + ")");
        }

        public static void Test_throttling_survives_a_failing_snapshot_store()
        {
            PollEngine engine; List<FakeAdapter> adapters; Clock clock; MemoryStore store; AdapterContext ctx;
            Build(out engine, out adapters, out clock, out store, out ctx);
            store.ThrowOnSave = true;
            engine.PollOnce(ctx, 5);
            clock.Now = clock.Now.AddSeconds(60);
            engine.PollOnce(ctx, 5);
            Assert.Equal(1, adapters[0].Calls, "claude 5-minute gap is kept from memory when the file cannot be written");
        }

        public static void Test_a_cache_dated_in_the_future_does_not_suppress_polling()
        {
            PollEngine engine; List<FakeAdapter> adapters; Clock clock; MemoryStore store; AdapterContext ctx;
            Build(out engine, out adapters, out clock, out store, out ctx);
            engine.PollOnce(ctx, 5);
            clock.Now = clock.Now.AddHours(-1);
            engine.PollOnce(ctx, 5);
            Assert.Equal(2, adapters[0].Calls, "after the clock moved back the cached reading is not trusted");
        }
    }
}
