// TailOrbit：Tailscale 的 Windows 小工具——把文件拖到设备上，就用 Taildrop 发过去
// 主界面（MainWindow.cs、Star.cs）：点托盘图标或开始菜单打开。“星网”——本机在中间，设备按直连 / 中继 / 离线排在三圈上；
// 把文件拖到设备上就用 Taildrop 发过去；点设备弹出一圈按钮（Fan.cs：终端、网页、发文件、复制域名、掉线提醒）；
// 别的设备发来文件时光点流进本机（Inbox.cs）；左下是最近发过和收到的。退出在托盘菜单。
// 设置页（SettingsPage.cs）：本机在 Tailscale 里的名字和地址、出口节点、Tailscale 的常用开关；深浅色、开机自启、界面语言、两个剪贴板开关、收听本机声音；
// 检查更新、自动下载更新（Updates.cs，从 GitHub 发布页下载安装程序）。
// 待送（Outbox.cs）：离线的设备也能拖上去，文件先排着、在它旁边绕着转，对方一上线自动发过去。
// 自动出口节点（AutoExit.cs）：连上不信任的网络时自动改走设置页里选好的出口节点，回到信任的网络再关掉。
// 快捷面板（QuickPanel.cs）：按 Ctrl+Alt+A 弹出，把资源管理器里选中的文件（文件夹先打包成 zip）发给按下数字的那台设备。
// 剪贴板互通（ClipShare.cs）：自己的手机、Mac 读这台电脑的剪贴板，iPhone 把剪贴板发过来；按 Win+Alt+V 读 Mac 的剪贴板。
// 和 Mac 共用剪贴板（ClipSync.cs）：哪边复制，两边粘贴出来的都是它。
// 在手机上收听本机声音（SoundShare.cs）：自己的手机打开一个网页，就用手机和连着它的耳机听这台电脑正在放的声音，只差几十毫秒。
// 托盘菜单（TrayMenu.cs）：右键托盘图标，打开主界面、发文件、看收到的文件、开终端、两个剪贴板开关、退出。
// 环礁接口（Island.cs）：给屏幕顶上的通知岛用的本机接口——设备列表、发文件、收发进度和剪贴板动静；岛连着时不弹托盘通知。
// 安装、卸载（Setup.cs）：发布的 TailOrbit-Setup-<版本>.exe 就是本程序改了名，双击装到 %LOCALAPPDATA%\Programs\TailOrbit；从“设置 → 应用”卸载。
// 编译：powershell -ExecutionPolicy Bypass -File build.ps1（用 Windows 自带的 .NET Framework 4.8 编译器，只认 C# 5 语法）

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
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
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]

