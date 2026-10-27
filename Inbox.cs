// 后台常驻的两件事：别的设备发来的文件（收件），和重要设备的掉线提醒。主界面关着也照常工作。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace TailOrbit
{
    // 正在收的一批文件，给星网画进度用
    class Incoming
    {
        public double Fraction;   // 0～1；不知道总大小时 -1
        public string From;       // 发送方的 IP；还没认出来时 null
        public bool Done, Ok;
    }

    // 收件：订阅 Tailscale 的实时事件，里面的 IncomingFiles 就是正在收的文件。
    // 事件里不说是谁发的：先看连到本机收件端口（PeerAPI）的是哪台设备，看不到再看这段时间谁发来的字节最多
    static class Inbox
    {
        class Part
        {
            public string Name = "", Final;
            public long Size = -1, Got;
            public bool Done;
        }

        class Batch
        {
            public readonly Dictionary<string, Part> Parts = new Dictionary<string, Part>();
            public readonly Dictionary<string, long> RxStart = new Dictionary<string, long>();   // 按 nodekey：这批开始时各设备已经发来的字节
            public DateTime Touched;
            public Device From;
            public bool Resolving;
        }

        public static event Action<Incoming> Changed;   // 在界面线程上触发
        static Dispatcher ui;
        static volatile int[] ports = new int[0];   // 本机的收件端口（IPv4、IPv6 各一个）
        static readonly Dictionary<string, long> rx = new Dictionary<string, long>();
        static Batch batch;
        static readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        public static void Start()
        {
            ui = Dispatcher.CurrentDispatcher;
            tick.Tick += delegate { Check(); };
            Listen();
        }

        static void Listen()
        {
            Process p;
            try { p = ChildJob.Start(Tailscale.Psi("debug watch-ipn --peer-changes=false --peer-patches=false")); }
            catch (Win32Exception) { Ui.After(30000, Listen); return; }   // 没装 Tailscale：过会儿再看
            Task.Run(() => { ports = Tailscale.PeerApiPorts(); });
            var buf = new StringBuilder();
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                buf.AppendLine(e.Data);
                if (e.Data != "}") return;   // 每条事件是一段缩进的 JSON，以单独一行 } 结尾
                string text = buf.ToString();
                buf.Clear();
                try { Handle(js, text); }
                catch (Exception ex) { Log.Write(ex); }   // 后台线程漏出异常会让整个程序退出
            };
            p.ErrorDataReceived += (s, e) => { };
            p.EnableRaisingEvents = true;
            // Tailscale 重启、升级时事件流会断：等一会儿重新订阅
            p.Exited += delegate { ui.BeginInvoke(new Action(() => Ui.After(15000, Listen))); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        static void Handle(JavaScriptSerializer js, string text)
        {
            bool files = text.IndexOf("\"IncomingFiles\"", StringComparison.Ordinal) >= 0;
            if (!files && text.IndexOf("\"LivePeers\"", StringComparison.Ordinal) < 0) return;
            Dictionary<string, object> o;
            try { o = js.DeserializeObject(text) as Dictionary<string, object>; }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            var list = Tailscale.Get(o, "IncomingFiles") as object[];
            if (list != null)
            {
                var from = Ports.Remotes(ports);   // 趁连接还在，马上看是谁连进来的
                ui.BeginInvoke(new Action(() => OnFiles(list, from)));
            }
            var live = Tailscale.Get(Tailscale.Get(o, "Engine") as Dictionary<string, object>, "LivePeers") as Dictionary<string, object>;
            if (live == null) return;
            var now = new Dictionary<string, long>();
            foreach (var kv in live)
            {
                var peer = kv.Value as Dictionary<string, object>;
                if (peer != null) now[kv.Key] = Convert.ToInt64(Tailscale.Get(peer, "RxBytes") ?? 0L);
            }
            ui.BeginInvoke(new Action(() => OnEngine(now)));
        }

        static void OnFiles(object[] list, List<string> remotes)
        {
            if (batch == null)
            {
                batch = new Batch();
                foreach (var kv in rx) batch.RxStart[kv.Key] = kv.Value;
            }
            foreach (var f in list.OfType<Dictionary<string, object>>())
            {
                string name = (Tailscale.Get(f, "Name") as string) ?? "";
                string key = name + "|" + Tailscale.Get(f, "Started");
                Part part;
                if (!batch.Parts.TryGetValue(key, out part)) batch.Parts[key] = part = new Part { Name = name };
                part.Size = Convert.ToInt64(Tailscale.Get(f, "DeclaredSize") ?? -1L);
                part.Got = Convert.ToInt64(Tailscale.Get(f, "Received") ?? 0L);
                part.Done = true.Equals(Tailscale.Get(f, "Done"));
                var final = Tailscale.Get(f, "FinalPath") as string;
                if (!string.IsNullOrEmpty(final)) part.Final = final;
            }
            batch.Touched = DateTime.Now;
            if (remotes.Count > 0) Resolve(peer => ((Tailscale.Get(peer, "TailscaleIPs") as object[]) ?? new object[0]).Any(ip => remotes.Contains(ip as string)));
            tick.Start();
            Raise(State(batch));
        }

        // 每 2 秒一次：各设备累计发来多少字节。还没认出发送方时，看这批开始以来谁发来的最多（要明显比别人多）
        static void OnEngine(Dictionary<string, long> now)
        {
            if (batch != null)
            {
                string best = null;
                long most = 0, second = 0;
                foreach (var kv in now)
                {
                    if (!batch.RxStart.ContainsKey(kv.Key)) batch.RxStart[kv.Key] = kv.Value;   // 这批开始后才连上的：从现在算起
                    long d = kv.Value - batch.RxStart[kv.Key];
                    if (d > most) { second = most; most = d; best = kv.Key; }
                    else if (d > second) second = d;
                }
                if (best != null && most >= 32 * 1024 && most > 2 * second) Resolve(peer => best.Equals(Tailscale.Get(peer, "PublicKey")));
            }
            rx.Clear();
            foreach (var kv in now) rx[kv.Key] = kv.Value;
        }

        static void Resolve(Func<Dictionary<string, object>, bool> match)
        {
            var b = batch;
            if (b == null || b.From != null || b.Resolving) return;
            b.Resolving = true;
            Task.Run(() => Tailscale.FindPeer(match)).ContinueWith(t =>
            {
                b.Resolving = false;
                if (t.IsFaulted || t.Result == null || b.From != null) return;
                b.From = t.Result;
                if (b == batch) Raise(State(b));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 都收完了再等 1.5 秒（几个文件是一个接一个发的）；还没收完却 20 秒没动静，当成对方取消了
        static void Check()
        {
            if (batch == null) { tick.Stop(); return; }
            double quiet = (DateTime.Now - batch.Touched).TotalSeconds;
            if (batch.Parts.Values.All(p => p.Done) ? quiet < 1.5 : quiet < 20) return;
            var b = batch;
            batch = null;
            tick.Stop();
            var got = b.Parts.Values.Where(p => p.Done).ToList();
            var s = State(b);
            s.Done = true;
            s.Ok = got.Count > 0;
            Raise(s);
            if (got.Count == 0) return;
            // Windows 上收到的文件最后放在“下载”里
            string dl = Folders.Downloads();
            var paths = got.Select(p => p.Final != null && p.Final.StartsWith(dl, StringComparison.OrdinalIgnoreCase) ? p.Final : System.IO.Path.Combine(dl, p.Name)).ToList();
            History.RecordIn(b.From, paths, got.Sum(p => p.Got));
        }

        static Incoming State(Batch b)
        {
            long size = 0, got = 0;
            bool unknown = false;
            foreach (var p in b.Parts.Values)
            {
                if (p.Size < 0) unknown = true;
                else size += p.Size;
                got += p.Done && p.Size >= 0 ? p.Size : p.Got;
            }
            return new Incoming { Fraction = unknown || size <= 0 ? -1 : Math.Min(1, got / (double)size), From = b.From != null ? b.From.Ip : null };
        }

        static void Raise(Incoming s)
        {
            var h = Changed;
            if (h != null) h(s);
        }
    }

    // 掉线提醒：标记为重要的设备离线、恢复时弹托盘通知；每 30 秒看一次
    static class Alerts
    {
        class Seen
        {
            public string Key, Name;
            public bool Online;
            public DateTime LastSeen;
        }

        static Action<string, string> notify;   // 托盘通知（标题、正文）
        static readonly Dictionary<string, bool> online = new Dictionary<string, bool>();
        static readonly Dictionary<string, DateTime> since = new Dictionary<string, DateTime>();   // 什么时候离线的
        static DateTime last;
        static bool busy;

        public static void Start(Host h)
        {
            notify = h.Notify;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            t.Tick += delegate { Tick(); };
            t.Start();
            Tick();
        }

        // 刚标记上：记下现在的状态，从下一次变化开始报
        public static void Seed(Device d)
        {
            string key = Settings.Key(d);
            online[key] = d.Online;
            if (!d.Online) since[key] = DateTime.Now;
        }

        static void Tick()
        {
            if (busy || !Settings.Watching) return;
            busy = true;
            // 两次之间隔得太久，多半是电脑刚睡醒：这次只记状态，不报
            bool woke = last != DateTime.MinValue && (DateTime.Now - last).TotalSeconds > 90;
            last = DateTime.Now;
            Task.Run(() => Read()).ContinueWith(t =>
            {
                busy = false;
                if (!t.IsFaulted && t.Result != null) Compare(t.Result, woke);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 本机的 Tailscale 没在正常运行时不比（这时谁都像离线）
        static List<Seen> Read()
        {
            var r = Tailscale.Run("status --json");
            var root = r.Code == 0 ? Tailscale.Json(r.Out) : null;
            if (root == null || !"Running".Equals(Tailscale.Get(root, "BackendState"))) return null;
            var peers = Tailscale.Get(root, "Peer") as Dictionary<string, object>;
            var list = new List<Seen>();
            if (peers == null) return list;
            foreach (var p in peers.Values.OfType<Dictionary<string, object>>())
            {
                var s = new Seen { Name = Tailscale.ShortName(p), Online = true.Equals(Tailscale.Get(p, "Online")) };
                string id = (Tailscale.Get(p, "ID") as string) ?? "";
                s.Key = id != "" ? id : Tailscale.FirstIp(p);
                DateTime.TryParse(Tailscale.Get(p, "LastSeen") as string, out s.LastSeen);
                list.Add(s);
            }
            return list;
        }

        static void Compare(List<Seen> list, bool woke)
        {
            foreach (var s in list.Where(x => Settings.IsWatched(x.Key)))
            {
                bool was;
                bool known = online.TryGetValue(s.Key, out was);
                if (!s.Online && (!known || was)) since[s.Key] = s.LastSeen.Year > 2000 ? s.LastSeen : DateTime.Now;
                online[s.Key] = s.Online;
                if (!known || was == s.Online || woke) continue;
                if (!s.Online) notify(L.T("{0} 离线了", s.Name), L.T("恢复的时候再告诉你"));
                else
                {
                    DateTime t;
                    notify(L.T("{0} 恢复在线", s.Name), since.TryGetValue(s.Key, out t) ? L.T("离线了 {0}", Span(DateTime.Now - t)) : "");
                }
            }
        }

        static string Span(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return L.T("不到 1 分钟");
            if (t.TotalHours < 1) return L.T("{0} 分钟", (int)t.TotalMinutes);
            if (t.TotalDays < 2) return t.Minutes > 0 ? L.T("{0} 小时 {1} 分钟", (int)t.TotalHours, t.Minutes) : L.T("{0} 小时", (int)t.TotalHours);
            return L.T("{0} 天", (int)t.TotalDays);
        }
    }

    // 本程序自己的设置：%LOCALAPPDATA%\TailOrbit\settings.json。要掉线提醒的设备、界面语言、剪贴板互通、和 Mac 共用剪贴板
    static class Settings
    {
        static readonly string FilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailOrbit", "settings.json");
        static readonly HashSet<string> watch = new HashSet<string>();
        static string language = "auto", clipKey = "";
        static bool clipOn, syncMac;

        static Settings()
        {
            if (!File.Exists(FilePath)) return;
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath, Encoding.UTF8)) as Dictionary<string, object>;
                var list = Tailscale.Get(o, "watch") as object[];
                if (list != null) foreach (var s in list.OfType<string>()) watch.Add(s);
                language = (Tailscale.Get(o, "lang") as string) ?? "auto";
                clipOn = true.Equals(Tailscale.Get(o, "clip"));
                clipKey = (Tailscale.Get(o, "clipKey") as string) ?? "";
                syncMac = true.Equals(Tailscale.Get(o, "syncMac"));
            }
            catch (ArgumentException e) { Log.Write(e); }   // 文件坏了：当成没设置过，下次保存时覆盖
            catch (IOException e) { Log.Write(e); }
        }

        // 按 Tailscale 的设备 ID 记（换了 IP 也认得）；没有 ID 时用 IP
        public static string Key(Device d)
        {
            return d.StableId != "" ? d.StableId : d.Ip;
        }

        public static bool Watching { get { return watch.Count > 0; } }
        public static bool IsWatched(string key) { return watch.Contains(key); }
        public static bool Watched(Device d) { return watch.Contains(Key(d)); }

        public static void SetWatched(Device d, bool on)
        {
            if (on) watch.Add(Key(d));
            else watch.Remove(Key(d));
            Save();
        }

        // "auto" 跟着系统，或者 L.Codes 里的一个
        public static string Language
        {
            get { return language; }
            set { language = value; Save(); }
        }

        public static bool ClipOn
        {
            get { return clipOn; }
            set { clipOn = value; Save(); }
        }

        public static bool SyncMac
        {
            get { return syncMac; }
            set { syncMac = value; Save(); }
        }

        // 剪贴板网址里的随机码：第一次用时生成，关了再开不变（手机上的书签、快捷指令照样能用）
        public static string ClipKey
        {
            get
            {
                if (clipKey != "") return clipKey;
                const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
                var bytes = new byte[20];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                clipKey = new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
                Save();
                return clipKey;
            }
        }

        // 先写临时文件再换过去，写到一半断电也不会留下半个文件
        static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                string temp = FilePath + ".tmp";
                var o = new Dictionary<string, object> { { "watch", watch.ToList() }, { "lang", language }, { "clip", clipOn }, { "clipKey", clipKey }, { "syncMac", syncMac } };
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(o), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch (IOException e) { Log.Write(e); }
            catch (UnauthorizedAccessException e) { Log.Write(e); }
        }
    }

    static class Folders
    {
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        // “下载”文件夹（用户可能把它挪到了别的盘）
        public static string Downloads()
        {
            IntPtr p;
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) != 0)
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
    }
}
