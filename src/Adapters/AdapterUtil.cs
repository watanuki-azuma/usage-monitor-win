// Small helpers shared by the M2 adapters (and the M3 ones that follow them). New file, so nothing
// from M1 has to be changed to accommodate it.
// Language level: C# 5 (in-box csc.exe).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UsageMonitor
{
    // Convenience wrappers around the JSON shapes JavaScriptSerializer.DeserializeObject() returns,
    // so the adapters read as "get the object at key X and keep going" instead of casting twice.
    public static class AdapterUtil
    {
        public static IDictionary<string, object> Map(object value)
        {
            return JsonUtil.Map(value);
        }

        public static IList<object> List(object value)
        {
            return JsonUtil.List(value);
        }

        public static IDictionary<string, object> MapOf(object container, string key)
        {
            return JsonUtil.Map(JsonUtil.Get(JsonUtil.Map(container), key));
        }

        public static IList<object> ListOf(object container, string key)
        {
            return JsonUtil.List(JsonUtil.Get(JsonUtil.Map(container), key));
        }

        public static object Get(object container, string key)
        {
            return JsonUtil.Get(JsonUtil.Map(container), key);
        }

        public static object GetPath(object container, string firstKey, string secondKey)
        {
            return JsonUtil.Get(MapOf(container, firstKey), secondKey);
        }

        public static string Str(object container, string key, string fallback)
        {
            return JsonUtil.Str(JsonUtil.Map(container), key, fallback);
        }

        public static string StrPath(object container, string firstKey, string secondKey, string fallback)
        {
            IDictionary<string, object> inner = MapOf(container, firstKey);
            if (inner == null) return fallback;
            return JsonUtil.Str(inner, secondKey, fallback);
        }

        public static bool Has(object container, string key)
        {
            return JsonUtil.Get(JsonUtil.Map(container), key) != null;
        }

        // Truthy string check: "" counts as absent, exactly like JavaScript's `if (x.y)`.
        public static bool HasText(object container, string key)
        {
            string text = Str(container, key, null);
            return !string.IsNullOrEmpty(text);
        }

        public static double? Num(object value)
        {
            return JsonUtil.LooseNum(value);
        }

        public static int? Int(object value)
        {
            double? d = Num(value);
            if (!d.HasValue) return null;
            double rounded = Math.Round(d.Value, MidpointRounding.AwayFromZero);
            if (rounded < int.MinValue || rounded > int.MaxValue) return null;
            return (int)rounded;
        }

        public static bool? Bool(object value)
        {
            return JsonUtil.Bool(value);
        }

        // Used for the status line ("HTTP 500"), plan capitalisation and fallback labels.
        public static string Capitalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length == 1) return text.ToUpperInvariant();
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        // JS Math.round() ties away from zero; .NET's default ties to even.
        public static int Round(double value)
        {
            double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            if (rounded < int.MinValue) return int.MinValue;
            if (rounded > int.MaxValue) return int.MaxValue;
            return (int)rounded;
        }

        public static string NumText(object value)
        {
            if (value == null) return "null";
            string text = value as string;
            if (text != null) return text;
            if (value is bool) return (bool)value ? "true" : "false";
            double? d = Num(value);
            if (!d.HasValue) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return d.Value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // `new Date(ms)` — epoch milliseconds to a UTC DateTime, null when the value is unusable.
        public static DateTime? FromEpochMs(object value)
        {
            double? ms = Num(value);
            if (!ms.HasValue) return null;
            return FromEpochMs(ms.Value);
        }

        private static DateTime? FromEpochMs(double ms)
        {
            if (double.IsNaN(ms) || double.IsInfinity(ms)) return null;
            if (ms < -62135596800000.0 || ms > 253402300799999.0) return null;   // outside DateTime's range
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
        }

        // `Date.now() + seconds * 1000` — used by the Codex adapter for reset_after_seconds.
        public static DateTime? EpochPlus(object container, string key, DateTime nowUtc)
        {
            double? seconds = Num(Get(container, key));
            if (!seconds.HasValue) return null;
            return FromEpochMs(ToEpochMs(nowUtc) + seconds.Value * 1000.0);
        }

        public static double ToEpochMs(DateTime utc)
        {
            return (Models.ToUtc(utc) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        // ------------------------------------------------------------------------------------------
        // Generic indented JSON writer over the shapes JavaScriptSerializer produces. Used to rewrite
        // credential files without dropping the keys we do not understand (SPEC 5.3): the output is
        // JSON.stringify(value, null, 2) again, 2 spaces, `"key": value`, null for unknown.
        // ------------------------------------------------------------------------------------------
        public static string IndentedJson(object value)
        {
            JsonWriter w = new JsonWriter();
            WriteValue(w, value);
            return w.ToString();
        }

        private static void WriteValue(JsonWriter w, object value)
        {
            if (value == null) { w.String(null); return; }

            string text = value as string;
            if (text != null) { w.String(text); return; }

            if (value is bool) { w.Bool((bool)value); return; }

            if (value is DateTime) { w.String(Models.ToIso((DateTime)value)); return; }

            IDictionary<string, object> map = JsonUtil.Map(value);
            if (map != null)
            {
                w.BeginObject();
                foreach (KeyValuePair<string, object> kv in map)
                {
                    w.Key(kv.Key == null ? "" : kv.Key);
                    WriteValue(w, kv.Value);
                }
                w.EndObject();
                return;
            }

            IList<object> list = JsonUtil.List(value);
            if (list != null)
            {
                w.BeginArray();
                foreach (object item in list) WriteValue(w, item);
                w.EndArray();
                return;
            }

            double? number = JsonUtil.Num(value);
            if (number.HasValue) { w.Double(number.Value); return; }

            w.String(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }
}
