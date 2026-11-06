using System;

namespace Pitzutz
{
    /// Grid-assisted walking. An actor is always on a lane in at least one axis. Asking to turn while between two lanes
    /// slides it into the nearest lane that leads somewhere, so corners never catch it.
    public partial class Sim
    {
        void Drive(Player p, Pad pad)
        {
            float speed = p.Speed;
            var ground = floor[p.Cx, p.Cy];
            if (ground == F.Quick) speed *= .5f;
            float dist = speed * Dt;
            bool moved = false;

            if (ground == F.Ice)
            {
                // Momentum: the hero keeps going until a wall stops it; a new press changes the slide
                if (pad.Moving) { p.iceX = pad.mx; p.iceY = pad.my; }
                if (p.iceX != 0 || p.iceY != 0)
                {
                    moved = Go(p, p.iceX, p.iceY, dist * 1.15f, true);
                    if (!moved) { if (pad.Moving) moved = Go(p, pad.mx, pad.my, dist, true); p.iceX = p.iceY = 0; }
                }
            }
            else
            {
                p.iceX = p.iceY = 0;
                if (pad.Moving)
                {
                    moved = Go(p, pad.mx, pad.my, dist, true);
                    if (!moved && (pad.sx != 0 || pad.sy != 0)) moved = Go(p, pad.sx, pad.sy, dist, true);
                    if (!moved) { p.faceX = pad.mx; p.faceY = pad.my; }
                }
            }

            p.moving = moved;
            if (moved) p.walkT += dist;

            Belt(p);
            Gust(p);
            Portal(p);
            FloorStep(p);
            if (pad.bomb) Drop(p);
        }

        /// Moves the player one step along a direction. Returns false if it could not move at all.
        bool Go(Player p, int dx, int dy, float dist, bool walking)
        {
            bool horizontal = dx != 0;
            float along = horizontal ? p.x : p.y, side = horizontal ? p.y : p.x;
            int s = horizontal ? dx : dy;
            int ac = R(along), sc = R(side);
            float off = side - sc;
            const float Eps = .001f;

            if (Math.Abs(off) > Eps)
            {
                // Between two lanes: use the nearest lane that is open ahead, else the other one
                int other = sc + (off > 0 ? 1 : -1);
                bool nearOpen = LaneOpen(p, horizontal, ac, sc, s, along), farOpen = LaneOpen(p, horizontal, ac, other, s, along);
                int lane = nearOpen ? sc : farOpen ? other : int.MinValue;
                if (lane == int.MinValue) return false;
                float need = lane - side, step = Math.Min(Math.Abs(need), dist);
                float nd = side + Math.Sign(need) * step;
                if (horizontal) p.y = nd; else p.x = nd;
                if (walking) { p.faceX = dx; p.faceY = dy; }
                dist -= step;
                if (dist <= 0) return true;
                side = nd; sc = lane;
            }

            // On a lane: run to the centre of the last cell that is open
            float limit = float.MaxValue;
            int cx = horizontal ? ac : sc, cy = horizontal ? sc : ac;
            int nx = cx + dx, ny = cy + dy;
            float past = (along - ac) * s;       // how far beyond the centre of this cell
            if (!Open(p, cx, cy, nx, ny))
            {
                limit = -past;                    // up to the centre
                if (limit <= Eps)
                {
                    if (walking) { Kick(p, nx, ny, dx, dy); p.faceX = dx; p.faceY = dy; }
                    return false;
                }
            }
            float go = Math.Min(dist, limit);
            if (horizontal) { p.x += s * go; p.y = sc; } else { p.y += s * go; p.x = sc; }
            if (walking) { p.faceX = dx; p.faceY = dy; }
            return go > Eps;
        }

        bool LaneOpen(Player p, bool horizontal, int ac, int lane, int s, float along)
        {
            int cx = horizontal ? ac : lane, cy = horizontal ? lane : ac;
            if (!In(cx, cy)) return false;
            if (Solid(tile[cx, cy]) && !(cx == p.Cx && cy == p.Cy)) return false;
            int dx = horizontal ? s : 0, dy = horizontal ? 0 : s;
            return Open(p, cx, cy, cx + dx, cy + dy);
        }

        void Kick(Player p, int x, int y, int dx, int dy)
        {
            if (!p.kick || !In(x, y)) return;
            var b = BombAt(x, y);
            if (b == null || b.Sliding) return;
            if (!BombFree(b, x + dx, y + dy)) return;
            b.sx = dx; b.sy = dy; b.nudged = false;
            Emit(Ev.Kick, x, y, p.id);
        }

        void Belt(Player p)
        {
            var (dx, dy) = BeltDir(floor[p.Cx, p.Cy]);
            if (dx != 0 || dy != 0) Go(p, dx, dy, 1.5f * Dt, false);
        }

        public static (int, int) BeltDir(F f) => f switch { F.ConvU => (0, 1), F.ConvD => (0, -1), F.ConvL => (-1, 0), F.ConvR => (1, 0), _ => (0, 0) };

        void Gust(Player p)
        {
            if (windState == 2) Go(p, windX, windY, 1.5f * Dt, false);
        }

        void Portal(Player p)
        {
            int cx = p.Cx, cy = p.Cy, cell = cy * W + cx;
            if (p.teleCell == cell) return;
            p.teleCell = -1;
            if (floor[cx, cy] != F.Tele || Math.Abs(p.x - cx) > .3f || Math.Abs(p.y - cy) > .3f) return;
            int to = tele[cx, cy];
            p.x = to % W; p.y = to / W; p.teleCell = to;
            Emit(Ev.Teleport, p.x, p.y, p.id);
        }

        /// Crumbling floor gives way a little each time someone leaves it, and a pit fills in again later.
        void FloorStep(Player p)
        {
            int cell = p.Cy * W + p.Cx;
            if (cell == p.lastCell) return;
            int was = p.lastCell;
            p.lastCell = cell;

            int cx = p.Cx, cy = p.Cy;
            if (floor[cx, cy] == F.SecretTile && !secretFound && item[cx, cy] == Item.None)
            {
                floor[cx, cy] = F.Plain; item[cx, cy] = Item.Secret; Emit(Ev.Reveal, cx, cy);
            }
            if (was < 0) return;
            int wx = was % W, wy = was / W;
            if (floor[wx, wy] != F.Crumble) return;
            crumble[wx, wy]++;
            Emit(Ev.Crumble, wx, wy);
            if (crumble[wx, wy] >= 3 && BombAt(wx, wy) == null)
            {
                floor[wx, wy] = F.Pit; timer[wx, wy] = 14; crumble[wx, wy] = 0;
                item[wx, wy] = Item.None;
            }
        }
    }
}
