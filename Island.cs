// 给“环礁”（屏幕顶上正中的通知岛）开的本机接口：设备列表、发文件，以及收发进度、剪贴板动静的事件流。说明在环礁那边的 docs/tailorbit-api.md。
// 只听 127.0.0.1:46214——46213 被 tailscale serve 转发到 tailnet，从那进来的请求也像本机发的，分不清。
// 每次启动换一把随机令牌写进 island.json，请求要带它；带 Origin 头的（网页发来的）、Host 不对的（DNS rebinding）一律不理
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;

namespace TailOrbit
{
    static class Island
    {
        public const int Port = 46214;
        const int PollMs = 10000;   // 岛连着时多久看一次设备有没有增减、上下线
        static readonly string ConfigPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailOrbit", "island.json");
        static readonly object gate = new object();
        static readonly List<Queue<string>> clients = new List<Queue<string>>();         // 每条事件流一个待发队列
        static readonly Dictionary<int, SendJob> jobs = new Dictionary<int, SendJob>();   // 还没结束的发送，取消时按编号找
        static readonly Dictionary<string, Pace> paces = new Dictionary<string, Pace>();
        static List<Device> known = new List<Device>();   // 最近一次的设备：剪贴板动静按 IP 找名字
        static string shape;                              // 设备签名，增减、上下线时会变
        static Host host;
        static string token;
        static Timer poll, flush;

        // 一次收发的进度节奏：状态变了马上发，进度最多每 200 毫秒一条，压下的最后一条过一会儿补上
        class Pace
        {
            public DateTime Last;
            public string State, Pending;
        }

        // 有岛连着事件流：收发文件、剪贴板经过就不再弹托盘通知
        public static bool Connected
        {
            get { lock (gate) return clients.Count > 0; }
        }

