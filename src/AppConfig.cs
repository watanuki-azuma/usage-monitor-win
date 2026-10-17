// config.json. Port of reference/js/config.js (milestone M1).
// Language level: C# 5 (in-box csc.exe).
using System;
using System.Collections.Generic;

namespace UsageMonitor
{
    public class AppConfig
    {
        public static readonly int[] AllowedIntervals = new int[] { 5, 15, 30, 60 };

        // Only these three adapters have a refresh_tokens switch in the file; anything else that
        // round-trips through Parse() still defaults to true, like JS's `!== false`.
        private static readonly string[] AdapterIds = new string[] { "claude", "gemini", "grok" };

        private readonly Dictionary<string, bool> adapters = new Dictionary<string, bool>();

        public int PollIntervalSecs { get; set; }
        public int ConfigVersion { get; set; }
        public bool ChipsDocked { get; set; }
        public bool ChipsHidden { get; set; }
        public int? ChipsX { get; set; }
        public int? ChipsY { get; set; }
        public int? ChipsDockX { get; set; }
        public int? ChipsDockY { get; set; }
        public bool FlyoutDocked { get; set; }
        public bool FlyoutPinned { get; set; }
        public int? FlyoutX { get; set; }
        public int? FlyoutY { get; set; }
        public int? FlyoutDockX { get; set; }
        public int? FlyoutDockY { get; set; }
        public bool Autostart { get; set; }
        // Kept only so the file stays compatible; the native app has no updater.
        public bool CheckUpdatesOnStartup { get; set; }
        public bool NotifyOnLimitReached { get; set; }
        // Look of the chips bar and flyout: classic, rpg, fluent or terminal.
        public string Skin { get; set; }

        public AppConfig()
        {
            ApplyDefaults();
        }

        public bool RefreshTokens(string adapterId)
        {
            if (string.IsNullOrEmpty(adapterId)) return true;
            bool value;
            if (adapters.TryGetValue(adapterId, out value)) return value;
            return true;
        }

        public void SetRefreshTokens(string adapterId, bool value)
        {
            if (string.IsNullOrEmpty(adapterId)) return;
            adapters[adapterId] = value;
        }

        public static AppConfig Default()
        {
            return new AppConfig();
        }

        // Tolerant like config.js normalize(): junk, arrays, non-objects and wrong-typed values all
        // fall back to the defaults instead of throwing, and every call builds a fresh object so
        // callers never share mutable state.
        public static AppConfig Parse(string json)
        {
            AppConfig c = new AppConfig();

            IDictionary<string, object> raw = JsonUtil.Map(JsonUtil.Parse(json));
            if (raw == null) return c;

            c.PollIntervalSecs = IntervalOf(raw);
            c.ConfigVersion = IntOf(raw, "config_version", 5);            c.ChipsDocked = BoolOf(raw, "chips_docked", true);
            c.ChipsHidden = BoolOf(raw, "chips_hidden", false);
            c.ChipsX = CoordOf(raw, "chips_x");
            c.ChipsY = CoordOf(raw, "chips_y");
            c.ChipsDockX = CoordOf(raw, "chips_dock_x");
            c.ChipsDockY = CoordOf(raw, "chips_dock_y");
            c.FlyoutDocked = BoolOf(raw, "flyout_docked", false);
            c.FlyoutPinned = BoolOf(raw, "flyout_pinned", false);
            c.FlyoutX = CoordOf(raw, "flyout_x");
            c.FlyoutY = CoordOf(raw, "flyout_y");
            c.FlyoutDockX = CoordOf(raw, "flyout_dock_x");
            c.FlyoutDockY = CoordOf(raw, "flyout_dock_y");
            c.Autostart = BoolOf(raw, "autostart", true);
            c.CheckUpdatesOnStartup = BoolOf(raw, "check_updates_on_startup", true);
            c.NotifyOnLimitReached = BoolOf(raw, "notify_on_limit_reached", true);
            c.Skin = SkinOf(raw);

            IDictionary<string, object> rawAdapters = JsonUtil.Map(JsonUtil.Get(raw, "adapters"));
            for (int i = 0; i < AdapterIds.Length; i++)
            {
                string id = AdapterIds[i];
                IDictionary<string, object> entry = JsonUtil.Map(JsonUtil.Get(rawAdapters, id));
                object flag = JsonUtil.Get(entry, "refresh_tokens");
                // Only a literal false turns refreshing off, exactly like the JS `!== false`.
                c.adapters[id] = (flag is bool) && (bool)flag == false ? false : true;
            }
            return c;
        }

