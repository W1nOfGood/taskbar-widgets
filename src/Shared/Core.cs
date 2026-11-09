using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

// Native DLLs (hid.dll, winmm.dll, dwmapi.dll...) are only loaded from System32, never from the app folder.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace TaskbarWidgets
{
    /// <summary>Bridges WinRT async operations to Tasks without System.Runtime.WindowsRuntime.</summary>
    internal static class Rt
    {
        public static Task Await(Windows.Foundation.IAsyncAction op)
        {
            var tcs = new TaskCompletionSource<bool>();
            op.Completed = (o, st) =>
            {
                try
                {
                    if (st == Windows.Foundation.AsyncStatus.Completed) { o.GetResults(); tcs.TrySetResult(true); }
                    else tcs.TrySetException(new Exception("WinRT action " + st));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };
            return tcs.Task;
        }

        public static Task<T> Await<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = (o, st) =>
            {
                try
                {
                    if (st == Windows.Foundation.AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                    else tcs.TrySetException(new Exception("WinRT operation " + st));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };
            return tcs.Task;
        }

        public static void Fire<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            Await(op).ContinueWith(t => { var e = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    internal static class Demo
    {
        /// <summary>TW_DEMO=1: fake devices / media for screenshots, so nothing personal ends up in docs.</summary>
        public static readonly bool On = Environment.GetEnvironmentVariable("TW_DEMO") == "1";
    }

    internal static class Log
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarWidgets");

        public static void Info(string app, string msg)
        {
            if (Environment.GetEnvironmentVariable("TW_DEBUG") != "1") return;
            try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, app + ".log"), DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine); }
            catch { }
        }

        public static void Write(string app, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, app + ".log"), DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>Tiny key=value settings file in %APPDATA%\TaskbarWidgets.</summary>
    internal sealed class Settings
    {
        readonly string path;
        readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public event Action Changed;

        public Settings(string app)
        {
            path = Path.Combine(Log.Dir, (Demo.On ? "demo-" : "") + app + ".ini"); // demo never touches real settings
            try
            {
                if (File.Exists(path))
                    foreach (var line in File.ReadAllLines(path))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) map[line.Substring(0, i).Trim()] = line.Substring(i + 1);
                    }
            }
            catch { }
        }

        public string Get(string k, string def) { string v; return map.TryGetValue(k, out v) ? v : def; }
        public int Int(string k, int def) { int v; return int.TryParse(Get(k, null), out v) ? v : def; }
        public bool Bool(string k, bool def) { string v = Get(k, null); return v == null ? def : v == "1"; }

        public void Set(string k, string v)
        {
            string old;
            if (map.TryGetValue(k, out old) && old == v) return;
            map[k] = v;
            Save();
            if (Changed != null) Changed();
        }
        public void Set(string k, int v) { Set(k, v.ToString()); }
        public void Set(string k, bool v) { Set(k, v ? "1" : "0"); }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                var lines = new List<string>();
                foreach (var kv in map) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(path, lines.ToArray());
            }
            catch { }
        }
    }

    internal sealed class Theme
    {
        public bool Dark;
        public Color Bg, Card, CardHover, Border, Text, Sub, Faint, Accent, AccentSoft, Track, Good, Mid, Low, TabBg, Input;

        public static readonly string[] AccentNames = { "Copper", "Windows accent", "Blue", "Green", "Purple", "Pink", "Teal" };
        static readonly Color[] accents = {
            Color.FromArgb(168, 108, 62), Color.Empty, Color.FromArgb(59, 130, 246), Color.FromArgb(34, 160, 107),
            Color.FromArgb(139, 92, 246), Color.FromArgb(224, 85, 154), Color.FromArgb(20, 160, 170) };

        public static readonly string[] ModeNames = { "Match Windows", "Dark", "Light" };

        public static bool SystemDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return !(v is int && (int)v == 1);
                }
            }
            catch { return true; }
        }

        static Color WindowsAccent()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int) { int c = (int)v; return Color.FromArgb(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF); }
                }
            }
            catch { }
            return accents[0];
        }

        public static Theme From(Settings s)
        {
            int mode = s.Int("theme", 0);
            bool dark = mode == 0 ? SystemDark() : mode == 1;
            int ai = Math.Max(0, Math.Min(accents.Length - 1, s.Int("accent", 0)));
            Color acc = ai == 1 ? WindowsAccent() : accents[ai];
            var t = new Theme { Dark = dark, Accent = acc };
            if (dark)
            {
                t.Bg = Color.FromArgb(30, 30, 32); t.Card = Color.FromArgb(40, 40, 43); t.CardHover = Color.FromArgb(48, 48, 52);
                t.Border = Color.FromArgb(56, 56, 61); t.Text = Color.FromArgb(242, 242, 244); t.Sub = Color.FromArgb(160, 160, 166);
                t.Faint = Color.FromArgb(110, 110, 116); t.Track = Color.FromArgb(70, 70, 76); t.TabBg = Color.FromArgb(38, 38, 41);
                t.Input = Color.FromArgb(50, 50, 55);
            }
            else
            {
                t.Bg = Color.FromArgb(243, 243, 245); t.Card = Color.White; t.CardHover = Color.FromArgb(247, 247, 249);
                t.Border = Color.FromArgb(222, 222, 228); t.Text = Color.FromArgb(24, 24, 27); t.Sub = Color.FromArgb(96, 96, 104);
                t.Faint = Color.FromArgb(150, 150, 158); t.Track = Color.FromArgb(205, 205, 212); t.TabBg = Color.FromArgb(232, 232, 236);
                t.Input = Color.FromArgb(236, 236, 240);
            }
            t.AccentSoft = Color.FromArgb(dark ? 70 : 45, acc);
            t.Good = Color.FromArgb(34, 197, 140); t.Mid = Color.FromArgb(245, 166, 35); t.Low = Color.FromArgb(239, 68, 68);
            return t;
        }

        public Color LevelColor(int level, int lowThreshold)
        {
            if (level < 0) return Faint;
            if (level <= lowThreshold) return Low;
            if (level <= Math.Max(lowThreshold + 1, 50)) return Mid;
            return Good;
        }
    }

    internal static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsEnabled(string name)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(name) != null;
        }

        public static void Set(string name, bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(name, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                else if (k.GetValue(name) != null) k.DeleteValue(name);
            }
        }
    }
}
