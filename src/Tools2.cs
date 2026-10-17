// More hitting implements. Rigid ones reuse RigidTool (held by the handle, trail the swing);
// floppy ones (the wet fish) are ropes with a body drawn along them.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BigBootyCheems
{
    // ---------------------------------------------------------- chancla: a flip-flop, held by the toe end
    class Chancla : RigidTool
    {
        const float L = 76, W = 28;
        public Chancla(Buddy o) : base(o, 220) { }
        protected override float HandleLen { get { return 18; } }
        protected override float HeadLen { get { return L - 18; } }
        protected override float HeadHalfWidth { get { return 15; } }
        protected override float GripAt { get { return 8; } }
        protected override string SpriteName { get { return "chancla"; } }
        protected override float Stiffness { get { return 150; } }
        protected override float PowerMin { get { return 0.8f; } }
        protected override float PowerMax { get { return 1.5f; } }
        protected override Sfx Sound { get { return null; } }

        protected override void DrawShape(Graphics g)
        {
            // footprint-shaped sole: narrower at the heel (far end), wide at the toe (handle end)
            using (var sole = new GraphicsPath())
            {
                sole.AddBezier(0, 0, 0, -W * 0.55f, L * 0.35f, -W * 0.55f, L * 0.55f, -W * 0.38f);
                sole.AddBezier(L * 0.55f, -W * 0.38f, L * 0.8f, -W * 0.3f, L, -W * 0.42f, L, 0);
                sole.AddBezier(L, 0, L, W * 0.42f, L * 0.8f, W * 0.3f, L * 0.55f, W * 0.38f);
                sole.AddBezier(L * 0.55f, W * 0.38f, L * 0.35f, W * 0.55f, 0, W * 0.55f, 0, 0);
                using (var b = new LinearGradientBrush(new PointF(0, -W / 2), new PointF(0, W / 2), Color.FromArgb(255, 70, 140, 220), Color.FromArgb(255, 30, 90, 170)))
                    g.FillPath(b, sole);
                using (var edge = new Pen(Color.FromArgb(255, 20, 60, 120), 1.6f)) g.DrawPath(edge, sole);
            }
            // the thong strap: a Y from the toe post back to both sides
            using (var strap = new Pen(Color.FromArgb(255, 245, 245, 245), 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(strap, 14, 0, 36, -W * 0.42f);
                g.DrawLine(strap, 14, 0, 36, W * 0.42f);
            }
            using (var post = new SolidBrush(Color.FromArgb(255, 230, 230, 230))) g.FillEllipse(post, 11, -3, 6, 6);
        }
    }

    // ---------------------------------------------------------- floppy things: a rope with a body drawn along it
    abstract class FloppyTool : Tool
    {
        protected readonly int N; protected readonly float Seg;
        protected readonly float[] px, py, qx, qy;

        protected FloppyTool(Buddy o, int n, float seg) : base(o, 2 * (int)(n * seg) + 80)
        {
            N = n; Seg = seg;
            px = new float[N]; py = new float[N]; qx = new float[N]; qy = new float[N];
        }

        protected abstract float Thick(int i);            // body thickness at node i
        protected virtual float Bend { get { return 0; } } // 0 = limp, up to ~0.5 = springy
        protected virtual float MaxBendDeg { get { return 180; } }   // most a joint may fold
        protected virtual float Drag { get { return 0.994f; } }
        protected virtual float MinSpeed { get { return 800; } }
        protected virtual float PowerMin { get { return 0.6f; } }
        protected virtual float PowerMax { get { return 1.3f; } }
        protected virtual void OnHit() { }
        protected virtual float FloorLift { get { return 2; } }   // how far the rope line sits above the floor when lying
        protected override float RestLift { get { return FloorLift + 2; } }

        protected override void PlaceAtRest()
        {
            for (int i = 0; i < N; i++) { px[i] = hx - i * Seg; py[i] = hy; qx[i] = px[i]; qy[i] = py[i]; }
        }

        protected override bool Simulate(float dt, int now)
        {
            float floorY = Floor.Bottom - FloorLift;
            const int Sub = 3;
            float h = dt / Sub, maxMove = 0;
            float p0x = px[0], p0y = py[0];
            for (int s = 0; s < Sub; s++)
            {
                float f = (s + 1f) / Sub;
                px[0] = p0x + (hx - p0x) * f; py[0] = p0y + (hy - p0y) * f;
                for (int i = 1; i < N; i++)
                {
                    float nx = px[i] + (px[i] - qx[i]) * Drag, ny = py[i] + (py[i] - qy[i]) * Drag + 2000 * h * h;
                    qx[i] = px[i]; qy[i] = py[i]; px[i] = nx; py[i] = ny;
                }
                for (int it = 0; it < 10; it++)
                {
                    for (int i = 0; i < N - 1; i++) Link(i, i + 1, Seg, 1f);
                    if (Bend > 0) for (int i = 0; i < N - 2; i++) Link(i, i + 2, Seg * 2, Bend);   // resists folding
                    for (int i = 1; i < N; i++)
                        if (py[i] > floorY) { py[i] = floorY; qx[i] = px[i] - (px[i] - qx[i]) * 0.6f; }
                }
                if (MaxBendDeg < 180) LimitBend();
                // anything in the far half landing on the butt fast enough
                if (held && now - lastHit > 320)
                    for (int i = N - 1; i >= N / 2; i--)
                    {
                        float vxi = (px[i] - qx[i]) / h, vyi = (py[i] - qy[i]) / h;
                        float sp = (float)Math.Sqrt(vxi * vxi + vyi * vyi);
                        if (sp < MinSpeed || !owner.ToolTouches(new PointF(px[i], py[i]))) continue;
                        float power = PowerMin + (PowerMax - PowerMin) * RigidTool.Clamp((sp - MinSpeed) / 2400f, 0, 1);
                        owner.ToolHit(new PointF(px[i], py[i]), Math.Sign(vxi), power, null);
                        OnHit();
                        lastHit = now;
                        break;
                    }
                for (int i = 1; i < N; i++) maxMove = Math.Max(maxMove, Math.Abs(px[i] - qx[i]) + Math.Abs(py[i] - qy[i]));
            }
            return maxMove / h > 15;
        }

        // walk out from the held end: if a joint folds past the limit, swing the next node back into the cone
        void LimitBend()
        {
            double lim = MaxBendDeg * Math.PI / 180;
            for (int i = 1; i < N - 1; i++)
            {
                double a0 = Math.Atan2(py[i] - py[i - 1], px[i] - px[i - 1]);
                double a1 = Math.Atan2(py[i + 1] - py[i], px[i + 1] - px[i]);
                double d = a1 - a0;
                while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
                if (Math.Abs(d) <= lim) continue;
                // turn the rest of the body (and where it was last step, so its motion turns too) about this joint
                double rot = Math.Sign(d) * lim - d;
                float c = (float)Math.Cos(rot), sn = (float)Math.Sin(rot), cx = px[i], cy = py[i];
                for (int j = i + 1; j < N; j++)
                {
                    float x = px[j] - cx, y = py[j] - cy; px[j] = cx + x * c - y * sn; py[j] = cy + x * sn + y * c;
                    x = qx[j] - cx; y = qy[j] - cy; qx[j] = cx + x * c - y * sn; qy[j] = cy + x * sn + y * c;
                }
            }
        }

        void Link(int a, int b, float rest, float k)
        {
            float dx = px[b] - px[a], dy = py[b] - py[a];
            float len = (float)Math.Sqrt(dx * dx + dy * dy) + 1e-4f;
            float diff = (len - rest) / len * k;
            if (a == 0) { px[b] -= dx * diff; py[b] -= dy * diff; }
            else { px[a] += dx * diff * 0.5f; py[a] += dy * diff * 0.5f; px[b] -= dx * diff * 0.5f; py[b] -= dy * diff * 0.5f; }
        }

        // a smooth body outline following the rope, with per-node thickness
        protected GraphicsPath BodyPath(int ox, int oy, int from, int to)
        {
            var left = new List<PointF>(); var right = new List<PointF>();
            for (int i = from; i <= to; i++)
            {
                int a = Math.Max(from, i - 1), b = Math.Min(to, i + 1);
                float dx = px[b] - px[a], dy = py[b] - py[a], len = (float)Math.Sqrt(dx * dx + dy * dy) + 1e-4f;
                float nx = -dy / len, ny = dx / len, w = Thick(i) / 2;
                left.Add(new PointF(px[i] - ox + nx * w, py[i] - oy + ny * w));
                right.Insert(0, new PointF(px[i] - ox - nx * w, py[i] - oy - ny * w));
            }
            var path = new GraphicsPath();
            path.AddCurve(left.ToArray(), 0.4f);
            path.AddCurve(right.ToArray(), 0.4f);
            path.CloseFigure();
            return path;
        }

        // direction of the rope at node i (unit vector, pointing away from the held end)
        protected PointF Dir(int i)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(N - 1, i + 1);
            float dx = px[b] - px[a], dy = py[b] - py[a], len = (float)Math.Sqrt(dx * dx + dy * dy) + 1e-4f;
            return new PointF(dx / len, dy / len);
        }
    }

    // ---------------------------------------------------------- wet fish: your tuna art, held by the tail
    // Modelled as one pendulum hanging from your hand (smooth, no jitter), plus a body curve that lags
    // the swing so it flops. Drawn as thin slices along that curve, so turns stay smooth.
    class WetFish : Tool
    {
        // in the art (468x189, head on the left): where the tail stalk is gripped, and the body's centre line
        const float ArtGrip = 400, ArtMid = 96, ArtW = 468, ArtH = 189, BodyLen = 125;
        const int Slices = 36;
        static Bitmap sprite; static float scale;
        float ang = (float)Math.PI, angV, bend;          // body direction (tail -> head), its spin, and the flop curve
        float pvx, pvy; bool wasHeld;                    // hand velocity last step, for acceleration
        readonly PointF[] lastHits = new PointF[4]; bool haveLast;

        public WetFish(Buddy o) : base(o, 2 * (int)(BodyLen + 40) + 60)
        {
            if (sprite != null) return;
            scale = BodyLen / ArtGrip;
            using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Tool_fish.png"))
            using (var raw = new Bitmap(s))
            {
                int w = (int)Math.Ceiling(ArtW * scale), h = (int)Math.Ceiling(ArtH * scale);
                sprite = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(sprite))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TranslateTransform(w, 0); g.ScaleTransform(-1, 1);   // tail end at x = 0
                    g.DrawImage(raw, 0, 0, ArtW * scale, ArtH * scale);
                }
            }
        }

        protected override float RestLift { get { return (ArtH - ArtMid) * scale * 0.72f; } }
        protected override void PlaceAtRest() { ang = (float)Math.PI; angV = 0; bend = 0; haveLast = false; }

        // body point at distance s from the grip (negative = the tail fin behind the hand)
        PointF At(float s, out float dir)
        {
            // constant curvature: direction turns by 'bend' radians over the body length
            float k = bend / BodyLen;
            dir = ang + k * Math.Max(0, s);
            if (s <= 0) return new PointF(hx + (float)Math.Cos(ang) * s, hy + (float)Math.Sin(ang) * s);
            if (Math.Abs(k) < 1e-5f) return new PointF(hx + (float)Math.Cos(ang) * s, hy + (float)Math.Sin(ang) * s);
            // arc from the grip
            float x = ((float)Math.Sin(ang + k * s) - (float)Math.Sin(ang)) / k;
            float y = (-(float)Math.Cos(ang + k * s) + (float)Math.Cos(ang)) / k;
            return new PointF(hx + x, hy + y);
        }

        protected override bool Simulate(float dt, int now)
        {
            if (held && !wasHeld) { pvx = vx; pvy = vy; }   // no kick from the grab itself
            wasHeld = held;
            if (held)
            {
                // pendulum under gravity, with the hand's acceleration swinging it
                float ax = (vx - pvx) / dt, ay = (vy - pvy) / dt;
                ax = RigidTool.Clamp(ax, -60000, 60000); ay = RigidTool.Clamp(ay, -60000, 60000);
                float gx = -ax, gy = 2000 - ay;                       // effective gravity in the hand's frame
                float tx = -(float)Math.Sin(ang), ty = (float)Math.Cos(ang);
                float L = BodyLen * 0.55f;
                angV += ((gx * tx + gy * ty) / L - 4.5f * angV) * dt;
                angV = RigidTool.Clamp(angV, -28, 28);
                ang += angV * dt;
                // flop: the head lags behind the spin, eased so it never snaps
                float want = RigidTool.Clamp(-angV * 0.06f, -0.9f, 0.9f);
                bend += (want - bend) * Math.Min(1, dt * 14);
            }
            else
            {
                // dropped: settle flat on whichever side is nearer, body straightening out
                float a = RigidTool.Wrap(ang);
                float target = Math.Abs(a) < Math.PI / 2 ? 0 : (a > 0 ? (float)Math.PI : -(float)Math.PI);
                ang = a;
                angV += (120 * RigidTool.Wrap(target - ang) - 14 * angV) * dt;
                ang += angV * dt;
                bend += (0 - bend) * Math.Min(1, dt * 8);
            }
            pvx = vx; pvy = vy;

            // hits: a few points along the front half of the body
            float dummy;
            for (int k = 0; k < 4; k++)
            {
                var p = At(BodyLen * (0.45f + 0.18f * k), out dummy);
                if (held && haveLast && now - lastHit > 320)
                {
                    float hvx = (p.X - lastHits[k].X) / dt, hvy = (p.Y - lastHits[k].Y) / dt;
                    float sp = (float)Math.Sqrt(hvx * hvx + hvy * hvy);
                    if (sp > 750 && owner.ToolTouches(p))
                    {
                        float power = 0.6f + 0.7f * RigidTool.Clamp((sp - 750) / 2400f, 0, 1);
                        owner.ToolHit(p, Math.Sign(hvx), power, null);
                        lastHit = now;
                        angV *= -0.4f;                 // bounces off the cheek
                    }
                }
                lastHits[k] = p;
            }
            haveLast = true;
            return Math.Abs(angV) > 0.05f || Math.Abs(bend) > 0.01f;
        }

        protected override void DrawTool(Graphics g, int ox, int oy)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float tail = (ArtW - ArtGrip) * scale, h = sprite.Height, cy = ArtMid * scale;
            float total = tail + BodyLen, step = total / Slices;
            float d0, d1;
            var a = At(-tail, out d0);
            for (int k = 0; k < Slices; k++)
            {
                float s1 = -tail + (k + 1) * step;
                var b = At(s1, out d1);
                // normal from the slice's middle direction; a little overlap hides any seam
                float dm = (d0 + d1) / 2, nx = -(float)Math.Sin(dm), ny = (float)Math.Cos(dm);
                float ex = (float)Math.Cos(dm) * 0.8f, ey = (float)Math.Sin(dm) * 0.8f;
                var dest = new[]
                {
                    new PointF(a.X - nx * cy - ox, a.Y - ny * cy - oy),
                    new PointF(b.X + ex - nx * cy - ox, b.Y + ey - ny * cy - oy),
                    new PointF(a.X + nx * (h - cy) - ox, a.Y + ny * (h - cy) - oy),
                };
                float u0 = k * step, u1 = Math.Min(sprite.Width, (k + 1) * step + 0.8f);
                g.DrawImage(sprite, dest, new RectangleF(u0, 0, u1 - u0, h), GraphicsUnit.Pixel);
                a = b; d0 = d1;
            }
        }
    }
}
