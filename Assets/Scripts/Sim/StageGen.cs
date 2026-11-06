using System;
using System.Collections.Generic;

namespace Pitzutz
{
    public class Kit { public int bombs = 1, flame = 2, speed; public bool kick, shield; }

    public partial class Sim
    {
        /// The least a hero has when entering a world, so a stage stays playable after losing everything.
        public static Kit BaseKit(StageDef d)
        {
            var k = new Kit();
            k.bombs = 1 + (d.world + 1) / 2 + (d.Boss ? 1 : 0);
            k.flame = 2 + d.world / 2 + (d.Boss ? 1 : 0);
            return k;
        }

        /// Builds a stage from its map and a seed, trying the next seed until the check passes. Never returns an unfair stage.
        public static Sim Story(StageDef d, int seed, Kit kit)
        {
            for (int tries = 0; tries < 200; tries++)
            {
                var s = new Sim();
                s.Fill(d, seed + tries * 7919, kit);
                if (Solver.Check(s) == null) return s;
            }
            throw new InvalidOperationException("No fair layout for stage " + d.id);
        }

        void Fill(StageDef d, int stageSeed, Kit kit)
        {
            def = d; seed = stageSeed; rng = new Rng(stageSeed); mode = Mode.Story;
            var teleports = new Dictionary<char, int>();
            var maybe = new List<(int x, int y)>();
            var free = new List<(int x, int y)>();
            int sx = 1, sy = H - 2;

            if (d.map.Length != H) throw new InvalidOperationException(d.id + ": map has " + d.map.Length + " rows");
            for (int row = 0; row < H; row++)
            {
                string line = d.map[row];
                if (line.Length != W) throw new InvalidOperationException(d.id + ": row " + row + " has " + line.Length + " columns");
                int y = H - 1 - row;
                for (int x = 0; x < W; x++)
                {
                    char c = line[x];
                    bool edge = x == 0 || y == 0 || x == W - 1 || y == H - 1;
                    if (edge) { tile[x, y] = T.Edge; continue; }
                    switch (c)
                    {
                        case '#': tile[x, y] = T.Wall; break;
                        case 'C': tile[x, y] = T.Crack; break;
                        case 'S': sx = x; sy = y; break;
                        case '.': free.Add((x, y)); break;
                        case ' ': maybe.Add((x, y)); break;
                        case 'B': tile[x, y] = T.Block; break;
                        case '^': floor[x, y] = F.ConvU; break;
                        case 'v': floor[x, y] = F.ConvD; break;
                        case '<': floor[x, y] = F.ConvL; break;
                        case '>': floor[x, y] = F.ConvR; break;
                        case 'i': floor[x, y] = F.Ice; break;
                        case '~': floor[x, y] = F.Ice; maybe.Add((x, y)); break;
                        case 'q': floor[x, y] = F.Quick; break;
                        case 'a': case 'b': case 'c':
                            floor[x, y] = F.Tele;
                            if (teleports.TryGetValue(c, out int mate)) { tele[x, y] = mate; tele[mate % W, mate / W] = y * W + x; }
                            else teleports[c] = y * W + x;
                            break;
                        case 'U': floor[x, y] = F.OneU; break;
                        case 'D': floor[x, y] = F.OneD; break;
                        case 'L': floor[x, y] = F.OneL; break;
                        case 'R': floor[x, y] = F.OneR; break;
                        case 'x': floor[x, y] = F.Crumble; break;
                        case 'n': AddJet(x, y, 0, 1); break;
                        case 's': AddJet(x, y, 0, -1); break;
                        case 'e': AddJet(x, y, 1, 0); break;
                        case 'w': AddJet(x, y, -1, 0); break;
                        case 'o': floor[x, y] = F.Road; break;
                        case 'k': tile[x, y] = T.Keg; break;
                        case 'r': gateCells[x, y] = 1; break;
                        case 'u': gateCells[x, y] = 2; break;
                        case 't': floor[x, y] = F.Switch; break;
                        case '*': floor[x, y] = F.SecretTile; break;
                        default: throw new InvalidOperationException(d.id + ": unknown map letter " + c);
                    }
                }
            }
            Gates();
            foreach (var l in d.lanes) lanes.Add(new CarLane { row = l.row, dir = l.dir, speed = l.speed, period = l.period, phase = l.phase });
            foreach (var t in d.tracks) AddMover(t.x0, t.y0, t.x1, t.y1, t.pause);
            windT = 5;

            var hero = new Player { id = 0, x = sx, y = sy };
            hero.bombsMax = kit.bombs; hero.flame = kit.flame; hero.speedLv = kit.speed; hero.kick = kit.kick; hero.shield = kit.shield;
            players.Add(hero);

            // Blocks from the map's maybe-cells, kept off the hero's first steps
            var blocks = new List<(int x, int y)>();
            foreach (var (x, y) in maybe)
            {
                if (Math.Abs(x - sx) + Math.Abs(y - sy) <= 2) continue;
                if (rng.Float() < d.density) { tile[x, y] = d.hedges ? T.Hedge : T.Block; blocks.Add((x, y)); }
            }

            if (d.Boss) { exitX = 7; exitY = 6; exitSeen = true; AddBoss(d.bossKind); return; }

            // The exit sits under a block far from the start
            var far = blocks.FindAll(b => Math.Abs(b.x - sx) + Math.Abs(b.y - sy) >= 8);
            if (far.Count == 0) far = blocks;
            var door = far[rng.Int(far.Count)];
            exitX = door.x; exitY = door.y; hidden[door.x, door.y] = Item.Exit;
            blocks.Remove(door);

            // Enemies stand on free cells far from the start. Mimics stand in for a block.
            blocks.Sort((a, b) => Near(a, sx, sy).CompareTo(Near(b, sx, sy)));
            var spots = new List<(int x, int y)>();
            foreach (var (x, y) in maybe) if (tile[x, y] == T.Empty && Math.Abs(x - sx) + Math.Abs(y - sy) >= 6) spots.Add((x, y));
            foreach (var (x, y) in free) if (floor[x, y] == F.Plain && Math.Abs(x - sx) + Math.Abs(y - sy) >= 6 && !OnTrack(x, y)) spots.Add((x, y));
            rng.Shuffle(spots);
            int use = 0;
            foreach (var (k, n) in d.foes)
                for (int i = 0; i < n; i++)
                {
                    if (k == EnemyKind.Mimic)
                    {
                        int at = -1;
                        for (int j = blocks.Count - 1; j >= 0; j--) if (Near(blocks[j], sx, sy) >= 6) { at = j; break; }
                        if (at >= 0)
                        {
                            var (bx, by) = blocks[at]; blocks.RemoveAt(at);
                            tile[bx, by] = T.Empty; AddEnemy(k, bx, by);
                            continue;
                        }
                    }
                    if (use >= spots.Count)
                    {
                        // A crowded stage: open a block for this enemy
                        var (bx, by) = blocks[blocks.Count - 1]; blocks.RemoveAt(blocks.Count - 1);
                        tile[bx, by] = T.Empty;
                        AddEnemy(k, bx, by);
                        continue;
                    }
                    var (ex, ey) = spots[use++];
                    AddEnemy(k, ex, ey);
                }

            // Kegs and mirages take over some blocks, then the guaranteed early power-ups, then a quarter of the rest
            for (int i = 0; i < d.early.Length && i < blocks.Count; i++) hidden[blocks[i].x, blocks[i].y] = d.early[i];
            var rest = blocks.GetRange(Math.Min(d.early.Length, blocks.Count), blocks.Count - Math.Min(d.early.Length, blocks.Count));
            rng.Shuffle(rest);
            int mirages = (int)(rest.Count * d.mirageShare), kegs = (int)(rest.Count * d.kegShare);
            for (int i = 0; i < mirages; i++) { var (x, y) = rest[i]; tile[x, y] = T.Mirage; }
            for (int i = mirages; i < mirages + kegs; i++) { var (x, y) = rest[i]; tile[x, y] = T.Keg; }
            rest.RemoveRange(0, mirages + kegs);
            int power = (int)Math.Round(rest.Count * .25f);
            for (int i = 0; i < power && i < rest.Count; i++) hidden[rest[i].x, rest[i].y] = RollPower(rng);
        }

