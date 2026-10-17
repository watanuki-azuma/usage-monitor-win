// Colours, fonts and cached GDI+ objects for the owner-drawn UI (milestone M5).
//
// Port of reference/js/ui/shared.css: one place where every colour of the app lives, plus the
// brand accents (claude #e07a5f, codex #60a5fa, gemini #7c9cff, grok #d6d3d1) and the brand
// initials (C / X / G / K) drawn on a rounded badge.
//
// Fonts, brushes and pens are built exactly once, in the static constructor, and are shared by
// every paint: the UI must not allocate GDI objects per frame.
//
// Language level: C# 5.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UsageMonitor
{
    internal static class Theme
    {
        // Design scale: 1.0 at 96 DPI. Everything below is expressed in design pixels and
        // multiplied by this factor, so the bar stays legible on a scaled display.
        public static readonly float Scale = Native.DpiScale();

        // ---- colours (shared.css) -------------------------------------------------------------
        public static readonly Color Bg = Color.FromArgb(240, 17, 24, 39);          // rgba(17,24,39,0.94)
        public static readonly Color BgSolid = Color.FromArgb(17, 24, 39);           // #111827
        public static readonly Color Border = Color.FromArgb(55, 65, 81);            // #374151
        public static readonly Color BorderLight = Color.FromArgb(36, 255, 255, 255); // rgba(255,255,255,0.14)
        public static readonly Color CardBg = Color.FromArgb(23, 31, 44);            // rgba(31,41,55,0.42) on top of the bg
        public static readonly Color CardBorder = Color.FromArgb(20, 255, 255, 255);  // rgba(255,255,255,0.08)
        public static readonly Color Text = Color.FromArgb(243, 244, 246);            // #f3f4f6
        public static readonly Color RowText = Color.FromArgb(209, 213, 219);         // #d1d5db
        public static readonly Color Muted = Color.FromArgb(156, 163, 175);           // #9ca3af
        public static readonly Color Dim = Color.FromArgb(107, 114, 128);             // #6b7280
        public static readonly Color Green = Color.FromArgb(34, 197, 94);             // #22c55e
        public static readonly Color Red = Color.FromArgb(239, 68, 68);               // #ef4444
        public static readonly Color BarTrack = Color.FromArgb(31, 41, 55);           // #1f2937
        public static readonly Color Amber = Color.FromArgb(245, 158, 11);            // #f59e0b
        public static readonly Color ChipGreenText = Color.FromArgb(187, 247, 208);   // #bbf7d0
        public static readonly Color ChipRedText = Color.FromArgb(254, 202, 202);     // #fecaca
        public static readonly Color ChipGrayText = Color.FromArgb(156, 163, 175);    // #9ca3af
        public static readonly Color PillBg = Color.FromArgb(200, 15, 21, 34);        // rgba(17,24,39,0.7) over the bar
        public static readonly Color BadgeFill = Color.FromArgb(60, 255, 255, 255);
        public static readonly Color HoverFill = Color.FromArgb(16, 255, 255, 255);
        public static readonly Color PressFill = Color.FromArgb(28, 255, 255, 255);
        public static readonly Color ActiveFill = Color.FromArgb(38, 251, 191, 36);
        public static readonly Color IdleFill = Color.FromArgb(26, 255, 255, 255);
        public static readonly Color DockedBar = Color.FromArgb(140, 96, 165, 250);   // rgba(96,165,250,0.55)

        // Magenta is the "not painted" colour: WinForms punches it out of the layered window,
        // which is what gives the bar its rounded corners.
        public static readonly Color KeyColor = Color.FromArgb(255, 255, 0, 255);
        // Opaque colour behind the rounded body; the window itself is cut to the rounded shape with a Region
        // (a colour key blended magenta into the anti-aliased border).
        public static readonly Color FormBg = Color.FromArgb(255, 17, 24, 39);

        // ---- brand accents -------------------------------------------------------------------
        public static readonly Color Claude = Color.FromArgb(224, 122, 95);   // #e07a5f
        public static readonly Color Codex = Color.FromArgb(96, 165, 250);     // #60a5fa
        public static readonly Color Gemini = Color.FromArgb(124, 156, 255);  // #7c9cff
        public static readonly Color Grok = Color.FromArgb(214, 211, 209);    // #d6d3d1

        // ---- fonts (created once) ------------------------------------------------------------
        public static readonly Font ChipFont = new Font("Segoe UI", Px(11), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font BadgeFont = new Font("Segoe UI", Px(9), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font RowLabelFont = new Font("Segoe UI", Px(11), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font RowFont = new Font("Segoe UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font RowPctFont = new Font("Segoe UI", Px(11), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font NameFont = new Font("Segoe UI", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font PlanFont = new Font("Segoe UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Badge2Font = new Font("Segoe UI", Px(10), FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font HeadFont = new Font("Segoe UI", Px(12), FontStyle.Bold, GraphicsUnit.Pixel);
        public static readonly Font ButtonFont = new Font("Segoe UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);

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

        public static readonly Pen GreenPen = MakePen(Green);
        public static readonly Pen RedPen = MakePen(Red);
        public static readonly Pen GrayPen = MakePen(Dim);
        public static readonly Pen BorderLightPen = MakePen(BorderLight);
        public static readonly Pen CardBorderPen = MakePen(CardBorder);
        public static readonly Pen BadgePen = MakePen(Color.FromArgb(70, 255, 255, 255));
        // The docked flyout draws a 2px accent line at its bottom edge (shared.css).
        public static readonly Pen DockedPen = new Pen(DockedBar, Math.Max(2f, Scale * 2f));

        // Dashed outline for stale chips: one pen per colour class, created once.
        public static readonly Pen StaleGreenPen = MakePen(Green);
        public static readonly Pen StaleRedPen = MakePen(Red);
        public static readonly Pen StaleGrayPen = MakePen(Dim);

        static Theme()
        {
            StaleGreenPen.DashStyle = DashStyle.Dash;
            StaleRedPen.DashStyle = DashStyle.Dash;
            StaleGrayPen.DashStyle = DashStyle.Dash;
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

        // A rounded badge with the provider's initial; no SVG parsing anywhere.
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
            if (colorClass == ChipModel.ClassGreen) return GreenPen;
            return GrayPen;
        }

        // Dashed border for stale chips.
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

        // Right-aligned inside the given box.
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

        // Elide with an ellipsis when the text does not fit (row labels, like the CSS text-overflow).
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
