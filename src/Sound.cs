// Sound: a tiny always-warm mixer. Starting an audio stream from cold can swallow the first few
// milliseconds (the sharp crack of a slap), which made some hits sound muffled or distant. So while
// the cursor is near the cheems (or a tool is held) we keep one waveOut stream running, playing
// silence, and mix hits straight into it. The stream shuts off after a while of nothing happening
// so it never keeps the PC awake.
using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;

namespace BigBootyCheems
{
    class Sfx
    {
        internal readonly short[] Samples;      // mono, Mixer.Rate
        readonly SoundPlayer fallback;

        public Sfx(Stream wav)
        {
            byte[] bytes;
            using (var ms = new MemoryStream()) { wav.CopyTo(ms); bytes = ms.ToArray(); }
            Samples = Decode(bytes);
            fallback = new SoundPlayer(new MemoryStream(bytes));
            try { fallback.Load(); } catch { }
        }

        public void Play()
        {
            if (Samples != null && Mixer.Play(Samples)) return;
            try { fallback.Play(); } catch { }
        }

        // 16-bit PCM wav -> mono at the mixer rate (linear resample)
        static short[] Decode(byte[] b)
        {
            int fmtAt = -1, dataAt = -1, dataLen = 0;
            for (int p = 12; p + 8 <= b.Length;)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
                int len = BitConverter.ToInt32(b, p + 4);
                if (id == "fmt ") fmtAt = p + 8;
                else if (id == "data") { dataAt = p + 8; dataLen = Math.Min(len, b.Length - p - 8); }
                p += 8 + len + (len & 1);
            }
            if (fmtAt < 0 || dataAt < 0) return null;
            int tag = BitConverter.ToInt16(b, fmtAt), ch = BitConverter.ToInt16(b, fmtAt + 2);
            int rate = BitConverter.ToInt32(b, fmtAt + 4), bits = BitConverter.ToInt16(b, fmtAt + 14);
            if (tag != 1 || bits != 16 || ch < 1) return null;
            int n = dataLen / (2 * ch);
            var mono = new float[n];
            for (int i = 0; i < n; i++)
            {
                float s = 0;
                for (int c = 0; c < ch; c++) s += BitConverter.ToInt16(b, dataAt + (i * ch + c) * 2);
                mono[i] = s / ch;
            }
            int m = (int)((long)n * Mixer.Rate / rate);
            var outp = new short[m];
            for (int i = 0; i < m; i++)
            {
                double src = (double)i * rate / Mixer.Rate; int k = (int)src; double t = src - k;
                float a = mono[Math.Min(k, n - 1)], c2 = mono[Math.Min(k + 1, n - 1)];
                outp[i] = (short)(a + (c2 - a) * t);
            }
            return outp;
        }
    }

    static class Mixer
    {
        public const int Rate = 44100;
        const int BufSamples = 882, NBuf = 4;            // 20 ms buffers, ~60-80 ms queued
        const int IdleOffMs = 20000;                     // stop the stream after this long with nothing to do
        static IntPtr hwo;
        static IntPtr[] hdrs = new IntPtr[NBuf], datas = new IntPtr[NBuf];
        static readonly List<int[]> voices = new List<int[]>();   // {soundIndex, position}
        static readonly List<short[]> sounds = new List<short[]>();
        static readonly object gate = new object();
        static Thread thread; static volatile bool running; static volatile int lastUse;
        static bool broken;
        static readonly short[] mix = new short[BufSamples];
        static readonly int[] acc = new int[BufSamples];

        // keep (or get) the stream running; call while a hit is likely soon
        public static void Warm()
        {
            lastUse = Environment.TickCount;
            if (running || broken) return;
            lock (gate)
            {
                if (running || broken) return;
                if (!Open()) { broken = true; return; }
                running = true;
                thread = new Thread(Pump) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "bbc-audio" };
                thread.Start();
            }
        }

        public static bool Play(short[] s)
        {
            Warm();
            if (!running) return false;
            lock (gate)
            {
                int idx = sounds.IndexOf(s);
                if (idx < 0) { sounds.Add(s); idx = sounds.Count - 1; }
                if (voices.Count >= 6) voices.RemoveAt(0);
                voices.Add(new[] { idx, 0 });
            }
            return true;
        }

        static bool Open()
        {
            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * 2, nBlockAlign = 2, wBitsPerSample = 16 };
            try
            {
                if (waveOutOpen(out hwo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { hwo = IntPtr.Zero; return false; }
                int hs = Marshal.SizeOf(typeof(WAVEHDR));
                for (int i = 0; i < NBuf; i++)
                {
                    datas[i] = Marshal.AllocHGlobal(BufSamples * 2);
                    Marshal.Copy(new short[BufSamples], 0, datas[i], BufSamples);
                    hdrs[i] = Marshal.AllocHGlobal(hs);
                    Marshal.StructureToPtr(new WAVEHDR { lpData = datas[i], dwBufferLength = BufSamples * 2 }, hdrs[i], false);
                    waveOutPrepareHeader(hwo, hdrs[i], hs);
                }
                for (int i = 0; i < NBuf; i++) waveOutWrite(hwo, hdrs[i], hs);
                return true;
            }
            catch { Close(); return false; }
        }

        static void Close()
        {
            try
            {
                if (hwo != IntPtr.Zero)
                {
                    waveOutReset(hwo);
                    int hs = Marshal.SizeOf(typeof(WAVEHDR));
                    for (int i = 0; i < NBuf; i++) if (hdrs[i] != IntPtr.Zero) waveOutUnprepareHeader(hwo, hdrs[i], hs);
                    waveOutClose(hwo); hwo = IntPtr.Zero;
                }
            }
            catch { }
            for (int i = 0; i < NBuf; i++)
            {
                if (hdrs[i] != IntPtr.Zero) { Marshal.FreeHGlobal(hdrs[i]); hdrs[i] = IntPtr.Zero; }
                if (datas[i] != IntPtr.Zero) { Marshal.FreeHGlobal(datas[i]); datas[i] = IntPtr.Zero; }
            }
        }

        static void Pump()
        {
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            int flagsOfs = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
            while (true)
            {
                bool busy;
                lock (gate) busy = voices.Count > 0;
                if (!busy && Environment.TickCount - lastUse > IdleOffMs)
                {
                    lock (gate) { running = false; Close(); }
                    return;
                }
                for (int i = 0; i < NBuf; i++)
                {
                    if ((Marshal.ReadInt32(hdrs[i], flagsOfs) & 1) == 0) continue;   // WHDR_DONE: free to refill
                    Fill();
                    Marshal.Copy(mix, 0, datas[i], BufSamples);
                    waveOutWrite(hwo, hdrs[i], hs);
                }
                Thread.Sleep(3);
            }
        }

        static void Fill()
        {
            Array.Clear(acc, 0, BufSamples);
            lock (gate)
            {
                for (int v = voices.Count - 1; v >= 0; v--)
                {
                    var s = sounds[voices[v][0]]; int pos = voices[v][1];
                    int n = Math.Min(BufSamples, s.Length - pos);
                    for (int i = 0; i < n; i++) acc[i] += s[pos + i];
                    voices[v][1] = pos + n;
                    if (pos + n >= s.Length) voices.RemoveAt(v);
                }
            }
            for (int i = 0; i < BufSamples; i++) mix[i] = (short)Math.Max(-32768, Math.Min(32767, acc[i]));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
    }
}
