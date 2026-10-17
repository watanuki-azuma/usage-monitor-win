// Helper routines for the JRPG Status Screen theme (milestone M5).
// Language level: C# 5 (in-box csc.exe): no ?. , no $"" , no expression-bodied members, no out var.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UsageMonitor.Skins.Rpg
{
    internal static class Fx
    {
        // Classic JRPG dialog window box with double white border and 2px dark separation.
        public static void DrawRpgBox(Graphics g, Rectangle rect, Color topColor, Color bottomColor)
        {
            if (rect.Width <= 6 || rect.Height <= 6) return;

            // 1. Fill interior with rich vertical gradient
            using (LinearGradientBrush brush = new LinearGradientBrush(rect, topColor, bottomColor, LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, rect);
            }

            // 2. Crisp outer white border
            g.DrawRectangle(Theme.BorderWhitePen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

            // 3. Inner border inset by 3 design pixels (leaves a clean 2px dark gap between white lines)
            int inset = Theme.S(3);
            if (inset < 3) inset = 3;
            Rectangle inner = new Rectangle(rect.X + inset, rect.Y + inset, rect.Width - inset * 2 - 1, rect.Height - inset * 2 - 1);

            if (inner.Width > 0 && inner.Height > 0)
            {
                g.DrawRectangle(Theme.BorderInnerPen, inner.X, inner.Y, inner.Width, inner.Height);
            }
        }

        public static void DrawRpgBox(Graphics g, Rectangle rect, Color topColor, Color bottomColor, Color outerBorder, Color innerBorder)
        {
            if (rect.Width <= 6 || rect.Height <= 6) return;

            using (LinearGradientBrush brush = new LinearGradientBrush(rect, topColor, bottomColor, LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, rect);
            }

            using (Pen outerPen = new Pen(outerBorder, Math.Max(1f, Theme.Scale)))
            using (Pen innerPen = new Pen(innerBorder, Math.Max(1f, Theme.Scale)))
            {
                g.DrawRectangle(outerPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                int inset = Theme.S(3);
                if (inset < 3) inset = 3;
                Rectangle inner = new Rectangle(rect.X + inset, rect.Y + inset, rect.Width - inset * 2 - 1, rect.Height - inset * 2 - 1);
                if (inner.Width > 0 && inner.Height > 0)
                {
                    g.DrawRectangle(innerPen, inner.X, inner.Y, inner.Width, inner.Height);
                }
            }
        }

        // Classic 16-bit RPG gauge / health meter bar with recessed background, specular shine, and glowing cap.
        public static void DrawHpBar(Graphics g, Rectangle track, double fillPct, Color barColor, Color barColorDark)
        {
            if (track.Width <= 2 || track.Height <= 2) return;

            // Recessed track background
            g.FillRectangle(Theme.TrackBgBrush, track);

            // Inner shadow along top of track for 16-bit sunken slot bevel
            g.DrawLine(Theme.ShadowPen90, track.X + 1, track.Y + 1, track.Right - 2, track.Y + 1);

            // 1px border around the track
            g.DrawRectangle(Theme.TrackBorderPen, track.X, track.Y, track.Width - 1, track.Height - 1);

            // Subtle outer highlight along bottom for chiseled recess
            g.DrawLine(Theme.TrackHighlightPen, track.X, track.Bottom, track.Right - 1, track.Bottom);

            int fillWidth = (int)Math.Round((track.Width - 2) * (fillPct / 100.0), MidpointRounding.AwayFromZero);
            if (fillWidth < 0) fillWidth = 0;
            if (fillWidth > track.Width - 2) fillWidth = track.Width - 2;

            // Ensure critical remaining health (> 0%) is clearly visible as a glowing sliver
            if (fillPct > 0 && fillWidth < Theme.S(3)) fillWidth = Theme.S(3);

            if (fillWidth > 0)
            {
                Rectangle fill = new Rectangle(track.X + 1, track.Y + 1, fillWidth, track.Height - 2);
                using (LinearGradientBrush fillBrush = new LinearGradientBrush(fill, barColor, barColorDark, LinearGradientMode.Vertical))
                {
                    g.FillRectangle(fillBrush, fill);
                }

                // Specular highlight at top of bar for cartridge/glass sheen
                if (fill.Height > 2)
                {
                    g.DrawLine(Theme.ShinePen180, fill.X, fill.Y, fill.Right - 1, fill.Y);
                }

                // 1px lower shadow for 3D cartridge cylindrical depth
                if (fill.Height > 4)
                {
                    g.DrawLine(Theme.ShadowPen90, fill.X, fill.Bottom - 1, fill.Right - 1, fill.Bottom - 1);
                }

                // Leading edge glowing cap (classic RPG health bar tip)
                if (fillWidth > 2)
                {
                    g.DrawLine(Theme.CapPen235, fill.Right - 1, fill.Y, fill.Right - 1, fill.Bottom - 1);
                }
            }
        }

        public static void DrawHpBar(Graphics g, Rectangle track, double fillPct, Color barColor, Color barColorDark, Color trackBorder)
        {
            if (track.Width <= 2 || track.Height <= 2) return;

            g.FillRectangle(Theme.TrackBgBrush, track);
            g.DrawLine(Theme.ShadowPen90, track.X + 1, track.Y + 1, track.Right - 2, track.Y + 1);

            using (Pen borderPen = new Pen(trackBorder, 1f))
            {
                g.DrawRectangle(borderPen, track.X, track.Y, track.Width - 1, track.Height - 1);
            }

            g.DrawLine(Theme.TrackHighlightPen, track.X, track.Bottom, track.Right - 1, track.Bottom);

            int fillWidth = (int)Math.Round((track.Width - 2) * (fillPct / 100.0), MidpointRounding.AwayFromZero);
            if (fillWidth < 0) fillWidth = 0;
            if (fillWidth > track.Width - 2) fillWidth = track.Width - 2;

            if (fillPct > 0 && fillWidth < Theme.S(3)) fillWidth = Theme.S(3);

            if (fillWidth > 0)
            {
                Rectangle fill = new Rectangle(track.X + 1, track.Y + 1, fillWidth, track.Height - 2);
                using (LinearGradientBrush fillBrush = new LinearGradientBrush(fill, barColor, barColorDark, LinearGradientMode.Vertical))
                {
                    g.FillRectangle(fillBrush, fill);
                }

                if (fill.Height > 2)
                {
                    g.DrawLine(Theme.ShinePen180, fill.X, fill.Y, fill.Right - 1, fill.Y);
                }

                if (fill.Height > 4)
                {
                    g.DrawLine(Theme.ShadowPen90, fill.X, fill.Bottom - 1, fill.Right - 1, fill.Bottom - 1);
                }

                if (fillWidth > 2)
                {
                    g.DrawLine(Theme.CapPen235, fill.Right - 1, fill.Y, fill.Right - 1, fill.Bottom - 1);
                }
            }
        }

        public static void DrawHpBar(Graphics g, Rectangle track, double fillPct, Color barColor, Color barColorDark, Color trackBg, Color trackBorder)
        {
            DrawHpBar(g, track, fillPct, barColor, barColorDark, trackBorder);
        }

        // Renders the classic retro pointing hand cursor glyph (\u261B in Segoe UI Symbol) centered in box.
        public static void DrawHandCursor(Graphics g, Rectangle box, Color color)
        {
            Rectangle shadow = new Rectangle(box.X + 1, box.Y + 1, box.Width, box.Height);
            Theme.DrawTextCenter(g, "\u261B", Theme.HandCursorFont, shadow, Color.FromArgb(0, 0, 0));
            Theme.DrawTextCenter(g, "\u261B", Theme.HandCursorFont, box, color);
        }

        public static void DrawHandCursor(Graphics g, Point pt, Color color)
        {
            Theme.DrawTextAt(g, "\u261B", Theme.HandCursorFont, new Point(pt.X + 1, pt.Y + 1), Color.FromArgb(0, 0, 0));
            Theme.DrawTextAt(g, "\u261B", Theme.HandCursorFont, pt, color);
        }

        // Classic RPG text with 1px black drop-shadow.
        public static void DrawTextWithShadow(Graphics g, string text, Font font, Point pt, Color textColor, Color shadowColor)
        {
            Theme.DrawTextAt(g, text, font, new Point(pt.X + 1, pt.Y + 1), shadowColor);
            Theme.DrawTextAt(g, text, font, pt, textColor);
        }

        // A small crisp status badge (e.g. [ KO ], [ UNMET ]) using static GDI objects.
        public static void DrawBadge(Graphics g, Rectangle rect, string text, SolidBrush bgBrush, Color textColor, Pen borderPen)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            g.FillRectangle(bgBrush, rect);
            g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            Theme.DrawTextCenter(g, text, Theme.KoFont, rect, textColor);
        }

        public static void DrawBadge(Graphics g, Rectangle rect, string text, Color bgColor, Color textColor, Color borderColor)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            using (SolidBrush bgBrush = new SolidBrush(bgColor))
            {
                g.FillRectangle(bgBrush, rect);
            }
            using (Pen borderPen = new Pen(borderColor, 1f))
            {
                g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
            Theme.DrawTextCenter(g, text, Theme.KoFont, rect, textColor);
        }
    }
}
