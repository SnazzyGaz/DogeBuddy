// Spray: a click-through overlay that fires a liquid splatter at the nearest right wall,
// lets it slump down, pool, and fade. Drops are blended as metaballs so they read as one goo.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BigBootyCheems
{
    class SprayOverlay : Form
    {
        // ---- tuning (screen px, seconds)
        const int MaxDrops = 1000;                        // room for a few bursts; the oldest pooled drops get recycled
        const float EmitSecs = 0.55f, EmitRate = 620f;   // drops per second while spraying
        const float Gravity = 1700f;
        const float SpeedMin = 1500f, SpeedMax = 2300f;
        const float FadeAfter = 5.5f, FadeOver = 3.0f;   // after the spray starts
        const float Thresh = 0.55f;                       // metaball surface level
        const int Cell = 2;                               // field resolution (screen px per cell)

        // drops
        float[] x = new float[MaxDrops], y = new float[MaxDrops], vx = new float[MaxDrops], vy = new float[MaxDrops];
        float[] r = new float[MaxDrops], slide = new float[MaxDrops];
        byte[] state = new byte[MaxDrops];               // 0 unused, 1 flying, 2 on wall, 3 on floor
        int count;
        readonly Random rng = new Random();

        // overlay geometry (screen)
        int left, top, ow, oh, wallX, floorY;
        float emitX, emitY, emitted;
        int startTick, lastTick, burstTick;

        // field + output
        int fw, fh; float[] field;
        int[] px;
        IntPtr memDC, dib, bits;
        int dirtyX0, dirtyY0, dirtyX1, dirtyY1;          // last drawn area (overlay px)
        Timer timer;
        public bool Finished { get; private set; }
        // The cheems draws whatever liquid falls inside its own window (behind its art, same frame),
        // so this overlay leaves that rectangle empty. FrameReady lets the cheems redraw in lock-step.
        public Rectangle Hole;
        public Action FrameReady;
        public int[] Pixels { get { return px; } }
        // where the liquid is this frame, in screen coordinates (empty when there's none)
        public Rectangle LiquidRect { get { return dirtyX1 > dirtyX0 && dirtyY1 > dirtyY0 ? new Rectangle(left + dirtyX0, top + dirtyY0, dirtyX1 - dirtyX0, dirtyY1 - dirtyY0) : Rectangle.Empty; } }
        public int OverlayLeft { get { return left; } }
        public int OverlayTop { get { return top; } }
        public int OverlayWidth { get { return ow; } }
        public int OverlayHeight { get { return oh; } }
        int[] zeros;
        // profiling (BBC_PROFILE=1): ms spent per stage, logged once per second
        public static bool Profile = Environment.GetEnvironmentVariable("BBC_PROFILE") == "1";
        public static double TSim, TField, TShade, TBuddy, TPush; public static int Frames;
        static Stopwatch sw = Stopwatch.StartNew();
        static double Now() { return sw.Elapsed.TotalMilliseconds; }
        public static void Log(string extra)
        {
            if (!Profile || Frames == 0) return;
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "bbc-profile.txt"),
                string.Format("frames={0} sim={1:F2} field={2:F2} shade={3:F2} buddy={4:F2} push={5:F2} ms/frame {6}\r\n",
                Frames, TSim / Frames, TField / Frames, TShade / Frames, TBuddy / Frames, TPush / Frames, extra));
            TSim = TField = TShade = TBuddy = TPush = 0; Frames = 0;
        }
        double tA;

        public SprayOverlay(Point emit, Screen screen)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            var wa = screen.WorkingArea;
            wallX = screen.Bounds.Right;                 // the monitor's right edge
            floorY = wa.Bottom;                          // land on top of the taskbar
            emitX = emit.X; emitY = emit.Y;
            left = Math.Max(screen.Bounds.Left, emit.X - 120);
            top = screen.Bounds.Top;
            ow = Math.Max(40, wallX - left); oh = Math.Max(40, floorY - top);
            fw = ow / Cell + 2; fh = oh / Cell + 2;
            field = new float[fw * fh];
            px = new int[ow * oh];
            Location = new Point(left, top); Size = new Size(ow, oh);

            var bmi = new Native.BITMAPINFO { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFO)), biWidth = ow, biHeight = -oh, biPlanes = 1, biBitCount = 32 };
            IntPtr sdc = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(sdc);
            dib = Native.CreateDIBSection(sdc, ref bmi, 0, out bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, sdc);
            Native.SelectObject(memDC, dib);

            startTick = lastTick = burstTick = Environment.TickCount;
            timer = new Timer { Interval = 16 };
            timer.Tick += (s, e) => Step();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // layered + click-through + no taskbar button + always on top + never takes focus
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8 | 0x8000000;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnLoad(EventArgs e) { base.OnLoad(e); timer.Start(); }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (memDC != IntPtr.Zero) { Native.DeleteObject(dib); Native.DeleteDC(memDC); memDC = IntPtr.Zero; }
            base.OnFormClosed(e);
        }

        float Rand(float a, float b) { return a + (float)rng.NextDouble() * (b - a); }

        // another burst while the last one is still on screen: works if it comes from inside this overlay
        public bool CanEmitFrom(Point p) { return !IsDisposed && p.X >= left + 20 && p.X < wallX - 20 && p.Y >= top && p.Y < floorY; }

        public void Again(Point emit)
        {
            emitX = emit.X; emitY = emit.Y; emitted = 0;
            burstTick = startTick = Environment.TickCount;     // restarts the emission and the fade-out clock
        }

        // make room for n new drops: drop the dead ones, then the oldest pooled ones
        void MakeRoom(int n)
        {
            if (count + n <= MaxDrops) return;
            int j = 0;
            for (int i = 0; i < count; i++) if (state[i] != 0) MoveDrop(i, j++);
            count = j;
            int need = count + n - MaxDrops;
            if (need <= 0) return;
            j = 0;
            for (int i = 0; i < count; i++)
            {
                if (need > 0 && state[i] == 3) { need--; continue; }
                MoveDrop(i, j++);
            }
            count = j;
        }
        void MoveDrop(int a, int b)
        {
            if (a == b) return;
            x[b] = x[a]; y[b] = y[a]; vx[b] = vx[a]; vy[b] = vy[a]; r[b] = r[a]; slide[b] = slide[a]; state[b] = state[a];
        }

        void Emit(int n, float dt)
        {
            MakeRoom(n);
            for (int k = 0; k < n && count < MaxDrops; k++)
            {
                int i = count++;
                float sp = Rand(SpeedMin, SpeedMax);
                float ang = Rand(-0.22f, 0.10f);             // mostly flat, a bit of upward spread
                vx[i] = sp * (float)Math.Cos(ang); vy[i] = sp * (float)Math.Sin(ang);
                // spread this frame's drops along the path they'd have covered since last frame,
                // so the stream stays joined to the butt instead of leaving a gap
                float tau = (float)k / Math.Max(1, n) * dt + Rand(0, dt / Math.Max(1, n));
                x[i] = emitX + Rand(-3, 3) + vx[i] * tau; y[i] = emitY + Rand(-6, 6) + vy[i] * tau + 0.5f * Gravity * tau * tau;
                vy[i] += Gravity * tau;
                r[i] = Rand(3.5f, 7.5f);
                slide[i] = rng.NextDouble() < 0.15 ? Rand(80, 150) : Rand(8, 38);     // slow slump, a few runny drips
                state[i] = 1;
            }
        }

        void Step()
        {
            tA = Now();
            int now = Environment.TickCount;
            float dt = Math.Min(0.033f, Math.Max(0.001f, (now - lastTick) / 1000f));
            lastTick = now;
            float age = (now - startTick) / 1000f, bage = (now - burstTick) / 1000f;

            for (int i = 0; i < count; i++)
            {
                switch (state[i])
                {
                    case 1: // flying
                        vy[i] += Gravity * dt;
                        vx[i] *= 1 - 0.15f * dt;
                        x[i] += vx[i] * dt; y[i] += vy[i] * dt;
                        if (x[i] + r[i] * 0.5f >= wallX)
                        {
                            // splat: flatten onto the wall and spread a little; a few spatter back off it
                            x[i] = wallX - r[i] * Rand(0.2f, 2.2f) - Rand(0, 6);   // builds up some thickness
                            if (rng.NextDouble() < 0.12)
                            {
                                vx[i] = -Rand(120, 380); vy[i] = -Rand(80, 320); r[i] *= 0.55f;
                                x[i] -= 4;
                            }
                            else { state[i] = 2; vx[i] = 0; vy[i] = vy[i] * 0.2f + Rand(-260, 260); r[i] *= 1.25f; }   // splat spreads up and down
                        }
                        else if (y[i] >= floorY - r[i] * 0.5f) { state[i] = 3; y[i] = floorY - r[i] * 0.5f; vy[i] = 0; vx[i] *= 0.25f; }
                        else if (x[i] < left - 40 || y[i] > floorY + 40) state[i] = 0;
                        break;
                    case 2: // sliding down the wall, thick and slow
                        vy[i] += (slide[i] - vy[i]) * Math.Min(1f, dt * 3.5f);   // splash spread dies off, then it slumps
                        y[i] += vy[i] * dt;
                        x[i] += (wallX - r[i] * 0.45f - x[i]) * Math.Min(1f, dt * 0.6f);   // slowly sags onto the wall
                        if (y[i] >= floorY - r[i] * 0.5f) { state[i] = 3; y[i] = floorY - r[i] * 0.5f; vx[i] = -Rand(5, 40); }
                        break;
                    case 3: // pooled on the floor, spreading out a touch
                        x[i] += vx[i] * dt; vx[i] *= 1 - 3f * dt;
                        if (x[i] > wallX - r[i] * 0.4f) x[i] = wallX - r[i] * 0.4f;
                        break;
                }
            }

            if (bage < EmitSecs + dt)
            {
                float want = Math.Min(EmitSecs, bage) * EmitRate;
                int n = (int)(want - emitted); if (n > 0) { Emit(n, dt); emitted += n; }
            }

            float fade = age < FadeAfter ? 1f : Math.Max(0f, 1f - (age - FadeAfter) / FadeOver);
            if (fade <= 0f) { Finished = true; Close(); return; }
            double t1 = Now(); TSim += t1 - tA;
            Draw(fade);
            Frames++; if (Frames >= 30) Log("overlay " + ow + "x" + oh);
        }

        // ---------------------------------------------------------- metaball render
        void Draw(float fade)
        {
            // clear last frame's area
            if (dirtyX1 > dirtyX0)
                for (int yy = dirtyY0; yy < dirtyY1; yy++) Array.Clear(px, yy * ow + dirtyX0, dirtyX1 - dirtyX0);
            // field bounds this frame
            int fx0 = fw, fy0 = fh, fx1 = 0, fy1 = 0;
            for (int i = 0; i < count; i++)
            {
                if (state[i] == 0) continue;
                float R = r[i] * 2.3f + 6;
                int a0 = (int)((x[i] - left - R) / Cell), a1 = (int)((x[i] - left + R) / Cell) + 1;
                int b0 = (int)((y[i] - top - R * 2.5f) / Cell), b1 = (int)((y[i] - top + R * 2.5f) / Cell) + 1;
                fx0 = Math.Min(fx0, Math.Max(0, a0)); fx1 = Math.Max(fx1, Math.Min(fw, a1));
                fy0 = Math.Min(fy0, Math.Max(0, b0)); fy1 = Math.Max(fy1, Math.Min(fh, b1));
            }
            if (fx1 <= fx0 || fy1 <= fy0) { dirtyX0 = dirtyX1 = dirtyY0 = dirtyY1 = 0; if (FrameReady != null) FrameReady(); Push(); return; }
            double tf = Now();
            for (int yy = fy0; yy < fy1; yy++) Array.Clear(field, yy * fw + fx0, fx1 - fx0);

            for (int i = 0; i < count; i++)
            {
                if (state[i] == 0) continue;
                float rr = r[i] * (0.75f + 0.25f * fade);
                float R = rr * 2.3f;
                // stretch along motion: streaks in flight, drips on the wall, flat puddles on the floor
                float sx = 1, sy = 1;
                if (state[i] == 1) { float sp = (float)Math.Sqrt(vx[i] * vx[i] + vy[i] * vy[i]); sx = 1 + sp / 900f; }
                else if (state[i] == 2) sy = 1 + Math.Abs(vy[i]) / 45f;
                else { sx = 1.8f; sy = 0.6f; }
                float ux = 1, uy = 0;
                if (state[i] == 1) { float l = (float)Math.Sqrt(vx[i] * vx[i] + vy[i] * vy[i]) + 1e-3f; ux = vx[i] / l; uy = vy[i] / l; }
                float ext = R * Math.Max(sx, sy);
                int a0 = Math.Max(fx0, (int)((x[i] - left - ext) / Cell)), a1 = Math.Min(fx1, (int)((x[i] - left + ext) / Cell) + 1);
                int b0 = Math.Max(fy0, (int)((y[i] - top - ext) / Cell)), b1 = Math.Min(fy1, (int)((y[i] - top + ext) / Cell) + 1);
                float invR2 = 1f / (R * R);
                for (int b = b0; b < b1; b++)
                {
                    float dy = b * Cell + top - y[i];
                    for (int a = a0; a < a1; a++)
                    {
                        float dx = a * Cell + left - x[i];
                        float p, q;
                        if (state[i] == 1) { p = (dx * ux + dy * uy) / sx; q = -dx * uy + dy * ux; }
                        else { p = dx / sx; q = dy / sy; }
                        float d2 = (p * p + q * q) * invR2;
                        if (d2 >= 1f) continue;
                        float k = 1 - d2; field[b * fw + a] += k * k * k;
                    }
                }
            }

            double ts = Now(); TField += ts - tf;
            // shade: surface where field crosses the threshold, lit from the top-left, glossy highlight
            int ox0 = fx0 * Cell, ox1 = Math.Min(ow, fx1 * Cell), oy0 = fy0 * Cell, oy1 = Math.Min(oh, fy1 * Cell);
            for (int oy = oy0; oy < oy1; oy++)
            {
                float gy = (float)oy / Cell; int b = (int)gy; float ty = gy - b;
                if (b < 1 || b >= fh - 2) continue;
                for (int ox = ox0; ox < ox1; ox++)
                {
                    float gx = (float)ox / Cell; int a = (int)gx; float tx = gx - a;
                    if (a < 1 || a >= fw - 2) continue;
                    int i0 = b * fw + a;
                    float f00 = field[i0], f10 = field[i0 + 1], f01 = field[i0 + fw], f11 = field[i0 + fw + 1];
                    if (f00 + f10 + f01 + f11 < (Thresh - 0.12f) * 2f) continue;
                    float f = (f00 * (1 - tx) + f10 * tx) * (1 - ty) + (f01 * (1 - tx) + f11 * tx) * ty;
                    float al = Smooth(Thresh - 0.06f, Thresh + 0.06f, f);
                    if (al <= 0.01f) continue;
                    float nx = (field[i0 + 1] - field[i0 - 1]), ny = (field[i0 + fw] - field[i0 - fw]);
                    float nl = (float)Math.Sqrt(nx * nx + ny * ny + 0.08f);
                    nx /= nl; ny /= nl;
                    float lit = -(nx * -0.6f + ny * -0.8f);                 // light from top-left
                    float depth = Math.Min(1f, (f - Thresh) * 0.9f);
                    float shade = 0.72f + 0.35f * lit;
                    float cr = (128 - 52 * depth) * shade, cg = (80 - 34 * depth) * shade, cb = (36 - 16 * depth) * shade;
                    float spec = Math.Max(0f, lit); spec = spec * spec * spec * spec * spec * spec * 0.55f * Smooth(Thresh, Thresh + 0.35f, f);
                    cr += 255 * spec; cg += 235 * spec; cb += 200 * spec;
                    float A = al * fade * 0.97f;
                    int ia = (int)(A * 255);
                    int ir = Clamp((int)(cr * A)), ig = Clamp((int)(cg * A)), ib = Clamp((int)(cb * A));
                    px[oy * ow + ox] = (ia << 24) | (Math.Min(ir, ia) << 16) | (Math.Min(ig, ia) << 8) | Math.Min(ib, ia);
                }
            }
            dirtyX0 = ox0; dirtyX1 = ox1; dirtyY0 = oy0; dirtyY1 = oy1;
            double tb = Now(); TShade += tb - ts;
            if (FrameReady != null) FrameReady();   // cheems composites its part now, from this exact frame
            double tp = Now(); TBuddy += tp - tb;
            Push();
            TPush += Now() - tp;
        }

        static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }
        static float Smooth(float a, float b, float v) { float t = Math.Max(0, Math.Min(1, (v - a) / (b - a))); return t * t * (3 - 2 * t); }

        // redraw now with the current Hole (used when the cheems starts/stops being dragged)
        public void RePush() { if (!IsDisposed) Push(); }

        void Push()
        {
            int bx0 = dirtyX0, bx1 = dirtyX1, by0 = dirtyY0, by1 = dirtyY1;
            if (bx1 <= bx0 || by1 <= by0) { bx0 = 0; by0 = 0; bx1 = 1; by1 = 1; }   // nothing to show: 1 transparent pixel
            int bw = bx1 - bx0, bh = by1 - by0;
            // copy just that box into the bitmap, leaving the cheems' rectangle blank (it draws that part itself)
            int hx0 = Hole.Left - left, hx1 = Hole.Right - left, hy0 = Hole.Top - top, hy1 = Hole.Bottom - top;
            if (zeros == null) zeros = new int[ow];
            for (int yy = by0; yy < by1; yy++)
            {
                IntPtr row = IntPtr.Add(bits, (yy * ow + bx0) * 4);
                if (yy >= hy0 && yy < hy1 && hx1 > bx0 && hx0 < bx1)
                {
                    int a0 = Math.Max(bx0, hx0), a1 = Math.Min(bx1, hx1);
                    if (a0 > bx0) Marshal.Copy(px, yy * ow + bx0, row, a0 - bx0);
                    Marshal.Copy(zeros, 0, IntPtr.Add(bits, (yy * ow + a0) * 4), a1 - a0);
                    if (bx1 > a1) Marshal.Copy(px, yy * ow + a1, IntPtr.Add(bits, (yy * ow + a1) * 4), bx1 - a1);
                }
                else Marshal.Copy(px, yy * ow + bx0, row, bw);
            }
            if (bw == 1 && bh == 1) Marshal.WriteInt32(bits, 0);
            Native.GdiFlush();
            IntPtr sdc = Native.GetDC(IntPtr.Zero);
            var dst = new Native.POINT { x = left + bx0, y = top + by0 }; var sz = new Native.SIZE { cx = bw, cy = bh };
            var s0 = new Native.POINT { x = bx0, y = by0 };
            var bf = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, sdc, ref dst, ref sz, memDC, ref s0, 0, ref bf, 2);
            Native.ReleaseDC(IntPtr.Zero, sdc);
        }
    }
}
