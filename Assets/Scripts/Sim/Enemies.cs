using System;
using System.Collections.Generic;

namespace Pitzutz
{
    public partial class Sim
    {
        public static float SpeedOf(EnemyKind k) => k switch
        {
            EnemyKind.Snail => 1.2f, EnemyKind.Beetle => 2f, EnemyKind.Ghost => 1.5f, EnemyKind.Fox => 2.4f,
            EnemyKind.Frog => 1.9f, EnemyKind.Turtle => 1.5f, EnemyKind.Mimic => 2.5f, _ => 3.7f,
        };

        public static int ValueOf(EnemyKind k) => k switch
        {
            EnemyKind.Snail => 100, EnemyKind.Beetle => 200, EnemyKind.Ghost => 300, EnemyKind.Fox => 400,
            EnemyKind.Frog => 400, EnemyKind.Turtle => 600, EnemyKind.Mimic => 500, _ => 100,
        };

        public static bool Flies(EnemyKind k) => k == EnemyKind.Ghost || k == EnemyKind.Wasp;

        public Enemy AddEnemy(EnemyKind k, int x, int y)
        {
            var e = new Enemy { kind = k, x = x, y = y, tx = x, ty = y, speed = SpeedOf(k), hp = k == EnemyKind.Turtle ? 2 : 1, dormant = k == EnemyKind.Mimic, t = rng.Float() * 2 };
            enemies.Add(e);
            return e;
        }

        bool EnemyOpen(Enemy e, int fx, int fy, int x, int y)
        {
            if (!In(x, y) || MoverOn(x, y)) return false;
            var t = tile[x, y];
            bool fly = Flies(e.kind);
            if (t == T.Edge || t == T.Wall || t == T.Crack || t == T.Shrunk || t == T.GateRed || t == T.GateBlue) return false;
            if (!fly)
            {
                if (Solid(t) || floor[x, y] == F.Pit || floor[x, y] == F.Road || OneWayStops(fx, fy, x, y)) return false;
                if (BombAt(x, y) != null || DormantAt(x, y) != null) return false;
            }
            return true;
        }

        void StepEnemies()
        {
            for (int i = enemies.Count - 1; i >= 0; i--)
            {
                var e = enemies[i];
                e.t += Dt;
                if (e.invuln > 0) e.invuln -= Dt;
                if (flame[e.Cx, e.Cy] > 0 && !jetFlame[e.Cx, e.Cy] && e.invuln <= 0) Damage(e);
                if (e.dead) { enemies.RemoveAt(i); continue; }
                if (e.dormant)
                {
                    foreach (var p in players)
                        if (p.alive && Math.Abs(p.x - e.x) + Math.Abs(p.y - e.y) <= 2.1f) { e.dormant = false; e.t = 0; e.invuln = .3f; }
                    continue;
                }
                if (e.kind == EnemyKind.Mimic && e.t < .5f) continue;    // opens its lid first
                Walk(e);
            }
        }

        void Damage(Enemy e)
        {
            e.hp--;
            if (e.hp > 0)
            {
                e.shell = true; e.invuln = 1.1f; e.speed *= 1.7f;
                Emit(Ev.EnemyHit, e.x, e.y);
                return;
            }
            e.dead = true;
            score += ValueOf(e.kind); kills++;
            Emit(Ev.EnemyDie, e.x, e.y, (int)e.kind);
        }

        void Walk(Enemy e)
        {
            float step = (e.hopping ? 3.4f : e.speed) * Dt;
            if (floor[e.Cx, e.Cy] == F.Quick && !Flies(e.kind)) step *= .6f;
            while (step > 0)
            {
                float dx = e.tx - e.x, dy = e.ty - e.y, d = Math.Abs(dx) + Math.Abs(dy);
                if (d <= 1e-4f) { e.x = e.tx; e.y = e.ty; e.hopping = false; if (!Decide(e)) return; continue; }
                float go = Math.Min(d, step);
                e.x += Math.Sign(dx) * go; e.y += Math.Sign(dy) * go;
                step -= go;
                if (go < d) return;
            }
        }

