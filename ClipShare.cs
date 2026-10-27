// 剪贴板互通：自己的手机、Mac 读这台电脑的剪贴板，iPhone 把它的剪贴板发过来，这台电脑读 Mac 的剪贴板。
// 手机和 Mac 打开 https://<本机>:7443/c/<随机码>/ 这个网页（快捷指令取 …/raw），FileServer 收到后交给这里现读剪贴板答复；
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
            if (!on) Settings.ClipOn = false;
            string why = Apply(on);
            if (on && why == null) Settings.ClipOn = true;
            return on ? why : null;
        }

        static string Apply(bool on)
        {
            if (on && FileServer.Error != null) return FileServer.Error;
            var r = Tailscale.Run("serve status --json");
            if (r.Code != 0) return Tailscale.FirstLine(r.Err, L.T("没改成"));
            var mounted = new List<string>();
            var web = Tailscale.Get(Tailscale.Json(r.Out), "Web") as Dictionary<string, object>;
            if (web != null)
                foreach (var hp in web.Where(x => x.Key.EndsWith(":" + Shares.PrivatePort)))
                {
                    var handlers = Tailscale.Get(hp.Value as Dictionary<string, object>, "Handlers") as Dictionary<string, object>;
                    if (handlers != null) mounted.AddRange(handlers.Keys.Where(k => k.StartsWith("/c/")));
                }
            foreach (var old in mounted.Where(k => !on || k != Mount))
                Tailscale.Run("serve --yes --https=" + Shares.PrivatePort + " --set-path=" + old + " off");
            if (!on) { Url = null; return null; }
            if (!mounted.Contains(Mount))
            {
                var a = Tailscale.Run("serve --bg --yes --https=" + Shares.PrivatePort + " --set-path=" + Mount + " http://127.0.0.1:" + FileServer.Port + Mount);
                if (a.Code != 0) return Tailscale.FirstLine(a.Err, L.T("没改成"));
            }
            var self = Tailscale.Get(Status(), "Self") as Dictionary<string, object>;
            Url = "https://" + ((Tailscale.Get(self, "DNSName") as string) ?? "").TrimEnd('.') + ":" + Shares.PrivatePort + Mount + "/";
            return null;
        }

        static Dictionary<string, object> Status()
        {
            var r = Tailscale.Run("status --json");
            return r.Code == 0 ? Tailscale.Json(r.Out) : null;
        }

        // ---------- 网页和接口：GET / 网页，GET raw 纯文字（快捷指令用），POST raw 把发来的放进剪贴板 ----------

        // rest 是请求头后面已经读进来的那部分正文
        public static void Serve(Stream s, string method, string target, Dictionary<string, string> headers, byte[] rest)
        {
            int q = target.IndexOf('?');
            var parts = (q >= 0 ? target.Substring(0, q) : target).Split('/');   // "", "c", 随机码[, 后面的]
            string tail = parts.Length > 3 ? string.Join("/", parts.Skip(3)) : null;
            bool head = method == "HEAD";
            if (!Settings.ClipOn || parts.Length < 3 || !Same(parts[2], Settings.ClipKey) || headers.ContainsKey("Tailscale-Funnel-Request") || (tail != null && tail != "" && tail != "raw"))
            {
                Fail(s, 404, "剪贴板互通没开，或者链接不对。", head);
                return;
            }
            if (tail == null) { FileServer.Redirect(s, Uri.EscapeDataString(parts[2]) + "/"); return; }
            // Tailscale 转发时带上访客的账号（客户端自己伪造的同名头会被它去掉）；带标签的设备、别人的账号都没有或对不上
            var status = Status();
            string login;
            if (!headers.TryGetValue("Tailscale-User-Login", out login) || !string.Equals(login, Owner(status), StringComparison.OrdinalIgnoreCase))
            {
                Fail(s, 403, "只有登录了同一个 Tailscale 账号的设备能看。", head);
                return;
            }
            string ip;
            string from = Visitor(status, headers, out ip);
            if (method == "POST" && tail == "raw") { Receive(s, headers, rest, from, ip); return; }
            if (method != "GET" && !head) { FileServer.Head(s, 405, "text/plain; charset=utf-8", 0, ""); return; }
            string text = Application.Current.Dispatcher.Invoke<string>(() => { bool secret; return Peek(out secret); });
            text = text == null ? "" : text.Replace("\r\n", "\n");
            byte[] body = Encoding.UTF8.GetBytes(tail == "raw" ? text : Page(status, text));
            if (tail == "raw" && text == "") { FileServer.Head(s, 204, "text/plain; charset=utf-8", 0, "", "no-store"); return; }
            FileServer.Head(s, 200, (tail == "raw" ? "text/plain" : "text/html") + "; charset=utf-8", body.Length, "", "no-store");
            if (head) return;
            s.Write(body, 0, body.Length);
            Tell(L.T("{0} 读了这台电脑的剪贴板", from), L.T("{0} 个字", text.Length), ip, false);
        }

        static void Receive(Stream s, Dictionary<string, string> headers, byte[] rest, string from, string ip)
        {
            string raw;
            long length;
            if (!headers.TryGetValue("Content-Length", out raw) || !long.TryParse(raw, out length) || length < 0) { FileServer.Head(s, 411, "text/plain; charset=utf-8", 0, ""); return; }
            if (length > MaxBody) { FileServer.Head(s, 413, "text/plain; charset=utf-8", 0, ""); return; }
            var body = new byte[length];
            int got = Math.Min(rest.Length, body.Length);
            Buffer.BlockCopy(rest, 0, body, 0, got);
            while (got < body.Length)
            {
                int k = s.Read(body, got, body.Length - got);
                if (k <= 0) return;   // 发到一半断了
                got += k;
            }
            string text = Encoding.UTF8.GetString(body);
            if (text == "") { FileServer.Head(s, 204, "text/plain; charset=utf-8", 0, ""); return; }
            if (!Application.Current.Dispatcher.Invoke<bool>(() => Ui.CopyText(Windows(text)))) { FileServer.Head(s, 503, "text/plain; charset=utf-8", 0, ""); return; }
            FileServer.Head(s, 200, "text/plain; charset=utf-8", 0, "");
            Tell(L.T("{0} 发来了剪贴板", from), L.T("{0} 个字，可以直接粘贴", text.Length), ip, true);
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
                    secret = Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") || Off("CanUploadToCloudClipboard") || Off("CanIncludeInClipboardHistory");
                    return Clipboard.GetText();
                }
                catch (COMException)
                {
                    if (i == 4) return null;
                    Thread.Sleep(30);
                }
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
        static bool Same(string a, string b)
        {
            if (a.Length != b.Length || b.Length == 0) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        static string Owner(Dictionary<string, object> status)
        {
            var self = Tailscale.Get(status, "Self") as Dictionary<string, object>;
            var users = Tailscale.Get(status, "User") as Dictionary<string, object>;
            var me = Tailscale.Get(users, Convert.ToString(Tailscale.Get(self, "UserID"), System.Globalization.CultureInfo.InvariantCulture)) as Dictionary<string, object>;
            return (Tailscale.Get(me, "LoginName") as string) ?? "\0";   // 读不到时谁也对不上
        }

        // 哪台设备：Tailscale 转发时在 X-Forwarded-For 里写的是对方的 Tailscale 地址
        static string Visitor(Dictionary<string, object> status, Dictionary<string, string> headers, out string ip)
        {
            headers.TryGetValue("X-Forwarded-For", out ip);
            ip = (ip ?? "").Split(',')[0].Trim();
            var peers = Tailscale.Get(status, "Peer") as Dictionary<string, object>;
            var nodes = peers == null ? new List<Dictionary<string, object>>() : peers.Values.OfType<Dictionary<string, object>>().ToList();
            nodes.Add(Tailscale.Get(status, "Self") as Dictionary<string, object>);
            string at = ip;
            var node = nodes.FirstOrDefault(n => n != null && ((Tailscale.Get(n, "TailscaleIPs") as object[]) ?? new object[0]).Contains(at));
            return node != null ? Tailscale.ShortName(node) : L.T("一台设备");
        }

        static void Tell(string title, string text, string ip, bool inward)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                host.Notify(title, text);
                host.ClipFlow(ip, inward);
            }));
        }

        static void Fail(Stream s, int code, string why, bool head)
        {
            var body = Encoding.UTF8.GetBytes(FileServer.Page(FileServer.Say("打不开"), FileServer.Say(why), ""));
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
                "if(T){$('t').textContent=T;$('m').textContent=" + js.Serialize(FileServer.Say("{0} 个字", "#")) + ".replace('#',T.length)}" +
                "else{$('t').textContent=" + say("剪贴板里没有文字") + ";$('t').className='empty';$('copy').style.display='none'}" +
                "$('copy').textContent=" + say("复制") + ";$('send').textContent=" + say("把我的剪贴板发过去") + ";" +
                // 先用老办法（选中一个隐藏的文本框再“复制”）：要在点按的那一下里同步做，不少浏览器、内嵌网页不给新接口写剪贴板
                "var old=function(){var a=document.createElement('textarea');a.value=T;a.setAttribute('readonly','');a.style.cssText='position:fixed;top:0;opacity:0';" +
                "document.body.appendChild(a);a.select();a.setSelectionRange(0,T.length);var ok=false;try{ok=document.execCommand('copy')}catch(e){}document.body.removeChild(a);return ok};" +
                "$('copy').onclick=function(){var yes=function(){say(" + say("已复制") + ")},no=function(){say(" + say("没复制成，长按上面的字自己复制") + ")};" +
                "if(old())yes();else if(navigator.clipboard)navigator.clipboard.writeText(T).then(yes,no);else no()};" +
                "$('send').onclick=function(){navigator.clipboard.readText().then(function(x){if(!x){say(" + say("剪贴板里没有文字") + ");return}" +
                "return fetch('raw',{method:'POST',body:x}).then(function(r){say(r.ok?" + say("已发过去") + ":" + say("没发成") + ")})}).catch(function(){say(" + say("没发成") + ")})};" +
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
            if (mac == null) host.Notify(L.T("没有能读剪贴板的 Mac"), L.T("要 Mac 在线、开着远程登录，并且 ssh 配置里有它。"));
            else Pull(mac);
        }

        // 读到就放进本机剪贴板，星网上从 Mac 往本机流一下
        public static async void Pull(Device mac)
        {
            string target = Ssh.Target(mac), text = null;
            string why = await Task.Run(() => Pbpaste(target, out text));
            if (why == null && text == "") why = L.T("{0} 的剪贴板里没有文字", mac.Name);
            if (why == null && !Ui.CopyText(Windows(text))) why = L.T("剪贴板被占用，再试一次");
            if (why != null) { host.Notify(L.T("没读到 {0} 的剪贴板", mac.Name), why); return; }
            host.Notify(L.T("读到了 {0} 的剪贴板", mac.Name), L.T("{0} 个字，可以直接粘贴", text.Length));
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
            catch (Win32Exception) { return L.T("这台电脑没装 ssh"); }
            using (p)
            {
                var output = p.StandardOutput.ReadToEndAsync();
                var err = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                    return L.T("对方没回应");
                }
                if (p.ExitCode != 0) return Tailscale.FirstLine(err.Result, L.T("ssh 没连上"));
                text = output.Result;
                return null;
            }
        }
    }
}
