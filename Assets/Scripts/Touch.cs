using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.UI;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace Pitzutz
{
    /// Joystick on one side, a big bomb button on the other. The board leaves both side strips free, so nothing covers play.
    /// A thumb anywhere on the stick's half steers, measured from the stick's centre; anywhere on the other half drops a bomb.
    public class TouchUi
    {
        public static TouchUi Current;

        /// True only when the system reports a touch screen.
        public static bool Available => Touchscreen.current != null;

        public static readonly (string name, Color color)[] Themes =
        {
            ("אדום", new Color(.92f, .27f, .27f)), ("כחול", new Color(.24f, .55f, .95f)), ("ירוק", new Color(.25f, .75f, .42f)),
            ("צהוב", new Color(.98f, .78f, .2f)), ("סגול", new Color(.65f, .4f, .92f)), ("לבן", new Color(.93f, .95f, 1f)),
        };

        const float StickSize = 300, BombSize = 280;
        static readonly Color Ink = new Color(.08f, .1f, .18f);

        class Disc
        {
            public RectTransform rect; public Image shadow, face, ring, mark; public bool held; public float size;
            public Vector2 Center => rect.position;
            public void Paint(Color theme, float alpha)
            {
                float a = held ? Mathf.Min(1, alpha + .12f) : alpha;
                float k = held ? 1.15f : 1;
                face.color = new Color(theme.r * k, theme.g * k, theme.b * k, a);
                ring.color = new Color(Ink.r, Ink.g, Ink.b, Mathf.Min(1, a + .2f));
                shadow.color = new Color(0, 0, 0, .5f * Mathf.Min(1, a + .25f));
                if (mark != null) { var m = mark.color; m.a = Mathf.Min(1, a + .25f); mark.color = m; }
            }
        }

        readonly GameObject root;
        readonly Disc stick, knob, bomb, pause;
        readonly HashSet<int> ignored = new HashSet<int>(), live = new HashSet<int>();
        readonly System.Func<float> scale;
        int polled = -1, stickFinger = -1;
        bool pausePressed, bombPressed, preview;
        Vector2? tap, finger;
        Vector2 stickValue;
        Color theme = Themes[0].color; float alpha = .85f; bool flip;

        public TouchUi(Hud hud, Transform parent, System.Func<float> canvasScale)
        {
            Current = this; scale = canvasScale;
            EnhancedTouchSupport.Enable();   // keeps a tap that begins and ends between two frames
            root = new GameObject("Touch", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var r = (RectTransform)root.transform; r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero;

            var disc = Round(d => Mathf.Clamp01((1 - d) * 128));
            var circle = Round(d => Mathf.Clamp01((1 - d) * 128) * Mathf.Clamp01((d - .92f) * 128));
            var soft = Round(d => Mathf.Pow(Mathf.Clamp01((1 - d) / .28f), 1.5f));
            RoundDisc = disc;
            stick = Make(root.transform, disc, circle, soft, null);
            knob = Make(stick.rect, disc, circle, soft, null);
            bomb = Make(root.transform, disc, circle, soft, Art.Center("btn_bomb"));
            pause = Make(root.transform, disc, circle, soft, null);
            var label = hud.Label(44, TextAnchor.MiddleCenter, new Vector2(.5f, .5f), Vector2.zero, new Vector2(100, 100), pause.rect);
            label.text = "II";
            root.SetActive(false);
        }

        public static Sprite RoundDisc { get; private set; }

        static Disc Make(Transform parent, Sprite disc, Sprite circle, Sprite soft, Sprite icon)
        {
            var o = new GameObject("Disc", typeof(RectTransform));
            o.transform.SetParent(parent, false);
            var b = new Disc { rect = (RectTransform)o.transform };
            b.shadow = Layer("Shadow", soft, new Vector2(1.22f, 1.22f), new Vector2(0, -.04f));
            b.face = Layer("Face", disc, Vector2.one, Vector2.zero);
            b.ring = Layer("Ring", circle, Vector2.one, Vector2.zero);
            if (icon != null) { b.mark = Layer("Mark", icon, new Vector2(.62f, .62f), Vector2.zero); b.mark.color = Color.white; b.mark.preserveAspect = true; }
            return b;

            Image Layer(string name, Sprite s, Vector2 size, Vector2 shift)
            {
                var m = new GameObject(name, typeof(Image)); m.transform.SetParent(o.transform, false);
                var i = m.GetComponent<Image>(); i.sprite = s; i.raycastTarget = false;
                var t = i.rectTransform; t.anchorMin = (Vector2.one - size) / 2 + shift; t.anchorMax = (Vector2.one + size) / 2 + shift; t.offsetMin = t.offsetMax = Vector2.zero;
                return i;
            }
        }

        /// Reads the saved look and position; called again on every change, for the live preview.
        public void Configure(Save s)
        {
            alpha = s.buttonAlpha; flip = s.leftHanded;
            theme = Themes[Mathf.Clamp(s.buttonColor, 0, Themes.Length - 1)].color;
            float k = s.buttonSize, gap = 28, S = StickSize * k, B = BombSize * k;
            Place(stick, flip ? 1 : 0, (flip ? -1 : 1) * (gap + S / 2), gap + S / 2 + 20, S);
            Place(bomb, flip ? 0 : 1, (flip ? 1 : -1) * (gap + B / 2), gap + B / 2 + 30, B);
            knob.rect.anchorMin = knob.rect.anchorMax = new Vector2(.5f, .5f); knob.rect.anchoredPosition = Vector2.zero;
            knob.rect.sizeDelta = new Vector2(S * .46f, S * .46f); knob.size = S * .46f;
            pause.rect.anchorMin = pause.rect.anchorMax = new Vector2(0, 1); pause.rect.anchoredPosition = new Vector2(66, -62); pause.rect.sizeDelta = new Vector2(100, 100); pause.size = 100;
            PaintAll();

            void Place(Disc b, float side, float x, float y, float size)
            {
                b.rect.anchorMin = b.rect.anchorMax = new Vector2(side, 0);
                b.rect.anchoredPosition = new Vector2(x, y); b.rect.sizeDelta = new Vector2(size, size); b.size = size;
            }
        }

        void PaintAll()
        {
            stick.Paint(theme, alpha * .75f); knob.Paint(theme, alpha); bomb.Paint(theme, alpha); pause.Paint(theme, alpha);
            bomb.rect.localScale = Vector3.one * (bomb.held ? 1.07f : 1);
        }

        public bool Visible => root.activeSelf && !preview;
        public float BombWidth => bomb.size;
        public float StickWidth => stick.size;
        public Color FaceColor => bomb.face.color;
        public Vector2 Spot(string name) => name == "bomb" ? bomb.Center : name == "stick" ? stick.Center : pause.Center;

        public void SetVisible(bool on) { if (!preview && root.activeSelf != on) root.SetActive(on); }

        /// Shows the buttons behind the settings menu, without reacting to fingers.
        public void Preview(bool on) { preview = on; root.SetActive(on); PaintAll(); }

        /// A finger that came down this frame, for menus.
        public Vector2? Tap { get { Poll(); return tap; } }
        /// Any finger still on the glass, for dragging a slider.
        public Vector2? Finger { get { Poll(); return finger; } }
        public bool PausePressed { get { Poll(); return pausePressed; } }
        public bool BombPressed { get { Poll(); return bombPressed; } }
        public Vector2 Stick { get { Poll(); return Visible ? stickValue : Vector2.zero; } }

        void Poll()
        {
            if (polled == Time.frameCount) return;
            polled = Time.frameCount;
            tap = null; finger = null; pausePressed = false; bombPressed = false;
            bool holdBomb = false, stickSeen = false;
            live.Clear();
            float px = scale();
            foreach (var t in ETouch.activeTouches)
            {
                var phase = t.phase;
                if (phase == UnityEngine.InputSystem.TouchPhase.Ended || phase == UnityEngine.InputSystem.TouchPhase.Canceled) continue;
                bool began = phase == UnityEngine.InputSystem.TouchPhase.Began;
                int id = t.touchId;
                Vector2 at = t.screenPosition;
                live.Add(id);
                finger ??= at;
                if (began)
                {
                    tap ??= at;
                    GameInput.TouchUsed();
                    if (!Visible) ignored.Add(id);   // a menu tap must not land on a button that appears under it
                    else if (Vector2.Distance(at, pause.Center) < 80 * px) { pausePressed = true; ignored.Add(id); }
                }
                if (ignored.Contains(id)) continue;
                bool stickSide = at.x < UnityEngine.Screen.width / 2f != flip;
                if (stickSide)
                {
                    if (stickFinger < 0) stickFinger = id;
                    if (stickFinger != id) continue;
                    stickSeen = true;
                    Vector2 v = (at - stick.Center) / (stick.size * .5f * px);
                    stickValue = v.sqrMagnitude > 1 ? v.normalized : v;
                }
                else
                {
                    holdBomb = true;
                    if (began) bombPressed = true;
                }
            }
            ignored.IntersectWith(live);
            if (!stickSeen) { stickFinger = -1; stickValue = Vector2.zero; }
            knob.rect.anchoredPosition = stickValue * stick.size * .27f;
            bool old = stick.held || bomb.held;
            knob.held = stick.held = stickSeen; bomb.held = holdBomb;
            if (old != (stickSeen || holdBomb) || stickSeen) PaintAll();
        }

        // Generated at runtime, so the buttons stay sharp and need no art.
        static Sprite Round(System.Func<float, float> alphaAt)
        {
            const int n = 256; var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    px[y * n + x] = new Color(1, 1, 1, alphaAt(Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(n / 2f, n / 2f)) / (n / 2f)));
            t.SetPixels32(px); t.Apply(false, true);
            return Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect);
        }
    }
}
