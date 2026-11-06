using System;
using System.Collections.Generic;

namespace Pitzutz
{
    public class Skill
    {
        public float think = .1f, mistake, aim = 1;
        public float notice = 9;           // a rival's bomb is only noticed with this many seconds left on it
        public int cap = 8;                // never more bombs than this out at once
        public bool traps, kicks, smart;
        public static readonly Skill Easy = new Skill { think = .45f, mistake = .15f, aim = .5f, notice = .6f, cap = 1 };
        public static readonly Skill Normal = new Skill { think = .3f, mistake = .06f, aim = .7f, notice = .8f, cap = 2, kicks = true };
        public static readonly Skill Hard = new Skill { think = .1f, traps = true, kicks = true, smart = true };
    }

    /// A player without hands: it reads the same world as a person and answers with the same pad.
    /// Every plan draws a timeline per cell (what burns when), then works backward from the end of the timeline
    /// to learn from which cell at which moment it can still stay alive. It only ever walks through such moments.
    public class Brain
    {
        const int Slots = 64;              // timeline of 6.4 seconds
        const float Slot = .1f;
        const int W = Sim.W, H = Sim.H, Cells = W * H;
        const int Rest = 4, St = 5;        // a node is cell * 5 + slide direction (ice), or + Rest when not sliding
        const float DarkSight = 5;

        readonly Sim s; readonly Player me; readonly Skill skill; readonly Rng rng;
        static readonly (int x, int y)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        // The future
        readonly ulong[] burn = new ulong[Cells], jetBits = new ulong[Cells], savedBurn = new ulong[Cells], fire = new ulong[Cells];
        readonly float[] clearAt = new float[Cells];
        readonly List<(int x, int y, float fuse, int range)> chain = new List<(int, int, float, int)>();
        bool blind;                        // leaves enemies out, for the check of a stage before it starts
        float look = 1.2f, pad = .2f;      // how far ahead enemies are feared and how much room is left around flames;
                                           // both shrink only when there is no other way
        bool loose;                        // nothing is safe any more: just run

        // The graph of steps
        readonly bool[] open = new bool[Cells];
        readonly int[] nodes = new int[Cells * St];
        int nodeCount;
        readonly int[] eFirst = new int[Cells * St + 1], eLast = new int[Cells * St + 1];
        readonly int[] eTo = new int[Cells * St * 6], eFrom = new int[Cells * St * 6], eVia = new int[Cells * St * 6];
        readonly byte[] eCost = new byte[Cells * St * 6], ePress = new byte[Cells * St * 6];
        int edgeCount;

        // Which moments are survivable, and which the bot can get to
        readonly ulong[] alive = new ulong[Cells * St], savedAlive = new ulong[Cells * St], seen = new ulong[Cells * St];
        readonly int[] parent = new int[Cells * St * Slots];
        readonly int[] arrive = new int[Cells], arriveNode = new int[Cells];
        int startNode, underfoot = -1, startAt = -1;

        // What it is doing
        struct Seg { public int press, land; public float until; }
        readonly List<Seg> segs = new List<Seg>();
        int seg;
        float clock;
        bool wantBomb;
        int goalCell = -1; bool goalBomb;
        readonly Dictionary<int, float> tabu = new Dictionary<int, float>();
        float stuckT, pressT; int lastCell = -1;

        // What it knows
        readonly List<Enemy> foes = new List<Enemy>();
        readonly float[] heat = new float[Cells];
        readonly bool[] burnt = new bool[Cells];     // hedges it has already looked behind
        readonly int[] soft = new int[Cells];
        bool needFlip; int huntHere, flipGoal = -1;
        readonly int[] bfs = new int[Cells];
        readonly int[] shrinkRank = new int[Cells];
        readonly float[] tie = new float[Cells];     // fixed small preferences, so that ties do not always go to the same corner
        bool hasIce;

        public Brain(Sim sim, Player p, Skill k, int seed)
        {
            s = sim; me = p; skill = k; rng = new Rng(seed + 31 * p.id);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (s.floor[x, y] == F.Ice) hasIce = true;
            for (int i = 0; i < s.shrinkOrder.Count; i++) { var (x, y) = s.shrinkOrder[i]; shrinkRank[y * W + x] = i + 1; }
            for (int i = 0; i < Cells; i++) tie[i] = rng.Float() * .1f;
        }

        public static Action<string> Debug;

        public Pad Think()
        {
            var pad = default(Pad);
            if (!me.alive) return pad;
            clock -= Sim.Dt;
            if (clock <= 0) { clock = skill.think; Plan(); }
            if (wantBomb) { pad.bomb = true; wantBomb = false; }
            Steer(ref pad);
            return pad;
        }

        void Steer(ref Pad pad)
        {
            int cell = me.Cy * W + me.Cx;
            while (seg < segs.Count)
            {
                var g = segs[seg];
                if (g.press == Rest) { if (s.time >= g.until) { seg++; continue; } break; }
                if (g.land == cell && g.until >= 0)
                {
                    // On a belt the middle of the cell has to be reached, or the belt takes the bot back
                    bool belt = Sim.BeltDir(s.floor[me.Cx, me.Cy]) != (0, 0);
                    if (!belt || Math.Abs(me.x - me.Cx) + Math.Abs(me.y - me.Cy) < .15f) { seg++; continue; }
                }
                break;
            }
            if (seg < segs.Count && segs[seg].press != Rest)
            {
                var g = segs[seg];
                pad.mx = Dirs[g.press].x; pad.my = Dirs[g.press].y;
                if (g.until < 0) seg++;             // a brake: one press
                return;
            }
            var ground = s.floor[me.Cx, me.Cy];
            if (ground == F.Ice || Sim.BeltDir(ground) != (0, 0)) return;
            // Standing still means standing in the middle of a cell, not on its border
            float ox = me.Cx - me.x, oy = me.Cy - me.y;
            if (Math.Abs(ox) > .06f) pad.mx = Math.Sign(ox); else if (Math.Abs(oy) > .06f) pad.my = Math.Sign(oy);
        }

        // ---------- the future ----------

        static ulong Span(float from, float to)
        {
            int a = Math.Max(0, (int)(from / Slot)), b = Math.Min(Slots - 1, (int)Math.Ceiling(to / Slot));
            if (a >= Slots || b < 0 || b < a) return 0;
            return Bits(a, b);
        }

        static ulong Bits(int a, int b)
        {
            if (b >= Slots) b = Slots - 1;
            if (a > b) return 0;
            return (~0UL >> (63 - (b - a))) << a;
        }

        void Mark(int x, int y, float from, float to) { if (Sim.In(x, y)) burn[y * W + x] |= Span(from, to); }

