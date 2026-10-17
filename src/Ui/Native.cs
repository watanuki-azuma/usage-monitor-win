// Win32 interop for the UI layer (milestone M5).
//
// Everything the UI needs that WinForms does not offer out of the box: finding the taskbar and
// the tray notification area, per-monitor DPI awareness, the layered/tool-window styles that
// make the chips bar click-through and taskbar-button-less, the cursor position for dragging
// and the HKCU "Run" key used for autostart.
//
// No syntax newer than C# 5 is used anywhere in this file.
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace UsageMonitor
{
    internal static class Native
    {
        // window styles used by the chips bar (a tool window that never takes focus)
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;

        public const int SWP_NOSIZE = 0x0001;
        public const int SWP_NOMOVE = 0x0002;
        public const int SWP_NOZORDER = 0x0004;
        public const int SWP_NOACTIVATE = 0x0010;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const int LOGPIXELSY = 90;

        // HKCU\Software\Microsoft\Windows\CurrentVersion\Run
        public const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        public const string RunValueName = "Usage Monitor";

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }

            public RectI ToRectI()
            {
                return new RectI(Left, Top, Right - Left, Bottom - Top);
            }
        }

        // ---- windows -------------------------------------------------------------------------

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr windowHandle, out RECT rect);

        // Gives our (possibly background) app the foreground so a shortcut menu that follows
        // dismisses on an outside click (item 5).
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr windowHandle);

        // The W variants are exported by both the 32-bit and the 64-bit user32, and window
        // styles are 32-bit values, so Get/SetWindowLong are safe to use here.
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowLongW", SetLastError = true)]
        public static extern IntPtr GetWindowLong(IntPtr windowHandle, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowLongW", SetLastError = true)]
        public static extern IntPtr SetWindowLong(IntPtr windowHandle, int index, IntPtr newValue);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter,
                                               int x, int y, int width, int height, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool MoveWindow(IntPtr windowHandle, int x, int y, int width, int height, bool repaint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr windowHandle);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr windowHandle, IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr dc, int index);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool DestroyIcon(IntPtr handle);

        // ---- DPI -----------------------------------------------------------------------------

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetDpiForSystem();

        // System-DPI aware for the whole process, called once before any window exists. The UI is
        // laid out against the single system DPI (Theme.Scale) and the OS bitmap-scales each
        // window on a monitor with a different DPI, so a drag to another display keeps one size
        // instead of the wrong one; per-monitor awareness would break that for the dock helpers.
        public static void SetSystemDpiAware()
        {
            try { SetProcessDPIAware(); }
            catch (Exception) { /* nothing else we can do */ }
        }

        // Design scale of the UI: 1.0 at 96 DPI. Capped so a 4K monitor does not blow up the bar.
        public static float DpiScale()
        {
            int dpi = 0;
            try
            {
                int value = GetDpiForSystem();
                if (value >= 72 && value <= 480) dpi = value;
            }
            catch (Exception) { dpi = 0; }
            if (dpi < 72) dpi = SystemDpi();
            if (dpi < 72) dpi = 96;
            if (dpi > 192) dpi = 192;
            return dpi / 96f;
        }

        private static int SystemDpi()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return 0;
            try
            {
                int value = GetDeviceCaps(dc, LOGPIXELSY);
                return value;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }

        // ---- taskbar helpers -----------------------------------------------------------------

        public static IntPtr TaskbarWindow()
        {
            return FindWindow("Shell_TrayWnd", null);
        }

        public static IntPtr TrayNotifyWindow()
        {
            IntPtr tray = TaskbarWindow();
            if (tray == IntPtr.Zero) return IntPtr.Zero;
            return FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        }

        public static bool RectOf(IntPtr handle, out RectI rect)
        {
            rect = default(RectI);
            if (handle == IntPtr.Zero) return false;
            RECT raw;
            if (!GetWindowRect(handle, out raw)) return false;
            rect = raw.ToRectI();
            return true;
        }

        public static RectI ToRectI(Rectangle value)
        {
            return new RectI(value.X, value.Y, value.Width, value.Height);
        }

        public static void SetTopMost(IntPtr handle, bool topMost)
        {
            if (handle == IntPtr.Zero) return;
            SetWindowPos(handle, topMost ? HWND_TOPMOST : HWND_NOTOPMOST,
                         0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // True when the window currently carries the WS_EX_TOPMOST extended style, i.e. it is a
        // topmost window. The 1-second tick uses this to skip a redundant SetWindowPos when the
        // chips bar is already topmost (item 6).
        public static bool IsTopMost(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;
            try
            {
                long style = GetWindowLong(handle, GWL_EXSTYLE).ToInt64();
                return (style & WS_EX_TOPMOST) != 0;
            }
            catch (Exception) { return false; }
        }

        public static void MoveTo(IntPtr handle, int x, int y, int width, int height)
        {
            if (handle == IntPtr.Zero) return;
            MoveWindow(handle, x, y, width, height, true);
            SetTopMost(handle, true);
        }

        public static Point CursorPosition()
        {
            POINT point;
            if (!GetCursorPos(out point)) return Point.Empty;
            return new Point(point.X, point.Y);
        }

        // ---- HKCU Run (autostart) -------------------------------------------------------------

        private static readonly UIntPtr HKEY_CURRENT_USER = new UIntPtr(0x80000001u);

        private const int KEY_QUERY_VALUE = 0x0001;
        private const int KEY_SET_VALUE = 0x0002;
        private const int REG_SZ = 1;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegOpenKeyEx(UIntPtr key, string subKey, int options, int desired, out UIntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegQueryValueEx(UIntPtr key, string valueName, int reserved,
                                                  out int type, System.Text.StringBuilder data, ref int length);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegSetValueEx(UIntPtr key, string valueName, int reserved,
                                                int type, string data, int length);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegDeleteValue(UIntPtr key, string valueName);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegCloseKey(UIntPtr key);

        public static string ReadRunValue()
        {
            UIntPtr key;
            int rc = RegOpenKeyEx(HKEY_CURRENT_USER, RunKeyPath, 0, KEY_QUERY_VALUE, out key);
            if (rc != 0) return null;
            try
            {
                System.Text.StringBuilder buffer = new System.Text.StringBuilder(1024);
                int length = buffer.Capacity;
                int type;
                rc = RegQueryValueEx(key, RunValueName, 0, out type, buffer, ref length);
                if (rc != 0) return null;
                return UnquoteRunValue(buffer.ToString());
            }
            finally
            {
                RegCloseKey(key);
            }
        }

        public static bool WriteRunValue(string value)
        {
            if (value == null) return false;
            string stored = QuoteRunValue(value);
            UIntPtr key;
            int rc = RegOpenKeyEx(HKEY_CURRENT_USER, RunKeyPath, 0, KEY_SET_VALUE, out key);
            if (rc != 0) return false;
            try
            {
                int length = (stored.Length + 1) * sizeof(char);
                rc = RegSetValueEx(key, RunValueName, 0, REG_SZ, stored, length);
                return rc == 0;
            }
            finally
            {
                RegCloseKey(key);
            }
        }

        // The Run value is stored quoted ("C:\path with spaces\UsageMonitor.exe") so Windows keeps
        // a path with spaces whole when it launches; the same pair of quotes is stripped on read.
        private static string QuoteRunValue(string value)
        {
            if (value == null) return null;
            int len = value.Length;
            if (len >= 2 && value[0] == '"' && value[len - 1] == '"') return value;
            return "\"" + value + "\"";
        }

        private static string UnquoteRunValue(string value)
        {
            if (value == null) return null;
            int len = value.Length;
            if (len >= 2 && value[0] == '"' && value[len - 1] == '"')
            {
                return value.Substring(1, len - 2);
            }
            return value;
        }

        public static bool DeleteRunValue()
        {
            UIntPtr key;
            int rc = RegOpenKeyEx(HKEY_CURRENT_USER, RunKeyPath, 0, KEY_SET_VALUE, out key);
            if (rc != 0) return true;   // nothing to remove
            try
            {
                return RegDeleteValue(key, RunValueName) == 0;
            }
            finally
            {
                RegCloseKey(key);
            }
        }
    }
}
