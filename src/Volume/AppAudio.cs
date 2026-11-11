using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TaskbarWidgets.Volume
{
    /// <summary>
    /// Per-app audio for the widget:
    ///  - finds the audio session of the app that is playing music (so scrolling changes only that app, not the game),
    ///  - finds voice-chat apps (Discord, Teams, Zoom...) and other playing media (lectures, videos),
    ///  - one controller drives the music app's volume:  applied = your volume × song leveling × ducking.
    ///      * leveling: measures each song's loudness (the session meter is read before the app volume) and turns loud
    ///        songs down / quiet songs up, so every song plays at about the same level,
    ///      * ducking: lowers the music while someone talks in voice chat or a lecture/video plays.
    /// Your own volume is saved to disk while we adjust it, so a crash or forced close can never leave the music low.
    /// </summary>
    internal sealed class AppAudio
    {
        public event Action Changed;
        readonly Control ui;
        readonly Settings S;
        readonly Func<NowPlaying> current;
        readonly Func<List<NowPlaying>> allMedia;
        List<AppSession> live = new List<AppSession>();
        bool busy;
        readonly Timer refresh = new Timer { Interval = 4000 };
        readonly Timer tick = new Timer { Interval = 100 };

        // ---- controller state
        string managedKey;           // music session whose volume we are driving
        float baseVol = 1f;          // the user's volume for it (what we restore to)
        float lastSet = -1f;         // what we last wrote, to notice the user changing it meanwhile
        float duckFactor = 1f;       // ducking multiplier (1 = not lowered)
        double levelDb;              // leveling gain in dB relative to your volume: about -headroom for a typical song,
                                     // 0 for a quiet one (lifted), lower for a loud one
        DateTime lastTalk = DateTime.MinValue, lastVideo = DateTime.MinValue;

        // ---- leveling state
        double refDb = double.NaN;   // your typical loudness, learned over many songs
        string lastTrack; DateTime trackAt = DateTime.MinValue;

        static readonly string[] voiceApps = {
            "discord", "discordptb", "discordcanary", "vesktop", "ms-teams", "teams", "zoom", "slack", "skype", "telegram",
            "whatsapp", "signal", "mumble", "ts3client_win64", "ts3client_win32", "teamspeak", "webex", "ciscocollabhost", "guilded", "element", "ventrilo" };

        public static readonly string[] LevelNames = { "Off", "Gentle", "Normal", "Strong" };

        public AppAudio(Control ui, Settings s, Func<NowPlaying> current, Func<List<NowPlaying>> allMedia)
        {
            this.ui = ui; S = s; this.current = current; this.allMedia = allMedia;
            refresh.Tick += (o, e) => Refresh();
            tick.Tick += (o, e) => Tick();
            double r;
            if (double.TryParse(S.Get("levelRef2", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) refDb = r;
        }

        public void Start() { refresh.Interval = S.Bool("rememberVol", true) ? 2000 : 4000; refresh.Start(); Refresh(); }

        public void Refresh()
        {
            if (busy || Demo.On) return;
            busy = true;
            Task.Run(() => Audio.TakeSessions()).ContinueWith(t =>
            {
                busy = false;
                if (t.IsFaulted || ui.IsDisposed) return;
                var old = live;
                live = t.Result;
                Audio.Release(old);
                RecoverAfterCrash();
                RememberVolumes();
                var np = current();
                var mu = Music;
                Log.Info("volume", "sessions: " + string.Join(" | ", live.Select(x => x.Name + " [" + Path.GetFileName(x.ExePath ?? "") + "] peak " +
                                                    x.LivePeak().ToString("F3", CultureInfo.InvariantCulture) + " vol " + x.LiveVolume().ToString("F2", CultureInfo.InvariantCulture))) +
                         " || media app=" + (np == null ? "-" : np.AppId) + " -> music=" + (mu == null ? "none" : mu.Name) +
                         " || voice=" + string.Join(",", Voice.Select(x => x.Name)) + " || video=" + string.Join(",", Videos.Select(x => x.Name)));
                bool need = mu != null && (LevelMode > 0 || S.Bool("duck", true) && Voice.Count > 0 || S.Bool("duckVideo", true) && Videos.Count > 0) || managedKey != null;
                if (need && !tick.Enabled) tick.Start();
                if (!need && tick.Enabled) tick.Stop();
                if (Changed != null) Changed();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public int LevelMode { get { return Math.Max(0, Math.Min(3, S.Int("level", 2))); } }

        // ---------------------------------------------------------------- matching

        static string ExeName(AppSession s) { return Path.GetFileNameWithoutExtension(s.ExePath ?? "").ToLowerInvariant(); }

        public static bool IsVoice(AppSession s)
        {
            if (s.System) return false;
            string exe = ExeName(s);
            return voiceApps.Contains(exe) || exe.Contains("discord");
        }

        /// <summary>Does this audio session belong to the media app with this AUMID?</summary>
        static bool Matches(AppSession s, string aumid)
        {
            if (s.System || string.IsNullOrEmpty(aumid)) return false;
            string a = aumid.ToLowerInvariant();
            string id = (s.Identifier ?? "").ToLowerInvariant(), path = (s.ExePath ?? "").ToLowerInvariant(), exe = ExeName(s);
            int bang = a.IndexOf('!');
            if (bang > 0)
            {
                // packaged app: "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> lives in ...\Microsoft.ZuneMusic_...\
                string fam = a.Substring(0, bang);
                int us = fam.IndexOf('_');
                string name = us > 0 ? fam.Substring(0, us) : fam;
                return path.Contains(name) || id.Contains(name);
            }
            if (a == "308046b0af4a39cb") return exe == "firefox"; // Firefox's AUMID is a hash
            var wanted = new List<string> { Path.GetFileNameWithoutExtension(a) };
            int dot = a.LastIndexOf('.');
            if (dot >= 0 && !a.EndsWith(".exe")) wanted.Add(a.Substring(dot + 1)); // "com.squirrel.Discord.Discord" -> "discord"
            return wanted.Any(w => w.Length > 0 && (exe == w || id.Contains("\\" + w + ".exe")));
        }

        /// <summary>Audio session of the app whose track the widget shows (null if not found / nothing playing).</summary>
        public AppSession Music
        {
            get
            {
                var np = current();
                if (np == null) return null;
                var mine = live.Where(s => Matches(s, np.AppId)).ToList();
                if (mine.Count <= 1) return mine.FirstOrDefault();
                // the app has more than one process with sound (Apple Music: the app and its player): take the one
                // that is playing, and stay with the one we drive unless another one is clearly the one playing
                var cur = mine.FirstOrDefault(s => s.Key == managedKey);
                var loud = mine.OrderByDescending(s => s.LivePeak()).First();
                if (cur != null && (cur.LivePeak() > 0.004f || loud.LivePeak() <= 0.004f)) return cur;
                return loud.LivePeak() > 0.004f ? loud : mine[0];
            }
        }

        public List<AppSession> Voice { get { return live.Where(IsVoice).ToList(); } }

        /// <summary>Audio sessions of other media that is playing (a lecture or video in a browser, a video player...).</summary>
        public List<AppSession> Videos
        {
            get
            {
                var music = current();
                var res = new List<AppSession>();
                if (music == null) return res;
                var musicSession = Music;
                foreach (var np in allMedia())
                {
                    if (!np.Playing || np.AppId == music.AppId || MediaManager.IsMusicApp(np.AppId)) continue;
                    foreach (var s in live)
                        if (Matches(s, np.AppId) && (musicSession == null || s.Key != musicSession.Key) && !res.Contains(s)) res.Add(s);
                }
                return res;
            }
        }

        // ---------------------------------------------------------------- user volume changes

        /// <summary>The music app's volume as the user set it (ignores our temporary adjustments).</summary>
        public float MusicVolume(AppSession m)
        {
            if (m == null) return 0;
            return managedKey == m.Key ? baseVol : m.LiveVolume();
        }

        public void SetMusicVolume(AppSession m, float v)
        {
            v = Math.Max(0, Math.Min(1, v));
            if (managedKey == m.Key) { baseVol = v; SaveBase(); Apply(m, true); }
            else m.SetVolume(v);
            if (v > 0 && m.LiveMuted()) m.SetMute(false);
        }

        /// <summary>Leveling headroom: typical songs play this much under your volume so quiet ones can be lifted.</summary>
        double Headroom { get { return LevelMode == 1 ? 3 : LevelMode == 3 ? 6 : LevelMode == 2 ? 4 : 0; } }
        double MaxCut { get { return LevelMode == 1 ? 6 : LevelMode == 3 ? 15 : 10; } }

        float Multiplier
        {
            get
            {
                return (float)(Math.Pow(10, levelDb / 20) * duckFactor);
            }
        }

        void Apply(AppSession m, bool force)
        {
            float v = Math.Max(0, Math.Min(1, baseVol * Multiplier));
            if (force || lastSet < 0 || Math.Abs(v - lastSet) > 0.002f) { m.SetVolume(v); lastSet = v; }
        }

        // ---------------------------------------------------------------- the controller (every 100 ms while needed)

        void Tick()
        {
            var music = Music;
            var np = current();
            if (music == null || IsVoice(music)) { Release(); tick.Stop(); return; }
            if (managedKey != null && managedKey != music.Key) Release(); // switched to another music app: give the old one back

            bool playing = np != null && np.Playing;
            var voice = S.Bool("duck", true) ? Voice : new List<AppSession>();
            var videos = S.Bool("duckVideo", true) ? Videos : new List<AppSession>();

            // --- ducking target
            float peak = 0;
            foreach (var v in voice) peak = Math.Max(peak, v.LivePeak());
            // the music can leak into the voice app (audio routed through a virtual cable, your own stream...):
            // take out the part that is just the music, or the music would lower itself in a loop
            float musicOut = playing ? music.LivePeak() * music.LiveVolume() : 0; // the meter reads before the app volume
            if (Talking(peak, Echo(peak, musicOut) * musicOut) && playing) lastTalk = DateTime.Now;
            float vpeak = 0;
            foreach (var v in videos) vpeak = Math.Max(vpeak, v.LivePeak());
            if (vpeak > 0.015f && playing) lastVideo = DateTime.Now;
            bool duck = (DateTime.Now - lastTalk).TotalMilliseconds < 1500 || (DateTime.Now - lastVideo).TotalMilliseconds < 4000;
            float amount = Math.Max(0.1f, Math.Min(0.95f, S.Int("duckAmount", 60) / 100f));
            float duckTarget = duck ? 1f - amount : 1f;
            if (duck != wasDucking)
            {
                wasDucking = duck;
                Log.Info("volume", string.Format(CultureInfo.InvariantCulture, "duck {0}: voice peak {1:F3} (floor {2:F3}, talk {3:F1} s ago) video peak {4:F3} [{5}] (video {6:F1} s ago)",
                    duck ? "ON" : "off", peak, voiceFloor, (DateTime.Now - lastTalk).TotalSeconds, vpeak,
                    string.Join(",", videos.Select(x => x.Name)), (DateTime.Now - lastVideo).TotalSeconds));
            }

            // nothing to do at all: stay out of the way
            if (managedKey == null && LevelMode == 0 && duckTarget >= 1f) return;

            if (managedKey == null) StartManaging(music);

            // the user moved the app's own slider (Windows mixer, the app itself...) since our last write: adopt it
            float cur = music.LiveVolume();
            if (lastSet >= 0 && Math.Abs(cur - lastSet) > 0.02f)
            {
                baseVol = Math.Min(1f, cur / Math.Max(0.02f, Multiplier));
                SaveBase();
            }

            duckFactor += (duckTarget - duckFactor) * (duckTarget < duckFactor ? 0.35f : 0.1f); // quick down, slow up
            if (Math.Abs(duckFactor - duckTarget) < 0.01f) duckFactor = duckTarget;

            if (LevelMode > 0) UpdateLevel(music, np, playing);
            else levelDb = MoveTowards(levelDb, 0, 0.3); // fade leveling out after it is switched off

            Apply(music, false);

            // fully back to the user's volume and nothing else to do: hand the volume back
            if (LevelMode == 0 && duckFactor >= 1f && Math.Abs(levelDb) < 0.05) Release();
        }

        // ---- voice detection: open mics send hiss, clicks and bumps all the time, which must not lower the music
        bool wasDucking;
        readonly float[] echoRatios = new float[100]; // voice level ÷ music level, last 10 s of music
        int echoN, echoIdx;
        float echo;                                   // how much of the music shows up in the voice app (0 = none)

        /// <summary>
        /// Learns how much of the music leaks into the voice app. The voice level is compared with the music's
        /// output; a low percentile of the ratio over 10 s is the leak, because an echo is there all the time and
        /// talking is not. Without a leak the ratio is only the call's hiss, so it stays near 0.
        /// </summary>
        float Echo(float voicePeak, float musicOut)
        {
            if (musicOut < 0.01f) return echo; // music too quiet to tell
            echoRatios[echoIdx] = Math.Min(2f, voicePeak / musicOut);
            echoIdx = (echoIdx + 1) % echoRatios.Length;
            if (echoN < echoRatios.Length) echoN++;
            if (echoN < 20) return echo; // 2 s of music first
            var sorted = echoRatios.Take(echoN).OrderBy(x => x).ToArray();
            float was = echo;
            echo = sorted[echoN / 5]; // 20th percentile
            if (Math.Abs(echo - was) > 0.1f) Log.Info("volume", string.Format(CultureInfo.InvariantCulture, "music echo in voice app: {0:F2}", echo));
            return echo;
        }
        float voiceFloor = 0.01f;                    // background level of the call, learned
        readonly bool[] voiceHits = new bool[8];     // last 800 ms: above the speech threshold?
        int voiceIdx;

        /// <summary>Someone is talking: after taking out the echo of the music, sound clearly above the call's
        /// background in at least 4 of the last 8 readings (about half a second). Clicks, bumps, an open mic's
        /// hiss and your own music coming back don't count.</summary>
        bool Talking(float peak, float echoLevel)
        {
            float rest = Math.Max(0f, peak - echoLevel * 1.1f); // what is left after taking out the music's echo
            // the floor follows quiet moments down at once and creeps up slowly, so constant hiss becomes the floor
            voiceFloor = rest < voiceFloor ? rest : voiceFloor + (rest - voiceFloor) * 0.002f;
            voiceHits[voiceIdx] = rest > Math.Max(0.025f, voiceFloor * 3);
            voiceIdx = (voiceIdx + 1) % voiceHits.Length;
            return voiceHits.Count(h => h) >= 4;
        }

        void UpdateLevel(AppSession music, NowPlaying np, bool playing)
        {
            if (!playing) return; // paused: hold the current gain
            string track = np.Line;
            if (track != lastTrack)
            {
                // learn your typical level from the song that just ended (if we heard enough of it)
                if (!double.IsNaN(songDb) && songAudioSec > 20)
                {
                    refDb = double.IsNaN(refDb) ? songDb : refDb + (songDb - refDb) * 0.25;
                    S.Set("levelRef2", refDb.ToString("F2", CultureInfo.InvariantCulture));
                }
                lastTrack = track; trackAt = DateTime.Now;
                Array.Clear(hist, 0, hist.Length); histN = 0; songDb = double.NaN; songAudioSec = 0; shortDb = double.NaN;
            }

            float p = music.LivePeak();                 // measured before the app volume: the song itself
            if (p > 0.004f)                             // ignore silence between songs
            {
                double db = 20 * Math.Log10(p);
                shortDb = double.IsNaN(shortDb) ? db : shortDb + (db - shortDb) * (1 - Math.Exp(-0.1 / 1.5)); // ~1.5 s loudness
                songAudioSec += 0.1;
                int bin = Math.Max(0, Math.Min(hist.Length - 1, (int)Math.Round(-shortDb)));
                hist[bin]++; histN++;
                if (songAudioSec >= 3)
                {
                    // how loud the loud parts of this song get (90th percentile), so quiet intros/verses don't count
                    int need = histN / 10, acc = 0;
                    double p90 = double.NaN;
                    for (int b = 0; b < hist.Length; b++) { acc += hist[b]; if (acc > need) { p90 = -b; break; } }
                    // only grows within a song: the gain never pumps back up in a quiet bridge
                    if (!double.IsNaN(p90)) songDb = double.IsNaN(songDb) ? p90 : Math.Max(songDb, p90);
                }
            }

            double reference = double.IsNaN(refDb) ? songDb : refDb; // the very first song becomes the reference
            if (double.IsNaN(songDb) || double.IsNaN(reference))
            {
                // first seconds of a song: only react to a sudden blast well above your typical level
                if (!double.IsNaN(shortDb) && !double.IsNaN(refDb) && shortDb > refDb + 6)
                    levelDb = MoveTowards(levelDb, Math.Max(-MaxCut - Headroom, refDb - shortDb - Headroom), 0.3);
                return;
            }
            // typical song -> -headroom, quiet song -> up to 0 (your volume), loud song -> down to -(cut + headroom)
            double want = Math.Max(-MaxCut, Math.Min(Headroom, reference - songDb)) - Headroom;
            levelDb = MoveTowards(levelDb, want, want < levelDb ? 0.15 : 0.1); // 1.5 dB/s down, 1 dB/s up
            if ((DateTime.Now - levelLogAt).TotalSeconds > 5)
            {
                levelLogAt = DateTime.Now;
                Log.Info("volume", string.Format(CultureInfo.InvariantCulture, "level: song {0:F1} dB (now {1:F1}), typical {2:F1} dB, gain {3:F1} dB, duck {4:F2}, applied {5:F2} (your volume {6:F2})",
                    songDb, shortDb, reference, levelDb, duckFactor, lastSet, baseVol));
            }
        }
        DateTime levelLogAt = DateTime.MinValue;
        readonly int[] hist = new int[80];              // 1 dB bins of short-term loudness for the current song
        int histN; double shortDb = double.NaN, songDb = double.NaN, songAudioSec;

        static double MoveTowards(double v, double target, double maxStep)
        {
            if (Math.Abs(target - v) <= maxStep) return target;
            return v + Math.Sign(target - v) * maxStep;
        }

        void StartManaging(AppSession music)
        {
            managedKey = music.Key;
            float saved;
            // a previous run was force-closed while adjusting: its saved value is the real one
            baseVol = TryLoadBase(music.Key, out saved) ? saved : music.LiveVolume();
            duckFactor = 1f;
            levelDb = 0;
            lastSet = -1f;
            SaveBase();
            Log.Info("volume", "managing " + music.Name + " base=" + baseVol);
        }

        /// <summary>Puts the music app back to the user's volume and stops adjusting it.</summary>
        public void Release()
        {
            if (managedKey == null) return;
            var s = live.FirstOrDefault(x => x.Key == managedKey);
            if (s != null) s.SetVolume(baseVol);
            S.Set("managed." + Hash(managedKey), "");
            Log.Info("volume", "released, restored " + baseVol);
            managedKey = null; duckFactor = 1f; lastSet = -1f;
        }

        /// <summary>Kept for callers: same as <see cref="Release"/>.</summary>
        public void Restore() { Release(); }

        // ---------------------------------------------------------------- per-app volume memory

        Dictionary<string, float> seen = new Dictionary<string, float>(); // session instance -> volume last time we looked
        readonly Dictionary<string, DateTime> firstSeen = new Dictionary<string, DateTime>();
        bool primed;

        /// <summary>The app a session belongs to, as a stable settings key (exe name, or the package for store apps).</summary>
        static string AppKey(AppSession s)
        {
            if (s.System) return null;
            string path = (s.ExePath ?? "").ToLowerInvariant();
            if (path.Length == 0) return null;
            int wa = path.IndexOf("\\windowsapps\\");
            if (wa >= 0)
            {
                // store apps: the package name stays the same across updates, the folder doesn't
                string rest = path.Substring(wa + 13);
                int us = rest.IndexOf('_');
                if (us > 0) return "pkg." + rest.Substring(0, us);
            }
            return Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>
        /// Remembers the volume you give each app and puts it back when the app starts again
        /// (Windows forgets it often: new versions, device changes, games that reset it...).
        /// </summary>
        void RememberVolumes()
        {
            if (!S.Bool("rememberVol", true)) { seen.Clear(); primed = false; return; }
            var now = new Dictionary<string, float>();
            foreach (var s in live)
            {
                string app = AppKey(s);
                if (app == null || string.IsNullOrEmpty(s.InstanceId) || now.ContainsKey(s.InstanceId)) continue;
                // for the music app we drive, "its volume" is yours, not our temporary leveling/ducking value
                float v = s.Key == managedKey ? baseVol : s.LiveVolume();
                float before;
                string key = "appvol." + app;
                if (seen.TryGetValue(s.InstanceId, out before))
                {
                    if (Math.Abs(v - before) > 0.01f)
                    {
                        DateTime born;
                        float saved;
                        bool young = firstSeen.TryGetValue(s.InstanceId, out born) && (DateTime.Now - born).TotalSeconds < 15;
                        if (young && s.Key != managedKey && float.TryParse(S.Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out saved) &&
                            saved >= 0 && saved <= 1 && Math.Abs(saved - v) > 0.01f)
                        {
                            // a just-started app reset its own volume (many games do): that's not you, put yours back
                            s.SetVolume(saved);
                            Log.Info("volume", "restored " + s.Name + " to " + saved + " (it reset itself)");
                            v = saved;
                        }
                        else S.Set(key, v.ToString("F3", CultureInfo.InvariantCulture)); // you changed it
                    }
                }
                else if (primed)
                {
                    // the app (re)started: give it back its remembered volume
                    float saved;
                    if (float.TryParse(S.Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out saved) && saved >= 0 && saved <= 1 &&
                        Math.Abs(saved - v) > 0.01f && s.Key != managedKey)
                    {
                        s.SetVolume(saved);
                        Log.Info("volume", "restored " + s.Name + " to " + saved);
                        v = saved;
                    }
                    else if (S.Get(key, "") == "") S.Set(key, v.ToString("F3", CultureInfo.InvariantCulture));
                }
                else
                {
                    // first look after the widget starts: apps already running keep what they have
                    S.Set(key, v.ToString("F3", CultureInfo.InvariantCulture));
                }
                now[s.InstanceId] = v;
                if (!firstSeen.ContainsKey(s.InstanceId)) firstSeen[s.InstanceId] = primed ? DateTime.Now : DateTime.MinValue;
            }
            seen = now;
            foreach (var gone in firstSeen.Keys.Where(k => !now.ContainsKey(k)).ToList()) firstSeen.Remove(gone);
            primed = true;
        }

        public void ForgetVolumes()
        {
            foreach (var s in live) { string app = AppKey(s); if (app != null) S.Set("appvol." + app, ""); }
        }

        // ---------------------------------------------------------------- crash safety

        void SaveBase()
        {
            if (managedKey != null) S.Set("managed." + Hash(managedKey), baseVol.ToString("F4", CultureInfo.InvariantCulture));
        }

        bool TryLoadBase(string key, out float v)
        {
            return float.TryParse(S.Get("managed." + Hash(key), ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 1;
        }

        /// <summary>After a crash/forced close a session may still be lowered: put any such session back right away.</summary>
        void RecoverAfterCrash()
        {
            foreach (var s in live)
            {
                if (s.Key == managedKey) continue;
                float v;
                if (!TryLoadBase(s.Key, out v)) continue;
                s.SetVolume(v);
                S.Set("managed." + Hash(s.Key), "");
                Log.Info("volume", "recovered " + s.Name + " to " + v);
            }
        }

        static string Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }
}
