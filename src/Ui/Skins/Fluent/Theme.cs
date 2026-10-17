// Colours, fonts and cached GDI+ objects for the owner-drawn UI (milestone M5).
// Theme: v8-fluent (Windows 11 Fluent dark / Mica / Acrylic).
//
// Language level: C# 5 (in-box csc.exe). No ?. , no $"" , no expression-bodied members, no out var.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace UsageMonitor.Skins.Fluent
{
    internal static class Theme
    {
        // Design scale: 1.0 at 96 DPI. Everything below is expressed in design pixels and
        // multiplied by this factor, so the bar stays legible on a scaled display.
        public static readonly float Scale = Native.DpiScale();

        // ---- colours (Windows 11 Fluent Dark / Mica) -------------------------------------------
        public static readonly Color Bg = Color.FromArgb(240, 20, 20, 24);            // Mica dark base #141418
        public static readonly Color BgSolid = Color.FromArgb(20, 20, 24);             // #141418
        public static readonly Color Border = Color.FromArgb(52, 52, 58);              // Fluent surface border #34343a
        public static readonly Color BorderLight = Color.FromArgb(42, 255, 255, 255);  // rgba(255,255,255,0.16)
        public static readonly Color CardBg = Color.FromArgb(40, 40, 46);              // Acrylic card surface #28282e
        public static readonly Color CardBorder = Color.FromArgb(38, 255, 255, 255);  // rgba(255,255,255,0.15)
        public static readonly Color Text = Color.FromArgb(245, 246, 248);            // Text Primary #f5f6f8
        public static readonly Color RowText = Color.FromArgb(220, 224, 230);         // Text Secondary #dce0e6
        public static readonly Color Muted = Color.FromArgb(154, 160, 170);           // Text Muted #9aa0aa
        public static readonly Color Dim = Color.FromArgb(108, 114, 124);             // Text Dim #6c727c
        public static readonly Color Green = Color.FromArgb(34, 197, 94);             // Fluent Green / Success #22c55e
        public static readonly Color Red = Color.FromArgb(239, 68, 68);               // Fluent Danger Red #ef4444
        public static readonly Color BarTrack = Color.FromArgb(16, 16, 20);           // Recessed pill meter track #101014
        public static readonly Color Amber = Color.FromArgb(245, 158, 11);            // Fluent Warning Amber #f59e0b
        public static readonly Color ChipGreenText = Color.FromArgb(245, 246, 248);    // Fluent primary/secondary text #f5f6f8
        public static readonly Color ChipRedText = Color.FromArgb(254, 202, 202);     // Crisp light coral for alert #fecaca
        public static readonly Color ChipGrayText = Color.FromArgb(148, 163, 184);    // Sleek slate #94a3b8
        public static readonly Color ChipAmberText = Color.FromArgb(252, 211, 77);    // Fluent warning amber #fcd34d
        public static readonly Color PillBg = Color.FromArgb(36, 36, 41);             // Taskbar pill background #242429
        public static readonly Color BadgeFill = Color.FromArgb(60, 255, 255, 255);
        public static readonly Color HoverFill = Color.FromArgb(42, 255, 255, 255);   // Button hover
        public static readonly Color PressFill = Color.FromArgb(16, 255, 255, 255);   // Button pressed
        public static readonly Color ActiveFill = Color.FromArgb(0, 103, 192);        // Windows 11 Accent Blue #0067c0
        public static readonly Color IdleFill = Color.FromArgb(22, 255, 255, 255);    // Button idle
        public static readonly Color DockedBar = Color.FromArgb(0, 120, 212);         // Fluent Accent Blue #0078d4

        // Magenta is the "not painted" colour: WinForms punches it out of the layered window.
        public static readonly Color KeyColor = Color.FromArgb(255, 255, 0, 255);
        // Opaque colour behind the rounded body; cut to rounded shape with a Region.
        public static readonly Color FormBg = Color.FromArgb(255, 20, 20, 24);

        // ---- brand accents (vibrant Fluent tones) --------------------------------------------
        public static readonly Color Claude = Color.FromArgb(234, 88, 12);            // #ea580c warm orange/coral
        public static readonly Color Codex = Color.FromArgb(59, 130, 246);            // #3b82f6 electric blue
        public static readonly Color Gemini = Color.FromArgb(129, 140, 248);          // #818cf8 vibrant indigo
        public static readonly Color Grok = Color.FromArgb(203, 213, 225);            // #cbd5e1 sleek platinum

        // ---- fonts (created once, Segoe UI Semibold for Windows 11 Fluent feel) ---------------
        public static readonly Font ChipFont = MakeFluentFont(Px(11), true);
        public static readonly Font BadgeFont = MakeFluentFont(Px(9), true);
        public static readonly Font RowLabelFont = MakeFluentFont(Px(11), true);
        public static readonly Font RowFont = MakeFluentFont(Px(11), false);
        public static readonly Font RowPctFont = MakeFluentFont(Px(11), true);
        public static readonly Font NameFont = MakeFluentFont(Px(12), true);
        public static readonly Font PlanFont = MakeFluentFont(Px(11), false);
        public static readonly Font Badge2Font = MakeFluentFont(Px(10), true);
        public static readonly Font HeadFont = MakeFluentFont(Px(13), true);
        public static readonly Font ButtonFont = MakeFluentFont(Px(11), true);

        // ---- brushes and pens (created once) --------------------------------------------------
        public static readonly SolidBrush KeyBrush = new SolidBrush(KeyColor);
        public static readonly SolidBrush BgBrush = new SolidBrush(Bg);
        public static readonly SolidBrush PillBrush = new SolidBrush(PillBg);
        public static readonly SolidBrush CardBrush = new SolidBrush(CardBg);
        public static readonly SolidBrush TextBrush = new SolidBrush(Text);
        public static readonly SolidBrush RowBrush = new SolidBrush(RowText);
        public static readonly SolidBrush MutedBrush = new SolidBrush(Muted);
        public static readonly SolidBrush DimBrush = new SolidBrush(Dim);
        public static readonly SolidBrush AmberBrush = new SolidBrush(Amber);
        public static readonly SolidBrush BarTrackBrush = new SolidBrush(BarTrack);
        public static readonly SolidBrush GreenBrush = new SolidBrush(Green);
        public static readonly SolidBrush RedBrush = new SolidBrush(Red);
        public static readonly SolidBrush BadgeBrush = new SolidBrush(BadgeFill);
        public static readonly SolidBrush ClaudeBrush = new SolidBrush(Claude);
        public static readonly SolidBrush CodexBrush = new SolidBrush(Codex);
        public static readonly SolidBrush GeminiBrush = new SolidBrush(Gemini);
        public static readonly SolidBrush GrokBrush = new SolidBrush(Grok);
        public static readonly SolidBrush HoverBrush = new SolidBrush(HoverFill);
        public static readonly SolidBrush PressBrush = new SolidBrush(PressFill);
        public static readonly SolidBrush ActiveBrush = new SolidBrush(ActiveFill);
        public static readonly SolidBrush IdleBrush = new SolidBrush(IdleFill);
        public static readonly SolidBrush DockedBrush = new SolidBrush(DockedBar);
        public static readonly SolidBrush HeaderIconBrush1 = new SolidBrush(Color.FromArgb(0, 120, 212));
        public static readonly SolidBrush HeaderIconBrush2 = new SolidBrush(Color.FromArgb(59, 130, 246));
        public static readonly SolidBrush HeaderIconBrush3 = new SolidBrush(Color.FromArgb(96, 205, 255));
        public static readonly SolidBrush WhiteBrush = new SolidBrush(Color.White);

        public static readonly Pen GreenPen = MakePen(Color.FromArgb(45, 255, 255, 255));
        public static readonly Pen RedPen = MakePen(Red);
        public static readonly Pen AmberPen = MakePen(Amber);
        public static readonly Pen GrayPen = MakePen(Color.FromArgb(42, 255, 255, 255));
        public static readonly Pen BorderLightPen = MakePen(BorderLight);
        public static readonly Pen CardBorderPen = MakePen(CardBorder);
        public static readonly Pen BadgePen = MakePen(Color.FromArgb(80, 255, 255, 255));
        public static readonly Pen DockedPen = new Pen(DockedBar, Math.Max(2f, Scale * 2f));
        public static readonly Pen TrackBorderPen = MakePen(Color.FromArgb(28, 255, 255, 255));

        public static readonly Pen StaleGreenPen = MakePen(Color.FromArgb(220, 245, 158, 11));
        public static readonly Pen StaleRedPen = MakePen(Red);
        public static readonly Pen StaleGrayPen = MakePen(Dim);

        static Theme()
        {
            StaleGreenPen.DashStyle = DashStyle.Dash;
            StaleRedPen.DashStyle = DashStyle.Dash;
            StaleGrayPen.DashStyle = DashStyle.Dash;
            StaleGreenPen.DashCap = DashCap.Round;
            StaleRedPen.DashCap = DashCap.Round;
            StaleGrayPen.DashCap = DashCap.Round;
            StaleGreenPen.DashPattern = new float[] { 3f, 2.5f };
            StaleRedPen.DashPattern = new float[] { 3f, 2.5f };
            StaleGrayPen.DashPattern = new float[] { 3f, 2.5f };
        }

        private static Font MakeFluentFont(float sizePx, bool semibold)
        {
            if (semibold)
            {
                try
                {
                    Font f = new Font("Segoe UI Semibold", sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
                    if (f.Name.Equals("Segoe UI Semibold", StringComparison.OrdinalIgnoreCase))
                    {
                        return f;
                    }
                    f.Dispose();
                }
                catch { }
                return new Font("Segoe UI", sizePx, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            return new Font("Segoe UI", sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static float Px(float design)
        {
            return Math.Max(1f, design * Scale);
        }

        private static Pen MakePen(Color color)
        {
            return new Pen(color, Math.Max(1f, Scale));
        }

        // ---- brand marks ---------------------------------------------------------------------

        public static Color Accent(string providerId)
        {
            if (providerId == "claude") return Claude;
            if (providerId == "codex") return Codex;
            if (providerId == "gemini") return Gemini;
            if (providerId == "grok") return Grok;
            return Muted;
        }

        public static string Initial(string providerId)
        {
            if (providerId == "claude") return "C";
            if (providerId == "codex") return "X";
            if (providerId == "gemini") return "G";
            if (providerId == "grok") return "K";
            if (providerId == "loading") return "\u2026";
            return "?";
        }

        public static Color BadgeTextColor(string providerId)
        {
            if (providerId == "grok") return Color.FromArgb(20, 20, 24);
            return Color.White;
        }

        public static SolidBrush AccentBrush(string providerId)
        {
            if (providerId == "claude") return ClaudeBrush;
            if (providerId == "codex") return CodexBrush;
            if (providerId == "gemini") return GeminiBrush;
            if (providerId == "grok") return GrokBrush;
            return MutedBrush;
        }

        public static Color ChipTextColor(string colorClass)
        {
            if (colorClass == ChipModel.ClassRed) return ChipRedText;
            if (colorClass == ChipModel.ClassGreen) return ChipGreenText;
            return ChipGrayText;
        }

        public static Color ChipBorderColor(string colorClass)
        {
            if (colorClass == ChipModel.ClassRed) return Red;
            if (colorClass == ChipModel.ClassGreen) return Color.FromArgb(36, 255, 255, 255);
            return Dim;
        }

        public static Pen ChipPen(string colorClass)
        {
            if (colorClass == ChipModel.ClassRed) return RedPen;
            if (colorClass == ChipModel.ClassGreen) return GreenPen;
            return GrayPen;
        }

        public static Pen ChipStalePen(string colorClass)
        {
            if (colorClass == ChipModel.ClassRed) return StaleRedPen;
            if (colorClass == ChipModel.ClassGreen) return StaleGreenPen;
            return StaleGrayPen;
        }

        // ---- drawing helpers -----------------------------------------------------------------

        public static int S(int design)
        {
            return (int)Math.Round(design * Scale, MidpointRounding.AwayFromZero);
        }

        public static int S(float design)
        {
            return (int)Math.Round(design * Scale, MidpointRounding.AwayFromZero);
        }

        public static float Radius(float design)
        {
            return Math.Max(1f, design * Scale);
        }

        public static GraphicsPath RoundPath(Rectangle rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            int diameter = (int)Math.Round(radius * 2f, MidpointRounding.AwayFromZero);
            if (diameter < 1) diameter = 1;
            if (diameter > rect.Width) diameter = rect.Width;
            if (diameter > rect.Height) diameter = rect.Height;

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Brush brush, Rectangle rect, float radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (GraphicsPath path = RoundPath(rect, radius))
            {
                g.FillPath(brush, path);
            }
        }

        public static void DrawRound(Graphics g, Pen pen, Rectangle rect, float radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (GraphicsPath path = RoundPath(rect, radius))
            {
                g.DrawPath(pen, path);
            }
        }

        public static Size MeasureText(string text, Font font)
        {
            if (text == null) text = "";
            Size size = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            return size;
        }

        public static int TextWidth(string text, Font font)
        {
            return MeasureText(text, font).Width;
        }

        public static void DrawTextAt(Graphics g, string text, Font font, Point location, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderingHint oldHint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (SolidBrush brush = new SolidBrush(color))
            {
                using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    sf.FormatFlags |= StringFormatFlags.NoWrap;
                    g.DrawString(text, font, brush, (float)location.X, (float)location.Y, sf);
                }
            }
            g.TextRenderingHint = oldHint;
        }

        public static void DrawTextAt(Graphics g, string text, Font font, Point location, Color color, Color backColor)
        {
            DrawTextAt(g, text, font, location, color);
        }

        public static void DrawTextRight(Graphics g, string text, Font font, Rectangle box, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderingHint oldHint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (SolidBrush brush = new SolidBrush(color))
            {
                using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    sf.Alignment = StringAlignment.Far;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags |= StringFormatFlags.NoWrap;
                    RectangleF r = new RectangleF((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height);
                    g.DrawString(text, font, brush, r, sf);
                }
            }
            g.TextRenderingHint = oldHint;
        }

        public static void DrawTextCenter(Graphics g, string text, Font font, Rectangle box, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderingHint oldHint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (SolidBrush brush = new SolidBrush(color))
            {
                using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags |= StringFormatFlags.NoWrap;
                    RectangleF r = new RectangleF((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height);
                    g.DrawString(text, font, brush, r, sf);
                }
            }
            g.TextRenderingHint = oldHint;
        }

        // Draw badge initial using vector glyph path with anti-aliasing to eliminate subpixel RGB chromatic aberration
        public static void DrawBadgeInitial(Graphics g, string initial, Font font, Rectangle badgeRect, Color color)
        {
            if (string.IsNullOrEmpty(initial)) return;
            using (GraphicsPath textPath = new GraphicsPath())
            {
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    float emSize = Math.Max(8f, badgeRect.Height * 0.68f);
                    RectangleF r = new RectangleF((float)badgeRect.X, (float)badgeRect.Y - 0.5f, (float)badgeRect.Width, (float)badgeRect.Height);
                    textPath.AddString(initial, font.FontFamily, (int)font.Style, emSize, r, sf);
                }
                using (SolidBrush b = new SolidBrush(color))
                {
                    SmoothingMode oldSm = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(b, textPath);
                    g.SmoothingMode = oldSm;
                }
            }
        }

        // Draw a radiant Fluent brand squircle badge with subtle lighting and crisp vector initial
        public static void DrawBrandBadge(Graphics g, Rectangle badgeRect, string providerId, float radius)
        {
            if (badgeRect.Width <= 0 || badgeRect.Height <= 0) return;
            Color accent = Accent(providerId);
            Color lightAccent = Fx.Lighten(accent, 0.18f);
            Color darkAccent = Fx.Darken(accent, 0.10f);

            using (GraphicsPath path = RoundPath(badgeRect, radius))
            {
                using (LinearGradientBrush lgb = new LinearGradientBrush(badgeRect, lightAccent, darkAccent, LinearGradientMode.ForwardDiagonal))
                {
                    g.FillPath(lgb, path);
                }
                g.DrawPath(BadgePen, path);
            }

            if (badgeRect.Width > 6 && badgeRect.Height > 4)
            {
                using (Pen hl = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                {
                    g.DrawLine(hl, badgeRect.X + 2, badgeRect.Y + 1, badgeRect.Right - 2, badgeRect.Y + 1);
                }
            }

            DrawBadgeInitial(g, Initial(providerId), BadgeFont, badgeRect, BadgeTextColor(providerId));
        }

        public static string Fit(string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (maxWidth <= 0) return "";
            if (TextWidth(text, font) <= maxWidth) return text;
            if (TextWidth("\u2026", font) >= maxWidth) return "\u2026";
            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int mid = (low + high) / 2;
                string candidate = text.Substring(0, mid) + "\u2026";
                if (TextWidth(candidate, font) <= maxWidth) low = mid + 1;
                else high = mid;
            }
            int take = low > 0 ? low - 1 : 0;
            return text.Substring(0, take) + "\u2026";
        }
    }
}
