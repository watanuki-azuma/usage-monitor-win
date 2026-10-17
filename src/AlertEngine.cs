// Alert engine. Port of reference/js/alerts.js + the alert-state half of reference/js/cache.js
// (milestone M4).
//
// Semantics kept from the JS:
//   * one toast per window key (id|kind|label|resetMinute) — 80% fires once, 100% fires once and
//     only when notifyOnLimit (otherwise it is a plain 80% toast);
//   * only providers whose state is "ok" alert; expired resets and unknown windows are ignored;
//   * crossing 80 again after the usage dropped re-arms the alert; every entry carries the reset
//     time it belongs to (now + 7 days when the window has no reset) and is pruned once that
//     passes, so quiet windows cannot accumulate state forever;
//   * the state file is { "fired": { "<key>": { "crossed80": …, "crossed100": …, "until": … } } }.
//
// The engine is static and pure except for mutating the AlertState it is given. All time comes from
// the nowUtc argument. Language level: C# 5 (in-box csc.exe).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UsageMonitor
{
    public static class AlertEngine
    {
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ------------------------------------------------------------------------------------------
        // evaluate(): mutates state (prune + crossed flags) and returns the toasts to show now.
        // ------------------------------------------------------------------------------------------
        public static List<Toast> Evaluate(Snapshot s, AlertState state, bool notifyOnLimit, DateTime nowUtc)
        {
            List<Toast> toasts = new List<Toast>();
            DateTime now = Models.ToUtc(nowUtc);

            // A null state cannot be mutated by the caller, but alerting must still work.
            AlertState target = state == null ? new AlertState() : state;
            if (target.Fired == null) target.Fired = new Dictionary<string, AlertEntry>();
            Prune(target, now);

            if (s == null || s.Providers == null) return toasts;

            foreach (ProviderSnapshot p in s.Providers)
            {
                if (p == null || p.Status == null) continue;
                if (p.Status.State != State.Ok) continue;    // stale/logged_out/… never alert
                if (p.Windows == null) continue;

                foreach (UsageWindow w in p.Windows)
                {
                    if (w == null || !w.UsedPct.HasValue) continue;
                    double used = w.UsedPct.Value;
                    if (double.IsNaN(used) || double.IsInfinity(used)) continue;
                    if (w.ResetsAt.HasValue && Models.ToUtc(w.ResetsAt.Value) <= now) continue;

                    string key = KeyFor(p, w);
                    AlertEntry prev;
                    if (!target.Fired.TryGetValue(key, out prev) || prev == null) prev = new AlertEntry();

                    if (used >= Models.AlertUsedPct && !prev.Crossed80 && !(notifyOnLimit && used >= 100))
                    {
                        toasts.Add(Toast(p, w, PercentText(used) + "/100% used"));
                        prev.Crossed80 = true;
                        prev.Until = w.ResetsAt;
                    }
                    if (notifyOnLimit && used >= 100 && !prev.Crossed100)
                    {
                        toasts.Add(Toast(p, w, "limit reached"));
                        prev.Crossed100 = true;
                        prev.Crossed80 = true;
                        prev.Until = w.ResetsAt;
                    }
                    if (used < Models.AlertUsedPct)
                    {
                        prev.Crossed80 = false;
                        prev.Crossed100 = false;
                    }
                    // Every tracked entry gets a deadline, so a window that never alerted is
                    // pruned as well (an unknown reset is presumed to recycle within a week).
                    prev.Until = w.ResetsAt.HasValue ? w.ResetsAt : now.AddDays(7);
                    target.Fired[key] = prev;
                }
            }
            return toasts;
        }

        private static Toast Toast(ProviderSnapshot p, UsageWindow w, string body)
        {
            Toast t = new Toast();
            t.Title = "Usage Monitor \u2014 " + (p.DisplayName == null ? "" : p.DisplayName);
            t.Body = (w.Label == null ? "" : w.Label) + " " + body;
            return t;
        }

        // JavaScript Math.round: halves go away from zero.
        private static string PercentText(double used)
        {
            long rounded = (long)Math.Round(used, MidpointRounding.AwayFromZero);
            return rounded.ToString(CultureInfo.InvariantCulture);
        }

        // keyFor(): "<id>|<kind>|<label>|<resetMinute>" — the reset minute makes every period new.
        private static string KeyFor(ProviderSnapshot p, UsageWindow w)
        {
            string resets = "0";
            if (w.ResetsAt.HasValue)
            {
                resets = ResetMinute(w.ResetsAt.Value).ToString(CultureInfo.InvariantCulture);
            }
            string label = w.Label == null ? "" : w.Label;
            return (p.Id == null ? "" : p.Id) + "|" + (w.Kind == null ? "" : w.Kind) + "|" + label + "|" + resets;
        }

        // Minutes since the Unix epoch, floored like JavaScript's Math.floor(t / 60000).
        private static long ResetMinute(DateTime resetsAtUtc)
        {
            double minutes = (Models.ToUtc(resetsAtUtc) - UnixEpoch).TotalMinutes;
            return (long)Math.Floor(minutes);
        }

        // prune(): drop entries whose deadline (every entry has one) has passed.
        private static void Prune(AlertState state, DateTime now)
        {
            if (state.Fired == null || state.Fired.Count == 0) return;
            List<string> expired = null;
            foreach (KeyValuePair<string, AlertEntry> kv in state.Fired)
            {
                AlertEntry e = kv.Value;
                if (e == null || (e.Until.HasValue && Models.ToUtc(e.Until.Value) <= now))
                {
                    if (expired == null) expired = new List<string>();
                    expired.Add(kv.Key);
                }
            }
            if (expired == null) return;
            foreach (string key in expired) state.Fired.Remove(key);
        }

        // ------------------------------------------------------------------------------------------
        // alert-state.json — tolerant like cache.js loadAlertState(): never null, garbage ignored.
        // ------------------------------------------------------------------------------------------
        public static AlertState ParseState(string json)
        {
            AlertState state = new AlertState();
            System.Collections.Generic.IDictionary<string, object> root = JsonUtil.Map(JsonUtil.Parse(json));
            if (root == null) return state;

            System.Collections.Generic.IDictionary<string, object> fired = JsonUtil.Map(JsonUtil.Get(root, "fired"));
            if (fired == null) return state;

            foreach (KeyValuePair<string, object> kv in fired)
            {
                System.Collections.Generic.IDictionary<string, object> eo = JsonUtil.Map(kv.Value);
                if (eo == null) continue;    // null / array / scalar entries are skipped

                AlertEntry e = new AlertEntry();
                e.Crossed80 = JsonUtil.Bool(JsonUtil.Get(eo, "crossed80")) ?? false;
                e.Crossed100 = JsonUtil.Bool(JsonUtil.Get(eo, "crossed100")) ?? false;
                e.Until = Models.ParseIso(JsonUtil.Str(eo, "until", null));
                state.Fired[kv.Key] = e;
            }
            return state;
        }

        public static string SerializeState(AlertState state)
        {
            JsonWriter w = new JsonWriter();
            w.BeginRootObject();
            w.Key("fired");
            w.BeginObject();
            if (state != null && state.Fired != null)
            {
                foreach (KeyValuePair<string, AlertEntry> kv in state.Fired)
                {
                    AlertEntry e = kv.Value;
                    if (e == null) continue;
                    w.Key(kv.Key);
                    w.BeginObject();
                    w.Key("crossed80"); w.Bool(e.Crossed80);
                    w.Key("crossed100"); w.Bool(e.Crossed100);
                    // undefined in JavaScript => the key is left out of the file entirely.
                    if (e.Until.HasValue) { w.Key("until"); w.String(Models.ToIso(e.Until.Value)); }
                    w.EndObject();
                }
            }
            w.EndObject();
            w.EndObject();
            return w.ToString();
        }
    }
}
