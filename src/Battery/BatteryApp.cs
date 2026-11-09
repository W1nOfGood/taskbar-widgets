using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarWidgets.Battery
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Updater.WaitForPrevious(Environment.GetCommandLineArgs()); // after an update: let the old instance exit first
            bool fresh;
            using (var mutex = new Mutex(true, "TaskbarWidgets.Battery.SingleInstance", out fresh))
            {
                if (!fresh) return;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (o, e) => Log.Write("battery", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (o, e) => Log.Write("battery", e.ExceptionObject as Exception);
                Application.Run(new BatteryWidget());
            }
        }
    }

    internal sealed class BatteryWidget : TaskbarWidget
    {
        public readonly DeviceManager Devices;
        readonly System.Windows.Forms.Timer fx = new System.Windows.Forms.Timer { Interval = 120 };
        string effectKey; DateTime effectEnd;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern bool PlaySound(string name, IntPtr mod, int flags);

        public BatteryWidget() : base(new Settings("battery"), "battery", 0)
        {
            Devices = new DeviceManager(this, S);
            Devices.Changed += () => { Render(); CheckAlerts(); if (Panel != null && Panel.Visible) Panel.Invalidate(); };
            Devices.Connected += d => OnConnection(d, true);
            Devices.Disconnected += d => OnConnection(d, false);
            Panel = new BatteryPanel(this);
            SetupTray("Battery widget", Glyph.Headphones);
            fx.Tick += (o, e) => { if (DateTime.Now > effectEnd) { fx.Stop(); effectKey = null; } Render(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Devices.Start();
        }

        public int LowThreshold { get { return S.Int("lowThreshold", 20); } }

        public List<BatteryDevice> OnTaskbar
        {
            get { return Devices.All.Where(d => d.Connected && !S.Bool("hide." + d.Key, false)).ToList(); }
        }

        protected override bool HasContent { get { return OnTaskbar.Count > 0 || S.Int("whenEmpty", 0) == 0; } }

        // ---------------------------------------------------------------- events

        readonly Dictionary<string, DateTime> lastChange = new Dictionary<string, DateTime>();

        /// <summary>
        /// Connect/disconnect feedback, kept quiet on purpose: wireless dongle devices (Razer, Logitech...) "disconnect"
        /// every time they fall asleep, and flaky links can flap; neither should produce popups or sounds.
        /// </summary>
        void OnConnection(BatteryDevice d, bool on)
        {
            if (S.Bool("hide." + d.Key, false)) return;
            DateTime last;
            bool flapping = lastChange.TryGetValue(d.Key, out last) && (DateTime.Now - last).TotalMinutes < 3;
            lastChange[d.Key] = DateTime.Now;
            bool sleepy = d.Via != null && d.Via.StartsWith("2.4 GHz");   // dongle devices sleep/wake constantly
            if (on && S.Int("effect", 0) != 2)
            {
                effectKey = d.Key; effectEnd = DateTime.Now.AddSeconds(2.6);
                fx.Start();
            }
            if (sleepy || flapping) return;
            if (S.Bool("notifyConnect", false))
                Notify(d.Name + (on ? " connected" : " disconnected"),
                       on ? (d.Level >= 0 ? "Battery " + d.Level + "%" : "Connected via " + d.Via) : (d.LastLevel >= 0 ? "Last level " + d.LastLevel + "%" : d.Via));
            if (S.Bool("sound", true)) PlaySound(on ? "DeviceConnect" : "DeviceDisconnect", IntPtr.Zero, 0x10000 | 0x1 /*ALIAS|ASYNC*/);
        }

        /// <summary>
        /// One low-battery alert per discharge: it is remembered in the settings file (survives sleep/wake,
        /// reconnects and app restarts) and only re-armed once the device has been charged back up.
        /// </summary>
        void CheckAlerts()
        {
            int thr = LowThreshold;
            foreach (var d in Devices.All)
            {
                if (!d.Connected || d.Level < 0) continue;
                string lowKey = "alerted.low." + d.Key, fullKey = "alerted.full." + d.Key;
                if (d.Level <= thr)
                {
                    if (!S.Bool(lowKey, false))
                    {
                        S.Set(lowKey, true);
                        string left = Devices.TimeLeft(d);
                        if (left != null && left.StartsWith("~")) left = " (" + left.Replace(" (from past use)", "") + ")"; else left = "";
                        if (S.Bool("notify", true)) Notify(d.Name + " battery is low", d.Level + "% remaining" + left + ". Charge it soon.");
                    }
                }
                else if (d.Level >= thr + 10 && S.Bool(lowKey, false)) S.Set(lowKey, false); // charged: re-arm

                bool plugged = d.Charging || d.Key == "sys" && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
                if (S.Bool("fullAlert", true) && d.Level >= 100 && plugged)
                {
                    if (!S.Bool(fullKey, false))
                    {
                        S.Set(fullKey, true);
                        Notify(d.Name + " is fully charged", "Unplug the cable to protect the battery.");
                    }
                }
                else if (d.Level < 90 && S.Bool(fullKey, false)) S.Set(fullKey, false);
            }
        }

        // ---------------------------------------------------------------- widget painting

        sealed class Item { public BatteryDevice D; public int W; }

        int Elements(int count)
        {
            int e = S.Int("elements", 0);
            if (e == 0) return count <= 3 ? 1 : 3;
            return e;
        }

        bool Grid(int count)
        {
            int l = S.Int("layout", 0);
            return l == 2 ? count > 1 : l == 0 && count >= 4;
        }

        float IconScale { get { int v = S.Int("iconSize", 0); return v == 0 ? 1f : v / 100f; } }

        int ItemWidth(Graphics g, BatteryDevice d, int ih, int elements, Font f)
        {
            bool vertical = S.Int("orientation", 0) == 1;
            int gap = (int)(ih * 0.2f), w = 0;
            if (elements != 2) w += (int)(ih * 0.5f * IconScale) + gap;                       // icon
            if (elements == 1 || elements == 2 || elements == 4) w += (int)(vertical ? ih * 0.28f : ih * 0.66f) + gap; // battery
            if (elements <= 3) w += (int)Math.Ceiling(g.MeasureString(Pct(d), f, PointF.Empty, StringFormat.GenericTypographic).Width) + 1; // text
            else w -= gap;
            return Math.Max(w, (int)(ih * 0.5f));
        }

        static string Pct(BatteryDevice d) { return d.Level >= 0 ? d.Level + "%" : "–"; }

        /// <summary>
        /// Picks the richest look that fits the free space (AvailWidth): the user's choice first,
        /// then icon + %, then icon only (colored by level), then the same in two rows.
        /// </summary>
        void Choose(Graphics g, int h, List<BatteryDevice> vis, out int el, out bool grid)
        {
            int n = vis.Count;
            var options = new List<KeyValuePair<int, bool>> { new KeyValuePair<int, bool>(Elements(n), Grid(n)) };
            options.Add(new KeyValuePair<int, bool>(3, Grid(n)));
            options.Add(new KeyValuePair<int, bool>(5, Grid(n)));
            if (n > 1) { options.Add(new KeyValuePair<int, bool>(3, true)); options.Add(new KeyValuePair<int, bool>(5, true)); }
            foreach (var o in options)
            {
                el = o.Key; grid = o.Value;
                if (AvailWidth == int.MaxValue || WidthFor(g, h, vis, el, grid) <= AvailWidth) return;
            }
            el = options[options.Count - 1].Key; grid = options[options.Count - 1].Value;
        }

        protected override int MeasureWidth(Graphics g, int h)
        {
            var vis = OnTaskbar;
            if (vis.Count == 0) return h + (int)(h * 0.3f);
            int el; bool grid;
            Choose(g, h, vis, out el, out grid);
            return WidthFor(g, h, vis, el, grid);
        }

        int WidthFor(Graphics g, int h, List<BatteryDevice> vis, int el, bool grid)
        {
            int style = S.Int("style", 0);
            int pad = (int)(h * 0.3f);
            int ih = grid ? h / 2 : h;
            int sep = style == 2 ? (int)(4 * K) : (int)(h * 0.36f);
            using (var f = WidgetFont(ih, false))
            {
                var widths = vis.Select(d => ItemWidth(g, d, ih, el, f) + (style == 2 ? pad : 0)).ToList();
                if (!grid) return pad * 2 + widths.Sum() + sep * (widths.Count - 1);
                int cols = (vis.Count + 1) / 2, total = 0;
                for (int c = 0; c < cols; c++)
                    total += Math.Max(widths[c * 2], c * 2 + 1 < widths.Count ? widths[c * 2 + 1] : 0);
                return pad * 2 + total + sep * (cols - 1);
            }
        }

        protected override void PaintWidget(Graphics g, Rectangle r)
        {
            int style = S.Int("style", 0);
            PaintBackground(g, r, style);
            var vis = OnTaskbar;
            int pad = (int)(r.Height * 0.3f);
            if (vis.Count == 0)
            {
                var br = new RectangleF(r.X + pad, r.Y + r.Height * 0.36f, r.Height * 0.6f, r.Height * 0.28f);
                if (style == 2) Gfx.Fill(g, r, r.Height / 2f, Color.FromArgb((int)(2.55 * S.Int("opacity", 95)), T.Dark ? Color.FromArgb(28, 28, 30) : Color.White));
                Gfx.Battery(g, br, -1, T.Faint, T.Faint, false, false);
                return;
            }
            int el; bool grid;
            Choose(g, r.Height, vis, out el, out grid);
            int ih = grid ? r.Height / 2 : r.Height;
            int sep = style == 2 ? (int)(4 * K) : (int)(r.Height * 0.36f);
            using (var f = WidgetFont(ih, false))
            {
                var items = vis.Select(d => new Item { D = d, W = ItemWidth(g, d, ih, el, f) }).ToList();
                float x = r.X + pad;
                int cols = grid ? (items.Count + 1) / 2 : items.Count;
                for (int c = 0; c < cols; c++)
                {
                    int colW = 0;
                    for (int row = 0; row < (grid ? 2 : 1); row++)
                    {
                        int i = grid ? c * 2 + row : c;
                        if (i >= items.Count) break;
                        var it = items[i];
                        int cw = it.W + (style == 2 ? pad : 0);
                        colW = Math.Max(colW, cw);
                        float y = r.Y + row * ih;
                        if (style == 2)
                        {
                            var pill = new RectangleF(x - pad / 2f + (grid ? 0 : 0), y + ih * 0.08f, cw, ih * 0.84f);
                            if (!grid) pill = new RectangleF(x - pad / 2f, r.Y + 1, cw, r.Height - 2);
                            Gfx.Fill(g, pill, pill.Height / 2f, Color.FromArgb((int)(2.55 * S.Int("opacity", 95)), T.Dark ? Color.FromArgb(28, 28, 30) : Color.White));
                            Gfx.Stroke(g, pill, pill.Height / 2f, Gfx.Alpha(T.Accent, 160), Math.Max(1f, K));
                        }
                        PaintItem(g, it.D, x + (style == 2 ? 0 : 0), y, ih, el, f, Alpha(it.D));
                    }
                    if (c < cols - 1 && style == 0 && !grid)
                    {
                        float sx = x + colW + sep / 2f;
                        using (var p = new Pen(Gfx.Alpha(T.Sub, 90), Math.Max(1f, K))) g.DrawLine(p, sx, r.Y + r.Height * 0.28f, sx, r.Bottom - r.Height * 0.28f);
                    }
                    x += colW + sep;
                }
            }
        }

        int Alpha(BatteryDevice d)
        {
            if (effectKey != d.Key) return 255;
            double t = (effectEnd - DateTime.Now).TotalMilliseconds;
            if (S.Int("effect", 0) == 0) return ((int)(t / 240)) % 2 == 0 ? 255 : 40;   // blink
            return (int)(150 + 105 * Math.Cos(t / 160.0));                              // pulse
        }

        void PaintItem(Graphics g, BatteryDevice d, float x, float y, int ih, int el, Font f, int alpha)
        {
            bool vertical = S.Int("orientation", 0) == 1;
            int gap = (int)(ih * 0.2f);
            Color lvl = T.LevelColor(d.Level, LowThreshold);
            int istyle = S.Int("iconStyle", 0);
            if (el != 2)
            {
                float isz = ih * 0.5f * IconScale;
                Color ic = el == 5 ? lvl : (S.Int("style", 0) == 3 ? T.Accent : Gfx.Mix(T.Accent, T.Text, T.Dark ? 0.15f : 0f));
                Gfx.Icon(g, d.Kind, new RectangleF(x, y + (ih - isz) / 2, isz, isz), Gfx.Alpha(ic, alpha), istyle);
                x += isz + gap;
            }
            if (el == 1 || el == 2 || el == 4)
            {
                RectangleF br = vertical
                    ? new RectangleF(x, y + ih * 0.24f, ih * 0.28f, ih * 0.52f)
                    : new RectangleF(x, y + ih * 0.34f, ih * 0.66f, ih * 0.32f);
                Color outline = Gfx.Alpha(d.Level >= 0 && d.Level <= LowThreshold ? lvl : WidgetText, alpha * 3 / 4);
                Gfx.Battery(g, br, d.Level, Gfx.Alpha(lvl, alpha), outline, vertical, d.Charging);
                x += br.Width + gap;
            }
            if (el <= 3)
            {
                Color tc = d.Level >= 0 && d.Level <= LowThreshold ? T.Low : WidgetText;
                float th = f.GetHeight(g);
                using (var b = new SolidBrush(Gfx.Alpha(tc, alpha)))
                    g.DrawString(Pct(d), f, b, x, y + (ih - th) / 2f - 0.5f, StringFormat.GenericTypographic);
            }
        }

        protected override void OnWidgetClick(Point p, MouseButtons b) { tip.Hide(this); tipOn = false; OpenPanel(-1); }

        // hover tooltip: every device with its level and time left
        readonly ToolTip tip = new ToolTip { InitialDelay = 450, ShowAlways = true };
        bool tipOn;

        protected override void OnHoverMove()
        {
            bool over = Mouse.X >= 0;
            if (over && !tipOn) { tipOn = true; tip.SetToolTip(this, TipText()); }
            else if (!over && tipOn) { tipOn = false; tip.Hide(this); }
        }

        string TipText()
        {
            var lines = new List<string>();
            foreach (var d in OnTaskbar)
            {
                string left = Devices.TimeLeft(d);
                lines.Add(d.Name + ":  " + (d.Level >= 0 ? d.Level + "%" : "–") + (left != null ? "  ·  " + left : ""));
            }
            if (lines.Count == 0) lines.Add("No battery devices connected");
            lines.Add("Click for details");
            return string.Join("\n", lines);
        }

        protected override void AddMenuItems(ContextMenuStrip m)
        {
            m.Items.Add("Refresh battery levels", null, (o, e) => Devices.RefreshLevels());
            m.Items.Add("Bluetooth settings", null, (o, e) => { try { System.Diagnostics.Process.Start("ms-settings:bluetooth"); } catch { } });
        }
    }

    internal sealed class BatteryPanel : PanelForm
    {
        readonly BatteryWidget bw;
        bool showDisconnected = true, showHelp;

        public BatteryPanel(BatteryWidget w) : base(w, new[] { "Devices", "Alerts", "General", "Design" }) { bw = w; }

        protected override string HeaderTitle { get { return "Battery widget"; } }
        protected override string HeaderSub
        {
            get
            {
                int n = bw.Devices.All.Count(d => d.Connected);
                return n == 0 ? "No devices connected" : n == 1 ? "1 device connected" : n + " devices connected";
            }
        }
        protected override Glyph HeaderGlyph { get { return Glyph.Headphones; } }
        protected override void OnRefresh() { bw.Devices.RefreshLevels(); }

        protected override void BuildTab(Ui ui)
        {
            switch (Tab)
            {
                case 0: DevicesTab(ui); break;
                case 1: AlertsTab(ui); break;
                case 2: GeneralTab(ui); break;
                default: DesignTab(ui); break;
            }
        }

        void DevicesTab(Ui ui)
        {
            var all = bw.Devices.All;
            var on = all.Where(d => d.Connected).ToList();
            var off = all.Where(d => !d.Connected).ToList();
            ui.Section("Connected");
            if (on.Count == 0) { ui.BeginCard(); ui.Label("No battery devices are connected right now.", F.Body, T.Sub); ui.EndCard(); }
            foreach (var d in on) DeviceTile(ui, d);
            if (off.Count > 0)
            {
                ui.Expander("Disconnected (" + off.Count + ")", showDisconnected, v => showDisconnected = v);
                if (showDisconnected) foreach (var d in off) DeviceTile(ui, d);
            }
            ui.Expander("Is a device missing?", showHelp, v => showHelp = v);
            if (showHelp)
            {
                ui.BeginCard();
                ui.Label("Supported: any Bluetooth device that reports battery to Windows or has the standard Bluetooth LE battery service; Sony headphones and earbuds (read from the headset itself); AirPods and Beats; Razer and Logitech wireless receivers; this PC's own battery.", F.Small, T.Sub);
                ui.Label("Not yet: SteelSeries, Corsair and HyperX dongles (private protocols). If a level looks stuck, turn the device off and on so it reports again.", F.Small, T.Sub);
                ui.Button("Open Bluetooth settings", false, () => { try { System.Diagnostics.Process.Start("ms-settings:bluetooth"); } catch { } });
                ui.EndCard();
            }
        }

        void DeviceTile(Ui ui, BatteryDevice d)
        {
            bool hidden = W.S.Bool("hide." + d.Key, false);
            int h = P(70);
            var r = ui.Tile(h, true, () => DeviceMenu(d, ui), () => DeviceMenu(d, ui));
            if (!ui.Draw) return;
            var g = ui.G;
            bool dim = !d.Connected;
            var ib = new Rectangle(r.X + P(12), r.Y + P(15), P(40), P(40));
            Gfx.Fill(g, ib, P(9), T.Dark ? Color.FromArgb(52, 52, 57) : Color.FromArgb(238, 238, 242));
            Gfx.Icon(g, d.Kind, Rectangle.Inflate(ib, -P(10), -P(10)), dim ? T.Faint : T.Accent, 0);
            int tx = ib.Right + P(12);
            string pct = d.Connected ? (d.Level >= 0 ? d.Level + "%" : "–") : (d.LastLevel >= 0 ? d.LastLevel + "%" : "–");
            int pw = ui.TextW("100%", F.Value) + P(4);
            var batt = new RectangleF(r.Right - P(14) - pw - P(40), r.Y + r.Height / 2f - P(7), P(32), P(15));
            int textW = (int)batt.X - tx - P(8);
            ui.DrawText(d.Name, F.BodyBold, dim ? T.Sub : T.Text, new Rectangle(tx, r.Y + P(15), textW, P(20)), TextFormatFlags.EndEllipsis);
            string eta = bw.Devices.TimeLeft(d), lasts = bw.Devices.FullChargeLasts(d);
            string sub = d.Connected
                ? (eta != null ? eta + " · " : (d.Charging ? "Charging · " : "Connected · ")) + d.Via
                : "Disconnected" + (d.LastLevel >= 0 ? " · last " + d.LastLevel + "%" : "") + (lasts != null ? " · lasts " + lasts : "");
            if (hidden) sub += " · hidden";
            ui.DrawText(sub, F.Small, T.Faint, new Rectangle(tx, r.Y + P(37), textW, P(18)), TextFormatFlags.EndEllipsis);
            int lvl = d.Connected ? d.Level : d.LastLevel;
            Color lc = d.Connected ? T.LevelColor(lvl, bw.LowThreshold) : T.Faint;
            Gfx.Battery(g, batt, lvl, lc, d.Connected ? (T.Dark ? Color.FromArgb(230, 230, 235) : T.Sub) : T.Faint, false, d.Charging);
            ui.DrawText(pct, F.Value, d.Connected ? (lvl >= 0 && lvl <= bw.LowThreshold ? T.Low : lc) : T.Faint,
                new Rectangle(r.Right - P(14) - pw, r.Y, pw, r.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        void DeviceMenu(BatteryDevice d, Ui ui)
        {
            var m = Menus.Create(T);
            var show = new ToolStripMenuItem("Show on taskbar") { Checked = !W.S.Bool("hide." + d.Key, false) };
            show.Click += (o, e) => { W.S.Set("hide." + d.Key, show.Checked); Invalidate(); };
            m.Items.Add(show);
            m.Items.Add("Refresh battery level", null, (o, e) => bw.Devices.RefreshLevels());
            if (d.Key.StartsWith("bt:"))
                m.Items.Add("Bluetooth settings", null, (o, e) => { try { System.Diagnostics.Process.Start("ms-settings:bluetooth"); } catch { } });
            var c = PointToClient(Cursor.Position);
            ShowMenu(m, new Rectangle(c.X, c.Y, P(200), 0));
        }

        void AlertsTab(Ui ui)
        {
            var S = W.S;
            ui.Section("Alerts");
            ui.BeginCard();
            ui.Slider("Alert when battery is below", "Low-battery notification and red indicator (0% to 99%).", S.Int("lowThreshold", 20), 0, 99, "%",
                v => S.Set("lowThreshold", v));
            int[] presets = { 10, 20, 50, 80 };
            ui.Chips("Quick:", new[] { "10%", "20%", "50%", "80%" }, Array.IndexOf(presets, S.Int("lowThreshold", 20)), i => S.Set("lowThreshold", presets[i]));
            ui.Toggle("Low battery notification", "Once per discharge. It won't repeat until the device has been charged again.", S.Bool("notify", true), v => S.Set("notify", v));
            ui.Toggle("Connect / disconnect notifications", "Popup when a Bluetooth device connects or disconnects. Off by default: the widget blinks instead. Never for sleeping dongle mice.", S.Bool("notifyConnect", false), v => S.Set("notifyConnect", v));
            ui.Toggle("Battery charged to 100% (unplug cable)", "Notification at 100% while charging, to protect the battery. Needs a device that reports charging.", S.Bool("fullAlert", true), v => S.Set("fullAlert", v));
            ui.Toggle("Sound on connect or disconnect", "Plays the Windows device sounds.", S.Bool("sound", true), v => S.Set("sound", v));
            ui.Dropdown("Connect effect", "Animation on the taskbar when a device connects.", new[] { "Blink (flash)", "Pulse", "None" }, S.Int("effect", 0), v => S.Set("effect", v));
            ui.EndCard();
        }

        void GeneralTab(Ui ui)
        {
            CompanionCard(ui, "VolumeMediaWidget.exe", "Volume & Media widget", "What is playing (Spotify, YouTube...) with its controls, plus a per-app volume mixer, on the taskbar.",
                Glyph.Volume, "⏮  ⏸  ⏭   Coldplay – Viva La Vida        🔊 64%");
            BehaviorSection(ui, "TaskbarWidgets.Battery");
            ui.Section("When nothing is connected");
            ui.BeginCard();
            ui.Dropdown("Empty widget", null, new[] { "Show an empty battery", "Hide the widget" }, W.S.Int("whenEmpty", 0), v => W.S.Set("whenEmpty", v));
            ui.EndCard();
            UpdatesSection(ui);
        }

        void DesignTab(Ui ui)
        {
            var S = W.S;
            AppearanceSection(ui);
            ui.Section("Devices on the taskbar");
            ui.BeginCard();
            ui.Dropdown("Visible elements", "Choose whether to show percentage, battery icon or only the device icon.",
                new[] { "Automatic (by space)", "Icon + battery + %", "Battery + %", "Icon + %", "Icon + battery", "Icon only (colored by level)" }, S.Int("elements", 0), v => S.Set("elements", v));
            ui.Dropdown("Device layout", "Arrangement in 1 row or grid (2 rows) when several devices are connected.",
                new[] { "Automatic (adapts to space)", "1 row", "Grid (2 rows)" }, S.Int("layout", 0), v => S.Set("layout", v));
            ui.Dropdown("Battery orientation", "Classic horizontal or stylized vertical battery.", new[] { "Horizontal", "Vertical" }, S.Int("orientation", 0), v => S.Set("orientation", v));
            ui.EndCard();
            ui.Section("Icons");
            ui.BeginCard();
            ui.Dropdown("Icon style", "Fine outline, solid silhouette or minimalist.", new[] { "Modern outline", "Solid", "Minimal" }, S.Int("iconStyle", 0), v => S.Set("iconStyle", v));
            ui.Chips("Icon size:", new[] { "Auto", "Custom" }, S.Int("iconSize", 0) == 0 ? 0 : 1, i => S.Set("iconSize", i == 0 ? 0 : 100));
            if (S.Int("iconSize", 0) != 0)
                ui.Slider("Icon size", null, S.Int("iconSize", 100), 60, 140, "%", v => S.Set("iconSize", v));
            ui.Label("Battery level colors", F.Body, T.Text);
            if (ui.Draw)
            {
                string[] names = { "High", "Medium", "Low" };
                int[] lv = { 90, 45, 10 };
                for (int i = 0; i < 3; i++)
                {
                    float x = ui.X0 + i * P(70);
                    Gfx.Battery(ui.G, new RectangleF(x + P(6), ui.Y + P(8), P(34), P(16)), lv[i], T.LevelColor(lv[i], 20), T.Sub, false, false);
                    ui.DrawText(names[i], F.Small, T.Sub, new Rectangle((int)x, ui.Y + P(28), P(48), P(16)), TextFormatFlags.HorizontalCenter);
                }
            }
            ui.Gap(48);
            ui.EndCard();
        }
    }
}
