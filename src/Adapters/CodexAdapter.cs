// Codex (ChatGPT usage) adapter. Port of reference/js/adapters/codex.js (milestone M2).
// OAuth tokens live in ~/.codex/auth.json; usage comes from the wham/usage endpoint.
// Language level: C# 5 (in-box csc.exe). No WinForms here.
using System;
using System.Collections.Generic;
using System.IO;

namespace UsageMonitor
{
    public class CodexAdapter : IUsageAdapter
    {
        public const string AdapterId = "codex";
        public const string DisplayNameText = "Codex";

        private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

        public CodexAdapter() { }

        public string Id { get { return AdapterId; } }

        public string DisplayName { get { return DisplayNameText; } }

        // ------------------------------------------------------------------------------------------
        // probing and credentials
        // ------------------------------------------------------------------------------------------

        private static bool CliOnPath(Paths paths)
        {
            return paths != null && paths.FindOnPath("codex") != null;
        }

        private static bool AuthPresent(Paths paths)
        {
            try
            {
                return paths != null && paths.CodexAuth != null && File.Exists(paths.CodexAuth);
            }
            catch (Exception) { return false; }
        }

        private static ProviderSnapshot Empty(string state, string message, string hint, DateTime nowUtc)
        {
            ProviderStatus status = new ProviderStatus();
            status.State = state;
            status.Message = message;
            status.Hint = hint;
            return Models.EmptyProvider(AdapterId, DisplayNameText, status, nowUtc);
        }

        // ------------------------------------------------------------------------------------------
        // mapping
        // ------------------------------------------------------------------------------------------

        // classifyWindow() from codex.js: the window length picks the kind, the provider name (when
        // there is one) is prefixed to the label, and the reset comes from reset_at (epoch seconds)
        // or reset_after_seconds (relative to now).
        private static UsageWindow ClassifyWindow(object usedPct, object seconds, object resetAt,
            object resetAfterSeconds, string fallbackLabel, DateTime nowUtc)
        {
            string kind = Kind.WeeklyScoped;
            string label = fallbackLabel;

            double window = AdapterUtil.Num(seconds).HasValue ? AdapterUtil.Num(seconds).Value : double.NaN;
            if (window >= 14400 && window <= 21600)
            {
                kind = Kind.FiveHour;
                label = "5h";
            }
            else if (window >= 518400 && window <= 691200)
            {
                kind = Kind.Weekly;
                label = "Weekly";
            }
            else if (window >= 82800 && window <= 90000)
            {
                kind = Kind.Daily;
                label = "Daily";
            }
            else if (string.IsNullOrEmpty(label))
            {
                int hours = AdapterUtil.Round(window / 3600.0);
                if (hours >= 24) label = AdapterUtil.Round(hours / 24.0) + "d";
                else label = hours + "h";
            }

            // The provider name is only kept for the "additional_rate_limits" entries.
            if (!string.IsNullOrEmpty(fallbackLabel) && fallbackLabel != "5h" && fallbackLabel != "Weekly")
                label = (fallbackLabel + " " + (label == fallbackLabel ? "" : label)).Trim();

            // Number(reset_at) * 1000 when present, Date.now() + Number(reset_after_seconds) * 1000
            // otherwise, and null when neither yields a finite date.
            DateTime? resetsAt = null;
            double? resetAtNum = AdapterUtil.Num(resetAt);
            double? resetAfter = AdapterUtil.Num(resetAfterSeconds);
            if (resetAtNum.HasValue)
                resetsAt = AdapterUtil.FromEpochMs(resetAtNum.Value * 1000.0);
            else if (resetAfter.HasValue)
                // Range-checked: an unusable reset_after_seconds yields "unknown", it must never
                // throw a date out of DateTime's range and lose the whole provider.
                resetsAt = AdapterUtil.FromEpochMs(AdapterUtil.ToEpochMs(nowUtc) + resetAfter.Value * 1000.0);

            UsageWindow w = new UsageWindow();
            w.Kind = kind;
            w.Label = label;
            double? used = Models.Percentage(usedPct);
            w.UsedPct = used;
            w.RemainingPct = used.HasValue ? Models.RemainingPct(used) : null;
            w.ResetsAt = resetsAt;
            return w;
        }

