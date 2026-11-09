using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarWidgets
{
    internal static class Native
    {
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000,
                         WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20;
        public const int GWL_EXSTYLE = -20;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
        public const uint EVENT_SYSTEM_FOREGROUND = 3;
        public const uint WINEVENT_OUTOFCONTEXT = 0;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; public SIZE(int w, int h) { cx = w; cy = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

        public delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLENDFUNCTION bf, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int val);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr val);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate cb, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_SKIPOWNPROCESS = 2;

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

        public static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Releases unused pages after the panel closes so the idle footprint stays small.</summary>
        public static void TrimMemory()
        {
            try
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }

        public static void RoundWindow(IntPtr h, bool dark, Color border)
        {
            try
            {
                int v = 2; DwmSetWindowAttribute(h, 33, ref v, 4);           // DWMWA_WINDOW_CORNER_PREFERENCE = round
                int d = dark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref d, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
                int c = border.R | (border.G << 8) | (border.B << 16);
                DwmSetWindowAttribute(h, 34, ref c, 4);                        // DWMWA_BORDER_COLOR
            }
            catch { }
        }
    }
}
