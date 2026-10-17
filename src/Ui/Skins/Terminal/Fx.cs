// Visual effects and drawing helpers for the Hacker Terminal theme.
//
// Language level: C# 5.
using System;
using System.Drawing;

namespace UsageMonitor.Skins.Terminal
{
    internal static class Fx
    {
        public static readonly Color PromptUser = Color.FromArgb(51, 255, 85);       // bright phosphor green
        public static readonly Color PromptHost = Color.FromArgb(80, 200, 255);      // electric cyan
        public static readonly Color PromptPunct = Color.FromArgb(140, 230, 155);    // mid green
        public static readonly Color PromptCmd = Color.FromArgb(235, 255, 240);      // bright white-green
        public static readonly Color ScanlineColor = Color.FromArgb(14, 0, 0, 0);    // subtle CRT scanlines

        public static readonly Pen ScanlinePen = new Pen(ScanlineColor, 1f);
        public static readonly Pen InnerBezelPen = new Pen(Color.FromArgb(18, 50, 26), 1f);

        public static readonly SolidBrush PromptUserBrush = new SolidBrush(PromptUser);
        public static readonly SolidBrush PromptHostBrush = new SolidBrush(PromptHost);
        public static readonly SolidBrush PromptPunctBrush = new SolidBrush(PromptPunct);
        public static readonly SolidBrush PromptCmdBrush = new SolidBrush(PromptCmd);

        // Subtle CRT scanlines: horizontal 1px dark lines across bounds aligned to global raster
        public static void DrawScanlines(Graphics g, Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            int startY = bounds.Top + 1;
            int rem = startY % 3;
            if (rem != 1)
            {
                startY += (4 - rem) % 3;
            }
            for (int y = startY; y < bounds.Bottom - 1; y += 3)
            {
                g.DrawLine(ScanlinePen, bounds.Left + 1, y, bounds.Right - 1, y);
            }
        }

        // Draws a box-drawing cross "+" at intersection (x, y)
        public static void DrawCross(Graphics g, Pen pen, int x, int y, int size)
        {
            g.DrawLine(pen, x - size, y, x + size, y);
            g.DrawLine(pen, x, y - size, x, y + size);
        }

        // Draws a terminal box frame with divider line and box-drawing corner/junction highlights
        public static void DrawTerminalBox(Graphics g, Rectangle rect, int dividerY, Pen borderPen, Pen crossPen)
        {
            DrawTerminalBox(g, rect, dividerY, borderPen, crossPen, 0);
        }

        public static void DrawTerminalBox(Graphics g, Rectangle rect, int dividerY, Pen borderPen, Pen crossPen, int leftOffset)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            int left = rect.X;
            int top = rect.Y;
            int right = rect.Right - 1;
            int bottom = rect.Bottom - 1;

            // Frame rectangle
            g.DrawRectangle(borderPen, left, top, rect.Width - 1, rect.Height - 1);

            // Divider line & T-junctions
            if (dividerY > top && dividerY < bottom)
            {
                int divLeft = left + leftOffset;
                g.DrawLine(borderPen, divLeft, dividerY, right, dividerY);
                // Left T-junction
                g.DrawLine(crossPen, divLeft, dividerY - 3, divLeft, dividerY + 3);
                g.DrawLine(crossPen, divLeft, dividerY, divLeft + 4, dividerY);
                // Right T-junction (┤)
                g.DrawLine(crossPen, right, dividerY - 3, right, dividerY + 3);
                g.DrawLine(crossPen, right - 4, dividerY, right, dividerY);
            }

            // 4 Corner brackets (stay directly on border lines, creating glowing corner accents)
            int cornerLen = Theme.S(7);
            // Top-left ┌
            g.DrawLine(crossPen, left, top, left + cornerLen, top);
            g.DrawLine(crossPen, left, top, left, top + cornerLen);

            // Top-right ┐
            g.DrawLine(crossPen, right - cornerLen, top, right, top);
            g.DrawLine(crossPen, right, top, right, top + cornerLen);

            // Bottom-left └
            g.DrawLine(crossPen, left, bottom - cornerLen, left, bottom);
            g.DrawLine(crossPen, left, bottom, left + cornerLen, bottom);

            // Bottom-right ┘
            g.DrawLine(crossPen, right - cornerLen, bottom, right, bottom);
            g.DrawLine(crossPen, right, bottom - cornerLen, right, bottom);
        }
    }
}
