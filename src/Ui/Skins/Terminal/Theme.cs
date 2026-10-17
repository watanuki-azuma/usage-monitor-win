// Colours, fonts and cached GDI+ objects for the Hacker Terminal theme.
//
// Language level: C# 5.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UsageMonitor.Skins.Terminal
{
    internal static class Theme
    {
        // Design scale: 1.0 at 96 DPI.
        public static readonly float Scale = Native.DpiScale();

        // ---- colours (Hacker Terminal) --------------------------------------------------------
        // Terminal CRT pitch black backgrounds
        public static readonly Color Bg = Color.FromArgb(245, 10, 14, 10);
        public static readonly Color BgSolid = Color.FromArgb(10, 14, 10);
        public static readonly Color Border = Color.FromArgb(30, 80, 42);
        public static readonly Color BorderLight = Color.FromArgb(45, 115, 60);
        public static readonly Color CardBg = Color.FromArgb(14, 20, 14);
        public static readonly Color CardBorder = Color.FromArgb(38, 95, 48);
        public static readonly Color Text = Color.FromArgb(225, 255, 235);
        public static readonly Color RowText = Color.FromArgb(140, 230, 155);
        public static readonly Color Muted = Color.FromArgb(110, 180, 130);
        public static readonly Color Dim = Color.FromArgb(75, 130, 95);
        public static readonly Color Green = Color.FromArgb(51, 255, 85);           // #33ff55 phosphor green
        public static readonly Color Red = Color.FromArgb(255, 68, 68);             // #ff4444 phosphor red
        public static readonly Color BarTrack = Color.FromArgb(58, 125, 75);         // clear phosphor dot track
        public static readonly Color Amber = Color.FromArgb(255, 176, 0);           // #ffb000 phosphor amber
        public static readonly Color ChipGreenText = Color.FromArgb(51, 255, 85);
        public static readonly Color ChipRedText = Color.FromArgb(255, 85, 85);
        public static readonly Color ChipGrayText = Color.FromArgb(114, 176, 133);   // readable phosphor gray
        public static readonly Color PillBg = Color.FromArgb(12, 18, 14);
        public static readonly Color BadgeFill = Color.FromArgb(40, 51, 255, 85);
        public static readonly Color HoverFill = Color.FromArgb(35, 51, 255, 85);
        public static readonly Color PressFill = Color.FromArgb(60, 51, 255, 85);
        public static readonly Color ActiveFill = Color.FromArgb(255, 176, 0);       // amber for active
        public static readonly Color IdleFill = Color.FromArgb(16, 24, 18);
        public static readonly Color DockedBar = Color.FromArgb(51, 255, 85);

        public static readonly Color KeyColor = Color.FromArgb(255, 255, 0, 255);
        public static readonly Color FormBg = Color.FromArgb(255, 10, 14, 10);

        // ---- brand accents -------------------------------------------------------------------
        public static readonly Color Claude = Color.FromArgb(255, 133, 85);    // warm terminal amber-orange
        public static readonly Color Codex = Color.FromArgb(80, 200, 255);      // electric cyan
        public static readonly Color Gemini = Color.FromArgb(140, 164, 255);   // bright violet/indigo
        public static readonly Color Grok = Color.FromArgb(210, 220, 215);     // phosphor silver

        // ---- fonts (Cascadia Mono with fallback to Consolas) ----------------------------------
        private static string monoFamily;
        private static string MonoFamily
        {
            get
            {
                if (monoFamily == null)
                {
                    try
                    {
                        using (Font test = new Font("Cascadia Mono", 10f, FontStyle.Regular, GraphicsUnit.Pixel))
                        {
                            if (test.Name.IndexOf("Cascadia", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                monoFamily = "Cascadia Mono";
                                return monoFamily;
                            }
                        }
                    }
                    catch (Exception) { }
                    monoFamily = "Consolas";
                }
                return monoFamily;
            }
        }

        private static float Px(float design)
        {
            return Math.Max(1f, design * Scale);
        }

        private static Font MakeMonoFont(float designPx, FontStyle style)
        {
            return new Font(MonoFamily, Px(designPx), style, GraphicsUnit.Pixel);
        }

        public static readonly Font ChipFont = MakeMonoFont(11f, FontStyle.Bold);
        public static readonly Font BadgeFont = MakeMonoFont(9.5f, FontStyle.Bold);
        public static readonly Font RowLabelFont = MakeMonoFont(11f, FontStyle.Bold);
        public static readonly Font RowFont = MakeMonoFont(11f, FontStyle.Regular);
        public static readonly Font RowPctFont = MakeMonoFont(11f, FontStyle.Bold);
        public static readonly Font NameFont = MakeMonoFont(12f, FontStyle.Bold);
        public static readonly Font PlanFont = MakeMonoFont(10.5f, FontStyle.Regular);
        public static readonly Font Badge2Font = MakeMonoFont(10f, FontStyle.Regular);
        public static readonly Font HeadFont = MakeMonoFont(12f, FontStyle.Bold);
        public static readonly Font ButtonFont = MakeMonoFont(11f, FontStyle.Bold);

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

        // Cached card header background brushes
        public static readonly SolidBrush CardHeadNormalBrush = new SolidBrush(Color.FromArgb(18, 30, 22));
        public static readonly SolidBrush CardHeadStaleBrush = new SolidBrush(Color.FromArgb(34, 26, 12));
        public static readonly SolidBrush CardHeadAlertBrush = new SolidBrush(Color.FromArgb(40, 14, 14));
        public static readonly SolidBrush CardHeadOfflineBrush = new SolidBrush(Color.FromArgb(16, 22, 18));

        // Cached meter bracket & dot track brushes
        public static readonly SolidBrush MeterBracketGreenBrush = new SolidBrush(Color.FromArgb(90, 195, 115));
        public static readonly SolidBrush MeterTrackRedBrush = new SolidBrush(Color.FromArgb(120, 42, 42));
        public static readonly SolidBrush MeterTrackAmberBrush = new SolidBrush(Color.FromArgb(120, 85, 25));

        // Cached button background brushes and pen
        public static readonly SolidBrush ButtonIdleBgBrush = new SolidBrush(Color.FromArgb(14, 20, 15));
        public static readonly SolidBrush ButtonHoverBgBrush = new SolidBrush(Color.FromArgb(20, 60, 30));
        public static readonly SolidBrush ButtonPressBgBrush = new SolidBrush(Color.FromArgb(40, 100, 50));
        public static readonly Pen ButtonPen = MakePen(Color.FromArgb(45, 115, 60));
        public static readonly Color ButtonIdleText = Color.FromArgb(170, 240, 185);
        public static readonly Color OfflineText = Color.FromArgb(170, 195, 180);
        public static readonly Color PlanText = Color.FromArgb(130, 200, 150);

        public static readonly Pen GreenPen = MakePen(Green);
        public static readonly Pen RedPen = MakePen(Red);
        public static readonly Pen AmberPen = MakePen(Amber);
        public static readonly Pen GrayPen = MakePen(Dim);
        public static readonly Pen BorderLightPen = MakePen(BorderLight);
        public static readonly Pen CardBorderPen = MakePen(CardBorder);
        public static readonly Pen AlertCardBorderPen = MakePen(Color.FromArgb(110, 40, 40));
        public static readonly Pen StaleCardBorderPen = MakePen(Color.FromArgb(100, 75, 25));
        public static readonly Pen OfflineCardBorderPen = MakePen(Color.FromArgb(40, 70, 48));
        public static readonly Pen BadgePen = MakePen(Color.FromArgb(100, 51, 255, 85));
        public static readonly Pen DockedPen = new Pen(DockedBar, Math.Max(2f, Scale * 2f));

        public static readonly Pen StaleGreenPen = MakePen(Amber);
        public static readonly Pen StaleRedPen = MakePen(Red);
        public static readonly Pen StaleGrayPen = MakePen(Dim);

        static Theme()
        {
            StaleGreenPen.DashStyle = DashStyle.Dash;
            StaleRedPen.DashStyle = DashStyle.Dash;
            StaleGrayPen.DashStyle = DashStyle.Dash;
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
            if (providerId == "loading") return ">";
            return "?";
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
            if (colorClass == ChipModel.ClassGreen) return Green;
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

        public static float Radius(float design)
        {
            return Math.Max(1f, design * Scale);
        }

        public static GraphicsPath RoundPath(Rectangle rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
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
            if (text == null || text.Length == 0) return;
            TextRenderer.DrawText(g, text, font, location, color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        public static void DrawTextRight(Graphics g, string text, Font font, Rectangle box, Color color)
        {
            if (text == null || text.Length == 0) return;
            TextRenderer.DrawText(g, text, font, box, color, TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        public static void DrawTextCenter(Graphics g, string text, Font font, Rectangle box, Color color)
        {
            if (text == null || text.Length == 0) return;
            TextRenderer.DrawText(g, text, font, box, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.NoClipping);
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
