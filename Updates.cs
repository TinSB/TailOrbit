// TailOrbit 自己的更新：问 GitHub 上最新发布的版本，比这一份新就下载它的安装程序（TailOrbit-Setup-<版本>.exe）。
// 下载完先核对：大小和 SHA-256 要和 GitHub 发布页给的一样，文件得是 TailOrbit、版本号对得上，对不上就删掉、不装。
// 装：不弹窗口地运行安装程序，它先让开着的这一份退出，装好再打开新版的主界面。
// 平时只在设置页点“检查更新”时才连 GitHub；开了“自动下载更新”，启动一分钟后、之后每 24 小时自己看一次，有新版就在后台下好，等用户点“重启并更新”
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace TailOrbit
{
    static class Updates
    {
        const string Api = "https://api.github.com/repos/TinSB/TailOrbit/releases/latest";
        static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailOrbit", "update");

        // idle 还没看过 / checking / latest 已是最新 / available 有新版 / downloading / ready 已下好 / installing / failed
        public static string State = "idle";
        public static string Latest = "", Error = "";   // 新版本号；失败原因（存简体中文原文，显示时再翻）
        public static double Fraction;                   // 下载进度 0～1
        public static event Action Changed;              // 都在界面线程上触发
        static Host host;
        static Dispatcher ui;
        static string url, digest, file;
        static long size;
        static readonly DispatcherTimer daily = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };

        public static void Start(Host h)
        {
            host = h;
            ui = Dispatcher.CurrentDispatcher;
            daily.Tick += delegate { if (Settings.AutoUpdate) Check(true); };
            daily.Start();
            if (Settings.AutoUpdate) Ui.After(60000, () => Check(true));   // 刚开机网络可能还没好
        }

        // 设置页上那个按钮：下好了就装；知道有新版就下载、下好直接装；不然去检查
        public static void Next()
        {
            if (State == "ready") Install();
            else if (State == "available" || State == "failed" && url != null) Download(true);
            else Check(false);
        }

        // 拨“自动下载更新”：打开时马上在后台看一次，已经知道有新版就直接下
        public static void Auto(bool on)
        {
            Settings.AutoUpdate = on;
            if (!on) return;
            if (State == "available") Download(false);
            else Check(true);
        }

        // background：后台自己看的，有新版接着在后台下好；用户点的只报有没有，等他点“立即更新”
        static void Check(bool background)
        {
            if (State == "checking" || State == "downloading" || State == "ready" || State == "installing") return;
            Set("checking");
            Task.Run(() => Ask()).ContinueWith(t =>
            {
                if (t.IsFaulted) Fail(Why(t.Exception.GetBaseException()));
                else if (!t.Result) Set("latest");
                else if (background) Download(false);
                else Set("available");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // install：用户点的，下好直接装；后台下的，下好提示一声，等用户点
        static void Download(bool install)
        {
            Fraction = 0;
            Set("downloading");
            Task.Run(() => Fetch()).ContinueWith(t =>
            {
                string why = t.IsFaulted ? Why(t.Exception.GetBaseException()) : t.Result;
                if (why != null) { Fail(why); return; }
                Set("ready");
                if (install) Install();
                else host.Notify(L.T("TailOrbit {0} 已下载", Latest), L.T("重启 TailOrbit 即可完成更新。"), false, () => host.ShowPage("settings"));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 不弹窗口地运行安装程序：它先让开着的这一份（就是本程序）退出，装好打开新版的主界面
        static void Install()
        {
            Set("installing");
            try { Process.Start(new ProcessStartInfo(file, "--quiet --open") { UseShellExecute = false }); }
            catch (Win32Exception e) { Fail(e.Message); }
        }

        // 读 GitHub 上最新发布的版本；比这一份新返回 true，并记下安装程序的地址、大小和 SHA-256
        static bool Ask()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(Api);
            req.UserAgent = "TailOrbit/" + Setup.Version;   // GitHub 不收没有 User-Agent 的请求
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;
            string json;
            using (var res = req.GetResponse())
            using (var r = new StreamReader(res.GetResponseStream())) json = r.ReadToEnd();
            var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            string tag = ((Tailscale.Get(o, "tag_name") as string) ?? "").TrimStart('v');
            Version v;
            if (!Version.TryParse(tag, out v)) throw new InvalidDataException("tag");
            if (v <= new Version(Setup.Version)) return false;
            var asset = ((Tailscale.Get(o, "assets") as object[]) ?? new object[0]).OfType<Dictionary<string, object>>()
                .FirstOrDefault(a => (Tailscale.Get(a, "name") as string) == "TailOrbit-Setup-" + tag + ".exe");
            string sha = asset == null ? null : Tailscale.Get(asset, "digest") as string;
            if (sha == null || !sha.StartsWith("sha256:")) throw new InvalidDataException("asset");   // 没有能核对的安装包：不下
            Latest = tag;
            url = (string)Tailscale.Get(asset, "browser_download_url");
            size = Convert.ToInt64(Tailscale.Get(asset, "size"));
            digest = sha.Substring(7).ToLowerInvariant();
            file = Path.Combine(Folder, "TailOrbit-Setup-" + tag + ".exe");
            return true;
        }

        // 下载并核对；返回 null 表示好了，否则是原因。已经下好、核对过的直接用
        static string Fetch()
        {
            if (File.Exists(file) && Verify(file) == null) return null;
            Directory.CreateDirectory(Folder);
            foreach (var old in Directory.GetFiles(Folder)) File.Delete(old);   // 以前下的：装过的旧版、没下完的
            string part = file + ".part";
            var req = (HttpWebRequest)WebRequest.Create(url);   // GitHub 会转到下载服务器
            req.UserAgent = "TailOrbit/" + Setup.Version;
            req.ReadWriteTimeout = 30000;
            using (var res = req.GetResponse())
            using (var from = res.GetResponseStream())
            using (var to = File.Create(part))
            {
                long got = 0;
                int shown = -1;
                var buf = new byte[1 << 16];
                for (int n; (n = from.Read(buf, 0, buf.Length)) > 0; )
                {
                    to.Write(buf, 0, n);
                    got += n;
                    int pct = (int)(100 * got / Math.Max(1, size));
                    if (pct == shown) continue;
                    shown = pct;
                    ui.BeginInvoke(new Action(() => { Fraction = Math.Min(1, pct / 100.0); Raise(); }));
                }
            }
            string why = Verify(part);
            if (why != null) { File.Delete(part); return why; }
            File.Move(part, file);
            return null;
        }

        static string Verify(string path)
        {
            if (new FileInfo(path).Length != size) return "安装包不完整，请重试。";
            using (var s = File.OpenRead(path))
            using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant() != digest) return "安装包校验失败，已删除。";
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(path); }
            catch (BadImageFormatException) { return "安装包校验失败，已删除。"; }
            if (name.Name != "TailOrbit" || new Version(name.Version.Major, name.Version.Minor, name.Version.Build) != new Version(Latest)) return "安装包校验失败，已删除。";
            return null;
        }

        static string Why(Exception e)
        {
            if (e is WebException) return "无法连接 GitHub，请检查网络连接后重试。";
            if (e is IOException || e is UnauthorizedAccessException) return e.Message;
            return "无法读取版本信息，请稍后重试。";
        }

        static void Fail(string why)
        {
            Error = why;
            Set("failed");
        }

        static void Set(string state)
        {
            State = state;
            Raise();
        }

        static void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }
    }
}
