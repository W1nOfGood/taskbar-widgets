using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarWidgets
{
    /// <summary>
    /// Brings a widget back after a crash. Some crashes (heap corruption inside Windows' media or audio code)
    /// end the process instantly, so nothing inside it can catch them. Two layers:
    /// 1. Windows is asked to restart the app after a crash or hang (it does that once the app ran 60 s).
    /// 2. Each widget watches its sibling. A widget leaves a "running" marker while it runs and deletes it
    ///    when it exits normally, so a marker without a process means it crashed: the sibling starts it again.
    /// Exiting from the menu, updates, the installer and Windows shutdown all exit normally, so they never
    /// trigger a restart.
    /// </summary>
    internal static class Watchdog
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int RegisterApplicationRestart(string cmdLine, int flags);
        const int RESTART_NO_PATCH = 4, RESTART_NO_REBOOT = 8; // restart after a crash or hang only

        static int slot = -1;
        static System.Windows.Forms.Timer timer;
        static readonly List<DateTime> restarts = new List<DateTime>();
        static readonly string[] Exes = AppInfo.Exes;

        static string Marker(int s) { return Path.Combine(Log.Dir, "running." + s); }

        public static void Start(int mySlot)
        {
            if (Demo.On || Environment.GetEnvironmentVariable("TW_FAKE_VERSION") != null) return;
            slot = mySlot;
            try { RegisterApplicationRestart("", RESTART_NO_PATCH | RESTART_NO_REBOOT); } catch { }
            try { Directory.CreateDirectory(Log.Dir); File.WriteAllText(Marker(slot), Process.GetCurrentProcess().MainModule.FileName); } catch { }
            timer = new System.Windows.Forms.Timer { Interval = 15000 };
            timer.Tick += (o, e) => CheckSibling();
            timer.Start();
        }

        /// <summary>Normal exit: no restart wanted.</summary>
        public static void Stop()
        {
            if (slot < 0) return;
            if (timer != null) { timer.Dispose(); timer = null; }
            try { File.Delete(Marker(slot)); } catch { }
        }

        static void CheckSibling()
        {
            try
            {
                int other = 1 - slot;
                string marker = Marker(other);
                if (!File.Exists(marker)) return;                        // not running, or closed normally
                string exe = File.ReadAllText(marker).Trim();
                if (!File.Exists(exe)) { File.Delete(marker); return; }  // uninstalled or moved
                if (Running(Exes[other])) return;
                Mutex m;
                if (Mutex.TryOpenExisting("TaskbarWidgets.Updating", out m)) { m.Dispose(); return; } // mid-update

                restarts.RemoveAll(t => (DateTime.Now - t).TotalHours > 1);
                if (restarts.Count >= 3) return; // crashing over and over: stop trying until the next hour
                restarts.Add(DateTime.Now);
                Log.Write(slot == 0 ? "battery" : "volume",new Exception("Sibling " + Exes[other] + " stopped unexpectedly; starting it again"));
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            }
            catch { }
        }

        static bool Running(string exe)
        {
            var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }
    }
}
