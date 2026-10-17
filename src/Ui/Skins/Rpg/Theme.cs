// Colours, fonts and cached GDI+ objects for the JRPG Status Screen theme (milestone M5).
// Language level: C# 5.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UsageMonitor.Skins.Rpg
{
    internal static class Theme
    {
        // Design scale: 1.0 at 96 DPI.
        public static readonly float Scale = Native.DpiScale();

        // ---- colours: classic JRPG palette ----------------------------------------------------
        // Midnight blue outer frame and canvas
        public static readonly Color FormBg = Color.FromArgb(255, 4, 8, 28);
        public static readonly Color BgSolid = Color.FromArgb(4, 8, 28);
        public static readonly Color Bg = Color.FromArgb(240, 6, 12, 38);

        // Window background gradient
        public static readonly Color WindowTop = Color.FromArgb(14, 28, 84);
        public static readonly Color WindowBottom = Color.FromArgb(4, 8, 28);

        // Borders
        public static readonly Color Border = Color.FromArgb(255, 255, 255);
        public static readonly Color BorderWhite = Color.FromArgb(255, 255, 255);
        public static readonly Color BorderInner = Color.FromArgb(230, 240, 255);
        public static readonly Color BorderLight = Color.FromArgb(100, 160, 210, 255);
        public static readonly Color CardBorder = Color.FromArgb(255, 255, 255);

        // Card backgrounds: rich deep blue gradient
        public static readonly Color CardTop = Color.FromArgb(20, 48, 136);
        public static readonly Color CardBottom = Color.FromArgb(6, 14, 52);
        public static readonly Color CardBg = Color.FromArgb(18, 42, 120);

        // KO (logged out) card background: desaturated dark slate
        public static readonly Color CardKoTop = Color.FromArgb(28, 24, 38);
        public static readonly Color CardKoBottom = Color.FromArgb(10, 8, 18);

        // Text
        public static readonly Color Text = Color.FromArgb(255, 255, 255);
        public static readonly Color RowText = Color.FromArgb(230, 240, 255);
        public static readonly Color Muted = Color.FromArgb(185, 205, 235);
        public static readonly Color Dim = Color.FromArgb(130, 150, 185);
        public static readonly Color Gold = Color.FromArgb(255, 215, 0);

        // Status & health colors
        public static readonly Color Green = Color.FromArgb(46, 204, 113);
        public static readonly Color GreenLight = Color.FromArgb(74, 222, 128);
        public static readonly Color GreenDark = Color.FromArgb(22, 163, 74);
        public static readonly Color GreenText = Color.FromArgb(80, 235, 120);

        public static readonly Color Amber = Color.FromArgb(245, 158, 11);
        public static readonly Color YellowLight = Color.FromArgb(253, 224, 71);
        public static readonly Color YellowDark = Color.FromArgb(202, 138, 4);
        public static readonly Color AmberText = Color.FromArgb(255, 210, 40);

        public static readonly Color Red = Color.FromArgb(239, 68, 68);
        public static readonly Color RedLight = Color.FromArgb(248, 113, 113);
        public static readonly Color RedDark = Color.FromArgb(185, 28, 28);
        public static readonly Color RedText = Color.FromArgb(255, 90, 90);

        public static readonly Color BarTrack = Color.FromArgb(8, 14, 36);
        public static readonly Color BarTrackBorder = Color.FromArgb(40, 65, 125);

        // Chip text colors
        public static readonly Color ChipGreenText = Color.FromArgb(134, 239, 172);
        public static readonly Color ChipRedText = Color.FromArgb(252, 165, 165);
        public static readonly Color ChipGrayText = Color.FromArgb(160, 175, 195);
        public static readonly Color PillBg = Color.FromArgb(200, 10, 20, 54);

        // Buttons & badges
        public static readonly Color BadgeFill = Color.FromArgb(60, 255, 255, 255);
        public static readonly Color HoverFill = Color.FromArgb(40, 60, 130);
        public static readonly Color PressFill = Color.FromArgb(60, 90, 180);
        public static readonly Color ActiveFill = Color.FromArgb(60, 50, 12);
        public static readonly Color IdleFill = Color.FromArgb(16, 28, 72);
        public static readonly Color DockedBar = Color.FromArgb(180, 96, 165, 250);

        // Key color for transparent cutouts
        public static readonly Color KeyColor = Color.FromArgb(255, 255, 0, 255);

        // ---- brand accents -------------------------------------------------------------------
        public static readonly Color Claude = Color.FromArgb(224, 122, 95);
        public static readonly Color Codex = Color.FromArgb(96, 165, 250);
        public static readonly Color Gemini = Color.FromArgb(124, 156, 255);
        public static readonly Color Grok = Color.FromArgb(214, 211, 209);

        // ---- fonts (created once) ------------------------------------------------------------
        public static readonly Font ChipFont = new Font("Consolas", Px(11), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font BadgeFont = new Font("Bahnschrift", Px(9f), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font RowLabelFont = new Font("Segoe UI", Px(11), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font RowFont = new Font("Segoe UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font RowPctFont = new Font("Consolas", Px(11), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font NameFont = new Font("Segoe UI", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font PlanFont = new Font("Segoe UI", Px(10), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font Badge2Font = new Font("Segoe UI", Px(10), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font HeadFont = new Font("Segoe UI", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font ButtonFont = new Font("Segoe UI", Px(10), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font HandCursorFont = new Font("Segoe UI Symbol", Px(15), FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font StatTagFont = new Font("Bahnschrift", Px(10), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font KoFont = new Font("Bahnschrift", Px(9), FontStyle.Bold, GraphicsUnit.Pixel);

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
        public static readonly SolidBrush GoldBrush = new SolidBrush(Gold);
        public static readonly SolidBrush BarTrackBrush = new SolidBrush(BarTrack);
        public static readonly SolidBrush TrackBgBrush = new SolidBrush(BarTrack);
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
        public static readonly SolidBrush KoBgBrush = new SolidBrush(Color.FromArgb(192, 24, 24));
        public static readonly SolidBrush UnmetBgBrush = new SolidBrush(Color.FromArgb(32, 40, 70));
        public static readonly SolidBrush StaleBadgeBgBrush = new SolidBrush(Color.FromArgb(64, 38, 8));

        public static readonly Pen GreenPen = MakePen(Green);
        public static readonly Pen ChipGreenPen = MakePen(Color.FromArgb(36, 140, 80));
        public static readonly Pen RedPen = MakePen(Red);
        public static readonly Pen GrayPen = MakePen(Dim);
        public static readonly Pen BorderLightPen = MakePen(BorderLight);
        public static readonly Pen CardBorderPen = MakePen(CardBorder);
        public static readonly Pen BorderWhitePen = MakePen(BorderWhite);
        public static readonly Pen BorderInnerPen = MakePen(BorderInner);
        public static readonly Pen BadgePen = MakePen(Color.FromArgb(140, 255, 255, 255));
        public static readonly Pen GoldPen = MakePen(Gold);
        public static readonly Pen AmberPen = MakePen(Amber);
        public static readonly Pen KoBorderPen = MakePen(Gold);
        public static readonly Pen UnmetBorderPen = MakePen(Color.FromArgb(140, 165, 210));
        public static readonly Pen ButtonBorderIdlePen = MakePen(Color.FromArgb(140, 170, 220));
        public static readonly Pen ButtonInnerActivePen = MakePen(Color.FromArgb(120, 95, 25));
        public static readonly Pen ButtonInnerHoverPen = MakePen(Color.FromArgb(100, 160, 240));
        public static readonly Pen ButtonInnerIdlePen = MakePen(Color.FromArgb(30, 50, 110));
        public static readonly Pen TrackBorderPen = MakePen(BarTrackBorder);
        public static readonly Pen TrackHighlightPen = MakePen(Color.FromArgb(40, 80, 160));
        public static readonly Pen DockedPen = new Pen(DockedBar, Math.Max(2f, Scale * 2f));

        public static readonly Pen ShinePen40 = new Pen(Color.FromArgb(40, 255, 255, 255), 1f);
        public static readonly Pen ShinePen50 = new Pen(Color.FromArgb(50, 255, 255, 255), 1f);
        public static readonly Pen ShinePen180 = new Pen(Color.FromArgb(180, 255, 255, 255), 1f);
        public static readonly Pen CapPen235 = new Pen(Color.FromArgb(235, 255, 255, 255), 1f);
        public static readonly Pen ShadowPen90 = new Pen(Color.FromArgb(90, 0, 0, 0), 1f);

        // Dashed outline for stale chips
        public static readonly Pen StaleGreenPen = MakePen(Green);
        public static readonly Pen StaleRedPen = MakePen(Red);
        public static readonly Pen StaleGrayPen = MakePen(Dim);
        public static readonly Pen StaleAmberPen = MakePen(Amber);

        static Theme()
        {
            StaleGreenPen.DashStyle = DashStyle.Dash;
            StaleRedPen.DashStyle = DashStyle.Dash;
            StaleGrayPen.DashStyle = DashStyle.Dash;
            StaleAmberPen.DashStyle = DashStyle.Dash;
        }

        public static float Px(float design)
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
            if (colorClass == ChipModel.ClassGreen) return ChipGreenPen;
            return GrayPen;
        }

        public static Pen ChipStalePen(string colorClass)
        {
            return StaleAmberPen;
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
            TextRenderer.DrawText(g, text, font, box, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
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
