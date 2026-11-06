using System;
using UnityEngine;

namespace Pitzutz
{
    public class RunState { public int score, lives = 3; public Kit kit = new Kit(); }

    public class BattleState { public int foes = 1, level = 1, arena; public readonly int[] wins = new int[4]; public int round; }

    /// Runs the Sim on screen: 60 fixed ticks a second, whatever the display does, then turns its events into sound, shaking and vibration.
    public class Game : MonoBehaviour
    {
        App app;
        public Sim sim;
        public Board board;
        public int stage = -1;
        public bool paused, skipIntro;
        public BattleState battle;
        public Func<Pad> botHand;                   // a test plays instead of the keyboard
        public int ticks;
        Brain[] brains;
        Pad[] pads;
        float acc, card;
        bool bombLatch, ending;
        float endT;
        int lastCount = -1;

        public bool Running => sim != null;
        public bool InBattle => battle != null;

        public void Init(App a) { app = a; board = new Board(); }

        public void Unload()
        {
            sim = null; battle = null; stage = -1; paused = false; ending = false;
            board.Hide();
            app.hud.Card("", "");
        }

        public void StartStory(int index, Kit carry)
        {
            battle = null; stage = index; paused = false; ending = false;
            var d = Stages.All[index];
            var low = Sim.BaseKit(d);
            var kit = new Kit
            {
                bombs = Mathf.Max(low.bombs, carry.bombs), flame = Mathf.Max(low.flame, carry.flame), speed = carry.speed, kick = carry.kick, shield = carry.shield,
            };
            sim = Sim.Story(d, Environment.TickCount & 0xFFFFF, kit);
            Begin(d.music);
            card = skipIntro ? 0 : 2.6f;
            if (card > 0) app.hud.Card(d.Boss ? d.name : d.Code + "  " + d.name, d.hint);
        }

        public void StartBattle(BattleState b)
        {
            battle = b; stage = -1; paused = false; ending = false;
            b.round++;
            sim = Sim.Battle(b.arena, b.foes + 1, Environment.TickCount & 0xFFFFF, new Kit());
            var skill = b.level == 0 ? Skill.Easy : b.level == 1 ? Skill.Normal : Skill.Hard;
            brains = new Brain[sim.players.Count];
            for (int i = 1; i < brains.Length; i++) brains[i] = new Brain(sim, sim.players[i], skill, sim.seed + i);
            Begin("m_battle");
            card = skipIntro ? 0 : 1.9f;
            if (card > 0) app.hud.Card("סיבוב " + b.round, "מנצח שני סיבובים - לוקח את הקרב");
        }

        void Begin(string music)
        {
            acc = 0; ticks = 0; lastCount = -1;
            pads = new Pad[sim.players.Count];
            if (!InBattle) brains = null;
            board.Load(sim);
            Sound.Music(music);
            app.hud.Refresh(sim, app.run, battle, stage);
        }

        void Update()
        {
            if (sim == null || paused) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
            if (card > 0)
            {
                card -= dt;
                if (card <= 0) app.hud.Card("", "");
                board.Draw(dt);
                return;
            }
            if (ending) { board.Draw(dt); endT -= dt; if (endT <= 0) app.GameEnded(sim); return; }

            var hand = botHand != null ? botHand() : GameInput.Read();
            if (ticks == 0) hand.bomb = false;     // the press that started the stage is not a bomb
            if (hand.bomb) bombLatch = true;
            acc += dt;
            while (acc >= Sim.Dt && sim.outcome == Outcome.Running)
            {
                acc -= Sim.Dt;
                Step(hand);
                hand.bomb = false;
            }
            board.Draw(dt);
            app.hud.Refresh(sim, app.run, battle, stage);
            if (sim.outcome != Outcome.Running) Finish();
        }

        /// One tick. Tests call this directly to run faster than real time.
        public void Step(Pad hand)
        {
            hand.bomb |= bombLatch; bombLatch = false;
            pads[0] = hand;
            if (brains != null) for (int i = 1; i < brains.Length; i++) pads[i] = brains[i].Think();
            sim.Tick(pads);
            ticks++;
            foreach (var e in sim.events) Heard(e);
            sim.events.Clear();
            if (sim.mode == Mode.Story && sim.timeLeft <= 10 && sim.timeLeft > 0)
            {
                int c = Mathf.CeilToInt(sim.timeLeft);
                if (c != lastCount) { lastCount = c; Sound.Play("countdown", .7f); }
            }
        }

        void Heard(Event e)
        {
            bool me = e.who == 0;
            switch (e.kind)
            {
                case Ev.Place: Sound.Play("place", .8f); if (me) Haptics.Buzz(20, .35f); break;
                case Ev.Boom: Sound.Play(UnityEngine.Random.value < .5f ? "boom" : "boom2"); board.Shake(.08f); break;
                case Ev.Brick: Sound.Play("brick", .5f); board.Debris(e.x, e.y); break;
                case Ev.Kick: Sound.Play("kick"); break;
                case Ev.Power: Sound.Play("power"); board.Sparkle(e.x, e.y); if (me) Haptics.Buzz(30, .5f); break;
                case Ev.PowerBurn: Sound.Play("power_bad", .7f); break;
                case Ev.ShieldBreak: Sound.Play("hurt"); board.Shake(.12f); if (me) Haptics.Buzz(70, .8f); break;
                case Ev.Die: Sound.Play("die"); board.Shake(.2f); if (me) Haptics.Buzz(180, 1); break;
                case Ev.EnemyDie: Sound.Play("enemy_die"); board.Sparkle(e.x, e.y); break;
                case Ev.EnemyHit: Sound.Play("boss_hit", .6f); break;
                case Ev.ExitOpen: Sound.Play("exit_open"); break;
                case Ev.StageClear: break;
                case Ev.Teleport: Sound.Play("teleport", .7f); break;
                case Ev.Wind: Sound.Play("wind"); break;
                case Ev.Car: Sound.Play("car", .3f); break;
                case Ev.Hurry: Sound.Play("hurry"); app.hud.Flash("הזמן נגמר! צרעות בדרך"); break;
                case Ev.Switch: Sound.Play("switch"); break;
                case Ev.Jet: Sound.Play("jet", .5f); break;
                case Ev.Crumble: Sound.Play("crumble", .5f); break;
                case Ev.Secret: Sound.Play("secret"); board.Sparkle(e.x, e.y, "secret_token"); app.hud.Flash("מצאת סוד! 1000 נקודות"); break;
                case Ev.Reveal: Sound.Play("confirm", .6f); break;
                case Ev.BossHit: Sound.Play("boss_hit"); board.Shake(.14f); Haptics.Buzz(40, .6f); break;
                case Ev.BossRoar: Sound.Play("boss_roar", .8f); break;
                case Ev.BossWarn: Sound.Play("boss_warn"); break;
                case Ev.BossDie: Sound.Play("boss_die"); board.Shake(.4f); break;
                case Ev.Shrink: Sound.Play("shrink", .5f); board.Shake(.05f); break;
            }
        }

        void Finish()
        {
            ending = true;
            if (sim.outcome == Outcome.Cleared) { Sound.Music(null); Sound.Play("stage_clear"); endT = 1.8f; app.hud.Card("השלב הושלם!", sim.secretFound ? "וגם מצאת את הסוד" : "ניקוד " + sim.score); }
            else if (sim.outcome == Outcome.RoundOver) { app.RoundEnded(sim); endT = 2.4f; }
            else endT = .2f;
        }
    }
}
