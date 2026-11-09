using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace TaskbarWidgets.Battery
{
    /// <summary>
    /// Reads the real battery level from Sony headphones/earbuds over Sony's own control channel
    /// (the one the Sony "Headphones Connect" app uses). Windows' generic Bluetooth battery value is often
    /// stale or wrong for these (e.g. 90% while the headset is at 26%).
    /// Frames: 0x3E [type seq len(4, big endian) payload checksum] 0x3C, with 0x3C/0x3D/0x3E escaped as 0x3D, b &amp; 0xEF.
    /// Battery: v2 request 0x22 type → reply 0x23 type level charging (dual: left/right pairs); v1 uses 0x10 → 0x11.
    /// </summary>
    internal static class SonyBattery
    {
        static readonly Guid V2 = new Guid("956C7B26-D49A-4BA8-B03F-B17D393CB6E2");
        static readonly Guid V1 = new Guid("96CC203E-5068-46ad-B32D-E316F5E069BA");

        public sealed class Result { public int Level = -1; public bool Charging; }

        /// <summary>Blocking; call from a background thread. addresses = connected devices (12 hex chars, upper case).</summary>
        public static Dictionary<string, Result> Query(ICollection<string> addresses)
        {
            var res = new Dictionary<string, Result>();
            if (addresses.Count == 0) return res;
            foreach (var proto in new[] { V2, V1 })
            {
                IReadOnlyList<DeviceInformation> infos;
                try
                {
                    var t = Rt.Await(DeviceInformation.FindAllAsync(RfcommDeviceService.GetDeviceSelector(RfcommServiceId.FromUuid(proto))));
                    if (!t.Wait(5000)) { Log.Info("battery", "sony: service lookup timed out"); continue; }
                    infos = t.Result;
                }
                catch (Exception ex) { Log.Info("battery", "sony: lookup failed " + ex.GetBaseException().Message); continue; }
                Log.Info("battery", "sony: " + infos.Count + " service(s) for " + proto + ", connected=" + string.Join(",", addresses));
                foreach (var info in infos)
                {
                    string addr = AddressOf(info.Id);
                    Log.Info("battery", "sony: service " + info.Id + " -> " + addr);
                    if (addr == null || !addresses.Contains(addr) || res.ContainsKey(addr)) continue;
                    try
                    {
                        var q = QueryOne(info.Id, proto == V2);
                        bool done = q.Wait(9000);
                        Log.Info("battery", "sony: query " + addr + " done=" + done + " level=" + (done && q.Result != null ? q.Result.Level.ToString() : "-"));
                        if (done && q.Result != null && q.Result.Level >= 0) res[addr] = q.Result;
                    }
                    catch (Exception ex) { Log.Write("battery", ex); }
                }
            }
            return res;
        }

        // "Bluetooth#Bluetooth00:11:22:33:44:55-aa:bb:cc:dd:ee:ff#RFCOMM:..." -> "AABBCCDDEEFF"
        static string AddressOf(string id)
        {
            int dash = id.IndexOf('-');
            int hash = dash < 0 ? -1 : id.IndexOf('#', dash);
            if (dash < 0 || hash < 0) return null;
            string a = id.Substring(dash + 1, hash - dash - 1).Replace(":", "").ToUpperInvariant();
            return a.Length == 12 ? a : null;
        }

        static async Task<Result> QueryOne(string serviceId, bool v2)
        {
            var svc = await Rt.Await(RfcommDeviceService.FromIdAsync(serviceId));
            if (svc == null) return null;
            using (svc)
            using (var sock = new StreamSocket())
            {
                var connect = Rt.Await(sock.ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName));
                if (await Task.WhenAny(connect, Task.Delay(6000)) != connect) return null;
                await connect;
                var link = new Link(sock);
                byte req = v2 ? (byte)0x22 : (byte)0x10, rep = (byte)(req + 1);

                await link.Send(new byte[] { 0x00, 0x00 });          // init / protocol info
                await link.Collect(700);
                foreach (byte type in new byte[] { 0x00, 0x01 })     // 0 = single battery (headphones), 1 = left/right (earbuds)
                {
                    await link.Send(new byte[] { req, type });
                    foreach (var p in await link.Collect(1200))
                    {
                        if (p.Length < 4 || p[0] != rep || p[1] != type) continue;
                        var r = new Result { Level = p[2], Charging = p[3] == 1 };
                        if (type == 0x01 && p.Length >= 6)
                        {
                            // earbuds: report the lower bud (0 means that bud is in the case / not reported)
                            int l = p[2], rr = p[4];
                            r.Level = l == 0 ? rr : rr == 0 ? l : Math.Min(l, rr);
                            r.Charging = p[3] == 1 || p[5] == 1;
                        }
                        if (r.Level >= 0 && r.Level <= 100) return r;
                    }
                }
                return null;
            }
        }

        /// <summary>One RFCOMM conversation: framing, escaping, sequence numbers and ACKs.</summary>
        sealed class Link
        {
            readonly DataWriter w;
            readonly DataReader r;
            readonly List<byte> buf = new List<byte>();
            Task<uint> pending;
            byte seq;

            public Link(StreamSocket s)
            {
                w = new DataWriter(s.OutputStream);
                r = new DataReader(s.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
            }

            public Task Send(byte[] payload) { return Frame(0x0c, seq, payload); }

            async Task Frame(byte type, byte sq, byte[] payload)
            {
                var body = new List<byte> { type, sq, (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length };
                body.AddRange(payload);
                byte sum = 0; foreach (var b in body) sum += b;
                body.Add(sum);
                var frame = new List<byte> { 0x3e };
                foreach (var b in body)
                {
                    if (b == 0x3c || b == 0x3d || b == 0x3e) { frame.Add(0x3d); frame.Add((byte)(b & 0xef)); }
                    else frame.Add(b);
                }
                frame.Add(0x3c);
                w.WriteBytes(frame.ToArray());
                await Rt.Await(w.StoreAsync());
            }

            /// <summary>Reads for up to ms milliseconds; returns the payloads of command messages (ACKs them).</summary>
            public async Task<List<byte[]>> Collect(int ms)
            {
                var res = new List<byte[]>();
                var until = DateTime.Now.AddMilliseconds(ms);
                while (DateTime.Now < until)
                {
                    if (pending == null) pending = Rt.Await(r.LoadAsync(512));
                    var load = pending;
                    if (await Task.WhenAny(load, Task.Delay(Math.Max(1, (int)(until - DateTime.Now).TotalMilliseconds))) != load) break;
                    pending = null;
                    uint n = await load;
                    if (n == 0) break;
                    var chunk = new byte[n]; r.ReadBytes(chunk); buf.AddRange(chunk);
                    int end;
                    while ((end = buf.IndexOf(0x3c)) >= 0)
                    {
                        int start = buf.IndexOf(0x3e);
                        if (start < 0 || start > end) { buf.RemoveRange(0, end + 1); continue; }
                        var raw = buf.Skip(start + 1).Take(end - start - 1).ToList();
                        buf.RemoveRange(0, end + 1);
                        var un = new List<byte>();
                        for (int i = 0; i < raw.Count; i++) un.Add(raw[i] == 0x3d && i + 1 < raw.Count ? (byte)(raw[++i] | 0x10) : raw[i]);
                        if (un.Count < 7) continue;
                        byte type = un[0], sq = un[1];
                        int len = (un[2] << 24) | (un[3] << 16) | (un[4] << 8) | un[5];
                        if (len < 0 || 6 + len > un.Count) continue;
                        if (type == 0x01) { seq = (byte)(1 - seq); continue; }  // ACK for our message
                        res.Add(un.Skip(6).Take(len).ToArray());
                        await Frame(0x01, (byte)(1 - sq), new byte[0]);       // ACK theirs
                    }
                }
                return res;
            }
        }
    }
}
