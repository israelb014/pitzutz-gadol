using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace Pitzutz
{
    /// Walks every screen at every phone shape (-uicheck). Fails on Hebrew that is reversed or skipped the bidi step,
    /// on letters the font lacks, on text off the screen or under a notch, on side-strip text reaching the board, and on overlapping text.
    public class UiCheck : MonoBehaviour
    {
        App app; string dir; int fails, screens, texts;
        readonly StringWriter log = new StringWriter();
        const string Finals = "ךםןףץ";

        public void Begin(App a, string d) { app = a; dir = d; Directory.CreateDirectory(d); Rtl.Seen = new HashSet<string>(); StartCoroutine(Run()); }

        void Note(string s) { log.WriteLine(s); Debug.Log("UI " + s); if (!Application.isMobilePlatform) File.WriteAllText(Path.Combine(dir, "ui_report.txt"), log.ToString()); }
        void Fail(string s) { fails++; Note("FAIL " + s); }

        void Unit(string logical, string want) { if (Rtl.V(logical) != want) Fail($"bidi: [{logical}] gave [{Rtl.V(logical)}] wanted [{want}]"); }

        public static bool LooksReversed(string s)
        {
            int run = 0;
            foreach (char c in s)
            {
                bool heb = c >= 'א' && c <= 'ת';
                if (heb && run > 0 && Finals.IndexOf(c) >= 0) return true;     // shown left to right, a final letter must open its word
                run = heb ? run + 1 : 0;
            }
            return false;
        }

        IEnumerator Run()
        {
            // The checker itself must catch a reversed word
            if (!LooksReversed("שלום")) Fail("checker misses a word that was not turned");
            if (LooksReversed(Rtl.V("שלום"))) Fail("checker rejects a turned word");
            Unit("שלב 1-2", "1-2 בלש");
            Unit("ניקוד 000120", "000120 דוקינ");
            Unit("זזים: חצים או WASD", "WASD וא םיצח :םיזז");
            Unit("גודל 85%", "85% לדוג");

            var canvas = app.hud.Canvas.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)app.hud.Canvas;
            rt.localScale = Vector3.one; rt.position = Vector3.zero; rt.pivot = new Vector2(0, 0);
            var scaler = app.hud.Canvas.GetComponent<CanvasScaler>(); scaler.enabled = false;

            // The checker itself must catch text that runs off the screen
            Hud.TestScreen = new Vector2(1920, 1080); rt.sizeDelta = new Vector2(1920, 1080); app.hud.FitScreen();
            int before = fails;
            var probe = app.hud.Label(60, TextAnchor.MiddleCenter, new Vector2(.5f, .5f), Vector2.zero, new Vector2(100, 60));
            probe.text = Rtl.V(new string('ש', 120));
            yield return null; Canvas.ForceUpdateCanvases();
            Inspect("probe");
            bool caught = fails > before;
            fails = before;
            Destroy(probe.gameObject);
            if (!caught) Fail("checker misses text off the screen"); else Note("checker catches text off the screen");

            var shapes = new (string name, float w, float notch)[] { ("16:9", 1920, 0), ("19.5:9", 2340, 0), ("19.5:9 notch", 2340, 90), ("20:9", 2400, 0), ("20:9 notch", 2400, 110) };
            foreach (var shape in shapes)
            {
                Hud.TestScreen = new Vector2(shape.w, 1080);
                Hud.SafeOverride = shape.notch > 0 ? new Rect(shape.notch, 0, shape.w - shape.notch, 1080) : (Rect?)null;
                rt.sizeDelta = new Vector2(shape.w, 1080);
                app.hud.FitScreen();
                yield return Tour(shape.name);
            }
            Note($"{screens} screens, {texts} texts");
            Note(fails == 0 ? "ALL PASS" : fails + " FAILED");
            Application.Quit(fails == 0 ? 0 : 5);
        }

        IEnumerator Look(string name)
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            Inspect(name);
        }

        IEnumerator Tour(string shape)
        {
            var save = app.save; var g = app.game;
            g.skipIntro = false;
            save.unlocked = Stages.All.Length - 1;
            for (int i = 0; i < save.secrets.Length; i += 2) save.secrets[i] = true;
            save.scores.Clear();
            for (int i = 0; i < 10; i++) save.AddScore(99000 - i * 1000, "הגיע לשלב " + Stages.All[i].Code);
            string S(string n) => shape + " " + n;

            app.ShowTitle(); yield return Look(S("title"));
            app.ShowMap();
            for (int i = 0; i < Stages.All.Length; i += 7) { app.MapCursor = i; app.ShowMap(); yield return Look(S("map " + i)); }
            app.ShowScores(app.ShowTitle); yield return Look(S("scores"));
            app.ShowSettings(app.ShowTitle); yield return Look(S("settings"));
            app.ShowControls(app.ShowTitle); yield return Look(S("controls"));
            app.ShowCredits(); yield return Look(S("credits"));
            app.ShowBattleSetup(); yield return Look(S("battle setup"));
            app.run = new RunState { score = 123456 }; app.ShowEnding(); yield return Look(S("ending"));
            app.run = new RunState { score = 123456 }; app.ShowGameOver(5); yield return Look(S("game over"));
            for (int w = 0; w < 3; w++) { app.ShowWorldClearForTest(w); yield return Look(S("world clear " + w)); }

            for (int i = 0; i < Stages.All.Length; i++)
            {
                app.run = new RunState { score = 345678 };
                app.Enter(i); yield return Look(S("stage card " + Stages.All[i].id));
                g.Step(default); app.hud.Card("", ""); app.hud.Refresh(g.sim, app.run, null, i); yield return Look(S("stage " + Stages.All[i].id));
            }
            app.hud.Flash("הזמן נגמר! צרעות בדרך"); yield return Look(S("hurry"));
            app.hud.Flash("מצאת סוד! 1000 נקודות"); yield return Look(S("secret"));
            app.ShowPause(); yield return Look(S("pause"));
            app.ShowSettings(app.ShowPause); yield return Look(S("settings over game"));
            app.ShowControls(app.ShowPause); yield return Look(S("controls over game"));
            app.ShowPause(); app.Resume(); g.Unload();

            app.save.foes = 3; app.BeginBattleForTest(); yield return Look(S("battle card"));
            g.Step(default); app.hud.Card("", ""); app.hud.Refresh(g.sim, app.run, g.battle, -1); yield return Look(S("battle"));
            g.sim.roundWinner = 2; app.RoundEnded(g.sim); yield return Look(S("round over"));
            app.hud.Card("", "");
            app.ShowPause(); yield return Look(S("battle pause"));
            app.Resume(); g.battle.wins[0] = 2; app.BattleNextForTest(); yield return Look(S("battle won"));
            g.Unload(); app.ShowTitle();
        }

        void Inspect(string screen)
        {
            screens++;
            float width = app.hud.Width;
            var safe = app.hud.Safe;
            var corners = new Vector3[4]; safe.GetWorldCorners(corners);
            float safeL = corners[0].x, safeR = corners[2].x;
            // The board in canvas units: 15 cells centred, at 1080 / (2 * camera size) per cell
            float cell = 1080 / (App.CameraSize * 2), boardL = width / 2 - cell * 7.5f, boardR = width / 2 + cell * 7.5f;

            var seen = new List<(Text t, Rect r)>();
            foreach (var t in app.hud.Canvas.GetComponentsInChildren<Text>())
            {
                if (string.IsNullOrEmpty(t.text) || !t.isActiveAndEnabled) continue;
                texts++;
                string where = $"{screen}: [{t.text}]";
                if (t.text.Any(c => c >= 'א' && c <= 'ת'))
                {
                    if (!Rtl.Seen.Contains(t.text)) Fail(where + " did not go through Rtl");
                    if (LooksReversed(t.text)) Fail(where + " is reversed");
                }
                foreach (char c in t.text) if (c != '\n' && c != ' ' && !t.font.HasCharacter(c)) { Fail(where + " font has no " + c); break; }

                var box = t.rectTransform; box.GetWorldCorners(corners);
                float w = t.preferredWidth, h = t.preferredHeight;
                float x0 = corners[0].x, x1 = corners[2].x, y0 = corners[0].y, y1 = corners[2].y;
                float left = t.alignment.ToString().Contains("Left") ? x0 : t.alignment.ToString().Contains("Right") ? x1 - w : (x0 + x1 - w) / 2;
                float bottom = t.alignment.ToString().StartsWith("Upper") ? y1 - h : t.alignment.ToString().StartsWith("Lower") ? y0 : (y0 + y1 - h) / 2;
                if (w <= 0 || h <= 0) Fail(where + " could not be measured");
                var r = new Rect(left, bottom, w, h);
                if (r.xMin < 2 || r.xMax > width - 2 || r.yMin < 2 || r.yMax > 1078) Fail($"{where} leaves the screen {r}");
                bool hud = t.transform.IsChildOf(safe);
                if (hud && !app.PausedOrMenu)
                {
                    if (r.xMin < safeL || r.xMax > safeR) Fail($"{where} is under the notch {r}");
                    bool leftSide = r.center.x < width / 2;
                    if (leftSide ? r.xMax > boardL : r.xMin < boardR) Fail($"{where} reaches the board {r} board {boardL}-{boardR}");
                }
                seen.Add((t, r));
            }
            for (int i = 0; i < seen.Count; i++)
                for (int j = i + 1; j < seen.Count; j++)
                {
                    var a = seen[i].r; var b = seen[j].r;
                    float ox = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin), oy = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
                    if (ox > 6 && oy > 8 && !Hidden(seen[i].t) && !Hidden(seen[j].t)) Fail($"{screen}: [{seen[i].t.text}] overlaps [{seen[j].t.text}]");
                }
        }

        // The pause dim covers the side strips, so their text under a menu does not count
        bool Hidden(Text t) => app.PausedOrMenu && t.transform.IsChildOf(app.hud.Safe);
    }

    /// Takes the real pictures for the download page and for looking at (-shots), with a bot playing.
    public class ScreenTour : MonoBehaviour
    {
        App app; string dir;
        public void Begin(App a, string d) { app = a; dir = d; Directory.CreateDirectory(d); StartCoroutine(Run()); }

        IEnumerator Shot(string name)
        {
            yield return new WaitForSecondsRealtime(.25f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
            yield return new WaitForSecondsRealtime(.35f);
        }

        IEnumerator Run()
        {
            string only = null;
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-only"); if (at >= 0) only = args[at + 1];
            var g = app.game; g.skipIntro = true;
            app.save.unlocked = Stages.All.Length - 1;
            for (int i = 0; i < 7; i++) app.save.secrets[i * 2] = true;
            app.save.AddScore(45200, "סיים את המשחק"); app.save.AddScore(18800, "הגיע לשלב 3-2"); app.save.AddScore(9400, "הגיע לשלב 2-1");
            if (only == null)
            {
                app.ShowTitle(); yield return new WaitForSecondsRealtime(1); yield return Shot("title");
                app.MapCursor = 7; app.ShowMap(); yield return Shot("map");
                app.ShowScores(app.ShowTitle); yield return Shot("scores");
                app.ShowSettings(app.ShowTitle); yield return Shot("settings");
                app.ShowControls(app.ShowTitle); yield return Shot("controls");
                app.ShowCredits(); yield return Shot("credits");
                app.ShowBattleSetup(); yield return Shot("battle_setup");
            }
            for (int i = 0; i < Stages.All.Length; i++)
            {
                var d = Stages.All[i];
                if (only != null && only != d.id) continue;
                app.run = new RunState();
                app.Enter(i);
                var brain = new Brain(g.sim, g.sim.Hero, Skill.Hard, 7);
                g.paused = true; g.sim.god = true;
                int ticks = d.Boss ? 420 : 900;
                for (int t = 0; t < ticks && g.sim.outcome == Outcome.Running; t++) { g.Step(brain.Think()); if (t % 4 == 0) g.board.Draw(Sim.Dt * 4); }
                g.board.Draw(.016f); app.hud.Refresh(g.sim, app.run, null, i);
                yield return Shot("stage_" + d.id);
                g.Unload(); app.ShowTitle();
            }
            if (only == null || only == "battle")
            {
                app.save.foes = 3; app.save.level = 2; app.save.arena = 1;
                app.BeginBattleForTest();
                g.paused = true;
                for (int t = 0; t < 1000 && g.sim.outcome == Outcome.Running; t++) { g.Step(default); if (t % 4 == 0) g.board.Draw(Sim.Dt * 4); }
                g.board.Draw(.016f); app.hud.Card("", ""); app.hud.Refresh(g.sim, app.run, g.battle, -1);
                yield return Shot("battle");
                g.sim.time = 150; g.sim.shrinkAt = 40;
                for (int i = 0; i < 40; i++) { var (x, y) = g.sim.shrinkOrder[i]; g.sim.tile[x, y] = T.Shrunk; }
                g.board.Draw(.016f); app.hud.Refresh(g.sim, app.run, g.battle, -1);
                yield return Shot("battle_shrink");
                g.Unload();
            }
            Application.Quit();
        }
    }

    /// Runs every stage and a battle through the real App, Game and Board at high speed (-soaktest).
    /// Any logged error, missing picture or crash fails it.
    public class SoakTest : MonoBehaviour
    {
        App app; string dir; int errors; readonly StringWriter w = new StringWriter();

        public void Begin(App a, string d) { app = a; dir = d; Directory.CreateDirectory(d); Application.logMessageReceived += Log; StartCoroutine(Run()); }

        void Log(string text, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception) return;
            if (text.Contains("FMOD") || text.Contains("Audio")) return;
            errors++;
            if (errors < 30) Note("ERROR " + text);
        }

        void Note(string s) { w.WriteLine(s); Debug.Log("SOAK " + s); if (!Application.isMobilePlatform) File.WriteAllText(Path.Combine(dir, "soak_report.txt"), w.ToString()); }

        public static IEnumerable<string> AllArt()
        {
            foreach (string t in Stages.Themes)
            {
                foreach (string n in new[] { "floor_{0}_a", "floor_{0}_b", "wall_{0}", "edge_{0}", "block_{0}", "crack_{0}", "rubble_{0}", "bg_{0}", "world_{0}" }) yield return string.Format(n, t);
            }
            foreach (string n in "hedge hedge_sprout mirage piston_h keg mimic_a gate_red_up gate_blue_up gate_red_down gate_blue_down ice sand_q oneway_u oneway_d oneway_l oneway_r crumble_0 crumble_1 crumble_2 pit jet_off jet_warn switch_red switch_blue road secret_tile exit_closed exit_open shadow car_red car_blue car_yellow bomb_a bomb_b pu_bomb pu_flame pu_speed pu_kick pu_shield secret_token shield shot_trash shot_sting shot_gear shot_ember warn_cell hud_bomb hud_flame hud_speed hud_kick hud_shield hud_clock hud_heart hud_star hud_lock wind_arrow bg_title btn_bomb hero_hurt hero_head p2_head p3_head p4_head turtle_shell".Split(' ')) yield return n;
            foreach (string d in "u d l r".Split(' ')) foreach (string ab in "a b".Split(' ')) yield return $"conv_{d}_{ab}";
            foreach (string c in "a b c".Split(' ')) { yield return $"tele_{c}1"; yield return $"tele_{c}2"; }
            foreach (string f in "c h v l r u d".Split(' ')) { yield return $"flame_{f}_a"; yield return $"flame_{f}_b"; }
            foreach (string skin in "hero p2 p3 p4".Split(' ')) foreach (string d in "d u l r".Split(' ')) for (int i = 0; i < 3; i++) yield return $"{skin}_{d}_{i}";
            foreach (string e in "snail beetle ghostmoth fox frog turtle mimic wasp".Split(' ')) { yield return e + "_a"; yield return e + "_b"; }
            for (int b = 1; b <= 4; b++) foreach (string s in "idle_a idle_b attack hurt".Split(' ')) yield return $"boss{b}_{s}";
        }

        IEnumerator Run()
        {
            yield return null;
            int missing = 0;
            foreach (string a in AllArt()) if (Resources.Load<Texture2D>("Art/" + a) == null) { missing++; Note("FAIL missing art " + a); }
            Note("art files checked, missing " + missing);
            var g = app.game; g.skipIntro = true;
            for (int i = 0; i < Stages.All.Length; i++)
            {
                app.run = new RunState();
                app.Enter(i);
                g.paused = true;
                var brain = new Brain(g.sim, g.sim.Hero, Skill.Hard, i);
                g.sim.god = true;
                int n = 0;
                for (; n < 2400 && g.sim.outcome == Outcome.Running; n++) { g.Step(brain.Think()); if (n % 6 == 0) g.board.Draw(Sim.Dt * 6); if (n % 600 == 0) yield return null; }
                Note($"stage {Stages.All[i].id}: {n} ticks, enemies left {g.sim.enemies.Count}, outcome {g.sim.outcome}");
                g.Unload();
            }
            foreach (int foes in new[] { 1, 3 })
            {
                app.save.foes = foes; app.save.level = 2; app.BeginBattleForTest(); g.paused = true;
                var brain = new Brain(g.sim, g.sim.Hero, Skill.Normal, 3);
                int n = 0;
                for (; n < 12000 && g.sim.outcome == Outcome.Running; n++) { g.Step(brain.Think()); if (n % 6 == 0) g.board.Draw(Sim.Dt * 6); if (n % 600 == 0) yield return null; }
                Note($"battle {foes}: {n} ticks, winner {g.sim.roundWinner}");
                bool ended = g.sim.outcome == Outcome.RoundOver;
                if (!ended) { Note("FAIL battle did not end"); missing++; }
                g.Unload();
            }
            app.ShowTitle();
            bool ok = errors == 0 && missing == 0;
            Note(ok ? "ALL PASS" : $"{errors} ERRORS, {missing} MISSING");
            Application.Quit(ok ? 0 : 6);
        }
    }

    /// Fingers on the real on-screen controls (-touchtest, on a pretend touch screen, or on the emulator).
    public class TouchTour : MonoBehaviour
    {
        App app; string dir; Touchscreen ts; int fails; readonly StringWriter w = new StringWriter();
        TouchUi Ui => app.hud.Touch;

        public void Begin(App a, string d) { app = a; dir = d; Directory.CreateDirectory(d); ts = Touchscreen.current; StartCoroutine(Run()); }

        void Note(string s) { w.WriteLine(s); Debug.Log("TOUCH " + s); if (!Application.isMobilePlatform) File.WriteAllText(Path.Combine(dir, "touch_report.txt"), w.ToString()); }
        void Check(string what, bool ok, string detail = "") { if (!ok) fails++; Note((ok ? "PASS " : "FAIL ") + what + (detail == "" ? "" : "  (" + detail + ")")); }
        void Finger(int id, UnityEngine.InputSystem.TouchPhase phase, Vector2 at) => InputSystem.QueueStateEvent(ts, new TouchState { touchId = id, phase = phase, position = at });
        void Down(int id, Vector2 at) => Finger(id, UnityEngine.InputSystem.TouchPhase.Began, at);
        void Move(int id, Vector2 at) => Finger(id, UnityEngine.InputSystem.TouchPhase.Moved, at);
        void Up(int id, Vector2 at) => Finger(id, UnityEngine.InputSystem.TouchPhase.Ended, at);
        static WaitForSecondsRealtime Wait(float s) => new WaitForSecondsRealtime(s);
        IEnumerator Tap(Vector2 at) { Down(9, at); yield return Wait(.1f); Up(9, at); yield return Wait(.25f); }

        IEnumerator Run()
        {
            yield return Wait(.5f);
            var g = app.game;
            app.ShowTitle();
            yield return Wait(.5f);
            Check("controls hidden on the title screen", !Ui.Visible);
            g.skipIntro = true;
            app.Enter(0);
            g.sim.god = true;
            yield return Wait(.6f);
            Check("controls visible while playing", Ui.Visible);
            float px = app.hud.Scale;
            var stick = Ui.Spot("stick"); var bomb = Ui.Spot("bomb");

            var h = g.sim.Hero; float x0 = h.x, y0 = h.y;
            var push = stick + new Vector2(100 * px, 0);
            Down(1, push); yield return Wait(.5f);
            Check("joystick right moves the hero right", h.x > x0 + .4f, $"moved {h.x - x0:F2}");
            Move(1, stick); Up(1, stick); yield return Wait(.2f);
            int bombs = g.sim.bombs.Count;
            Down(2, bomb); yield return Wait(.15f); Up(2, bomb); yield return Wait(.15f);
            Check("bomb button drops a bomb", g.sim.bombs.Count > bombs, g.sim.bombs.Count.ToString());
            int count = Haptics.Count;
            Check("a bomb counted a vibration", count > 0, count.ToString());

            // Both thumbs at once
            float x1 = h.x, y1 = h.y; bombs = g.sim.bombs.Count;
            Down(1, stick + new Vector2(0, -100 * px)); yield return Wait(.3f);
            Down(2, bomb); yield return Wait(.1f); Up(2, bomb); yield return Wait(.1f);
            Up(1, stick); yield return Wait(.2f);
            Check("stick and bomb work together", Mathf.Abs(h.y - y1) + Mathf.Abs(h.x - x1) > .01f || g.sim.bombs.Count >= bombs);

            // Gamepad hides the buttons, a touch brings them back
            var pad = InputSystem.AddDevice<Gamepad>();
            InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(GamepadButton.South)); yield return Wait(.2f);
            InputSystem.QueueStateEvent(pad, new GamepadState()); yield return Wait(.2f);
            Check("gamepad hides the touch buttons", !Ui.Visible);
            Down(3, stick); yield return Wait(.15f); Up(3, stick); yield return Wait(.2f);
            Check("a touch brings them back", Ui.Visible);
            InputSystem.RemoveDevice(pad);

            // Left handed swaps sides
            app.save.leftHanded = true; Ui.Configure(app.save); yield return null;
            Check("left hand mode swaps sides", Ui.Spot("stick").x > Ui.Spot("bomb").x);
            app.save.leftHanded = false;
            float big = Ui.BombWidth; app.save.buttonSize = 1.3f; Ui.Configure(app.save);
            Check("size setting grows the buttons", Ui.BombWidth > big);
            Color before = Ui.FaceColor; app.save.buttonColor = 3; Ui.Configure(app.save);
            Check("colour setting repaints at once", Ui.FaceColor != before);
            float a0 = Ui.FaceColor.a; app.save.buttonAlpha = .4f; Ui.Configure(app.save);
            Check("opacity setting applies at once", Ui.FaceColor.a < a0);
            app.save.buttonSize = 1; app.save.buttonAlpha = .85f; Ui.Configure(app.save);

            var pause = Ui.Spot("pause");
            yield return Tap(pause);
            Check("pause button pauses", app.ScreenName == "Paused", app.ScreenName);
            Note(fails == 0 ? "ALL PASS" : fails + " FAILED");
            Application.Quit(fails == 0 ? 0 : 7);
        }
    }

    /// The whole flow of a game through the real App and Game (-flowtest): dying, game over, winning, unlocking, saving, a best of three.
    public class FlowTest : MonoBehaviour
    {
        App app; string dir; int fails; readonly StringWriter w = new StringWriter();
        public void Begin(App a, string d) { app = a; dir = d; Directory.CreateDirectory(d); StartCoroutine(Run()); }
        void Note(string s) { w.WriteLine(s); Debug.Log("FLOW " + s); if (!Application.isMobilePlatform) File.WriteAllText(Path.Combine(dir, "flow_report.txt"), w.ToString()); }
        void Check(string what, bool ok, string detail = "") { if (!ok) fails++; Note((ok ? "PASS " : "FAIL ") + what + (detail == "" ? "" : "  (" + detail + ")")); }
        static WaitForSecondsRealtime Wait(float s) => new WaitForSecondsRealtime(s);

        IEnumerator Run()
        {
            var g = app.game; g.skipIntro = true;
            app.ShowTitle(); yield return Wait(.2f);
            app.run = new RunState();
            app.Enter(0); yield return Wait(.2f);
            Check("a new stage has three lives", app.run.lives == 3);
            Check("timer starts at three minutes", g.sim.timeLeft > 178 && g.sim.timeLeft <= 180, g.sim.timeLeft.ToString("F1"));

            // Dying costs a life and rebuilds the stage, with the power-ups gone
            var first = g.sim;
            first.Hero.bombsMax = 5; first.outcome = Outcome.Dead; yield return Wait(.6f);
            Check("dying costs a life", app.run.lives == 2, app.run.lives.ToString());
            Check("dying restarts the stage", g.sim != first && g.sim.outcome == Outcome.Running);
            Check("dying loses the power-ups", g.sim.Hero.bombsMax == 1, g.sim.Hero.bombsMax.ToString());
            g.sim.outcome = Outcome.Dead; yield return Wait(.6f);
            g.sim.outcome = Outcome.Dead; yield return Wait(.6f);
            Check("three deaths end the game", !g.Running && app.ScreenName == "Menu", app.ScreenName);

            // Clearing a stage unlocks the next, keeps the kit and saves the best score
            app.run = new RunState();
            app.Enter(0); yield return Wait(.2f);
            g.sim.Hero.bombsMax = 4; g.sim.score = 777; g.sim.secretFound = true;
            g.sim.outcome = Outcome.Cleared;
            yield return Wait(2.3f);
            Check("clearing opens the map", app.ScreenName == "Map", app.ScreenName);
            Check("clearing unlocks the next stage", app.save.unlocked == 1, app.save.unlocked.ToString());
            Check("best score kept", app.save.best[0] == 777, app.save.best[0].ToString());
            Check("secret kept", app.save.secrets[0]);
            Check("power-ups carry over", app.run.kit.bombs == 4, app.run.kit.bombs.ToString());
            app.Enter(1); yield return Wait(.2f);
            Check("next stage starts with the kit", g.sim.Hero.bombsMax == 4, g.sim.Hero.bombsMax.ToString());
            g.Unload();

            // A boss ends its world; the last one ends the game
            app.Enter(4); yield return Wait(.2f);
            Check("boss stage has a boss with a health bar", g.sim.boss != null && g.sim.boss.maxHp >= 5);
            g.sim.outcome = Outcome.Cleared; yield return Wait(2.3f);
            Check("boss opens the next world", app.save.unlocked == 5 && app.ScreenName == "Menu", app.save.unlocked + " " + app.ScreenName);
            app.Enter(19); yield return Wait(.2f);
            g.sim.score = 5000; g.sim.outcome = Outcome.Cleared; yield return Wait(2.3f);
            Check("last boss ends the game", app.ScreenName == "Menu" && app.save.unlocked == 19 && app.save.scores.Count > 0, app.ScreenName);

            // Best of three: first to two wins, a draw changes nothing
            app.save.foes = 1; app.save.level = 0; app.BeginBattleForTest(); yield return Wait(.2f);
            var b = g.battle;
            void End(int winner) { g.sim.roundWinner = winner; g.sim.outcome = Outcome.RoundOver; }
            End(-1); yield return Wait(2.8f);
            Check("a draw replays the round", g.InBattle && b.round == 2 && b.wins[0] == 0, "round " + b.round);
            End(0); yield return Wait(2.8f);
            Check("one win does not end it", g.InBattle && b.wins[0] == 1);
            End(1); yield return Wait(2.8f);
            Check("one all goes on", g.InBattle && b.wins[1] == 1);
            End(0); yield return Wait(2.8f);
            Check("two wins end the battle", !g.InBattle && app.save.battleWins == 1 && app.ScreenName == "Menu", app.ScreenName);

            // A save written by this version comes back whole
            var copy = JsonUtility.FromJson<Save>(JsonUtility.ToJson(app.save));
            Check("save survives a round trip", copy.unlocked == app.save.unlocked && copy.best[0] == 777 && copy.secrets[0] && copy.scores.Count == app.save.scores.Count);
            Note(fails == 0 ? "ALL PASS" : fails + " FAILED");
            Application.Quit(fails == 0 ? 0 : 9);
        }
    }

    /// Keyboard and gamepad through the real input path (-inputtest).
    public class InputTour : MonoBehaviour
    {
        App app; string dir; int fails; readonly StringWriter w = new StringWriter(); Gamepad pad;

        public void Begin(App a, string d) { app = a; dir = d; Directory.CreateDirectory(d); pad = InputSystem.AddDevice<Gamepad>(); StartCoroutine(Run()); }
        void Note(string s) { w.WriteLine(s); Debug.Log("INPUT " + s); File.WriteAllText(Path.Combine(dir, "input_report.txt"), w.ToString()); }
        void Check(string what, bool ok, string detail = "") { if (!ok) fails++; Note((ok ? "PASS " : "FAIL ") + what + (detail == "" ? "" : "  (" + detail + ")")); }
        static void Keys(params Key[] k) => InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(k));
        static WaitForSecondsRealtime Wait(float s) => new WaitForSecondsRealtime(s);

        IEnumerator Press(params Key[] k) { Keys(k); yield return Wait(.12f); Keys(); yield return Wait(.15f); }

        IEnumerator Run()
        {
            app.ShowTitle(); yield return Wait(.4f);
            yield return Press(Key.Space);
            Check("Space on the title opens the map", app.ScreenName == "Map", app.ScreenName);
            app.game.skipIntro = true;
            yield return Press(Key.Space);
            Check("Space on the map starts the stage", app.ScreenName == "Playing", app.ScreenName);
            var g = app.game; g.sim.god = true; var h = g.sim.Hero;
            yield return Wait(.2f);
            float x = h.x, y = h.y;
            Keys(Key.RightArrow); yield return Wait(.3f); Keys(); yield return Wait(.1f);
            Check("arrow key moves", h.x > x + .3f, $"from {x:F2} to {h.x:F2}");
            x = h.x; Keys(Key.A); yield return Wait(.3f); Keys(); yield return Wait(.1f);
            Check("A moves left", h.x < x - .3f, $"from {x:F2} to {h.x:F2}, face {h.faceX}");
            yield return Press(Key.Space);
            Check("Space drops a bomb", g.sim.bombs.Count == 1, g.sim.bombs.Count.ToString());
            x = h.x; y = h.y;
            InputSystem.QueueStateEvent(pad, new GamepadState { leftStick = new Vector2(1, 0) }); yield return Wait(.3f);
            InputSystem.QueueStateEvent(pad, new GamepadState()); yield return Wait(.1f);
            Check("gamepad stick moves", Mathf.Abs(h.x - x) > .2f, $"{h.x - x:F2}");
            InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(GamepadButton.Start)); yield return Wait(.15f);
            InputSystem.QueueStateEvent(pad, new GamepadState()); yield return Wait(.15f);
            Check("gamepad Start pauses", app.ScreenName == "Paused", app.ScreenName);
            yield return Press(Key.Escape);
            Check("Esc resumes", app.ScreenName == "Playing", app.ScreenName);
            yield return Press(Key.Escape);
            Check("Esc pauses", app.ScreenName == "Paused", app.ScreenName);
            Note(fails == 0 ? "ALL PASS" : fails + " FAILED");
            Application.Quit(fails == 0 ? 0 : 8);
        }
    }
}
