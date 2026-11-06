using System;
using System.Collections.Generic;
using UnityEngine;

namespace Pitzutz
{
    /// One music channel and a small pool of effect voices.
    public static class Sound
    {
        static AudioSource music;
        static AudioSource[] voices;
        static int next;
        static string current;
        static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        public static float MusicVolume = .7f, SfxVolume = .8f;

        /// Every sound the game ships. The build compares these with the folder and with the code, and the audio test plays each one.
        public static readonly string[] Effects = { "place", "fuse", "boom", "boom2", "brick", "kick", "power", "power_bad", "hurt", "die", "enemy_die", "exit_open", "stage_clear", "gameover", "teleport", "wind", "car", "hurry", "switch", "jet", "crumble", "secret", "boss_hit", "boss_roar", "boss_die", "boss_warn", "select", "confirm", "battle_win", "battle_lose", "shrink", "countdown" };
        public static readonly string[] Tracks = { "m_title", "m_city", "m_desert", "m_factory", "m_fortress", "m_boss", "m_battle", "m_ending" };

        /// Tells the audio test what the game asked to play.
        public static Action<string> Started;

        public static void Init(GameObject host)
        {
            // Nothing is heard without a listener, and Unity gives no error when there is none
            if (UnityEngine.Object.FindAnyObjectByType<AudioListener>() == null) host.AddComponent<AudioListener>();
            music = host.AddComponent<AudioSource>();
            music.loop = true; music.playOnAwake = false;
            voices = new AudioSource[14];
            for (int i = 0; i < voices.Length; i++) { voices[i] = host.AddComponent<AudioSource>(); voices[i].playOnAwake = false; }
        }

        static AudioClip Clip(string name)
        {
            if (!clips.TryGetValue(name, out var c)) clips[name] = c = Resources.Load<AudioClip>("Audio/" + name);
            if (c == null) Debug.LogError("Missing sound: " + name);
            return c;
        }

        public static void Play(string name, float volume = 1)
        {
            var c = Clip(name);
            if (c == null || voices == null || AudioListener.pause) return;
            var v = voices[next = (next + 1) % voices.Length];
            v.pitch = 1; v.volume = volume * SfxVolume;
            v.PlayOneShot(c);
            Started?.Invoke("sfx " + name);
        }

        public static void Music(string name, bool loop = true)
        {
            if (music == null) return;
            if (name == current && music.isPlaying) return;
            current = name;
            music.clip = name == null ? null : Clip(name);
            music.loop = loop;
            music.volume = MusicVolume * .6f;
            if (music.clip != null) { music.Play(); Started?.Invoke("music " + name); } else music.Stop();
        }

        /// The phone left the game or came back. Music picks up where it stopped.
        public static void Background(bool away) { if (AudioListener.pause != away) Debug.Log("AUDIO " + (away ? "paused" : "resumed")); AudioListener.pause = away; }

        public static void Duck(bool on) { if (music != null) music.volume = MusicVolume * (on ? .2f : .6f); }
        public static bool MusicPlaying => music != null && music.isPlaying;
        public static void ApplyVolume() { if (music != null) music.volume = MusicVolume * .6f; }
    }
}
