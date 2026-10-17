// Clothes: headgear and shoes drawn on top of the cheems after the wobble, so they stay rigid.
// Items are full-canvas PNGs, either the clothes canvas (450x732, hero 58 px down) or the hero canvas
// (450x674), embedded as Hat_<Name>.png / Shoes_<Name>.png.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BigBootyCheems
{
    partial class Buddy
    {
        // one wearable slot (headgear, shoes, ...): items are embedded as <Prefix><Name>.png
        class Slot
        {
            public string Title, Prefix, Key, Current = "";
            public bool Warps;          // body clothing: baked into the art so it wobbles with him
            public List<string> Names = new List<string>();
            public int[] Px;            // the item being worn, art-sized, premultiplied
            public int[] Px2;           // optional second frame (Name2.png), flipped between to animate
            public bool Frame2;
        }
        readonly List<Slot> slots = new List<Slot>();

        static readonly Dictionary<string, string> Pretty = new Dictionary<string, string>
        {
            { "Astro", "Astronaut Helmet" }, { "BikeHelm", "Motorbike Helmet" }, { "HardHat", "Hard Hat" }, { "Partyhat", "Party Hat" },
            { "boots", "Boots" }, { "crocs", "Crocs" }, { "jordans", "Jordans" },
            { "Hdres", "Headdress" }, { "Policehat", "Police Hat" }, { "ltherd", "Leather Daddy Vest" },
            { "Gasm", "Gas Mask" }, { "sung", "Sunglasses" },
            { "cb", "Cowboy Hat" }, { "hawa", "Hawaiian Shirt" }, { "HiVis", "Hi-Vis Vest" },
            { "bnda", "Bandit Bandana" }, { "pplr", "Propeller Hat" }, { "rollers", "Roller Skates" },
        };

        static string Label(string n)
        {
            string p;
            if (Pretty.TryGetValue(n, out p)) return p;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n.Length; i++) { if (i > 0 && char.IsUpper(n[i]) && !char.IsUpper(n[i - 1])) sb.Append(' '); sb.Append(i == 0 ? char.ToUpper(n[i]) : n[i]); }
            return sb.ToString();
        }

        void SetupClothes()
        {
            // inserted in reverse so they read Headgear, Face, Top, Shoes (and layer shoes, top, face, hat)
            baseOpen = (int[])srcOpen.Clone(); baseHalf = (int[])srcHalf.Clone(); baseShut = (int[])srcShut.Clone();
            // everything is baked into his art so it moves with him (sway, head turning, wobble)
            AddSlot("Shoes", "Shoes_", "Shoes", true);
            AddSlot("Top", "Top_", "Top", true);
            AddSlot("Face", "Face_", "Face", true);
            AddSlot("Headgear", "Hat_", "Hat", true);
        }
        int[] baseOpen, baseHalf, baseShut;     // the bare hero (each eye frame)

        void AddSlot(string title, string prefix, string key, bool warps)
        {
            var slot = new Slot { Title = title, Prefix = prefix, Key = key, Warps = warps };
            foreach (var r in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (r.StartsWith(prefix) && r.EndsWith(".png")) slot.Names.Add(r.Substring(prefix.Length, r.Length - prefix.Length - 4));
            // "X2" next to "X" is X's second animation frame, not an item of its own
            slot.Names.RemoveAll(n => n.EndsWith("2") && slot.Names.Contains(n.Substring(0, n.Length - 1)));
            if (slot.Names.Count == 0) return;
            slot.Names.Sort(StringComparer.OrdinalIgnoreCase);
            slots.Insert(0, slot);
            var menuItem = new ToolStripMenuItem(title);
            var none = new ToolStripMenuItem("None", null, (s, e) => Wear(slot, ""));
            menuItem.DropDownItems.Add(none);
            menuItem.DropDownItems.Add(new ToolStripSeparator());
            var items = new Dictionary<string, ToolStripMenuItem>();
            foreach (var n in slot.Names)
            {
                string name = n;
                var it = new ToolStripMenuItem(Label(n), null, (s, e) => Wear(slot, name));
                items[n] = it; menuItem.DropDownItems.Add(it);
            }
            menuItem.DropDownOpening += (s, e) =>
            {
                none.Checked = slot.Current == "";
                foreach (var kv in items) kv.Value.Checked = kv.Key == slot.Current;
            };
            menu.Items.Insert(0, menuItem);
            string saved = LoadSetting(key);
            if (!string.IsNullOrEmpty(saved) && slot.Names.Contains(saved)) Wear(slot, saved, false);
        }

        void Wear(Slot slot, string n) { Wear(slot, n, true); }

        void Wear(Slot slot, string n, bool save)
        {
            slot.Current = n;
            slot.Px = null; slot.Px2 = null; slot.Frame2 = false;
            if (n != "")
            {
                using (var s = Res(slot.Prefix + n + ".png")) using (var raw = new Bitmap(s)) slot.Px = Rasterize(raw);
                var s2 = Res(slot.Prefix + n + "2.png");
                if (s2 != null) using (s2) using (var raw = new Bitmap(s2)) slot.Px2 = Rasterize(raw);
            }
            if (save) SaveSetting(slot.Key, n);
            if (slot.Warps) RebuildFrames();
            artValid = false;
            if (IsHandleCreated) Render();
        }

        // same scaling as the hero art. Items drawn on the plain hero canvas (450x674) are moved down
        // to where the hero sits on the taller clothes canvas (450x732).
        int[] Rasterize(Bitmap raw)
        {
            var pix = new int[w * h];
            float oy = raw.Height <= Tune.CanvasH - Tune.HeroTop ? Tune.HeroTop * Tune.Scale : 0;
            using (var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(raw, 0, oy, raw.Width * Tune.Scale, raw.Height * Tune.Scale);
                }
                var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                Marshal.Copy(d.Scan0, pix, 0, pix.Length); b.UnlockBits(d);
            }
            return pix;
        }

        // body clothing goes into the art itself (each eye frame), so the warp moves it with the body
        void RebuildFrames()
        {
            Array.Copy(baseOpen, srcOpen, srcOpen.Length);
            Array.Copy(baseHalf, srcHalf, srcHalf.Length);
            Array.Copy(baseShut, srcShut, srcShut.Length);
            for (int i = slots.Count - 1; i >= 0; i--)
                if (slots[i].Warps && slots[i].Px != null)
                    foreach (var f in new[] { srcOpen, srcHalf, srcShut }) Over(slots[i].Frame2 && slots[i].Px2 != null ? slots[i].Px2 : slots[i].Px, f, w, 0, 0);
        }

        // two-frame items (the propeller hat) flip frames continuously
        int lastSpinFlip;
        void AnimateClothes(int now)
        {
            bool any = false;
            foreach (var sl in slots) if (sl.Px2 != null) any = true;
            if (!any) return;
            const int every = 45;   // full speed, all the time
            if (now - lastSpinFlip < every) return;
            lastSpinFlip = now;
            foreach (var sl in slots) if (sl.Px2 != null) sl.Frame2 = !sl.Frame2;
            RebuildFrames();
            artValid = false;
            if (!timer.Enabled) Render();   // otherwise the render timer picks it up
        }

        // rigid items (hats, shoes) over the (already warped) art, bottom slot first
        void CompositeHat()
        {
            for (int i = slots.Count - 1; i >= 0; i--)
                if (!slots[i].Warps && slots[i].Px != null)
                    Over(slots[i].Frame2 && slots[i].Px2 != null ? slots[i].Px2 : slots[i].Px, artPx, cw, Tune.Pad, Tune.Pad);
        }

        // item (art-sized) over dst, which is 'stride' wide with the art placed at (ox, oy)
        void Over(int[] item, int[] artPx, int stride, int ox, int oy)
        {
            for (int y = 0; y < h; y++)
            {
                int row = (y + oy) * stride + ox, hr = y * w;
                for (int x = 0; x < w; x++)
                {
                    int c = item[hr + x];
                    if (c == 0) continue;
                    int a = (c >> 24) & 0xFF;
                    if (a == 255) { artPx[row + x] = c; continue; }
                    int d = artPx[row + x], k = 255 - a, r = 0;
                    for (int sh = 0; sh < 32; sh += 8)
                        r |= Math.Min(255, ((c >> sh) & 0xFF) + (((d >> sh) & 0xFF) * k + 127) / 255) << sh;
                    artPx[row + x] = r;
                }
            }
        }

        // ---------------------------------------------------------- tiny settings file
        static string SettingsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BigBootyCheems", "settings.ini"); }
        }
        static string LoadSetting(string key)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                foreach (var line in File.ReadAllLines(SettingsPath))
                    if (line.StartsWith(key + "=")) return line.Substring(key.Length + 1).Trim();
            }
            catch { }
            return null;
        }
        static void SaveSetting(string key, string value)
        {
            try
            {
                var lines = new List<string>();
                if (File.Exists(SettingsPath)) lines.AddRange(File.ReadAllLines(SettingsPath));
                lines.RemoveAll(l => l.StartsWith(key + "="));
                lines.Add(key + "=" + value);
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, lines.ToArray());
            }
            catch { }
        }
    }
}
