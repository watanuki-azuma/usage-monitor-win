using System;
using System.Collections.Generic;

namespace UsageMonitor.Tests
{
    public static class TaskbarMathTests
    {
        static void Rect(string what, RectI r, int x, int y, int w, int h)
        {
            Assert.Equal(x, r.X, what + ".X");
            Assert.Equal(y, r.Y, what + ".Y");
            Assert.Equal(w, r.W, what + ".W");
            Assert.Equal(h, r.H, what + ".H");
        }

        public static void Test_Edge_picks_the_thickest_gap_and_defaults_to_bottom()
        {
            RectI screen = new RectI(0, 0, 1920, 1080);
            Assert.Equal("bottom", TaskbarMath.Edge(screen, new RectI(0, 0, 1920, 1040)), "bottom taskbar");
            Assert.Equal("top", TaskbarMath.Edge(screen, new RectI(0, 40, 1920, 1040)), "top taskbar");
            Assert.Equal("left", TaskbarMath.Edge(screen, new RectI(60, 0, 1860, 1080)), "left taskbar");
            Assert.Equal("right", TaskbarMath.Edge(screen, new RectI(0, 0, 1860, 1080)), "right taskbar");
            Assert.Equal("bottom", TaskbarMath.Edge(screen, new RectI(0, 0, 1920, 1080)), "auto-hide -> bottom");
            Assert.Equal("bottom", TaskbarMath.Edge(screen, new RectI(6, 0, 1914, 1080)), "thickest gap <= 8 px (left, 6) -> bottom default");
            // negative-origin monitor (secondary display above the primary)
            Assert.Equal("bottom", TaskbarMath.Edge(new RectI(0, -1080, 1920, 1080), new RectI(0, -1080, 1920, 1040)), "negative origin");
        }

        public static void Test_DockBeforeTray_horizontal_and_vertical()
        {
            RectI bar = new RectI(0, 1040, 1920, 40);
            RectI tray = new RectI(1500, 1040, 420, 40);
            Rect("horizontal", TaskbarMath.DockBeforeTray(bar, tray, 300, 28, 4), 1196, 1046, 300, 28);

            RectI vbar = new RectI(0, 0, 60, 1080);
            RectI vtray = new RectI(0, 900, 60, 180);
            Rect("vertical", TaskbarMath.DockBeforeTray(vbar, vtray, 48, 120, 4), 6, 776, 48, 120);

            RectI neg = new RectI(0, -40, 1920, 40);
            RectI negTray = new RectI(1500, -40, 420, 40);
            Rect("negative origin", TaskbarMath.DockBeforeTray(neg, negTray, 300, 28, 4), 1196, -34, 300, 28);
        }

        public static void Test_Clamp_keeps_rectangles_on_screen()
        {
            RectI work = new RectI(0, 0, 1920, 1040);
            Rect("inside", TaskbarMath.Clamp(new RectI(100, 100, 300, 28), work), 100, 100, 300, 28);
            Rect("right overflow", TaskbarMath.Clamp(new RectI(1800, 100, 300, 28), work), 1620, 100, 300, 28);
            Rect("left overflow", TaskbarMath.Clamp(new RectI(-50, 100, 300, 28), work), 0, 100, 300, 28);
            Rect("bottom overflow", TaskbarMath.Clamp(new RectI(100, 1035, 300, 28), work), 100, 1012, 300, 28);
            Rect("top overflow", TaskbarMath.Clamp(new RectI(100, -20, 300, 28), work), 100, 0, 300, 28);
            Rect("bigger than bounds pins to origin", TaskbarMath.Clamp(new RectI(500, 500, 3000, 28), work), 0, 500, 3000, 28);
            RectI second = new RectI(-1920, 0, 1920, 1040);
            Rect("second monitor", TaskbarMath.Clamp(new RectI(-100, 10, 300, 28), second), -300, 10, 300, 28);
        }

        public static void Test_IsOverTray_uses_the_window_centre()
        {
            RectI tray = new RectI(1500, 1040, 420, 40);
            Assert.True(TaskbarMath.IsOverTray(new RectI(1600, 1042, 100, 28), tray), "centre inside");
            Assert.False(TaskbarMath.IsOverTray(new RectI(1400, 1042, 100, 28), tray), "centre at x=1450 is left of tray");
            Assert.False(TaskbarMath.IsOverTray(new RectI(1600, 900, 100, 28), tray), "above the tray");
            Assert.False(TaskbarMath.IsOverTray(new RectI(1600, 1042, 100, 28), null), "no tray known");
        }

