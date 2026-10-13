// DogeBuddy - standalone desktop doge whose eyes follow the cursor.
// Port of the "DogeEyes" Rainmeter skin by MintyDoge.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("DogeBuddy")]
[assembly: AssemblyProduct("DogeBuddy")]
[assembly: AssemblyVersion("1.7.0.0")]

namespace DogeBuddy
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "DogeBuddy_SingleInstance_7f3a", out created))
            {
                if (!created) return;
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new PetForm());
            }
        }
    }

    // ---------------------------------------------------------------- settings
    class Settings
    {
        public int Outfit = 0;          // 0 = none, 1..7
        public float Zoom = 1.0f;       // 1.0 = same size as the Rainmeter skin
        public int X = int.MinValue, Y = int.MinValue;
        public bool Locked = false;
        public bool HideFullscreen = true;
        public bool Tray = true;
        public int NapMinutes = 2;      // 0 = never

        static string AppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
        static string Dir { get { return Path.Combine(AppData, "DogeBuddy"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                // carry settings over from the old "DogePet" name
                string old = Path.Combine(Path.Combine(AppData, "DogePet"), "settings.ini");
                if (!File.Exists(FilePath) && File.Exists(old)) { Directory.CreateDirectory(Dir); File.Copy(old, FilePath); }
                if (!File.Exists(FilePath)) return s;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    int i = raw.IndexOf('=');
                    if (i < 0) continue;
                    string k = raw.Substring(0, i).Trim(), v = raw.Substring(i + 1).Trim();
                    var ci = CultureInfo.InvariantCulture;
                    switch (k)
                    {
                        case "Outfit": int.TryParse(v, out s.Outfit); break;
                        case "Zoom": float.TryParse(v, NumberStyles.Float, ci, out s.Zoom); break;
                        case "X": int.TryParse(v, out s.X); break;
                        case "Y": int.TryParse(v, out s.Y); break;
                        case "Locked": s.Locked = v == "1"; break;
                        case "HideFullscreen": s.HideFullscreen = v == "1"; break;
                        case "Tray": s.Tray = v == "1"; break;
                        case "NapMinutes": int.TryParse(v, out s.NapMinutes); break;
                    }
                }
            }
            catch { }
            if (s.Outfit < 0 || s.Outfit > 7) s.Outfit = 0;
            if (s.Zoom < 0.3f || s.Zoom > 1.0f) s.Zoom = 1.0f;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var ci = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine("Outfit=" + Outfit);
                sb.AppendLine("Zoom=" + Zoom.ToString(ci));
                sb.AppendLine("X=" + X);
                sb.AppendLine("Y=" + Y);
                sb.AppendLine("Locked=" + (Locked ? 1 : 0));
                sb.AppendLine("HideFullscreen=" + (HideFullscreen ? 1 : 0));
                sb.AppendLine("Tray=" + (Tray ? 1 : 0));
                sb.AppendLine("NapMinutes=" + NapMinutes);
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- layout (from DogeEye.ini, Scale = 2/3)
    static class Geo
    {
        public const float S = 2f / 3f;
        public static readonly RectangleF Body = new RectangleF(0, 0, 195 * S, 338 * S);
        public static readonly RectangleF Eyes = new RectangleF(40.2f * S, 85 * S - 1, 83 * S, 53 * S); // raised 1px so up/down look balanced
        public const float EyeMax = 5 * S;   // max pupil travel
        public const float EyeDiv = 12 * S;  // distance divisor near the eyes

        public static readonly string[] OutfitNames = {
            "No outfit", "Army Doge", "Business Doge", "Hoodie Doge", "Chef Doge",
            "Vault Dweller", "Astro Doge", "Tropical Doge" };

        // W, H (scaled), X, Y (unscaled px in the ini), PreserveAspectRatio
        static readonly float[,] Clothing = {
            {180,293,4,23,0}, {191,260,-1.5f,50,0}, {197,187,-3,93,0}, {196,330,-5,0,1},
            {185,320,0,43,1}, {190,310,0,11,1}, {185,320,0,15,1} };

        public static RectangleF OutfitRect(int n, Size img)
        {
            int i = n - 1;
            float w = Clothing[i, 0] * S, h = Clothing[i, 1] * S, x = Clothing[i, 2], y = Clothing[i, 3];
            if (Clothing[i, 4] == 0) return new RectangleF(x, y, w, h);
            float r = Math.Min(w / img.Width, h / img.Height);
            float nw = img.Width * r, nh = img.Height * r;
            return new RectangleF(x + (w - nw) / 2, y + (h - nh) / 2, nw, nh);
        }

        // body rect as drawn (PreserveAspectRatio=1)
        public static RectangleF BodyRect(Size img)
        {
            float r = Math.Min(Body.Width / img.Width, Body.Height / img.Height);
            float nw = img.Width * r, nh = img.Height * r;
            return new RectangleF((Body.Width - nw) / 2, (Body.Height - nh) / 2, nw, nh);
        }
    }

    // ---------------------------------------------------------------- the pet window
    class PetForm : Form
    {
        Settings cfg;
        Bitmap srcBody, srcHover, srcEyes, srcHalf, srcClosed, srcMiffed;
        Bitmap[] srcOutfits = new Bitmap[OutfitCount + 1];
        const int OutfitCount = 7;

        // scaled assets for current zoom
        Bitmap body, hover, eyes, outfit, half, closed, miffed;
        PointF bodyPos, eyesPos, outfitPos;
        PointF eyeCenter;
        float eyeMax, eyeDiv;
        RectangleF bounds; // union of all layers in layout units (unzoomed)

        // layered-window back buffer
        IntPtr memDC, dib, oldObj, bits;
        Bitmap canvas;
        int cw, ch;

        int winX, winY;
        float lastDx = float.NaN, lastDy = float.NaN;
        bool lastHover, hidden, dragging, leftDown, menuOpen;
        Point dragStart, winStart;
        IntPtr lastFg = IntPtr.Zero;
        int tickCount;

        // blinking / napping
        static readonly Random rng = new Random();
        Point lastCursor;
        int lastMoveTick, nextBlinkTick, blinkStart = -1, sleepStart = -1;
        float lastLid = -1; int lastZ = -1;
        const int BlinkMs = 190; // half 0-50, closed 50-140, half 140-190

        // moods
        int miffedUntil, groggyUntil;            // tick deadlines (0 = off)
        List<int> pokes = new List<int>();       // recent click times
        int shakeTurns, shakeDir, shakeLastX;    // direction changes while dragging
        bool clickWoke;                          // this click woke it up; don't count it as a poke
        bool lastMiffed;

        // smoothed eyes
        float eyeX, eyeY; int lastEyeTick;
        const float EyeLagMs = 90f;              // higher = lazier eyes

        static bool Before(int now, int deadline) { return deadline != 0 && deadline - now > 0; }
        bool Miffed(int now) { return Before(now, miffedUntil); }
        bool Groggy(int now) { return Before(now, groggyUntil); }
        void GetMiffed(int ms) { miffedUntil = Environment.TickCount + ms; if (miffedUntil == 0) miffedUntil = 1; blinkStart = -1; }

        System.Windows.Forms.Timer timer;
        ContextMenuStrip menu;
        NotifyIcon tray;
        Icon appIcon;

        public PetForm()
        {
            cfg = Settings.Load();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "DogeBuddy";

            appIcon = new Icon(Res("doge.ico"));
            Icon = appIcon;
            srcBody = LoadPng("DogImage.png");
            srcHover = LoadPng("DogImageHovered.png");
            srcEyes = LoadPng("eyes.png");
            srcHalf = LoadPng("DogBlinkHalf.png");
            srcClosed = LoadPng("DogBlinkClosed.png");
            srcMiffed = LoadPng("DogMiffed.png");
            for (int i = 1; i <= OutfitCount; i++) srcOutfits[i] = LoadPng("Clothing" + i + ".png");

            // union of every layer so the doge's feet stay put whatever it wears
            bounds = Geo.BodyRect(srcBody.Size);
            bounds = RectangleF.Union(bounds, Geo.Eyes);
            for (int i = 1; i <= OutfitCount; i++) bounds = RectangleF.Union(bounds, Geo.OutfitRect(i, srcOutfits[i].Size));
            lastMoveTick = Environment.TickCount;
            nextBlinkTick = lastMoveTick + 3000;

            BuildMenu();
            tray = new NotifyIcon { Icon = appIcon, Text = "DogeBuddy", ContextMenuStrip = menu, Visible = cfg.Tray };

            ApplyZoom();
            PlaceInitial();

            timer = new System.Windows.Forms.Timer { Interval = 33 };
            timer.Tick += OnTick;
            SystemEvents.DisplaySettingsChanged += (s, e) => BeginInvoke((Action)ClampToScreen);
            RefreshStartupPath();
        }

        static Stream Res(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        }

        static Bitmap LoadPng(string name)
        {
            using (var s = Res(name))
            using (var b = new Bitmap(s))
            {
                var c = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(c)) g.DrawImage(b, 0, 0, b.Width, b.Height);
                return c;
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Render(true);
            timer.Start();
        }

        // ------------------------------------------------------------ scaling
        static Bitmap Scale(Bitmap src, RectangleF r, float z, out PointF pos, RectangleF origin)
        {
            float x = (r.X - origin.X) * z, y = (r.Y - origin.Y) * z;
            float w = r.Width * z, h = r.Height * z;
            int iw = Math.Max(1, (int)Math.Round(w)), ih = Math.Max(1, (int)Math.Round(h));
            var b = new Bitmap(iw, ih, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY); // no edge bleed
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, iw, ih), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            pos = new PointF((float)Math.Round(x), (float)Math.Round(y));
            return b;
        }

        void ApplyZoom()
        {
            float z = cfg.Zoom;
            DisposeIf(ref body); DisposeIf(ref hover); DisposeIf(ref eyes); DisposeIf(ref half); DisposeIf(ref closed); DisposeIf(ref miffed);
            body = Scale(srcBody, Geo.BodyRect(srcBody.Size), z, out bodyPos, bounds);
            PointF dummy;
            hover = Scale(srcHover, Geo.BodyRect(srcHover.Size), z, out dummy, bounds);
            half = Scale(srcHalf, Geo.BodyRect(srcHalf.Size), z, out dummy, bounds);
            closed = Scale(srcClosed, Geo.BodyRect(srcClosed.Size), z, out dummy, bounds);
            miffed = Scale(srcMiffed, Geo.BodyRect(srcMiffed.Size), z, out dummy, bounds);
            eyes = Scale(srcEyes, Geo.Eyes, z, out eyesPos, bounds);
            eyeCenter = new PointF(eyesPos.X + eyes.Width / 2f, eyesPos.Y + eyes.Height / 2f);
            eyeMax = Geo.EyeMax * z;
            eyeDiv = Geo.EyeDiv * z;
            ApplyOutfit();

            int nw = (int)Math.Ceiling(bounds.Width * z) + 2, nh = (int)Math.Ceiling(bounds.Height * z) + 2;
            if (nw != cw || nh != ch) CreateBuffer(nw, nh);
            lastDx = float.NaN;
        }

        void ApplyOutfit()
        {
            DisposeIf(ref outfit);
            if (cfg.Outfit > 0)
            {
                var src = srcOutfits[cfg.Outfit];
                outfit = Scale(src, Geo.OutfitRect(cfg.Outfit, src.Size), cfg.Zoom, out outfitPos, bounds);
            }
            lastDx = float.NaN;
        }

        static void DisposeIf(ref Bitmap b) { if (b != null) { b.Dispose(); b = null; } }

        void CreateBuffer(int w, int h)
        {
            FreeBuffer();
            cw = w; ch = h;
            var bmi = new Native.BITMAPINFO();
            bmi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFO));
            bmi.biWidth = w; bmi.biHeight = -h; bmi.biPlanes = 1; bmi.biBitCount = 32;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            dib = Native.CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, screen);
            oldObj = Native.SelectObject(memDC, dib);
            canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        }

        void FreeBuffer()
        {
            if (canvas != null) { canvas.Dispose(); canvas = null; }
            if (memDC != IntPtr.Zero)
            {
                Native.SelectObject(memDC, oldObj);
                Native.DeleteObject(dib);
                Native.DeleteDC(memDC);
                memDC = IntPtr.Zero; dib = IntPtr.Zero; bits = IntPtr.Zero;
            }
        }

        // ------------------------------------------------------------ positioning
        void PlaceInitial()
        {
            if (cfg.X == int.MinValue || !OnAnyScreen(new Rectangle(cfg.X, cfg.Y, cw, ch)))
                DefaultPosition();
            else { winX = cfg.X; winY = cfg.Y; }
            Location = new Point(winX, winY);
            Size = new Size(cw, ch);
        }

        void DefaultPosition()
        {
            // feet resting on top of the taskbar, near the right-hand side
            var wa = Screen.PrimaryScreen.WorkingArea;
            winX = wa.Right - cw - 260;
            winY = wa.Bottom - ch;
            cfg.X = winX; cfg.Y = winY;
        }

        static bool OnAnyScreen(Rectangle r)
        {
            foreach (var s in Screen.AllScreens)
            {
                var i = Rectangle.Intersect(s.Bounds, r);
                if (i.Width >= 30 && i.Height >= 30) return true;
            }
            return false;
        }

        void ClampToScreen()
        {
            if (!OnAnyScreen(new Rectangle(winX, winY, cw, ch))) { DefaultPosition(); cfg.Save(); }
            MoveTo(winX, winY);
        }

        void MoveTo(int x, int y)
        {
            winX = x; winY = y;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, cw, ch, Native.SWP_NOACTIVATE);
            Render(true);
        }

        // ------------------------------------------------------------ drawing
        void Render(bool force)
        {
            if (canvas == null || !IsHandleCreated) return;
            Point c = Cursor.Position;
            float ex = winX + eyeCenter.X, ey = winY + eyeCenter.Y;
            float dx = c.X - ex, dy = c.Y - ey;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            float ox = 0, oy = 0;
            if (dist > 0.01f)
            {
                float m = Math.Min(eyeMax, dist / eyeDiv);
                ox = dx / dist * m; oy = dy / dist * m;
            }
            int now = Environment.TickCount;
            // ease the pupils towards the cursor instead of snapping
            int dt = lastEyeTick == 0 ? 1000 : Math.Max(0, Math.Min(200, now - lastEyeTick));
            lastEyeTick = now;
            float k = 1f - (float)Math.Exp(-dt / EyeLagMs);
            eyeX += (ox - eyeX) * k; eyeY += (oy - eyeY) * k;
            ox = (float)Math.Round(eyeX * 4) / 4f; oy = (float)Math.Round(eyeY * 4) / 4f;

            bool hov = dragging; // mouth open only while being carried around
            bool mf = !dragging && Miffed(now);
            float lid = LidAmount(now);
            int zf = sleepStart >= 0 && now - sleepStart > 2500 ? ((now - sleepStart) / 100) : -1;
            if (!force && ox == lastDx && oy == lastDy && hov == lastHover && lid == lastLid && zf == lastZ && mf == lastMiffed) return;
            lastDx = ox; lastDy = oy; lastHover = hov; lastLid = lid; lastZ = zf; lastMiffed = mf;

            using (var g = Graphics.FromImage(canvas))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(eyes, eyesPos.X + ox, eyesPos.Y + oy, eyes.Width, eyes.Height);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                Bitmap face = hov ? hover : mf ? miffed : lid >= 0.75f ? closed : lid > 0.25f ? half : body;
                g.DrawImageUnscaled(face, (int)bodyPos.X, (int)bodyPos.Y);
                if (outfit != null) g.DrawImageUnscaled(outfit, (int)outfitPos.X, (int)outfitPos.Y);
                if (zf >= 0) DrawZzz(g, now - sleepStart - 2500);
            }
            Native.GdiFlush();

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            var dst = new Native.POINT { x = winX, y = winY };
            var sz = new Native.SIZE { cx = cw, cy = ch };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref sz, memDC, ref src, 0, ref blend, 2);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }

        // ------------------------------------------------------------ blinking & napping
        void UpdateBlinkAndSleep()
        {
            int now = Environment.TickCount;
            Point c = Cursor.Position;
            if (c != lastCursor || dragging || menuOpen)
            {
                lastCursor = c;
                lastMoveTick = now;
                if (sleepStart >= 0)
                {
                    // stirred awake: groggy for a moment (click it now and it gets miffed)
                    sleepStart = -1;
                    groggyUntil = now + 1500;
                }
            }
            if (groggyUntil != 0 && !Groggy(now))
            {
                groggyUntil = 0;
                blinkStart = now - 50;               // eyes pop open
                nextBlinkTick = now + 2500 + rng.Next(4000);
            }
            if (miffedUntil != 0 && !Miffed(now))
            {
                miffedUntil = 0;
                nextBlinkTick = now + 400;           // a blink to reset the face
            }
            if (sleepStart < 0 && !Groggy(now) && !Miffed(now) && cfg.NapMinutes > 0 && now - lastMoveTick > cfg.NapMinutes * 60000)
            {
                sleepStart = now;
                blinkStart = -1;
            }
            if (sleepStart < 0)
            {
                if (blinkStart >= 0 && now - blinkStart >= BlinkMs) blinkStart = -1;
                if (blinkStart < 0 && now - nextBlinkTick >= 0 && !lastHover && !Miffed(now) && !Groggy(now))
                {
                    blinkStart = now;
                    // sometimes a quick double blink
                    nextBlinkTick = rng.Next(6) == 0 ? now + BlinkMs + 120 : now + 3000 + rng.Next(4500);
                }
            }
        }

        // 0 = open, 1 = shut
        float LidAmount(int now)
        {
            float l = 0;
            if (sleepStart >= 0)
            {
                float t = (now - sleepStart) / 1000f;
                l = t < 2.0f ? 0.5f : 1f;                            // drowsy, then asleep
            }
            else if (Groggy(now)) l = 0.5f;
            else if (blinkStart >= 0)
            {
                int t = now - blinkStart;
                l = t < 50 ? 0.5f : t < 140 ? 1f : t < BlinkMs ? 0.5f : 0f;
            }
            return (float)Math.Round(l * 12) / 12f;
        }

        static float Ease(float t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        void DrawZzz(Graphics g, int ms)
        {
            var oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float z = cfg.Zoom;
            float headX = bodyPos.X + body.Width * 0.72f, headY = bodyPos.Y + body.Height * 0.16f;
            // three z's rising and fading in a staggered 3 s loop
            for (int i = 0; i < 3; i++)
            {
                float t = ((ms + i * 1000) % 3000) / 3000f;
                float alpha = t < 0.15f ? t / 0.15f : (t > 0.7f ? (1 - t) / 0.3f : 1f);
                float size = (8 + 6 * t) * z;
                float x = headX + (6 + 14 * t + (float)Math.Sin(t * 6.28f) * 3) * z;
                float y = headY - (34 * t) * z;
                using (var path = new GraphicsPath())
                {
                    path.AddString(t > 0.5f ? "Z" : "z", FontFamily.GenericSansSerif, (int)FontStyle.Bold, size,
                                   new PointF(x, y), StringFormat.GenericDefault);
                    int a = (int)(255 * Math.Max(0, Math.Min(1, alpha)));
                    using (var outline = new Pen(Color.FromArgb(a * 3 / 4, 40, 50, 70), 2.2f * z) { LineJoin = LineJoin.Round })
                    using (var fill = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                    {
                        g.DrawPath(outline, path);
                        g.FillPath(fill, path);
                    }
                }
            }
            g.SmoothingMode = oldSmooth;
        }

        bool IsOverPet(Point c)
        {
            int x = c.X - winX, y = c.Y - winY;
            if (x < 0 || y < 0 || x >= cw || y >= ch || bits == IntPtr.Zero) return false;
            int px = Marshal.ReadInt32(bits, (y * cw + x) * 4);
            return ((px >> 24) & 0xFF) > 0;
        }

        // ------------------------------------------------------------ main loop
        void OnTick(object sender, EventArgs e)
        {
            if (++tickCount % 8 == 0) CheckZOrderAndFullscreen();   // ~4x per second
            if (hidden) return;
            UpdateBlinkAndSleep();
            if (sleepStart >= 0 && blinkStart < 0 && Environment.TickCount - sleepStart > 3000 && tickCount % 3 != 0) return; // ~10 fps while asleep
            Render(false);
        }

        static readonly HashSet<string> ShellClasses = new HashSet<string> {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
        static readonly HashSet<string> FlyoutClasses = new HashSet<string> {
            "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "#32768", "ForegroundStaging", "MultitaskingViewFrame",
            "TaskListThumbnailWnd", "Shell_InputSwitchTopLevelWindow" };

        void CheckZOrderAndFullscreen()
        {
            if (menuOpen) return;
            IntPtr fg = Native.GetForegroundWindow();
            string cls = ClassOf(fg);

            bool full = false;
            if (cfg.HideFullscreen && fg != IntPtr.Zero && fg != Handle && !ShellClasses.Contains(cls))
            {
                Native.RECT r;
                if (Native.GetWindowRect(fg, out r))
                {
                    var scr = Screen.FromHandle(fg).Bounds;
                    var mine = Screen.FromRectangle(new Rectangle(winX, winY, cw, ch)).Bounds;
                    full = r.left <= scr.Left && r.top <= scr.Top && r.right >= scr.Right && r.bottom >= scr.Bottom
                           && scr == mine;
                }
            }

            if (full && !hidden)
            {
                hidden = true;
                Native.ShowWindow(Handle, Native.SW_HIDE);
            }
            else if (!full && hidden)
            {
                hidden = false;
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                Render(true);
            }

            // the taskbar jumps above other topmost windows when clicked; hop back on top
            if (!hidden && (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || (fg != lastFg && !FlyoutClasses.Contains(cls))))
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            lastFg = fg;
        }

        static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            Native.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        // ------------------------------------------------------------ mouse
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                leftDown = true; dragging = false;
                dragStart = Cursor.Position; winStart = new Point(winX, winY);
                shakeTurns = 0; shakeDir = 0; shakeLastX = dragStart.X;
                Capture = true;
                int now = Environment.TickCount;
                clickWoke = sleepStart >= 0 || Groggy(now);
                if (clickWoke)
                {
                    // rude awakening
                    sleepStart = -1; groggyUntil = 0;
                    lastMoveTick = now;
                    GetMiffed(3000);
                    Render(true);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!leftDown || cfg.Locked) return;
            Point c = Cursor.Position;
            if (!dragging && (Math.Abs(c.X - dragStart.X) > 3 || Math.Abs(c.Y - dragStart.Y) > 3)) dragging = true;
            if (dragging)
            {
                // count sharp left/right direction changes = being shaken
                // shakeLastX tracks the furthest point in the current direction
                int d = c.X - shakeLastX;
                if (shakeDir == 0) { if (Math.Abs(d) >= 25) { shakeDir = Math.Sign(d); shakeLastX = c.X; } }
                else if (d * shakeDir > 0) shakeLastX = c.X;
                else if (Math.Abs(d) >= 25) { shakeTurns++; shakeDir = -shakeDir; shakeLastX = c.X; }
                MoveTo(winStart.X + c.X - dragStart.X, winStart.Y + c.Y - dragStart.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                Capture = false;
                leftDown = false;
                int now = Environment.TickCount;
                if (dragging)
                {
                    dragging = false;
                    cfg.X = winX; cfg.Y = winY; cfg.Save();
                    if (shakeTurns >= 4) GetMiffed(3000);   // you shook it
                    Render(true);
                }
                else if (!clickWoke)
                {
                    // poke poke poke
                    pokes.Add(now);
                    pokes.RemoveAll(t => now - t > 1500);
                    if (pokes.Count >= 3) { pokes.Clear(); GetMiffed(2500); Render(true); }
                }
                clickWoke = false;
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowMenu();
            }
        }

        void ShowMenu()
        {
            UpdateMenuChecks();
            Native.SetForegroundWindow(Handle); // lets the menu close when clicking elsewhere
            // open the menu towards the inside of the doge's own monitor
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

        // keep a submenu on the same monitor as its parent menu
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

        // ------------------------------------------------------------ menu
        ToolStripMenuItem[] outfitItems = new ToolStripMenuItem[OutfitCount + 1];
        List<KeyValuePair<float, ToolStripMenuItem>> sizeItems = new List<KeyValuePair<float, ToolStripMenuItem>>();
        ToolStripMenuItem miLock, miFull, miStartup, miTray, napMenu;

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            menu.Opening += (s, e) => { menuOpen = true; UpdateMenuChecks(); };
            menu.Closed += (s, e) => { menuOpen = false; };

            var outfits = new ToolStripMenuItem("Outfit");
            for (int i = 0; i <= OutfitCount; i++)
            {
                int n = i;
                outfitItems[i] = new ToolStripMenuItem(Geo.OutfitNames[i], null, (s, e) => SetOutfit(n));
                outfits.DropDownItems.Add(outfitItems[i]);
                if (i == 0) outfits.DropDownItems.Add(new ToolStripSeparator());
            }
            KeepOnScreen(outfits);
            menu.Items.Add(outfits);
            menu.Items.Add(new ToolStripMenuItem("Next outfit", null, (s, e) => SetOutfit((cfg.Outfit + 1) % (OutfitCount + 1))));

            var size = new ToolStripMenuItem("Size");
            foreach (var p in new[] { new KeyValuePair<string, float>("Small", 0.6f),
                                      new KeyValuePair<string, float>("Medium", 0.8f),
                                      new KeyValuePair<string, float>("Normal", 1.0f) })
            {
                float z = p.Value;
                var it = new ToolStripMenuItem(p.Key, null, (s, e) => SetZoom(z));
                size.DropDownItems.Add(it);
                sizeItems.Add(new KeyValuePair<float, ToolStripMenuItem>(z, it));
            }
            KeepOnScreen(size);
            menu.Items.Add(size);
            menu.Items.Add(new ToolStripSeparator());

            miLock = new ToolStripMenuItem("Lock position", null, (s, e) => { cfg.Locked = !cfg.Locked; cfg.Save(); });
            miFull = new ToolStripMenuItem("Hide when a fullscreen app is open", null, (s, e) => { cfg.HideFullscreen = !cfg.HideFullscreen; cfg.Save(); });
            miStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) => SetStartup(!GetStartup()));
            miTray = new ToolStripMenuItem("Show tray icon", null, (s, e) => { cfg.Tray = !cfg.Tray; tray.Visible = cfg.Tray; cfg.Save(); });
            var nap = new ToolStripMenuItem("Nap when idle");
            foreach (int m in new[] { 0, 1, 2, 5, 10 })
            {
                int mins = m;
                var it = new ToolStripMenuItem(m == 0 ? "Never" : m + (m == 1 ? " minute" : " minutes"), null,
                    (s, e) => { cfg.NapMinutes = mins; cfg.Save(); }) { Tag = m };
                nap.DropDownItems.Add(it);
            }
            napMenu = nap;
            KeepOnScreen(nap);
            menu.Items.Add(nap);
            menu.Items.Add(miLock);
            menu.Items.Add(new ToolStripMenuItem("Reset position", null, (s, e) => { DefaultPosition(); cfg.Save(); MoveTo(winX, winY); }));
            menu.Items.Add(miFull);
            menu.Items.Add(miStartup);
            menu.Items.Add(miTray);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Close()));
        }

        void UpdateMenuChecks()
        {
            for (int i = 0; i <= OutfitCount; i++) outfitItems[i].Checked = cfg.Outfit == i;
            foreach (var p in sizeItems) p.Value.Checked = Math.Abs(p.Key - cfg.Zoom) < 0.01f;
            miLock.Checked = cfg.Locked;
            miFull.Checked = cfg.HideFullscreen;
            miStartup.Checked = GetStartup();
            miTray.Checked = cfg.Tray;
            foreach (ToolStripMenuItem it in napMenu.DropDownItems) it.Checked = (int)it.Tag == cfg.NapMinutes;
        }

        void SetOutfit(int n)
        {
            cfg.Outfit = n; cfg.Save();
            ApplyOutfit();
            Render(true);
        }

        void SetZoom(float z)
        {
            // keep the feet anchored at the same spot
            int oldW = cw, oldH = ch;
            cfg.Zoom = z;
            ApplyZoom();
            int nx = winX + (oldW - cw) / 2, ny = winY + (oldH - ch);
            cfg.X = nx; cfg.Y = ny; cfg.Save();
            MoveTo(nx, ny);
        }

        // ------------------------------------------------------------ startup
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static string ExeCmd { get { return "\"" + Application.ExecutablePath + "\""; } }

        static bool GetStartup()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("DogeBuddy") != null; }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("DogeBuddy", ExeCmd);
                    else k.DeleteValue("DogeBuddy", false);
                }
            }
            catch { }
        }

        static void RefreshStartupPath()
        {
            try
            {
                // migrate the old "DogePet" startup entry
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null && k.GetValue("DogePet") != null) { k.DeleteValue("DogePet", false); SetStartup(true); }
            }
            catch { }
            if (GetStartup()) SetStartup(true); // follow the exe if it was moved
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            FreeBuffer();
            base.OnFormClosed(e);
        }
    }

    // ---------------------------------------------------------------- Win32
    static class Native
    {
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x8000000;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            public int bmiColors;
        }

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT ptDst, ref SIZE sz, IntPtr src, ref POINT ptSrc, int key, ref BLENDFUNCTION bf, int flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }
}