        /// Fills the timeline: bombs with their chains, flames, cars, jets, warned cells, shots, and anything else that bites.
        void Foresee(int extra = -1, float extraFuse = 0)
        {
            Array.Clear(burn, 0, Cells);
            Array.Clear(jetBits, 0, Cells);
            Array.Clear(fire, 0, Cells);

            foreach (var j in s.jets)
            {
                float cycle = (s.time + j.phase) % j.period, begin = j.period - j.fire;
                float first = cycle >= begin ? begin - cycle : begin - cycle;
                foreach (var (x, y) in s.JetLine(j))
                    for (int n = 0; n < 3; n++)
                    {
                        ulong m = Span(first + n * j.period - pad, first + n * j.period + j.fire + Math.Min(pad, .15f));
                        burn[y * W + x] |= m; jetBits[y * W + x] |= m;
                    }
            }

            // Whatever lands on a bomb sets it off
            foreach (var q in s.strikes) if (Sim.In(q.x, q.y)) jetBits[q.y * W + q.x] |= Span(q.delay, q.delay + .1f);
            foreach (var sh in s.shots)
            {
                float x = sh.x, y = sh.y;
                for (int k = 0; k < 30; k++)
                {
                    int cx = Sim.R(x), cy = Sim.R(y);
                    if (!Sim.In(cx, cy) || s.Solid(s.tile[cx, cy])) break;
                    jetBits[cy * W + cx] |= Span(k * .1f - .1f, k * .1f + .1f);
                    x += sh.vx * .1f; y += sh.vy * .1f;
                }
            }

            chain.Clear();
            foreach (var b in s.bombs)
                if (!b.dead && (b.owner == me.id || b.fuse <= skill.notice)) AddBomb(b.Cx, b.Cy, b.fuse, b.range, b.sx, b.sy, b.nudged);
            if (extra >= 0) AddBomb(extra % W, extra / W, extraFuse, me.flame, 0, 0, false);

            // A jet sets a bomb off, and so does a bomb inside another's cross
            for (int i = 0; i < chain.Count; i++)
            {
                var c = chain[i];
                ulong j = jetBits[c.y * W + c.x];
                if (j == 0) continue;
                int slot = 0; while ((j >> slot & 1) == 0) slot++;
                if (slot * Slot < c.fuse) { c.fuse = slot * Slot; chain[i] = c; }
            }
            bool changed = true;
            for (int round = 0; changed && round < 8; round++)
            {
                changed = false;
                for (int i = 0; i < chain.Count; i++)
                    for (int j = 0; j < chain.Count; j++)
                    {
                        if (i == j || chain[j].fuse <= chain[i].fuse) continue;
                        if (Reaches(chain[i].x, chain[i].y, chain[i].range, chain[j].x, chain[j].y))
                        {
                            var c = chain[j]; c.fuse = chain[i].fuse; chain[j] = c; changed = true;
                        }
                    }
            }
            chain.Sort((a, b) => a.fuse.CompareTo(b.fuse));
            for (int i = 0; i < Cells; i++) clearAt[i] = float.MaxValue;
            foreach (var c in chain) Cross(c.x, c.y, c.range, c.fuse);

            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (s.flame[x, y] > 0) Mark(x, y, 0, s.flame[x, y] + .1f);

            Cars();

            foreach (var q in s.strikes)
            {
                float life = q.kind < 0 ? .5f : q.life + .2f;
                Mark(q.x, q.y, q.delay - pad, q.delay + life);
                if (Sim.In(q.x, q.y)) fire[q.y * W + q.x] |= Span(q.delay - pad, q.delay + life);
            }

            foreach (var sh in s.shots)
            {
                float x = sh.x, y = sh.y;
                for (int k = 0; k < 30; k++)
                {
                    int cx = Sim.R(x), cy = Sim.R(y);
                    if (!Sim.In(cx, cy) || s.Solid(s.tile[cx, cy])) break;
                    Mark(cx, cy, k * .1f - .3f, k * .1f + .3f);
                    x += sh.vx * .1f; y += sh.vy * .1f;
                }
            }

            var o = s.boss;
            if (o != null && !o.dead && !o.hidden && o.air <= 0)
            {
                // Walking bosses follow the bot; the truck only lines up with its row
                float reach = o.half + .25f + pad, rage = o.Frac <= .5f ? 1.35f : 1;
                float vx = o.state != 0 || o.kind == 1 ? 0 : 1.6f * rage, vy = o.state != 0 ? 0 : (o.kind == 1 ? 2.4f : 1.6f) * rage;
                float until = look > .3f ? Slots * Slot : look + .2f;
                for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
                {
                    float dx = Math.Abs(o.x - x), dy = Math.Abs(o.y - y);
                    if (dx >= reach + vx * look || dy >= reach + vy * look) continue;
                    float tx = dx < reach ? 0 : (dx - reach) / vx, ty = dy < reach ? 0 : (dy - reach) / vy;
                    Mark(x, y, Math.Max(tx, ty), until);
                }
            }

            foreach (var m in s.movers) Shuttle(m);

            if (!blind) foreach (var e in foes) Prowl(e);

            if (s.mode == Mode.Battle && s.time > Sim.ShrinkAt - 7)
            {
                float t = Math.Max(0, Sim.ShrinkAt - s.time) + (s.time >= Sim.ShrinkAt ? s.shrinkT : .4f);
                for (int k = s.shrinkAt; k < s.shrinkOrder.Count - 1 && t < Slots * Slot; k++, t += .4f)
                {
                    var (x, y) = s.shrinkOrder[k];
                    burn[y * W + x] |= Span(t - .5f, Slots * Slot);
                }
            }
        }

        void AddBomb(int x, int y, float fuse, int range, int sx, int sy, bool nudged)
        {
            float t = 0;
            for (int i = 0; i < 16; i++)
            {
                int dx = sx, dy = sy;
                if (dx == 0 && dy == 0) (dx, dy) = Sim.BeltDir(s.floor[x, y]);
                if (dx == 0 && dy == 0) break;
                if (!BombGoes(x, y, x + dx, y + dy)) { if (sx != 0 || sy != 0) { sx = sy = 0; continue; } break; }
                t += 1 / 7f;
                if (t >= fuse) break;
                x += dx; y += dy;
                if (nudged) { sx = sy = 0; nudged = false; }
            }
            chain.Add((x, y, fuse, range));
            if (!s.Windy) return;

            // A gust moves a bomb a cell every .65 seconds. Before the arrow shows, it could be any way
            float from = s.windState == 2 ? 0 : s.windState == 1 ? s.windT : s.windT + 1.3f;
            float to = s.windState == 2 ? s.windT : from + 2.6f;
            if (fuse <= from) return;
            int n = (int)((Math.Min(fuse, to) - from) / .65f) + 1;
            for (int d = 0; d < 4; d++)
            {
                if (s.windState == 2 && (Dirs[d].x != s.windX || Dirs[d].y != s.windY)) continue;
                if (s.windState == 1 && (Dirs[d].x != s.nextWindX || Dirs[d].y != s.nextWindY)) continue;
                int cx = x, cy = y;
                for (int i = 0; i < n; i++)
                {
                    if (!BombGoes(cx, cy, cx + Dirs[d].x, cy + Dirs[d].y)) break;
                    cx += Dirs[d].x; cy += Dirs[d].y;
                    chain.Add((cx, cy, fuse, range));
                }
            }
        }

        bool BombGoes(int fx, int fy, int x, int y) =>
            Sim.In(x, y) && !s.Solid(s.tile[x, y]) && s.floor[x, y] != F.Pit && !s.OneWayStops(fx, fy, x, y) && s.BombAt(x, y) == null;

