// 和 Mac 共用剪贴板：两台电脑像一个剪贴板，哪边最后复制，两边粘贴出来的就是哪份。传文字和图片（截图这类），密码也照样传。
// 本机一复制系统就会通知（Host 收 WM_CLIPBOARDUPDATE），用 ssh 把它写进 Mac 的剪贴板；
// Mac 那边：ssh 过去挂一个 JXA 小脚本，只盯剪贴板的变化编号，一变就把内容送回来（不用在 Mac 上装东西）。
// 文字和图片都有时传文字（Excel 复制表格时两样都有）；图片一律转成 PNG 传，超过 32 MB 的不传，弹个通知。
// 两边写进去时都带一个“同步来的”记号，看到它就不再传回去。密码管理器复制的东西，传过去时也标成密码：
// 对面照样能粘贴，只是不进剪贴板历史、不同步到云。只连一台 Mac（第一台在线、ssh 配置里有的）；
// Mac 睡着就等它醒，接上时把断开期间哪边复制过的补给另一边
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TailOrbit
{
    // 两边之间传的一份：文字或 PNG 图片。Key 用来认“两边已经是同一份了”
    class ClipItem
    {
        public string Text;
        public byte[] Png;
        public bool Secret;
        public string Key;

        public static ClipItem OfText(string text, bool secret)
        {
            text = text.Replace("\r\n", "\n");
            return new ClipItem { Text = text, Secret = secret, Key = text };
        }

        public static ClipItem OfPng(byte[] png, bool secret)
        {
            using (var h = SHA1.Create()) return new ClipItem { Png = png, Secret = secret, Key = "\0png " + Convert.ToBase64String(h.ComputeHash(png)) };
        }
    }

    static class ClipSync
    {
        public const string Marker = "TailOrbit.Synced";   // 本机剪贴板里“这是从 Mac 同步来的”
        const int MaxChars = 1000000;
        public const int MaxPng = 32 << 20;                // 超过 32 MB 的图片不传：iPhone 的照片之类转成 PNG 能上百 MB

        // Mac 上的看守。每 0.3 秒看一眼变化编号，变了就报“clip 编号 种类 内容 是不是密码(1/0)”：种类 text 是文字、image 是 PNG 图片，
        // 内容是 base64；big 是太大没传的图片，内容是字节数；- 是不传的（文件、刚同步来的）。
        // 接上时先报 hello：编号和这边上次知道的（KNOWN）不一样才带上内容——断开期间 Mac 复制过的要补过来，没变的不用再传一遍。
        // 每 15 秒报一句 beat：这边据此发现连接断了；这边退出后它写不出去，也会自己退出
        const string Watcher = @"ObjC.import('AppKit');
var p = $.NSPasteboard.generalPasteboard, out = $.NSFileHandle.fileHandleWithStandardOutput;
var skip = ['com.tailorbit.synced', 'public.file-url'];
var hush = ['org.nspasteboard.ConcealedType', 'org.nspasteboard.TransientType', 'com.agilebits.onepassword'];
function say(s) { out.writeData($(s + '\n').dataUsingEncoding($.NSUTF8StringEncoding)); }
function has(t, list) { return list.some(function (x) { return t.indexOf(x) >= 0; }); }
function b64(d) { return d.base64EncodedStringWithOptions(0).js; }
function item() {
  var t = ObjC.deepUnwrap(p.types) || [], h = has(t, hush) ? ' 1' : ' 0';
  if (has(t, skip)) return '- - 0';
  var s = p.stringForType('public.utf8-plain-text');
  if (!s.isNil() && Number(s.length) > 0) return Number(s.length) > 1000000 ? '- - 0' : 'text ' + b64(s.dataUsingEncoding($.NSUTF8StringEncoding)) + h;
  var d = p.dataForType('public.png');
  if (d.isNil() && $.NSImage.canInitWithPasteboard(p)) {
    var r = $.NSBitmapImageRep.imageRepWithData($.NSImage.alloc.initWithPasteboard(p).TIFFRepresentation);
    if (!r.isNil()) d = r.representationUsingTypeProperties(4, $());
  }
  if (d.isNil() || Number(d.length) == 0) return '- - 0';
  return Number(d.length) > MAXPNG ? 'big ' + Number(d.length) + h : 'image ' + b64(d) + h;
}
var last = Number(p.changeCount), n = 0;
say('hello ' + last + ' ' + (KNOWN < 0 || last == KNOWN ? '- - 0' : item()));
for (;;) {
  delay(0.3);
  var c = Number(p.changeCount);
  if (c != last) { last = c; say('clip ' + c + ' ' + item()); }
  if (++n % 50 == 0) say('beat');
}";

        static readonly object gate = new object();
        static Host host;
        static int round;              // 每开、关一次加一：旧的看守循环看到对不上就收工
        static Process watcher;        // 正挂着的看守（ssh 进程）
        static volatile Device mac;    // 连着的那台；null 是没连上
        static long macCount = -1;     // Mac 剪贴板上次的变化编号，-1 是还不知道。只在看守线程上碰
        static ClipItem pending;       // 等着送去 Mac 的：送的时候又复制了，送完只补最新的那份
        static bool pushing;
        static string last;            // 两边现在共有的那一份的 Key。只在界面线程上碰
        static bool missed;            // 断开时本机复制过：接上后补给 Mac。只在界面线程上碰

        public static event Action Changed;   // 连上、断开：设置页那一行跟着变
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
            catch (Win32Exception) { return L.T("本机未安装 SSH"); }
            var snap = Tailscale.Load();
            if (snap.Error != null) return snap.Error;
            return snap.Devices.Any(d => d.Os == "macOS" && Ssh.Target(d) != null) ? null : L.T("未找到可连接的 Mac。Mac 需要已开启“远程登录”，并已添加到 SSH 配置中。");
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
            string script = Watcher.Replace("KNOWN", macCount.ToString(CultureInfo.InvariantCulture)).Replace("MAXPNG", MaxPng.ToString(CultureInfo.InvariantCulture));
            var p = Remote(Ssh.Target(d), script, "-o ServerAliveInterval=10 -o ServerAliveCountMax=3 ");
            lock (gate)
            {
                if (round != me) { Kill(p); p.Dispose(); return false; }
                watcher = p;
            }
            var err = p.StandardError.ReadToEndAsync();
            var lines = new Lines(p.StandardOutput.BaseStream);
            try
            {
                for (;;)
                {
                    string line = lines.Next();
                    if (line == null) break;   // 45 秒一个字节都没有：连接已经断了
                    heard = true;
                    Heard(me, d, line);
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

        // 看守报的一行。一行可能很长（几十 MB 的图片）：只要还有字节在来就接着等，45 秒一个字节都没有才算断了
        class Lines
        {
            readonly Stream s;
            readonly byte[] buf = new byte[1 << 16];
            int at, end;

            public Lines(Stream s) { this.s = s; }

            public string Next()
            {
                var sb = new StringBuilder();
                for (;;)
                {
                    if (at == end)
                    {
                        var r = s.ReadAsync(buf, 0, buf.Length);
                        if (!r.Wait(45000) || r.Result == 0) return null;
                        at = 0;
                        end = r.Result;
                    }
                    int nl = Array.IndexOf(buf, (byte)'\n', at, end - at);
                    int stop = nl < 0 ? end : nl;
                    sb.Append(Encoding.ASCII.GetString(buf, at, stop - at));
                    at = nl < 0 ? end : nl + 1;
                    if (nl >= 0) return sb.ToString();
                }
            }
        }

        static void Heard(int me, Device d, string line)
        {
            var f = line.Split(' ');
            long count;
            if (f.Length < 5 || !long.TryParse(f[1], out count)) return;   // beat
            bool secret = f[4] == "1";
            ClipItem c = f[2] == "text" ? ClipItem.OfText(Encoding.UTF8.GetString(Convert.FromBase64String(f[3])), secret)
                : f[2] == "image" ? ClipItem.OfPng(Convert.FromBase64String(f[3]), secret) : null;
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
                    if (moved && c != null) Take(d, c);
                    else if (mine) Copied();
                });
                return;
            }
            macCount = count;
            if (c != null) OnUi(me, () => Take(d, c));
            else if (f[2] == "big") OnUi(me, () => TooBig(long.Parse(f[3], CultureInfo.InvariantCulture)));
        }

        // Mac 那边复制了：放进本机剪贴板（带上记号，免得再传回去），星网上从 Mac 往本机流一下
        static void Take(Device d, ClipItem c)
        {
            if (c.Key == last || !Put(c, true)) return;
            last = c.Key;
            host.ClipFlow(d.Ip, true);
        }

        // 放进本机剪贴板。图片放两份：PNG 原样（浏览器、Office 认它，透明也在），再放一份位图给只认位图的程序（画图之类）。
        // synced：带上“从 Mac 同步来的”记号，不再传回 Mac、也不推给手机（iPhone 发来的不带，照常同步给 Mac）
        public static bool Put(ClipItem c, bool synced)
        {
            var data = new DataObject();
            if (c.Png == null) data.SetText(ClipShare.Windows(c.Text));
            else
            {
                data.SetData("PNG", new MemoryStream(c.Png));
                data.SetImage(BitmapFrame.Create(new MemoryStream(c.Png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
            }
            if (synced) data.SetData(Marker, new MemoryStream(new byte[] { 1 }));
            if (c.Secret)   // Mac 那边是密码：本机也别进剪贴板历史、别同步到云
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
            if (mac == null) { missed = true; return; }   // 接上时再读、再补
            ClipItem c;
            try
            {
                if (Clipboard.ContainsData(Marker)) return;   // 是刚从 Mac 同步来的
                c = Read();
            }
            catch (COMException) { return; }   // 别的程序正开着剪贴板
            if (c == null || c.Key == last) return;
            if (c.Png != null && c.Png.Length > MaxPng) { TooBig(c.Png.Length); return; }
            last = c.Key;
            lock (gate)
            {
                pending = c;
                if (pushing) return;
                pushing = true;
            }
            Task.Run(() => Push());
        }

        // 本机剪贴板里能传的：先看文字（Excel 复制表格时文字、图片都有，要的是文字），没有文字再看图片。在界面线程上调。
        // Tailscale 的访问令牌不传：连管理接口、开通云盘时 TailOrbit 自己从剪贴板拿走，不该先被同步到 Mac、推到手机上
        public static ClipItem Read()
        {
            bool secret;
            string text = ClipShare.Peek(out secret);
            if (!string.IsNullOrEmpty(text)) return text.Length > MaxChars || Api.Token.IsMatch(text) ? null : ClipItem.OfText(text, secret);
            byte[] png = Png();
            return png == null ? null : ClipItem.OfPng(png, ClipShare.Secret());
        }

        // 现成的 PNG 格式（截图工具、浏览器复制的图片都带）直接用；只有位图的编成 PNG。
        // 编的时候不要透明度：只有位图的多半是截图，那里的 Alpha 常常是乱的，带过去会变成透明的一片
        static byte[] Png()
        {
            var s = Clipboard.GetData("PNG") as MemoryStream;
            if (s != null && s.Length > 0) return s.ToArray();
            if (!Clipboard.ContainsImage()) return null;
            var bmp = Clipboard.GetImage();
            if (bmp == null) return null;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0)));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        // 太大的图片不传：弹个通知，免得以为同步坏了
        static void TooBig(long bytes)
        {
            host.Notify(L.T("图片过大，未同步"), L.T("此图片大小为 {0:0.#} MB（上限为 {1} MB），可使用“发送文件”发送。", bytes / 1048576.0, MaxPng >> 20));
        }

        // 一次送一份；送的时候本机又复制了，送完接着送最新的
        static void Push()
        {
            for (;;)
            {
                ClipItem c;
                lock (gate)
                {
                    c = pending;
                    pending = null;
                    if (c == null) { pushing = false; return; }
                }
                var d = mac;
                if (d != null && Write(Ssh.Target(d), c))
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => host.ClipFlow(d.Ip, false)));
                else   // 没送到（Mac 刚睡着）：接上时再补
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => { last = null; missed = true; }));
            }
        }

        // 写进 Mac 的剪贴板，带上记号（看守看到就不再传回来）；是密码再标成密码，Mac 上的剪贴板工具就不记它。
        // 图片放 PNG 原样，再放一份 TIFF 给只认 TIFF 的老程序
        static bool Write(string target, ClipItem c)
        {
            string b64 = Convert.ToBase64String(c.Png ?? Encoding.UTF8.GetBytes(c.Text));
            string script = @"ObjC.import('AppKit');
var p = $.NSPasteboard.generalPasteboard;
var d = $.NSData.alloc.initWithBase64EncodedStringOptions('" + b64 + @"', 0);
var it = $.NSPasteboardItem.alloc.init;
" + (c.Png == null ? "it.setStringForType($.NSString.alloc.initWithDataEncoding(d, $.NSUTF8StringEncoding), 'public.utf8-plain-text');\n"
                : "it.setDataForType(d, 'public.png');\nvar img = $.NSImage.alloc.initWithData(d);\nif (!img.isNil()) it.setDataForType(img.TIFFRepresentation, 'public.tiff');\n")
              + "it.setDataForType($.NSData.data, 'com.tailorbit.synced');\n"
              + (c.Secret ? "it.setDataForType($.NSData.data, 'org.nspasteboard.ConcealedType');\n" : "") + @"p.clearContents;
p.writeObjects($.NSArray.arrayWithObject(it)) ? 'ok' : 'no';";
            try
            {
                using (var p = Remote(target, script, ""))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(15000 + b64.Length / 400)) { Kill(p); return false; }   // 大图片多给点时间：按最慢每秒 400 KB 算
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
