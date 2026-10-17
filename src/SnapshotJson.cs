// snapshot.json serialisation. Shape is kept identical to reference/js/cache.js's JSON.stringify
// output so usage.py and the model-router keep reading the file (milestone M1).
// Language level: C# 5 (in-box csc.exe).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace UsageMonitor
{
    public static class SnapshotJson
    {
        public static string Serialize(Snapshot s)
        {
            if (s == null) return null;

            JsonWriter w = new JsonWriter();
            w.BeginRootObject();
            w.Key("generated_at");
            w.String(Models.ToIso(s.GeneratedAt));
            w.Key("poll_interval_secs");
            w.Number((long)s.PollIntervalSecs);

            w.Key("providers");
            w.BeginArray();
            if (s.Providers != null)
            {
                foreach (ProviderSnapshot p in s.Providers)
                {
                    if (p == null) continue;
                    w.BeginObject();

                    w.Key("id");
                    w.String(p.Id);
                    w.Key("display_name");
                    w.String(p.DisplayName);

                    w.Key("status");
                    w.BeginObject();
                    ProviderStatus st = p.Status;
                    if (st != null)
                    {
                        w.Key("state");
                        w.String(st.State);
                        // undefined in JavaScript => the key is left out of the file entirely.
                        if (!string.IsNullOrEmpty(st.Hint)) { w.Key("hint"); w.String(st.Hint); }
                        if (!string.IsNullOrEmpty(st.Message)) { w.Key("message"); w.String(st.Message); }
                        if (!string.IsNullOrEmpty(st.Reason)) { w.Key("reason"); w.String(st.Reason); }
                        if (st.RetryAt.HasValue) { w.Key("retry_at"); w.String(Models.ToIso(st.RetryAt.Value)); }
                        if (st.AgeSecs.HasValue) { w.Key("age_secs"); w.Number((long)st.AgeSecs.Value); }
                    }
                    w.EndObject();

                    w.Key("plan");
                    w.String(p.Plan);

                    w.Key("windows");
                    w.BeginArray();
                    if (p.Windows != null)
                    {
                        foreach (UsageWindow win in p.Windows)
                        {
                            if (win == null) continue;
                            w.BeginObject();
                            w.Key("kind"); w.String(win.Kind);
                            w.Key("label"); w.String(win.Label);
                            w.Key("used_pct"); w.Double(win.UsedPct);
                            w.Key("remaining_pct"); w.Double(win.RemainingPct);
                            w.Key("resets_at"); w.String(win.ResetsAt.HasValue ? Models.ToIso(win.ResetsAt.Value) : null);
                            w.EndObject();
                        }
                    }
                    w.EndArray();

                    List<string> footnotes = p.Footnotes;
                    if (footnotes != null && footnotes.Count > 0)
                    {
                        w.Key("footnotes");
                        w.BeginArray();
                        foreach (string f in footnotes) w.String(f);
                        w.EndArray();
                    }

                    w.Key("fetched_at");
                    w.String(Models.ToIso(p.FetchedAt));
                    w.Key("source");
                    w.String(p.Source);

                    w.EndObject();
                }
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        // Tolerant like cache.js loadSnapshot(): a provider is only kept when it has a known id, an
        // array of windows, a status with a string state and a parseable fetched_at. Everything else
        // (garbage, missing file, no usable provider) yields null so callers fall back to defaults.
        public static Snapshot Parse(string json)
        {
            IDictionary<string, object> data = JsonUtil.Map(JsonUtil.Parse(json));
            if (data == null) return null;

            IList<object> providers = JsonUtil.List(JsonUtil.Get(data, "providers"));
            if (providers == null) return null;

            List<ProviderSnapshot> kept = new List<ProviderSnapshot>();
            foreach (object item in providers)
            {
                IDictionary<string, object> po = JsonUtil.Map(item);
                if (po == null) continue;

                string id = JsonUtil.Str(po, "id", null);
                if (id == null || Array.IndexOf(Models.ProviderIds, id) < 0) continue;

                if (JsonUtil.List(JsonUtil.Get(po, "windows")) == null) continue;

                IDictionary<string, object> so = JsonUtil.Map(JsonUtil.Get(po, "status"));
                if (so == null) continue;
                string state = JsonUtil.Str(so, "state", null);
                if (state == null) continue;

                DateTime? fetched = Models.ParseIso(JsonUtil.Str(po, "fetched_at", null));
                if (!fetched.HasValue) continue;

                ProviderSnapshot p = new ProviderSnapshot();
                p.Id = id;
                p.DisplayName = JsonUtil.Str(po, "display_name", null);

                ProviderStatus st = new ProviderStatus();
                st.State = state;
                st.Hint = JsonUtil.Str(so, "hint", null);
                st.Message = JsonUtil.Str(so, "message", null);
                st.Reason = JsonUtil.Str(so, "reason", null);
                st.RetryAt = Models.ParseIso(JsonUtil.Str(so, "retry_at", null));
                st.AgeSecs = JsonUtil.Int(JsonUtil.Get(so, "age_secs"));
                p.Status = st;

                p.Plan = JsonUtil.Str(po, "plan", null);

                foreach (object wo in JsonUtil.List(JsonUtil.Get(po, "windows")))
                {
                    IDictionary<string, object> wm = JsonUtil.Map(wo);
                    if (wm == null) continue;
                    string kind = JsonUtil.Str(wm, "kind", null);
                    string label = JsonUtil.Str(wm, "label", null);
                    if (kind == null || label == null) continue;
                    p.Windows.Add(Models.WindowOf(kind, label, JsonUtil.Get(wm, "used_pct"),
                                                  JsonUtil.Str(wm, "resets_at", null)));
                }

                p.Footnotes = JsonUtil.StrList(JsonUtil.Get(po, "footnotes"));

                p.FetchedAt = fetched.Value;
                p.Source = JsonUtil.Str(po, "source", "none");
                kept.Add(p);
            }

            if (kept.Count == 0) return null;

            Snapshot s = new Snapshot();
            DateTime? generated = Models.ParseIso(JsonUtil.Str(data, "generated_at", null));
            s.GeneratedAt = generated.HasValue ? generated.Value : DateTime.UtcNow;
            s.PollIntervalSecs = JsonUtil.Int(JsonUtil.Get(data, "poll_interval_secs")) ?? 0;
            s.Providers = kept;
            return s;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Reading. Any value produced by JavaScriptSerializer may be int, long, decimal, double,
    // string, bool, null, object[], ArrayList or Dictionary<string,object>: normalise all.
    // Shared with AppConfig (same "pure logic" layer).
    // ------------------------------------------------------------------------------------------
    internal static class JsonUtil
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        internal static object Parse(string json)
        {
            if (json == null) return null;
            string text = json.Trim();
            if (text.Length == 0) return null;
            try { return Serializer.DeserializeObject(text); }
            catch (Exception) { return null; }
        }

        internal static IDictionary<string, object> Map(object value)
        {
            if (value is IDictionary<string, object>) return (IDictionary<string, object>)value;
            return null;
        }

        internal static IList<object> List(object value)
        {
            if (value is object[]) return (IList<object>)value;
            if (value is ArrayList)
            {
                ArrayList raw = (ArrayList)value;
                List<object> items = new List<object>(raw.Count);
                foreach (object item in raw) items.Add(item);
                return items;
            }
            return null;
        }

        internal static object Get(IDictionary<string, object> map, string key)
        {
            if (map == null || key == null) return null;
            object value;
            return map.TryGetValue(key, out value) ? value : null;
        }

        internal static string Str(IDictionary<string, object> map, string key, string fallback)
        {
            object value = Get(map, key);
            return value is string ? (string)value : fallback;
        }

        internal static bool? Bool(object value)
        {
            return value is bool ? (bool?)value : null;
        }

        // Every numeric CLR type a JSON body can arrive as. No clamping: unlike Models.Percentage
        // this is for values where a negative number is meaningful (coordinates, versions).
        internal static double? Num(object value)
        {
            if (value is double)
            {
                double d = (double)value;
                return double.IsNaN(d) || double.IsInfinity(d) ? (double?)null : d;
            }
            if (value is float) return (double)(float)value;
            if (value is decimal) return (double)(decimal)value;
            if (value is int) return (double)(int)value;
            if (value is long) return (double)(long)value;
            if (value is short) return (double)(short)value;
            if (value is byte) return (double)(byte)value;
            if (value is sbyte) return (double)(sbyte)value;
            if (value is ushort) return (double)(ushort)value;
            if (value is uint) return (double)(uint)value;
            if (value is ulong) return (double)(ulong)value;
            return null;
        }

        // Num() plus numeric strings, the way JavaScript's Number() reads "poll_interval_secs".
        internal static double? LooseNum(object value)
        {
            double? d = Num(value);
            if (d.HasValue) return d;
            string text = value as string;
            if (text == null || text.Trim().Length == 0) return null;
            double n;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return null;
            return double.IsNaN(n) || double.IsInfinity(n) ? (double?)null : n;
        }

        internal static int? Int(object value)
        {
            double? d = LooseNum(value);
            if (!d.HasValue) return null;
            double rounded = Math.Round(d.Value, MidpointRounding.AwayFromZero);
            if (rounded < int.MinValue || rounded > int.MaxValue) return null;
            return (int)rounded;
        }

        internal static List<string> StrList(object value)
        {
            List<string> items = new List<string>();
            IList<object> raw = List(value);
            if (raw == null) return items;
            foreach (object item in raw)
            {
                if (item is string) items.Add((string)item);
            }
            return items;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Writing. JavaScriptSerializer cannot indent, so JSON.stringify(value, null, 2) is
    // reproduced by hand: two spaces, one space after the colon, no HTML escaping, no escaped
    // solidus, shortest round-trip number form (so 98.0 stays "98"), null for unknown.
    // ------------------------------------------------------------------------------------------
    internal sealed class JsonWriter
    {
        private readonly StringBuilder sb = new StringBuilder(1024);

        // One flag per open container: has anything been written into it yet.
        private readonly List<bool> hasContent = new List<bool>();

        // A key has been written, so the next value needs no separator of its own.
        private bool pendingKey = false;

        public override string ToString()
        {
            return sb.ToString();
        }

        private void Separator()
        {
            if (hasContent.Count == 0) return;
            if (hasContent[hasContent.Count - 1]) sb.Append(',');
            sb.Append('\n');
            for (int i = 0; i < hasContent.Count; i++) sb.Append("  ");
        }

        public void BeginRootObject()
        {
            sb.Append('{');
            hasContent.Add(false);
        }

        public void BeginObject()
        {
            BeforeValue();
            sb.Append('{');
            hasContent.Add(false);
        }

        public void BeginArray()
        {
            BeforeValue();
            sb.Append('[');
            hasContent.Add(false);
        }

        private void BeforeValue()
        {
            if (!pendingKey) Separator();
            pendingKey = false;
        }

        private void AfterValue()
        {
            pendingKey = false;
            if (hasContent.Count > 0) hasContent[hasContent.Count - 1] = true;
        }

        private void CloseContainer(char end)
        {
            bool hadContent = hasContent[hasContent.Count - 1];
            hasContent.RemoveAt(hasContent.Count - 1);
            pendingKey = false;
            if (hadContent)
            {
                sb.Append('\n');
                for (int i = 0; i < hasContent.Count; i++) sb.Append("  ");
            }
            sb.Append(end);
            if (hasContent.Count > 0) hasContent[hasContent.Count - 1] = true;
        }

        public void EndObject() { CloseContainer('}'); }

        public void EndArray() { CloseContainer(']'); }

        public void Key(string name)
        {
            Separator();
            sb.Append('"');
            Escape(name == null ? "" : name);
            sb.Append("\": ");
            pendingKey = true;
        }

        public void String(string value)
        {
            BeforeValue();
            if (value == null) sb.Append("null");
            else { sb.Append('"'); Escape(value); sb.Append('"'); }
            AfterValue();
        }

        public void Number(long value)
        {
            BeforeValue();
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
            AfterValue();
        }

        // Coordinates and other optional numbers.
        public void NullableNumber(long? value)
        {
            BeforeValue();
            if (!value.HasValue) sb.Append("null");
            else sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
            AfterValue();
        }

        public void Bool(bool value)
        {
            BeforeValue();
            sb.Append(value ? "true" : "false");
            AfterValue();
        }

        public void Double(double? value)
        {
            BeforeValue();
            if (!value.HasValue) sb.Append("null");
            else
            {
                double d = value.Value;
                // JavaScript prints NaN/Infinity as null.
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            }
            AfterValue();
        }

        // Mirrors JavaScript escaping: control characters escaped, & < > / and non-ASCII as-is.
        private void Escape(string text)
        {
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
        }
    }
}
