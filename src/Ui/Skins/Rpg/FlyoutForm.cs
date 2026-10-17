// The flyout window (milestone M5): an owner-drawn, borderless, top-most tool window with a
// two-column grid of provider cards (name, plan, stale badge, one row per usage window with a
// meter bar, NN/100% and the reset ETA), plus the header controls (refresh, interval, dock, pin).
//
// Restyled as a classic JRPG Status Screen with dark blue gradient window boxes, double white borders,
// HP/MP bars (green -> yellow -> red), LV plan text, hand cursor glyph on first card, and KO for logged out.
//
// Language level: C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UsageMonitor.Skins.Rpg
{
    internal sealed class FlyoutForm : FlyoutFormBase
    {
        // Layout, in design pixels at 96 DPI.
        private const int MinWidth = 710;

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
        private const int MeterHeight = 8;
        private const int LabelMax = 104;
        private const int PctWidth = 46;
        private const int EtaWidth = 46;
        private const int RowGapXMeter = 6;

        private const int BadgeSize = 13;
        private const int BadgeGap = 5;

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

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            Region old = Region;
            using (GraphicsPath path = Theme.RoundPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Theme.Radius(12)))
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
            if (ignoreDeactivate) return;
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

            // RPG Status Screen fields
            public double Remaining;
            public string RemainingText;
            public string StatTag; // "HP", "MP", "SP"

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
            public bool IsLoggedOut;
            public bool IsNotInstalled;

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
                card.IsLoggedOut = status != null && status.State == State.LoggedOut;
                card.IsNotInstalled = status != null && status.State == State.NotInstalled;

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
                    int windowIndex = 0;
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

                        // RPG Status Screen: show remaining quota as HP/MP/SP
                        double rem = window.RemainingPct.HasValue ? window.RemainingPct.Value : (100.0 - line.Fill);
                        if (rem < 0) rem = 0;
                        if (rem > 100) rem = 100;
                        line.Remaining = rem;
                        int remInt = (int)Math.Round(rem, MidpointRounding.AwayFromZero);
                        line.RemainingText = remInt.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(3) + "/100";

                        if (windowIndex == 0) line.StatTag = "HP";
                        else if (windowIndex == 1) line.StatTag = "MP";
                        else line.StatTag = "SP";

                        card.Lines.Add(line);
                        windowIndex++;
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
                    sb.Append(':').Append(line.RemainingText);
                    sb.Append(':').Append(line.StatTag);
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

        internal void ApplyLayout()
        {
            LayoutInfo info = ComputeLayout(0);
            Size size = info.Size;
            contentSize = size;
            if (ClientSize == size) return;
            ClientSize = size;
            RepositionAfterResize();
        }

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

            // JRPG outer window frame with deep blue gradient and double border
            Rectangle body = new Rectangle(0, 0, width - 1, height - 1);
            using (LinearGradientBrush bgBrush = new LinearGradientBrush(body, Theme.WindowTop, Theme.WindowBottom, LinearGradientMode.Vertical))
            {
                Theme.FillRound(g, bgBrush, body, Theme.Radius(12));
            }
            Theme.DrawRound(g, Theme.BorderWhitePen, body, Theme.Radius(12));
            int inset = Theme.S(3);
            if (inset < 3) inset = 3;
            Rectangle innerBody = new Rectangle(body.X + inset, body.Y + inset, body.Width - inset * 2, body.Height - inset * 2);
            if (innerBody.Width > 0 && innerBody.Height > 0)
            {
                Theme.DrawRound(g, Theme.BorderInnerPen, innerBody, Theme.Radius(9));
            }
            if (Docked) g.DrawLine(Theme.DockedPen, body.Left + Theme.S(6), body.Bottom, body.Right - Theme.S(6), body.Bottom);

            PaintHeader(g, width);
            etaColumn = EtaColumnWidth();
            for (int i = 0; i < cards.Count; i++)
            {
                PaintCard(g, info.CardAt(i), cards[i], i);
            }

            foreach (ButtonModel button in buttons)
            {
                PaintButton(g, button);
            }
        }

        private void PaintHeader(Graphics g, int width)
        {
            Point titlePt = new Point(Theme.S(Pad) + Theme.S(Border),
                                      Theme.S(Pad) + Theme.S(HeaderHeight) / 2 - TextHeight(Theme.HeadFont) / 2);
            Fx.DrawTextWithShadow(g, "⚔ PARTY STATUS · USAGE MONITOR ⚔", Theme.HeadFont, titlePt, Theme.Gold, Color.FromArgb(0, 0, 0));
        }

        private void PaintButton(Graphics g, ButtonModel button)
        {
            if (button == null || button.Rect.Width <= 0) return;

            Rectangle r = button.Rect;
            Pen borderPen;
            Pen innerBorderPen;
            Color topFill;
            Color botFill;
            Color textCol;

            if (button.Active)
            {
                borderPen = Theme.GoldPen;
                innerBorderPen = Theme.ButtonInnerActivePen;
                topFill = Color.FromArgb(72, 56, 18);
                botFill = Color.FromArgb(36, 28, 8);
                textCol = Theme.Gold;
            }
            else if (pressedButton == button.Id)
            {
                borderPen = Theme.BorderWhitePen;
                innerBorderPen = Theme.ButtonInnerHoverPen;
                topFill = Color.FromArgb(48, 72, 160);
                botFill = Color.FromArgb(24, 36, 90);
                textCol = Color.White;
            }
            else if (hoverButton == button.Id)
            {
                borderPen = Theme.BorderWhitePen;
                innerBorderPen = Theme.ButtonInnerHoverPen;
                topFill = Color.FromArgb(32, 60, 140);
                botFill = Color.FromArgb(16, 30, 75);
                textCol = Color.White;
            }
            else
            {
                borderPen = Theme.BorderWhitePen;
                innerBorderPen = Theme.ButtonInnerIdlePen;
                topFill = Color.FromArgb(20, 38, 96);
                botFill = Color.FromArgb(8, 16, 48);
                textCol = Color.FromArgb(240, 245, 255);
            }

            using (LinearGradientBrush brush = new LinearGradientBrush(r, topFill, botFill, LinearGradientMode.Vertical))
            {
                Theme.FillRound(g, brush, r, Theme.Radius(4));
            }
            Theme.DrawRound(g, borderPen, r, Theme.Radius(4));
            if (r.Width > 6 && r.Height > 6)
            {
                Rectangle inner = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                Theme.DrawRound(g, innerBorderPen, inner, Theme.Radius(2));
            }
            if (r.Height > 4)
            {
                g.DrawLine(Theme.ShinePen40, r.X + 2, r.Y + 1, r.Right - 3, r.Y + 1);
            }

            Theme.DrawTextCenter(g, button.Label, Theme.ButtonFont, new Rectangle(r.X + 1, r.Y + 1, r.Width, r.Height), Color.FromArgb(0, 0, 0));
            Theme.DrawTextCenter(g, button.Label, Theme.ButtonFont, r, textCol);
        }

        private void PaintCard(Graphics g, Rectangle card, CardModel model, int cardIndex)
        {
            if (card.Width <= 0 || card.Height <= 0) return;
            string id = model.Provider == null ? null : model.Provider.Id;
            bool isKo = model.IsLoggedOut;
            bool isUnmet = model.IsNotInstalled;

            // 1. Draw classic JRPG double-white-border gradient window box (clean unbroken border)
            Color topCol = isKo ? Theme.CardKoTop : (isUnmet ? Color.FromArgb(22, 28, 48) : Theme.CardTop);
            Color botCol = isKo ? Theme.CardKoBottom : (isUnmet ? Color.FromArgb(8, 10, 22) : Theme.CardBottom);
            Fx.DrawRpgBox(g, card, topCol, botCol);

            int contentX = card.X + Theme.S(CardPadX);
            int contentWidth = card.Width - Theme.S(CardPadX) * 2;
            int headY = card.Y + Theme.S(CardPadTop);

            // 2. Cursor slot: pointing hand cursor glyph on first card (points at Claude Code in Column 0)
            int cursorWidth = (cardIndex % 2 == 0) ? Theme.S(19) : 0;
            int cursorX = contentX;
            if (cardIndex == 0)
            {
                Rectangle cursorBox = new Rectangle(cursorX, headY + Theme.S(1), cursorWidth, Theme.S(CardHeadHeight));
                Fx.DrawHandCursor(g, cursorBox, Color.White);
            }

            // 3. Provider Crest / Badge (vertically aligned per column)
            int badgeX = cursorX + cursorWidth;
            Rectangle badge = new Rectangle(badgeX, headY + (Theme.S(CardHeadHeight) - Theme.S(BadgeSize)) / 2,
                                            Theme.S(BadgeSize), Theme.S(BadgeSize));
            SolidBrush accent = Theme.AccentBrush(id);
            Theme.FillRound(g, accent, badge, Theme.Radius(3));
            Theme.DrawRound(g, isKo ? Theme.GrayPen : Theme.GoldPen, badge, Theme.Radius(3));
            Theme.DrawTextCenter(g, Theme.Initial(id), Theme.BadgeFont, badge, Theme.BgSolid);

            // 4. Provider Name with classic RPG text drop-shadow (dimmed for fallen / unmet party members)
            int nameX = badge.Right + Theme.S(BadgeGap);
            string name = Theme.Fit(model.Name, Theme.NameFont, Math.Max(Theme.S(50), contentWidth - (nameX - contentX) - Theme.S(85)));
            Color nameColor = isKo ? Color.FromArgb(165, 155, 175) : (isUnmet ? Color.FromArgb(180, 195, 225) : Theme.Text);
            Fx.DrawTextWithShadow(g, name, Theme.NameFont,
                                  new Point(nameX, headY + (Theme.S(CardHeadHeight) - TextHeight(Theme.NameFont)) / 2),
                                  nameColor, Color.FromArgb(0, 0, 0));

            // 5. Right side of header: Status badge or small "Lv" style plan text
            int planRight = card.Right - Theme.S(CardPadX);
            int planLeft = nameX + Math.Min(Theme.TextWidth(name, Theme.NameFont), contentWidth) + Theme.S(8);
            if (planLeft < planRight)
            {
                if (isKo)
                {
                    int koW = Theme.S(36);
                    int koH = Theme.S(14);
                    Rectangle koRect = new Rectangle(planRight - koW, headY + (Theme.S(CardHeadHeight) - koH) / 2, koW, koH);
                    Fx.DrawBadge(g, koRect, "KO", Theme.KoBgBrush, Color.White, Theme.KoBorderPen);
                }
                else if (isUnmet)
                {
                    int unmetW = Theme.S(48);
                    int unmetH = Theme.S(14);
                    Rectangle unmetRect = new Rectangle(planRight - unmetW, headY + (Theme.S(CardHeadHeight) - unmetH) / 2, unmetW, unmetH);
                    Fx.DrawBadge(g, unmetRect, "UNMET", Theme.UnmetBgBrush, Color.FromArgb(200, 215, 245), Theme.UnmetBorderPen);
                }
                else if (!string.IsNullOrEmpty(model.Plan))
                {
                    string planText = "LV. " + model.Plan;
                    string plan = Theme.Fit(planText, Theme.PlanFont, planRight - planLeft);
                    int planW = Theme.TextWidth(plan, Theme.PlanFont);
                    Point planPt = new Point(planRight - planW, headY + (Theme.S(CardHeadHeight) - TextHeight(Theme.PlanFont)) / 2);
                    Fx.DrawTextWithShadow(g, plan, Theme.PlanFont, planPt, Theme.Gold, Color.FromArgb(0, 0, 0));
                }
            }

            int y = headY + Theme.S(CardHeadHeight) + Theme.S(CardHeadGap);

            // Calculate cardLabelWidth based on longest label in this card (never exceed LabelMax)
            int cardLabelWidth = Theme.S(24);
            foreach (LineModel l in model.Lines)
            {
                if (l == null || l.Kind != LineRow || string.IsNullOrEmpty(l.Label)) continue;
                int lw = Theme.TextWidth(l.Label, Theme.RowLabelFont) + Theme.S(2);
                if (lw > cardLabelWidth) cardLabelWidth = lw;
            }

            // 6. Paint Lines: HP/MP meter rows, stale warning, extra credits, notes
            for (int i = 0; i < model.Lines.Count; i++)
            {
                LineModel line = model.Lines[i];
                int lineHeight = line.Height;
                int gap = i > 0 ? Theme.S(RowGap) : 0;
                y += gap;

                if (line.Kind == LineRow)
                {
                    // A. Label (e.g. "5h", "Wk", "Claude/GPT Wk" - fully visible, never clipped)
                    int labelWidth = Math.Min(Theme.S(LabelMax), cardLabelWidth);
                    if (labelWidth < Theme.S(24)) labelWidth = Theme.S(24);

                    string label = Theme.Fit(line.Label, Theme.RowLabelFont, labelWidth);
                    Color labelColor = line.Alert ? Color.FromArgb(254, 202, 202) : Theme.RowText;
                    Point labelPt = new Point(contentX, y + (lineHeight - TextHeight(Theme.RowLabelFont)) / 2);
                    Fx.DrawTextWithShadow(g, label, Theme.RowLabelFont, labelPt, labelColor, Color.FromArgb(0, 0, 0));

                    // B. Stat Tag: "HP", "MP", "SP"
                    int tagX = contentX + labelWidth + Theme.S(4);
                    int tagWidth = Theme.S(16);
                    Rectangle tagRect = new Rectangle(tagX, y + (lineHeight - Theme.S(12)) / 2, tagWidth, Theme.S(12));
                    Color tagColor = (line.StatTag == "HP") ? Theme.Gold : ((line.StatTag == "MP") ? Color.FromArgb(140, 200, 255) : Color.FromArgb(210, 180, 255));
                    Theme.DrawTextCenter(g, line.StatTag, Theme.StatTagFont, new Rectangle(tagRect.X + 1, tagRect.Y + 1, tagRect.Width, tagRect.Height), Color.FromArgb(0, 0, 0));
                    Theme.DrawTextCenter(g, line.StatTag, Theme.StatTagFont, tagRect, tagColor);

                    // C. Reset ETA column (right-aligned)
                    int etaRight = contentX + contentWidth;
                    int etaLeft = etaRight - etaColumn;
                    Rectangle etaBox = new Rectangle(etaLeft, y + (lineHeight - TextHeight(Theme.RowFont)) / 2, etaRight - etaLeft, lineHeight);
                    Theme.DrawTextRight(g, line.Eta, Theme.RowFont, new Rectangle(etaBox.X + 1, etaBox.Y + 1, etaBox.Width, etaBox.Height), Color.FromArgb(0, 0, 0));
                    Theme.DrawTextRight(g, line.Eta, Theme.RowFont, etaBox, Theme.Muted);

                    // D. Value column: remaining HP ("90/100", "84/100", "2/100" in Consolas bold)
                    int pctWidth = Math.Max(Theme.S(PctWidth), Theme.TextWidth(line.RemainingText, Theme.RowPctFont) + Theme.S(2));
                    int pctLeft = etaLeft - Theme.S(RowGapXMeter) - pctWidth;

                    // Meter & text color: "green -> yellow -> red as usage rises"
                    Color hpTextColor;
                    Color hpBarTop;
                    Color hpBarBottom;
                    if (line.Fill >= 80 || line.Alert)
                    {
                        hpTextColor = Theme.RedText;
                        hpBarTop = Theme.RedLight;
                        hpBarBottom = Theme.RedDark;
                    }
                    else if (line.Fill >= 50)
                    {
                        hpTextColor = Theme.AmberText;
                        hpBarTop = Theme.YellowLight;
                        hpBarBottom = Theme.YellowDark;
                    }
                    else
                    {
                        hpTextColor = Theme.GreenText;
                        hpBarTop = Theme.GreenLight;
                        hpBarBottom = Theme.GreenDark;
                    }

                    Rectangle pctBox = new Rectangle(pctLeft, y + (lineHeight - TextHeight(Theme.RowPctFont)) / 2, pctWidth, lineHeight);
                    Theme.DrawTextRight(g, line.RemainingText, Theme.RowPctFont, new Rectangle(pctBox.X + 1, pctBox.Y + 1, pctBox.Width, pctBox.Height), Color.FromArgb(0, 0, 0));
                    Theme.DrawTextRight(g, line.RemainingText, Theme.RowPctFont, pctBox, hpTextColor);

                    // E. Meter Bar (showing remaining as HP)
                    int meterX = tagX + tagWidth + Theme.S(4);
                    int meterWidth = pctLeft - Theme.S(RowGapXMeter) - meterX;
                    if (meterWidth < Theme.S(24))
                    {
                        meterWidth = Theme.S(24);
                    }

                    int meterHeight = Theme.S(MeterHeight);
                    int meterY = y + (lineHeight - meterHeight) / 2;
                    Rectangle track = new Rectangle(meterX, meterY, meterWidth, meterHeight);

                    Color trackBorder = (line.Fill >= 80 || line.Alert) ? Color.FromArgb(140, 36, 48) : Theme.BarTrackBorder;
                    Fx.DrawHpBar(g, track, line.Remaining, hpBarTop, hpBarBottom, trackBorder);
                }
                else if (line.Kind == LineStale)
                {
                    string badgeTag = (line.Text != null && line.Text.IndexOf("rate limited", StringComparison.OrdinalIgnoreCase) >= 0) ? "SLOW" : "STALE";
                    int badgeW = Theme.S(38);
                    int badgeH = Theme.S(13);
                    Rectangle badgeRect = new Rectangle(contentX, y + (lineHeight - badgeH) / 2, badgeW, badgeH);
                    Fx.DrawBadge(g, badgeRect, badgeTag, Theme.StaleBadgeBgBrush, Theme.AmberText, Theme.AmberPen);

                    int textX = contentX + badgeW + Theme.S(6);
                    int textMaxW = contentWidth - (textX - contentX);
                    string rawText = line.Text == null ? "" : line.Text;
                    if (rawText.Length > 0 && char.IsLower(rawText[0])) rawText = char.ToUpper(rawText[0]) + rawText.Substring(1);
                    string text = Theme.Fit(rawText, Theme.RowFont, textMaxW);
                    Fx.DrawTextWithShadow(g, text, Theme.RowFont,
                                          new Point(textX, y + (lineHeight - TextHeight(Theme.RowFont)) / 2),
                                          Theme.AmberText, Color.FromArgb(0, 0, 0));
                }
                else if (line.Kind == LineExtra)
                {
                    string text = Theme.Fit("◆ ITEM: " + line.Text, Theme.RowFont, contentWidth);
                    Point itemPt = new Point(contentX, y + (lineHeight - TextHeight(Theme.RowFont)) / 2);
                    Fx.DrawTextWithShadow(g, text, Theme.RowFont, itemPt, Theme.Gold, Color.FromArgb(0, 0, 0));
                }
                else if (line.Kind == LineNote)
                {
                    string text = Theme.Fit(line.Text, Theme.RowFont, contentWidth);
                    Point notePt = new Point(contentX, y + (lineHeight - TextHeight(Theme.RowFont)) / 2);
                    Fx.DrawTextWithShadow(g, text, Theme.RowFont, notePt, Theme.Dim, Color.FromArgb(0, 0, 0));
                }
                else if (line.Kind == LineHint)
                {
                    string text = Theme.Fit("▶ " + line.Text, Theme.RowFont, contentWidth);
                    Point hintPt = new Point(contentX, y + (lineHeight - TextHeight(Theme.RowFont)) / 2);
                    Color hintCol = isKo ? Color.FromArgb(210, 170, 180) : Theme.Muted;
                    Fx.DrawTextWithShadow(g, text, Theme.RowFont, hintPt, hintCol, Color.FromArgb(0, 0, 0));
                }

                y += lineHeight;
            }

            // 7. For fallen (KO) or unmet party members with extra vertical room,
            // render authentic ghosted RPG status gauges and condition details to fill the void.
            if ((isKo || isUnmet) && model.Lines.Count <= 1 && card.Bottom - y >= Theme.S(24))
            {
                int ghostY = y + Theme.S(5);
                int ghostH = Theme.S(RowHeight);

                string ghostLabel = isKo ? "  0/100" : " --/---";
                Color ghostColor = isKo ? Theme.RedText : Theme.Dim;
                string ghostEta = isKo ? "REVIVE" : "LOCKED";
                Color ghostEtaColor = isKo ? Theme.RedText : Theme.Dim;

                // A. Column 1: Row label ("--") aligned with 5h/Wk
                int labelWidth = Theme.S(24);
                Point labelPt = new Point(contentX, ghostY + (ghostH - TextHeight(Theme.RowLabelFont)) / 2);
                Fx.DrawTextWithShadow(g, "--", Theme.RowLabelFont, labelPt, Theme.Dim, Color.FromArgb(0, 0, 0));

                // B. Column 2: Stat tag ("HP") aligned with HP tags in active cards
                int tagX = contentX + labelWidth + Theme.S(4);
                int tagWidth = Theme.S(16);
                Rectangle tagRect = new Rectangle(tagX, ghostY + (ghostH - Theme.S(12)) / 2, tagWidth, Theme.S(12));
                Color hpTagColor = isKo ? Theme.RedText : Theme.Dim;
                Theme.DrawTextCenter(g, "HP", Theme.StatTagFont, new Rectangle(tagRect.X + 1, tagRect.Y + 1, tagRect.Width, tagRect.Height), Color.FromArgb(0, 0, 0));
                Theme.DrawTextCenter(g, "HP", Theme.StatTagFont, tagRect, hpTagColor);

                // C. Column 5: Reset ETA / Condition status ("REVIVE" / "LOCKED") right-aligned
                int etaRight = contentX + contentWidth;
                int etaLeft = etaRight - etaColumn;
                Rectangle etaRect = new Rectangle(etaLeft, ghostY + (ghostH - TextHeight(Theme.RowFont)) / 2, etaRight - etaLeft, ghostH);
                Theme.DrawTextRight(g, ghostEta, Theme.RowFont, new Rectangle(etaRect.X + 1, etaRect.Y + 1, etaRect.Width, etaRect.Height), Color.FromArgb(0, 0, 0));
                Theme.DrawTextRight(g, ghostEta, Theme.RowFont, etaRect, ghostEtaColor);

                // D. Column 4: Value ("  0/100" / " --/---") in Consolas bold
                int pctWidth = Math.Max(Theme.S(PctWidth), Theme.TextWidth(ghostLabel, Theme.RowPctFont) + Theme.S(2));
                int pctLeft = etaLeft - Theme.S(RowGapXMeter) - pctWidth;
                Rectangle pctRect = new Rectangle(pctLeft, ghostY + (ghostH - TextHeight(Theme.RowPctFont)) / 2, pctWidth, ghostH);
                Theme.DrawTextRight(g, ghostLabel, Theme.RowPctFont, new Rectangle(pctRect.X + 1, pctRect.Y + 1, pctRect.Width, pctRect.Height), Color.FromArgb(0, 0, 0));
                Theme.DrawTextRight(g, ghostLabel, Theme.RowPctFont, pctRect, ghostColor);

                // E. Column 3: Gauge track (starting at tagX + 16 + 4, aligned with active cards!)
                int trackX = tagX + tagWidth + Theme.S(4);
                int trackW = pctLeft - Theme.S(RowGapXMeter) - trackX;
                if (trackW < Theme.S(24)) trackW = Theme.S(24);

                Rectangle ghostTrack = new Rectangle(trackX, ghostY + (ghostH - Theme.S(MeterHeight)) / 2, trackW, Theme.S(MeterHeight));
                Color trackBorder = isKo ? Color.FromArgb(140, 36, 48) : Theme.BarTrackBorder;
                Fx.DrawHpBar(g, ghostTrack, 0, Theme.RedLight, Theme.RedDark, trackBorder);

                // F. Condition banner if vertical space allows
                int bannerY = ghostY + ghostH + Theme.S(4);
                if (bannerY + TextHeight(Theme.RowFont) <= card.Bottom - Theme.S(CardPadBottom))
                {
                    string statusMsg = isKo ? "◆ STATUS: KNOCKED OUT · RUN COMMAND TO REVIVE" : "◆ STATUS: UNLOCKED ONCE INSTALLED";
                    Color statusCol = isKo ? Color.FromArgb(235, 120, 120) : Color.FromArgb(140, 170, 215);
                    Fx.DrawTextWithShadow(g, statusMsg, Theme.RowFont, new Point(contentX, bannerY), statusCol, Color.FromArgb(0, 0, 0));
                }
            }
        }

        private int TextHeight(Font font)
        {
            return Theme.MeasureText("Ag", font).Height;
        }
    }
}
