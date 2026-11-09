using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace TaskbarWidgets.Battery
{
    /// <summary>Standard Bluetooth LE Battery Service (0x180F / 0x2A19), read directly from the device.</summary>
    internal static class GattBattery
    {
        /// <summary>Blocking; background thread. Returns -1 when the device has no readable battery service.</summary>
        public static int Read(string address12)
        {
            try
            {
                ulong addr = Convert.ToUInt64(address12, 16);
                var t = ReadAsync(addr);
                return t.Wait(8000) ? t.Result : -1;
            }
            catch { return -1; }
        }

        static async Task<int> ReadAsync(ulong addr)
        {
            var dev = await Rt.Await(BluetoothLEDevice.FromBluetoothAddressAsync(addr));
            if (dev == null) return -1;
            using (dev)
            {
                var sr = await Rt.Await(dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached));
                if (sr.Status != GattCommunicationStatus.Success) return -1;
                foreach (var svc in sr.Services)
                    using (svc)
                    {
                        var cr = await Rt.Await(svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached));
                        if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0) continue;
                        var rv = await Rt.Await(cr.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached));
                        if (rv.Status != GattCommunicationStatus.Success || rv.Value == null || rv.Value.Length < 1) continue;
                        int level = DataReader.FromBuffer(rv.Value).ReadByte();
                        if (level <= 100) return level;
                    }
            }
            return -1;
        }
    }

    /// <summary>
    /// Apple AirPods / Beats: they broadcast left, right and case battery in Apple's "proximity pairing"
    /// advertisement (manufacturer 0x004C, type 0x07). Addresses are randomised, so the reading is matched to
    /// the connected Apple headset when there is exactly one. Only nearby (strong signal) adverts count.
    /// The watcher only runs while such a headset is connected.
    /// </summary>
    internal sealed class AirPodsWatcher
    {
        BluetoothLEAdvertisementWatcher watcher;
        public int Level = -1, Left = -1, Right = -1, Case = -1;
        public bool Charging;
        public DateTime At = DateTime.MinValue;

        public static bool IsApple(string name)
        {
            return name != null && Regex.IsMatch(name, @"airpods|beats|powerbeats", RegexOptions.IgnoreCase);
        }

        public bool Running { get { return watcher != null; } }

        public void Start()
        {
            if (watcher != null) return;
            try
            {
                watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                watcher.Received += OnReceived;
                watcher.Start();
            }
            catch (Exception ex) { Log.Write("battery", ex); watcher = null; }
        }

        public void Stop()
        {
            if (watcher == null) return;
            try { watcher.Received -= OnReceived; watcher.Stop(); } catch { }
            watcher = null;
        }

        void OnReceived(BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs a)
        {
            try
            {
                if (a.RawSignalStrengthInDBm < -70) return; // someone else's AirPods across the room
                foreach (var md in a.Advertisement.ManufacturerData)
                {
                    if (md.CompanyId != 0x004C || md.Data == null || md.Data.Length < 9) continue;
                    var d = new byte[md.Data.Length];
                    DataReader.FromBuffer(md.Data).ReadBytes(d);
                    if (d[0] != 0x07 || d[1] < 0x19) continue;   // proximity pairing message
                    bool flip = (d[5] & 0x20) == 0;                 // which bud is "primary" decides nibble order
                    int a1 = d[6] >> 4, a2 = d[6] & 0x0F;
                    int left = flip ? a2 : a1, right = flip ? a1 : a2, cs = d[7] & 0x0F;
                    Func<int, int> pct = v => v <= 10 ? v * 10 : -1; // 15 = unknown
                    Left = pct(left); Right = pct(right); Case = pct(cs);
                    int chg = d[7] >> 4;
                    Charging = (chg & 0x03) != 0;
                    int lv = Left < 0 ? Right : Right < 0 ? Left : Math.Min(Left, Right);
                    if (lv < 0) lv = Case;
                    if (lv >= 0) { Level = Math.Min(100, Math.Max(0, lv)); At = DateTime.Now; }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Logitech wireless devices behind a Unifying / Bolt / Lightspeed receiver (and wired ones), via HID++ 2.0:
    /// feature 0x1004 Unified Battery, else 0x1000 Battery Status, else 0x1001 Battery Voltage.
    /// </summary>
    internal static class LogitechHidpp
    {
        public sealed class Result { public string Key, Name; public int Level = -1; public bool Charging, Wireless; public Glyph Kind = Glyph.Mouse; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort a, b, c, d, e, f, g, h, i, j;
        }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string n, uint acc, uint share, IntPtr sa, uint disp, uint flags, IntPtr t);

        const byte SwId = 0x0A;
        static readonly Regex pidRx = new Regex(@"pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);

        /// <summary>Blocking; background thread.</summary>
        public static List<Result> Query()
        {
            var results = new List<Result>();
            foreach (var path in HidEnum.Paths())
            {
                string low = path.ToLowerInvariant();
                if (!low.Contains("vid_046d")) continue;
                using (var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    IntPtr pp; HIDP_CAPS caps;
                    if (!HidD_GetPreparsedData(h, out pp)) continue;
                    try { HidP_GetCaps(pp, out caps); } finally { HidD_FreePreparsedData(pp); }
                    // the HID++ "long" collection: vendor page 0xFF00, 20-byte reports
                    if (caps.UsagePage != 0xFF00 || caps.OutputReportByteLength != 20) continue;
                    var pm = pidRx.Match(low);
                    string pid = pm.Success ? pm.Groups[1].Value.ToUpperInvariant() : "0000";
                    try
                    {
                        using (var fs = new FileStream(h, FileAccess.ReadWrite, 1, true))
                        {
                            var link = new Link(fs);
                            // 0xFF = the device itself (wired / direct), 1..6 = devices paired to a receiver
                            foreach (byte idx in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                            {
                                var r = QueryDevice(link, idx);
                                if (r == null) continue;
                                r.Key = "lg:" + pid + ":" + idx.ToString("X2");
                                r.Wireless = idx != 0xFF;
                                results.Add(r);
                            }
                        }
                    }
                    catch (Exception ex) { Log.Write("battery", ex); }
                }
            }
            return results;
        }

        static Result QueryDevice(Link link, byte idx)
        {
            int f1004 = link.FeatureIndex(idx, 0x1004);
            if (f1004 == -2) return null; // nothing there (empty receiver slot / device off)
            var r = new Result();
            byte[] p;
            if (f1004 > 0 && (p = link.Call(idx, (byte)f1004, 1, null)) != null)
            {
                r.Level = p[0]; r.Charging = p[2] == 1 || p[2] == 2;
            }
            else
            {
                int f1000 = link.FeatureIndex(idx, 0x1000);
                if (f1000 > 0 && (p = link.Call(idx, (byte)f1000, 0, null)) != null)
                {
                    r.Level = p[0]; r.Charging = p[2] >= 1 && p[2] <= 4;
                }
                else
                {
                    int f1001 = link.FeatureIndex(idx, 0x1001);
                    if (f1001 > 0 && (p = link.Call(idx, (byte)f1001, 0, null)) != null)
                    {
                        int mv = (p[0] << 8) | p[1];
                        r.Level = Math.Max(0, Math.Min(100, (mv - 3500) * 100 / 700)); // rough Li-ion curve
                        r.Charging = (p[2] & 0x80) != 0;
                    }
                }
            }
            if (r.Level < 0 || r.Level > 100) return null;
            // name + type via feature 0x0005 (Device Name & Type)
            r.Name = "Logitech device";
            int f5 = link.FeatureIndex(idx, 0x0005);
            if (f5 > 0)
            {
                var c = link.Call(idx, (byte)f5, 0, null);
                if (c != null && c[0] > 0 && c[0] < 64)
                {
                    var sb = new StringBuilder();
                    while (sb.Length < c[0])
                    {
                        var part = link.Call(idx, (byte)f5, 1, new[] { (byte)sb.Length });
                        if (part == null) break;
                        string chunk = Encoding.ASCII.GetString(part).TrimEnd('\0');
                        if (chunk.Length == 0) break;
                        sb.Append(chunk);
                    }
                    if (sb.Length > 0) r.Name = sb.ToString().Substring(0, Math.Min(sb.Length, c[0])).Trim();
                }
                var ty = link.Call(idx, (byte)f5, 2, null);
                if (ty != null)
                    switch (ty[0])
                    {
                        case 0: case 2: r.Kind = Glyph.Keyboard; break;
                        case 8: r.Kind = Glyph.Headphones; break;
                        case 9: r.Kind = Glyph.Gamepad; break;
                        default: r.Kind = Glyph.Mouse; break;
                    }
            }
            if (!r.Name.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase) && !r.Name.StartsWith("G ") && !r.Name.StartsWith("MX"))
                r.Name = "Logitech " + r.Name;
            return r;
        }

        sealed class Link
        {
            readonly FileStream fs;
            public Link(FileStream fs) { this.fs = fs; }

            /// <summary>Root feature lookup. Returns the feature index, 0 if unsupported, -2 if the device does not answer.</summary>
            public int FeatureIndex(byte idx, int feature)
            {
                var p = Call(idx, 0x00, 0, new[] { (byte)(feature >> 8), (byte)feature });
                if (p == null) return -2;
                return p[0];
            }

            /// <summary>Sends a long HID++ 2.0 request and returns the 16 parameter bytes of the matching reply.</summary>
            public byte[] Call(byte idx, byte feat, int fn, byte[] args)
            {
                var req = new byte[20];
                req[0] = 0x11; req[1] = idx; req[2] = feat; req[3] = (byte)((fn << 4) | SwId);
                if (args != null) Array.Copy(args, 0, req, 4, Math.Min(args.Length, 16));
                try { fs.Write(req, 0, 20); fs.Flush(); } catch { return null; }
                var until = DateTime.Now.AddMilliseconds(400);
                while (DateTime.Now < until)
                {
                    var buf = new byte[20];
                    var ar = fs.BeginRead(buf, 0, 20, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(Math.Max(1, (int)(until - DateTime.Now).TotalMilliseconds)))
                    {
                        CancelIoEx(fs.SafeFileHandle, IntPtr.Zero);
                        try { fs.EndRead(ar); } catch { }
                        return null;
                    }
                    int n;
                    try { n = fs.EndRead(ar); } catch { return null; }
                    if (n < 7 || buf[1] != idx) continue;
                    // HID++ 1.0 error (0x8F) or 2.0 error (0xFF) for our request: device absent / unsupported
                    if ((buf[0] == 0x10 && buf[2] == 0x8F) || (buf[0] == 0x11 && buf[2] == 0xFF && buf[3] == feat)) return null;
                    if (buf[0] == 0x11 && buf[2] == feat && buf[3] == req[3])
                    {
                        var p = new byte[16]; Array.Copy(buf, 4, p, 0, 16); return p;
                    }
                }
                return null;
            }

            [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeHandle h, IntPtr ov);
        }
    }

    /// <summary>Shared HID device-interface enumeration.</summary>
    internal static class HidEnum
    {
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr r; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

        public static List<string> Paths()
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
