using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TaskbarWidgets
{
    /// <summary>
    /// Checks GitHub for a newer release (once a day, nothing is sent but the request itself) and can install it:
    /// download → verify SHA-256 (GitHub's published digest) → swap the .exe files (running ones are renamed, with
    /// rollback on any error) → restart both widgets. Shared by both apps; only one installs at a time.
    /// </summary>
    internal sealed class Updater
    {
        public static Updater Instance;
        public event Action Changed;
        public readonly Settings S = new Settings("updates");
        public string Latest, PageUrl, Status = "";
        public bool Available, Busy;
        string zipUrl, zipSha;
        readonly TaskbarWidget host;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };

        public Updater(TaskbarWidget host)
        {
            this.host = host;
            Instance = this;
            timer.Tick += (o, e) => { if (Due) CheckNow(false); };
        }

        public bool CheckEnabled { get { return S.Bool("check", true); } set { S.Set("check", value); } }
        public bool AutoInstall { get { return S.Bool("auto", true); } set { S.Set("auto", value); } }

        bool Due
        {
            get
            {
                long t; long.TryParse(S.Get("last", "0"), out t);
                return CheckEnabled && (DateTime.UtcNow - new DateTime(Math.Max(0, t), DateTimeKind.Utc)).TotalHours >= 20;
            }
        }

        public void Start()
        {
            if (Demo.On) return;
            CleanupOldFiles();
            timer.Start();
            var first = new System.Windows.Forms.Timer { Interval = 45000 }; // don't compete with startup
            first.Tick += (o, e) => { first.Dispose(); if (Due) CheckNow(false); };
            first.Start();
        }

        void Raise() { if (Changed != null) Changed(); }

        void Post(Action a) { if (!host.IsDisposed) try { host.BeginInvoke(a); } catch { } }

        // ---------------------------------------------------------------- check

        public void CheckNow(bool user)
        {
            if (Busy) return;
            Busy = true; Status = "Checking for updates…"; Raise();
            Task.Run(() => Fetch()).ContinueWith(t =>
            {
                Busy = false;
                if (t.IsFaulted)
                {
                    Status = "Couldn't check for updates (" + t.Exception.GetBaseException().Message + ")";
                    Raise();
                    return;
                }
                S.Set("last", DateTime.UtcNow.Ticks.ToString());
                if (!Available) { Status = "You're on the latest version (" + AppInfo.Current + ")."; Raise(); return; }
                Status = "Version " + Latest + " is available.";
                Raise();
                if (AutoInstall && !user) Install();
                else if (S.Get("notified", "") != Latest)
                {
                    S.Set("notified", Latest);
                    host.Notify("Taskbar Widgets " + Latest + " is available", "Open the widget's settings to install it.");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Fetch()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string json;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "TaskbarWidgets-Updater";
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = wc.DownloadString("https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
            }
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!tag.Success) throw new Exception("unexpected reply");
            Latest = tag.Groups[1].Value.TrimStart('v', 'V');
            PageUrl = "https://github.com/" + AppInfo.Repo + "/releases/tag/" + tag.Groups[1].Value;
            zipUrl = null; zipSha = null;
            // Our zip's asset: from its "name" to its "browser_download_url" (the asset's last field). The asset object
            // contains a nested "uploader" object, so it can't be matched as one flat {...} block.
            var nm = Regex.Match(json, "\"name\"\\s*:\\s*\"" + Regex.Escape(AppInfo.ZipName) + "\"");
            if (nm.Success)
            {
                string rest = json.Substring(nm.Index);
                var u = Regex.Match(rest, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"");
                if (u.Success)
                {
                    zipUrl = u.Groups[1].Value;
                    var d = Regex.Match(rest.Substring(0, u.Index), "\"digest\"\\s*:\\s*\"sha256:([0-9a-fA-F]{64})\"");
                    if (d.Success) zipSha = d.Groups[1].Value.ToLowerInvariant();
                }
            }
            Available = Newer(Latest, AppInfo.Current) && zipUrl != null &&
                        zipUrl.StartsWith("https://github.com/" + AppInfo.Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        static bool Newer(string a, string b)
        {
            Version va, vb;
            return Version.TryParse(Norm(a), out va) && Version.TryParse(Norm(b), out vb) && va > vb;
        }

        static string Norm(string v)
        {
            v = Regex.Match(v ?? "", @"\d+(\.\d+){0,3}").Value;
            return v.Contains(".") ? v : v + ".0";
        }

        // ---------------------------------------------------------------- install

        public void Install()
        {
            if (Busy || !Available) return;
            Busy = true; Status = "Downloading " + Latest + "…"; Raise();
            string ver = Latest;
            Task.Run(() => Download(ver)).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Busy = false;
                    Status = "Update failed: " + t.Exception.GetBaseException().Message;
                    Log.Write("update", t.Exception.GetBaseException());
                    host.Notify("Couldn't update Taskbar Widgets", "Click to download " + ver + " from GitHub.", () => OpenPage());
                    Raise();
                    return;
                }
                if (t.Result == null) { Busy = false; Raise(); return; } // another widget is installing it
                Status = "Installing " + ver + "…"; Raise();
                SwapAndRestart(t.Result, ver);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void OpenPage() { try { Process.Start(new ProcessStartInfo(PageUrl ?? ("https://github.com/" + AppInfo.Repo + "/releases")) { UseShellExecute = true }); } catch { } }

        static Mutex installLock;

        /// <summary>Downloads and unpacks the release into a temp folder; returns that folder (null if someone else is updating).</summary>
        string Download(string ver)
        {
            bool mine;
            installLock = new Mutex(false, "TaskbarWidgets.Updating");
            try { mine = installLock.WaitOne(0); } catch (AbandonedMutexException) { mine = true; }
            if (!mine) return null;
            string dir = Path.Combine(Path.GetTempPath(), "TaskbarWidgets-update-" + ver);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, AppInfo.ZipName);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "TaskbarWidgets-Updater";
                wc.DownloadFile(zipUrl, zip);
            }
            if (zipSha != null)
            {
                string got;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(zip))
                    got = string.Concat(sha.ComputeHash(fs).Select(b => b.ToString("x2")));
                if (got != zipSha) throw new Exception("the download is damaged (checksum mismatch)");
            }
            string ext = Path.Combine(dir, "files");
            ZipFile.ExtractToDirectory(zip, ext);
            if (!AppInfo.Exes.Any(e => File.Exists(Path.Combine(ext, e)))) throw new Exception("the download has no apps in it");
            return ext;
        }

        /// <summary>Replaces the exes next to this one (renaming the running ones), then restarts the widgets.</summary>
        void SwapAndRestart(string ext, string ver)
        {
            string appDir = Path.GetDirectoryName(Application.ExecutablePath);
            var done = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var exe in AppInfo.Exes)
                {
                    string src = Path.Combine(ext, exe), dst = Path.Combine(appDir, exe);
                    if (!File.Exists(src)) continue;
                    string old = dst + ".old";
                    if (File.Exists(old)) try { File.Delete(old); } catch { old = dst + "." + DateTime.Now.Ticks + ".old"; }
                    if (File.Exists(dst)) File.Move(dst, old);      // allowed even while it runs
                    File.Copy(src, dst);
                    done.Add(exe);
                }
            }
            catch (Exception ex)
            {
                // put everything back
                foreach (var exe in done)
                {
                    string dst = Path.Combine(appDir, exe);
                    try { File.Delete(dst); } catch { }
                }
                foreach (var exe in AppInfo.Exes)
                {
                    string dst = Path.Combine(appDir, exe), old = dst + ".old";
                    if (!File.Exists(dst) && File.Exists(old)) try { File.Move(old, dst); } catch { }
                }
                Busy = false;
                Status = ex is UnauthorizedAccessException ? "Can't update this folder (no permission). Download it from GitHub instead." : "Update failed: " + ex.Message;
                Log.Write("update", ex);
                host.Notify("Couldn't update Taskbar Widgets", "Click to download " + ver + " from GitHub.", () => OpenPage());
                Raise();
                return;
            }

            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKey, true))
                    if (k != null && string.Equals(((k.GetValue("InstallLocation") as string) ?? "").TrimEnd('\\'), appDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        k.SetValue("DisplayVersion", ver);
            }
            catch { }

            // restart the other widget (if it runs) and then ourselves; new instances wait for the old ones to exit
            int self = Process.GetCurrentProcess().Id;
            for (int slot = 0; slot < AppInfo.Exes.Length; slot++)
            {
                if (slot == host.Slot) continue;
                IntPtr w = Native.FindWindow(null, "TaskbarWidgets.Slot" + slot);
                if (w == IntPtr.Zero) continue;
                uint pid; Native.GetWindowThreadProcessId(w, out pid);
                Native.PostMessage(w, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero); // graceful: restores volumes
                Process.Start(new ProcessStartInfo(Path.Combine(appDir, AppInfo.Exes[slot]), "--wait " + pid) { UseShellExecute = false });
            }
            Process.Start(new ProcessStartInfo(Path.Combine(appDir, AppInfo.Exes[host.Slot]), "--wait " + self + " --updated " + ver) { UseShellExecute = false });
            try { installLock.ReleaseMutex(); } catch { }
            Application.Exit();
        }

        static void CleanupOldFiles()
        {
            try
            {
                string appDir = Path.GetDirectoryName(Application.ExecutablePath);
                foreach (var f in Directory.GetFiles(appDir, "*.old")) try { File.Delete(f); } catch { }
            }
            catch { }
        }

        /// <summary>Program.Main helper: "--wait PID" waits for the previous instance to exit first.</summary>
        public static void WaitForPrevious(string[] args)
        {
            int i = Array.IndexOf(args, "--wait"), pid;
            if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out pid)) return;
            try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(15000); } catch { }
        }

        public static string UpdatedTo(string[] args)
        {
            int i = Array.IndexOf(args, "--updated");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
