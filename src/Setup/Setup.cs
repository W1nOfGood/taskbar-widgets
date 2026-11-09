using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarWidgets.Setup
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool uninstall = args.Contains("--uninstall"), quiet = args.Contains("--quiet") || args.Contains("/S");
            if (quiet)
            {
                // unattended: "TaskbarWidgetsSetup.exe --quiet" installs with defaults, "--uninstall --quiet" removes
                try
                {
                    if (uninstall) Installer.Uninstall(args.Contains("--delete-settings"), null);
                    else { var o = Installer.Options.Defaults(); Installer.Install(o, null); Installer.Launch(o); }
                    return 0;
                }
                catch { return 1; }
            }
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(uninstall));
            return 0;
        }
    }

    /// <summary>Per-user install (no admin): files, shortcuts, startup entries and the "Installed apps" entry.</summary>
    internal static class Installer
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static readonly string[] Titles = { "Battery widget", "Volume & Media" };

        public sealed class Options
        {
            public bool Battery = true, Volume = true, Startup = true, StartMenu = true, Desktop;
            public string Dir;
            public bool Want(int i) { return i == 0 ? Battery : Volume; }

            public static Options Defaults()
            {
                var o = new Options { Dir = InstalledDir ?? DefaultDir };
                if (InstalledDir != null)
                {
                    // keep what the user chose last time
                    o.Battery = File.Exists(Path.Combine(o.Dir, AppInfo.Exes[0]));
                    o.Volume = File.Exists(Path.Combine(o.Dir, AppInfo.Exes[1]));
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        o.Startup = k != null && AppInfo.RunNames.Any(n => k.GetValue(n) != null);
                    o.Desktop = AppInfo.Exes.Any(e => File.Exists(DesktopLink(e)));
                    o.StartMenu = Directory.Exists(MenuDir);
                    if (!o.Battery && !o.Volume) o.Battery = o.Volume = true;
                }
                return o;
            }
        }

        public static string DefaultDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Name); }
        }

        public static string InstalledDir
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKey))
                {
                    var d = k == null ? null : k.GetValue("InstallLocation") as string;
                    return d != null && Directory.Exists(d) ? d : null;
                }
            }
        }

        public static string InstalledVersion
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKey)) return k == null ? null : k.GetValue("DisplayVersion") as string; }
        }

        static string MenuDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name); } }
        static string DesktopLink(string exe) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Titles[Array.IndexOf(AppInfo.Exes, exe)] + ".lnk"); }

        public static void Install(Options o, Action<string, int> progress)
        {
            Action<string, int> step = (s, p) => { if (progress != null) progress(s, p); };
            step("Closing running widgets…", 5);
            CloseWidgets();

            step("Copying files…", 25);
            Directory.CreateDirectory(o.Dir);
            for (int i = 0; i < AppInfo.Exes.Length; i++)
            {
                string dst = Path.Combine(o.Dir, AppInfo.Exes[i]);
                if (o.Want(i)) WriteResource("TaskbarWidgets." + AppInfo.Exes[i], dst);
                else if (File.Exists(dst)) TryDelete(dst);
            }
            WriteResource("TaskbarWidgets.LICENSE", Path.Combine(o.Dir, "LICENSE.txt"));
            string self = Application.ExecutablePath, uninst = Path.Combine(o.Dir, "Uninstall.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninst, true);
            foreach (var old in Directory.GetFiles(o.Dir, "*.old")) TryDelete(old);

            step("Creating shortcuts…", 55);
            if (Directory.Exists(MenuDir)) Directory.Delete(MenuDir, true);
            if (o.StartMenu)
            {
                Directory.CreateDirectory(MenuDir);
                for (int i = 0; i < AppInfo.Exes.Length; i++)
                    if (o.Want(i)) Shortcut(Path.Combine(MenuDir, Titles[i] + ".lnk"), Path.Combine(o.Dir, AppInfo.Exes[i]), "", Titles[i]);
                Shortcut(Path.Combine(MenuDir, "Uninstall " + AppInfo.Name + ".lnk"), uninst, "--uninstall", "Remove " + AppInfo.Name);
            }
            for (int i = 0; i < AppInfo.Exes.Length; i++)
            {
                string link = DesktopLink(AppInfo.Exes[i]);
                if (o.Desktop && o.Want(i)) Shortcut(link, Path.Combine(o.Dir, AppInfo.Exes[i]), "", Titles[i]);
                else TryDelete(link);
            }

            step("Registering…", 75);
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                for (int i = 0; i < AppInfo.Exes.Length; i++)
                {
                    if (o.Startup && o.Want(i)) run.SetValue(AppInfo.RunNames[i], "\"" + Path.Combine(o.Dir, AppInfo.Exes[i]) + "\"");
                    else if (run.GetValue(AppInfo.RunNames[i]) != null) run.DeleteValue(AppInfo.RunNames[i]);
                }
            using (var own = Registry.CurrentUser.CreateSubKey(AppInfo.OwnKey))
            {
                own.SetValue("StartupChosen", o.Startup ? 1 : 0);
                own.SetValue("InstallDir", o.Dir);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(AppInfo.UninstallKey))
            {
                k.SetValue("DisplayName", AppInfo.Name);
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", AppInfo.Repo.Split('/')[0]);
                k.SetValue("DisplayIcon", Path.Combine(o.Dir, o.Battery ? AppInfo.Exes[0] : AppInfo.Exes[1]) + ",0");
                k.SetValue("InstallLocation", o.Dir);
                k.SetValue("UninstallString", "\"" + uninst + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" --uninstall --quiet");
                k.SetValue("ModifyPath", "\"" + uninst + "\"");
                k.SetValue("URLInfoAbout", "https://github.com/" + AppInfo.Repo);
                k.SetValue("HelpLink", "https://github.com/" + AppInfo.Repo + "/issues");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long bytes = Directory.GetFiles(o.Dir).Sum(f => new FileInfo(f).Length);
                k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            }
            step("Done", 100);
        }

        public static void Launch(Options o)
        {
            for (int i = 0; i < AppInfo.Exes.Length; i++)
                if (o.Want(i))
                    try { Process.Start(new ProcessStartInfo(Path.Combine(o.Dir, AppInfo.Exes[i])) { UseShellExecute = true, WorkingDirectory = o.Dir }); } catch { }
        }

        public static void Uninstall(bool deleteSettings, Action<string, int> progress)
        {
            Action<string, int> step = (s, p) => { if (progress != null) progress(s, p); };
            string dir = InstalledDir;
            step("Closing the widgets…", 10);
            CloseWidgets(); // graceful: they put any lowered music volume back first

            step("Removing startup entries and shortcuts…", 40);
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (run != null) foreach (var n in AppInfo.RunNames) if (run.GetValue(n) != null) run.DeleteValue(n);
            try { if (Directory.Exists(MenuDir)) Directory.Delete(MenuDir, true); } catch { }
            foreach (var e in AppInfo.Exes) TryDelete(DesktopLink(e));
            try { Registry.CurrentUser.DeleteSubKeyTree(AppInfo.UninstallKey, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(AppInfo.OwnKey, false); } catch { }

            step("Removing files…", 70);
            if (deleteSettings)
                try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarWidgets"), true); } catch { }
            if (dir != null)
            {
                // only what we installed: never anything else that happens to live in that folder
                string self = Path.GetFullPath(Application.ExecutablePath);
                var ours = AppInfo.Exes.Concat(new[] { "LICENSE.txt", "Uninstall.exe" }).Select(n => Path.Combine(dir, n)).ToList();
                ours.AddRange(Directory.GetFiles(dir, "*.old"));
                ours.AddRange(Directory.GetFiles(dir, "*.new"));
                foreach (var f in ours)
                    if (!string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
                // we may be running from that folder (Uninstall.exe): remove ourselves and then the folder (only if
                // it is empty: rmdir without /s) a moment after we exit
                string cmd = "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + Path.Combine(dir, "Uninstall.exe") + "\" & rmdir \"" + dir + "\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            step("Done", 100);
        }

        /// <summary>Asks running widgets to close (WM_CLOSE: they restore volumes), then makes sure they're gone.</summary>
        static void CloseWidgets()
        {
            for (int slot = 0; slot < AppInfo.Exes.Length; slot++)
            {
                IntPtr w = Native.FindWindow(null, "TaskbarWidgets.Slot" + slot);
                if (w != IntPtr.Zero) Native.PostMessage(w, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
            var until = DateTime.Now.AddSeconds(6);
            foreach (var exe in AppInfo.Exes)
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                    using (p)
                    {
                        try
                        {
                            int left = Math.Max(0, (int)(until - DateTime.Now).TotalMilliseconds);
                            if (!p.WaitForExit(left)) { p.Kill(); p.WaitForExit(2000); }
                        }
                        catch { }
                    }
        }

        static void WriteResource(string name, string dst)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new Exception("installer is missing " + name);
                string tmp = dst + ".new";
                using (var f = File.Create(tmp)) s.CopyTo(f);
                if (File.Exists(dst))
                {
                    try { File.Delete(dst); }
                    catch { File.Move(dst, dst + "." + DateTime.Now.Ticks + ".old"); } // still locked: move it aside
                }
                File.Move(tmp, dst);
            }
        }

        static void Shortcut(string link, string target, string args, string description)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }

    /// <summary>The setup window: same look as the widgets' panels, painted by hand.</summary>
    internal sealed class SetupForm : Form
    {
        enum Page { Options, Working, Done, Uninstall, Removed, Failed }
        Page page;
        readonly Installer.Options o = Installer.Options.Defaults();
        bool deleteSettings;
        string status = "", error = "";
        int progress;
        readonly string installedVersion = Installer.InstalledDir != null ? Installer.InstalledVersion : null;
        readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();
        Point mouse = new Point(-1, -1);
        float K = 1;
        bool dark;
        Color bg, card, border, text, sub, faint, input, accent = Color.FromArgb(168, 108, 62), good = Color.FromArgb(34, 197, 140);
        Font fTitle, fBody, fBold, fSmall;

        public SetupForm(bool uninstall)
        {
            page = uninstall ? Page.Uninstall : Page.Options;
            Text = AppInfo.Name + (uninstall ? " – Uninstall" : " Setup");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            dark = SystemDark();
            bg = dark ? Color.FromArgb(30, 30, 32) : Color.FromArgb(243, 243, 245);
            card = dark ? Color.FromArgb(40, 40, 43) : Color.White;
            border = dark ? Color.FromArgb(56, 56, 61) : Color.FromArgb(222, 222, 228);
            text = dark ? Color.FromArgb(242, 242, 244) : Color.FromArgb(24, 24, 27);
            sub = dark ? Color.FromArgb(160, 160, 166) : Color.FromArgb(96, 96, 104);
            faint = dark ? Color.FromArgb(110, 110, 116) : Color.FromArgb(150, 150, 158);
            input = dark ? Color.FromArgb(50, 50, 55) : Color.FromArgb(236, 236, 240);
            BackColor = bg;
        }

        static bool SystemDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return !(k != null && k.GetValue("AppsUseLightTheme") is int && (int)k.GetValue("AppsUseLightTheme") == 1);
            }
            catch { return true; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            uint dpi = Native.GetDpiForWindow(Handle);
            K = (dpi == 0 ? 96 : dpi) / 96f;
            ClientSize = new Size(P(460), P(page == Page.Uninstall ? 330 : 560));
            CenterToScreen();
            int d = dark ? 1 : 0;
            Native.DwmSetWindowAttribute(Handle, 20, ref d, 4); // dark title bar
            const string ff = "Segoe UI Variable Text";
            fTitle = new Font(ff, 21 * K, FontStyle.Bold, GraphicsUnit.Pixel);
            fBody = new Font(ff, 14 * K, FontStyle.Regular, GraphicsUnit.Pixel);
            fBold = new Font(ff, 14 * K, FontStyle.Bold, GraphicsUnit.Pixel);
            fSmall = new Font(ff, 12 * K, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        int P(float v) { return (int)Math.Round(v * K); }

        // ---------------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(bg);
            hits.Clear();
            int pad = P(24), w = ClientSize.Width - 2 * pad, y = P(24);

            // header: both app icons + name
            AppIcon(g, new Rectangle(pad, y, P(46), P(46)), true);
            AppIcon(g, new Rectangle(pad + P(54), y, P(46), P(46)), false);
            int tx = pad + P(116);
            Txt(g, AppInfo.Name, fTitle, text, new Rectangle(tx, y - P(2), w, P(30)));
            Txt(g, "Version " + AppInfo.Version + " · free & open source", fSmall, sub, new Rectangle(tx, y + P(28), w, P(20)));
            y += P(74);

            switch (page)
            {
                case Page.Options: PaintOptions(g, pad, w, y); break;
                case Page.Working: PaintWorking(g, pad, w, y); break;
                case Page.Done: PaintMessage(g, pad, w, y, true, installedVersion == null ? "Installed!" : "Updated!",
                    "The widgets are on your taskbar now. You'll also find them in the Start menu, and they update themselves.",
                    new[] { "Close" }, new Action[] { Close }); break;
                case Page.Uninstall: PaintUninstall(g, pad, w, y); break;
                case Page.Removed: PaintMessage(g, pad, w, y, true, "Removed", AppInfo.Name + " was uninstalled. Thanks for trying it!",
                    new[] { "Close" }, new Action[] { Close }); break;
                case Page.Failed: PaintMessage(g, pad, w, y, false, "Something went wrong", error,
                    new[] { "Close" }, new Action[] { Close }); break;
            }
        }

        void PaintOptions(Graphics g, int pad, int w, int y)
        {
            if (installedVersion != null)
            {
                var note = new Rectangle(pad, y, w, P(40));
                Fill(g, note, P(8), Color.FromArgb(dark ? 50 : 30, accent));
                Txt(g, "Version " + installedVersion + " is installed. Install again to update or change options.", fSmall, text,
                    Rectangle.Inflate(note, -P(12), 0), TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                y += P(52);
            }
            var rows = new[]
            {
                new { T = "Battery widget", D = "Battery of your headphones, mouse, controllers…", V = o.Battery, A = (Action)(() => o.Battery = !o.Battery || !o.Volume) },
                new { T = "Volume & Media widget", D = "Now playing, per-app volume, study mode…", V = o.Volume, A = (Action)(() => o.Volume = !o.Volume || !o.Battery) },
                new { T = "Start with Windows", D = "Recommended: the widgets are simply there.", V = o.Startup, A = (Action)(() => o.Startup = !o.Startup) },
                new { T = "Start menu shortcuts", D = null as string, V = o.StartMenu, A = (Action)(() => o.StartMenu = !o.StartMenu) },
                new { T = "Desktop shortcuts", D = null as string, V = o.Desktop, A = (Action)(() => o.Desktop = !o.Desktop) },
            };
            int rowH = P(52), cardH = rows.Length * rowH;
            var c = new Rectangle(pad, y, w, cardH);
            Fill(g, c, P(10), card); Stroke(g, c, P(10), border);
            for (int i = 0; i < rows.Length; i++)
            {
                var r = new Rectangle(pad, y + i * rowH, w, rowH);
                if (i > 0) using (var pen = new Pen(border)) g.DrawLine(pen, r.Left, r.Top, r.Right, r.Top);
                bool hv = r.Contains(mouse);
                int ty = rows[i].D == null ? r.Y + (rowH - P(20)) / 2 : r.Y + P(8);
                Txt(g, rows[i].T, fBody, text, new Rectangle(r.X + P(14), ty, w - P(80), P(20)));
                if (rows[i].D != null) Txt(g, rows[i].D, fSmall, sub, new Rectangle(r.X + P(14), r.Y + P(28), w - P(80), P(18)));
                Toggle(g, new Rectangle(r.Right - P(58), r.Y + (rowH - P(24)) / 2, P(44), P(24)), rows[i].V, hv);
                var act = rows[i].A;
                hits.Add(new KeyValuePair<Rectangle, Action>(r, () => { act(); Invalidate(); }));
            }
            y += cardH + P(16);

            // folder
            Txt(g, "Install folder", fSmall, sub, new Rectangle(pad, y, w, P(18)));
            y += P(20);
            var box = new Rectangle(pad, y, w, P(34));
            Fill(g, box, P(7), input);
            var change = new Rectangle(box.Right - P(80), box.Y, P(80), box.Height);
            Txt(g, o.Dir, fSmall, text, new Rectangle(box.X + P(10), box.Y, box.Width - P(96), box.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis);
            Txt(g, "Change…", fSmall, change.Contains(mouse) ? text : accent, change, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            hits.Add(new KeyValuePair<Rectangle, Action>(change, PickFolder));
            y += P(48);
            Txt(g, "Installs just for you: no admin rights needed. Uninstall anytime from Settings → Apps.", fSmall, faint, new Rectangle(pad, y, w, P(36)), TextFormatFlags.WordBreak);

            // buttons
            int by = ClientSize.Height - pad - P(40);
            var primary = new Rectangle(pad + w - P(150), by, P(150), P(40));
            var second = new Rectangle(primary.X - P(130), by, P(120), P(40));
            Button(g, primary, installedVersion == null ? "Install" : "Update", true, StartInstall);
            if (installedVersion != null) Button(g, second, "Uninstall", false, () => { page = Page.Uninstall; Invalidate(); });
            else Button(g, second, "Cancel", false, Close);
        }

        void PaintWorking(Graphics g, int pad, int w, int y)
        {
            y += P(40);
            Txt(g, status, fBody, text, new Rectangle(pad, y, w, P(22)));
            y += P(36);
            var track = new Rectangle(pad, y, w, P(6));
            Fill(g, track, P(3), input);
            Fill(g, new Rectangle(track.X, track.Y, Math.Max(P(6), track.Width * progress / 100), track.Height), P(3), accent);
        }

        void PaintUninstall(Graphics g, int pad, int w, int y)
        {
            Txt(g, "Uninstall " + AppInfo.Name + "?", fBold, text, new Rectangle(pad, y, w, P(22)));
            Txt(g, "Removes both widgets, their shortcuts, the startup entries and the entry in Settings → Apps. Any music volume the widget lowered is put back.",
                fSmall, sub, new Rectangle(pad, y + P(26), w, P(52)), TextFormatFlags.WordBreak);
            y += P(88);
            var r = new Rectangle(pad, y, w, P(48));
            Fill(g, r, P(10), card); Stroke(g, r, P(10), border);
            Txt(g, "Also delete my settings", fBody, text, new Rectangle(r.X + P(14), r.Y, w - P(80), r.Height), TextFormatFlags.VerticalCenter);
            Toggle(g, new Rectangle(r.Right - P(58), r.Y + P(12), P(44), P(24)), deleteSettings, r.Contains(mouse));
            hits.Add(new KeyValuePair<Rectangle, Action>(r, () => { deleteSettings = !deleteSettings; Invalidate(); }));

            int by = ClientSize.Height - pad - P(40);
            var primary = new Rectangle(pad + w - P(150), by, P(150), P(40));
            Button(g, primary, "Uninstall", true, StartUninstall);
            Button(g, new Rectangle(primary.X - P(130), by, P(120), P(40)), "Cancel", false, Close);
        }

        void PaintMessage(Graphics g, int pad, int w, int y, bool ok, string title, string body, string[] buttons, Action[] actions)
        {
            y += P(10);
            var ic = new Rectangle(pad, y, P(40), P(40));
            using (var b = new SolidBrush(ok ? good : Color.FromArgb(239, 68, 68))) g.FillEllipse(b, ic);
            using (var pen = new Pen(Color.White, P(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (ok) g.DrawLines(pen, new[] { new Point(ic.X + P(11), ic.Y + P(21)), new Point(ic.X + P(18), ic.Y + P(28)), new Point(ic.X + P(30), ic.Y + P(13)) });
                else { g.DrawLine(pen, ic.X + P(13), ic.Y + P(13), ic.Right - P(13), ic.Bottom - P(13)); g.DrawLine(pen, ic.Right - P(13), ic.Y + P(13), ic.X + P(13), ic.Bottom - P(13)); }
            }
            Txt(g, title, fBold, text, new Rectangle(ic.Right + P(14), y + P(2), w - P(60), P(22)));
            Txt(g, body, fSmall, sub, new Rectangle(ic.Right + P(14), y + P(26), w - P(60), P(80)), TextFormatFlags.WordBreak);
            int by = ClientSize.Height - P(24) - P(40);
            Button(g, new Rectangle(pad + w - P(150), by, P(150), P(40)), buttons[0], true, actions[0]);
        }

        // ---------------------------------------------------------------- actions

        void PickFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Where should " + AppInfo.Name + " be installed?", SelectedPath = Directory.Exists(o.Dir) ? o.Dir : Path.GetDirectoryName(o.Dir) })
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    string pick = d.SelectedPath;
                    if (!pick.TrimEnd('\\').EndsWith(AppInfo.Name, StringComparison.OrdinalIgnoreCase)) pick = Path.Combine(pick, AppInfo.Name);
                    o.Dir = pick;
                    Invalidate();
                }
        }

        void StartInstall()
        {
            page = Page.Working; status = "Starting…"; progress = 0; Invalidate();
            Task.Run(() => Installer.Install(o, (s, p) => BeginInvoke(new Action(() => { status = s; progress = p; Invalidate(); }))))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) { error = t.Exception.GetBaseException().Message; page = Page.Failed; }
                    else { Installer.Launch(o); page = Page.Done; }
                    Invalidate();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void StartUninstall()
        {
            page = Page.Working; status = "Starting…"; progress = 0;
            ClientSize = new Size(ClientSize.Width, P(330));
            Invalidate();
            Task.Run(() => Installer.Uninstall(deleteSettings, (s, p) => BeginInvoke(new Action(() => { status = s; progress = p; Invalidate(); }))))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) { error = t.Exception.GetBaseException().Message; page = Page.Failed; }
                    else page = Page.Removed;
                    Invalidate();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------------------------------------------------------------- input

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            bool over = hits.Any(h => h.Key.Contains(e.Location));
            Cursor = over ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); mouse = new Point(-1, -1); Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (var h in hits.ToList()) if (h.Key.Contains(e.Location)) { h.Value(); return; }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && page != Page.Working) Close();
        }

        // ---------------------------------------------------------------- drawing helpers

        void AppIcon(Graphics g, Rectangle r, bool battery)
        {
            Gfx.Fill(g, r, r.Width * 0.24f, accent);
            float s = r.Width;
            if (battery)
            {
                Gfx.Icon(g, Glyph.Headphones, new RectangleF(r.X + s * 0.16f, r.Y + s * 0.1f, s * 0.68f, s * 0.5f), Color.White, 1);
                Gfx.Battery(g, new RectangleF(r.X + s * 0.22f, r.Y + s * 0.64f, s * 0.56f, s * 0.22f), 70, good, Color.White, false, false);
            }
            else Gfx.Icon(g, Glyph.Volume, new RectangleF(r.X + s * 0.18f, r.Y + s * 0.18f, s * 0.64f, s * 0.64f), Color.White, 1);
        }

        void Toggle(Graphics g, Rectangle r, bool on, bool hover)
        {
            if (on) Gfx.Fill(g, r, r.Height / 2f, hover ? ControlPaint.Light(accent, 0.15f) : accent);
            else { Gfx.Fill(g, r, r.Height / 2f, hover ? input : card); Gfx.Stroke(g, r, r.Height / 2f, sub, Math.Max(1f, K)); }
            int d = r.Height - P(on ? 8 : 10);
            int kx = on ? r.Right - P(4) - d : r.X + P(5);
            using (var b = new SolidBrush(on ? Color.White : sub)) g.FillEllipse(b, kx, r.Y + (r.Height - d) / 2f, d, d);
        }

        void Button(Graphics g, Rectangle r, string label, bool primary, Action a)
        {
            bool hv = r.Contains(mouse);
            Color c = primary ? (hv ? ControlPaint.Light(accent, 0.12f) : accent) : (hv ? border : input);
            Fill(g, r, P(8), c);
            Txt(g, label, fBold, primary ? Color.White : text, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            hits.Add(new KeyValuePair<Rectangle, Action>(r, a));
        }

        static void Fill(Graphics g, Rectangle r, float rad, Color c) { Gfx.Fill(g, r, rad, c); }
        void Stroke(Graphics g, Rectangle r, float rad, Color c) { Gfx.Stroke(g, r, rad, c, Math.Max(1f, K)); }

        static void Txt(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags extra = TextFormatFlags.Default)
        {
            TextRenderer.DrawText(g, s, f, r, c, extra | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
