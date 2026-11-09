using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration;

namespace TaskbarWidgets.Battery
{
    internal sealed class BatteryDevice
    {
        public string Key, Name, Via;
        public Glyph Kind;
        public bool Connected, Charging;
        public int Level = -1;      // live level, -1 unknown
        public int LastLevel = -1;  // remembered level for disconnected devices
        public int Shown { get { return Connected ? Level : -1; } }
    }

    /// <summary>Collects devices from Bluetooth (event driven), Razer HID and the system battery.</summary>
    internal sealed class DeviceManager
    {
        public event Action Changed;
        public event Action<BatteryDevice> Connected, Disconnected;

        readonly Control ui;
        readonly Settings S;
        readonly Dictionary<string, BatteryDevice> devices = new Dictionary<string, BatteryDevice>();
        readonly Dictionary<string, DeviceInformation> infos = new Dictionary<string, DeviceInformation>();
        DeviceWatcher watcher;
        bool enumerated;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        int busy;

        const string AqsBt = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
        static readonly string[] fullProps = { "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired", "System.Devices.Aep.DeviceAddress",
                                               "System.Devices.Aep.Bluetooth.Cod.Major", "System.Devices.Aep.Bluetooth.Cod.Minor",
                                               "System.Devices.Aep.Bluetooth.Le.Appearance.Category" };
        static readonly string[] minProps = { "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired", "System.Devices.Aep.DeviceAddress" };

        public DeviceManager(Control ui, Settings s) { this.ui = ui; S = s; }

        public List<BatteryDevice> All
        {
            get { return devices.Values.OrderByDescending(d => d.Connected).ThenBy(d => d.Key == "sys" ? 0 : 1).ThenBy(d => d.Name).ToList(); }
        }

        public void Start()
        {
            if (Demo.On) { LoadDemo(); return; }
            AddSystemBattery();
            StartWatcher(fullProps);
            poll.Interval = 60000;
            poll.Tick += (o, e) => RefreshLevels();
            poll.Start();
            RefreshLevels();
        }

        void StartWatcher(string[] props)
        {
            try
            {
                watcher = DeviceInformation.CreateWatcher(AqsBt, props, DeviceInformationKind.AssociationEndpoint);
                watcher.Added += (w, d) => Post(() => { infos[d.Id] = d; if (enumerated) RebuildBluetooth(); });
                watcher.Updated += (w, u) => Post(() => { DeviceInformation d; if (infos.TryGetValue(u.Id, out d)) { d.Update(u); RebuildBluetooth(); } });
                watcher.Removed += (w, u) => Post(() => { infos.Remove(u.Id); RebuildBluetooth(); });
                watcher.EnumerationCompleted += (w, o) => Post(() => FinishEnumeration("event"));
                watcher.Start();
                // On some PCs Windows never raises EnumerationCompleted for Bluetooth endpoints: don't wait for it.
                var fallback = new System.Windows.Forms.Timer { Interval = 3000 };
                fallback.Tick += (o, e) => { fallback.Dispose(); FinishEnumeration("timeout"); };
                fallback.Start();
            }
            catch (Exception ex)
            {
                Log.Write("battery", ex);
                if (props != minProps) StartWatcher(minProps);
            }
        }

        void FinishEnumeration(string why)
        {
            if (enumerated) return;
            Log.Info("battery", "bt enumeration complete (" + why + ")");
            RebuildBluetooth();
            enumerated = true;
            RefreshLevels();
        }

        void Post(Action a)
        {
            if (ui.IsDisposed) return;
            try { ui.BeginInvoke(a); } catch { }
        }

        void Raise() { if (Changed != null) Changed(); }

        BatteryDevice Get(string key, string name)
        {
            BatteryDevice d;
            if (!devices.TryGetValue(key, out d))
            {
                d = new BatteryDevice { Key = key, Name = name, LastLevel = S.Int("last." + key, -1) };
                devices[key] = d;
            }
            return d;
        }

