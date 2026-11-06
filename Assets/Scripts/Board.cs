using System.Collections.Generic;
using UnityEngine;

namespace Pitzutz
{
    /// Draws a Sim. Reads it every frame and never changes it, so what is on screen is always the rules' own truth.
    public class Board
    {
        const int W = Sim.W, H = Sim.H;
        readonly Transform root;
        readonly SpriteRenderer[,] over = new SpriteRenderer[W, H], solid = new SpriteRenderer[W, H], items = new SpriteRenderer[W, H], fire = new SpriteRenderer[W, H];
        readonly SpriteRenderer back, exitDoor, bossBody, bossShadow, windArrow;
        readonly Pool bombPool, shotPool, warnPool, carPool, moverPool, foePool, heroPool, shadowPool, shieldPool, puffPool;
        readonly List<Puff> puffs = new List<Puff>();
        readonly Dark dark;
        Sim s; string theme;
        float shake, flash;
        public float Clock;

        class Puff { public SpriteRenderer r; public float t, life, vx, vy, spin, grow; }

        static int Order(float y, int layer) => (int)((H - y) * 20) + layer;

        class Pool
        {
            readonly List<SpriteRenderer> all = new List<SpriteRenderer>(); readonly Transform parent; int used;
            public Pool(Transform p) { parent = p; }
            public SpriteRenderer Next()
            {
                if (used == all.Count) all.Add(Art.Spawn("Pooled", parent, null, 0));
                var r = all[used++]; r.gameObject.SetActive(true); r.color = Color.white; r.flipX = false; r.transform.localScale = Vector3.one; r.transform.localRotation = Quaternion.identity;
                return r;
            }
            public void Reset() => used = 0;
            public void Hide() { for (int i = used; i < all.Count; i++) if (all[i].gameObject.activeSelf) all[i].gameObject.SetActive(false); }
        }

        public Board()
        {
            root = new GameObject("Board").transform;
            back = Art.Spawn("Back", root, null, -100);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    over[x, y] = Art.Spawn("Over", root, null, 2); over[x, y].transform.position = new Vector3(x - .5f, y - .5f);
                    solid[x, y] = Art.Spawn("Solid", root, null, Order(y, 8)); solid[x, y].transform.position = new Vector3(x - .5f, y - .5f);
                    items[x, y] = Art.Spawn("Item", root, null, Order(y, 3)); items[x, y].transform.position = new Vector3(x, y);
                    fire[x, y] = Art.Spawn("Fire", root, null, Order(y, 12)); fire[x, y].transform.position = new Vector3(x, y);
                }
            exitDoor = Art.Spawn("Exit", root, null, 3);
            bossBody = Art.Spawn("Boss", root, null, 0);
            bossShadow = Art.Spawn("BossShadow", root, Art.Center("shadow"), 5);
            windArrow = Art.Spawn("Wind", root, Art.Center("wind_arrow"), 900);
            bombPool = new Pool(root); shotPool = new Pool(root); warnPool = new Pool(root); carPool = new Pool(root); moverPool = new Pool(root);
            foePool = new Pool(root); heroPool = new Pool(root); shadowPool = new Pool(root); shieldPool = new Pool(root); puffPool = new Pool(root);
            dark = new Dark(root);
            floorsRoot = new GameObject("Floors").transform; floorsRoot.SetParent(root);
            root.gameObject.SetActive(false);
        }

        string floorA, floorB, wallArt, edgeArt, blockArt, crackArt;
        readonly T[,] seenTile = new T[W, H]; readonly Item[,] seenItem = new Item[W, H];
        static readonly string[,] Belt = { { "conv_u_a", "conv_u_b" }, { "conv_d_a", "conv_d_b" }, { "conv_l_a", "conv_l_b" }, { "conv_r_a", "conv_r_b" } };
        static readonly string[,] Portal = { { "tele_a1", "tele_a2" }, { "tele_b1", "tele_b2" }, { "tele_c1", "tele_c2" } };
        static readonly string[] Cracks = { "crumble_0", "crumble_1", "crumble_2" };

        readonly Transform floorsRoot;
        readonly List<GameObject> floors = new List<GameObject>();

