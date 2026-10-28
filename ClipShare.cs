// 剪贴板互通：自己的手机、Mac 读这台电脑的剪贴板，iPhone 把它的剪贴板发过来，这台电脑读 Mac 的剪贴板。
// 手机和 Mac 打开 https://<本机>:7443/c/<随机码>/ 这个网页（快捷指令取 …/raw），FileServer 收到后交给这里现读剪贴板答复；
// 配套的 iPhone App（ios/）一直连着 …/watch：本机一复制，文字或图片马上推过去、放进手机剪贴板；它发过来的也可以是图片。
// Tailscale 只在自己的设备之间转发（serve，不走 Funnel），这里再只认登录同一个账号的设备。每次现读、不存盘；
// 都是自己的设备，密码管理器复制的密码也照样给。
// 读 Mac：用 ~/.ssh/config 里它的别名登录过去跑 pbpaste
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;

namespace TailOrbit
{
    static class ClipShare
    {
        const int MaxBody = 1 << 20;   // 发来的最多 1MB
        static readonly object serving = new object();
        static Host host;

        static string Mount { get { return "/c/" + Settings.ClipKey; } }

        // 开着时手机要打开的网址；关着是 null
        public static string Url { get; private set; }

        // 读 Mac 的快捷键；没注册上是 null
        public static string Hotkey;

        // 启动时对齐：开着就确保转发挂着，关着就把以前挂的撤掉（serve --bg 挂的东西重启后还在）
        public static void Start(Host h)
        {
            host = h;
            Task.Run(() => Apply(Settings.ClipOn));
        }

        // 开、关；返回 null 表示成功，否则是开不成的原因。
        // 关总能关上：先让小服务立刻不给，转发撤不掉（比如 Tailscale 没在跑）也不要紧，下次启动再撤
        public static string Turn(bool on)
        {
            if (!on)
            {
                Settings.ClipOn = false;
                lock (gate)
                {
                    foreach (var w in watchers) w.Ended = true;
                    Monitor.PulseAll(gate);
                }
            }
            string why = Apply(on);
            if (on && why == null) Settings.ClipOn = true;
            return on ? why : null;
        }

        static string Apply(bool on)
        {
            string url;
            string why = Expose("/c/", Mount, on, out url);
            if (why == null) Url = url;
            return why;
        }

        // 在 tailscale serve 的 7443 上把 prefix 开头的转发对齐成只剩 mount 这一个（on 为 false 时全撤掉）。
        // 成功返回 null，url 是手机要打开的网址（撤掉时是 null）。在手机上收听本机声音（SoundShare.cs）也用它。
        // serve 的设置是读出来改了再写回去：两个开关同时拨（比如程序刚启动）时一个一个来，别互相盖掉
        public static string Expose(string prefix, string mount, bool on, out string url)
        {
            url = null;
            if (on && FileServer.Error != null) return FileServer.Error;
            lock (serving)
            {
                var r = Tailscale.Run("serve status --json");
                if (r.Code != 0) return Tailscale.FirstLine(r.Err, L.T("更改失败"));
                var mounted = new List<string>();
                var web = Tailscale.Get(Tailscale.Json(r.Out), "Web") as Dictionary<string, object>;
                if (web != null)
                    foreach (var hp in web.Where(x => x.Key.EndsWith(":" + Shares.PrivatePort)))
                    {
                        var handlers = Tailscale.Get(hp.Value as Dictionary<string, object>, "Handlers") as Dictionary<string, object>;
                        if (handlers != null) mounted.AddRange(handlers.Keys.Where(k => k.StartsWith(prefix)));
                    }
                foreach (var old in mounted.Where(k => !on || k != mount))
                    Tailscale.Run("serve --yes --https=" + Shares.PrivatePort + " --set-path=" + old + " off");
                if (!on) return null;
                if (!mounted.Contains(mount))
                {
                    var a = Tailscale.Run("serve --bg --yes --https=" + Shares.PrivatePort + " --set-path=" + mount + " http://127.0.0.1:" + FileServer.Port + mount);
                    if (a.Code != 0) return Tailscale.FirstLine(a.Err, L.T("更改失败"));
                }
            }
            var self = Tailscale.Get(Status(), "Self") as Dictionary<string, object>;
            url = "https://" + ((Tailscale.Get(self, "DNSName") as string) ?? "").TrimEnd('.') + ":" + Shares.PrivatePort + mount + "/";
            return null;
        }

