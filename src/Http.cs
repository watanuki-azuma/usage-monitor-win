// Milestone M2: real HTTP transport.
//
// Port of reference/js/http.js. Same rules as the JS version, which means:
//   * only http/https, anything else is rejected up front
//   * timeoutMs covers the whole exchange (headers, request body, response body)
//   * a response body is capped at 2 MiB (an exception when exceeded)
//   * non-2xx statuses are *returned*, never thrown
//   * header names are lower-cased in HttpResult.Headers
//
// Extra care taken here (SPEC M2):
//   * ServicePointManager.SecurityProtocol is bumped to include TLS 1.2
//   * redirects are never followed, so credentials can never travel to another host
//
// Language level: C# 5 (in-box csc.exe). No WinForms in this file.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace UsageMonitor
{
    public class RealHttp : IHttp
    {
        public const int DefaultTimeoutMs = 8000;
        public const int MaxBodyBytes = 2 * 1024 * 1024;

        // SecurityProtocolType.Tls12 (0x00000C00) does not exist in the .NET 4.0 reference
        // assemblies, so the flag is spelled as a cast instead of the enum member. OR-ing an
        // unknown flag into the property is harmless on runtimes that do not know it.
        private const SecurityProtocolType Tls12 = (SecurityProtocolType)0x00000C00;

        static RealHttp()
        {
            try { ServicePointManager.Expect100Continue = false; }
            catch (Exception) { /* not fatal: the request just becomes one round trip longer */ }

            try { ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | Tls12; }
            catch (Exception) { /* platform without TLS 1.2: keep whatever the OS negotiated */ }
        }

        public HttpResult Request(string method, string url, IDictionary<string, string> headers, byte[] body, int timeoutMs)
        {
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            Uri uri = ParseUrl(url);

            string verb = string.IsNullOrEmpty(method) ? "GET" : method.Trim().ToUpperInvariant();
            // One monotonic deadline for the whole exchange, exactly like the JS killer timer: it
            // starts before the request is created and is re-checked while the body is read, so a
            // server that trickles bytes can never hold the call open past timeoutMs.
            Stopwatch clock = Stopwatch.StartNew();
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(uri);
            req.Method = verb;
            // Credentials must never follow a redirect onto another host (SPEC M2). The simplest
            // way to guarantee that is to not follow redirects at all: a 3xx is reported as a
            // status, exactly like the Node reference implementation, which never redirects.
            req.AllowAutoRedirect = false;
            req.KeepAlive = false;
            req.Timeout = timeout;
            req.ReadWriteTimeout = timeout;
            req.ProtocolVersion = HttpVersion.Version11;

            ApplyHeaders(req, headers);

            if (body != null && body.Length > 0)
            {
                req.ContentLength = body.Length;
                using (Stream stream = req.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }
            }

            HttpWebResponse resp;
            try
            {
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException ex)
            {
                // Non-2xx answers come back as a WebException that carries the response; the
                // status plus body are returned to the caller. A WebException *without* a
                // response is a genuine transport failure (refused, timeout, DNS, TLS).
                resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;
            }

            try
            {
                byte[] raw = ReadBody(resp.GetResponseStream(), timeout, clock, req);
                string text = raw.Length == 0 ? "" : Encoding.UTF8.GetString(raw);

                HttpResult result = new HttpResult();
                result.Status = (int)resp.StatusCode;
                result.Text = text;
                result.Raw = raw;
                result.Headers = HeaderMap(resp.Headers);
                result.Json = JsonUtil.Parse(text);
                return result;
            }
            finally
            {
                try { resp.Close(); }
                catch (Exception) { /* nothing useful to do with a failing close */ }
            }
        }

        // ------------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------------

        private static Uri ParseUrl(string url)
        {
            if (url == null || url.Trim().Length == 0) throw new ArgumentNullException("url");

            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri))
                throw new UriFormatException("not an absolute URL: " + url);

            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https")
                throw new NotSupportedException("unsupported protocol: " + uri.Scheme);
            return uri;
        }

        // Restricted headers (Content-Type, User-Agent, ...) cannot go through Headers.Add().
        private static void ApplyHeaders(HttpWebRequest req, IDictionary<string, string> headers)
        {
            if (req == null || headers == null) return;

            foreach (KeyValuePair<string, string> kv in headers)
            {
                if (kv.Key == null) continue;
                string name = kv.Key.Trim();
                if (name.Length == 0) continue;
                string value = kv.Value;

                switch (name.ToLowerInvariant())
                {
                    case "content-type":
                        req.ContentType = value == null ? null : value;
                        break;
                    case "accept":
                        req.Accept = value == null ? null : value;
                        break;
                    case "user-agent":
                        req.UserAgent = value == null ? null : value;
                        break;
                    case "host":
                        if (!string.IsNullOrEmpty(value)) req.Host = value;
                        break;
                    case "referer":
                        req.Referer = value == null ? null : value;
                        break;
                    case "expect":
                        if (!string.IsNullOrEmpty(value)) req.Expect = value;
                        break;
                    case "connection":
                        if (!string.IsNullOrEmpty(value)) req.Connection = value;
                        break;
                    case "if-modified-since":
                        {
                            DateTime when;
                            if (value != null && DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                                req.IfModifiedSince = when;
                            break;
                        }
                    case "date":
                        {
                            DateTime when;
                            if (value != null && DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                                req.Date = when;
                            break;
                        }
                    default:
                        if (value != null) req.Headers.Add(name, value);
                        break;
                }
            }
        }

        // Accumulates the body and gives up as soon as the cap is exceeded, so a runaway server
        // cannot exhaust memory. The per-read ReadTimeout only bounds a *single* read, so the
        // shared deadline is checked here as well and the request is aborted when it expires.
        private static byte[] ReadBody(Stream stream, int timeoutMs, Stopwatch clock, HttpWebRequest req)
        {
            if (stream == null) return new byte[0];

            MemoryStream buffer = new MemoryStream(1024);
            byte[] chunk = new byte[8192];
            while (true)
            {
                long left = timeoutMs - clock.ElapsedMilliseconds;
                if (left <= 0)
                {
                    Abort(req);
                    throw new TimeoutException("http request exceeded " + timeoutMs + " ms");
                }

                // Never let one read outlive the shared deadline either.
                try { stream.ReadTimeout = (int)Math.Min(left, (long)int.MaxValue); }
                catch (Exception) { /* stream without a timeout: the socket level still applies */ }

                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                if (buffer.Length + read > MaxBodyBytes)
                    throw new InvalidOperationException("response body exceeds " + MaxBodyBytes + " bytes");
                buffer.Write(chunk, 0, read);

                if (clock.ElapsedMilliseconds >= timeoutMs)
                {
                    Abort(req);
                    throw new TimeoutException("http request exceeded " + timeoutMs + " ms");
                }
            }
            return buffer.ToArray();
        }

        // Killing the request releases the connection immediately instead of waiting for the
        // network stack to give up on its own.
        private static void Abort(HttpWebRequest req)
        {
            try { if (req != null) req.Abort(); }
            catch (Exception) { /* aborting a request that already failed is the normal case */ }
        }

        private static Dictionary<string, string> HeaderMap(WebHeaderCollection headers)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (headers == null) return map;

            foreach (string key in headers.AllKeys)
            {
                if (key == null) continue;
                string name = key.ToLowerInvariant();
                string value = headers.Get(key);
                if (value == null) continue;
                // Multi-valued headers (Set-Cookie) arrive comma-joined, like Node's headers map.
                map[name] = value;
            }
            return map;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Convenience wrappers around IHttp.Request, matching reference/js/http.js's getJson /
    // postJson / postForm. Json is parsed from the response text when it is valid JSON and left
    // null when the body is empty or garbage (this never throws).
    // ------------------------------------------------------------------------------------------
    public static class HttpHelpers
    {
        public static HttpResult GetJson(IHttp http, string url, IDictionary<string, string> headers)
        {
            HttpResult result = Http(http).Request("GET", url, headers, null, RealHttp.DefaultTimeoutMs);
            return WithJson(result);
        }

        public static HttpResult PostJson(IHttp http, string url, IDictionary<string, string> headers, object body)
        {
            Dictionary<string, string> merged = Merge(headers, "Content-Type", "application/json");
            byte[] payload = JsonBody.ToUtf8(body);
            HttpResult result = Http(http).Request("POST", url, merged, payload, RealHttp.DefaultTimeoutMs);
            return WithJson(result);
        }

        public static HttpResult PostForm(IHttp http, string url, IDictionary<string, string> headers, IDictionary<string, string> fields)
        {
            Dictionary<string, string> merged = Merge(headers, "Content-Type", "application/x-www-form-urlencoded");
            byte[] payload = new UTF8Encoding(false).GetBytes(FormBody(fields));
            HttpResult result = Http(http).Request("POST", url, merged, payload, RealHttp.DefaultTimeoutMs);
            return WithJson(result);
        }

        private static IHttp Http(IHttp http)
        {
            if (http == null) throw new ArgumentNullException("http");
            return http;
        }

        // Caller headers win over the defaults, like the JS spread of { "Content-Type": x, ...headers }.
        private static Dictionary<string, string> Merge(IDictionary<string, string> headers, string name, string defaultValue)
        {
            Dictionary<string, string> merged = new Dictionary<string, string>(StringComparer.Ordinal);
            merged[name] = defaultValue;
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> kv in headers)
                {
                    if (kv.Key == null) continue;
                    merged[kv.Key] = kv.Value;
                }
            }
            return merged;
        }

        private static HttpResult WithJson(HttpResult result)
        {
            if (result == null) return null;
            if (result.Json == null) result.Json = JsonUtil.Parse(result.Text);
            return result;
        }

        // application/x-www-form-urlencoded, the way URLSearchParams serialises a plain object.
        private static string FormBody(IDictionary<string, string> fields)
        {
            if (fields == null) return "";
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (KeyValuePair<string, string> kv in fields)
            {
                if (!first) sb.Append('&');
                first = false;
                sb.Append(Uri.EscapeDataString(kv.Key == null ? "" : kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kv.Value == null ? "" : kv.Value));
            }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Compact JSON for request bodies. M1's JsonWriter always indents (it reproduces
    // JSON.stringify(value, null, 2) for the on-disk files), so POST bodies get their own writer.
    // It walks the same shapes JavaScriptSerializer.DeserializeObject produces: IDictionary for
    // objects, object[]/ArrayList for arrays, string/bool/number/null for scalars.
    // ------------------------------------------------------------------------------------------
    internal static class JsonBody
    {
        public static string Compact(object value)
        {
            StringBuilder sb = new StringBuilder(256);
            Write(sb, value, 0);
            return sb.ToString();
        }

        public static byte[] ToUtf8(object value)
        {
            return new UTF8Encoding(false).GetBytes(Compact(value));
        }

        private static void Write(StringBuilder sb, object value, int depth)
        {
            if (depth > 32 || value == null)
            {
                sb.Append("null");
                return;
            }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is string) { Escape(sb, (string)value); return; }
            if (value is char) { Escape(sb, ((char)value).ToString()); return; }
            if (value is DateTime) { Escape(sb, Models.ToIso((DateTime)value)); return; }

            IDictionary<string, object> map = JsonUtil.Map(value);
            if (map != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Escape(sb, kv.Key == null ? "" : kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }

            IList<object> list = JsonUtil.List(value);
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }

            double? number = JsonUtil.Num(value);
            if (number.HasValue)
            {
                double d = number.Value;
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            Escape(sb, value.ToString());
        }

        private static void Escape(StringBuilder sb, string text)
        {
            sb.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c == '\b') sb.Append("\\b");
                else if (c == '\f') sb.Append("\\f");
                else if (c < 0x20)
                {
                    sb.Append("\\u");
                    sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
