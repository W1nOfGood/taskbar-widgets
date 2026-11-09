using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TaskbarWidgets.Volume
{
    /// <summary>
    /// Per-app audio for the widget:
    ///  - finds the audio session of the app that is playing music (so scrolling changes only that app, not the game),
    ///  - finds voice-chat apps (Discord, Teams, Zoom...),
    ///  - "ducking": lowers the music while someone talks in voice chat and fades it back afterwards.
    /// The session list is refreshed in the background every few seconds; volume changes go straight to the session.
    /// </summary>
    internal sealed class AppAudio
    {
        public event Action Changed;
        readonly Control ui;
        readonly Settings S;
        readonly Func<NowPlaying> current;
        List<AppSession> live = new List<AppSession>();
        bool busy;
        readonly Timer refresh = new Timer { Interval = 4000 };
        readonly Timer duckTimer = new Timer { Interval = 100 };

        // ducking state
        string duckKey;           // session currently lowered by us
        float baseVol = 1f;       // the user's volume for it (what we restore to)
        float factor = 1f;        // current multiplier (1 = not lowered)
        float lastSet = -1f;      // what we last wrote, to notice the user changing it meanwhile
        DateTime lastTalk = DateTime.MinValue;

        static readonly string[] voiceApps = {
            "discord", "discordptb", "discordcanary", "vesktop", "ms-teams", "teams", "zoom", "slack", "skype", "telegram",
            "whatsapp", "signal", "mumble", "ts3client_win64", "ts3client_win32", "teamspeak", "webex", "ciscocollabhost", "guilded", "element", "ventrilo" };

        public AppAudio(Control ui, Settings s, Func<NowPlaying> current)
        {
            this.ui = ui; S = s; this.current = current;
            refresh.Tick += (o, e) => Refresh();
            duckTimer.Tick += (o, e) => DuckTick();
        }

        public void Start() { refresh.Start(); Refresh(); }

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
                var np = current();
                var mu = Music;
                Log.Info("volume", "sessions: " + string.Join(" | ", live.Select(x => x.Name + " [" + Path.GetFileName(x.ExePath ?? "") + "]")) +
                         " || media app=" + (np == null ? "-" : np.AppId) + " -> music=" + (mu == null ? "none" : mu.Name) +
                         " || voice=" + string.Join(",", Voice.Select(x => x.Name)));
                bool needDuck = S.Bool("duck", true) && Voice.Count > 0 && mu != null || duckKey != null;
                if (needDuck && !duckTimer.Enabled) duckTimer.Start();
                if (!needDuck && duckTimer.Enabled) duckTimer.Stop();
                if (Changed != null) Changed();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

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
                return live.FirstOrDefault(s => Matches(s, np.AppId));
            }
        }

        public List<AppSession> Voice { get { return live.Where(IsVoice).ToList(); } }

        // ---------------------------------------------------------------- user volume changes

        /// <summary>The music app's volume as the user set it (ignores our temporary ducking).</summary>
        public float MusicVolume(AppSession m)
        {
            if (m == null) return 0;
            return duckKey == m.Key ? baseVol : m.LiveVolume();
        }

        public void SetMusicVolume(AppSession m, float v)
        {
            v = Math.Max(0, Math.Min(1, v));
            if (duckKey == m.Key)
            {
                baseVol = v;
                lastSet = baseVol * factor;
                m.SetVolume(lastSet);
            }
            else m.SetVolume(v);
            if (v > 0 && m.LiveMuted()) m.SetMute(false);
        }

        // ---------------------------------------------------------------- ducking

        void DuckTick()
        {
            var music = Music;
            var voice = Voice;
            var np = current();
            bool enabled = S.Bool("duck", true) && music != null && voice.Count > 0 && !IsVoice(music);
            if (!enabled || (duckKey != null && music != null && duckKey != music.Key)) { Restore(); if (!enabled) duckTimer.Stop(); return; }

            float peak = 0;
            foreach (var v in voice) peak = Math.Max(peak, v.LivePeak());
            if (peak > 0.02f && np != null && np.Playing) lastTalk = DateTime.Now;
            bool talking = (DateTime.Now - lastTalk).TotalMilliseconds < 1300;
            float amount = Math.Max(0.1f, Math.Min(0.95f, S.Int("duckAmount", 60) / 100f));
            float target = talking ? 1f - amount : 1f;
            if (duckKey == null && target >= 1f) return; // idle

            if (duckKey == null) { duckKey = music.Key; baseVol = music.LiveVolume(); factor = 1f; lastSet = -1f; }
            float cur = music.LiveVolume();
            // the user moved the app's slider (e.g. in the Windows mixer) while lowered: adopt it
            if (lastSet >= 0 && Math.Abs(cur - lastSet) > 0.02f) baseVol = Math.Min(1f, cur / Math.Max(0.05f, factor));

            factor += (target - factor) * (target < factor ? 0.35f : 0.1f);  // quick down, slow up
            if (Math.Abs(factor - target) < 0.01f) factor = target;
            lastSet = baseVol * factor;
            music.SetVolume(lastSet);
            if (factor >= 1f) { duckKey = null; lastSet = -1f; }
        }

        /// <summary>Puts a lowered app back to the user's volume (music app changed, feature turned off, app exit).</summary>
        public void Restore()
        {
            if (duckKey == null) return;
            var s = live.FirstOrDefault(x => x.Key == duckKey);
            if (s != null) s.SetVolume(baseVol);
            duckKey = null; factor = 1f; lastSet = -1f;
        }
    }
}
