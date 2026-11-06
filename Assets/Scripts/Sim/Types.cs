using System;
using System.Collections.Generic;

namespace Pitzutz
{
    /// What stands on a cell. Everything except Empty, Sprout and Mirage stops actors.
    public enum T : byte { Empty, Edge, Wall, Block, Hedge, Sprout, Mirage, Crack, Keg, GateRed, GateBlue, Burning, Shrunk }

    /// What the ground of a cell is.
    public enum F : byte { Plain, ConvU, ConvD, ConvL, ConvR, Ice, Quick, Tele, OneU, OneD, OneL, OneR, Crumble, Pit, Jet, Switch, Road, SecretTile }

    public enum Item : byte { None, Bomb, Flame, Speed, Kick, Shield, Exit, Secret }

    public enum EnemyKind : byte { Snail, Beetle, Ghost, Fox, Frog, Turtle, Mimic, Wasp }

    /// Everything the view, the sound and the phone vibration need to hear about.
    public enum Ev : byte { Place, Boom, Brick, Kick, Power, PowerBurn, ShieldBreak, Die, EnemyDie, ExitOpen, StageClear, Teleport, Wind, Car, Hurry, Switch, Jet, Crumble, Secret, BossHit, BossRoar, BossDie, BossWarn, Shrink, EnemyHit, Reveal }

    public struct Event { public Ev kind; public float x, y; public int who; }

    /// One frame of input. The main direction wins; the second one is used when the main one is blocked.
    public struct Pad
    {
        public int mx, my, sx, sy;
        public bool bomb;
        public bool Moving => mx != 0 || my != 0;
    }

    public class Player
    {
        public int id;
        public float x, y;
        public int faceX, faceY = -1;
        public bool alive = true, moving, bot;
        public int bombsMax = 1, flame = 2, speedLv;
        public bool kick, shield;
        public float invuln, walkT, deadT;
        public int iceX, iceY;
        public int teleCell = -1;
        public int lastCell = -1;
        public float Speed => 3.6f + .5f * speedLv;
        public int Cx => Sim.R(x);
        public int Cy => Sim.R(y);
    }

    public class Bomb
    {
        public float x, y, fuse;
        public int owner, range, sx, sy;
        public uint pass;                 // players still standing on it when it was placed
        public bool dead;
        public int teleCell = -1;
        public float beltT;
        public bool nudged;               // moved by a belt or a gust: one cell, not a kick
        public int nudgeFrom = -1;
        public int Cx => Sim.R(x);
        public int Cy => Sim.R(y);
        public bool Sliding => sx != 0 || sy != 0;
    }

    public class Enemy
    {
        public EnemyKind kind;
        public float x, y;
        public int tx, ty, dirX, dirY = -1;
        public int hp = 1;
        public float speed, invuln, t, hop;
        public bool dormant, shell, hopping, dead;
        public int Cx => Sim.R(x);
        public int Cy => Sim.R(y);
    }

    /// A warned cell: harmless until delay runs out, then it burns for life seconds.
    public class Strike { public int x, y; public float delay, life; public int kind; public bool makesBlock; }

    /// A flying boss projectile: it stops at the first solid cell.
    public class Shot { public float x, y, vx, vy; public int kind; public float life; }

    public class Car { public int row, dir, tint; public float x, speed; }
    public class CarLane { public int row, dir; public float speed, period, phase; }

    /// A steel wall that shuttles along a straight line of cells.
    public class Mover
    {
        public int[] cellsX, cellsY;
        public int at, step = 1;
        public float x, y, wait, pause = 1.1f, speed = 2.6f;
        public int nextX, nextY;
        public bool moving;
    }

    public class Jet { public int x, y, dx, dy; public float period = 5, phase, warn = 1, fire = 1.1f; public bool lit; }

    public class Boss
    {
        public int kind;
        public float x, y, half = 1.2f;
        public int hp, maxHp;
        public float hurtT, stateT, t, air;
        public int state, step, dirX, dirY;
        public bool hidden, attacking, dead;
        public float Frac => (float)hp / maxHp;
    }

    /// A small fast random source, so a stage can be replayed from its seed.
    public class Rng
    {
        uint s;
        public Rng(int seed) { s = (uint)seed * 2654435761u + 88172645u; if (s == 0) s = 1; Next(); Next(); }
        public uint Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
        public int Int(int n) => n <= 0 ? 0 : (int)(Next() % (uint)n);
        public float Float() => (Next() & 0xFFFFFF) / (float)0x1000000;
        public void Shuffle<V>(IList<V> a) { for (int i = a.Count - 1; i > 0; i--) { int j = Int(i + 1); (a[i], a[j]) = (a[j], a[i]); } }
    }
}
