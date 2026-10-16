// DogeBuddy - standalone desktop doge whose eyes follow the cursor.
// Port of the "DogeEyes" Rainmeter skin by MintyDoge.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("DogeBuddy")]
[assembly: AssemblyProduct("DogeBuddy")]
[assembly: AssemblyVersion("1.14.1.0")]

namespace DogeBuddy
{
    static class Program
    {
        public static bool JustUpdated;

        [STAThread]
        static void Main(string[] args)
        {
            JustUpdated = Array.IndexOf(args, "--updated") >= 0;
            using (var mutex = new Mutex(false, "DogeBuddy_SingleInstance_7f3a"))
            using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "DogeBuddy_Quit_7f3a"))
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
                        MessageBox.Show("An older DogeBuddy is still running and didn't close.\n\nRight-click the doge, choose Exit, then start this one again.",
                            "DogeBuddy", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    Application.SetCompatibleTextRenderingDefault(false);
                    var form = new PetForm();
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

    // ---------------------------------------------------------------- settings
    class Settings
    {
        public int Outfit = 0;          // 0 = none, 1..7
        public int Hat = 0;             // 0 = none, 1..N (worn on top of any outfit)
        public float Zoom = 1.0f;       // 1.0 = same size as the Rainmeter skin
        public int X = int.MinValue, Y = int.MinValue;
        public bool Locked = false;
        public bool HideFullscreen = true;
        public bool Tray = true;
        public int NapMinutes = 2;      // 0 = never
        public int LayoutVer = 1;       // 2 = side padding for tilting, 4 = current hat set

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
                        case "Hat": int.TryParse(v, out s.Hat); break;
                        case "Zoom": float.TryParse(v, NumberStyles.Float, ci, out s.Zoom); break;
                        case "X": int.TryParse(v, out s.X); break;
                        case "Y": int.TryParse(v, out s.Y); break;
                        case "Locked": s.Locked = v == "1"; break;
                        case "HideFullscreen": s.HideFullscreen = v == "1"; break;
                        case "Tray": s.Tray = v == "1"; break;
                        case "NapMinutes": int.TryParse(v, out s.NapMinutes); break;
                        case "LayoutVer": int.TryParse(v, out s.LayoutVer); break;
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
                sb.AppendLine("Hat=" + Hat);
                sb.AppendLine("Zoom=" + Zoom.ToString(ci));
                sb.AppendLine("X=" + X);
                sb.AppendLine("Y=" + Y);
                sb.AppendLine("Locked=" + (Locked ? 1 : 0));
                sb.AppendLine("HideFullscreen=" + (HideFullscreen ? 1 : 0));
                sb.AppendLine("Tray=" + (Tray ? 1 : 0));
                sb.AppendLine("NapMinutes=" + NapMinutes);
                sb.AppendLine("LayoutVer=" + LayoutVer);
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
        public const float TiltPad = 28;     // spare room either side so the doge can lean

        // hats: worn on the head on top of any outfit. Positions are in DogImage.png pixels
        // (negative y = above the art). RSB/RSG are full-canvas overlays on DogImage.png.
        public static readonly string[] HatNames = {
            "No hat",
            "Blue Partyhat",
            "Green Partyhat" };
        public static readonly RectangleF[] HatRects = {
            RectangleF.Empty,
            new RectangleF(0, 0, 130, 225),
            new RectangleF(0, 0, 130, 225) };
        // area the v1.14 test hats covered; used once to move a v1.14 window back
        public static readonly RectangleF V14Hats = new RectangleF(-6, -12, 142, 175);
        public static int HatCount { get { return HatNames.Length - 1; } }
        public const float MaxTilt = 6;      // degrees

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
        Bitmap srcBody, srcHover, srcEyes, srcHalf, srcClosed, srcMiffed, srcHappy, srcHeld;
        Bitmap[] srcOutfits = new Bitmap[OutfitCount + 1];
        Bitmap[] srcHats;
        Bitmap hat; PointF hatPos;
        PointF oldOrigin, v14Origin;   // where the window's top-left used to sit before hats made it taller
        const int OutfitCount = 7;

        // scaled assets for current zoom
        Bitmap body, hover, eyes, outfit, half, closed, miffed, happy, held;
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
        int blinkPhase, phaseStart; bool phaseShown;   // 1 = half, 2 = shut, 3 = half; each step is drawn at least once
        float lastLid = -1; int lastZ = -1;
        const int BlinkMs = 190; // half 0-50, closed 50-140, half 140-190

        // moods
        int miffedUntil, groggyUntil;            // tick deadlines (0 = off)
        List<int> pokes = new List<int>();       // recent click times
        int shakeTurns, shakeDir, shakeLastX;    // direction changes while dragging
        List<int> shakeTimes = new List<int>();  // when each hard direction change happened
        bool shakeAngry;                         // shaken hard enough to get cross (beats the fun open mouth)
        const int ShakeSwing = 40, ShakeTurnsToAnger = 6, ShakeWindowMs = 1500;
        bool clickWoke;                          // this click woke it up; don't count it as a poke
        bool lastMiffed, lastHappy, lastWild, lastCross;

        // carried: mouth ajar; swung hard: mouth wide open ("weee")
        float dragVy; int lastDragY, wildUntil, weeAfterDrop;
        const int WeeHoldMs = 700, WeeAfterDropMs = 900;   // how long the "weee" face lingers
        const float WildSpeed = 1600f;   // px/s at Normal size

        // smoothed eyes
        float eyeX, eyeY; int lastEyeTick;
        const float EyeLagMs = 90f;              // higher = lazier eyes

        // now and then it stops tracking and gives you the classic doge look
        static readonly PointF ClassicLook = new PointF(1.5f, 1.25f); // pupil offset at Normal size
        int lookUntil, nextLookTick, sweepStart;
        float sweepFromX, sweepFromY;
        bool sweepBack, idleLook;
        const int IdleLookMs = 30000;          // after this long with no mouse/keyboard, hold the classic look
        const int SweepInMs = 900, SweepOutMs = 700;
        Point lookCursor;

        // petting
        int happyUntil, petTurns, petDir, petLastX, petFirstTick;
        bool Happy(int now) { return Before(now, happyUntil); }

        // hover on the nose too long and it glares at you
        int noseStart; bool noseGlare;
        const int NoseMs = 2000;
        static readonly RectangleF NoseArt = new RectangleF(25, 77, 25, 22); // in DogImage.png pixels

        // gentle sway now and again
        int swayStart, nextSwayTick;
        const float SwayDeg = 1.3f, SwayPeriod = 2.6f, SwaySecs = 5.2f;

        // tilt / wobble (degrees, spring physics)
        float angle, angVel, dragVx, lastAngle; int lastPhysTick, lastDragTick, lastDragX;
        float pivotX = float.NaN, pivotY, grabLever = -1f;  // swing point (canvas px) and which way it swings

        // watching you type
        int typingUntil, lastClickTick; uint lastInputTime; Point typingCursor;
        volatile bool caretValid, caretBusy; volatile int caretX, caretY; int lastCaretQuery;
        bool Typing(int now) { return Before(now, typingUntil); }

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
            srcHappy = LoadPng("DogHappy.png");
            srcHeld = LoadPng("DogOpenM.png");
            for (int i = 1; i <= OutfitCount; i++) srcOutfits[i] = LoadPng("Clothing" + i + ".png");
            srcHats = new Bitmap[Geo.HatCount + 1];
            srcHats[1] = LoadPng("RSB.png");
            srcHats[2] = LoadPng("RSG.png");
            if (cfg.Hat < 0 || cfg.Hat > Geo.HatCount) cfg.Hat = 0;

            // union of every layer so the doge's feet stay put whatever it wears
            bounds = Geo.BodyRect(srcBody.Size);
            bounds = RectangleF.Union(bounds, Geo.Eyes);
            for (int i = 1; i <= OutfitCount; i++) bounds = RectangleF.Union(bounds, Geo.OutfitRect(i, srcOutfits[i].Size));
            oldOrigin = new PointF(bounds.X - Geo.TiltPad, bounds.Y);
            var v14 = RectangleF.Union(bounds, Geo.V14Hats);
            v14Origin = new PointF(v14.X - Geo.TiltPad, v14.Y);
            for (int i = 1; i <= Geo.HatCount; i++) bounds = RectangleF.Union(bounds, Geo.HatRects[i]);
            bounds = new RectangleF(bounds.X - Geo.TiltPad, bounds.Y, bounds.Width + 2 * Geo.TiltPad, bounds.Height);
            lastMoveTick = Environment.TickCount;
            nextBlinkTick = lastMoveTick + 3000;
            nextSwayTick = lastMoveTick + 20000 + rng.Next(20000);
            nextLookTick = lastMoveTick + 15000 + rng.Next(15000);

            BuildMenu();
            tray = new NotifyIcon { Icon = appIcon, Text = "DogeBuddy", ContextMenuStrip = menu, Visible = cfg.Tray };

            ApplyZoom();
            PlaceInitial();

            timer = new System.Windows.Forms.Timer { Interval = 33 };
            timer.Tick += OnTick;
            SystemEvents.DisplaySettingsChanged += (s, e) => BeginInvoke((Action)ClampToScreen);
            RefreshStartupPath();

        }

        // ------------------------------------------------------------ updates
        bool updateBusy;

        void CheckForUpdates(bool manual)
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
                        if (manual) MessageBox.Show(this, "Couldn't reach GitHub to check for updates.\n\n" + err, "DogeBuddy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (rel.Version <= Updater.Current)
                    {
                        if (manual) MessageBox.Show(this, "You're on the latest version (v" + Updater.Short(Updater.Current) + ").", "DogeBuddy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    string notes = (rel.Notes ?? "").Trim();
                    if (notes.Length > 400) notes = notes.Substring(0, 400) + "...";
                    var ask = MessageBox.Show(this,
                        "DogeBuddy v" + Updater.Short(rel.Version) + " is out (you have v" + Updater.Short(Updater.Current) + ").\n\n" +
                        (notes.Length > 0 ? notes + "\n\n" : "") + "Update now? DogeBuddy will restart.",
                        "DogeBuddy update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
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
                        MessageBox.Show(this, "The update didn't install, so nothing was changed.\n\n" + err, "DogeBuddy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    try { Process.Start(Application.ExecutablePath, "--updated"); }
                    catch (Exception ex) { MessageBox.Show(this, "Updated, but couldn't restart: " + ex.Message, "DogeBuddy"); }
                    Close();
                }));
            });
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
            if (Program.JustUpdated && tray.Visible)
                tray.ShowBalloonTip(4000, "DogeBuddy", "Updated to v" + Updater.Short(Updater.Current), ToolTipIcon.Info);
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
            DisposeIf(ref body); DisposeIf(ref hover); DisposeIf(ref eyes); DisposeIf(ref half); DisposeIf(ref closed); DisposeIf(ref miffed); DisposeIf(ref happy); DisposeIf(ref held);
            body = Scale(srcBody, Geo.BodyRect(srcBody.Size), z, out bodyPos, bounds);
            PointF dummy;
            hover = Scale(srcHover, Geo.BodyRect(srcHover.Size), z, out dummy, bounds);
            half = Scale(srcHalf, Geo.BodyRect(srcHalf.Size), z, out dummy, bounds);
            closed = Scale(srcClosed, Geo.BodyRect(srcClosed.Size), z, out dummy, bounds);
            miffed = Scale(srcMiffed, Geo.BodyRect(srcMiffed.Size), z, out dummy, bounds);
            happy = Scale(srcHappy, Geo.BodyRect(srcHappy.Size), z, out dummy, bounds);
            held = Scale(srcHeld, Geo.BodyRect(srcHeld.Size), z, out dummy, bounds);
            eyes = Scale(srcEyes, Geo.Eyes, z, out eyesPos, bounds);
            eyeCenter = new PointF(eyesPos.X + eyes.Width / 2f, eyesPos.Y + eyes.Height / 2f);
            eyeMax = Geo.EyeMax * z;
            eyeDiv = Geo.EyeDiv * z;
            ApplyOutfit();
            ApplyHat();

            int nw = (int)Math.Ceiling(bounds.Width * z) + 2, nh = (int)Math.Ceiling(bounds.Height * z) + 2;
            if (nw != cw || nh != ch) CreateBuffer(nw, nh);
            lastDx = float.NaN;
        }

