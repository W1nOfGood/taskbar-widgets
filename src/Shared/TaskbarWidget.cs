using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarWidgets
{
    internal enum Edge { Bottom, Top, Left, Right }

    /// <summary>One taskbar: the main one or a secondary display's.</summary>
    internal sealed class Bar
    {
        public IntPtr Hwnd;
        public Rectangle Rect, Monitor;
        public Edge Edge;
        public bool Vertical { get { return Edge == Edge.Left || Edge == Edge.Right; } }

        /// <summary>False while an auto-hide taskbar is tucked away.</summary>
        public bool Shown
        {
            get
            {
                var i = Rectangle.Intersect(Rect, Monitor);
                return Rect.Width > 0 && Rect.Height > 0 && i.Width * (long)i.Height * 2 >= Rect.Width * (long)Rect.Height;
            }
        }

        /// <summary>Main taskbar first, then the other displays' taskbars from left to right.</summary>
        public static List<IntPtr> All()
        {
            var list = new List<IntPtr>();
            IntPtr main = Native.FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);
            var sec = new List<KeyValuePair<int, IntPtr>>();
            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                Native.RECT r; Native.GetWindowRect(h, out r);
                sec.Add(new KeyValuePair<int, IntPtr>(r.Left * 4 + r.Top, h));
            }
            sec.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var kv in sec) list.Add(kv.Value);
            return list;
        }

        public static Bar Get(int index)
        {
            var all = All();
            var b = new Bar { Hwnd = index >= 0 && index < all.Count ? all[index] : (all.Count > 0 ? all[0] : IntPtr.Zero) };
            Native.RECT r;
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (b.Hwnd != IntPtr.Zero && Native.GetWindowRect(b.Hwnd, out r) && Native.GetMonitorInfo(Native.MonitorFromWindow(b.Hwnd, 2), ref mi))
            {
                b.Rect = r.ToRectangle();
                b.Monitor = mi.rcMonitor.ToRectangle();
            }
            else
            {
                var sc = Screen.PrimaryScreen;
                b.Monitor = sc.Bounds;
                b.Rect = new Rectangle(sc.Bounds.Left, sc.WorkingArea.Bottom, sc.Bounds.Width, Math.Max(40, sc.Bounds.Bottom - sc.WorkingArea.Bottom));
            }
            int cx = b.Rect.Left + b.Rect.Width / 2, cy = b.Rect.Top + b.Rect.Height / 2;
            int mx = b.Monitor.Left + b.Monitor.Width / 2, my = b.Monitor.Top + b.Monitor.Height / 2;
            if (b.Rect.Width >= b.Rect.Height) b.Edge = cy < my ? Edge.Top : Edge.Bottom;
            else b.Edge = cx < mx ? Edge.Left : Edge.Right;
            return b;
        }
    }

    /// <summary>
    /// A per-pixel-alpha window that lives on the taskbar. Subclasses only measure and paint.
    /// It reads where the taskbar's own buttons are and only uses free space, shrinking to fit
    /// (subclasses read <see cref="AvailWidth"/>) or moving to a bigger free gap when needed.
    /// </summary>
    internal abstract class TaskbarWidget : Form
    {
        public readonly Settings S;
        public readonly string AppId;
        public readonly int Slot;
        public Theme T;
        public float K = 1f;
        public PanelForm Panel;
        public NotifyIcon Tray;

        public static readonly string[] AnchorNames = { "Next to Start button", "Left side of taskbar", "Next to clock / tray", "Where I drag it" };
        static readonly string[] anchorKeys = { "start", "left", "tray", "custom" };

        /// <summary>Width the subclass may use (it should shrink its content to fit when it can).</summary>
        protected int AvailWidth = int.MaxValue;
        /// <summary>True while the mouse is over the widget / a button is pressed (for visual feedback).</summary>
        protected bool Hovering, Pressed;
        protected Point Mouse = new Point(-1, -1);

        readonly Timer tick = new Timer { Interval = 400 };
        readonly Timer scanSoon = new Timer { Interval = 350 };
        Native.WinEventDelegate hookProc;
        IntPtr hook;
        bool hoverHidden, fullHidden;
        Size lastSize; int lastNatural, lastPrefAvail = -1; byte constAlpha = 255;
        int ticks;
        DateTime lastInput = DateTime.Now; bool trimmed;

        protected TaskbarWidget(Settings s, string appId, int slot)
        {
            S = s; AppId = appId; Slot = slot;
            T = Theme.From(s);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(1, 1);
            Location = new Point(-32000, -32000);
            Text = WindowTitle(slot);
            tick.Tick += (o, e) => OnTick();
            scanSoon.Tick += (o, e) => { scanSoon.Stop(); RequestScan(); };
            slideTimer.Tick += (o, e) => SlideStep();
            S.Changed += () => { T = Theme.From(S); Render(); };
            SystemEvents.UserPreferenceChanged += OnPrefsChanged;
            SystemEvents.DisplaySettingsChanged += (o, e) => BeginInvoke(new Action(() => { scans.Clear(); Render(); RequestScan(); }));
        }

        public string AnchorKey { get { return S.Get("anchor", "start"); } }
        public int AnchorIndex { get { return Math.Max(0, Array.IndexOf(anchorKeys, AnchorKey)); } set { S.Set("anchor", anchorKeys[value]); } }
        static string WindowTitle(int slot) { return "TaskbarWidgets.Slot" + slot; }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style = Native.WS_POPUP;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hookProc = (h, ev, hwnd, a, b, c, d) =>
            {
                if (IsDisposed) return;
                CheckZOrder();
                scanSoon.Stop(); scanSoon.Start(); // apps opening/closing re-center the icons
            };
            hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            ApplyInteractive();
            CurrentBar();
        }

        static readonly int WM_TASKBARCREATED = Native.RegisterWindowMessage("TaskbarCreated");

        /// <summary>
        /// Owned windows always stay above their owner, so making the taskbar our owner keeps the
        /// widget visible even when Explorer re-raises the taskbar (plain TOPMOST is not enough on Windows 11).
        /// </summary>
        void AttachToTaskbar(IntPtr tb)
        {
            if (tb != IntPtr.Zero && Native.GetWindowLongPtr(Handle, -8 /*GWLP_HWNDPARENT*/) != tb)
                Native.SetWindowLongPtr(Handle, -8, tb);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            tick.Start();
            Render();
            RequestScan();
            new Updater(this).Start();
            string updated = Updater.UpdatedTo(Environment.GetCommandLineArgs());
            if (updated != null)
                Notify("Taskbar Widgets updated to " + updated, "Click to see what's new.", () => Updater.Instance.OpenPage());
            // Startup touches many pages that are never used again: give them back once settled.
            var trim = new Timer { Interval = 8000 };
            trim.Tick += (o, ev) => { trim.Dispose(); if (Panel == null || !Panel.Visible) Native.TrimMemory(); };
            trim.Start();
            // "--panel N" opens the panel on tab N at startup (handy for screenshots / testing)
            var args = Environment.GetCommandLineArgs();
            int ai = Array.IndexOf(args, "--panel"), tab;
            if (ai > 0 && ai + 1 < args.Length && int.TryParse(args[ai + 1], out tab))
            {
                var t = new Timer { Interval = 1500 };
                t.Tick += (o, ev) => { t.Dispose(); OpenPanel(tab); };
                t.Start();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
            if (locHook != IntPtr.Zero) Native.UnhookWinEvent(locHook);
            SystemEvents.UserPreferenceChanged -= OnPrefsChanged;
            if (Tray != null) Tray.Visible = false;
            base.OnFormClosed(e);
        }

        void OnPrefsChanged(object o, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
                BeginInvoke(new Action(ThemeChanged));
        }

        public void ThemeChanged()
        {
            T = Theme.From(S);
            Render();
            if (Panel != null && Panel.Visible) { Panel.ApplyTheme(); Panel.Invalidate(); }
        }

        /// <summary>Call on any user interaction (keeps the process "warm", see idle trimming).</summary>
        public void Touch() { lastInput = DateTime.Now; trimmed = false; }

        // ---------------------------------------------------------------- abstract content

        protected abstract int MeasureWidth(Graphics g, int height);
        protected abstract void PaintWidget(Graphics g, Rectangle r);
        protected abstract void OnWidgetClick(Point p, MouseButtons b);
        protected virtual void OnWidgetWheel(int delta) { }
        protected virtual bool HasContent { get { return true; } }

        // ---------------------------------------------------------------- taskbar & displays

        IntPtr hostHwnd;

        public static string[] DisplayNames()
        {
            var all = Bar.All();
            var names = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                var b = Bar.Get(i);
                names.Add((i == 0 ? "Main taskbar" : "Taskbar on display " + (i + 1)) + "  (" + b.Monitor.Width + "×" + b.Monitor.Height + ")");
            }
            if (names.Count == 0) names.Add("Main taskbar");
            return names.ToArray();
        }

        Bar CurrentBar()
        {
            var bar = Bar.Get(S.Int("display", 0));
            if (bar.Hwnd != hostHwnd)
            {
                hostHwnd = bar.Hwnd;
                HookTaskbarMoves(bar.Hwnd);
                if (bar.Hwnd != IntPtr.Zero && !scans.ContainsKey(bar.Hwnd))
                {
                    var saved = LoadScan(S.Int("display", 0));
                    if (saved != null) scans[bar.Hwnd] = saved;
                }
            }
            if (IsHandleCreated) AttachToTaskbar(bar.Hwnd);
            uint dpi = bar.Hwnd != IntPtr.Zero ? Native.GetDpiForWindow(bar.Hwnd) : 96;
            K = (dpi == 0 ? 96 : dpi) / 96f;
            return bar;
        }

        public Rectangle TaskbarRect() { return Bar.Get(S.Int("display", 0)).Rect; }

        int WidgetHeight(Bar bar)
        {
            if (bar.Vertical) return Math.Max(24, (int)Math.Round(36 * K));
            int th = bar.Rect.Height; // 48 normal, ~32 with "smaller taskbar buttons"
            return Math.Max(18, th - 2 * Math.Max(3, (int)Math.Round(th * 0.125)));
        }

        int GapPx { get { return Math.Max(4, (int)Math.Round(6 * K)); } }

        // ---------------------------------------------------------------- free-space layout

        sealed class Gap { public int L, R; public int W { get { return R - L; } } }

        struct Placement { public Point At; public int Width, Avail, PrefAvail; public bool Fits, Preferred; }

        /// <summary>Taskbar buttons by taskbar, from UI Automation (kept while the taskbar is auto-hidden).</summary>
        readonly Dictionary<IntPtr, Uia.Scan> scans = new Dictionary<IntPtr, Uia.Scan>();
        bool scanBusy;

        Rectangle StartRect(Bar bar)
        {
            Uia.Scan sc;
            if (scans.TryGetValue(bar.Hwnd, out sc) && !sc.Start.IsEmpty) return sc.Start;
            IntPtr st = Native.FindWindowEx(bar.Hwnd, IntPtr.Zero, "Start", null); // legacy HWND that tracks the XAML Start button
            Native.RECT r;
            if (st != IntPtr.Zero && Native.GetWindowRect(st, out r) && r.Right > r.Left) return r.ToRectangle();
            return Rectangle.Empty;
        }

        int TrayLeft(Bar bar)
        {
            Uia.Scan sc;
            if (scans.TryGetValue(bar.Hwnd, out sc) && sc.Tray.Count > 0) return sc.Tray.Min(t => t.Left);
            IntPtr tray = Native.FindWindowEx(bar.Hwnd, IntPtr.Zero, "TrayNotifyWnd", null);
            Native.RECT r;
            if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out r) && r.Right > r.Left) return r.Left;
            return bar.Rect.Right - (int)(bar.Rect.Width * 0.12);
        }

        /// <summary>Horizontal free gaps on the taskbar: everything minus buttons, tray and lower-slot sibling widgets.</summary>
        List<Gap> FreeGaps(Bar bar)
        {
            var busy = new List<KeyValuePair<int, int>>();
            Uia.Scan sc;
            if (scans.TryGetValue(bar.Hwnd, out sc))
                foreach (var b in sc.Buttons) busy.Add(new KeyValuePair<int, int>(b.Left, b.Right));
            else
            {
                var st = StartRect(bar);
                if (!st.IsEmpty) busy.Add(new KeyValuePair<int, int>(st.Left, st.Right));
            }
            busy.Add(new KeyValuePair<int, int>(TrayLeft(bar), bar.Rect.Right));
            // sibling widgets (lower slots) are obstacles too: higher slots adapt to lower ones
            for (int s = 0; s < Slot; s++)
            {
                IntPtr sib = Native.FindWindow(null, WindowTitle(s));
                Native.RECT r;
                // compare with the taskbar strip, not the screen: with auto-hide both sit just off-screen
                var strip = Rectangle.Inflate(bar.Rect, 0, bar.Rect.Height);
                if (sib != IntPtr.Zero && Native.IsWindowVisible(sib) && Native.GetWindowRect(sib, out r) && r.ToRectangle().IntersectsWith(strip))
                    busy.Add(new KeyValuePair<int, int>(r.Left, r.Right));
            }
            busy.Sort((a, b) => a.Key.CompareTo(b.Key));
            var gaps = new List<Gap>();
            int x = bar.Rect.Left;
            foreach (var iv in busy)
            {
                if (iv.Key > x) gaps.Add(new Gap { L = x, R = iv.Key });
                x = Math.Max(x, iv.Value);
            }
            if (bar.Rect.Right > x) gaps.Add(new Gap { L = x, R = bar.Rect.Right });
            // slivers (e.g. the few pixels between a sibling widget and the tray) are not usable space
            int minGap = 2 * GapPx + (int)(16 * K);
            gaps.RemoveAll(g => g.W < minGap);
            return gaps;
        }

        /// <summary>Chooses where to go. measure(avail) returns the width the content needs when given avail pixels.</summary>
        Placement Place(Bar bar, int h, Func<int, int> measure)
        {
            int m = GapPx;
            if (bar.Vertical) return PlaceVertical(bar, h, measure(int.MaxValue));
            int y = bar.Rect.Top + (bar.Rect.Height - h) / 2;
            string anchor = AnchorKey;

            if (anchor == "custom")
            {
                int w = measure(int.MaxValue);
                int x = bar.Rect.Left + (int)(S.Int("customX", 200) * K);
                x = Math.Max(bar.Rect.Left, Math.Min(bar.Rect.Right - w, x));
                return new Placement { At = new Point(x, y), Width = w, Avail = int.MaxValue, Fits = true };
            }

            var gaps = FreeGaps(bar);
            bool fromRight = anchor != "left";
            Gap pref = null;
            if (anchor == "left") pref = gaps.FirstOrDefault();
            else
            {
                int edge;
                if (anchor == "start") { var st = StartRect(bar); edge = st.IsEmpty ? TrayLeft(bar) : st.Left; }
                else edge = TrayLeft(bar);
                pref = gaps.Where(g => g.R <= edge + 1).OrderByDescending(g => g.R).FirstOrDefault();
            }

            int prefAvail = pref == null ? 0 : Math.Max(0, pref.W - 2 * m);
            Func<Gap, int, Placement> make = (g, w) =>
            {
                int avail = g.W - 2 * m;
                int x = fromRight ? g.R - m - w : g.L + m;
                x = Math.Max(bar.Rect.Left, Math.Min(bar.Rect.Right - w, x));
                return new Placement { At = new Point(x, y), Width = w, Avail = avail, Fits = w <= avail, Preferred = g == pref, PrefAvail = prefAvail };
            };

            if (pref != null)
            {
                int w = measure(Math.Max(0, pref.W - 2 * m));
                if (w <= pref.W - 2 * m) return make(pref, w);
            }
            // not enough room there: the biggest other free gap (e.g. left of Start when the icons reach the tray)
            var big = gaps.Where(g => g != pref).OrderByDescending(g => g.W).FirstOrDefault();
            if (big != null)
            {
                int w = measure(Math.Max(0, big.W - 2 * m));
                if (w <= big.W - 2 * m) return make(big, w);
            }
            // nothing fits even in compact form: use the roomiest gap anyway
            var best = pref == null || (big != null && big.W > pref.W) ? big : pref;
            if (best == null) { int w = measure(int.MaxValue); return new Placement { At = new Point(bar.Rect.Right - w - m, y), Width = w, Avail = w }; }
            int bw = measure(Math.Max(0, best.W - 2 * m));
            return make(best, bw);
        }

        Placement PlaceVertical(Bar bar, int h, int w)
        {
            // Side taskbars (ExplorerPatcher, Windows 10 style): float next to the bar, near its bottom.
            int m = GapPx, stacked = 0;
            for (int s = 0; s < Slot; s++)
            {
                IntPtr sib = Native.FindWindow(null, WindowTitle(s));
                if (sib != IntPtr.Zero && Native.IsWindowVisible(sib)) stacked++;
            }
            int y = bar.Rect.Bottom - m - h - stacked * (h + m);
            y = Math.Max(bar.Monitor.Top, Math.Min(bar.Monitor.Bottom - h, y));
            int x;
            if (bar.Edge == Edge.Left) x = bar.Shown ? bar.Rect.Right + m : bar.Monitor.Left - w - 2;
            else x = bar.Shown ? bar.Rect.Left - m - w : bar.Monitor.Right + 2;
            return new Placement { At = new Point(x, y), Width = w, Avail = int.MaxValue, Fits = true };
        }

        // ---------------------------------------------------------------- render & move

        /// <summary>Repaints the bitmap. Call when content, theme or available space changes.</summary>
        public void Render()
        {
            if (!IsHandleCreated || IsDisposed) return;
            var bar = CurrentBar();
            int h = WidgetHeight(bar);
            if (!HasContent) { if (Visible) Hide(); return; }

            Placement pl;
            using (var mb = new Bitmap(1, 1))
            using (var mg = Graphics.FromImage(mb))
            {
                AvailWidth = int.MaxValue;
                lastNatural = Math.Max(1, MeasureWidth(mg, h));
                pl = Place(bar, h, avail =>
                {
                    if (avail >= lastNatural) return lastNatural;
                    AvailWidth = avail;
                    return Math.Max(1, MeasureWidth(mg, h));
                });
                AvailWidth = pl.Width >= lastNatural ? int.MaxValue : Math.Max(1, pl.Avail);
                lastPrefAvail = pl.PrefAvail;
            }
            int w = Math.Max(1, pl.Width);

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // alpha 1 everywhere: invisible, but the whole rectangle catches clicks
                    // (layered windows let clicks fall through fully transparent pixels)
                    g.Clear(Color.FromArgb(1, 0, 0, 0));
                    PaintWidget(g, new Rectangle(0, 0, w, h));
                }
                lastSize = new Size(w, h);
                if (Environment.GetEnvironmentVariable("TW_DEBUG") == "1")
                    try { Directory.CreateDirectory(Log.Dir); bmp.Save(Path.Combine(Log.Dir, "debug-" + AppId + ".png")); } catch { }

                Point at = pl.At;
                Native.RECT cur;
                bool have = Native.GetWindowRect(Handle, out cur) && Visible;
                if (down && have) at = new Point(cur.Left, cur.Top);             // never move under a pressed mouse
                else if (sliding) { slideTo = pl.At; at = slidePos; }
                else if (have && ShouldSlide(new Point(cur.Left, cur.Top), pl.At)) { StartSlide(new Point(cur.Left, cur.Top), pl.At); at = slidePos; }
                Push(bmp, at);
            }
            if (!Visible && !fullHidden) Show();
            CheckZOrder();
        }

        void Push(Bitmap bmp, Point at)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen);
            IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), old = Native.SelectObject(mem, hb);
            try
            {
                var sz = new Native.SIZE(bmp.Width, bmp.Height);
                var src = new Native.POINT(0, 0);
                var dst = new Native.POINT(at.X, at.Y);
                var bf = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = constAlpha, AlphaFormat = Native.AC_SRC_ALPHA };
                if (!Native.UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref bf, Native.ULW_ALPHA))
                    Log.Write(AppId, new Exception("UpdateLayeredWindow failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            }
            finally
            {
                Native.SelectObject(mem, old); Native.DeleteObject(hb); Native.DeleteDC(mem); Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        void SetConstAlpha(byte a)
        {
            if (constAlpha == a) return;
            constAlpha = a;
            Render();
        }

        /// <summary>Moves without repainting when possible (taskbar sliding, icons re-centering...).</summary>
        public void Reposition()
        {
            if (!IsHandleCreated || lastSize.IsEmpty || dragging || down) return;
            var bar = CurrentBar();
            if (WidgetHeight(bar) != lastSize.Height) { Render(); return; }
            int width = lastSize.Width;
            var pl = Place(bar, lastSize.Height, avail => avail >= width ? width : int.MaxValue);
            // Re-measure (shrink or grow) instead of just moving when: we no longer fit, more room opened up
            // while we are shrunk, or the preferred spot changed size (staying there shrunk beats jumping away).
            if (!pl.Fits || pl.Width != width || (lastNatural > width && pl.Avail > width + 4 * K) ||
                (!pl.Preferred && pl.PrefAvail != lastPrefAvail)) { Render(); return; }
            Native.RECT cur;
            if (!Native.GetWindowRect(Handle, out cur)) return;
            var from = new Point(cur.Left, cur.Top);
            if (sliding) { slideTo = pl.At; return; }
            if (from == pl.At) return;
            if (Visible && ShouldSlide(from, pl.At)) StartSlide(from, pl.At);
            else MoveTo(pl.At.X, pl.At.Y);
        }

        void MoveTo(int x, int y)
        {
            Native.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 4 /*NOZORDER*/);
        }

        // ---------------------------------------------------------------- slide animation

        readonly Timer slideTimer = new Timer { Interval = 10 };
        bool sliding; Point slideFrom, slideTo, slidePos; DateTime slideStart;
        const double SlideMs = 170;

        bool ShouldSlide(Point from, Point to)
        {
            if (!S.Bool("slide", true) || from == to) return false;
            int d = Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y);
            return d < 700 * K && from.X > -20000;
        }

        void StartSlide(Point from, Point to)
        {
            slideFrom = from; slideTo = to; slidePos = from; slideStart = DateTime.Now;
            if (!sliding) { sliding = true; slideTimer.Start(); }
        }

        void SlideStep()
        {
            if (down) return;
            double t = Math.Min(1, (DateTime.Now - slideStart).TotalMilliseconds / SlideMs);
            double e = 1 - Math.Pow(1 - t, 3); // ease-out cubic
            slidePos = new Point((int)Math.Round(slideFrom.X + (slideTo.X - slideFrom.X) * e), (int)Math.Round(slideFrom.Y + (slideTo.Y - slideFrom.Y) * e));
            MoveTo(slidePos.X, slidePos.Y);
            if (t >= 1) { sliding = false; slideTimer.Stop(); if (slidePos != slideTo) MoveTo(slideTo.X, slideTo.Y); }
        }

        // ---------------------------------------------------------------- follow the taskbar the moment it moves

        Native.WinEventDelegate locProc;
        IntPtr locHook;
        bool barWasShown;

        void HookTaskbarMoves(IntPtr tb)
        {
            if (locHook != IntPtr.Zero) { Native.UnhookWinEvent(locHook); locHook = IntPtr.Zero; }
            uint pid;
            if (tb == IntPtr.Zero || Native.GetWindowThreadProcessId(tb, out pid) == 0) return;
            if (locProc == null)
                locProc = (h, ev, hwnd, idObject, idChild, thread, time) =>
                {
                    // cheap filter first: only the taskbar window itself (OBJID_WINDOW = 0)
                    if (hwnd != hostHwnd || idObject != 0 || idChild != 0 || IsDisposed) return;
                    Reposition();
                    var bar = Bar.Get(S.Int("display", 0));
                    bool shown = bar.Shown;
                    if (shown && !barWasShown) { scanSoon.Stop(); scanSoon.Interval = 250; scanSoon.Start(); }
                    barWasShown = shown;
                };
            locHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, locProc,
                                             pid, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        }

        /// <summary>Reads the taskbar's buttons off the UI thread (cross-process UI Automation can take a few ms).</summary>
        void RequestScan()
        {
            scanSoon.Interval = 350;
            if (scanBusy || !IsHandleCreated || IsDisposed) return;
            var bar = Bar.Get(S.Int("display", 0));
            if (bar.Vertical || !bar.Shown || bar.Hwnd == IntPtr.Zero) return; // hidden XAML is not readable; keep the last scan
            scanBusy = true;
            IntPtr h = bar.Hwnd;
            Task.Run(() => { try { return Uia.ScanTaskbar(h); } catch { return new Uia.Scan(); } })
                .ContinueWith(t =>
                {
                    scanBusy = false;
                    if (IsDisposed || !t.Result.Ok) return;
                    Uia.Scan old;
                    bool changed = !scans.TryGetValue(h, out old) || !Same(old, t.Result);
                    scans[h] = t.Result;
                    if (changed) { SaveScan(S.Int("display", 0), t.Result); Reposition(); }
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // The last scanned layout is remembered so an auto-hidden taskbar is laid out right on its first reveal.
        static string ScanFile(int display) { return Path.Combine(Log.Dir, "taskbar-layout-" + display + ".txt"); }

        static void SaveScan(int display, Uia.Scan sc)
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                var lines = new List<string>();
                Func<string, Rectangle, string> f = (k, r) => k + " " + r.X + " " + r.Y + " " + r.Width + " " + r.Height;
                if (!sc.Start.IsEmpty) lines.Add(f("S", sc.Start));
                foreach (var b in sc.Buttons) lines.Add(f("B", b));
                foreach (var t in sc.Tray) lines.Add(f("T", t));
                File.WriteAllLines(ScanFile(display), lines.ToArray());
            }
            catch { }
        }

        static Uia.Scan LoadScan(int display)
        {
            try
            {
                string p = ScanFile(display);
                if (!File.Exists(p)) return null;
                var sc = new Uia.Scan();
                foreach (var line in File.ReadAllLines(p))
                {
                    var a = line.Split(' ');
                    int x, y, w, h;
                    if (a.Length != 5 || !int.TryParse(a[1], out x) || !int.TryParse(a[2], out y) || !int.TryParse(a[3], out w) || !int.TryParse(a[4], out h)) continue;
                    var r = new Rectangle(x, y, w, h);
                    if (a[0] == "S") sc.Start = r; else if (a[0] == "B") sc.Buttons.Add(r); else if (a[0] == "T") sc.Tray.Add(r);
                }
                sc.Ok = sc.Buttons.Count > 0;
                return sc.Ok ? sc : null;
            }
            catch { return null; }
        }

        static bool Same(Uia.Scan a, Uia.Scan b)
        {
            if (a.Buttons.Count != b.Buttons.Count || a.Start != b.Start) return false;
            for (int i = 0; i < a.Buttons.Count; i++)
                if (a.Buttons[i].Left != b.Buttons[i].Left || a.Buttons[i].Width != b.Buttons[i].Width) return false;
            return true;
        }

        // ---------------------------------------------------------------- z-order & visibility

        void OnTick()
        {
            ticks++;
            if (ticks % 5 == 0) { CheckZOrder(); RequestScan(); }
            if (ticks % 3 == 0) Reposition();

            if (S.Bool("hoverHide", false) && Visible)
            {
                Native.POINT p; Native.GetCursorPos(out p);
                bool inside = Bounds.Contains(p.X, p.Y);
                if (inside != hoverHidden)
                {
                    hoverHidden = inside;
                    ApplyInteractive();
                    SetConstAlpha(inside ? (byte)0 : (byte)255);
                }
            }
            else if (hoverHidden) { hoverHidden = false; ApplyInteractive(); SetConstAlpha(255); }

            // Give memory back only after a long idle period: trimming right after use makes the next
            // click page everything back in, which feels like a short freeze.
            if (!trimmed && (DateTime.Now - lastInput).TotalSeconds > 120 && (Panel == null || !Panel.Visible))
            {
                trimmed = true;
                Native.TrimMemory();
            }
        }

        public void ApplyInteractive()
        {
            if (!IsHandleCreated) return;
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            bool through = hoverHidden || !S.Bool("interactive", true);
            int nex = through ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
            if (nex != ex) Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, nex);
        }

        void CheckZOrder()
        {
            if (!IsHandleCreated || IsDisposed) return;
            bool full = S.Bool("hideFullscreen", true) && ForegroundIsFullscreen();
            if (full != fullHidden)
            {
                fullHidden = full;
                if (full) Hide(); else if (HasContent) Show();
            }
            AttachToTaskbar(hostHwnd); // WinForms re-owns the window on Show(); take it back
            if (!fullHidden && Visible)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        bool ForegroundIsFullscreen()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle || fg == Native.GetShellWindow()) return false;
            if (Panel != null && Panel.IsHandleCreated && fg == Panel.Handle) return false;
            string cls = Native.ClassOf(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls.StartsWith("Windows.UI.Core")) return false;
            // Exclusive D3D games / presentation mode, as Windows itself reports them
            int quns;
            if (Native.SHQueryUserNotificationState(out quns) == 0 && (quns == 3 || quns == 4)) return true;
            // A maximized window covers the whole monitor when the taskbar auto-hides, but it is not full screen
            if (Native.IsZoomed(fg)) return false;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(fg, 2), ref mi)) return false;
            var m = mi.rcMonitor;
            // Only hide when the full-screen window is on our taskbar's monitor.
            if (m.ToRectangle() != Bar.Get(S.Int("display", 0)).Monitor) return false;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }

        // ---------------------------------------------------------------- mouse

        bool down, dragging;
        Point downScreen, downLocal; int dragGrabX;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Touch();
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            {
                down = true; dragging = false;
                downScreen = Cursor.Position; downLocal = e.Location; dragGrabX = e.X;
                Pressed = true; Render();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Hovering) { Hovering = true; Touch(); Render(); }
            if (down && !dragging && Control.MouseButtons == MouseButtons.Left)
            {
                // screen-space threshold so a tiny wobble while clicking is still a click
                var p = Cursor.Position;
                int thr = Math.Max(SystemInformation.DragSize.Width * 2, (int)(10 * K));
                if (Math.Abs(p.X - downScreen.X) > thr || Math.Abs(p.Y - downScreen.Y) > thr * 2) dragging = true;
            }
            if (dragging)
            {
                var bar = Bar.Get(S.Int("display", 0));
                int x = Math.Max(bar.Rect.Left, Math.Min(bar.Rect.Right - Width, Cursor.Position.X - dragGrabX));
                MoveTo(x, Top);
                return;
            }
            if (Mouse != e.Location) { Mouse = e.Location; OnHoverMove(); }
        }

        protected virtual void OnHoverMove() { }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Mouse = new Point(-1, -1);
            bool redraw = Hovering || Pressed;
            Hovering = false;
            if (!down) Pressed = false;
            OnHoverMove();
            if (redraw) Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Touch();
            bool wasDown = down;
            down = false;
            if (Pressed) { Pressed = false; Render(); }
            if (dragging)
            {
                dragging = false;
                var bar = Bar.Get(S.Int("display", 0));
                S.Set("customX", (int)((Left - bar.Rect.Left) / K));
                AnchorIndex = 3;
                return;
            }
            if (e.Button == MouseButtons.Right) { ShowWidgetMenu(Cursor.Position); return; }
            // act on where the button went down (content may have shifted by a pixel since)
            if (wasDown) OnWidgetClick(downLocal, e.Button);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0;
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            if (m.Msg == WM_TASKBARCREATED && WM_TASKBARCREATED != 0)
            {
                // Explorer restarted: new taskbar windows, re-attach and re-add the tray icon
                scans.Clear(); hostHwnd = IntPtr.Zero;
                if (Tray != null && Tray.Visible) { Tray.Visible = false; Tray.Visible = true; }
                BeginInvoke(new Action(() => { Render(); RequestScan(); }));
            }
            if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED) BeginInvoke(new Action(Render));
            if (m.Msg == WM_MOUSEWHEEL)
            {
                Touch();
                int delta = (short)((long)m.WParam >> 16);
                OnWidgetWheel(delta);
                return;
            }
            base.WndProc(ref m);
        }

        // ---------------------------------------------------------------- menus, panel, tray

        protected virtual void AddMenuItems(ContextMenuStrip m) { }

        public void ShowWidgetMenu(Point screen)
        {
            var m = Menus.Create(T);
            m.Items.Add("Open panel", null, (o, e) => OpenPanel(-1));
            AddMenuItems(m);
            m.Items.Add(new ToolStripSeparator());
            var pos = new ToolStripMenuItem("Position");
            for (int i = 0; i < AnchorNames.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(AnchorNames[i]) { Checked = AnchorIndex == i };
                it.Click += (o, e) => { AnchorIndex = idx; };
                pos.DropDownItems.Add(it);
            }
            Menus.Style(pos.DropDown, T);
            m.Items.Add(pos);
            m.Items.Add("Settings", null, (o, e) => OpenPanel(PanelForm.GeneralTabIndex));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Exit", null, (o, e) => Application.Exit());
            m.Closed += (o, e) => BeginInvoke(new Action(() => m.Dispose()));
            m.Show(screen);
        }

        public void OpenPanel(int tab)
        {
            if (Panel == null) return;
            Touch();
            if (Panel.Visible && tab < 0) { Panel.HideNow(); return; }
            if (Panel.JustHidden && tab < 0) return;
            Panel.ShowNear(Bounds, tab);
        }

        public void SetupTray(string tip, Glyph glyph)
        {
            Tray = new NotifyIcon { Text = tip, Visible = S.Bool("trayIcon", true) };
            using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Gfx.Fill(g, new RectangleF(1, 1, 30, 30), 8, T.Accent);
                    Gfx.Icon(g, glyph, new RectangleF(6, 6, 20, 20), Color.White, 1);
                }
                Tray.Icon = Icon.FromHandle(bmp.GetHicon());
            }
            Tray.BalloonTipClicked += (o, e) => { var a = balloonClick; balloonClick = null; if (a != null) a(); };
            Tray.MouseUp += (o, e) =>
            {
                if (e.Button == MouseButtons.Left) OpenPanel(-1);
                else ShowWidgetMenu(Cursor.Position);
            };
        }

        Action balloonClick;

        public void Notify(string title, string text, Action onClick = null)
        {
            if (Tray == null) return;
            balloonClick = onClick;
            bool wasVisible = Tray.Visible;
            Tray.Visible = true;
            Tray.ShowBalloonTip(5000, title, text, ToolTipIcon.None);
            if (!wasVisible)
            {
                var t = new Timer { Interval = 8000 };
                t.Tick += (o, e) => { t.Dispose(); if (!S.Bool("trayIcon", true)) Tray.Visible = false; };
                t.Start();
            }
        }

        // ---------------------------------------------------------------- painting helpers

        public Font WidgetFont(int height, bool bold)
        {
            float px = Math.Max(9f, height * 0.36f) * S.Int("textScale", 100) / 100f;
            return new Font("Segoe UI Variable Text", px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }

        /// <summary>Background for the 4 widget styles: 0 standard, 1 frameless, 2 capsules, 3 neon. Includes hover/press feedback.</summary>
        public void PaintBackground(Graphics g, RectangleF r, int style)
        {
            int alpha = (int)(255 * S.Int("opacity", 95) / 100f);
            float rad = r.Height * 0.28f;
            var inner = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            Color bg = T.Dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(246, 246, 248);
            if (Hovering) bg = Gfx.Mix(bg, T.Text, Pressed ? 0.04f : 0.08f);
            switch (style)
            {
                case 1: // frameless: only a soft highlight on hover
                    if (Hovering) Gfx.Fill(g, inner, rad, Gfx.Alpha(T.Text, Pressed ? 18 : 28));
                    break;
                case 3: // neon
                    Gfx.Fill(g, inner, rad, Color.FromArgb(alpha, Hovering ? Color.FromArgb(24, 24, 30) : Color.FromArgb(12, 12, 16)));
                    for (int i = 3; i >= 1; i--)
                        Gfx.Stroke(g, new RectangleF(inner.X + i, inner.Y + i, inner.Width - 2 * i, inner.Height - 2 * i), rad - i, Gfx.Alpha(T.Accent, 50 / i), i * 1.6f);
                    Gfx.Stroke(g, inner, rad, Gfx.Alpha(T.Accent, 240), Math.Max(1f, K));
                    break;
                case 2: // capsules draw their own pills
                    break;
                default:
                    Gfx.Fill(g, inner, rad, Color.FromArgb(Math.Max(alpha, Hovering ? 200 : 0), bg));
                    Gfx.Stroke(g, inner, rad, Gfx.Alpha(T.Accent, Hovering ? 255 : Math.Min(alpha, 200)), Math.Max(1f, K));
                    break;
            }
        }

        public Color WidgetText { get { return S.Int("style", 0) == 3 ? Color.White : T.Text; } }
    }

    internal static class Menus
    {
        public static ContextMenuStrip Create(Theme t)
        {
            var m = new ContextMenuStrip();
            Style(m, t);
            return m;
        }

        public static void Style(ToolStripDropDown m, Theme t)
        {
            m.Renderer = new MenuRenderer(t);
            m.Font = new Font("Segoe UI", 9.5f);
            m.ForeColor = t.Text;
            m.BackColor = t.Card;
            var cm = m as ToolStripDropDownMenu;
            if (cm != null) { cm.ShowImageMargin = false; cm.ShowCheckMargin = true; }
        }

        sealed class MenuRenderer : ToolStripProfessionalRenderer
        {
            readonly Theme t;
            public MenuRenderer(Theme t) : base(new Colors(t)) { this.t = t; RoundedEdges = false; }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? t.Text : t.Faint;
                base.OnRenderItemText(e);
            }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = t.Sub; base.OnRenderArrow(e); }
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = e.ImageRectangle;
                using (var p = new Pen(t.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(p, new[] { new PointF(r.X + r.Width * 0.2f, r.Y + r.Height * 0.52f), new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.74f), new PointF(r.X + r.Width * 0.82f, r.Y + r.Height * 0.28f) });
            }
        }

        sealed class Colors : ProfessionalColorTable
        {
            readonly Theme t;
            public Colors(Theme t) { this.t = t; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return t.Card; } }
            public override Color ImageMarginGradientBegin { get { return t.Card; } }
            public override Color ImageMarginGradientMiddle { get { return t.Card; } }
            public override Color ImageMarginGradientEnd { get { return t.Card; } }
            public override Color MenuBorder { get { return t.Border; } }
            public override Color MenuItemBorder { get { return t.CardHover; } }
            public override Color MenuItemSelected { get { return t.CardHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return t.CardHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return t.CardHover; } }
            public override Color SeparatorDark { get { return t.Border; } }
            public override Color SeparatorLight { get { return t.Border; } }
            public override Color CheckBackground { get { return Color.Transparent; } }
            public override Color CheckSelectedBackground { get { return Color.Transparent; } }
            public override Color CheckPressedBackground { get { return Color.Transparent; } }
        }
    }

    internal static class Companion
    {
        static readonly Dictionary<string, KeyValuePair<DateTime, bool>> runningCache = new Dictionary<string, KeyValuePair<DateTime, bool>>();

        public static string ExePath(string exeName)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            return Path.Combine(dir, exeName);
        }

        /// <summary>Cached for 3 s: the panel repaints on every hover, and listing processes is slow.</summary>
        public static bool Running(string exeName)
        {
            KeyValuePair<DateTime, bool> c;
            if (runningCache.TryGetValue(exeName, out c) && (DateTime.Now - c.Key).TotalSeconds < 3) return c.Value;
            var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName));
            bool r = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            runningCache[exeName] = new KeyValuePair<DateTime, bool>(DateTime.Now, r);
            return r;
        }

        public static void Launch(string exeName)
        {
            string p = ExePath(exeName);
            if (File.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
            runningCache.Remove(exeName);
        }
    }
}
