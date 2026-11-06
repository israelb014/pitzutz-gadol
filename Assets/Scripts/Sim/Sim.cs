using System;
using System.Collections.Generic;

namespace Pitzutz
{
    public enum Mode { Story, Battle }
    public enum Outcome { Running, Cleared, Dead, RoundOver }

    /// The whole game world, without Unity: the same code runs on screen, in the bots and in the build checks.
    public partial class Sim
    {
        public const int W = 15, H = 13;
        public const float Dt = 1 / 60f, Fuse = 3f, FlameLife = .55f, BurnLife = .45f, StageTime = 180f, ShrinkAt = 120f;

        public static int R(float v) => (int)Math.Floor(v + .5f);
        public static bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;

        public readonly T[,] tile = new T[W, H];
        public readonly F[,] floor = new F[W, H];
        public readonly Item[,] hidden = new Item[W, H];
        public readonly Item[,] item = new Item[W, H];
        public readonly int[,] tele = new int[W, H];          // index of the partner cell, y * W + x
        public readonly int[,] crumble = new int[W, H];
        public readonly float[,] timer = new float[W, H];     // burn, regrow and refill clocks
        public readonly float[,] flame = new float[W, H];
        public readonly byte[,] flameMask = new byte[W, H];   // see FlameBits
        public readonly bool[,] jetFlame = new bool[W, H];

        public const byte FH = 1, FV = 2, FC = 4, TipL = 8, TipR = 16, TipU = 32, TipD = 64;

        public readonly List<Player> players = new List<Player>();
        public readonly List<Bomb> bombs = new List<Bomb>();
        public readonly List<Enemy> enemies = new List<Enemy>();
        public readonly List<Strike> strikes = new List<Strike>();
        public readonly List<Shot> shots = new List<Shot>();
        public readonly List<Car> cars = new List<Car>();
        public readonly List<CarLane> lanes = new List<CarLane>();
        public readonly List<Mover> movers = new List<Mover>();
        public readonly List<Jet> jets = new List<Jet>();
        public readonly List<Event> events = new List<Event>();
        public Boss boss;

        public Mode mode = Mode.Story;
        public StageDef def;
        public int seed;
        public Rng rng;
        public float time, timeLeft = StageTime;
        public Outcome outcome;
        public int exitX = -1, exitY = -1;
        public bool exitOpen, exitSeen, secretFound, hurry, gateRedUp = true;
        public int score, kills;
        public float hurryT, switchCool, clearT;
        public bool god;                                      // tests only

        // Wind (desert): calm, warning, gust
        public int windX, windY;
        public float windT = 5; public int windState;         // 0 calm, 1 warning, 2 blowing
        public bool Windy => def != null && def.wind;
        public int nextWindX, nextWindY;

        // Battle
        public readonly List<(int x, int y)> shrinkOrder = new List<(int, int)>();
        public int shrinkAt; public float shrinkT = .4f;
        public int roundWinner = -1;
        public float overT;

        public Player Hero => players[0];

        public void Emit(Ev k, float x = 0, float y = 0, int who = 0) => events.Add(new Event { kind = k, x = x, y = y, who = who });

        public bool Solid(T t) => t != T.Empty && t != T.Sprout && t != T.Mirage;

        public Bomb BombAt(int x, int y)
        {
            for (int i = 0; i < bombs.Count; i++) { var b = bombs[i]; if (!b.dead && b.Cx == x && b.Cy == y) return b; }
            return null;
        }

        public Enemy DormantAt(int x, int y)
        {
            foreach (var e in enemies) if (e.dormant && !e.dead && e.Cx == x && e.Cy == y) return e;
            return null;
        }

        public bool MoverOn(int x, int y)
        {
            foreach (var m in movers)
            {
                if (m.cellsX[m.at] == x && m.cellsY[m.at] == y) return true;
                if (m.moving && m.nextX == x && m.nextY == y) return true;
            }
            return false;
        }

        /// One-way cells can not be entered or left against their arrow.
        public bool OneWayStops(int fromX, int fromY, int toX, int toY)
        {
            int dx = toX - fromX, dy = toY - fromY;
            return Against(floor[toX, toY], dx, dy) || Against(floor[fromX, fromY], dx, dy);
        }

        static bool Against(F f, int dx, int dy) =>
            f == F.OneU && dy < 0 || f == F.OneD && dy > 0 || f == F.OneL && dx > 0 || f == F.OneR && dx < 0;

