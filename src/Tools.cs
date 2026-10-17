// Tools: whip, cat o' nine tails, chancla, baseball bat and wet fish. You pick them up with the mouse (press and hold), swing at the butt,
// and drop anywhere (they fall to the taskbar and can be picked up again, or fetched from the menu).
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
    partial class Buddy
    {
        public bool ToolHeld;
        public Sfx BatSound;
        readonly Dictionary<string, Tool> toolsOut = new Dictionary<string, Tool>();

        void SetupTools()
        {
            BatSound = new Sfx(Res("bat.wav"));
            var tools = new ToolStripMenuItem("Tools");
            // picking a tool puts it out; picking it again puts it away
            var kinds = new[] { "Whip", "Cat o' Nine Tails", "Chancla", "Baseball Bat", "Wet Fish" };
            var gaps = new[] { 70, 55, 35, 50, 45 };
            var items = new List<ToolStripMenuItem>();
            for (int k = 0; k < kinds.Length; k++)
            {
                string name = kinds[k]; int gap = gaps[k];
                var it = new ToolStripMenuItem(name, null, (s, e) =>
                {
                    Tool t;
                    if (toolsOut.TryGetValue(name, out t) && !t.IsDisposed) { t.Close(); toolsOut.Remove(name); return; }
                    t = MakeTool(name); toolsOut[name] = t; t.Show(); t.Fetch(ToolSpot(gap));
                });
                items.Add(it); tools.DropDownItems.Add(it);
            }
            tools.DropDownOpening += (s, e) =>
            {
                foreach (var it in items) { Tool t; it.Checked = toolsOut.TryGetValue(it.Text, out t) && !t.IsDisposed; }
            };
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add("Put tools away", null, (s, e) =>
            {
                foreach (var t in toolsOut.Values) if (!t.IsDisposed) t.Close();
                toolsOut.Clear();
            });
            menu.Items.Insert(0, tools);
            // testing only: BBC_TOOLS=paddle,whip,bat,cat puts them out at launch
            var env = Environment.GetEnvironmentVariable("BBC_TOOLS");
            if (!string.IsNullOrEmpty(env))
            {
                var tt = new System.Windows.Forms.Timer { Interval = 800 };
                tt.Tick += (s, e) =>
                {
                    tt.Stop();
                    foreach (var it in items)
                        foreach (var w in env.Split(','))
                            if (w.Length > 0 && it.Text.ToLower().Contains(w)) it.PerformClick();
                };
                tt.Start();
            }
        }

        Tool MakeTool(string name)
        {
            switch (name)
            {
                case "Whip": return new Whip(this);
                case "Cat o' Nine Tails": return new Flogger(this);
                case "Baseball Bat": return new Bat(this);
                case "Chancla": return new Chancla(this);
                default: return new WetFish(this);
            }
        }

        // a spot on the floor just beside the cheems (left if there's room, else right)
        PointF ToolSpot(int gap)
        {
            var wa = Screen.FromPoint(new Point(winX + cw / 2, winY + ch / 2)).WorkingArea;
            float x = winX + Tune.Pad - gap;
            if (x - 150 < wa.Left) x = winX + cw - Tune.Pad + gap + 130;
            return new PointF(x, wa.Bottom);
        }

        public bool ToolTouches(PointF p) { return InButt(new Point((int)p.X, (int)p.Y)); }

        public void ToolHit(PointF p, float dir, float power, Sfx sound)
        {
            if (Environment.GetEnvironmentVariable("BBC_LOG") == "1")
                System.IO.File.AppendAllText("hits.log", Environment.TickCount + " hit " + p + " dir " + dir + " pow " + power + "\r\n");
            DoSlap((int)p.X, (int)p.Y, dir, power, sound ?? whip);
        }

        // a heavy blow squashes the whole body down for a moment, on top of the slap wobble
        public void ToolThump(float amount) { StartMove(MoveKind.Landing, 1.3f, amount); }
    }

    // ---------------------------------------------------------- shared plumbing: a layered window centred on the hand
    abstract class Tool : Form
    {
        protected readonly Buddy owner;
        protected readonly int S;                 // window is S x S, centred on the hand
        protected float hx, hy, vx, vy;           // hand point (screen) and its velocity
        protected bool held, resting = true;
        protected int lastTick, lastHit;
        readonly Queue<KeyValuePair<int, PointF>> trail = new Queue<KeyValuePair<int, PointF>>();
        IntPtr memDC, dib, bits; Bitmap bmp;
        readonly System.Windows.Forms.Timer timer;

        protected Tool(Buddy owner, int size)
        {
            this.owner = owner; S = size;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000); Size = new Size(S, S);
            var bmi = new Native.BITMAPINFO { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFO)), biWidth = S, biHeight = -S, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            dib = Native.CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, screen);
            Native.SelectObject(memDC, dib);
            bmp = new Bitmap(S, S, S * 4, PixelFormat.Format32bppPArgb, bits);
            timer = new System.Windows.Forms.Timer { Interval = 15 };
            timer.Tick += (s, e) => Step();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x8 | 0x8000000; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected Rectangle Floor { get { return Screen.FromPoint(new Point((int)hx, (int)hy)).WorkingArea; } }

        public void Fetch(PointF spot)
        {
            hx = spot.X; hy = spot.Y - RestLift; vx = vy = 0; held = false;
            PlaceAtRest(); resting = false; Wake();
        }

        protected void Wake() { lastTick = 0; if (!timer.Enabled) timer.Start(); }

        void Step()
        {
            int now = Environment.TickCount;
            float dt = lastTick == 0 ? 0.015f : Math.Max(0.004f, Math.Min(0.04f, (now - lastTick) / 1000f));
            lastTick = now;
            if (held)
            {
                var c = Cursor.Position;
                trail.Enqueue(new KeyValuePair<int, PointF>(now, c));
                while (trail.Count > 2 && now - trail.Peek().Key > 60) trail.Dequeue();
                var o = trail.Peek(); float span = Math.Max(0.015f, (now - o.Key) / 1000f);
                vx = (c.X - o.Value.X) / span; vy = (c.Y - o.Value.Y) / span;
                var gofs = Grip();
                hx = c.X + gofs.X; hy = c.Y + gofs.Y;
            }
            else if (!resting)
            {
                // tossed: falls to the taskbar with a little bounce
                var wa = Floor;
                vy += 2200 * dt; hx += vx * dt; hy += vy * dt;
                float fy = wa.Bottom - RestLift;
                if (hy > fy)
                {
                    hy = fy;
                    if (vy > 150) { vy = -vy * 0.25f; vx *= 0.6f; } else { vy = 0; vx *= 0.8f; }
                }
                if (hx < wa.Left + 10) { hx = wa.Left + 10; vx = -vx * 0.3f; }
                if (hx > wa.Right - 10) { hx = wa.Right - 10; vx = -vx * 0.3f; }
            }
            bool moving = Simulate(dt, now);
            Draw();
            if (!held && hy >= Floor.Bottom - RestLift - 0.5f && Math.Abs(vy) < 1 && Math.Abs(vx) < 20 && !moving)
            {
                resting = true; timer.Stop();
            }
        }

        void Draw()
        {
            int ox = (int)Math.Round(hx) - S / 2, oy = (int)Math.Round(hy) - S / 2;
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawTool(g, ox, oy);
            }
            Native.GdiFlush();
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            var dst = new Native.POINT { x = ox, y = oy }; var sz = new Native.SIZE { cx = S, cy = S }; var s0 = new Native.POINT();
            var bf = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref sz, memDC, ref s0, 0, ref bf, 2);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }

        // where the hand is relative to the cursor while held (the paddle is held by its blade)
        protected virtual PointF Grip() { return PointF.Empty; }
        protected abstract float RestLift { get; }          // hand height above the floor when lying down
        protected abstract void PlaceAtRest();
        protected abstract bool Simulate(float dt, int now); // returns true while still settling
        protected abstract void DrawTool(Graphics g, int ox, int oy);

        protected bool CanHit(int now) { return now - lastHit > 230; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            held = true; resting = false; owner.ToolHeld = true; Capture = true;
            trail.Clear();
            Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);   // above the cheems
            Wake();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !held) return;
            held = false; owner.ToolHeld = false; Capture = false;
            // keep some of the throw, but not enough to fling it off the screen
            float sp = (float)Math.Sqrt(vx * vx + vy * vy);
            if (sp > 1500) { vx *= 1500 / sp; vy *= 1500 / sp; }
            Wake();
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            timer.Stop();
            if (held) owner.ToolHeld = false;
            bmp.Dispose(); Native.DeleteObject(dib); Native.DeleteDC(memDC);
        }
    }

    // ---------------------------------------------------------- rigid tools: held by the handle, trail your swing, rebound on a hit
    abstract class RigidTool : Tool
    {
        protected float ang = (float)Math.PI, angV;     // direction from hand to tip
        PointF lastFace; bool haveFace;

        protected RigidTool(Buddy o, int size) : base(o, size) { }

        protected abstract float HandleLen { get; }
        protected abstract float HeadLen { get; }
        protected abstract float HeadHalfWidth { get; }   // how far it sticks up when lying down
        protected virtual float GripAt { get { return HandleLen * 0.45f; } }
        protected virtual float Stiffness { get { return 140; } }
        protected virtual float Damping { get { return 15; } }
        protected virtual float MinSpeed { get { return 650; } }
        protected virtual float PowerMin { get { return 0.9f; } }
        protected virtual float PowerMax { get { return 1.8f; } }
        protected virtual float Rebound { get { return 6; } }
        protected abstract Sfx Sound { get; }
        protected virtual void OnHit(float power) { }

        protected override float RestLift { get { return HeadHalfWidth; } }
        protected override PointF Grip() { return new PointF(-(float)Math.Cos(ang) * GripAt, -(float)Math.Sin(ang) * GripAt); }
        protected override void PlaceAtRest() { ang = (float)Math.PI; angV = 0; haveFace = false; }

        protected override bool Simulate(float dt, int now)
        {
            float target;
            if (held)
                // held up, leaning against the direction of travel
                target = -(float)Math.PI / 2 - Clamp(vx * 0.00055f, -1.25f, 1.25f) + Clamp(vy * 0.0002f, -0.3f, 0.3f) * Math.Sign(vx + 0.01f);
            else
            {
                // lying down: whichever flat side is nearer
                float a = Wrap(ang);
                target = Math.Abs(a) < Math.PI / 2 ? 0 : (a > 0 ? (float)Math.PI : -(float)Math.PI);
                ang = a;
            }
            angV += (Stiffness * Wrap(target - ang) - Damping * angV) * dt;
            ang += angV * dt;

            // the head's sweet spot
            float d = HandleLen + HeadLen * 0.55f;
            var face = new PointF(hx + (float)Math.Cos(ang) * d, hy + (float)Math.Sin(ang) * d);
            if (held && haveFace && CanHit(now))
            {
                float fvx = (face.X - lastFace.X) / dt, fvy = (face.Y - lastFace.Y) / dt;
                float sp = (float)Math.Sqrt(fvx * fvx + fvy * fvy);
                if (sp > MinSpeed)
                    for (int k = 0; k < 4; k++)   // check a few points along the head
                    {
                        float dd = HandleLen + HeadLen * (0.15f + 0.25f * k);
                        var p = new PointF(hx + (float)Math.Cos(ang) * dd, hy + (float)Math.Sin(ang) * dd);
                        if (!owner.ToolTouches(p)) continue;
                        float power = PowerMin + (PowerMax - PowerMin) * Clamp((sp - MinSpeed) / 2200f, 0, 1);
                        owner.ToolHit(p, Math.Sign(fvx), power, Sound);
                        OnHit(power);
                        lastHit = now;
                        angV = -angV * 0.5f - Math.Sign(fvx) * Rebound;    // bounce back off the cheek
                        break;
                    }
            }
            lastFace = face; haveFace = true;
            return Math.Abs(angV) > 0.05f || Math.Abs(Wrap(target - ang)) > 0.01f;
        }

        protected override void DrawTool(Graphics g, int ox, int oy)
        {
            g.TranslateTransform(hx - ox, hy - oy);
            g.RotateTransform(ang * 180 / (float)Math.PI);
            var spr = Sprite();
            if (spr != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(spr, 0, -spr.Height / 2f, spr.Width, spr.Height);
            }
            else DrawShape(g);   // +x runs from the handle end to the tip
            g.ResetTransform();
        }

        // your art, if there is any: a PNG lying sideways with the held end on the RIGHT (Tool_<name>.png).
        // It's flipped and scaled once so it runs handle -> tip along +x, (HandleLen + HeadLen) long.
        protected virtual string SpriteName { get { return null; } }
        static readonly Dictionary<string, Bitmap> sprites = new Dictionary<string, Bitmap>();
        Bitmap Sprite()
        {
            if (SpriteName == null) return null;
            Bitmap b;
            if (sprites.TryGetValue(SpriteName, out b)) return b;
            var st = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Tool_" + SpriteName + ".png");
            if (st != null)
                using (st) using (var raw = new Bitmap(st))
                {
                    float len = HandleLen + HeadLen, sc = len / raw.Width;
                    int w = (int)Math.Ceiling(len), h = (int)Math.Ceiling(raw.Height * sc);
                    b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.TranslateTransform(w, 0); g.ScaleTransform(-1, 1);
                        g.DrawImage(raw, 0, 0, len, raw.Height * sc);
                    }
                }
            sprites[SpriteName] = b;
            return b;
        }
        protected abstract void DrawShape(Graphics g);

        protected static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath(); float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        internal static float Clamp(float v, float a, float b) { return v < a ? a : v > b ? b : v; }
        internal static float Wrap(float a) { while (a > Math.PI) a -= 2 * (float)Math.PI; while (a < -Math.PI) a += 2 * (float)Math.PI; return a; }
    }

    // ---------------------------------------------------------- baseball bat: long, heavy, swings slower, hits hardest
    class Bat : RigidTool
    {
        const float HL = 48, BL = 92, Knob = 6, Thin = 4.2f, Fat = 11f;
        public Bat(Buddy o) : base(o, 320) { }
        protected override float HandleLen { get { return HL; } }
        protected override float HeadLen { get { return BL; } }
        protected override float HeadHalfWidth { get { return 6.5f; } }
        protected override float GripAt { get { return HL * 0.3f; } }
        protected override string SpriteName { get { return "bat"; } }
        protected override float Stiffness { get { return 95; } }       // heavier: lags the swing more
        protected override float Damping { get { return 11; } }
        protected override float MinSpeed { get { return 600; } }
        protected override float PowerMin { get { return 1.2f; } }
        protected override float PowerMax { get { return 2.4f; } }
        protected override float Rebound { get { return 4; } }
        protected override Sfx Sound { get { return owner.BatSound; } }
        protected override void OnHit(float power) { owner.ToolThump(0.35f + 0.25f * (power - 1.2f)); }

        protected override void DrawShape(Graphics g)
        {
            // one smooth outline: knob, thin handle, flaring out to a rounded barrel end
            var path = new GraphicsPath();
            var top = new List<PointF>(); var bot = new List<PointF>();
            for (int i = 0; i <= 40; i++)
            {
                float x = (HL + BL) * i / 40f;
                float r;
                if (x < HL * 0.6f) r = Thin;
                else { float t = (x - HL * 0.6f) / (HL + BL - HL * 0.6f); r = Thin + (Fat - Thin) * (float)Math.Pow(Math.Min(1, t * 1.35f), 1.6); }
                top.Add(new PointF(x, -r)); bot.Insert(0, new PointF(x, r));
            }
            path.AddLines(top.ToArray());
            path.AddArc(HL + BL - Fat, -Fat, Fat * 2, Fat * 2, -90, 180);
            path.AddLines(bot.ToArray());
            path.CloseFigure();
            using (var wb = new LinearGradientBrush(new PointF(0, -Fat), new PointF(0, Fat), Color.FromArgb(255, 236, 206, 160), Color.FromArgb(255, 178, 132, 80)))
                g.FillPath(wb, path);
            using (var grain = new Pen(Color.FromArgb(50, 120, 80, 40), 1f))
                for (int i = -1; i <= 1; i++) g.DrawLine(grain, HL, i * 4, HL + BL - 6, i * 4 + 1);
            // grip tape and knob
            using (var tape = new SolidBrush(Color.FromArgb(255, 40, 40, 44)))
                g.FillRectangle(tape, 2, -Thin - 0.5f, HL * 0.55f, Thin * 2 + 1);
            using (var stripe = new Pen(Color.FromArgb(120, 90, 90, 96), 1.2f))
                for (float x = 5; x < HL * 0.55f; x += 4) g.DrawLine(stripe, x, -Thin, x + 2.5f, Thin);
            using (var knob = new SolidBrush(Color.FromArgb(255, 60, 60, 66)))
                g.FillEllipse(knob, -Knob * 0.6f, -Knob, Knob * 1.4f, Knob * 2);
            using (var edge = new Pen(Color.FromArgb(160, 120, 84, 44), 1.1f)) g.DrawPath(edge, path);
            path.Dispose();
        }
    }

    // ---------------------------------------------------------- whip: a rope (verlet chain) on a short handle; the tip cracks
    class Whip : Tool
    {
        const int N = 36; const float Seg = 8f, HandleLen = 30;
        readonly float[] px = new float[N], py = new float[N], ox_ = new float[N], oy_ = new float[N];
        float ang = (float)Math.PI, angV;

        public Whip(Buddy o) : base(o, 2 * (int)(HandleLen + N * Seg) + 40) { }

        protected override float RestLift { get { return 4; } }

        protected override void PlaceAtRest()
        {
            ang = (float)Math.PI; angV = 0;
            // lay it out along the floor, away from the cheems
            for (int i = 0; i < N; i++)
            {
                px[i] = hx - HandleLen - i * Seg; py[i] = hy;
                ox_[i] = px[i]; oy_[i] = py[i];
            }
        }

        protected override bool Simulate(float dt, int now)
        {
            // handle: held pointing up and forward, leaning with the swing
            float target = held ? -(float)Math.PI / 2 - RigidTool.Clamp(vx * 0.0005f, -1.1f, 1.1f)
                                : (Math.Abs(RigidTool.Wrap(ang)) < Math.PI / 2 ? 0 : (float)Math.PI);
            ang = RigidTool.Wrap(ang);
            angV += (160 * RigidTool.Wrap(target - ang) - 16 * angV) * dt;
            ang += angV * dt;
            float bx = hx + (float)Math.Cos(ang) * HandleLen, by = hy + (float)Math.Sin(ang) * HandleLen;
            float floorY = Floor.Bottom - 2;

            const int Sub = 4;
            float h = dt / Sub, maxMove = 0;
            float pbx = px[0], pby = py[0];
            for (int s = 0; s < Sub; s++)
            {
                // the rope's base follows the handle tip smoothly through the frame
                float f = (s + 1f) / Sub;
                px[0] = pbx + (bx - pbx) * f; py[0] = pby + (by - pby) * f;
                for (int i = 1; i < N; i++)
                {
                    float nx = px[i] + (px[i] - ox_[i]) * 0.996f;
                    float ny = py[i] + (py[i] - oy_[i]) * 0.996f + 2000 * h * h;
                    ox_[i] = px[i]; oy_[i] = py[i]; px[i] = nx; py[i] = ny;
                }
                for (int it = 0; it < 14; it++)
                {
                    for (int i = 0; i < N - 1; i++)
                    {
                        float dx = px[i + 1] - px[i], dy = py[i + 1] - py[i];
                        float len = (float)Math.Sqrt(dx * dx + dy * dy) + 1e-4f;
                        float diff = (len - Seg) / len;
                        if (i == 0) { px[1] -= dx * diff; py[1] -= dy * diff; }
                        else { px[i] += dx * diff * 0.5f; py[i] += dy * diff * 0.5f; px[i + 1] -= dx * diff * 0.5f; py[i + 1] -= dy * diff * 0.5f; }
                    }
                    for (int i = 1; i < N; i++)
                        if (py[i] > floorY) { py[i] = floorY; ox_[i] = px[i] - (px[i] - ox_[i]) * 0.6f; }   // floor + friction
                }
                // tip: crack in the air when it breaks the "sound barrier", slap when it lands on the butt
                float tvx = (px[N - 1] - ox_[N - 1]) / h, tvy = (py[N - 1] - oy_[N - 1]) / h;
                float tsp = (float)Math.Sqrt(tvx * tvx + tvy * tvy);
                if (held && now - lastHit > 380)
                {
                    for (int i = N - 4; i < N; i++)
                    {
                        float vxi = (px[i] - ox_[i]) / h, vyi = (py[i] - oy_[i]) / h;
                        float spi = (float)Math.Sqrt(vxi * vxi + vyi * vyi);
                        if (spi < 1100 || !owner.ToolTouches(new PointF(px[i], py[i]))) continue;
                        float power = 0.7f + 0.8f * RigidTool.Clamp((spi - 1100) / 2600f, 0, 1);
                        owner.ToolHit(new PointF(px[i], py[i]), Math.Sign(vxi), power, null);
                        lastHit = now;
                        break;
                    }
                }
                maxMove = Math.Max(maxMove, tsp);
            }
            return maxMove > 15 || Math.Abs(angV) > 0.05f;
        }

        protected override void DrawTool(Graphics g, int ox, int oy)
        {
            float bx = hx + (float)Math.Cos(ang) * HandleLen - ox, by = hy + (float)Math.Sin(ang) * HandleLen - oy;
            // lash: thick at the handle, tapering to a thin popper
            for (int i = 0; i < N - 1; i++)
            {
                float t = (float)i / (N - 1);
                float wdt = 5.4f - 3.6f * t;
                var col = i >= N - 3 ? Color.FromArgb(255, 205, 180, 140) : Color.FromArgb(255, (int)(120 + 40 * t), (int)(74 + 26 * t), 38);
                using (var pen = new Pen(col, i >= N - 3 ? 1.3f : wdt) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, px[i] - ox, py[i] - oy, px[i + 1] - ox, py[i + 1] - oy);
            }
            // handle: dark leather with a lighter wrap and a knob at the end
            using (var pen = new Pen(Color.FromArgb(255, 48, 30, 20), 8) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, hx - ox, hy - oy, bx, by);
            using (var wrap = new Pen(Color.FromArgb(150, 150, 100, 60), 1.4f))
                for (float f = 0.15f; f < 0.9f; f += 0.14f)
                {
                    float cx = hx - ox + (bx - (hx - ox)) * f, cy = hy - oy + (by - (hy - oy)) * f;
                    float nx = -(float)Math.Sin(ang) * 4, ny = (float)Math.Cos(ang) * 4;
                    g.DrawLine(wrap, cx - nx, cy - ny, cx + nx + (float)Math.Cos(ang) * 2, cy + ny + (float)Math.Sin(ang) * 2);
                }
            using (var knob = new SolidBrush(Color.FromArgb(255, 70, 44, 28)))
                g.FillEllipse(knob, hx - ox - 5.5f, hy - oy - 5.5f, 11, 11);
        }
    }

    // ---------------------------------------------------------- cat o' nine tails: a short handle with nine leather tails
    // Each tail is its own little rope, so they fan out, trail and land in a flurry.
    class Flogger : Tool
    {
        const int Tails = 9, N = 13; const float Seg = 8f, HandleLen = 40;
        readonly float[,] px = new float[Tails, N], py = new float[Tails, N], qx = new float[Tails, N], qy = new float[Tails, N];
        readonly float[] drag = new float[Tails];
        float ang = (float)Math.PI, angV;

        public Flogger(Buddy o) : base(o, 2 * (int)(HandleLen + N * Seg) + 60)
        {
            var r = new Random();
            for (int t = 0; t < Tails; t++) drag[t] = 0.990f + 0.006f * (float)r.NextDouble();   // tails move slightly differently
        }

        protected override float RestLift { get { return 4; } }

        float Spread(int t) { return (t - (Tails - 1) / 2f) * 1.9f; }   // where each tail is tied on, across the end

        protected override void PlaceAtRest()
        {
            ang = (float)Math.PI; angV = 0;
            for (int t = 0; t < Tails; t++)
                for (int i = 0; i < N; i++)
                {
                    // fan out along the floor
                    px[t, i] = hx - HandleLen - i * Seg; py[t, i] = hy - Math.Abs(Spread(t)) * i * 0.05f;
                    qx[t, i] = px[t, i]; qy[t, i] = py[t, i];
                }
        }

        protected override bool Simulate(float dt, int now)
        {
            float target = held ? -(float)Math.PI / 2 - RigidTool.Clamp(vx * 0.0005f, -1.1f, 1.1f)
                                : (Math.Abs(RigidTool.Wrap(ang)) < Math.PI / 2 ? 0 : (float)Math.PI);
            ang = RigidTool.Wrap(ang);
            angV += (160 * RigidTool.Wrap(target - ang) - 16 * angV) * dt;
            ang += angV * dt;
            float ca = (float)Math.Cos(ang), sa = (float)Math.Sin(ang);
            float bx = hx + ca * HandleLen, by = hy + sa * HandleLen;
            float floorY = Floor.Bottom - 2;

            const int Sub = 3;
            float h = dt / Sub, maxMove = 0;
            for (int s = 0; s < Sub; s++)
            {
                float f = (s + 1f) / Sub;
                int hits = 0; float hxs = 0, hys = 0, hvx = 0, best = 0;
                for (int t = 0; t < Tails; t++)
                {
                    // tie point: spread across the end of the handle
                    float ax = bx - sa * Spread(t), ay = by + ca * Spread(t);
                    px[t, 0] += (ax - px[t, 0]) * f; py[t, 0] += (ay - py[t, 0]) * f;
                    for (int i = 1; i < N; i++)
                    {
                        float nx = px[t, i] + (px[t, i] - qx[t, i]) * drag[t];
                        float ny = py[t, i] + (py[t, i] - qy[t, i]) * drag[t] + 2000 * h * h;
                        qx[t, i] = px[t, i]; qy[t, i] = py[t, i]; px[t, i] = nx; py[t, i] = ny;
                    }
                    for (int it = 0; it < 8; it++)
                    {
                        for (int i = 0; i < N - 1; i++)
                        {
                            float dx = px[t, i + 1] - px[t, i], dy = py[t, i + 1] - py[t, i];
                            float len = (float)Math.Sqrt(dx * dx + dy * dy) + 1e-4f;
                            float diff = (len - Seg) / len;
                            if (i == 0) { px[t, 1] -= dx * diff; py[t, 1] -= dy * diff; }
                            else { px[t, i] += dx * diff * 0.5f; py[t, i] += dy * diff * 0.5f; px[t, i + 1] -= dx * diff * 0.5f; py[t, i + 1] -= dy * diff * 0.5f; }
                        }
                        for (int i = 1; i < N; i++)
                            if (py[t, i] > floorY) { py[t, i] = floorY; qx[t, i] = px[t, i] - (px[t, i] - qx[t, i]) * 0.6f; }
                    }
                    // the last couple of nodes are what land
                    for (int i = N - 2; i < N; i++)
                    {
                        float vxi = (px[t, i] - qx[t, i]) / h, vyi = (py[t, i] - qy[t, i]) / h;
                        float spi = (float)Math.Sqrt(vxi * vxi + vyi * vyi);
                        maxMove = Math.Max(maxMove, spi);
                        if (!held || spi < 650 || now - lastHit <= 300) continue;
                        if (!owner.ToolTouches(new PointF(px[t, i], py[t, i]))) continue;
                        hits++; hxs += px[t, i]; hys += py[t, i]; hvx += vxi; best = Math.Max(best, spi);
                        break;
                    }
                }
                if (hits > 0)
                {
                    // the more tails that land, the harder it hits
                    float power = (0.45f + 0.07f * hits) + 0.5f * RigidTool.Clamp((best - 650) / 2500f, 0, 1);
                    owner.ToolHit(new PointF(hxs / hits, hys / hits), Math.Sign(hvx), power, null);   // the plain slap sound
                    lastHit = now;
                }
            }
            return maxMove > 15 || Math.Abs(angV) > 0.05f;
        }

        protected override void DrawTool(Graphics g, int ox, int oy)
        {
            float bx = hx + (float)Math.Cos(ang) * HandleLen - ox, by = hy + (float)Math.Sin(ang) * HandleLen - oy;
            // tails: black leather, thinning to the ends, each with a little knot
            using (var tail = new Pen(Color.FromArgb(255, 28, 24, 24), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var shine = new Pen(Color.FromArgb(90, 120, 110, 110), 0.8f))
            using (var knot = new SolidBrush(Color.FromArgb(255, 20, 18, 18)))
                for (int t = 0; t < Tails; t++)
                {
                    var pts = new PointF[N];
                    for (int i = 0; i < N; i++) pts[i] = new PointF(px[t, i] - ox, py[t, i] - oy);
                    g.DrawLines(tail, pts);
                    g.DrawLines(shine, pts);
                    g.FillEllipse(knot, pts[N - 1].X - 2.2f, pts[N - 1].Y - 2.2f, 4.4f, 4.4f);
                }
            // handle: braided black leather, a silver ring where the tails join, and a pommel
            using (var pen = new Pen(Color.FromArgb(255, 34, 30, 30), 8) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
                g.DrawLine(pen, hx - ox, hy - oy, bx, by);
            using (var braid = new Pen(Color.FromArgb(140, 110, 100, 100), 1.2f))
                for (float f = 0.12f; f < 0.85f; f += 0.1f)
                {
                    float cx = hx - ox + (bx - (hx - ox)) * f, cy = hy - oy + (by - (hy - oy)) * f;
                    float nx = -(float)Math.Sin(ang) * 4, ny = (float)Math.Cos(ang) * 4;
                    g.DrawLine(braid, cx - nx, cy - ny, cx + nx + (float)Math.Cos(ang) * 3, cy + ny + (float)Math.Sin(ang) * 3);
                }
            using (var ring = new Pen(Color.FromArgb(255, 200, 200, 210), 2.4f))
            {
                float nx = -(float)Math.Sin(ang) * 6, ny = (float)Math.Cos(ang) * 6;
                g.DrawLine(ring, bx - nx, by - ny, bx + nx, by + ny);
            }
            using (var pommel = new SolidBrush(Color.FromArgb(255, 190, 190, 200)))
                g.FillEllipse(pommel, hx - ox - 5, hy - oy - 5, 10, 10);
        }
    }
}