        public static void Test_FlyoutAnchor_prefers_above_and_clamps_horizontally()
        {
            RectI work = new RectI(0, 0, 1920, 1040);
            RectI chips = new RectI(1200, 1010, 300, 28);
            Rect("above the chips, centred", TaskbarMath.FlyoutAnchor(chips, work, 580, 280, 8), 1060, 722, 580, 280);
            RectI nearRight = new RectI(1700, 1010, 300, 28);
            Rect("clamped on the right", TaskbarMath.FlyoutAnchor(nearRight, work, 580, 280, 8), 1340, 722, 580, 280);
            RectI top = new RectI(100, 4, 300, 28);
            Rect("no room above -> below", TaskbarMath.FlyoutAnchor(top, work, 580, 280, 8), 0, 40, 580, 280);
        }
    }

    public static class ChipModelTests
    {
        static ProviderSnapshot P(string id, string state, params UsageWindow[] ws)
        {
            ProviderSnapshot p = new ProviderSnapshot();
            p.Id = id;
            p.DisplayName = id;
            p.Status = new ProviderStatus();
            p.Status.State = state;
            p.Windows.AddRange(ws);
            p.FetchedAt = Dates.Utc(2026, 10, 11, 12, 0, 0);
            return p;
        }

        static UsageWindow W(string kind, double? used)
        {
            UsageWindow w = new UsageWindow();
            w.Kind = kind;
            w.Label = kind;
            w.UsedPct = used;
            return w;
        }

        public static void Test_chip_text_colour_and_stale_flag()
        {
            ChipInfo green = ChipModel.For(P("claude", State.Ok, W(Kind.FiveHour, 42.4)));
            Assert.Equal("42/100", green.Text, "text");
            Assert.Equal("green", green.ColorClass, "green");
            Assert.False(green.Stale, "not stale");
            Assert.Equal("claude", green.Id, "id");

            ChipInfo red = ChipModel.For(P("codex", State.Ok, W(Kind.FiveHour, 80)));
            Assert.Equal("red", red.ColorClass, "red at 80");

            ChipInfo gray = ChipModel.For(P("grok", State.LoggedOut));
            Assert.Equal("—", gray.Text, "dash");
            Assert.Equal("gray", gray.ColorClass, "gray");

            ChipInfo stale = ChipModel.For(P("gemini", State.Stale, W(Kind.Daily, 10)));
            Assert.True(stale.Stale, "stale");
            Assert.Equal("green", stale.ColorClass, "stale keeps colour");

            ChipInfo prefers = ChipModel.For(P("claude", State.Ok, W(Kind.Weekly, 99), W(Kind.FiveHour, 5)));
            Assert.Equal("5/100", prefers.Text, "five hour before weekly");
        }

        public static void Test_Key_changes_only_when_something_visible_changes()
        {
            Snapshot a = new Snapshot();
            a.Providers.Add(P("claude", State.Ok, W(Kind.FiveHour, 42.4)));
            a.Providers.Add(P("grok", State.LoggedOut));
            Snapshot b = new Snapshot();
            b.Providers.Add(P("claude", State.Ok, W(Kind.FiveHour, 42.4)));
            b.Providers.Add(P("grok", State.LoggedOut));
            b.Providers[0].FetchedAt = b.Providers[0].FetchedAt.AddMinutes(5);
            b.GeneratedAt = a.GeneratedAt.AddMinutes(5);
            Assert.Equal(ChipModel.Key(a), ChipModel.Key(b), "timestamps are not visible");

            Snapshot c = new Snapshot();
            c.Providers.Add(P("claude", State.Ok, W(Kind.FiveHour, 42.6)));
            c.Providers.Add(P("grok", State.LoggedOut));
            Assert.True(ChipModel.Key(a) != ChipModel.Key(c), "42.4 -> 42.6 changes the rounded text");

            Snapshot d = new Snapshot();
            d.Providers.Add(P("claude", State.Ok, W(Kind.FiveHour, 42.4)));
            d.Providers.Add(P("grok", State.Stale));
            Assert.True(ChipModel.Key(a) != ChipModel.Key(d), "state change is visible");
        }
    }
}