namespace TailOrbit
{
    static class Program
    {
        public const string ShowEventName = @"Local\TailOrbit.ShowMain";

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        [STAThread]
        static void Main(string[] args)
        {
            // 发布出去的 TailOrbit-Setup-*.exe、“设置 → 应用”里点卸载：走安装程序那一套（Setup.cs），不占“只开一个”的名额
            string setup = Setup.Mode(args);
            if (setup != null)
            {
                Environment.ExitCode = Setup.Run(setup, args);
                return;
            }
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
                // 进消息循环之前线程上还没有同步上下文，启动时就开始的后台活（清过期分享等）要靠它把结果送回界面线程。
                // 以前是托盘的 WinForms 菜单顺手装上的，菜单换成自己画的以后要自己装
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                L.Use(Settings.Language);
                new Host().Start(!args.Contains("--tray"));
                app.Run();
            }
        }
    }

    // 程序是 csc 直接编出来的 IL，第一次用到的代码都要在界面线程上现场编译（全部加起来约 0.35 秒），
    // 散落在第一次切页、弹菜单、按快捷键的那一下。启动后趁空闲先编好：每片不超过 2ms，排在界面所有活儿后面，动画照常跑
    static class Warm
    {
        // 先编最先会用到的：星网、设备菜单、各页，再是快捷面板和二维码，其余按原顺序
        static readonly string[] First = { "MainWindow", "Node", "Fan", "Kit", "Segmented", "NetPage", "SettingsPage", "SharePage", "DrivePage", "QuickPanel", "Qr" };
        const BindingFlags All = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static void Start(Action done)
        {
            // 列方法本身也要反射：做成按需取，和编译一起分片，不在第一片里一口气列完
            var todo = typeof(Warm).Assembly.GetTypes()
                .Where(t => !t.ContainsGenericParameters && !typeof(Delegate).IsAssignableFrom(t))
                .OrderBy(Rank)
                .SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All & ~BindingFlags.Static)))
                .Where(m => !m.IsAbstract && !m.ContainsGenericParameters && (m.Attributes & MethodAttributes.PinvokeImpl) == 0
                    && (m.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0)
                .GetEnumerator();
            Slice(todo, done);
        }

        static int Rank(Type t)
        {
            while (t.DeclaringType != null) t = t.DeclaringType;   // 闭包、async 状态机跟着它外面那个类
            int i = Array.IndexOf(First, t.Name);
            return i < 0 ? First.Length : i;
        }

        static void Slice(IEnumerator<MethodBase> todo, Action done)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var watch = Stopwatch.StartNew();
                bool more = true;
                while (watch.Elapsed.TotalMilliseconds < 2 && (more = todo.MoveNext()))
                {
                    try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(todo.Current.MethodHandle); }
                    catch (Exception) { }   // 个别方法提前编不了：跳过，用到时照常现场编
                }
                if (more) Slice(todo, done);
                else { todo.Dispose(); done(); }
            }));
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
        public string Ip = "", Name = "", Os = "", StableId = "", Dns = "";   // Dns：MagicDNS 全名，不带末尾的点
        public string Link = "";   // direct 直连 / relay 经中继 / "" 还不知道（只有正在通信的设备，status 才说得出走哪条路）
        public bool Online, Ssh;   // Ssh：对方开了 Tailscale SSH
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

    static partial class Tailscale
    {
        // 官方安装包装在 Program Files\Tailscale；那里没有就到 PATH 里找，还找不到就是没装（启动时抛 Win32Exception）。
        // 每次现查：程序开着的时候也可能刚装上
        public static readonly string Installed = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        public static string Exe { get { return File.Exists(Installed) ? Installed : "tailscale.exe"; } }

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
                    d.Dns = ((Get(peer, "DNSName") as string) ?? "").TrimEnd('.');
                    d.Ssh = Get(peer, "sshHostKeys") is object[];
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
            d.Dns = dns.TrimEnd('.');
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
            if (line.Contains("NeedsLogin") || line.Contains("not logged in")) return L.T("Tailscale 未登录");
            if (line.Contains("failed to connect") || line.Contains("is Tailscale running")) return L.T("Tailscale 未运行");
            return line.Length > 0 ? line : L.T("无法读取设备列表");
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
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                object windows = Call(shell, "Windows"), view = null;
                if (cls.ToString() == "CabinetWClass")
                {
                    // Win11 一个资源管理器窗口里可能开着几个标签页，只取正在显示的那个
                    IntPtr tab = FindWindowEx(fg, IntPtr.Zero, "ShellTabWindowClass", null);
                    int count = Convert.ToInt32(Get(windows, "Count"));
                    for (int i = 0; i < count; i++)
                    {
                        object w = Call(windows, "Item", i);
                        if (w == null || new IntPtr(Convert.ToInt64(Get(w, "HWND"))) != fg) continue;
                        if (tab != IntPtr.Zero && TabOf(w) != tab) continue;
                        view = Get(w, "Document");
                        break;
                    }
                }
                else if (cls.ToString() == "Progman" || cls.ToString() == "WorkerW")
                {
                    // FindWindowSW(ref loc, ref root, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH)：第 1、2、4 个参数按引用传
                    var args = new object[] { 0, null, 8, 0, 1 };
                    var byRef = new ParameterModifier(5);
                    byRef[0] = byRef[1] = byRef[3] = true;
                    object desk = windows.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod, null, windows, args, new[] { byRef }, null, null);
                    if (desk != null) view = Get(desk, "Document");
                }
                if (view != null)
                {
                    object items = Call(view, "SelectedItems");
                    int count = Convert.ToInt32(Get(items, "Count"));
                    for (int i = 0; i < count; i++) paths.Add((string)Get(Call(items, "Item", i), "Path"));
                }
            }
            catch (Exception) { }   // 资源管理器正忙或窗口正在关：当成没选中
            // 盘符本身（比如在“此电脑”里选了 C 盘）不发
            return paths.Where(p => File.Exists(p) || (Directory.Exists(p) && System.IO.Path.GetPathRoot(p) != p)).ToList();
        }

        // 直接走 IDispatch 调属性和方法（不用 dynamic：它第一次绑定要几百毫秒）
        static object Get(object o, string name)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
        }

        static object Call(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
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

        static int count;

        public readonly int Id = Interlocked.Increment(ref count);
        public Device Target;
        public List<string> Paths;
        public Action<double, string> Progress;
        public long TotalBytes;
        public bool Queued;   // 待送的（对方刚上线）：星网上先让绕着它转的卫星落进去
        // 给环礁看的：preparing 打包中 / sending / retrying 对方没回应、还在重试 / done / failed / canceled；Fraction 不知道进度时是 -1
        public string State = "preparing", Error = "";
        public double Fraction = Unknown;
        public static event Action<SendJob> Changed;   // 在发送的后台线程上触发
        readonly object sync = new object();
        bool ended;
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
            string result = "发送失败";   // 漏出异常时也按失败报给环礁
            try { return result = Deliver(); }
            finally
            {
                Error = string.IsNullOrEmpty(result) ? "" : result;
                Set(result == null ? 1 : Keep, result == null ? "done" : result == "" ? "canceled" : "failed", true);
            }
        }

        // 进度和文字给界面，状态顺手报给环礁
        void Report(double f, string text, string state)
        {
            Progress(f, text);
            Set(f, state, false);
        }

        // 结束（done、failed、canceled）以后，迟到的进度不再报
        void Set(double f, string state, bool end)
        {
            lock (sync)
            {
                if (ended) return;
                ended = end;
                State = state;
                if (f != Keep) Fraction = f;
                var h = Changed;
                if (h != null) h(this);
            }
        }

        string Deliver()
        {
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TailOrbit", Guid.NewGuid().ToString("N"));
            try
            {
                var files = new List<string>();
                foreach (var p in Paths)
                {
                    if (!Directory.Exists(p)) { files.Add(p); continue; }
                    Report(Unknown, L.T("正在打包…"), "preparing");
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
                Report(f, (int)(f * 100) + "%", "sending");
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
            Report(watch == null ? Unknown : 0, watch == null ? L.T("正在发送…") : "0%", "sending");
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
                else if (line.StartsWith("# warning:")) Report(Keep, L.T("对方无响应，正在重试…"), "retrying");
                else if (line.Length > 0 && !line.StartsWith("sending \"")) lastError = line;
            };
            cp.BeginOutputReadLine();
            cp.BeginErrorReadLine();
            if (canceled) Kill(cp);
            cp.WaitForExit();   // 不设时限：大文件发多久都等
            if (canceled) return "";
            return cp.ExitCode == 0 ? null : (lastError ?? "发送失败");   // 存原文进记录，显示时再按当前语言翻
        }
    }

    class Transfer
    {
        public DateTime Time;
        public string Target = "", Ip = "", Os = "", Result = "", Error = "";   // 收到的文件：Target 是发送方
        public List<string> Paths = new List<string>();
        public long Bytes;
        public bool In;      // 别的设备发来的
        public bool Fresh;   // 这次运行时收到、还没点开看过（不存盘）
    }

    // 传输记录（发出去的和收到的）：一行一条追加写进 history.jsonl，只增不删
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
                t.In = Text(o, "dir") == "in";
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
            Add(new Transfer
            {
                Time = DateTime.Now, Target = j.Target.Name, Ip = j.Target.Ip, Os = j.Target.Os, Paths = j.Paths, Bytes = j.TotalBytes,
                Result = result == null ? "ok" : result == "" ? "cancel" : "fail", Error = result ?? ""
            });
        }

        // 收到了文件；from 是 null 时没认出是哪台设备发的
        public static void RecordIn(Device from, List<string> paths, long bytes)
        {
            Add(new Transfer
            {
                Time = DateTime.Now, In = true, Fresh = true, Target = from != null ? from.Name : "", Ip = from != null ? from.Ip : "", Os = from != null ? from.Os : "",
                Paths = paths, Bytes = bytes, Result = "ok"
            });
        }

        static void Add(Transfer t)
        {
            Items.Add(t);
            var o = new Dictionary<string, object>
            {
                { "time", t.Time.ToString("o") }, { "target", t.Target }, { "ip", t.Ip }, { "os", t.Os },
                { "paths", t.Paths }, { "bytes", t.Bytes }, { "result", t.Result }, { "error", t.Error }
            };
            if (t.In) o["dir"] = "in";
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
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static string Command { get { return For(System.Reflection.Assembly.GetExecutingAssembly().Location); } }

        // 开机时怎么启动这一份：只待在托盘里
        public static string For(string exe)
        {
            return "\"" + exe + "\" --tray";
        }

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

    // 星网配色：灰阶分出层次，只有“送到了”和“没送到”带颜色；默认跟着系统的深浅色走，设置页里可以固定成浅色或深色
    class Theme
    {
        public readonly bool Dark;
        public readonly Color BgColor, FgColor, InkColor, MuteColor, DimColor, OkColor, BadColor;
        public readonly Brush Bg, Fg, Ink, Mute, Dim, Hair, Wash, Fill, Rim, Chip, Tile, Pop, Ok, Bad, OkSoft, BadSoft;

        public Theme()
        {
            Dark = WantDark;
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

        // 设置里选的深浅色；跟随系统时看 Windows 给应用用的那一项（和任务栏的深浅色是两个开关）
        public static bool WantDark
        {
            get
            {
                if (Settings.Theme != "auto") return Settings.Theme == "dark";
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return v is int && (int)v == 0;
            }
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
        // 西文用 Segoe UI，汉字、假名、韩文各用那个语言自己的系统字体（日文不能拿中文字形顶替）
        public static FontFamily TextFont { get { return Font("Segoe UI Variable Text, Segoe UI"); } }
        public static FontFamily DisplayFont { get { return Font("Segoe UI Variable Display, Segoe UI"); } }
        static readonly Dictionary<string, FontFamily> fonts = new Dictionary<string, FontFamily>();

        static FontFamily Font(string latin)
        {
            string cjk = L.Code == "zh-Hant" ? "Microsoft JhengHei UI" : L.Code == "ja" ? "Yu Gothic UI" : L.Code == "ko" ? "Malgun Gothic" : "Microsoft YaHei UI";
            string name = latin + ", " + cjk;
            FontFamily f;
            if (!fonts.TryGetValue(name, out f)) fonts[name] = f = new FontFamily(name);
            return f;
        }
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
            Drop(target, prop);
            target.SetValue(prop, from);   // 延迟开始前先停在起点，免得闪一下
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = ease ?? Ease };
            ((IAnimatable)target).BeginAnimation(prop, a);
        }

        // 已经在终点就不再起一段动画，直接定在那儿：主界面每 10 秒刷新一次，多数时候什么都没变，
        // 以前每次都给每台设备起一串 0.3～0.7 秒的动画，窗口一直在白渲染（10-27 实测）
        public static void AnimTo(DependencyObject target, DependencyProperty prop, double to, int ms, IEasingFunction ease = null)
        {
            Drop(target, prop);
            if ((double)target.GetValue(prop) == to) { Settle(target, prop, to); return; }
            ((IAnimatable)target).BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Ease });
        }

        static void Settle(DependencyObject target, DependencyProperty prop, object value)
        {
            ((IAnimatable)target).BeginAnimation(prop, null);
            target.SetValue(prop, value);
        }

        // 一直循环的动画（RepeatBehavior.Forever）一律走 Loop / Unloop：只在 owner 看得见时跑（换页、收起窗口、被换下来就停，
        // 再看得见从头跑），停的时候把时钟真停掉。只 BeginAnimation(属性, null) 是把时钟从属性上摘下来，时钟本身还在
        // WPF 的计时树上空转，直到下一次完整的垃圾回收，这期间整个窗口每秒白渲染几十帧（10-27 实测：去过分享页以后，
        // 五页空闲时都还在每秒渲染 36～80 帧）。AnimTo、AnimFrom、Tint 换掉循环时也会先把它停掉
        class Looping
        {
            public UIElement Owner;
            public AnimationTimeline Anim;
            public AnimationClock Clock;
            public DependencyPropertyChangedEventHandler Seen;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Looping>> loops =
            new System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Looping>>();

        public static void Loop(UIElement owner, DependencyObject target, DependencyProperty prop, AnimationTimeline a)
        {
            Unloop(target, prop);
            var l = new Looping { Owner = owner, Anim = a };
            l.Seen = (s, e) => Run(target, prop, l, (bool)e.NewValue);
            owner.IsVisibleChanged += l.Seen;
            loops.GetOrCreateValue(target)[prop] = l;
            Run(target, prop, l, owner.IsVisible);
        }

        public static void Unloop(DependencyObject target, DependencyProperty prop)
        {
            Drop(target, prop);
            ((IAnimatable)target).BeginAnimation(prop, null);
        }

        // 不再循环：停掉时钟、不再跟着 owner 起停；属性上的动画由调用的地方接着换掉或撤掉（换掉时从当前值接着走）
        static void Drop(DependencyObject target, DependencyProperty prop)
        {
            Dictionary<DependencyProperty, Looping> m;
            Looping l;
            if (!loops.TryGetValue(target, out m) || !m.TryGetValue(prop, out l)) return;
            m.Remove(prop);
            l.Owner.IsVisibleChanged -= l.Seen;
            if (l.Clock != null) l.Clock.Controller.Stop();
        }

        static void Run(DependencyObject target, DependencyProperty prop, Looping l, bool on)
        {
            if (on == (l.Clock != null)) return;
            if (on)
            {
                l.Clock = l.Anim.CreateClock();
                ((IAnimatable)target).ApplyAnimationClock(prop, l.Clock);
                return;
            }
            ((IAnimatable)target).BeginAnimation(prop, null);
            l.Clock.Controller.Stop();
            l.Clock = null;
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
            Drop(b, SolidColorBrush.ColorProperty);
            if (b.Color == to) { Settle(b, SolidColorBrush.ColorProperty, to); return; }
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)));
        }

        // 起外部程序（浏览器、资源管理器、终端）：放到后台线程，别让界面线程等它几十毫秒、把正在播的动画卡住。
        // 没装、打不开（比如没有默认浏览器）就算了，和以前一样不弹错
        public static void Launch(Action start)
        {
            var t = new Thread(() =>
            {
                try { start(); }
                catch (System.ComponentModel.Win32Exception) { }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);   // ShellExecute 要单线程套间
            t.Start();
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
        public static Brush Shimmer(UIElement owner, Color c, out TranslateTransform shift)
        {
            shift = new TranslateTransform();
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5), RelativeTransform = shift };
            b.GradientStops.Add(new GradientStop(Theme.Fade(c, 0), 0));
            b.GradientStops.Add(new GradientStop(Theme.Fade(c, 0.22), 0.5));
            b.GradientStops.Add(new GradientStop(Theme.Fade(c, 0), 1));
            Loop(owner, shift, TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever });
            return b;
        }

        public static void StopShimmer(ref TranslateTransform shift)
        {
            if (shift == null) return;
            Unloop(shift, TranslateTransform.XProperty);
            shift = null;
        }

        public static bool CopyText(string s)
        {
            try { Clipboard.SetText(s); return true; }
            catch (COMException) { return false; }   // 别的程序正开着剪贴板
        }

        // 弹出层（Placement=Bottom）里的卡片右边对齐到 anchor 右边时的横向偏移。弹出层对齐的是卡片本身，不算给阴影留的外边距；
        // Windows 设成“惯用右手”（平板设置里那项）时，它拿卡片的右边去对齐，偏移就是 0（10-27 在 175% 缩放下两种设置都实测过）
        public static double RightUnder(FrameworkElement anchor, double width)
        {
            return SystemParameters.MenuDropAlignment ? 0 : anchor.ActualWidth - width;
        }

        public static ToolTip Tip(Theme th, string text)
        {
            return new ToolTip { Content = text, Background = th.Pop, Foreground = th.Fg, BorderBrush = th.Hair, BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 7), FontSize = 13 };
        }

        public static string Describe(List<string> paths)
        {
            return paths.Count == 1 ? System.IO.Path.GetFileName(paths[0]) : L.T("{0} 个项目", paths.Count);
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
            if (t.Date == today) return L.T("今天 {0}", t.ToString("HH:mm"));
            if (t.Date == today.AddDays(-1)) return L.T("昨天 {0}", t.ToString("HH:mm"));
            var f = L.Culture.DateTimeFormat;   // 日期按界面语言的习惯写：中文 10月27日，英文 October 27
            return t.Year == today.Year ? t.ToString(f.MonthDayPattern + " HH:mm", L.Culture) : t.ToString(f.ShortDatePattern, L.Culture);
        }

        // 程序图标有深浅两份（make-icon.ps1 画的，编译时嵌进程序）：深底白点的 TailOrbit.ico、浅底黑点的 TailOrbit-light.ico，
        // 都是本机和一颗卫星。任务栏、托盘、开始菜单跟着 Windows 的深浅色，程序里面跟着界面的深浅色
        public static Stream IconStream(bool light)
        {
            return typeof(Ui).Assembly.GetManifestResourceStream(light ? "TailOrbit-light.ico" : "TailOrbit.ico");
        }

        // Windows 自己（任务栏、开始菜单）是不是浅色：和程序界面的深浅色是两个开关。老版本 Windows 没有这一项，任务栏总是深色
        public static bool SystemLight
        {
            get
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
                return v is int && (int)v == 1;
            }
        }

        public static Drawing.Icon TrayIcon()
        {
            using (var s = IconStream(SystemLight)) return new Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);
        }

        public static ImageSource WindowIcon()
        {
            var ms = new MemoryStream();   // 整个 ico 留在内存里，任务栏和 Alt+Tab 各挑合适的尺寸
            using (var s = IconStream(SystemLight)) s.CopyTo(ms);
            ms.Position = 0;
            return BitmapFrame.Create(ms);
        }

        // 程序里画的小图标（左上角、托盘菜单）：挑不小于显示像素的最小那一版，不拿 256 的大图硬缩
        public static BitmapSource IconFrame(double px, bool light)
        {
            using (var s = IconStream(light))
            {
                var frames = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderBy(f => f.PixelWidth).ToList();
                return frames.FirstOrDefault(f => f.PixelWidth >= px - 0.5) ?? frames.Last();
            }
        }
    }

    class Host
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        public Theme Theme = new Theme();   // 换深浅色时整个换成新的一份，窗口跟着重建
        QuickPanel panel;
        MainWindow main;
        HwndSource sink;
        WinForms.NotifyIcon tray;
        TrayMenu menu, spare;
        string spareFor;
        bool quitting, trayLight;
        EventWaitHandle showSignal, quitSignal;
        bool sweeping;
        readonly List<SendJob> loose = new List<SendJob>();   // 环礁上发起、没在星网上画的发送：退出时取消
        public bool HotkeyOk { get; private set; }   // Ctrl+Alt+A 注册上了：托盘菜单里给它画键帽
        public event Action Toggled;                 // 托盘菜单拨了剪贴板开关：设置页那两项跟着变
        Action balloonClick;                         // 点托盘通知时做什么（比如打开设置页）；每条通知各管各的

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

            tray = new WinForms.NotifyIcon { Icon = Ui.TrayIcon(), Text = "TailOrbit" };
            trayLight = Ui.SystemLight;
            // Windows 换了深浅色：托盘、任务栏、开始菜单上的图标跟着换；界面选了跟随系统的也跟着换
            SystemEvents.UserPreferenceChanged += (s, e) => { if (e.Category == UserPreferenceCategory.General) panel.Dispatcher.BeginInvoke(new Action(Restyle)); };
            Task.Run(() => Setup.Restyle());
            tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenFromTray(); };
            tray.MouseUp += (s, e) => { if (e.Button == WinForms.MouseButtons.Right) ShowMenu(); };
            tray.BalloonTipClicked += delegate { var a = balloonClick; balloonClick = null; if (a != null) a(); };
            tray.Visible = true;

            HotkeyOk = RegisterHotKey(sink.Handle, 1, 0x0002 | 0x0001 | 0x4000, 0x41);   // Ctrl+Alt+A，按住不连发
            if (!HotkeyOk) Notify(L.T("Ctrl+Alt+A 已被其他程序占用"), L.T("你也可以从托盘图标打开主界面来发送文件。"));
            // Win+Alt+V 读 Mac 的剪贴板（Win+V 是本机的剪贴板历史）；被占了就只能从设备菜单里读
            if (RegisterHotKey(sink.Handle, 2, 0x0008 | 0x0001 | 0x4000, 0x56)) ClipShare.Hotkey = "Win+Alt+V";
            AddClipboardFormatListener(sink.Handle);   // 本机剪贴板一变就收到通知：和 Mac 共用剪贴板

            // 别处再启动一次本程序（开始菜单、双击）时，把主界面拿出来
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { panel.Dispatcher.BeginInvoke(new Action(ShowMain)); }, null, Timeout.Infinite, false);
            // 装新版、卸载时安装程序叫它退出：照点托盘“退出”那样收拾干净（停掉 ssh 等子进程）
            quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Setup.QuitEventName);
            ThreadPool.RegisterWaitForSingleObject(quitSignal, delegate { panel.Dispatcher.BeginInvoke(new Action(Quit)); }, null, Timeout.Infinite, true);

            // 分享出去的文件由本程序提供；公开的到点关掉（程序没开时到点的，下次启动先关）
            FileServer.Start();
            ClipShare.Start(this);
            SoundShare.Start(this);
            ClipSync.Start(this);
            Island.Start(this);
            var sweep = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            sweep.Tick += delegate { Sweep(); };
            sweep.Start();
            Sweep();
            Inbox.Start();
            Alerts.Start(this);
            Outbox.Start(this);
            Updates.Start(this);
            AutoExit.Start(this);
            if (showMain) ShowMain();
            // 开屏动画放完、手头没事了再预编译；编完顺手把快捷面板的窗口句柄建好、备好托盘菜单、把主界面建好并读一遍设备，
            // 第一次按 Ctrl+Alt+A、第一次右键托盘、第一次点开主界面都少等一截
            Ui.After(2500, () => Warm.Start(() =>
            {
                new WindowInteropHelper(panel).EnsureHandle();
                SpareMenu();
                if (main != null) return;
                main = new MainWindow(this);
                main.Preload();
            }));
        }

        // 改别的系统设置也会来这一下：深浅色真变了才换
        // 界面选了跟随系统时也跟着换（有文件正在发就等发完）
        void Restyle()
        {
            if (quitting) return;
            bool light = Ui.SystemLight;
            if (light != trayLight)
            {
                trayLight = light;
                var old = tray.Icon;
                tray.Icon = Ui.TrayIcon();
                old.Dispose();
                if (main != null) main.Icon = Ui.WindowIcon();
                Task.Run(() => Setup.Restyle());
            }
            if (Theme.WantDark == Theme.Dark) return;
            if (Busy) Ui.After(5000, Restyle);
            else Rebuild(main != null ? main.PageKey : "home");
        }

        void Sweep()
        {
            if (sweeping) return;
            sweeping = true;
            Task.Run(() => Shares.Sweep()).ContinueWith(t =>
            {
                sweeping = false;
                if (!t.IsFaulted && t.Result.Count > 0) Notify(L.T("公开分享已到期关闭"), string.Join(L.T("、"), t.Result));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 右键托盘图标：每次现做一份菜单（语言、开关都是当下的）
        void ShowMenu()
        {
            if (menu != null) menu.Dismiss();
            var m = spare;
            spare = null;
            if (m == null || spareFor != MenuState())
            {
                if (m != null) m.Close();
                m = new TrayMenu(this);
            }
            menu = m;
            m.Closed += delegate { if (menu == m) menu = null; SpareMenu(); };
            m.Open();
        }

        // 右键时现建菜单窗口、头一回显示要一百多毫秒：空闲时先备好下一份
        void SpareMenu()
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (spare != null || quitting) return;
                spare = new TrayMenu(this);
                spareFor = MenuState();
                spare.Prepare();
            }));
        }

        // 菜单建的时候读的那些：变了备好的那份就作废
        string MenuState()
        {
            return L.Code + "|" + Theme.Dark + "|" + HotkeyOk + "|" + Settings.ClipOn + "|" + Settings.SyncMac;
        }

        // 从托盘的“隐藏图标”区点开时，那个弹窗收起会抢焦点，等它收完再把窗口拿到前面
        public void OpenFromTray()
        {
            Ui.After(250, ShowMain);
            if (main == null) main = new MainWindow(this);   // 反正要等这 250ms：先把窗口建好，到点只剩显示
        }

        // 托盘菜单里的“发文件”：弹出快捷面板，先点设备再选文件（同样等托盘弹窗收完）
        public void SendFromTray()
        {
            Ui.After(250, () => panel.ShowFor(new List<string>()));
        }

        public void RaiseToggled()
        {
            var h = Toggled;
            if (h != null) h();
        }

        // 换界面语言：主界面原地换成新语言的一份（停在设置页、位置大小不变），快捷面板也换；托盘菜单每次现做，不用管
        public void SetLanguage(string setting)
        {
            if (Busy) { Notify(L.T("暂不切换语言"), L.T("有文件正在发送，请在发送完成后再切换。")); return; }
            Settings.Language = setting;
            L.Use(setting);
            Rebuild("settings");
        }

        // mode："auto" 跟随系统、"light"、"dark"
        public void SetTheme(string mode)
        {
            if (Busy) { Notify(L.T("暂不切换外观"), L.T("有文件正在发送，请在发送完成后再切换外观。")); return; }
            Settings.Theme = mode;
            if (Theme.WantDark != Theme.Dark) Rebuild("settings");
        }

        bool Busy { get { return (main != null && main.Busy) || panel.Busy; } }

        // 换了语言或深浅色：快捷面板换成新的一份，主界面原地换成新的一份（停在 page 那页、位置大小不变）；托盘菜单每次现做，不用管。
        // 调用前已经挡掉了正在发送的情况
        void Rebuild(string page)
        {
            Theme = new Theme();
            panel.Close();
            panel = new QuickPanel(this);
            if (main == null) return;
            var old = main;
            main = new MainWindow(this);
            main.Replace(old, page);
            old.Quitting = true;
            old.Close();
        }

        public void ShowMain()
        {
            if (main == null) main = new MainWindow(this);
            main.Open();
        }

        // 一页跳到另一页（比如网络页里“去看”打不开的分享）
        public void ShowPage(string key)
        {
            ShowMain();
            main.Switch(key);
        }

        // 剪贴板经过了这台设备：星网上光点流一小段，环礁上也亮一下
        public void ClipFlow(string ip, bool inward)
        {
            if (main != null) main.ClipFlow(ip, inward);
            Island.Clip(ip, inward);
        }

        // 环礁上拖来的文件、等到对方上线的待送：主界面建好、那台设备在星网上又没在发，就照常画光点；不然在后台发。都照常记进传输记录
        public void Deliver(SendJob j)
        {
            if (main != null && main.Send(j)) return;
            j.Progress = (f, text) => { };
            loose.Add(j);
            Task.Run(() => j.Run()).ContinueWith(t =>
            {
                loose.Remove(j);
                string result = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                History.Record(j, result);
                if (result == null) Notify(L.T("已发送到 {0}", j.Target.Name), Ui.Describe(j.Paths), true);
                else if (result != "") Notify(L.T("发送到 {0} 失败", j.Target.Name), L.T(result), true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x031D)   // WM_CLIPBOARDUPDATE：等系统发完这条通知再去读剪贴板
            {
                panel.Dispatcher.BeginInvoke(new Action(() => { ClipSync.Copied(); ClipShare.Copied(); }));
                return IntPtr.Zero;
            }
            if (msg != 0x0312) return IntPtr.Zero;   // WM_HOTKEY
            if (menu != null) menu.Dismiss();
            if (wParam.ToInt32() == 2)
            {
                handled = true;
                panel.Dispatcher.BeginInvoke(new Action(ClipShare.PullAny));
                return IntPtr.Zero;
            }
            if (wParam.ToInt32() != 1) return IntPtr.Zero;
            handled = true;
            IntPtr fg = GetForegroundWindow();   // 先记下按热键时在前台的窗口，再去读它的选中项
            panel.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (panel.IsActive) panel.HidePanel();
                else panel.ShowFor(Selection.FromWindow(fg));
            }));
            return IntPtr.Zero;
        }

        // island：这件事环礁上也看得到（收发文件、剪贴板经过），岛连着时右下角就不再弹；
        // 岛接手了通知时，其余的也交给岛弹，右下角都不弹
        public void Notify(string title, string text, bool island = false, Action click = null)
        {
            if (island && Island.Connected) return;
            if (Island.Notice(title, text, click)) return;
            balloonClick = click;
            tray.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.None);
        }

        public void Quit()
        {
            quitting = true;
            Outbox.Stop();
            panel.Abort();
            if (main != null) main.Abort();
            foreach (var j in loose) j.Cancel();
            UnregisterHotKey(sink.Handle, 1);
            UnregisterHotKey(sink.Handle, 2);
            RemoveClipboardFormatListener(sink.Handle);
            ClipSync.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (main != null) main.Quitting = true;
            Application.Current.Shutdown();
        }
    }
}
