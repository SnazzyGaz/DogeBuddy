// Updater: manual "Check for updates..." against this repo's GitHub releases. The new exe is only
// installed if its SHA-256 matches the SHA256SUMS.txt published with the release.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace BigBootyCheems
{
    static class Updater
    {
        const string DefaultApi = "https://api.github.com/repos/SnazzyGaz/BigBootyCheems/releases/latest";
        // for local testing only: point at a fake release feed
        static string Api { get { return Environment.GetEnvironmentVariable("BBC_UPDATE_API") ?? DefaultApi; } }

        public static Version Current { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public static string Short(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        public class Release { public Version Version; public string Notes, ExeUrl, SumsUrl; }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "BigBootyCheems/" + Short(Current);
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
                if (name.Equals("BigBootyCheems.exe", StringComparison.OrdinalIgnoreCase)) rel.ExeUrl = url;
                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) rel.SumsUrl = url;
            }
            if (rel.ExeUrl == null || rel.SumsUrl == null) throw new Exception("The latest release is missing BigBootyCheems.exe or SHA256SUMS.txt.");
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
                if (parts.Length == 2 && parts[1].Equals("BigBootyCheems.exe", StringComparison.OrdinalIgnoreCase)) expected = parts[0].ToLowerInvariant();
            }
            if (expected == null || expected.Length != 64) throw new Exception("SHA256SUMS.txt has no hash for BigBootyCheems.exe.");

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
}
