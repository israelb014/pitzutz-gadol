using System;
using System.Collections.Generic;

namespace Pitzutz
{
    public partial class Sim
    {
        public int Active(Player p)
        {
            int n = 0;
            foreach (var b in bombs) if (!b.dead && b.owner == p.id) n++;
            return n;
        }

        public bool CanDrop(Player p)
        {
            int x = p.Cx, y = p.Cy;
            return p.alive && Active(p) < p.bombsMax && BombAt(x, y) == null && !Solid(tile[x, y]) && floor[x, y] != F.Tele && floor[x, y] != F.Pit;
        }

        void Drop(Player p)
        {
            if (!CanDrop(p)) return;
            int x = p.Cx, y = p.Cy;
            var b = new Bomb { x = x, y = y, fuse = Fuse, owner = p.id, range = p.flame };
            foreach (var q in players) if (q.alive && Math.Abs(q.x - x) < .95f && Math.Abs(q.y - y) < .95f) b.pass |= 1u << q.id;
            bombs.Add(b);
            Emit(Ev.Place, x, y, p.id);
        }

        bool BombFree(Bomb self, int x, int y)
        {
            if (!In(x, y) || Solid(tile[x, y]) || floor[x, y] == F.Pit || MoverOn(x, y) || DormantAt(x, y) != null) return false;
            int fx = self.Cx, fy = self.Cy;
            if (OneWayStops(fx, fy, x, y)) return false;
            var other = BombAt(x, y);
            if (other != null && other != self) return false;
            foreach (var p in players) if (p.alive && p.Cx == x && p.Cy == y) return false;
            foreach (var e in enemies) if (!e.dead && e.Cx == x && e.Cy == y) return false;
            return true;
        }

        void Bombs()
        {
            for (int i = 0; i < bombs.Count; i++)
            {
                var b = bombs[i];
                if (b.dead) continue;
                b.fuse -= Dt;
                foreach (var p in players)
                    if ((b.pass >> p.id & 1) != 0 && (Math.Abs(p.x - b.x) >= .95f || Math.Abs(p.y - b.y) >= .95f)) b.pass &= ~(1u << p.id);
                Roll(b);
                if (b.fuse <= 0) Detonate(b);
            }
            bombs.RemoveAll(b => b.dead);
        }

        void Roll(Bomb b)
        {
            int cx = b.Cx, cy = b.Cy;
            if (!b.Sliding)
            {
                // Belts and gusts start a bomb moving one cell at a time
                var (dx, dy) = BeltDir(floor[cx, cy]);
                if (dx == 0 && dy == 0 && windState == 2 && b.beltT <= 0) { dx = windX; dy = windY; b.beltT = .5f; }
                if (b.beltT > 0) b.beltT -= Dt;
                if ((dx != 0 || dy != 0) && BombFree(b, cx + dx, cy + dy)) { b.sx = dx; b.sy = dy; b.pass = 0; b.nudged = true; b.nudgeFrom = cy * W + cx; }
                return;
            }
            float step = 7f * Dt;
            float nx = b.x + b.sx * step, ny = b.y + b.sy * step;
            // Crossing a cell centre: look ahead, stop here if the next cell is taken
            bool crossing = b.sx != 0 ? (b.x - cx) * b.sx <= 0 && (nx - cx) * b.sx > 0 : (b.y - cy) * b.sy <= 0 && (ny - cy) * b.sy > 0;
            bool arrived = b.nudged && b.nudgeFrom != cy * W + cx;
            if (crossing && (arrived || !BombFree(b, cx + b.sx, cy + b.sy))) { b.x = cx; b.y = cy; b.sx = b.sy = 0; b.nudged = false; return; }
            b.x = nx; b.y = ny;
            int cell = cy * W + cx;
            if (floor[cx, cy] == F.Tele && b.teleCell != cell && Math.Abs(b.x - cx) < .2f && Math.Abs(b.y - cy) < .2f)
            {
                int to = tele[cx, cy];
                b.x = to % W; b.y = to / W; b.teleCell = to;
                Emit(Ev.Teleport, b.x, b.y);
            }
        }

        public void Detonate(Bomb b)
        {
            if (b.dead) return;
            b.dead = true;
            int x = b.Cx, y = b.Cy;
            Emit(Ev.Boom, x, y, b.owner);
            Blast(x, y, b.range);
        }

        static readonly (int x, int y, byte axis, byte tip)[] Rays = { (-1, 0, FH, TipL), (1, 0, FH, TipR), (0, 1, FV, TipU), (0, -1, FV, TipD) };

