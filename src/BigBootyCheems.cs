// Big Booty Cheems - prototype: desktop buddy with a slappable, jelly-wobbly butt.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Big Booty Cheems")]
[assembly: AssemblyProduct("Big Booty Cheems")]
[assembly: AssemblyVersion("1.0.1.0")]

namespace BigBootyCheems
{
    static class Program
    {
        public static bool JustUpdated;

        [STAThread]
        static void Main(string[] args)
        {
            JustUpdated = Array.IndexOf(args, "--updated") >= 0;
            using (var mutex = new Mutex(false, "BigBootyCheems_SingleInstance"))
            using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "BigBootyCheems_Quit"))
            {
                bool owned;
                try { owned = mutex.WaitOne(JustUpdated ? 10000 : 0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    // already running: ask that copy to close, and take over (newest launch wins)
                    quit.Set();
                    try { owned = mutex.WaitOne(6000); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned)
                    {
                        MessageBox.Show("An older Big Booty Cheems is still running and didn't close.\n\nRight-click him, choose Exit, then start this one again.",
                            "Big Booty Cheems", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }
                quit.Reset();
                try
                {
                    Updater.CleanUpOld();
                    try { Native.SetProcessDPIAware(); } catch { }
                    Native.NoPowerThrottling();
                    Application.EnableVisualStyles();
                    var form = new Buddy();
                    var listener = new Thread(() =>
                    {
                        quit.WaitOne();
                        try { form.BeginInvoke((Action)form.Close); } catch { }
                    }) { IsBackground = true };
                    listener.Start();
                    Application.Run(form);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    // ---------------------------------------------------------------- tuning (in base-art pixels unless noted)
    static class Tune
    {
        public const float Scale = 0.42f;           // base canvas 450x732 -> ~189x307 on screen
        // the hero art is 450x674; clothes use a canvas extended 58 px upwards, so the hero sits that far down
        public const int CanvasW = 450, CanvasH = 732, HeroTop = 58;
        // butt lobe (ellipse) in canvas pixels
        public const float ButtCX = 340, ButtCY = 412 + HeroTop, ButtRX = 88, ButtRY = 122;
        // belly (ellipse): poke it 5 times quickly and he sprays
        public const float BellyCX = 150, BellyCY = 420 + HeroTop, BellyRX = 95, BellyRY = 100;
        public const int BellyPokes = 5, BellyWindowMs = 1200;   // max gap between pokes
        public const float HeadBottom = 245 + HeroTop;        // above this nothing wobbles
        public const float FeetTop = 545 + HeroTop;           // below this nothing wobbles (feet stay planted)
        // jelly (screen pixels / seconds)
        public const float Amp = 13f;               // first slap push (screen px)
        public const float MaxAmp = 20f;            // stacked slaps cap
        public const float Squash = 0.08f;          // cheek bulge/squish (fraction of its size)
        public const float Freq = 4.4f;             // wobbles per second
        public const float Decay = 0.45f;           // seconds to fall to ~37%
        public const float Ripple = 42f;            // ripple wavelength across the cheek
        public const float Spread = 70f;            // how far from the slap point it jiggles
        public const float BodyShare = 0.22f;       // how much the rest of the body joins in
        // drag inertia: the flesh lags behind when carried, then springs back and jiggles
        public const float DragLag = 0.55f;         // how much of each movement the butt "misses"
        public const float DragMax = 24f;           // furthest it can lag (screen px)
        public const float DragSpring = 160f;       // stiffness (higher = snappier, faster wobble)
        public const float DragDamp = 6.5f;         // how quickly the jiggle dies out
        public const int Pad = 34;                  // spare room around the art for the wobble
        // swipe-slap: the cursor sweeping across the cheek fast enough counts as a hand
        public const float SwipeSpeed = 1100f;      // px/s needed to count as a slap
        public const float SwipeFull = 3000f;       // px/s for a full-strength slap
        public const int SwipeCooldownMs = 220;     // so one swipe = one slap
    }

    class Slap { public float X, Y, Dir, Amp; public int T0; }

    partial class Buddy : Form
    {
        int w, h, cw, ch;                 // art size and canvas size (screen px)
        int[] src;                        // premultiplied ARGB art (current eyes)
        int[] srcOpen, srcHalf, srcShut;  // eyes open / half / closed
        float[] mask;                     // how wobbly each pixel is (0..1)
        float[] headMask, swayMask;       // head (turns to look) and everything above the feet (sways)
        int[] outPx;
        IntPtr memDC, dib, oldObj, bits;
        int winX, winY;
        readonly List<Slap> slaps = new List<Slap>();
        System.Windows.Forms.Timer timer, poll;
        Sfx whip;
        readonly Queue<KeyValuePair<int, Point>> dragTrail = new Queue<KeyValuePair<int, Point>>();
        bool leftDown, dragging; Point dragStart, winStart;
        ContextMenuStrip menu;

        public Buddy()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Text = "Big Booty Cheems";
            using (var s = Res("Hero1.png")) using (var raw = new Bitmap(s)) srcOpen = LoadArt(raw);
            using (var s = Res("HalfB.png")) using (var raw = new Bitmap(s)) srcHalf = LoadArt(raw);
            using (var s = Res("Blink.png")) using (var raw = new Bitmap(s)) srcShut = LoadArt(raw);
            src = srcOpen;
            whip = new Sfx(Res("whip.wav"));

            cw = w + 2 * Tune.Pad; ch = h + 2 * Tune.Pad;
            outPx = new int[cw * ch];
            CreateBuffer();
            var wa = Screen.PrimaryScreen.WorkingArea;
            winX = wa.Right - cw - 300; winY = wa.Bottom - ch + Tune.Pad;   // feet on the taskbar
            Location = new Point(winX, winY); Size = new Size(cw, ch);

            menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Check for updates...", null, (s, e) => CheckForUpdates());
            menu.Items.Add(new ToolStripMenuItem("Big Booty Cheems v" + Updater.Short(Updater.Current)) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => Close());

            timer = new System.Windows.Forms.Timer { Interval = 15 };
            timer.Tick += (s, e) =>
            {
                UpdateInertia();
                int now = Environment.TickCount;
                if (slaps.Count > 0 || InertiaActive || IdleBusy) Render();
                else if (breathing) { if (now - lastBreathRender >= 45) { lastBreathRender = now; Render(); } }
                else timer.Stop();
            };
            poll = new System.Windows.Forms.Timer { Interval = 15 };
            poll.Tick += (s, e) => { PollSwipe(); IdleTick(); };
            SetupIdle();
            SetupTools();
            SetupClothes();
            foreach (ToolStripItem it in menu.Items)
                if (it is ToolStripMenuItem && ((ToolStripMenuItem)it).HasDropDownItems) KeepOnScreen((ToolStripMenuItem)it);
            lastPoll = Cursor.Position; lastPollTick = Environment.TickCount;
        }

        // ---------------------------------------------------------- updates (only when you ask)
        bool updateBusy;

        void CheckForUpdates()
        {
            if (updateBusy) return;
            updateBusy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.Release rel = null; string err = null;
                try { rel = Updater.GetLatest(); } catch (Exception ex) { err = ex.Message; }
                BeginInvoke((Action)(() =>
                {
                    updateBusy = false;
                    if (rel == null)
                    {
                        MessageBox.Show(this, "Couldn't reach GitHub to check for updates.\n\n" + err, "Big Booty Cheems", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (rel.Version <= Updater.Current)
                    {
                        MessageBox.Show(this, "You're on the latest version (v" + Updater.Short(Updater.Current) + ").", "Big Booty Cheems", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    string notes = (rel.Notes ?? "").Trim();
                    if (notes.Length > 400) notes = notes.Substring(0, 400) + "...";
                    var ask = MessageBox.Show(this,
                        "Big Booty Cheems v" + Updater.Short(rel.Version) + " is out (you have v" + Updater.Short(Updater.Current) + ").\n\n" +
                        (notes.Length > 0 ? notes + "\n\n" : "") + "Update now? He'll restart.",
                        "Big Booty Cheems update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (ask == DialogResult.Yes) InstallUpdate(rel);
                }));
            });
        }

        void InstallUpdate(Updater.Release rel)
        {
            updateBusy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { Updater.DownloadAndSwap(rel); } catch (Exception ex) { err = ex.Message; }
                BeginInvoke((Action)(() =>
                {
                    updateBusy = false;
                    if (err != null)
                    {
                        MessageBox.Show(this, "The update didn't install, so nothing was changed.\n\n" + err, "Big Booty Cheems", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    try { Process.Start(Application.ExecutablePath, "--updated"); }
                    catch (Exception ex) { MessageBox.Show(this, "Updated, but couldn't restart: " + ex.Message, "Big Booty Cheems"); }
                    Close();
                }));
            });
        }

        // ---------------------------------------------------------- menu placement: stay on the cheems' own monitor
        void ShowMenu()
        {
            Native.SetForegroundWindow(Handle);   // lets the menu close when clicking elsewhere
            Rectangle wa = Screen.FromRectangle(new Rectangle(winX, winY, cw, ch)).WorkingArea;
            Point p = Cursor.Position;
            p.X = Math.Max(wa.Left, Math.Min(p.X, wa.Right - 1));
            p.Y = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - 1));
            Size sz = menu.GetPreferredSize(Size.Empty);
            bool right = p.X + sz.Width <= wa.Right;
            bool up = p.Y - sz.Height >= wa.Top;
            var dir = up ? (right ? ToolStripDropDownDirection.AboveRight : ToolStripDropDownDirection.AboveLeft)
                         : (right ? ToolStripDropDownDirection.BelowRight : ToolStripDropDownDirection.BelowLeft);
            menu.Show(p, dir);
        }

        // open a submenu towards the side with room, and nudge it back if it still spills over
        void KeepOnScreen(ToolStripMenuItem item)
        {
            item.DropDownOpening += (s, e) =>
            {
                Rectangle wa = Screen.FromRectangle(menu.Bounds).WorkingArea;
                int w = item.DropDown.GetPreferredSize(Size.Empty).Width;
                item.DropDownDirection = menu.Bounds.Right + w <= wa.Right
                    ? ToolStripDropDownDirection.Right : ToolStripDropDownDirection.Left;
            };
            item.DropDownOpened += (s, e) =>
            {
                Rectangle wa = Screen.FromRectangle(menu.Bounds).WorkingArea;
                Rectangle b = item.DropDown.Bounds;
                int x = Math.Max(wa.Left, Math.Min(b.X, wa.Right - b.Width));
                int y = Math.Max(wa.Top, Math.Min(b.Y, wa.Bottom - b.Height));
                if (x != b.X || y != b.Y) item.DropDown.Location = new Point(x, y);
            };
        }

        static Stream Res(string n) { return Assembly.GetExecutingAssembly().GetManifestResourceStream(n); }

        int[] LoadArt(Bitmap raw)
        {
            w = (int)Math.Round(Tune.CanvasW * Tune.Scale); h = (int)Math.Round(Tune.CanvasH * Tune.Scale);
            int[] pix;
            using (var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(raw, 0, Tune.HeroTop * Tune.Scale, raw.Width * Tune.Scale, raw.Height * Tune.Scale);
                }
                var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                pix = new int[w * h]; Marshal.Copy(d.Scan0, pix, 0, pix.Length); b.UnlockBits(d);
            }
            // wobbliness: full inside the butt lobe (soft edge), a little across the body, none on head/feet
            mask = new float[w * h]; headMask = new float[w * h]; swayMask = new float[w * h];
            float s = Tune.Scale;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float bx = x / s, by = y / s;
                    float ex = (bx - Tune.ButtCX) / Tune.ButtRX, ey = (by - Tune.ButtCY) / Tune.ButtRY;
                    float e = (float)Math.Sqrt(ex * ex + ey * ey);           // 1 = ellipse edge
                    float butt = Smooth(1.35f, 0.85f, e);                     // soft falloff past the edge
                    float vert = Smooth(Tune.HeadBottom - 40, Tune.HeadBottom + 30, by) * Smooth(Tune.FeetTop + 25, Tune.FeetTop - 35, by);
                    mask[y * w + x] = Math.Max(butt, Tune.BodyShare) * vert;
                    headMask[y * w + x] = Smooth(Tune.HeadBottom + 25, Tune.HeadBottom - 45, by);
                    swayMask[y * w + x] = Smooth(Tune.FeetTop + 30, Tune.FeetTop - 40, by);
                }
            return pix;
        }

        static float Smooth(float a, float b, float v) { float t = Math.Max(0, Math.Min(1, (v - a) / (b - a))); return t * t * (3 - 2 * t); }

        // ---------------------------------------------------------- slapping
        void DoSlap(int sx, int sy) { DoSlap(sx, sy, 0, 1f); }

        // dir: -1 push left, +1 push right, 0 = work it out from which side was hit. power scales the push.
        void DoSlap(int sx, int sy, float dir, float power) { DoSlap(sx, sy, dir, power, whip); }

        void DoSlap(int sx, int sy, float dir, float power, Sfx sound)
        {
            int now = Environment.TickCount;
            lastSlapTick = now;
            float lx = sx - winX - Tune.Pad, ly = sy - winY - Tune.Pad;
            // a click pushes away from the side it hit; a swipe pushes the way the hand was travelling
            float cx = Tune.ButtCX * Tune.Scale;
            if (dir == 0) dir = lx >= cx ? -1 : 1;
            float carried = 0;
            foreach (var p in slaps) carried += p.Amp * (float)Math.Exp(-(now - p.T0) / 1000f / Tune.Decay);
            float amp = Math.Min(Tune.MaxAmp - carried * 0.5f, Tune.Amp * power);
            if (amp < 4) amp = 4;
            slaps.Add(new Slap { X = lx, Y = ly, Dir = dir, Amp = amp, T0 = now });
            if (slaps.Count > 4) slaps.RemoveAt(0);
            try { sound.Play(); } catch { }
            Flinch(power);
            timer.Start();
            Render();
        }

        // ---------------------------------------------------------- spray
        SprayOverlay spray;

        void Spray()
        {
            // fire from deep inside the butt, low down; the spray layer sits *behind* the cheems,
            // so the stream emerges from under the art instead of appearing in mid-air
            var emit = new Point(winX + Tune.Pad + (int)((Tune.ButtCX + Tune.ButtRX * 0.30f) * Tune.Scale),
                                 winY + Tune.Pad + (int)((Tune.ButtCY + Tune.ButtRY * 0.38f) * Tune.Scale));
            if (spray != null && !spray.IsDisposed && spray.CanEmitFrom(emit))
                spray.Again(emit);       // more on top of what's already there
            else
            {
                // first one, or he's moved somewhere the old splatter can't reach: start fresh
                if (spray != null && !spray.IsDisposed) spray.Close();
                var sp = new SprayOverlay(emit, Screen.FromPoint(emit));
                spray = sp;
                sp.Hole = new Rectangle(winX, winY, cw, ch);
                sp.FrameReady = SprayFrame;
                sp.FormClosed += (s2, e2) => { if (spray == sp) { spray = null; Render(); } };   // wipe any leftover liquid from our window
                sp.Show();
                Native.SetWindowPos(sp.Handle, Handle, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);   // just below us (NOSIZE|NOMOVE|NOACTIVATE)
            }
            // recoil: the cheeks get a squishy kick, no whip sound
            int now = Environment.TickCount;
            slaps.Add(new Slap { X = emit.X - winX - Tune.Pad - 6, Y = emit.Y - winY - Tune.Pad, Dir = -1, Amp = Tune.Amp * 0.8f, T0 = now });
            timer.Start();
        }

        // liquid inside our window goes *behind* the art: out = art over liquid (premultiplied)
        bool CompositeSpray()
        {
            if (spray == null || spray.IsDisposed || dragging) return false;
            if (!spray.LiquidRect.IntersectsWith(new Rectangle(winX, winY, cw, ch))) return false;
            bool any = false;
            int[] lp = spray.Pixels; int L = spray.OverlayLeft, T = spray.OverlayTop, LW = spray.OverlayWidth, LH = spray.OverlayHeight;
            int x0 = Math.Max(0, L - winX), x1 = Math.Min(cw, L + LW - winX);
            int y0 = Math.Max(0, T - winY), y1 = Math.Min(ch, T + LH - winY);
            for (int y = y0; y < y1; y++)
            {
                int lrow = (y + winY - T) * LW + (winX - L);
                for (int x = x0; x < x1; x++)
                {
                    int liq = lp[lrow + x];
                    if (liq == 0) continue;
                    any = true;
                    int art = outPx[y * cw + x];
                    int aa = (art >> 24) & 0xFF;
                    if (aa == 255) continue;
                    if (aa == 0) { outPx[y * cw + x] = liq; continue; }
                    int k = 255 - aa, r = 0;
                    for (int sh = 0; sh < 32; sh += 8)
                        r |= Math.Min(255, ((art >> sh) & 0xFF) + (((liq >> sh) & 0xFF) * k + 127) / 255) << sh;
                    outPx[y * cw + x] = r;
                }
            }
            return any;
        }

        // ---------------------------------------------------------- drag inertia (damped spring)
        float jx, jy, jvx, jvy; int lastWinX = int.MinValue, lastWinY, lastJTick;
        bool InertiaActive { get { return Math.Abs(jx) + Math.Abs(jy) > 0.15f || Math.Abs(jvx) + Math.Abs(jvy) > 2f; } }

        void UpdateInertia()
        {
            int now = Environment.TickCount;
            float dt = lastJTick == 0 ? 0.016f : Math.Max(0.001f, Math.Min(0.04f, (now - lastJTick) / 1000f));
            lastJTick = now;
            if (lastWinX == int.MinValue) { lastWinX = winX; lastWinY = winY; }
            int mx = winX - lastWinX, my = winY - lastWinY;
            lastWinX = winX; lastWinY = winY;
            // the window jumped by (mx, my): the flesh stays behind for a moment
            jx -= mx * Tune.DragLag; jy -= my * Tune.DragLag;
            float len = (float)Math.Sqrt(jx * jx + jy * jy);
            if (len > Tune.DragMax) { jx *= Tune.DragMax / len; jy *= Tune.DragMax / len; }
            // spring it back, with a bit of overshoot so it jiggles when you stop
            jvx += (-Tune.DragSpring * jx - Tune.DragDamp * jvx) * dt;
            jvy += (-Tune.DragSpring * jy - Tune.DragDamp * jvy) * dt;
            jx += jvx * dt; jy += jvy * dt;
            if (!dragging && !InertiaActive) { jx = jy = jvx = jvy = 0; }
        }

        // ---------------------------------------------------------- swipe detection (polled, so fast swipes can't skip past)
        int lastSlapTick, lastPollTick; Point lastPoll;
        // recent cursor samples, so speed is measured over ~50 ms rather than between two (possibly bunched-up) polls
        readonly Queue<KeyValuePair<int, Point>> trail = new Queue<KeyValuePair<int, Point>>();

        void PollSwipe()
        {
            int now = Environment.TickCount;
            Point c = Cursor.Position;
            Point p0 = lastPoll;
            int dt0 = now - lastPollTick;
            lastPoll = c; lastPollTick = now;
            trail.Enqueue(new KeyValuePair<int, Point>(now, c));
            while (trail.Count > 2 && now - trail.Peek().Key > 120) trail.Dequeue();
            if (dt0 <= 0 || dt0 > 100 || leftDown || dragging || ToolHeld || c == p0) return;
            // oldest sample at least 50 ms back (or the oldest we have)
            KeyValuePair<int, Point> old = trail.Peek();
            foreach (var t in trail) { if (now - t.Key >= 50) old = t; else break; }
            int span = now - old.Key;
            if (span < 30) return;
            float vx = (c.X - old.Value.X), vy = (c.Y - old.Value.Y);
            float speed = (float)Math.Sqrt(vx * vx + vy * vy) * 1000f / span;
            if (speed < Tune.SwipeSpeed || now - lastSlapTick < Tune.SwipeCooldownMs) return;
            float dx = c.X - p0.X, dy = c.Y - p0.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            // walk the path since the last poll; the first point on the cheek is where the hand lands
            int steps = Math.Max(1, (int)(len / 3));
            for (int i = 0; i <= steps; i++)
            {
                var q = new Point((int)(p0.X + dx * i / steps), (int)(p0.Y + dy * i / steps));
                if (!InButt(q)) continue;
                float power = 0.6f + 0.8f * Math.Min(1f, (speed - Tune.SwipeSpeed) / (Tune.SwipeFull - Tune.SwipeSpeed));
                DoSlap(q.X, q.Y, Math.Abs(vx) >= 1 ? Math.Sign(vx) : 0, power);
                return;
            }
        }

        // ---------------------------------------------------------- belly pokes
        int pokes, lastPoke;

        bool InBelly(Point screen)
        {
            int x = screen.X - winX - Tune.Pad, y = screen.Y - winY - Tune.Pad;
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            if (((src[y * w + x] >> 24) & 0xFF) < 40) return false;
            float bx = x / Tune.Scale, by = y / Tune.Scale;
            float ex = (bx - Tune.BellyCX) / Tune.BellyRX, ey = (by - Tune.BellyCY) / Tune.BellyRY;
            return ex * ex + ey * ey <= 1f;
        }

        void PokeBelly(Point p)
        {
            int now = Environment.TickCount;
            pokes = now - lastPoke <= Tune.BellyWindowMs ? pokes + 1 : 1;
            lastPoke = now;
            // a soft squish where you poked, no slap sound
            slaps.Add(new Slap { X = p.X - winX - Tune.Pad, Y = p.Y - winY - Tune.Pad, Dir = 1, Amp = Tune.Amp * 0.45f, T0 = now });
            if (slaps.Count > 4) slaps.RemoveAt(0);
            timer.Start();
            if (pokes >= Tune.BellyPokes) { pokes = 0; Spray(); }
        }

        bool InButt(Point screen)
        {
            int x = screen.X - winX - Tune.Pad, y = screen.Y - winY - Tune.Pad;
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            if (((src[y * w + x] >> 24) & 0xFF) < 40) return false;
            float bx = x / Tune.Scale, by = y / Tune.Scale;
            float ex = (bx - Tune.ButtCX) / Tune.ButtRX, ey = (by - Tune.ButtCY) / Tune.ButtRY;
            return ex * ex + ey * ey <= 1.1f;
        }

        // ---------------------------------------------------------- rendering (inverse warp, bilinear)
        int[] artPx;            // last warped art (no liquid); reused while the cheems is still
        bool artValid, hadLiquid;

        void Render()
        {
            int now = Environment.TickCount;
            slaps.RemoveAll(p => (now - p.T0) > Tune.Decay * 1000 * 6);
            PrepareMoves(now);
            bool wobbling = slaps.Count > 0 || InertiaActive || MovesActive || bScale != 0;
            if (artPx == null) artPx = new int[cw * ch];
            if (wobbling || !artValid) { WarpArt(now); CompositeHat(); artValid = !wobbling; }
            Array.Copy(artPx, outPx, outPx.Length);
            hadLiquid = CompositeSpray();
            Marshal.Copy(outPx, 0, bits, outPx.Length);
            Push();
        }

        // called by the spray each frame: skip entirely if no liquid is near us and none was last frame
        void SprayFrame()
        {
            if (spray == null) return;
            // while carried, the overlay draws all the liquid itself (it sits just below us) so
            // nothing has to stay in step with a moving window
            if (dragging) { spray.Hole = Rectangle.Empty; return; }
            spray.Hole = new Rectangle(winX, winY, cw, ch);
            bool near = spray.LiquidRect.IntersectsWith(spray.Hole);
            if (near || hadLiquid || slaps.Count > 0) Render();
        }

        void WarpArt(int now)
        {
            var outPx = artPx;   // warp into the cache
            Array.Clear(outPx, 0, outPx.Length);
            float w2 = 2 * (float)Math.PI * Tune.Freq;
            var live = slaps.ToArray();
            float[] env = new float[live.Length], ph = new float[live.Length];
            for (int i = 0; i < live.Length; i++)
            {
                float t = (now - live[i].T0) / 1000f;
                if (t < 0) { env[i] = 0; continue; }   // scheduled for later
                env[i] = live[i].Amp * (float)Math.Exp(-t / Tune.Decay) * Math.Min(1f, t / 0.03f);  // 30 ms attack: the hit
                ph[i] = w2 * t;
            }
            float inv2s2 = 1f / (2 * Tune.Spread * Tune.Spread);
            float cx = Tune.ButtCX * Tune.Scale, cy = Tune.ButtCY * Tune.Scale;
            // The wobble is smooth, so work it out on a coarse grid (every G px) and blend between grid points:
            // ~16x fewer sin/exp calls than doing every pixel.
            const int G = 4;
            int x0 = -Tune.Pad, y0 = -Tune.Pad, nx = (w + 2 * Tune.Pad) / G + 2, ny = (h + 2 * Tune.Pad) / G + 2;
            if (gdx == null || gdx.Length < nx * ny) { gdx = new float[nx * ny]; gdy = new float[nx * ny]; }
            for (int j = 0; j < ny; j++)
            {
                int y = y0 + j * G, my = Math.Max(0, Math.Min(h - 1, y));
                for (int i2 = 0; i2 < nx; i2++)
                {
                    int x = x0 + i2 * G, mx = Math.Max(0, Math.Min(w - 1, x));
                    float m = mask[my * w + mx];
                    float dx = 0, dy = 0;
                    // idle life: the whole body leans from the feet, and the head turns towards the cursor
                    float sm = swayMask[my * w + mx], hm = headMask[my * w + mx];
                    if (sm > 0.001f) dx += swayAng * (pivotY - y) * sm;
                    if (hm > 0.001f)
                    {
                        dx += (headDX - headRot * (y - neckY)) * hm;
                        dy += (headDY + headRot * (x - neckX)) * hm;
                    }
                    if (m > 0.001f)
                    {
                        // carried: flesh trails behind the movement
                        dx += jx * m; dy += jy * m * 0.75f;
                        float rx = x - cx, ry = y - cy;
                        for (int i = 0; i < live.Length; i++)
                        {
                            if (env[i] < 0.05f) continue;
                            float ddx = x - live[i].X, ddy = y - live[i].Y;
                            float d2 = ddx * ddx + ddy * ddy, dist = (float)Math.Sqrt(d2);
                            float near = (float)Math.Exp(-d2 * inv2s2);
                            float wgt = m * (0.4f + 0.6f * near);
                            float wave = (float)Math.Sin(ph[i] - dist / Tune.Ripple);
                            // sideways slosh away from the hand + a little vertical bounce
                            dx += live[i].Dir * env[i] * wgt * wave;
                            dy += env[i] * wgt * 0.4f * (float)Math.Cos(ph[i] * 1.07f - dist / Tune.Ripple);
                            // squash & stretch: the cheek squishes on impact, then rebounds
                            float sq = -Tune.Squash * (env[i] / Tune.Amp) * m * (float)Math.Sin(ph[i]);
                            dx += rx * sq; dy += ry * sq * 0.7f;
                        }
                        AddMoves(x, y, rx, ry, m, ref dx, ref dy);
                    }
                    gdx[j * nx + i2] = dx; gdy[j * nx + i2] = dy;
                }
            }
            for (int y = -Tune.Pad; y < h + Tune.Pad; y++)
            {
                float fyg = (float)(y - y0) / G; int j = (int)fyg; float ty = fyg - j;
                if (j >= ny - 1) { j = ny - 2; ty = 1; }
                int row = (y + Tune.Pad) * cw + Tune.Pad;
                for (int x = -Tune.Pad; x < w + Tune.Pad; x++)
                {
                    float fxg = (float)(x - x0) / G; int i2 = (int)fxg; float tx = fxg - i2;
                    if (i2 >= nx - 1) { i2 = nx - 2; tx = 1; }
                    int k = j * nx + i2;
                    float dx = (gdx[k] * (1 - tx) + gdx[k + 1] * tx) * (1 - ty) + (gdx[k + nx] * (1 - tx) + gdx[k + nx + 1] * tx) * ty;
                    float dy = (gdy[k] * (1 - tx) + gdy[k + 1] * tx) * (1 - ty) + (gdy[k + nx] * (1 - tx) + gdy[k + nx + 1] * tx) * ty;
                    bool inside = x >= 0 && y >= 0 && x < w && y < h;
                    if (dx * dx + dy * dy < 0.0025f)
                    {
                        if (inside) outPx[row + x] = src[y * w + x];   // not moving here: plain copy
                        continue;
                    }
                    outPx[row + x] = Sample(x - dx, y - dy);
                }
            }
        }
        float[] gdx, gdy;

        int Sample(float fx, float fy)
        {
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int a = Px(x0, y0), b = Px(x0 + 1, y0), c = Px(x0, y0 + 1), d = Px(x0 + 1, y0 + 1);
            if ((a | b | c | d) == 0) return 0;
            int r = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                float va = (a >> sh) & 0xFF, vb = (b >> sh) & 0xFF, vc = (c >> sh) & 0xFF, vd = (d >> sh) & 0xFF;
                float v = (va * (1 - tx) + vb * tx) * (1 - ty) + (vc * (1 - tx) + vd * tx) * ty;
                r |= ((int)(v + 0.5f) & 0xFF) << sh;
            }
            return r;
        }
        int Px(int x, int y) { return (x < 0 || y < 0 || x >= w || y >= h) ? 0 : src[y * w + x]; }

        // ---------------------------------------------------------- layered window plumbing
        void CreateBuffer()
        {
            var bmi = new Native.BITMAPINFO { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFO)), biWidth = cw, biHeight = -ch, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            dib = Native.CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, screen);
            oldObj = Native.SelectObject(memDC, dib);
        }

        void Push()
        {
            Native.GdiFlush();
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            var dst = new Native.POINT { x = winX, y = winY }; var sz = new Native.SIZE { cx = cw, cy = ch }; var s0 = new Native.POINT();
            var bf = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref sz, memDC, ref s0, 0, ref bf, 2);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x8 | 0x8000000; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e); Render(); poll.Start();
            // testing only: BBC_AUTOSPRAY=1 fires the spray 3 s after launch
            if (Environment.GetEnvironmentVariable("BBC_AUTOSPRAY") == "1")
            {
                var t = new System.Windows.Forms.Timer { Interval = 3000 };
                int shots = 0;
                t.Tick += (s2, e2) => { Spray(); t.Interval = 2500; if (++shots >= 3) t.Stop(); };   // three bursts, 2.5 s apart
                t.Start();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { leftDown = true; dragging = false; dragStart = Cursor.Position; winStart = new Point(winX, winY); Capture = true; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!leftDown) return;
            var c = Cursor.Position;
            if (!dragging && (Math.Abs(c.X - dragStart.X) > 3 || Math.Abs(c.Y - dragStart.Y) > 3))
            {
                dragging = true;
                if (spray != null && !spray.IsDisposed) { spray.Hole = Rectangle.Empty; spray.RePush(); Render(); }   // liquid out of our window
            }
            if (dragging)
            {
                winX = winStart.X + c.X - dragStart.X; winY = winStart.Y + c.Y - dragStart.Y;
                dragTrail.Enqueue(new KeyValuePair<int, Point>(Environment.TickCount, c));
                while (dragTrail.Count > 2 && Environment.TickCount - dragTrail.Peek().Key > 120) dragTrail.Dequeue();
                Native.SetWindowPos(Handle, new IntPtr(-1), winX, winY, cw, ch, 0x10);
                Push();
                timer.Start();   // inertia wobble runs on the render timer
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                Capture = false; leftDown = false;
                if (!dragging && InButt(Cursor.Position)) DoSlap(Cursor.Position.X, Cursor.Position.Y);
                else if (!dragging && InBelly(Cursor.Position)) PokeBelly(Cursor.Position);
                if (dragging)
                {
                    // how fast it was moving when let go (cursor still for a while = gentle drop)
                    float speed = 0; int now = Environment.TickCount;
                    while (dragTrail.Count > 0 && now - dragTrail.Peek().Key > 120) dragTrail.Dequeue();
                    if (dragTrail.Count > 0)
                    {
                        var o = dragTrail.Peek(); var c = Cursor.Position; int span = Math.Max(16, now - o.Key);
                        speed = (float)Math.Sqrt((c.X - o.Value.X) * (c.X - o.Value.X) + (c.Y - o.Value.Y) * (c.Y - o.Value.Y)) * 1000f / span;
                    }
                    dragTrail.Clear();
                    Landed(speed);
                }
                bool wasDragging = dragging;
                dragging = false;
                if (wasDragging && spray != null && !spray.IsDisposed)
                {
                    // back to drawing the liquid behind our own art
                    Render(); spray.Hole = new Rectangle(winX, winY, cw, ch); spray.RePush();
                }
            }
            else if (e.Button == MouseButtons.Right) ShowMenu();
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant, bmiColors;
        }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE i, int size);
        public static void NoPowerThrottling()
        {
            try { var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 0 }; SetProcessInformation(GetCurrentProcess(), 4, ref s, 12); } catch { }
        }
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pd, ref SIZE sz, IntPtr src, ref POINT ps, int key, ref BLENDFUNCTION bf, int flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint u, out IntPtr bits, IntPtr s, uint o);
    }
}