        public static Dictionary<string, object> Status()
        {
            var r = Tailscale.Run("status --json");
            return r.Code == 0 ? Tailscale.Json(r.Out) : null;
        }

        // ---------- 网页和接口：GET / 网页，GET raw 纯文字（快捷指令用），POST raw 把发来的放进剪贴板，GET watch 推给 iPhone App ----------

        // rest 是请求头后面已经读进来的那部分正文
        public static void Serve(Stream s, string method, string target, Dictionary<string, string> headers, byte[] rest)
        {
            int q = target.IndexOf('?');
            var parts = (q >= 0 ? target.Substring(0, q) : target).Split('/');   // "", "c", 随机码[, 后面的]
            string tail = parts.Length > 3 ? string.Join("/", parts.Skip(3)) : null;
            bool head = method == "HEAD";
            if (!Settings.ClipOn || parts.Length < 3 || !Same(parts[2], Settings.ClipKey) || headers.ContainsKey("Tailscale-Funnel-Request") || (tail != null && tail != "" && tail != "raw" && tail != "watch"))
            {
                Fail(s, 404, "剪贴板互通未开启，或链接无效。", head);
                return;
            }
            if (tail == null) { FileServer.Redirect(s, Uri.EscapeDataString(parts[2]) + "/"); return; }
            // Tailscale 转发时带上访客的账号（客户端自己伪造的同名头会被它去掉）；带标签的设备、别人的账号都没有或对不上
            var status = Status();
            string login;
            if (!headers.TryGetValue("Tailscale-User-Login", out login) || !string.Equals(login, Owner(status), StringComparison.OrdinalIgnoreCase))
            {
                Fail(s, 403, "仅限已登录同一 Tailscale 账号的设备查看。", head);
                return;
            }
            string ip;
            string from = Visitor(status, headers, out ip);
            if (method == "POST" && tail == "raw") { Receive(s, headers, rest, from, ip); return; }
            if (tail == "watch")
            {
                if (method == "GET") Watch(s, ip);
                else FileServer.Head(s, 405, "text/plain; charset=utf-8", 0, "");
                return;
            }
            if (method != "GET" && !head) { FileServer.Head(s, 405, "text/plain; charset=utf-8", 0, ""); return; }
            string text = Application.Current.Dispatcher.Invoke<string>(() => { bool secret; return Peek(out secret); });
            text = text == null ? "" : text.Replace("\r\n", "\n");
            byte[] body = Encoding.UTF8.GetBytes(tail == "raw" ? text : Page(status, text));
            if (tail == "raw" && text == "") { FileServer.Head(s, 204, "text/plain; charset=utf-8", 0, "", "no-store"); return; }
            FileServer.Head(s, 200, (tail == "raw" ? "text/plain" : "text/html") + "; charset=utf-8", body.Length, "", "no-store");
            if (head) return;
            s.Write(body, 0, body.Length);
            Tell(L.T("{0} 读取了本机剪贴板", from), L.T("{0} 个字符", text.Length), ip, false);
        }