        void SetConnected(BatteryDevice d, bool on)
        {
            if (d.Connected == on) return;
            d.Connected = on;
            if (!on) { d.Charging = false; d.Level = -1; }
            if (!enumerated) return;
            if (on && Connected != null) Connected(d);
            if (!on && Disconnected != null) Disconnected(d);
        }

        void UpdateAirPodsWatcher()
        {
            bool want = devices.Values.Any(d => d.Connected && d.Key.StartsWith("bt:") && AirPodsWatcher.IsApple(d.Name));
            if (want && !airpods.Running)
            {
                airpods.Start();
                if (airTimer == null)
                {
                    airTimer = new System.Windows.Forms.Timer { Interval = 15000 };
                    airTimer.Tick += (o, e) => { if (ApplyAirPods()) Raise(); };
                }
                airTimer.Start();
            }
            else if (!want && airpods.Running) { airpods.Stop(); if (airTimer != null) airTimer.Stop(); }
        }

        /// <summary>Applies the latest AirPods advert to the connected Apple headset (only when there is exactly one).</summary>
        bool ApplyAirPods()
        {
            if (!airpods.Running || airpods.Level < 0 || (DateTime.Now - airpods.At).TotalMinutes > 3) return false;
            var apple = devices.Values.Where(d => d.Connected && d.Key.StartsWith("bt:") && AirPodsWatcher.IsApple(d.Name)).ToList();
            if (apple.Count != 1) return false;
            var a = apple[0];
            bool changed = a.Level != airpods.Level || a.Charging != airpods.Charging;
            SetLevel(a, airpods.Level);
            a.Charging = airpods.Charging;
            if (a.Kind == Glyph.Generic || a.Kind == Glyph.Headphones) a.Kind = Glyph.Earbuds;
            return changed;
        }

        // ---------------------------------------------------------------- time left (learned from your usage)

        sealed class Run { public DateTime T0, TLast; public int L0, LLast; public bool Charging; }
        readonly Dictionary<string, Run> runs = new Dictionary<string, Run>();

        static bool IsCharging(BatteryDevice d)
        {
            return d.Charging || d.Key == "sys" && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        }

        /// <summary>Follows each connected device's discharge / charge runs; a finished run teaches its typical rate.</summary>
        void TrackAll()
        {
            var now = DateTime.Now;
            foreach (var d in devices.Values)
            {
                if (!d.Connected || d.Level < 0 || (d.Via ?? "").Contains("asleep")) continue;
                bool chg = IsCharging(d);
                Run r;
                bool fresh = !runs.TryGetValue(d.Key, out r) || r.Charging != chg || (now - r.TLast).TotalMinutes > 30 ||
                             (!chg && d.Level > r.LLast + 2) || (chg && d.Level < r.LLast - 2);
                if (fresh)
                {
                    if (r != null) Learn(d.Key, r);
                    runs[d.Key] = new Run { T0 = now, TLast = now, L0 = d.Level, LLast = d.Level, Charging = chg };
                }
                else { r.TLast = now; r.LLast = d.Level; }
            }
        }

        void Learn(string key, Run r)
        {
            double h = (r.TLast - r.T0).TotalHours;
            int moved = r.Charging ? r.LLast - r.L0 : r.L0 - r.LLast;
            if (h < 0.33 || moved < 3) return; // too short to say anything
            double rate = moved / h;           // % per hour
            string k = (r.Charging ? "rate.charge." : "rate.drain.") + key;
            double old = ParseD(S.Get(k, ""));
            double learned = double.IsNaN(old) ? rate : old + (rate - old) * 0.3;
            S.Set(k, learned.ToString("F2", CultureInfo.InvariantCulture));
        }