        void Cars()
        {
            foreach (var l in s.lanes)
            {
                double now = (s.time + l.phase) / l.period;
                int k0 = (int)Math.Floor(now) - 8, k1 = (int)Math.Floor(now + Slots * Slot / l.period) + 1;
                float start = l.dir > 0 ? -2.5f : W + 1.5f, side = 1.75f / l.speed;
                for (int k = Math.Max(1, k0); k <= k1; k++)
                {
                    float born = k * l.period - l.phase - s.time;
                    for (int x = 1; x < W - 1; x++)
                    {
                        float at = born + (x - start) / (l.dir * l.speed);
                        Mark(x, l.row, at - side - pad + .1f, at + side);
                    }
                }
            }
        }

        /// Plays a moving wall forward and marks the cells it will fill.
        void Shuttle(Mover m)
        {
            int at = m.at, step = m.step; float t = 0, wait = m.wait; bool moving = m.moving;
            int nx = m.nextX, ny = m.nextY; float left = Math.Abs(m.nextX - m.x) + Math.Abs(m.nextY - m.y);
            float end = Slots * Slot;
            while (t < end)
            {
                int cx = m.cellsX[at], cy = m.cellsY[at];
                if (moving)
                {
                    float go = left / m.speed;
                    Mark(cx, cy, t - .15f, t + go * .6f + .15f); Mark(nx, ny, t - .15f, t + go + .2f);
                    t += go;
                    at += step; moving = false; wait = m.pause;
                }
                else
                {
                    Mark(cx, cy, t - .15f, t + wait + .15f);
                    t += wait;
                    int n = at + step;
                    if (n < 0 || n >= m.cellsX.Length) { step = -step; n = at + step; }
                    nx = m.cellsX[n]; ny = m.cellsY[n]; moving = true; left = 1;
                }
            }
        }

        /// Where an enemy could be soon: every cell a short walk from it, from the moment it could get there.
        void Prowl(Enemy e)
        {
            float speed = e.speed, touch = .62f / speed + .1f, late = look;
            bool fly = Sim.Flies(e.kind);
            int far = (int)Math.Ceiling(speed * late + .62f);
            float until = late > .3f ? Slots * Slot : late + .2f;
            Mark(e.Cx, e.Cy, 0, until);
            int head = 0, tail = 0;
            for (int i = 0; i < Cells; i++) soft[i] = -1;
            int c0 = e.ty * W + e.tx;
            soft[c0] = 0; bfs[tail++] = c0;
            float lead = (Math.Abs(e.tx - e.x) + Math.Abs(e.ty - e.y)) / speed;
            while (head < tail)
            {
                int c = bfs[head++], x = c % W, y = c / W, d = soft[c];
                Mark(x, y, lead + d / speed - touch, until);
                if (d >= far) continue;
                foreach (var (dx, dy) in Dirs)
                {
                    int nx = x + dx, ny = y + dy, n = ny * W + nx;
                    if (!Sim.In(nx, ny) || soft[n] >= 0) continue;
                    var t = s.tile[nx, ny];
                    if (t == T.Edge || t == T.Wall || t == T.Crack || t == T.Shrunk || t == T.GateRed || t == T.GateBlue) continue;
                    if (!fly && (s.Solid(t) || s.floor[nx, ny] == F.Pit || s.floor[nx, ny] == F.Road || s.OneWayStops(x, y, nx, ny))) continue;
                    soft[n] = d + 1; bfs[tail++] = n;
                }
            }
            if (e.kind == EnemyKind.Frog)
                foreach (var (dx, dy) in Dirs) Mark(e.Cx + dx * 2, e.Cy + dy * 2, 0, .9f);
        }

        bool Reaches(int x, int y, int range, int tx, int ty)
        {
            if (x != tx && y != ty) return false;
            int dx = Math.Sign(tx - x), dy = Math.Sign(ty - y), n = Math.Abs(tx - x) + Math.Abs(ty - y);
            if (n > range) return false;
            for (int i = 1; i < n; i++)
                if (s.Solid(s.tile[x + dx * i, y + dy * i])) return false;
            return true;
        }

        void Cross(int x, int y, int range, float at, int depth = 0)
        {
            float start = at - pad, end = at + Sim.FlameLife + Math.Min(pad, .1f);
            ulong span = Span(start, end);
            burn[y * W + x] |= span; fire[y * W + x] |= span;
            foreach (var (dx, dy) in Dirs)
                for (int i = 1; i <= range; i++)
                {
                    int cx = x + dx * i, cy = y + dy * i, c = cy * W + cx;
                    if (!Sim.In(cx, cy)) break;
                    var t = s.tile[cx, cy];
                    if (t == T.Edge || t == T.Wall || t == T.Shrunk || t == T.GateRed || t == T.GateBlue) break;
                    if (t == T.Crack) { if (clearAt[c] + .1f >= at) { clearAt[c] = Math.Min(clearAt[c], at); break; } }
                    if (t == T.Keg) { if (depth < 6) Cross(cx, cy, 3, at + .02f, depth + 1); break; }
                    burn[c] |= span; fire[c] |= span;
                    if (t == T.Block || t == T.Hedge)
                    {
                        // Gone once an earlier flame has burned it through
                        if (clearAt[c] + Sim.BurnLife - .1f >= at) { clearAt[c] = Math.Min(clearAt[c], at); break; }
                    }
                    if (t == T.Burning && s.timer[cx, cy] > at - .1f) break;
                }
        }

        // ---------- the graph of steps ----------

        float Speed(int x, int y, int d)
        {
            var f = s.floor[x, y];
            float v = me.Speed;
            if (f == F.Quick) v *= .5f; else if (f == F.Ice) v *= 1.15f;
            var (bx, by) = Sim.BeltDir(f);
            v += 1.5f * (bx * Dirs[d].x + by * Dirs[d].y);
            if (s.windState == 2) v += 1.5f * (s.windX * Dirs[d].x + s.windY * Dirs[d].y);
            return Math.Max(v, .8f);
        }

        bool Can(int x, int y, int d)
        {
            int nx = x + Dirs[d].x, ny = y + Dirs[d].y;
            return Sim.In(nx, ny) && open[ny * W + nx] && !s.OneWayStops(x, y, nx, ny);
        }

        void Edge(int from, int to, int cost, int press, int via)
        {
            int e = edgeCount++;
            eFrom[e] = from; eTo[e] = to; eCost[e] = (byte)cost; ePress[e] = (byte)press; eVia[e] = via;
        }

        void Step(int n, int x, int y, int d)
        {
            int nx = x + Dirs[d].x, ny = y + Dirs[d].y, via = ny * W + nx, land = via;
            int cost = Math.Max(1, (int)Math.Ceiling((.5f / Speed(x, y, d) + .5f / Speed(nx, ny, d)) / Slot - .15f));
            if (s.floor[nx, ny] == F.Tele) land = s.tele[nx, ny];
            bool ice = s.floor[land % W, land / W] == F.Ice;
            Edge(n, land * St + (ice ? d : Rest), cost, d, via);
        }

