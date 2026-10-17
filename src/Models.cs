// Core domain helpers. Port of reference/js/models.js (milestone M1).
// Language level: C# 5 (in-box csc.exe). No ?. , no $"" , no expression-bodied members, no out var,
// no pattern matching, no tuples.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UsageMonitor
{
    public static class Models
    {
        public const int AlertUsedPct = 80;

        public static readonly string[] ProviderIds = new string[] { "claude", "codex", "gemini", "grok" };

        // ------------------------------------------------------------------------------------------
        // percentage() — accepts the CLR numeric types plus numeric strings (JSON bodies reach us as
        // int / long / decimal / double, so every one of them has to be handled). Rejects null, bool,
        // objects, blank and non-numeric strings, NaN and +/-Infinity. Negatives clamp to 0; values
        // above 100 are preserved on purpose (overage is meaningful).
        // ------------------------------------------------------------------------------------------
        public static double? Percentage(object value)
        {
            if (value == null || value is bool) return null;

            double n;
            string text = value as string;
            if (text != null) return PercentageOfString(text);
            if (value is double) n = (double)value;
            else if (value is float) n = (double)(float)value;
            else if (value is decimal) n = (double)(decimal)value;
            else if (value is int) n = (double)(int)value;
            else if (value is long) n = (double)(long)value;
            else if (value is short) n = (double)(short)value;
            else if (value is byte) n = (double)(byte)value;
            else if (value is sbyte) n = (double)(sbyte)value;
            else if (value is ushort) n = (double)(ushort)value;
            else if (value is uint) n = (double)(uint)value;
            else if (value is ulong) n = (double)(ulong)value;
            else return null;

            if (double.IsNaN(n) || double.IsInfinity(n)) return null;
            if (n < 0) n = 0;
            return n;
        }

        private static double? PercentageOfString(string text)
        {
            if (text == null || text.Trim().Length == 0) return null;
            double n;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return null;
            if (double.IsNaN(n) || double.IsInfinity(n)) return null;
            if (n < 0) n = 0;
            return n;
        }

        public static double? RemainingPct(object used)
        {
            double? value = Percentage(used);
            if (!value.HasValue) return null;
            double left = 100 - value.Value;
            return left < 0 ? 0d : left;
        }

        // ------------------------------------------------------------------------------------------
        // windowOf() — normalises a raw provider window. Remaining is derived, never trusted.
        // ------------------------------------------------------------------------------------------
        public static UsageWindow WindowOf(string kind, string label, object usedPct, string resetsAt)
        {
            UsageWindow w = new UsageWindow();
            w.Kind = kind;
            w.Label = label;
            double? used = Percentage(usedPct);
            w.UsedPct = used;
            w.RemainingPct = used.HasValue ? RemainingPct(used) : null;
            w.ResetsAt = ParseIso(resetsAt);
            return w;
        }

        public static ProviderSnapshot EmptyProvider(string id, string displayName, ProviderStatus status, DateTime nowUtc)
        {
            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = id;
            p.DisplayName = displayName;
            p.Status = status;
            p.Plan = null;
            p.FetchedAt = ToUtc(nowUtc);
            p.Source = "none";
            return p;
        }

        // ------------------------------------------------------------------------------------------
        // currentWindow() — the window the taskbar chip shows. five_hour wins, then daily, then
        // anything; within the preferred group the highest usage wins (JS reduce keeps the first of
        // a tie, and the caller cares about object identity).
        // ------------------------------------------------------------------------------------------
        public static UsageWindow CurrentWindow(ProviderSnapshot p)
        {
            if (p == null || p.Windows == null) return null;

            UsageWindow best = null;
            int bestRank = 0;
            foreach (UsageWindow w in p.Windows)
            {
                if (w == null || !w.UsedPct.HasValue) continue;
                int rank = WindowRank(w.Kind);
                if (best == null || rank < bestRank)
                {
                    best = w;
                    bestRank = rank;
                    continue;
                }
                if (rank == bestRank && w.UsedPct.Value > best.UsedPct.Value) best = w;
            }
            return best;
        }

        private static int WindowRank(string kind)
        {
            if (kind == Kind.FiveHour) return 1;
            if (kind == Kind.Daily) return 2;
            return 3;
        }

        public static bool IsAlerting(UsageWindow w)
        {
            return w != null && w.UsedPct.HasValue && w.UsedPct.Value >= AlertUsedPct;
        }

        // ------------------------------------------------------------------------------------------
        // applyLocalResets() — the 1s UI tick. Never mutates the input (callers keep the cached
        // snapshot around), so every provider, status, window and footnote list is copied.
        // ------------------------------------------------------------------------------------------
        public static Snapshot ApplyLocalResets(Snapshot s, DateTime nowUtc, out bool changed)
        {
            changed = false;
            if (s == null || s.Providers == null) return s;

            DateTime now = ToUtc(nowUtc);
            List<ProviderSnapshot> providers = new List<ProviderSnapshot>();
            foreach (ProviderSnapshot p in s.Providers)
            {
                bool expired = false;
                List<UsageWindow> windows = new List<UsageWindow>();
                if (p != null && p.Windows != null)
                {
                    foreach (UsageWindow w in p.Windows)
                    {
                        if (w == null) continue;
                        if (!w.ResetsAt.HasValue || w.ResetsAt.Value > now)
                        {
                            windows.Add(w.Clone());
                            continue;
                        }
                        expired = true;
                        UsageWindow copy = w.Clone();
                        copy.UsedPct = null;
                        copy.RemainingPct = null;
                        copy.ResetsAt = null;
                        windows.Add(copy);
                    }
                }

                ProviderSnapshot clone = Clone(p);
                clone.Windows = windows;
                if (expired)
                {
                    ProviderStatus status = clone.Status == null ? new ProviderStatus() : CloneStatus(clone.Status);
                    status.State = State.Stale;
                    clone.Status = status;
                    changed = true;
                }
                providers.Add(clone);
            }

            Snapshot result = new Snapshot();
            result.GeneratedAt = s.GeneratedAt;
            result.PollIntervalSecs = s.PollIntervalSecs;
            result.Providers = providers;
            return result;
        }

        public static ProviderSnapshot Clone(ProviderSnapshot p)
        {
            ProviderSnapshot copy = new ProviderSnapshot();
            if (p == null) return copy;
            copy.Id = p.Id;
            copy.DisplayName = p.DisplayName;
            copy.Status = p.Status == null ? null : CloneStatus(p.Status);
            copy.Plan = p.Plan;
            copy.FetchedAt = p.FetchedAt;
            copy.Source = p.Source;
            copy.RateLimited = p.RateLimited;
            if (p.Windows != null)
            {
                foreach (UsageWindow w in p.Windows) copy.Windows.Add(w == null ? null : w.Clone());
            }
            if (p.Footnotes != null) copy.Footnotes.AddRange(p.Footnotes);
            return copy;
        }

        public static ProviderStatus CloneStatus(ProviderStatus s)
        {
            ProviderStatus copy = new ProviderStatus();
            if (s == null) return copy;
            copy.State = s.State;
            copy.Hint = s.Hint;
            copy.Message = s.Message;
            copy.Reason = s.Reason;
            copy.RetryAt = s.RetryAt;
            copy.AgeSecs = s.AgeSecs;
            return copy;
        }

        // ------------------------------------------------------------------------------------------
        // ISO-8601 parsing. JavaScriptSerializer hands dates back as strings, so every time value
        // in a snapshot goes through here. Accepts "YYYY-MM-DD[T| ]HH:MM[:SS[.f...]][Z|+HH[:MM]|-HH:MM]"
        // and returns a UTC DateTime; anything else is null (like a failed Date.parse).
        // ------------------------------------------------------------------------------------------
        public static DateTime? ParseIso(string text)
        {
            if (text == null) return null;
            string s = text.Trim();
            if (s.Length < 10) return null;

            int year, month, day, hour = 0, minute = 0, second = 0, fracTicks = 0, offsetMinutes = 0;
            if (!Digits(s, 0, 4, out year)) return null;
            if (s[4] != '-' || !Digits(s, 5, 2, out month)) return null;
            if (s[7] != '-' || !Digits(s, 8, 2, out day)) return null;

            int pos = 10;
            if (pos < s.Length)
            {
                char sep = s[pos];
                if (sep != 'T' && sep != 't' && sep != ' ') return null;
                pos++;
                if (!Digits(s, pos, 2, out hour)) return null;
                pos += 2;
                if (pos < s.Length && s[pos] != ':') return null;
                pos++;
                if (!Digits(s, pos, 2, out minute)) return null;
                pos += 2;
                if (pos < s.Length && s[pos] == ':')
                {
                    pos++;
                    if (!Digits(s, pos, 2, out second)) return null;
                    pos += 2;
                }
                if (pos < s.Length && s[pos] == '.')
                {
                    pos++;
                    int digits = 0;
                    long value = 0;
                    while (pos < s.Length)
                    {
                        char c = s[pos];
                        if (c < '0' || c > '9') break;
                        if (digits < 7)
                        {
                            value = value * 10 + (c - '0');
                            digits++;
                        }
                        pos++;
                    }
                    if (digits == 0) return null;
                    while (digits < 7)
                    {
                        value *= 10;
                        digits++;
                    }
                    fracTicks = (int)value;
                }
                if (pos < s.Length)
                {
                    char c = s[pos];
                    pos++;
                    int sign = 1;
                    if (c == 'Z' || c == 'z') sign = 0;
                    else if (c == '+' || c == '-') { if (c == '-') sign = -1; }
                    else return null;

                    if (sign != 0)
                    {
                        int oh, om = 0;
                        if (!Digits(s, pos, 2, out oh)) return null;
                        pos += 2;
                        if (pos < s.Length && s[pos] == ':')
                        {
                            pos++;
                            if (pos + 2 > s.Length || !Digits(s, pos, 2, out om)) return null;
                            pos += 2;
                        }
                        offsetMinutes = sign * (oh * 60 + om);
                    }
                }
                if (pos != s.Length) return null;
            }

            try
            {
                DateTime utc = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
                return utc.AddMinutes(-offsetMinutes).AddTicks(fracTicks);
            }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static bool Digits(string s, int start, int count, out int value)
        {
            value = 0;
            if (start < 0 || start + count > s.Length) return false;
            for (int i = 0; i < count; i++)
            {
                char c = s[start + i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }

        // ISO-8601 with milliseconds and a trailing Z, exactly like JavaScript's toISOString().
        public static string ToIso(DateTime value)
        {
            DateTime utc = ToUtc(value);
            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";
        }

        public static DateTime ToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
