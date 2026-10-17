// Presentation strings. Port of reference/js/ui/format.js (milestone M1).
// Language level: C# 5 (in-box csc.exe).
using System;
using System.Globalization;

namespace UsageMonitor
{
    public static class Format
    {
        private static readonly string[] DayNames = new string[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        private const string EmDash = "\u2014";

        // ------------------------------------------------------------------------------------------
        // Relative while it is nearer than a day ("45m", "1h 05m"), then an absolute clock time in
        // the local zone ("Wed 3:05 PM"). Minutes round half up exactly like JavaScript's Math.round.
        // ------------------------------------------------------------------------------------------
        public static string FormatEta(DateTime? resetsAtUtc, DateTime nowUtc, TimeZoneInfo tz)
        {
            if (!resetsAtUtc.HasValue) return "";

            TimeSpan left = resetsAtUtc.Value - nowUtc;
            if (left <= TimeSpan.Zero) return "now";

            double minutes = Math.Round(left.TotalMinutes, MidpointRounding.AwayFromZero);
            if (minutes < 24 * 60)
            {
                int total = (int)minutes;
                int h = total / 60;
                int m = total % 60;
                if (h > 0) return h + "h " + Pad(m) + "m";
                return m + "m";
            }

            DateTime local = resetsAtUtc.Value;
            if (local.Kind != DateTimeKind.Utc) local = Models.ToUtc(local);
            local = TimeZoneInfo.ConvertTime(local, tz);
            int hours = local.Hour;
            string ampm = hours >= 12 ? "PM" : "AM";
            hours = hours % 12;
            if (hours == 0) hours = 12;
            return DayNames[(int)local.DayOfWeek] + " " + hours + ":" + Pad(local.Minute) + " " + ampm;
        }

        public static string FormatUsedTotal(UsageWindow w, bool compact)
        {
            if (w == null || !w.UsedPct.HasValue) return compact ? EmDash : EmDash + "/100%";
            int used = (int)Math.Round(w.UsedPct.Value, MidpointRounding.AwayFromZero);
            return used + (compact ? "/100" : "/100%");
        }

        // A label that the taskbar row can show without the adult "Weekly" wording.
        public static string FlyoutLabel(UsageWindow w)
        {
            if (w == null) return null;
            string label = w.Label;
            bool custom = !string.IsNullOrEmpty(label);
            if (custom && label != "5h" && label != "Weekly" && label != "Daily" && label != "Credits") return label;
            if (w.Kind == Kind.FiveHour) return "5h";
            if (w.Kind == Kind.Weekly) return "Wk";
            if (w.Kind == Kind.Daily) return custom ? label : "Day";
            if (w.Kind == Kind.Credits) return "Credits";
            return label;
        }

        // Status line for the flyout header; null while the provider is fine (ok/stale draw nothing).
        public static string StatusText(ProviderSnapshot p)
        {
            ProviderStatus s = p == null ? null : p.Status;
            if (s == null) return null;
            if (s.State == State.LoggedOut) return string.IsNullOrEmpty(s.Hint) ? "Sign in" : s.Hint;
            if (s.State == State.NotInstalled) return string.IsNullOrEmpty(s.Hint) ? "Not installed" : s.Hint;
            if (s.State == State.FetchFailed) return string.IsNullOrEmpty(s.Message) ? "Error" : s.Message;
            if (s.State == State.Unknown) return string.IsNullOrEmpty(s.Message) ? "n/a" : s.Message;
            return null;
        }

        // What a stale chip shows; rate limiting adds the retry countdown when there is one.
        public static string StaleText(ProviderSnapshot p, DateTime nowUtc, TimeZoneInfo tz)
        {
            ProviderStatus s = p == null ? null : p.Status;
            if (s == null) return "stale";
            if (s.Reason != "rate_limited") return "stale";
            string eta = FormatEta(s.RetryAt, nowUtc, tz);
            if (string.IsNullOrEmpty(eta) || eta == "now") return "rate limited";
            return "rate limited \u00B7 retry in " + eta;
        }

        private static string Pad(int n)
        {
            return n < 10 ? "0" + n.ToString(CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);
        }
    }
}
