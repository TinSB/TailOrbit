// 和 Mac 共用剪贴板：两台电脑像一个剪贴板，哪边最后复制，两边粘贴出来的就是哪份。只传文字，密码也照样传。
// 本机一复制系统就会通知（Host 收 WM_CLIPBOARDUPDATE），用 ssh 把文字写进 Mac 的剪贴板；
// Mac 那边：ssh 过去挂一个 JXA 小脚本，只盯剪贴板的变化编号，一变就把文字送回来（不用在 Mac 上装东西）。
// 两边写进去时都带一个“同步来的”记号，看到它就不再传回去。密码管理器复制的密码，传过去时也标成密码：
// 对面照样能粘贴，只是不进剪贴板历史、不同步到云。只连一台 Mac（第一台在线、ssh 配置里有的）；
// Mac 睡着就等它醒，接上时把断开期间哪边复制过的补给另一边
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TailOrbit
{
    static class ClipSync
    {
        const string Marker = "TailOrbit.Synced";   // 本机剪贴板里“这是从 Mac 同步来的”
        const int MaxChars = 1000000;

        // Mac 上的看守。每 0.3 秒看一眼变化编号，变了就报“text 编号 内容的base64 是不是密码(1/0)”，不传的（文件、刚同步来的）报“seen 编号 - 0”；
        // 每 15 秒报一句 beat：这边据此发现连接断了；这边退出后它写不出去，也会自己退出
        const string Watcher = @"ObjC.import('AppKit');
var p = $.NSPasteboard.generalPasteboard, out = $.NSFileHandle.fileHandleWithStandardOutput;
var skip = ['com.tailorbit.synced', 'public.file-url'];
var hush = ['org.nspasteboard.ConcealedType', 'org.nspasteboard.TransientType', 'com.agilebits.onepassword'];
function say(s) { out.writeData($(s + '\n').dataUsingEncoding($.NSUTF8StringEncoding)); }
function has(t, list) { return list.some(function (x) { return t.indexOf(x) >= 0; }); }
function item() {
  var t = ObjC.deepUnwrap(p.types) || [];
  if (has(t, skip)) return '- 0';
  var s = p.stringForType('public.utf8-plain-text');
  if (s.isNil() || Number(s.length) == 0 || Number(s.length) > 1000000) return '- 0';
  return s.dataUsingEncoding($.NSUTF8StringEncoding).base64EncodedStringWithOptions(0).js + (has(t, hush) ? ' 1' : ' 0');
}
var last = Number(p.changeCount), n = 0;
say('hello ' + last + ' ' + item());
for (;;) {
  delay(0.3);
  var c = Number(p.changeCount);
  if (c != last) { last = c; var b = item(); say((b.charAt(0) == '-' ? 'seen ' : 'text ') + c + ' ' + b); }
  if (++n % 50 == 0) say('beat');
}";

        static readonly object gate = new object();
        static Host host;
        static int round;              // 每开、关一次加一：旧的看守循环看到对不上就收工
        static Process watcher;        // 正挂着的看守（ssh 进程）
        static volatile Device mac;    // 连着的那台；null 是没连上
        static long macCount = -1;     // Mac 剪贴板上次的变化编号，-1 是还不知道
        static string pending;         // 等着送去 Mac 的：送的时候又复制了，送完只补最新的那份
        static bool pendingSecret, pushing;
        static string last;            // 两边现在共有的那一份（换行统一成 \n）。只在界面线程上碰
        static bool missed;            // 断开时本机复制过：接上后补给 Mac。只在界面线程上碰

        public static event Action Changed;   // 连上、断开：本机页那一行跟着变
        public static string Peer { get { var d = mac; return d == null ? null : d.Name; } }
        public static string Problem { get; private set; }   // 连不上时 ssh 说的原因

        public static void Start(Host h)
        {
            host = h;
            if (Settings.SyncMac) Begin();
        }

        // 开、关；返回 null 表示成功，否则是开不成的原因
        public static string Turn(bool on)
        {
            if (on)
            {
                string why = Check();
                if (why != null) return why;
            }
            Settings.SyncMac = on;
            if (on) Begin();
            else Stop();
            return null;
        }

        // 关掉、退出程序时：看守停下，ssh 进程收掉
        public static void Stop()
        {
            lock (gate)
            {
                round++;
                Kill(watcher);
                watcher = null;
            }
            Connected(null, null);
        }

        // 要有 ssh，并且至少一台 Mac 在 ssh 配置里（睡着没关系，醒了就连上）
        static string Check()
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("ssh", "-V") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }))
                    p.WaitForExit(5000);
            }
            catch (Win32Exception) { return L.T("这台电脑没装 ssh"); }
            var snap = Tailscale.Load();
            if (snap.Error != null) return snap.Error;
            return snap.Devices.Any(d => d.Os == "macOS" && Ssh.Target(d) != null) ? null : L.T("没有能连的 Mac：Mac 要开着远程登录，并且 ssh 配置里有它。");
        }

        static void Begin()
        {
            int me;
            lock (gate) me = ++round;
            new Thread(() => Loop(me)) { IsBackground = true }.Start();
        }

        static bool Live(int me)
        {
            lock (gate) return round == me;
        }

        static void Loop(int me)
        {
            while (Live(me))
                try
                {
                    var d = Tailscale.Load().Devices.FirstOrDefault(ClipShare.Readable);
                    if (d == null)
                    {
                        Connected(null, null);
                        Pause(me, 15000);
                        continue;
                    }
                    string why;
                    bool heard = Watch(me, d, out why);
                    if (!Live(me)) return;
                    Connected(null, why);
                    // 连上过才断的（刚合盖）3 秒后就试；没连上的（Mac 睡着、没开远程登录）每 15 秒试一次：开盖后最多等这么久就接上
                    Pause(me, heard ? 3000 : 15000);
                }
                catch (Exception e)   // 后台线程上的异常会把整个程序带走：记下来，过一会儿再试
                {
                    Log.Write(e);
                    Pause(me, 60000);
                }
        }

        static void Pause(int me, int ms)
        {
            for (int t = 0; t < ms && Live(me); t += 500) Thread.Sleep(500);
        }

        // 挂上看守，一直听它报，直到断开。返回连上过没有（听到过它说话）；why 是断开的原因（ssh 的第一句报错，没有就是 null）
        static bool Watch(int me, Device d, out string why)
        {
            why = null;
            bool heard = false;
            var p = Remote(Ssh.Target(d), Watcher, "-o ServerAliveInterval=10 -o ServerAliveCountMax=3 ");
            lock (gate)
            {
                if (round != me) { Kill(p); p.Dispose(); return false; }
                watcher = p;
            }
            var err = p.StandardError.ReadToEndAsync();
            try
            {
                for (;;)
                {
                    var line = p.StandardOutput.ReadLineAsync();
                    if (!line.Wait(45000) || line.Result == null) break;   // 45 秒一句都没有：连接已经断了
                    heard = true;
                    Heard(me, d, line.Result);
                }
            }
            catch (AggregateException) { }   // 关掉时进程被收掉，读到一半断了
            finally
            {
                lock (gate) if (watcher == p) watcher = null;
                Kill(p);
            }
            why = err.Wait(2000) ? Tailscale.FirstLine(err.Result, null) : null;
            p.Dispose();
            return heard;
        }

        static void Heard(int me, Device d, string line)
        {
            var f = line.Split(' ');
            long count;
            if (f.Length < 4 || !long.TryParse(f[1], out count)) return;   // beat
            string text = f[2] == "-" ? null : Decode(f[2]);
            bool secret = f[3] == "1";
            if (f[0] == "hello")
            {
                bool moved = macCount >= 0 && count != macCount;   // 断开期间 Mac 那边复制过
                macCount = count;
                Connected(d, null);
                // 断开期间哪边复制过就补给另一边；两边都复制过，按 Mac 的（刚醒的那台多半是刚用过的）
                OnUi(me, () =>
                {
                    bool mine = missed;
                    missed = false;
                    if (moved && text != null) Take(d, text, secret);
                    else if (mine) Copied();
                });
                return;
            }
            macCount = count;
            if (f[0] == "text" && text != null) OnUi(me, () => Take(d, text, secret));
        }

        // Mac 那边复制了：放进本机剪贴板（带上记号，免得再传回去），星网上从 Mac 往本机流一下
        static void Take(Device d, string text, bool secret)
        {
            text = text.Replace("\r\n", "\n");
            if (text == last || !Put(ClipShare.Windows(text), secret)) return;
            last = text;
            host.ClipFlow(d.Ip, true);
        }

        static bool Put(string text, bool secret)
        {
            var data = new DataObject();
            data.SetText(text);
            data.SetData(Marker, new MemoryStream(new byte[] { 1 }));
            if (secret)   // Mac 那边是密码：本机也别进剪贴板历史、别同步到云
            {
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1 }));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
            }
            for (int i = 0; ; i++)
                try
                {
                    Clipboard.SetDataObject(data, true);
                    return true;
                }
                catch (COMException)
                {
                    if (i == 4) return false;   // 别的程序一直开着剪贴板
                    Thread.Sleep(30);
                }
        }

        // 本机剪贴板变了（Host 收到系统通知后在界面线程上调）
        public static void Copied()
        {
            if (!Settings.SyncMac) return;
            try { if (Clipboard.ContainsData(Marker)) return; }   // 是刚从 Mac 同步来的
            catch (COMException) { return; }
            bool secret;
            string text = ClipShare.Peek(out secret);
            if (text == null || text.Length > MaxChars) return;
            text = text.Replace("\r\n", "\n");
            if (mac == null) { missed = true; return; }
            if (text == last) return;
            last = text;
            lock (gate)
            {
                pending = text;
                pendingSecret = secret;
                if (pushing) return;
                pushing = true;
            }
            Task.Run(() => Push());
        }

        // 一次送一份；送的时候本机又复制了，送完接着送最新的
        static void Push()
        {
            for (;;)
            {
                string text;
                bool secret;
                lock (gate)
                {
                    text = pending;
                    secret = pendingSecret;
                    pending = null;
                    if (text == null) { pushing = false; return; }
                }
                var d = mac;
                if (d != null && Write(Ssh.Target(d), text, secret))
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => host.ClipFlow(d.Ip, false)));
                else   // 没送到（Mac 刚睡着）：接上时再补
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => { last = null; missed = true; }));
            }
        }

        // 写进 Mac 的剪贴板，带上记号（看守看到就不再传回来）；密码再标成密码，Mac 上的剪贴板工具就不记它
        static bool Write(string target, string text, bool secret)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            string script = @"ObjC.import('AppKit');
