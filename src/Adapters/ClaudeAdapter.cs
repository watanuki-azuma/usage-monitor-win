// Claude Code usage adapter. Port of reference/js/adapters/claude.js (milestone M2).
// OAuth tokens live in ~/.claude/.credentials.json; usage comes from the Anthropic OAuth endpoint.
// Language level: C# 5 (in-box csc.exe). No WinForms here.
using System;
using System.Collections.Generic;
using System.IO;

namespace UsageMonitor
{
    public class ClaudeAdapter : IUsageAdapter
    {
        public const string AdapterId = "claude";
        public const string DisplayNameText = "Claude Code";

        private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
        private const string TokenUrlLegacy = "https://console.anthropic.com/v1/oauth/token";

        // A token that expires within this window is refreshed before it is used.
        private const double RefreshSkewMs = 60000;

        public ClaudeAdapter() { }

        public string Id { get { return AdapterId; } }

        public string DisplayName { get { return DisplayNameText; } }

        // ------------------------------------------------------------------------------------------
        // probing and credentials
        // ------------------------------------------------------------------------------------------

        private static bool CliOnPath(Paths paths)
        {
            return paths != null && paths.FindOnPath("claude") != null;
        }

        private static bool CredsPresent(Paths paths)
        {
            try
            {
                return paths != null && paths.ClaudeCredentials != null && File.Exists(paths.ClaudeCredentials);
            }
            catch (Exception) { return false; }
        }

        // Read + parse the credential file, remembering its mtime for the compare-and-swap write.
        private static Dictionary<string, object> ReadCredsFile(string path, out IDictionary<string, object> oauth, out long mtimeTicks)
        {
            oauth = null;
            mtimeTicks = 0;
            try
            {
                if (path == null || !File.Exists(path)) return null;
                // statSync comes first in claude.js: the mtime captured here is the value the
                // compare-and-swap compares against, so it must be read before the contents.
                mtimeTicks = File.GetLastWriteTimeUtc(path).Ticks;
                string text = File.ReadAllText(path);
                object parsed;
                try { parsed = JsonUtil.Parse(text); }
                catch (ArgumentException) { return null; }
                catch (InvalidOperationException) { return null; }
                catch (FormatException) { return null; }
                catch (NotSupportedException) { return null; }
                IDictionary<string, object> root = JsonUtil.Map(parsed);
                if (root == null) return null;
                oauth = JsonUtil.Map(JsonUtil.Get(root, "claudeAiOauth"));
                return CopyMap(root);
            }
            catch (Exception) { return null; }
        }

        public static string PlanLabel(object oauth)
        {
            IDictionary<string, object> map = JsonUtil.Map(oauth);
            if (map == null) return null;

            string tier = JsonUtil.Str(map, "rateLimitTier", null);
            if (tier != null && tier.Length > 0)
            {
                if (tier == "default_claude_max_20x") return "Max 20x";
                if (tier == "default_claude_max_5x") return "Max 5x";
                if (tier.IndexOf("max_20", StringComparison.Ordinal) >= 0) return "Max 20x";
                if (tier.IndexOf("max_5", StringComparison.Ordinal) >= 0) return "Max 5x";
            }

            string subscription = JsonUtil.Str(map, "subscriptionType", null);
            if (subscription == null) return null;
            if (subscription == "max") return "Max";
            if (subscription == "pro") return "Pro";
            if (subscription == "enterprise") return "Enterprise";
            if (subscription.Length == 0) return null;
            return subscription;
        }

        // ------------------------------------------------------------------------------------------
        // fetch
        // ------------------------------------------------------------------------------------------

