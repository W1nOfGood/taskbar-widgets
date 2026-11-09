using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarWidgets.Volume
{
    internal sealed class NowPlaying
    {
        public GlobalSystemMediaTransportControlsSession Session;
        public string AppId, AppName, Title, Artist, Album;
        public bool Playing, CanPrev, CanNext, CanPlayPause, CanSeek;
        public TimeSpan Position, Duration;
        public DateTime PositionAt;
        public Bitmap Art, AppIcon;

        public TimeSpan LivePosition
        {
            get
            {
                if (!Playing || Duration <= TimeSpan.Zero) return Position;
                var p = Position + (DateTime.UtcNow - PositionAt);
                return p > Duration ? Duration : p;
            }
        }

        public string Line
        {
            get
            {
                if (string.IsNullOrEmpty(Title)) return AppName ?? "";
                return string.IsNullOrEmpty(Artist) ? Title : Artist + " – " + Title;
            }
        }
    }

    /// <summary>Wraps the Windows "now playing" sessions (same source as the volume flyout). Event driven.</summary>
    internal sealed class MediaManager
    {
        public event Action Changed;
        readonly Control ui;
        GlobalSystemMediaTransportControlsSessionManager mgr;
        readonly Dictionary<string, NowPlaying> sessions = new Dictionary<string, NowPlaying>();
        readonly HashSet<GlobalSystemMediaTransportControlsSession> hooked = new HashSet<GlobalSystemMediaTransportControlsSession>();
        public string PinnedAppId; // user picked a session in the panel

        public MediaManager(Control ui) { this.ui = ui; }

        public async void Start()
        {
            if (Demo.On) { LoadDemo(); return; }
            try
            {
                mgr = await Rt.Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                mgr.SessionsChanged += (m, a) => Post(Sync);
                mgr.CurrentSessionChanged += (m, a) => Post(Raise);
                Sync();
            }
            catch (Exception ex) { Log.Write("media", ex); }
        }

        void LoadDemo()
        {
            var art = new Bitmap(160, 160);
            using (var g = Graphics.FromImage(art))
            using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 160, 160), Color.FromArgb(255, 94, 58), Color.FromArgb(70, 40, 160), 45f))
            {
                g.FillRectangle(br, 0, 0, 160, 160);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var sun = new SolidBrush(Color.FromArgb(200, 255, 220, 120))) g.FillEllipse(sun, 45, 30, 70, 70);
                using (var hill = new SolidBrush(Color.FromArgb(230, 25, 18, 50)))
                    g.FillPolygon(hill, new[] { new Point(0, 160), new Point(0, 110), new Point(50, 80), new Point(95, 115), new Point(130, 90), new Point(160, 108), new Point(160, 160) });
            }
            sessions["demo.spotify"] = new NowPlaying
            {
                AppId = "demo.spotify", AppName = "Spotify", Title = "Neon Skyline", Artist = "The Midnight Drive", Album = "Afterglow",
                Playing = true, CanPrev = true, CanNext = true, CanPlayPause = true, CanSeek = true,
                Duration = TimeSpan.FromSeconds(222), Position = TimeSpan.FromSeconds(78), PositionAt = DateTime.UtcNow, Art = art
            };
            sessions["demo.browser"] = new NowPlaying { AppId = "demo.browser", AppName = "Browser", Title = "Lo-fi beats to code to", Artist = "Study Radio", CanPlayPause = true };
            Raise();
        }

        void Post(Action a) { if (!ui.IsDisposed) try { ui.BeginInvoke(a); } catch { } }
        void Raise() { if (Changed != null) Changed(); }

        public List<NowPlaying> All { get { return sessions.Values.OrderByDescending(s => s.Playing).ThenBy(s => s.AppName).ToList(); } }

        /// <summary>The session to show: pinned one, else a playing one, else Windows' current one.</summary>
        public NowPlaying Current
        {
            get
            {
                NowPlaying np;
                if (PinnedAppId != null && sessions.TryGetValue(PinnedAppId, out np)) return np;
                // A music app (Spotify, Apple Music...) is "the music" even when a lecture/video plays in a browser
                var music = sessions.Values.FirstOrDefault(s => s.Playing && IsMusicApp(s.AppId));
                if (music != null) return music;
                var playing = sessions.Values.FirstOrDefault(s => s.Playing);
                if (playing != null) return playing;
                var pausedMusic = sessions.Values.FirstOrDefault(s => IsMusicApp(s.AppId));
                if (pausedMusic != null) return pausedMusic;
                try
                {
                    var cur = mgr == null ? null : mgr.GetCurrentSession();
                    if (cur != null && sessions.TryGetValue(cur.SourceAppUserModelId, out np)) return np;
                }
                catch { }
                return sessions.Values.FirstOrDefault();
            }
        }

        void Sync()
        {
            if (mgr == null) return;
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> list;
            try { list = mgr.GetSessions(); } catch { return; }
            var ids = new HashSet<string>();
            foreach (var s in list)
            {
                string id = s.SourceAppUserModelId;
                ids.Add(id);
                NowPlaying np;
                if (!sessions.TryGetValue(id, out np))
                {
                    np = new NowPlaying { AppId = id, AppName = FriendlyApp(id) };
                    np.AppIcon = AppIconFor(id);
                    sessions[id] = np;
                }
                np.Session = s;
                if (!hooked.Contains(s))
                {
                    hooked.Add(s);
                    var cap = s;
                    s.MediaPropertiesChanged += (o, a) => Post(() => LoadProps(cap));
                    s.PlaybackInfoChanged += (o, a) => Post(() => LoadPlayback(cap));
                    s.TimelinePropertiesChanged += (o, a) => Post(() => LoadTimeline(cap));
                }
                LoadPlayback(s); LoadTimeline(s); LoadProps(s);
            }
            foreach (var k in sessions.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                var gone = sessions[k];
                if (gone.Art != null) gone.Art.Dispose();
                sessions.Remove(k);
            }
            hooked.RemoveWhere(s => !list.Contains(s));
            if (PinnedAppId != null && !sessions.ContainsKey(PinnedAppId)) PinnedAppId = null;
            Raise();
        }

        NowPlaying Find(GlobalSystemMediaTransportControlsSession s)
        {
            NowPlaying np;
            try { return sessions.TryGetValue(s.SourceAppUserModelId, out np) ? np : null; } catch { return null; }
        }

        void LoadPlayback(GlobalSystemMediaTransportControlsSession s)
        {
            var np = Find(s); if (np == null) return;
            try
            {
                var pi = s.GetPlaybackInfo();
                bool wasPlaying = np.Playing;
                np.Playing = pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                np.CanPrev = pi.Controls.IsPreviousEnabled;
                np.CanNext = pi.Controls.IsNextEnabled;
                np.CanPlayPause = pi.Controls.IsPlayPauseToggleEnabled || pi.Controls.IsPlayEnabled || pi.Controls.IsPauseEnabled;
                np.CanSeek = pi.Controls.IsPlaybackPositionEnabled;
                if (wasPlaying != np.Playing) { np.Position = np.LivePositionFrozen(wasPlaying); np.PositionAt = DateTime.UtcNow; }
            }
            catch { }
            Raise();
        }

        void LoadTimeline(GlobalSystemMediaTransportControlsSession s)
        {
            var np = Find(s); if (np == null) return;
            try
            {
                var tl = s.GetTimelineProperties();
                np.Duration = tl.EndTime - tl.StartTime;
                np.Position = tl.Position - tl.StartTime;
                var at = tl.LastUpdatedTime.UtcDateTime;
                np.PositionAt = at.Year < 2000 ? DateTime.UtcNow : at;
            }
            catch { }
            Raise();
        }

        async void LoadProps(GlobalSystemMediaTransportControlsSession s)
        {
            var np = Find(s); if (np == null) return;
            try
            {
                var p = await Rt.Await(s.TryGetMediaPropertiesAsync());
                if (p == null) return;
                bool trackChanged = np.Title != p.Title || np.Artist != p.Artist;
                np.Title = p.Title; np.Artist = p.Artist; np.Album = p.AlbumTitle;
                if (string.IsNullOrEmpty(np.Artist) && !string.IsNullOrEmpty(p.AlbumArtist)) np.Artist = p.AlbumArtist;
                Raise();
                if (trackChanged || np.Art == null)
                {
                    var art = p.Thumbnail == null ? null : await ReadImage(p.Thumbnail);
                    if (np.Art != null) np.Art.Dispose();
                    np.Art = art;
                    Raise();
                }
            }
            catch (Exception ex) { Log.Write("media", ex); }
        }

        static async Task<Bitmap> ReadImage(IRandomAccessStreamReference r)
        {
            try
            {
                using (var stream = await Rt.Await(r.OpenReadAsync()))
                {
                    uint size = (uint)Math.Min(stream.Size, 8 * 1024 * 1024);
                    if (size == 0) return null;
                    using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                    {
                        uint got = await Rt.Await(reader.LoadAsync(size));
                        var bytes = new byte[got];
                        reader.ReadBytes(bytes);
                        using (var ms = new MemoryStream(bytes))
                        using (var img = Image.FromStream(ms))
                        {
                            // keep a small copy only: the panel shows it at ~120 px
                            int side = 160;
                            var b = new Bitmap(side, side);
                            using (var g = Graphics.FromImage(b))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                float sc = Math.Max(side / (float)img.Width, side / (float)img.Height);
                                float w = img.Width * sc, h = img.Height * sc;
                                g.DrawImage(img, (side - w) / 2, (side - h) / 2, w, h);
                            }
                            return b;
                        }
                    }
                }
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- controls

        public void PlayPause(NowPlaying np) { if (np != null && np.Session != null) Rt.Fire(np.Session.TryTogglePlayPauseAsync()); }
        public void Next(NowPlaying np) { if (np != null && np.Session != null) Rt.Fire(np.Session.TrySkipNextAsync()); }
        public void Prev(NowPlaying np) { if (np != null && np.Session != null) Rt.Fire(np.Session.TrySkipPreviousAsync()); }
        public void Seek(NowPlaying np, TimeSpan to)
        {
            if (np == null || np.Session == null || !np.CanSeek) return;
            Rt.Fire(np.Session.TryChangePlaybackPositionAsync(to.Ticks));
            np.Position = to; np.PositionAt = DateTime.UtcNow;
        }

        // ---------------------------------------------------------------- app names & icons

        static readonly string[] musicApps = {
            "spotify", "applemusic", "itunes", "zunemusic", "amazonmusic", "amazon.music", "deezer", "tidal", "qobuz", "pandora",
            "soundcloud", "ytmdesktop", "youtube-music", "youtubemusic", "foobar2000", "musicbee", "aimp", "winamp", "dopamine",
            "plexamp", "cider", "audacious", "clementine", "strawberry", "nuclear", "musescore", "napster", "anghami", "joox", "line music" };

        /// <summary>Dedicated music players (not browsers or video players).</summary>
        public static bool IsMusicApp(string aumid)
        {
            string a = (aumid ?? "").ToLowerInvariant();
            return musicApps.Any(m => a.Contains(m));
        }

        static string FriendlyApp(string aumid)
        {
            string id = aumid ?? "";
            string low = id.ToLowerInvariant();
            if (low.Contains("spotify")) return "Spotify";
            if (low.Contains("chrome")) return "Chrome";
            if (low.Contains("msedge") || low.Contains("microsoftedge")) return "Edge";
            if (low.Contains("brave")) return "Brave";
            if (low.Contains("firefox") || low == "308046b0af4a39cb") return "Firefox";
            if (low.Contains("opera")) return "Opera";
            if (low.Contains("zunemusic") || low.Contains("media.player")) return "Media Player";
            if (low.Contains("applemusic") || low.Contains("itunes")) return "Apple Music";
            if (low.Contains("vlc")) return "VLC";
            if (low.Contains("discord")) return "Discord";
            if (low.Contains("potplayer")) return "PotPlayer";
            string exe = ExeFor(id);
            if (exe != null) return Audio.AppName(exe, 0);
            int bang = id.IndexOf('!');
            string s = bang > 0 ? id.Substring(bang + 1) : id;
            s = s.Replace(".exe", "");
            int dot = s.LastIndexOf('.');
            return dot >= 0 && dot < s.Length - 1 ? s.Substring(dot + 1) : s;
        }

        /// <summary>Finds a running executable for an AUMID like "Spotify.exe" or "chrome".</summary>
        static string ExeFor(string aumid)
        {
            if (string.IsNullOrEmpty(aumid) || aumid.Contains("!")) return null;
            string name = Path.GetFileNameWithoutExtension(aumid.Contains("\\") ? aumid : aumid.Replace(".exe", "") + ".exe");
            Process[] ps = null;
            try
            {
                ps = Process.GetProcessesByName(name);
                foreach (var p in ps)
                {
                    string path = Native.ProcessPath(p.Id);
                    if (path != null) return path;
                }
            }
            catch { }
            finally { if (ps != null) foreach (var p in ps) p.Dispose(); }
            return null;
        }

        static Bitmap AppIconFor(string aumid)
        {
            string exe = ExeFor(aumid);
            return exe == null ? null : Audio.IconFor(exe);
        }
    }

    internal static class NowPlayingExt
    {
        /// <summary>When playback state flips, freeze the interpolated position so it does not jump.</summary>
        public static TimeSpan LivePositionFrozen(this NowPlaying np, bool wasPlaying)
        {
            if (!wasPlaying || np.Duration <= TimeSpan.Zero) return np.Position;
            var p = np.Position + (DateTime.UtcNow - np.PositionAt);
            return p > np.Duration ? np.Duration : p;
        }
    }
}