        // Key order matches the Electron config.json.
        public string ToJson()
        {
            JsonWriter w = new JsonWriter();
            w.BeginRootObject();

            w.Key("poll_interval_secs"); w.Number((long)PollIntervalSecs);
            w.Key("config_version"); w.Number((long)ConfigVersion);
            w.Key("chips_docked"); w.Bool(ChipsDocked);
            w.Key("chips_hidden"); w.Bool(ChipsHidden);
            w.Key("chips_x"); w.NullableNumber(ChipsX.HasValue ? (long?)ChipsX.Value : null);
            w.Key("chips_y"); w.NullableNumber(ChipsY.HasValue ? (long?)ChipsY.Value : null);
            w.Key("chips_dock_x"); w.NullableNumber(ChipsDockX.HasValue ? (long?)ChipsDockX.Value : null);
            w.Key("chips_dock_y"); w.NullableNumber(ChipsDockY.HasValue ? (long?)ChipsDockY.Value : null);
            w.Key("flyout_docked"); w.Bool(FlyoutDocked);
            w.Key("flyout_pinned"); w.Bool(FlyoutPinned);
            w.Key("flyout_x"); w.NullableNumber(FlyoutX.HasValue ? (long?)FlyoutX.Value : null);
            w.Key("flyout_y"); w.NullableNumber(FlyoutY.HasValue ? (long?)FlyoutY.Value : null);
            w.Key("flyout_dock_x"); w.NullableNumber(FlyoutDockX.HasValue ? (long?)FlyoutDockX.Value : null);
            w.Key("flyout_dock_y"); w.NullableNumber(FlyoutDockY.HasValue ? (long?)FlyoutDockY.Value : null);
            w.Key("autostart"); w.Bool(Autostart);
            w.Key("check_updates_on_startup"); w.Bool(CheckUpdatesOnStartup);
            w.Key("notify_on_limit_reached"); w.Bool(NotifyOnLimitReached);
            w.Key("skin"); w.String(SkinRegistry.Normalize(Skin));

            w.Key("adapters");
            w.BeginObject();
            for (int i = 0; i < AdapterIds.Length; i++)
            {
                w.Key(AdapterIds[i]);
                w.BeginObject();
                w.Key("refresh_tokens"); w.Bool(RefreshTokens(AdapterIds[i]));
                w.EndObject();
            }
            w.EndObject();

            w.EndObject();
            return w.ToString();
        }

        private void ApplyDefaults()
        {
            PollIntervalSecs = 5;
            ConfigVersion = 5;
            ChipsDocked = true;
            ChipsHidden = false;
            ChipsX = null;
            ChipsY = null;
            ChipsDockX = null;
            ChipsDockY = null;
            FlyoutDocked = false;
            FlyoutPinned = false;
            FlyoutX = null;
            FlyoutY = null;
            FlyoutDockX = null;
            FlyoutDockY = null;
            Autostart = true;
            CheckUpdatesOnStartup = true;
            NotifyOnLimitReached = true;
            Skin = "classic";
            adapters.Clear();
            for (int i = 0; i < AdapterIds.Length; i++) adapters[AdapterIds[i]] = true;
        }

        // A boolean written as the string "false" (or anything non-boolean) keeps the default.
        private static bool BoolOf(IDictionary<string, object> map, string key, bool fallback)
        {
            bool? value = JsonUtil.Bool(JsonUtil.Get(map, key));
            return value.HasValue ? value.Value : fallback;
        }

        // Only the four known ids are kept; a missing, wrong-typed or unknown value is "classic".
        private static string SkinOf(IDictionary<string, object> map)
        {
            string value = JsonUtil.Get(map, "skin") as string;
            return SkinRegistry.Normalize(value);
        }

        // Coordinates must be finite numbers, otherwise null (the window will not restore).
        private static int? CoordOf(IDictionary<string, object> map, string key)
        {
            return IntOf(map, key, null);
        }

        private static int IntOf(IDictionary<string, object> map, string key, int fallback)
        {
            int? value = IntOf(map, key, null);
            return value.HasValue ? value.Value : fallback;
        }

        private static int? IntOf(IDictionary<string, object> map, string key, int? fallback)
        {
            double? d = JsonUtil.LooseNum(JsonUtil.Get(map, key));
            if (!d.HasValue) return fallback;
            double rounded = Math.Round(d.Value, MidpointRounding.AwayFromZero);
            if (rounded < int.MinValue || rounded > int.MaxValue) return fallback;
            return (int)rounded;
        }

        // A numeric string such as "15" is accepted, exactly like config.js's Number(...) checks.
        private static int IntervalOf(IDictionary<string, object> map)
        {
            double? d = JsonUtil.LooseNum(JsonUtil.Get(map, "poll_interval_secs"));
            if (!d.HasValue) return 5;
            double rounded = Math.Round(d.Value, MidpointRounding.AwayFromZero);
            if (rounded < int.MinValue || rounded > int.MaxValue) return 5;
            int candidate = (int)rounded;
            for (int i = 0; i < AllowedIntervals.Length; i++)
            {
                if (AllowedIntervals[i] == candidate) return candidate;
            }
            return 5;
        }
    }
}
