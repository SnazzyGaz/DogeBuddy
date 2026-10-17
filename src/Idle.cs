// Idle animations that need no extra art: breathing, twerk, clench,
// heavy landing and the hover buzz. All are displacement fields fed
// into the same warp as the slaps, so clothing will move with them later too.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BigBootyCheems
{
    enum MoveKind { Twerk, Clench, Landing }

    class Move { public MoveKind Kind; public int T0; public float Dur, Amp; public bool Auto; }

    partial class Buddy
    {
        readonly List<Move> moves = new List<Move>();
        readonly Random rng = new Random();
        bool breathing = true, autoIdle = true;
        int breathT0 = Environment.TickCount, lastBreathRender, nextIdleTick;
        ToolStripMenuItem breathItem, autoItem;

        // ---------------------------------------------------------- menu + timers
        void SetupIdle()
        {
            var test = new ToolStripMenuItem("Test idles");
            test.DropDownItems.Add("Twerk", null, (s, e) => StartMove(MoveKind.Twerk, 2.4f, 1f));
            test.DropDownItems.Add("Clench", null, (s, e) => StartMove(MoveKind.Clench, 1.0f, 1f));
            test.DropDownItems.Add("Heavy landing", null, (s, e) => StartMove(MoveKind.Landing, 1.6f, 1f));
            // (the test menu isn't shown; it's kept only so BBC_TEST can drive it in testing)
            breathItem = new ToolStripMenuItem("Idle motion (breathe, sway)", null, (s, e) => { breathing = !breathing; breathItem.Checked = breathing; if (breathing) timer.Start(); else { artValid = false; Render(); } }) { Checked = true };
            autoItem = new ToolStripMenuItem("Random idles (~1 min)", null, (s, e) => { autoIdle = !autoIdle; autoItem.Checked = autoIdle; ScheduleIdle(); }) { Checked = true };
            menu.Items.Insert(0, breathItem);
            menu.Items.Insert(1, autoItem);
            menu.Items.Insert(0, new ToolStripSeparator());
            ScheduleIdle();
            // testing only: BBC_TEST=twerk,clench,... runs each one 3.5 s apart
            var list = Environment.GetEnvironmentVariable("BBC_TEST");
            if (!string.IsNullOrEmpty(list))
            {
                autoIdle = false;
                var q = new Queue<string>(list.Split(','));
                var tt = new System.Windows.Forms.Timer { Interval = 3500 };
                tt.Tick += (s, e) =>
                {
                    if (q.Count == 0) { tt.Stop(); return; }
                    foreach (ToolStripItem it in test.DropDownItems)
                        if (it.Text.ToLower().StartsWith(q.Peek())) it.PerformClick();
                    q.Dequeue();
                };
                tt.Start();
            }
        }

        void ScheduleIdle()
        {
            // testing only: BBC_IDLEFAST=1 makes random idles come every few seconds
            bool fast = Environment.GetEnvironmentVariable("BBC_IDLEFAST") == "1";
            nextIdleTick = Environment.TickCount + (fast ? 3000 + rng.Next(1000) : 40000 + rng.Next(40000));
        }

        // called from the poll timer (always running)
        void IdleTick()
        {
            int now = Environment.TickCount;
            if (breathing && !timer.Enabled) timer.Start();
            UpdateHover(now);
            UpdateBlink(now);
            AnimateClothes(now);
            // keep the audio stream warm while a slap is likely, so the first crack isn't swallowed
            var cur = Cursor.Position; var near = new Rectangle(winX - 150, winY - 150, cw + 300, ch + 300);
            if (ToolHeld || near.Contains(cur)) Mixer.Warm();
            // no butt idles while you're busy with him: dragging, carrying a tool, cursor over him,
            // or him still jiggling. Any that's running stops, and the next one waits until things settle.
            var body = new Rectangle(winX + Tune.Pad, winY + Tune.Pad, w, h);
            bool busy = leftDown || dragging || ToolHeld || InertiaActive || body.Contains(cur) || hoverLevel > 0;
            if (busy)
            {
                if (moves.RemoveAll(m => m.Auto) > 0) artValid = false;   // only the random ones; menu tests play out
                if (nextIdleTick - now < 8000) nextIdleTick = now + (Environment.GetEnvironmentVariable("BBC_IDLEFAST") == "1" ? 3000 : 8000 + rng.Next(8000));
            }
            if (autoIdle && !busy && now - nextIdleTick >= 0 && moves.Count == 0 && slaps.Count == 0)
            {
                ScheduleIdle();
                if (Environment.GetEnvironmentVariable("BBC_LOG") == "1") System.IO.File.AppendAllText("idles.log", now + " idle\r\n");
                // clench is the regular; twerk is a rarer treat
                switch (rng.Next(5))
                {
                    case 0: StartMove(MoveKind.Twerk, 2.4f, 1f, true); break;
                    default: StartMove(MoveKind.Clench, 1.0f, 1f, true); break;
                }
            }
        }

        // ---------------------------------------------------------- blinking: half -> shut -> half -> open
        // Each frame is shown for at least one render, so throttled timers can't skip the shut frame.
        int nextBlink = Environment.TickCount + 2500, blinkPhase, blinkPhaseTick; bool doubleBlink;
        static readonly int[] BlinkMs = { 0, 45, 80, 45 };     // phase 1 half, 2 shut, 3 half

        void UpdateBlink(int now)
        {
            int[] want;
            if (now - flinchUntil < 0) { want = srcShut; blinkPhase = 3; blinkPhaseTick = now; }   // half-open on the way out
            else if (EyesSqueezed) { want = srcShut; blinkPhase = 0; }
            else
            {
                if (blinkPhase == 0 && now - nextBlink >= 0) { blinkPhase = 1; blinkPhaseTick = now; }
                else if (blinkPhase > 0 && now - blinkPhaseTick >= BlinkMs[blinkPhase])
                {
                    blinkPhase = blinkPhase == 3 ? 0 : blinkPhase + 1; blinkPhaseTick = now;
                    if (blinkPhase == 0)
                    {
                        // now and then a quick double blink
                        if (!doubleBlink && rng.Next(5) == 0) { doubleBlink = true; nextBlink = now + 140; }
                        else { doubleBlink = false; nextBlink = now + 2500 + rng.Next(4500); }
                    }
                }
                want = blinkPhase == 2 ? srcShut : blinkPhase == 0 ? srcOpen : srcHalf;
            }
            if (want != src) { src = want; artValid = false; Render(); }
        }

        // eyes screwed shut while clenching
        bool EyesSqueezed
        {
            get
            {
                foreach (var m in moves)
                    if (m.Kind == MoveKind.Clench) { float t = (Environment.TickCount - m.T0) / 1000f; return t >= 0.03f && t < m.Dur * 0.6f; }
                return false;
            }
        }

        // a slap on the butt makes him screw his eyes shut for a moment
        int flinchUntil;
        void Flinch(float power) { flinchUntil = Environment.TickCount + 380 + (int)(220 * Math.Min(1.5f, power)); }

        bool IdleBusy { get { return moves.Count > 0 || hoverLevel > 0; } }

        void StartMove(MoveKind k, float dur, float amp) { StartMove(k, dur, amp, false); }
        void StartMove(MoveKind k, float dur, float amp, bool auto)
        {
            moves.RemoveAll(m => m.Kind == k);
            moves.Add(new Move { Kind = k, T0 = Environment.TickCount, Dur = dur, Amp = amp, Auto = auto });
            timer.Start();
        }

        // ---------------------------------------------------------- one-shot events
        static float Ease(float t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        // ---------------------------------------------------------- hover shimmy: the butt thrusts while the cursor rests on it
        float hoverLevel; int lastHoverTick, hoverSince, shimmyT0;
        void UpdateHover(int now)
        {
            float dt = lastHoverTick == 0 ? 0.015f : Math.Min(0.05f, (now - lastHoverTick) / 1000f);
            lastHoverTick = now;
            bool over = !leftDown && !dragging && !ToolHeld && InButt(Cursor.Position);
            if (!over) hoverSince = 0; else if (hoverSince == 0) hoverSince = now;
            // wait a moment after arriving (and after any slap) so it never fights a slap
            bool on = over && now - hoverSince > 650 && now - lastSlapTick > 900;
            if (on && hoverLevel == 0) shimmyT0 = now;
            float target = on ? 1f : 0f;
            // quick to start, a touch slower to settle
            hoverLevel += (target - hoverLevel) * Math.Min(1f, dt * (on ? 5f : 6f));
            if (!on && hoverLevel < 0.02f) hoverLevel = 0;
            if (hoverLevel > 0 && !timer.Enabled) timer.Start();
        }

        // ---------------------------------------------------------- landing after a drag
        void Landed(float speed)
        {
            // speed: how fast the window was moving just before release (px/s)
            float amp = 0.7f + Math.Min(0.8f, speed / 2500f);
            StartMove(MoveKind.Landing, 1.6f, amp);
        }

        // ---------------------------------------------------------- displacement
        // per-frame values for the active moves, worked out once before the warp
        float bScale;                         // breathing swell
        float twY, twX, clS, laS, shX, shY;

        // ---------------------------------------------------------- idle life: sway and a faint head bob
        float swayAng, pivotY, headDX, headDY, headRot, neckX, neckY;

        void PrepareIdleLife(int now)
        {
            float s = Tune.Scale;
            pivotY = Tune.FeetTop * s; neckY = Tune.HeadBottom * s; neckX = 135 * s;
            if (!breathing) { swayAng = headDX = headDY = headRot = 0; return; }
            double t = (now - breathT0) / 1000.0;
            // two slow waves so the sway never looks like a metronome (~1-2 degrees at most)
            swayAng = (float)(0.017 * Math.Sin(2 * Math.PI * t / 5.7) + 0.007 * Math.Sin(2 * Math.PI * t / 3.3 + 1.1));
            // no cursor following: just a faint idle bob of the head
            headDX = 0;
            headDY = (float)Math.Sin(2 * Math.PI * t / 4.1) * 0.6f;
            headRot = (float)Math.Sin(2 * Math.PI * t / 6.3) * 0.008f;
        }

        void PrepareMoves(int now)
        {
            PrepareIdleLife(now);
            bScale = breathing ? 0.022f * (float)Math.Sin(2 * Math.PI * (now - breathT0) / 3600.0) : 0;
            twY = twX = clS = laS = 0;
            // hover shimmy: a steady back-and-forth thrust while the cursor rests on the butt
            double ht = (now - shimmyT0) / 1000.0;
            shX = hoverLevel * 3.2f * (float)Math.Sin(2 * Math.PI * 7 * ht);
            shY = 0;
            moves.RemoveAll(m => (now - m.T0) / 1000f > m.Dur);
            foreach (var m in moves)
            {
                float t = (now - m.T0) / 1000f;
                if (t < 0) continue;
                float env = Ease(t / 0.15f) * Ease((m.Dur - t) / 0.3f) * m.Amp;
                switch (m.Kind)
                {
                    case MoveKind.Twerk:
                        // quick pops up (snap) and a slower drop, ~4 per second, with a hip sway
                        float p = (float)((t * 4.2) % 1.0);
                        float pop = p < 0.3f ? (float)Math.Sin(p / 0.3f * Math.PI / 2) : (float)Math.Cos((p - 0.3f) / 0.7f * Math.PI / 2);
                        twY += -9f * env * pop;
                        twX += 4f * env * (float)Math.Sin(2 * Math.PI * 2.1 * t);
                        break;
                    case MoveKind.Clench:
                        // squeeze in, hold, let go with a jiggle
                        float sq = t < 0.12f ? Ease(t / 0.12f) : t < m.Dur * 0.55f ? 1 : 0;
                        float rel = t >= m.Dur * 0.55f ? (float)(Math.Exp(-(t - m.Dur * 0.55f) / 0.18) * Math.Cos(2 * Math.PI * 5 * (t - m.Dur * 0.55f))) : 0;
                        clS += (-0.11f * sq + 0.06f * rel) * m.Amp;
                        break;
                    case MoveKind.Landing:
                        // squashes down onto the feet, then springs back
                        laS += -0.13f * m.Amp * (float)(Math.Exp(-t / 0.42) * Math.Cos(2 * Math.PI * 2.6 * t));
                        break;
                }
            }
        }

        bool MovesActive { get { return twY != 0 || twX != 0 || clS != 0 || laS != 0 || hoverLevel > 0 || moves.Count > 0; } }

        // x, y: canvas px relative to art; rx, ry: offset from the butt centre; m: wobbliness
        void AddMoves(float x, float y, float rx, float ry, float m, ref float dx, ref float dy)
        {
            float s = bScale + clS;
            if (s != 0) { dx += rx * s * m; dy += ry * s * 0.65f * m; }
            if (laS != 0)
            {
                // squash about the floor line: shorter and wider
                float fy = Tune.FeetTop * Tune.Scale;
                dy += (y - fy) * laS * m; dx += rx * -laS * 0.7f * m;
            }
            float b = m * m;   // butt-weighted (body joins in less)
            dx += (twX + shX) * b;
            dy += (twY + shY) * b;
        }
    }
}