        /// Which steps exist right now. Moving walls are not here: they live on the timeline.
        void Build()
        {
            int here = startAt >= 0 ? startAt : me.Cy * W + me.Cx;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    open[y * W + x] = !s.Solid(s.tile[x, y]) && s.floor[x, y] != F.Pit;
            underfoot = -1;
            foreach (var b in s.bombs)
            {
                if (b.dead) continue;
                int c = b.Cy * W + b.Cx;
                open[c] = false;
                if (c == here) underfoot = c;     // once off a bomb there is no stepping back on
            }
            foreach (var e in s.enemies) if (e.dormant && !e.dead) open[e.Cy * W + e.Cx] = false;
            open[here] = true;

            nodeCount = 0; edgeCount = 0;
            for (int y = 1; y < H - 1; y++)
                for (int x = 1; x < W - 1; x++)
                {
                    int c = y * W + x;
                    if (!open[c]) continue;
                    var f = s.floor[x, y];
                    int rest = c * St + Rest;
                    nodes[nodeCount++] = rest;
                    eFirst[rest] = edgeCount;
                    var (bx, by) = Sim.BeltDir(f);
                    if (bx == 0 && by == 0) Edge(rest, rest, 1, Rest, c);
                    else
                    {
                        int d = bx > 0 ? 0 : bx < 0 ? 1 : by > 0 ? 2 : 3;
                        if (Can(x, y, d)) { int to = (y + by) * W + x + bx; Edge(rest, to * St + Rest, 6, Rest, to); }
                        else Edge(rest, rest, 1, Rest, c);
                    }
                    for (int d = 0; d < 4; d++) if (Can(x, y, d)) Step(rest, x, y, d);
                    eLast[rest] = edgeCount;
                    if (f != F.Ice) continue;

                    for (int v = 0; v < 4; v++)
                    {
                        int n = c * St + v;
                        nodes[nodeCount++] = n;
                        eFirst[n] = edgeCount;
                        bool ahead = Can(x, y, v);
                        if (ahead) Step(n, x, y, v); else Edge(n, rest, 1, Rest, c);
                        for (int d = 0; d < 4; d++)
                        {
                            if (d == v) continue;
                            if (Can(x, y, d)) { Step(n, x, y, d); continue; }
                            bool perpendicular = (Dirs[d].x != 0) != (Dirs[v].x != 0);
                            int ax = x + Dirs[v].x, ay = y + Dirs[v].y;
                            if (perpendicular && (!ahead || !Can(ax, ay, d))) Edge(n, rest, 1, d, c);   // leaning on a wall brakes
                        }
                        eLast[n] = edgeCount;
                    }
                }
            int ground = here * St + Rest;
            startNode = startAt < 0 && s.floor[me.Cx, me.Cy] == F.Ice && (me.iceX != 0 || me.iceY != 0) ? here * St + DirOf(me.iceX, me.iceY) : ground;
        }

        static int DirOf(int x, int y) => x > 0 ? 0 : x < 0 ? 1 : y > 0 ? 2 : 3;

        bool Pass(int c, int via, int t, int cost, int land)
        {
            if (via == c || loose) return true;
            int half = cost / 2;
            if (land != via && (burn[land] & Bits(t, t + cost)) != 0) return false;   // a portal can come sooner than planned
            return (burn[c] & Bits(t, t + half)) == 0 && (burn[via] & Bits(t + half, t + cost)) == 0;
        }

        /// From the end of the timeline back to now: from which moments can the bot stay alive?
        void Back(int avoid = -1)
        {
            Array.Clear(alive, 0, alive.Length);
            for (int t = Slots - 1; t >= 0; t--)
            {
                ulong bit = 1UL << t;
                for (int i = 0; i < nodeCount; i++)
                {
                    int n = nodes[i], c = n / St;
                    if ((burn[c] & bit) != 0) continue;
                    if (t == Slots - 1) { alive[n] |= bit; continue; }
                    for (int e = eFirst[n]; e < eLast[n]; e++)
                    {
                        int via = eVia[e];
                        if ((via == avoid || via == underfoot) && via != c) continue;
                        int nt = t + eCost[e];
                        if (nt < Slots && (alive[eTo[e]] >> nt & 1) == 0) continue;
                        if (!Pass(c, via, t, eCost[e], eTo[e] / St)) continue;
                        alive[n] |= bit;
                        break;
                    }
                }
            }
        }

        /// From now on: which of those moments can it get to, and when does it first reach each cell?
        void Forward(bool mustLive, int avoid = -1)
        {
            Array.Clear(seen, 0, seen.Length);
            for (int i = 0; i < Cells; i++) arrive[i] = -1;
            seen[startNode] = 1;
            parent[startNode * Slots] = -1;
            arrive[startNode / St] = 0; arriveNode[startNode / St] = startNode;
            for (int t = 0; t < Slots - 1; t++)
            {
                ulong bit = 1UL << t;
                for (int i = 0; i < nodeCount; i++)
                {
                    int n = nodes[i];
                    if ((seen[n] & bit) == 0) continue;
                    int c = n / St;
                    for (int e = eFirst[n]; e < eLast[n]; e++)
                    {
                        int to = eTo[e], nt = t + eCost[e], via = eVia[e];
                        if (nt >= Slots || (seen[to] >> nt & 1) != 0) continue;
                        if ((via == avoid || via == underfoot) && via != c) continue;
                        if (mustLive ? (alive[to] >> nt & 1) == 0 : (burn[to / St] >> nt & 1) != 0) continue;
                        if (!Pass(c, via, t, eCost[e], to / St)) continue;
                        seen[to] |= 1UL << nt;
                        parent[to * Slots + nt] = e;
                        int tc = to / St;
                        if (arrive[tc] < 0) { arrive[tc] = nt; arriveNode[tc] = to; }
                    }
                }
            }
        }

        readonly List<int> trail = new List<int>();

        void Follow(int node, int t)
        {
            trail.Clear();
            int n = node;
            while (t > 0)
            {
                int e = parent[n * Slots + t];
                if (e < 0) break;
                trail.Add(e);
                t -= eCost[e]; n = eFrom[e];
            }
            segs.Clear(); seg = 0;
            float at = s.time;
            for (int i = trail.Count - 1; i >= 0; i--)
            {
                int e = trail[i];
                at += eCost[e] * Slot;
                int press = ePress[e], land = eTo[e] / St;
                if (press == Rest)
                {
                    if (segs.Count > 0 && segs[segs.Count - 1].press == Rest) { var g = segs[segs.Count - 1]; g.until = at; segs[segs.Count - 1] = g; }
                    else segs.Add(new Seg { press = Rest, until = at });
                }
                else segs.Add(new Seg { press = press, land = land, until = land == eFrom[e] / St ? -1 : at });
            }
        }

        void GoTo(int cell) { Follow(arriveNode[cell], arrive[cell]); }

        void Stay() { segs.Clear(); seg = 0; }

        bool ClearFrom(int c, int t) => t < Slots && burn[c] >> t == 0;

