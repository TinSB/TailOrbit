// TailOrbit：Tailscale 的 Windows 小工具——把文件拖到设备上，就用 Taildrop 发过去
// 主界面（MainWindow.cs、Star.cs）：点托盘图标或开始菜单打开。“星网”——本机在中间，设备按直连 / 中继 / 离线排在三圈上；
// 把文件拖到设备上就用 Taildrop 发过去，也能点设备挑文件；左下是最近发过的，右上齿轮里是开机自启和退出。
// 快捷面板（QuickPanel.cs）：按 Ctrl+Alt+A 弹出，把资源管理器里选中的文件（文件夹先打包成 zip）发给按下数字的那台设备。
// 编译：powershell -ExecutionPolicy Bypass -File build.ps1（用 Windows 自带的 .NET Framework 4.8 编译器，只认 C# 5 语法）

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

// 任务管理器等处显示的名字
[assembly: System.Reflection.AssemblyTitle("TailOrbit")]
[assembly: System.Reflection.AssemblyProduct("TailOrbit")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

namespace TailOrbit
{
    static class Program
    {
        public const string ShowEventName = @"Local\TailOrbit.ShowMain";

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        [STAThread]
        static void Main(string[] args)
        {
            bool first;
            using (new Mutex(true, @"Local\TailOrbit.Single", out first))
            {
                if (!first)
                {
                    // 已经在运行（比如又从开始菜单点了一次）：叫它把主界面拿出来。
                    // 这次启动带着“可以切到前台”的许可，先转给它，不然主界面只会在任务栏上闪
                    AllowSetForegroundWindow(-1);   // ASFW_ANY
                    EventWaitHandle show;
                    if (EventWaitHandle.TryOpenExisting(ShowEventName, out show)) using (show) show.Set();
                    return;
                }
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // 常驻程序出了没想到的错也不能悄悄消失（消失了热键就没了），记下来接着跑
                app.DispatcherUnhandledException += (s, e) => { Log.Write(e.Exception); e.Handled = true; };
                new Host().Start(!args.Contains("--tray"));
                app.Run();
            }
        }
    }

    static class Log
    {
        public static void Write(Exception e)
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailOrbit");
                Directory.CreateDirectory(dir);
                File.AppendAllText(System.IO.Path.Combine(dir, "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + e + Environment.NewLine);
            }
            catch (Exception) { }   // 记日志本身不能再把程序弄崩
        }
    }

    class Device
    {
        public string Ip = "", Name = "", Os = "", StableId = "";
        public string Link = "";   // direct 直连 / relay 经中继 / "" 还不知道（只有正在通信的设备，status 才说得出走哪条路）
        public bool Online;
        public string Key { get { return Ip + "|" + Name + "|" + Online + "|" + Os; } }
    }

    class Snapshot
    {
        public List<Device> Devices = new List<Device>();
        public Device Self;
        public string Error;   // null 表示正常
    }

    class CmdResult
    {
        public int Code;
        public string Out, Err;
    }

    static class Tailscale
    {
        public static readonly string Exe = File.Exists(@"C:\Program Files\Tailscale\tailscale.exe") ? @"C:\Program Files\Tailscale\tailscale.exe" : "tailscale.exe";

        public static ProcessStartInfo Psi(string args)
        {
            var psi = new ProcessStartInfo(Exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            return psi;
        }

        public static CmdResult Run(string args)
        {
            using (var p = Process.Start(Psi(args)))
            {
                var err = p.StandardError.ReadToEndAsync();
                var r = new CmdResult();
                r.Out = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                r.Err = err.Result;
                r.Code = p.ExitCode;
                return r;
            }
        }

        // 能用 Taildrop 接收的设备（只有自己名下的，在线的排前面），外加本机信息
        public static Snapshot Load()
        {
            var snap = new Snapshot();
            var targets = Run("file cp --targets");
            if (targets.Code != 0)
            {
                snap.Error = Explain(targets.Err);
                return snap;
            }
            var list = new List<Device>();
            foreach (var raw in targets.Out.Split('\n'))
            {
                var parts = raw.TrimEnd('\r').Split('\t');
                if (parts.Length < 2) continue;
                var d = new Device();
                d.Ip = parts[0];
                d.Name = parts[1];
                d.Online = parts.Length < 3 || !parts[2].StartsWith("offline");
                list.Add(d);
            }
            var status = Run("status --json");
            if (status.Code == 0) FillFromStatus(snap, list, status.Out);
            snap.Devices = list.Where(d => d.Online).Concat(list.Where(d => !d.Online)).ToList();
            return snap;
        }

        static void FillFromStatus(Snapshot snap, List<Device> list, string json)
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            var root = js.DeserializeObject(json) as Dictionary<string, object>;
            var self = Get(root, "Self") as Dictionary<string, object>;
            if (self != null) snap.Self = ToDevice(self);
            var peers = Get(root, "Peer") as Dictionary<string, object>;
            if (peers == null) return;
            foreach (var peer in peers.Values.OfType<Dictionary<string, object>>())
            {
                var ips = Get(peer, "TailscaleIPs") as object[];
                if (ips == null) continue;
                foreach (var d in list.Where(x => ips.Contains(x.Ip)))
                {
                    d.Os = (Get(peer, "OS") as string) ?? "";
                    d.StableId = (Get(peer, "ID") as string) ?? "";
                    // 正在通信时 CurAddr 是对方的直连地址，为空说明走的中继；没在通信就看不出来
                    if (true.Equals(Get(peer, "Active"))) d.Link = ((Get(peer, "CurAddr") as string) ?? "") != "" ? "direct" : "relay";
                }
            }
        }

        // 探一下到这台设备走直连还是中继：tailscale ping 打通直连就停；对方没回应返回 ""
        public static string Probe(string ip)
        {
            var r = Run("ping --c 3 --timeout 2s " + ip);
            string link = "";
            foreach (var line in r.Out.Split('\n'))
            {
                int via = line.IndexOf(" via ", StringComparison.Ordinal);
                if (!line.StartsWith("pong from ") || via < 0) continue;   // pong from my-phone (100.x.y.z) via [2001:db8::1]:41641 in 4ms
                if (!line.Substring(via + 5).StartsWith("DERP(")) return "direct";
                link = "relay";
            }
            return link;
        }

        static Device ToDevice(Dictionary<string, object> node)
        {
            var d = new Device();
            var ips = Get(node, "TailscaleIPs") as object[];
            d.Ip = ips != null && ips.Length > 0 ? (ips[0] as string) ?? "" : "";
            string dns = (Get(node, "DNSName") as string) ?? "";
            d.Name = dns.Length > 0 ? dns.Split('.')[0] : (Get(node, "HostName") as string) ?? "";
            d.Os = (Get(node, "OS") as string) ?? "";
            d.Online = true;
            return d;
        }

        public static object Get(Dictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) ? v : null;
        }

        static string Explain(string err)
        {
            string line = (err ?? "").Trim().Split('\n')[0].Trim();
            if (line.Contains("NeedsLogin") || line.Contains("not logged in")) return "Tailscale 没有登录";
            if (line.Contains("failed to connect") || line.Contains("is Tailscale running")) return "Tailscale 没有运行";
            return line.Length > 0 ? line : "读不到设备列表";
        }
    }

    // 本程序退出（包括崩溃）时，放进这里的子进程跟着结束，不会留下孤儿进程
    static class ChildJob
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        static readonly IntPtr job = Create();

        static IntPtr Create()
        {
            IntPtr h = CreateJobObject(IntPtr.Zero, null);
            var info = new ExtendedLimits();
            info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            SetInformationJobObject(h, 9, ref info, Marshal.SizeOf(info));
            return h;
        }

        public static Process Start(ProcessStartInfo psi)
        {
            var p = Process.Start(psi);
            AssignProcessToJobObject(job, p.Handle);
            return p;
        }
    }

    static class Selection
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr obj); }

        [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IOleWindow { [PreserveSig] int GetWindow(out IntPtr hwnd); }

        // 当前窗口是资源管理器或桌面时，取出选中的文件和文件夹
        public static List<string> FromWindow(IntPtr fg)
        {
            var paths = new List<string>();
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic view = null;
                if (cls.ToString() == "CabinetWClass")
                {
                    // Win11 一个资源管理器窗口里可能开着几个标签页，只取正在显示的那个
                    IntPtr tab = FindWindowEx(fg, IntPtr.Zero, "ShellTabWindowClass", null);
                    foreach (dynamic w in shell.Windows())
                    {
                        if (new IntPtr(Convert.ToInt64(w.HWND)) != fg) continue;
                        if (tab != IntPtr.Zero && TabOf(w) != tab) continue;
                        view = w.Document;
                        break;
                    }
                }
                else if (cls.ToString() == "Progman" || cls.ToString() == "WorkerW")
                {
                    object loc = 0, root = null;
                    int hwnd;
                    view = shell.Windows().FindWindowSW(ref loc, ref root, 8, out hwnd, 1).Document;   // SWC_DESKTOP, SWFO_NEEDDISPATCH
                }
                if (view != null)
                    foreach (dynamic item in view.SelectedItems()) paths.Add((string)item.Path);
            }
            catch (Exception) { }   // 资源管理器正忙或窗口正在关：当成没选中
            // 盘符本身（比如在“此电脑”里选了 C 盘）不发
            return paths.Where(p => File.Exists(p) || (Directory.Exists(p) && System.IO.Path.GetPathRoot(p) != p)).ToList();
        }

        static IntPtr TabOf(object browser)
        {
            var sp = browser as IShellServiceProvider;
            if (sp == null) return IntPtr.Zero;
            Guid sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");   // SID_STopLevelBrowser
            Guid iid = new Guid("000214E2-0000-0000-C000-000000000046");   // IShellBrowser
            IntPtr ptr;
            if (sp.QueryService(ref sid, ref iid, out ptr) != 0 || ptr == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr h;
                ((IOleWindow)Marshal.GetObjectForIUnknown(ptr)).GetWindow(out h);
                return h;
            }
            finally { Marshal.Release(ptr); }
        }
    }

    class SendJob
    {
        public const double Unknown = -1, Keep = -2;   // 进度值：不知道进度 / 只改文字
        static readonly Regex LogPrefix = new Regex(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}(\.\d+)? ");

        public Device Target;
        public List<string> Paths;
        public Action<double, string> Progress;
        public long TotalBytes;
        volatile bool canceled;
        Process cp, watch;

        public void Cancel()
        {
            canceled = true;
            Kill(cp);
        }

        static void Kill(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(); }
            catch (InvalidOperationException) { }   // 刚好自己退出了
            catch (Win32Exception) { }
        }

        // 返回 null 表示发送成功，"" 表示取消了，其他是失败原因
        public string Run()
        {
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TailOrbit", Guid.NewGuid().ToString("N"));
            try
            {
                var files = new List<string>();
                foreach (var p in Paths)
                {
                    if (!Directory.Exists(p)) { files.Add(p); continue; }
                    Progress(Unknown, "打包中");
                    Directory.CreateDirectory(temp);
                    files.Add(Zip(p, temp));
                }
                return canceled ? "" : Push(files);
            }
            catch (OperationCanceledException) { return ""; }
            catch (IOException e) { return e.Message; }
            catch (UnauthorizedAccessException e) { return e.Message; }
            finally
            {
                Kill(watch);
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        string Zip(string dir, string temp)
        {
            var info = new DirectoryInfo(dir);
            string zipPath = System.IO.Path.Combine(temp, info.Name + ".zip");
            int skip = info.Parent.FullName.TrimEnd('\\').Length + 1;
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var f in Directory.EnumerateFiles(info.FullName, "*", SearchOption.AllDirectories))
                {
                    if (canceled) throw new OperationCanceledException();
                    zip.CreateEntryFromFile(f, f.Substring(skip).Replace('\\', '/'), CompressionLevel.Fastest);
                }
            }
            return zipPath;
        }

        string Push(List<string> files)
        {
            long[] sizes = files.Select(f => new FileInfo(f).Length).ToArray();
            TotalBytes = sizes.Sum();
            long total = Math.Max(1, TotalBytes);
            int index = 0;      // 正在发第几个文件
            long done = 0;      // 已经发完的字节
            long current = 0;   // 当前文件已发出的字节（Tailscale 实时事件报的）
            var gate = new object();
            Action report = () =>
            {
                double f;
                lock (gate) f = Math.Min(1.0, (done + current) / (double)total);
                Progress(f, (int)(f * 100) + "%");
            };

            // 实时进度：订阅 Tailscale 的事件流，官方命令行画进度条用的也是它；拿不到就只按文件数走
            try
            {
                watch = ChildJob.Start(Tailscale.Psi("debug watch-ipn --peer-changes=false --peer-patches=false"));
                var buf = new StringBuilder();
                var js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                watch.ErrorDataReceived += (s, e) => { };
                watch.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    buf.AppendLine(e.Data);
                    if (e.Data != "}") return;   // 每条事件是一段缩进的 JSON，以单独一行 } 结尾
                    string text = buf.ToString();
                    buf.Clear();
                    if (text.IndexOf("\"OutgoingFiles\"", StringComparison.Ordinal) < 0) return;
                    object[] outs;
                    try { outs = Tailscale.Get(js.DeserializeObject(text) as Dictionary<string, object>, "OutgoingFiles") as object[]; }
                    catch (ArgumentException) { return; }          // 格式对不上就不画进度，照样发送
                    catch (InvalidOperationException) { return; }
                    if (outs == null) return;
                    foreach (var of in outs.OfType<Dictionary<string, object>>())
                    {
                        if (true.Equals(Tailscale.Get(of, "Finished"))) continue;
                        if (Target.StableId != "" && !Target.StableId.Equals(Tailscale.Get(of, "PeerID"))) continue;
                        bool mine;
                        lock (gate)
                        {
                            // 事件里的文件名是网址编码过的（中文会变成 %E5%A4%A7…），先解码再比
                            mine = index < files.Count && System.IO.Path.GetFileName(files[index]) == Uri.UnescapeDataString((Tailscale.Get(of, "Name") as string) ?? "");
                            if (mine) current = Math.Max(current, Convert.ToInt64(Tailscale.Get(of, "Sent") ?? 0L));
                        }
                        if (mine) report();
                    }
                };
                watch.BeginErrorReadLine();
                watch.BeginOutputReadLine();
            }
            catch (Win32Exception) { watch = null; }

            var args = new StringBuilder("file cp --verbose");
            foreach (var f in files) args.Append(" \"").Append(f).Append('"');
            args.Append(' ').Append(Target.Ip).Append(':');
            string lastError = null;
            Progress(watch == null ? Unknown : 0, watch == null ? "发送中" : "0%");
            cp = Process.Start(Tailscale.Psi(args.ToString()));
            cp.OutputDataReceived += (s, e) => { };
            cp.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                string line = LogPrefix.Replace(e.Data, "").Trim();
                if (line.StartsWith("sent \""))
                {
                    lock (gate)
                    {
                        if (index < sizes.Length) done += sizes[index];
                        index++;
                        current = 0;
                    }
                    report();
                }
                else if (line.StartsWith("# warning:")) Progress(Keep, "对方没回应，还在重试");
                else if (line.Length > 0 && !line.StartsWith("sending \"")) lastError = line;
            };
            cp.BeginOutputReadLine();
            cp.BeginErrorReadLine();
            if (canceled) Kill(cp);
            cp.WaitForExit();   // 不设时限：大文件发多久都等
            if (canceled) return "";
            return cp.ExitCode == 0 ? null : (lastError ?? "发送失败");
        }
    }

    class Transfer
    {
        public DateTime Time;
        public string Target = "", Ip = "", Os = "", Result = "", Error = "";
        public List<string> Paths = new List<string>();
        public long Bytes;
    }

    // 传输记录：一行一条追加写进 history.jsonl，只增不删
    static class History
    {
        static readonly string FilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailOrbit", "history.jsonl");
        public static readonly List<Transfer> Items = Load();
        public static event Action Changed;

        static List<Transfer> Load()
        {
            var list = new List<Transfer>();
            if (!File.Exists(FilePath)) return list;
            var js = new JavaScriptSerializer();
            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                Dictionary<string, object> o;
                try { o = js.DeserializeObject(line) as Dictionary<string, object>; }
                catch (ArgumentException) { continue; }   // 写到一半断电留下的坏行，跳过，不影响其他记录
                if (o == null) continue;
                var t = new Transfer();
                DateTime.TryParse(Tailscale.Get(o, "time") as string, null, DateTimeStyles.RoundtripKind, out t.Time);
                t.Target = Text(o, "target");
                t.Ip = Text(o, "ip");
                t.Os = Text(o, "os");
                t.Result = Text(o, "result");
                t.Error = Text(o, "error");
                var paths = Tailscale.Get(o, "paths") as object[];
                if (paths != null) t.Paths = paths.OfType<string>().ToList();
                t.Bytes = Convert.ToInt64(Tailscale.Get(o, "bytes") ?? 0L);
                list.Add(t);
            }
            return list;
        }

        static string Text(Dictionary<string, object> o, string key)
        {
            return (Tailscale.Get(o, key) as string) ?? "";
        }

        public static void Record(SendJob j, string result)
        {
            var t = new Transfer
            {
                Time = DateTime.Now, Target = j.Target.Name, Ip = j.Target.Ip, Os = j.Target.Os, Paths = j.Paths, Bytes = j.TotalBytes,
                Result = result == null ? "ok" : result == "" ? "cancel" : "fail", Error = result ?? ""
            };
            Items.Add(t);
            var o = new Dictionary<string, object>
            {
                { "time", t.Time.ToString("o") }, { "target", t.Target }, { "ip", t.Ip }, { "os", t.Os },
                { "paths", t.Paths }, { "bytes", t.Bytes }, { "result", t.Result }, { "error", t.Error }
            };
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                File.AppendAllText(FilePath, new JavaScriptSerializer().Serialize(o) + "\n", new UTF8Encoding(false));
            }
            catch (IOException e) { Log.Write(e); }   // 记录写不进去不影响发送本身
            var h = Changed;
            if (h != null) h();
        }
    }

    // 开机自启：写当前用户的 Run 注册表项，只在用户自己点开关时改
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static string Command { get { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --tray"; } }

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && string.Equals(k.GetValue("TailOrbit") as string, Command, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("TailOrbit", Command);
                else k.DeleteValue("TailOrbit", false);
            }
        }
    }

    // 星网配色：灰阶分出层次，只有“送到了”和“没送到”带颜色；跟着系统的深浅色走
    class Theme
    {
        public readonly bool Dark;
        public readonly Color BgColor, FgColor, InkColor, MuteColor, DimColor, OkColor, BadColor;
        public readonly Brush Bg, Fg, Ink, Mute, Dim, Hair, Wash, Fill, Rim, Chip, Tile, Pop, Ok, Bad, OkSoft, BadSoft;

        public Theme()
        {
            object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            Dark = v is int && (int)v == 0;
            BgColor = C(Dark ? 0xFF1A1C1Fu : 0xFFE6E8EBu);
            FgColor = C(Dark ? 0xFFE7E9ECu : 0xFF1A1C1Fu);     // 在线设备、正文
            InkColor = C(Dark ? 0xFF1A1C1Fu : 0xFFF1F2F4u);    // 实心圆上的图标和数字
            MuteColor = C(Dark ? 0xFF8C929Au : 0xFF5B6169u);   // 设备名等次要文字
            DimColor = C(Dark ? 0xFF4A4F56u : 0xFFA3A9B0u);    // 离线
            OkColor = C(Dark ? 0xFF3BCB82u : 0xFF1D9A5Bu);
            BadColor = C(Dark ? 0xFFF0554Cu : 0xFFD33A31u);
            Bg = B(BgColor);
            Fg = B(FgColor);
            Ink = B(InkColor);
            Mute = B(MuteColor);
            Dim = B(DimColor);
            Ok = B(OkColor);
            Bad = B(BadColor);
            Hair = B(Fade(FgColor, Dark ? 0.08 : 0.12));   // 细线、描边、悬停底色
            Wash = B(Fade(FgColor, Dark ? 0.06 : 0.07));   // 选中的行
            Fill = B(Fade(FgColor, Dark ? 0.12 : 0.13));   // 进度
            Rim = B(Fade(FgColor, 0.2));                   // 键帽描边、关着的开关
            Chip = B(C(Dark ? 0xFF2A2D32u : 0xFFD6D9DDu));
            Tile = B(C(Dark ? 0xFF24272Bu : 0xFFDADDE1u));
            Pop = B(C(Dark ? 0xFF25282Cu : 0xFFF4F5F6u));
            OkSoft = B(Fade(OkColor, 0.18));
            BadSoft = B(Fade(BadColor, 0.18));
        }

        public static Color Fade(Color c, double alpha)
        {
            return Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);
        }

        static Color C(uint argb) { return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb); }

        static Brush B(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    // 快捷面板和主界面共用的小工具
    static class Ui
    {
        public static readonly FontFamily TextFont = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        public static readonly FontFamily DisplayFont = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
        public static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas");
        public static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        public static readonly IEasingFunction Glide = new CubicEase { EasingMode = EasingMode.EaseInOut };
        public static readonly IEasingFunction Spring = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };   // 冲过头一点再弹回来

        public static Grid Columns(int star, params UIElement[] cells)
        {
            var g = new Grid();
            for (int i = 0; i < cells.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = i == star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                Grid.SetColumn(cells[i], i);
                g.Children.Add(cells[i]);
            }
            return g;
        }

        public static void AnimFrom(DependencyObject target, DependencyProperty prop, double from, double to, int ms, int delay = 0, IEasingFunction ease = null)
        {
            target.SetValue(prop, from);   // 延迟开始前先停在起点，免得闪一下
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = ease ?? Ease };
            ((IAnimatable)target).BeginAnimation(prop, a);
        }

        public static void AnimTo(DependencyObject target, DependencyProperty prop, double to, int ms, IEasingFunction ease = null)
        {
            ((IAnimatable)target).BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Ease });
        }

        public static void ScaleTo(ScaleTransform s, double to, int ms, IEasingFunction ease = null)
        {
            AnimTo(s, ScaleTransform.ScaleXProperty, to, ms, ease);
            AnimTo(s, ScaleTransform.ScaleYProperty, to, ms, ease);
        }

        public static void ScaleFrom(ScaleTransform s, double from, double to, int ms, int delay = 0, IEasingFunction ease = null)
        {
            AnimFrom(s, ScaleTransform.ScaleXProperty, from, to, ms, delay, ease);
            AnimFrom(s, ScaleTransform.ScaleYProperty, from, to, ms, delay, ease);
        }

        public static void Tint(SolidColorBrush b, Color to, int ms)
        {
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)));
        }

        public static void After(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); a(); };
            t.Start();
        }

        // 左右晃一下：这台发不了、没发出去
        public static void Shake(TranslateTransform shift)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(440) };
            double[] xs = { 0, -9, 8, -6, 4, 0 };
            for (int k = 0; k < xs.Length; k++) a.KeyFrames.Add(new LinearDoubleKeyFrame(xs[k], KeyTime.FromPercent(k / (double)(xs.Length - 1))));
            shift.BeginAnimation(TranslateTransform.XProperty, a);
        }

        public static void Pop(ScaleTransform scale)
        {
            ScaleFrom(scale, 0.3, 1, 420, 0, Spring);
        }

        // 不知道进度时在进度条上来回流动的光
        public static Brush Shimmer(Color c, out TranslateTransform shift)
        {
            shift = new TranslateTransform();
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), RelativeTransform = shift };
            b.GradientStops.Add(new GradientStop(Theme.Fade(c, 0), 0));
            b.GradientStops.Add(new GradientStop(Theme.Fade(c, 0.22), 0.5));
            b.GradientStops.Add(new GradientStop(Theme.Fade(c, 0), 1));
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever });
            return b;
        }

        public static void StopShimmer(ref TranslateTransform shift)
        {
            if (shift == null) return;
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift = null;
        }

        public static bool CopyText(string s)
        {
            try { Clipboard.SetText(s); return true; }
            catch (COMException) { return false; }   // 别的程序正开着剪贴板
        }

        public static ToolTip Tip(Theme th, string text)
        {
            return new ToolTip { Content = text, Background = th.Pop, Foreground = th.Fg, BorderBrush = th.Hair, BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 7), FontSize = 13 };
        }

        public static string Describe(List<string> paths)
        {
            return paths.Count == 1 ? System.IO.Path.GetFileName(paths[0]) : paths.Count + " 个项目";
        }

        public static string FormatSize(long n)
        {
            if (n < 1024) return n + " B";
            string[] units = { "KB", "MB", "GB", "TB" };
            double v = n;
            int u = -1;
            do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
            return v.ToString(v < 10 ? "0.0" : "0") + " " + units[u];
        }

        public static string When(DateTime t)
        {
            var today = DateTime.Today;
            if (t.Date == today) return "今天 " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
            return t.Year == today.Year ? t.ToString("M月d日 HH:mm") : t.ToString("yyyy年M月d日");
        }

        // 托盘、窗口、开始菜单共用的图标：编译时嵌进程序的 TailOrbit.ico（石墨底，本机和一颗卫星）
        static Stream IconStream()
        {
            return typeof(Ui).Assembly.GetManifestResourceStream("TailOrbit.ico");
        }

        public static Drawing.Icon TrayIcon()
        {
            using (var s = IconStream()) return new Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);
        }

        public static ImageSource WindowIcon()
        {
            var ms = new MemoryStream();   // 整个 ico 留在内存里，任务栏和 Alt+Tab 各挑合适的尺寸
            using (var s = IconStream()) s.CopyTo(ms);
            ms.Position = 0;
            return BitmapFrame.Create(ms);
        }
    }

    class Host
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        public readonly Theme Theme = new Theme();
        public readonly Drawing.Icon Icon = Ui.TrayIcon();
        QuickPanel panel;
        MainWindow main;
        HwndSource sink;
        WinForms.NotifyIcon tray;
        EventWaitHandle showSignal;

        public void Start(bool showMain)
        {
            panel = new QuickPanel(this);
            var p = new HwndSourceParameters("TailOrbitHotkey");
            p.ParentWindow = new IntPtr(-3);   // HWND_MESSAGE：只收消息的隐藏窗口
            p.WindowStyle = 0;
            p.Width = 0;
            p.Height = 0;
            sink = new HwndSource(p);
            sink.AddHook(OnMessage);

            tray = new WinForms.NotifyIcon { Icon = Icon, Text = "TailOrbit" };
            // 从托盘的“隐藏图标”区点开时，那个弹窗收起会抢焦点，等它收完再把窗口拿到前面
            Action open = () => Ui.After(250, ShowMain);
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("打开主界面", null, delegate { open(); });
            menu.Items.Add("退出", null, delegate { Quit(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) open(); };
            tray.Visible = true;

            if (!RegisterHotKey(sink.Handle, 1, 0x0002 | 0x0001 | 0x4000, 0x41))   // Ctrl+Alt+A，按住不连发
                Notify("Ctrl+Alt+A 被别的程序占用了", "从托盘图标打开主界面也能发文件。");

            // 别处再启动一次本程序（开始菜单、双击）时，把主界面拿出来
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { panel.Dispatcher.BeginInvoke(new Action(ShowMain)); }, null, Timeout.Infinite, false);
            if (showMain) ShowMain();
        }

        public void ShowMain()
        {
            if (main == null) main = new MainWindow(this);
            main.Open();
        }

        IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != 0x0312 || wParam.ToInt32() != 1) return IntPtr.Zero;   // WM_HOTKEY
            handled = true;
            IntPtr fg = GetForegroundWindow();   // 先记下按热键时在前台的窗口，再去读它的选中项
            panel.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (panel.IsActive) panel.HidePanel();
                else panel.ShowFor(Selection.FromWindow(fg));
            }));
            return IntPtr.Zero;
        }

        public void Notify(string title, string text)
        {
            tray.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.None);
        }

        public void Quit()
        {
            panel.Abort();
            if (main != null) main.Abort();
            UnregisterHotKey(sink.Handle, 1);
            tray.Visible = false;
            tray.Dispose();
            if (main != null) main.Quitting = true;
            Application.Current.Shutdown();
        }
    }
}
