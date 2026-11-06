using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Pitzutz
{
    /// Screens and flow: title, map, battle setup, pause, scores, settings, credits, and the stage itself.
    public class App : MonoBehaviour
    {
        public const float CameraY = 6.12f, CameraSize = 6.75f;
        enum Screen { Title, Map, Playing, Paused, Menu }

        public Game game;
        public Hud hud;
        public Save save;
        public RunState run = new RunState();
        Screen screen;
        Camera cam;
        int mapCursor;
        readonly List<GameObject> screenObjects = new List<GameObject>();
        readonly List<Text> rows = new List<Text>();
        List<MenuItem> items; int cursor; Action backAction;

        class MenuItem
        {
            public Func<string> label; public Action ok; public Action<int> adjust;
            public Func<float> value; public Action<float> set;       // a slider
            public Color[] chips; public Func<int> chip; public Action<int> pick;   // a row of colours
            public RectTransform fill, knob; public List<Image> marks;
        }

        void Start()
        {
            cam = Camera.main;
            var ppc = cam.GetComponent<PixelPerfectCamera>();
            if (ppc != null) ppc.enabled = false;
            cam.orthographic = true; cam.orthographicSize = CameraSize; cam.transform.position = new Vector3(7, CameraY, -10);
            cam.backgroundColor = new Color(.1f, .12f, .2f);
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 1;
            if (Application.isMobilePlatform) PhoneSetup();
            string[] args = Environment.GetCommandLineArgs();
            bool Has(string a) => Array.IndexOf(args, a) >= 0;
            if (Has("-touchui") || Has("-touchtest")) InputSystem.AddDevice<Touchscreen>();
            Hud.SafeOverride = ParseRect(Arg(args, "-safearea"));

            foreach (string test in new[] { "-shots", "-uicheck", "-audiotest", "-soaktest", "-touchtest", "-inputtest", "-flowtest" }) Save.Sandbox |= Has(test);
            save = Save.Load();
            Sound.Init(gameObject);
            Sound.MusicVolume = save.music; Sound.SfxVolume = save.sfx;
            if (!save.fullscreen && !Application.isMobilePlatform) UnityEngine.Screen.SetResolution(1280, 720, false);
            Haptics.On = save.haptics;
            hud = new Hud();
            hud.Touch.Configure(save);
            game = gameObject.AddComponent<Game>();
            game.Init(this);
            mapCursor = Mathf.Min(save.unlocked, Stages.All.Length - 1);

            string dir = Arg(args, "-captureDir");
            if (Has("-shots")) { gameObject.AddComponent<ScreenTour>().Begin(this, dir); return; }
            if (Has("-uicheck")) { gameObject.AddComponent<UiCheck>().Begin(this, dir); return; }
            if (Has("-audiotest")) { ShowTitle(); gameObject.AddComponent<AudioTest>().Begin(this, dir); return; }
            if (Has("-soaktest")) { gameObject.AddComponent<SoakTest>().Begin(this, dir); return; }
            if (Has("-touchtest")) { gameObject.AddComponent<TouchTour>().Begin(this, dir); return; }
            if (Has("-flowtest")) { gameObject.AddComponent<FlowTest>().Begin(this, dir); return; }
            if (Has("-inputtest")) { gameObject.AddComponent<InputTour>().Begin(this, dir); return; }
            ShowTitle();
        }

        /// 60 fps and nothing the 2D art does not use, to save battery.
        void PhoneSetup()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            QualitySettings.antiAliasing = 0;
            cam.allowHDR = false; cam.allowMSAA = false;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp) { urp.supportsHDR = false; urp.msaaSampleCount = 1; urp.renderScale = 1; }
        }

        static Rect? ParseRect(string s)
        {
            var v = s.Split(',');
            if (v.Length != 4) return null;
            return new Rect(float.Parse(v[0]), float.Parse(v[1]), float.Parse(v[2]), float.Parse(v[3]));
        }

        // The phone sends the app to the background: stop the stage, keep progress.
        void OnApplicationPause(bool paused) { Away(paused); }
        void OnApplicationFocus(bool focus) { Away(!focus); }

        void Away(bool away)
        {
            if (!Application.isMobilePlatform) return;
            Sound.Background(away);
            if (away) Backgrounded();
        }

        /// Called by name from GameMain.java.
        public void OnSystemBack(string unused) => GameInput.SystemBack();

        public void Backgrounded()
        {
            if (save == null) return;
            save.Write();
            if (screen == Screen.Playing) ShowPause();
        }

        static string Arg(string[] a, string name)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : ".";
        }

        void Update()
        {
            if (hud == null) return;
            hud.Tick();
            bool playing = screen == Screen.Playing;
            if (screen != loggedScreen) { loggedScreen = screen; Debug.Log("SCREEN " + screen); }
            GameInput.Tick();
            bool shown = hud.Touch.Visible;
            if (shown != loggedTouch) { loggedTouch = shown; Debug.Log("TOUCHUI " + (shown ? "shown" : "hidden")); }
            hud.Touch.SetVisible(playing && TouchUi.Available && !GameInput.GamepadActive);
            if (Application.isMobilePlatform) UnityEngine.Screen.sleepTimeout = playing ? SleepTimeout.NeverSleep : SleepTimeout.SystemSetting;
            if (screen == Screen.Playing)
            {
                if (GameInput.PausePressed) ShowPause();
                return;
            }
            var m = GameInput.Menu();
            var tap = hud.Touch.Tap;
            if (screen == Screen.Map) { if (tap != null) MapTap(tap.Value); MapInput(m); return; }
            if (items == null) return;
            if (dragging && hud.Touch.Finger is Vector2 held) { Drag(held); return; }
            dragging = false;
            if (tap != null && MenuTap(tap.Value)) return;
            if (m.y != 0) { cursor = (cursor - m.y + items.Count) % items.Count; Sound.Play("select", .5f); PaintMenu(); }
            if (m.x != 0 && items[cursor].adjust != null) { items[cursor].adjust(m.x); PaintMenu(); }
            if (m.ok && items[cursor].ok != null) { Sound.Play("confirm"); items[cursor].ok(); }
            else if (m.back && backAction != null) { Sound.Play("select"); backAction(); }
        }

        Screen loggedScreen = (Screen)(-1); bool loggedTouch;

        // ---- generic menu ----

        const float TouchRowStep = 100;

        bool MenuTap(Vector2 at)
        {
            float px = hud.Scale;
            for (int i = 0; i < rows.Count; i++)
            {
                Vector2 c = rows[i].rectTransform.position;
                if (items[i].set != null || items[i].pick != null) c.x -= 290 * px;    // these rows sit to one side of the middle
                if (Mathf.Abs(at.y - c.y) > TouchRowStep / 2 * px || Mathf.Abs(at.x - c.x) > 560 * px) continue;
                cursor = i;
                var item = items[i];
                if (item.set != null) { dragging = true; Drag(at); return true; }
                if (item.pick != null)
                {
                    int chip = Mathf.RoundToInt(((at.x - c.x) / px - BarLeft) / ChipStep);
                    if (chip >= 0 && chip < item.chips.Length) { item.pick(chip); PaintMenu(); return true; }
                }
                if (item.ok == null && item.adjust != null) { item.adjust(at.x < c.x ? -1 : 1); PaintMenu(); return true; }
                PaintMenu();
                if (item.ok != null) { Sound.Play("confirm"); item.ok(); }
                return true;
            }
            return false;
        }

        const float BarLeft = -340, BarRight = 60, ChipStep = 78;
        bool dragging;

        void Drag(Vector2 at)
        {
            var item = items[cursor];
            if (item.set == null) { dragging = false; return; }
            float k = ((at.x - rows[cursor].rectTransform.position.x + 290 * hud.Scale) / hud.Scale - BarLeft) / (BarRight - BarLeft);
            item.set(Mathf.Clamp01(k));
            PaintMenu();
        }

        void ClearScreen()
        {
            hud.Touch.Preview(false); hud.SafeInFront(false); dragging = false;
            foreach (var o in screenObjects) Destroy(o);
            screenObjects.Clear(); rows.Clear(); items = null; backAction = null; cursor = 0;
        }

        void Backdrop(float dim, string art = "bg_city")
        {
            var b = hud.Picture(art, new Vector2(.5f, .5f), Vector2.zero, new Vector2(Mathf.Max(1920, hud.Width), Mathf.Max(1920, hud.Width) * 9 / 16));
            b.preserveAspect = false;
            screenObjects.Add(b.gameObject);
            b.transform.SetAsFirstSibling();
            var d = hud.Panel(new Color(.08f, .1f, .18f, dim));
            screenObjects.Add(d.gameObject);
            d.transform.SetSiblingIndex(1);
        }

        Text Line(string text, int size, float y, Color? color = null, float x = 0)
        {
            var t = hud.Label(size, TextAnchor.MiddleCenter, new Vector2(.5f, .5f), new Vector2(x, y), new Vector2(1800, size * 1.4f));
            t.text = Rtl.V(text); t.color = color ?? Color.white;
            screenObjects.Add(t.gameObject);
            return t;
        }

        void Menu(string title, List<MenuItem> list, Action back, float top = 120, float dim = .35f, bool withBackdrop = true, float? titleY = null, string art = "bg_city")
        {
            ClearScreen();
            if (withBackdrop) Backdrop(dim, art); else { var d = hud.Panel(new Color(.05f, .06f, .12f, .72f)); screenObjects.Add(d.gameObject); }
            Line(title, 92, titleY ?? top + 190);
            items = list; backAction = back; if (screen != Screen.Paused) screen = Screen.Menu;
            bool touch = TouchUi.Available;
            for (int i = 0; i < list.Count; i++)
            {
                var row = Line("", touch ? 60 : 54, top - i * (touch ? TouchRowStep : 78));
                rows.Add(row);
                if (list[i].set != null || list[i].pick != null) { row.rectTransform.anchoredPosition += new Vector2(290, 0); row.fontSize = 46; WideRow(list[i], row.rectTransform.anchoredPosition.y); }
            }
            PaintMenu();
        }

        /// Draws the slider or the colour chips beside the label.
        void WideRow(MenuItem m, float y)
        {
            Image Box(Color c, float x, Vector2 size)
            {
                var o = new GameObject("Bar", typeof(Image)); o.transform.SetParent(hud.Canvas, false);
                var i = o.GetComponent<Image>(); i.color = c; i.raycastTarget = false; i.sprite = hud.RoundSprite;
                i.rectTransform.anchorMin = i.rectTransform.anchorMax = new Vector2(.5f, .5f); i.rectTransform.anchoredPosition = new Vector2(x, y); i.rectTransform.sizeDelta = size;
                screenObjects.Add(o);
                return i;
            }
            if (m.set != null)
            {
                float w = BarRight - BarLeft;
                var back = Box(new Color(.12f, .14f, .22f, .95f), (BarLeft + BarRight) / 2, new Vector2(w + 12, 24));
                back.sprite = null;
                var fill = Box(new Color(1, .86f, .3f), BarLeft, new Vector2(w, 12)); fill.sprite = null;
                fill.rectTransform.pivot = new Vector2(0, .5f);
                m.fill = fill.rectTransform;
                m.knob = Box(Color.white, BarLeft, new Vector2(52, 52)).rectTransform;
                return;
            }
            m.marks = new List<Image>();
            for (int i = 0; i < m.chips.Length; i++)
            {
                m.marks.Add(Box(Color.white, BarLeft + i * ChipStep, new Vector2(70, 70)));
                Box(m.chips[i], BarLeft + i * ChipStep, new Vector2(56, 56));
            }
        }

        void PaintMenu()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                bool on = i == cursor;
                string s = items[i].label();
                rows[i].text = Rtl.V(on ? "< " + s + " >" : s);
                rows[i].color = on ? new Color(1, .86f, .3f) : Color.white;
                var m = items[i];
                if (m.fill != null)
                {
                    float k = m.value(), w = BarRight - BarLeft;
                    m.fill.sizeDelta = new Vector2(w * k, 12);
                    m.knob.anchoredPosition = new Vector2(BarLeft + w * k, m.knob.anchoredPosition.y);
                }
                if (m.marks != null)
                    for (int c = 0; c < m.marks.Count; c++) m.marks[c].color = c == m.chip() ? Color.white : new Color(.1f, .12f, .2f, .9f);
            }
        }

        static MenuItem Item(string label, Action ok) => new MenuItem { label = () => label, ok = ok };

        // ---- screens ----

        public void ShowTitle()
        {
            hud.Show(false); game.Unload();
            Sound.Music("m_title");
            screen = Screen.Title;
            bool started = save.unlocked > 0;
            var list = new List<MenuItem>
            {
                Item(started ? "המשך משחק" : "משחק חדש", () => { NewRun(); ShowMap(); }),
                Item("קרב", ShowBattleSetup),
                Item("שיאים", () => ShowScores(ShowTitle)),
                Item("הגדרות", () => ShowSettings(ShowTitle)),
                Item("קרדיטים", ShowCredits),
                Item("יציאה", Application.Quit),
            };
            if (Application.isMobilePlatform) list.RemoveAt(list.Count - 1);
            if (started) list.Insert(1, Item("משחק חדש", () => ConfirmNew()));
            Menu("פיצוץ גדול", list, null, 150, .05f, true, 400, "bg_title");
            foreach (var r in rows) r.rectTransform.anchoredPosition += new Vector2(260, 0);
            var logo = screenObjects[2].GetComponent<Text>();
            logo.rectTransform.anchoredPosition += new Vector2(260, 0); logo.fontSize = 150; logo.color = new Color(1, .84f, .25f);
            logo.rectTransform.sizeDelta = new Vector2(1200, 210);
            logo.GetComponent<Outline>().effectDistance = new Vector2(6, -6);
        }

        void ConfirmNew()
        {
            Menu("למחוק את ההתקדמות?", new List<MenuItem>
            {
                Item("לא, חזרה", ShowTitle),
                Item("כן, להתחיל מחדש", () => { save.unlocked = 0; mapCursor = 0; save.Write(); NewRun(); ShowMap(); }),
            }, ShowTitle, 40, .45f);
        }

        void NewRun() => run = new RunState();

        public void ShowScores(Action back)
        {
            var list = new List<MenuItem> { Item("חזרה", back) };
            Menu("שיאים", list, back, -440, .45f, true, 430);
            if (save.scores.Count == 0) Line("עוד אין שיאים. קדימה!", 48, 80);
            for (int i = 0; i < save.scores.Count; i++)
            {
                var r = save.scores[i];
                Line($"{i + 1}.  {r.score:000000}  -  {r.reached}  -  {r.date}", 40, 300 - i * 62);
            }
        }

        public void ShowSettings(Action back)
        {
            bool overGame = screen == Screen.Paused;
            var list = new List<MenuItem>
            {
                new MenuItem { label = () => "מוזיקה  " + Mathf.RoundToInt(save.music * 10), adjust = d => { save.music = Mathf.Clamp01(save.music + d * .1f); Sound.MusicVolume = save.music; Sound.ApplyVolume(); } },
                new MenuItem { label = () => "אפקטים  " + Mathf.RoundToInt(save.sfx * 10), adjust = d => { save.sfx = Mathf.Clamp01(save.sfx + d * .1f); Sound.SfxVolume = save.sfx; Sound.Play("power"); } },
                new MenuItem { label = () => "מסך מלא  " + (save.fullscreen ? "כן" : "לא"), ok = ToggleFull, adjust = d => ToggleFull() },
                Item("חזרה", () => { save.Write(); back(); }),
            };
            if (Application.isMobilePlatform) list.RemoveAt(2);
            if (TouchUi.Available) list.Insert(list.Count - 1, Item("שליטה", () => { save.Write(); ShowControls(() => ShowSettings(back)); }));
            Menu("הגדרות", list, () => { save.Write(); back(); }, TouchUi.Available ? 210 : 120, .45f, !overGame);
            var help = new Color(.85f, .9f, 1);
            if (TouchUi.Available)
            {
                Line("הקשה בצד ימין של השורה מגבירה, בצד שמאל מחלישה", 34, -250, help);
                Line("משמאל הג'ויסטיק, מימין כפתור הרימון", 34, -310, help);
                return;
            }
            Line("חצים ימינה ושמאלה משנים ערך", 34, -300, help);
            Line("זזים: חצים או WASD    רימון: Space    הפסקה: Esc", 34, -360, help);
            Line("גיימפד: סטיק או חצים, כל כפתור שם רימון, Start הפסקה", 34, -410, help);
        }

        /// Touch settings. Every change shows at once on the buttons around the menu.
        public void ShowControls(Action back)
        {
            bool overGame = screen == Screen.Paused;
            void Live() => hud.Touch.Configure(save);
            var colors = new Color[TouchUi.Themes.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = TouchUi.Themes[i].color;
            var list = new List<MenuItem>
            {
                new MenuItem { label = () => "שקיפות  " + Mathf.RoundToInt(save.buttonAlpha * 100) + "%", value = () => Mathf.InverseLerp(.3f, 1, save.buttonAlpha),
                    set = k => { save.buttonAlpha = Mathf.Lerp(.3f, 1, k); Live(); }, adjust = d => { save.buttonAlpha = Mathf.Clamp(save.buttonAlpha + d * .05f, .3f, 1); Live(); } },
                new MenuItem { label = () => "גודל  " + Mathf.RoundToInt(save.buttonSize * 100) + "%", value = () => Mathf.InverseLerp(.7f, 1.4f, save.buttonSize),
                    set = k => { save.buttonSize = Mathf.Lerp(.7f, 1.4f, k); Live(); }, adjust = d => { save.buttonSize = Mathf.Clamp(save.buttonSize + d * .05f, .7f, 1.4f); Live(); } },
                new MenuItem { label = () => "צבע  " + TouchUi.Themes[save.buttonColor].name, chips = colors, chip = () => save.buttonColor,
                    pick = c => { save.buttonColor = c; Live(); }, adjust = d => { save.buttonColor = (save.buttonColor + d + colors.Length) % colors.Length; Live(); } },
                new MenuItem { label = () => "מצב יד שמאל  " + (save.leftHanded ? "כן" : "לא"), ok = () => { save.leftHanded = !save.leftHanded; Live(); PaintMenu(); }, adjust = d => { save.leftHanded = !save.leftHanded; Live(); } },
                new MenuItem { label = () => "רטט  " + (save.haptics ? "כן" : "לא"), ok = () => { save.haptics = Haptics.On = !save.haptics; Haptics.Buzz(60, .7f); PaintMenu(); }, adjust = d => { save.haptics = Haptics.On = !save.haptics; Haptics.Buzz(60, .7f); } },
                Item("חזרה", () => { save.Write(); back(); }),
            };
            Menu("שליטה", list, () => { save.Write(); back(); }, 260, .5f, !overGame, 420);
            hud.SafeInFront(true);
            hud.Touch.Preview(true);
        }

        void ToggleFull()
        {
            save.fullscreen = !save.fullscreen;
            UnityEngine.Screen.fullScreenMode = save.fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            if (!save.fullscreen) UnityEngine.Screen.SetResolution(1280, 720, false);
            else UnityEngine.Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, true);
            PaintMenu();
        }

        public void ShowCredits()
        {
            Menu("קרדיטים", new List<MenuItem> { Item("חזרה", ShowTitle) }, ShowTitle, -420, .5f, true, 410);
            string[] text =
            {
                "גרפיקה: נוצרה בקוד במיוחד למשחק הזה",
                "צלילים ומוזיקה: נוצרו בקוד במיוחד למשחק הזה",
                "השראה לסגנון: Kenney (www.kenney.nl) - CC0",
                "גופן: Rubik - SIL OFL",
                "נבנה ב-Unity",
            };
            for (int i = 0; i < text.Length; i++) Line(text[i], 44, 230 - i * 80);
        }

        public void ShowPause()
        {
            screen = Screen.Paused; game.paused = true; Sound.Duck(true); Sound.Play("select"); hud.Flash("");
            var list = new List<MenuItem>
            {
                Item("המשך", Resume),
                Item("הגדרות", () => ShowSettings(ShowPause)),
                Item(game.InBattle ? "יציאה מהקרב" : "חזרה למפה", () => { bool b = game.InBattle; ClearScreen(); game.Unload(); Sound.Duck(false); if (b) ShowBattleSetup(); else ShowMap(); }),
            };
            if (!game.InBattle) list.Insert(1, Item("התחל את השלב מחדש", () => { ClearScreen(); screen = Screen.Playing; Sound.Duck(false); game.StartStory(game.stage, new Kit()); }));
            Menu("הפסקה", list, Resume, 120, .6f, false);
        }

        public void Resume() { ClearScreen(); screen = Screen.Playing; game.paused = false; Sound.Duck(false); }

        // ---- map: every world is a row, running right to left like the language ----

        static Vector2 Node(int i) => new Vector2(520 - (i % 5) * 245, 285 - (i / 5) * 175);

        public void ShowMap()
        {
            ClearScreen();
            hud.Show(false); screen = Screen.Map;
            Sound.Music("m_title");
            Backdrop(.3f, "bg_" + Stages.Themes[Stages.All[mapCursor].world]);
            Line("מפת המסע", 66, 455);
            for (int i = 0; i < Stages.All.Length; i++)
            {
                var d = Stages.All[i];
                if (i % 5 == 0) Line(Stages.WorldNames[d.world], 50, Node(i).y, new Color(1, .86f, .3f), 770);
                bool open = i <= save.unlocked;
                var n = hud.Picture(open ? "world_" + d.Theme : "hud_lock", new Vector2(.5f, .5f), Node(i), new Vector2(d.Boss ? 124 : 96, d.Boss ? 124 : 96));
                n.color = open ? Color.white : new Color(.8f, .8f, .85f);
                screenObjects.Add(n.gameObject);
                var t = hud.Label(30, TextAnchor.MiddleCenter, new Vector2(.5f, .5f), Node(i) + new Vector2(0, -66), new Vector2(200, 40));
                t.text = Rtl.V(d.Code); screenObjects.Add(t.gameObject);
                if (save.secrets[i])
                {
                    var star = hud.Picture("hud_star", new Vector2(.5f, .5f), Node(i) + new Vector2(50, 40), new Vector2(40, 40));
                    screenObjects.Add(star.gameObject);
                }
            }
            mapHero = hud.Picture("hero_head", new Vector2(.5f, .5f), Node(mapCursor) + new Vector2(0, 58), new Vector2(76, 76));
            screenObjects.Add(mapHero.gameObject);
            mapInfo = Line("", 46, -395);
            mapStats = Line("", 34, -445);
            Line(TouchUi.Available ? "הקשה על שלב בוחרת, הקשה נוספת משחקת, כפתור חזור לתפריט" : "חצים בוחרים, רווח משחק, Esc לתפריט", 30, -495, new Color(.9f, .95f, 1));
            PaintMap();
        }

        Image mapHero; Text mapInfo, mapStats;

        void PaintMap()
        {
            var d = Stages.All[mapCursor];
            mapHero.rectTransform.anchoredPosition = Node(mapCursor) + new Vector2(0, 58);
            mapInfo.text = Rtl.V($"{d.Code}: {d.name}");
            mapStats.text = Rtl.V($"שיא בשלב: {save.best[mapCursor]}    סודות: {save.SecretCount}/{Stages.All.Length}    ניקוד: {run.score}    חיים: {run.lives}");
        }

        void MapTap(Vector2 at)
        {
            var mid = new Vector2(UnityEngine.Screen.width, UnityEngine.Screen.height) / 2;
            int last = Mathf.Min(save.unlocked, Stages.All.Length - 1);
            for (int i = 0; i <= last; i++)
            {
                if (Vector2.Distance(at, mid + Node(i) * hud.Scale) > 90 * hud.Scale) continue;
                if (i == mapCursor) { Enter(i); return; }
                mapCursor = i; Sound.Play("select", .5f); PaintMap();
                return;
            }
        }

        void MapInput((int x, int y, bool ok, bool back) m)
        {
            // Left moves forward, because the path runs right to left
            int d = -m.x - m.y * 5;
            if (d != 0)
            {
                int to = Mathf.Clamp(mapCursor + d, 0, Mathf.Min(save.unlocked, Stages.All.Length - 1));
                if (to != mapCursor) { mapCursor = to; Sound.Play("select", .5f); PaintMap(); }
            }
            if (screen != Screen.Map) return;
            if (m.ok) Enter(mapCursor);
            else if (m.back) ShowTitle();
        }

        public string ScreenName => screen.ToString();
        public bool PausedOrMenu => screen != Screen.Playing;
        public void ShowWorldClearForTest(int w) => ShowWorldClear(w);
        public void BeginBattleForTest() => BeginBattle();
        public void BattleNextForTest() => BattleNext();
        public int MapCursor { get => mapCursor; set => mapCursor = value; }
        public void Enter(int stage) { ClearScreen(); hud.Show(true); screen = Screen.Playing; game.StartStory(stage, run.kit); }

        // ---- battle ----

        static readonly string[] Levels = { "קל", "בינוני", "קשה" };

        public void ShowBattleSetup()
        {
            hud.Show(false);
            Sound.Music("m_battle");
            void Foes(int d) { save.foes = (save.foes - 1 + d + 3) % 3 + 1; }
            void Level(int d) { save.level = (save.level + d + 3) % 3; }
            void Arena(int d) { save.arena = (save.arena + d + 4) % 4; }
            var list = new List<MenuItem>
            {
                Item("התחל קרב", BeginBattle),
                new MenuItem { label = () => "יריבים  " + save.foes, adjust = Foes, ok = () => { Foes(1); PaintMenu(); } },
                new MenuItem { label = () => "רמה  " + Levels[save.level], adjust = Level, ok = () => { Level(1); PaintMenu(); } },
                new MenuItem { label = () => "זירה  " + Stages.WorldNames[save.arena], adjust = Arena, ok = () => { Arena(1); PaintMenu(); } },
                Item("חזרה", () => { save.Write(); ShowTitle(); }),
            };
            Menu("קרב", list, () => { save.Write(); ShowTitle(); }, 190, .45f, true, 400, "bg_" + Stages.Themes[save.arena]);
            Line("מנצח שני סיבובים לוקח את הקרב. אחרי שתי דקות הזירה מתכווצת", 36, -360, new Color(.9f, .95f, 1));
            Line($"נצחונות: {save.battleWins}", 36, -420, new Color(1, .86f, .3f));
        }

        void BeginBattle()
        {
            save.Write();
            ClearScreen(); hud.Show(true); screen = Screen.Playing;
            game.StartBattle(new BattleState { foes = save.foes, level = save.level, arena = save.arena });
        }

        /// A round ended: score it and tell the player.
        public void RoundEnded(Sim s)
        {
            var b = game.battle;
            int w = s.roundWinner;
            if (w >= 0) b.wins[w]++;
            string who = w < 0 ? "תיקו!" : w == 0 ? "ניצחת בסיבוב!" : "יריב " + w + " ניצח בסיבוב";
            Sound.Play(w == 0 ? "battle_win" : "battle_lose");
            hud.Card(who, $"תוצאה: אתה {b.wins[0]}  -  יריבים {Mathf.Max(b.wins[1], Mathf.Max(b.wins[2], b.wins[3]))}");
        }

        /// The end of a stage or a round: what comes next.
        public void GameEnded(Sim s)
        {
            if (game.InBattle) { BattleNext(); return; }
            int i = game.stage;
            hud.Card("", "");
            if (s.outcome == Outcome.Cleared) StageDone(s, i);
            else Died(s, i);
        }

        void BattleNext()
        {
            var b = game.battle;
            int top = -1;
            for (int i = 0; i < 4; i++) if (b.wins[i] >= 2) top = i;
            if (top < 0) { game.StartBattle(b); return; }
            save.battleRounds++;
            if (top == 0) save.battleWins++;
            save.Write();
            game.Unload();
            hud.Show(false); screen = Screen.Menu;
            Sound.Music(null); Sound.Play(top == 0 ? "battle_win" : "battle_lose");
            var again = new BattleState { foes = b.foes, level = b.level, arena = b.arena };
            Menu(top == 0 ? "ניצחת בקרב!" : "הפסדת בקרב", new List<MenuItem>
            {
                Item("עוד קרב", BeginBattle),
                Item("חזרה", ShowBattleSetup),
            }, ShowBattleSetup, 0, .45f, true, 240, "bg_" + Stages.Themes[b.arena]);
        }

        void StageDone(Sim s, int i)
        {
            run.score += s.score;
            var h = s.Hero;
            run.kit = new Kit { bombs = h.bombsMax, flame = h.flame, speed = h.speedLv, kick = h.kick, shield = h.shield };
            save.best[i] = Mathf.Max(save.best[i], s.score);
            if (s.secretFound) save.secrets[i] = true;
            save.unlocked = Mathf.Max(save.unlocked, Mathf.Min(i + 1, Stages.All.Length - 1));
            save.Write();
            game.Unload();
            var d = Stages.All[i];
            if (d.Boss && d.world == 3) { ShowEnding(); return; }
            mapCursor = Mathf.Min(i + 1, Stages.All.Length - 1);
            if (d.Boss) { ShowWorldClear(d.world); return; }
            hud.Show(false);
            ShowMap();
        }

        static readonly string[] WorldEnd =
        {
            "העיר נקייה! בדרך למדבר, בעקבות מי ששלח את המשאית",
            "העקרב הובס. בקצה החולות עשן עולה מהמפעל",
            "המכבש נעצר. נשאר רק המבצר, ולורד הגחלים שבתוכו",
        };

        void ShowWorldClear(int world)
        {
            hud.Show(false); screen = Screen.Menu;
            Sound.Music("m_ending", false);
            Menu("עולם " + (world + 1) + " הושלם!", new List<MenuItem> { Item("המשך", ShowMap) }, ShowMap, -160, .4f, true, 220, "bg_" + Stages.Themes[world]);
            Line(WorldEnd[world], 46, 60);
        }

        public void ShowEnding()
        {
            hud.Show(false); screen = Screen.Menu;
            Sound.Music("m_ending", false);
            int rank = save.AddScore(run.score, "סיים את המשחק");
            Menu("ניצחת!", new List<MenuItem> { Item("לשיאים", () => ShowScores(ShowTitle)) }, () => ShowScores(ShowTitle), -260, .3f, true, 300, "bg_title");
            Line("לורד הגחלים הובס, ופיצי חוזר הביתה עם כובע מלא אבק", 50, 150);
            Line($"ניקוד סופי: {run.score}    סודות: {save.SecretCount}/{Stages.All.Length}", 46, 60);
            if (rank >= 0) Line($"מקום {rank + 1} בטבלת השיאים!", 46, -40, new Color(1, .86f, .3f));
            NewRun();
        }

        void Died(Sim s, int i)
        {
            run.lives--;
            run.score += s.score / 2;
            run.kit = new Kit();
            if (run.lives > 0) { game.StartStory(i, run.kit); return; }
            ShowGameOver(i);
        }

        public void ShowGameOver(int i)
        {
            game.Unload(); hud.Show(false);
            Sound.Music(null); Sound.Play("gameover");
            screen = Screen.Menu;
            int rank = save.AddScore(run.score, "הגיע לשלב " + Stages.All[i].Code);
            int score = run.score;
            Menu("המשחק נגמר", new List<MenuItem>
            {
                Item("נסה שוב את השלב", () => { NewRun(); Enter(i); }),
                Item("חזרה למפה", () => { NewRun(); ShowMap(); }),
            }, () => { NewRun(); ShowMap(); }, -120, .6f, true, 280);
            Line($"ניקוד: {score}", 56, 120);
            if (rank >= 0) Line($"מקום {rank + 1} בטבלת השיאים!", 46, 40, new Color(1, .86f, .3f));
        }
    }

    /// The canvas: in-stage stats in the side strips, plus the building blocks every screen uses.
    public class Hud
    {
        readonly Transform canvas;
        readonly GameObject stats, storyBox, battleBox;
        readonly Text stageText, nameText, timeText, scoreText, liveText, flashText, cardTitle, cardText, bossName, battleTime;
        readonly Image cardBack, bossFill; readonly GameObject bossBar;
        readonly Text[] powerText = new Text[3]; readonly Image[] powerIcon = new Image[5];
        readonly Image[] heads = new Image[4]; readonly Image[][] pips = new Image[4][];
        float flashT;
        readonly Font font;
        readonly CanvasScaler scaler;
        readonly RectTransform safe;
        Rect appliedSafe; Vector2Int appliedSize;

        /// For tests on a PC: pretend the phone reports this safe area, in pixels.
        public static Rect? SafeOverride;
        /// For the screen check: lay the canvas out as if the screen had this size, with no window.
        public static Vector2? TestScreen;
        public TouchUi Touch { get; }
        public Transform Canvas => canvas;
        public RectTransform Safe => safe;
        public Sprite RoundSprite { get; }
        Vector2 Size => TestScreen ?? new Vector2(UnityEngine.Screen.width, UnityEngine.Screen.height);
        /// Screen pixels per canvas unit.
        public float Scale => Size.x * 9 >= Size.y * 16 ? Size.y / 1080f : Size.x / 1920f;
        public float Width => Size.x / Scale;

        public Hud()
        {
            var o = new GameObject("Hud", typeof(Canvas), typeof(CanvasScaler));
            canvas = o.transform;
            o.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            scaler = o.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            font = Resources.Load<Font>("Fonts/Rubik");

            // HUD and touch buttons stay clear of the notch; menus are centered and backgrounds run edge to edge
            var so = new GameObject("Safe", typeof(RectTransform));
            so.transform.SetParent(canvas, false);
            safe = (RectTransform)so.transform;
            FitScreen();

            stats = new GameObject("Stats", typeof(RectTransform));
            stats.transform.SetParent(safe, false);
            Stretch((RectTransform)stats.transform);
            var tr = new Vector2(1, 1); var tl = new Vector2(0, 1);

            // Right strip: where and how long
            storyBox = Group("Story");
            stageText = Label(52, TextAnchor.UpperRight, tr, new Vector2(-28, -24), new Vector2(320, 70), storyBox.transform, tr);
            nameText = Label(30, TextAnchor.UpperRight, tr, new Vector2(-28, -92), new Vector2(320, 46), storyBox.transform, tr);
            var clock = Picture("hud_clock", tr, new Vector2(-60, -190), new Vector2(58, 58), stats.transform);
            timeText = Label(58, TextAnchor.UpperRight, tr, new Vector2(-104, -158), new Vector2(240, 70), stats.transform, tr);
            scoreText = Label(38, TextAnchor.UpperRight, tr, new Vector2(-28, -242), new Vector2(320, 56), storyBox.transform, tr);

            // Left strip: lives and what the hero carries
            Picture("hud_heart", tl, new Vector2(170, -60), new Vector2(60, 60), storyBox.transform);
            liveText = Label(46, TextAnchor.MiddleLeft, tl, new Vector2(208, -60), new Vector2(120, 60), storyBox.transform, new Vector2(0, .5f));
            string[] art = { "hud_bomb", "hud_flame", "hud_speed", "hud_kick", "hud_shield" };
            for (int i = 0; i < 5; i++)
            {
                float y = -158 - i * 70;
                powerIcon[i] = Picture(art[i], tl, new Vector2(66, y), new Vector2(58, 58), stats.transform);
                if (i < 3) powerText[i] = Label(40, TextAnchor.MiddleLeft, tl, new Vector2(106, y), new Vector2(100, 56), stats.transform, new Vector2(0, .5f));
            }

            // Battle: everyone's head, with a pip per won round
            battleBox = Group("Battle");
            battleTime = Label(30, TextAnchor.UpperRight, tr, new Vector2(-28, -240), new Vector2(320, 46), battleBox.transform, tr);
            string[] skins = { "hero_head", "p2_head", "p3_head", "p4_head" };
            for (int i = 0; i < 4; i++)
            {
                float y = -60 - (i / 2) * 0;
                float x = -60 - i * 78;
                heads[i] = Picture(skins[i], tr, new Vector2(x, -58), new Vector2(68, 68), battleBox.transform);
                pips[i] = new Image[2];
                for (int k = 0; k < 2; k++) pips[i][k] = Picture("hud_star", tr, new Vector2(x - 16 + k * 32, -112), new Vector2(30, 30), battleBox.transform);
            }

            bossBar = new GameObject("BossBar", typeof(RectTransform));
            bossBar.transform.SetParent(canvas, false);
            var bb = (RectTransform)bossBar.transform; bb.anchorMin = bb.anchorMax = new Vector2(.5f, 1); bb.anchoredPosition = new Vector2(0, -34); bb.sizeDelta = new Vector2(700, 30);
            var back = new GameObject("Back", typeof(Image)); back.transform.SetParent(bossBar.transform, false);
            back.GetComponent<Image>().color = new Color(.1f, .12f, .18f, .92f);
            Stretch(back.GetComponent<Image>().rectTransform);
            var fill = new GameObject("Fill", typeof(Image)); fill.transform.SetParent(bossBar.transform, false);
            bossFill = fill.GetComponent<Image>(); bossFill.color = new Color(.95f, .3f, .2f);
            Stretch(bossFill.rectTransform);
            bossName = Label(26, TextAnchor.MiddleCenter, new Vector2(.5f, .5f), Vector2.zero, new Vector2(700, 30), bossBar.transform);

            cardBack = Panel(new Color(.05f, .06f, .12f, .78f));
            cardBack.rectTransform.anchorMin = new Vector2(0, .34f); cardBack.rectTransform.anchorMax = new Vector2(1, .66f);
            cardTitle = Label(74, TextAnchor.MiddleCenter, new Vector2(.5f, .5f), new Vector2(0, 46), new Vector2(1800, 110), cardBack.transform);
            cardText = Label(38, TextAnchor.MiddleCenter, new Vector2(.5f, .5f), new Vector2(0, -60), new Vector2(1800, 60), cardBack.transform);
            cardTitle.color = new Color(1, .86f, .3f);
            cardBack.gameObject.SetActive(false);
            flashText = Label(46, TextAnchor.MiddleCenter, new Vector2(.5f, 1), new Vector2(0, -92), new Vector2(1200, 70), canvas);
            flashText.color = new Color(1, .9f, .4f);

            Touch = new TouchUi(this, safe, () => Scale);
            RoundSprite = TouchUi.RoundDisc;
            Show(false);
        }

        GameObject Group(string name)
        {
            var g = new GameObject(name, typeof(RectTransform));
            g.transform.SetParent(stats.transform, false);
            Stretch((RectTransform)g.transform);
            return g;
        }

        /// The canvas is always at least 1920x1080: wider phones gain width and every layout keeps its height.
        public void FitScreen()
        {
            var size = new Vector2Int((int)Size.x, (int)Size.y);
            var area = SafeOverride ?? (TestScreen != null ? new Rect(0, 0, size.x, size.y) : UnityEngine.Screen.safeArea);
            if (size == appliedSize && area == appliedSafe) return;
            appliedSize = size; appliedSafe = area;
            scaler.matchWidthOrHeight = size.x * 9 >= size.y * 16 ? 1 : 0;
            safe.anchorMin = new Vector2(area.xMin / size.x, area.yMin / size.y);
            safe.anchorMax = new Vector2(area.xMax / size.x, area.yMax / size.y);
            safe.offsetMin = safe.offsetMax = Vector2.zero;
        }

        static void Stretch(RectTransform r) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }

        public Image Panel(Color c)
        {
            var o = new GameObject("Panel", typeof(Image));
            o.transform.SetParent(canvas, false);
            var i = o.GetComponent<Image>(); i.color = c; i.raycastTarget = false;
            Stretch(i.rectTransform);
            return i;
        }

        public Text Label(int size, TextAnchor a, Vector2 anchor, Vector2 pos, Vector2 box, Transform parent = null, Vector2? pivot = null)
        {
            var o = new GameObject("Label", typeof(Text), typeof(Outline));
            o.transform.SetParent(parent != null ? parent : canvas, false);
            var t = o.GetComponent<Text>();
            t.font = font; t.fontSize = size; t.alignment = a; t.color = Color.white; t.fontStyle = FontStyle.Bold;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var s = o.GetComponent<Outline>(); s.effectColor = new Color(.14f, .16f, .24f, 1); s.effectDistance = new Vector2(3, -3);
            var r = t.rectTransform;
            r.anchorMin = r.anchorMax = anchor; r.pivot = pivot ?? new Vector2(.5f, .5f); r.anchoredPosition = pos; r.sizeDelta = box;
            return t;
        }

        public Image Picture(string art, Vector2 anchor, Vector2 pos, Vector2 size, Transform parent = null)
        {
            var o = new GameObject("Picture", typeof(Image));
            o.transform.SetParent(parent != null ? parent : canvas, false);
            var i = o.GetComponent<Image>(); i.sprite = Art.Center(art); i.raycastTarget = false; i.preserveAspect = true;
            var r = i.rectTransform; r.anchorMin = r.anchorMax = anchor; r.anchoredPosition = pos; r.sizeDelta = size;
            return i;
        }

        public void SafeInFront(bool on) { if (on) safe.SetAsLastSibling(); else safe.SetAsFirstSibling(); }

        public void Show(bool on)
        {
            stats.SetActive(on); bossBar.SetActive(false);
            if (!on) { Card("", ""); Flash(""); }
        }

        public void Flash(string text) { flashT = text == "" ? 0 : 2.6f; flashText.text = text == "" ? "" : Rtl.V(text); flashText.transform.SetAsLastSibling(); }
        public void Tick() { FitScreen(); if (flashT > 0 && (flashT -= Time.unscaledDeltaTime) <= 0) flashText.text = ""; }

        public void Card(string title, string text)
        {
            cardBack.gameObject.SetActive(title != "");
            cardTitle.text = title == "" ? "" : Rtl.V(title);
            cardText.text = text == "" ? "" : Rtl.V(text);
            cardBack.transform.SetAsLastSibling();
        }

        string lastClock, lastNote, lastBoss; int lastScore = -1, lastLives = -1, lastB = -1, lastF = -1, lastS = -1; int lastStage = -2;

        /// Reads the game state into the strips. Text only changes when a number does.
        public void Refresh(Sim s, RunState run, BattleState b, int stage)
        {
            var h = s.Hero;
            bool battle = b != null;
            storyBox.SetActive(!battle); battleBox.SetActive(battle);
            float left = battle ? Mathf.Max(0, Sim.ShrinkAt - s.time) : s.timeLeft;
            int sec = Mathf.CeilToInt(left);
            string clock = (sec / 60) + ":" + (sec % 60).ToString("00");
            if (clock != lastClock) { lastClock = clock; timeText.text = clock; timeText.color = sec <= 10 ? new Color(1, .4f, .3f) : Color.white; }
            if (h.bombsMax != lastB) { lastB = h.bombsMax; powerText[0].text = "x" + h.bombsMax; }
            if (h.flame != lastF) { lastF = h.flame; powerText[1].text = "x" + h.flame; }
            if (h.speedLv != lastS) { lastS = h.speedLv; powerText[2].text = "x" + (h.speedLv + 1); }
            powerIcon[3].color = h.kick ? Color.white : new Color(.2f, .22f, .3f, .55f);
            powerIcon[4].color = h.shield ? Color.white : new Color(.2f, .22f, .3f, .55f);

            if (battle)
            {
                string note = left > 0 ? "עד שהזירה מתכווצת" : "הזירה מתכווצת!";
                if (note != lastNote) { lastNote = note; battleTime.text = Rtl.V(note); }
                for (int i = 0; i < 4; i++)
                {
                    bool here = i < s.players.Count;
                    heads[i].gameObject.SetActive(here);
                    for (int k = 0; k < 2; k++)
                    {
                        pips[i][k].gameObject.SetActive(here);
                        if (here) pips[i][k].color = b.wins[i] > k ? Color.white : new Color(.15f, .17f, .25f, .8f);
                    }
                    if (here) heads[i].color = s.players[i].alive ? Color.white : new Color(.4f, .4f, .45f, .6f);
                }
                bossBar.SetActive(false);
                return;
            }

            int total = run.score + s.score;
            if (total != lastScore) { lastScore = total; scoreText.text = Rtl.V("ניקוד " + total.ToString("000000")); }
            if (run.lives != lastLives) { lastLives = run.lives; liveText.text = "x" + run.lives; }
            if (stage != lastStage)
            {
                lastStage = stage;
                stageText.text = Rtl.V(s.def.Code);
                nameText.text = Rtl.V(s.def.name);
            }
            bool boss = s.boss != null && !s.boss.dead;
            bossBar.SetActive(boss);
            if (boss)
            {
                bossFill.rectTransform.anchorMin = new Vector2(1 - s.boss.Frac, 0);
                if (lastBoss != s.def.name) { lastBoss = s.def.name; bossName.text = Rtl.V(lastBoss); }
            }
        }
    }
}
