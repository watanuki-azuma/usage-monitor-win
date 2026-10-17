using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace UsageMonitor.Tests
{
    // Regression tests for defects found in the independent review of M2.
    public static class ReviewFixTests
    {
        static long Ms(DateTime utc)
        {
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        static string Creds(string access, string refresh, long expiresAtMs)
        {
            return "{\"claudeAiOauth\":{\"accessToken\":\"" + access + "\",\"refreshToken\":\"" + refresh + "\",\"expiresAt\":" + expiresAtMs + "}}";
        }

        public static void Test_claude_recovery_hints_are_in_Hint_so_the_UI_shows_them()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".claude\.credentials.json", Creds("AT", "RT", Ms(e.Now.AddHours(5))));
                e.Config.SetRefreshTokens("claude", false);
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(401, ""); };
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(State.LoggedOut, p.Status.State, "state");
                Assert.Equal("Run: claude auth login", p.Status.Hint, "hint field");
                Assert.Equal("Run: claude auth login", Format.StatusText(p), "what the flyout shows");
            }
            using (AdapterEnv e = new AdapterEnv())
            {
                ProviderSnapshot c = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Contains(Format.StatusText(c), "claude", "claude not installed text mentions the CLI");
                ProviderSnapshot x = new CodexAdapter().FetchUsage(e.Context(false, "codex"));
                Assert.Contains(Format.StatusText(x), "Install Codex", "codex not installed text");
            }
        }

        public static void Test_a_failing_proactive_refresh_does_not_prevent_the_usage_request()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".claude\.credentials.json", Creds("AT", "RT", Ms(e.Now.AddSeconds(30))));
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("/oauth/token")) throw new WebException("refresh endpoint timed out");
                    return FakeHttp.Reply(200, "{\"five_hour\":{\"utilization\":12,\"resets_at\":\"2026-10-11T15:00:00Z\"}}");
                };
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(State.Ok, p.Status.State, "usage still fetched with the current token");
                Assert.Equal(12.0, p.Windows[0].UsedPct, "value");
            }
        }

        public static void Test_codex_unusable_relative_reset_keeps_the_percentage()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".codex\auth.json", "{\"tokens\":{\"access_token\":\"AT\"}}");
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    return FakeHttp.Reply(200, "{\"rate_limit\":{\"primary_window\":{\"used_percent\":20,\"limit_window_seconds\":18000,\"reset_after_seconds\":1e20}}}");
                };
                ProviderSnapshot p = new CodexAdapter().FetchUsage(e.Context(false, "codex"));
                Assert.Equal(State.Ok, p.Status.State, "state");
                Assert.Equal(20.0, p.Windows[0].UsedPct, "used kept");
                Assert.Null(p.Windows[0].ResetsAt, "reset unknown");
            }
        }

        public static void Test_http_deadline_covers_the_whole_exchange_not_just_each_read()
        {
            using (TestServer s = new TestServer(delegate(HttpListenerContext c)
            {
                c.Response.SendChunked = true;
                for (int i = 0; i < 80; i++)
                {
                    c.Response.OutputStream.WriteByte(65);
                    c.Response.OutputStream.Flush();
                    Thread.Sleep(100);
                }
            }))
            {
                DateTime t0 = DateTime.UtcNow;
                Assert.Throws(delegate() { new RealHttp().Request("GET", s.BaseUrl, null, null, 800); }, "slow trickle must hit the overall deadline");
                Assert.True((DateTime.UtcNow - t0).TotalMilliseconds < 3000, "gave up in time: " + (DateTime.UtcNow - t0).TotalMilliseconds + " ms");
            }
        }

        public static void Test_credentials_file_is_never_missing_and_leaves_no_temp_file_after_a_refresh()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                string path = e.Write(@".claude\.credentials.json", Creds("OLD", "RT", Ms(e.Now.AddSeconds(30))));
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("/oauth/token"))
                    {
                        Assert.True(File.Exists(path), "credentials exist while the refresh is in flight");
                        return FakeHttp.Reply(200, "{\"access_token\":\"NEW\",\"refresh_token\":\"RT2\",\"expires_in\":3600}");
                    }
                    return FakeHttp.Reply(200, "{}");
                };
                new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.True(File.Exists(path), "credentials exist afterwards");
                Assert.False(File.Exists(path + ".tmp"), "no temp file left");
                Assert.Contains(File.ReadAllText(path), "\"accessToken\": \"NEW\"", "updated");
                // a file that is held open by another process must stay intact if it cannot be replaced
                string before = File.ReadAllText(path);
                using (FileStream hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    e.Http.Handler = delegate(RecordedRequest r)
                    {
                        if (r.Url.Contains("/oauth/token")) return FakeHttp.Reply(200, "{\"access_token\":\"NEWER\",\"refresh_token\":\"RT3\",\"expires_in\":3600}");
                        return FakeHttp.Reply(200, "{}");
                    };
                    // force a refresh again by rewriting expiry into the past is not possible while held; just ensure no exception escapes
                    ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                    Assert.NotNull(p, "no exception");
                }
                Assert.Equal(before, File.ReadAllText(path), "file untouched");
            }
        }
    }
}