        public static void Start(Host h)
        {
            host = h;
            TcpListener l;
            try { l = FileServer.Listen(Port); }
            catch (SocketException e) { Log.Write(e); return; }   // 端口被占：岛连不上，别的照常
            var b = new byte[32];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            token = Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath));
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, Json(new Dictionary<string, object> { { "port", Port }, { "token", token } }), new UTF8Encoding(false));
                if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
                else File.Move(tmp, ConfigPath);
            }
            catch (IOException e) { Log.Write(e); l.Stop(); return; }
            catch (UnauthorizedAccessException e) { Log.Write(e); l.Stop(); return; }
            poll = new Timer(Poll);
            flush = new Timer(Flush);
            SendJob.Changed += OnJob;
            Inbox.Changed += OnInbox;
            new Thread(() => Accept(l)) { IsBackground = true, Name = "TailOrbit 环礁接口" }.Start();
        }

        static void Accept(TcpListener l)
        {
            while (true)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch (SocketException) { Thread.Sleep(200); continue; }
                new Thread(() => Serve(c)) { IsBackground = true }.Start();
            }
        }

        // 每个连接答一个请求；事件流那条一直开着。后台线程里漏出去的异常会让整个程序退出，所以全在这里接住
        static void Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = 15000;
                    var s = c.GetStream();
                    byte[] rest;
                    string head = FileServer.ReadHead(s, out rest);
                    if (head != null) Handle(s, head, rest);
                }
            }
            catch (IOException) { }   // 岛退出了、断开了
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Log.Write(e); }
        }

        static void Handle(Stream s, string head, byte[] early)
        {
            Dictionary<string, string> headers;
            var first = FileServer.Parse(head, out headers);
            if (first == null) { Reply(s, 400, null); return; }
            string at, auth;
            if (headers.ContainsKey("Origin") || !headers.TryGetValue("Host", out at) || at != "127.0.0.1:" + Port) { Reply(s, 403, null); return; }
            if (!headers.TryGetValue("Authorization", out auth) || !Same(auth, "Bearer " + token)) { Reply(s, 401, null); return; }
            string method = first[0], path = first[1];
            if (path == "/v1/devices" && method == "GET") Devices(s);
            else if (path == "/v1/events" && method == "GET") Events(s);
            else if (path == "/v1/send" && method == "POST") Send(s, headers, early);
            else if (path.Length > 16 && path.StartsWith("/v1/send/") && path.EndsWith("/cancel") && method == "POST") Cancel(s, path.Substring(9, path.Length - 16));
            else Reply(s, 404, null);
        }

        // 逐字比完再给结果：不让人按回应快慢一个字一个字猜令牌
        static bool Same(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ---------- 设备 ----------

        static void Devices(Stream s)
        {
            var snap = Load();
            var o = new Dictionary<string, object>
            {
                { "self", snap.Self == null ? null : new Dictionary<string, object> { { "name", snap.Self.Name }, { "os", snap.Self.Os } } },
                { "devices", snap.Devices.Select(d => new Dictionary<string, object> { { "id", Settings.Key(d) }, { "name", d.Name }, { "os", d.Os }, { "ip", d.Ip }, { "online", d.Online }, { "link", d.Link } }).ToList() }
            };
            if (snap.Error != null) o["error"] = snap.Error;   // Tailscale 没在跑、没登录：设备是空的，岛上可以说原因
            Reply(s, 200, o);
        }

        // 读一次设备，顺手记下来；和上次比有增减、上下线就告诉岛
        static Snapshot Load()
        {
            Snapshot snap;
            try { snap = Tailscale.Load(); }
            catch (Win32Exception) { snap = new Snapshot { Error = L.T("找不到 Tailscale") }; }
            string now = string.Join("\n", snap.Devices.Select(d => Settings.Key(d) + "|" + d.Name + "|" + d.Os + "|" + d.Online)) + "\n" + snap.Error;
            bool changed;
            lock (gate)
            {
                changed = shape != null && shape != now;
                shape = now;
                known = snap.Devices;
            }
            if (changed) Broadcast("{\"type\":\"devices\"}");
            return snap;
        }

        static void Poll(object state)
        {
            try { Load(); }
            catch (Exception e) { Log.Write(e); }   // 定时器线程上漏出异常会让整个程序退出
            lock (gate) if (clients.Count > 0) poll.Change(PollMs, Timeout.Infinite);
        }

        // ---------- 发文件 ----------

        // {"device":"<id>","paths":["C:\\a.pdf","D:\\照片"]}：和在星网上拖一次一样，照常记传输记录、画光点；进度走事件流
        static void Send(Stream s, Dictionary<string, string> headers, byte[] early)
        {
            var o = Body(s, headers, early);
            if (o == null) return;
            var id = Tailscale.Get(o, "device") as string;
            var list = Tailscale.Get(o, "paths") as object[];
            var paths = list == null ? null : list.OfType<string>().ToList();
            if (id == null || paths == null || paths.Count == 0 || paths.Count != list.Length) { Reply(s, 400, Error(L.T("请求不对。"))); return; }
            var missing = paths.FirstOrDefault(p => !Exists(p));
            if (missing != null) { Reply(s, 400, Error(L.T("找不到 {0}", NameOf(missing)))); return; }
            var snap = Load();
            var d = snap.Devices.FirstOrDefault(x => Settings.Key(x) == id);
            if (d == null) { Reply(s, 404, Error(snap.Error ?? L.T("找不到这台设备"))); return; }
            if (!d.Online) { Reply(s, 409, Error(L.T("{0} 不在线", d.Name))); return; }
            var j = new SendJob { Target = d, Paths = paths };
            lock (gate) jobs[j.Id] = j;
            Application.Current.Dispatcher.Invoke(() => host.SendFromIsland(j));
            Reply(s, 202, new Dictionary<string, object> { { "job", j.Id.ToString() } });
        }

        // 只收完整路径，而且文件或文件夹得在
        static bool Exists(string p)
        {
            try { return System.IO.Path.IsPathRooted(p) && (File.Exists(p) || Directory.Exists(p)); }
            catch (ArgumentException) { return false; }   // 路径里有不能用的字符
        }

        static string NameOf(string p)
        {
            try { return System.IO.Path.GetFileName(p.TrimEnd('\\')); }
            catch (ArgumentException) { return p; }
        }

        static void Cancel(Stream s, string id)
        {
            int n;
            SendJob j = null;
            lock (gate) if (int.TryParse(id, out n)) jobs.TryGetValue(n, out j);
            if (j == null) { Reply(s, 404, null); return; }
            j.Cancel();
            Reply(s, 204, null);
        }

        // 请求正文按 JSON 读；不对就直接回了，返回 null
        static Dictionary<string, object> Body(Stream s, Dictionary<string, string> headers, byte[] early)
        {
            string raw, expect;
            int length;
            if (!headers.TryGetValue("Content-Length", out raw) || !int.TryParse(raw, out length) || length < 0) { Reply(s, 411, null); return null; }
            if (length > 1 << 20) { Reply(s, 413, null); return null; }
            // .NET 发 POST 默认先问一句、等这行再发正文，不答它要白等 350 毫秒
            if (headers.TryGetValue("Expect", out expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase)) Write(s, "HTTP/1.1 100 Continue\r\n\r\n");
            var body = new byte[length];
            int got = Math.Min(early.Length, length);
            Buffer.BlockCopy(early, 0, body, 0, got);
            while (got < length)
            {
                int k = s.Read(body, got, length - got);
                if (k <= 0) return null;
                got += k;
            }
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(body)) as Dictionary<string, object>;
                if (o != null) return o;
            }
            catch (ArgumentException) { }
            Reply(s, 400, Error(L.T("请求不对。")));
            return null;
        }

        // ---------- 事件流 ----------

        // 一直开着：有事件就发 data 行，15 秒没事发一行 ping。写不出去说明岛断了
        static void Events(Stream s)
        {
            var q = new Queue<string>();
            lock (gate)
            {
                clients.Add(q);
                if (clients.Count == 1) poll.Change(PollMs, Timeout.Infinite);
            }
            try
            {
                Write(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
                while (true)
                {
                    string[] batch;
                    lock (gate)
                    {
                        if (q.Count == 0) Monitor.Wait(gate, 15000);
                        batch = q.ToArray();
                        q.Clear();
                    }
                    Write(s, batch.Length == 0 ? ": ping\n\n" : string.Concat(batch.Select(m => "data: " + m + "\n\n")));
                }
            }
            finally { lock (gate) clients.Remove(q); }
        }

        static void Broadcast(string json)
        {
            lock (gate)
            {
                foreach (var q in clients) q.Enqueue(json);
                Monitor.PulseAll(gate);
            }
        }

        static void Paced(string key, string state, bool end, string json)
        {
            lock (gate)
            {
                Pace p;
                if (!paces.TryGetValue(key, out p)) paces[key] = p = new Pace();
                var now = DateTime.UtcNow;
                if (state != p.State || end || (now - p.Last).TotalMilliseconds >= 200)
                {
                    p.State = state;
                    p.Last = now;
                    p.Pending = null;
                    Broadcast(json);
                    if (end) paces.Remove(key);
                    return;
                }
                if (p.Pending == null) flush.Change(200, Timeout.Infinite);
                p.Pending = json;
            }
        }

        static void Flush(object state)
        {
            lock (gate)
                foreach (var p in paces.Values.Where(x => x.Pending != null))
                {
                    Broadcast(p.Pending);
                    p.Pending = null;
                    p.Last = DateTime.UtcNow;
                }
        }

        // 所有发送都报（星网、快捷面板、岛上发起的），岛上能显示、能取消。在发送的后台线程上来
        static void OnJob(SendJob j)
        {
            bool end = j.State == "done" || j.State == "failed" || j.State == "canceled";
            lock (gate)
            {
                if (end) jobs.Remove(j.Id);
                else jobs[j.Id] = j;
            }
            var o = new Dictionary<string, object>
            {
                { "type", "send" }, { "job", j.Id.ToString() }, { "device", j.Target.Name }, { "os", j.Target.Os },
                { "files", j.Paths.Select(NameOf).ToList() }, { "bytes", j.TotalBytes }, { "progress", j.Fraction }, { "state", j.State }
            };
            if (j.State == "failed") o["error"] = L.T(j.Error);
            Paced("send:" + j.Id, j.State, end, Json(o));
        }

        // 收件：没认出发送方时 from 是 null；一个都没收到（对方取消、断了）算 canceled
        static void OnInbox(Incoming s)
        {
            string state = !s.Done ? "receiving" : s.Ok ? "done" : "canceled";
            var o = new Dictionary<string, object>
            {
                { "type", "receive" }, { "id", s.Id.ToString() }, { "from", s.Name }, { "os", s.Os ?? "" }, { "progress", s.Done && s.Ok ? 1.0 : s.Fraction }, { "state", state }
            };
            if (s.Files != null) o["files"] = s.Files;
            Paced("receive:" + s.Id, state, s.Done, Json(o));
        }

        // 剪贴板经过了这台设备：inward＝对方的剪贴板写进了这台电脑，否则是对方读走了这台电脑的。不带内容
        public static void Clip(string ip, bool inward)
        {
            Device d;
            lock (gate)
            {
                if (clients.Count == 0) return;
                d = known.FirstOrDefault(x => x.Ip == ip);
            }
            Broadcast(Json(new Dictionary<string, object> { { "type", "clip" }, { "device", d != null ? d.Name : ip }, { "os", d != null ? d.Os : "" }, { "direction", inward ? "in" : "out" } }));
        }

        // ---------- 回应 ----------

        static Dictionary<string, object> Error(string text)
        {
            return new Dictionary<string, object> { { "error", text } };
        }

        static void Reply(Stream s, int code, object o)
        {
            var b = o == null ? new byte[0] : Encoding.UTF8.GetBytes(Json(o));
            FileServer.Head(s, code, "application/json; charset=utf-8", b.Length, "", "no-store");
            s.Write(b, 0, b.Length);
        }

        static void Write(Stream s, string text)
        {
            var b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        static string Json(object o)
        {
            return new JavaScriptSerializer().Serialize(o);
        }
    }
}