        public static ProviderSnapshot MapWham(object body, DateTime nowUtc)
        {
            List<UsageWindow> windows = new List<UsageWindow>();
            IDictionary<string, object> map = JsonUtil.Map(body);

            if (map != null)
            {
                IDictionary<string, object> rateLimit = AdapterUtil.MapOf(map, "rate_limit");

                IDictionary<string, object> primary = AdapterUtil.MapOf(rateLimit, "primary_window");
                if (primary != null)
                {
                    object used = WindowUsed(primary);
                    if (used != null)
                        windows.Add(ClassifyWindow(used, AdapterUtil.Get(primary, "limit_window_seconds"),
                            AdapterUtil.Get(primary, "reset_at"), AdapterUtil.Get(primary, "reset_after_seconds"), "5h", nowUtc));
                }

                IDictionary<string, object> secondary = AdapterUtil.MapOf(rateLimit, "secondary_window");
                if (secondary != null)
                {
                    object used = WindowUsed(secondary);
                    if (used != null)
                        windows.Add(ClassifyWindow(used, AdapterUtil.Get(secondary, "limit_window_seconds"),
                            AdapterUtil.Get(secondary, "reset_at"), AdapterUtil.Get(secondary, "reset_after_seconds"), "Weekly", nowUtc));
                }

                IList<object> extraLimits = JsonUtil.List(JsonUtil.Get(map, "additional_rate_limits"));
                if (extraLimits != null)
                {
                    foreach (object item in extraLimits)
                    {
                        IDictionary<string, object> entry = JsonUtil.Map(item);
                        if (entry == null) continue;

                        IDictionary<string, object> window = AdapterUtil.MapOf(entry, "primary_window");
                        if (window == null) continue;

                        object used = AdapterUtil.Get(window, "used_percent");
                        if (used == null) continue;

                        string name = AdapterUtil.Str(entry, "name", null);
                        if (string.IsNullOrEmpty(name)) name = AdapterUtil.Str(entry, "id", null);
                        if (string.IsNullOrEmpty(name)) name = "extra";

                        windows.Add(ClassifyWindow(used, AdapterUtil.Get(window, "limit_window_seconds"),
                            AdapterUtil.Get(window, "reset_at"), AdapterUtil.Get(window, "reset_after_seconds"), name, nowUtc));
                    }
                }

                IDictionary<string, object> credits = AdapterUtil.MapOf(map, "rate_limit_reset_credits");
                if (credits != null)
                {
                    double? available = AdapterUtil.Num(AdapterUtil.Get(credits, "available_count"));
                    if (available.HasValue && available.Value > 0)
                    {
                        UsageWindow w = new UsageWindow();
                        w.Kind = Kind.Credits;
                        w.Label = "Reset credits \u00D7" + AdapterUtil.NumText(AdapterUtil.Get(credits, "available_count"));
                        w.UsedPct = null;
                        w.RemainingPct = null;
                        w.ResetsAt = null;
                        windows.Add(w);
                    }
                }
            }

            string plan = AdapterUtil.Str(map, "plan_type", null);
            if (string.IsNullOrEmpty(plan)) plan = null;
            else plan = AdapterUtil.Capitalize(plan);

            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = AdapterId;
            p.DisplayName = DisplayNameText;
            p.Status = new ProviderStatus();
            p.Status.State = State.Ok;
            p.Plan = plan;
            p.Windows = windows;
            p.FetchedAt = Models.ToUtc(nowUtc);
            p.Source = "api";
            return p;
        }

        // used_percent wins, remaining_percent is converted, and the window is skipped when neither
        // is present (exactly like codex.js).
        private static object WindowUsed(IDictionary<string, object> window)
        {
            object used = AdapterUtil.Get(window, "used_percent");
            if (used != null) return used;

            double? remaining = AdapterUtil.Num(AdapterUtil.Get(window, "remaining_percent"));
            if (remaining.HasValue) return 100.0 - remaining.Value;
            return null;
        }

        // ------------------------------------------------------------------------------------------
        // fetch
        // ------------------------------------------------------------------------------------------

        public ProviderSnapshot FetchUsage(AdapterContext ctx)
        {
            DateTime now = ctx != null && ctx.UtcNow != null ? ctx.UtcNow() : DateTime.UtcNow;

            Paths paths = ctx == null ? null : ctx.Paths;
            if (!AuthPresent(paths) && !CliOnPath(paths))
                return Empty(State.NotInstalled, null, "Install Codex, then run: codex login", now);
            if (!AuthPresent(paths))
                return Empty(State.LoggedOut, null, "Run: codex login", now);

            IDictionary<string, object> auth;
            try
            {
                auth = JsonUtil.Map(JsonUtil.Parse(File.ReadAllText(paths.CodexAuth)));
            }
            catch (ArgumentException) { auth = null; }
            catch (InvalidOperationException) { auth = null; }
            catch (FormatException) { auth = null; }
            catch (NotSupportedException) { auth = null; }
            catch (IOException) { auth = null; }
            catch (Exception) { auth = null; }

            if (auth == null)
                return Empty(State.LoggedOut, null, "Run: codex login", now);

            // An API-key login has no subscription windows to report.
            IDictionary<string, object> tokens = AdapterUtil.MapOf(auth, "tokens");
            bool hasAccessToken = tokens != null && !string.IsNullOrEmpty(AdapterUtil.Str(tokens, "access_token", null));
            bool hasApiKey = AdapterUtil.HasText(auth, "api_key") || AdapterUtil.HasText(auth, "OPENAI_API_KEY");
            if (hasApiKey && !hasAccessToken)
                return Empty(State.Unknown,
                    "Codex is signed in with an API key \u2014 subscription windows are unavailable", null, now);

            if (!hasAccessToken)
                return Empty(State.LoggedOut, null, "Run: codex login", now);

            string accessToken = AdapterUtil.Str(tokens, "access_token", null);
            string accountId = AdapterUtil.Str(tokens, "account_id", null);

            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + accessToken;
            headers["ChatGPT-Account-Id"] = accountId == null ? "" : accountId;
            headers["User-Agent"] = "Mozilla/5.0 usage-monitor/1.0";

            HttpResult res;
            try { res = HttpHelpers.GetJson(ctx == null ? null : ctx.Http, UsageUrl, headers); }
            catch (Exception ex) { return FetchFailed(ex, now); }

            int status = res == null ? 0 : res.Status;

            if (status == 401 || status == 403)
                return Empty(State.LoggedOut, null, "Open Codex once to refresh login", now);

            if (status == 429)
            {
                ProviderSnapshot p = Empty(State.FetchFailed, "rate limited", null, now);
                p.RateLimited = true;
                return p;
            }

            if (status != 200 || res.Json == null)
                return Empty(State.FetchFailed, "HTTP " + status, null, now);

            return MapWham(res.Json, now);
        }

        private static ProviderSnapshot FetchFailed(Exception ex, DateTime nowUtc)
        {
            string message = ex == null ? "transport error" : ex.Message;
            if (string.IsNullOrEmpty(message)) message = "transport error";
            return Empty(State.FetchFailed, message, null, nowUtc);
        }
    }
}