        public ProviderSnapshot FetchUsage(AdapterContext ctx)
        {
            DateTime now = ctx != null && ctx.UtcNow != null ? ctx.UtcNow() : DateTime.UtcNow;

            Paths paths = ctx == null ? null : ctx.Paths;
            if (!CredsPresent(paths) && !CliOnPath(paths))
                return Empty(State.NotInstalled, null, "Install Claude Code, then run: claude auth login", now);
            if (!CredsPresent(paths))
                return Empty(State.LoggedOut, null, "Run: claude auth login", now);

            IDictionary<string, object> oauth;
            long mtimeTicks;
            Dictionary<string, object> root = ReadCredsFile(paths.ClaudeCredentials, out oauth, out mtimeTicks);
            if (root == null)
                return Empty(State.LoggedOut, null, "Run: claude auth login", now);

            string accessToken = oauth == null ? null : JsonUtil.Str(oauth, "accessToken", null);
            string refreshToken = oauth == null ? null : JsonUtil.Str(oauth, "refreshToken", null);

            if (string.IsNullOrEmpty(accessToken))
                return Empty(State.Unknown,
                    "Claude is not signed in with a subscription (no OAuth token)", null, now);

            double? expiresAt = oauth == null ? null : JsonUtil.LooseNum(JsonUtil.Get(oauth, "expiresAt"));
            bool refreshEnabled = ctx == null || ctx.Config == null || ctx.Config.RefreshTokens(AdapterId);
            bool hasRefresh = !string.IsNullOrEmpty(refreshToken);

            // A token expiring within the skew window is rotated before it is used at all.
            bool refreshed = false;
            if (refreshEnabled && hasRefresh && expiresAt.HasValue && expiresAt.Value != 0)
            {
                double remaining = expiresAt.Value - AdapterUtil.ToEpochMs(now);
                if (remaining < RefreshSkewMs)
                {
                    int outcome;
                    try { outcome = RefreshNow(ctx, root, oauth, mtimeTicks, now); }
                    // A transport failure here must not cost us the usage request: we keep going
                    // with the token we already have (claude.js's "keep going with current token").
                    catch (Exception) { outcome = NotRefreshed; }
                    refreshed = outcome == Refreshed;
                    // A rejected grant means the login is gone; do not even call the usage endpoint.
                    if (outcome == Rejected)
                        return Empty(State.LoggedOut, null, "Run: claude auth login", now);

                    if (refreshed) Reload(ctx, ref root, ref oauth, ref mtimeTicks, ref accessToken, ref refreshToken);
                }
            }

            HttpResult res;
            try { res = HttpHelpers.GetJson(ctx == null ? null : ctx.Http, UsageUrl, UsageHeaders(accessToken)); }
            catch (Exception ex) { return FetchFailed(ex, now); }

            int status = res == null ? 0 : res.Status;

            // 401/403: rotate the token once, then retry with the new one.
            if ((status == 401 || status == 403) && refreshEnabled && !refreshed && hasRefresh)
            {
                try
                {
                    if (RefreshNow(ctx, root, oauth, mtimeTicks, now) == Refreshed)
                    {
                        Reload(ctx, ref root, ref oauth, ref mtimeTicks, ref accessToken, ref refreshToken);
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            try { res = HttpHelpers.GetJson(ctx == null ? null : ctx.Http, UsageUrl, UsageHeaders(accessToken)); }
                            catch (Exception ex) { return FetchFailed(ex, now); }
                        }
                    }
                }
                catch (Exception) { /* fall through to the status handling below */ }
            }

            return FromUsageResponse(res, oauth, now);
        }

        // After a successful rotation the file is read again, so a CLI-written token file wins over
        // our copy (this is claude.js's `creds = readCreds()`).
        private static void Reload(AdapterContext ctx, ref Dictionary<string, object> root,
            ref IDictionary<string, object> oauth, ref long mtimeTicks, ref string accessToken, ref string refreshToken)
        {
            if (ctx == null || ctx.Paths == null) return;

            IDictionary<string, object> freshOauth;
            long freshMtime;
            Dictionary<string, object> freshRoot = ReadCredsFile(ctx.Paths.ClaudeCredentials, out freshOauth, out freshMtime);
            if (freshRoot == null || freshOauth == null) return;

            root = freshRoot;
            oauth = freshOauth;
            mtimeTicks = freshMtime;
            accessToken = JsonUtil.Str(oauth, "accessToken", null);
            refreshToken = JsonUtil.Str(oauth, "refreshToken", null);
        }

        private const int NotRefreshed = 0;
        private const int Refreshed = 1;
        private const int Rejected = 2;

