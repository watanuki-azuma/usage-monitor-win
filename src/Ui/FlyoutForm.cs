// The flyout window (milestone M5): an owner-drawn, borderless, top-most tool window with a
// two-column grid of provider cards (name, plan, stale badge, one row per usage window with a
// meter bar, NN/100% and the reset ETA), plus the header controls (refresh, interval, dock, pin).
//
// Port of reference/js/ui/flyout.js + reference/js/ui/shared.css.
//
// Language level: C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace UsageMonitor
{
    internal sealed class FlyoutForm : FlyoutFormBase
    {
        // Layout, in design pixels at 96 DPI (shared.css .flyout / .provider / .row).
        private const int MinWidth = 640;

        private const int Pad = 12;
        private const int Border = 1;
        private const int HeaderHeight = 28;
        private const int HeaderGap = 8;
        private const int ColumnGap = 8;
        private const int ColumnMinHeight = 40;

        private const int CardPadX = 12;
        private const int CardPadTop = 8;
        private const int CardPadBottom = 9;
        private const int CardHeadHeight = 16;
        private const int CardHeadGap = 6;

        private const int RowHeight = 16;
        private const int RowGap = 6;
        private const int MeterHeight = 6;
        private const int LabelMax = 104;   // fits "Claude/GPT 5h"
        private const int PctWidth = 46;
        private const int EtaWidth = 46;
        private const int RowGapXMeter = 6;

        private const int BadgeSize = 13;
        private const int BadgeGap = 6;

        private const int ButtonHeight = 22;
        private const int ButtonPadX = 8;
        private const int ButtonGap = 6;

        // line kinds inside a card
        private const int LineRow = 0;
        private const int LineExtra = 1;
        private const int LineHint = 2;
        private const int LineNote = 3;
        private const int LineStale = 4;

        private const int ButtonRefresh = 1;
        private const int ButtonInterval = 2;
        private const int ButtonDock = 3;
        private const int ButtonPin = 4;

        private readonly AppController app;

        private List<CardModel> cards = new List<CardModel>();
        private List<ButtonModel> buttons = new List<ButtonModel>();
        private string renderKey = "";
        private DateTime snapshotNow = DateTime.UtcNow;
        private Size contentSize = new Size(Theme.S(MinWidth), 100);
        private int hoverButton;
        private int pressedButton;
        private bool ignoreDeactivate;
        private System.Windows.Forms.Timer deactivateTimer;
        private ContextMenuStrip intervalMenu;

        private bool dragging;
        private bool dragMoved;
        private Point dragOrigin;
        private Point dragStartCursor;

        internal FlyoutForm(AppController controller)
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
            Text = "UsageMonitorFlyout";
            BackColor = Theme.FormBg;
            ClientSize = new Size(Theme.S(MinWidth), 200);
        }

        // A tool window like the chips bar, but this one is allowed to take focus.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetTopMost(Handle, true);
        }

        // Like the chips bar, closing the flyout with Alt+F4 must not dispose it (a later
        // ShowFlyout would throw ObjectDisposedException). Cancel a user close and just hide it;
        // only a real shutdown (app exit, Windows logoff, task manager) closes it for good
        // (item 4).
        // Rounded window shape: a Region instead of a colour key, so no key colour can bleed into the
        // anti-aliased border.
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            Region old = Region;
            using (System.Drawing.Drawing2D.GraphicsPath path = Theme.RoundPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Theme.Radius(12)))
            {
                Region = new Region(path);
            }
            if (old != null) old.Dispose();
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

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintInto(e.Graphics, ClientSize);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                if (app != null) app.HideFlyout(true);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Point cursor = Native.CursorPosition();
                if (!dragMoved)
                {
                    int dx = cursor.X - dragStartCursor.X;
                    int dy = cursor.Y - dragStartCursor.Y;
                    if (dx < 0) dx = -dx;
                    if (dy < 0) dy = -dy;
                    if (dx < Theme.S(3) && dy < Theme.S(3)) return;
                    dragMoved = true;
                    Capture = true;
                }
                // Move only; the position is persisted once, on mouse up, never while dragging.
                Location = new Point(dragOrigin.X + cursor.X - dragStartCursor.X,
                                     dragOrigin.Y + cursor.Y - dragStartCursor.Y);
                return;
            }

            int hit = ButtonAt(e.Location);
            if (hit != hoverButton)
            {
                hoverButton = hit;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverButton != 0)
            {
                hoverButton = 0;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressedButton = ButtonAt(e.Location);
            if (pressedButton != 0)
            {
                Invalidate();
                return;
            }
            // Header / background drags the window (drag.js skips buttons and .provider cards).
            // Disabled while docked.
            if (!Docked && CardIndexAt(e.Location) < 0)
            {
                dragStartCursor = Native.CursorPosition();
                dragOrigin = Location;
                dragMoved = false;
                dragging = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (dragging)
            {
                bool moved = dragMoved;
                dragging = false;
                if (Capture) Capture = false;
                Invalidate();
                // Persist the drop position once, never while moving; only when not docked.
                if (moved && !Docked) PersistFlyoutPosition();
                return;
            }

            int hit = ButtonAt(e.Location);
            pressedButton = 0;
            Invalidate();

            if (hit == ButtonRefresh) PressRefresh();
            else if (hit == ButtonInterval) PressInterval();
            else if (hit == ButtonDock) PressDock();
            else if (hit == ButtonPin) PressPin();
            else PressCard(e.Location);
        }

        private void PersistFlyoutPosition()
        {
            if (app == null) return;
            app.RememberFlyoutPosition(Location.X, Location.Y);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Right) return;
            if (app != null) app.ShowTrayMenu();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // The interval menu takes the focus for a moment; that must not close the flyout.
            if (ignoreDeactivate) return;
            // SPEC 5.5 / main.js blur: only an unpinned, undocked flyout closes on losing focus.
            if (Docked || Pinned) return;
            if (deactivateTimer == null)
            {
                deactivateTimer = new System.Windows.Forms.Timer();
                deactivateTimer.Interval = 180;
                deactivateTimer.Tick += OnDeactivateTick;
            }
            deactivateTimer.Stop();
            deactivateTimer.Start();
        }

        private void OnDeactivateTick(object sender, EventArgs e)
        {
            if (deactivateTimer != null) deactivateTimer.Stop();
            if (ignoreDeactivate || app == null) return;
            if (Docked || Pinned) return;
            if (Focused || ContainsFocus) return;
            app.HideFlyout(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (deactivateTimer != null)
                {
                    deactivateTimer.Stop();
                    deactivateTimer.Dispose();
                    deactivateTimer = null;
                }
                if (intervalMenu != null)
                {
                    intervalMenu.Dispose();
                    intervalMenu = null;
                }
            }
            base.Dispose(disposing);
        }

        // --------------------------------------------------------------------------------------
        // button and card handling
        // --------------------------------------------------------------------------------------

        private bool Docked
        {
            get { return app == null ? false : app.FlyoutDocked; }
        }

        private bool Pinned
        {
            get { return app == null ? false : app.FlyoutPinned; }
        }

        private int IntervalSecs
        {
            get { return app == null ? 5 : app.PollIntervalSecs; }
        }

        private TimeZoneInfo Zone
        {
            get { return TimeZoneInfo.Local; }
        }

        private void PressRefresh()
        {
            if (app != null) app.RefreshNow();
        }

        private void PressDock()
        {
            if (app != null) app.ToggleFlyoutDock();
            Invalidate();
        }

        private void PressPin()
        {
            if (app != null) app.ToggleFlyoutPin();
            Invalidate();
        }

        private void PressInterval()
        {
            if (app == null) return;

            ContextMenuStrip menu = EnsureIntervalMenu();

            // The menu is built once; only the check marks follow the current interval.
            for (int i = 0; i < menu.Items.Count; i++)
            {
                ToolStripMenuItem item = menu.Items[i] as ToolStripMenuItem;
                if (item == null) continue;
                item.Checked = (item.Tag is int) && (int)item.Tag == IntervalSecs;
            }

            ignoreDeactivate = true;

            ButtonModel button = FindButton(ButtonInterval);
            Point origin;
            if (button != null)
            {
                origin = PointToScreen(new Point(button.Rect.X, button.Rect.Bottom + Theme.S(2)));
            }
            else
            {
                origin = Cursor.Position;
            }
            menu.Show(origin);
        }

        // Built once and reused: a fresh ContextMenuStrip per click (never disposed) leaked menus.
        private ContextMenuStrip EnsureIntervalMenu()
        {
            if (intervalMenu != null) return intervalMenu;

            ContextMenuStrip menu = new ContextMenuStrip();
            for (int i = 0; i < AppConfig.AllowedIntervals.Length; i++)
            {
                int secs = AppConfig.AllowedIntervals[i];
                int captured = secs;
                ToolStripMenuItem item = new ToolStripMenuItem("Every " + secs + " seconds");
                item.Tag = secs;
                item.Click += delegate(object sender, EventArgs e)
                {
                    if (app != null) app.SetInterval(captured);
                };
                menu.Items.Add(item);
            }
            menu.Closed += delegate(object sender, ToolStripDropDownClosedEventArgs e)
            {
                ignoreDeactivate = false;
            };
            intervalMenu = menu;
            return menu;
        }

        private void PressCard(Point location)
        {
            int column = CardIndexAt(location);
            if (column < 0 || column >= cards.Count) return;
            string id = cards[column].Provider == null ? null : cards[column].Provider.Id;
            if (id == null) return;
            if (app != null) app.OpenUsage(id);
        }

        private ButtonModel FindButton(int id)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i] != null && buttons[i].Id == id) return buttons[i];
            }
            return null;
        }

        private int ButtonAt(Point location)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                ButtonModel button = buttons[i];
                if (button == null) continue;
                if (button.Rect.Contains(location)) return button.Id;
            }
            return 0;
        }

        // --------------------------------------------------------------------------------------
        // model
        // --------------------------------------------------------------------------------------

        private sealed class LineModel
        {
            public int Kind;
            public string Label;
            public string Pct;
            public string Eta;
            public double Fill;
            public bool Alert;
            public string Text;

            public int Height
            {
                get
                {
                    switch (Kind)
                    {
                        case LineRow: return Theme.S(RowHeight);
                        default: return Theme.S(14);
                    }
                }
            }
        }

        private sealed class CardModel
        {
            public ProviderSnapshot Provider;
            public string Name = "";
            public string Plan = "";
            public string Stale = "";
            public string Hint = "";
            public List<LineModel> Lines = new List<LineModel>();
            public string Tooltip = "";

            public int LinesHeight()
            {
                int total = 0;
                for (int i = 0; i < Lines.Count; i++)
                {
                    if (i > 0) total += Theme.S(RowGap);
                    total += Lines[i].Height;
                }
                return total;
            }

            public int Height()
            {
                int total = Theme.S(CardPadTop) + Theme.S(CardHeadHeight) + Theme.S(CardHeadGap);
                total += LinesHeight();
                return total + Theme.S(CardPadBottom);
            }
        }

        private sealed class ButtonModel
        {
            public int Id;
            public string Label;
            public bool Active;
            public Rectangle Rect;

            public ButtonModel(int id, string label, bool active)
            {
                Id = id;
                Label = label;
                Active = active;
            }
        }

        internal override void SetSnapshot(Snapshot snapshot)
        {
            snapshotNow = DateTime.UtcNow;
            snapshotOf = snapshot;
            cards = BuildCards(snapshot, snapshotNow);
            string key = RenderKey(cards);
            if (key == renderKey) return;
            renderKey = key;

            ApplyLayout();
            Invalidate();
        }

        // The 1-second tick: recompute the ETA text and repaint only when it changed.
        internal override void UpdateEtas(DateTime nowUtc)
        {
            if (cards.Count == 0) return;
            List<CardModel> model = BuildCards(snapshotOf, nowUtc);
            string key = RenderKey(model);
            if (key == renderKey) return;
            renderKey = key;
            cards = model;
            Invalidate();
        }

        private Snapshot snapshotOf;

        private List<CardModel> BuildCards(Snapshot snapshot, DateTime nowUtc)
        {
            List<CardModel> result = new List<CardModel>();
            if (snapshot == null || snapshot.Providers == null) return result;

            foreach (ProviderSnapshot provider in snapshot.Providers)
            {
                if (provider == null) continue;
                CardModel card = new CardModel();
                card.Provider = provider;
                card.Name = provider.DisplayName == null ? (provider.Id == null ? "" : provider.Id) : provider.DisplayName;
                card.Plan = provider.Plan == null ? "" : provider.Plan;

                ProviderStatus status = provider.Status;
                if (status != null && status.State == State.Stale)
                    card.Stale = Format.StaleText(provider, nowUtc, Zone);

                if (card.Stale.Length > 0)
                {
                    LineModel staleLine = new LineModel();
                    staleLine.Kind = LineStale;
                    staleLine.Text = card.Stale;
                    card.Lines.Add(staleLine);
                }

                string hint = Format.StatusText(provider);

                if (provider.Windows != null)
                {
                    foreach (UsageWindow window in provider.Windows)
                    {
                        if (window == null) continue;
                        if (window.Kind == Kind.Credits && !window.UsedPct.HasValue)
                        {
                            LineModel extra = new LineModel();
                            extra.Kind = LineExtra;
                            extra.Text = window.Label == null ? "Credits" : window.Label;
                            card.Lines.Add(extra);
                            continue;
                        }

                        LineModel line = new LineModel();
                        line.Kind = LineRow;
                        line.Label = Format.FlyoutLabel(window) == null ? "" : Format.FlyoutLabel(window);
                        line.Pct = Format.FormatUsedTotal(window, false);
                        line.Eta = Format.FormatEta(window.ResetsAt, nowUtc, Zone);
                        line.Alert = Models.IsAlerting(window);
                        line.Fill = window.UsedPct.HasValue ? window.UsedPct.Value : 0d;
                        if (line.Fill < 0) line.Fill = 0;
                        if (line.Fill > 100) line.Fill = 100;
                        card.Lines.Add(line);
                    }

                    if (provider.Footnotes != null)
                    {
                        foreach (string note in provider.Footnotes)
                        {
                            if (string.IsNullOrEmpty(note)) continue;
                            LineModel line = new LineModel();
                            line.Kind = LineNote;
                            line.Text = note;
                            card.Lines.Add(line);
                        }
                    }
                }

                if (hint != null && card.Lines.Count == 0)
                {
                    card.Hint = hint;
                    LineModel hintLine = new LineModel();
                    hintLine.Kind = LineHint;
                    hintLine.Text = hint;
                    card.Lines.Add(hintLine);
                }
                else if (hint != null)
                {
                    card.Hint = hint;
                }

                result.Add(card);
            }
            return result;
        }

        private string RenderKey(List<CardModel> model)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(Docked ? "D" : "d");
            sb.Append(Pinned ? "P" : "p");
            sb.Append(IntervalSecs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('|');
            for (int i = 0; i < model.Count; i++)
            {
                CardModel card = model[i];
                if (i > 0) sb.Append(';');
                sb.Append(card.Name);
                sb.Append('/').Append(card.Plan);
                sb.Append('/').Append(card.Stale);
                sb.Append('/').Append(card.Hint);
                for (int j = 0; j < card.Lines.Count; j++)
                {
                    LineModel line = card.Lines[j];
                    sb.Append('|').Append(line.Kind);
                    sb.Append(':').Append(line.Label);
                    sb.Append(':').Append(line.Pct);
                    sb.Append(':').Append(line.Eta);
                    sb.Append(':').Append(line.Text == null ? "" : line.Text);
                    sb.Append(':').Append(line.Fill.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(':').Append(line.Alert ? "1" : "0");
                }
            }
            return sb.ToString();
        }

        // --------------------------------------------------------------------------------------
        // layout
        // --------------------------------------------------------------------------------------

        internal override Size MeasureSize()
        {
            return ComputeLayout(0).Size;
        }

        private LayoutInfo ComputeLayout(int reserved)
        {
            LayoutInfo info = new LayoutInfo();
            int innerWidth = Theme.S(MinWidth) - Theme.S(Pad) * 2 - Theme.S(Border) * 2;
            int columnWidth = (innerWidth - Theme.S(ColumnGap)) / 2;

            int rows = (cards.Count + 1) / 2;
            if (rows < 1) rows = 1;

            int bodyHeight = 0;
            List<int> rowHeights = new List<int>();
            for (int row = 0; row < rows; row++)
            {
                int tallest = Theme.S(ColumnMinHeight);
                for (int column = 0; column < 2; column++)
                {
                    int index = row * 2 + column;
                    if (index >= cards.Count) continue;
                    int height = cards[index].Height();
                    if (height > tallest) tallest = height;
                }
                rowHeights.Add(tallest);
                bodyHeight += tallest;
            }
            bodyHeight += (rows - 1) * Theme.S(ColumnGap);

            int totalHeight = Theme.S(Pad) + Theme.S(HeaderHeight) + Theme.S(HeaderGap) + bodyHeight + Theme.S(Pad);
            if (totalHeight < 120) totalHeight = 120;
            info.Size = new Size(Theme.S(MinWidth), totalHeight);
            info.ColumnWidth = columnWidth;
            info.RowHeights = rowHeights;

            int left = Theme.S(Pad) + Theme.S(Border);
            int top = Theme.S(Pad) + Theme.S(Border);

            // card rectangles, below the header row
            int y = top + Theme.S(HeaderHeight) + Theme.S(HeaderGap);
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < 2; column++)
                {
                    int index = row * 2 + column;
                    if (index >= cards.Count) continue;
                    int x = left + column * (columnWidth + Theme.S(ColumnGap));
                    info.CardRects.Add(new Rectangle(x, y, columnWidth, rowHeights[row]));
                }
                y += rowHeights[row] + Theme.S(ColumnGap);
            }

            // header buttons, right aligned: pin, dock, every Ns, refresh
            buttons = BuildButtons(info.Size.Width, top);

            return info;
        }

        private List<ButtonModel> BuildButtons(int width, int top)
        {
            List<ButtonModel> list = new List<ButtonModel>();
            int right = width - Theme.S(Pad) - Theme.S(Border);

            int x = right;
            AddButton(list, ButtonRefresh, "Refresh", false, ref x, top);
            AddButton(list, ButtonInterval, "Every " + IntervalSecs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s", false, ref x, top);
            AddButton(list, ButtonDock, "Dock", Docked, ref x, top);
            AddButton(list, ButtonPin, "Pin", Pinned, ref x, top);

            return list;
        }

        private void AddButton(List<ButtonModel> list, int id, string label, bool active, ref int x, int originY)
        {
            int width = Theme.TextWidth(label, Theme.ButtonFont) + Theme.S(ButtonPadX) * 2;
            int height = Theme.S(ButtonHeight);
            int left = x - width;
            ButtonModel button = new ButtonModel(id, label, active);
            button.Rect = new Rectangle(left, originY + (Theme.S(HeaderHeight) - height) / 2, width, height);
            list.Add(button);
            x = left - Theme.S(ButtonGap);
        }

        private class LayoutInfo
        {
            public Size Size;
            public int ColumnWidth;
            public List<int> RowHeights = new List<int>();
            public List<Rectangle> CardRects = new List<Rectangle>();

            public Rectangle CardAt(int index)
            {
                if (index < 0 || index >= CardRects.Count) return Rectangle.Empty;
                return CardRects[index];
            }
        }

        private int CardIndexAt(Point location)
        {
            LayoutInfo info = ComputeLayout(0);
            for (int i = 0; i < info.CardRects.Count; i++)
            {
                if (info.CardRects[i].Contains(location)) return i;
            }
            return -1;
        }

        // Recompute the client size (and re-place) after the content changed.
        internal void ApplyLayout()
        {
            LayoutInfo info = ComputeLayout(0);
            Size size = info.Size;
            contentSize = size;
            if (ClientSize == size) return;
            ClientSize = size;
            RepositionAfterResize();
        }

        // Keep the window anchored after a size change: a docked flyout re-runs the docked
        // placement (anchored above the taskbar via TaskbarMath.FlyoutAnchor); a floating one
        // keeps its top-left but is pulled back inside the working area of its screen.
        private void RepositionAfterResize()
        {
            if (app == null) return;
            if (Docked)
            {
                Place();
                return;
            }

            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            int x = Location.X;
            int y = Location.Y;
            if (x + ClientSize.Width > work.Right) x = work.Right - ClientSize.Width;
            if (y + ClientSize.Height > work.Bottom) y = work.Bottom - ClientSize.Height;
            if (x < work.X) x = work.X;
            if (y < work.Y) y = work.Y;
            if (x != Location.X || y != Location.Y)
            {
                Native.MoveTo(Handle, x, y, ClientSize.Width, ClientSize.Height);
            }
        }

        internal override void Place()
        {
            if (app == null) return;

            if (Docked)
            {
                RectI chips = app.ChipsWindowRect();
                // Anchor against the working area of the screen that actually holds the chips
                // (the taskbar screen when docked), not the primary one.
                Screen screen = Screen.FromPoint(new Point(chips.X + chips.W / 2, chips.Y + chips.H / 2));
                RectI work = Native.ToRectI(screen.WorkingArea);
                RectI place = TaskbarMath.FlyoutAnchor(chips, work, ClientSize.Width, ClientSize.Height, Theme.S(8));
                Native.MoveTo(Handle, place.X, place.Y, place.W, place.H);
                return;
            }

            if (Pinned && app.HasStoredFlyoutPosition())
            {
                Point stored = app.StoredFlyoutPosition();
                Screen screen = Screen.FromPoint(stored);
                Rectangle work = screen.WorkingArea;
                int x = stored.X;
                int y = stored.Y;
                if (x + ClientSize.Width > work.Right) x = work.Right - ClientSize.Width;
                if (y + ClientSize.Height > work.Bottom) y = work.Bottom - ClientSize.Height;
                if (x < work.X) x = work.X;
                if (y < work.Y) y = work.Y;
                Native.MoveTo(Handle, x, y, ClientSize.Width, ClientSize.Height);
                return;
            }

            RectI chipsRect = app.ChipsWindowRect();
            Screen target = Screen.FromPoint(new Point(chipsRect.X, chipsRect.Y));
            Rectangle area = target.WorkingArea;
            int placeX = chipsRect.X + (chipsRect.W - ClientSize.Width) / 2;
            int placeY = chipsRect.Y - ClientSize.Height - Theme.S(8);
            if (placeX + ClientSize.Width > area.Right) placeX = area.Right - ClientSize.Width;
            if (placeX < area.Left) placeX = area.Left;
            if (placeY < area.Top) placeY = chipsRect.Y + chipsRect.H + Theme.S(8);
            if (placeY + ClientSize.Height > area.Bottom) placeY = area.Bottom - ClientSize.Height;
            Native.MoveTo(Handle, placeX, placeY, ClientSize.Width, ClientSize.Height);
        }

        // --------------------------------------------------------------------------------------
        // painting
        // --------------------------------------------------------------------------------------

        // Width of the reset column: wide enough for the longest text on screen ("Sat 12:57 PM"),
        // the same for every row so the meters stay aligned.
        private int etaColumn = Theme.S(EtaWidth);

        private int EtaColumnWidth()
        {
            int widest = Theme.S(EtaWidth);
            foreach (CardModel card in cards)
            {
                if (card == null || card.Lines == null) continue;
                foreach (LineModel line in card.Lines)
                {
                    if (line == null || line.Kind != LineRow || string.IsNullOrEmpty(line.Eta)) continue;
                    int w = Theme.TextWidth(line.Eta, Theme.RowFont) + Theme.S(2);
                    if (w > widest) widest = w;
                }
            }
            return widest;
        }

        internal override void PaintInto(Graphics g, Size size)
        {
            LayoutInfo info = ComputeLayout(0);
            contentSize = info.Size;

            int width = size.Width;
            int height = size.Height;
            g.Clear(Theme.FormBg);

            Rectangle body = new Rectangle(0, 0, width - 1, height - 1);
            Theme.FillRound(g, Theme.BgBrush, body, Theme.Radius(12));
            Theme.DrawRound(g, Theme.BorderLightPen, body, Theme.Radius(12));
            if (Docked) g.DrawLine(Theme.DockedPen, body.Left + Theme.S(6), body.Bottom, body.Right - Theme.S(6), body.Bottom);

            PaintHeader(g, width);
            etaColumn = EtaColumnWidth();
            for (int i = 0; i < cards.Count; i++)
            {
                PaintCard(g, info.CardAt(i), cards[i]);
            }

            foreach (ButtonModel button in buttons)
            {
                PaintButton(g, button);
            }
        }

        private void PaintHeader(Graphics g, int width)
        {
            Theme.DrawTextAt(g, "Usage Monitor", Theme.HeadFont,
                             new Point(Theme.S(Pad) + Theme.S(Border), Theme.S(Pad) + Theme.S(HeaderHeight) / 2 - TextHeight(Theme.HeadFont) / 2),
                             Color.FromArgb(229, 231, 235));
        }

        private void PaintButton(Graphics g, ButtonModel button)
        {
            if (button == null || button.Rect.Width <= 0) return;

            Color fill = button.Active ? Theme.ActiveFill : (hoverButton == button.Id ? Theme.HoverFill : (pressedButton == button.Id ? Theme.PressFill : Color.FromArgb(0, 255, 255, 255)));
            if (button.Active) Theme.FillRound(g, Theme.ActiveBrush, button.Rect, Theme.Radius(6));
            else if (hoverButton == button.Id) Theme.FillRound(g, Theme.HoverBrush, button.Rect, Theme.Radius(6));
            else if (pressedButton == button.Id) Theme.FillRound(g, Theme.PressBrush, button.Rect, Theme.Radius(6));
            else Theme.FillRound(g, Theme.IdleBrush, button.Rect, Theme.Radius(6));

            Color textColor = button.Active ? Color.FromArgb(251, 191, 36) : Theme.Muted;
            Theme.DrawTextCenter(g, button.Label, Theme.ButtonFont, button.Rect, textColor);
        }

        private void PaintCard(Graphics g, Rectangle card, CardModel model)
        {
            if (card.Width <= 0 || card.Height <= 0) return;
            string id = model.Provider == null ? null : model.Provider.Id;

            Theme.FillRound(g, Theme.CardBrush, card, Theme.Radius(10));
            Theme.DrawRound(g, Theme.CardBorderPen, card, Theme.Radius(10));

            // left accent bar (shared.css .accent-* inset shadow)
            SolidBrush accent = Theme.AccentBrush(id);
            Rectangle bar = new Rectangle(card.X, card.Y + Theme.S(4), Theme.S(2), card.Height - Theme.S(8));
            g.FillRectangle(accent, bar);

            int contentX = card.X + Theme.S(CardPadX);
            int contentWidth = card.Width - Theme.S(CardPadX) * 2;

            // head: badge + name, plan on the right
            int headY = card.Y + Theme.S(CardPadTop);
            Rectangle badge = new Rectangle(contentX, headY + (Theme.S(CardHeadHeight) - Theme.S(BadgeSize)) / 2,
                                            Theme.S(BadgeSize), Theme.S(BadgeSize));
            Theme.FillRound(g, Theme.AccentBrush(id), badge, Theme.Radius(4));
            Theme.DrawRound(g, Theme.BadgePen, badge, Theme.Radius(4));
            Theme.DrawTextCenter(g, Theme.Initial(id), Theme.BadgeFont, badge, Theme.BgSolid);

            int nameX = badge.Right + Theme.S(BadgeGap);
            string name = Theme.Fit(model.Name, Theme.NameFont, Math.Max(Theme.S(50), contentWidth - (nameX - contentX)));
            Theme.DrawTextAt(g, name, Theme.NameFont,
                             new Point(nameX, headY + (Theme.S(CardHeadHeight) - TextHeight(Theme.NameFont)) / 2),
                             Theme.Text);

            int planRight = card.Right - Theme.S(CardPadX);
            int planLeft = nameX + Math.Min(Theme.TextWidth(name, Theme.NameFont), contentWidth) + Theme.S(8);
            if (planLeft < planRight)
            {
                string plan = Theme.Fit(model.Plan, Theme.PlanFont, planRight - planLeft);
                Theme.DrawTextRight(g, plan, Theme.PlanFont,
                                    new Rectangle(planLeft, headY, planRight - planLeft, Theme.S(CardHeadHeight)),
                                    Theme.Muted);
            }

            int y = headY + Theme.S(CardHeadHeight) + Theme.S(CardHeadGap);

            // The label column is as wide as this card's longest label (so short "5h"/"Wk" cards
            // keep long meters), capped at LabelMax.
            int cardLabelWidth = Theme.S(28);
            foreach (LineModel l in model.Lines)
            {
                if (l == null || l.Kind != LineRow || string.IsNullOrEmpty(l.Label)) continue;
                int lw = Theme.TextWidth(l.Label, Theme.RowLabelFont) + Theme.S(2);
                if (lw > cardLabelWidth) cardLabelWidth = lw;
            }

            for (int i = 0; i < model.Lines.Count; i++)
            {
                LineModel line = model.Lines[i];
                int lineHeight = line.Height;
                int gap = i > 0 ? Theme.S(RowGap) : 0;
                y += gap;

                if (line.Kind == LineRow)
                {
                    int labelWidth = Math.Min(Theme.S(LabelMax), contentWidth - Theme.S(PctWidth) - etaColumn - Theme.S(48));
                    labelWidth = Math.Min(labelWidth, cardLabelWidth);
                    if (labelWidth < Theme.S(28)) labelWidth = Math.Max(Theme.S(28), contentWidth / 4);

                    string label = Theme.Fit(line.Label, Theme.RowLabelFont, labelWidth);
                    Color labelColor = line.Alert ? Color.FromArgb(254, 202, 202) : Theme.RowText;
                    Theme.DrawTextAt(g, label, Theme.RowLabelFont, new Point(contentX, y), labelColor);

                    int afterLabel = contentX + labelWidth + Theme.S(RowGapXMeter);
                    int pctWidth = Math.Min(Theme.S(PctWidth), Theme.TextWidth(line.Pct, Theme.RowPctFont));
                    int etaRight = contentX + contentWidth;
                    int etaLeft = etaRight - etaColumn;
                    int pctLeft = etaLeft - Theme.S(RowGapXMeter) - pctWidth;
                    int meterX = afterLabel;
                    int meterWidth = pctLeft - Theme.S(RowGapXMeter) - meterX;
                    if (meterWidth < Theme.S(12))
                    {
                        meterWidth = Theme.S(12);
                        pctLeft = meterX + meterWidth + Theme.S(RowGapXMeter);
                        etaLeft = pctLeft + pctWidth + Theme.S(RowGapXMeter);
                        etaRight = contentX + contentWidth;
                    }

                    int meterY = y + (lineHeight - Theme.S(MeterHeight)) / 2;
                    Rectangle track = new Rectangle(meterX, meterY, meterWidth, Theme.S(MeterHeight));
                    Theme.FillRound(g, Theme.BarTrackBrush, track, Theme.Radius(MeterHeight / 2f));
                    int fillWidth = (int)Math.Round(meterWidth * (line.Fill / 100.0), MidpointRounding.AwayFromZero);
                    if (fillWidth < 0) fillWidth = 0;
                    if (fillWidth > meterWidth) fillWidth = meterWidth;
                    if (fillWidth > 0)
                    {
                        Rectangle fill = new Rectangle(track.X, track.Y, fillWidth, track.Height);
                        Theme.FillRound(g, line.Alert ? Theme.RedBrush : Theme.GreenBrush, fill, Theme.Radius(MeterHeight / 2f));
                    }

                    Color pctColor = line.Alert ? Color.FromArgb(254, 202, 202) : Theme.Text;
                    Theme.DrawTextRight(g, line.Pct, Theme.RowPctFont,
                                        new Rectangle(pctLeft, y, pctWidth + Theme.S(2), lineHeight), pctColor);
                    Theme.DrawTextRight(g, line.Eta, Theme.RowFont,
                                        new Rectangle(etaLeft, y, etaRight - etaLeft, lineHeight), Theme.Muted);
                }
                else if (line.Kind == LineStale)
                {
                    string text = Theme.Fit(line.Text, Theme.Badge2Font, contentWidth);
                    Theme.DrawTextRight(g, text, Theme.Badge2Font,
                                        new Rectangle(contentX, y, contentWidth, lineHeight), Theme.Amber);
                }
                else if (line.Kind == LineExtra)
                {
                    string text = Theme.Fit(line.Text, Theme.RowFont, contentWidth);
                    Theme.DrawTextAt(g, text, Theme.RowFont, new Point(contentX, y), Color.FromArgb(229, 231, 235));
                }
                else if (line.Kind == LineNote)
                {
                    string text = Theme.Fit(line.Text, Theme.RowFont, contentWidth);
                    Theme.DrawTextAt(g, text, Theme.RowFont, new Point(contentX, y), Theme.Dim);
                }
                else if (line.Kind == LineHint)
                {
                    string text = Theme.Fit(line.Text, Theme.RowFont, contentWidth);
                    Theme.DrawTextAt(g, text, Theme.RowFont, new Point(contentX, y), Theme.Muted);
                }

                y += lineHeight;
            }
        }

        private int TextHeight(Font font)
        {
            return Theme.MeasureText("Ag", font).Height;
        }
    }
}
