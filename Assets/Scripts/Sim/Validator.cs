using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Pitzutz
{
    /// Plays every stage through the real rules with a bot that only has a pad, on many layouts.
    /// The build stops if a stage can not be won: its fairness check fails, or the bot can not clear it.
    public static class Validator
    {
        public const int Seeds = 6, Attempts = 8;

        public class Result { public bool ok; public int cleared, deaths; public float bestTime; public string note = ""; }

        /// Plays one layout until it is cleared or the attempts run out. The kit is the worst case: what a hero has after losing everything.
        public static Result Play(StageDef d, int seed, int attempts = Attempts)
        {
            var r = new Result();
            for (int n = 0; n < attempts; n++)
            {
                var s = Sim.Story(d, seed + n * 1009, Sim.BaseKit(d));
                var brain = new Brain(s, s.Hero, Skill.Hard, seed + n);
                var pads = new Pad[1];
                int limit = (int)((Sim.StageTime + 90) / Sim.Dt);
                for (int t = 0; t < limit && s.outcome == Outcome.Running; t++)
                {
                    pads[0] = brain.Think();
                    s.Tick(pads);
                    s.events.Clear();
                }
                if (s.outcome == Outcome.Cleared) { r.ok = true; r.cleared = n + 1; r.bestTime = s.time; return r; }
                r.deaths++;
                r.note = s.outcome + $" at {s.time:F0}s, enemies {s.enemies.Count}, boss {(s.boss == null ? 0 : s.boss.hp)}, exit seen {s.exitSeen}, hero {s.Hero.Cx},{s.Hero.Cy}";
            }
            return r;
        }

        /// Writes a report. Returns true if every stage passes on every seed.
        public static bool Run(string report, Action<string> say = null)
        {
            var log = new StringBuilder();
            int fails = 0;
            void Note(string l) { log.AppendLine(l); say?.Invoke(l); }
            string self = SelfTest();
            if (self != null) { Note("FAIL self test: " + self); fails++; }

            // Layouts are independent, so all of them run at once; the report keeps the stage order
            var all = Stages.All;
            var results = new Result[all.Length, Seeds];
            var problems = new string[all.Length, Seeds];
            System.Threading.Tasks.Parallel.For(0, all.Length * Seeds, i =>
            {
                int st = i / Seeds, seed = i % Seeds + 1;
                problems[st, seed - 1] = Static(all[st], seed);
                if (problems[st, seed - 1] == null) results[st, seed - 1] = Play(all[st], seed * 101);
            });
            for (int st = 0; st < all.Length; st++)
            {
                var d = all[st];
                int won = 0; float time = 0; int tries = 0; string bad = "";
                for (int seed = 1; seed <= Seeds; seed++)
                {
                    string problem = problems[st, seed - 1];
                    if (problem != null) { bad += " seed " + seed + ": " + problem; fails++; continue; }
                    var r = results[st, seed - 1];
                    if (r.ok) { won++; time += r.bestTime; tries += r.cleared; }
                    else { bad += $" seed {seed}: bot lost {r.deaths} times, last {r.note}"; fails++; }
                }
                Note($"{(won == Seeds ? "PASS" : "FAIL")} {d.id} {d.Code} won {won}/{Seeds} tries {tries} avg {(won > 0 ? time / won : 0):F0}s{bad}");
            }
            Note(fails == 0 ? "ALL STAGES PASS" : fails + " FAILED");
            if (report != null) File.WriteAllText(report, log.ToString());
            return fails == 0;
        }

        /// Rules that must hold whatever the bots do. Returns what is broken, or null.
        public static string SelfTest()
        {
            foreach (var d in Stages.All)
            {
                int secrets = 0;
                foreach (var row in d.map) foreach (char c in row) if (c == 'C' || c == '*') secrets++;
                if (secrets != 1) return d.id + " has " + secrets + " secrets";
            }
            return Layouts() ?? Corners() ?? Unfair();
        }

        /// The exit is always under a block, and about a quarter of the blocks hold a power-up.
        static string Layouts()
        {
            foreach (var d in Stages.All)
            {
                if (d.Boss) continue;
                int blocks = 0, power = 0;
                for (int seed = 1; seed <= 8; seed++)
                {
                    var s = Sim.Story(d, seed, Sim.BaseKit(d));
                    if (s.tile[s.exitX, s.exitY] != T.Block && s.tile[s.exitX, s.exitY] != T.Hedge || s.hidden[s.exitX, s.exitY] != Item.Exit) return d.id + ": the exit is not under a block";
                    for (int y = 0; y < Sim.H; y++) for (int x = 0; x < Sim.W; x++)
                    {
                        if (s.tile[x, y] == T.Block || s.tile[x, y] == T.Hedge) blocks++;
                        if (s.hidden[x, y] != Item.None && s.hidden[x, y] != Item.Exit) power++;
                    }
                    int early = 0;
                    for (int y = 0; y < Sim.H; y++) for (int x = 0; x < Sim.W; x++) foreach (var it in d.early) if (s.hidden[x, y] == it) { early++; break; }
                    if (early < d.early.Length) return d.id + ": an early power-up is missing";
                }
                float share = (float)power / blocks;
                if (share < .18f || share > .36f) return d.id + $": {share:P0} of the blocks hold a power-up";
            }
            return null;
        }

        /// Walking into a corridor mouth up to .45 of a cell off its line must slide in and never stick.
        static string Corners()
        {
            var cases = new (int x, int y, int dx, int dy)[] { (3, 1, 0, 1), (3, 3, 0, -1), (1, 3, 1, 0), (3, 3, 1, 0), (5, 3, -1, 0), (3, 5, 0, -1) };
            foreach (var (x, y, dx, dy) in cases)
                for (int k = -9; k <= 9; k++)
                {
                    var s = Sim.Battle(0, 1, 1, new Kit());
                    for (int j = 0; j < Sim.H; j++) for (int i = 0; i < Sim.W; i++) if (s.tile[i, j] == T.Block) { s.tile[i, j] = T.Empty; s.hidden[i, j] = Item.None; }
                    var h = s.Hero;
                    h.x = x + (dx == 0 ? k * .05f : 0); h.y = y + (dy == 0 ? k * .05f : 0);
                    var pad = new[] { new Pad { mx = dx, my = dy } };
                    for (int t = 0; t < 40; t++) s.Tick(pad);
                    int tx = x + dx * 2, ty = y + dy * 2;
                    if (Math.Abs(h.x - tx) > .55f && dx != 0 || Math.Abs(h.y - ty) > .55f && dy != 0 || (dx != 0 ? Math.Abs(h.y - y) : Math.Abs(h.x - x)) > .01f)
                        return $"stuck at a corner: from {x},{y} going {dx},{dy} with offset {k * .05f:F2} ended at {h.x:F2},{h.y:F2}";
                }
            return null;
        }

        /// Layouts the fairness check has to refuse, so that it can be trusted.
        static string Unfair()
        {
            var sealedRoom = new StageDef
            {
                id = "test", map = new[]
                {
                    "###############",
                    "#S..#         #",
                    "#.#.# # # # # #",
                    "#...#         #",
                    "##### # # # # #",
                    "#             #",
                    "# # # # # # # #",
                    "#             #",
                    "# # # # # # # #",
                    "#             #",
                    "# # # # # # # #",
                    "#             #",
                    "###############",
                },
                foes = new[] { (EnemyKind.Snail, 3) }, density = .5f,
            };
            var trap = new StageDef
            {
                id = "test", map = new[]
                {
                    "###############",
                    "#S.           #",
                    "#.# # # # # # #",
                    "#             #",
                    "# # # # # # # #",
                    "#             #",
                    "# # # # # # # #",
                    "#             #",
                    "# # # # # # # #",
                    "#             #",
                    "#######D#######",
                    "#.............#",
                    "###############",
                },
                foes = new[] { (EnemyKind.Snail, 6) }, density = .5f,
            };
            foreach (var (d, name) in new[] { (sealedRoom, "a sealed room"), (trap, "a one-way trap") })
            {
                try { Sim.Story(d, 1, new Kit()); return "the fairness check let " + name + " through"; }
                catch (InvalidOperationException) { }
            }
            return null;
        }

        static string Static(StageDef d, int seed)
        {
            try { var s = Sim.Story(d, seed, Sim.BaseKit(d)); return Solver.Check(s); }
            catch (Exception e) { return e.Message; }
        }

        public static int Cli(string[] a)
        {
            if (a.Length > 0 && a[0] == "battle") return Battles();
            if (a.Length > 0 && a[0] == "bt") { BattleTrace(int.Parse(a[1]), float.Parse(a[2]), a.Length > 3 ? a[3] : "hard"); return 0; }
            if (a.Length > 0 && a[0] == "trace")
            {
                foreach (var d in Stages.All) if (d.id == a[1]) Trace(d, int.Parse(a[2]), a.Length > 3 ? int.Parse(a[3]) : 0, a.Length > 4 ? float.Parse(a[4]) : 270);
                return 0;
            }
            if (a.Length > 0 && a[0] == "att")
            {
                foreach (var d in Stages.All) if (d.id == a[1])
                    for (int n = 0; n < Attempts; n++) { var r = Play(d, int.Parse(a[2]) + n * 1009, 1); Console.WriteLine($"{int.Parse(a[2]) + n * 1009}: ok={r.ok} time={r.bestTime:F0} {r.note}"); }
                return 0;
            }
            if (a.Length > 0 && a[0] == "stress") return Stress(a.Length > 1 ? int.Parse(a[1]) : 12);
            if (a.Length > 0 && a[0] == "cost") { Cost(); return 0; }
            if (a.Length > 0 && a[0] == "self") { Console.WriteLine(SelfTest() ?? "self test ok"); return 0; }
            if (a.Length > 0 && a[0] == "deaths") { Deaths(a.Length > 1 ? a[1] : null); return 0; }
            if (a.Length > 0 && a[0] == "one")
            {
                foreach (var d in Stages.All) if (d.id == a[1])
                    for (int seed = 1; seed <= 6; seed++) { var r = Play(d, seed * 101, 3); Console.WriteLine($"{d.id} seed {seed}: ok={r.ok} tries={r.cleared} time={r.bestTime:F0} {r.note}"); }
                return 0;
            }
            return Run("report.txt", Console.WriteLine) ? 0 : 1;
        }

        /// The same test as Run, on other seeds: to learn how much of a pass is luck.
        static int Stress(int count)
        {
            var all = Stages.All;
            var won = new int[all.Length];
            var tries = new int[all.Length];
            System.Threading.Tasks.Parallel.For(0, all.Length * count, i =>
            {
                int st = i / count, seed = 5000 + i % count * 13;
                var r = Play(all[st], seed);
                lock (won) { if (r.ok) { won[st]++; tries[st] += r.cleared; } }
            });
            int bad = 0;
            for (int st = 0; st < all.Length; st++)
            {
                Console.WriteLine($"{all[st].id}: {won[st]}/{count} tries {tries[st]}");
                bad += count - won[st];
            }
            Console.WriteLine(bad + " layouts lost");
            return 0;
        }

        /// How long one bot takes to think, on the busiest stages.
        static void Cost()
        {
            foreach (var d in Stages.All)
            {
                var s = Sim.Story(d, 3, Sim.BaseKit(d));
                var brain = new Brain(s, s.Hero, Skill.Hard, 3);
                var pads = new Pad[1];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long worst = 0; int plans = 0; double sum = 0;
                for (int t = 0; t < 60 / Sim.Dt && s.outcome == Outcome.Running; t++)
                {
                    long a = clock.ElapsedTicks;
                    pads[0] = brain.Think();
                    long used = clock.ElapsedTicks - a;
                    sum += used; if (t > 60 && used > worst) worst = used; if (t % 6 == 0) plans++;
                    s.Tick(pads); s.events.Clear();
                }
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Console.WriteLine($"{d.id}: plan avg {sum / Math.Max(1, plans) * ms:F3} ms, worst {worst * ms:F3} ms");
            }
        }

        /// What kills the bot, per stage: for tuning.
        static void Deaths(string only)
        {
            foreach (var d in Stages.All)
            {
                if (only != null && d.id != only) continue;
                var tally = new SortedDictionary<string, int>();
                int wins = 0, plays = 0, left = 0, seen = 0, at180 = 0; float sum = 0;
                object gate = new object();
                System.Threading.Tasks.Parallel.For(0, 48, i =>
                {
                    var s = Sim.Story(d, 1000 + i * 37, Sim.BaseKit(d));
                    var brain = new Brain(s, s.Hero, Skill.Hard, 1000 + i * 37);
                    var pads = new Pad[1];
                    string why = "timeout";
                    for (int t = 0; t < 270 / Sim.Dt && s.outcome == Outcome.Running; t++)
                    {
                        pads[0] = brain.Think();
                        bool was = s.Hero.alive;
                        s.Tick(pads);
                        s.events.Clear();
                        if (was && !s.Hero.alive) why = Cause(s);
                        if (t == (int)(180 / Sim.Dt)) lock (gate) { left += s.enemies.Count; if (s.exitSeen) seen++; at180++; }
                    }
                    lock (gate)
                    {
                        plays++;
                        if (s.outcome == Outcome.Cleared) { wins++; sum += s.time; }
                        else { tally.TryGetValue(why, out int n); tally[why] = n + 1; if (only != null) Console.WriteLine($"{1000 + i * 37} {why} t={s.time:F0}"); }
                    }
                });
                var b = new StringBuilder($"{d.id}: {wins}/{plays} avg {(wins > 0 ? sum / wins : 0):F0}s at180 {at180} left {left} seen {seen} |");
                foreach (var kv in tally) b.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                Console.WriteLine(b);
            }
        }

        static string Cause(Sim s)
        {
            var h = s.Hero; int x = h.Cx, y = h.Cy;
            string late = s.time > 180 ? "late-" : "";
            if (s.flame[x, y] > 0) return late + (s.jetFlame[x, y] ? (s.boss != null ? "strike" : "jet") : "flame");
            foreach (var e in s.enemies) if (Math.Abs(e.x - h.x) < .7f && Math.Abs(e.y - h.y) < .7f) return late + e.kind;
            if (s.boss != null && Math.Abs(s.boss.x - h.x) < 1.6f && Math.Abs(s.boss.y - h.y) < 1.6f) return "boss" + s.boss.state;
            foreach (var c in s.cars) if (c.row == y) return "car";
            if (s.shots.Count > 0) return "shot";
            return late + "other";
        }

        static void Trace(StageDef d, int seed, int a2 = 0, float stop = 270)
        {
            var s = Sim.Story(d, seed, Sim.BaseKit(d));
            var brain = new Brain(s, s.Hero, Skill.Hard, seed);
            var pads = new Pad[1];
            var ring = new Queue<string>();
            Brain.Debug = l => ring.Enqueue(l);
            for (int t = 0; t < stop / Sim.Dt && s.outcome == Outcome.Running; t++)
            {
                pads[0] = brain.Think();
                s.Tick(pads);
                foreach (var e in s.events) if (e.kind != Ev.Brick) ring.Enqueue($"t={s.time:F2} {e.kind} {e.x},{e.y}");
                s.events.Clear();
                if (t % 15 == 0) ring.Enqueue(Picture(s, pads[0]));
                while (ring.Count > (a2 > 0 ? a2 : 30)) ring.Dequeue();
                if (!s.Hero.alive) break;
            }
            foreach (var l in ring) Console.WriteLine(l);
            Console.WriteLine(Picture(s, pads[0]));
            Console.WriteLine(s.outcome + " " + s.time);
            foreach (var e in s.enemies) Console.WriteLine($"{e.kind} {e.x:F2},{e.y:F2} to {e.tx},{e.ty} dormant {e.dormant} hp {e.hp}");
        }

        static string Picture(Sim s, Pad p)
        {
            var b = new StringBuilder($"t={s.time:F2} hero {s.Hero.x:F2},{s.Hero.y:F2} alive {s.Hero.alive} pad {p.mx},{p.my} bomb {p.bomb} active {s.Active(s.Hero)}\n");
            for (int y = Sim.H - 1; y >= 0; y--)
            {
                for (int x = 0; x < Sim.W; x++)
                {
                    char c = s.tile[x, y] switch { T.Edge => '#', T.Wall => '#', T.Crack => 'C', T.Block => 'B', T.Hedge => 'H', T.Burning => 'b', T.Keg => 'k', T.GateRed => 'r', T.GateBlue => 'u', T.Mirage => 'm', _ => '.' };
                    if (s.flame[x, y] > 0) c = '*';
                    if (s.BombAt(x, y) != null) c = 'O';
                    foreach (var e in s.enemies) if (e.Cx == x && e.Cy == y) c = 'e';
                    if (s.Hero.Cx == x && s.Hero.Cy == y) c = '@';
                    b.Append(c);
                }
                b.Append('\n');
            }
            return b.ToString();
        }

        /// One hard round, printed every few seconds from a moment on.
        static void BattleTrace(int seed, float from, string left)
        {
            var s = Sim.Battle(seed % 4, 2, seed, new Kit());
            var b0 = new Brain(s, s.players[0], left == "easy" ? Skill.Easy : left == "normal" ? Skill.Normal : Skill.Hard, seed); var b1 = new Brain(s, s.players[1], Skill.Hard, seed + 5);
            var pads = new Pad[2];
            for (int t = 0; t < 240 / Sim.Dt && s.outcome == Outcome.Running; t++)
            {
                pads[0] = b0.Think(); pads[1] = b1.Think(); s.Tick(pads);
                foreach (var e in s.events) if (e.kind == Ev.Kick) Console.WriteLine($"t={s.time:F1} kick by {e.who}");
                foreach (var e in s.events) if (e.kind == Ev.Die) Console.WriteLine($"t={s.time:F1} player {e.who} dies at {e.x:F1},{e.y:F1}");
                s.events.Clear();
                if (s.time >= from && t % 30 == 0)
                {
                    var b = new StringBuilder($"t={s.time:F1} p0 {s.players[0].x:F1},{s.players[0].y:F1} bombs {s.players[0].bombsMax} flame {s.players[0].flame}; p1 {s.players[1].x:F1},{s.players[1].y:F1} bombs {s.players[1].bombsMax} flame {s.players[1].flame}\n");
                    for (int y = Sim.H - 1; y >= 0; y--)
                    {
                        for (int x = 0; x < Sim.W; x++)
                        {
                            char c = s.tile[x, y] switch { T.Edge => '#', T.Wall => '#', T.Block => 'B', T.Burning => 'b', T.Shrunk => 'X', _ => s.item[x, y] != Item.None ? '+' : '.' };
                            if (s.flame[x, y] > 0) c = '*';
                            if (s.BombAt(x, y) != null) c = 'O';
                            if (s.players[0].alive && s.players[0].Cx == x && s.players[0].Cy == y) c = '0';
                            if (s.players[1].alive && s.players[1].Cx == x && s.players[1].Cy == y) c = '1';
                            b.Append(c);
                        }
                        b.Append('\n');
                    }
                    Console.WriteLine(b);
                }
            }
            Console.WriteLine($"winner {s.roundWinner} at {s.time:F1}");
        }

        /// Bots against bots: rounds must end, harder bots must win more often, and bots must not blow themselves up.
        public static int Battles()
        {
            Skill[] all = { Skill.Easy, Skill.Normal, Skill.Hard };
            string[] names = { "easy", "normal", "hard" };
            const int Rounds = 100;
            for (int a = 0; a < 3; a++) for (int b = a; b < 3; b++)
            {
                int wa = 0, wb = 0, draw = 0, unfinished = 0, suicide = 0, shrunk = 0; float time = 0;
                int cornerA = 0, cornerB = 0;
                var wallBy = new int[2]; var ownBy = new int[2]; var hitBy = new int[2];
                object gate = new object();
                int ca = a, cb = b;
                System.Threading.Tasks.Parallel.For(1, Rounds + 1, seed =>
                {
                    var s = Sim.Battle(seed % 4, 2, seed, new Kit());
                    int first = seed % 2;       // the skills swap corners every round
                    var b0 = new Brain(s, s.players[0], all[first == 0 ? ca : cb], seed); var b1 = new Brain(s, s.players[1], all[first == 0 ? cb : ca], seed + 5);
                    var pads = new Pad[2];
                    int own = 0, walls = 0;
                    var lastBomb = new int[2];
                    for (int t = 0; t < 240 / Sim.Dt && s.outcome == Outcome.Running; t++)
                    {
                        pads[0] = b0.Think(); pads[1] = b1.Think();
                        bool a0 = s.players[0].alive, a1 = s.players[1].alive;
                        s.Tick(pads);
                        foreach (var e in s.events)
                        {
                            if (e.kind == Ev.Boom) lastBomb[0] = e.who == 0 ? t : lastBomb[0];
                            if (e.kind == Ev.Boom) lastBomb[1] = e.who == 1 ? t : lastBomb[1];
                        }
                        for (int i = 0; i < 2; i++)
                        {
                            if (!(i == 0 ? a0 : a1) || s.players[i].alive) continue;
                            if (s.tile[s.players[i].Cx, s.players[i].Cy] == T.Shrunk) { walls++; lock (gate) wallBy[i ^ first]++; }
                            else if (t - lastBomb[i] < 40 && t - lastBomb[1 - i] >= 40) { own++; lock (gate) ownBy[i ^ first]++; }
                            else lock (gate) hitBy[i ^ first]++;
                        }
                        s.events.Clear();
                    }
                    lock (gate)
                    {
                        time += s.time; suicide += own; shrunk += walls;
                        if (s.outcome == Outcome.Running) unfinished++;
                        if (s.roundWinner < 0) draw++; else if ((s.roundWinner ^ first) == 0) wa++; else wb++;
                        if (s.roundWinner == 0) cornerA++; else if (s.roundWinner == 1) cornerB++;
                    }
                });
                Console.WriteLine($"{names[a]} vs {names[b]}: {wa} - {wb}, draws {draw}, unfinished {unfinished}, own-bomb deaths {suicide}, crushed by walls {shrunk}, avg {time / Rounds:F0}s | left: own {ownBy[0]} wall {wallBy[0]} hit {hitBy[0]}; right: own {ownBy[1]} wall {wallBy[1]} hit {hitBy[1]} | corners {cornerA}-{cornerB}");
            }
            return 0;
        }
    }
}