        static int Near((int x, int y) a, int sx, int sy) => Math.Abs(a.x - sx) + Math.Abs(a.y - sy);

        public static Item RollPower(Rng r)
        {
            int n = r.Int(100);
            return n < 30 ? Item.Bomb : n < 60 ? Item.Flame : n < 80 ? Item.Speed : n < 90 ? Item.Kick : Item.Shield;
        }

        void AddJet(int x, int y, int dx, int dy)
        {
            floor[x, y] = F.Jet;
            jets.Add(new Jet { x = x, y = y, dx = dx, dy = dy, phase = jets.Count * 1.25f });
        }

        bool OnTrack(int x, int y)
        {
            foreach (var m in movers) for (int i = 0; i < m.cellsX.Length; i++) if (m.cellsX[i] == x && m.cellsY[i] == y) return true;
            return false;
        }

        void AddMover(int x0, int y0, int x1, int y1, float pause)
        {
            int n = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1, dx = Math.Sign(x1 - x0), dy = Math.Sign(y1 - y0);
            var m = new Mover { cellsX = new int[n], cellsY = new int[n], x = x0, y = y0, pause = pause, wait = pause };
            for (int i = 0; i < n; i++) { m.cellsX[i] = x0 + dx * i; m.cellsY[i] = y0 + dy * i; }
            movers.Add(m);
        }

