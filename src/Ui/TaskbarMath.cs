// Taskbar geometry helpers (milestone M5). Pure math only: no WinForms / System.Drawing types.
// Everything is expressed with the RectI value type so the class stays unit-testable.
// Port of reference/js/taskbarLayout.js.
// Language level: C# 5 (in-box csc.exe): no ?. , no $"" , no expression-bodied members, no out var.
using System;

namespace UsageMonitor
{
    // Integer rectangle in screen coordinates (any monitor, so X/Y may be negative).
    public struct RectI
    {
        public int X, Y, W, H;

        public RectI(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }
    }

    public static class TaskbarMath
    {
        // A gap this thin is treated as "no taskbar there" (auto-hide, rounded corners, ...).
        private const int MinGap = 8;

        // ------------------------------------------------------------------------------------------
        // Which screen edge the taskbar sits on: the one with the thickest gap between the screen
        // rectangle and the work area. When no gap is meaningful the taskbar is assumed to be at
        // the bottom (Windows default, and the auto-hide case where the two rects are equal).
        // ------------------------------------------------------------------------------------------
        public static string Edge(RectI screen, RectI workArea)
        {
            int top = workArea.Y - screen.Y;
            int left = workArea.X - screen.X;
            int right = (screen.X + screen.W) - (workArea.X + workArea.W);
            int bottom = (screen.Y + screen.H) - (workArea.Y + workArea.H);

            int thickest = top;
            if (left > thickest) thickest = left;
            if (right > thickest) thickest = right;
            if (bottom > thickest) thickest = bottom;

            if (thickest <= MinGap) return "bottom";

            if (thickest == top) return "top";
            if (thickest == left) return "left";
            if (thickest == right) return "right";
            return "bottom";
        }

        // ------------------------------------------------------------------------------------------
        // Where a chips bar of w x h goes so it sits right before the tray notification area,
        // centred on the taskbar's long axis. Integer division, like the JavaScript port.
        // ------------------------------------------------------------------------------------------
        public static RectI DockBeforeTray(RectI taskbar, RectI trayNotify, int w, int h, int gap)
        {
            if (taskbar.W >= taskbar.H)
            {
                int x = trayNotify.X - gap - w;
                int y = taskbar.Y + (taskbar.H - h) / 2;
                return new RectI(x, y, w, h);
            }

            int vx = taskbar.X + (taskbar.W - w) / 2;
            int vy = trayNotify.Y - gap - h;
            return new RectI(vx, vy, w, h);
        }

        // ------------------------------------------------------------------------------------------
        // Move a rect fully inside bounds. A rect wider/taller than the bounds cannot fit, so it is
        // pinned to the bounds' origin on that axis instead (never resized, never scrolled).
        // ------------------------------------------------------------------------------------------
        public static RectI Clamp(RectI r, RectI bounds)
        {
            int x = r.X;
            int y = r.Y;

            if (r.W > bounds.W) x = bounds.X;
            else if (x + r.W > bounds.X + bounds.W) x = bounds.X + bounds.W - r.W;
            if (x < bounds.X) x = bounds.X;

            if (r.H > bounds.H) y = bounds.Y;
            else if (y + r.H > bounds.Y + bounds.H) y = bounds.Y + bounds.H - r.H;
            if (y < bounds.Y) y = bounds.Y;

            return new RectI(x, y, r.W, r.H);
        }

        // ------------------------------------------------------------------------------------------
        // True when the centre point of the window lands inside the (optional) tray rectangle —
        // i.e. the window is parked over the tray and should be docked again.
        // ------------------------------------------------------------------------------------------
        public static bool IsOverTray(RectI window, RectI? tray)
        {
            if (!tray.HasValue) return false;
            RectI t = tray.Value;

            int cx = window.X + window.W / 2;
            int cy = window.Y + window.H / 2;
            return cx >= t.X && cx < t.X + t.W && cy >= t.Y && cy < t.Y + t.H;
        }

        // ------------------------------------------------------------------------------------------
        // Flyout placement: centred on the chips bar, above it when there is room, otherwise below,
        // then kept on the work area.
        // ------------------------------------------------------------------------------------------
        public static RectI FlyoutAnchor(RectI chips, RectI work, int w, int h, int gap)
        {
            int x = chips.X + (chips.W - w) / 2;
            int y = chips.Y - gap - h;
            if (y < work.Y) y = chips.Y + chips.H + gap;
            return Clamp(new RectI(x, y, w, h), work);
        }
    }
}