        static double ParseD(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        /// <summary>% per hour for this device, mixing the current run with what was learned before.</summary>
        double Rate(BatteryDevice d, bool charging, out bool fromHistory)
        {
            fromHistory = false;
            double cur = double.NaN, h = 0;
            Run r;
            if (runs.TryGetValue(d.Key, out r) && r.Charging == charging)
            {
                h = (r.TLast - r.T0).TotalHours;
                int moved = charging ? r.LLast - r.L0 : r.L0 - r.LLast;
                if (h >= 0.33 && moved >= 2) cur = moved / h;
            }
            double learned = ParseD(S.Get((charging ? "rate.charge." : "rate.drain.") + d.Key, ""));
            if (!double.IsNaN(cur) && !double.IsNaN(learned)) { double w = Math.Min(1, h / 2); return w * cur + (1 - w) * learned; }
            if (!double.IsNaN(cur)) return cur;
            fromHistory = !double.IsNaN(learned);
            return learned;
        }

        /// <summary>"~5 h 20 min left", "full in ~40 min", or null when there's no estimate yet.</summary>
        public string TimeLeft(BatteryDevice d)
        {
            if (!d.Connected || d.Level < 0) return null;
            bool chg = IsCharging(d);
            if (d.Key == "sys" && !chg)
            {
                int secs = SystemInformation.PowerStatus.BatteryLifeRemaining; // Windows' own estimate
                if (secs > 0) return "~" + Fmt(secs / 3600.0) + " left";
            }
            if (chg && d.Level >= 100) return "fully charged";
            bool hist;
            double rate = Rate(d, chg, out hist);
            if (double.IsNaN(rate) || rate < 0.05) return null;
            double hours = chg ? (100 - d.Level) / rate : d.Level / rate;
            if (hours > 24 * 30) return null;
            return chg ? "full in ~" + Fmt(hours) : "~" + Fmt(hours) + " left" + (hist ? " (from past use)" : "");
        }

        /// <summary>How long a full charge usually lasts, learned from past use ("~22 h"), or null.</summary>
        public string FullChargeLasts(BatteryDevice d)
        {
            double learned = ParseD(S.Get("rate.drain." + d.Key, ""));
            return double.IsNaN(learned) || learned < 0.05 ? null : "~" + Fmt(100 / learned);
        }

        public static string Fmt(double hours)
        {
            if (hours >= 48) return Math.Round(hours / 24) + " days";
            if (hours >= 10) return Math.Round(hours) + " h";
            if (hours >= 1)
            {
                int m = (int)Math.Round(hours * 6) * 10; // 10-minute steps
                return m % 60 == 0 ? (m / 60) + " h" : (m / 60) + " h " + (m % 60) + " min";
            }
            return Math.Max(5, (int)Math.Round(hours * 12) * 5) + " min";
        }

        public void SetLevel(BatteryDevice d, int level)
        {
            if (level < 0 || level > 100) return;
            d.Level = level;
            if (d.LastLevel != level) { d.LastLevel = level; S.Set("last." + d.Key, level); }
        }

        void LoadDemo()
        {
            Action<string, string, Glyph, string, bool, int, bool> add = (key, name, kind, via, on, level, charging) =>
            {
                var d = new BatteryDevice { Key = key, Name = name, Kind = kind, Via = via, Connected = on, Charging = charging };
                if (on) d.Level = level;
                d.LastLevel = level;
                devices[key] = d;
            };
            add("demo1", "Viper V3 Pro", Glyph.Mouse, "2.4 GHz", true, 82, false);
            add("demo2", "WH-1000XM5", Glyph.Headphones, "Bluetooth", true, 64, false);
            add("demo3", "Xbox Controller", Glyph.Gamepad, "Bluetooth LE", true, 14, false);
            add("demo4", "Galaxy Buds2 Pro", Glyph.Earbuds, "Bluetooth", false, 45, false);
            add("demo5", "JBL Flip 6", Glyph.Speaker, "Bluetooth", false, 100, false);
            // made-up history so the screenshots show time-left estimates
            var rates = new Dictionary<string, double> { { "demo1", 5.1 }, { "demo2", 3.4 }, { "demo3", 7.5 }, { "demo4", 16 }, { "demo5", 8.4 } };
            foreach (var kv in rates)
            {
                S.Set("rate.drain." + kv.Key, kv.Value.ToString("F2", CultureInfo.InvariantCulture));
                var d = devices[kv.Key];
                if (d.Connected) runs[kv.Key] = new Run { T0 = DateTime.Now.AddHours(-2), TLast = DateTime.Now, L0 = d.Level + (int)Math.Round(kv.Value * 2), LLast = d.Level };
            }
            enumerated = true;
            Raise();
        }

        // ---------------------------------------------------------------- Bluetooth

        void RebuildBluetooth()
        {
            var seen = new HashSet<string>();
            // A device can appear twice (classic + LE) with the same address: merge before applying.
            var groups = infos.Values
                .Where(di => Prop(di, "System.Devices.Aep.IsPaired") is bool && (bool)Prop(di, "System.Devices.Aep.IsPaired"))
                .GroupBy(di => (Prop(di, "System.Devices.Aep.DeviceAddress") as string ?? "").Replace(":", "").Replace("-", "").ToUpperInvariant());
            foreach (var grp in groups)
            {
                if (grp.Key.Length != 12) continue;
                string key = "bt:" + grp.Key;
                var list = grp.ToList();
                Func<DeviceInformation, bool> isConn = di => Prop(di, "System.Devices.Aep.IsConnected") is bool && (bool)Prop(di, "System.Devices.Aep.IsConnected");
                var main = list.FirstOrDefault(isConn) ?? list[0];
                bool conn = list.Any(isConn);
                bool le = main.Id.IndexOf("BluetoothLE", StringComparison.OrdinalIgnoreCase) >= 0;
                var d = Get(key, main.Name);
                d.Name = main.Name;
                d.Via = le ? "Bluetooth LE" : "Bluetooth";
                d.Kind = Glyph.Generic;
                foreach (var di in list)
                {
                    var k = GuessKind(di.Name, Prop(di, "System.Devices.Aep.Bluetooth.Cod.Major"), Prop(di, "System.Devices.Aep.Bluetooth.Cod.Minor"),
                                      Prop(di, "System.Devices.Aep.Bluetooth.Le.Appearance.Category"));
                    if (k != Glyph.Generic) { d.Kind = k; break; }
                }
                bool was = d.Connected;
                SetConnected(d, conn);
                if (!was && conn && enumerated) { sonyAt = DateTime.MinValue; ScheduleLevels(); }
                if (!conn) sonyLast.Remove(grp.Key);
                seen.Add(key);
            }
            foreach (var k in devices.Keys.Where(k => k.StartsWith("bt:") && !seen.Contains(k)).ToList()) devices.Remove(k);
            UpdateAirPodsWatcher();
            Raise();
        }

        static object Prop(DeviceInformation d, string name)
        {
            object v;
            return d.Properties.TryGetValue(name, out v) ? v : null;
        }

        static Glyph GuessKind(string name, object major, object minor, object appearance)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (Regex.IsMatch(n, @"mouse|viper|deathadder|basilisk|mx master|mx anywhere|g pro|g502|g305|pulsefire|\bmx\b")) return Glyph.Mouse;
            if (Regex.IsMatch(n, @"keyboard|\bkb\b|keys|huntsman|blackwidow|k380|mx keys")) return Glyph.Keyboard;
            if (Regex.IsMatch(n, @"buds|pods|\bwf-|earbud|\btws\b|freebuds|\bear\b")) return Glyph.Earbuds;
            if (Regex.IsMatch(n, @"headphone|headset|\bwh-|wear|\bmax\b|quietcomfort|\bqc\d|arctis|cloud|blackshark|kraken|rt-b")) return Glyph.Headphones;
            if (Regex.IsMatch(n, @"speaker|jbl (charge|flip|go|clip|xtreme|boombox)|soundlink|boom|\bsrs-")) return Glyph.Speaker;
            if (Regex.IsMatch(n, @"controller|gamepad|xbox|dualsense|dualshock|joy-con|pro controller")) return Glyph.Gamepad;
            if (Regex.IsMatch(n, @"watch|band|fitbit")) return Glyph.Watch;
            if (Regex.IsMatch(n, @"\bpen\b|stylus|pencil")) return Glyph.Pen;
            if (Regex.IsMatch(n, @"iphone|galaxy|pixel|phone|redmi|oneplus|xiaomi|\bpoco\b|\d+\s*n?pro\b")) return Glyph.Phone;

            int mj = major is ushort ? (ushort)major : (major is uint ? (int)(uint)major : -1);
            int mn = minor is ushort ? (ushort)minor : (minor is uint ? (int)(uint)minor : -1);
            if (mj == 2) return Glyph.Phone;
            if (mj == 1) return Glyph.Laptop;
            if (mj == 7) return Glyph.Watch;
            if (mj == 4) return mn == 5 || mn == 7 || mn == 10 ? Glyph.Speaker : Glyph.Headphones;
            if (mj == 5)
            {
                if ((mn & 0x0F) == 1 || (mn & 0x0F) == 2) return Glyph.Gamepad;
                if ((mn & 0x30) == 0x20) return Glyph.Mouse;
                if ((mn & 0x30) == 0x10 || (mn & 0x30) == 0x30) return Glyph.Keyboard;
            }
            int ap = appearance is ushort ? (ushort)appearance : -1;
            if (ap == 1) return Glyph.Phone;
            if (ap == 3) return Glyph.Watch;
            if (ap == 33 || ap == 37) return Glyph.Headphones;
            return Glyph.Generic;
        }

