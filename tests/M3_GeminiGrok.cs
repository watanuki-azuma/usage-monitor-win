using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UsageMonitor.Tests
{
    public class FakeCred : ICredentialReader
    {
        public Dictionary<string, string> Blobs = new Dictionary<string, string>();
        public List<string> Reads = new List<string>();

        public string ReadGenericUtf8(string target)
        {
            Reads.Add(target);
            string v;
            return Blobs.TryGetValue(target, out v) ? v : null;
        }
    }

    public static class GeminiMappingTests
    {
        static readonly DateTime Now = Dates.Utc(2026, 10, 11, 12, 0, 0);
        static object J(string s) { return new JsonHelper().Parse(s); }

        public static void Test_UsedFromFraction()
        {
            Assert.Equal(75.0, GeminiAdapter.UsedFromFraction(0.25), "0.25 remaining");
            Assert.Equal(0.0, GeminiAdapter.UsedFromFraction(1), "1 remaining");
            Assert.Equal(50.0, GeminiAdapter.UsedFromFraction("0.5"), "numeric string");
            Assert.Equal(0.0, GeminiAdapter.UsedFromFraction(1.5), "clamped low");
            Assert.Equal(100.0, GeminiAdapter.UsedFromFraction(-1), "clamped high");
            object[] bad = new object[] { null, "", "  ", "Infinity", true, "x" };
            foreach (object v in bad) Assert.Null(GeminiAdapter.UsedFromFraction(v), "invalid " + (v ?? "null"));
        }

        public static void Test_ExpiryEpoch_accepts_seconds_millis_and_iso()
        {
            Assert.Equal(1800000000.0, GeminiAdapter.ExpiryEpoch("1800000000"), "seconds string");
            Assert.Equal(1800000000.0, GeminiAdapter.ExpiryEpoch("1800000000000"), "millis string");
            Assert.Equal(1800000000.0, GeminiAdapter.ExpiryEpoch(1800000000000L), "millis number");
            Assert.Equal(1800000000.0, GeminiAdapter.ExpiryEpoch(1800000000), "seconds number");
            Assert.Equal(1800000000.0, GeminiAdapter.ExpiryEpoch("2027-01-15T08:00:00Z"), "iso");
            Assert.Null(GeminiAdapter.ExpiryEpoch(null), "null");
            Assert.Null(GeminiAdapter.ExpiryEpoch(""), "empty");
            Assert.Null(GeminiAdapter.ExpiryEpoch("not a date"), "garbage");
        }

        public static void Test_MapSummary_never_shows_another_providers_quota()
        {
            List<UsageWindow> w = GeminiAdapter.MapSummary(J("{\"groups\":[{\"displayName\":\"Claude\",\"buckets\":[{\"remainingFraction\":0}]}]}"), Now);
            Assert.Equal(0, w.Count, "claude group ignored");
        }

        public static void Test_MapSummary_collects_all_gemini_groups_and_classifies_buckets()
        {
            List<UsageWindow> two = GeminiAdapter.MapSummary(J("{\"groups\":[{\"displayName\":\"Gemini Pro\",\"buckets\":[{\"remainingFraction\":0.5,\"window\":\"DAILY\"}]},"
                + "{\"displayName\":\"Gemini Flash\",\"buckets\":[{\"remainingFraction\":0.5,\"window\":\"DAILY\"}]}]}"), Now);
            Assert.Equal(2, two.Count, "two groups");
            Assert.Equal("Gemini Pro Daily", two[0].Label, "label includes group when several groups");
            Assert.Equal(Kind.Daily, two[0].Kind, "kind daily");
            Assert.Equal(50.0, two[0].UsedPct, "used");

            List<UsageWindow> one = GeminiAdapter.MapSummary(J("{\"groups\":[{\"displayName\":\"Gemini\",\"buckets\":["
                + "{\"displayName\":\"5 hour limit\",\"remainingFraction\":0.9,\"resetTime\":\"2026-10-11T15:00:00Z\"},"
                + "{\"displayName\":\"Weekly\",\"remainingFraction\":0.4},"
                + "{\"bucketId\":\"mystery\",\"remainingFraction\":0.1},"
                + "{\"displayName\":\"skip me\"}]}]}"), Now);
            Assert.Equal(3, one.Count, "bucket without fraction skipped");
            Assert.Equal(Kind.FiveHour, one[0].Kind, "five hour");
            Assert.Equal("5h", one[0].Label, "5h label");
            Assert.Equal(Dates.Utc(2026, 10, 11, 15, 0, 0), one[0].ResetsAt, "reset time");
            Assert.Equal(Kind.Weekly, one[1].Kind, "weekly");
            Assert.Equal("Weekly", one[1].Label, "weekly label");
            Assert.Equal(60.0, one[1].UsedPct, "weekly used");
            Assert.Equal(Kind.WeeklyScoped, one[2].Kind, "unknown bucket");
            Assert.Equal("mystery", one[2].Label, "falls back to bucket id");
        }

        public static void Test_MapModels_filters_to_gemini_and_uses_keyed_ids()
        {
            List<UsageWindow> w = GeminiAdapter.MapModels(J("{\"models\":{\"claude-opus\":{\"quotaInfo\":{\"remainingFraction\":0}},"
                + "\"gemini-pro\":{\"displayName\":\"Pro\",\"quotaInfo\":{\"remainingFraction\":0.25}}}}"), Now);
            Assert.Equal(1, w.Count, "only gemini");
            Assert.Equal(75.0, w[0].UsedPct, "used");
            Assert.Equal(Kind.Quota, w[0].Kind, "kind quota");
            Assert.Equal("Pro", w[0].Label, "short label");
        }
    }

    public static class GeminiAdapterTests
    {
        const string Host1 = "https://daily-cloudcode-pa.googleapis.com";

        static string Blob(string access, string refresh, string expiry)
        {
            return "{\"token\":{\"access_token\":\"" + access + "\",\"refresh_token\":\"" + refresh + "\",\"expiry\":\"" + expiry + "\"}}";
        }

        const string LoadOk = "{\"cloudaicompanionProject\":\"proj-1\",\"paidTier\":{\"name\":\"Google AI Pro\"}}";
        const string Summary = "{\"groups\":[{\"displayName\":\"Gemini\",\"buckets\":["
            + "{\"displayName\":\"5 hour\",\"remainingFraction\":0.8,\"resetTime\":\"2026-10-11T15:00:00Z\"},"
            + "{\"displayName\":\"Weekly\",\"remainingFraction\":0.5,\"resetTime\":\"2026-10-17T00:00:00Z\"}]}]}";

        static void FakeAgy(AdapterEnv e)
        {
            // fake values; literals are split so secret scanners do not flag them
            e.Write(@"AppData\Local\agy\bin\agy.exe", "junk 1071234567890-abcdefg.apps.google" + "usercontent.com junk GOCS" + "PX-AbCdEfGhIjKlMnOpQrStUvWx more junk");
        }

        public static void Test_not_installed_and_logged_out()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Credentials = new FakeCred();
                ProviderSnapshot a = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.Equal(State.NotInstalled, a.Status.State, "not installed");
                ProviderSnapshot b = new GeminiAdapter().FetchUsage(e.Context(true, "agy"));
                Assert.Equal(State.LoggedOut, b.Status.State, "logged out");
                Assert.Equal("Run: agy  (sign in with Google)", b.Status.Hint, "hint (two spaces)");
                Assert.Equal(0, e.Http.Requests.Count, "no requests");
            }
        }

        public static void Test_happy_path_with_credential_manager_blob()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("AT", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, LoadOk);
                    if (r.Url.EndsWith(":retrieveUserQuotaSummary")) return FakeHttp.Reply(200, Summary);
                    return FakeHttp.Reply(404, "");
                };
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.Equal(State.Ok, p.Status.State, "state");
                Assert.Equal("Gemini", p.DisplayName, "name");
                Assert.Equal("Google AI Pro", p.Plan, "plan");
                Assert.Equal(2, p.Windows.Count, "windows");
                Assert.Equal(20.0, p.Windows[0].UsedPct, "5h used");
                Assert.Equal(50.0, p.Windows[1].UsedPct, "weekly used");
                Assert.Contains(cred.Reads[0], "gemini:antigravity", "credential target");
                Assert.Equal(2, e.Http.Requests.Count, "load + summary");
                Assert.Equal(Host1 + "/v1internal:loadCodeAssist", e.Http.Requests[0].Url, "load url");
                Assert.Contains(e.Http.Requests[0].Body, "\"ideType\":\"ANTIGRAVITY\"", "load body metadata");
                Assert.Equal("Bearer AT", e.Http.Requests[0].Header("Authorization"), "authorization");
                Assert.Equal("antigravity/windows/amd64", e.Http.Requests[0].Header("User-Agent"), "user agent");
                Assert.Contains(e.Http.Requests[1].Body, "\"project\":\"proj-1\"", "summary body carries project");
            }
        }

        public static void Test_falls_back_to_quota_then_models_endpoint()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("AT", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, LoadOk);
                    if (r.Url.EndsWith(":retrieveUserQuotaSummary")) return FakeHttp.Reply(200, "{\"groups\":[]}");
                    if (r.Url.EndsWith(":retrieveUserQuota")) return FakeHttp.Reply(200, "{\"buckets\":[{\"modelId\":\"gemini-3-pro\",\"remainingFraction\":0.3,\"resetTime\":\"2026-10-12T00:00:00Z\"},{\"modelId\":\"gemini-flash\",\"remainingFraction\":0.9}]}");
                    return FakeHttp.Reply(404, "");
                };
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.Equal(State.Ok, p.Status.State, "state");
                Assert.Equal(1, p.Windows.Count, "one shared-pool row");
                Assert.Equal(Kind.FiveHour, p.Windows[0].Kind, "five_hour");
                Assert.Equal("5h", p.Windows[0].Label, "5h label");
                Assert.Equal(70.0, p.Windows[0].UsedPct, "highest used wins");
            }
        }

        public static void Test_rate_limit_on_quota_stops_immediately()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("AT", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, LoadOk);
                    return FakeHttp.Reply(429, "");
                };
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.True(p.RateLimited, "rate limited");
                Assert.Equal(State.FetchFailed, p.Status.State, "state");
                Assert.Equal(2, e.Http.Requests.Count, "no further endpoints or hosts are tried after a 429");
            }
        }

        public static void Test_401_triggers_refresh_with_client_extracted_from_agy_binary()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeAgy(e);
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("OLD", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("oauth2.googleapis.com/token")) return FakeHttp.Reply(200, "{\"access_token\":\"NEW\",\"expires_in\":3600}");
                    string auth = r.Header("Authorization");
                    if (auth != "Bearer NEW") return FakeHttp.Reply(401, "");
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, LoadOk);
                    return FakeHttp.Reply(200, Summary);
                };
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.Equal(State.Ok, p.Status.State, "ok after refresh");
                RecordedRequest tok = null;
                foreach (RecordedRequest r in e.Http.Requests) if (r.Url.Contains("oauth2.googleapis.com/token")) tok = r;
                Assert.NotNull(tok, "token endpoint called");
                Assert.Equal("POST", tok.Method, "post");
                Assert.Contains(tok.Body, "client_id=1071234567890-abcdefg.apps.google" + "usercontent.com", "client id extracted from binary");
                Assert.Contains(tok.Body, "client_secret=GOCS" + "PX-AbCdEfGhIjKlMnOpQrStUvWx", "client secret extracted from binary");
                Assert.Contains(tok.Body, "refresh_token=RT", "refresh token");
                Assert.Contains(tok.Body, "grant_type=refresh_token", "grant type");
            }
        }

        public static void Test_expired_access_token_is_refreshed_before_use_and_failure_means_logged_out()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeAgy(e);
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("OLD", "RT", "2000-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("oauth2.googleapis.com/token")) return FakeHttp.Reply(200, "{\"access_token\":\"NEW\",\"expires_in\":3600}");
                    return FakeHttp.Reply(401, "");
                };
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.Equal(State.LoggedOut, p.Status.State, "401 even with fresh token -> logged out");
                Assert.Equal("Run: agy  (token expired)", p.Status.Hint, "hint");
                Assert.Equal("POST", e.Http.Requests[0].Method, "refresh happened first");
                Assert.Contains(e.Http.Requests[1].Header("Authorization"), "NEW", "uses refreshed token");
            }
        }

        public static void Test_refresh_disabled_by_config()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeAgy(e);
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("OLD", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Config.SetRefreshTokens("gemini", false);
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(401, ""); };
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.Equal(State.LoggedOut, p.Status.State, "logged out");
                foreach (RecordedRequest r in e.Http.Requests) Assert.True(!r.Url.Contains("oauth2.googleapis.com"), "token endpoint not called");
            }
        }
    }

    public static class GrokTests
    {
        static readonly DateTime Now = Dates.Utc(2026, 10, 11, 12, 0, 0);
        static object J(string s) { return new JsonHelper().Parse(s); }

        static byte[] Hex(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        static HttpResult Binary(int status, byte[] bytes)
        {
            HttpResult h = new HttpResult();
            h.Status = status;
            h.Raw = bytes;
            h.Text = "";
            h.Headers = new Dictionary<string, string>();
            return h;
        }

        public static void Test_monthly_fallback_is_not_labelled_weekly()
        {
            ProviderSnapshot p = GrokAdapter.MapBilling(J("{\"used\":{\"val\":25},\"monthlyLimit\":{\"val\":100},\"productUsage\":[null,{}]}"), Now);
            Assert.Equal(Kind.Monthly, p.Windows[0].Kind, "monthly");
            Assert.Equal("Monthly", p.Windows[0].Label, "label");
            Assert.Equal(25.0, p.Windows[0].UsedPct, "used");
            Assert.Equal(0, GrokAdapter.MapBilling(J("{\"used\":{\"val\":25},\"monthlyLimit\":{\"val\":\"0\"}}"), Now).Windows.Count, "zero limit -> no window");
        }

        public static void Test_omitted_proto3_percent_is_zero_while_period_is_live()
        {
            ProviderSnapshot p = GrokAdapter.MapBilling(J("{\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"start\":\"2020-01-01T00:00:00Z\",\"end\":\"2099-01-01T00:00:00Z\"},\"isUnifiedBillingUser\":true}"), Now);
            Assert.Equal(Kind.Weekly, p.Windows[0].Kind, "weekly");
            Assert.Equal(0.0, p.Windows[0].UsedPct, "0 percent");
            Assert.Equal(Dates.Utc(2099, 1, 1, 0, 0, 0), p.Windows[0].ResetsAt, "period end");
            ProviderSnapshot ended = GrokAdapter.MapBilling(J("{\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"start\":\"2020-01-01T00:00:00Z\",\"end\":\"2021-01-01T00:00:00Z\"}}"), Now);
            Assert.Equal(0, ended.Windows.Count, "ended period -> nothing invented");
        }

        public static void Test_product_usage_is_summed_and_explained_in_footnotes()
        {
            ProviderSnapshot p = GrokAdapter.MapBilling(J("{\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"end\":\"2099-01-01T00:00:00Z\"},"
                + "\"productUsage\":[{\"product\":\"GrokBuild\",\"usagePercent\":8},{\"product\":\"GrokChat\",\"usagePercent\":2}]}"), Now);
            Assert.Equal(Kind.Weekly, p.Windows[0].Kind, "weekly");
            Assert.Equal(10.0, p.Windows[0].UsedPct, "sum");
            Assert.Equal(2, p.Footnotes.Count, "footnotes");
            Assert.Equal("Build 8% of pool", p.Footnotes[0], "footnote 0");
            Assert.Equal("Chat 2% of pool", p.Footnotes[1], "footnote 1");
            Assert.Equal("SuperGrok", p.Plan, "plan");
            Assert.Equal("Grok Build", p.DisplayName, "name");
        }

        public static void Test_credit_windows()
        {
            ProviderSnapshot capped = GrokAdapter.MapBilling(J("{\"creditUsagePercent\":34.5,\"onDemandCap\":{\"val\":50},\"onDemandUsed\":{\"val\":10}}"), Now);
            Assert.Equal(34.5, capped.Windows[0].UsedPct, "explicit percent");
            Assert.Equal(Kind.Credits, capped.Windows[1].Kind, "credits window");
            Assert.Equal(20.0, capped.Windows[1].UsedPct, "10 of 50");
            ProviderSnapshot prepaid = GrokAdapter.MapBilling(J("{\"creditUsagePercent\":1,\"prepaidBalance\":{\"val\":12.5}}"), Now);
            Assert.Equal("Credits $12.50", prepaid.Windows[1].Label, "prepaid label");
            Assert.Null(prepaid.Windows[1].UsedPct, "no percentage");
        }

        public static void Test_ParseCreditsGrpcWeb()
        {
            Assert.Equal(34.0, GrokAdapter.ParseCreditsGrpcWeb(Hex("00000000070a050d00000842")), "float32 percent");
            Assert.Null(GrokAdapter.ParseCreditsGrpcWeb(new byte[0]), "empty");
            Assert.Null(GrokAdapter.ParseCreditsGrpcWeb(Hex("00000000")), "truncated header");
            Assert.Null(GrokAdapter.ParseCreditsGrpcWeb(Hex("800000000f677270632d7374617475733a2030")), "trailer-only frame");
        }

        static string Accounts(string extra)
        {
            return "{\"old\":{\"key\":\"old-token\",\"create_time\":\"2024-01-01\"},"
                + "\"current\":{\"key\":\"current-token\",\"create_time\":\"2025-01-01\"" + extra + "}}";
        }

        public static void Test_not_installed_logged_out_and_api_key()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                Assert.Equal(State.NotInstalled, new GrokAdapter().FetchUsage(e.Context(false, "grok")).Status.State, "not installed");
                Assert.Equal(State.LoggedOut, new GrokAdapter().FetchUsage(e.Context(true, "grok")).Status.State, "logged out");
                e.Write(@".grok\auth.json", "{\"apiKey\":{\"foo\":\"bar\"}}");
                Assert.Equal(State.Unknown, new GrokAdapter().FetchUsage(e.Context(true, "grok")).Status.State, "no account entry with key -> unknown");
            }
        }

        public static void Test_refresh_updates_only_the_selected_account_and_runs_once()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                string path = e.Write(@".grok\auth.json", Accounts(",\"expires_at\":\"2020-01-01\",\"refresh_token\":\"rt\",\"oidc_client_id\":\"cid\""));
                int refreshes = 0;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("openid-configuration")) return FakeHttp.Reply(404, "");
                    if (r.Method == "POST")
                    {
                        refreshes++;
                        return FakeHttp.Reply(200, "{\"access_token\":\"fresh\",\"refresh_token\":\"rotated\"}");
                    }
                    return FakeHttp.Reply(401, "");
                };
                ProviderSnapshot p = new GrokAdapter().FetchUsage(e.Context(false, "grok"));
                Assert.Equal(State.LoggedOut, p.Status.State, "401 after refresh -> logged out");
                Assert.Equal(1, refreshes, "refreshed exactly once");
                string saved = File.ReadAllText(path);
                Assert.Contains(saved, "\"key\": \"old-token\"", "other account untouched");
                Assert.Contains(saved, "\"key\": \"fresh\"", "selected account updated");
                Assert.Contains(saved, "\"refresh_token\": \"rotated\"", "rotated refresh token stored");
                RecordedRequest post = null;
                foreach (RecordedRequest r in e.Http.Requests) if (r.Method == "POST") post = r;
                Assert.Contains(post.Url, "auth.x.ai/oauth/token", "default token endpoint");
                Assert.Contains(post.Body, "client_id=cid", "client id");
                Assert.Contains(post.Body, "grant_type=refresh_token", "grant");
            }
        }

        public static void Test_fills_omitted_percent_from_grpc_web_credits()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".grok\auth.json", "{\"current\":{\"key\":\"tok\",\"create_time\":\"2025-01-01\"}}");
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("cli-chat-proxy.grok.com/v1/billing"))
                        return FakeHttp.Reply(200, "{\"config\":{\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"start\":\"2020-01-01T00:00:00Z\",\"end\":\"2099-01-01T00:00:00Z\"}}}");
                    if (r.Url.Contains("GetGrokCreditsConfig")) return Binary(200, Hex("00000000070a050d00000842"));
                    return FakeHttp.Reply(404, "");
                };
                ProviderSnapshot p = new GrokAdapter().FetchUsage(e.Context(false, "grok"));
                Assert.Equal(State.Ok, p.Status.State, "ok");
                Assert.Equal(Kind.Weekly, p.Windows[0].Kind, "weekly");
                Assert.Equal(34.0, p.Windows[0].UsedPct, "percent from grpc");
                RecordedRequest billing = e.Http.Requests[0];
                Assert.Equal("Bearer tok", billing.Header("Authorization"), "authorization");
                Assert.Equal("xai-grok-cli", billing.Header("X-XAI-Token-Auth"), "token auth header");
            }
        }

        public static void Test_rate_limit_and_http_errors()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".grok\auth.json", "{\"current\":{\"key\":\"tok\",\"create_time\":\"2025-01-01\"}}");
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(429, ""); };
                ProviderSnapshot a = new GrokAdapter().FetchUsage(e.Context(false, "grok"));
                Assert.True(a.RateLimited, "429 flagged");
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(500, ""); };
                ProviderSnapshot b = new GrokAdapter().FetchUsage(e.Context(false, "grok"));
                Assert.Equal("HTTP 500", b.Status.Message, "status message");
            }
        }
    }
}