        /// A battle round: same rules, the four corners, no exit, no enemies.
        public static Sim Battle(int theme, int count, int seed, Kit kit)
        {
            var s = new Sim { mode = Mode.Battle, seed = seed, rng = new Rng(seed) };
            s.def = new StageDef { world = theme, index = 0, id = "battle", music = "m_battle", name = "קרב" };
            var corners = new[] { (1, H - 2), (W - 2, 1), (W - 2, H - 2), (1, 1) };
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (x == 0 || y == 0 || x == W - 1 || y == H - 1) s.tile[x, y] = T.Edge;
                    else if (x % 2 == 0 && y % 2 == 0) s.tile[x, y] = T.Wall;
                }
            var blocks = new List<(int x, int y)>();
            for (int y = 1; y < H - 1; y++)
                for (int x = 1; x < W - 1; x++)
                {
                    if (s.tile[x, y] != T.Empty) continue;
                    bool nearCorner = false;
                    foreach (var (cx, cy) in corners) if (Math.Abs(x - cx) + Math.Abs(y - cy) <= 2) nearCorner = true;
                    if (nearCorner || s.rng.Float() > .75f) continue;
                    s.tile[x, y] = T.Block; blocks.Add((x, y));
                }
            s.rng.Shuffle(blocks);
            int power = blocks.Count / 4;
            for (int i = 0; i < power; i++) s.hidden[blocks[i].x, blocks[i].y] = RollPower(s.rng);
            for (int i = 0; i < count; i++)
            {
                var p = new Player { id = i, x = corners[i].Item1, y = corners[i].Item2, bot = i > 0, bombsMax = kit.bombs, flame = kit.flame };
                s.players.Add(p);
            }
            s.BuildShrink();
            s.timeLeft = 0;
            return s;
        }
    }

    /// The fairness check every generated stage has to pass.
    public static class Solver
    {
        const int W = Sim.W, H = Sim.H, Cells = W * H;
        static readonly (int x, int y)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        /// Returns null when fair, otherwise what is wrong. Blocks count as passable, gates as the switches allow.
        /// Fair means: wherever the hero can get to, it can still get to every enemy, the exit, the secret and every switch.
        public static string Check(Sim s)
        {
            var edges = Graph(s, out var open);
            var h = s.Hero;
            int start = h.Cy * W + h.Cx + (s.gateRedUp ? Cells : 0);
            var reach = Walk(edges, new List<int> { start });
            var back = new List<int>[2 * Cells];         // the same edges the other way round
            for (int i = 0; i < back.Length; i++) back[i] = new List<int>(4);
            for (int i = 0; i < edges.Length; i++) foreach (int j in edges[i]) back[j].Add(i);

            foreach (var e in s.enemies)
                if (!Everywhere(back, reach, open, e.Cx, e.Cy, false)) return "enemy cut off at " + e.Cx + "," + e.Cy;
            if (s.exitX >= 0 && !Everywhere(back, reach, open, s.exitX, s.exitY, false)) return "exit cut off";
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                if (s.tile[x, y] == T.Crack && !Everywhere(back, reach, open, x, y, true)) return "secret cut off";
                if (s.floor[x, y] == F.SecretTile && !Everywhere(back, reach, open, x, y, false)) return "secret tile cut off";
                if (s.floor[x, y] == F.Switch && !Everywhere(back, reach, open, x, y, false)) return "switch cut off";
            }
            if (!Brain.FirstBombSafe(s)) return "no escape from the first bomb";
            // Behind a one-way door there is no going back: the hero must be able to bomb its way on from there
            for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
            {
                var (dx, dy) = Door(s.floor[x, y]);
                if ((dx != 0 || dy != 0) && !Brain.BombSpotFrom(s, x + dx, y + dy)) return "no escape from a bomb behind the door at " + x + "," + y;
            }
            return null;
        }

        static (int, int) Door(F f) => f switch { F.OneU => (0, 1), F.OneD => (0, -1), F.OneL => (-1, 0), F.OneR => (1, 0), _ => (0, 0) };

        /// Can every place the hero can reach still get to this cell (or, for a wall, next to it)?
        static bool Everywhere(List<int>[] back, bool[] reach, bool[] open, int x, int y, bool beside)
        {
            var goals = new List<int>();
            if (beside)
            {
                foreach (var (dx, dy) in Dirs)
                    if (Sim.In(x + dx, y + dy)) { goals.Add((y + dy) * W + x + dx); goals.Add((y + dy) * W + x + dx + Cells); }
            }
            else { goals.Add(y * W + x); goals.Add(y * W + x + Cells); }
            goals.RemoveAll(g => !open[g]);
            if (goals.Count == 0) return false;

            var from = Walk(back, goals);
            for (int i = 0; i < reach.Length; i++) if (reach[i] && !from[i]) return false;
            return true;
        }

        static bool[] Walk(List<int>[] edges, List<int> starts)
        {
            var seen = new bool[2 * Cells];
            var q = new Queue<int>();
            foreach (int a in starts) { seen[a] = true; q.Enqueue(a); }
            while (q.Count > 0)
                foreach (int n in edges[q.Dequeue()])
                    if (!seen[n]) { seen[n] = true; q.Enqueue(n); }
            return seen;
        }

        static bool Stone(Sim s, int x, int y, bool redUp)
        {
            var t = s.tile[x, y];
            if (t == T.Edge || t == T.Wall || t == T.Crack || t == T.Shrunk) return true;
            int g = s.gateCells[x, y];
            return g != 0 && (g == 1) == redUp;
        }

        /// Nodes are cell + gate position. Flame on a switch moves the hero from one half of the graph to the other.
        static List<int>[] Graph(Sim s, out bool[] open)
        {
            var edges = new List<int>[2 * Cells];
            open = new bool[2 * Cells];
            for (int i = 0; i < edges.Length; i++) edges[i] = new List<int>(4);
            int range = s.Hero.flame;
            for (int half = 0; half < 2; half++)
            {
                bool redUp = half == 1;
                int off = half * Cells;
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                {
                    if (Stone(s, x, y, redUp)) continue;
                    int c = y * W + x;
                    open[c + off] = true;
                    if (s.floor[x, y] == F.Tele) edges[c + off].Add(s.tele[x, y] + off);
                    foreach (var (dx, dy) in Dirs)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (Sim.In(nx, ny) && !Stone(s, nx, ny, redUp) && !s.OneWayStops(x, y, nx, ny)) edges[c + off].Add(ny * W + nx + off);
                    }
                    if (s.floor[x, y] == F.Tele || s.IsGate(x, y)) continue;
                    if (HitsSwitch(s, x, y, range, redUp)) edges[c + off].Add(c + Cells - off);
                }
            }
            return edges;
        }

        static bool HitsSwitch(Sim s, int x, int y, int range, bool redUp)
        {
            if (s.floor[x, y] == F.Switch) return true;
            foreach (var (dx, dy) in Dirs)
                for (int i = 1; i <= range; i++)
                {
                    int cx = x + dx * i, cy = y + dy * i;
                    if (!Sim.In(cx, cy) || Stone(s, cx, cy, redUp)) break;
                    if (s.floor[cx, cy] == F.Switch) return true;
                }
            return false;
        }

        /// Every cell the hero can get to, counting bombable blocks (optional) and both gate positions as passable.
        public static bool[,] Flood(Sim s, int sx, int sy, bool throughBlocks)
        {
            var seen = new bool[W, H];
            var q = new Queue<(int x, int y)>();
            seen[sx, sy] = true; q.Enqueue((sx, sy));
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue();
                if (s.floor[x, y] == F.Tele)
                {
                    int to = s.tele[x, y], tx = to % W, ty = to / W;
                    if (!seen[tx, ty]) { seen[tx, ty] = true; q.Enqueue((tx, ty)); }
                }
                foreach (var (dx, dy) in Dirs)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!Sim.In(nx, ny) || seen[nx, ny]) continue;
                    var t = s.tile[nx, ny];
                    bool wall = t == T.Edge || t == T.Wall || t == T.Crack || t == T.Shrunk;
                    if (wall || !throughBlocks && s.Solid(t) || s.OneWayStops(x, y, nx, ny)) continue;
                    seen[nx, ny] = true; q.Enqueue((nx, ny));
                }
            }
            return seen;
        }
    }
}