        // 手机发来的：文字，或 PNG 图片（Content-Type: image/png，iPhone App 发图片时用）
        static void Receive(Stream s, Dictionary<string, string> headers, byte[] rest, string from, string ip)
        {
            string raw, type;
            long length;
            bool png = headers.TryGetValue("Content-Type", out type) && type.StartsWith("image/png", StringComparison.OrdinalIgnoreCase);
            if (!headers.TryGetValue("Content-Length", out raw) || !long.TryParse(raw, out length) || length < 0) { FileServer.Head(s, 411, "text/plain; charset=utf-8", 0, ""); return; }
            if (length > (png ? ClipSync.MaxPng : MaxBody)) { FileServer.Head(s, 413, "text/plain; charset=utf-8", 0, ""); return; }
            var body = new byte[length];
            int got = Math.Min(rest.Length, body.Length);
            Buffer.BlockCopy(rest, 0, body, 0, got);
            while (got < body.Length)
            {
                int k = s.Read(body, got, body.Length - got);
                if (k <= 0) return;   // 发到一半断了
                got += k;
            }
            var c = png ? ClipItem.OfPng(body, false) : ClipItem.OfText(Encoding.UTF8.GetString(body), false);
            if (!png && c.Text == "") { FileServer.Head(s, 204, "text/plain; charset=utf-8", 0, ""); return; }
            if (png && !(body.Length > 8 && body[0] == 0x89 && body[1] == 'P' && body[2] == 'N' && body[3] == 'G')) { FileServer.Head(s, 415, "text/plain; charset=utf-8", 0, ""); return; }
            if (!Application.Current.Dispatcher.Invoke<bool>(() => Take(c))) { FileServer.Head(s, 503, "text/plain; charset=utf-8", 0, ""); return; }
            FileServer.Head(s, 200, "text/plain; charset=utf-8", 0, "");
            Tell(L.T("收到来自 {0} 的剪贴板内容", from), png ? L.T("图片，可直接粘贴") : L.T("{0} 个字符，可直接粘贴", c.Text.Length), ip, true);
        }

        // 界面线程上：放进本机剪贴板（照常同步给 Mac），记下它：它引起的剪贴板变化不再推回给手机
        static bool Take(ClipItem c)
        {
            if (c.Png == null ? !Ui.CopyText(Windows(c.Text)) : !ClipSync.Put(c, false)) return false;
            received = c.Key;
            return true;
        }

        // ---------- 推给 iPhone App：GET watch 一直连着，本机一复制就把文字或图片推过去 ----------
        // 一条：1 字节种类（0 心跳、1 文字、2 PNG）、1 字节标记（1 是密码、2 是刚连上时补的）、4 字节小端长度、内容。
        // 没东西推时 25 秒一个心跳；本机复制完 60 秒内连上的（手机刚换网、刚打开 App），先补上这一份。
        // 推的总是最新的一份：手机那边还没收走、本机又复制了，旧的就不推了

        class Watcher
        {
            public byte[] Next;   // 等着推的那一条
            public bool Ended;    // 关了剪贴板互通：马上断开
        }

        static readonly object gate = new object();
        static readonly List<Watcher> watchers = new List<Watcher>();
        static readonly byte[] Beat = new byte[6];
        static int beatMs = 25000;
        static DateTime copiedAt;   // 本机上次复制的时间。这几个只在界面线程上碰
        static string received;     // iPhone 刚发来、放进本机剪贴板的那份
        static string pushed;       // 上次推出去的那份和推的时间：同一次复制系统有时连发两条通知
        static DateTime pushedAt;

        // 本机剪贴板变了（Host 收到系统通知后在界面线程上调）：有 iPhone App 连着就推过去
        public static void Copied()
        {
            if (!Settings.ClipOn) return;
            copiedAt = DateTime.UtcNow;
            lock (gate)
                if (watchers.Count == 0) return;
            var c = Current();
            if (c == null || (c.Key == pushed && (DateTime.UtcNow - pushedAt).TotalSeconds < 2)) return;
            pushed = c.Key;
            pushedAt = DateTime.UtcNow;
            Offer(c);
        }

        static void Offer(ClipItem c)
        {
            var r = Record(c, false);
            lock (gate)
            {
                foreach (var w in watchers) w.Next = r;
                Monitor.PulseAll(gate);
            }
        }

        // 本机剪贴板里该推给手机的那份。从 Mac 同步来的不推（Mac 和 iPhone 之间系统自己会同步），手机刚发来的也不推回去
        static ClipItem Current()
        {
            try
            {
                if (Clipboard.ContainsData(ClipSync.Marker)) return null;
                var c = ClipSync.Read();
                return c == null || c.Key == received || (c.Png != null && c.Png.Length > ClipSync.MaxPng) ? null : c;
            }
            catch (COMException) { return null; }   // 别的程序正开着剪贴板
        }