        // ---------------------------------------------------------------- levels (background thread)

        void ScheduleLevels()
        {
            // Windows publishes the level a few seconds after a device connects
            foreach (int ms in new[] { 4000, 15000 })
            {
                var t = new System.Windows.Forms.Timer { Interval = ms };
                t.Tick += (o, e) => { t.Dispose(); RefreshLevels(); };
                t.Start();
            }
        }

        public void RefreshLevels()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) { again = true; return; } // run again when the current pass ends
            UpdateSystemBattery();
            // Sony headsets: ask the headset itself (Windows' value is often wrong), at most every 2 minutes
            var connectedBt = new HashSet<string>(devices.Values.Where(x => x.Connected && x.Key.StartsWith("bt:")).Select(x => x.Key.Substring(3)));
            bool askSony = S.Bool("sonyDirect", true) && connectedBt.Count > 0 && (DateTime.Now - sonyAt).TotalSeconds > 110;
            if (askSony) sonyAt = DateTime.Now;
            Log.Info("battery", "refresh: connectedBt=" + connectedBt.Count + " askSony=" + askSony);
            // Bluetooth LE devices Windows has no level for: read the standard Battery Service ourselves
            var needGatt = askSony ? devices.Values.Where(x => x.Connected && x.Key.StartsWith("bt:") && x.Level < 0 && !sonyLast.ContainsKey(x.Key.Substring(3)))
                                                   .Select(x => x.Key.Substring(3)).ToList() : new List<string>();
            Task.Run(() =>
            {
                Dictionary<string, int> bt = null; List<RazerHid.Result> rz = null; List<LogitechHidpp.Result> lg = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { bt = PnpBattery.Read(); } catch (Exception ex) { Log.Write("battery", ex); }
                Log.Info("battery", "pnp " + sw.ElapsedMilliseconds + " ms"); sw.Restart();
                try { rz = RazerHid.Query(); } catch (Exception ex) { Log.Write("battery", ex); }
                Log.Info("battery", "razer " + sw.ElapsedMilliseconds + " ms"); sw.Restart();
                try { lg = LogitechHidpp.Query(); } catch (Exception ex) { Log.Write("battery", ex); }
                Log.Info("battery", "logitech " + sw.ElapsedMilliseconds + " ms");
                Dictionary<string, SonyBattery.Result> sony = null;
                if (askSony) try { sony = SonyBattery.Query(connectedBt); } catch (Exception ex) { Log.Write("battery", ex); }
                var gatt = new Dictionary<string, int>();
                foreach (var a in needGatt)
                {
                    if (sony != null && sony.ContainsKey(a)) continue;
                    int lv = GattBattery.Read(a);
                    if (lv >= 0) gatt[a] = lv;
                }
                Post(() =>
                {
                    Interlocked.Exchange(ref busy, 0);
                    Apply(bt, rz, sony, lg, gatt);
                    if (again) { again = false; RefreshLevels(); }
                });
            });
        }

        DateTime sonyAt = DateTime.MinValue;
        readonly Dictionary<string, SonyBattery.Result> sonyLast = new Dictionary<string, SonyBattery.Result>();

        readonly Dictionary<string, int> gattLast = new Dictionary<string, int>();
        bool again;
        readonly AirPodsWatcher airpods = new AirPodsWatcher();
        System.Windows.Forms.Timer airTimer;

        void Apply(Dictionary<string, int> bt, List<RazerHid.Result> rz, Dictionary<string, SonyBattery.Result> sony,
                   List<LogitechHidpp.Result> lg, Dictionary<string, int> gatt)
        {
            if (sony != null) foreach (var kv in sony) sonyLast[kv.Key] = kv.Value;
            if (gatt != null) foreach (var kv in gatt) gattLast[kv.Key] = kv.Value;
            if (bt != null)
                foreach (var d in devices.Values.Where(x => x.Key.StartsWith("bt:")))
                {
                    int lv;
                    if (!bt.TryGetValue(d.Key.Substring(3), out lv)) continue;
                    // A headset we read directly before: Windows' value for it is known to be wrong, so show
                    // the last real reading until the headset answers again.
                    if (d.Connected && S.Bool("direct." + d.Key, false) && !sonyLast.ContainsKey(d.Key.Substring(3)))
                    {
                        if (d.LastLevel >= 0) d.Level = d.LastLevel;
                        continue;
                    }
                    if (d.Connected) SetLevel(d, lv);
                    else if (d.LastLevel < 0) { d.LastLevel = lv; }
                }
            // the headset's own answer beats Windows' cached value
            foreach (var d in devices.Values.Where(x => x.Key.StartsWith("bt:") && x.Connected))
            {
                SonyBattery.Result sr;
                if (!sonyLast.TryGetValue(d.Key.Substring(3), out sr)) continue;
                SetLevel(d, sr.Level);
                d.Charging = sr.Charging;
                S.Set("direct." + d.Key, true);
                if (!d.Via.EndsWith("(read from headset)")) d.Via += " (read from headset)";
            }
            foreach (var d in devices.Values.Where(x => x.Key.StartsWith("bt:") && x.Connected && x.Level < 0))
            {
                int gl;
                if (gattLast.TryGetValue(d.Key.Substring(3), out gl)) SetLevel(d, gl);
            }
            ApplyAirPods();
            TrackAll();
            if (lg != null)
            {
                var seen = new HashSet<string>();
                foreach (var r in lg)
                {
                    seen.Add(r.Key);
                    var d = Get(r.Key, r.Name);
                    d.Name = r.Name; d.Kind = r.Kind; d.Via = r.Wireless ? "2.4 GHz" : "USB";
                    SetConnected(d, true);
                    SetLevel(d, r.Level);
                    d.Charging = r.Charging;
                }
                // a receiver slot that stops answering = device off or asleep: keep showing the last level briefly,
                // drop it once the receiver itself is gone
                foreach (var k in devices.Keys.Where(k => k.StartsWith("lg:") && !seen.Contains(k)).ToList())
                {
                    var d = devices[k];
                    if (d.LastLevel >= 0 && lg.Any(x => x.Key.Split(':')[1] == k.Split(':')[1])) { d.Level = d.LastLevel; d.Via = "2.4 GHz · asleep"; }
                    else { SetConnected(d, false); devices.Remove(k); }
                }
            }
            if (rz != null)
            {
                var seen = new HashSet<string>();
                foreach (var r in rz)
                {
                    string key = "rz:" + r.Pid;
                    seen.Add(key);
                    var d = Get(key, r.Name);
                    d.Name = r.Name; d.Kind = r.Kind; d.Via = r.Wireless ? "2.4 GHz" : "USB";
                    if (r.Level >= 0)
                    {
                        SetConnected(d, true);
                        SetLevel(d, r.Level);
                        d.Charging = r.Charging;
                    }
                    else
                    {
                        // Dongle present but the device is asleep: keep it on the taskbar with its last level
                        // instead of "disconnecting" it every few minutes.
                        bool first = !d.Connected && d.LastLevel < 0;
                        if (!first) { SetConnected(d, true); d.Level = d.LastLevel; d.Charging = false; d.Via = "2.4 GHz · asleep"; }
                    }
                }
                foreach (var k in devices.Keys.Where(k => k.StartsWith("rz:") && !seen.Contains(k)).ToList())
                {
                    SetConnected(devices[k], false);
                    devices.Remove(k);
                }
            }
            Raise();
        }

        // ---------------------------------------------------------------- system battery (laptops)

        void AddSystemBattery()
        {
            var ps = SystemInformation.PowerStatus;
            if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0 || ps.BatteryChargeStatus == BatteryChargeStatus.Unknown) return;
            var d = Get("sys", "This PC");
            d.Kind = Glyph.Laptop; d.Via = "Built-in battery"; d.Connected = true;
            UpdateSystemBattery();
        }

        void UpdateSystemBattery()
        {
            BatteryDevice d;
            if (!devices.TryGetValue("sys", out d)) return;
            var ps = SystemInformation.PowerStatus;
            SetLevel(d, (int)Math.Round(ps.BatteryLifePercent * 100));
            d.Charging = ps.PowerLineStatus == PowerLineStatus.Online && d.Level < 100;
            d.Via = ps.PowerLineStatus == PowerLineStatus.Online ? "Plugged in" : "On battery";
        }
    }

    /// <summary>Reads DEVPKEY_Bluetooth_Battery that Windows exposes for hands-free / HID Bluetooth devices.</summary>
    internal static class PnpBattery
    {
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public int pid; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(IntPtr cls, string enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int idx, ref SP_DEVINFO_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA d, ref DEVPROPKEY key, out int type, byte[] buf, int size, out int req, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA d, StringBuilder sb, int size, out int req);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static readonly Regex addr = new Regex(@"(?<![0-9A-F])[0-9A-F]{12}(?![0-9A-F])", RegexOptions.Compiled);
        static readonly Regex guid = new Regex(@"\{[^}]*\}", RegexOptions.Compiled);

        public static Dictionary<string, int> Read()
        {
            var res = new Dictionary<string, int>();
            var key = new DEVPROPKEY { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
            foreach (var en in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
            {
                IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, en, IntPtr.Zero, 0x4 /*ALLCLASSES*/);
                if (set == new IntPtr(-1)) continue;
                try
                {
                    var d = new SP_DEVINFO_DATA(); d.cbSize = Marshal.SizeOf(d);
                    var buf = new byte[4];
                    for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                    {
                        int type, req;
                        if (!SetupDiGetDevicePropertyW(set, ref d, ref key, out type, buf, buf.Length, out req, 0) || type != 3) continue;
                        var sb = new StringBuilder(512);
                        if (!SetupDiGetDeviceInstanceId(set, ref d, sb, sb.Capacity, out req)) continue;
                        string id = guid.Replace(sb.ToString().ToUpperInvariant(), "");
                        foreach (Match m in addr.Matches(id))
                        {
                            int old;
                            if (!res.TryGetValue(m.Value, out old) || buf[0] > 0) res[m.Value] = buf[0];
                        }
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
            }
            return res;
        }
    }

    /// <summary>Razer wireless mice/keyboards/headsets via the Razer feature-report protocol (same one Synapse uses).</summary>
    internal static class RazerHid
    {
        public sealed class Result { public string Pid, Name; public int Level = -1; public bool Charging, Wireless; public Glyph Kind; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder sb, int len);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string n, uint acc, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr r; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

        static readonly Dictionary<string, byte> transaction = new Dictionary<string, byte>();
        static readonly Regex pidRx = new Regex(@"pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);

        public static List<Result> Query()
        {
            var results = new List<Result>();
            var done = new HashSet<string>();
            foreach (var path in HidPaths())
            {
                string lower = path.ToLowerInvariant();
                if (!lower.Contains("vid_1532")) continue;
                var pm = pidRx.Match(lower);
                if (!pm.Success) continue;
                string pid = pm.Groups[1].Value.ToUpperInvariant();
                if (done.Contains(pid)) continue;
                // feature reports live on interface 0 (or the only interface)
                if (lower.Contains("&mi_") && !lower.Contains("&mi_00")) continue;
                if (lower.Contains("&col") && !lower.Contains("&mi_00#") && !lower.Contains("&col01")) continue;

                using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    byte[] resp;
                    int status = Send(h, pid, 0x07, 0x80, out resp);
                    if (status < 0) continue; // interface does not speak the protocol
                    done.Add(pid);
                    var sb = new StringBuilder(128);
                    string name = HidD_GetProductString(h, sb, 256) && sb.Length > 0 ? sb.ToString() : "Razer device";
                    name = Regex.Replace(name, @"\s*(dongle|receiver|hyperspeed)\s*$", "", RegexOptions.IgnoreCase).Trim();
                    var r = new Result { Pid = pid, Name = name, Wireless = true, Kind = KindFor(name) };
                    if (status == 0x02)
                    {
                        r.Level = (int)Math.Round(resp[10] * 100 / 255.0);
                        byte[] cr;
                        if (Send(h, pid, 0x07, 0x84, out cr) == 0x02) r.Charging = cr[10] == 1;
                    }
                    else if (status == 0x05) continue; // not a battery device (wired-only product)
                    results.Add(r);
                }
            }
            return results;
        }

        static Glyph KindFor(string name)
        {
            string n = name.ToLowerInvariant();
            if (Regex.IsMatch(n, "kraken|barracuda|blackshark|nari|hammerhead|headset")) return Glyph.Headphones;
            if (Regex.IsMatch(n, "huntsman|blackwidow|deathstalker|ornata|cynosa|keyboard|pro type")) return Glyph.Keyboard;
            if (Regex.IsMatch(n, "wolverine|kishi|raiju|controller")) return Glyph.Gamepad;
            return Glyph.Mouse;
        }

        /// <summary>Returns the Razer status byte (2 = ok, 1 busy, 3 fail, 4 timeout/asleep, 5 unsupported) or -1.</summary>
        static int Send(SafeFileHandle h, string pid, byte cls, byte cmd, out byte[] resp)
        {
            resp = null;
            var tids = new List<byte>();
            byte known;
            if (transaction.TryGetValue(pid, out known)) tids.Add(known);
            foreach (var t in new byte[] { 0x1F, 0x3F, 0xFF, 0x9F }) if (!tids.Contains(t)) tids.Add(t);
            int last = -1;
            foreach (var tid in tids)
            {
                var req = new byte[91];
                req[2] = tid; req[6] = 0x02; req[7] = cls; req[8] = cmd;
                byte crc = 0; for (int i = 3; i < 89; i++) crc ^= req[i]; req[89] = crc;
                if (!HidD_SetFeature(h, req, req.Length)) return -1;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    Thread.Sleep(attempt == 0 ? 40 : 80);
                    var r = new byte[91];
                    if (!HidD_GetFeature(h, r, r.Length)) return -1;
                    if (r[1] == 0x01) continue; // busy, ask again
                    last = r[1];
                    if (r[1] == 0x02 && r[7] == cls && r[8] == cmd) { transaction[pid] = tid; resp = r; return 2; }
                    break;
                }
                if (last == 0x04) return 4; // the dongle answered: device is asleep, no need to try other ids
            }
            return last;
        }

        static List<string> HidPaths()
        {
            var res = new List<string>();
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12 /*PRESENT|INTERFACE*/);
            if (set == new IntPtr(-1)) return res;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA(); data.cbSize = Marshal.SizeOf(data);
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data); i++)
                {
                    int req; SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, req, out req, IntPtr.Zero))
                            res.Add(Marshal.PtrToStringUni(buf + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return res;
        }
    }
}