        /// The nearest place that stays quiet to the end of the timeline; on ice only a place where the bot can come to rest.
        bool Refuge(out int cell)
        {
            int best = int.MaxValue; cell = -1;
            for (int c = 0; c < Cells; c++)
            {
                int t = arrive[c];
                if (t < 0 || !ClearFrom(c, t)) continue;
                int score = t * 4 + Risk(c) + (int)(tie[c] * 30);
                if (score < best) { best = score; cell = c; }
            }
            return cell >= 0;
        }

        /// Prefers places with room around them, and in a battle, away from the falling walls.
        int Risk(int c)
        {
            int x = c % W, y = c / W, ways = 0, r = 0;
            for (int d = 0; d < 4; d++) if (Can(x, y, d)) ways++;
            if (ways <= 1) r += 6;
            // A rival nearby can shut a corridor with one bomb; a crossing has more ways out
            if (skill.smart && s.mode == Mode.Battle && ways <= 2)
                foreach (var p in s.players)
                    if (p != me && p.alive && Math.Abs(p.Cx - x) + Math.Abs(p.Cy - y) <= 5) r += 12;
            var f = s.floor[x, y];
            if (f == F.Ice || f == F.Crumble) r += 4;
            if (Sim.BeltDir(f) != (0, 0)) r += 60;
            if (Closing) r += (s.shrinkOrder.Count - shrinkRank[c]) / 2;
            return r;
        }

        void Settle()
        {
            if (Refuge(out int c)) { if (c == me.Cy * W + me.Cx && startNode % St == Rest) Hold(); else GoTo(c); return; }
            int best = -1, bx = -1;
            for (int i = 0; i < Cells; i++)
            {
                if (arrive[i] < 0) continue;
                int life = FirstBurn(i, arrive[i]);
                if (life > best) { best = life; bx = i; }
            }
            if (bx >= 0) GoTo(bx); else Stay();
        }

        /// Stay here, on purpose, as long as the timeline allows.
        void Hold() { Stay(); }

        int FirstBurn(int c, int from)
        {
            for (int i = from; i < Slots; i++) if ((burn[c] >> i & 1) != 0) return i;
            return Slots;
        }

        /// Would dropping a bomb in this cell, at this moment, leave a way out?
        bool SafeDrop(int cell, int node, int t)
        {
            Array.Copy(burn, savedBurn, Cells);
            Array.Copy(alive, savedAlive, alive.Length);
            float was = look;
            if (pressT < 2.5f) look = Math.Max(look, 1.2f) * 1.5f;      // the way out must not depend on enemies keeping away
            Foresee(cell, Sim.Fuse + t * Slot);
            look = was;
            Back(cell);
            bool ok = (alive[node] >> t & 1) != 0;
            Array.Copy(savedBurn, burn, Cells);
            Array.Copy(savedAlive, alive, alive.Length);
            return ok;
        }

        /// Is there any cell the hero can walk to from the start where a first bomb leaves a way out?
        public static bool FirstBombSafe(Sim s)
        {
            var b = new Brain(s, s.Hero, Skill.Hard, 1);
            return b.AnyBombSpot();
        }

        /// The same question for someone who has just come through a one-way door.
        public static bool BombSpotFrom(Sim s, int x, int y)
        {
            var b = new Brain(s, s.Hero, Skill.Hard, 1) { startAt = y * W + x };
            return b.AnyBombSpot();
        }

        bool AnyBombSpot()
        {
            blind = true;
            Foresee(); Build(); Back();
            var reach = Solver.Flood(s, startAt < 0 ? me.Cx : startAt % W, startAt < 0 ? me.Cy : startAt / W, false);
            for (int y = 1; y < H - 1; y++)
                for (int x = 1; x < W - 1; x++)
                {
                    if (!reach[x, y] || !CanDropAt(x, y)) continue;
                    int cell = y * W + x;
                    Foresee(cell, Sim.Fuse);
                    Back(cell);
                    if ((alive[cell * St + Rest] & 1) != 0) return true;
                }
            return false;
        }

        bool CanDropAt(int x, int y) =>
            !s.Solid(s.tile[x, y]) && s.floor[x, y] != F.Tele && s.floor[x, y] != F.Pit && s.floor[x, y] != F.Road && s.BombAt(x, y) == null;

        // ---------- deciding ----------

        void Look()
        {
            foes.Clear();
            foreach (var e in s.enemies)
            {
                if (e.dead || e.dormant) continue;
                if (s.def != null && s.def.dark && Math.Abs(e.x - me.x) + Math.Abs(e.y - me.y) > DarkSight && !Lit(e.Cx, e.Cy)) continue;
                foes.Add(e);
            }
        }

        /// In the dark flames and bombs light up what stands next to them.
        bool Lit(int x, int y)
        {
            foreach (var b in s.bombs) if (Math.Abs(b.x - x) + Math.Abs(b.y - y) <= 3) return true;
            for (int j = -2; j <= 2; j++) for (int i = -2; i <= 2; i++) if (Sim.In(x + i, y + j) && s.flame[x + i, y + j] > 0) return true;
            return false;
        }

