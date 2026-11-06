using System;

namespace Pitzutz
{
    /// The four bosses. Only flames hurt them; every attack is warned on the floor first.
    public partial class Sim
    {
        const float MinX = 3, MaxX = 11, MinY = 3, MaxY = 9;

        public void AddBoss(int kind)
        {
            int hp = kind switch { 1 => 6, 2 => 7, 3 => 8, _ => 10 };
            boss = new Boss { kind = kind, x = 7, y = 6, hp = hp, maxHp = hp, stateT = 2 };
            Emit(Ev.BossRoar);
        }

        void StepBoss()
        {
            var b = boss;
            if (b == null || b.dead) return;
            b.t += Dt;
            if (b.hurtT > 0) b.hurtT -= Dt;
            b.stateT -= Dt;
            if (!b.hidden && b.air <= 0 && b.hurtT <= 0 && Burned(b))
            {
                b.hp--; b.hurtT = 1.1f; score += 200;
                Emit(Ev.BossHit, b.x, b.y);
                if (b.hp <= 0)
                {
                    b.dead = true; score += 3000 * b.kind;
                    foreach (var e in enemies) e.dead = true;
                    enemies.Clear(); shots.Clear(); strikes.Clear();
                    Emit(Ev.BossDie, b.x, b.y);
                    return;
                }
            }
            switch (b.kind)
            {
                case 1: Truck(b); break;
                case 2: Scorpion(b); break;
                case 3: Press(b); break;
                default: Knight(b); break;
            }
        }

        bool Burned(Boss b)
        {
            int x0 = R(b.x - b.half), x1 = R(b.x + b.half), y0 = R(b.y - b.half), y1 = R(b.y + b.half);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                if (In(x, y) && flame[x, y] > 0 && !jetFlame[x, y]) return true;
            return false;
        }

        Player Target()
        {
            foreach (var p in players) if (p.alive) return p;
            return Hero;
        }

        float Rage(Boss b) => b.Frac <= .5f ? 1.35f : 1;

        static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;

        void To(Boss b, float x, float y, float speed)
        {
            float step = speed * Dt, dx = x - b.x, dy = y - b.y;
            b.x += Math.Sign(dx) * Math.Min(step, Math.Abs(dx));
            b.y += Math.Sign(dy) * Math.Min(step, Math.Abs(dy));
        }

        void Set(Boss b, int state, float seconds) { b.state = state; b.stateT = seconds; b.attacking = state != 0; }

        void Warn(int x, int y, float delay, float life = .4f, int kind = 0, bool block = false)
        {
            if (In(x, y) && !Solid(tile[x, y])) strikes.Add(new Strike { x = x, y = y, delay = delay, life = life, kind = kind, makesBlock = block });
        }

        void Area(int x, int y, float delay)
        {
            for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) Warn(x + i, y + j, delay);
        }

        void Fire(float x, float y, float vx, float vy, int kind) =>
            shots.Add(new Shot { x = x, y = y, vx = vx, vy = vy, kind = kind, life = 5 });

        const float RamWarn = 1.1f;

        /// The charge breaks the rubble it runs into.
        void Smash(Boss b)
        {
            int x0 = R(b.x - b.half), x1 = R(b.x + b.half), y0 = R(b.y - b.half), y1 = R(b.y + b.half);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                if (In(x, y) && tile[x, y] == T.Block) { tile[x, y] = T.Burning; timer[x, y] = BurnLife; Emit(Ev.Brick, x, y); }
        }