        static readonly (int x, int y)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        bool Decide(Enemy e)
        {
            int cx = e.tx, cy = e.ty;

            if (e.kind == EnemyKind.Frog && e.t > 2.2f)
            {
                var d0 = Dirs[rng.Int(4)];
                int mx = cx + d0.x, my = cy + d0.y, lx = cx + d0.x * 2, ly = cy + d0.y * 2;
                if (In(lx, ly) && In(mx, my) && EnemyOpen(e, cx, cy, lx, ly) && tile[mx, my] != T.Edge && tile[mx, my] != T.Wall && flame[lx, ly] <= 0 && !OneWayStops(cx, cy, mx, my))
                {
                    e.tx = lx; e.ty = ly; e.dirX = d0.x; e.dirY = d0.y; e.hopping = true; e.t = 0;
                    return true;
                }
            }

            int goX = 0, goY = 0;
            bool chase = e.kind == EnemyKind.Fox || e.kind == EnemyKind.Mimic || e.kind == EnemyKind.Wasp;
            if (chase)
            {
                var target = Nearest(e);
                if (target != null)
                {
                    var (px, py) = Toward(e, target.Cx, target.Cy, e.kind == EnemyKind.Wasp ? 30 : 8);
                    goX = px; goY = py;
                }
            }
            if (goX == 0 && goY == 0 && e.kind == EnemyKind.Ghost && rng.Int(10) < 3)
            {
                var target = Nearest(e);
                if (target != null)
                {
                    int sx = Math.Sign(target.Cx - cx), sy = Math.Sign(target.Cy - cy);
                    if (sx != 0 && EnemyOpen(e, cx, cy, cx + sx, cy)) goX = sx;
                    else if (sy != 0 && EnemyOpen(e, cx, cy, cx, cy + sy)) goY = sy;
                }
            }
            if (goX == 0 && goY == 0) (goX, goY) = Wander(e, cx, cy);
            if (goX == 0 && goY == 0) { e.t += 0; e.dirX = -e.dirX; e.dirY = -e.dirY; return false; }
            e.dirX = goX; e.dirY = goY; e.tx = cx + goX; e.ty = cy + goY;
            return true;
        }

        (int, int) Wander(Enemy e, int cx, int cy)
        {
            bool ahead = (e.dirX != 0 || e.dirY != 0) && EnemyOpen(e, cx, cy, cx + e.dirX, cy + e.dirY);
            int turnChance = e.kind == EnemyKind.Snail ? 15 : 40;
            var options = new List<(int x, int y)>(4);
            foreach (var d in Dirs)
            {
                if (d.x == -e.dirX && d.y == -e.dirY) continue;
                if (d.x == e.dirX && d.y == e.dirY) continue;
                if (EnemyOpen(e, cx, cy, cx + d.x, cy + d.y)) options.Add(d);
            }
            if (ahead && (options.Count == 0 || rng.Int(100) >= turnChance)) return (e.dirX, e.dirY);
            if (options.Count > 0) return options[rng.Int(options.Count)];
            if (ahead) return (e.dirX, e.dirY);
            return EnemyOpen(e, cx, cy, cx - e.dirX, cy - e.dirY) ? (-e.dirX, -e.dirY) : (0, 0);
        }

        Player Nearest(Enemy e)
        {
            Player best = null; float d = float.MaxValue;
            foreach (var p in players)
            {
                if (!p.alive) continue;
                float q = Math.Abs(p.x - e.x) + Math.Abs(p.y - e.y);
                if (q < d) { d = q; best = p; }
            }
            return best;
        }

        readonly int[] seen = new int[W * H], queue = new int[W * H], first = new int[W * H];
        int seenMark;

        /// First step of the shortest way to the target, or nothing if it is further than the limit.
        (int, int) Toward(Enemy e, int gx, int gy, int limit)
        {
            seenMark++;
            int head = 0, tail = 0, start = e.ty * W + e.tx;
            queue[tail++] = start; seen[start] = seenMark; first[start] = -1;
            int[] depth = depthBuf; depth[start] = 0;
            while (head < tail)
            {
                int c = queue[head++], cx = c % W, cy = c / W;
                if (cx == gx && cy == gy)
                {
                    int f = first[c];
                    if (f < 0) return (0, 0);
                    return (Dirs[f].x, Dirs[f].y);
                }
                if (depth[c] >= limit) continue;
                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + Dirs[i].x, ny = cy + Dirs[i].y, n = ny * W + nx;
                    if (!In(nx, ny) || seen[n] == seenMark || !EnemyOpen(e, cx, cy, nx, ny)) continue;
                    if (flame[nx, ny] > 0) continue;
                    seen[n] = seenMark; first[n] = first[c] < 0 ? i : first[c]; depth[n] = depth[c] + 1;
                    queue[tail++] = n;
                }
            }
            return (0, 0);
        }
        readonly int[] depthBuf = new int[W * H];
    }
}