        /// Can this player step from one cell into the next one? Bombs count unless the player is still standing on them.
        public bool Open(Player p, int fx, int fy, int x, int y)
        {
            if (!In(x, y) || Solid(tile[x, y]) || floor[x, y] == F.Pit) return false;
            if (OneWayStops(fx, fy, x, y) || MoverOn(x, y) || DormantAt(x, y) != null) return false;
            var b = BombAt(x, y);
            return b == null || p != null && (b.pass >> p.id & 1) != 0;
        }

        // ---------- the tick ----------

        public void Tick(Pad[] pads)
        {
            if (outcome != Outcome.Running) return;
            time += Dt;
            if (switchCool > 0) switchCool -= Dt;
            if (mode == Mode.Story) StoryClock(); else BattleClock();

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (!p.alive) { p.deadT += Dt; continue; }
                if (p.invuln > 0) p.invuln -= Dt;
                Drive(p, i < pads.Length ? pads[i] : default);
            }
            Bombs();
            Burn();
            Hazards();
            StepEnemies();
            StepBoss();
            Touching();
            Finish();
        }

        void StoryClock()
        {
            timeLeft -= Dt;
            if (timeLeft > 0) return;
            timeLeft = 0;
            if (!hurry) { hurry = true; hurryT = 0; Emit(Ev.Hurry); }
            hurryT -= Dt;
            if (hurryT <= 0)
            {
                hurryT = 20;
                int alive = 0;
                foreach (var e in enemies) if (e.kind == EnemyKind.Wasp) alive++;
                for (int i = 0; i < 3 && alive < 6; i++, alive++) SpawnWasp();
            }
        }

        /// Fast enemies come in from the corner furthest from the hero.
        void SpawnWasp()
        {
            int bx = 1, by = 1; float best = -1;
            foreach (var (x, y) in new[] { (1, 1), (13, 1), (1, 11), (13, 11), (7, 1), (7, 11) })
            {
                float d = Math.Abs(x - Hero.x) + Math.Abs(y - Hero.y) + rng.Float() * 3;
                if (d > best) { best = d; bx = x; by = y; }
            }
            AddEnemy(EnemyKind.Wasp, bx, by);
        }

        void BattleClock()
        {
            if (time < ShrinkAt || shrinkAt >= shrinkOrder.Count || overT > 0) return;   // the last one standing has won: the walls stop
            shrinkT -= Dt;
            if (shrinkT > 0) return;
            shrinkT = .4f;
            var (x, y) = shrinkOrder[shrinkAt++];
            tile[x, y] = T.Shrunk; item[x, y] = Item.None; hidden[x, y] = Item.None; floor[x, y] = F.Plain;
            var b = BombAt(x, y); if (b != null) b.dead = true;
            bombs.RemoveAll(q => q.dead);
            Player spared = shrinkAt == shrinkOrder.Count ? Strongest() : null;
            foreach (var p in players) if (p.alive && p != spared && (p.Cx == x && p.Cy == y || spared != null)) Kill(p);
            Emit(Ev.Shrink, x, y);
        }

        /// The walls have closed with several still standing: whoever collected the most wins. A tie stays a draw.
        Player Strongest()
        {
            Player best = null; int top = -1; bool tie = false;
            foreach (var p in players)
            {
                if (!p.alive) continue;
                int power = p.bombsMax + p.flame + p.speedLv + (p.kick ? 1 : 0) + (p.shield ? 1 : 0);
                if (power > top) { top = power; best = p; tie = false; }
                else if (power == top) tie = true;
            }
            return tie ? null : best;
        }

        /// Spiral from the outside in, the way the walls fall.
        public void BuildShrink()
        {
            shrinkOrder.Clear();
            int l = 1, r = W - 2, b = 1, t = H - 2;
            while (l <= r && b <= t)
            {
                for (int x = l; x <= r; x++) Add(x, t);
                for (int y = t - 1; y >= b; y--) Add(r, y);
                if (b < t) for (int x = r - 1; x >= l; x--) Add(x, b);
                if (l < r) for (int y = b + 1; y < t; y++) Add(l, y);
                l++; r--; b++; t--;
            }
            void Add(int x, int y) { if (tile[x, y] != T.Wall) shrinkOrder.Add((x, y)); }
        }

        // ---------- hits ----------

        public void Hit(Player p)
        {
            if (!p.alive || god || p.invuln > 0) return;
            if (p.shield) { p.shield = false; p.invuln = 1.6f; Emit(Ev.ShieldBreak, p.x, p.y, p.id); return; }
            Kill(p);
        }

