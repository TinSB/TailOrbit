// 没装 Tailscale 时替用户装上：从 Tailscale 官网下载对应处理器的安装包，确认签名有效、签名者是 Tailscale Inc.，
// 再交给 msiexec 安装（会弹一次管理员确认）。装好打开 Tailscale，登录交给它自己的提示。
// 安装程序、主界面各页、快捷面板“未安装 Tailscale”的地方共用这一份；设置页的“更新 Tailscale”也走它（装新版就是覆盖安装）
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TailOrbit
{
    static class TailscaleSetup
    {
        static Task<string> running;

        public static bool Missing { get { return !File.Exists(Tailscale.Installed); } }

        // progress 收下载进度 0～1，开始安装时收到 -1；owner 是管理员确认框跟着的窗口。
        // 返回 null 表示装好了，否则是给用户看的原因。两处同时点只装一次
        public static Task<string> Install(IProgress<double> progress, IntPtr owner)
        {
            if (running == null || running.IsCompleted) running = Run(progress, owner);
            return running;
        }

        static async Task<string> Run(IProgress<double> progress, IntPtr owner)
        {
            string msi = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TailOrbit-" + Guid.NewGuid().ToString("N") + ".msi");
            try
            {
                try { await Task.Run(() => Download(msi, progress)); }
                catch (WebException) { return L.T("无法下载 Tailscale，请检查网络连接后重试。"); }
                catch (IOException) { return L.T("无法下载 Tailscale，请检查网络连接后重试。"); }   // 下到一半断线
                progress.Report(-1);
                return await Task.Run(() => Apply(msi, owner));
            }
            finally
            {
                try { File.Delete(msi); }
                catch (IOException) { }   // 杀毒软件还在扫：留在临时文件夹，系统会清
            }
        }

        static void Download(string to, IProgress<double> progress)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string arch = Arm64() ? "arm64" : "amd64";
            var req = (HttpWebRequest)WebRequest.Create("https://pkgs.tailscale.com/stable/tailscale-setup-latest-" + arch + ".msi");   // 302 到当前版本
            req.UserAgent = "TailOrbit/" + Setup.Version;
            req.ReadWriteTimeout = 30000;
            using (var res = req.GetResponse())
            using (var from = res.GetResponseStream())
            using (var file = File.Create(to))
            {
                long total = res.ContentLength, got = 0;
                int shown = -1;
                var buf = new byte[1 << 16];
                for (int n; (n = from.Read(buf, 0, buf.Length)) > 0; )
                {
                    file.Write(buf, 0, n);
                    got += n;
                    int pct = total > 0 ? (int)(got * 100 / total) : 0;
                    if (pct != shown) progress.Report((shown = pct) / 100.0);
                }
                if (total > 0 && got != total) throw new IOException("incomplete");
            }
        }

        static string Apply(string msi, IntPtr owner)
        {
            if (!Signed(msi)) return L.T("安装包未通过签名验证，已终止安装。请前往 tailscale.com 下载 Tailscale。");
            int code;
            try
            {
                var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "msiexec.exe"), "/i \"" + msi + "\" /qn /norestart") { UseShellExecute = true, Verb = "runas", ErrorDialogParentHandle = owner };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    code = p.ExitCode;
                }
            }
            catch (Win32Exception e) { return e.NativeErrorCode == 1223 ? L.T("已取消安装 Tailscale。") : L.T("Tailscale 安装失败：{0}", e.Message); }   // 1223：管理员确认点了“否”
            if (code == 1602) return L.T("已取消安装 Tailscale。");
            if (code == 1618) return L.T("另一个安装程序正在运行，请稍后重试。");
            if (code != 0 && code != 1641 && code != 3010) return L.T("Tailscale 安装失败（错误代码：{0}）。", code);   // 1641、3010：装好了，要重启才完全生效
            // 以当前用户打开 Tailscale（不能从提了权的 msiexec 里开）：没登录时它会提示登录
            try { Process.Start(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Tailscale.Installed), "tailscale-ipn.exe")); }
            catch (Win32Exception) { }   // 打不开不影响已经装好
            return null;
        }

        // Windows 验签名（文件没被改过、证书链可信、时间戳有效），再看签名者是不是 Tailscale Inc.
        static bool Signed(string file)
        {
            var info = new TrustFile { Size = Marshal.SizeOf(typeof(TrustFile)), Path = file };
            IntPtr p = Marshal.AllocHGlobal(info.Size);
            try
            {
                Marshal.StructureToPtr(info, p, false);
                var data = new TrustData { Size = Marshal.SizeOf(typeof(TrustData)), UiChoice = 2, UnionChoice = 1, File = p };   // 不弹界面，验一个文件
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");                                                    // WINTRUST_ACTION_GENERIC_VERIFY_V2
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
            }
            finally
            {
                Marshal.DestroyStructure(p, typeof(TrustFile));
                Marshal.FreeHGlobal(p);
            }
            try
            {
                var names = new X509Certificate2(X509Certificate.CreateFromSignedFile(file)).SubjectName.Format(true).Split('\n').Select(s => s.Trim()).ToList();
                return names.Contains("CN=Tailscale Inc.") && names.Contains("O=Tailscale Inc.");
            }
            catch (CryptographicException) { return false; }
        }

        // ARM 电脑上本程序可能是 x64 模拟运行，要问系统本身是什么处理器
        static bool Arm64()
        {
            ushort process, native;
            try { return IsWow64Process2(new IntPtr(-1), out process, out native) && native == 0xAA64; }
            catch (EntryPointNotFoundException) { return false; }   // Windows 10 1709 以前没有这个函数，也还没有 ARM 版 Windows 电脑
        }

        [DllImport("kernel32.dll")] static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
        [DllImport("wintrust.dll")] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TrustFile
        {
            public int Size;
            public string Path;
            public IntPtr Handle, KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TrustData
        {
            public int Size;
            public IntPtr PolicyCallback, SipClient;
            public int UiChoice, RevocationChecks, UnionChoice;
            public IntPtr File;
            public int StateAction;
            public IntPtr StateData, UrlReference;
            public int ProvFlags, UiContext;
            public IntPtr SignatureSettings;
        }

        // 主界面各页没装 Tailscale 时的整页提示：图标、标题、一句说明、安装按钮
        public static FrameworkElement View(Theme th, Action done)
        {
            var s = new StackPanel { Width = 320, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            s.Children.Add(new IconView("inbox", 32, th.Mute) { HorizontalAlignment = HorizontalAlignment.Center });
            s.Children.Add(new TextBlock { Text = L.T("未安装 Tailscale"), FontFamily = Ui.DisplayFont, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = th.Fg, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 6) });
            s.Children.Add(new TextBlock { Text = L.T("TailOrbit 需要 Tailscale 才能运行。"), FontSize = 13, LineHeight = 20, Foreground = th.Mute, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 22) });
            s.Children.Add(new InstallButton(th, done));
            return s;
        }
    }

    // “安装 Tailscale”：装好调 done；失败在按钮下面写原因，按钮变成“重试”
    class InstallButton : StackPanel
    {
        readonly ProgressButton button;
        readonly TextBlock note;
        readonly Action done;

        public InstallButton(Theme th, Action done)
        {
            this.done = done;
            button = new ProgressButton(th, L.T("安装 Tailscale"), Go) { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Center };
            note = new TextBlock { FontSize = 12, LineHeight = 18, Foreground = th.Bad, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            Children.Add(button);
            Children.Add(note);
        }

        async void Go()
        {
            note.Visibility = Visibility.Collapsed;
            button.Text = L.T("正在下载 Tailscale…");
            button.Progress(0);
            var w = Window.GetWindow(this);
            string why = await TailscaleSetup.Install(new Progress<double>(p => { if (p < 0) button.Text = L.T("正在安装 Tailscale…"); button.Progress(p); }), w == null ? IntPtr.Zero : new WindowInteropHelper(w).Handle);
            button.Progress(null);
            if (why == null)
            {
                button.Text = L.T("安装 Tailscale");
                done();
                return;
            }
            button.Text = L.T("重试");
            note.Text = why;
            note.Visibility = Visibility.Visible;
        }
    }

    // 主按钮，忙的时候就地变成进度条：知道进度从左往右填，不知道进度一道光来回扫
    class ProgressButton : Border
    {
        readonly Theme th;
        readonly TextBlock label;
        readonly SolidColorBrush face, ink;
        readonly Rectangle bar = new Rectangle { RenderTransformOrigin = new Point(0, 0.5), Opacity = 0 };
        readonly ScaleTransform filled = new ScaleTransform(0, 1);
        TranslateTransform sweep;

        public bool Busy { get; private set; }
        public string Text { set { label.Text = value; } }

        public ProgressButton(Theme th, string text, Action click)
        {
            this.th = th;
            face = new SolidColorBrush(th.FgColor);
            ink = new SolidColorBrush(th.InkColor);
            label = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ink, Margin = new Thickness(16, 0, 16, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            bar.RenderTransform = filled;
            var inside = new Grid { Children = { bar, label } };
            inside.SizeChanged += delegate { inside.Clip = new RectangleGeometry(new Rect(inside.RenderSize), 11, 11); };
            Height = 40;
            CornerRadius = new CornerRadius(11);
            Background = face;
            Cursor = Cursors.Hand;
            Child = inside;
            MouseEnter += delegate { if (!Busy) Opacity = 0.86; };
            MouseLeave += delegate { Opacity = 1; };
            MouseLeftButtonUp += delegate { if (!Busy) click(); };
        }

        // null：平常的按钮；小于 0：在忙、不知道进度；0～1：进度
        public void Progress(double? p)
        {
            if ((p != null) != Busy)
            {
                Busy = p != null;
                Opacity = 1;
                Cursor = Busy ? Cursors.Arrow : Cursors.Hand;
                Ui.Tint(face, Busy ? ((SolidColorBrush)th.Hair).Color : th.FgColor, 220);
                Ui.Tint(ink, Busy ? th.FgColor : th.InkColor, 220);
                if (!Busy)
                {
                    Ui.StopShimmer(ref sweep);
                    Ui.AnimTo(bar, OpacityProperty, 0, 220);
                    return;
                }
                filled.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                filled.ScaleX = 0;
                Ui.AnimTo(bar, OpacityProperty, 1, 160);
            }
            if (p == null) return;
            if (p < 0)
            {
                if (sweep == null) bar.Fill = Ui.Shimmer(this, th.FgColor, out sweep);
                Ui.AnimTo(filled, ScaleTransform.ScaleXProperty, 1, 240);
                return;
            }
            Ui.StopShimmer(ref sweep);
            bar.Fill = th.Fill;
            Ui.AnimTo(filled, ScaleTransform.ScaleXProperty, p.Value, 240);
        }
    }
}