var p = $.NSPasteboard.generalPasteboard;
var s = $.NSString.alloc.initWithDataEncoding($.NSData.alloc.initWithBase64EncodedStringOptions('" + b64 + @"', 0), $.NSUTF8StringEncoding);
var it = $.NSPasteboardItem.alloc.init;
it.setStringForType(s, 'public.utf8-plain-text');
it.setDataForType($.NSData.data, 'com.tailorbit.synced');
" + (secret ? "it.setDataForType($.NSData.data, 'org.nspasteboard.ConcealedType');\n" : "") + @"p.clearContents;
p.writeObjects($.NSArray.arrayWithObject(it)) ? 'ok' : 'no';";
            try
            {
                using (var p = Remote(target, script, ""))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(15000)) { Kill(p); return false; }
                    return p.ExitCode == 0 && output.Result.Trim() == "ok";
                }
            }
            catch (Win32Exception) { return false; }   // ssh 不见了
        }

        // ssh 过去跑 osascript，脚本从标准输入喂进去（脚本里只有英文字母，不用管编码）
        static Process Remote(string target, string script, string extra)
        {
            var p = Process.Start(new ProcessStartInfo("ssh", "-o BatchMode=yes -o ConnectTimeout=6 " + extra + target + " \"osascript -l JavaScript\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.ASCII, StandardErrorEncoding = Encoding.UTF8
            });
            try
            {
                p.StandardInput.Write(script);
                p.StandardInput.Close();
            }
            catch (IOException) { }   // ssh 已经退出（连不上）：它的报错在标准错误里
            return p;
        }

        static string Decode(string b64)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch (FormatException) { return null; }   // 连接断在半行上
        }

        static void Connected(Device d, string why)
        {
            if (mac == d && Problem == why) return;
            mac = d;
            Problem = why;
            var h = Changed;
            if (h != null) Application.Current.Dispatcher.BeginInvoke(h);
        }

        static void OnUi(int me, Action a)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => { if (Live(me)) a(); }));
        }

        static void Kill(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }
}