        /// A cross of flame from one cell, stopped by the rules of the game.
        public void Blast(int x, int y, int range)
        {
            Light(x, y, FC);
            Flip(x, y);
            foreach (var r in Rays)
            {
                for (int i = 1; i <= range; i++)
                {
                    int cx = x + r.x * i, cy = y + r.y * i;
                    if (!In(cx, cy)) break;
                    bool last = i == range;
                    var t = tile[cx, cy];
                    if (t == T.Edge || t == T.Wall || t == T.Shrunk || t == T.GateRed || t == T.GateBlue || t == T.Burning || MoverOn(cx, cy)) break;
                    if (t == T.Crack) { tile[cx, cy] = T.Empty; item[cx, cy] = Item.Secret; Emit(Ev.Reveal, cx, cy); Emit(Ev.Brick, cx, cy); break; }
                    if (t == T.Keg)
                    {
                        tile[cx, cy] = T.Empty; hidden[cx, cy] = Item.None;
                        Emit(Ev.Boom, cx, cy);
                        Blast(cx, cy, 3);
                        break;
                    }
                    if (t == T.Block || t == T.Hedge)
                    {
                        tile[cx, cy] = T.Burning; timer[cx, cy] = BurnLife;
                        score += 10; Emit(Ev.Brick, cx, cy);
                        Light(cx, cy, (byte)(r.axis | r.tip));
                        break;
                    }
                    if (t == T.Mirage) tile[cx, cy] = T.Empty;
                    var m = DormantAt(cx, cy);
                    if (m != null) { Light(cx, cy, (byte)(r.axis | r.tip)); break; }
                    Flip(cx, cy);
                    var other = BombAt(cx, cy);
                    if (other != null) { Light(cx, cy, r.axis); Detonate(other); break; }
                    var it = item[cx, cy];
                    if (it != Item.None && it != Item.Secret)
                    {
                        item[cx, cy] = Item.None; Emit(Ev.PowerBurn, cx, cy);
                        Light(cx, cy, (byte)(r.axis | r.tip));
                        break;
                    }
                    Light(cx, cy, (byte)(r.axis | (last ? r.tip : 0)));
                }
            }
        }

        void Flip(int x, int y)
        {
            if (floor[x, y] != F.Switch || switchCool > 0) return;
            switchCool = .7f; gateRedUp = !gateRedUp; Gates(); Emit(Ev.Switch, x, y);
        }

        void Light(int x, int y, byte bits)
        {
            flame[x, y] = FlameLife;
            flameMask[x, y] |= bits;
            jetFlame[x, y] = false;
        }

        void Gates()
        {
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                if (!IsGate(x, y)) continue;
                bool red = gateCells[x, y] == 1, up = red == gateRedUp;
                tile[x, y] = up ? (red ? T.GateRed : T.GateBlue) : T.Empty;
            }
        }

        public readonly byte[,] gateCells = new byte[W, H];     // 1 red, 2 blue
        public bool IsGate(int x, int y) => gateCells[x, y] != 0;

        /// Clocks of the cells: flames dying, blocks finishing burning, hedges and pits coming back.
        void Burn()
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (flame[x, y] > 0) { flame[x, y] -= Dt; if (flame[x, y] <= 0) { flameMask[x, y] = 0; jetFlame[x, y] = false; } }
                    if (timer[x, y] <= 0) continue;
                    timer[x, y] -= Dt;
                    if (timer[x, y] > 0) continue;
                    var t = tile[x, y];
                    if (t == T.Burning)
                    {
                        var h = hidden[x, y];
                        hidden[x, y] = Item.None;
                        tile[x, y] = T.Empty;
                        if (h == Item.Exit) { exitSeen = true; Emit(Ev.Reveal, x, y); }
                        else if (h != Item.None) item[x, y] = h;
                        else if (def != null && def.hedges && floor[x, y] == F.Plain && item[x, y] == Item.None && !(x == exitX && y == exitY)) { tile[x, y] = T.Sprout; timer[x, y] = 16; }
                    }
                    else if (t == T.Sprout)
                    {
                        if (Occupied(x, y)) timer[x, y] = 1.5f;
                        else tile[x, y] = T.Hedge;
                    }
                    else if (floor[x, y] == F.Pit)
                    {
                        floor[x, y] = F.Crumble;
                    }
                }
        }

        bool Occupied(int x, int y)
        {
            if (BombAt(x, y) != null) return true;
            foreach (var p in players) if (p.alive && Math.Abs(p.x - x) < 1 && Math.Abs(p.y - y) < 1) return true;
            foreach (var e in enemies) if (!e.dead && Math.Abs(e.x - x) < 1 && Math.Abs(e.y - y) < 1) return true;
            return false;
        }
    }
}
