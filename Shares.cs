// 分享：tailscale serve / funnel 上挂着的东西，和 TailOrbit 自己建的文件分享。
// Windows 上让 Tailscale 直接分享文件要管理员权限，所以文件由 TailOrbit 自己的小服务（FileServer）提供，
// Tailscale 只负责把 /s/<随机码> 转发过去：只给自己设备看的挂在 7443 端口，公开到互联网的挂在 10000 端口
//（Funnel 只许用 443、8443、10000，用 10000 不会把 443、8443 上的服务一起公开）。
// 随机码对应哪个文件、公开到几点，记在 %LOCALAPPDATA%\TailOrbit\shares.json；公开的到点由 Sweep 关掉
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace TailOrbit
{
    // TailOrbit 建的一条文件分享；改动时整条换掉，别的线程拿到的不会是改到一半的
    class Share
    {
        public string Slug = "", Source = "";
        public DateTime? Since, Expires;   // 公开时：几点公开的、几点自动关（UTC）
        public bool Public { get { return Expires != null; } }
    }

    // serve 上挂着的一条
    class ServeEntry
    {
        public string Host = "", Path = "/", Kind = "", Target = "";   // Kind：proxy 转发到服务 / path 文件 / text 文字 / tcp 端口转发
        public int Port;
        public bool Https = true, Public;
        public Share Mine;            // TailOrbit 建的文件分享；null 是用户自己用命令挂的
        public string Dead = "";      // 打不开的原因；空表示正常
        public string Program = "";   // 转发到的本机服务是哪个程序在听

        // 界面认一条分享用；TailOrbit 建的公开、收回时端口会变，按随机码认
        public string Key { get { return Mine != null ? "s:" + Mine.Slug : Port + Path; } }

        public string Url
        {
            get
            {
                if (Kind == "tcp") return Host + ":" + Port;
                bool std = Port == (Https ? 443 : 80);
                string path = Path == "/" ? "" : string.Join("/", Path.Split('/').Select(s => Uri.EscapeDataString(s)));
                return (Https ? "https://" : "http://") + Host + (std ? "" : ":" + Port) + path;
            }
        }

        // 卫星旁边的短名字
        public string Label
        {
            get
            {
                if (Mine != null) return Mine.Source != "" ? Shares.NameOf(Mine.Source) : "/s/" + Mine.Slug;
                if (Kind == "path") return Shares.NameOf(Target);
                return ":" + Port + (Path.Length > 1 ? Path : "");
            }
        }

        // 详情里的大标题
        public string Title
        {
            get
            {
                if (Mine != null || Kind == "path") return Label;
                if (Kind == "text") return L.T("文本");
                if (Kind == "tcp") return L.T("端口转发 :{0}", Port);
                if (Program != "") return Program;
                return LocalPort > 0 ? L.T("本机端口 {0}", LocalPort) : Target;
            }
        }

        // 转发到本机哪个端口：http://localhost:8788 → 8788；转发到别的机器返回 0
        public int LocalPort
        {
            get
            {
                if (Kind != "proxy" && Kind != "tcp") return 0;
                string t = Target;
                int scheme = t.IndexOf("://", StringComparison.Ordinal);
                if (scheme >= 0) t = t.Substring(scheme + 3);
                t = t.Split('/')[0];
                int port, colon = t.LastIndexOf(':');
                if (colon < 0) return int.TryParse(t, out port) ? port : 0;   // 只写了端口号
                string host = t.Substring(0, colon);
                if (host != "localhost" && host != "127.0.0.1" && host != "[::1]") return 0;
                return int.TryParse(t.Substring(colon + 1), out port) ? port : 0;
            }
        }
    }

    static class Shares
    {
        public const int PrivatePort = 7443, PublicPort = 10000;
        static readonly string StorePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailOrbit", "shares.json");
        static readonly object gate = new object();
        static readonly Dictionary<string, Share> store = ReadStore();

        static string Origin { get { return "http://127.0.0.1:" + FileServer.Port; } }

        public static Share Find(string slug)
        {
            lock (gate)
            {
                Share s;
                return store.TryGetValue(slug, out s) ? s : null;
            }
        }

        public static string NameOf(string path)
        {
            string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            return name != "" ? name : path;
        }

        // 读出现在挂着的所有分享；null 表示读不到 Tailscale
        public static List<ServeEntry> Load()
        {
            var r = Tailscale.Run("serve status --json");
            if (r.Code != 0) return null;
            var list = new List<ServeEntry>();
            var root = Tailscale.Json(r.Out);   // 什么都没挂时输出的不是 JSON
            var funnel = Tailscale.Get(root, "AllowFunnel") as Dictionary<string, object>;
            var tcp = Tailscale.Get(root, "TCP") as Dictionary<string, object>;
            var web = Tailscale.Get(root, "Web") as Dictionary<string, object>;
            if (web != null)
                foreach (var hp in web)
                {
                    int port, colon = hp.Key.LastIndexOf(':');
                    if (colon < 0 || !int.TryParse(hp.Key.Substring(colon + 1), out port)) continue;
                    var handlers = Tailscale.Get(hp.Value as Dictionary<string, object>, "Handlers") as Dictionary<string, object>;
                    if (handlers == null) continue;
                    var portCfg = Tailscale.Get(tcp, port.ToString(CultureInfo.InvariantCulture)) as Dictionary<string, object>;
                    foreach (var h in handlers)
                    {
                        var cfg = h.Value as Dictionary<string, object>;
                        var e = new ServeEntry { Host = hp.Key.Substring(0, colon), Port = port, Path = h.Key, Https = !true.Equals(Tailscale.Get(portCfg, "HTTP")), Public = funnel != null && true.Equals(Tailscale.Get(funnel, hp.Key)) };
                        string proxy = Tailscale.Get(cfg, "Proxy") as string, path = Tailscale.Get(cfg, "Path") as string;
                        if (port == PrivatePort && e.Path.StartsWith("/c/") && proxy == Origin + e.Path) continue;   // 剪贴板互通的转发：在本机页开关，不算分享
                        if (proxy != null) { e.Kind = "proxy"; e.Target = proxy; }
                        else if (path != null) { e.Kind = "path"; e.Target = path; }
                        else { e.Kind = "text"; e.Target = (Tailscale.Get(cfg, "Text") as string) ?? ""; }
                        if (proxy != null && (port == PrivatePort || port == PublicPort) && e.Path.StartsWith("/s/") && proxy == Origin + e.Path.Substring(2))
                            e.Mine = Find(e.Path.Substring(3)) ?? new Share { Slug = e.Path.Substring(3) };
                        list.Add(e);
                    }
                }
            if (tcp != null)
                foreach (var p in tcp)
                {
                    var fwd = Tailscale.Get(p.Value as Dictionary<string, object>, "TCPForward") as string;
                    int port;
                    if (fwd != null && int.TryParse(p.Key, out port)) list.Add(new ServeEntry { Kind = "tcp", Port = port, Path = "", Target = fwd, Host = list.Select(x => x.Host).FirstOrDefault() ?? "" });
                }
            Check(list);
            return list;
        }

        // 打不打得开：文件还在不在、转发到的本机端口有没有程序在听（顺便记下是哪个程序）
        static void Check(List<ServeEntry> list)
        {
            Dictionary<int, int> listeners = null;
            foreach (var e in list)
            {
                if (e.Mine != null)
                {
                    if (e.Mine.Source == "") e.Dead = L.T("未找到此分享对应的文件");
                    else if (!File.Exists(e.Mine.Source) && !Directory.Exists(e.Mine.Source)) e.Dead = L.T("原文件已不存在");
                    else if (FileServer.Error != null) e.Dead = FileServer.Error;
                    continue;
                }
                if (e.Kind == "path")
                {
                    if (!File.Exists(e.Target) && !Directory.Exists(e.Target)) e.Dead = L.T("文件已不存在");
                    continue;
                }
                int port = e.LocalPort;
                if (port == 0) continue;
                if (listeners == null) listeners = Ports.Listeners();
                int pid;
                if (!listeners.TryGetValue(port, out pid)) { e.Dead = L.T("没有程序在监听此端口"); continue; }
                e.Program = ProcessName(pid);
            }
        }

        static string ProcessName(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch (ArgumentException) { return ""; }           // 刚好退出了
            catch (InvalidOperationException) { return ""; }
            catch (Win32Exception) { return ""; }              // 系统进程，没权限看名字
        }

        // ---------- 动作：返回 null 表示成功，否则是原因 ----------

        // 新建：先只给自己的设备看。key 是新分享的 ServeEntry.Key，界面拿它选中这颗
        public static string Add(string source, out string key)
        {
            key = null;
            if (FileServer.Error != null) return FileServer.Error;
            string slug = NewSlug();
            Save(new Share { Slug = slug, Source = source });   // 先登记，转发一接上就能打开
            var r = Tailscale.Run("serve --bg --yes --https=" + PrivatePort + " --set-path=/s/" + slug + " " + Origin + "/" + slug);
            if (r.Code != 0)
            {
                Remove(slug);
                return Tailscale.FirstLine(r.Err, L.T("分享失败"));
            }
            key = "s:" + slug;
            return null;
        }

        // 公开：挂到 10000 端口打开 Funnel，再从 7443 撤下；已经公开的只改几点关
        public static string Publish(ServeEntry e, TimeSpan ttl)
        {
            var before = e.Mine;
            var now = DateTime.UtcNow;
            Save(new Share { Slug = before.Slug, Source = before.Source, Since = now, Expires = now + ttl });
            if (e.Public) return null;
            var r = Tailscale.Run("funnel --bg --yes --https=" + PublicPort + " --set-path=" + e.Path + " " + e.Target);
            if (r.Code != 0)
            {
                Save(before);
                return Tailscale.FirstLine(r.Err, L.T("公开失败"));
            }
            Tailscale.Run("serve --yes --https=" + PrivatePort + " --set-path=" + e.Path + " off");
            return null;
        }

        // 收回到只给自己的设备看：先挂回 7443，再关掉 10000 上的
        public static string Unpublish(ServeEntry e)
        {
            var r = Tailscale.Run("serve --bg --yes --https=" + PrivatePort + " --set-path=" + e.Path + " " + e.Target);
            if (r.Code != 0) return Tailscale.FirstLine(r.Err, L.T("取消公开失败"));
            r = Tailscale.Run("funnel --yes --https=" + PublicPort + " --set-path=" + e.Path + " off");
            if (r.Code != 0) return L.T("无法关闭公开访问：{0}", Tailscale.FirstLine(r.Err, L.T("funnel off 执行失败")));
            Save(new Share { Slug = e.Mine.Slug, Source = e.Mine.Source });
            return null;
        }

        public static string Stop(ServeEntry e)
        {
            string cmd = e.Kind == "tcp"
                ? "serve --yes --tcp=" + e.Port + " off"
                : (e.Public ? "funnel" : "serve") + " --yes " + (e.Https ? "--https=" : "--http=") + e.Port + " " + Arg("--set-path=" + e.Path) + " off";
            var r = Tailscale.Run(cmd);
            if (r.Code != 0) return Tailscale.FirstLine(r.Err, L.T("停止失败"));
            if (e.Mine != null) Remove(e.Mine.Slug);
            return null;
        }

        // 到点的公开分享关掉，返回关掉的名字；托盘程序每半分钟调一次
        public static List<string> Sweep()
        {
            var closed = new List<string>();
            List<Share> due;
            lock (gate) due = store.Values.Where(s => s.Expires != null && s.Expires.Value <= DateTime.UtcNow).ToList();
            if (due.Count == 0) return closed;
            var live = Load();
            if (live == null) return closed;   // 读不到 Tailscale：下一轮再试
            foreach (var s in due)
            {
                var e = live.FirstOrDefault(x => x.Mine != null && x.Mine.Slug == s.Slug);
                if (e != null && Stop(e) != null) continue;   // 没关掉：下一轮再试
                Remove(s.Slug);
                closed.Add(NameOf(s.Source));
            }
            return closed;
        }

        // ---------- 记录 ----------

        static void Save(Share s)
        {
            lock (gate)
            {
                store[s.Slug] = s;
                WriteStore();
            }
        }

        static void Remove(string slug)
        {
            lock (gate) if (store.Remove(slug)) WriteStore();
        }

        static Dictionary<string, Share> ReadStore()
        {
            var map = new Dictionary<string, Share>();
            Dictionary<string, object> raw = null;
            try
            {
                if (File.Exists(StorePath)) raw = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(StorePath, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch (IOException e) { Log.Write(e); }
            catch (ArgumentException e)
            {
                // 写坏了：原样留一份再当成空的，分享本身还挂在 serve 上
                Log.Write(e);
                try { File.Copy(StorePath, StorePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); }
                catch (IOException) { }
            }
            if (raw == null) return map;
            foreach (var kv in raw)
            {
                var o = kv.Value as Dictionary<string, object>;
                if (o != null) map[kv.Key] = new Share { Slug = kv.Key, Source = (Tailscale.Get(o, "source") as string) ?? "", Since = Time(Tailscale.Get(o, "since")), Expires = Time(Tailscale.Get(o, "expires")) };
            }
            return map;
        }

        // 先写到临时文件再换上，写到一半断电也不会把记录写坏
        static void WriteStore()
        {
            var raw = new Dictionary<string, object>();
            foreach (var s in store.Values)
                raw[s.Slug] = new Dictionary<string, object> { { "source", s.Source }, { "since", Stamp(s.Since) }, { "expires", Stamp(s.Expires) } };
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath));
                string tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(raw), new UTF8Encoding(false));
                if (File.Exists(StorePath)) File.Replace(tmp, StorePath, null);
                else File.Move(tmp, StorePath);
            }
            catch (IOException e) { Log.Write(e); }
        }

        static DateTime? Time(object v)
        {
            DateTime t;
            return v is string && DateTime.TryParse((string)v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t.ToUniversalTime() : (DateTime?)null;
        }

        static string Stamp(DateTime? t)
        {
            return t == null ? null : t.Value.ToString("o", CultureInfo.InvariantCulture);
        }

        static string NewSlug()
        {
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            var bytes = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
        }

        // 带空格的参数整个加引号（用户自己挂的分享路径里可能有空格）
        static string Arg(string s)
        {
            return s.Contains(" ") ? "\"" + s + "\"" : s;
        }
    }

    // 本机的 TCP 连接表（GetExtendedTcpTable）：哪些端口有程序在听，谁连了进来
    static class Ports
    {
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        // 在听的端口 → 进程号
        public static Dictionary<int, int> Listeners()
        {
            var map = new Dictionary<int, int>();
            Scan(2, 3, 24, row => { int p = Port(row, 8); if (!map.ContainsKey(p)) map[p] = Marshal.ReadInt32(row, 20); });     // IPv4：MIB_TCPROW_OWNER_PID
            Scan(23, 3, 56, row => { int p = Port(row, 20); if (!map.ContainsKey(p)) map[p] = Marshal.ReadInt32(row, 52); });   // IPv6：MIB_TCP6ROW_OWNER_PID
            return map;
        }

        // 连到本机这几个端口上的对方地址（收文件时认发送方用）
        public static List<string> Remotes(int[] local)
        {
            var list = new List<string>();
            if (local.Length == 0) return list;
            Scan(2, 5, 24, row => { if (local.Contains(Port(row, 8))) list.Add(new IPAddress((long)(uint)Marshal.ReadInt32(row, 12)).ToString()); });
            Scan(23, 5, 56, row =>
            {
                if (!local.Contains(Port(row, 20))) return;
                var b = new byte[16];
                Marshal.Copy(IntPtr.Add(row, 24), b, 0, 16);
                list.Add(new IPAddress(b).ToString());
            });
            return list.Where(a => a != "0.0.0.0" && a != "::").Distinct().ToList();   // 去掉“在听”那一行
        }

        static int Port(IntPtr row, int at)
        {
            int raw = Marshal.ReadInt32(row, at);
            return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);   // 网络字节序
        }

        // tableClass：3 只要在听的，5 全部
        static void Scan(int family, int tableClass, int rowSize, Action<IntPtr> each)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, tableClass, 0);
            if (size <= 0) return;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, family, tableClass, 0) != 0) return;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++) each(IntPtr.Add(buf, 4 + i * rowSize));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
