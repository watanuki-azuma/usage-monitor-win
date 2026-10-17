using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace UsageMonitor.Tests
{
    public class RecordedRequest
    {
        public string Method;
        public string Url;
        public Dictionary<string, string> Headers;
        public string Body;

        public string Header(string name)
        {
            foreach (KeyValuePair<string, string> kv in Headers)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
    }

    public class FakeHttp : IHttp
    {
        public List<RecordedRequest> Requests = new List<RecordedRequest>();
        public Func<RecordedRequest, HttpResult> Handler;

        public HttpResult Request(string method, string url, IDictionary<string, string> headers, byte[] body, int timeoutMs)
        {
            RecordedRequest r = new RecordedRequest();
            r.Method = method;
            r.Url = url;
            r.Headers = new Dictionary<string, string>();
            if (headers != null) foreach (KeyValuePair<string, string> kv in headers) r.Headers[kv.Key] = kv.Value;
            r.Body = body == null ? null : Encoding.UTF8.GetString(body);
            Requests.Add(r);
            if (Handler == null) throw new InvalidOperationException("unexpected HTTP request to " + url);
            return Handler(r);
        }

        public static HttpResult Reply(int status, string json)
        {
            HttpResult h = new HttpResult();
            h.Status = status;
            h.Text = json ?? "";
            h.Raw = Encoding.UTF8.GetBytes(h.Text);
            h.Headers = new Dictionary<string, string>();
            return h;
        }
    }

    public class AdapterEnv : IDisposable
    {
        public string Home;
        public string BinDir;
        public FakeHttp Http = new FakeHttp();
        public AppConfig Config = AppConfig.Default();
        public DateTime Now = Dates.Utc(2026, 10, 11, 12, 0, 0);
        public ICredentialReader Credentials;

        public AdapterEnv()
        {
            Home = TempDir.Create();
            BinDir = Path.Combine(Home, "bin");
            Directory.CreateDirectory(BinDir);
        }

        public AdapterContext Context(bool cliOnPath, string cliName)
        {
            if (cliOnPath) File.WriteAllText(Path.Combine(BinDir, cliName + ".cmd"), "@echo off");
            Dictionary<string, string> env = new Dictionary<string, string>();
            env["USERPROFILE"] = Home;
            env["APPDATA"] = Path.Combine(Home, "AppData", "Roaming");
            env["LOCALAPPDATA"] = Path.Combine(Home, "AppData", "Local");
            env["PATH"] = BinDir;
            Paths paths = new Paths(delegate(string k) { string v; return env.TryGetValue(k, out v) ? v : null; });
            AdapterContext c = new AdapterContext();
            c.Http = Http;
            c.Paths = paths;
            c.Credentials = Credentials;
            c.Config = Config;
            DateTime now = Now;
            c.UtcNow = delegate() { return now; };
            return c;
        }

        public string Write(string relative, string content)
        {
            string p = Path.Combine(Home, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content, new UTF8Encoding(false));
            return p;
        }

        public void Dispose()
        {
            try { Directory.Delete(Home, true); } catch (Exception) { }
        }
    }

    // A tiny localhost HTTP server for RealHttp tests.
    public class TestServer : IDisposable
    {
        HttpListener listener;
        Thread thread;
        public string BaseUrl;
        public Action<HttpListenerContext> Handler;
        public volatile bool Stop;

        public TestServer(Action<HttpListenerContext> handler)
        {
            Handler = handler;
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = "http://localhost:" + port + "/";
            listener = new HttpListener();
            listener.Prefixes.Add(BaseUrl);
            listener.Start();
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        void Loop()
        {
            while (!Stop)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch (Exception) { return; }
                try { Handler(ctx); }
                catch (Exception) { }
                try { ctx.Response.Close(); }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            Stop = true;
            try { listener.Close(); } catch (Exception) { }
        }
    }

    public static class HttpTests
    {
        public static void Test_GET_returns_status_text_and_lowercase_headers()
        {
            using (TestServer s = new TestServer(delegate(HttpListenerContext c)
            {
                c.Response.StatusCode = 200;
                c.Response.AddHeader("X-Thing", "yes");
                byte[] b = Encoding.UTF8.GetBytes("{\"a\":1}");
                c.Response.OutputStream.Write(b, 0, b.Length);
            }))
            {
                RealHttp http = new RealHttp();
                HttpResult r = http.Request("GET", s.BaseUrl + "x", null, null, 5000);
                Assert.Equal(200, r.Status, "status");
                Assert.Equal("{\"a\":1}", r.Text, "text");
                Assert.Equal(7, r.Raw.Length, "raw length");
                Assert.Equal("yes", r.Headers["x-thing"], "header key lower-cased");
            }
        }

        public static void Test_POST_sends_body_and_custom_headers()
        {
            string seenBody = null, seenAuth = null, seenType = null, seenMethod = null;
            using (TestServer s = new TestServer(delegate(HttpListenerContext c)
            {
                seenMethod = c.Request.HttpMethod;
                seenAuth = c.Request.Headers["Authorization"];
                seenType = c.Request.ContentType;
                using (StreamReader rd = new StreamReader(c.Request.InputStream, Encoding.UTF8)) seenBody = rd.ReadToEnd();
                c.Response.StatusCode = 201;
            }))
            {
                RealHttp http = new RealHttp();
                Dictionary<string, string> h = new Dictionary<string, string>();
                h["Authorization"] = "Bearer abc";
                h["Content-Type"] = "application/json";
                HttpResult r = http.Request("POST", s.BaseUrl, h, Encoding.UTF8.GetBytes("{\"k\":\"日本\"}"), 5000);
                Assert.Equal(201, r.Status, "status");
                Assert.Equal("POST", seenMethod, "method");
                Assert.Equal("Bearer abc", seenAuth, "authorization header");
                Assert.Contains(seenType, "application/json", "content type");
                Assert.Equal("{\"k\":\"日本\"}", seenBody, "body utf-8");
            }
        }

        public static void Test_non_2xx_is_returned_not_thrown()
        {
            using (TestServer s = new TestServer(delegate(HttpListenerContext c)
            {
                c.Response.StatusCode = 429;
                byte[] b = Encoding.UTF8.GetBytes("slow down");
                c.Response.OutputStream.Write(b, 0, b.Length);
            }))
            {
                HttpResult r = new RealHttp().Request("GET", s.BaseUrl, null, null, 5000);
                Assert.Equal(429, r.Status, "status 429");
                Assert.Equal("slow down", r.Text, "body of error response");
            }
        }

        public static void Test_rejects_unsupported_schemes()
        {
            RealHttp http = new RealHttp();
            Assert.Throws(delegate() { http.Request("GET", "file:///C:/Windows/win.ini", null, null, 2000); }, "file scheme");
            Assert.Throws(delegate() { http.Request("GET", "ftp://example.test/x", null, null, 2000); }, "ftp scheme");
        }

        public static void Test_rejects_oversized_response()
        {
            using (TestServer s = new TestServer(delegate(HttpListenerContext c)
            {
                byte[] big = new byte[2 * 1024 * 1024 + 1];
                c.Response.ContentLength64 = big.Length;
                c.Response.OutputStream.Write(big, 0, big.Length);
            }))
            {
                Assert.Throws(delegate() { new RealHttp().Request("GET", s.BaseUrl, null, null, 10000); }, "over 2 MiB");
            }
        }

        public static void Test_accepts_response_of_exactly_2_MiB()
        {
            using (TestServer s = new TestServer(delegate(HttpListenerContext c)
            {
                byte[] big = new byte[2 * 1024 * 1024];
                for (int i = 0; i < big.Length; i++) big[i] = 65;
                c.Response.ContentLength64 = big.Length;
                c.Response.OutputStream.Write(big, 0, big.Length);
            }))
            {
                HttpResult r = new RealHttp().Request("GET", s.BaseUrl, null, null, 10000);
                Assert.Equal(2 * 1024 * 1024, r.Raw.Length, "exactly 2 MiB accepted");
            }
        }

        public static void Test_times_out()
        {
            using (TestServer s = new TestServer(delegate(HttpListenerContext c) { Thread.Sleep(3000); }))
            {
                DateTime t0 = DateTime.UtcNow;
                Assert.Throws(delegate() { new RealHttp().Request("GET", s.BaseUrl, null, null, 400); }, "timeout");
                Assert.True((DateTime.UtcNow - t0).TotalMilliseconds < 2500, "timeout is honoured");
            }
        }

        public static void Test_helpers_parse_json_and_tolerate_garbage()
        {
            FakeHttp f = new FakeHttp();
            f.Handler = delegate(RecordedRequest r)
            {
                if (r.Url.EndsWith("/ok")) return FakeHttp.Reply(200, "{\"a\":[1,2],\"n\":null}");
                return FakeHttp.Reply(502, "<html>bad gateway</html>");
            };
            HttpResult ok = HttpHelpers.GetJson(f, "https://x.test/ok", null);
            Assert.Equal(200, ok.Status, "status");
            Assert.NotNull(ok.Json, "json parsed");
            Dictionary<string, object> d = (Dictionary<string, object>)ok.Json;
            Assert.True(d["a"] is object[], "arrays are object[] (DeserializeObject)");
            HttpResult bad = HttpHelpers.GetJson(f, "https://x.test/bad", null);
            Assert.Equal(502, bad.Status, "bad status");
            Assert.Null(bad.Json, "non-json body -> Json null");
            Assert.Equal("GET", f.Requests[0].Method, "GET method");
        }

        public static void Test_helpers_PostJson_and_PostForm_encode_bodies()
        {
            FakeHttp f = new FakeHttp();
            f.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(200, "{}"); };
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["grant_type"] = "refresh_token";
            body["n"] = 3;
            HttpHelpers.PostJson(f, "https://x.test/j", null, body);
            RecordedRequest j = f.Requests[0];
            Assert.Equal("POST", j.Method, "method");
            Assert.Contains(j.Header("Content-Type"), "application/json", "json content type");
            Assert.Contains(j.Body, "\"grant_type\":\"refresh_token\"", "json body");

            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["a"] = "1";
            fields["b"] = "x y&z";
            HttpHelpers.PostForm(f, "https://x.test/f", null, fields);
            RecordedRequest form = f.Requests[1];
            Assert.Contains(form.Header("Content-Type"), "application/x-www-form-urlencoded", "form content type");
            Assert.Equal("a=1&b=x%20y%26z", form.Body, "form body is percent-encoded");
        }
    }

    public static class ClaudeAdapterTests
    {
        const string UsageUrlPart = "api.anthropic.com/api/oauth/usage";

        static string Creds(string access, string refresh, long expiresAtMs, string extra)
        {
            return "{\"claudeAiOauth\":{\"accessToken\":\"" + access + "\",\"refreshToken\":\"" + refresh + "\",\"expiresAt\":" + expiresAtMs + (extra ?? "") + "}}";
        }

        static long Ms(DateTime utc)
        {
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        const string FullUsage = "{\"five_hour\":{\"utilization\":42.5,\"resets_at\":\"2026-10-11T15:00:00Z\"},"
            + "\"seven_day\":{\"utilization\":10,\"resets_at\":\"2026-10-17T00:00:00Z\"},"
            + "\"limits\":[{\"kind\":\"weekly_scoped\",\"scope\":{\"model\":{\"display_name\":\"Opus\"}},\"percent\":77,\"resets_at\":\"2026-10-17T00:00:00Z\"}],"
            + "\"spend\":{\"enabled\":true,\"limit\":{\"amount_minor\":2000},\"used\":{\"amount_minor\":500}}}";

        public static void Test_not_installed_when_no_credentials_and_no_cli()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal("claude", p.Id, "id");
                Assert.Equal(State.NotInstalled, p.Status.State, "state");
                Assert.Equal(0, e.Http.Requests.Count, "no requests");
            }
        }

        public static void Test_logged_out_when_cli_present_but_no_credentials()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(true, "claude"));
                Assert.Equal(State.LoggedOut, p.Status.State, "state");
                Assert.Equal("Run: claude auth login", p.Status.Hint, "hint");
            }
        }

        public static void Test_unknown_when_no_oauth_token()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".claude\.credentials.json", "{\"somethingElse\":{}}");
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(State.Unknown, p.Status.State, "state");
                Assert.Contains(p.Status.Message, "not signed in", "message");
            }
        }

        public static void Test_happy_path_maps_all_window_kinds()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".claude\.credentials.json", Creds("AT", "RT", Ms(e.Now.AddHours(5)), ",\"rateLimitTier\":\"default_claude_max_5x\",\"subscriptionType\":\"max\""));
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(200, FullUsage); };
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(State.Ok, p.Status.State, "state");
                Assert.Equal("Claude Code", p.DisplayName, "display name");
                Assert.Equal("Max 5x", p.Plan, "plan");
                Assert.Equal("api", p.Source, "source");
                Assert.Equal(e.Now, p.FetchedAt, "fetched at uses injected clock");
                Assert.Equal(4, p.Windows.Count, "window count");
                Assert.Equal(Kind.FiveHour, p.Windows[0].Kind, "w0 kind");
                Assert.Equal("5h", p.Windows[0].Label, "w0 label");
                Assert.Equal(42.5, p.Windows[0].UsedPct, "w0 used");
                Assert.Equal(Dates.Utc(2026, 10, 11, 15, 0, 0), p.Windows[0].ResetsAt, "w0 reset");
                Assert.Equal(Kind.Weekly, p.Windows[1].Kind, "w1 kind");
                Assert.Equal("Weekly", p.Windows[1].Label, "w1 label");
                Assert.Equal(10.0, p.Windows[1].UsedPct, "w1 used");
                Assert.Equal(Kind.WeeklyScoped, p.Windows[2].Kind, "w2 kind");
                Assert.Equal("Opus weekly", p.Windows[2].Label, "w2 label");
                Assert.Equal(77.0, p.Windows[2].UsedPct, "w2 used");
                Assert.Equal(Kind.Credits, p.Windows[3].Kind, "w3 kind");
                Assert.Equal("Credits", p.Windows[3].Label, "w3 label");
                Assert.Equal(25.0, p.Windows[3].UsedPct, "w3 used (500/2000)");
                Assert.Equal(1, e.Http.Requests.Count, "single request");
                RecordedRequest req = e.Http.Requests[0];
                Assert.Equal("GET", req.Method, "method");
                Assert.Contains(req.Url, UsageUrlPart, "url");
                Assert.Equal("Bearer AT", req.Header("Authorization"), "authorization");
                Assert.Equal("oauth-2025-04-20", req.Header("anthropic-beta"), "beta header");
            }
        }

        public static void Test_MapUsage_does_not_invent_zero_from_a_reset_time()
        {
            ProviderSnapshot p = ClaudeAdapter.MapUsage(new JsonHelper().Parse("{\"five_hour\":{\"resets_at\":\"2030-01-01T00:00:00Z\"}}"), null, Dates.Utc(2026, 1, 1, 0, 0, 0));
            Assert.Equal(1, p.Windows.Count, "window kept");
            Assert.Null(p.Windows[0].UsedPct, "used stays null");
        }

        public static void Test_PlanLabel()
        {
            JsonHelper j = new JsonHelper();
            Assert.Equal("Max 20x", ClaudeAdapter.PlanLabel(j.Parse("{\"rateLimitTier\":\"default_claude_max_20x\"}")), "20x");
            Assert.Equal("Max 5x", ClaudeAdapter.PlanLabel(j.Parse("{\"rateLimitTier\":\"default_claude_max_5x\"}")), "5x");
            Assert.Equal("Max 20x", ClaudeAdapter.PlanLabel(j.Parse("{\"rateLimitTier\":\"x_max_20_y\"}")), "regex 20");
            Assert.Equal("Max 5x", ClaudeAdapter.PlanLabel(j.Parse("{\"rateLimitTier\":\"x_max_5_y\"}")), "regex 5");
            Assert.Equal("Max", ClaudeAdapter.PlanLabel(j.Parse("{\"subscriptionType\":\"max\"}")), "max");
            Assert.Equal("Pro", ClaudeAdapter.PlanLabel(j.Parse("{\"subscriptionType\":\"pro\"}")), "pro");
            Assert.Equal("Enterprise", ClaudeAdapter.PlanLabel(j.Parse("{\"subscriptionType\":\"enterprise\"}")), "enterprise");
            Assert.Equal("team", ClaudeAdapter.PlanLabel(j.Parse("{\"subscriptionType\":\"team\"}")), "passthrough");
            Assert.Null(ClaudeAdapter.PlanLabel(j.Parse("{}")), "none");
            Assert.Null(ClaudeAdapter.PlanLabel(null), "null oauth");
        }

        public static void Test_401_refresh_then_429_keeps_the_rate_limit_and_stores_new_tokens()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                string credPath = e.Write(@".claude\.credentials.json", Creds("OLD", "RT", Ms(e.Now.AddHours(5)), ""));
                int usageCalls = 0;
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("/oauth/token")) return FakeHttp.Reply(200, "{\"access_token\":\"NEW\",\"refresh_token\":\"RT2\",\"expires_in\":3600}");
                    usageCalls++;
                    return FakeHttp.Reply(usageCalls == 1 ? 401 : 429, "");
                };
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.True(p.RateLimited, "rate limited flag");
                Assert.Equal(State.FetchFailed, p.Status.State, "state");
                Assert.Equal("rate limited", p.Status.Message, "message");
                string saved = File.ReadAllText(credPath);
                Assert.Contains(saved, "\"accessToken\": \"NEW\"", "new access token written (indented json)");
                Assert.Contains(saved, "\"refreshToken\": \"RT2\"", "rotated refresh token written");
                Assert.Contains(saved, "claudeAiOauth", "structure kept");
                Assert.Equal(2, usageCalls, "usage retried after refresh");
                RecordedRequest second = null;
                foreach (RecordedRequest r in e.Http.Requests) if (r.Url.Contains(UsageUrlPart)) second = r;
                Assert.Equal("Bearer NEW", second.Header("Authorization"), "retry uses the new token");
            }
        }

        public static void Test_expiring_token_is_refreshed_before_the_first_usage_call()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".claude\.credentials.json", Creds("OLD", "RT", Ms(e.Now.AddSeconds(30)), ""));
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("/oauth/token")) return FakeHttp.Reply(200, "{\"access_token\":\"NEW\",\"expires_in\":3600}");
                    return FakeHttp.Reply(200, FullUsage);
                };
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(State.Ok, p.Status.State, "ok");
                Assert.Equal(2, e.Http.Requests.Count, "refresh + usage");
                Assert.Equal("POST", e.Http.Requests[0].Method, "refresh first");
                Assert.Contains(e.Http.Requests[0].Url, "platform.claude.com/v1/oauth/token", "token url");
                Assert.Contains(e.Http.Requests[0].Body, "\"grant_type\":\"refresh_token\"", "grant type");
                Assert.Contains(e.Http.Requests[0].Body, "\"client_id\":\"9d1c250a-e61b-44d9-88ed-5944d1962f5e\"", "client id");
                Assert.Contains(e.Http.Requests[0].Body, "\"refresh_token\":\"RT\"", "refresh token");
                Assert.Equal("Bearer NEW", e.Http.Requests[1].Header("Authorization"), "usage with new token");
            }
        }

        public static void Test_refresh_rejected_means_logged_out_without_usage_call()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".claude\.credentials.json", Creds("OLD", "RT", Ms(e.Now.AddSeconds(30)), ""));
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(400, "{\"error\":\"invalid_grant\"}"); };
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(State.LoggedOut, p.Status.State, "logged out");
                Assert.Equal(1, e.Http.Requests.Count, "only the refresh was attempted");
            }
        }

        public static void Test_refresh_can_be_disabled_by_config()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".claude\.credentials.json", Creds("OLD", "RT", Ms(e.Now.AddSeconds(30)), ""));
                e.Config.SetRefreshTokens("claude", false);
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(401, ""); };
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(State.LoggedOut, p.Status.State, "logged out on 401");
                foreach (RecordedRequest r in e.Http.Requests) Assert.Equal("GET", r.Method, "no POST (refresh) when disabled");
            }
        }

        public static void Test_other_http_errors()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".claude\.credentials.json", Creds("AT", "RT", Ms(e.Now.AddHours(5)), ""));
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(500, "oops"); };
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(State.FetchFailed, p.Status.State, "state");
                Assert.Equal("HTTP 500", p.Status.Message, "message");
                Assert.False(p.RateLimited, "not rate limited");
            }
        }

        public static void Test_transport_errors_become_fetch_failed_not_exceptions()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".claude\.credentials.json", Creds("AT", "RT", Ms(e.Now.AddHours(5)), ""));
                e.Http.Handler = delegate(RecordedRequest r) { throw new WebException("boom"); };
                ProviderSnapshot p = new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(State.FetchFailed, p.Status.State, "state");
            }
        }

        public static void Test_token_file_is_not_overwritten_when_it_changed_during_refresh()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                string credPath = e.Write(@".claude\.credentials.json", Creds("OLD", "RT", Ms(e.Now.AddSeconds(30)), ""));
                string concurrent = Creds("CLI-WROTE-THIS", "RT-FROM-CLI", Ms(e.Now.AddHours(8)), "");
                e.Http.Handler = delegate(RecordedRequest r)
                {
                    if (r.Url.Contains("/oauth/token"))
                    {
                        File.WriteAllText(credPath, concurrent);
                        File.SetLastWriteTimeUtc(credPath, DateTime.UtcNow.AddMinutes(10));
                        return FakeHttp.Reply(200, "{\"access_token\":\"NEW\",\"refresh_token\":\"RT2\",\"expires_in\":3600}");
                    }
                    return FakeHttp.Reply(200, FullUsage);
                };
                new ClaudeAdapter().FetchUsage(e.Context(false, "claude"));
                Assert.Equal(concurrent, File.ReadAllText(credPath), "the other writer's file is left untouched");
            }
        }
    }

    public static class CodexAdapterTests
    {
        static long Epoch(DateTime utc)
        {
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static void Test_MapWham_handles_daily_window_and_bad_reset()
        {
            ProviderSnapshot p = CodexAdapter.MapWham(new JsonHelper().Parse("{\"rate_limit\":{\"primary_window\":{\"used_percent\":20,\"limit_window_seconds\":86400,\"reset_at\":\"bad\"}}}"), Dates.Utc(2026, 10, 11, 12, 0, 0));
            Assert.Equal(Kind.Daily, p.Windows[0].Kind, "kind");
            Assert.Equal("Daily", p.Windows[0].Label, "label");
            Assert.Equal(20.0, p.Windows[0].UsedPct, "used");
            Assert.Null(p.Windows[0].ResetsAt, "reset null");
        }

        public static void Test_api_key_login_makes_no_request()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".codex\auth.json", "{\"OPENAI_API_KEY\":\"sk-fake\"}");
                ProviderSnapshot p = new CodexAdapter().FetchUsage(e.Context(false, "codex"));
                Assert.Equal(State.Unknown, p.Status.State, "state");
                Assert.Equal(0, e.Http.Requests.Count, "no request");
            }
        }

        public static void Test_missing_install_and_missing_login()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                ProviderSnapshot a = new CodexAdapter().FetchUsage(e.Context(false, "codex"));
                Assert.Equal(State.NotInstalled, a.Status.State, "not installed");
                ProviderSnapshot b = new CodexAdapter().FetchUsage(e.Context(true, "codex"));
                Assert.Equal(State.LoggedOut, b.Status.State, "logged out");
                Assert.Equal("Run: codex login", b.Status.Hint, "hint");
                e.Write(@".codex\auth.json", "{\"tokens\":{}}");
                ProviderSnapshot c = new CodexAdapter().FetchUsage(e.Context(true, "codex"));
                Assert.Equal(State.LoggedOut, c.Status.State, "no access token -> logged out");
            }
        }

        public static void Test_happy_path()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".codex\auth.json", "{\"tokens\":{\"access_token\":\"AT\",\"account_id\":\"acc-1\"}}");
                DateTime five = Dates.Utc(2026, 10, 11, 15, 0, 0);
                DateTime week = Dates.Utc(2026, 10, 18, 3, 0, 0);
                string body = "{\"plan_type\":\"plus\",\"rate_limit\":{"
                    + "\"primary_window\":{\"used_percent\":20,\"limit_window_seconds\":18000,\"reset_at\":" + Epoch(five) + "},"
                    + "\"secondary_window\":{\"used_percent\":55,\"limit_window_seconds\":604800,\"reset_after_seconds\":3600}},"
                    + "\"additional_rate_limits\":[{\"name\":\"GPT-5-Codex\",\"primary_window\":{\"used_percent\":5,\"limit_window_seconds\":604800,\"reset_at\":" + Epoch(week) + "}},"
                    + "{\"name\":\"NoUsed\",\"primary_window\":{\"limit_window_seconds\":604800}}],"
                    + "\"rate_limit_reset_credits\":{\"available_count\":2}}";
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(200, body); };
                ProviderSnapshot p = new CodexAdapter().FetchUsage(e.Context(false, "codex"));
                Assert.Equal(State.Ok, p.Status.State, "state");
                Assert.Equal("Plus", p.Plan, "plan capitalised");
                Assert.Equal("Codex", p.DisplayName, "name");
                Assert.Equal(4, p.Windows.Count, "windows (extra without used skipped, credits added)");
                Assert.Equal(Kind.FiveHour, p.Windows[0].Kind, "w0 kind");
                Assert.Equal("5h", p.Windows[0].Label, "w0 label");
                Assert.Equal(20.0, p.Windows[0].UsedPct, "w0 used");
                Assert.Equal(five, p.Windows[0].ResetsAt, "w0 reset from epoch seconds");
                Assert.Equal(Kind.Weekly, p.Windows[1].Kind, "w1 kind");
                Assert.Equal(55.0, p.Windows[1].UsedPct, "w1 used");
                Assert.Equal(e.Now.AddSeconds(3600), p.Windows[1].ResetsAt, "w1 reset from reset_after_seconds");
                Assert.Equal("GPT-5-Codex Weekly", p.Windows[2].Label, "w2 label");
                Assert.Equal(5.0, p.Windows[2].UsedPct, "w2 used");
                Assert.Equal(Kind.Credits, p.Windows[3].Kind, "w3 kind");
                Assert.Equal("Reset credits \u00D72", p.Windows[3].Label, "w3 label");
                Assert.Null(p.Windows[3].UsedPct, "credits have no percentage");
                RecordedRequest req = e.Http.Requests[0];
                Assert.Contains(req.Url, "chatgpt.com/backend-api/wham/usage", "url");
                Assert.Equal("Bearer AT", req.Header("Authorization"), "authorization");
                Assert.Equal("acc-1", req.Header("ChatGPT-Account-Id"), "account id");
            }
        }

        public static void Test_remaining_percent_is_converted()
        {
            ProviderSnapshot p = CodexAdapter.MapWham(new JsonHelper().Parse("{\"rate_limit\":{\"primary_window\":{\"remaining_percent\":30,\"limit_window_seconds\":18000}}}"), Dates.Utc(2026, 10, 11, 12, 0, 0));
            Assert.Equal(70.0, p.Windows[0].UsedPct, "100 - remaining");
        }

        public static void Test_auth_failures_and_rate_limit()
        {
            using (AdapterEnv e = new AdapterEnv())
            {
                e.Write(@".codex\auth.json", "{\"tokens\":{\"access_token\":\"AT\"}}");
                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(401, ""); };
                ProviderSnapshot a = new CodexAdapter().FetchUsage(e.Context(false, "codex"));
                Assert.Equal(State.LoggedOut, a.Status.State, "401 -> logged out");
                Assert.Equal("Open Codex once to refresh login", a.Status.Hint, "hint");

                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(429, ""); };
                ProviderSnapshot b = new CodexAdapter().FetchUsage(e.Context(false, "codex"));
                Assert.True(b.RateLimited, "429 flagged");
                Assert.Equal(State.FetchFailed, b.Status.State, "429 state");

                e.Http.Handler = delegate(RecordedRequest r) { return FakeHttp.Reply(503, ""); };
                ProviderSnapshot c = new CodexAdapter().FetchUsage(e.Context(false, "codex"));
                Assert.Equal("HTTP 503", c.Status.Message, "other status");
            }
        }
    }
}
