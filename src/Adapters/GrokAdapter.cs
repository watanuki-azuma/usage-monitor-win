// Grok Build usage adapter. Port of reference/js/adapters/grok.js (milestone M3).
//
// Tokens live in ~/.grok/auth.json (one entry per signed-in account; the newest one wins).
// Usage comes from the REST billing endpoint; when the REST response has no percentage (proto3
// omits a zero) the grpc-web GetGrokCreditsConfig call fills it in. The protobuf and grpc-web
// frame parsing is hand written (walkProto / readVarint / firstGrpcWebDataMessage) so no
// protobuf dependency is needed.
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
    public class GrokAdapter : IUsageAdapter
    {
        public const string AdapterId = "grok";
        public const string DisplayNameText = "Grok Build";

        private const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
        private const string GrpcCreditsUrl = "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";
        private const int GrpcTimeoutMs = 4000;

        // GetGrokCreditsConfigRequest { bool exclude_legacy_monthly_usage = 1; } as a grpc-web frame.
        private static readonly byte[] GrpcRequest = new byte[] { 0, 0, 0, 0, 2, 0x08, 0x00 };

        // A token that expires within this window is refreshed before it is used.
        private const double RefreshSkewMs = 60000;

        private const string DefaultIssuer = "https://auth.x.ai";

        public GrokAdapter() { }

        public string Id { get { return AdapterId; } }

        public string DisplayName { get { return DisplayNameText; } }

        // ------------------------------------------------------------------------------------------
        // mapping (body -> snapshot). Takes whatever JavaScriptSerializer produced, never throws.
        // ------------------------------------------------------------------------------------------

        public static ProviderSnapshot MapBilling(object body, DateTime nowUtc)
        {
            DateTime now = Models.ToUtc(nowUtc);
            IDictionary<string, object> map = JsonUtil.Map(body);
            IDictionary<string, object> cfg = AdapterUtil.MapOf(map, "config");
            if (cfg == null) cfg = map;
            if (cfg == null) cfg = new Dictionary<string, object>();

            List<UsageWindow> windows = new List<UsageWindow>();
            double? pct = WeeklyPct(cfg);
            bool fromCredits = AdapterUtil.Has(cfg, "creditUsagePercent") || ProductPercents(cfg).Count > 0;

            // proto3 JSON omits a 0.0 creditUsagePercent. Grok's /usage still draws a 0% bar when
            // the period is live, so the zero is only invented for a live period.
            if (!pct.HasValue && HasActivePeriod(cfg, now)) pct = 0.0;

            if (pct.HasValue)
            {
                string kind, label;
                PeriodMeta(cfg, fromCredits, out kind, out label);
                windows.Add(Models.WindowOf(kind, label, pct.Value, PeriodEnd(cfg)));
            }

            double? cap = FieldVal(cfg, "onDemandCap");
            double? prepaid = FieldVal(cfg, "prepaidBalance");
            double? used = FieldVal(cfg, "onDemandUsed");

            if (cap.HasValue && cap.Value > 0)
            {
                double usedVal = used.HasValue ? used.Value : 0.0;
                windows.Add(Models.WindowOf(Kind.Credits, "Credits", usedVal / cap.Value * 100.0, null));
            }
            else if (prepaid.HasValue && prepaid.Value > 0)
            {
                UsageWindow w = new UsageWindow();
                w.Kind = Kind.Credits;
                w.Label = "Credits $" + prepaid.Value.ToString("F", CultureInfo.InvariantCulture);
                w.UsedPct = null;
                w.RemainingPct = null;
                w.ResetsAt = null;
                windows.Add(w);
            }

            List<string> footnotes = new List<string>();
            IList<object> products = JsonUtil.List(JsonUtil.Get(cfg, "productUsage"));
            if (products != null)
            {
                foreach (object item in products)
                {
                    IDictionary<string, object> p = JsonUtil.Map(item);
                    if (p == null) continue;
                    string product = JsonUtil.Str(p, "product", null);
                    if (string.IsNullOrEmpty(product)) continue;
                    if (JsonUtil.Get(p, "usagePercent") == null) continue;

                    string name = Regex.Replace(product, "^Grok", "");
                    footnotes.Add(name + " " + AdapterUtil.NumText(JsonUtil.Get(p, "usagePercent")) + "% of pool");
                }
            }

            ProviderSnapshot p2 = new ProviderSnapshot();
            p2.Id = AdapterId;
            p2.DisplayName = DisplayNameText;
            p2.Status = new ProviderStatus();
            p2.Status.State = State.Ok;
            p2.Plan = "SuperGrok";
            p2.Windows = windows;
            p2.Footnotes = footnotes;
            p2.FetchedAt = now;
            p2.Source = "api";
            return p2;
        }

        // weeklyPct(): the explicit credit percentage wins, then the sum of the product pools,
        // then used / monthlyLimit.
        private static double? WeeklyPct(IDictionary<string, object> cfg)
        {
            if (cfg == null) return null;

            double? explicitPct = JsonUtil.LooseNum(JsonUtil.Get(cfg, "creditUsagePercent"));
            if (explicitPct.HasValue) return explicitPct;

            List<double> products = ProductPercents(cfg);
            if (products.Count > 0)
            {
                double sum = 0;
                foreach (double value in products) sum += value;
                return sum;
            }

            double? used = FieldVal(cfg, "used");
            double? limit = FieldVal(cfg, "monthlyLimit");
            if (used.HasValue && limit.HasValue && limit.Value > 0)
                return used.Value / limit.Value * 100.0;
            return null;
        }

        private static List<double> ProductPercents(IDictionary<string, object> cfg)
        {
            List<double> values = new List<double>();
            if (cfg == null) return values;

            IList<object> list = JsonUtil.List(JsonUtil.Get(cfg, "productUsage"));
            if (list == null) return values;

            foreach (object item in list)
            {
                IDictionary<string, object> p = JsonUtil.Map(item);
                if (p == null) continue;
                if (string.IsNullOrEmpty(JsonUtil.Str(p, "product", null))) continue;
                double? n = JsonUtil.LooseNum(JsonUtil.Get(p, "usagePercent"));
                if (n.HasValue) values.Add(n.Value);
            }
            return values;
        }

        // The .val wrapper the billing config uses for money/usage numbers.
        private static double? FieldVal(IDictionary<string, object> cfg, string key)
        {
            if (cfg == null) return null;
            return JsonUtil.LooseNum(JsonUtil.Get(AdapterUtil.MapOf(cfg, key), "val"));
        }

        private static string PeriodEnd(IDictionary<string, object> cfg)
        {
            if (cfg == null) return null;
            string end = AdapterUtil.StrPath(cfg, "currentPeriod", "end", null);
            if (!string.IsNullOrEmpty(end)) return end;
            return JsonUtil.Str(cfg, "billingPeriodEnd", null);
        }

        private static bool HasActivePeriod(IDictionary<string, object> cfg, DateTime nowUtc)
        {
            if (cfg == null) return false;

            string start = AdapterUtil.StrPath(cfg, "currentPeriod", "start", null);
            if (string.IsNullOrEmpty(start)) start = JsonUtil.Str(cfg, "billingPeriodStart", null);
            string end = PeriodEnd(cfg);
            if (string.IsNullOrEmpty(start) && string.IsNullOrEmpty(end)) return false;

            DateTime? startDt = Models.ParseIso(start);
            DateTime? endDt = Models.ParseIso(end);
            double now = AdapterUtil.ToEpochMs(nowUtc);
            double startMs = startDt.HasValue ? AdapterUtil.ToEpochMs(startDt.Value) : double.NaN;
            double endMs = endDt.HasValue ? AdapterUtil.ToEpochMs(endDt.Value) : double.NaN;

            if (!double.IsNaN(startMs) && !double.IsNaN(endMs)) return startMs <= now && now < endMs;
            if (!double.IsNaN(endMs)) return now < endMs;
            if (!double.IsNaN(startMs)) return startMs <= now;
            return false;
        }

        private static void PeriodMeta(IDictionary<string, object> cfg, bool fromCreditsPercent,
            out string kind, out string label)
        {
            string type = JsonUtil.Str(AdapterUtil.MapOf(cfg, "currentPeriod"), "type", null);
            if (type == null) type = "";

            if (Regex.IsMatch(type, "WEEKLY", RegexOptions.IgnoreCase)) { kind = Kind.Weekly; label = "Weekly"; return; }
            if (Regex.IsMatch(type, "MONTHLY", RegexOptions.IgnoreCase)) { kind = Kind.Monthly; label = "Monthly"; return; }
            if (fromCreditsPercent) { kind = Kind.Weekly; label = "Weekly"; return; }

            double? limit = FieldVal(cfg, "monthlyLimit");
            if (limit.HasValue && limit.Value > 0) { kind = Kind.Monthly; label = "Monthly"; return; }

            kind = Kind.Weekly;
            label = "Weekly";
        }

        // ------------------------------------------------------------------------------------------
        // grpc-web / protobuf parsing (hand written port of the same three functions in grok.js)
        // ------------------------------------------------------------------------------------------

        // Returns the credit usage percent carried by the response, or null when there is none.
        public static double? ParseCreditsGrpcWeb(byte[] frame)
        {
            if (frame == null || frame.Length == 0) return null;

            byte[] message = FirstGrpcWebDataMessage(frame);
            if (message == null) return null;

            byte[] config = null;
            double? percent = null;
            if (!WalkProto(message, delegate(int field, int wire, byte[] bytes, ulong varint)
                {
                    if (field == 1 && wire == 2) config = bytes;
                })) return null;
            if (config == null) config = message;

            if (!WalkProto(config, delegate(int field, int wire, byte[] bytes, ulong varint)
            {
                if (field != 1) return;
                if (wire == 5 && bytes.Length == 4) percent = ReadFloatLE(bytes);
                else if (wire == 1 && bytes.Length == 8) percent = ReadDoubleLE(bytes);
            })) return null;

            if (!percent.HasValue || double.IsNaN(percent.Value) || double.IsInfinity(percent.Value)) return null;
            return percent;
        }

        // A grpc-web body is a sequence of 5-byte headers (1 flag byte + 4 length bytes, big
        // endian) followed by that many message bytes. The trailers ("grpc-status: 0") are skipped.
        private static byte[] FirstGrpcWebDataMessage(byte[] bytes)
        {
            int offset = 0;
            while (offset + 5 <= bytes.Length)
            {
                byte compressed = bytes[offset];
                uint length = ((uint)bytes[offset + 1] << 24) | ((uint)bytes[offset + 2] << 16)
                    | ((uint)bytes[offset + 3] << 8) | (uint)bytes[offset + 4];
                offset += 5;

                long end = (long)offset + (long)length;
                if (end > bytes.Length) return null;

                byte[] frame = new byte[length];
                Array.Copy(bytes, offset, frame, 0, (int)length);
                offset += (int)length;

                if (compressed == 0x80) continue;
                if (compressed != 0) continue;

                if (frame.Length >= 11)
                {
                    bool trailer = true;
                    for (int i = 0; i < 11; i++)
                    {
                        if (frame[i] != (byte)"grpc-status"[i]) { trailer = false; break; }
                    }
                    if (trailer) continue;
                }
                return frame;
            }
            return null;
        }

        // Walks the fields of a protobuf message. Wire 2 (length delimited), 5 (fixed32) and
        // 1 (fixed64) hand over their bytes; wire 0 (varint) hands over the decoded value, the
        // same split the JS port makes. Returns false as soon as a byte sequence is malformed:
        // a truncated tag, a length that overruns the buffer (or a 32-bit int) or an unknown
        // wire type, so the caller never maps a half decoded message.
        private delegate void ProtoFieldHandler(int field, int wire, byte[] bytes, ulong varint);

        private static bool WalkProto(byte[] buf, ProtoFieldHandler onField)
        {
            if (buf == null || onField == null) return true;

            int offset = 0;
            while (offset < buf.Length)
            {
                ulong tag;
                int next;
                if (!ReadVarint(buf, offset, out tag, out next)) return false;
                int field = (int)(tag >> 3);
                int wire = (int)(tag & 7);
                offset = next;

                if (wire == 2)
                {
                    ulong len;
                    if (!ReadVarint(buf, offset, out len, out next)) return false;
                    long end = (long)next + (long)len;
                    if (end > buf.Length) return false;

                    byte[] bytes = new byte[len];
                    Array.Copy(buf, next, bytes, 0, (int)len);
                    onField(field, wire, bytes, 0);
                    offset = (int)end;
                }
                else if (wire == 5)
                {
                    if (offset + 4 > buf.Length) return false;
                    byte[] bytes = new byte[4];
                    Array.Copy(buf, offset, bytes, 0, 4);
                    onField(field, wire, bytes, 0);
                    offset += 4;
                }
                else if (wire == 1)
                {
                    if (offset + 8 > buf.Length) return false;
                    byte[] bytes = new byte[8];
                    Array.Copy(buf, offset, bytes, 0, 8);
                    onField(field, wire, bytes, 0);
                    offset += 8;
                }
                else if (wire == 0)
                {
                    // The decoded value is never used by this walker, and a wire-0 varint can
                    // be a full 64-bit value (up to ten bytes) — wider than the 32 bits
                    // ReadVarint accepts for a tag or a length. Skip the bytes instead of
                    // decoding them, so a large unused varint does not discard the message.
                    int after;
                    if (!SkipVarint(buf, offset, out after)) return false;
                    onField(field, wire, null, 0);
                    offset = after;
                }
                else return false;
            }
            return true;
        }

        // Base-128 varint, strictly bounded: at most five bytes (a 32-bit value is all a tag or
        // a length needs) and no silent wrap to zero. The JS port masks the accumulator with
        // `>>> 0`, which turns a length such as 0x100000005 into a small one and makes the
        // walk read the wrong bytes, so a varint that does not fit a uint fails instead.
        private static bool ReadVarint(byte[] buf, int offset, out ulong value, out int next)
        {
            value = 0;
            next = offset;
            ulong result = 0;
            int shift = 0;
            int pos = offset;
            while (pos < buf.Length && shift <= 28)
            {
                byte b = buf[pos++];
                // The fifth byte contributes bits 28..34; anything above bit 31 cannot be a uint.
                if (shift == 28 && (b & 0x70) != 0) return false;
                result |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                {
                    value = result;
                    next = pos;
                    return true;
                }
                shift += 7;
            }
            return false;
        }

        // Skips a wire-0 (varint) value without decoding it. A protobuf varint can carry a full
        // uint64 across at most ten bytes; only the low 32 bits are ever needed for a tag or a
        // length, which is why ReadVarint above stays strict. This allows up to ten bytes and
        // fails the parse on a sequence that keeps going past the tenth byte (an unterminated
        // varint longer than 10 bytes) or runs off the end of the buffer.
        private static bool SkipVarint(byte[] buf, int offset, out int next)
        {
            next = offset;
            int pos = offset;
            int count = 0;
            while (pos < buf.Length)
            {
                byte b = buf[pos++];
                count++;
                // A byte without the continuation bit ends the varint (checked before the
                // length check, so a legitimate ten-byte varint still terminates on the tenth).
                if ((b & 0x80) == 0) { next = pos; return true; }
                if (count >= 10) return false;
            }
            return false;
        }

        private static double ReadFloatLE(byte[] bytes)
        {
            byte[] le = LittleEndian(bytes);
            return BitConverter.ToSingle(le, 0);
        }

        private static double ReadDoubleLE(byte[] bytes)
        {
            byte[] le = LittleEndian(bytes);
            return BitConverter.ToDouble(le, 0);
        }

        private static byte[] LittleEndian(byte[] bytes)
        {
            if (BitConverter.IsLittleEndian) return bytes;
            byte[] reversed = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) reversed[i] = bytes[bytes.Length - 1 - i];
            return reversed;
        }

        // ------------------------------------------------------------------------------------------
        // fetch
        // ------------------------------------------------------------------------------------------

        public ProviderSnapshot FetchUsage(AdapterContext ctx)
        {
            DateTime now = ctx != null && ctx.UtcNow != null ? ctx.UtcNow() : DateTime.UtcNow;
            Paths paths = ctx == null ? null : ctx.Paths;

            bool credsPresent = false;
            try
            {
                credsPresent = paths != null && paths.GrokAuth != null && File.Exists(paths.GrokAuth);
            }
            catch (Exception) { credsPresent = false; }

            bool cliOnPath = false;
            try { cliOnPath = paths != null && paths.FindOnPath("grok") != null; }
            catch (Exception) { cliOnPath = false; }

            if (!credsPresent && !cliOnPath)
                return Empty(State.NotInstalled, null, "Install Grok Build, then run: grok login", now);
            if (!credsPresent)
                return Empty(State.LoggedOut, null, "Run: grok login", now);

            long mtimeTicks;
            IDictionary<string, object> json;
            try
            {
                string authPath = paths.GrokAuth;
                mtimeTicks = File.GetLastWriteTimeUtc(authPath).Ticks;
                json = JsonUtil.Map(JsonUtil.Parse(File.ReadAllText(authPath)));
            }
            catch (Exception) { return Empty(State.LoggedOut, null, "Run: grok login", now); }

            if (json == null) return Empty(State.LoggedOut, null, "Run: grok login", now);

            string topKey;
            IDictionary<string, object> entry = PickEntry(json, out topKey);
            if (entry == null)
                return Empty(State.Unknown,
                    "Grok is on an API key \u2014 SuperGrok weekly % is a subscription meter", null, now);

            string token = JsonUtil.Str(entry, "key", null);
            string entryKey = token;
            bool refreshed = false;
            bool refreshEnabled = ctx == null || ctx.Config == null || ctx.Config.RefreshTokens(AdapterId);
            double nowMs = AdapterUtil.ToEpochMs(now);
            double? expMs = ExpiryMs(entry, token);

            if (refreshEnabled && expMs.HasValue && expMs.Value != 0 && expMs.Value - nowMs < RefreshSkewMs)
            {
                try
                {
                    string next = RefreshGrok(ctx, entry, paths.GrokAuth, mtimeTicks, topKey, entryKey, now);
                    if (next != null) { token = next; refreshed = true; }
                }
                catch (Exception) { /* keep the token we have */ }
            }

            HttpResult res;
            try { res = HttpHelpers.GetJson(ctx == null ? null : ctx.Http, BillingUrl, CliHeaders(token, entry)); }
            catch (Exception ex) { return FetchFailed(ex, now); }

            int status = res == null ? 0 : res.Status;

            // 401/403: rotate once, then retry (a token that was just refreshed is not rotated
            // a second time in the same pass).
            if ((status == 401 || status == 403) && refreshEnabled && !refreshed)
            {
                try
                {
                    string next = RefreshGrok(ctx, entry, paths.GrokAuth, mtimeTicks, topKey, entryKey, now);
                    if (next != null)
                    {
                        token = next;
                        res = HttpHelpers.GetJson(ctx == null ? null : ctx.Http, BillingUrl, CliHeaders(token, entry));
                    }
                }
                catch (Exception) { /* fall through to the status handling below */ }
                status = res == null ? 0 : res.Status;
            }

            if (status == 401 || status == 403)
                return Empty(State.LoggedOut, null, "Run grok login (token expired)", now);

            if (status == 429)
            {
                ProviderSnapshot p = Empty(State.FetchFailed, "rate limited", null, now);
                p.RateLimited = true;
                return p;
            }

            if (status != 200 || res.Json == null)
                return Empty(State.FetchFailed, "HTTP " + status, null, now);

            IDictionary<string, object> billing = AdapterUtil.MapOf(res.Json, "config");
            if (billing == null) billing = JsonUtil.Map(res.Json);
            if (billing != null && WeeklyPct(billing) == null)
            {
                try
                {
                    double? credit = FetchCreditsGrpc(ctx, token);
                    if (credit.HasValue) SetCreditUsagePercent(billing, credit.Value);
                }
                catch (Exception) { /* the REST period still maps */ }
            }

            return MapBilling(res.Json, now);
        }

        // pickEntry(): every top-level value that carries a "key" is an account; the newest
        // create_time wins (string compare, exactly like localeCompare on ISO dates).
        private static IDictionary<string, object> PickEntry(IDictionary<string, object> json, out string topKey)
        {
            topKey = null;
            if (json == null) return null;

            List<string> keys = new List<string>();
            List<IDictionary<string, object>> entries = new List<IDictionary<string, object>>();
            foreach (KeyValuePair<string, object> kv in json)
            {
                IDictionary<string, object> value = JsonUtil.Map(kv.Value);
                if (value == null) continue;
                // JS: values.filter((v) => v && v.key) — a missing, non-string or empty key is
                // not a signed-in account, so it is never selected.
                if (string.IsNullOrEmpty(JsonUtil.Str(value, "key", null))) continue;
                keys.Add(kv.Key);
                entries.Add(value);
            }
            if (entries.Count == 0) return null;

            // Stable insertion sort, descending by create_time (JS sort is stable too).
            for (int i = 1; i < entries.Count; i++)
            {
                string key = keys[i];
                IDictionary<string, object> moving = entries[i];
                int j = i - 1;
                while (j >= 0 && string.CompareOrdinal(CreateTime(entries[j]), CreateTime(moving)) < 0)
                {
                    entries[j + 1] = entries[j];
                    keys[j + 1] = keys[j];
                    j--;
                }
                entries[j + 1] = moving;
                keys[j + 1] = key;
            }
            IDictionary<string, object> entry = entries[0];

            // JS: Object.keys(json).find((k) => json[k] === entry)
            foreach (KeyValuePair<string, object> kv in json)
            {
                if (object.ReferenceEquals(kv.Value, entry)) { topKey = kv.Key; break; }
            }
            return entry;
        }

        private static string CreateTime(IDictionary<string, object> entry)
        {
            string time = JsonUtil.Str(entry, "create_time", null);
            return time == null ? "" : time;
        }

        private static double? ExpiryMs(IDictionary<string, object> entry, string token)
        {
            string expiresAt = JsonUtil.Str(entry, "expires_at", null);
            DateTime? parsed = Models.ParseIso(expiresAt);
            if (parsed.HasValue) return AdapterUtil.ToEpochMs(parsed.Value);
            return JwtExpMs(token);
        }

        // A JWT's exp claim is the fallback when auth.json carries no expires_at.
        private static double? JwtExpMs(string token)
        {
            try
            {
                if (string.IsNullOrEmpty(token)) return null;
                string[] parts = token.Split('.');
                if (parts.Length < 2) return null;

                string payload = Base64UrlDecode(parts[1]);
                if (payload == null) return null;

                IDictionary<string, object> map = JsonUtil.Map(JsonUtil.Parse(payload));
                if (map == null) return null;

                double? exp = JsonUtil.LooseNum(JsonUtil.Get(map, "exp"));
                if (!exp.HasValue || exp.Value == 0) return null;
                return exp.Value * 1000.0;
            }
            catch (Exception) { return null; }
        }

        private static string Base64UrlDecode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string padded = text.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            byte[] bytes = Convert.FromBase64String(padded);
            return Encoding.UTF8.GetString(bytes);
        }

        private static Dictionary<string, string> CliHeaders(string token, IDictionary<string, object> entry)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + (token == null ? "" : token);
            headers["Accept"] = "application/json";
            headers["X-XAI-Token-Auth"] = "xai-grok-cli";
            headers["User-Agent"] = "grok-cli/1.0";

            string userId = entry == null ? null : JsonUtil.Str(entry, "user_id", null);
            if (!string.IsNullOrEmpty(userId)) headers["x-userid"] = userId;
            return headers;
        }

        private static Dictionary<string, string> AcceptJson()
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["Accept"] = "application/json";
            return headers;
        }

        // refreshGrok(): OIDC refresh grant, then a compare-and-swap write of the account we
        // actually used (so a file the CLI rewrote in the meantime is never clobbered).
        private static string RefreshGrok(AdapterContext ctx, IDictionary<string, object> entry, string credsPath,
            long mtimeTicks, string topKey, string entryKey, DateTime nowUtc)
        {
            string issuer = JsonUtil.Str(entry, "oidc_issuer", null);
            if (string.IsNullOrEmpty(issuer)) issuer = DefaultIssuer;
            while (issuer.EndsWith("/", StringComparison.Ordinal)) issuer = issuer.Substring(0, issuer.Length - 1);

            string clientId = JsonUtil.Str(entry, "oidc_client_id", null);
            string refreshToken = JsonUtil.Str(entry, "refresh_token", null);
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(refreshToken)) return null;

            IHttp http = ctx == null ? null : ctx.Http;
            if (http == null) return null;

            string tokenUrl = issuer + "/oauth/token";
            try
            {
                HttpResult disc = HttpHelpers.GetJson(http, issuer + "/.well-known/openid-configuration", AcceptJson());
                if (disc != null && disc.Status == 200 && disc.Json != null)
                {
                    string endpoint = JsonUtil.Str(JsonUtil.Map(disc.Json), "token_endpoint", null);
                    if (!string.IsNullOrEmpty(endpoint)) tokenUrl = endpoint;
                }
            }
            catch (Exception) { /* use the default token endpoint */ }

            Dictionary<string, string> fields = new Dictionary<string, string>();
            fields["grant_type"] = "refresh_token";
            fields["refresh_token"] = refreshToken;
            fields["client_id"] = clientId;

            HttpResult res = HttpHelpers.PostForm(http, tokenUrl, AcceptJson(), fields);
            if (res == null || res.Status != 200 || res.Json == null) return null;

            IDictionary<string, object> granted = JsonUtil.Map(res.Json);
            string access = granted == null ? null : JsonUtil.Str(granted, "access_token", null);
            if (string.IsNullOrEmpty(access)) return null;

            try
            {
                if (!File.Exists(credsPath)) return access;
                if (File.GetLastWriteTimeUtc(credsPath).Ticks != mtimeTicks) return access;

                IDictionary<string, object> root = JsonUtil.Map(JsonUtil.Parse(File.ReadAllText(credsPath)));
                IDictionary<string, object> current = root == null || topKey == null
                    ? null
                    : JsonUtil.Map(JsonUtil.Get(root, topKey));

                // JS: if (!current || current.key !== entry.key) return access_token. The
                // account we refreshed must still be the one that sits at that key.
                if (current == null) return access;
                if (!string.Equals(JsonUtil.Str(current, "key", null), entryKey, StringComparison.Ordinal))
                    return access;

                Dictionary<string, object> updated = CopyMap(current);
                updated["key"] = access;

                string rotated = granted == null ? null : JsonUtil.Str(granted, "refresh_token", null);
                if (!string.IsNullOrEmpty(rotated)) updated["refresh_token"] = rotated;

                double? expiresIn = granted == null ? null : JsonUtil.LooseNum(JsonUtil.Get(granted, "expires_in"));
                if (expiresIn.HasValue && expiresIn.Value != 0)
                {
                    updated["expires_at"] = Models.ToIso(
                        AdapterUtil.FromEpochMs(AdapterUtil.ToEpochMs(nowUtc) + expiresIn.Value * 1000.0).Value);
                }

                root[topKey] = updated;

                // One atomic replace: the destination is never deleted first, so a crash or a
                // failing write in between cannot leave the user's credentials missing, and a
                // failing replace keeps the file we started with (the catch only removes the
                // temp copy). Written without a BOM, so the next reader sees plain JSON.
                string tmp = credsPath + ".tmp";
                File.WriteAllText(tmp, AdapterUtil.IndentedJson(root), new UTF8Encoding(false));

                // The check and the swap are kept as close together as the API allows: if the
                // CLI touched the file in the meantime we drop the temp copy instead.
                if (File.GetLastWriteTimeUtc(credsPath).Ticks != mtimeTicks)
                {
                    DeleteQuietly(tmp);
                    return access;
                }
                File.Replace(tmp, credsPath, null);
            }
            catch (Exception)
            {
                // The in-memory token is still usable. Only remove the scratch copy when the
                // credentials file is still there: a File.Replace that failed after it had
                // already unlinked the destination (ERROR_UNABLE_TO_MOVE_REPLACEMENT) leaves the
                // .tmp file as the only copy, so deleting it would destroy the credentials.
                string tmp = credsPath + ".tmp";
                if (File.Exists(credsPath)) DeleteQuietly(tmp);
            }

            return access;
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { /* nothing to clean, or nothing we can do */ }
        }

        private static double? FetchCreditsGrpc(AdapterContext ctx, string token)
        {
            IHttp http = ctx == null ? null : ctx.Http;
            if (http == null) return null;

            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + (token == null ? "" : token);
            headers["Content-Type"] = "application/grpc-web+proto";
            headers["x-grpc-web"] = "1";
            headers["X-XAI-Token-Auth"] = "xai-grok-cli";
            headers["Accept"] = "application/grpc-web+proto";
            headers["Origin"] = "https://grok.com";
            headers["Referer"] = "https://grok.com/?_s=usage";
            headers["User-Agent"] = "grok-cli/1.0";

            HttpResult res = http.Request("POST", GrpcCreditsUrl, headers, GrpcRequest, GrpcTimeoutMs);
            if (res == null || res.Status != 200 || res.Raw == null) return null;

            string grpcStatus = null;
            if (res.Headers != null)
            {
                string value;
                if (res.Headers.TryGetValue("grpc-status", out value)) grpcStatus = value;
            }
            if (!string.IsNullOrEmpty(grpcStatus) && grpcStatus != "0") return null;

            return ParseCreditsGrpcWeb(res.Raw);
        }

        private static void SetCreditUsagePercent(IDictionary<string, object> billing, double percent)
        {
            Dictionary<string, object> map = billing as Dictionary<string, object>;
            if (map != null) { map["creditUsagePercent"] = percent; return; }

            IDictionary<string, object> copy = CopyMap(billing);
            if (copy == null) return;
            copy["creditUsagePercent"] = percent;
        }

        // ------------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------------

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

        private static Dictionary<string, object> CopyMap(IDictionary<string, object> source)
        {
            if (source == null) return null;
            Dictionary<string, object> copy = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> kv in source) copy[kv.Key] = kv.Value;
            return copy;
        }
    }
}