        void Plan()
        {
            Look();
            look = 1.2f; pad = s.mode == Mode.Battle ? .35f : .2f; loose = false;
            Foresee(); Build(); Back();
            if ((alive[startNode] & 1) == 0) { look = .6f; pad = .15f; Foresee(); Back(); }
            if ((alive[startNode] & 1) == 0) { look = .25f; pad = .05f; Foresee(); Back(); }
            int cx = me.Cx, cy = me.Cy, cell = cy * W + cx;
            if (s.tile[cx, cy] == T.Sprout) burnt[cell] = true;
            for (int c = 0; c < Cells; c++) if (s.tile[c % W, c / W] == T.Sprout) burnt[c] = true;

            bool safe = (alive[startNode] & 1) != 0;
            loose = !safe;
            Forward(safe);
            if (Debug != null) Debug($"t={s.time:F2} at {cx},{cy} ({me.x:F2},{me.y:F2}) safe={safe} goal {goalCell % W},{goalCell / W} bomb={goalBomb} segs={segs.Count}/{seg} look={look} n={nodeCount} e={edgeCount} burn={Convert.ToString((long)burn[cell], 2).PadLeft(64, '0')}");

            if (!safe)
            {
                // Nothing is sure. Run for whatever lasts the longest, and leave a bomb behind if it can still help
                Cornered(cell);
                return;
            }

            // Pressed by an enemy: no errands. Leave a bomb if that is safe, and get away
            pressT = look < 1.2f ? pressT + skill.think : Math.Max(0, pressT - skill.think * .5f);
            if (wantBomb) pressT = 0;
            if (look < 1.2f && pressT < 2.5f)
            {
                bool drop = s.Active(me) < me.bombsMax && s.CanDrop(me) && CanDropAt(cx, cy) && SafeDrop(cell, startNode, 0);
                if (drop)
                {
                    wantBomb = true;
                    Foresee(cell, Sim.Fuse); Back(cell); Forward(true, cell);
                }
                Settle();
                goalCell = -1;
                lastCell = cell;
                return;
            }

            stuckT = cell == lastCell ? stuckT + skill.think : 0;
            lastCell = cell;
            if (stuckT > 1.6f && goalCell >= 0 && goalCell != cell) { tabu[goalCell] = s.time + 4; stuckT = 0; goalCell = -1; }

            if (skill.mistake > 0 && rng.Float() < skill.mistake) { Stray(cx, cy); return; }
            if (skill.kicks && me.kick && TryKick(cx, cy)) return;

            // The way out, when there is one, beats everything
            if (s.mode == Mode.Story && s.exitOpen && s.exitX >= 0 && s.tile[s.exitX, s.exitY] == T.Empty && s.boss == null)
            {
                int door = s.exitY * W + s.exitX;
                if (arrive[door] >= 0) { goalCell = door; goalBomb = false; GoTo(door); return; }
            }

            // Danger on the way here: leaving comes first, and early, while there is still room for surprises
            if ((fire[cell] & Bits(0, 25)) != 0) { Settle(); return; }

            bool canBomb = s.Active(me) < Math.Min(me.bombsMax, skill.cap);
            Heat(); Gates(); Distances();
            huntHere = soft[cell] < 0 ? 999 : soft[cell];

            // The walls are coming here: get to the middle before anything else, if the way is open
            if (FallsSoon(cell, s.shrinkOrder.Count - s.shrinkAt < 40 ? 2.5f : 13))
            {
                int best = huntHere, go = -1;
                for (int c = 0; c < Cells; c++)
                    if (arrive[c] >= 0 && soft[c] >= 0 && soft[c] < best) { best = soft[c]; go = c; }
                if (go >= 0) { goalCell = -1; GoTo(go); return; }
            }

            float bestScore = 0; int goal = -1; bool bombGoal = false;
            for (int i = 0; i < 4; i++) top[i] = (-1, 0);
            for (int c = 0; c < Cells; c++)
            {
                if (arrive[c] < 0 || (fire[c] & Bits(arrive[c], arrive[c] + 26)) != 0 || FallsSoon(c, s.shrinkOrder.Count - s.shrinkAt < 40 ? 2.5f : 10)) continue;
                int x = c % W, y = c / W;
                float dist = 1 + arrive[c] * Slot * me.Speed;
                float v = CellValue(x, y);
                if (v > 0 && !Tabu(c))
                {
                    float sc = v / dist * (c == goalCell && !goalBomb ? 1.3f : 1 + tie[c]);
                    if (sc > bestScore) { bestScore = sc; goal = c; bombGoal = false; }
                }
                if (CanDropAt(x, y) && !Tabu(c) && (canBomb || Sim.BeltDir(s.floor[x, y]) == (0, 0)))
                {
                    float b = BombValue(x, y) * skill.aim;
                    if (b > 0) Keep(c, b / dist * (c == goalCell && goalBomb ? 1.3f : 1 + tie[c]));
                }
            }
            for (int i = 0; i < 4; i++)
            {
                var (c, sc) = top[i];
                if (c < 0 || sc <= bestScore) break;
                // With every bomb out it still walks to the next spot, to be ready there
                if (!canBomb) { bestScore = sc; goal = c; bombGoal = false; break; }
                if (SafeDrop(c, arriveNode[c], arrive[c])) { bestScore = sc; goal = c; bombGoal = true; break; }
                if (c == cell) tabu[c] = s.time + .6f;
            }

            if (goal < 0) { goalCell = -1; Roam(cell); return; }
            goalCell = goal; goalBomb = bombGoal;
            if (goal == cell)
            {
                if (bombGoal && s.CanDrop(me))
                {
                    wantBomb = true; pressT = 0;
                    Foresee(cell, Sim.Fuse); Back(cell); Forward(true, cell);
                    Settle();
                    goalCell = -1;
                    return;
                }
                if (Sim.BeltDir(s.floor[cx, cy]) != (0, 0)) Settle(); else Stay();
                return;
            }
            GoTo(goal);
        }

        readonly (int c, float v)[] top = new (int, float)[4];

        void Keep(int c, float v)
        {
            for (int i = 0; i < top.Length; i++)
                if (top[i].c < 0 || v > top[i].v)
                {
                    for (int j = top.Length - 1; j > i; j--) top[j] = top[j - 1];
                    top[i] = (c, v);
                    return;
                }
        }

        void Cornered(int cell)
        {
            if (s.Active(me) < me.bombsMax && s.CanDrop(me) && (fire[cell] & Bits(0, 34)) == 0)
                foreach (var e in foes)
                    if (Math.Abs(e.x - me.x) + Math.Abs(e.y - me.y) <= 3 && Lined(me.Cx, me.Cy, e.Cx, e.Cy)) { wantBomb = true; break; }
            Settle();
        }

        bool Lined(int x, int y, int tx, int ty) => Reaches(x, y, me.flame, tx, ty);

        bool Tabu(int c) => tabu.TryGetValue(c, out float until) && until > s.time;

        void Stray(int cx, int cy)
        {
            var d = Dirs[rng.Int(4)];
            int nx = cx + d.x, ny = cy + d.y;
            if (Sim.In(nx, ny) && arrive[ny * W + nx] >= 0) GoTo(ny * W + nx); else Settle();
        }

        /// Nothing worth doing in reach: get closer to something that is.
        void Roam(int cell)
        {
            int best = int.MaxValue, go = -1;
            for (int c = 0; c < Cells; c++)
            {
                if (arrive[c] < 0 || !ClearFrom(c, arrive[c])) continue;
                int d = soft[c] < 0 ? 200 : soft[c];
                int score = d * 8 + arrive[c] + Risk(c) + rng.Int(3);
                if (score < best) { best = score; go = c; }
            }
            if (go >= 0 && go != cell) GoTo(go); else Settle();
        }

        /// Steps from each cell to the nearest thing to hunt, walking through blocks and everything a bomb can open.
        void Distances()
        {
            int head = 0, tail = 0;
            for (int i = 0; i < Cells; i++) soft[i] = -1;
            bool closing = Closing;
            if (closing)
            {
                for (int i = Math.Max(s.shrinkAt, s.shrinkOrder.Count - 14); i < s.shrinkOrder.Count; i++)
                {
                    var (mx, my) = s.shrinkOrder[i];
                    if (soft[my * W + mx] < 0) { soft[my * W + mx] = 0; bfs[tail++] = my * W + mx; }
                }
            }
            else if (flipGoal >= 0) { soft[flipGoal] = 0; bfs[tail++] = flipGoal; }
            else foreach (var e in foes) { int ec = e.Cy * W + e.Cx; if (soft[ec] < 0) { soft[ec] = 0; bfs[tail++] = ec; } }
            var o = s.boss;
            if (o != null && !o.dead) { int c = Sim.R(o.y) * W + Sim.R(o.x); soft[c] = 0; bfs[tail++] = c; }
            if (!closing) foreach (var p in s.players) if (p != me && p.alive) { int c = p.Cy * W + p.Cx; if (soft[c] < 0) { soft[c] = 0; bfs[tail++] = c; } }
            if (s.mode == Mode.Story && tail == 0)
                for (int c = 0; c < Cells; c++)
                {
                    var t = s.tile[c % W, c / W];
                    if ((t == T.Block || t == T.Hedge && !burnt[c]) && !s.exitSeen || s.exitSeen && c == s.exitY * W + s.exitX) { soft[c] = 0; bfs[tail++] = c; }
                }
            if (tail == 0 && s.mode == Mode.Story)
                for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++) if (x % 4 == 1 && y % 4 == 1 && open[y * W + x]) { soft[y * W + x] = 0; bfs[tail++] = y * W + x; }
            while (head < tail)
            {
                int c = bfs[head++], x = c % W, y = c / W;
                foreach (var (dx, dy) in Dirs)
                {
                    int nx = x + dx, ny = y + dy, n = ny * W + nx;
                    if (!Sim.In(nx, ny) || soft[n] >= 0) continue;
                    var t = s.tile[nx, ny];
                    if (t == T.Edge || t == T.Wall || t == T.Crack || t == T.Shrunk || t == T.GateRed || t == T.GateBlue) continue;
                    if (s.OneWayStops(nx, ny, x, y)) continue;
                    soft[n] = soft[c] + 1; bfs[tail++] = n;
                }
            }
        }