        public void Load(Sim sim)
        {
            s = sim; theme = Stages.Themes[sim.def.world];
            floorA = "floor_" + theme + "_a"; floorB = "floor_" + theme + "_b"; wallArt = "wall_" + theme; edgeArt = "edge_" + theme; blockArt = "block_" + theme; crackArt = "crack_" + theme;
            System.Array.Clear(seenTile, 0, seenTile.Length); System.Array.Clear(seenItem, 0, seenItem.Length);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { seenTile[x, y] = (T)255; seenItem[x, y] = (Item)255; }
            root.gameObject.SetActive(true);
            foreach (var o in floors) Object.Destroy(o);
            floors.Clear();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    bool even = (x + y) % 2 == 0;     // one order per picture, so the whole floor is two draw calls
                    var f = Art.Spawn("Floor", floorsRoot, Art.Tile(even ? floorA : floorB), even ? 0 : 1);
                    f.transform.position = new Vector3(x - .5f, y - .5f);
                    floors.Add(f.gameObject);
                }
            var cam = UnityEngine.Camera.main;
            back.sprite = Art.Center("bg_" + theme);
            back.transform.position = new Vector3(7, 6, 5);
            float high = cam.orthographicSize * 2, wide = high * cam.aspect;
            float k = Mathf.Max(wide / back.sprite.bounds.size.x, high / back.sprite.bounds.size.y);
            back.transform.localScale = new Vector3(k, k, 1);
            puffs.Clear(); shake = 0; flash = 0;
            dark.Show(sim.def.dark);
            Draw(0);
        }

        public void Hide() { root.gameObject.SetActive(false); s = null; }

        string Solid(T t, int x, int y) => t switch
        {
            T.Edge => edgeArt, T.Wall => wallArt, T.Shrunk => wallArt, T.Block => blockArt, T.Burning => null,
            T.Hedge => "hedge", T.Sprout => "hedge_sprout", T.Mirage => "mirage", T.Crack => crackArt, T.Keg => "keg",
            T.GateRed => "gate_red_up", T.GateBlue => "gate_blue_up", _ => null,
        };

        string Over(int x, int y, int beat)
        {
            int k = beat % 2;
            switch (s.floor[x, y])
            {
                case F.ConvU: return Belt[0, k];
                case F.ConvD: return Belt[1, k];
                case F.ConvL: return Belt[2, k];
                case F.ConvR: return Belt[3, k];
                case F.Ice: return "ice";
                case F.Quick: return "sand_q";
                case F.Tele: return Portal[Mathf.Min(y * W + x, s.tele[x, y]) % 3, k];
                case F.OneU: return "oneway_u";
                case F.OneD: return "oneway_d";
                case F.OneL: return "oneway_l";
                case F.OneR: return "oneway_r";
                case F.Crumble: return Cracks[Mathf.Clamp(s.crumble[x, y], 0, 2)];
                case F.Pit: return "pit";
                case F.Jet:
                    foreach (var j in s.jets) if (j.x == x && j.y == y) return s.JetWarning(j) ? "jet_warn" : "jet_off";
                    return "jet_off";
                case F.Switch: return s.gateRedUp ? "switch_red" : "switch_blue";
                case F.Road: return "road";
                case F.SecretTile: return "secret_tile";
            }
            if (s.gateCells[x, y] == 1 && s.tile[x, y] == T.Empty) return "gate_red_down";
            if (s.gateCells[x, y] == 2 && s.tile[x, y] == T.Empty) return "gate_blue_down";
            return null;
        }

        static string ItemArt(Item i) => i switch
        {
            Item.Bomb => "pu_bomb", Item.Flame => "pu_flame", Item.Speed => "pu_speed", Item.Kick => "pu_kick", Item.Shield => "pu_shield", Item.Secret => "secret_token", _ => null,
        };

        static string FlameArt(byte m, string ab)
        {
            if ((m & Sim.FC) != 0 || (m & Sim.FH) != 0 && (m & Sim.FV) != 0) return "flame_c_" + ab;
            if ((m & Sim.TipL) != 0) return "flame_l_" + ab;
            if ((m & Sim.TipR) != 0) return "flame_r_" + ab;
            if ((m & Sim.TipU) != 0) return "flame_u_" + ab;
            if ((m & Sim.TipD) != 0) return "flame_d_" + ab;
            return (m & Sim.FV) != 0 ? "flame_v_" + ab : "flame_h_" + ab;
        }

        static void Show(SpriteRenderer r, string art, System.Func<string, Sprite> how)
        {
            if (art == null) { if (r.sprite != null) r.sprite = null; return; }
            var sp = how(art);
            if (r.sprite != sp) r.sprite = sp;
        }

        /// lag: how far between two ticks this frame is, for screens faster than the 60 ticks.
        public void Draw(float dt)
        {
            if (s == null) return;
            Clock += dt;
            int beat = (int)(Clock * 6);
            string ab = beat % 2 == 0 ? "a" : "b";

            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    Show(over[x, y], Over(x, y, beat), Art.Tile);
                    var t = s.tile[x, y];
                    var so = solid[x, y];
                    if (t == T.Burning)
                    {
                        if (seenTile[x, y] != t) Show(so, s.def.hedges ? "hedge" : blockArt, Art.Tile);
                        float k = Mathf.Clamp01(s.timer[x, y] / Sim.BurnLife);
                        so.color = new Color(1, .35f + .65f * k, .2f + .8f * k, .3f + .7f * k);
                    }
                    else
                    {
                        if (seenTile[x, y] != t) { Show(so, Solid(t, x, y), Art.Tile); so.color = Color.white; }
                        if (t == T.Mirage) so.color = new Color(1, 1, 1, .92f + .08f * Mathf.Sin(Clock * 3 + x + y));
                    }
                    seenTile[x, y] = t;
                    var it = items[x, y];
                    var kind = s.item[x, y];
                    if (seenItem[x, y] != kind) { Show(it, ItemArt(kind), Art.Center); seenItem[x, y] = kind; }
                    if (kind != Item.None) { float b = Mathf.Sin(Clock * 5 + x * 1.3f) * .05f; it.transform.position = new Vector3(x, y + .04f + b); }
                    var fl = fire[x, y];
                    if (s.flame[x, y] > 0)
                    {
                        Show(fl, FlameArt(s.flameMask[x, y], ab), Art.Center);
                        fl.color = new Color(1, 1, 1, Mathf.Clamp01(s.flame[x, y] * 6));
                    }
                    else if (fl.sprite != null) fl.sprite = null;
                }

            // Exit
            bool shown = s.exitX >= 0 && s.mode == Mode.Story && s.tile[s.exitX, s.exitY] == T.Empty && s.hidden[s.exitX, s.exitY] == Item.None && (!s.def.Boss || s.exitOpen);
            exitDoor.gameObject.SetActive(shown);
            if (shown)
            {
                exitDoor.sprite = Art.Tile(s.exitOpen ? "exit_open" : "exit_closed");
                exitDoor.transform.position = new Vector3(s.exitX - .5f, s.exitY - .5f);
                exitDoor.sortingOrder = Order(s.exitY, 2);
            }

            Reset();
            foreach (var w in s.strikes)
            {
                var r = warnPool.Next(); r.sprite = Art.Center("warn_cell"); r.sortingOrder = Order(w.y, 11);
                r.transform.position = new Vector3(w.x, w.y);
                r.color = new Color(1, 1, 1, .55f + .45f * Mathf.Sin(Clock * 20));
            }
            foreach (var b in s.bombs)
            {
                if (b.dead) continue;
                var r = bombPool.Next(); r.sprite = Art.Center(((int)(Clock * (b.fuse < 1 ? 12 : 5))) % 2 == 0 ? "bomb_a" : "bomb_b"); r.sortingOrder = Order(b.y, 6);
                float pulse = 1 + (b.fuse < 1 ? .1f : .05f) * Mathf.Sin(Clock * (b.fuse < 1 ? 26 : 10));
                r.transform.position = new Vector3(b.x, b.y + .05f); r.transform.localScale = new Vector3(pulse, pulse, 1);
                if (b.fuse < .5f) r.color = new Color(1, .7f, .6f);
                Shadow(b.x, b.y - .3f, .8f, Order(b.y, 4));
            }
            foreach (var m in s.movers)
            {
                var r = moverPool.Next(); r.sprite = Art.Tile("piston_h"); r.sortingOrder = Order(m.y, 8);
                r.transform.position = new Vector3(m.x - .5f, m.y - .5f);
            }
            foreach (var c in s.cars)
            {
                var r = carPool.Next(); r.sprite = Art.Center(c.tint == 0 ? "car_red" : c.tint == 1 ? "car_blue" : "car_yellow"); r.sortingOrder = Order(c.row, 10);
                r.flipX = c.dir < 0; r.transform.position = new Vector3(c.x, c.row + .08f);
            }
            foreach (var q in s.shots)
            {
                var r = shotPool.Next(); r.sprite = Art.Center(q.kind switch { 1 => "shot_sting", 2 => "shot_gear", 3 => "shot_ember", _ => "shot_trash" }); r.sortingOrder = Order(q.y, 13);
                r.transform.position = new Vector3(q.x, q.y); r.transform.localRotation = Quaternion.Euler(0, 0, Clock * 400);
            }
            foreach (var e in s.enemies)
            {
                if (e.dead) continue;
                var r = foePool.Next();
                string name = e.kind.ToString().ToLowerInvariant();
                if (e.kind == EnemyKind.Ghost) name = "ghostmoth";
                if (e.dormant) { r.sprite = Art.Tile("mimic_a"); r.sortingOrder = Order(e.y, 8); r.transform.position = new Vector3(e.x - .5f, e.y - .5f); continue; }
                bool second = ((int)(Clock * 8 + e.x)) % 2 == 0;
                r.sprite = e.shell && e.invuln > 0 ? Art.Feet("turtle_shell", .06f) : Art.Feet(e.kind == EnemyKind.Mimic ? "mimic_b" : name + (second ? "_a" : "_b"), .06f);
                r.flipX = e.dirX > 0;
                float hop = e.hopping ? .5f * Mathf.Sin(Mathf.Repeat(Clock * 3, 1) * Mathf.PI) : 0;
                r.sortingOrder = Order(e.y, 9);
                float fly = Sim.Flies(e.kind) ? .15f + .05f * Mathf.Sin(Clock * 7 + e.x) : 0;
                r.transform.position = new Vector3(e.x, e.y - .34f + hop + fly);
                r.transform.localScale = new Vector3(.82f, .82f, 1);
                if (e.invuln > 0 && (int)(Clock * 18) % 2 == 0) r.color = new Color(1, 1, 1, .5f);
                Shadow(e.x, e.y - .34f, .7f, Order(e.y, 4));
            }
            foreach (var p in s.players) Actor(p);
            BossDraw();
            Wind();
            Hide2();
            Puffs(dt);
            dark.Paint(s, Clock);
            Aim();
        }

        void Reset() { bombPool.Reset(); shotPool.Reset(); warnPool.Reset(); carPool.Reset(); moverPool.Reset(); foePool.Reset(); heroPool.Reset(); shadowPool.Reset(); shieldPool.Reset(); }
        void Hide2() { bombPool.Hide(); shotPool.Hide(); warnPool.Hide(); carPool.Hide(); moverPool.Hide(); foePool.Hide(); heroPool.Hide(); shadowPool.Hide(); shieldPool.Hide(); }

        void Shadow(float x, float y, float size, int order)
        {
            var r = shadowPool.Next(); r.sprite = Art.Center("shadow"); r.sortingOrder = order;
            r.transform.position = new Vector3(x, y); r.transform.localScale = new Vector3(size, size, 1);
        }

        void Actor(Player p)
        {
            string skin = p.id == 0 ? "hero" : "p" + (p.id + 1);
            if (!p.alive && p.deadT > 1.1f) return;
            var r = heroPool.Next();
            r.sortingOrder = Order(p.y, 9);
            r.transform.position = new Vector3(p.x, p.y - .36f);
            r.transform.localScale = new Vector3(.86f, .86f, 1);
            if (!p.alive)
            {
                r.sprite = Art.Feet(p.id == 0 ? "hero_hurt" : skin + "_d_0");
                float k = p.deadT / 1.1f;
                r.transform.localRotation = Quaternion.Euler(0, 0, k * 200);
                r.transform.localScale = Vector3.one * .86f * (1 - k * .7f);
                r.color = new Color(1, 1, 1, 1 - k);
                return;
            }
            string d = p.faceX > 0 ? "r" : p.faceX < 0 ? "l" : p.faceY > 0 ? "u" : "d";
            int f = p.moving ? 1 + (int)(p.walkT * 3) % 2 : 0;
            r.sprite = Art.Feet($"{skin}_{d}_{f}");
            if (p.invuln > 0 && (int)(Clock * 16) % 2 == 0) r.color = new Color(1, 1, 1, .45f);
            Shadow(p.x, p.y - .36f, .75f, Order(p.y, 4));
            if (p.shield)
            {
                var q = shieldPool.Next(); q.sprite = Art.Feet("shield"); q.sortingOrder = Order(p.y, 10);
                q.transform.position = r.transform.position; q.transform.localScale = new Vector3(.9f, .9f, 1);
                q.color = new Color(1, 1, 1, .7f + .2f * Mathf.Sin(Clock * 6));
            }
        }

        void BossDraw()
        {
            var b = s.boss;
            bool on = b != null && !b.hidden && !(b.dead && s.clearT > 1.6f);
            bossBody.gameObject.SetActive(on);
            bossShadow.gameObject.SetActive(b != null && !b.dead);
            if (b == null) return;
            bool air = b.air > 0;
            if (!b.dead)
            {
                float sx = air ? b.dirX : b.x, sy = air ? b.dirY : b.y;
                bossShadow.transform.position = new Vector3(sx, sy - 1.1f);
                float k = air ? 3f - b.air * 1.2f : 3.2f;
                bossShadow.transform.localScale = new Vector3(k, k, 1);
                bossShadow.sortingOrder = Order(sy, 4);
                bossShadow.gameObject.SetActive(!b.hidden);
            }
            if (!on) return;
            string name = b.hurtT > .75f ? "_hurt" : b.attacking ? "_attack" : (int)(Clock * 4) % 2 == 0 ? "_idle_a" : "_idle_b";
            bossBody.sprite = Art.Feet("boss" + b.kind + name, .03f);
            float lift = air ? Mathf.Sin(Mathf.Clamp01(b.air) * Mathf.PI) * 2.2f + (1 - Mathf.Clamp01(b.air)) * 0 : 0;
            bossBody.transform.position = new Vector3(air ? Mathf.Lerp(b.dirX, b.x, b.air) : b.x, (air ? Mathf.Lerp(b.dirY, b.y, b.air) : b.y) - 1.25f + lift);
            bossBody.sortingOrder = Order(b.y - 1, 9) + (air ? 200 : 0);
            bossBody.flipX = b.dirX > 0 && b.kind == 1;
            bossBody.color = b.dead ? new Color(1, 1, 1, Mathf.Clamp01(1.6f - s.clearT)) : b.hurtT > 0 && (int)(Clock * 18) % 2 == 0 ? new Color(1, .55f, .5f) : Color.white;
            if (b.dead) { bossBody.transform.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(Clock * 40) * 4); if (Random.value < .3f) Burst(b.x + Random.Range(-1.2f, 1.2f), b.y + Random.Range(-1, 1.2f), "flame_c_a", 1); }
            else bossBody.transform.localRotation = Quaternion.identity;
        }

        void Wind()
        {
            bool on = s.windState > 0;
            windArrow.gameObject.SetActive(on);
            if (!on) return;
            int dx = s.windState == 1 ? s.nextWindX : s.windX, dy = s.windState == 1 ? s.nextWindY : s.windY;
            windArrow.transform.position = new Vector3(7, 6, 0);
            windArrow.transform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(dy, dx) * Mathf.Rad2Deg);
            windArrow.transform.localScale = Vector3.one * (s.windState == 2 ? 3.2f : 2.4f + .3f * Mathf.Sin(Clock * 14));
            windArrow.color = new Color(1, 1, 1, s.windState == 1 ? .6f : .4f);
            if (s.windState == 2 && Random.value < .7f)
            {
                var p = Burst(Random.Range(0f, 14f), Random.Range(0f, 12f), "rubble_desert", 0);
                p.vx = dx * 9; p.vy = dy * 9; p.life = .6f; p.r.transform.localScale = Vector3.one * .35f;
            }
        }

        // ---- effects ----

        public void Shake(float amount) => shake = Mathf.Max(shake, amount);

        Puff Burst(float x, float y, string art, int kind)
        {
            var r = puffPool.Next(); r.sprite = Art.Center(art); r.sortingOrder = 800;
            r.transform.position = new Vector3(x, y);
            var p = new Puff { r = r, life = .5f, vx = Random.Range(-1.5f, 1.5f), vy = Random.Range(-.5f, 2.2f), spin = Random.Range(-300, 300), grow = kind == 1 ? -1 : -.4f };
            puffs.Add(p);
            return p;
        }

        public void Debris(float x, float y)
        {
            for (int i = 0; i < 5; i++)
            {
                var p = Burst(x, y, "rubble_" + theme, 0);
                p.r.transform.localScale = Vector3.one * Random.Range(.25f, .5f);
            }
        }

        public void Sparkle(float x, float y, string art = "hud_star")
        {
            for (int i = 0; i < 6; i++)
            {
                var p = Burst(x, y, art, 0);
                p.r.transform.localScale = Vector3.one * .35f; p.vy = Random.Range(1, 3.5f); p.life = .7f;
            }
        }

        void Puffs(float dt)
        {
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                var p = puffs[i];
                p.t += dt;
                if (p.t >= p.life) { p.r.gameObject.SetActive(false); puffs.RemoveAt(i); continue; }
                var tr = p.r.transform;
                tr.position += new Vector3(p.vx, p.vy) * dt;
                tr.localRotation *= Quaternion.Euler(0, 0, p.spin * dt);
                p.vy -= 5 * dt;
                p.r.color = new Color(1, 1, 1, 1 - p.t / p.life);
            }
            // Finished ones go back to the pool only when none are alive, which keeps the pool simple
            if (puffs.Count == 0) puffPool.Reset();
        }

        void Aim()
        {
            var cam = UnityEngine.Camera.main;
            shake = Mathf.Max(0, shake - Time.unscaledDeltaTime * .9f);
            float a = shake;
            cam.transform.position = new Vector3(7 + Random.Range(-a, a), App.CameraY + Random.Range(-a, a), -10);
        }

        /// Everything outside the light is black; the hero's lamp, bombs and flames push it back.
        class Dark
        {
            const int TW = 96, TH = 56;
            readonly Texture2D tex; readonly SpriteRenderer view; readonly Color32[] px = new Color32[TW * TH]; readonly float[] light = new float[TW * TH];
            const float Left = -9f, Bottom = -2f, CellX = 32f / TW, CellY = 17f / TH;
            bool on;

            public Dark(Transform parent)
            {
                tex = new Texture2D(TW, TH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var sp = Sprite.Create(tex, new Rect(0, 0, TW, TH), Vector2.zero, 1, 0, SpriteMeshType.FullRect);
                view = Art.Spawn("Dark", parent, sp, 950);
                view.transform.position = new Vector3(Left, Bottom);
                view.transform.localScale = new Vector3(CellX, CellY, 1);
                view.gameObject.SetActive(false);
            }

            public void Show(bool show) { on = show; view.gameObject.SetActive(show); }

            void Add(float x, float y, float radius)
            {
                int x0 = Mathf.Max(0, (int)((x - radius - Left) / CellX)), x1 = Mathf.Min(TW - 1, (int)((x + radius - Left) / CellX) + 1);
                int y0 = Mathf.Max(0, (int)((y - radius - Bottom) / CellY)), y1 = Mathf.Min(TH - 1, (int)((y + radius - Bottom) / CellY) + 1);
                for (int j = y0; j <= y1; j++)
                    for (int i = x0; i <= x1; i++)
                    {
                        float dx = Left + (i + .5f) * CellX - x, dy = Bottom + (j + .5f) * CellY - y;
                        float k = 1 - Mathf.Sqrt(dx * dx + dy * dy) / radius;
                        if (k <= 0) continue;
                        k = Mathf.Min(1, k * 2.4f);
                        if (k > light[j * TW + i]) light[j * TW + i] = k;
                    }
            }

            public void Paint(Sim s, float clock)
            {
                if (!on) return;
                System.Array.Clear(light, 0, light.Length);
                foreach (var p in s.players) if (p.alive) Add(p.x, p.y, 3.4f + Mathf.Sin(clock * 9) * .06f);
                foreach (var b in s.bombs) Add(b.x, b.y, 1.9f);
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (s.flame[x, y] > 0) Add(x, y, 1.8f);
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(5, 6, 20, (byte)(245 * (1 - light[i])));
                tex.SetPixels32(px); tex.Apply(false);
            }
        }
    }
}
