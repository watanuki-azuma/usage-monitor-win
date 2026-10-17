// Fluent UI drawing helpers (milestone M5).
// Theme: v8-fluent (Windows 11 Fluent dark / Mica / Acrylic).
//
// Language level: C# 5 (in-box csc.exe). No ?. , no $"" , no expression-bodied members, no out var.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace UsageMonitor.Skins.Fluent
{
    internal static class Fx
    {
        public static Color Lighten(Color color, float amount)
        {
            int r = (int)Math.Min(255f, color.R + (255 - color.R) * amount);
            int g = (int)Math.Min(255f, color.G + (255 - color.G) * amount);
            int b = (int)Math.Min(255f, color.B + (255 - color.B) * amount);
            return Color.FromArgb(color.A, r, g, b);
        }

        public static Color Darken(Color color, float amount)
        {
            int r = (int)Math.Max(0f, color.R * (1f - amount));
            int g = (int)Math.Max(0f, color.G * (1f - amount));
            int b = (int)Math.Max(0f, color.B * (1f - amount));
            return Color.FromArgb(color.A, r, g, b);
        }

        public static Color Alpha(Color color, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        public static Color Blend(Color from, Color to, float t)
        {
            if (t <= 0f) return from;
            if (t >= 1f) return to;
            int a = (int)(from.A + (to.A - from.A) * t);
            int r = (int)(from.R + (to.R - from.R) * t);
            int g = (int)(from.G + (to.G - from.G) * t);
            int b = (int)(from.B + (to.B - from.B) * t);
            return Color.FromArgb(a, r, g, b);
        }

        // Cached static brushes and pens for static painting efficiency (BRIEF rule 4)
        private static readonly SolidBrush BtnActiveBrush = new SolidBrush(Theme.ActiveFill);
        private static readonly Pen BtnActivePen = new Pen(Color.FromArgb(0, 120, 212), 1f);
        private static readonly SolidBrush BtnPressBrush = new SolidBrush(Color.FromArgb(255, 28, 28, 32));
        private static readonly Pen BtnPressPen = new Pen(Color.FromArgb(25, 255, 255, 255), 1f);
        private static readonly SolidBrush BtnHoverBrush = new SolidBrush(Color.FromArgb(255, 52, 52, 58));
        private static readonly Pen BtnHoverPen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
        private static readonly SolidBrush BtnIdleBrush = new SolidBrush(Color.FromArgb(255, 40, 40, 45));
        private static readonly Pen BtnIdlePen = new Pen(Color.FromArgb(42, 255, 255, 255), 1f);

        private static readonly Pen BtnActiveHlPen = new Pen(Color.FromArgb(75, 255, 255, 255), 1f);
        private static readonly Pen BtnHoverHlPen = new Pen(Color.FromArgb(45, 255, 255, 255), 1f);
        private static readonly Pen BtnIdleHlPen = new Pen(Color.FromArgb(28, 255, 255, 255), 1f);

        private static readonly Pen SheenPen = new Pen(Color.FromArgb(120, 255, 255, 255), 1f);
        private static readonly Pen TrackShadowPen = new Pen(Color.FromArgb(60, 0, 0, 0), 1f);

        private static readonly SolidBrush StaleBannerBrush = new SolidBrush(Color.FromArgb(32, 245, 158, 11));
        private static readonly Pen StaleBannerPen = new Pen(Color.FromArgb(110, 245, 158, 11), 1f);

        private static readonly SolidBrush CmdBoxBrush = new SolidBrush(Color.FromArgb(18, 255, 255, 255));
        private static readonly Pen CmdBoxPen = new Pen(Color.FromArgb(36, 255, 255, 255), 1f);

        private static readonly SolidBrush CreditTagBrush = new SolidBrush(Color.FromArgb(20, 255, 255, 255));
        private static readonly Pen CreditTagPen = new Pen(Color.FromArgb(38, 255, 255, 255), 1f);

        // Draw an Acrylic / Layered Fluent card surface with smooth ambient brand glow & top highlight
        public static void DrawAcrylicCard(Graphics g, Rectangle rect, Color brandAccent, float radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            // Rich vertical gradient fill: slightly lighter at top, dark at bottom
            Color topColor = Color.FromArgb(255, 42, 42, 48);   // #2a2a30
            Color bottomColor = Color.FromArgb(255, 26, 26, 31); // #1a1a1f

            using (GraphicsPath path = Theme.RoundPath(rect, radius))
            {
                // 1. Base card surface
                using (LinearGradientBrush lgb = new LinearGradientBrush(rect, topColor, bottomColor, LinearGradientMode.Vertical))
                {
                    g.FillPath(lgb, path);
                }

                // 2. Ambient brand glow: smoothly washes down from top across card (NO diagonal tiling!)
                if (brandAccent != Color.Empty && brandAccent.A > 0)
                {
                    using (LinearGradientBrush glowBrush = new LinearGradientBrush(rect, Color.Transparent, Color.Transparent, LinearGradientMode.Vertical))
                    {
                        ColorBlend cb = new ColorBlend(3);
                        cb.Colors = new Color[] {
                            Alpha(brandAccent, 56),
                            Alpha(brandAccent, 18),
                            Color.FromArgb(0, brandAccent)
                        };
                        cb.Positions = new float[] { 0f, 0.40f, 1f };
                        glowBrush.InterpolationColors = cb;
                        g.FillPath(glowBrush, path);
                    }
                }

                // 3. 1px elevation border
                g.DrawPath(Theme.CardBorderPen, path);
            }

            // 4. Soft 1px specular top highlight line across card
            int hlInset = (int)Math.Round(radius, MidpointRounding.AwayFromZero);
            int hlWidth = rect.Width - hlInset * 2;
            if (hlWidth > 4)
            {
                Rectangle hlRect = new Rectangle(rect.X + hlInset, rect.Y + 1, hlWidth, 1);
                using (LinearGradientBrush hlBrush = new LinearGradientBrush(hlRect, Color.FromArgb(0, 255, 255, 255), Color.FromArgb(120, 255, 255, 255), LinearGradientMode.Horizontal))
                {
                    ColorBlend cb = new ColorBlend(3);
                    cb.Colors = new Color[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) };
                    cb.Positions = new float[] { 0f, 0.5f, 1f };
                    hlBrush.InterpolationColors = cb;
                    using (Pen hlPen = new Pen(hlBrush, 1f))
                    {
                        g.DrawLine(hlPen, rect.X + hlInset, rect.Y + 1, rect.Right - hlInset, rect.Y + 1);
                    }
                }
            }
        }

        // Draw an accent-coloured pill meter with a tactile gradient and recessed track
        public static void DrawPillMeter(Graphics g, Rectangle track, double fillPct, Color accentColor, bool isAlert, float radius)
        {
            if (track.Width <= 0 || track.Height <= 0) return;

            // Recessed dark well track
            using (GraphicsPath trackPath = Theme.RoundPath(track, radius))
            {
                g.FillPath(Theme.BarTrackBrush, trackPath);
                g.DrawPath(Theme.TrackBorderPen, trackPath);

                // Inset top shadow and bottom groove highlight inside the track
                if (track.Width > 8)
                {
                    int inset = (int)Math.Max(2f, radius);
                    g.DrawLine(TrackShadowPen, track.X + inset, track.Y + 1, track.Right - inset, track.Y + 1);
                    using (Pen bottomGroove = new Pen(Color.FromArgb(20, 255, 255, 255), 1f))
                    {
                        g.DrawLine(bottomGroove, track.X + inset, track.Bottom - 1, track.Right - inset, track.Bottom - 1);
                    }
                }
            }

            if (fillPct <= 0d) return;

            int fillWidth = (int)Math.Round(track.Width * (fillPct / 100.0), MidpointRounding.AwayFromZero);
            if (fillWidth < 0) fillWidth = 0;
            if (fillWidth > track.Width) fillWidth = track.Width;
            if (fillWidth <= 0) return;

            // Keep pill shape even for very small fills
            int minPill = Math.Min(track.Height, track.Width);
            if (fillWidth < minPill) fillWidth = minPill;

            Rectangle fillRect = new Rectangle(track.X, track.Y, fillWidth, track.Height);

            // Determine gradient colors based on state
            Color lightAccent;
            Color darkAccent;

            if (fillPct >= 100d || (isAlert && fillPct >= 100d))
            {
                // Full / Critical Red gradient
                lightAccent = Color.FromArgb(255, 252, 120, 120); // #fc7878
                darkAccent = Color.FromArgb(255, 200, 28, 28);    // #c81c1c
            }
            else if (fillPct >= 80d || isAlert)
            {
                // Warning Amber/Orange gradient
                lightAccent = Color.FromArgb(255, 253, 224, 71);  // #fde047
                darkAccent = Color.FromArgb(255, 217, 119, 6);    // #d97706
            }
            else
            {
                // Accent-coloured pill with tactile vertical gradient
                lightAccent = Lighten(accentColor, 0.30f);
                darkAccent = Darken(accentColor, 0.12f);
            }

            using (GraphicsPath fillPath = Theme.RoundPath(fillRect, radius))
            {
                using (LinearGradientBrush fillBrush = new LinearGradientBrush(fillRect, lightAccent, darkAccent, LinearGradientMode.Vertical))
                {
                    g.FillPath(fillBrush, fillPath);
                }

                // 1px top highlight sheen along the pill fill
                if (fillRect.Width > 4 && fillRect.Height > 2)
                {
                    g.DrawLine(SheenPen, fillRect.X + 2, fillRect.Y + 1, fillRect.Right - 2, fillRect.Y + 1);
                }

                // Subtle luminous gleam on the leading cap of the progress pill
                if (fillRect.Width > 6 && fillRect.Height > 4)
                {
                    int gleamX = fillRect.Right - 2;
                    using (Pen gleamPen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                    {
                        g.DrawLine(gleamPen, gleamX, fillRect.Y + 2, gleamX, fillRect.Bottom - 2);
                    }
                }
            }
        }

        // Draw a Windows 11 Fluent Button
        public static void DrawFluentButton(Graphics g, Rectangle rect, string label, Font font,
                                            bool active, bool hover, bool pressed, float radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            Color text;
            Pen hlPen = null;

            using (GraphicsPath path = Theme.RoundPath(rect, radius))
            {
                if (active)
                {
                    // Windows 11 Accent Blue
                    using (LinearGradientBrush lgb = new LinearGradientBrush(rect, Color.FromArgb(255, 0, 120, 212), Color.FromArgb(255, 0, 95, 175), LinearGradientMode.Vertical))
                    {
                        g.FillPath(lgb, path);
                    }
                    g.DrawPath(BtnActivePen, path);
                    text = Color.FromArgb(255, 255, 255);
                    hlPen = BtnActiveHlPen;
                }
                else if (pressed)
                {
                    g.FillPath(BtnPressBrush, path);
                    g.DrawPath(BtnPressPen, path);
                    text = Color.FromArgb(160, 164, 172);
                }
                else if (hover)
                {
                    using (LinearGradientBrush lgb = new LinearGradientBrush(rect, Color.FromArgb(255, 58, 58, 66), Color.FromArgb(255, 46, 46, 52), LinearGradientMode.Vertical))
                    {
                        g.FillPath(lgb, path);
                    }
                    g.DrawPath(BtnHoverPen, path);
                    text = Color.FromArgb(255, 255, 255);
                    hlPen = BtnHoverHlPen;
                }
                else
                {
                    // Idle: tactile, visible Fluent button surface
                    using (LinearGradientBrush lgb = new LinearGradientBrush(rect, Color.FromArgb(255, 44, 44, 50), Color.FromArgb(255, 34, 34, 39), LinearGradientMode.Vertical))
                    {
                        g.FillPath(lgb, path);
                    }
                    g.DrawPath(BtnIdlePen, path);
                    text = Color.FromArgb(224, 226, 230);
                    hlPen = BtnIdleHlPen;
                }
            }

            // Top highlight for active/hover/idle button
            if (hlPen != null && rect.Width > 6 && rect.Height > 4)
            {
                g.DrawLine(hlPen, rect.X + 2, rect.Y + 1, rect.Right - 2, rect.Y + 1);
            }

            Theme.DrawTextCenter(g, label, font, rect, text);
        }

        // Draw a Fluent Warning / Stale Pill Banner
        public static void DrawStaleBanner(Graphics g, Rectangle rect, string text, Font font, float radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            using (GraphicsPath path = Theme.RoundPath(rect, radius))
            {
                g.FillPath(StaleBannerBrush, path);
                g.DrawPath(StaleBannerPen, path);
            }

            // Amber text with bullet dot
            string fullText = "\u25CF " + text;
            Theme.DrawTextCenter(g, fullText, font, rect, Color.FromArgb(252, 211, 77));
        }

        // Draw a Fluent Command / Terminal Box (for logged-out or unconfigured providers)
        public static void DrawCommandBox(Graphics g, Rectangle rect, string text, Font font, float radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            // Recessed dark well terminal surface
            using (GraphicsPath path = Theme.RoundPath(rect, radius))
            {
                using (LinearGradientBrush lgb = new LinearGradientBrush(rect, Color.FromArgb(255, 22, 22, 28), Color.FromArgb(255, 15, 15, 19), LinearGradientMode.Vertical))
                {
                    g.FillPath(lgb, path);
                }
                g.DrawPath(CmdBoxPen, path);
            }

            // Specular 1px top sheen
            if (rect.Width > 8 && rect.Height > 4)
            {
                using (Pen hl = new Pen(Color.FromArgb(32, 255, 255, 255), 1f))
                {
                    g.DrawLine(hl, rect.X + 3, rect.Y + 1, rect.Right - 3, rect.Y + 1);
                }
            }

            // Frosted "CLI" action pill on the right side
            int tagPadX = Theme.S(6);
            int tagW = Theme.TextWidth("CLI", Theme.BadgeFont) + tagPadX * 2;
            int tagH = Theme.S(16);
            int tagX = rect.Right - Theme.S(6) - tagW;
            int tagY = rect.Y + (rect.Height - tagH) / 2;
            Rectangle tagRect = new Rectangle(tagX, tagY, tagW, tagH);
            using (GraphicsPath tagPath = Theme.RoundPath(tagRect, Theme.Radius(3.5f)))
            {
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(24, 255, 255, 255)))
                {
                    g.FillPath(tb, tagPath);
                }
                using (Pen tp = new Pen(Color.FromArgb(36, 255, 255, 255), 1f))
                {
                    g.DrawPath(tp, tagPath);
                }
            }
            Theme.DrawTextCenter(g, "CLI", Theme.BadgeFont, tagRect, Color.FromArgb(148, 163, 184));

            int padX = Theme.S(10);
            string prompt = "> ";
            int promptW = Theme.TextWidth(prompt, font);
            int availW = tagX - Theme.S(6) - (rect.X + padX) - promptW;
            string fitText = Theme.Fit(text, font, availW);

            int textY = rect.Y + (rect.Height - Theme.MeasureText("Ag", font).Height) / 2;
            Theme.DrawTextAt(g, prompt, font, new Point(rect.X + padX, textY), Color.FromArgb(56, 189, 248));
            Theme.DrawTextAt(g, fitText, font, new Point(rect.X + padX + promptW, textY), Color.FromArgb(220, 226, 235));
        }

        // Draw a Fluent Credit Token Tag (for extra quotas like Codex reset credits)
        public static void DrawCreditTag(Graphics g, Rectangle rect, string text, Font font, float radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            using (GraphicsPath path = Theme.RoundPath(rect, radius))
            {
                using (LinearGradientBrush lgb = new LinearGradientBrush(rect, Color.FromArgb(255, 34, 34, 40), Color.FromArgb(255, 24, 24, 29), LinearGradientMode.Vertical))
                {
                    g.FillPath(lgb, path);
                }
                g.DrawPath(CreditTagPen, path);
            }

            if (rect.Width > 8 && rect.Height > 4)
            {
                using (Pen hl = new Pen(Color.FromArgb(30, 255, 255, 255), 1f))
                {
                    g.DrawLine(hl, rect.X + 3, rect.Y + 1, rect.Right - 3, rect.Y + 1);
                }
            }

            string display = "\u25C6 " + text;
            Theme.DrawTextCenter(g, display, font, rect, Color.FromArgb(220, 226, 230));
        }
    }
}
