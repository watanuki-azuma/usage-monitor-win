// Tray icon and its context menu (milestone M5). Port of the tray half of reference/js/main.js.
//
// The icon is drawn at runtime (rounded badge in the claude accent with the "C" initial), so the
// app ships no image files. Left click opens the flyout, right click pops the menu:
//   Hide/Show chips, Refresh now, Refresh every {5,15,30,60} seconds, Open/Hide flyout,
//   snap/unsnap flyout and chips, Start with Windows (HKCU Run), Quit.
//
// The menu is built once and its labels/check marks are refreshed before each popup (a fresh
// ContextMenuStrip per click would leak menus). Language level: C# 5.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace UsageMonitor
{
    internal sealed class TrayController : IDisposable
    {
        private const double TrayOpacity = 0.96;

        private readonly AppController app;
        private NotifyIcon icon;
        private Bitmap iconBitmap;
        private Icon trayIcon;
        private bool disposed;

        private ContextMenuStrip menu;
        private ToolStripMenuItem chipsItem;
        private ToolStripMenuItem flyoutItem;
        private ToolStripMenuItem flyoutDockItem;
        private ToolStripMenuItem chipsDockItem;
        private ToolStripMenuItem autostartItem;
        private ToolStripMenuItem[] intervalItems = new ToolStripMenuItem[AppConfig.AllowedIntervals.Length];
        private ToolStripMenuItem[] skinItems = new ToolStripMenuItem[SkinRegistry.Ids.Length];

        internal TrayController(AppController owner)
        {
            app = owner;
        }

        internal void Start()
        {
            if (icon != null) return;

            iconBitmap = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(iconBitmap))
            {
                DrawIcon(g);
            }
            IntPtr handle = iconBitmap.GetHicon();
            try
            {
                using (Icon fromHandle = Icon.FromHandle(handle))
                {
                    trayIcon = new Icon(fromHandle, new Size(16, 16));
                }
            }
            finally
            {
                Native.DestroyIcon(handle);
            }

            icon = new NotifyIcon();
            icon.Icon = trayIcon;
            icon.Text = "Usage Monitor";
            icon.Visible = true;
            icon.MouseClick += OnIconClick;
            icon.BalloonTipClicked += OnBalloonClick;
        }

        private void DrawIcon(Graphics g)
        {
            g.Clear(Color.Transparent);
            using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(TrayOpacity * 255.0), 17, 24, 39)))
            {
                g.FillRectangle(brush, 0, 0, 16, 16);
            }
            using (SolidBrush accent = new SolidBrush(Theme.Claude))
            {
                g.FillEllipse(accent, 2, 2, 12, 12);
            }
            using (Font font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                TextRenderer.DrawText(g, "C", font, new Rectangle(0, 0, 16, 16), Color.FromArgb(17, 24, 39),
                                     TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void OnIconClick(object sender, MouseEventArgs e)
        {
            if (app == null) return;
            if (e.Button == MouseButtons.Right)
            {
                // The old app popped the app menu on tray right-click (main.js right-click).
                ShowMenuAtCursor();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (app.FlyoutVisible) app.HideFlyout(false);
            else app.ShowFlyout();
        }

        private void OnBalloonClick(object sender, EventArgs e)
        {
            if (app == null) return;
            app.ShowFlyout();
        }

        internal void ShowBalloon(string title, string body)
        {
            if (icon == null) return;
            try
            {
                icon.ShowBalloonTip(10000, title, body, ToolTipIcon.None);
            }
            catch (Exception) { /* a missing tray icon must never kill the poll loop */ }
        }

        internal void ShowMenu(Control owner, Point location)
        {
            EnsureMenu();
            RefreshMenu();
            // Make our app the foreground window first, otherwise a posted menu does not close
            // on an outside click (item 5).
            IntPtr foreground = app == null ? IntPtr.Zero : app.MenuForegroundHandle(owner);
            if (foreground != IntPtr.Zero) Native.SetForegroundWindow(foreground);
            menu.Show(owner, location);
        }

        // Tray right-click pops the same menu at the cursor in screen coordinates. It is parented
        // to whichever of our windows is on screen when there is one, otherwise shown on the
        // application message loop directly.
        private void ShowMenuAtCursor()
        {
            EnsureMenu();
            RefreshMenu();
            Control owner = app == null ? null : app.MenuOwner();
            IntPtr foreground = app == null ? IntPtr.Zero : app.MenuForegroundHandle(owner);
            if (foreground != IntPtr.Zero) Native.SetForegroundWindow(foreground);
            if (owner != null && !owner.IsDisposed)
            {
                menu.Show(owner, owner.PointToClient(Cursor.Position));
            }
            else
            {
                menu.Show(Cursor.Position);
            }
        }

        private void EnsureMenu()
        {
            if (menu != null) return;

            menu = new ContextMenuStrip();
            menu.Items.Add(Item("Refresh now", "refresh"));
            ToolStripMenuItem interval = new ToolStripMenuItem("Refresh every");
            for (int i = 0; i < AppConfig.AllowedIntervals.Length; i++)
            {
                int secs = AppConfig.AllowedIntervals[i];
                ToolStripMenuItem entry = new ToolStripMenuItem(secs + " seconds");
                entry.Tag = "interval:" + secs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                entry.Click += OnMenuItemClick;
                intervalItems[i] = entry;
                interval.DropDownItems.Add(entry);
            }
            menu.Items.Add(interval);

            menu.Items.Add(new ToolStripSeparator());

            chipsItem = Item("Hide chips", "chips");
            menu.Items.Add(chipsItem);

            flyoutItem = Item("Open flyout", "flyout");
            menu.Items.Add(flyoutItem);

            flyoutDockItem = new ToolStripMenuItem("Snap flyout to taskbar");
            flyoutDockItem.Tag = "flyout-dock";
            flyoutDockItem.Click += OnMenuItemClick;
            menu.Items.Add(flyoutDockItem);

            chipsDockItem = new ToolStripMenuItem("Snap chips to taskbar");
            chipsDockItem.Tag = "chips-dock";
            chipsDockItem.Click += OnMenuItemClick;
            menu.Items.Add(chipsDockItem);

            autostartItem = new ToolStripMenuItem("Start with Windows");
            autostartItem.Tag = "autostart";
            autostartItem.Click += OnMenuItemClick;
            menu.Items.Add(autostartItem);

            ToolStripMenuItem design = new ToolStripMenuItem("Design");
            string[] skinIds = SkinRegistry.Ids;
            string[] skinNames = SkinRegistry.Names;
            for (int i = 0; i < skinIds.Length; i++)
            {
                ToolStripMenuItem entry = new ToolStripMenuItem(skinNames[i]);
                entry.Tag = "skin:" + skinIds[i];
                entry.Click += OnMenuItemClick;
                skinItems[i] = entry;
                design.DropDownItems.Add(entry);
            }
            menu.Items.Add(design);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Quit", "quit"));
        }

        private ToolStripMenuItem Item(string label, string tag)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Tag = tag;
            item.Click += OnMenuItemClick;
            return item;
        }

        private void OnMenuItemClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null || app == null) return;
            string tag = item.Tag as string;
            if (tag == null) return;

            if (tag == "refresh") app.RefreshNow();
            else if (tag == "quit") app.Quit();
            else if (tag == "chips") app.SetChipsHidden(!app.ChipsHidden);
            else if (tag == "flyout")
            {
                if (app.FlyoutVisible) app.HideFlyout(true);
                else app.ShowFlyout();
            }
            else if (tag == "flyout-dock")
            {
                if (app.FlyoutDocked) app.SetFlyoutDocked(false);
                else { app.SetFlyoutPinned(true); app.SetFlyoutDocked(true); app.ShowFlyout(); }
            }
            else if (tag == "chips-dock") app.ToggleChipsDocked();
            else if (tag == "autostart") app.SetAutostart(!app.Autostart);
            else if (tag.StartsWith("skin:", StringComparison.Ordinal)) app.SetSkin(tag.Substring("skin:".Length));
            else if (tag != null && tag.StartsWith("interval:", StringComparison.Ordinal))
            {
                string text = tag.Substring("interval:".Length);
                int secs;
                if (int.TryParse(text, out secs)) app.SetInterval(secs);
            }
        }

        // Labels and check marks follow the current config, so the menu is always in sync.
        private void RefreshMenu()
        {
            if (menu == null) return;

            if (chipsItem != null) chipsItem.Text = app != null && app.ChipsHidden ? "Show chips" : "Hide chips";
            if (flyoutItem != null) flyoutItem.Text = app != null && app.FlyoutVisible ? "Hide flyout" : "Open flyout";
            if (flyoutDockItem != null)
                flyoutDockItem.Text = app != null && app.FlyoutDocked ? "Unsnap flyout from taskbar" : "Snap flyout to taskbar";
            if (chipsDockItem != null)
                chipsDockItem.Text = app != null && app.ChipsDocked ? "Unsnap chips from taskbar" : "Snap chips to taskbar";
            if (autostartItem != null) autostartItem.Checked = app != null && app.Autostart;

            string[] ids = SkinRegistry.Ids;
            string skin = app == null ? SkinRegistry.Classic : SkinRegistry.Normalize(app.Config.Skin);
            for (int i = 0; i < skinItems.Length; i++)
            {
                if (skinItems[i] == null) continue;
                skinItems[i].Checked = ids[i] == skin;
            }

            int current = app == null ? 5 : app.PollIntervalSecs;
            for (int i = 0; i < intervalItems.Length; i++)
            {
                if (intervalItems[i] == null) continue;
                intervalItems[i].Checked = AppConfig.AllowedIntervals[i] == current;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (icon != null)
            {
                icon.Visible = false;
                icon.MouseClick -= OnIconClick;
                icon.BalloonTipClicked -= OnBalloonClick;
                icon.Dispose();
                icon = null;
            }
            if (menu != null)
            {
                menu.Dispose();
                menu = null;
            }
            if (trayIcon != null)
            {
                trayIcon.Dispose();
                trayIcon = null;
            }
            if (iconBitmap != null)
            {
                iconBitmap.Dispose();
                iconBitmap = null;
            }
        }
    }
}
