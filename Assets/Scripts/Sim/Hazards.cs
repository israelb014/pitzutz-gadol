using System;
using System.Collections.Generic;

namespace Pitzutz
{
    /// Things of the stage that keep moving on their own. Everything here is on a clock, so bots can read the future.
    public partial class Sim
    {
        void Hazards()
        {
            if (Windy) WindClock();
            Traffic();
            Walls();
            Fire();
            Falling();
            Flying();
        }

        void WindClock()
        {
            windT -= Dt;
            if (windT > 0) return;
            windState = (windState + 1) % 3;
            if (windState == 0) { windT = 4.5f; windX = windY = 0; }
            else if (windState == 1)
            {
                windT = 1.3f;
                var d = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }[rng.Int(4)];
                nextWindX = d.Item1; nextWindY = d.Item2;
                Emit(Ev.Wind);
            }
            else { windT = 2.6f; windX = nextWindX; windY = nextWindY; }
        }

        // ---- cars ----

        const float CarStart = -2.5f, CarRoad = W + 5f;

        void Traffic()
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                var l = lanes[i];
                float before = (time - Dt + l.phase) / l.period, now = (time + l.phase) / l.period;
                if (Math.Floor(before) != Math.Floor(now))
                {
                    cars.Add(new Car { row = l.row, dir = l.dir, speed = l.speed, x = l.dir > 0 ? CarStart : W - 1 - CarStart, tint = (int)(Math.Floor(now) % 3) });
                    Emit(Ev.Car);
                }
            }
            for (int i = cars.Count - 1; i >= 0; i--)
            {
                var c = cars[i];
                c.x += c.dir * c.speed * Dt;
                if (c.x < CarStart - .1f || c.x > W - 1 - CarStart + .1f) { cars.RemoveAt(i); continue; }
                var b = BombAt(R(c.x + c.dir), c.row);
                if (b != null) Detonate(b);
            }
        }

        /// Will a car be over this cell at this time?
        public bool CarOver(int x, int y, float at)
        {
            foreach (var l in lanes)
            {
                if (l.row != y) continue;
                float u = (at + l.phase) / l.period;
                for (double k = Math.Floor(u); k > Math.Floor(u) - 8; k--)
                {
                    float age = (float)(u - k) * l.period;
                    float cx = (l.dir > 0 ? CarStart : W - 1 - CarStart) + l.dir * l.speed * age;
                    if (Math.Abs(cx - x) < 1.6f) return true;
                }
            }
            return false;
        }

        // ---- moving walls ----

        void Walls()
        {
            foreach (var m in movers)
            {
                if (!m.moving)
                {
                    m.wait -= Dt;
                    if (m.wait > 0) continue;
                    int n = m.at + m.step;
                    if (n < 0 || n >= m.cellsX.Length) { m.step = -m.step; n = m.at + m.step; }
                    int nx = m.cellsX[n], ny = m.cellsY[n];
                    if (BombAt(nx, ny) != null || EnemyIn(nx, ny) || Solid(tile[nx, ny])) { m.step = -m.step; m.wait = .4f; continue; }
                    m.nextX = nx; m.nextY = ny; m.moving = true;
                    item[nx, ny] = Item.None;
                    continue;
                }
                float dx = Math.Sign(m.nextX - m.x), dy = Math.Sign(m.nextY - m.y), step = m.speed * Dt;
                m.x += dx * step; m.y += dy * step;
                foreach (var p in players)
                {
                    if (!p.alive || Math.Abs(p.x - m.x) >= .9f || Math.Abs(p.y - m.y) >= .9f) continue;
                    if ((p.x - m.x) * dx + (p.y - m.y) * dy < -.1f) continue;   // the wall is moving away
                    Go(p, (int)dx, (int)dy, step, false);
                    if (Math.Abs(p.x - m.x) < .55f && Math.Abs(p.y - m.y) < .55f) Hit(p);
                }
                if ((m.nextX - m.x) * dx + (m.nextY - m.y) * dy <= 0)
                {
                    m.x = m.nextX; m.y = m.nextY; m.at += m.step; m.moving = false; m.wait = m.pause;
                }
            }
        }

        bool EnemyIn(int x, int y)
        {
            foreach (var e in enemies) if (!e.dead && (e.Cx == x && e.Cy == y || e.tx == x && e.ty == y)) return true;
            return false;
        }

        // ---- fire jets ----

        public (float start, float end) JetNext(Jet j, float at)
        {
            float cycle = (at + j.phase) % j.period, begin = j.period - j.fire;
            float wait = cycle < begin ? begin - cycle : begin - cycle + j.period;
            if (cycle >= begin) return (0, j.period - cycle);
            return (wait, wait + j.fire);
        }

        public bool JetWarning(Jet j)
        {
            float cycle = (time + j.phase) % j.period;
            return cycle >= j.period - j.fire - j.warn;
        }

        void Fire()
        {
            foreach (var j in jets)
            {
                float cycle = (time + j.phase) % j.period;
                bool on = cycle >= j.period - j.fire;
                if (on && !j.lit) { Emit(Ev.Jet, j.x, j.y); foreach (var (x, y) in JetLine(j)) LightJet(x, y, j.dx != 0 ? FH : FV, j.fire); }
                j.lit = on;
            }
        }

        public IEnumerable<(int x, int y)> JetLine(Jet j)
        {
            yield return (j.x, j.y);
            for (int i = 1; i <= 6; i++)
            {
                int x = j.x + j.dx * i, y = j.y + j.dy * i;
                if (!In(x, y) || Solid(tile[x, y])) yield break;
                yield return (x, y);
            }
        }

        void LightJet(int x, int y, byte bits, float life)
        {
            if (flame[x, y] > 0 && !jetFlame[x, y]) return;
            flame[x, y] = life; flameMask[x, y] |= bits; jetFlame[x, y] = true;
            var b = BombAt(x, y);
            if (b != null) Detonate(b);
        }

        // ---- things thrown by bosses ----

        void Falling()
        {
            for (int i = strikes.Count - 1; i >= 0; i--)
            {
                var s = strikes[i];
                s.delay -= Dt;
                if (s.delay > 0) continue;
                strikes.RemoveAt(i);
                if (s.kind < 0 || !In(s.x, s.y)) continue;
                var t = tile[s.x, s.y];
                if (Solid(t)) continue;
                LightJet(s.x, s.y, FC, s.life);
                if (s.makesBlock && t == T.Empty && item[s.x, s.y] == Item.None && BombAt(s.x, s.y) == null && timer[s.x, s.y] <= 0)
                {
                    if (RubbleFits(s.x, s.y))
                    {
                        tile[s.x, s.y] = T.Block;
                        int roll = rng.Int(10);
                        hidden[s.x, s.y] = roll < 2 ? Item.Bomb : roll < 4 ? Item.Flame : Item.None;
                    }
                }
            }
        }

        /// Rubble stays off the outer ring and off everybody's feet, and must leave everybody plenty of room to walk.
        bool RubbleFits(int x, int y)
        {
            if (x <= 1 || y <= 1 || x >= W - 2 || y >= H - 2) return false;
            int count = 0;
            for (int j = 1; j < H - 1; j++) for (int i = 1; i < W - 1; i++) if (tile[i, j] == T.Block) count++;
            if (count >= 12) return false;
            foreach (var p in players)
            {
                if (!p.alive) continue;
                if (p.Cx == x && p.Cy == y) return false;
                tile[x, y] = T.Block;
                int room = Room(p.Cx, p.Cy);
                tile[x, y] = T.Empty;
                if (room < 16) return false;
            }
            return true;
        }

        int Room(int sx, int sy)
        {
            seenMark++;
            int head = 0, tail = 0, start = sy * W + sx;
            queue[tail++] = start; seen[start] = seenMark;
            while (head < tail)
            {
                int c = queue[head++], cx = c % W, cy = c / W;
                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + Dirs[i].x, ny = cy + Dirs[i].y, n = ny * W + nx;
                    if (!In(nx, ny) || seen[n] == seenMark || Solid(tile[nx, ny])) continue;
                    seen[n] = seenMark; queue[tail++] = n;
                }
            }
            return tail;
        }

        void Flying()
        {
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                var s = shots[i];
                s.x += s.vx * Dt; s.y += s.vy * Dt; s.life -= Dt;
                int x = R(s.x), y = R(s.y);
                if (s.life <= 0 || !In(x, y) || Solid(tile[x, y]) && tile[x, y] != T.Burning) { shots.RemoveAt(i); continue; }
                var b = BombAt(x, y);
                if (b != null) { Detonate(b); shots.RemoveAt(i); }
            }
        }
    }
}