        // POSTs the refresh grant and, on success, rewrites the token file (compare-and-swap, so a
        // file the CLI changed underneath us is never clobbered).
        private static int RefreshNow(AdapterContext ctx, Dictionary<string, object> root,
            IDictionary<string, object> oauth, long mtimeTicks, DateTime nowUtc)
        {
            IHttp http = ctx == null ? null : ctx.Http;
            if (http == null || root == null) return NotRefreshed;

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["grant_type"] = "refresh_token";
            body["refresh_token"] = oauth == null ? null : JsonUtil.Str(oauth, "refreshToken", null);
            body["client_id"] = ClientId;

            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["Content-Type"] = "application/json";
            headers["User-Agent"] = "usage-monitor/1.0";

            HttpResult res = HttpHelpers.PostJson(http, TokenUrl, headers, body);
            if (res != null && (res.Status == 404 || res.Status == 405))
                res = HttpHelpers.PostJson(http, TokenUrlLegacy, headers, body);
            if (res == null) return NotRefreshed;
            // A rejected grant: the token is gone, not merely invalid.
            if (res.Status == 400 || res.Status == 401) return Rejected;
            if (res.Status != 200) return NotRefreshed;

            IDictionary<string, object> tokens = JsonUtil.Map(res.Json);
            if (tokens == null) return NotRefreshed;
            object granted;
            if (!tokens.TryGetValue("access_token", out granted) || granted == null) return NotRefreshed;

            string token = granted as string;
            if (token != null && token.Length == 0) token = null;
            string refresh = JsonUtil.Str(tokens, "refresh_token", null);
            if (refresh != null && refresh.Length == 0) refresh = null;
            double? expiresIn = JsonUtil.LooseNum(JsonUtil.Get(tokens, "expires_in"));

            string credPath = ctx == null || ctx.Paths == null ? null : ctx.Paths.ClaudeCredentials;
            if (credPath == null) return Refreshed;

            // Compare-and-swap: only rewrite when the file has not changed since it was read.
            // This check-then-replace race with the CLI itself cannot be closed without
            // cooperation from the CLI, so at least the original file is never destroyed first.
            try
            {
                if (!File.Exists(credPath)) return Refreshed;
                if (File.GetLastWriteTimeUtc(credPath).Ticks != mtimeTicks) return Refreshed;

                Dictionary<string, object> nextOauth = CopyMap(oauth);
                if (nextOauth == null) nextOauth = new Dictionary<string, object>();
                if (token != null) nextOauth["accessToken"] = token;
                if (refresh != null) nextOauth["refreshToken"] = refresh;
                if (expiresIn.HasValue && !double.IsNaN(expiresIn.Value) && !double.IsInfinity(expiresIn.Value))
                    nextOauth["expiresAt"] = AdapterUtil.ToEpochMs(nowUtc) + expiresIn.Value * 1000.0;

                root["claudeAiOauth"] = nextOauth;

                // One atomic replace: the destination is never deleted first, so a crash in
                // between cannot leave the user's credentials missing, and a failing replace
                // keeps the original file (the catch below only removes the temp copy).
                string tmp = credPath + ".tmp";
                File.WriteAllText(tmp, AdapterUtil.IndentedJson(root),
                    new System.Text.UTF8Encoding(false));
                File.Replace(tmp, credPath, null);
            }
            catch (Exception)
            {
                try
                {
                    string tmp = credPath + ".tmp";
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch (Exception) { /* nothing to clean, or nothing we can do */ }
            }
            return Refreshed;
        }

        private static ProviderSnapshot FromUsageResponse(HttpResult res, IDictionary<string, object> oauth, DateTime nowUtc)
        {
            int status = res == null ? 0 : res.Status;

            if (status == 401 || status == 403)
                return Empty(State.LoggedOut, null, "Run: claude auth login", nowUtc);

            if (status == 429)
            {
                ProviderSnapshot p = Empty(State.FetchFailed, "rate limited", null, nowUtc);
                p.RateLimited = true;
                return p;
            }

            if (status != 200 || res.Json == null)
                return Empty(State.FetchFailed, "HTTP " + status, null, nowUtc);

            return MapUsage(res.Json, oauth, nowUtc);
        }

        private static ProviderSnapshot FetchFailed(Exception ex, DateTime nowUtc)
        {
            string message = ex == null ? "transport error" : ex.Message;
            if (string.IsNullOrEmpty(message)) message = "transport error";
            return Empty(State.FetchFailed, message, null, nowUtc);
        }

        private static ProviderSnapshot Empty(string state, string message, string hint, DateTime nowUtc)
        {
            ProviderStatus status = new ProviderStatus();
            status.State = state;
            status.Message = message;
            status.Hint = hint;
            return Models.EmptyProvider(AdapterId, DisplayNameText, status, nowUtc);
        }

        private static Dictionary<string, string> UsageHeaders(string accessToken)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + (accessToken == null ? "" : accessToken);
            headers["anthropic-beta"] = "oauth-2025-04-20";
            headers["anthropic-version"] = "2023-06-01";
            headers["x-app"] = "cli";
            headers["User-Agent"] = "claude-cli/2.1.201 (external, cli)";
            headers["anthropic-dangerous-direct-browser-access"] = "true";
            return headers;
        }

        // ------------------------------------------------------------------------------------------
        // mapping (body -> snapshot). Must never throw on an unexpected shape (SPEC 5.3).
        // ------------------------------------------------------------------------------------------

