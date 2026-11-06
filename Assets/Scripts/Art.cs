using System.Collections.Generic;
using UnityEngine;

namespace Pitzutz
{
    /// Loads single-image sprites from Resources/Art. 96 pixels = 1 cell.
    public static class Art
    {
        public const int PPU = 96;
        static Material unlit;

        public static Material Unlit => unlit != null ? unlit : unlit = Resources.Load<Material>("Materials/SpriteUnlit");

        static readonly Dictionary<string, Sprite> tiles = new Dictionary<string, Sprite>(), centers = new Dictionary<string, Sprite>(), feet = new Dictionary<string, Sprite>();

        // One table per pivot, so drawing a frame builds no strings
        static Sprite Get(string name, Vector2 pivot, Dictionary<string, Sprite> cache)
        {
            if (cache.TryGetValue(name, out var s)) return s;
            var t = Resources.Load<Texture2D>("Art/" + name);
            if (t == null) { Debug.LogError("Missing art: " + name); t = Texture2D.whiteTexture; }
            s = Sprite.Create(t, new Rect(0, 0, t.width, t.height), pivot, PPU, 0, SpriteMeshType.FullRect);
            return cache[name] = s;
        }

        /// Pivot at the bottom left: at (x - .5, y - .5) the sprite fills a cell, and anything taller rises above it.
        public static Sprite Tile(string name) => Get(name, Vector2.zero, tiles);
        public static Sprite Center(string name) => Get(name, new Vector2(.5f, .5f), centers);
        /// Pivot at the feet of an actor canvas.
        public static Sprite Feet(string name, float up = .05f) => Get(name, new Vector2(.5f, up), feet);

        public static SpriteRenderer Spawn(string name, Transform parent, Sprite s, int order)
        {
            var o = new GameObject(name);
            o.transform.SetParent(parent, false);
            var r = o.AddComponent<SpriteRenderer>();
            r.sharedMaterial = Unlit; r.sprite = s; r.sortingOrder = order;
            return r;
        }
    }

    public static class Haptics
    {
        public static bool On = true;
        public static int Count;       // for the tests
#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaClass main;
#endif
        public static void Buzz(int ms, float strength)
        {
            if (!On) return;
            Count++;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                main ??= new AndroidJavaClass("com.ibfix.pitzutzgadol.GameMain");
                main.CallStatic("buzz", ms, Mathf.RoundToInt(strength * 255));
            }
            catch (System.Exception e) { Debug.LogWarning("Vibration failed: " + e.Message); On = false; }
#endif
        }
    }
}
