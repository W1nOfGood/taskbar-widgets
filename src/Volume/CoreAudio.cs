using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace TaskbarWidgets.Volume
{
    // ---------------------------------------------------------------- COM interop (Core Audio)

    enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, int mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(16)] public IntPtr pad;
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(int ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(int ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(int ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s);
    }

    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName(string n, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath(string p, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // Undocumented but stable since Windows 7: used by every "switch output device" tool.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(); [PreserveSig] int GetDeviceFormat(); [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat(); [PreserveSig] int GetProcessingPeriod(); [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode(); [PreserveSig] int SetShareMode(); [PreserveSig] int GetPropertyValue(); [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility();
    }

    // ---------------------------------------------------------------- friendly wrappers

    internal sealed class AudioDevice
    {
        public string Id, Name;
    }

    internal sealed class AppSession
    {
        public int Pid;
        public string Name, ExePath, Identifier;
        /// <summary>Stable identity across re-enumerations (same app, same device).</summary>
        public string Key { get { return Identifier ?? ("pid:" + Pid); } }
        public bool System;
        public float Volume, Peak;
        public bool Muted;
        internal ISimpleAudioVolume Vol;
        internal IAudioMeterInformation Meter;
        internal IAudioSessionControl2 Ctl;

        static readonly Guid ctx = Guid.Empty;
        public void SetVolume(float v) { Guid g = ctx; if (Vol != null) Vol.SetMasterVolume(Math.Max(0, Math.Min(1, v)), ref g); Volume = v; }
        public void SetMute(bool m) { Guid g = ctx; if (Vol != null) Vol.SetMute(m, ref g); Muted = m; }
        public float LiveVolume() { float v = Volume; try { if (Vol != null) Vol.GetMasterVolume(out v); } catch { } return v; }
        public bool LiveMuted() { bool m = Muted; try { if (Vol != null) Vol.GetMute(out m); } catch { } return m; }
        public float LivePeak() { float p = 0; try { if (Meter != null) Meter.GetPeakValue(out p); } catch { } return p; }
    }

    /// <summary>Master volume, output devices and per-app sessions. All calls on the UI thread (STA).</summary>
    internal sealed class Audio : IDisposable
    {
        readonly IMMDeviceEnumerator en;
        IMMDevice device;
        IAudioEndpointVolume endpoint;
        IAudioMeterInformation masterMeter;
        public string DeviceId, DeviceName;
        static readonly Guid ctx = Guid.Empty;
        static Guid IID_EndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        static Guid IID_Meter = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
        static Guid IID_SessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        static PROPERTYKEY PKEY_FriendlyName = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

        public Audio()
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            EnsureDevice();
        }

        /// <summary>Re-acquires the default output if it changed. Cheap; called by the poll timer.</summary>
        public bool EnsureDevice()
        {
            IMMDevice d;
            if (en.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out d) != 0 || d == null)
            {
                Release(); DeviceId = null; DeviceName = "No output device"; return true;
            }
            string id; d.GetId(out id);
            if (id == DeviceId) { Marshal.ReleaseComObject(d); return false; }
            Release();
            device = d; DeviceId = id; DeviceName = NameOf(d);
            object o;
            Guid iid = IID_EndpointVolume;
            if (d.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o) == 0) endpoint = (IAudioEndpointVolume)o;
            iid = IID_Meter;
            if (d.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) masterMeter = (IAudioMeterInformation)o;
            return true;
        }

        void Release()
        {
            if (endpoint != null) { Marshal.ReleaseComObject(endpoint); endpoint = null; }
            if (masterMeter != null) { Marshal.ReleaseComObject(masterMeter); masterMeter = null; }
            if (device != null) { Marshal.ReleaseComObject(device); device = null; }
        }

        static string NameOf(IMMDevice d)
        {
            try
            {
                IPropertyStore ps;
                if (d.OpenPropertyStore(0, out ps) != 0) return "Speakers";
                try
                {
                    PROPVARIANT v; var k = PKEY_FriendlyName;
                    if (ps.GetValue(ref k, out v) == 0 && v.vt == 31 /*VT_LPWSTR*/)
                    {
                        string s = Marshal.PtrToStringUni(v.ptr);
                        Marshal.FreeCoTaskMem(v.ptr);
                        return s;
                    }
                }
                finally { Marshal.ReleaseComObject(ps); }
            }
            catch { }
            return "Speakers";
        }

        public float Volume
        {
            get { float v = 0; if (endpoint != null) endpoint.GetMasterVolumeLevelScalar(out v); return v; }
            set { Guid g = ctx; if (endpoint != null) endpoint.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, value)), ref g); }
        }

        public bool Muted
        {
            get { bool m = false; if (endpoint != null) endpoint.GetMute(out m); return m; }
            set { Guid g = ctx; if (endpoint != null) endpoint.SetMute(value, ref g); }
        }

        public float Peak { get { float p = 0; if (masterMeter != null) masterMeter.GetPeakValue(out p); return p; } }

        public List<AudioDevice> Outputs() { return OutputsOf(en); }

        static List<AudioDevice> OutputsOf(IMMDeviceEnumerator en)
        {
            var list = new List<AudioDevice>();
            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(EDataFlow.Render, 1 /*ACTIVE*/, out col) != 0) return list;
            int n; col.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IMMDevice d;
                if (col.Item(i, out d) != 0) continue;
                string id; d.GetId(out id);
                list.Add(new AudioDevice { Id = id, Name = NameOf(d) });
                Marshal.ReleaseComObject(d);
            }
            Marshal.ReleaseComObject(col);
            return list;
        }

        public static bool SetDefault(string id)
        {
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigClient();
                pc.SetDefaultEndpoint(id, ERole.Console);
                pc.SetDefaultEndpoint(id, ERole.Multimedia);
                pc.SetDefaultEndpoint(id, ERole.Communications);
                Marshal.ReleaseComObject(pc);
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- sessions

        static readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        internal sealed class Snapshot
        {
            public List<AppSession> Sessions = new List<AppSession>();
            public List<AudioDevice> Outputs = new List<AudioDevice>();
        }

        /// <summary>
        /// Lists output devices and per-app sessions with its own enumerator. Runs on a background (MTA)
        /// thread so the panel never waits on it; the UI thread then talks to the sessions through COM.
        /// </summary>
        public static Snapshot TakeSnapshot()
        {
            var snap = new Snapshot();
            var en2 = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            try
            {
                snap.Outputs = OutputsOf(en2);
                IMMDevice d;
                if (en2.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out d) == 0 && d != null)
                {
                    try { snap.Sessions = SessionsOf(d); } finally { Marshal.ReleaseComObject(d); }
                }
            }
            finally { Marshal.ReleaseComObject(en2); }
            foreach (var s in snap.Sessions) if (!s.System) IconFor(s.ExePath); // warm the icon cache off the UI thread
            return snap;
        }

        /// <summary>Sessions of the default output only (no device list, no icons). Background thread.</summary>
        public static List<AppSession> TakeSessions()
        {
            var en2 = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            try
            {
                IMMDevice d;
                if (en2.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out d) != 0 || d == null) return new List<AppSession>();
                try { return SessionsOf(d); } finally { Marshal.ReleaseComObject(d); }
            }
            finally { Marshal.ReleaseComObject(en2); }
        }

        public static void Release(List<AppSession> list) { foreach (var s in list) ReleaseSession(s); }

        public static void UpdatePeaks(List<AppSession> list)
        {
            foreach (var s in list) { float p = 0; try { if (s.Meter != null) s.Meter.GetPeakValue(out p); } catch { } s.Peak = p; }
        }

        static List<AppSession> SessionsOf(IMMDevice device)
        {
            var sessions = new List<AppSession>();
            object o; Guid iid = IID_SessionManager2;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return sessions;
            var mgr = (IAudioSessionManager2)o;
            IAudioSessionEnumerator e;
            if (mgr.GetSessionEnumerator(out e) == 0)
            {
                int n; e.GetCount(out n);
                var byPid = new Dictionary<int, AppSession>();
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl2 c;
                    if (e.GetSession(i, out c) != 0 || c == null) continue;
                    int state; c.GetState(out state);
                    if (state == 2 /*expired*/) { Marshal.ReleaseComObject(c); continue; }
                    int pid; c.GetProcessId(out pid);
                    bool sys = c.IsSystemSoundsSession() == 0;
                    if (byPid.ContainsKey(pid) && !sys) { Marshal.ReleaseComObject(c); continue; } // one row per app
                    var s = new AppSession { Pid = pid, System = sys, Ctl = c };
                    string sid; if (c.GetSessionIdentifier(out sid) == 0) s.Identifier = sid;
                    s.Vol = c as ISimpleAudioVolume;
                    s.Meter = c as IAudioMeterInformation;
                    if (s.Vol != null) { s.Vol.GetMasterVolume(out s.Volume); s.Vol.GetMute(out s.Muted); }
                    Describe(s, c);
                    byPid[pid] = s;
                    sessions.Add(s);
                }
                Marshal.ReleaseComObject(e);
            }
            Marshal.ReleaseComObject(mgr);
            sessions.Sort((a, b) => a.System != b.System ? (a.System ? 1 : -1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return sessions;
        }

        static void ReleaseSession(AppSession s)
        {
            if (s.Ctl != null) { try { Marshal.ReleaseComObject(s.Ctl); } catch { } s.Ctl = null; s.Vol = null; s.Meter = null; }
        }

        static void Describe(AppSession s, IAudioSessionControl2 c)
        {
            if (s.System) { s.Name = "System sounds"; return; }
            s.ExePath = Native.ProcessPath(s.Pid);
            string dn; c.GetDisplayName(out dn);
            if (!string.IsNullOrEmpty(dn) && !dn.StartsWith("@")) { s.Name = dn; return; }
            s.Name = AppName(s.ExePath, s.Pid);
        }

        public static string AppName(string exe, int pid)
        {
            if (!string.IsNullOrEmpty(exe))
            {
                try
                {
                    var fv = FileVersionInfo.GetVersionInfo(exe);
                    if (!string.IsNullOrWhiteSpace(fv.FileDescription)) return fv.FileDescription.Trim();
                }
                catch { }
                return Path.GetFileNameWithoutExtension(exe);
            }
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; } catch { return "App"; }
        }

        public static Bitmap IconFor(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            Bitmap b;
            lock (iconCache) if (iconCache.TryGetValue(exe, out b)) return b;
            try
            {
                using (var ic = Icon.ExtractAssociatedIcon(exe)) b = ic == null ? null : ic.ToBitmap();
            }
            catch { b = null; }
            lock (iconCache)
            {
                if (iconCache.Count > 40) iconCache.Clear();
                iconCache[exe] = b;
            }
            return b;
        }

        public void Dispose()
        {
            Release();
            Marshal.ReleaseComObject(en);
        }
    }
}