        public static ProviderSnapshot MapUsage(object body, object oauth, DateTime nowUtc)
        {
            List<UsageWindow> windows = new List<UsageWindow>();

            IDictionary<string, object> map = JsonUtil.Map(body);

            IDictionary<string, object> fiveHour = map == null ? null : AdapterUtil.MapOf(map, "five_hour");
            if (fiveHour != null && KeepsWindow(fiveHour))
                windows.Add(Models.WindowOf(Kind.FiveHour, "5h",
                    JsonUtil.Get(fiveHour, "utilization"),
                    JsonUtil.Str(fiveHour, "resets_at", null)));

            IDictionary<string, object> sevenDay = map == null ? null : AdapterUtil.MapOf(map, "seven_day");
            if (sevenDay != null && KeepsWindow(sevenDay))
                windows.Add(Models.WindowOf(Kind.Weekly, "Weekly",
                    JsonUtil.Get(sevenDay, "utilization"),
                    JsonUtil.Str(sevenDay, "resets_at", null)));

            if (map != null)
            {
                IList<object> limits = JsonUtil.List(JsonUtil.Get(map, "limits"));
                if (limits != null)
                {
                    foreach (object item in limits)
                    {
                        if (!ScopeWeeklyIsWanted(item)) continue;

                        IDictionary<string, object> scope = AdapterUtil.MapOf(item, "scope");
                        string name = AdapterUtil.StrPath(scope, "model", "display_name", null);
                        if (string.IsNullOrEmpty(name)) continue;

                        object used = JsonUtil.Get(AdapterUtil.Map(item), "percent");
                        string resetsAt = AdapterUtil.Str(item, "resets_at", null);
                        windows.Add(Models.WindowOf(Kind.WeeklyScoped, name + " weekly", used, resetsAt));
                    }
                }

                IDictionary<string, object> spend = AdapterUtil.MapOf(map, "spend");
                if (spend != null && SpendIsWanted(spend))
                {
                    double? used = JsonUtil.LooseNum(AdapterUtil.GetPath(spend, "used", "amount_minor"));
                    double? limit = JsonUtil.LooseNum(AdapterUtil.GetPath(spend, "limit", "amount_minor"));
                    windows.Add(Models.WindowOf(Kind.Credits, "Credits", SpendPct(used, limit), null));
                }
            }

            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = AdapterId;
            p.DisplayName = DisplayNameText;
            p.Status = new ProviderStatus();
            p.Status.State = State.Ok;
            p.Plan = PlanLabel(oauth);
            p.Windows = windows;
            p.FetchedAt = Models.ToUtc(nowUtc);
            p.Source = "api";
            return p;
        }

        private static bool KeepsWindow(IDictionary<string, object> window)
        {
            if (window == null) return false;
            return JsonUtil.Get(window, "utilization") != null
                || !string.IsNullOrEmpty(JsonUtil.Str(window, "resets_at", null));
        }

        private static bool ScopeWeeklyIsWanted(object limit)
        {
            IDictionary<string, object> map = AdapterUtil.Map(limit);
            if (map == null) return false;
            if (JsonUtil.Str(map, "kind", null) != Kind.WeeklyScoped) return false;
            if (AdapterUtil.MapOf(map, "scope") == null) return false;
            if (AdapterUtil.MapOf(AdapterUtil.MapOf(map, "scope"), "model") == null) return false;
            return !string.IsNullOrEmpty(AdapterUtil.StrPath(AdapterUtil.MapOf(map, "scope"), "model", "display_name", null));
        }

        private static bool SpendIsWanted(IDictionary<string, object> spend)
        {
            if (spend == null) return false;
            if (!(JsonUtil.Bool(JsonUtil.Get(spend, "enabled")) ?? false)) return false;
            if (AdapterUtil.MapOf(spend, "limit") == null) return false;
            if (AdapterUtil.MapOf(spend, "used") == null) return false;
            return true;
        }

        private static double? SpendPct(double? used, double? limit)
        {
            if (!used.HasValue || !limit.HasValue) return null;
            if (limit.Value == 0) return null;   // a falsy denominator is "unknown" in JS, not 0
            return used.Value / limit.Value * 100.0;
        }

        // ------------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------------

        private static Dictionary<string, object> CopyMap(IDictionary<string, object> source)
        {
            if (source == null) return null;
            Dictionary<string, object> copy = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> kv in source) copy[kv.Key] = kv.Value;
            return copy;
        }
    }
}