        static void Watch(Stream s, string ip)
        {
            var me = new Watcher();
            var now = Application.Current.Dispatcher.Invoke<ClipItem>(() => (DateTime.UtcNow - copiedAt).TotalSeconds < 60 ? Current() : null);
            if (now != null) me.Next = Record(now, true);
            lock (gate) watchers.Add(me);
            try
            {
                s.WriteTimeout = 10000;   // 对方没了、网络卡死：写不出去 10 秒就断开（App 会重连）
                var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
                s.Write(h, 0, h.Length);
                s.Write(Beat, 0, Beat.Length);   // 先来一个心跳：iPhone 那边要收到正文才回报连上了
                while (true)
                {
                    byte[] r;
                    lock (gate)
                    {
                        if (me.Next == null && !me.Ended) Monitor.Wait(gate, beatMs);
                        if (me.Ended) return;
                        r = me.Next ?? Beat;
                        me.Next = null;
                    }
                    s.Write(r, 0, r.Length);
                    if (r != Beat) Application.Current.Dispatcher.BeginInvoke(new Action(() => host.ClipFlow(ip, false)));
                }
            }
            finally
            {
                lock (gate) watchers.Remove(me);
            }
        }

        static byte[] Record(ClipItem c, bool catchUp)
        {
            var body = c.Png ?? Encoding.UTF8.GetBytes(c.Text);
            var r = new byte[6 + body.Length];
            r[0] = (byte)(c.Png != null ? 2 : 1);
            r[1] = (byte)((c.Secret ? 1 : 0) | (catchUp ? 2 : 0));
            for (int i = 0; i < 4; i++) r[2 + i] = (byte)(body.Length >> (8 * i));
            Buffer.BlockCopy(body, 0, r, 6, body.Length);
            return r;
        }

        // 在界面线程上调：WPF 的剪贴板只能在单线程套间里用。别的程序正开着剪贴板时稍等再试；没有文字返回 null。
        // secret：密码管理器复制密码时附带了“别进剪贴板历史、别同步到云”的标记（照样给；传到别处时把这个意思带过去）
        public static string Peek(out bool secret)
        {
            secret = false;
            for (int i = 0; ; i++)
                try
                {
                    if (!Clipboard.ContainsText()) return null;
                    secret = Secret();
                    return Clipboard.GetText();
                }
                catch (COMException)
                {
                    if (i == 4) return null;
                    Thread.Sleep(30);
                }
        }

        // 剪贴板里有“别进剪贴板历史、别同步到云”的标记（密码管理器复制时会带；图片也看它）
        public static bool Secret()
        {
            return Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") || Off("CanUploadToCloudClipboard") || Off("CanIncludeInClipboardHistory");
        }

        // 值是 0 表示别同步到别处、别进剪贴板历史
        static bool Off(string format)
        {
            var m = Clipboard.GetData(format) as MemoryStream;
            return m != null && m.Length >= 4 && BitConverter.ToInt32(m.ToArray(), 0) == 0;
        }

