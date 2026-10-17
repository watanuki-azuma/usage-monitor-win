// The taskbar chips bar (milestone M5). Owner-drawn, borderless, always on top, no taskbar
// button and no focus stealing (WS_EX_NOACTIVATE): one rounded pill per provider showing the
// brand badge, the provider initial and "NN/100".
//
// Port of reference/js/ui/chips.js + the chips half of reference/js/main.js:
//   * docked mode parks the bar immediately left of TrayNotifyWnd inside the taskbar rectangle
//   * dragging undocks it, dropping it back over the tray re-docks it
//   * click toggles the flyout, right-click opens the tray menu
//   * repaints only when the rendered chips actually change (ChipModel.Key)
//
// Language level: C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace UsageMonitor.Skins.Terminal
{
    internal sealed class ChipsForm : ChipsFormBase
    {
        // Layout, in design pixels at 96 DPI (shared.css .pct-icon / .chips).
        private const int BadgeSize = 14;
        private const int BadgeGap = 4;
        private const int ChipHeight = 22;
        private const int ChipPadX = 7;
        private const int ChipGap = 6;
        private const int BarPadX = 6;
        private const int BarPadY = 3;
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
            // Magenta is punched out of the layered window -> rounded corners and click-through.
            BackColor = Theme.FormBg;
            ClientSize = new Size(Theme.S(340), Theme.S(30));
        }

        // A tool window (no taskbar button) that never takes focus.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        // Rounded window shape: a Region instead of a colour key (see FlyoutForm.OnSizeChanged).
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            Region old = Region;
            using (System.Drawing.Drawing2D.GraphicsPath path = Theme.RoundPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Theme.Radius(6)))
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

        // Right-click anywhere on the bar opens the tray menu, like the Electron chips window.
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

        // The app has no main window: a user close of the chips bar (Alt+F4 / WM_CLOSE) would
        // dispose it and a later Show() would throw ObjectDisposedException. Cancel such a close
        // and just hide the bar; only a real shutdown (app exit, Windows logoff, task manager)
        // is allowed to close it for good (item 4).
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

        internal override void SetSnapshot(Snapshot snapshot)
        {
            List<ChipInfo> next = new List<ChipInfo>();
            if (snapshot != null && snapshot.Providers != null)
            {
                foreach (ProviderSnapshot provider in snapshot.Providers)
                {
                    if (provider == null) continue;
                    next.Add(ChipModel.For(provider));
                }
            }

            bool loadingNow = next.Count == 0;
            string key = ChipModel.Key(snapshot) + "|" + (loadingNow ? "1" : "0");
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
            int content = Theme.S(BadgeSize) + Theme.S(BadgeGap) + Theme.TextWidth(text, Theme.ChipFont);
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
            Theme.FillRound(g, Theme.BgBrush, bar, Theme.Radius(6));
            Theme.DrawRound(g, Theme.BorderLightPen, bar, Theme.Radius(6));
            Fx.DrawScanlines(g, bar);

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
            int pillWidth = PillWidthFor(text);
            Rectangle pill = new Rectangle(x, y, pillWidth, height);
            if (pill.Right > ClientSize.Width) return;

            // Terminal status cell: dark background, tinted if alerting or offline
            Brush pillBrush;
            if (colorClass == ChipModel.ClassRed) pillBrush = Theme.CardHeadAlertBrush;
            else if (stale) pillBrush = Theme.CardHeadStaleBrush;
            else if (colorClass == ChipModel.ClassGray) pillBrush = Theme.CardHeadOfflineBrush;
            else pillBrush = Theme.PillBrush;

            g.FillRectangle(pillBrush, pill);

            // Cell border: dashed when stale, solid otherwise
            Pen pen = stale ? Theme.ChipStalePen(colorClass) : Theme.ChipPen(colorClass);
            g.DrawRectangle(pen, pill.X, pill.Y, pill.Width - 1, pill.Height - 1);

            // Brand badge: square terminal key
            int badgeX = pill.X + Theme.S(ChipPadX);
            int badgeY = pill.Y + (pill.Height - Theme.S(BadgeSize)) / 2;
            Rectangle badge = new Rectangle(badgeX, badgeY, Theme.S(BadgeSize), Theme.S(BadgeSize));
            SolidBrush accent = Theme.AccentBrush(providerId);
            g.FillRectangle(accent, badge);
            g.DrawRectangle(Pens.Black, badge.X, badge.Y, badge.Width - 1, badge.Height - 1);
            Theme.DrawTextCenter(g, Theme.Initial(providerId), Theme.BadgeFont, badge, Theme.BgSolid);

            // Value text: bold monospace
            int textX = badge.Right + Theme.S(BadgeGap);
            Color textColor = stale ? Theme.Amber : Theme.ChipTextColor(colorClass);
            Theme.DrawTextAt(g, text, Theme.ChipFont,
                             new Point(textX, pill.Y + (pill.Height - TextHeight(Theme.ChipFont)) / 2),
                             textColor);
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
                // The taskbar rectangle spans the whole long axis, so the bar can stick out of
                // the work area: keep it on the monitor the taskbar lives on.
                place = TaskbarMath.Clamp(place, Native.ToRectI(Screen.PrimaryScreen.Bounds));
            }
            else
            {
                // No room left before the tray (a vertical taskbar, or a crowded one): park the
                // bar just outside the taskbar, still aligned with the tray.
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
                // Read the saved point first, then pick the monitor that shows it (nearest when
                // it sits on no screen, e.g. a monitor that was unplugged), then clamp.
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

            // Only rewrite the stored coordinates when they were genuinely off every monitor (or
            // on the first placement). A clamp that merely pulls an on-screen bar in must not
            // overwrite the user's saved anchor.
            if (app != null && (!stored || offScreen))
            {
                app.RememberChipsPosition(x, y);
            }
        }

        // The screen that contains the point, or the nearest one when it lies on no screen.
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

        // A drag that ends over the tray docks the bar again; anywhere else undocks it.
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