        // Boss 1: lines itself up and rams; throws trash that turns into blocks
        void Truck(Boss b)
        {
            var h = Target();
            switch (b.state)
            {
                case 0:
                    To(b, b.x, Clamp(R(h.y), MinY, MaxY), 2.4f * Rage(b));
                    if (b.stateT > 0) break;
                    if (b.step++ % 2 == 0)
                    {
                        // Always toward the far wall of the arena, so a charge is never a stand-still
                        b.dirX = b.x <= MinX ? 1 : b.x >= MaxX ? -1 : h.x >= b.x ? 1 : -1;
                        b.y = R(b.y);
                        for (int x = 1; x < W - 1; x++) for (int k = -1; k <= 1; k++) Warn(x, R(b.y) + k, RamWarn, 0, -1);
                        Emit(Ev.BossWarn);
                        Set(b, 1, RamWarn);
                    }
                    else
                    {
                        int n = b.Frac <= .5f ? 5 : 3;
                        Warn(h.Cx, h.Cy, 1.1f, .3f, 1, true);
                        for (int i = 1; i < n; i++) Warn(h.Cx + rng.Int(7) - 3, h.Cy + rng.Int(7) - 3, 1.1f, .3f, 1, true);
                        Emit(Ev.BossRoar);
                        Set(b, 3, 1.5f);
                    }
                    break;
                case 1:
                    if (b.stateT <= 0) Set(b, 2, 3);
                    break;
                case 2:
                    b.x += b.dirX * 7.5f * Rage(b) * Dt;
                    Smash(b);
                    if (b.x <= MinX || b.x >= MaxX) { b.x = Clamp(b.x, MinX, MaxX); Emit(Ev.Boom, b.x, b.y); Set(b, 3, 1.7f); }
                    break;
                default:
                    if (b.stateT <= 0) Set(b, 0, 1.6f / Rage(b));
                    break;
            }
        }

        const float StingWarn = 1.05f;

        void WarnStings(Boss b)
        {
            int bx = (int)b.x, by = (int)b.y;
            foreach (var d in Dirs)
                for (int i = 1; i < W; i++)
                {
                    int x = bx + d.x * i, y = by + d.y * i;
                    if (!In(x, y) || Solid(tile[x, y])) break;
                    Warn(x, y, StingWarn + i / 5.5f, 0, -1);
                }
            if (b.Frac > .5f) return;
            foreach (var (dx, dy) in Diagonals)
                for (int i = 1; i < W; i++)
                {
                    int x = bx + dx * i, y = by + dy * i;
                    if (!In(x, y) || Solid(tile[x, y])) break;
                    Warn(x, y, StingWarn + i / 4f, 0, -1);
                }
        }

        static readonly (int x, int y)[] Diagonals = { (1, 1), (1, -1), (-1, 1), (-1, -1) };

        // Boss 2: burrows and bursts out under the hero; stings in straight lines
        void Scorpion(Boss b)
        {
            var h = Target();
            switch (b.state)
            {
                case 0:
                    To(b, Clamp(h.x, MinX, MaxX), Clamp(h.y, MinY, MaxY), 1.5f * Rage(b));
                    if (b.stateT > 0) break;
                    if (b.step++ % 2 == 0) { b.hidden = true; Emit(Ev.BossRoar); Set(b, 1, 1.1f); }
                    else
                    {
                        b.x = R(b.x); b.y = R(b.y);
                        WarnStings(b);
                        Emit(Ev.BossWarn); Set(b, 3, StingWarn);
                    }
                    break;
                case 1:
                    if (b.stateT > 0) break;
                    b.x = R(Clamp(h.x, MinX, MaxX)); b.y = R(Clamp(h.y, MinY, MaxY));
                    Area((int)b.x, (int)b.y, 1.05f);
                    Emit(Ev.BossWarn);
                    Set(b, 2, 1.05f);
                    break;
                case 2:
                    if (b.stateT > 0) break;
                    b.hidden = false; Emit(Ev.Boom, b.x, b.y);
                    if (b.Frac <= .5f && enemies.Count < 2)
                    {
                        int x = R(b.x), y = R(b.y) + 2;
                        if (!Solid(tile[x, y]) && Math.Abs(h.x - x) + Math.Abs(h.y - y) >= 3) AddEnemy(EnemyKind.Beetle, x, y);
                    }
                    Set(b, 4, 1.7f);
                    break;
                case 3:
                    if (b.stateT > 0) break;
                    foreach (var d in Dirs) Fire(b.x, b.y, d.x * 5.5f, d.y * 5.5f, 1);
                    if (b.Frac <= .5f) foreach (var d in Diagonals) Fire(b.x, b.y, d.x * 4, d.y * 4, 1);
                    Set(b, 4, 1.1f);
                    break;
                default:
                    if (b.stateT <= 0) Set(b, 0, 1.8f / Rage(b));
                    break;
            }
        }