        public static string Windows(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        // 随机码逐字比完，不在第一个不同的字上提前返回
        public static bool Same(string a, string b)
        {
            if (a.Length != b.Length || b.Length == 0) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string Owner(Dictionary<string, object> status)
        {
            var self = Tailscale.Get(status, "Self") as Dictionary<string, object>;
            var users = Tailscale.Get(status, "User") as Dictionary<string, object>;
            var me = Tailscale.Get(users, Convert.ToString(Tailscale.Get(self, "UserID"), System.Globalization.CultureInfo.InvariantCulture)) as Dictionary<string, object>;
            return (Tailscale.Get(me, "LoginName") as string) ?? "\0";   // 读不到时谁也对不上
        }

        // 哪台设备：Tailscale 转发时在 X-Forwarded-For 里写的是对方的 Tailscale 地址
        public static string Visitor(Dictionary<string, object> status, Dictionary<string, string> headers, out string ip)
        {
            headers.TryGetValue("X-Forwarded-For", out ip);
            ip = (ip ?? "").Split(',')[0].Trim();
            var peers = Tailscale.Get(status, "Peer") as Dictionary<string, object>;
            var nodes = peers == null ? new List<Dictionary<string, object>>() : peers.Values.OfType<Dictionary<string, object>>().ToList();
            nodes.Add(Tailscale.Get(status, "Self") as Dictionary<string, object>);
            string at = ip;
            var node = nodes.FirstOrDefault(n => n != null && ((Tailscale.Get(n, "TailscaleIPs") as object[]) ?? new object[0]).Contains(at));
            return node != null ? Tailscale.ShortName(node) : L.T("未知设备");
        }

        static void Tell(string title, string text, string ip, bool inward)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                host.Notify(title, text, true);
                host.ClipFlow(ip, inward);
            }));
        }

        public static void Fail(Stream s, int code, string why, bool head)
        {
            var body = Encoding.UTF8.GetBytes(FileServer.Page(FileServer.Say("无法打开"), FileServer.Say(why), ""));
            FileServer.Head(s, code, "text/html; charset=utf-8", body.Length, "");
            if (!head) s.Write(body, 0, body.Length);
        }

        // 给手机、Mac 看的网页：电脑剪贴板里的字，一个“复制”；再一个把自己的剪贴板发过来
        static string Page(Dictionary<string, object> status, string text)
        {
            string pc = Tailscale.ShortName((Tailscale.Get(status, "Self") as Dictionary<string, object>) ?? new Dictionary<string, object>());
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };   // 会把 < > & 转义，放进 <script> 里也安全
            Func<string, string> say = zh => js.Serialize(FileServer.Say(zh));
            string refresh = string.Concat(Icons.Data("refresh").Select(d => "<path d=\"" + d + "\"/>"));
            // 标题由 FileServer.Page 给；右上角一个刷新（加到主屏幕后打开的是全屏网页，没有浏览器的刷新按钮）。
            // 字占满中间，两个按钮压在底部，单手拇指够得着
            string body =
                "<style>main{position:relative;min-height:100vh;min-height:100dvh;box-sizing:border-box;display:flex;flex-direction:column;" +
                "padding-bottom:calc(20px + env(safe-area-inset-bottom))}h1{padding-right:52px}" +
                ".ico{position:absolute;top:26px;right:16px;width:40px;height:40px;border-radius:12px;border:0;background:var(--tile);color:var(--fg);display:grid;place-items:center;cursor:pointer}" +
                "pre{flex:1;min-height:120px;margin:0;padding:16px;border-radius:16px;background:var(--tile);white-space:pre-wrap;word-break:break-word;overflow:auto;font-family:inherit;font-size:16px;line-height:1.6}" +
                ".meta{color:var(--mute);font-size:13px;margin:10px 4px 16px;min-height:18px}" +
                ".btn{display:block;width:100%;height:54px;border:0;border-radius:16px;font-family:inherit;font-size:16px;font-weight:600;margin-top:10px;cursor:pointer}" +
                ".main{background:var(--fg);color:var(--bg)}.sub{background:var(--tile);color:var(--fg);font-weight:400}" +
                ".note{min-height:22px;margin-top:12px;text-align:center;color:var(--mute);font-size:14px}.empty{color:var(--mute)}</style>" +
                "<button class=\"ico\" onclick=\"location.reload()\" aria-label=\"" + System.Net.WebUtility.HtmlEncode(FileServer.Say("刷新")) + "\">" +
                "<svg viewBox=\"0 0 20 20\" width=\"20\" height=\"20\">" + refresh + "</svg></button>" +
                "<pre id=\"t\"></pre><div class=\"meta\" id=\"m\"></div>" +
                "<button class=\"btn main\" id=\"copy\"></button><button class=\"btn sub\" id=\"send\"></button><div class=\"note\" id=\"n\"></div>" +
                "<script>(function(){var T=" + js.Serialize(text) + ",$=function(i){return document.getElementById(i)},say=function(s){$('n').textContent=s};" +
                "if(T){$('t').textContent=T;$('m').textContent=" + js.Serialize(FileServer.Say("{0} 个字符", "#")) + ".replace('#',T.length)}" +
                "else{$('t').textContent=" + say("剪贴板中没有文本") + ";$('t').className='empty';$('copy').style.display='none'}" +
                "$('copy').textContent=" + say("复制") + ";$('send').textContent=" + say("发送我的剪贴板") + ";" +
                // 先用老办法（选中一个隐藏的文本框再“复制”）：要在点按的那一下里同步做，不少浏览器、内嵌网页不给新接口写剪贴板
                "var old=function(){var a=document.createElement('textarea');a.value=T;a.setAttribute('readonly','');a.style.cssText='position:fixed;top:0;opacity:0';" +
                "document.body.appendChild(a);a.select();a.setSelectionRange(0,T.length);var ok=false;try{ok=document.execCommand('copy')}catch(e){}document.body.removeChild(a);return ok};" +
                "$('copy').onclick=function(){var yes=function(){say(" + say("已复制") + ")},no=function(){say(" + say("复制失败，请长按上方文字手动复制。") + ")};" +
                "if(old())yes();else if(navigator.clipboard)navigator.clipboard.writeText(T).then(yes,no);else no()};" +
                "$('send').onclick=function(){navigator.clipboard.readText().then(function(x){if(!x){say(" + say("剪贴板中没有文本") + ");return}" +
                "return fetch('raw',{method:'POST',body:x}).then(function(r){say(r.ok?" + say("已发送") + ":" + say("发送失败") + ")})}).catch(function(){say(" + say("发送失败") + ")})};" +
                "})()</script>";
            return FileServer.Page(FileServer.Say("{0} 的剪贴板", pc), null, body);
        }

        // ---------- 这台电脑读 Mac 的剪贴板 ----------

        public static bool Readable(Device d)
        {
            return d.Online && d.Os == "macOS" && Ssh.Target(d) != null;
        }

        // 快捷键：读第一台能读的 Mac
        public static async void PullAny()
        {
            var snap = await Task.Run(() => Tailscale.Load());
            var mac = snap.Devices.FirstOrDefault(Readable);
            if (mac == null) host.Notify(L.T("未找到可读取剪贴板的 Mac"), L.T("Mac 需要在线、已开启“远程登录”，并已添加到 SSH 配置中。"));
            else Pull(mac);
        }

        // 读到就放进本机剪贴板，星网上从 Mac 往本机流一下
        public static async void Pull(Device mac)
        {
            string target = Ssh.Target(mac), text = null;
            string why = await Task.Run(() => Pbpaste(target, out text));
            if (why == null && text == "") why = L.T("{0} 的剪贴板中没有文本", mac.Name);
            if (why == null && !Ui.CopyText(Windows(text))) why = L.T("剪贴板正被其他程序占用，请重试。");
            if (why != null) { host.Notify(L.T("无法读取 {0} 的剪贴板", mac.Name), why); return; }
            host.Notify(L.T("已读取 {0} 的剪贴板", mac.Name), L.T("{0} 个字符，可直接粘贴", text.Length), true);
            host.ClipFlow(mac.Ip, true);
        }

        // pbpaste 按 LANG 决定输出的编码，ssh 进去时没设就不是 UTF-8，中文会乱，所以显式给它
        static string Pbpaste(string target, out string text)
        {
            text = null;
            var psi = new ProcessStartInfo("ssh", "-o BatchMode=yes -o ConnectTimeout=6 " + target + " \"LANG=en_US.UTF-8 pbpaste\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8
            };
            Process p;
            try { p = Process.Start(psi); }
            catch (Win32Exception) { return L.T("本机未安装 SSH"); }
            using (p)
            {
                var output = p.StandardOutput.ReadToEndAsync();
                var err = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                    return L.T("对方无响应");
                }
                if (p.ExitCode != 0) return Tailscale.FirstLine(err.Result, L.T("SSH 连接失败"));
                text = output.Result;
                return null;
            }
        }
    }
}
