// Poll engine. Port of reference/js/poller.js (milestone M4).
//
// Semantics kept from the JS:
//   * every adapter runs in parallel (on the thread pool) behind a per-adapter timeout; a timed-out
//     operation is *retained* and consumed by a later poll, so the same adapter is never started
//     twice for one tick of work (token refreshes must never overlap);
//   * concurrent PollOnce calls coalesce on one in-flight collection and all get the same Snapshot;
//   * per-provider backoff 2/4/8/16/30/30… minutes on RateLimited, reported as status reason
//     "rate_limited" plus retry_at;
//   * claude is polled at most every 5 minutes (Anthropic answers 429 to 1-minute polling);
//   * fetch_failed/stale results merge with the cached provider (stale + cached windows + age_secs);
//   * ApplyLocalResets runs over the collected result and the snapshot is saved after every poll;
//     a failing store never loses the fresh data.
//
// All time comes from the injected utcNow. No static mutable state: two PollEngine instances (and
// their backoff/pending tables) are fully independent. Nothing throws out of PollOnce.
// Language level: C# 5 (in-box csc.exe). No ?. , no $"" , no expression-bodied members, no out var,
// no auto-property initializers, no pattern matching, no tuples.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace UsageMonitor
{
    public class PollEngine
    {
        // Anthropic's OAuth usage endpoint answers 429 when polled every minute (poller.js MIN_INTERVAL_MS).
        private const string ClaudeId = "claude";
        private const long ClaudeMinGapMs = 5 * 60 * 1000;
        private const long MaxBackoffTicks = 30 * 60 * TimeSpan.TicksPerSecond;
        private const string RateLimitedReason = "rate_limited";

        private readonly IList<IUsageAdapter> adapters;   // Models.ProviderIds order first, then extras
        private readonly ISnapshotStore store;
        private readonly Func<DateTime> utcNow;
        private readonly int adapterTimeoutMs;

        private readonly object gate = new object();
        // Operations started by an earlier poll that have not finished yet (timeout retention).
        private readonly Dictionary<string, Task<ProviderSnapshot>> pending = new Dictionary<string, Task<ProviderSnapshot>>();
        private readonly Dictionary<string, DateTime> backoffUntil = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> rateLimitHits = new Dictionary<string, int>();

        // The latest snapshot this engine produced. The store is the durable copy; this one keeps
        // the throttling and the cache merge working while the file cannot be read or written.
        private Snapshot lastSnapshot;

        // The collection currently in flight; concurrent callers wait on it instead of starting their own.
        private PollOperation active;

        public PollEngine(IList<IUsageAdapter> adapters, ISnapshotStore store, Func<DateTime> utcNow, int adapterTimeoutMs)
        {
            this.store = store;
            this.utcNow = utcNow;
            this.adapterTimeoutMs = adapterTimeoutMs < 0 ? 0 : adapterTimeoutMs;
            this.adapters = Ordered(adapters);
        }

        // ------------------------------------------------------------------------------------------
        // Blocking: runs one full collection and returns the snapshot. Concurrent calls join the
        // in-flight collection and receive the very same Snapshot instance.
        // ------------------------------------------------------------------------------------------
        public Snapshot PollOnce(AdapterContext ctx, int pollIntervalSecs)
        {
            PollOperation op = null;
            bool leader = false;
            lock (gate)
            {
                if (active != null) op = active;
                else { op = new PollOperation(); active = op; leader = true; }
            }

            if (leader)
            {
                Snapshot snapshot = null;
                try { snapshot = Collect(ctx, pollIntervalSecs); }
                catch (Exception) { snapshot = null; }   // Collect swallows store errors already
                op.Run(snapshot);
                lock (gate) { if (object.ReferenceEquals(active, op)) active = null; }
            }
            return op.Result();
        }

        // ------------------------------------------------------------------------------------------
        // mergeWithCache(): a failed/stale fresh result is only masked by the cache when the cache
        // actually holds windows; the cached timestamp and windows are shown, the state becomes
        // stale, the source becomes "cache" and the age of the cached data is reported.
        // ------------------------------------------------------------------------------------------
        public static ProviderSnapshot MergeWithCache(ProviderSnapshot fresh, ProviderSnapshot cached, DateTime nowUtc)
        {
            if (cached == null) return fresh;
            if (fresh == null) return cached;

            ProviderStatus fs = fresh.Status;
            string state = fs == null ? null : fs.State;
            if (state == State.Ok) return fresh;

            if ((state == State.FetchFailed || state == State.Stale) &&
                cached.Windows != null && cached.Windows.Count > 0)
            {
                ProviderSnapshot merged = Models.Clone(cached);
                ProviderStatus st = new ProviderStatus();
                st.State = State.Stale;
                st.AgeSecs = AgeSeconds(Models.ToUtc(nowUtc), cached.FetchedAt);
                merged.Status = st;
                merged.Source = "cache";
                return merged;
            }
            return fresh;
        }

        // ------------------------------------------------------------------------------------------
        // One full collection: adapters in parallel, then the snapshot is reset locally and saved.
        // ------------------------------------------------------------------------------------------
        private Snapshot Collect(AdapterContext ctx, int pollIntervalSecs)
        {
            DateTime now = Models.ToUtc(utcNow());

            Snapshot previous = null;
            try { if (store != null) previous = store.Load(); }
            catch (Exception) { previous = null; }
            // The store is the source of truth, but when it has nothing to give (or refuses to be
            // read or written) the last snapshot we produced is the previous one.
            if (previous == null)
            {
                lock (gate) { previous = lastSnapshot; }
            }

            Dictionary<string, ProviderSnapshot> prevById = new Dictionary<string, ProviderSnapshot>();
            if (previous != null && previous.Providers != null)
            {
                foreach (ProviderSnapshot p in previous.Providers)
                {
                    if (p == null || p.Id == null) continue;
                    if (!prevById.ContainsKey(p.Id)) prevById.Add(p.Id, p);
                }
            }

            int count = adapters.Count;
            Task<ProviderSnapshot>[] tasks = new Task<ProviderSnapshot>[count];
            for (int i = 0; i < count; i++)
            {
                int index = i;
                IUsageAdapter adapter = adapters[i];
                tasks[i] = Task.Factory.StartNew<ProviderSnapshot>(delegate()
                {
                    return CollectProvider(adapter, ctx, prevById, now);
                });
            }

            // Every provider task blocks at most one adapter timeout, so this returns even while a
            // timed-out adapter is still running in the background.
            try { Task.WaitAll(tasks); }
            catch (Exception) { /* handled per task below */ }

            ProviderSnapshot[] results = new ProviderSnapshot[count];
            for (int i = 0; i < count; i++)
            {
                results[i] = ResultOf(tasks[i], SafeId(adapters[i]), SafeName(adapters[i]), now);
            }

            Snapshot snap = new Snapshot();
            snap.GeneratedAt = now;
            snap.PollIntervalSecs = pollIntervalSecs;
            List<ProviderSnapshot> providers = new List<ProviderSnapshot>(count);
            for (int i = 0; i < count; i++) if (results[i] != null) providers.Add(results[i]);
            snap.Providers = providers;

            bool changed;
            Snapshot next = Models.ApplyLocalResets(snap, now, out changed);

            lock (gate) { lastSnapshot = next; }

            try { if (store != null) store.Save(next); }
            catch (Exception) { /* a failed write must never lose the fresh data */ }

            return next;
        }

        // ------------------------------------------------------------------------------------------
        // Per-provider part of collect(): backoff, claude min-gap, pending retention + timeout,
        // rate-limit bookkeeping and the cache merge. Runs on the thread pool, in parallel.
        // ------------------------------------------------------------------------------------------
        private ProviderSnapshot CollectProvider(IUsageAdapter adapter, AdapterContext ctx,
                                                 Dictionary<string, ProviderSnapshot> prevById, DateTime now)
        {
            string id = SafeId(adapter);
            string name = SafeName(adapter);
            try
            {
                ProviderSnapshot cached = null;
                prevById.TryGetValue(id, out cached);

                Task<ProviderSnapshot> task = null;
                lock (gate)
                {
                    DateTime backoffEnd;
                    bool backing = backoffUntil.TryGetValue(id, out backoffEnd) && now < backoffEnd;
                    if (backing)
                    {
                        if (cached != null && cached.Windows != null && cached.Windows.Count > 0)
                            return StaleFromCache(cached, now, id);
                        if (cached != null) return cached;
                        // no cache at all -> fall through and try the adapter anyway (like poller.js)
                    }

                    // A cached FetchedAt in the future means the clock moved back, not that the
                    // reading is fresh: a negative age is "not recent" and must not suppress the
                    // poll.
                    double ageMs = cached == null ? 0 : (now - Models.ToUtc(cached.FetchedAt)).TotalMilliseconds;
                    if (id == ClaudeId && cached != null && cached.Status != null &&
                        (cached.Status.State == State.Ok || cached.Status.State == State.LoggedOut) &&
                        ageMs >= 0 && ageMs < ClaudeMinGapMs)
                    {
                        return cached;
                    }

                    if (!pending.TryGetValue(id, out task))
                    {
                        task = Task.Factory.StartNew<ProviderSnapshot>(delegate() { return SafeFetch(adapter, ctx, now); });
                        pending[id] = task;
                    }
                }

                bool finished = task.Wait(adapterTimeoutMs);
                ProviderSnapshot fresh;
                if (!finished)
                {
                    // Timed out: the operation stays pending so a later poll consumes the late result.
                    fresh = FailedProvider(id, name, id + " timeout", now);
                }
                else
                {
                    ProviderSnapshot fetched = ResultOf(task, id, name, now);
                    lock (gate)
                    {
                        Task<ProviderSnapshot> still;
                        if (pending.TryGetValue(id, out still) && object.ReferenceEquals(still, task)) pending.Remove(id);
                    }

                    if (fetched.RateLimited)
                    {
                        // 2, 4, 8, 16, then 30 minutes while the provider keeps rate limiting.
                        // The exponent is capped so a very long run of rate limits cannot overflow
                        // the tick count into a backoff somewhere in the past.
                        lock (gate)
                        {
                            int hits;
                            rateLimitHits.TryGetValue(id, out hits);
                            hits++;
                            rateLimitHits[id] = hits;
                            int exponent = hits < 5 ? hits : 5;
                            double wanted = Math.Pow(2, exponent) * 60.0 * TimeSpan.TicksPerSecond;
                            long ticks = wanted > MaxBackoffTicks ? MaxBackoffTicks : (long)wanted;
                            backoffUntil[id] = now.AddTicks(ticks);
                        }
                    }
                    else if (fetched.Status != null && fetched.Status.State == State.Ok)
                    {
                        lock (gate)
                        {
                            backoffUntil.Remove(id);
                            rateLimitHits.Remove(id);
                        }
                    }
                    fresh = fetched;
                }

                ProviderSnapshot merged = MergeWithCache(fresh, cached, now);

                bool hasHits = false, hasUntil = false;
                DateTime backoffNow = now;
                lock (gate)
                {
                    int hitsNow;
                    hasHits = rateLimitHits.TryGetValue(id, out hitsNow);
                    hasUntil = backoffUntil.TryGetValue(id, out backoffNow);
                }
                if (hasHits && merged != null && merged.Status != null && merged.Status.State == State.Stale)
                {
                    ProviderStatus st = Models.CloneStatus(merged.Status);
                    st.Reason = RateLimitedReason;
                    st.RetryAt = hasUntil ? backoffNow : now;
                    merged.Status = st;
                }
                return merged;
            }
            catch (Exception)
            {
                return FailedProvider(id, name, "poll failed", now);
            }
        }

        // Backoff branch: show the cached provider as stale, with the rate-limit reason attached.
        private ProviderSnapshot StaleFromCache(ProviderSnapshot cached, DateTime now, string id)
        {
            ProviderSnapshot merged = Models.Clone(cached);
            ProviderStatus st = new ProviderStatus();
            st.State = State.Stale;
            st.AgeSecs = AgeSeconds(now, cached.FetchedAt);

            bool hasHits = false, hasUntil = false;
            DateTime backoffNow = now;
            lock (gate)
            {
                int hits;
                hasHits = rateLimitHits.TryGetValue(id, out hits);
                hasUntil = backoffUntil.TryGetValue(id, out backoffNow);
            }
            if (hasHits)
            {
                st.Reason = RateLimitedReason;
                st.RetryAt = hasUntil ? backoffNow : now;
            }
            merged.Status = st;
            merged.Source = "cache";
            return merged;
        }

        // A failing adapter becomes a fetch_failed provider for that provider only.
        private static ProviderSnapshot SafeFetch(IUsageAdapter adapter, AdapterContext ctx, DateTime now)
        {
            try { return adapter.FetchUsage(ctx); }
            catch (Exception ex) { return FailedProvider(SafeId(adapter), SafeName(adapter), MessageOf(ex), now); }
        }

        private static ProviderSnapshot ResultOf(Task<ProviderSnapshot> task, string id, string name, DateTime now)
        {
            try
            {
                ProviderSnapshot r = task.Result;
                return r == null ? FailedProvider(id, name, "adapter produced no snapshot", now) : r;
            }
            catch (Exception ex)
            {
                Exception inner = Flatten(ex);
                return FailedProvider(id, name, MessageOf(inner), now);
            }
        }

        private static ProviderSnapshot FailedProvider(string id, string displayName, string message, DateTime now)
        {
            ProviderStatus st = new ProviderStatus();
            st.State = State.FetchFailed;
            st.Message = message;
            return Models.EmptyProvider(id, displayName, st, now);
        }

        private static Exception Flatten(Exception ex)
        {
            while (ex is AggregateException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        private static string MessageOf(Exception ex)
        {
            if (ex == null) return "adapter failed";
            string m = ex.Message;
            return string.IsNullOrEmpty(m) ? ex.GetType().Name : m;
        }

        private static int AgeSeconds(DateTime nowUtc, DateTime fetchedAtUtc)
        {
            double age = (Models.ToUtc(nowUtc) - Models.ToUtc(fetchedAtUtc)).TotalSeconds;
            if (age <= 0) return 0;
            if (age > int.MaxValue) return int.MaxValue;
            return (int)Math.Round(age, MidpointRounding.AwayFromZero);
        }

        private static string SafeId(IUsageAdapter a)
        {
            return a == null || a.Id == null ? "" : a.Id;
        }

        private static string SafeName(IUsageAdapter a)
        {
            if (a == null) return "";
            return a.DisplayName == null ? SafeId(a) : a.DisplayName;
        }

        // Provider order: Models.ProviderIds first (claude, codex, gemini, grok), then anything else
        // the caller supplied, in the given order. Duplicates are ignored.
        private static List<IUsageAdapter> Ordered(IList<IUsageAdapter> adapters)
        {
            List<IUsageAdapter> list = new List<IUsageAdapter>();
            if (adapters == null) return list;

            foreach (string id in Models.ProviderIds)
            {
                foreach (IUsageAdapter a in adapters)
                {
                    if (a == null || a.Id == null) continue;
                    if (a.Id == id) { list.Add(a); break; }
                }
            }
            foreach (IUsageAdapter a in adapters)
            {
                if (a == null || a.Id == null) continue;
                if (Array.IndexOf(Models.ProviderIds, a.Id) >= 0) continue;
                bool seen = false;
                foreach (IUsageAdapter b in list)
                {
                    if (object.ReferenceEquals(b, a)) { seen = true; break; }
                }
                if (!seen) list.Add(a);
            }
            return list;
        }

        // One in-flight collection. The leader fills it; every concurrent caller waits and gets the
        // identical snapshot back (coalescing).
        private sealed class PollOperation
        {
            private readonly ManualResetEventSlim done = new ManualResetEventSlim(false);
            private Snapshot result;

            public void Run(Snapshot snapshot)
            {
                result = snapshot;
                done.Set();
            }

            public Snapshot Result()
            {
                done.Wait();
                return result;
            }
        }
    }
}
