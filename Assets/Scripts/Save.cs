using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Pitzutz
{
    [Serializable]
    public class ScoreRow { public int score; public string date; public string reached; }

    /// Progress, best scores and settings, in one JSON file. Written after every stage and every change.
    [Serializable]
    public class Save
    {
        public int unlocked;                 // index of the furthest stage that can be played
        public int[] best = new int[Stages.All.Length];
        public bool[] secrets = new bool[Stages.All.Length];
        public List<ScoreRow> scores = new List<ScoreRow>();
        public float music = .7f, sfx = .8f;
        public bool fullscreen = true;
        public int battleWins, battleRounds;
        public int foes = 1, level = 1, arena;

        // Touch controls
        public float buttonAlpha = .85f, buttonSize = 1;
        public bool leftHanded, haptics = true;
        public int buttonColor;

        /// Tests must never touch the player's real progress.
        public static bool Sandbox;

        static string FilePath => Path.Combine(Application.persistentDataPath, "save.json");

        public int SecretCount { get { int n = 0; foreach (bool b in secrets) if (b) n++; return n; } }

        public static Save Load()
        {
            if (Sandbox) return new Save();
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonUtility.FromJson<Save>(File.ReadAllText(FilePath));
                    int n = Stages.All.Length;
                    if (s.best == null || s.best.Length != n) s.best = new int[n];
                    if (s.secrets == null || s.secrets.Length != n) s.secrets = new bool[n];
                    return s;
                }
            }
            catch (Exception e) { Debug.LogWarning("Save unreadable, starting fresh: " + e.Message); }
            return new Save();
        }

        public void Write()
        {
            if (Sandbox) return;
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(this));
                File.Copy(tmp, FilePath, true); File.Delete(tmp);
            }
            catch (Exception e) { Debug.LogWarning("Could not save: " + e.Message); }
        }

        /// Returns the rank (0 based) if the score made the top ten, otherwise -1.
        public int AddScore(int score, string reached)
        {
            if (score <= 0) return -1;
            var row = new ScoreRow { score = score, date = DateTime.Now.ToString("dd.MM.yy"), reached = reached };
            int at = scores.FindIndex(r => r.score < score);
            if (at < 0) at = scores.Count;
            if (at >= 10) return -1;
            scores.Insert(at, row);
            if (scores.Count > 10) scores.RemoveAt(10);
            Write();
            return at;
        }
    }
}
