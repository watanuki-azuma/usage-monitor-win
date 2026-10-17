// The taskbar chips bar (milestone M5). Owner-drawn, borderless, always on top, no taskbar
// button and no focus stealing (WS_EX_NOACTIVATE): one rounded pill per provider showing the
// brand badge, the provider initial and "NN/100" (or "KO" for logged out).
//
// Restyled as a mini JRPG Party HUD bar.
//
// Language level: C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UsageMonitor.Skins.Rpg
{
    internal sealed class ChipsForm : ChipsFormBase
    {
        // Layout, in design pixels at 96 DPI.
        private const int BadgeSize = 13;
        private const int BadgeGap = 5;
        private const int ChipHeight = 20;
        private const int ChipPadX = 7;
        private const int ChipGap = 8;
        private const int BarPadX = 7;
        private const int BarPadY = 4;
        private const int BarBorder = 1;
        private const int DockGap = 4;
        private const int MinWidth = 96;
        private const int MaxWidth = 720;

        private static readonly string[] LoadingTexts = new string[] { "loading\u2026" };
        private readonly AppController app;

        private List<ChipInfo> chips = new List<ChipInfo>();
        private string renderKey = "";
        private bool loading = true;

        private bool dragging;
        private bool dragMoved;
        private Point dragOrigin;
        private Point dragStartCursor;

        internal ChipsForm(AppController controller)
        {
            app = controller;
            InitializeForm();
        }

        private void InitializeForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Text = "UsageMonitorChips";
            BackColor = Theme.FormBg;
            ClientSize = new Size(Theme.S(340), Theme.S(30));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            Region old = Region;
            using (GraphicsPath path = Theme.RoundPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Theme.Radius(6)))
            {
                Region = new Region(path);
            }
            if (old != null) old.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintInto(e.Graphics, ClientSize);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                dragStartCursor = Native.CursorPosition();
                dragOrigin = Location;
                dragMoved = false;
                dragging = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;

            Point cursor = Native.CursorPosition();
            if (!dragMoved)
            {
                int dx = cursor.X - dragStartCursor.X;
                int dy = cursor.Y - dragStartCursor.Y;
                if (dx < 0) dx = -dx;
                if (dy < 0) dy = -dy;
                if (dx < Theme.S(3) && dy < Theme.S(3)) return;
                dragMoved = true;
                if (!dragMoved) Capture = true;
            }

            Location = new Point(dragOrigin.X + cursor.X - dragStartCursor.X,
                                 dragOrigin.Y + cursor.Y - dragStartCursor.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            bool moved = dragMoved;
            dragging = false;
            if (Capture) Capture = false;

            if (moved) FinishDrag();
            else if (app != null) app.ToggleFlyout();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Right) return;
            if (app != null) app.ShowTrayMenu();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetTopMost(Handle, true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            if (e.CloseReason == CloseReason.ApplicationExitCall ||
                e.CloseReason == CloseReason.WindowsShutDown ||
                e.CloseReason == CloseReason.TaskManagerClosing)
            {
                return;
            }
            e.Cancel = true;
            Hide();
        }

        // --------------------------------------------------------------------------------------
        // model -> pixels
        // --------------------------------------------------------------------------------------

        // The RPG flyout shows REMAINING (HP 90/100 = 10% used), so the chips show the same number.
        // Colour and stale flag still follow the used percentage (ChipModel). Windows without a
        // used value keep the neutral dash text.
        internal static ChipInfo ChipFor(ProviderSnapshot provider)
        {
            ChipInfo chip = ChipModel.For(provider);
            UsageWindow w = provider == null ? null : Models.CurrentWindow(provider);
            if (w == null || !w.UsedPct.HasValue) return chip;

            double used = w.UsedPct.Value;
            if (used < 0) used = 0;
            if (used > 100) used = 100;
            double rem = w.RemainingPct.HasValue ? w.RemainingPct.Value : (100.0 - used);
            if (rem < 0) rem = 0;
            if (rem > 100) rem = 100;
            int remInt = (int)Math.Round(rem, MidpointRounding.AwayFromZero);
            chip.Text = remInt.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/100";
            return chip;
        }

        private static string RemainingKey(List<ChipInfo> list)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append(list[i] == null || list[i].Text == null ? "" : list[i].Text).Append('\u0001');
            }
            return sb.ToString();
        }

        internal override void SetSnapshot(Snapshot snapshot)
        {
            List<ChipInfo> next = new List<ChipInfo>();
            if (snapshot != null && snapshot.Providers != null)
            {
                foreach (ProviderSnapshot provider in snapshot.Providers)
                {
                    if (provider == null) continue;
                    next.Add(ChipFor(provider));
                }
            }

            bool loadingNow = next.Count == 0;
            string key = ChipModel.Key(snapshot) + "|" + RemainingKey(next) + "|" + (loadingNow ? "1" : "0");
            loading = loadingNow;
            chips = next;
            if (key == renderKey) return;
            renderKey = key;

            Size size = MeasureSize();
            if (!IsHandleCreated) ClientSize = size;
            else SetClientSize(size);
            Invalidate();
        }

        internal override Size MeasureSize()
        {
            if (loading || chips.Count == 0) return MeasureSizeOf(LoadingTexts);

            int count = chips.Count;
            int width = Theme.S(BarPadX) * 2 + Theme.S(BarBorder) * 2;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) width += Theme.S(ChipGap);
                width += PillWidth(chips[i]);
            }
            if (width < Theme.S(MinWidth)) width = Theme.S(MinWidth);
            if (width > Theme.S(MaxWidth)) width = Theme.S(MaxWidth);
            int height = Theme.S(ChipHeight) + Theme.S(BarPadY) * 2 + Theme.S(BarBorder) * 2;
            return new Size(width, height);
        }

        private Size MeasureSizeOf(string[] texts)
        {
            int width = Theme.S(BarPadX) * 2 + Theme.S(BarBorder) * 2;
            for (int i = 0; i < texts.Length; i++)
            {
                if (i > 0) width += Theme.S(ChipGap);
                width += PillWidthFor(texts[i]);
            }
            if (width < Theme.S(MinWidth)) width = Theme.S(MinWidth);
            int height = Theme.S(ChipHeight) + Theme.S(BarPadY) * 2 + Theme.S(BarBorder) * 2;
            return new Size(width, height);
        }

        private int PillWidth(ChipInfo chip)
        {
            return PillWidthFor(chip == null ? "" : chip.Text);
        }

        private int PillWidthFor(string text)
        {
            string displayText = (text == "—") ? "KO" : text;
            int content = Theme.S(BadgeSize) + Theme.S(BadgeGap) + Theme.TextWidth(displayText, Theme.ChipFont);
            return content + Theme.S(ChipPadX) * 2;
        }

        // --------------------------------------------------------------------------------------
        // painting
        // --------------------------------------------------------------------------------------

        internal override void PaintInto(Graphics g, Size size)
        {
            int width = size.Width;
            int height = size.Height;
            g.Clear(Theme.FormBg);

            Rectangle bar = new Rectangle(0, 0, width - 1, height - 1);
            using (LinearGradientBrush brush = new LinearGradientBrush(bar, Theme.WindowTop, Theme.WindowBottom, LinearGradientMode.Vertical))
            {
                Theme.FillRound(g, brush, bar, Theme.Radius(6));
            }
            Theme.DrawRound(g, Theme.BorderWhitePen, bar, Theme.Radius(6));
            int inset = Theme.S(3);
            if (inset < 3) inset = 3;
            Rectangle innerBar = new Rectangle(bar.X + inset, bar.Y + inset, bar.Width - inset * 2, bar.Height - inset * 2);
            if (innerBar.Width > 0 && innerBar.Height > 0)
            {
                Theme.DrawRound(g, Theme.BorderInnerPen, innerBar, Theme.Radius(4));
            }

            int x = Theme.S(BarPadX) + Theme.S(BarBorder);
            int y = Theme.S(BarPadY) + Theme.S(BarBorder);
            int pillHeight = Theme.S(ChipHeight);

            if (loading || chips.Count == 0)
            {
                DrawPill(g, x, y, pillHeight, "loading", LoadingTexts[0], ChipModel.ClassGray, false);
                return;
            }

            foreach (ChipInfo chip in chips)
            {
                if (chip == null) continue;
                int pillWidth = PillWidth(chip);
                DrawPill(g, x, y, pillHeight, chip.Id, chip.Text, chip.ColorClass, chip.Stale);
                x += pillWidth + Theme.S(ChipGap);
            }
        }

        private void DrawPill(Graphics g, int x, int y, int height, string providerId, string text,
                              string colorClass, bool stale)
        {
            string displayText = (text == "—") ? "KO" : text;
            bool isKo = (text == "—") || (displayText == "KO");

            int pillWidth = PillWidthFor(text);
            Rectangle pill = new Rectangle(x, y, pillWidth, height);
            if (pill.Right > ClientSize.Width) return;

            Color topCol;
            Color botCol;
            if (isKo)
            {
                topCol = Color.FromArgb(24, 18, 28);
                botCol = Color.FromArgb(10, 8, 14);
            }
            else if (stale)
            {
                topCol = Color.FromArgb(38, 28, 12);
                botCol = Color.FromArgb(18, 12, 6);
            }
            else if (colorClass == ChipModel.ClassRed)
            {
                topCol = Color.FromArgb(40, 18, 26);
                botCol = Color.FromArgb(18, 8, 12);
            }
            else
            {
                topCol = Color.FromArgb(18, 38, 96);
                botCol = Color.FromArgb(6, 12, 38);
            }

            using (LinearGradientBrush brush = new LinearGradientBrush(pill, topCol, botCol, LinearGradientMode.Vertical))
            {
                Theme.FillRound(g, brush, pill, Theme.Radius(3));
            }
            if (pill.Height > 4)
            {
                g.DrawLine(Theme.ShinePen50, pill.X + 2, pill.Y + 1, pill.Right - 3, pill.Y + 1);
            }

            Pen borderPen;
            if (isKo) borderPen = Theme.GrayPen;
            else if (stale) borderPen = Theme.ChipStalePen(colorClass);
            else borderPen = Theme.ChipPen(colorClass);

            Theme.DrawRound(g, borderPen, pill, Theme.Radius(3));

            int badgeX = pill.X + Theme.S(ChipPadX);
            int badgeY = pill.Y + (pill.Height - Theme.S(BadgeSize)) / 2;
            Rectangle badge = new Rectangle(badgeX, badgeY, Theme.S(BadgeSize), Theme.S(BadgeSize));
            SolidBrush accent = Theme.AccentBrush(providerId);
            Theme.FillRound(g, accent, badge, Theme.Radius(2));
            Theme.DrawRound(g, isKo ? Theme.RedPen : Theme.GoldPen, badge, Theme.Radius(2));
            Theme.DrawTextCenter(g, Theme.Initial(providerId), Theme.BadgeFont, badge, Theme.BgSolid);

            int textX = badge.Right + Theme.S(BadgeGap);
            Color textCol;
            if (isKo) textCol = Theme.RedText;
            else if (stale) textCol = Theme.AmberText;
            else textCol = Theme.ChipTextColor(colorClass);

            int textY = pill.Y + (pill.Height - TextHeight(Theme.ChipFont)) / 2;
            Theme.DrawTextAt(g, displayText, Theme.ChipFont, new Point(textX + 1, textY + 1), Color.FromArgb(0, 0, 0));
            Theme.DrawTextAt(g, displayText, Theme.ChipFont, new Point(textX, textY), textCol);
        }

        private int TextHeight(Font font)
        {
            return Theme.MeasureText("Ag", font).Height;
        }

        // --------------------------------------------------------------------------------------
        // docking and dragging
        // --------------------------------------------------------------------------------------

        internal bool Docked
        {
            get { return app == null ? true : app.ChipsDocked; }
        }

        internal bool Hidden
        {
            get { return app == null ? false : app.ChipsHidden; }
        }

        internal override void Place()
        {
            if (Docked) PlaceDocked();
            else PlaceFloating();
        }

        internal override void PlaceDocked()
        {
            Size size = ClientSize;
            RectI taskbar = TaskbarRect();
            RectI tray = TrayRect();
            int gap = Theme.S(DockGap);
            bool horizontal = taskbar.W >= taskbar.H;

            RectI place = TaskbarMath.DockBeforeTray(taskbar, tray, size.Width, size.Height, gap);

            bool fits = horizontal
                ? (tray.X - taskbar.X) >= size.Width + gap
                : (tray.Y - taskbar.Y) >= size.Height + gap;

            if (fits)
            {
                place = TaskbarMath.Clamp(place, Native.ToRectI(Screen.PrimaryScreen.Bounds));
            }
            else
            {
                if (horizontal)
                {
                    place = new RectI(tray.X - gap - size.Width, taskbar.Y - gap - size.Height,
                                      size.Width, size.Height);
                }
                else
                {
                    place = new RectI(taskbar.X + taskbar.W + gap, tray.Y - gap - size.Height,
                                      size.Width, size.Height);
                }
                place = TaskbarMath.Clamp(place, Native.ToRectI(Screen.PrimaryScreen.Bounds));
            }

            Native.MoveTo(Handle, place.X, place.Y, place.W, place.H);
            if (app != null) app.RememberDockPosition(place.X, place.Y);
        }

        internal void PlaceFloating()
        {
            int width = ClientSize.Width;
            int height = ClientSize.Height;

            bool stored = app != null && app.HasStoredChipsPosition();
            Screen screen;
            int targetX;
            int targetY;
            bool offScreen = false;

            if (stored)
            {
                targetX = app.ChipsX(0);
                targetY = app.ChipsY(0);
                Point saved = new Point(targetX, targetY);
                screen = ScreenFromPoint(saved);
                offScreen = !PointOnAnyScreen(saved);
            }
            else
            {
                screen = Screen.FromControl(this);
                Rectangle area = screen.WorkingArea;
                targetX = area.Right - width - 12;
                targetY = area.Bottom - height - 8;
            }

            Rectangle work = screen.WorkingArea;
            int x = targetX;
            int y = targetY;
            if (x < work.X) x = work.X;
            if (y < work.Y) y = work.Y;
            if (x + width > work.Right) x = work.Right - width;
            if (y + height > work.Bottom) y = work.Bottom - height;

            Native.MoveTo(Handle, x, y, width, height);

            if (app != null && (!stored || offScreen))
            {
                app.RememberChipsPosition(x, y);
            }
        }

        private static Screen ScreenFromPoint(Point point)
        {
            Screen[] all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Bounds.Contains(point)) return all[i];
            }
            return Screen.FromPoint(point);
        }

        private static bool PointOnAnyScreen(Point point)
        {
            Screen[] all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Bounds.Contains(point)) return true;
            }
            return false;
        }

        private void FinishDrag()
        {
            RectI window = new RectI(Location.X, Location.Y, ClientSize.Width, ClientSize.Height);
            bool overTray = TaskbarMath.IsOverTray(window, TrayRect());
            if (overTray)
            {
                if (app != null) app.SetChipsDocked(true);
                PlaceDocked();
                return;
            }

            if (app != null)
            {
                app.SetChipsDocked(false);
                app.RememberChipsPosition(Location.X, Location.Y);
            }
            PlaceFloating();
        }

        private RectI TaskbarRect()
        {
            RectI rect;
            if (Native.RectOf(Native.TaskbarWindow(), out rect)) return rect;
            return Native.ToRectI(Screen.PrimaryScreen.Bounds);
        }

        private RectI TrayRect()
        {
            RectI rect;
            if (Native.RectOf(Native.TrayNotifyWindow(), out rect)) return rect;
            return default(RectI);
        }

        // --------------------------------------------------------------------------------------
        // sizing
        // --------------------------------------------------------------------------------------

        private void SetClientSize(Size size)
        {
            if (ClientSize == size) return;
            ClientSize = size;
            if (Docked) PlaceDocked();
        }
    }
}
