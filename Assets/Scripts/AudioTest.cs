using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Pitzutz
{
    /// Run by the build on the built player (-audiotest). It measures the mixed sound that reaches the listener,
    /// so "the source is playing" is not enough: a game with no listener plays every source and is still silent.
    public class AudioTest : MonoBehaviour
    {
        const float Heard = .0005f;
        App app; string dir; int fails;
        readonly StringWriter log = new StringWriter();
        readonly List<string> started = new List<string>();
        readonly float[] left = new float[1024], right = new float[1024];

        public void Begin(App a, string d) { app = a; dir = d; StartCoroutine(Tour()); }

        void Note(string s)
        {
            log.WriteLine(s); Debug.Log("AUDIO " + s);
            // A phone has no writable working folder, so there the log is the report
            if (!Application.isMobilePlatform) File.WriteAllText(Path.Combine(dir, "audio_report.txt"), log.ToString());
        }

        void Check(string what, bool ok, string detail = "") { if (!ok) fails++; Note((ok ? "PASS " : "FAIL ") + what + (detail == "" ? "" : "  (" + detail + ")")); }

        float Now()
        {
            AudioListener.GetOutputData(left, 0); AudioListener.GetOutputData(right, 1);
            float peak = 0;
            for (int i = 0; i < left.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(left[i]), Mathf.Abs(right[i]));
            return peak;
        }

        float peak;

        /// Waits until sound reaches the listener, or the time runs out.
        IEnumerator Listen(float seconds)
        {
            peak = 0;
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end && peak < Heard) { yield return null; peak = Now(); }
        }

        /// Waits until the output has gone quiet, or the time runs out.
        IEnumerator Quiet(float seconds)
        {
            peak = 1;
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end && peak >= Heard) { yield return null; peak = Now(); }
        }

        bool Asked(string what) => started.Contains(what);

        IEnumerator Tour()
        {
            Note("version " + Application.version + " on " + Application.platform);
            Sound.Started = started.Add;
            var config = AudioSettings.GetConfiguration();
            Note($"output {config.sampleRate} Hz, {config.speakerMode}, buffer {config.dspBufferSize}");

            var fresh = new Save();
            var old = JsonUtility.FromJson<Save>("{\"unlocked\":2}");
            Check("default volumes are above 0", fresh.music > 0 && fresh.sfx > 0, $"music {fresh.music}, effects {fresh.sfx}");
            Check("a save from before volumes existed keeps them above 0", old.music > 0 && old.sfx > 0);
            Check("game runs at the saved volumes", Sound.MusicVolume > 0 && Sound.SfxVolume > 0);
            var listeners = FindObjectsByType<AudioListener>(FindObjectsInactive.Include);
            Check("exactly one audio listener", listeners.Length == 1 && listeners[0].isActiveAndEnabled, listeners.Length + " found");
            Check("master volume is on", AudioListener.volume > 0 && !AudioListener.pause);

            foreach (string name in Sound.Effects) Clip(name);
            foreach (string name in Sound.Tracks) Clip(name);

            // The title screen started its music by itself
            Check("title screen asked for music", Sound.MusicPlaying);
            yield return Listen(5);
            Note("music started m_title");
            Check("title music reaches the listener", peak >= Heard, "peak " + peak.ToString("F4"));

            // Into a real stage: its music, then a real bomb dropped with the normal input path
            started.Clear();
            app.game.skipIntro = true;
            app.Enter(0);
            yield return Listen(5);
            Check("stage started its music", Asked("music " + Stages.All[0].music));
            Check("stage music reaches the listener", peak >= Heard, "peak " + peak.ToString("F4"));

            Sound.Music(null);
            yield return Quiet(3);
            Check("output is silent with nothing playing", peak < Heard, "peak " + peak.ToString("F4"));
            started.Clear();
            app.game.paused = true;
            app.game.Step(new Pad { bomb = true });
            Check("a bomb asked for its sound", Asked("sfx place"));
            yield return Listen(2);
            Note("sfx started place");
            Check("bomb sound reaches the listener", peak >= Heard, "peak " + peak.ToString("F4"));

            foreach (string name in Sound.Effects)
            {
                yield return Quiet(3);
                Sound.Play(name);
                yield return Listen(2);
                Check("effect " + name + " is heard", peak >= Heard, "peak " + peak.ToString("F4"));
            }
            foreach (string name in Sound.Tracks)
            {
                Sound.Music(null);
                yield return Quiet(3);
                Sound.Music(name);
                yield return Listen(5);
                Check("music " + name + " is heard", peak >= Heard, "peak " + peak.ToString("F4"));
            }

            // Leaving for the background silences everything; coming back brings the same music back
            Sound.Background(true);
            yield return Quiet(3);
            Check("background stops the sound", peak < Heard, "peak " + peak.ToString("F4"));
            Sound.Background(false);
            yield return Listen(5);
            Check("coming back resumes the music", peak >= Heard, "peak " + peak.ToString("F4"));

            Sound.Started = null;
            Note(fails == 0 ? "ALL PASS" : fails + " FAILED");
            Application.Quit(fails == 0 ? 0 : 4);
        }

        void Clip(string name)
        {
            var c = Resources.Load<AudioClip>("Audio/" + name);
            Check("clip " + name + " is in the player", c != null && c.length > 0, c == null ? "missing" : c.length.ToString("F2") + "s");
        }
    }
}
