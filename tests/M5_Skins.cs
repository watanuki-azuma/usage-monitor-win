using System;
using System.Collections.Generic;
using System.Drawing;

namespace UsageMonitor.Tests
{
    public static class SkinTests
    {
        static ProviderSnapshot P(string id, double? used)
        {
            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = id;
            p.DisplayName = id;
            p.Plan = "Pro";
            p.Status = new ProviderStatus();
            p.Status.State = State.Ok;
            if (used.HasValue)
            {
                p.Windows.Add(Models.WindowOf(Kind.FiveHour, "5h", used.Value, "2026-10-11T15:00:00Z"));
                p.Windows.Add(Models.WindowOf(Kind.Weekly, "Weekly", 40d, "2026-10-14T15:00:00Z"));
            }
            p.FetchedAt = Dates.Utc(2026, 10, 11, 12, 0, 0);
            return p;
        }

        static Snapshot Fixture()
        {
            Snapshot s = new Snapshot();
            s.Providers.Add(P("claude", 10d));
            s.Providers.Add(P("codex", 85d));
            s.Providers.Add(P("gemini", null));
            s.Providers.Add(P("grok", 100d));
            return s;
        }

        public static void Test_config_skin_defaults_roundtrips_and_unknown_is_classic()
        {
            Assert.Equal("classic", AppConfig.Default().Skin, "default");
            Assert.Equal("classic", AppConfig.Parse("{}").Skin, "missing");
            Assert.Equal("classic", AppConfig.Parse("{\"skin\":\"bogus\"}").Skin, "unknown");
            Assert.Equal("classic", AppConfig.Parse("{\"skin\":5}").Skin, "wrong type");
            string[] ids = new string[] { "classic", "rpg", "fluent", "terminal" };
            for (int i = 0; i < ids.Length; i++)
            {
                AppConfig c = AppConfig.Default();
                c.Skin = ids[i];
                string json = c.ToJson();
                Assert.True(json.Contains("\"skin\""), "json key written for " + ids[i]);
                Assert.Equal(ids[i], AppConfig.Parse(json).Skin, "roundtrip " + ids[i]);
            }
        }

        public static void Test_registry_creates_every_skin_and_unknown_is_classic()
        {
            Assert.True(SkinRegistry.CreateChips("nope", null) is ChipsForm, "unknown chips -> classic");
            Assert.True(SkinRegistry.CreateFlyout(null, null) is FlyoutForm, "null flyout -> classic");
            Assert.True(SkinRegistry.CreateChips("rpg", null).GetType().Namespace == "UsageMonitor.Skins.Rpg", "rpg ns");
            Assert.Equal(4, SkinRegistry.Ids.Length, "four ids");
            Assert.Equal("RPG", SkinRegistry.NameOf("rpg"), "menu name");
        }

        public static void Test_each_skin_measures_and_paints_chips_and_flyout()
        {
            Snapshot snapshot = Fixture();
            string[] ids = SkinRegistry.Ids;
            for (int i = 0; i < ids.Length; i++)
            {
                using (ChipsFormBase chips = SkinRegistry.CreateChips(ids[i], null))
                {
                    chips.SetSnapshot(snapshot);
                    Size size = chips.MeasureSize();
                    Assert.True(size.Width > 0 && size.Height > 0, ids[i] + " chips size");
                    using (Bitmap bmp = new Bitmap(size.Width, size.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        chips.PaintInto(g, size);
                    }
                }
                using (FlyoutFormBase flyout = SkinRegistry.CreateFlyout(ids[i], null))
                {
                    flyout.SetSnapshot(snapshot);
                    Size size = flyout.MeasureSize();
                    Assert.True(size.Width > 0 && size.Height > 0, ids[i] + " flyout size");
                    using (Bitmap bmp = new Bitmap(size.Width, size.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        flyout.PaintInto(g, size);
                    }
                }
            }
        }

        public static void Test_rpg_chips_show_remaining_while_colour_follows_used()
        {
            ChipInfo low = UsageMonitor.Skins.Rpg.ChipsForm.ChipFor(P("claude", 10d));
            Assert.Equal("90/100", low.Text, "10% used -> 90 left");
            Assert.Equal("green", low.ColorClass, "green");

            ChipInfo warn = UsageMonitor.Skins.Rpg.ChipsForm.ChipFor(P("codex", 85d));
            Assert.Equal("15/100", warn.Text, "85% used -> 15 left");
            Assert.Equal("red", warn.ColorClass, "warn follows used");

            ChipInfo full = UsageMonitor.Skins.Rpg.ChipsForm.ChipFor(P("grok", 100d));
            Assert.Equal("0/100", full.Text, "100% used -> 0 left");
            Assert.Equal("red", full.ColorClass, "full is red");

            ChipInfo none = UsageMonitor.Skins.Rpg.ChipsForm.ChipFor(P("gemini", null));
            Assert.Equal("—", none.Text, "no window keeps the dash");

            // the other skins' chips are untouched: they still come from ChipModel (used)
            Assert.Equal("10/100", ChipModel.For(P("claude", 10d)).Text, "classic stays used");
        }
    }
}