        float CellValue(int x, int y)
        {
            var it = s.item[x, y];
            float v = 0;
            if (it == Item.Secret) v = 6;
            else if (it != Item.None)
            {
                v = it switch
                {
                    Item.Kick => me.kick ? 1 : 8, Item.Shield => me.shield ? 1 : 12, Item.Speed => me.speedLv >= 3 ? 1 : 8,
                    Item.Bomb => me.bombsMax >= 5 ? 2 : 10, _ => me.flame >= 6 ? 2 : 10,
                };
            }
            if (s.mode == Mode.Story && s.floor[x, y] == F.SecretTile && s.boss == null) v += 2;
            return v * skill.aim;
        }

        /// How likely each cell is to hold a target when a bomb dropped now goes off.
        /// In a battle: the walls are about to come, or coming.
        bool Closing => s.mode == Mode.Battle && s.time > Sim.ShrinkAt - 30;

        /// Falls within this many seconds?
        bool FallsSoon(int c, float seconds)
        {
            if (!Closing) return false;
            int left = shrinkRank[c] - 1 - s.shrinkAt;
            return Math.Max(0, Sim.ShrinkAt - s.time) + left * .4f < seconds;
        }

        void Heat()
        {
            Array.Clear(heat, 0, Cells);
            foreach (var p in s.players)
            {
                if (p == me || !p.alive) continue;
                for (int d = 0; d < 4; d++)
                    for (int i = 0; i <= 2; i++)
                    {
                        int x = p.Cx + Dirs[d].x * i, y = p.Cy + Dirs[d].y * i;
                        if (Sim.In(x, y) && !s.Solid(s.tile[x, y])) heat[y * W + x] += s.time > Sim.ShrinkAt ? 1.5f : .3f;
                    }
            }
            foreach (var e in foes)
            {
                bool chaser = e.kind == EnemyKind.Fox || e.kind == EnemyKind.Mimic || e.kind == EnemyKind.Wasp;
                int depth = chaser ? 7 : Math.Min(4, (int)(e.speed * 2) + 1);
                int head = 0, tail = 0;
                for (int i = 0; i < Cells; i++) soft[i] = -1;
                int c0 = e.ty * W + e.tx;
                soft[c0] = 0; bfs[tail++] = c0;
                while (head < tail)
                {
                    int c = bfs[head++], x = c % W, y = c / W;
                    if (soft[c] >= depth) continue;
                    foreach (var (dx, dy) in Dirs)
                    {
                        int nx = x + dx, ny = y + dy, n = ny * W + nx;
                        if (!Sim.In(nx, ny) || soft[n] >= 0) continue;
                        var t = s.tile[nx, ny];
                        if (t == T.Edge || t == T.Wall || t == T.Crack || t == T.Shrunk || t == T.GateRed || t == T.GateBlue) continue;
                        if (!Sim.Flies(e.kind) && (s.Solid(t) || s.floor[nx, ny] == F.Road)) continue;
                        soft[n] = soft[c] + 1; bfs[tail++] = n;
                    }
                }
                int near = tail;
                if (chaser) { near = 0; while (near < tail && soft[bfs[near]] <= 2) near++; }
                float share = (chaser ? 22f : 9f) / near;
                for (int i = 0; i < near; i++) heat[bfs[i]] += share;
                // Hunters come to the bot: the bomb that waits for them is the one at its feet
                if (chaser && soft[me.Cy * W + me.Cx] >= 0) heat[me.Cy * W + me.Cx] += 4;
            }
        }

        bool Closed(int x, int y, bool redUp)
        {
            var t = s.tile[x, y];
            if (t == T.Edge || t == T.Wall || t == T.Crack || t == T.Shrunk) return true;
            int g = s.gateCells[x, y];
            return g != 0 && (g == 1) == redUp;
        }

        bool SwitchInLine(int x, int y, bool redUp)
        {
            if (s.floor[x, y] == F.Switch) return true;
            foreach (var (dx, dy) in Dirs)
                for (int i = 1; i <= me.flame; i++)
                {
                    int cx = x + dx * i, cy = y + dy * i;
                    if (!Sim.In(cx, cy) || Closed(cx, cy, redUp)) break;
                    if (s.floor[cx, cy] == F.Switch) return true;
                }
            return false;
        }

        bool Work(int c)
        {
            if (s.enemies.Count > 0)
            {
                foreach (var e in s.enemies) if (!e.dead && e.Cy * W + e.Cx == c) return true;
                return false;
            }
            if (s.exitSeen) return c == s.exitY * W + s.exitX;
            return s.tile[c % W, c / W] == T.Block;
        }

        readonly bool[] flipZone = new bool[Cells];
        readonly int[] gateQueue = new int[2 * Cells], flipAt = new int[2 * Cells];

