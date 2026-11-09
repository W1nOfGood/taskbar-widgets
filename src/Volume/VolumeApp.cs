using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarWidgets.Volume
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            bool fresh;
            using (var mutex = new Mutex(true, "TaskbarWidgets.Volume.SingleInstance", out fresh))
            {
                if (!fresh) return;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (o, e) => Log.Write("volume", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (o, e) => Log.Write("volume", e.ExceptionObject as Exception);
                Application.Run(new VolumeWidget());
            }
        }
    }

    internal sealed class VolumeWidget : TaskbarWidget
    {
        public readonly Audio Audio;
        public readonly MediaManager Media;
        public readonly AppAudio Apps;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly List<KeyValuePair<Rectangle, Action>> buttons = new List<KeyValuePair<Rectangle, Action>>();
        int lastVol = -1; bool lastMute; string lastLine;
        Rectangle hoverBtn;

        public VolumeWidget() : base(new Settings("volume"), "volume", 1)
        {
            Audio = new Audio();
            Media = new MediaManager(this);
            Apps = new AppAudio(this, S, () => Media.Current, () => Media.All);
            Apps.Changed += () => Poll();
            string lastApp = null;
            Media.Changed += () =>
            {
                var np = Media.Current;
                string line = np == null ? null : np.Line + np.Playing;
                if (line != lastLine) { lastLine = line; Render(); }
                string app = np == null ? null : np.AppId;
                if (app != lastApp) { lastApp = app; Apps.Refresh(); } // a different player: find its audio session
                if (Panel != null && Panel.Visible) Panel.Invalidate();
            };
            Panel = new VolumePanel(this);
            SetupTray("Volume & Media widget", Glyph.Volume);
            poll.Tick += (o, e) => Poll();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Media.Start();
            Apps.Start();
            poll.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Apps.Restore(); // never leave the music lowered
            base.OnFormClosed(e);
        }

        void Poll()
        {
            bool devChanged = Audio.EnsureDevice();
            int v = (int)Math.Round(Audio.Volume * 100);
            bool m = Audio.Muted;
            var np = Media.Current;
            bool progress = np != null && np.Playing && S.Bool("progress", true) && np.Duration > TimeSpan.Zero;
            string shown = ShownState();
            if (devChanged || v != lastVol || m != lastMute || progress || shown != lastShown) { lastVol = v; lastMute = m; lastShown = shown; Render(); }
        }

        // ---------------------------------------------------------------- what the volume part shows / controls

        string lastShown;
        Glyph overrideGlyph; int overridePct; bool overrideMuted; DateTime overrideUntil = DateTime.MinValue;

        /// <summary>Scroll controls the music app (default) or the master volume.</summary>
        bool AppMode { get { return S.Int("wheelTarget", 0) == 0; } }

        /// <summary>The music app's audio session when the widget controls it, else null (= master).</summary>
        AppSession MusicTarget { get { return AppMode ? Apps.Music : null; } }

        void ShownValue(out Glyph glyph, out int pct, out bool muted)
        {
            if (DateTime.Now < overrideUntil) { glyph = overrideGlyph; pct = overridePct; muted = overrideMuted; return; }
            var mt = MusicTarget;
            if (mt != null)
            {
                pct = (int)Math.Round(Apps.MusicVolume(mt) * 100);
                muted = mt.LiveMuted() || lastMute;
                glyph = Glyph.Note;
                return;
            }
            pct = VolumePct; muted = lastMute; glyph = Glyph.Volume;
        }

        string ShownState() { Glyph g; int p; bool m; ShownValue(out g, out p, out m); return g + ":" + p + ":" + m; }

        /// <summary>Briefly shows another target's level (Shift = master, Ctrl = voice chat) after a scroll.</summary>
        void Flash(Glyph g, int pct, bool muted)
        {
            overrideGlyph = g; overridePct = pct; overrideMuted = muted; overrideUntil = DateTime.Now.AddSeconds(1.6);
            Render();
            var t = new System.Windows.Forms.Timer { Interval = 1700 };
            t.Tick += (o, e) => { t.Dispose(); Render(); };
            t.Start();
        }

        /// <summary>Mute/unmute what the widget controls: the music app, or everything.</summary>
        public void ToggleTargetMute()
        {
            var mt = MusicTarget;
            if (mt != null) { mt.SetMute(!mt.LiveMuted()); lastShown = null; Render(); }
            else ToggleMute();
        }

        public int VolumePct { get { return lastVol < 0 ? (int)Math.Round(Audio.Volume * 100) : lastVol; } }

        public void SetVolume(int pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            Audio.Volume = pct / 100f;
            if (pct > 0 && Audio.Muted) Audio.Muted = false;
            lastVol = pct; lastMute = Audio.Muted;
            Render();
        }

        public void ToggleMute() { Audio.Muted = !Audio.Muted; lastMute = Audio.Muted; Render(); }

        protected override bool HasContent
        {
            get { return Media.Current != null || S.Int("whenIdle", 0) == 0; }
        }

        // ---------------------------------------------------------------- layout

        sealed class WLayout
        {
            public RectangleF Icon, Prev, Play, Next, Title, Divider, VolIcon, VolText;
            public int Width;
            public string Line;
        }

        /// <summary>
        /// Richest layout that fits AvailWidth: first the title gets shorter, then it goes,
        /// then the app icon, then prev/next, then the play button.
        /// </summary>
        WLayout Measure(Graphics g, int h)
        {
            WLayout L = null;
            for (int level = 0; level <= 4; level++)
            {
                L = MeasureLevel(g, h, level, float.MaxValue);
                if (L.Width <= AvailWidth) return L;
                if (level == 0 && L.Title.Width > 0)
                {
                    float tw = L.Title.Width - (L.Width - AvailWidth);
                    if (tw >= 56 * K) return MeasureLevel(g, h, 0, tw);
                }
            }
            return L;
        }

        WLayout MeasureLevel(Graphics g, int h, int level, float titleMax)
        {
            var L = new WLayout();
            var np = Media.Current;
            float pad = h * 0.3f, gap = h * 0.16f, x = pad;
            float btn = h * 0.6f;
            using (var f = WidgetFont(h, false))
            {
                if (np != null)
                {
                    if (S.Bool("showIcon", true) && level < 2) { L.Icon = new RectangleF(x, h * 0.25f, h * 0.5f, h * 0.5f); x += L.Icon.Width + gap * 1.5f; }
                    if (S.Bool("showButtons", true) && level < 4)
                    {
                        if (level < 3) { L.Prev = new RectangleF(x, (h - btn) / 2, btn, btn); x += btn; }
                        L.Play = new RectangleF(x, (h - btn) / 2, btn, btn); x += btn;
                        if (level < 3) { L.Next = new RectangleF(x, (h - btn) / 2, btn, btn); x += btn; }
                        x += gap;
                    }
                    int mode = S.Int("titleMode", 0);
                    if (mode != 2 && level < 1)
                    {
                        L.Line = mode == 1 ? (string.IsNullOrEmpty(np.Title) ? np.AppName : np.Title) : np.Line;
                        float max = Math.Min(S.Int("titleWidth", 200) * K, titleMax);
                        float tw = Math.Min(max, g.MeasureString(L.Line ?? "", f, PointF.Empty, StringFormat.GenericTypographic).Width + 2);
                        L.Title = new RectangleF(x, 0, tw, h); x += tw + gap;
                    }
                }
                else if (!S.Bool("showVolume", true))
                {
                    L.Icon = new RectangleF(x, h * 0.25f, h * 0.5f, h * 0.5f); x += L.Icon.Width + gap;
                    L.Line = "Nothing playing";
                    float tw = g.MeasureString(L.Line, f, PointF.Empty, StringFormat.GenericTypographic).Width + 2;
                    L.Title = new RectangleF(x, 0, tw, h); x += tw + gap;
                }
                if (S.Bool("showVolume", true))
                {
                    if (x > pad + 1) { L.Divider = new RectangleF(x, h * 0.28f, 1, h * 0.44f); x += gap * 1.6f; }
                    L.VolIcon = new RectangleF(x, h * 0.27f, h * 0.46f, h * 0.46f); x += L.VolIcon.Width + gap * 0.6f;
                    float tw = g.MeasureString("100%", f, PointF.Empty, StringFormat.GenericTypographic).Width + 2;
                    L.VolText = new RectangleF(x, 0, tw, h); x += tw;
                }
                else x -= gap;
            }
            L.Width = (int)Math.Ceiling(x + pad);
            return L;
        }

        protected override int MeasureWidth(Graphics g, int h) { return Measure(g, h).Width; }

        protected override void PaintWidget(Graphics g, Rectangle r)
        {
            int style = S.Int("style", 0);
            var L = Measure(g, r.Height);
            buttons.Clear();
            Color bgc = T.Dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(246, 246, 248);
            if (style == 2) // capsules: media pill + volume pill
            {
                int a = (int)(2.55 * S.Int("opacity", 95));
                float split = L.Divider.Width > 0 ? L.Divider.X : r.Width;
                var p1 = new RectangleF(0.5f, 0.5f, split - 3, r.Height - 1);
                var p2 = new RectangleF(split + 3, 0.5f, r.Width - split - 3.5f, r.Height - 1);
                if (p1.Width > 4) { Gfx.Fill(g, p1, p1.Height / 2, Color.FromArgb(a, bgc)); Gfx.Stroke(g, p1, p1.Height / 2, Gfx.Alpha(T.Accent, 160), Math.Max(1f, K)); }
                if (L.Divider.Width > 0 && p2.Width > 4) { Gfx.Fill(g, p2, p2.Height / 2, Color.FromArgb(a, bgc)); Gfx.Stroke(g, p2, p2.Height / 2, Gfx.Alpha(T.Accent, 160), Math.Max(1f, K)); }
                L.Divider = RectangleF.Empty;
            }
            else PaintBackground(g, r, style);

            var np = Media.Current;
            Color fg = WidgetText;
            Color icon = style == 3 ? T.Accent : fg;
            if (L.Icon.Width > 0)
            {
                if (np != null && np.AppIcon != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(np.AppIcon, L.Icon);
                }
                else Gfx.Icon(g, Glyph.Note, L.Icon, Gfx.Mix(T.Accent, fg, 0.2f), 1);
            }
            if (L.Play.Width > 0 && np != null)
            {
                if (L.Prev.Width > 0) Button(g, L.Prev, Glyph.Prev, icon, np.CanPrev, () => Media.Prev(Media.Current));
                Button(g, L.Play, np.Playing ? Glyph.Pause : Glyph.Play, icon, np.CanPlayPause, () => Media.PlayPause(Media.Current));
                if (L.Next.Width > 0) Button(g, L.Next, Glyph.Next, icon, np.CanNext, () => Media.Next(Media.Current));
            }
            if (L.Title.Width > 0 && L.Line != null)
            {
                using (var f = WidgetFont(r.Height, false))
                using (var sf = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
                using (var b = new SolidBrush(np == null ? T.Sub : fg))
                    g.DrawString(L.Line, f, b, new RectangleF(L.Title.X, 0, L.Title.Width, r.Height), sf);
                // thin progress line under the title
                if (np != null && np.Duration > TimeSpan.Zero && S.Bool("progress", true))
                {
                    float frac = (float)Math.Max(0, Math.Min(1, np.LivePosition.TotalSeconds / np.Duration.TotalSeconds));
                    float y = r.Height * 0.8f;
                    using (var p = new Pen(Gfx.Alpha(fg, 40), Math.Max(1.5f, K * 1.5f))) g.DrawLine(p, L.Title.X, y, L.Title.Right, y);
                    using (var p = new Pen(T.Accent, Math.Max(1.5f, K * 1.5f))) g.DrawLine(p, L.Title.X, y, L.Title.X + L.Title.Width * frac, y);
                }
            }
            if (L.Divider.Width > 0)
                using (var p = new Pen(Gfx.Alpha(T.Sub, 90), Math.Max(1f, K))) g.DrawLine(p, L.Divider.X, L.Divider.Y, L.Divider.X, L.Divider.Bottom);
            if (L.VolIcon.Width > 0)
            {
                Glyph vg; int pct; bool muted;
                ShownValue(out vg, out pct, out muted);
                var hit = RectangleF.FromLTRB(L.VolIcon.X - 3, 2, L.VolText.Right + 2, r.Height - 2);
                if (hoverBtn == Rectangle.Round(hit)) Gfx.Fill(g, hit, hit.Height * 0.3f, Gfx.Alpha(fg, 30));
                Glyph drawn = muted && vg == Glyph.Volume ? Glyph.Mute : vg;
                var ir = vg == Glyph.Volume ? L.VolIcon : RectangleF.Inflate(L.VolIcon, -L.VolIcon.Width * 0.08f, -L.VolIcon.Height * 0.08f);
                Gfx.Icon(g, drawn, ir, muted ? T.Low : (vg == Glyph.Note ? T.Accent : icon), vg == Glyph.Volume ? 0 : 1);
                using (var f = WidgetFont(r.Height, false))
                using (var sf = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center })
                using (var b = new SolidBrush(muted ? T.Sub : fg))
                    g.DrawString(pct + "%", f, b, new RectangleF(L.VolText.X, 0, L.VolText.Width + 4, r.Height), sf);
                buttons.Add(new KeyValuePair<Rectangle, Action>(Rectangle.Round(hit), ToggleTargetMute));
            }
        }

        void Button(Graphics g, RectangleF r, Glyph glyph, Color c, bool enabled, Action a)
        {
            var rr = Rectangle.Round(r);
            if (enabled && hoverBtn == rr) using (var b = new SolidBrush(Gfx.Alpha(c, 34))) g.FillEllipse(b, r);
            float inset = r.Width * 0.27f;
            Gfx.Icon(g, glyph, new RectangleF(r.X + inset, r.Y + inset, r.Width - 2 * inset, r.Height - 2 * inset), enabled ? c : Gfx.Alpha(c, 70), 1);
            if (enabled) buttons.Add(new KeyValuePair<Rectangle, Action>(rr, a));
        }

        protected override void OnHoverMove()
        {
            var hit = buttons.FirstOrDefault(b => b.Key.Contains(Mouse)).Key;
            if (hit != hoverBtn) { hoverBtn = hit; Render(); }
        }

        protected override void OnWidgetClick(Point p, MouseButtons b)
        {
            if (b == MouseButtons.Middle) { if (S.Bool("middleMute", true)) ToggleTargetMute(); return; }
            foreach (var bt in buttons)
                if (bt.Key.Contains(p)) { bt.Value(); return; }
            OpenPanel(-1);
        }

        /// <summary>
        /// Scroll = the music app only (so a game keeps its volume). Shift + scroll = master volume.
        /// Ctrl + scroll = voice chat (Discord, Teams...). "Master volume" mode swaps scroll and Shift + scroll.
        /// </summary>
        protected override void OnWidgetWheel(int delta)
        {
            float step = S.Int("wheelStep", 2) / 100f * Math.Sign(delta);
            var mods = Control.ModifierKeys;
            if ((mods & Keys.Control) != 0)
            {
                var v = Apps.Voice.FirstOrDefault();
                if (v != null)
                {
                    v.SetVolume(v.LiveVolume() + step);
                    if (step > 0 && v.LiveMuted()) v.SetMute(false);
                    Flash(Glyph.Headphones, (int)Math.Round(v.LiveVolume() * 100), v.LiveMuted());
                    return;
                }
            }
            var music = Apps.Music;
            bool wantMusic = AppMode != ((mods & Keys.Shift) != 0); // Shift flips the target
            if (wantMusic && music != null)
            {
                Apps.SetMusicVolume(music, Apps.MusicVolume(music) + step);
                int p = (int)Math.Round(Apps.MusicVolume(music) * 100);
                if (AppMode) { lastShown = null; Render(); } else Flash(Glyph.Note, p, music.LiveMuted());
                return;
            }
            SetVolume(VolumePct + (int)Math.Round(step * 100));
            if (AppMode && music != null) Flash(Glyph.Volume, VolumePct, lastMute);
        }

        protected override void AddMenuItems(ContextMenuStrip m)
        {
            m.Items.Add(Audio.Muted ? "Unmute" : "Mute", null, (o, e) => ToggleMute());
            m.Items.Add("Sound settings", null, (o, e) => { try { System.Diagnostics.Process.Start("ms-settings:sound"); } catch { } });
            m.Items.Add("Volume mixer", null, (o, e) => { try { System.Diagnostics.Process.Start("ms-settings:apps-volume"); } catch { } });
        }
    }

    internal sealed class VolumePanel : PanelForm
    {
        readonly VolumeWidget vw;
        readonly System.Windows.Forms.Timer live = new System.Windows.Forms.Timer();
        List<AppSession> sessions = new List<AppSession>();
        List<AudioDevice> outputs = new List<AudioDevice>();
        DateTime sessionsAt;

        public VolumePanel(VolumeWidget w) : base(w, new[] { "Player", "Mixer", "General", "Design" })
        {
            vw = w;
            live.Tick += (o, e) => LiveTick();
        }

        protected override string HeaderTitle { get { return "Volume & Media"; } }
        protected override string HeaderSub
        {
            get
            {
                var np = vw.Media.Current;
                return np == null ? vw.Audio.DeviceName : (np.Playing ? "Playing on " : "Paused on ") + np.AppName;
            }
        }
        protected override Glyph HeaderGlyph { get { return Glyph.Volume; } }

        protected override void OnOpening() { RefreshAudio(); live.Interval = 90; live.Start(); }
        protected override void OnClosing2() { live.Stop(); }
        protected override void OnRefresh() { RefreshAudio(); }

        bool refreshing;

        /// <summary>Re-lists sessions and outputs on a background thread; the panel keeps painting meanwhile.</summary>
        void RefreshAudio()
        {
            vw.Audio.EnsureDevice();
            sessionsAt = DateTime.Now;
            if (Demo.On)
            {
                if (sessions.Count == 0)
                    sessions = new List<AppSession> {
                        new AppSession { Name = "Spotify", Volume = 0.8f, Peak = 0.55f },
                        new AppSession { Name = "Discord", Volume = 0.45f, Peak = 0.2f },
                        new AppSession { Name = "Browser", Volume = 1f, Muted = true },
                        new AppSession { Name = "System sounds", System = true, Volume = 0.6f } };
                outputs = new List<AudioDevice> {
                    new AudioDevice { Id = vw.Audio.DeviceId, Name = "Headphones (Wireless)" },
                    new AudioDevice { Id = "demo-spk", Name = "Speakers (Realtek Audio)" } };
                return;
            }
            if (refreshing) return;
            refreshing = true;
            System.Threading.Tasks.Task.Run(() => Audio.TakeSnapshot()).ContinueWith(t =>
            {
                refreshing = false;
                if (t.IsFaulted || IsDisposed) return;
                var old = sessions;
                sessions = t.Result.Sessions;
                outputs = t.Result.Outputs;
                Audio.Release(old);
                if (Visible) Invalidate();
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        void LiveTick()
        {
            if (!Visible) { live.Stop(); return; }
            if (Tab == 1)
            {
                if ((DateTime.Now - sessionsAt).TotalSeconds > 2.5) RefreshAudio();
                if (!Demo.On) Audio.UpdatePeaks(sessions);
                Invalidate();
            }
            else if (Tab == 0)
            {
                // the progress bar only needs a few frames per second
                if ((DateTime.Now - sessionsAt).TotalMilliseconds > 450) { sessionsAt = DateTime.Now; Invalidate(); }
            }
        }

        protected override void BuildTab(Ui ui)
        {
            switch (Tab)
            {
                case 0: PlayerTab(ui); break;
                case 1: MixerTab(ui); break;
                case 2: GeneralTab(ui); break;
                default: DesignTab(ui); break;
            }
        }

        // ---------------------------------------------------------------- player

        void PlayerTab(Ui ui)
        {
            var np = vw.Media.Current;
            ui.Section("Now playing");
            if (np == null)
            {
                ui.BeginCard();
                ui.Label("Nothing is playing. Start music or a video in Spotify, a browser, Media Player... and it will appear here and on the taskbar.", F.Body, T.Sub);
                ui.EndCard();
            }
            else
            {
                var card = ui.Tile(P(250), false, null);
                if (ui.Draw) DrawPlayer(ui, card, np);
            }

            var others = vw.Media.All.Where(s => np == null || s.AppId != np.AppId).ToList();
            if (others.Count > 0 || vw.Media.PinnedAppId != null)
            {
                ui.Section("Other players");
                if (vw.Media.PinnedAppId != null)
                {
                    ui.BeginCard();
                    ui.Button("Follow whatever is playing (automatic)", false, () => { vw.Media.PinnedAppId = null; vw.Render(); });
                    ui.EndCard();
                }
                foreach (var s in others)
                {
                    var cap = s;
                    var r = ui.Tile(P(58), true, () => { vw.Media.PinnedAppId = cap.AppId; vw.Render(); });
                    if (!ui.Draw) continue;
                    var ic = new Rectangle(r.X + P(12), r.Y + P(13), P(32), P(32));
                    DrawAppIcon(ui.G, s, ic);
                    int tx = ic.Right + P(12), tw = r.Right - tx - P(70);
                    ui.DrawText(s.AppName, F.BodyBold, T.Text, new Rectangle(tx, r.Y + P(10), tw, P(20)), TextFormatFlags.EndEllipsis);
                    ui.DrawText(string.IsNullOrEmpty(s.Line) ? "Idle" : s.Line, F.Small, T.Sub, new Rectangle(tx, r.Y + P(31), tw, P(18)), TextFormatFlags.EndEllipsis);
                    ui.DrawText(s.Playing ? "Playing" : "Paused", F.Small, s.Playing ? T.Good : T.Faint, new Rectangle(r.Right - P(80), r.Y, P(66), r.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
            }
        }

        void DrawAppIcon(Graphics g, NowPlaying s, Rectangle ic)
        {
            if (s.AppIcon != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(s.AppIcon, ic); }
            else { Gfx.Fill(g, ic, P(7), T.Input); Gfx.Icon(g, Glyph.Note, Rectangle.Inflate(ic, -P(7), -P(7)), T.Accent, 1); }
        }

        void DrawPlayer(Ui ui, Rectangle card, NowPlaying np)
        {
            var g = ui.G;
            var art = new Rectangle(card.X + P(16), card.Y + P(16), P(116), P(116));
            if (np.Art != null)
            {
                using (var path = Gfx.Round(art, P(10)))
                {
                    var st = g.Save();
                    g.SetClip(path);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(np.Art, art);
                    g.Restore(st);
                }
            }
            else
            {
                Gfx.Fill(g, art, P(10), T.Input);
                Gfx.Icon(g, Glyph.Note, Rectangle.Inflate(art, -P(34), -P(34)), T.Accent, 1);
            }
            int tx = art.Right + P(14), tw = card.Right - P(16) - tx;
            string title = string.IsNullOrEmpty(np.Title) ? np.AppName : np.Title;
            int th = Math.Min(ui.TextH(title, F.Big, tw), P(52));
            ui.DrawText(title, F.Big, T.Text, new Rectangle(tx, art.Y, tw, th), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            int y = art.Y + th + P(4);
            if (!string.IsNullOrEmpty(np.Artist)) { ui.DrawText(np.Artist, F.Body, T.Sub, new Rectangle(tx, y, tw, P(20)), TextFormatFlags.EndEllipsis); y += P(22); }
            if (!string.IsNullOrEmpty(np.Album)) { ui.DrawText(np.Album, F.Small, T.Faint, new Rectangle(tx, y, tw, P(18)), TextFormatFlags.EndEllipsis); }
            var appR = new Rectangle(tx, art.Bottom - P(20), P(20), P(20));
            if (np.AppIcon != null) g.DrawImage(np.AppIcon, appR);
            else Gfx.Icon(g, Glyph.Note, Rectangle.Inflate(appR, -P(2), -P(2)), T.Accent, 1);
            ui.DrawText(np.AppName, F.Small, T.Sub, new Rectangle(appR.Right + P(6), appR.Y, tw - P(26), appR.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // timeline
            int ty = art.Bottom + P(18);
            var bar = new Rectangle(card.X + P(16), ty, card.Width - P(32), P(14));
            if (np.Duration > TimeSpan.Zero)
            {
                var pos = np.LivePosition;
                float f = (float)Math.Max(0, Math.Min(1, pos.TotalSeconds / np.Duration.TotalSeconds));
                var track = new RectangleF(bar.X, bar.Y + bar.Height / 2f - P(2), bar.Width, P(4));
                Gfx.Fill(g, track, P(2), T.Track);
                Gfx.Fill(g, new RectangleF(track.X, track.Y, track.Width * f, track.Height), P(2), T.Accent);
                if (np.CanSeek)
                {
                    float kx = track.X + track.Width * f;
                    using (var b = new SolidBrush(T.Accent)) g.FillEllipse(b, kx - P(6), track.Y + track.Height / 2 - P(6), P(12), P(12));
                    ui.Hit(bar, null, null, (p, hr) =>
                    {
                        float ff = Math.Max(0, Math.Min(1, (p.X - hr.X) / (float)hr.Width));
                        vw.Media.Seek(np, TimeSpan.FromSeconds(np.Duration.TotalSeconds * ff));
                    });
                }
                ui.DrawText(Fmt(pos), F.Small, T.Sub, new Rectangle(bar.X, bar.Bottom + P(2), P(60), P(16)), TextFormatFlags.Left);
                ui.DrawText(Fmt(np.Duration), F.Small, T.Sub, new Rectangle(bar.Right - P(60), bar.Bottom + P(2), P(60), P(16)), TextFormatFlags.Right);
            }

            // controls
            int cy = bar.Bottom + P(26);
            int big = P(46), small = P(36), gap = P(26);
            int cx = card.X + card.Width / 2;
            var play = new Rectangle(cx - big / 2, cy, big, big);
            var prev = new Rectangle(play.X - gap - small, cy + (big - small) / 2, small, small);
            var next = new Rectangle(play.Right + gap, prev.Y, small, small);
            CtlButton(ui, prev, Glyph.Prev, np.CanPrev, false, () => vw.Media.Prev(np));
            CtlButton(ui, play, np.Playing ? Glyph.Pause : Glyph.Play, np.CanPlayPause, true, () => vw.Media.PlayPause(np));
            CtlButton(ui, next, Glyph.Next, np.CanNext, false, () => vw.Media.Next(np));
        }

        void CtlButton(Ui ui, Rectangle r, Glyph glyph, bool enabled, bool primary, Action a)
        {
            var g = ui.G;
            bool hv = enabled && ui.Hovered(r);
            if (primary) using (var b = new SolidBrush(enabled ? (hv ? ControlPaint.Light(T.Accent, 0.15f) : T.Accent) : T.Track)) g.FillEllipse(b, r);
            else if (hv) using (var b = new SolidBrush(T.CardHover)) g.FillEllipse(b, r);
            int inset = (int)(r.Width * (primary ? 0.3f : 0.24f));
            Gfx.Icon(g, glyph, Rectangle.Inflate(r, -inset, -inset), primary ? Color.White : (enabled ? T.Text : T.Faint), 1);
            if (enabled) ui.Hit(r, a);
        }

        static string Fmt(TimeSpan t)
        {
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        // ---------------------------------------------------------------- mixer

        void MixerTab(Ui ui)
        {
            var A = vw.Audio;
            var S = W.S;
            ui.Section("Output");
            ui.BeginCard();
            int sel = outputs.FindIndex(o => o.Id == A.DeviceId);
            ui.Dropdown("Output device", null, outputs.Select(o => o.Name).ToArray(), sel,
                i => { if (i < outputs.Count && Audio.SetDefault(outputs[i].Id)) { RefreshAudio(); vw.Render(); } });
            ui.RowStart();
            VolumeRow(ui, Glyph.Volume, null, "Master volume", vw.VolumePct / 100f, A.Muted, A.Peak,
                v => vw.SetVolume((int)Math.Round(v * 100)), vw.ToggleMute);
            ui.EndCard();

            ui.Section("Auto-lower music");
            ui.BeginCard();
            ui.Toggle("While people talk in voice chat", "Discord, Teams, Zoom, Slack, TeamSpeak, Mumble... The music fades down, then back up. Games are not touched.",
                S.Bool("duck", true), v => { S.Set("duck", v); vw.Apps.Restore(); vw.Apps.Refresh(); });
            ui.Toggle("While a lecture or video plays", "A course, YouTube or a video player in your browser keeps its volume; only the music app is lowered.",
                S.Bool("duckVideo", true), v => { S.Set("duckVideo", v); vw.Apps.Restore(); vw.Apps.Refresh(); });
            if (S.Bool("duck", true) || S.Bool("duckVideo", true))
                ui.Slider("Lower music by", null, S.Int("duckAmount", 60), 20, 90, "%", v => S.Set("duckAmount", v));
            var voice = vw.Apps.Voice;
            var videos = vw.Apps.Videos;
            var music = vw.Apps.Music;
            string status = music == null ? "No music app is playing." : "Music: " + music.Name;
            if (Demo.On) status = "Music: Spotify  ·  Video: Browser";
            if (voice.Count > 0) status += "  \u00B7  Voice: " + string.Join(", ", voice.Select(x => x.Name).Distinct());
            if (videos.Count > 0) status += "  \u00B7  Video: " + string.Join(", ", videos.Select(x => x.Name).Distinct());
            ui.Label(status, F.Small, music == null && !Demo.On ? T.Faint : T.Good);
            ui.EndCard();

            ui.Section("Apps");
            ui.BeginCard();
            if (sessions.Count == 0) ui.Label("No app is using audio on this device right now.", F.Body, T.Sub);
            foreach (var s in sessions)
            {
                var cap = s;
                ui.RowStart();
                VolumeRow(ui, s.System ? Glyph.Bolt : Glyph.Note, s.System ? null : Audio.IconFor(s.ExePath), s.Name, s.Volume, s.Muted, s.Peak * A.Volume,
                    v => { cap.SetVolume(v); }, () => cap.SetMute(!cap.Muted));
            }
            ui.EndCard();
            ui.BeginCard();
            ui.Button("Open Windows volume mixer", false, () => { try { System.Diagnostics.Process.Start("ms-settings:apps-volume"); } catch { } });
            ui.EndCard();
        }

        void VolumeRow(Ui ui, Glyph glyph, Bitmap icon, string name, float vol, bool muted, float peak, Action<float> set, Action mute)
        {
            int y0 = ui.Y;
            var ic = new Rectangle(ui.X0, y0 + P(4), P(30), P(30));
            int tx = ic.Right + P(12), tw = ui.Width - (tx - ui.X0);
            int pct = (int)Math.Round(vol * 100);
            if (ui.Draw)
            {
                if (icon != null) { ui.G.InterpolationMode = InterpolationMode.HighQualityBicubic; ui.G.DrawImage(icon, Rectangle.Inflate(ic, -P(2), -P(2))); }
                else { Gfx.Fill(ui.G, ic, P(7), T.Input); Gfx.Icon(ui.G, glyph, Rectangle.Inflate(ic, -P(7), -P(7)), T.Accent, 1); }
                ui.DrawText(name, F.Body, muted ? T.Sub : T.Text, new Rectangle(tx, y0, tw - P(84), P(20)), TextFormatFlags.EndEllipsis);
                ui.DrawText(muted ? "Muted" : pct + "%", F.BodyBold, muted ? T.Low : T.Text, new Rectangle(ui.X0 + ui.Width - P(84), y0, P(50), P(20)), TextFormatFlags.Right);
            }
            // mute button
            var mb = new Rectangle(ui.X0 + ui.Width - P(28), y0 - P(4), P(28), P(28));
            if (ui.Draw)
            {
                if (ui.Hovered(mb)) Gfx.Fill(ui.G, mb, P(6), T.CardHover);
                Gfx.Icon(ui.G, muted ? Glyph.Mute : Glyph.Volume, Rectangle.Inflate(mb, -P(6), -P(6)), muted ? T.Low : T.Sub, 0);
            }
            ui.Hit(mb, mute);
            ui.Y = y0 + P(22);
            ui.X0 += tx - ui.X0 - P(9); ui.Width = tw + P(9);
            ui.SliderBar(new Rectangle(ui.X0, ui.Y, ui.Width, P(22)), pct, 0, 100, v => { set(v / 100f); Invalidate(); });
            // live meter
            if (ui.Draw && peak > 0.001f)
            {
                var m = new RectangleF(ui.X0 + P(9), ui.Y + P(19), (ui.Width - P(18)) * Math.Min(1f, peak), P(2));
                Gfx.Fill(ui.G, m, P(1), Gfx.Alpha(T.Good, 200));
            }
            ui.X0 = ic.X; ui.Width = tw + (tx - ic.X);
            ui.Y += P(22);
        }

        // ---------------------------------------------------------------- general & design

        void GeneralTab(Ui ui)
        {
            var S = W.S;
            CompanionCard(ui, "BatteryWidget.exe", "Battery widget", "Battery level of your Bluetooth headphones, mouse, keyboard and controllers, right on the taskbar.",
                Glyph.Headphones, "🎧 90%   🖱 2%");
            BehaviorSection(ui, "TaskbarWidgets.Volume");
            ui.Section("Controls");
            ui.BeginCard();
            ui.Dropdown("Scroll wheel controls", "Shift + scroll = the other one. Ctrl + scroll = voice chat (Discord, Teams...).",
                new[] { "Only the music app (games keep their volume)", "Master volume (everything)" }, S.Int("wheelTarget", 0), v => S.Set("wheelTarget", v));
            ui.Slider("Mouse wheel step", "Scroll over the widget to change the volume.", S.Int("wheelStep", 2), 1, 10, "%", v => S.Set("wheelStep", v));
            ui.Toggle("Middle-click mutes", "Click the mouse wheel on the widget to mute or unmute.", S.Bool("middleMute", true), v => S.Set("middleMute", v));
            ui.EndCard();
        }

        void DesignTab(Ui ui)
        {
            var S = W.S;
            AppearanceSection(ui);
            ui.Section("Widget content");
            ui.BeginCard();
            ui.Dropdown("Track text", null, new[] { "Artist – Title", "Title only", "Hidden" }, S.Int("titleMode", 0), v => S.Set("titleMode", v));
            ui.Slider("Maximum text width", "Longer titles end with …", S.Int("titleWidth", 200), 80, 420, "px", v => S.Set("titleWidth", v));
            ui.Toggle("App icon", "Spotify, browser, player... icon on the left.", S.Bool("showIcon", true), v => S.Set("showIcon", v));
            ui.Toggle("Media buttons", "Previous, play/pause and next.", S.Bool("showButtons", true), v => S.Set("showButtons", v));
            ui.Toggle("Progress line", "Thin line under the title showing the track position.", S.Bool("progress", true), v => S.Set("progress", v));
            ui.Toggle("Volume level", "Speaker icon and percentage. Scroll to change, click to mute.", S.Bool("showVolume", true), v => S.Set("showVolume", v));
            ui.Dropdown("When nothing is playing", null, new[] { "Show what is left (volume / placeholder)", "Hide the widget" }, S.Int("whenIdle", 0), v => S.Set("whenIdle", v));
            ui.EndCard();
        }
    }
}
