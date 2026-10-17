using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UsageMonitor.Tests
{
    // Regression tests for the independent review of M3 (Gemini/Grok). Written by the project
    // owner, not by the model that implements the fixes.
    public static class M3ReviewTests
    {
        static byte[] Hex(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        static string Blob(string access, string refresh, string expiry)
        {
            return "{\"token\":{\"access_token\":\"" + access + "\",\"refresh_token\":\"" + refresh + "\",\"expiry\":\"" + expiry + "\"}}";
        }

        const string Summary = "{\"groups\":[{\"displayName\":\"Gemini\",\"buckets\":["
            + "{\"displayName\":\"5 hour\",\"remainingFraction\":0.8,\"resetTime\":\"2026-10-11T15:00:00Z\"}]}]}";

        // ---- Grok: credential file safety -------------------------------------------------

        public static void Test_grok_refresh_does_not_overwrite_an_account_the_cli_replaced_meanwhile()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                string body = "{\"current\":{\"key\":\"stale-token\",\"create_time\":\"2025-01-01\",\"expires_at\":\"2020-01-01\",\"refresh_token\":\"rt\",\"oidc_client_id\":\"cid\"}}";
                string path = e.Write(@".grok\auth.json", body);
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("openid-configuration")) return FakeHttp.Reply(404, "");
                    if (r.Method == "POST")
                    {
                        // the CLI logs in again while our refresh request is in flight; the file
                        // keeps its old timestamp (coarse clocks), only the account key differs
                        File.WriteAllText(path, body.Replace("stale-token", "cli-new-token"));
                        File.SetLastWriteTimeUtc(path, stamp);
                        return FakeHttp.Reply(200, "{\"access_token\":\"fresh\",\"refresh_token\":\"rotated\"}");
                    }
                    return FakeHttp.Reply(401, "");
                };
                new GrokAdapter().FetchUsage(e.Context(false, "grok"));
                string saved = File.ReadAllText(path);
                Assert.Contains(saved, "cli-new-token", "the CLI's new login survives");
                Assert.True(!saved.Contains("\"key\": \"fresh\""), "our refresh result is not written over it");
            }
        }

        public static void Test_grok_refresh_leaves_no_temp_file_and_a_valid_json_file()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                string path = e.Write(@".grok\auth.json", "{\"current\":{\"key\":\"tok\",\"create_time\":\"2025-01-01\",\"expires_at\":\"2020-01-01\",\"refresh_token\":\"rt\",\"oidc_client_id\":\"cid\",\"user_id\":\"u1\"}}");
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("openid-configuration")) return FakeHttp.Reply(404, "");
                    if (r.Method == "POST") return FakeHttp.Reply(200, "{\"access_token\":\"fresh\",\"expires_in\":3600}");
                    return FakeHttp.Reply(401, "");
                };
                new GrokAdapter().FetchUsage(e.Context(false, "grok"));
                Assert.True(File.Exists(path), "auth.json still exists");
                Assert.True(!File.Exists(path + ".tmp"), "no temp file left behind");
                string saved = File.ReadAllText(path);
                Assert.Contains(saved, "\"key\": \"fresh\"", "token written");
                Assert.Contains(saved, "\"user_id\": \"u1\"", "unrelated fields kept");
                Assert.True(saved.Length > 0 && saved[0] == '{', "no BOM, plain JSON");
            }
        }

        public static void Test_grok_account_with_empty_key_is_not_selected()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".grok\auth.json", "{\"old\":{\"key\":\"old-token\",\"create_time\":\"2024-01-01\"},\"newer\":{\"key\":\"\",\"create_time\":\"2025-01-01\"}}");
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(401, ""); };
                new GrokAdapter().FetchUsage(e.Context(false, "grok"));
                Assert.True(e.Http.Requests.Count > 0, "a request was made");
                Assert.Equal("Bearer old-token", e.Http.Requests[0].Header("Authorization"), "falls back to the account that has a key");
            }
        }

        public static void Test_grpc_unused_wide_varint_fields_do_not_discard_the_percent()
        {
            // an unrelated varint field (field 2, 10-byte int64) in front of the nested message
            Assert.Equal(34.0, GrokAdapter.ParseCreditsGrpcWeb(Hex("0000000012" + "10FFFFFFFFFFFFFFFFFF01" + "0a05" + "0d00000842")), "10-byte varint is skipped");
            // a malformed (never terminated) varint still fails cleanly
            Assert.Null(GrokAdapter.ParseCreditsGrpcWeb(Hex("000000000c" + "10FFFFFFFFFFFFFFFFFFFF" + "00")), "11+ byte varint");
        }
        public static void Test_grpc_length_varint_overflow_is_rejected()
        {
            // nested length 0x100000005 would wrap to 5 when truncated to 32 bits
            Assert.Null(GrokAdapter.ParseCreditsGrpcWeb(Hex("000000000b0a85808080100d00000842")), "overflowing length");
            Assert.Equal(34.0, GrokAdapter.ParseCreditsGrpcWeb(Hex("00000000070a050d00000842")), "well-formed frame still parses");
        }

        // ---- Gemini ------------------------------------------------------------------------

        public static void Test_gemini_plan_label_falls_back_to_the_tier_id()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("AT", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, "{\"cloudaicompanionProject\":\"p\",\"currentTier\":{\"id\":\"free-tier\"}}");
                    return FakeHttp.Reply(200, Summary);
                };
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.Equal("Free", p.Plan, "free-tier id without a name");
            }
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("AT", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, "{\"cloudaicompanionProject\":\"p\",\"paidTier\":{\"id\":\"g1-pro-tier\"},\"currentTier\":{\"id\":\"free-tier\"}}");
                    return FakeHttp.Reply(200, Summary);
                };
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.Equal("g1-pro-tier", p.Plan, "paid tier id is used when it has no name");
            }
        }

        public static void Test_gemini_quota_fallback_ignores_other_providers_models()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("AT", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, "{\"cloudaicompanionProject\":\"p\"}");
                    if (r.Url.EndsWith(":retrieveUserQuotaSummary")) return FakeHttp.Reply(200, "{\"groups\":[]}");
                    if (r.Url.EndsWith(":retrieveUserQuota"))
                        return FakeHttp.Reply(200, "{\"buckets\":[{\"modelId\":\"claude-sonnet-5\",\"remainingFraction\":0},{\"modelId\":\"gemini-3-pro\",\"remainingFraction\":0.8}]}");
                    return FakeHttp.Reply(404, "");
                };
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                Assert.Equal(State.Ok, p.Status.State, "state");
                Assert.Equal(1, p.Windows.Count, "only the Gemini bucket");
                Assert.Equal("5h", p.Windows[0].Label, "label");
                Assert.Equal(20.0, p.Windows[0].UsedPct, "used from the Gemini bucket, not the exhausted Claude one");
            }
        }

        public static void Test_gemini_secret_stops_at_the_next_GOCSPX_marker()
        {
            // the real agy binary stores secrets back to back: GOCSPX-<28 chars>GOCSPX-<16 chars>-...
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@"AppData\Local\agy\bin\agy.exe",
                    // fake values; literals are split so secret scanners do not flag them
                    "junk 1071234567890-abcdefg.apps.google" + "usercontent.com GOCS" + "PX-AbCdEfGhIjKlMnOpQrStUvWxYzABGOCS" + "PX-ZZZZZZZZZZZZZZZZ-tail");
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("OLD", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("oauth2.googleapis.com/token")) return FakeHttp.Reply(200, "{\"access_token\":\"NEW\",\"expires_in\":3600}");
                    if (r.Header("Authorization") != "Bearer NEW") return FakeHttp.Reply(401, "");
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, "{\"cloudaicompanionProject\":\"p\"}");
                    return FakeHttp.Reply(200, Summary);
                };
                new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                RecordedRequest tok = null;
                foreach (RecordedRequest r in e.Http.Requests) if (r.Url.Contains("oauth2.googleapis.com/token")) tok = r;
                Assert.NotNull(tok, "token endpoint called");
                Assert.Contains(tok.Body, "client_secret=GOCS" + "PX-AbCdEfGhIjKlMnOpQrStUvWxYzAB&", "secret ends where the next marker starts");
            }
        }
        public static void Test_gemini_client_extraction_survives_long_digit_runs_quickly()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                string junk = new string('7', 120000);
                e.Write(@"AppData\Local\agy\bin\agy.exe", junk + " 1071234567890-abcdefg.apps.google" + "usercontent.com junk GOCS" + "PX-AbCdEfGhIjKlMnOpQrStUvWx tail");
                FakeCred cred = new FakeCred();
                cred.Blobs["gemini:antigravity"] = Blob("OLD", "RT", "2099-01-01T00:00:00Z");
                e.Credentials = cred;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("oauth2.googleapis.com/token")) return FakeHttp.Reply(200, "{\"access_token\":\"NEW\",\"expires_in\":3600}");
                    if (r.Header("Authorization") != "Bearer NEW") return FakeHttp.Reply(401, "");
                    if (r.Url.EndsWith(":loadCodeAssist")) return FakeHttp.Reply(200, "{\"cloudaicompanionProject\":\"p\"}");
                    return FakeHttp.Reply(200, Summary);
                };
                Stopwatch sw = Stopwatch.StartNew();
                ProviderSnapshot p = new GeminiAdapter().FetchUsage(e.Context(false, "agy"));
                sw.Stop();
                Assert.Equal(State.Ok, p.Status.State, "ok after refresh");
                Assert.True(sw.ElapsedMilliseconds < 3000, "extraction took " + sw.ElapsedMilliseconds + " ms");
                RecordedRequest tok = null;
                foreach (RecordedRequest r in e.Http.Requests) if (r.Url.Contains("oauth2.googleapis.com/token")) tok = r;
                Assert.NotNull(tok, "token endpoint called");
                Assert.Contains(tok.Body, "client_id=1071234567890-abcdefg.apps.google" + "usercontent.com", "client id");
            }
        }
    }
}