        /// Looks for the nearest job over both gate positions. If the way there needs a flip, it also tells where
        /// to stand for it: flipping is only right from the side of the gate the job will be connected to.
        void Gates()
        {
            needFlip = false; flipGoal = -1;
            bool any = false;
            for (int c = 0; c < Cells && !any; c++) any = s.floor[c % W, c / W] == F.Switch;
            if (!any || s.mode != Mode.Story) return;
            // A flame already on its way to a switch does the job; a second one would undo it
            foreach (var b in s.bombs)
                if (!b.dead)
                    for (int c = 0; c < Cells; c++)
                        if (s.floor[c % W, c / W] == F.Switch && Reaches(b.Cx, b.Cy, b.range + 1, c % W, c / W)) return;

            for (int i = 0; i < 2 * Cells; i++) flipAt[i] = -2;
            int head = 0, tail = 0, here = me.Cy * W + me.Cx, half = s.gateRedUp ? Cells : 0;
            gateQueue[tail++] = here + half; flipAt[here + half] = -1;
            int flip = -1;
            while (head < tail)
            {
                int n = gateQueue[head++], off = n >= Cells ? Cells : 0, c = n - off, x = c % W, y = c / W;
                bool red = off != 0;
                if (Work(c)) { flip = flipAt[n]; break; }
                if (s.floor[x, y] == F.Tele) Visit(s.tele[x, y] + off, flipAt[n], ref tail);
                foreach (var (dx, dy) in Dirs)
                {
                    int nx = x + dx, ny = y + dy;
                    if (Sim.In(nx, ny) && !Closed(nx, ny, red) && !s.OneWayStops(x, y, nx, ny)) Visit(ny * W + nx + off, flipAt[n], ref tail);
                }
                if (s.floor[x, y] != F.Tele && !s.IsGate(x, y) && SwitchInLine(x, y, red))
                    Visit(c + Cells - off, flipAt[n] >= 0 ? flipAt[n] : c, ref tail);
            }
            if (flip < 0) return;

            // After the flip, is the bot on the same side as that spot?
            bool after = !s.gateRedUp;
            for (int i = 0; i < Cells; i++) soft[i] = -1;
            head = 0; tail = 0;
            soft[flip] = 0; bfs[tail++] = flip;
            while (head < tail)
            {
                int c = bfs[head++], x = c % W, y = c / W;
                if (s.floor[x, y] == F.Tele && soft[s.tele[x, y]] < 0) { soft[s.tele[x, y]] = 0; bfs[tail++] = s.tele[x, y]; }
                foreach (var (dx, dy) in Dirs)
                {
                    int nx = x + dx, ny = y + dy, n = ny * W + nx;
                    if (!Sim.In(nx, ny) || soft[n] >= 0 || Closed(nx, ny, after)) continue;
                    soft[n] = 0; bfs[tail++] = n;
                }
            }
            needFlip = soft[here] >= 0; flipGoal = flip;
            for (int i = 0; i < Cells; i++) flipZone[i] = soft[i] >= 0;
        }

        void Visit(int n, int flip, ref int tail)
        {
            if (flipAt[n] != -2) return;
            flipAt[n] = flip; gateQueue[tail++] = n;
        }

        /// What a bomb dropped here would achieve.
        float BombValue(int x, int y)
        {
            float v = heat[y * W + x] + Boss(x, y) + Rival(x, y);
            if (skill.traps && s.mode == Mode.Battle) v += Trap(x, y);
            int range = me.flame;
            bool story = s.mode == Mode.Story;
            bool flip = needFlip && flipZone[y * W + x];
            if (s.floor[x, y] == F.Switch) v += flip ? 30 : -1;
            foreach (var (dx, dy) in Dirs)
                for (int i = 1; i <= range; i++)
                {
                    int cx = x + dx * i, cy = y + dy * i, c = cy * W + cx;
                    if (!Sim.In(cx, cy)) break;
                    var t = s.tile[cx, cy];
                    if (t == T.Edge || t == T.Wall || t == T.Shrunk || t == T.GateRed || t == T.GateBlue || t == T.Burning) break;
                    if (t == T.Crack) { v += .8f; break; }
                    if (t == T.Block || t == T.Hedge)
                    {
                        if (s.DormantAt(cx, cy) == null)
                        {
                            v += !burnt[c] && story && !s.exitSeen ? 1.3f : !story ? 1 : .15f;
                            if (soft[c] >= 0 && soft[c] < huntHere) v += 1.2f;     // opens the way to what it hunts
                        }
                        break;
                    }
                    if (t == T.Keg) { v += 1; break; }
                    if (s.DormantAt(cx, cy) != null) { v += 4; break; }
                    if (s.floor[cx, cy] == F.Switch) v += flip ? 30 : -1;
                    if (s.BombAt(cx, cy) != null) break;
                    var it = s.item[cx, cy];
                    if (it != Item.None && it != Item.Secret) { v -= 5; break; }
                    v += heat[c] + Boss(cx, cy) + Rival(cx, cy);
                }
            return v;
        }

        float Boss(int x, int y)
        {
            var b = s.boss;
            if (b == null || b.dead || b.hidden || b.air > 0) return 0;
            float dx = Math.Abs(b.x - x), dy = Math.Abs(b.y - y);
            return dx <= b.half + .4f && dy <= b.half + .4f ? 5 : dx <= b.half + 1.6f && dy <= b.half + 1.6f ? 2 : 0;
        }

        float Rival(int x, int y)
        {
            if (s.mode != Mode.Battle) return 0;
            float v = 0;
            foreach (var p in s.players)
                if (p != me && p.alive && p.Cx == x && p.Cy == y) v += skill.smart ? 5 : 2.5f;
            return v;
        }

        readonly int[] stamp = new int[Cells];
        int stampMark;

        /// Hard bots ask what a bomb here leaves a rival: if it can reach no quiet cell, the bomb is a trap.
        float Trap(int x, int y)
        {
            float v = 0;
            foreach (var p in s.players)
            {
                if (p == me || !p.alive || Math.Abs(p.Cx - x) + Math.Abs(p.Cy - y) > 5) continue;
                stampMark++;
                stamp[y * W + x] = stampMark;
                foreach (var (dx, dy) in Dirs)
                    for (int i = 1; i <= me.flame; i++)
                    {
                        int cx = x + dx * i, cy = y + dy * i;
                        if (!Sim.In(cx, cy) || s.Solid(s.tile[cx, cy])) break;
                        stamp[cy * W + cx] = stampMark;
                    }
                int quiet = 0, head = 0, tail = 0, start = p.Cy * W + p.Cx;
                for (int i = 0; i < Cells; i++) soft2[i] = -1;
                soft2[start] = 0; trapQ[tail++] = start;
                while (head < tail && quiet < 3)
                {
                    int c = trapQ[head++], cx = c % W, cy = c / W;
                    if (stamp[c] != stampMark && fire[c] == 0 && !FallsSoon(c, 4.5f)) quiet++;
                    if (soft2[c] >= 9) continue;
                    foreach (var (dx, dy) in Dirs)
                    {
                        int nx = cx + dx, ny = cy + dy, n = ny * W + nx;
                        if (!Sim.In(nx, ny) || soft2[n] >= 0 || n == y * W + x || !s.Open(p, cx, cy, nx, ny)) continue;
                        soft2[n] = soft2[c] + 1; trapQ[tail++] = n;
                    }
                }
                v += quiet == 0 ? 30 : quiet == 1 ? 8 : quiet == 2 ? 2 : 0;
            }
            return v;
        }

        readonly int[] soft2 = new int[Cells], trapQ = new int[Cells];

        bool TryKick(int cx, int cy)
        {
            for (int d = 0; d < 4; d++)
            {
                int dx = Dirs[d].x, dy = Dirs[d].y;
                var b = Sim.In(cx + dx, cy + dy) ? s.BombAt(cx + dx, cy + dy) : null;
                if (b == null || b.Sliding) continue;
                foreach (var p in s.players)
                {
                    if (p == me || !p.alive) continue;
                    bool inLine = dx != 0 ? p.Cy == cy && (p.Cx - cx) * dx > 1 : p.Cx == cx && (p.Cy - cy) * dy > 1;
                    if (!inLine) continue;
                    segs.Clear(); seg = 0;
                    segs.Add(new Seg { press = d, land = (cy + dy) * W + cx + dx, until = s.time + .2f });
                    return true;
                }
            }
            return false;
        }
    }
}
