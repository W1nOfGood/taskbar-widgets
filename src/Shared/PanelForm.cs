using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TaskbarWidgets
{
    internal sealed class Fonts
    {
        public Font Title, Sub, Body, BodyBold, Small, Section, Big, Value;
        public Fonts(float k)
        {
            const string ff = "Segoe UI Variable Text";
            Title = new Font(ff, 15.5f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            Sub = new Font(ff, 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            Body = new Font(ff, 14f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            BodyBold = new Font(ff, 14f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            Small = new Font(ff, 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            Section = new Font(ff, 11.5f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            Big = new Font(ff, 19f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            Value = new Font(ff, 15f * k, FontStyle.Bold, GraphicsUnit.Pixel);
        }
    }

    /// <summary>Flyout settings panel. Everything inside is painted by an immediate-mode <see cref="Ui"/>.</summary>
    internal abstract class PanelForm : Form
    {
        public const int GeneralTabIndex = 2;
        protected readonly TaskbarWidget W;
        public Theme T;
        public float K = 1f;
        public Fonts F;
        protected string[] Tabs;
        public int Tab;
        int scroll, contentH, contentTop;
        readonly List<Hit> hits = new List<Hit>();
        internal readonly List<int> CardHeights = new List<int>();
        Hit dragHit, downHit, hoverHit;
        Point downPoint;
        Point mouse = new Point(-1, -1);
        public bool MenuOpen;
        DateTime hiddenAt = DateTime.MinValue;
        Bitmap buffer;

        internal sealed class Hit
        {
            public Rectangle R;
            public Action Click, Right;
            public Action<Point, Rectangle> Drag;
        }

        protected PanelForm(TaskbarWidget w, string[] tabs)
        {
            W = w; Tabs = tabs;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            Text = "Widget panel";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ApplyTheme();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW; return cp; }
        }

        public void ApplyTheme()
        {
            T = W.T; K = W.K;
            F = new Fonts(K);
            BackColor = T.Bg;
            if (IsHandleCreated) Native.RoundWindow(Handle, T.Dark, T.Border);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundWindow(Handle, T.Dark, T.Border);
        }

        public int P(float v) { return (int)Math.Round(v * K); }

        // ---------------------------------------------------------------- show / hide

        public bool JustHidden { get { return (DateTime.Now - hiddenAt).TotalMilliseconds < 350; } }

        public void ShowNear(Rectangle anchor, int tab)
        {
            ApplyTheme();
            if (tab >= 0) { Tab = tab; scroll = 0; }
            int w = P(430), h = P(600);
            var scr = Screen.FromPoint(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2));
            var wa = scr.WorkingArea;
            h = Math.Min(h, wa.Height - P(16));
            int x = Math.Max(wa.Left + P(8), Math.Min(wa.Right - w - P(8), anchor.Left + anchor.Width / 2 - w / 2));
            bool taskbarAtBottom = anchor.Top + anchor.Height / 2 > scr.Bounds.Top + scr.Bounds.Height / 2;
            int y = taskbarAtBottom ? Math.Min(wa.Bottom, anchor.Top) - h - P(10) : Math.Max(wa.Top, anchor.Bottom) + P(10);
            Bounds = new Rectangle(x, y, w, h);
            OnOpening();
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            Invalidate();
        }

        public void HideNow()
        {
            if (!Visible) return;
            Hide();
            hiddenAt = DateTime.Now;
            if (buffer != null) { buffer.Dispose(); buffer = null; }
            OnClosing2();
            // Only a managed GC here: emptying the working set right after use makes the next click
            // page everything back in (felt as a short freeze). The widget trims after a long idle instead.
            GC.Collect();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!MenuOpen && Environment.GetEnvironmentVariable("TW_PIN") != "1") HideNow();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) HideNow();
        }

        protected virtual void OnOpening() { }
        protected virtual void OnClosing2() { }
        protected abstract string HeaderTitle { get; }
        protected abstract string HeaderSub { get; }
        protected abstract Glyph HeaderGlyph { get; }
        protected abstract void BuildTab(Ui ui);
        protected virtual void OnRefresh() { }

        // ---------------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(T.Bg);
            hits.Clear();
            int pad = P(14);

            // header
            var icon = new Rectangle(pad, P(12), P(38), P(38));
            Gfx.Fill(g, icon, P(9), T.Card);
            Gfx.Icon(g, HeaderGlyph, Rectangle.Inflate(icon, -P(8), -P(8)), T.Accent, 1);
            TextRenderer.DrawText(g, HeaderTitle, F.Title, new Point(icon.Right + P(10), P(12)), T.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, HeaderSub, F.Sub, new Point(icon.Right + P(10), P(33)), T.Sub, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            var close = new Rectangle(Width - pad - P(30), P(16), P(30), P(30));
            var refresh = new Rectangle(close.Left - P(34), close.Top, close.Width, close.Height);
            HeaderButton(g, close, Glyph.Close, HideNow);
            HeaderButton(g, refresh, Glyph.Refresh, () => { OnRefresh(); Invalidate(); });

            // tabs
            var tr = new Rectangle(pad, P(62), Width - 2 * pad, P(38));
            Gfx.Fill(g, tr, P(9), T.TabBg);
            Gfx.Stroke(g, tr, P(9), T.Border, 1);
            float tw = (tr.Width - P(8)) / (float)Tabs.Length;
            for (int i = 0; i < Tabs.Length; i++)
            {
                var r = new Rectangle((int)(tr.X + P(4) + i * tw), tr.Y + P(4), (int)tw, tr.Height - P(8));
                bool active = i == Tab;
                if (active)
                {
                    Gfx.Fill(g, r, P(7), T.Card);
                    Gfx.Stroke(g, r, P(7), T.Accent, Math.Max(1f, K));
                }
                else if (r.Contains(mouse)) Gfx.Fill(g, r, P(7), Gfx.Alpha(T.Card, 140));
                TextRenderer.DrawText(g, Tabs[i], F.Body, r, active ? T.Text : T.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                int idx = i;
                hits.Add(new Hit { R = r, Click = () => { Tab = idx; scroll = 0; } });
            }

            // content (two passes: measure card heights, then draw)
            contentTop = tr.Bottom + P(10);
            var clip = new Rectangle(0, contentTop, Width, Height - contentTop);
            int visibleH = clip.Height;
            // pass 1: measure (no drawing) to learn card heights and total content height
            CardHeights.Clear();
            var mui = new Ui(this, g, false, 0, clip, mouse);
            mui.X0 = pad; mui.Width = Width - 2 * pad; mui.Y = P(2);
            BuildTab(mui);
            contentH = mui.Y + P(14);
            scroll = Math.Max(0, Math.Min(scroll, contentH - visibleH));

            // pass 2: draw the whole content into an opaque buffer without transforms
            // (GDI text ignores Graphics transforms/clips), then blit the visible slice.
            int bh = Math.Max(contentH, visibleH);
            if (buffer == null || buffer.Width != Width || buffer.Height != bh)
            {
                if (buffer != null) buffer.Dispose();
                buffer = new Bitmap(Width, bh, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            }
            using (var bg = Graphics.FromImage(buffer))
            {
                bg.SmoothingMode = SmoothingMode.AntiAlias;
                bg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                bg.Clear(T.Bg);
                var ui = new Ui(this, bg, true, contentTop - scroll, clip, mouse);
                ui.X0 = pad; ui.Width = Width - 2 * pad; ui.Y = P(2);
                BuildTab(ui);
            }
            g.DrawImage(buffer, clip, new Rectangle(0, scroll, Width, visibleH), GraphicsUnit.Pixel);

            // scrollbar
            if (contentH > visibleH)
            {
                float frac = visibleH / (float)contentH;
                int th = Math.Max(P(30), (int)(visibleH * frac));
                int ty = contentTop + (int)((visibleH - th) * (scroll / (float)Math.Max(1, contentH - visibleH)));
                Gfx.Fill(g, new Rectangle(Width - P(6), ty, P(3), th), P(2), Gfx.Alpha(T.Sub, 120));
            }
        }

        void HeaderButton(Graphics g, Rectangle r, Glyph glyph, Action a)
        {
            if (r.Contains(mouse)) Gfx.Fill(g, r, P(7), T.CardHover);
            Gfx.Icon(g, glyph, Rectangle.Inflate(r, -P(8), -P(8)), T.Sub, 0);
            hits.Add(new Hit { R = r, Click = a });
        }

        internal void AddHit(Hit h) { hits.Add(h); }

        Hit HitAt(Point p)
        {
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(p)) return hits[i];
            return null;
        }

        // ---------------------------------------------------------------- input

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            W.Touch();
            mouse = e.Location;
            if (dragHit != null) { dragHit.Drag(e.Location, dragHit.R); Invalidate(); return; }
            var h = HitAt(e.Location);
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            if (h != hoverHit) { hoverHit = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouse = new Point(-1, -1); hoverHit = null; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            W.Touch();
            downPoint = e.Location;
            downHit = HitAt(e.Location);
            if (e.Button == MouseButtons.Left && downHit != null && downHit.Drag != null)
            {
                dragHit = downHit; Capture = true;
                dragHit.Drag(e.Location, dragHit.R);
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragHit != null) { dragHit = null; Capture = false; Invalidate(); return; }
            // The panel repaints often (live meters, progress bar) and every repaint rebuilds the hit list,
            // so compare by position, not by object: same target = the hit under the release point also
            // contains the press point.
            var h = HitAt(e.Location);
            if (h != null && downHit != null && h.R.Contains(downPoint))
            {
                if (e.Button == MouseButtons.Left && h.Click != null) h.Click();
                else if (e.Button == MouseButtons.Right && h.Right != null) h.Right();
                Invalidate();
            }
            downHit = null;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll = Math.Max(0, scroll - e.Delta * P(60) / 120);
            Invalidate();
        }

        // ---------------------------------------------------------------- menus

        public void ShowMenu(Rectangle clientRect, string[] opts, int sel, Action<int> set)
        {
            var m = Menus.Create(T);
            for (int i = 0; i < opts.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(opts[i]) { Checked = i == sel };
                it.Click += (o, e) => { set(idx); Invalidate(); };
                m.Items.Add(it);
            }
            ShowMenu(m, clientRect);
        }

        public void ShowMenu(ContextMenuStrip m, Rectangle clientRect)
        {
            m.MinimumSize = new Size(clientRect.Width, 0);
            MenuOpen = true;
            m.Closed += (o, e) =>
            {
                MenuOpen = false;
                BeginInvoke(new Action(() => { m.Dispose(); if (Visible && Form.ActiveForm != this) Activate(); }));
            };
            m.Show(this, new Point(clientRect.Left, clientRect.Bottom + P(4)));
        }

        // ---------------------------------------------------------------- shared sections

        protected void CompanionCard(Ui ui, string exe, string name, string desc, Glyph glyph, string preview)
        {
            bool installed = System.IO.File.Exists(Companion.ExePath(exe));
            bool running = installed && Companion.Running(exe);
            ui.BeginCard();
            var r = new Rectangle(ui.X0, ui.Y, ui.Width, P(42));
            if (ui.Draw)
            {
                Gfx.Fill(ui.G, r, P(8), T.Dark ? Color.FromArgb(24, 24, 28) : Color.FromArgb(232, 232, 238));
                Gfx.Stroke(ui.G, Rectangle.Inflate(r, -P(6), -P(6)), P(6), Gfx.Alpha(T.Accent, 180), 1);
                ui.DrawText(preview, F.Small, T.Sub, new Rectangle(r.X + P(16), r.Y, r.Width - P(32), r.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            ui.Y = r.Bottom + P(12);
            var ic = new Rectangle(ui.X0, ui.Y, P(40), P(40));
            if (ui.Draw) { Gfx.Fill(ui.G, ic, P(9), T.Accent); Gfx.Icon(ui.G, glyph, Rectangle.Inflate(ic, -P(9), -P(9)), Color.White, 1); }
            int tx = ic.Right + P(12), tw = ui.Width - (tx - ui.X0);
            int y0 = ui.Y;
            int h1 = ui.TextH(name, F.BodyBold, tw);
            if (ui.Draw) ui.DrawText(name, F.BodyBold, T.Text, new Rectangle(tx, y0, tw, h1), TextFormatFlags.WordBreak);
            int h2 = ui.TextH(desc, F.Small, tw);
            if (ui.Draw) ui.DrawText(desc, F.Small, T.Sub, new Rectangle(tx, y0 + h1 + P(2), tw, h2), TextFormatFlags.WordBreak);
            ui.Y = Math.Max(ic.Bottom, y0 + h1 + P(2) + h2) + P(12);
            if (!installed)
                ui.Label("Not found next to this app. Build both apps into the same folder to link them.", F.Small, T.Faint);
            else if (running)
                ui.Label("✓ Running. Both widgets share the taskbar without overlapping.", F.Small, T.Good);
            else
                ui.Button("Open " + name + "  ›", true, () => { Companion.Launch(exe); });
            ui.EndCard();
        }

        protected void BehaviorSection(Ui ui, string runName)
        {
            var S = W.S;
            ui.Section("Behavior");
            ui.BeginCard();
            ui.Toggle("Hide in full-screen apps", "Gets out of the way for games and videos.", S.Bool("hideFullscreen", true), v => S.Set("hideFullscreen", v));
            ui.Toggle("Hide when the mouse is over it", "Disappears while the mouse is over it so you can click what is underneath.", S.Bool("hoverHide", false), v => S.Set("hoverHide", v));
            ui.Toggle("Slide with auto-hide taskbar", "Smoothly slides up and down together with a hidden taskbar.", S.Bool("slide", true), v => S.Set("slide", v));
            ui.Toggle("Interactive", "Click: open panel · Right-click: menu · Drag: move. When off, use the tray icon.", S.Bool("interactive", true),
                v => { S.Set("interactive", v); if (!v) S.Set("trayIcon", true); W.ApplyInteractive(); if (W.Tray != null) W.Tray.Visible = S.Bool("trayIcon", true); });
            ui.Toggle("Show tray icon", "Icon in the notification area for the menu and notifications.", S.Bool("trayIcon", true),
                v => { S.Set("trayIcon", v || !S.Bool("interactive", true)); if (W.Tray != null) W.Tray.Visible = S.Bool("trayIcon", true); });
            bool startup = false;
            try { startup = Startup.IsEnabled(runName); } catch { }
            ui.Toggle("Start with Windows", "Launch automatically when you sign in.", startup, v => { try { Startup.Set(runName, v); } catch { } });
            ui.Dropdown("Position", "Where the widget sits. It never covers taskbar icons: it shrinks or moves to free space. You can also drag it.", TaskbarWidget.AnchorNames, W.AnchorIndex, v => W.AnchorIndex = v);
            var displays = TaskbarWidget.DisplayNames();
            if (displays.Length > 1 || S.Int("display", 0) > 0)
                ui.Dropdown("Display", "Which taskbar to sit on (needs 'Show my taskbar on all displays').", displays, Math.Min(S.Int("display", 0), displays.Length - 1), v => S.Set("display", v));
            ui.EndCard();
        }

        protected void AppearanceSection(Ui ui)
        {
            var S = W.S;
            ui.Section("Appearance");
            ui.BeginCard();
            ui.Dropdown("Theme", null, Theme.ModeNames, S.Int("theme", 0), v => { S.Set("theme", v); W.ThemeChanged(); });
            ui.Dropdown("Accent color", "Used for the frame, toggles and highlights.", Theme.AccentNames, S.Int("accent", 0), v => { S.Set("accent", v); W.ThemeChanged(); });
            ui.Dropdown("Widget style", "Visual design: standard frame, frameless floating, capsules or neon.",
                new[] { "Standard (with frame)", "Frameless", "Capsules", "Neon" }, S.Int("style", 0), v => S.Set("style", v));
            ui.Slider("Widget background opacity", null, S.Int("opacity", 95), 20, 100, "%", v => S.Set("opacity", v));
            ui.Slider("Text size", null, S.Int("textScale", 100), 80, 130, "%", v => S.Set("textScale", v));
            ui.EndCard();
        }
    }

    /// <summary>Immediate-mode layout + drawing helper. Coordinates are in scrolled content space.</summary>
    internal sealed class Ui
    {
        public readonly Graphics G;
        public readonly bool Draw;
        public readonly PanelForm P;
        public Theme T { get { return P.T; } }
        public Fonts F { get { return P.F; } }
        public int X0, Width, Y;
        readonly int offsetY;
        readonly Rectangle clip;
        readonly Point mouse;
        int cardIndex, cardStartY, savedX, savedW, rows;
        bool inCard;
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

        public Ui(PanelForm p, Graphics g, bool draw, int offsetY, Rectangle clip, Point mouse)
        {
            P = p; G = g; Draw = draw; this.offsetY = offsetY; this.clip = clip; this.mouse = mouse;
        }

        public int Px(float v) { return P.P(v); }

        public Point MouseContent { get { return new Point(mouse.X, mouse.Y - offsetY); } }
        public bool Hovered(Rectangle r) { return clip.Contains(mouse) && r.Contains(MouseContent); }

        public void Hit(Rectangle r, Action click, Action right = null, Action<Point, Rectangle> drag = null)
        {
            if (!Draw) return;
            var cr = new Rectangle(r.X, r.Y + offsetY, r.Width, r.Height);
            cr.Intersect(clip);
            if (cr.Width <= 0 || cr.Height <= 0) return;
            // keep the full (unclipped) rect for drags so slider maths stay correct
            P.AddHit(new PanelForm.Hit { R = drag != null ? new Rectangle(r.X, r.Y + offsetY, r.Width, r.Height) : cr, Click = click, Right = right, Drag = drag });
        }

        public int TextH(string s, Font f, int width)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(G, s, f, new Size(width, int.MaxValue), Wrap).Height;
        }

        public int TextW(string s, Font f)
        {
            return TextRenderer.MeasureText(G, s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        }

        public void DrawText(string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            if (Draw && !string.IsNullOrEmpty(s)) TextRenderer.DrawText(G, s, f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        }

        public void Gap(float v) { Y += Px(v); }

        public void Section(string title)
        {
            Y += Px(8);
            int h = TextH(title.ToUpperInvariant(), F.Section, Width);
            DrawText(title.ToUpperInvariant(), F.Section, T.Text, new Rectangle(X0 + Px(2), Y, Width, h), TextFormatFlags.Left);
            Y += h + Px(9);
        }

        public void Label(string s, Font f, Color c)
        {
            RowStart();
            int h = TextH(s, f, Width);
            DrawText(s, f, c, new Rectangle(X0, Y, Width, h), Wrap);
            Y += h;
        }

        public void BeginCard()
        {
            cardStartY = Y;
            if (Draw && cardIndex < P.CardHeights.Count)
            {
                var r = new Rectangle(X0, Y, Width, P.CardHeights[cardIndex]);
                Gfx.Fill(G, r, Px(10), T.Card);
                Gfx.Stroke(G, r, Px(10), T.Border, 1);
            }
            savedX = X0; savedW = Width;
            X0 += Px(14); Width -= Px(28); Y += Px(13);
            inCard = true; rows = 0;
        }

        public void EndCard()
        {
            Y += Px(13);
            if (!Draw) P.CardHeights.Add(Y - cardStartY);
            cardIndex++;
            X0 = savedX; Width = savedW; inCard = false;
            Y += Px(10);
        }

        /// <summary>Plain card with custom content: returns the card rect for drawing; height must be known.</summary>
        public Rectangle Tile(int height, bool hoverable, Action click, Action right = null)
        {
            var r = new Rectangle(X0, Y, Width, height);
            if (Draw)
            {
                bool hv = hoverable && Hovered(r);
                Gfx.Fill(G, r, Px(10), hv ? T.CardHover : T.Card);
                Gfx.Stroke(G, r, Px(10), T.Border, 1);
                if (click != null || right != null) Hit(r, click, right);
            }
            Y += height + Px(8);
            return r;
        }

        public void RowStart()
        {
            if (inCard && rows > 0)
            {
                Y += Px(11);
                if (Draw) using (var p = new Pen(T.Border, 1)) G.DrawLine(p, X0 - Px(14), Y, X0 + Width + Px(14), Y);
                Y += Px(11);
            }
            rows++;
        }

        int TitleBlock(string title, string desc, int width, int y)
        {
            int h1 = TextH(title, F.Body, width);
            DrawText(title, F.Body, T.Text, new Rectangle(X0, y, width, h1), Wrap);
            if (string.IsNullOrEmpty(desc)) return h1;
            int h2 = TextH(desc, F.Small, width);
            DrawText(desc, F.Small, T.Sub, new Rectangle(X0, y + h1 + Px(3), width, h2), Wrap);
            return h1 + Px(3) + h2;
        }

        public void Toggle(string title, string desc, bool val, Action<bool> set)
        {
            RowStart();
            int tw = Px(44), th = Px(24);
            int textW = Width - tw - Px(14);
            int y0 = Y;
            int h = Math.Max(TitleBlock(title, desc, textW, y0), th);
            var tr = new Rectangle(X0 + Width - tw, y0 + (h - th) / 2, tw, th);
            if (Draw) DrawToggle(tr, val, Hovered(new Rectangle(X0, y0, Width, h)));
            Hit(new Rectangle(X0, y0, Width, h), () => set(!val));
            Y += h;
        }

        public void DrawToggle(Rectangle r, bool on, bool hover)
        {
            if (on) Gfx.Fill(G, r, r.Height / 2f, hover ? ControlPaint.Light(T.Accent, 0.15f) : T.Accent);
            else
            {
                Gfx.Fill(G, r, r.Height / 2f, hover ? T.CardHover : T.Card);
                Gfx.Stroke(G, r, r.Height / 2f, T.Sub, Math.Max(1f, P.K));
            }
            int d = r.Height - Px(on ? 8 : 10);
            int kx = on ? r.Right - Px(4) - d : r.X + Px(5);
            using (var b = new SolidBrush(on ? Color.White : T.Sub)) G.FillEllipse(b, kx, r.Y + (r.Height - d) / 2f, d, d);
        }

        public void Dropdown(string title, string desc, string[] opts, int sel, Action<int> set)
        {
            RowStart();
            Y += TitleBlock(title, desc, Width, Y) + Px(9);
            var box = new Rectangle(X0, Y, Width, Px(36));
            if (Draw)
            {
                bool hv = Hovered(box);
                Gfx.Fill(G, box, Px(7), hv ? Gfx.Mix(T.Input, T.Text, 0.06f) : T.Input);
                string txt = sel >= 0 && sel < opts.Length ? opts[sel] : "";
                DrawText(txt, F.Body, T.Text, new Rectangle(box.X + Px(12), box.Y, box.Width - Px(40), box.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                Gfx.Icon(G, Glyph.Chevron, new Rectangle(box.Right - Px(28), box.Y + Px(10), Px(16), Px(16)), T.Sub, 0);
                var screenRect = new Rectangle(box.X, box.Y + offsetY, box.Width, box.Height);
                Hit(box, () => P.ShowMenu(screenRect, opts, sel, set));
            }
            Y += box.Height;
        }

        public void Slider(string title, string desc, int val, int min, int max, string unit, Action<int> set)
        {
            RowStart();
            int vw = Px(70);
            int y0 = Y;
            int h = TitleBlock(title, desc, Width - vw, y0);
            DrawText(val + " " + unit, F.BodyBold, T.Text, new Rectangle(X0 + Width - vw, y0, vw, Px(20)), TextFormatFlags.Right);
            Y += h + Px(8);
            SliderBar(new Rectangle(X0, Y, Width, Px(22)), val, min, max, set);
            Y += Px(22);
        }

        public void SliderBar(Rectangle r, int val, int min, int max, Action<int> set)
        {
            int inset = Px(9);
            var track = new RectangleF(r.X + inset, r.Y + r.Height / 2f - Px(2), r.Width - 2 * inset, Px(4));
            float f = (val - min) / (float)Math.Max(1, max - min);
            if (Draw)
            {
                Gfx.Fill(G, track, Px(2), T.Track);
                Gfx.Fill(G, new RectangleF(track.X, track.Y, track.Width * f, track.Height), Px(2), T.Accent);
                float kx = track.X + track.Width * f, ky = r.Y + r.Height / 2f, kr = Px(8);
                using (var b = new SolidBrush(T.Card)) G.FillEllipse(b, kx - kr, ky - kr, kr * 2, kr * 2);
                using (var pen = new Pen(T.Accent, Px(3))) G.DrawEllipse(pen, kx - kr + Px(1), ky - kr + Px(1), kr * 2 - Px(2), kr * 2 - Px(2));
            }
            Hit(r, null, null, (p, hr) =>
            {
                float ff = (p.X - (hr.X + inset)) / (float)Math.Max(1, hr.Width - 2 * inset);
                ff = Math.Max(0, Math.Min(1, ff));
                set(min + (int)Math.Round(ff * (max - min)));
            });
        }

        public void Chips(string label, string[] opts, int sel, Action<int> set)
        {
            RowStart();
            int x = X0;
            int ch = Px(28);
            if (!string.IsNullOrEmpty(label))
            {
                DrawText(label, F.Small, T.Sub, new Rectangle(x, Y, Px(60), ch), TextFormatFlags.VerticalCenter);
                x += TextW(label, F.Small) + Px(10);
            }
            for (int i = 0; i < opts.Length; i++)
            {
                int w = TextW(opts[i], F.Small) + Px(24);
                if (x + w > X0 + Width) { x = X0; Y += ch + Px(6); }
                var r = new Rectangle(x, Y, w, ch);
                if (Draw)
                {
                    bool on = i == sel, hv = Hovered(r);
                    Gfx.Fill(G, r, ch / 2f, on ? T.Accent : (hv ? T.CardHover : T.Input));
                    DrawText(opts[i], F.Small, on ? Color.White : T.Text, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                int idx = i;
                Hit(r, () => set(idx));
                x += w + Px(8);
            }
            Y += ch;
        }

        public void Button(string text, bool primary, Action click)
        {
            RowStart();
            var r = new Rectangle(X0, Y, Width, Px(40));
            if (Draw)
            {
                bool hv = Hovered(r);
                Color bg = primary ? (hv ? ControlPaint.Light(T.Accent, 0.12f) : T.Accent) : (hv ? T.CardHover : T.Input);
                Gfx.Fill(G, r, Px(8), bg);
                DrawText(text, F.BodyBold, primary ? Color.White : T.Text, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            Hit(r, click);
            Y += r.Height;
        }

        public void Expander(string title, bool open, Action<bool> set)
        {
            var r = new Rectangle(X0, Y, Width, Px(26));
            if (Draw)
            {
                var gr = new RectangleF(r.X, r.Y + Px(7), Px(12), Px(12));
                var st = G.Save();
                G.TranslateTransform(gr.X + gr.Width / 2, gr.Y + gr.Height / 2);
                if (!open) G.RotateTransform(-90);
                Gfx.Icon(G, Glyph.Chevron, new RectangleF(-gr.Width / 2, -gr.Height / 2, gr.Width, gr.Height), T.Sub, 0);
                G.Restore(st);
                DrawText(title, F.Body, Hovered(r) ? T.Text : T.Sub, new Rectangle(r.X + Px(18), r.Y, r.Width - Px(18), r.Height), TextFormatFlags.VerticalCenter);
            }
            Hit(r, () => set(!open));
            Y += r.Height + Px(4);
        }
    }
}
