using System;
using System.Collections.Generic;

namespace UsageMonitor.Tests
{
    // AGY (Antigravity) shapes seen live on 2026-10-16: the quota summary only carries the
    // third-party "Claude and GPT models" group, the per-model quota lists new Gemini ids.
    public static class AgyQuotaTests
    {
        static string Blob()
        {
            return "{\"token\":{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expiry\":\"2099-01-01T00:00:00Z\"}}";
        }

        const string Load = "{\"cloudaicompanionProject\":\"p\",\"paidTier\":{\"id\":\"g1-pro-tier\",\"name\":\"Google AI Pro\"}}";
        const string ThirdPartyGroup = "{\"displayName\":\"Claude and GPT models\",\"buckets\":["
            + "{\"bucketId\":\"3p-weekly\",\"displayName\":\"Weekly Limit Remaining\",\"window\":\"weekly\",\"resetTime\":\"2026-10-17T13:30:20Z\",\"remainingFraction\":0.75},"
            + "{\"bucketId\":\"3p-5h\",\"displayName\":\"Five Hour Limit Remaining\",\"window\":\"5h\",\"resetTime\":\"2026-10-16T16:31:36Z\",\"remainingFraction\":1}]}";
        const string Quota = "{\"buckets\":["
            + "{\"modelId\":\"chat_20706\",\"remainingFraction\":1},"
            + "{\"modelId\":\"claude-opus-5-5-high\",\"remainingFraction\":0,\"resetTime\":\"2026-10-16T16:31:36Z\"},"
            + "{\"modelId\":\"gemini-3.8-flash-tiered\",\"remainingFraction\":1,\"resetTime\":\"2026-10-16T16:31:36Z\"},"
            + "{\"modelId\":\"gemini-3.8-flash-low\",\"remainingFraction\":1,\"resetTime\":\"2026-10-16T16:31:36Z\"},"
            + "{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":0.6,\"resetTime\":\"2026-10-16T16:31:36Z\"},"
            + "{\"modelId\":\"tab_flash_lite_preview\",\"remainingFraction\":0}]}";

        static ProviderSnapshot Fetch(string summary, List<string> calls)
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob();
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (calls != null) calls.Add(r.Url.Substring(r.Url.LastIndexOf(':') + 1));
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, Load);
                    if (r.Url.EndsWith(":retrieveUserQuotaSummary")) return FakeHttp.Reply(200, summary);
                    if (r.Url.EndsWith(":retrieveUserQuota")) return FakeHttp.Reply(200, Quota);
                    return FakeHttp.Reply(404, "");
                };
                return new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
            }
        }

        static UsageWindow Find(ProviderSnapshot p, string label)
        {
            foreach (UsageWindow w in p.Windows) if (w.Label == label) return w;
            return null;
        }

        public static void Test_third_party_group_is_shown_as_its_own_rows_next_to_gemini()
        {
            ProviderSnapshot p = Fetch("{\"groups\":[" + ThirdPartyGroup + "]}", null);
            Assert.Equal(State.Ok, p.Status.State, "state");
            Assert.Equal("Google AI Pro", p.Plan, "plan");

            // One Gemini row first: the shared 5-hour pool, taken from the highest-use bucket
            Assert.Equal("5h", p.Windows[0].Label, "single gemini 5h row");
            Assert.Equal(Kind.FiveHour, p.Windows[0].Kind, "gemini pool is five_hour");
            Assert.Equal(40.0, p.Windows[0].UsedPct, "gemini used");
            Assert.Equal(Dates.Utc(2026, 10, 16, 16, 31, 36), p.Windows[0].ResetsAt, "gemini reset");

            UsageWindow five = Find(p, "Claude/GPT 5h");
            UsageWindow week = Find(p, "Claude/GPT Wk");
            Assert.NotNull(five, "third-party 5h row");
            Assert.NotNull(week, "third-party weekly row");
            Assert.Equal(Kind.Quota, five.Kind, "not reported as Gemini's own five_hour");
            Assert.Equal(Kind.Quota, week.Kind, "not reported as Gemini's own weekly");
            Assert.Equal(0.0, five.UsedPct, "5h used");
            Assert.Equal(25.0, week.UsedPct, "weekly used");
            Assert.Equal(Dates.Utc(2026, 10, 17, 13, 30, 20), week.ResetsAt, "weekly reset");
            Assert.Equal(3, p.Windows.Count, "one Gemini row + two Claude/GPT rows");

            foreach (UsageWindow w in p.Windows)
            {
                Assert.True(w.Label.IndexOf("opus", StringComparison.OrdinalIgnoreCase) < 0, "per-model Claude bucket not shown as a Gemini row");
                Assert.True(w.Label.IndexOf("tab", StringComparison.OrdinalIgnoreCase) < 0, "tab-completion bucket not shown");
            }
        }

        public static void Test_gemini_summary_group_still_wins_and_skips_the_fallback()
        {
            List<string> calls = new List<string>();
            string gemini = "{\"displayName\":\"Gemini models\",\"buckets\":["
                + "{\"displayName\":\"5 hour\",\"remainingFraction\":0.8,\"resetTime\":\"2026-10-16T15:00:00Z\"},"
                + "{\"displayName\":\"Weekly\",\"remainingFraction\":0.5,\"resetTime\":\"2026-10-20T00:00:00Z\"}]}";
            ProviderSnapshot p = Fetch("{\"groups\":[" + gemini + "," + ThirdPartyGroup + "]}", calls);
            Assert.Equal(State.Ok, p.Status.State, "state");
            Assert.Equal(Kind.FiveHour, p.Windows[0].Kind, "gemini 5h stays five_hour");
            Assert.Equal(20.0, p.Windows[0].UsedPct, "gemini 5h");
            Assert.Equal(Kind.Weekly, p.Windows[1].Kind, "gemini weekly stays weekly");
            Assert.NotNull(Find(p, "Claude/GPT Wk"), "third-party rows appended");
            Assert.True(!calls.Contains("retrieveUserQuota"), "no per-model fallback when the summary has Gemini rows");
        }

        public static void Test_new_model_ids_get_readable_short_labels()
        {
            List<UsageWindow> w = GeminiAdapter.MapModels(new JsonHelper().Parse("{\"models\":["
                + "{\"model\":\"gemini-3.5-flash-lite\",\"quotaInfo\":{\"remainingFraction\":0.1}},"
                + "{\"model\":\"gemini-3.8-flash-tiered\",\"quotaInfo\":{\"remainingFraction\":0.2}}]}"), Dates.Utc(2026, 10, 16, 0, 0, 0));
            Assert.Equal("3.5 Flash Lite", w[0].Label, "lite variant");
            Assert.Equal("3.8 Flash", w[1].Label, "tier suffix dropped");
        }
    }
}
