// Gemini / Antigravity usage adapter. Port of reference/js/adapters/gemini.js (milestone M3).
//
// Credentials come from three places, in order: the Windows Credential Manager
// ("gemini:antigravity", injected as ctx.Credentials), the on-disk token files under
// ~/.gemini, or the ANTIGRAVITY_* environment variables. Usage comes from the
// v1internal:* endpoints of the two API hosts; the quota summary is tried first and the
// quota / models endpoints are the fallbacks.
//
// The OAuth client id/secret are extracted at runtime from the agy binary — never hard-coded
// (with AGY_CLIENT_ID / AGY_CLIENT_SECRET and GEMINI_OAUTH_CLIENT_ID / GEMINI_OAUTH_CLIENT_SECRET
// as environment overrides).
//
// Language level: C# 5 (in-box csc.exe). No WinForms here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageMonitor
{
    public class GeminiAdapter : IUsageAdapter
    {
        public const string AdapterId = "gemini";
        public const string DisplayNameText = "Gemini";

        private const string WinCredTarget = "gemini:antigravity";
        private const string TokenUrl = "https://oauth2.googleapis.com/token";

        private static readonly string[] ApiHosts = new string[]
        {
            "https://daily-cloudcode-pa.googleapis.com",  // agy itself talks to daily; prod reports a separate, unused pool
            "https://cloudcode-pa.googleapis.com",
        };

        private const string UserAgent = "antigravity/windows/amd64";

        // An access token that expires within this window is refreshed before it is used.
        private const double RefreshSkewSeconds = 60;

        // ------------------------------------------------------------------------------------------
        // Mapping helpers (exact signatures from SPEC 4). They take whatever
        // JavaScriptSerializer.DeserializeObject() produced and never throw on odd shapes.
        // ------------------------------------------------------------------------------------------

        // usedFromFraction() — a remaining fraction (0..1) becomes a used percentage, clamped
        // to 0..100. Non-numeric input is null, exactly like the JS Number.isFinite() guard.
        public static double? UsedFromFraction(object remaining)
        {
            if (remaining == null || remaining is bool) return null;
            string text = remaining as string;
            if (text != null && text.Trim().Length == 0) return null;

            double? frac = JsonUtil.LooseNum(remaining);
            if (!frac.HasValue) return null;

            double used = (1.0 - frac.Value) * 100.0;
            if (used < 0) used = 0;
            if (used > 100) used = 100;
            return used;
        }

        // expiryEpoch() — accepts epoch seconds, epoch millis or an ISO-8601 string and always
        // answers in epoch seconds (the millis form is divided down when it is obviously millis).
        public static double? ExpiryEpoch(object value)
        {
            if (value == null) return null;

            string text = value as string;
            if (text == null)
            {
                double? number = JsonUtil.LooseNum(value);
                if (!number.HasValue) return null;
                return number.Value > 1e12 ? number.Value / 1000.0 : number.Value;
            }

            if (text.Length == 0) return null;
            double parsed;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
            {
                return parsed > 1e12 ? parsed / 1000.0 : parsed;
            }

            DateTime? iso = Models.ParseIso(text);
            return iso.HasValue ? AdapterUtil.ToEpochMs(iso.Value) / 1000.0 : (double?)null;
        }

        // mapSummary() — only the groups that belong to Gemini are shown, so one provider's
        // quota never leaks into another's row.
        public static List<UsageWindow> MapSummary(object data, DateTime nowUtc)
        {
            List<UsageWindow> windows = new List<UsageWindow>();
            IDictionary<string, object> map = JsonUtil.Map(data);
            if (map == null) return windows;

            IList<object> raw = JsonUtil.List(JsonUtil.Get(map, "groups"));
            if (raw == null) return windows;

            List<IDictionary<string, object>> groups = new List<IDictionary<string, object>>();
            foreach (object item in raw)
            {
                IDictionary<string, object> group = JsonUtil.Map(item);
                if (group == null) continue;
                if (IsGeminiGroup(group)) groups.Add(group);
            }

            foreach (IDictionary<string, object> group in groups)
            {
                IList<object> buckets = JsonUtil.List(JsonUtil.Get(group, "buckets"));
                if (buckets == null) continue;

                foreach (object item in buckets)
                {
                    IDictionary<string, object> bucket = JsonUtil.Map(item);
                    if (bucket == null) continue;
                    if (JsonUtil.Get(bucket, "remainingFraction") == null) continue;

                    double? used = UsedFromFraction(JsonUtil.Get(bucket, "remainingFraction"));
                    if (!used.HasValue) continue;

                    string kind, label;
                    ClassifyBucket(bucket, out kind, out label);
                    if (groups.Count > 1) label = PrefixGroupLabel(group, label);

                    windows.Add(Models.WindowOf(kind, label, used.Value, JsonUtil.Str(bucket, "resetTime", null)));
                }
            }
            return windows;
        }

        // The non-Gemini groups of the summary (AGY also sells "Claude and GPT models" with their
        // own 5-hour and weekly limits). They get their own rows, labelled by group, and the kind
        // Quota so nothing that reads snapshot.json takes them for Gemini's own five_hour/weekly.
        public static List<UsageWindow> MapOtherGroups(object data)
        {
            List<UsageWindow> windows = new List<UsageWindow>();
            IDictionary<string, object> map = JsonUtil.Map(data);
            if (map == null) return windows;

            IList<object> raw = JsonUtil.List(JsonUtil.Get(map, "groups"));
            if (raw == null) return windows;

            foreach (object item in raw)
            {
                IDictionary<string, object> group = JsonUtil.Map(item);
                if (group == null || IsGeminiGroup(group)) continue;

                IList<object> buckets = JsonUtil.List(JsonUtil.Get(group, "buckets"));
                if (buckets == null) continue;
                string prefix = ShortGroup(group);
                List<UsageWindow> groupRows = new List<UsageWindow>();

                foreach (object b in buckets)
                {
                    IDictionary<string, object> bucket = JsonUtil.Map(b);
                    if (bucket == null) continue;
                    double? used = UsedFromFraction(JsonUtil.Get(bucket, "remainingFraction"));
                    if (!used.HasValue) continue;

                    string kind, label;
                    ClassifyBucket(bucket, out kind, out label);
                    string suffix = kind == Kind.FiveHour ? "5h" : kind == Kind.Weekly ? "Wk" : label;
                    string text = prefix.Length == 0 ? suffix : prefix + " " + suffix;
                    UsageWindow win = Models.WindowOf(Kind.Quota, text, used.Value, JsonUtil.Str(bucket, "resetTime", null));
                    if (kind == Kind.FiveHour) groupRows.Insert(0, win); else groupRows.Add(win);
                }
                windows.AddRange(groupRows);
            }
            return windows;
        }

        private static double ModelVersion(string id)
        {
            Match m = VersionFamilyPattern.Match(id == null ? "" : id.Trim());
            double v;
            if (m.Success && m.Groups[1].Success &&
                double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        private static bool IsLite(string id)
        {
            return id != null && id.IndexOf("lite", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // "Claude and GPT models" -> "Claude/GPT".
        private static string ShortGroup(IDictionary<string, object> group)
        {
            string name = GroupLabel(group).Trim();
            if (name.EndsWith(" models", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 7);
            name = Regex.Replace(name, @"\s+and\s+|\s*&\s*|\s*,\s*", "/", RegexOptions.IgnoreCase);
            return name.Length > 14 ? name.Substring(0, 14) : name;
        }

        // mapModels() — the fetchAvailableModels response: an array or an id-keyed object, only
        // the Gemini models, the two highest-usage quotas.
        public static List<UsageWindow> MapModels(object data, DateTime nowUtc)
        {
            List<IDictionary<string, object>> models = new List<IDictionary<string, object>>();
            IDictionary<string, object> map = JsonUtil.Map(data);
            if (map != null)
            {
                object raw = JsonUtil.Get(map, "models");
                IList<object> list = JsonUtil.List(raw);
                if (list != null)
                {
                    foreach (object item in list)
                    {
                        IDictionary<string, object> m = JsonUtil.Map(item);
                        if (m != null) models.Add(m);
                    }
                }
                else
                {
                    IDictionary<string, object> keyed = JsonUtil.Map(raw);
                    if (keyed != null)
                    {
                        // Object.entries(): the key becomes the model id when the entry has none.
                        foreach (KeyValuePair<string, object> kv in keyed)
                        {
                            IDictionary<string, object> merged = CopyMap(JsonUtil.Map(kv.Value));
                            if (merged == null) continue;
                            object existing;
                            bool hasModel = merged.TryGetValue("model", out existing) && existing is string
                                && ((string)existing).Length > 0;
                            if (!hasModel) merged["model"] = kv.Key;
                            models.Add(merged);
                        }
                    }
                }
            }

            List<QuotaEntry> scored = new List<QuotaEntry>();
            foreach (IDictionary<string, object> model in models)
            {
                IDictionary<string, object> quota = AdapterUtil.MapOf(model, "quotaInfo");
                if (quota == null) continue;
                if (JsonUtil.Get(quota, "remainingFraction") == null) continue;

                double? used = UsedFromFraction(JsonUtil.Get(quota, "remainingFraction"));
                if (!used.HasValue) continue;

                QuotaEntry q = new QuotaEntry();
                q.Used = used.Value;
                q.Name = FirstText(model, "displayName", "label", "model", "name");
                q.Id = JsonUtil.Str(model, "model", null) ?? JsonUtil.Str(model, "name", null) ?? "";
                q.ResetTime = JsonUtil.Str(quota, "resetTime", null);
                scored.Add(q);
            }

            List<QuotaEntry> picked = new List<QuotaEntry>();
            foreach (QuotaEntry q in SortByUsedDesc(scored))
            {
                string name = q.Name == null ? "" : q.Name;
                if (name.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) < 0
                    && q.Id.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) < 0) continue;
                picked.Add(q);
                if (picked.Count == 2) break;
            }

            List<UsageWindow> windows = new List<UsageWindow>();
            foreach (QuotaEntry q in picked)
                windows.Add(Models.WindowOf(Kind.Quota, ShortModel(q.Name), q.Used, q.ResetTime));
            return windows;
        }

        // mapQuotaBuckets() — the retrieveUserQuota response (buckets per model). Live, every
        // Gemini model reports the same remainingFraction and resetTime: they share one 5-hour
        // pool, so it is shown as a single five_hour row (the highest-use bucket, worst case).
        // No weekly Gemini bucket exists here (prod's "gemini-weekly" never moves with agy use).
        private static List<UsageWindow> MapQuotaBuckets(object data)
        {
            List<UsageWindow> windows = new List<UsageWindow>();
            IDictionary<string, object> map = JsonUtil.Map(data);
            if (map == null) return windows;

            IList<object> raw = JsonUtil.List(JsonUtil.Get(map, "buckets"));
            if (raw == null) return windows;

            List<QuotaEntry> scored = new List<QuotaEntry>();
            foreach (object item in raw)
            {
                IDictionary<string, object> bucket = JsonUtil.Map(item);
                if (bucket == null) continue;
                if (JsonUtil.Get(bucket, "remainingFraction") == null) continue;

                double? used = UsedFromFraction(JsonUtil.Get(bucket, "remainingFraction"));
                if (!used.HasValue) continue;

                QuotaEntry q = new QuotaEntry();
                q.Used = used.Value;
                q.Name = JsonUtil.Str(bucket, "modelId", null) ?? JsonUtil.Str(bucket, "id", null);
                q.Id = q.Name;
                q.ResetTime = JsonUtil.Str(bucket, "resetTime", null);

                // Only buckets of a Gemini model are candidates (a bucket that belongs to
                // another provider must never take the row), before sorting by used%.
                if (!IsGeminiBucket(bucket)) continue;
                scored.Add(q);
            }

            // Ties (typically every model at the same value) go to the newest model, non-Lite first.
            List<QuotaEntry> ordered = new List<QuotaEntry>(scored);
            List<QuotaEntry> original = new List<QuotaEntry>(scored);
            ordered.Sort(delegate(QuotaEntry a, QuotaEntry b)
            {
                int c = b.Used.CompareTo(a.Used);
                if (c != 0) return c;
                c = ModelVersion(b.Name).CompareTo(ModelVersion(a.Name));
                if (c != 0) return c;
                c = IsLite(a.Name).CompareTo(IsLite(b.Name));
                if (c != 0) return c;
                return original.IndexOf(a).CompareTo(original.IndexOf(b));
            });
            if (ordered.Count > 0)
                windows.Add(Models.WindowOf(Kind.FiveHour, "5h", ordered[0].Used, ordered[0].ResetTime));
            return windows;
        }

        // isGeminiBucket(): the retrieveUserQuota fallback is shared across providers, so a
        // bucket only counts as a candidate when its model id (modelId / name / id) mentions
        // "gemini" — the same rule mapModels() applies to its entries.
        private static bool IsGeminiBucket(IDictionary<string, object> bucket)
        {
            if (bucket == null) return false;
            string[] fields = new string[] { "modelId", "name", "id" };
            foreach (string field in fields)
            {
                string value = JsonUtil.Str(bucket, field, null);
                if (value != null && value.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------
        // adapter plumbing
        // ------------------------------------------------------------------------------------------

        // Refreshed credentials and the extracted OAuth client are cached per instance so two
        // adapters never share state (the JS module-level cache is not an option here).
        private GemCreds memCreds;
        private OauthClient oauthClient;
        // When ExtractOauthClient() fails, the UTC time of that failure is remembered here
        // (DateTime.MinValue means "no failure cached"). A recently failed extraction is not
        // retried for ten minutes, so an expired token does not rescan the ~190 MB agy binary
        // (up to twice) on every poll. A success clears it again.
        private DateTime oauthClientFailedUtc = DateTime.MinValue;

        public GeminiAdapter() { }

        public string Id { get { return AdapterId; } }

        public string DisplayName { get { return DisplayNameText; } }

        public ProviderSnapshot FetchUsage(AdapterContext ctx)
        {
            DateTime now = ctx != null && ctx.UtcNow != null ? ctx.UtcNow() : DateTime.UtcNow;
            Paths paths = ctx == null ? null : ctx.Paths;
            bool cliOnPath = CliOnPath(paths);

            GemCreds creds;
            try { creds = GetCreds(ctx, now); }
            catch (Exception) { creds = null; }

            if (creds == null && !cliOnPath)
                return Empty(State.NotInstalled, null, "Install Antigravity CLI, then run: agy", now);
            if (creds == null)
                return Empty(State.LoggedOut, null, "Run: agy  (sign in with Google)", now);

            bool refreshEnabled = ctx == null || ctx.Config == null || ctx.Config.RefreshTokens(AdapterId);
            Dictionary<string, object> loadBody = LoadBody(creds, paths);

            string token = creds.AccessToken;
            HostResult lastFail = null;

            foreach (string host in ApiHosts)
            {
                HostResult result;
                try { result = FetchFromHost(ctx, host, token, loadBody, now); }
                catch (Exception) { HostResult transport = new HostResult(); transport.Failed = true; lastFail = transport; continue; }

                if (result.AuthFailed && refreshEnabled && !string.IsNullOrEmpty(creds.RefreshToken))
                {
                    GemCreds next;
                    try { next = RefreshAccess(ctx, creds, now); }
                    catch (Exception) { next = null; }
                    if (next != null && !string.IsNullOrEmpty(next.AccessToken))
                    {
                        creds = next;
                        token = next.AccessToken;
                        try { result = FetchFromHost(ctx, host, token, loadBody, now); }
                        catch (Exception) { HostResult transport = new HostResult(); transport.Failed = true; lastFail = transport; continue; }
                    }
                }

                if (result.AuthFailed)
                    return Empty(State.LoggedOut, null, "Run: agy  (token expired)", now);

                if (result.Ok)
                {
                    ProviderSnapshot p = new ProviderSnapshot();
                    p.Id = AdapterId;
                    p.DisplayName = DisplayNameText;
                    p.Status = new ProviderStatus();
                    p.Status.State = State.Ok;
                    p.Plan = result.Plan;
                    p.Windows = result.Windows;
                    p.FetchedAt = now;
                    p.Source = "api";
                    return p;
                }

                lastFail = result;
                if (result.Failed && result.HasStatus && result.Status == 429) break;
            }

            if (lastFail != null && lastFail.Failed && lastFail.HasStatus && lastFail.Status == 429)
            {
                ProviderSnapshot p = Empty(State.FetchFailed, "rate limited", null, now);
                p.RateLimited = true;
                return p;
            }

            string message = lastFail != null && lastFail.HasStatus && lastFail.Status != 0
                ? "HTTP " + lastFail.Status
                : "quota unavailable";
            return Empty(State.FetchFailed, message, null, now);
        }

        // fetchFromHost(): loadCodeAssist for the project + plan, then the quota summary and its
        // two fallbacks. Any 401/403 anywhere means "the token is dead"; 429 everywhere means
        // "back off".
        private static HostResult FetchFromHost(AdapterContext ctx, string host, string token,
            Dictionary<string, object> loadBody, DateTime nowUtc)
        {
            HostResult r = new HostResult();

            HttpResult load = AssistPost(ctx, host, token, "loadCodeAssist", loadBody);
            if (load == null) { r.Failed = true; return r; }
            if (load.Status == 401 || load.Status == 403) { r.AuthFailed = true; return r; }
            if (load.Status != 200 || load.Json == null) { r.Failed = true; r.Status = load.Status; r.HasStatus = true; return r; }

            string project = ExtractProject(load.Json);
            r.Plan = PlanLabel(load.Json);

            Dictionary<string, object> quotaBody = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(project)) quotaBody["project"] = project;

            HttpResult summary = AssistPost(ctx, host, token, "retrieveUserQuotaSummary", quotaBody);
            if (summary.Status == 429) { r.Failed = true; r.Status = 429; r.HasStatus = true; return r; }
            if (summary.Status == 401 || summary.Status == 403) { r.AuthFailed = true; return r; }

            // Third-party (Claude/GPT) rows from the summary are appended after the Gemini rows,
            // whichever endpoint the Gemini rows come from.
            List<UsageWindow> others = new List<UsageWindow>();
            if (summary.Status == 200 && summary.Json != null)
            {
                others = MapOtherGroups(summary.Json);
                List<UsageWindow> windows = MapSummary(summary.Json, nowUtc);
                if (windows.Count > 0) return Done(r, windows, others);
            }

            HttpResult quota = AssistPost(ctx, host, token, "retrieveUserQuota", quotaBody);
            if (quota.Status == 429) { r.Failed = true; r.Status = 429; r.HasStatus = true; return r; }
            if (quota.Status == 401 || quota.Status == 403) { r.AuthFailed = true; return r; }
            if (quota.Status == 200 && quota.Json != null)
            {
                List<UsageWindow> windows = MapQuotaBuckets(quota.Json);
                if (windows.Count > 0) return Done(r, windows, others);
            }

            HttpResult models = AssistPost(ctx, host, token, "fetchAvailableModels", quotaBody);
            if (models.Status == 429) { r.Failed = true; r.Status = 429; r.HasStatus = true; return r; }
            if (models.Status == 401 || models.Status == 403) { r.AuthFailed = true; return r; }
            if (models.Status == 200 && models.Json != null)
            {
                List<UsageWindow> windows = MapModels(models.Json, nowUtc);
                if (windows.Count > 0) return Done(r, windows, others);
            }

            if (others.Count > 0) return Done(r, new List<UsageWindow>(), others);

            // JS: [models, quota, summary].find((r) => r.status !== 200)?.status
            r.Failed = true;
            if (models.Status != 200) { r.Status = models.Status; r.HasStatus = true; }
            else if (quota.Status != 200) { r.Status = quota.Status; r.HasStatus = true; }
            else if (summary.Status != 200) { r.Status = summary.Status; r.HasStatus = true; }
            return r;
        }

        private static HttpResult AssistPost(AdapterContext ctx, string host, string token, string method, object body)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + (token == null ? "" : token);
            headers["User-Agent"] = UserAgent;
            return HttpHelpers.PostJson(ctx == null ? null : ctx.Http, host + "/v1internal:" + method, headers, body);
        }

        // ------------------------------------------------------------------------------------------
        // credentials
        // ------------------------------------------------------------------------------------------

        private static bool CliOnPath(Paths paths)
        {
            if (paths == null) return false;
            return paths.FindOnPath("agy") != null || paths.FindOnPath("gemini") != null;
        }

        private GemCreds GetCreds(AdapterContext ctx, DateTime nowUtc)
        {
            bool refreshEnabled = ctx == null || ctx.Config == null || ctx.Config.RefreshTokens(AdapterId);
            if (AccessValid(memCreds, nowUtc)) return memCreds;

            GemCreds creds = ReadStoredCreds(ctx);
            if (creds == null) return null;

            if (refreshEnabled && !AccessValid(creds, nowUtc) && !string.IsNullOrEmpty(creds.RefreshToken))
            {
                GemCreds next;
                try { next = RefreshAccess(ctx, creds, nowUtc); }
                catch (Exception) { next = null; }
                if (next != null) return next;
            }

            memCreds = creds;
            return creds;
        }

        private static bool AccessValid(GemCreds creds, DateTime nowUtc)
        {
            if (creds == null || string.IsNullOrEmpty(creds.AccessToken)) return false;
            // JS: `if (!creds.expiry_epoch) return true;` — an absent (or zeroed) expiry is trusted.
            if (!creds.ExpiryEpoch.HasValue || creds.ExpiryEpoch.Value == 0) return true;
            return creds.ExpiryEpoch.Value - AdapterUtil.ToEpochMs(nowUtc) / 1000.0 > RefreshSkewSeconds;
        }

        // readStoredCreds(): wincred first, then the token files, then the environment.
        private static GemCreds ReadStoredCreds(AdapterContext ctx)
        {
            ICredentialReader reader = ctx == null ? null : ctx.Credentials;
            if (reader != null)
            {
                string blob = null;
                try { blob = reader.ReadGenericUtf8(WinCredTarget); }
                catch (Exception) { blob = null; }

                IDictionary<string, object> parsed = JsonUtil.Map(JsonUtil.Parse(blob));
                if (parsed != null)
                {
                    IDictionary<string, object> token = JsonUtil.Map(JsonUtil.Get(parsed, "token"));
                    GemCreds fromBlock = FromTokenBlock(token != null ? token : parsed, "wincred");
                    if (fromBlock != null) return fromBlock;
                }
            }

            Paths paths = ctx == null ? null : ctx.Paths;
            GemCreds fileCred = FromFile(paths == null ? null : paths.AntigravityToken)
                ?? FromFile(paths == null ? null : paths.GeminiOAuth);
            if (fileCred != null) return fileCred;

            string envRefresh = Environment.GetEnvironmentVariable("ANTIGRAVITY_REFRESH_TOKEN");
            string envAccess = Environment.GetEnvironmentVariable("ANTIGRAVITY_ACCESS_TOKEN");
            if (!string.IsNullOrEmpty(envRefresh) || !string.IsNullOrEmpty(envAccess))
            {
                GemCreds fromEnv = new GemCreds();
                fromEnv.AccessToken = string.IsNullOrEmpty(envAccess) ? null : envAccess;
                fromEnv.RefreshToken = string.IsNullOrEmpty(envRefresh) ? null : envRefresh;
                fromEnv.Source = "env";
                return fromEnv;
            }
            return null;
        }

        private static GemCreds FromTokenBlock(IDictionary<string, object> token, string source)
        {
            if (token == null) return null;

            string access = JsonUtil.Str(token, "access_token", null);
            if (string.IsNullOrEmpty(access)) access = JsonUtil.Str(token, "accessToken", null);
            string refresh = JsonUtil.Str(token, "refresh_token", null);
            if (string.IsNullOrEmpty(refresh)) refresh = JsonUtil.Str(token, "refreshToken", null);
            if (string.IsNullOrEmpty(access) && string.IsNullOrEmpty(refresh)) return null;

            GemCreds creds = new GemCreds();
            creds.AccessToken = string.IsNullOrEmpty(access) ? null : access;
            creds.RefreshToken = string.IsNullOrEmpty(refresh) ? null : refresh;

            object expiry = JsonUtil.Get(token, "expiry");
            if (expiry == null) expiry = JsonUtil.Get(token, "expiry_date");
            if (expiry == null) expiry = JsonUtil.Get(token, "expires_at");
            creds.ExpiryEpoch = ExpiryEpoch(expiry);
            creds.Source = source;
            return creds;
        }

        private static GemCreds FromFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                IDictionary<string, object> json = JsonUtil.Map(JsonUtil.Parse(File.ReadAllText(path)));
                if (json == null) return null;
                IDictionary<string, object> token = JsonUtil.Map(JsonUtil.Get(json, "token"));
                return FromTokenBlock(token != null ? token : json, "file");
            }
            catch (Exception) { return null; }
        }

        private GemCreds RefreshAccess(AdapterContext ctx, GemCreds creds, DateTime nowUtc)
        {
            if (creds == null || string.IsNullOrEmpty(creds.RefreshToken)) return null;

            OauthClient client = ExtractOauthClient(ctx);
            if (client == null) return null;

            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["Accept"] = "application/json";
            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["client_id"] = client.ClientId;
            fields["client_secret"] = client.ClientSecret;
            fields["refresh_token"] = creds.RefreshToken;
            fields["grant_type"] = "refresh_token";

            HttpResult res = HttpHelpers.PostForm(ctx == null ? null : ctx.Http, TokenUrl, headers, fields);
            if (res == null || res.Status != 200 || res.Json == null) return null;

            IDictionary<string, object> token = JsonUtil.Map(res.Json);
            string access = token == null ? null : JsonUtil.Str(token, "access_token", null);
            if (string.IsNullOrEmpty(access)) return null;

            string refresh = token == null ? null : JsonUtil.Str(token, "refresh_token", null);
            if (string.IsNullOrEmpty(refresh)) refresh = creds.RefreshToken;

            double? expiresIn = token == null ? null : JsonUtil.LooseNum(JsonUtil.Get(token, "expires_in"));
            double seconds = expiresIn.HasValue && expiresIn.Value != 0 ? expiresIn.Value : 3600.0;

            GemCreds next = new GemCreds();
            next.AccessToken = access;
            next.RefreshToken = refresh;
            next.ExpiryEpoch = AdapterUtil.ToEpochMs(nowUtc) / 1000.0 + seconds;
            next.Source = creds.Source;
            memCreds = next;
            return next;
        }

        // extractOauthClient(): the environment overrides win, otherwise the client id and secret
        // are scraped out of the agy binary (that is where the CLI keeps them).
        private OauthClient ExtractOauthClient(AdapterContext ctx)
        {
            if (oauthClient != null) return oauthClient;

            DateTime now = ctx != null && ctx.UtcNow != null ? ctx.UtcNow() : DateTime.UtcNow;

            // Negative cache: a recent failed scan (no binary, or no secrets in it) is not
            // retried for ten minutes. Without it, an expired token re-read the whole ~190 MB
            // binary on every poll, up to twice per pass.
            if (oauthClientFailedUtc != DateTime.MinValue
                && (now - oauthClientFailedUtc).TotalMinutes < 10.0)
                return null;

            OauthClient found = ExtractOauthClientUncached(ctx);
            if (found != null)
            {
                oauthClient = found;
                oauthClientFailedUtc = DateTime.MinValue;   // a success forgets any earlier failure
                return oauthClient;
            }

            oauthClientFailedUtc = now;
            return null;
        }

        // The actual extraction (environment overrides, then the agy binary). Returns the client
        // or null; it never touches the cache fields itself.
        private static OauthClient ExtractOauthClientUncached(AdapterContext ctx)
        {
            string envId = Environment.GetEnvironmentVariable("AGY_CLIENT_ID");
            if (string.IsNullOrEmpty(envId)) envId = Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_ID");
            string envSecret = Environment.GetEnvironmentVariable("AGY_CLIENT_SECRET");
            if (string.IsNullOrEmpty(envSecret)) envSecret = Environment.GetEnvironmentVariable("GEMINI_OAUTH_CLIENT_SECRET");
            if (!string.IsNullOrEmpty(envId) && !string.IsNullOrEmpty(envSecret))
            {
                OauthClient fromEnv = new OauthClient();
                fromEnv.ClientId = envId;
                fromEnv.ClientSecret = envSecret;
                return fromEnv;
            }

            string binPath = FindAgyBinary(ctx == null ? null : ctx.Paths);
            if (binPath == null) return null;

            List<string> ids = null;
            List<string> secrets = null;
            try
            {
                if (!File.Exists(binPath)) return null;
                ScanBinary(binPath, out ids, out secrets);
            }
            catch (Exception) { return null; }
            if (ids == null || ids.Count == 0 || secrets == null || secrets.Count == 0) return null;

            string clientId = null;
            foreach (string id in ids)
            {
                if (id.StartsWith("1071", StringComparison.Ordinal)) { clientId = id; break; }
            }
            if (clientId == null) clientId = ids[0];

            OauthClient found = new OauthClient();
            found.ClientId = clientId;
            found.ClientSecret = secrets[0];
            return found;
        }

        // The two markers a client id and a client secret always sit next to, as raw bytes.
        private static readonly byte[] ClientIdSuffix = Encoding.ASCII.GetBytes(".apps.googleusercontent.com");
        private static readonly byte[] SecretPrefix = Encoding.ASCII.GetBytes("GOCSPX-");

        // Reading the whole binary into one string (and running the old regexes over it) is
        // quadratic on a long digit run, so the file is scanned in chunks and only the
        // candidates are kept, never the text.
        private const int ScanChunk = 1024 * 1024;
        // A candidate can straddle a chunk boundary; 256 bytes of overlap keep even a long one
        // (ten or more digits + "-" + the run + the 26-byte suffix) visible in both chunks.
        private const int ScanOverlap = 256;

        // Scans the binary for client ids and secrets:
        //   id     — at every occurrence of ".apps.googleusercontent.com", walk back over the
        //            [a-z0-9]+ run (at least one), the single "-" and the digit run (at least
        //            ten) in front of it: <digits>-[a-z0-9]+.apps.googleusercontent.com.
        //   secret — "GOCSPX-" plus the [A-Za-z0-9_]{20,40} run that directly follows it
        //            (the first 40 bytes when the run is longer).
        // The scan stops as soon as a "1071" id and a secret are both known. Any failure
        // leaves the lists as they are, never an exception.
        private static void ScanBinary(string path, out List<string> ids, out List<string> secrets)
        {
            ids = new List<string>();
            secrets = new List<string>();

            byte[] buf = new byte[ScanChunk + ScanOverlap];
            using (FileStream fs = File.OpenRead(path))
            {
                int carried = 0;   // bytes of the previous chunk repeated at the front of buf
                while (true)
                {
                    int read = 0;
                    while (read < ScanChunk)
                    {
                        int n = fs.Read(buf, carried + read, ScanChunk - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    int limit = carried + read;
                    if (limit == 0) break;

                    // A short fill means the stream is exhausted, so the window ends on the
                    // last byte of the file and a candidate cut off by that end is final.
                    bool atEnd = read < ScanChunk;

                    ScanClientIds(buf, limit, ids);
                    ScanSecrets(buf, limit, atEnd, secrets);

                    // At the end of the file nothing follows the window, so its tail is not
                    // repeated: carrying it on would scan the same bytes twice.
                    if (atEnd) break;
                    if (HasPreferredId(ids) && secrets.Count > 0) break;

                    int carry = limit < ScanOverlap ? limit : ScanOverlap;
                    Array.Copy(buf, limit - carry, buf, 0, carry);
                    carried = carry;
                }
            }
        }

        private static void ScanClientIds(byte[] buf, int limit, List<string> ids)
        {
            int from = 0;
            while (from + ClientIdSuffix.Length <= limit)
            {
                int at = IndexOfBytes(buf, from, limit, ClientIdSuffix);
                if (at < 0) return;

                int run = at - 1;                       // walk back over [a-z0-9]+
                while (run >= 0 && IsLowerAlnum(buf[run])) run--;
                int head = run + 1;                     // first byte of that run
                if (head == at || run < 0 || buf[run] != (byte)'-')
                {
                    from = at + 1;                      // no run, or not "<digits>-"
                    continue;
                }

                int digits = run - 1;                   // walk back over the digit run
                while (digits >= 0 && IsDigit(buf[digits])) digits--;
                if (run - digits - 1 < 10)              // fewer than ten digits: not an id
                {
                    from = at + 1;
                    continue;
                }

                int start = digits + 1;
                ids.Add(Latin1(buf, start, at + ClientIdSuffix.Length - start));
                from = at + 1;
            }
        }

        private static void ScanSecrets(byte[] buf, int limit, bool atEnd, List<string> secrets)
        {
            int from = 0;
            while (from + SecretPrefix.Length <= limit)
            {
                int at = IndexOfBytes(buf, from, limit, SecretPrefix);
                if (at < 0) return;

                int body = at + SecretPrefix.Length;
                int run = 0;                            // [A-Za-z0-9_]{20,40}, capped at 40
                // The binary stores secrets back to back (GOCSPX-<28>GOCSPX-<16>...), and the JS
                // reference splits on the marker, so a run also ends where the next marker begins.
                while (run < 40 && body + run < limit && IsSecretChar(buf[body + run])
                    && !StartsWithAt(buf, body + run, limit, SecretPrefix)) run++;
                // A run the window cut short (not the cap, not the end of the file) is
                // completed by the next chunk, which repeats the last 256 bytes at its
                // front. Recording the short form here would put a truncated candidate
                // first, so it is skipped and picked up whole next time round.
                bool cut = run < 40 && body + run >= limit && !atEnd;
                if (!cut && run >= 20) secrets.Add(Latin1(buf, at, SecretPrefix.Length + run));
                from = at + 1;
            }
        }

        private static bool HasPreferredId(List<string> ids)
        {
            foreach (string id in ids)
                if (id.StartsWith("1071", StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool StartsWithAt(byte[] buf, int at, int limit, byte[] needle)
        {
            if (at + needle.Length > limit) return false;
            for (int j = 0; j < needle.Length; j++)
                if (buf[at + j] != needle[j]) return false;
            return true;
        }
        private static int IndexOfBytes(byte[] buf, int from, int limit, byte[] needle)
        {
            int n = needle.Length;
            int last = limit - n;
            byte first = needle[0];
            for (int i = from; i <= last; i++)
            {
                if (buf[i] != first) continue;
                int j = 1;
                while (j < n && buf[i + j] == needle[j]) j++;
                if (j == n) return i;
            }
            return -1;
        }

        private static bool IsDigit(byte b)
        {
            return b >= (byte)'0' && b <= (byte)'9';
        }

        private static bool IsLowerAlnum(byte b)
        {
            return IsDigit(b) || (b >= (byte)'a' && b <= (byte)'z');
        }

        private static bool IsSecretChar(byte b)
        {
            return (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z')
                || IsDigit(b) || b == (byte)'_';
        }

        // The latin1 mapping the JS reference reads the binary with: byte -> char.
        private static string Latin1(byte[] bytes, int start, int length)
        {
            if (bytes == null || start < 0 || length < 0 || start + length > bytes.Length) return null;
            StringBuilder sb = new StringBuilder(length);
            for (int i = 0; i < length; i++) sb.Append((char)bytes[start + i]);
            return sb.ToString();
        }

        private static string FindAgyBinary(Paths paths)
        {
            if (paths == null) return null;
            string fromPath = paths.FindOnPath("agy");
            if (fromPath != null) return fromPath;

            string[] candidates = paths.AgyBinaryCandidates;
            if (candidates != null)
            {
                foreach (string candidate in candidates)
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    try { if (File.Exists(candidate)) return candidate; }
                    catch (Exception) { /* keep looking */ }
                }
            }
            return null;
        }

        private static Dictionary<string, object> LoadBody(GemCreds creds, Paths paths)
        {
            string ideType = "ANTIGRAVITY";
            if (creds != null && creds.Source == "file")
            {
                bool oauth = paths != null && paths.GeminiOAuth != null && File.Exists(paths.GeminiOAuth);
                bool antigravity = paths != null && paths.AntigravityToken != null && File.Exists(paths.AntigravityToken);
                if (oauth && !antigravity) ideType = "IDE_UNSPECIFIED";
            }

            Dictionary<string, object> metadata = new Dictionary<string, object>();
            metadata["ideType"] = ideType;
            metadata["platform"] = "PLATFORM_UNSPECIFIED";
            metadata["pluginType"] = "GEMINI";

            Dictionary<string, object> body = new Dictionary<string, object>();
            body["metadata"] = metadata;
            return body;
        }

        // ------------------------------------------------------------------------------------------
        // classification / labels
        // ------------------------------------------------------------------------------------------

        private static bool IsGeminiGroup(IDictionary<string, object> group)
        {
            string name = FirstText(group, "displayName", "name", "groupId");
            return name != null && name.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GroupLabel(IDictionary<string, object> group)
        {
            string name = FirstText(group, "displayName", "name", "groupId");
            return name == null ? "" : name;
        }

        private static string PrefixGroupLabel(IDictionary<string, object> group, string label)
        {
            string prefix = GroupLabel(group);
            if (prefix.Length == 0) return label;
            return prefix + " " + label;
        }

        // classifyBucket(): the bucket name (and window field) decide the kind. The reference
        // regex is /five.?hour|5h|FIVE_HOUR/i, which does not match the "5 hour limit" style the
        // API actually returns, so the separator is optional here as well.
        private static readonly Regex FiveHourPattern =
            new Regex(@"five[ _.-]?hour|5[ _.-]?h", RegexOptions.IgnoreCase);
        private static readonly Regex WeekPattern =
            new Regex(@"week", RegexOptions.IgnoreCase);
        private static readonly Regex DayPattern =
            new Regex(@"day|daily|RPD", RegexOptions.IgnoreCase);

        private static void ClassifyBucket(IDictionary<string, object> bucket, out string kind, out string label)
        {
            string name = FirstText(bucket, "displayName", "bucketId");
            string bucketLabel = name == null ? "" : name;
            string window = JsonUtil.Str(bucket, "window", null);
            string joined = bucketLabel + " " + (window == null ? "" : window);

            if (FiveHourPattern.IsMatch(joined)) { kind = Kind.FiveHour; label = "5h"; return; }
            if (WeekPattern.IsMatch(joined)) { kind = Kind.Weekly; label = "Weekly"; return; }
            if (DayPattern.IsMatch(joined)) { kind = Kind.Daily; label = "Daily"; return; }

            kind = Kind.WeeklyScoped;
            label = bucketLabel.Length == 0 ? "Quota" : bucketLabel;
        }

        // shortModel(): "gemini-3-pro" -> "3 Pro", "gemini-flash" -> "Flash", anything unknown
        // keeps (at most) the first 14 characters of its id.
        private static readonly Regex ModelFlashPattern =
            new Regex(@"3\.?8.*flash|3-flash|3\.8", RegexOptions.IgnoreCase);
        private static readonly Regex ModelThreeProPattern =
            new Regex(@"3.*pro", RegexOptions.IgnoreCase);
        private static readonly Regex ModelFlashWordPattern =
            new Regex(@"flash", RegexOptions.IgnoreCase);
        private static readonly Regex ModelProWordPattern =
            new Regex(@"pro", RegexOptions.IgnoreCase);
        private static readonly Regex ModelPrefixPattern =
            new Regex(@"^models/", RegexOptions.None);
        private static readonly Regex GeminiPrefixPattern =
            new Regex(@"^gemini-", RegexOptions.None);

        private static HostResult Done(HostResult r, List<UsageWindow> gemini, List<UsageWindow> others)
        {
            List<UsageWindow> all = new List<UsageWindow>(gemini);
            all.AddRange(others);
            r.Ok = true;
            r.Windows = all;
            return r;
        }

        // "<version>-<family>[-lite]" ids ("gemini-3.8-flash-tiered", "gemini-3.5-flash-lite",
        // "gemini-2.5-pro") become "3.8 Flash", "3.5 Flash Lite", "2.5 Pro"; tier/effort suffixes
        // (-tiered, -low, -agent, ...) are dropped.
        private static readonly Regex VersionFamilyPattern = new Regex(
            @"^(?:models/)?(?:gemini[-_ ])?(?:(\d+(?:\.\d+)?)[-_ ])?(flash|pro|ultra|nano)(?:[-_ ](lite))?(?:$|[-_ ])",
            RegexOptions.IgnoreCase);

        private static string ShortModel(object modelId)
        {
            string s = modelId == null ? "" : Convert.ToString(modelId, CultureInfo.InvariantCulture);
            if (s.Length == 0) return "Model";
            Match vf = VersionFamilyPattern.Match(s.Trim());
            if (vf.Success)
            {
                string family = vf.Groups[2].Value.ToLowerInvariant();
                string text = char.ToUpperInvariant(family[0]) + family.Substring(1);
                if (vf.Groups[3].Success) text += " Lite";
                if (vf.Groups[1].Success) text = vf.Groups[1].Value + " " + text;
                return text;
            }
            if (ModelFlashPattern.IsMatch(s)) return "3 Flash";
            if (ModelThreeProPattern.IsMatch(s)) return "3 Pro";
            if (ModelFlashWordPattern.IsMatch(s)) return "Flash";
            if (ModelProWordPattern.IsMatch(s)) return "Pro";

            string stripped = GeminiPrefixPattern.Replace(ModelPrefixPattern.Replace(s, ""), "");
            return stripped.Length > 14 ? stripped.Substring(0, 14) : stripped;
        }

        private static string ExtractProject(object data)
        {
            IDictionary<string, object> map = JsonUtil.Map(data);
            if (map == null) return null;

            object project = JsonUtil.Get(map, "cloudaicompanionProject");
            string text = project as string;
            if (!string.IsNullOrEmpty(text)) return text;

            IDictionary<string, object> obj = JsonUtil.Map(project);
            if (obj != null) return JsonUtil.Str(obj, "id", null);
            return null;
        }

        // planLabel(): the paid tier's name wins; otherwise the current tier's, where a tier
        // without a name is its id and the free tier is always shown as "Free".
        private static string PlanLabel(object data)
        {
            IDictionary<string, object> map = JsonUtil.Map(data);
            if (map == null) return null;

            string paidName = TierName(JsonUtil.Get(map, "paidTier"));
            if (!string.IsNullOrEmpty(paidName)) return paidName;

            object current = JsonUtil.Get(map, "currentTier");
            string currentId = TierId(current);
            string currentName = TierName(current);
            if (!string.IsNullOrEmpty(currentName))
            {
                if (currentId == "free-tier" || currentName.IndexOf("free", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Free";
                return currentName;
            }
            return null;
        }

        // tierName(): a tier's name is its non-empty `name`, otherwise its non-empty `id`.
        // (JS: `tier.name || tier.id || null`.)
        private static string TierName(object raw)
        {
            string text = raw as string;
            if (text != null) return text.Length == 0 ? null : text;

            IDictionary<string, object> tier = JsonUtil.Map(raw);
            if (tier == null) return null;

            string name = JsonUtil.Str(tier, "name", null);
            if (string.IsNullOrEmpty(name)) name = JsonUtil.Str(tier, "id", null);
            return string.IsNullOrEmpty(name) ? null : name;
        }

        // tierId(): the tier's id — the `id` field of an object tier, and the string itself
        // for a plain string tier (which counts as both name and id). (JS: `tier.id || null`.)
        private static string TierId(object raw)
        {
            string text = raw as string;
            if (text != null) return text.Length == 0 ? null : text;

            IDictionary<string, object> tier = JsonUtil.Map(raw);
            if (tier == null) return null;

            string id = JsonUtil.Str(tier, "id", null);
            return string.IsNullOrEmpty(id) ? null : id;
        }

        // ------------------------------------------------------------------------------------------
        // shared helpers / small types
        // ------------------------------------------------------------------------------------------

        private sealed class HostResult
        {
            public bool Ok;
            public bool AuthFailed;
            public bool Failed;
            public bool HasStatus;
            public int Status;
            public string Plan;
            public List<UsageWindow> Windows;
        }

        private sealed class GemCreds
        {
            public string AccessToken;
            public string RefreshToken;
            public double? ExpiryEpoch;
            public string Source;
        }

        private sealed class OauthClient
        {
            public string ClientId;
            public string ClientSecret;
        }

        private sealed class QuotaEntry
        {
            public double Used;
            public string Name;
            public string Id;
            public string ResetTime;
        }

        // JS Array.prototype.sort is stable; List.Sort is not.
        private static List<QuotaEntry> SortByUsedDesc(List<QuotaEntry> entries)
        {
            List<QuotaEntry> sorted = new List<QuotaEntry>(entries);
            for (int i = 1; i < sorted.Count; i++)
            {
                QuotaEntry moving = sorted[i];
                int j = i - 1;
                while (j >= 0 && sorted[j].Used < moving.Used)
                {
                    sorted[j + 1] = sorted[j];
                    j--;
                }
                sorted[j + 1] = moving;
            }
            return sorted;
        }

        private static string FirstText(IDictionary<string, object> map, params string[] keys)
        {
            if (map == null || keys == null) return null;
            foreach (string key in keys)
            {
                string value = JsonUtil.Str(map, key, null);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return null;
        }

        private static Dictionary<string, object> CopyMap(IDictionary<string, object> source)
        {
            if (source == null) return null;
            Dictionary<string, object> copy = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> kv in source) copy[kv.Key] = kv.Value;
            return copy;
        }

        private static ProviderSnapshot Empty(string state, string message, string hint, DateTime nowUtc)
        {
            ProviderStatus status = new ProviderStatus();
            status.State = state;
            status.Message = message;
            status.Hint = hint;
            return Models.EmptyProvider(AdapterId, DisplayNameText, status, nowUtc);
        }
    }
}