        // Boss 3: jumps and lands on the hero; rolls gears down whole lanes
        void Press(Boss b)
        {
            var h = Target();
            switch (b.state)
            {
                case 0:
                    To(b, Clamp(h.x, MinX, MaxX), Clamp(h.y, MinY, MaxY), 1.3f * Rage(b));
                    if (b.stateT > 0) break;
                    if (b.step++ % 2 == 0)
                    {
                        b.dirX = R(Clamp(h.x, MinX, MaxX)); b.dirY = R(Clamp(h.y, MinY, MaxY));
                        Area(b.dirX, b.dirY, 1.25f);
                        Emit(Ev.BossWarn);
                        b.air = 1;
                        Set(b, 1, 1.25f);
                    }
                    else
                    {
                        bool rows = rng.Int(2) == 0;
                        int skip = 1 + 2 * rng.Int(rows ? 5 : 6), lanes = 0;
                        for (int c = 1; c < (rows ? H - 1 : W - 1); c += 2)
                        {
                            if (c == skip) continue;
                            if (lanes++ % (b.Frac <= .5f ? 1 : 2) != 0) continue;
                            for (int i = 1; i < (rows ? W - 1 : H - 1); i++) Warn(rows ? i : c, rows ? c : i, 1 + (rows ? i - 1 : H - 2 - i) / 6f, 0, -1);
                            pendingLanes.Add((rows, c));
                        }
                        Emit(Ev.BossWarn);
                        Set(b, 3, 1);
                    }
                    break;
                case 1:
                    b.air = Math.Max(.01f, b.stateT);
                    if (b.stateT > 0) break;
                    b.air = 0; b.x = b.dirX; b.y = b.dirY;
                    Emit(Ev.Boom, b.x, b.y);
                    Set(b, 4, 1.7f);
                    break;
                case 3:
                    if (b.stateT > 0) break;
                    foreach (var (rows, c) in pendingLanes)
                    {
                        if (rows) Fire(1, c, 6, 0, 2); else Fire(c, H - 2, 0, -6, 2);
                    }
                    pendingLanes.Clear();
                    Set(b, 4, 1.3f);
                    break;
                default:
                    if (b.stateT <= 0) Set(b, 0, 1.7f / Rage(b));
                    break;
            }
        }
        readonly System.Collections.Generic.List<(bool rows, int c)> pendingLanes = new System.Collections.Generic.List<(bool, int)>();

        // Boss 4: burns its whole row and column, calls embers from the sky, and blinks away
        void Knight(Boss b)
        {
            var h = Target();
            switch (b.state)
            {
                case 0:
                    To(b, Clamp(h.x, MinX, MaxX), Clamp(h.y, MinY, MaxY), 1.6f * Rage(b));
                    if (b.stateT > 0) break;
                    switch (b.step++ % 3)
                    {
                        case 0:
                            int cx = R(b.x), cy = R(b.y);
                            b.x = cx; b.y = cy;
                            for (int x = 1; x < W - 1; x++) Warn(x, cy, 1.1f, .55f);
                            for (int y = 1; y < H - 1; y++) if (y != cy) Warn(cx, y, 1.1f, .55f);
                            Emit(Ev.BossRoar);
                            Set(b, 4, 1.8f);
                            break;
                        case 1:
                            int n = b.Frac <= .5f ? 14 : 9;
                            Warn(h.Cx, h.Cy, 1.2f, .45f, 3);
                            for (int i = 1; i < n; i++) Warn(1 + rng.Int(W - 2), 1 + rng.Int(H - 2), 1.2f + (i % 3) * .35f, .45f, 3);
                            Emit(Ev.BossWarn);
                            Set(b, 4, 2.1f);
                            break;
                        default:
                            int tx, ty, tries = 0;
                            do { tx = (int)MinX + rng.Int(9); ty = (int)MinY + rng.Int(7); } while (Math.Abs(tx - h.x) + Math.Abs(ty - h.y) < 5 && ++tries < 20);
                            if (Math.Abs(tx - h.x) + Math.Abs(ty - h.y) < 5) { Set(b, 4, .5f); break; }
                            b.x = tx; b.y = ty;
                            Emit(Ev.Teleport, b.x, b.y);
                            Set(b, 4, 1.4f);
                            break;
                    }
                    break;
                default:
                    if (b.stateT <= 0) Set(b, 0, 1.3f / Rage(b));
                    break;
            }
        }
    }
}
