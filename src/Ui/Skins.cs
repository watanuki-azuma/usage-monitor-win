// Registry of the switchable skins (class SkinRegistry: "Skins" is taken by the UsageMonitor.Skins namespace): ids, menu names and the factories for the chips bar and the
// flyout. An unknown id falls back to classic.
//
// Language level: C# 5.
using System;

namespace UsageMonitor
{
    internal static class SkinRegistry
    {
        internal const string Classic = "classic";
        internal const string Rpg = "rpg";
        internal const string Fluent = "fluent";
        internal const string Terminal = "terminal";

        private static readonly string[] ids = new string[] { Classic, Rpg, Fluent, Terminal };
        private static readonly string[] names = new string[] { "Classic", "RPG", "Fluent", "Terminal" };

        internal static string[] Ids { get { return (string[])ids.Clone(); } }

        internal static string[] Names { get { return (string[])names.Clone(); } }

        internal static string NameOf(string id)
        {
            int index = IndexOf(id);
            return names[index < 0 ? 0 : index];
        }

        // Unknown, empty or null ids map to "classic".
        internal static string Normalize(string id)
        {
            int index = IndexOf(id);
            return index < 0 ? Classic : ids[index];
        }

        internal static bool IsKnown(string id)
        {
            return IndexOf(id) >= 0;
        }

        internal static ChipsFormBase CreateChips(string id, AppController app)
        {
            switch (Normalize(id))
            {
                case Rpg: return new UsageMonitor.Skins.Rpg.ChipsForm(app);
                case Fluent: return new UsageMonitor.Skins.Fluent.ChipsForm(app);
                case Terminal: return new UsageMonitor.Skins.Terminal.ChipsForm(app);
                default: return new ChipsForm(app);
            }
        }

        internal static FlyoutFormBase CreateFlyout(string id, AppController app)
        {
            switch (Normalize(id))
            {
                case Rpg: return new UsageMonitor.Skins.Rpg.FlyoutForm(app);
                case Fluent: return new UsageMonitor.Skins.Fluent.FlyoutForm(app);
                case Terminal: return new UsageMonitor.Skins.Terminal.FlyoutForm(app);
                default: return new FlyoutForm(app);
            }
        }

        // Every Theme copy fixes its Scale to the system DPI in its static initialiser. Call this
        // once after the process is DPI aware so all four copies are initialised against the same
        // value, not lazily at first use.
        internal static float InitScale()
        {
            float scale = Theme.Scale;
            float a = UsageMonitor.Skins.Rpg.Theme.Scale;
            float b = UsageMonitor.Skins.Fluent.Theme.Scale;
            float c = UsageMonitor.Skins.Terminal.Theme.Scale;
            return scale + (a - a) + (b - b) + (c - c);
        }

        private static int IndexOf(string id)
        {
            if (id == null) return -1;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == id) return i;
            }
            return -1;
        }
    }
}
