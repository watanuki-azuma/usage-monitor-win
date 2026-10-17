// Application wiring for the native UI (milestone M5): config, snapshot, poll thread, forms,
// tray, alerts, toasts, autostart and the single-instance lock.
//
// Port of the "app.whenReady" half of reference/js/main.js minus the renderer processes:
//   * one UI thread (this message loop) plus one background poll thread
//   * the poll thread never touches a control: results are marshalled with BeginInvoke
//   * a single 1-second timer applies local resets and refreshes the ETA text
//   * single instance via a named Mutex; a second launch pokes an EventWaitHandle so the first
//     instance shows its flyout
//
// Language level: C# 5.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsageMonitor
{
    internal sealed class AppController : ApplicationContext
    {
        private readonly Paths paths;
        private readonly string dataDir;
        private readonly bool noPoll;

        private AppConfig config;
        private readonly FileSnapshotStore store;
        private readonly UsageLog usageLog;
        private readonly FileAlertStore alertStore;
        private AlertState alertState;
        private Snapshot current;

        private ChipsFormBase chipsForm;
        private FlyoutFormBase flyoutForm;
        private readonly TrayController tray;

        // A tiny hidden window whose handle is created on the UI thread in the constructor;
        // every PostToUi marshals through it so the poll thread never touches a live control.
        private Control dispatcher;

        private Log log;

        private System.Windows.Forms.Timer uiTimer;
        private Thread pollThread;
        private volatile bool stopping;
        private readonly ManualResetEventSlim wakePoll = new ManualResetEventSlim(false);

        // display / taskbar change tracking, evaluated on the single 1-second UI tick.
        // displayChangedFlag is raised from the SystemEvents thread and consumed (read-and-
        // cleared) on the UI thread with Interlocked, so a change that lands between two ticks
        // is never lost (item 8). Both the Shell_TrayWnd and the TrayNotifyWnd rectangles are
        // watched.
        private bool displayEventsSubscribed;
        private volatile int displayChangedFlag;
        private bool trayRectValid;
        private RectI lastTrayRect;
        private bool notifyRectValid;
        private RectI lastNotifyRect;

        private readonly Dictionary<string, string> loggedStatus = new Dictionary<string, string>();

        private EventWaitHandle activateEvent;
        private RegisteredWaitHandle activateWait;

        internal AppController(Paths paths, AppConfig config, string dataDir, bool noPoll)
        {
            this.paths = paths;
            this.config = config;
            this.dataDir = dataDir;
            this.noPoll = noPoll;

            log = new Log(Path.Combine(DataDir(), "usage-monitor.log"));

            store = new FileSnapshotStore(paths.SnapshotPath);

            usageLog = new UsageLog(Path.Combine(DataDir(), "usage-log.jsonl"));
            alertStore = new FileAlertStore(paths.AlertStatePath);
            alertState = alertStore.Load();

            Snapshot cached = store.Load();
            if (cached != null) current = cached;

            // Hidden dispatcher for cross-thread marshalling: force its handle to be created
            // right here, on the UI thread, before StartSingleInstanceWatch / StartPollThread can
            // post anything. (Item 3: PostToUi no longer calls CreateControl from the poller.)
            dispatcher = new Control();
            IntPtr dispatcherHandle = dispatcher.Handle;
            if (dispatcherHandle == IntPtr.Zero) dispatcher.CreateControl();

            // Watch for a display / taskbar change so a docked bar follows the taskbar when the
            // resolution changes or explorer restarts. It only raises a flag; the re-placement
            // itself rides on the single 1-second UI tick.
            try
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                displayEventsSubscribed = true;
            }
            catch (Exception) { /* never fatal: the 1 s tray-rect check still runs */ }

            // The tray controller needs "this" for its menu callbacks, so it is built here.
            tray = new TrayController(this);

            chipsForm = SkinRegistry.CreateChips(config.Skin, this);
            flyoutForm = SkinRegistry.CreateFlyout(config.Skin, this);
            // No MainForm: the ApplicationContext runs without one (item 2), so the chips bar is
            // visible only through the explicit Show() below, never forced by Application.Run.

            if (current != null)
            {
                chipsForm.SetSnapshot(current);
                flyoutForm.SetSnapshot(current);
            }
            else
            {
                chipsForm.SetSnapshot(null);
                flyoutForm.SetSnapshot(null);
            }

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 1000;
            uiTimer.Tick += OnTick;
            uiTimer.Start();

            // Always place the chips bar, even when it is hidden: the flyout anchors to the
            // chips window, which would otherwise stay at (0,0) and drag the flyout into the
            // top-left corner (item 7). Place() only moves and sizes the window; Show() is what
            // makes it visible, so a hidden bar stays hidden.
            chipsForm.Place();
            if (!config.ChipsHidden)
            {
                chipsForm.Show();
            }
            if (config.FlyoutDocked || config.FlyoutPinned)
            {
                flyoutForm.Place();
                flyoutForm.Show();
            }

            tray.Start();
        }

        private TrayController Tray
        {
            get { return tray; }
        }

        // --------------------------------------------------------------------------------------
        // state the forms read
        // --------------------------------------------------------------------------------------

        internal bool ChipsDocked { get { return config.ChipsDocked; } }

        internal bool ChipsHidden { get { return config.ChipsHidden; } }

        internal bool FlyoutDocked { get { return config.FlyoutDocked; } }

        internal bool FlyoutPinned { get { return config.FlyoutPinned; } }

        internal bool FlyoutVisible
        {
            get { return flyoutForm != null && flyoutForm.Visible; }
        }

        internal bool Autostart { get { return config.Autostart; } }

        internal int PollIntervalSecs { get { return config.PollIntervalSecs; } }

        internal AppConfig Config { get { return config; } }

        internal RectI ChipsWindowRect()
        {
            if (chipsForm == null) return default(RectI);
            return new RectI(chipsForm.Location.X, chipsForm.Location.Y,
                             chipsForm.ClientSize.Width, chipsForm.ClientSize.Height);
        }

        internal bool HasStoredFlyoutPosition()
        {
            return config.FlyoutX.HasValue && config.FlyoutY.HasValue;
        }

        internal Point StoredFlyoutPosition()
        {
            return new Point(config.FlyoutX.HasValue ? config.FlyoutX.Value : 0,
                             config.FlyoutY.HasValue ? config.FlyoutY.Value : 0);
        }

        internal int ChipsX(int fallback) { return config.ChipsX.HasValue ? config.ChipsX.Value : fallback; }

        internal int ChipsY(int fallback) { return config.ChipsY.HasValue ? config.ChipsY.Value : fallback; }

        internal bool HasStoredChipsPosition()
        {
            return config.ChipsX.HasValue && config.ChipsY.HasValue;
        }

        private string DataDir()
        {
            return dataDir;
        }

        // --------------------------------------------------------------------------------------
        // lifecycle
        // --------------------------------------------------------------------------------------

        internal void Start()
        {
            StartSingleInstanceWatch();
            // Like autostart.js apply(): the config value is written to the registry and the
            // actual state is read back, so the tray check mark always tells the truth.
            ApplyAutostart();
            if (!noPoll) StartPollThread();
            log.Info("started (interval " + config.PollIntervalSecs + "s, docked=" + config.ChipsDocked + ")");
        }

        private void ApplyAutostart()
        {
            try
            {
                SetAutostart(config.Autostart);
            }
            catch (Exception ex)
            {
                log.Warn("autostart apply failed: " + SafeMessage(ex));
            }
        }

        private void StartSingleInstanceWatch()
        {
            try
            {
                activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
                activateWait = ThreadPool.RegisterWaitForSingleObject(activateEvent,
                    delegate(object state, bool timedOut) { PostToUi(delegate() { ShowFlyout(); }); },
                    null, -1, false);
            }
            catch (Exception ex)
            {
                log.Warn("single-instance watch failed: " + ex.Message);
            }
        }

        private void StartPollThread()
        {
            pollThread = new Thread(PollLoop);
            pollThread.IsBackground = true;
            pollThread.Name = "usage-monitor-poll";
            pollThread.Start();
        }

        internal void Stop()
        {
            stopping = true;
            if (wakePoll != null) wakePoll.Set();
        }

        internal void Quit()
        {
            try { SaveConfig(); } catch (Exception) { /* nothing useful to do on the way out */ }
            Stop();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (uiTimer != null)
                {
                    uiTimer.Stop();
                    uiTimer.Dispose();
                    uiTimer = null;
                }
                if (activateWait != null) activateWait.Unregister(null);
                Stop();
                if (displayEventsSubscribed)
                {
                    try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; }
                    catch (Exception) { /* already torn down */ }
                    displayEventsSubscribed = false;
                }
                if (chipsForm != null) chipsForm.Dispose();
                if (flyoutForm != null) flyoutForm.Dispose();
                if (dispatcher != null)
                {
                    try { dispatcher.Dispose(); }
                    catch (Exception) { /* the UI thread is going away */ }
                    dispatcher = null;
                }
                Tray.Dispose();
            }
            base.Dispose(disposing);
        }

        // --------------------------------------------------------------------------------------
        // poll thread
        // --------------------------------------------------------------------------------------

        private void PollLoop()
        {
            IHttp http = new RealHttp();
            ICredentialReader credentials = new WinCredReader();
            List<IUsageAdapter> adapters = new List<IUsageAdapter>();
            adapters.Add(new ClaudeAdapter());
            adapters.Add(new CodexAdapter());
            adapters.Add(new GeminiAdapter());
            adapters.Add(new GrokAdapter());

            PollEngine engine = new PollEngine(adapters, store, UtcNow, 25000);

            while (!stopping)
            {
                int interval = config.PollIntervalSecs;
                if (interval <= 0) interval = 5;

                Snapshot snapshot = null;
                try
                {
                    AdapterContext ctx = new AdapterContext();
                    ctx.Http = http;
                    ctx.Paths = paths;
                    ctx.Credentials = credentials;
                    ctx.Config = config;
                    ctx.UtcNow = UtcNow;
                    snapshot = engine.PollOnce(ctx, interval);
                }
                catch (Exception ex)
                {
                    log.Warn("poll failed: " + SafeMessage(ex));
                }

                if (snapshot != null)
                {
                    usageLog.Append(snapshot, DateTime.UtcNow);
                    Snapshot captured = snapshot;
                    PostToUi(delegate() { ApplySnapshot(captured); });
                }

                wakePoll.Wait(interval * 1000);
                wakePoll.Reset();
            }
        }

        private static DateTime UtcNow()
        {
            return DateTime.UtcNow;
        }

        private static string SafeMessage(Exception ex)
        {
            if (ex == null) return "";
            string message = ex.Message;
            return string.IsNullOrEmpty(message) ? ex.GetType().Name : message;
        }

        // Never touch a control from this thread: marshal to the UI thread through the hidden
        // dispatcher, whose handle was created on the UI thread in the constructor.
        private void PostToUi(MethodInvoker action)
        {
            if (action == null) return;
            Control target = dispatcher;
            if (target == null || !target.IsHandleCreated || target.IsDisposed) return;
            try
            {
                target.BeginInvoke(action);
            }
            catch (Exception ex)
            {
                try { log.Warn("ui post failed: " + SafeMessage(ex)); }
                catch (Exception) { /* the log itself is gone; nothing left to report */ }
            }
        }

        // --------------------------------------------------------------------------------------
        // UI thread
        // --------------------------------------------------------------------------------------

        private void ApplySnapshot(Snapshot snapshot)
        {
            if (snapshot == null) return;
            current = snapshot;

            chipsForm.SetSnapshot(snapshot);
            flyoutForm.SetSnapshot(snapshot);

            LogProviderFailures(snapshot);
            EvaluateAlerts(snapshot);
        }

        // Log a provider failure once per (state, message) change. Tokens are never logged.
        private void LogProviderFailures(Snapshot snapshot)
        {
            if (snapshot.Providers == null) return;

            foreach (ProviderSnapshot provider in snapshot.Providers)
            {
                if (provider == null) continue;
                string id = provider.Id == null ? "" : provider.Id;
                ProviderStatus status = provider.Status;
                string state = status == null || status.State == null ? "" : status.State;
                string message = status == null ? "" : status.Message;

                bool failing = state == State.FetchFailed || state == State.LoggedOut ||
                               state == State.NotInstalled || state == State.Unknown;
                if (!failing)
                {
                    if (loggedStatus.ContainsKey(id)) loggedStatus.Remove(id);
                    continue;
                }

                string text = state + " " + message;
                string previous;
                if (loggedStatus.TryGetValue(id, out previous) && previous == text) continue;
                loggedStatus[id] = text;
                log.Warn("provider " + id + ": " + text);
            }
        }

        private void EvaluateAlerts(Snapshot snapshot)
        {
            try
            {
                List<Toast> toasts = AlertEngine.Evaluate(snapshot, alertState, config.NotifyOnLimitReached, DateTime.UtcNow);
                for (int i = 0; i < toasts.Count; i++)
                {
                    Toast toast = toasts[i];
                    if (toast == null) continue;
                    Tray.ShowBalloon(toast.Title == null ? "Usage Monitor" : toast.Title,
                                     toast.Body == null ? "" : toast.Body);
                }
                alertStore.Save(alertState);
            }
            catch (Exception ex)
            {
                log.Warn("alert evaluation failed: " + SafeMessage(ex));
            }
        }

        // The single 1-second UI tick: follow the taskbar, apply local resets, refresh the ETA.
        private void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;

            if (flyoutForm != null && flyoutForm.Visible) flyoutForm.UpdateEtas(now);

            CheckTrayAndDisplay();

            if (current == null) return;
            bool changed;
            Snapshot next = Models.ApplyLocalResets(current, now, out changed);
            if (!changed) return;

            current = next;
            ApplySnapshot(next);
            if (!noPoll && wakePoll != null) wakePoll.Set();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // Raised on the thread that owns SystemEvents' message pump, NOT our UI thread, so
            // the flag is shared with Interlocked. The single 1-second UI tick consumes it
            // (reads and clears it) inside CheckTrayAndDisplay.
            Interlocked.Exchange(ref displayChangedFlag, 1);
        }

        // Follow the taskbar when the display resolution changes or the Shell_TrayWnd /
        // TrayNotifyWnd rectangles move (e.g. explorer restarts, tray icons added or removed).
        // A couple of cheap GetWindowRect per second; re-placement only happens when something
        // actually changed, and only for dock-anchored windows.
        private void CheckTrayAndDisplay()
        {
            bool displayChanged = Interlocked.Exchange(ref displayChangedFlag, 0) != 0;

            RectI trayRect;
            bool haveTray = Native.RectOf(Native.TaskbarWindow(), out trayRect);
            RectI notifyRect;
            bool haveNotify = Native.RectOf(Native.TrayNotifyWindow(), out notifyRect);

            bool geometryChanged = displayChanged || !trayRectValid || !notifyRectValid
                || (haveTray && !SameRect(trayRect, lastTrayRect))
                || (haveNotify && !SameRect(notifyRect, lastNotifyRect));

            if (geometryChanged)
            {
                if (haveTray) { lastTrayRect = trayRect; trayRectValid = true; }
                if (haveNotify) { lastNotifyRect = notifyRect; notifyRectValid = true; }

                if (chipsForm != null && config.ChipsDocked) chipsForm.PlaceDocked();
                if (flyoutForm != null && flyoutForm.Visible && config.FlyoutDocked) flyoutForm.Place();
            }

            // Keep a visible, docked chips bar above ordinary windows without stealing focus.
            // Check once a second and only re-assert topmost when the window is not already the
            // topmost window in its z-order, so an unmoved bar never makes a SetWindowPos call.
            if (chipsForm != null && !chipsForm.IsDisposed && chipsForm.Visible && config.ChipsDocked
                && !Native.IsTopMost(chipsForm.Handle))
            {
                Native.SetTopMost(chipsForm.Handle, true);
            }
        }

        private static bool SameRect(RectI a, RectI b)
        {
            return a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H;
        }

        // --------------------------------------------------------------------------------------
        // actions called by the forms and the tray menu
        // --------------------------------------------------------------------------------------

        internal void ToggleFlyout()
        {
            if (flyoutForm == null) return;
            if (flyoutForm.Visible && !config.FlyoutDocked) HideFlyout(true);
            else ShowFlyout();
        }

        internal void ShowFlyout()
        {
            if (flyoutForm == null) return;
            if (config.ChipsHidden) { /* nothing to anchor to: use the stored position */ }
            flyoutForm.Place();
            flyoutForm.Show();
            flyoutForm.Activate();
            if (chipsForm != null) Native.SetTopMost(chipsForm.Handle, true);
        }

        internal void HideFlyout(bool force)
        {
            if (flyoutForm == null) return;
            if (!force && config.FlyoutDocked) return;
            flyoutForm.Hide();
        }

        internal void ShowTrayMenu()
        {
            if (chipsForm == null) return;
            Point origin = chipsForm.PointToClient(Cursor.Position);
            Tray.ShowMenu(chipsForm, origin);
        }

        // The window a shortcut menu should be parented to: the flyout when it is on screen (it
        // is the one that takes focus), otherwise the chips bar when visible. Null when neither
        // (both hidden) — the caller then shows the menu on the app message loop directly.
        internal System.Windows.Forms.Control MenuOwner()
        {
            if (flyoutForm != null && !flyoutForm.IsDisposed && flyoutForm.Visible) return flyoutForm;
            if (chipsForm != null && !chipsForm.IsDisposed && chipsForm.Visible) return chipsForm;
            return null;
        }

        // The window handle to make foreground right before a shortcut menu is shown: the owner
        // when it is on screen (a hidden owner cannot take the foreground), otherwise the hidden
        // dispatcher control, whose handle still belongs to our UI thread. A ContextMenuStrip
        // only dismisses on an outside click once our app owns the foreground window (item 5).
        internal IntPtr MenuForegroundHandle(System.Windows.Forms.Control owner)
        {
            System.Windows.Forms.Control target = owner;
            if (target == null || target.IsDisposed || !target.IsHandleCreated || !target.Visible)
                target = dispatcher;
            if (target == null || target.IsDisposed || !target.IsHandleCreated) return IntPtr.Zero;
            return target.Handle;
        }

        internal void RefreshNow()
        {
            if (wakePoll != null) wakePoll.Set();
        }

        internal void SetInterval(int secs)
        {
            int next = 5;
            for (int i = 0; i < AppConfig.AllowedIntervals.Length; i++)
            {
                if (AppConfig.AllowedIntervals[i] == secs) next = secs;
            }
            config.PollIntervalSecs = next;
            SaveConfig();
            RefreshNow();
        }

        internal void SetAutostart(bool enabled)
        {
            bool ok = true;
            if (enabled)
            {
                string exe = ExePath();
                if (exe == null || !Native.WriteRunValue(exe))
                {
                    ok = false;
                    log.Warn("autostart write failed");
                }
            }
            else
            {
                Native.DeleteRunValue();
            }

            config.Autostart = ok && enabled && Native.ReadRunValue() == ExePath();
            SaveConfig();
        }

        internal void SetChipsHidden(bool hidden)
        {
            config.ChipsHidden = hidden;
            if (chipsForm != null)
            {
                if (hidden) chipsForm.Hide();
                else { chipsForm.Place(); chipsForm.Show(); }
            }
            SaveConfig();
        }

        internal void SetChipsDocked(bool docked)
        {
            config.ChipsDocked = docked;
            if (chipsForm != null)
            {
                if (docked)
                {
                    chipsForm.PlaceDocked();
                }
                else
                {
                    int x = chipsForm.Location.X;
                    int y = chipsForm.Location.Y;
                    config.ChipsX = x;
                    config.ChipsY = y;
                }
            }
            SaveConfig();
        }

        // Switch the look live. The choice is saved now; the form swap is posted to the UI loop so
        // it never runs inside the tray menu's own click handler (the menu may be parented to one
        // of the forms that is about to be disposed).
        internal void SetSkin(string id)
        {
            string next = SkinRegistry.Normalize(id);
            if (next == SkinRegistry.Normalize(config.Skin) && chipsForm != null) return;
            config.Skin = next;
            SaveConfig();
            PostToUi(delegate() { SwapSkin(next); });
        }

        private void SwapSkin(string id)
        {
            if (stopping || uiTimer == null) return;

            ChipsFormBase oldChips = chipsForm;
            FlyoutFormBase oldFlyout = flyoutForm;
            bool flyoutWasOpen = oldFlyout != null && !oldFlyout.IsDisposed && oldFlyout.Visible;

            chipsForm = SkinRegistry.CreateChips(id, this);
            flyoutForm = SkinRegistry.CreateFlyout(id, this);
            chipsForm.SetSnapshot(current);
            flyoutForm.SetSnapshot(current);

            // Same visibility state as before: the chips are always placed (the flyout anchors to
            // them), shown unless hidden; the flyout is shown again if it was open.
            chipsForm.Place();
            if (!config.ChipsHidden)
            {
                chipsForm.Show();
                Native.SetTopMost(chipsForm.Handle, true);
            }
            if (flyoutWasOpen)
            {
                flyoutForm.Place();
                flyoutForm.Show();
            }

            if (oldFlyout != null) oldFlyout.Dispose();
            if (oldChips != null) oldChips.Dispose();
        }

        internal void ToggleChipsDocked()
        {
            SetChipsDocked(!config.ChipsDocked);
        }

        internal void RememberDockPosition(int x, int y)
        {
            if (config.ChipsDockX.HasValue && config.ChipsDockY.HasValue &&
                config.ChipsDockX.Value == x && config.ChipsDockY.Value == y) return;
            config.ChipsDockX = x;
            config.ChipsDockY = y;
            SaveConfig();
        }

        internal void RememberChipsPosition(int x, int y)
        {
            if (config.ChipsX.HasValue && config.ChipsY.HasValue &&
                config.ChipsX.Value == x && config.ChipsY.Value == y) return;
            config.ChipsX = x;
            config.ChipsY = y;
            SaveConfig();
        }

        // Called once, from a flyout drag end, to persist where an undocked flyout was dropped.
        internal void RememberFlyoutPosition(int x, int y)
        {
            if (config.FlyoutX.HasValue && config.FlyoutY.HasValue &&
                config.FlyoutX.Value == x && config.FlyoutY.Value == y) return;
            config.FlyoutX = x;
            config.FlyoutY = y;
            SaveConfig();
        }

        internal void SetFlyoutDocked(bool docked)
        {
            config.FlyoutDocked = docked;
            if (docked) config.FlyoutPinned = true;
            if (flyoutForm != null)
            {
                if (!docked)
                {
                    config.FlyoutX = flyoutForm.Location.X;
                    config.FlyoutY = flyoutForm.Location.Y;
                }
                flyoutForm.Place();
            }
            SaveConfig();
        }

        internal void SetFlyoutPinned(bool pinned)
        {
            config.FlyoutPinned = pinned;
            if (!pinned) config.FlyoutDocked = false;
            SaveConfig();
        }

        internal void ToggleFlyoutDock()
        {
            SetFlyoutDocked(!config.FlyoutDocked);
        }

        internal void ToggleFlyoutPin()
        {
            SetFlyoutPinned(!config.FlyoutPinned);
        }

        internal void OpenUsage(string providerId)
        {
            string url = null;
            if (providerId == "claude") url = "https://claude.ai/settings/usage";
            else if (providerId == "codex") url = "https://chatgpt.com/codex/settings/usage";
            else if (providerId == "gemini") url = "https://antigravity.google";
            else if (providerId == "grok") url = "https://grok.com";
            if (url == null) return;

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(url);
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch (Exception ex)
            {
                log.Warn("open usage failed: " + SafeMessage(ex));
            }
        }

        // --------------------------------------------------------------------------------------
        // helpers
        // --------------------------------------------------------------------------------------

        private void SaveConfig()
        {
            try
            {
                AtomicFile.Write(paths.ConfigPath, config.ToJson());
            }
            catch (Exception ex)
            {
                log.Warn("config write failed: " + SafeMessage(ex));
            }
        }

        private static string ExePath()
        {
            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                if (entry != null && !string.IsNullOrEmpty(entry.Location)) return entry.Location;
            }
            catch (Exception) { /* fall through to the WinForms answer */ }
            try
            {
                string exe = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(exe)) return exe;
            }
            catch (Exception) { /* last resort below */ }
            try
            {
                return Process.GetCurrentProcess().MainModule.FileName;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