        void ApplyHat()
        {
            DisposeIf(ref hat);
            if (cfg.Hat > 0) hat = Scale(srcHats[cfg.Hat], Geo.HatRects[cfg.Hat], cfg.Zoom, out hatPos, bounds);
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
            if (cfg.LayoutVer < 2)
            {
                // the window got wider on both sides; keep the doge where it was
                if (cfg.X != int.MinValue) cfg.X -= (int)Math.Round(Geo.TiltPad * cfg.Zoom);
                cfg.LayoutVer = 2; cfg.Save();
            }
            if (cfg.LayoutVer < 3)
            {
                if (cfg.X != int.MinValue)
                {
                    cfg.X -= (int)Math.Round((oldOrigin.X - (bounds.X)) * cfg.Zoom);
                    cfg.Y -= (int)Math.Round((oldOrigin.Y - bounds.Y) * cfg.Zoom);
                }
                cfg.LayoutVer = 4; cfg.Save();
            }
            else if (cfg.LayoutVer == 3)
            {
                // v1.14 window was sized for the test hats; keep the doge where it was
                if (cfg.X != int.MinValue)
                {
                    cfg.X -= (int)Math.Round((v14Origin.X - bounds.X) * cfg.Zoom);
                    cfg.Y -= (int)Math.Round((v14Origin.Y - bounds.Y) * cfg.Zoom);
                }
                cfg.LayoutVer = 4; cfg.Save();
            }
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
            if (Typing(Environment.TickCount) && caretValid) c = new Point(caretX, caretY);
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
            if (noseGlare) { ox = ClassicLook.X * cfg.Zoom; oy = ClassicLook.Y * cfg.Zoom; }
            bool looking = Before(now, lookUntil);
            int dt = lastEyeTick == 0 ? 1000 : Math.Max(0, Math.Min(200, now - lastEyeTick));
            lastEyeTick = now;
            if (looking)
            {
                // slow, even sweep over to the classic look
                float e = Ease((now - sweepStart) / (float)SweepInMs);
                eyeX = sweepFromX + (ClassicLook.X * cfg.Zoom - sweepFromX) * e;
                eyeY = sweepFromY + (ClassicLook.Y * cfg.Zoom - sweepFromY) * e;
            }
            else if (sweepBack && now - sweepStart < SweepOutMs)
            {
                // and a slow sweep back to wherever the cursor is
                float e = Ease((now - sweepStart) / (float)SweepOutMs);
                eyeX = sweepFromX + (ox - sweepFromX) * e;
                eyeY = sweepFromY + (oy - sweepFromY) * e;
            }
            else
            {
                // normal tracking: ease towards the cursor instead of snapping
                sweepBack = false;
                float k = 1f - (float)Math.Exp(-dt / (noseGlare ? 220f : EyeLagMs));
                eyeX += (ox - eyeX) * k; eyeY += (oy - eyeY) * k;
            }
            ox = (float)Math.Round(eyeX * 8) / 8f; oy = (float)Math.Round(eyeY * 8) / 8f;

            bool wee = !dragging && Before(now, weeAfterDrop) && !Miffed(now);
            bool hov = dragging || wee;
            bool wild = (dragging && Before(now, wildUntil)) || wee;
            bool cross = dragging && shakeAngry;
            bool mf = !dragging && Miffed(now);
            bool hp = !dragging && !mf && Happy(now);
            float ang = (float)Math.Round(angle * 10) / 10f;
            float lid = LidAmount(now);
            int zf = sleepStart >= 0 && now - sleepStart > 2500 ? ((now - sleepStart) / 100) : -1;
            if (!force && ox == lastDx && oy == lastDy && hov == lastHover && wild == lastWild && cross == lastCross && lid == lastLid && zf == lastZ && mf == lastMiffed && hp == lastHappy && ang == lastAngle) { phaseShown = true; return; }
            lastDx = ox; lastDy = oy; lastHover = hov; lastWild = wild; lastCross = cross; lastLid = lid; lastZ = zf; lastMiffed = mf; lastHappy = hp; lastAngle = ang;

            using (var g = Graphics.FromImage(canvas))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                if (ang != 0)
                {
                    // lean around the feet
                    // swing around wherever it was grabbed (feet until the first grab)
                    float px = float.IsNaN(pivotX) ? bodyPos.X + body.Width / 2f : pivotX;
                    float py = float.IsNaN(pivotX) ? bodyPos.Y + body.Height : pivotY;
                    g.TranslateTransform(px, py); g.RotateTransform(ang); g.TranslateTransform(-px, -py);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                }
                g.DrawImage(eyes, eyesPos.X + ox, eyesPos.Y + oy, eyes.Width, eyes.Height);
                g.InterpolationMode = ang != 0 ? InterpolationMode.Bilinear : InterpolationMode.NearestNeighbor;
                Bitmap face = hov ? (cross ? miffed : wild ? hover : held) : mf ? miffed : hp ? happy : lid >= 0.75f ? closed : lid > 0.25f ? half : body;
                g.DrawImage(face, (int)bodyPos.X, (int)bodyPos.Y, face.Width, face.Height);
                if (outfit != null) g.DrawImage(outfit, (int)outfitPos.X, (int)outfitPos.Y, outfit.Width, outfit.Height);
                if (hat != null) g.DrawImage(hat, (int)hatPos.X, (int)hatPos.Y, hat.Width, hat.Height);
                g.ResetTransform();
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
            phaseShown = true;
        }