        void Kill(Player p)
        {
            if (!p.alive || god) return;
            p.alive = false; p.deadT = 0; p.shield = false;
            Emit(Ev.Die, p.x, p.y, p.id);
            if (mode == Mode.Battle) DropItems(p);
        }

        /// A beaten rival leaves some of what it picked up, scattered on free floor.
        void DropItems(Player p)
        {
            var free = new List<(int x, int y)>();
            for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
                if (tile[x, y] == T.Empty && item[x, y] == Item.None && floor[x, y] == F.Plain && BombAt(x, y) == null && flame[x, y] <= 0) free.Add((x, y));
            rng.Shuffle(free);
            var drop = new List<Item>();
            for (int i = 1; i < p.bombsMax && drop.Count < 3; i++) drop.Add(Item.Bomb);
            for (int i = 2; i < p.flame && drop.Count < 5; i++) drop.Add(Item.Flame);
            if (p.kick) drop.Add(Item.Kick);
            for (int i = 0; i < drop.Count && i < free.Count; i++) item[free[i].x, free[i].y] = drop[i];
        }

        void Touching()
        {
            foreach (var p in players)
            {
                if (!p.alive) continue;
                int cx = p.Cx, cy = p.Cy;
                if (flame[cx, cy] > 0) Hit(p);
                if (!p.alive) continue;
                foreach (var e in enemies)
                {
                    if (e.dormant || e.dead) continue;
                    if (Math.Abs(e.x - p.x) < .62f && Math.Abs(e.y - p.y) < .62f) { Hit(p); break; }
                }
                if (!p.alive) continue;
                if (boss != null && !boss.dead && !boss.hidden && boss.air <= 0
                    && Math.Abs(boss.x - p.x) < boss.half + .25f && Math.Abs(boss.y - p.y) < boss.half + .25f) Hit(p);
                if (!p.alive) continue;
                foreach (var s in shots)
                    if (Math.Abs(s.x - p.x) < .45f && Math.Abs(s.y - p.y) < .45f) { Hit(p); break; }
                if (!p.alive) continue;
                foreach (var c in cars)
                    if (c.row == cy && Math.Abs(c.x - p.x) < 1.25f && Math.Abs(c.row - p.y) < .6f) { Hit(p); break; }
                if (!p.alive) continue;
                Take(p, cx, cy);
            }
        }

        void Take(Player p, int x, int y)
        {
            var it = item[x, y];
            if (it == Item.None) return;
            item[x, y] = Item.None;
            switch (it)
            {
                case Item.Bomb: p.bombsMax = Math.Min(8, p.bombsMax + 1); break;
                case Item.Flame: p.flame = Math.Min(8, p.flame + 1); break;
                case Item.Speed: p.speedLv = Math.Min(5, p.speedLv + 1); break;
                case Item.Kick: p.kick = true; break;
                case Item.Shield: p.shield = true; break;
                case Item.Secret: secretFound = true; score += 1000; Emit(Ev.Secret, x, y, p.id); return;
            }
            score += 50;
            Emit(Ev.Power, x, y, p.id);
        }

        void Finish()
        {
            if (mode == Mode.Battle)
            {
                int alive = 0, last = -1;
                foreach (var p in players) if (p.alive) { alive++; last = p.id; }
                if (alive <= 1)
                {
                    overT += Dt;
                    if (overT < 1.3f) return;     // time for a shared explosion to count as a draw
                    alive = 0; last = -1;
                    foreach (var p in players) if (p.alive) { alive++; last = p.id; }
                    roundWinner = alive == 1 ? last : -1;
                    outcome = Outcome.RoundOver;
                }
                return;
            }

            var h = Hero;
            if (!h.alive) { if (h.deadT > 1.6f) outcome = Outcome.Dead; return; }

            bool cleared = enemies.Count == 0 && (boss == null || boss.dead);
            if (cleared && !exitOpen) { exitOpen = true; Emit(Ev.ExitOpen, exitX, exitY); }
            if (boss != null && boss.dead)
            {
                clearT += Dt;
                if (clearT > 2.2f) { outcome = Outcome.Cleared; Emit(Ev.StageClear); }
                return;
            }
            if (exitOpen && exitX >= 0 && tile[exitX, exitY] == T.Empty && Math.Abs(h.x - exitX) < .4f && Math.Abs(h.y - exitY) < .4f)
            {
                score += (int)timeLeft * 10;
                outcome = Outcome.Cleared; Emit(Ev.StageClear);
            }
        }
    }
}