        // ------------------------------------------------------------ blinking & napping
        void UpdateBlinkAndSleep()
        {
            int now = Environment.TickCount;
            Point c = Cursor.Position;
            if (c != lastCursor || dragging || menuOpen || Typing(now))
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
                StartBlink(now, 2);                  // eyes pop open from shut
                nextBlinkTick = now + 2500 + rng.Next(4000);
            }
            if (miffedUntil != 0 && !Miffed(now))
            {
                miffedUntil = 0;
                nextBlinkTick = now + 400;           // a blink to reset the face
            }
            // the look: every 20-45 s while awake and idle-ish; moving the mouse a lot breaks it
            if (lookUntil != 0)
            {
                Point lc = Cursor.Position;
                int moved = Math.Abs(lc.X - lookCursor.X) + Math.Abs(lc.Y - lookCursor.Y);
                // the idle look holds until you touch the mouse or keyboard (or it nods off); the random one is brief
                bool broke = moved > (idleLook ? 3 : 200) || (idleLook && Typing(now)) || dragging || Miffed(now) || Happy(now) || sleepStart >= 0;
                if (broke || !Before(now, lookUntil))
                {
                    lookUntil = 0; idleLook = false;
                    sweepStart = now; sweepFromX = eyeX; sweepFromY = eyeY; sweepBack = true;
                    nextLookTick = now + 20000 + rng.Next(25000);
                }
            }
            else if (now - lastMoveTick > IdleLookMs && sleepStart < 0 && !Groggy(now) && !Miffed(now) && !Happy(now) && !dragging && !menuOpen)
            {
                idleLook = true;
                lookUntil = NonZero(now + 10 * 86400000);
                sweepStart = now; sweepFromX = eyeX; sweepFromY = eyeY; sweepBack = false;
                lookCursor = Cursor.Position;
            }
            else if (now - nextLookTick >= 0)
            {
                if (sleepStart < 0 && !Groggy(now) && !Miffed(now) && !Happy(now) && !Typing(now) && !dragging && !menuOpen)
                {
                    lookUntil = now + 2600 + rng.Next(1500); if (lookUntil == 0) lookUntil = 1;
                    sweepStart = now; sweepFromX = eyeX; sweepFromY = eyeY; sweepBack = false;
                    lookCursor = Cursor.Position;
                }
                else nextLookTick = now + 5000;
            }
            if (sleepStart < 0 && !Groggy(now) && !Miffed(now) && cfg.NapMinutes > 0 && now - lastMoveTick > cfg.NapMinutes * 60000)
            {
                sleepStart = now;
                blinkStart = -1;
            }
            if (sleepStart < 0)
            {
                if (blinkStart >= 0 && phaseShown && now - phaseStart >= (blinkPhase == 2 ? 90 : 50))
                {
                    // advance only once the current step has actually been on screen (laptops can throttle our timer)
                    blinkPhase++; phaseStart = now; phaseShown = false;
                    if (blinkPhase > 3) { blinkStart = -1; blinkPhase = 0; }
                }
                if (blinkStart < 0 && now - nextBlinkTick >= 0 && !lastHover && !Miffed(now) && !Groggy(now) && !Happy(now))
                {
                    StartBlink(now, 1);
                    // sometimes a quick double blink
                    nextBlinkTick = rng.Next(6) == 0 ? now + BlinkMs + 120 : now + 3000 + rng.Next(4500);
                }
            }
        }

        void StartBlink(int now, int phase) { blinkStart = now; blinkPhase = phase; phaseStart = now; phaseShown = false; }

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
            else if (blinkStart >= 0) l = blinkPhase == 2 ? 1f : 0.5f;
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

        // ------------------------------------------------------------ watching you type
        // No keyboard hook: if Windows reports fresh input but the mouse hasn't moved, it was the keyboard.
        void UpdateTyping()
        {
            int now = Environment.TickCount;
            var lii = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO)) };
            if (!Native.GetLastInputInfo(ref lii)) return;
            Point c = Cursor.Position;
            // mouse buttons only (left, right, middle, X1, X2): held now, or pressed since we last asked
            foreach (int vk in new[] { 0x01, 0x02, 0x04, 0x05, 0x06 })
                if ((Native.GetAsyncKeyState(vk) & 0x8001) != 0) lastClickTick = now;
            if (lii.dwTime != lastInputTime)
            {
                bool mouseMoved = c != typingCursor;
                bool clicked = now - lastClickTick < 300;
                if (!mouseMoved && !clicked && lastInputTime != 0) typingUntil = NonZero(now + 1500);
                if (clicked) typingUntil = 0;
                if (mouseMoved) typingUntil = 0;     // grabbing the mouse hands attention back to it
                lastInputTime = lii.dwTime;
            }
            typingCursor = c;
            if (Typing(now) && !caretBusy && now - lastCaretQuery > 150)
            {
                lastCaretQuery = now;
                caretBusy = true;
                ThreadPool.QueueUserWorkItem(_ => { try { QueryCaret(); } finally { caretBusy = false; } });
            }
        }

        static int NonZero(int t) { return t == 0 ? 1 : t; }

        // Where is the text cursor? 1) classic Win32 caret, 2) accessibility caret (browsers, Electron),
        // 3) otherwise the middle of the focused control.
        void QueryCaret()
        {
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Handle) { caretValid = false; return; }
                uint pid;
                uint tid = Native.GetWindowThreadProcessId(fg, out pid);
                var gti = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                IntPtr focus = fg;
                if (Native.GetGUIThreadInfo(tid, ref gti))
                {
                    if (gti.hwndFocus != IntPtr.Zero) focus = gti.hwndFocus;
                    var r = gti.rcCaret;
                    if (gti.hwndCaret != IntPtr.Zero && (r.right > r.left || r.bottom > r.top))
                    {
                        var p = new Native.POINT { x = r.left, y = (r.top + r.bottom) / 2 };
                        Native.ClientToScreen(gti.hwndCaret, ref p);
                        caretX = p.x; caretY = p.y; caretValid = true; return;
                    }
                }
                try
                {
                    object acc;
                    Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71"); // IAccessible
                    if (Native.AccessibleObjectFromWindow(focus, 0xFFFFFFF8 /* OBJID_CARET */, ref iid, out acc) == 0 && acc != null)
                    {
                        int l, t, w, h;
                        ((Accessibility.IAccessible)acc).accLocation(out l, out t, out w, out h, 0);
                        if (w > 0 || h > 0) { caretX = l; caretY = t + h / 2; caretValid = true; return; }
                    }
                }
                catch { }
                Native.RECT fr;
                if (Native.GetWindowRect(focus, out fr) && fr.right > fr.left)
                {
                    caretX = (fr.left + fr.right) / 2; caretY = (fr.top + fr.bottom) / 2; caretValid = true; return;
                }
                caretValid = false;
            }
            catch { caretValid = false; }
        }

        // ------------------------------------------------------------ tilt & wobble
        void UpdateNose()
        {
            int now = Environment.TickCount;
            float k = body.Width / (float)srcBody.Width;
            var nose = new RectangleF(winX + bodyPos.X + NoseArt.X * k, winY + bodyPos.Y + NoseArt.Y * k, NoseArt.Width * k, NoseArt.Height * k);
            bool on = nose.Contains(Cursor.Position) && !leftDown && !dragging && sleepStart < 0 && !menuOpen && Math.Abs(angle) < 1f;
            if (!on) { noseStart = 0; noseGlare = false; return; }
            if (noseStart == 0) noseStart = NonZero(now);
            if (now - noseStart > NoseMs)
            {
                // keep it miffed while you're still on the nose; it lets go shortly after you move off
                noseGlare = true;
                miffedUntil = NonZero(now + 350);
                happyUntil = 0;
            }
        }

        void UpdateTilt()
        {
            int now = Environment.TickCount;
            float dt = lastPhysTick == 0 ? 0.033f : Math.Max(0.001f, Math.Min(0.05f, (now - lastPhysTick) / 1000f));
            lastPhysTick = now;
            if (dragging && now - lastDragTick > 60) dragVx *= (float)Math.Exp(-dt / 0.08f); // stopped moving
            // the part away from your grab lags behind: held by the head the body swings, held by the feet the head tips
            float target = dragging ? Math.Max(-Geo.MaxTilt, Math.Min(Geo.MaxTilt, dragVx * 0.006f * grabLever)) : 0f;

            // idle sway: a couple of slow side-to-side rocks from the feet, every 40-90 s while awake
            bool calm = !dragging && !leftDown && sleepStart < 0 && !Miffed(now) && !Happy(now) && !menuOpen;
            if (swayStart == 0 && now - nextSwayTick >= 0)
            {
                if (calm && Math.Abs(angle) < 0.1f && Math.Abs(angVel) < 1f) { swayStart = NonZero(now); pivotX = float.NaN; }
                else nextSwayTick = now + 5000;
            }
            if (swayStart != 0)
            {
                float t = (now - swayStart) / 1000f;
                if (!calm || t > SwaySecs) { swayStart = 0; nextSwayTick = now + 40000 + rng.Next(50000); }
                else target = SwayDeg * (float)(Math.Sin(2 * Math.PI * t / SwayPeriod) * Math.Sin(Math.PI * t / SwaySecs));
            }
            // underdamped spring: leans into the drag, wobbles back upright when dropped
            float acc = 170f * (target - angle) - 11f * angVel;
            angVel += acc * dt;
            angle += angVel * dt;
            angle = Math.Max(-Geo.MaxTilt - 2, Math.Min(Geo.MaxTilt + 2, angle));
            if (!dragging && Math.Abs(angle) < 0.05f && Math.Abs(angVel) < 0.5f) { angle = 0; angVel = 0; }
        }

        // ------------------------------------------------------------ main loop
        void OnTick(object sender, EventArgs e)
        {
            if (++tickCount % 8 == 0) CheckZOrderAndFullscreen();   // ~4x per second
            if (hidden) return;
            UpdateTyping();
            UpdateNose();
            UpdateTilt();
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
                dragVx = 0; dragVy = 0; wildUntil = 0; weeAfterDrop = 0; lastDragTick = 0;
                dragStart = Cursor.Position; winStart = new Point(winX, winY);
                if (Math.Abs(angle) < 0.5f)
                {
                    // new swing point = the grabbed pixel; lever: +1 grabbed at the top, -1 at the feet, ~0 in the middle
                    pivotX = dragStart.X - winX; pivotY = dragStart.Y - winY;
                    float com = bodyPos.Y + body.Height * 0.55f;
                    grabLever = Math.Max(-1f, Math.Min(1f, (com - pivotY) / (body.Height * 0.45f)));
                }
                shakeTurns = 0; shakeDir = 0; shakeLastX = dragStart.X; shakeTimes.Clear(); shakeAngry = false;
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
            if (!leftDown) { Pet(); return; }
            if (cfg.Locked) return;
            Point c = Cursor.Position;
            {
                int now = Environment.TickCount, dtm = Math.Max(1, now - lastDragTick);
                if (lastDragTick != 0 && dtm < 200)
                {
                    dragVx = dragVx * 0.6f + 0.4f * ((c.X - lastDragX) * 1000f / dtm);
                    dragVy = dragVy * 0.6f + 0.4f * ((c.Y - lastDragY) * 1000f / dtm);
                    float speed = (float)Math.Sqrt(dragVx * dragVx + dragVy * dragVy);
                    if (speed > WildSpeed * cfg.Zoom) wildUntil = NonZero(now + WeeHoldMs);   // stays open a moment after it slows
                }
                lastDragTick = now; lastDragX = c.X; lastDragY = c.Y;
            }
            if (!dragging && (Math.Abs(c.X - dragStart.X) > 3 || Math.Abs(c.Y - dragStart.Y) > 3)) dragging = true;
            if (dragging)
            {
                // count sharp left/right direction changes = being shaken
                // shakeLastX tracks the furthest point in the current direction
                int d = c.X - shakeLastX;
                // a fast swing is fun (open mouth, from speed); lots of hard back-and-forth in quick succession is a shake (angry)
                int swing = (int)(ShakeSwing * cfg.Zoom);
                if (shakeDir == 0) { if (Math.Abs(d) >= swing) { shakeDir = Math.Sign(d); shakeLastX = c.X; } }
                else if (d * shakeDir > 0) shakeLastX = c.X;
                else if (Math.Abs(d) >= swing)
                {
                    int tnow = Environment.TickCount;
                    shakeTurns++; shakeDir = -shakeDir; shakeLastX = c.X;
                    shakeTimes.Add(tnow);
                    shakeTimes.RemoveAll(t => tnow - t > ShakeWindowMs);
                    if (shakeTimes.Count >= ShakeTurnsToAnger) shakeAngry = true;
                }
                MoveTo(winStart.X + c.X - dragStart.X, winStart.Y + c.Y - dragStart.Y);
            }
        }

        // rub back and forth over the head
        void Pet()
        {
            Point c = Cursor.Position;
            int now = Environment.TickCount;
            float headBottom = winY + bodyPos.Y + body.Height * 0.42f;
            if (c.Y > headBottom || !IsOverPet(c)) { petTurns = 0; petDir = 0; return; }
            int swing = (int)Math.Max(4, 6 * cfg.Zoom);
            int d = c.X - petLastX;
            if (petDir == 0) { if (Math.Abs(d) >= swing) { petDir = Math.Sign(d); petLastX = c.X; } else if (petLastX == 0) petLastX = c.X; return; }
            if (d * petDir > 0) { petLastX = c.X; return; }
            if (Math.Abs(d) < swing) return;
            petDir = -petDir; petLastX = c.X;
            if (petTurns == 0 || now - petFirstTick > 1600) { petTurns = 0; petFirstTick = now; }
            petTurns++;
            if (petTurns >= 3)
            {
                happyUntil = NonZero(now + 2000);   // keeps extending while you keep petting
                miffedUntil = 0; groggyUntil = 0; sleepStart = -1; blinkStart = -1;
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
                    if (shakeAngry) GetMiffed(3000);   // you shook it: stays cross for a bit after you let go
                    else if (Before(now, wildUntil)) weeAfterDrop = NonZero(now + WeeAfterDropMs);   // let go mid-swing: keep the weee while it wobbles
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
        ToolStripMenuItem[] hatItems;
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
            var hats = new ToolStripMenuItem("Hat");
            hatItems = new ToolStripMenuItem[Geo.HatCount + 1];
            for (int i = 0; i <= Geo.HatCount; i++)
            {
                int n = i;
                hatItems[i] = new ToolStripMenuItem(Geo.HatNames[i], null, (s, e) => SetHat(n));
                hats.DropDownItems.Add(hatItems[i]);
                if (i == 0) hats.DropDownItems.Add(new ToolStripSeparator());
            }
            KeepOnScreen(hats);
            menu.Items.Add(hats);
            menu.Items.Add(new ToolStripMenuItem("Next outfit", null, (s, e) => SetOutfit((cfg.Outfit + 1) % (OutfitCount + 1))));
            menu.Items.Add(new ToolStripMenuItem("Next hat", null, (s, e) => SetHat((cfg.Hat + 1) % (Geo.HatCount + 1))));

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
            menu.Items.Add(new ToolStripMenuItem("Check for updates...", null, (s, e) => CheckForUpdates(true)));
            menu.Items.Add(new ToolStripMenuItem("DogeBuddy v" + Updater.Short(Updater.Current)) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Close()));
        }

        void UpdateMenuChecks()
        {
            for (int i = 0; i <= OutfitCount; i++) outfitItems[i].Checked = cfg.Outfit == i;
            for (int i = 0; i <= Geo.HatCount; i++) hatItems[i].Checked = cfg.Hat == i;
            foreach (var p in sizeItems) p.Value.Checked = Math.Abs(p.Key - cfg.Zoom) < 0.01f;
            miLock.Checked = cfg.Locked;
            miFull.Checked = cfg.HideFullscreen;
            miStartup.Checked = GetStartup();
            miTray.Checked = cfg.Tray;
            foreach (ToolStripMenuItem it in napMenu.DropDownItems) it.Checked = (int)it.Tag == cfg.NapMinutes;
        }

        void SetHat(int n)
        {
            cfg.Hat = n; cfg.Save();
            ApplyHat();
            Render(true);
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

    // ---------------------------------------------------------------- updater
    // Asks GitHub for the latest release, downloads DogeBuddy.exe, checks it against the
    // release's SHA256SUMS.txt, then swaps it in (a running exe can be renamed, not overwritten).
    static class Updater
    {
        const string DefaultApi = "https://api.github.com/repos/SnazzyGaz/DogeBuddy/releases/latest";
        // for local testing only: point at a fake release feed
        static string Api { get { return Environment.GetEnvironmentVariable("DOGEBUDDY_UPDATE_API") ?? DefaultApi; } }

        public static Version Current { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public static string Short(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        public class Release { public Version Version; public string Notes, ExeUrl, SumsUrl; }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "DogeBuddy/" + Short(Current);
            return wc;
        }

        public static Release GetLatest()
        {
            string json;
            using (var wc = Client()) { wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json"; json = wc.DownloadString(Api); }
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var rel = new Release();
            string tag = ((d["tag_name"] as string) ?? "").TrimStart('v', 'V');
            Version v;
            if (!Version.TryParse(tag, out v)) throw new Exception("Unrecognised version tag: " + tag);
            rel.Version = new Version(v.Major, v.Minor, Math.Max(0, v.Build), 0);
            object body; if (d.TryGetValue("body", out body)) rel.Notes = body as string;
            foreach (var o in (object[])d["assets"])
            {
                var a = (Dictionary<string, object>)o;
                string name = (a["name"] as string) ?? "", url = a["browser_download_url"] as string;
                if (name.Equals("DogeBuddy.exe", StringComparison.OrdinalIgnoreCase)) rel.ExeUrl = url;
                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) rel.SumsUrl = url;
            }
            if (rel.ExeUrl == null || rel.SumsUrl == null) throw new Exception("The latest release is missing DogeBuddy.exe or SHA256SUMS.txt.");
            return rel;
        }

        public static void DownloadAndSwap(Release rel)
        {
            string exe = Application.ExecutablePath;
            string tmp = exe + ".download", old = exe + ".old";

            string sums;
            using (var wc = Client()) sums = wc.DownloadString(rel.SumsUrl);
            string expected = null;
            foreach (var line in sums.Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].Equals("DogeBuddy.exe", StringComparison.OrdinalIgnoreCase)) expected = parts[0].ToLowerInvariant();
            }
            if (expected == null || expected.Length != 64) throw new Exception("SHA256SUMS.txt has no hash for DogeBuddy.exe.");

            try { File.Delete(tmp); } catch { }
            using (var wc = Client()) wc.DownloadFile(rel.ExeUrl, tmp);

            byte[] data = File.ReadAllBytes(tmp);
            string actual;
            using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            if (actual != expected || data.Length < 1024 || data[0] != 'M' || data[1] != 'Z')
            {
                try { File.Delete(tmp); } catch { }
                throw new Exception("The download didn't match its published SHA-256 hash, so it was thrown away.");
            }

            try { File.Delete(old); } catch { }
            File.Move(exe, old);                       // allowed while running
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }      // put the original back
        }

        public static void CleanUpOld()
        {
            string old = Application.ExecutablePath + ".old";
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); } catch { Thread.Sleep(250); }
            }
            try { File.Delete(Application.ExecutablePath + ".download"); } catch { }
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

        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gti);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("oleacc.dll")]
        public static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);
        // Laptops on battery slow down background apps' timers (EcoQoS), which can swallow quick animations like blinks.
        public static void NoPowerThrottling()
        {
            try
            {
                var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = 0 /* off */ };
                SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref st, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
            }
            catch { } // older Windows: not available, nothing to do
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
