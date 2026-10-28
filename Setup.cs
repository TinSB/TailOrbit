// 安装和卸载。发布出去的 TailOrbit-Setup-<版本>.exe 就是本程序换了个名字：双击它（或带 --install）把自己装到
// %LOCALAPPDATA%\Programs\TailOrbit——不用管理员权限，放进开始菜单，登记到“设置 → 应用”，从那里卸载（--uninstall）。
// 换新版就再运行一次安装：先叫开着的那个退出，再换掉程序，设置和记录都留着。
// --quiet 不弹窗口（给脚本用），装好开在托盘里，再加 --open 打开主界面（程序自己更新时用）；卸载时再加 --purge，连设置、传输记录一起删
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace TailOrbit
{
    static class Setup
    {
        public const string QuitEventName = @"Local\TailOrbit.Quit";   // 装新版、卸载时叫开着的那个退出
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TailOrbit";
        static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string Dir = Path.Combine(Local, "Programs", "TailOrbit");
        public static readonly string Exe = Path.Combine(Dir, "TailOrbit.exe");
        static readonly string Data = Path.Combine(Local, "TailOrbit");
        static readonly string Link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TailOrbit.lnk");
        static readonly string Self = Assembly.GetExecutingAssembly().Location;

        public static string Version
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        // 这次启动是来装、来卸的吗；null 表示都不是，照常运行
        public static string Mode(string[] args)
        {
            if (args.Contains("--uninstall")) return "uninstall";
            if (args.Contains("--install") || Path.GetFileNameWithoutExtension(Self).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0) return "install";
            return null;
        }

        // 装着的版本；没装是 null
        public static string Installed
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    return k != null && File.Exists(Exe) ? k.GetValue("DisplayVersion") as string : null;
            }
        }

        // 原来开着开机自启（不管指向哪一份）：换新版时照旧
        public static bool AutostartWanted
        {
            get
            {
                if (Installed == null) return true;   // 第一次装默认开
                using (var k = Registry.CurrentUser.OpenSubKey(Autostart.RunKey)) return k != null && k.GetValue("TailOrbit") != null;
            }
        }

        public static int Run(string mode, string[] args)
        {
            if (args.Contains("--quiet"))
            {
                string why = mode == "install" ? Install(AutostartWanted) : Uninstall(args.Contains("--purge"));
                // 装好打开新版；没装成也把原来那份开回来（开着的已经被叫退出了）
                if (mode == "install" && File.Exists(Exe)) Process.Start(Exe, args.Contains("--open") ? "" : "--tray");
                else if (why == null) RemoveProgram();
                return why == null ? 0 : 1;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            L.Use(Settings.Language);
            var w = new SetupWindow(mode == "install");
            app.Run(w);
            if (w.Removed) RemoveProgram();
            return 0;
        }

        // 装上（或换成这一版）；返回 null 表示装好了，否则是原因
        public static string Install(bool autostart)
        {
            try
            {
                Stop();
                Directory.CreateDirectory(Dir);
                if (!Same(Self, Exe)) Copy(Self, Exe);
                WriteIcons();
                try { Shortcut(); }
                catch (COMException) { }   // 有的公司电脑禁了 WScript.Shell：没有开始菜单那一项，程序照样能用
                Register();
                using (var k = Registry.CurrentUser.CreateSubKey(Autostart.RunKey))
                {
                    if (autostart) k.SetValue("TailOrbit", Autostart.For(Exe));
                    else k.DeleteValue("TailOrbit", false);
                }
                return null;
            }
            catch (IOException e) { return e.Message; }
            catch (UnauthorizedAccessException e) { return e.Message; }
        }

        // 卸掉；purge 时连设置、传输记录、分享列表一起删。程序文件本身等这个进程退出后再删（RemoveProgram）
        public static string Uninstall(bool purge)
        {
            try
            {
                Stop();
                // 在 tailscale serve 上挂的转发（文件分享、剪贴板互通）撤掉；Tailscale 没在跑就撤不了，留下的转发也只是打不开
                try
                {
                    foreach (var e in Shares.Load() ?? new List<ServeEntry>()) if (e.Mine != null) Shares.Stop(e);
                    ClipShare.Turn(false);
                }
                catch (Win32Exception) { }   // 没装 Tailscale
                using (var k = Registry.CurrentUser.OpenSubKey(Autostart.RunKey, true))
                    if (k != null && string.Equals(k.GetValue("TailOrbit") as string, Autostart.For(Exe), StringComparison.OrdinalIgnoreCase)) k.DeleteValue("TailOrbit");
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                File.Delete(Link);
                if (purge) { if (Directory.Exists(Data)) Directory.Delete(Data, true); }
                else if (File.Exists(Path.Combine(Data, "island.json"))) File.Delete(Path.Combine(Data, "island.json"));   // 桌面小组件看它知道 TailOrbit 在不在
                return null;
            }
            catch (IOException e) { return e.Message; }
            catch (UnauthorizedAccessException e) { return e.Message; }
        }

        // 删程序文件。正在运行的就是装着的那份时删不掉，交给 cmd 等这个进程退出两秒后再删；只删这一个文件和空了的文件夹
        static void RemoveProgram()
        {
            if (Same(Self, Exe))
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + Exe + "\" \"" + IconFile(false) + "\" \"" + IconFile(true) + "\" & rd \"" + Dir + "\"") { CreateNoWindow = true, UseShellExecute = false });
                return;
            }
            try
            {
                File.Delete(Exe);
                File.Delete(IconFile(false));
                File.Delete(IconFile(true));
                Directory.Delete(Dir);
            }
            catch (IOException) { }   // 文件夹里还有别的东西：留着
        }

        // 叫开着的 TailOrbit 退出（它会顺手停掉自己开的 ssh 这些）；8 秒还没退、或者是不认这个信号的旧版，就连子进程一起强关
        static void Stop()
        {
            var me = Process.GetCurrentProcess();
            var running = Process.GetProcessesByName("TailOrbit").Where(p => p.Id != me.Id && p.SessionId == me.SessionId).ToList();
            if (running.Count == 0) return;
            EventWaitHandle quit;
            bool asked = EventWaitHandle.TryOpenExisting(QuitEventName, out quit);
            if (asked) using (quit) quit.Set();
            foreach (var p in running)
                using (p)
                    if (!asked || !p.WaitForExit(8000))
                        using (var k = Process.Start(new ProcessStartInfo("taskkill", "/f /t /pid " + p.Id) { CreateNoWindow = true, UseShellExecute = false })) k.WaitForExit(5000);
        }

        static void Copy(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) { if (i == 15) throw; Thread.Sleep(200); }   // 刚退出的程序文件可能还被占着一小会儿
            }
        }

        static void Shortcut()
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            var s = shell.CreateShortcut(Link);
            s.TargetPath = Exe;
            s.WorkingDirectory = Dir;
            s.Description = "TailOrbit";
            s.IconLocation = IconFile(Ui.SystemLight) + ",0";
            s.Save();
        }

        // 开始菜单那一项、“设置 → 应用”里的图标要跟着 Windows 的深浅色，可程序文件本身只能带一份图标（深色那份）：
        // 两份都放到程序旁边，快捷方式和登记的图标指向对得上的那份。两份文件名不同，换过去时系统才会重新读，不用缓存里的旧图
        static string IconFile(bool light)
        {
            return Path.Combine(Dir, light ? "TailOrbit-light.ico" : "TailOrbit-dark.ico");
        }

        static void WriteIcons()
        {
            foreach (bool light in new[] { false, true })
                using (var s = Ui.IconStream(light))
                using (var f = File.Create(IconFile(light))) s.CopyTo(f);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int eventId, int flags, string item1, IntPtr item2);

        // 装好的这一份开着时：Windows 的深浅色和开始菜单那一项的图标对不上就换过去（启动时、Windows 换深浅色时各看一次）
        public static void Restyle()
        {
            if (!Same(Self, Exe) || !File.Exists(Link)) return;
            try
            {
                string icon = IconFile(Ui.SystemLight);
                if (!File.Exists(icon)) WriteIcons();
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                var s = shell.CreateShortcut(Link);
                if (string.Equals((string)s.IconLocation, icon + ",0", StringComparison.OrdinalIgnoreCase)) return;
                s.IconLocation = icon + ",0";
                s.Save();
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true)) if (k != null) k.SetValue("DisplayIcon", icon);
                SHChangeNotify(0x2000, 0x0005, Link, IntPtr.Zero);   // SHCNE_UPDATEITEM、SHCNF_PATHW：叫开始菜单重读这一项
            }
            catch (COMException e) { Log.Write(e); }                 // 公司电脑禁了 WScript.Shell
            catch (IOException e) { Log.Write(e); }
            catch (UnauthorizedAccessException e) { Log.Write(e); }
        }

        // 登记到“设置 → 应用”：那里显示名字、版本、大小，点“卸载”就运行 --uninstall
        static void Register()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "TailOrbit");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "TinSB");
                k.SetValue("DisplayIcon", IconFile(Ui.SystemLight));
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + Exe + "\" --uninstall --quiet");
                k.SetValue("URLInfoAbout", "https://github.com/TinSB/TailOrbit");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        static bool Same(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
    }

    // 安装、卸载的小窗：和托盘菜单一样的卡片，按住上半截能拖
    class SetupWindow : Window
    {
        const double Pad = 24;
        readonly Theme th = new Theme();
        readonly bool install;
        readonly Toggle option, tailscale;   // tailscale：没装 Tailscale 时问要不要一起装
        readonly ProgressButton go;          // 安装用；卸载用红框的 action
        readonly Border action, optionRow;
        readonly TextBlock label, note;
        readonly ScaleTransform pop = new ScaleTransform(0.96, 0.96);
        bool busy, placed;   // placed：TailOrbit 已经装好，只剩装 Tailscale

        public bool Removed { get; private set; }   // 卸掉了：窗口关后再删程序文件

        public SetupWindow(bool install)
        {
            this.install = install;
            string installed = Setup.Installed;
            Title = install ? "TailOrbit" : L.T("卸载 TailOrbit");
            Icon = Ui.WindowIcon();
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var close = Kit.IconButton(th, "close", L.T("关闭"), Cancel, 32);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, -10, -10, 0);

            var head = new StackPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 22) };
            head.Children.Add(new Image { Source = Ui.IconFrame(128, !th.Dark), Width = 64, Height = 64 });
            var title = new TextBlock { Text = install ? "TailOrbit" : L.T("卸载 TailOrbit"), FontFamily = Ui.DisplayFont, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = th.Fg, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 4) };
            head.Children.Add(title);
            // 换新版时写“旧版 → 新版”
            string version = install && installed != null && installed != Setup.Version ? installed + "  →  " + Setup.Version : install ? Setup.Version : installed ?? "";
            if (version != "") head.Children.Add(new TextBlock { Text = version, FontSize = 13, Foreground = th.Mute, HorizontalAlignment = HorizontalAlignment.Center });
            head.MouseLeftButtonDown += delegate { DragMove(); };

            // 安装时问开机自启、没装 Tailscale 再问要不要一起装；卸载时问要不要连设置和记录一起删。整行都能点
            option = new Toggle(th, install && Setup.AutostartWanted);
            optionRow = Row(install ? L.T("开机时自动启动") : L.T("同时删除设置和传输记录"), null, option);
            var rows = new StackPanel();
            rows.Children.Add(optionRow);
            if (install && TailscaleSetup.Missing)
            {
                tailscale = new Toggle(th, true);
                rows.Children.Add(Row(L.T("同时安装 Tailscale"), L.T("TailOrbit 需要 Tailscale 才能运行，将从 Tailscale 官网下载并安装。"), tailscale));
            }

            string verb = !install ? L.T("卸载") : installed == null ? L.T("安装") : installed == Setup.Version ? L.T("重新安装") : L.T("更新");
            if (install) action = go = new ProgressButton(th, verb, Go);
            else
            {
                action = Kit.Danger(th, verb, Go);
                label = (TextBlock)action.Child;
            }
            note = new TextBlock { FontSize = 13, LineHeight = 19, Foreground = th.Bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 2, 0), Visibility = Visibility.Collapsed };

            var stack = new StackPanel { Width = 300 };
            stack.Children.Add(close);
            stack.Children.Add(head);
            stack.Children.Add(rows);
            stack.Children.Add(action);
            stack.Children.Add(note);
            var card = new Border
            {
                Margin = new Thickness(Pad), Padding = new Thickness(26, 22, 26, 26), CornerRadius = new CornerRadius(18), Background = th.Pop, BorderBrush = th.Hair, BorderThickness = new Thickness(1),
                Child = stack, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = pop,
                Effect = new DropShadowEffect { Color = Colors.Black, Direction = 270, ShadowDepth = 12, BlurRadius = 36, Opacity = th.Dark ? 0.45 : 0.18 }
            };
            TextElement.SetFontFamily(card, Ui.TextFont);
            Content = card;
            Opacity = 0;
            Loaded += delegate
            {
                Ui.AnimTo(this, OpacityProperty, 1, 180);
                Ui.ScaleTo(pop, 1, 320, Ui.Spring);
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Cancel(); };
        }

        Border Row(string title, string caption, Toggle t)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = th.Fg, TextWrapping = TextWrapping.Wrap });
            if (caption != null) text.Children.Add(new TextBlock { Text = caption, FontSize = 12, LineHeight = 17, Foreground = th.Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            var row = new Border { MinHeight = 48, Padding = new Thickness(14, 8, 10, 8), CornerRadius = new CornerRadius(12), Background = th.Tile, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 12), Child = Ui.Columns(0, text, t) };
            row.MouseLeftButtonUp += delegate
            {
                if (busy || (placed && t != tailscale)) return;   // 装好以后开机自启已经定了，只剩 Tailscale 这一行能改
                t.Set(!t.On, true);
                if (placed) go.Text = t.On ? L.T("重试") : L.T("完成");
            };
            return row;
        }

        async void Go()
        {
            if (busy) return;
            busy = true;
            note.Visibility = Visibility.Collapsed;
            if (!install)
            {
                label.Text = L.T("正在卸载…");
                action.Opacity = 0.6;
                bool purge = option.On;
                string err = await Task.Run(() => Setup.Uninstall(purge));
                if (err != null) { Fail(L.T("卸载未完成：{0}", err)); return; }
                Removed = true;
                label.Text = L.T("已卸载");
                Ui.After(1200, Close);
                return;
            }
            if (!placed)
            {
                go.Text = L.T("正在安装…");
                go.Progress(-1);
                bool on = option.On;
                string err = await Task.Run(() => Setup.Install(on));
                if (err != null) { Fail(L.T("安装失败：{0}", err)); return; }
                placed = true;
                Ui.AnimTo(optionRow, OpacityProperty, 0.45, 200);
            }
            if (tailscale != null && tailscale.On && TailscaleSetup.Missing)
            {
                go.Text = L.T("正在下载 Tailscale…");
                go.Progress(0);
                string err = await TailscaleSetup.Install(new Progress<double>(p => { if (p < 0) go.Text = L.T("正在安装 Tailscale…"); go.Progress(p); }), new WindowInteropHelper(this).Handle);
                if (err != null) { Fail(err); return; }
            }
            Finish();
        }

        void Fail(string why)
        {
            busy = false;
            if (install)
            {
                go.Progress(null);
                go.Text = L.T("重试");
            }
            else
            {
                action.Opacity = 1;
                label.Text = L.T("重试");
            }
            note.Text = why;
            note.Visibility = Visibility.Visible;
        }

        // 打开装好的那一份：它的主界面一出来就说明装好了
        void Finish()
        {
            try { Process.Start(Setup.Exe); }
            catch (Win32Exception) { }
            Close();
        }

        // 关窗：TailOrbit 已经装好（只是 Tailscale 没装上、或者不装了）就照常打开它
        void Cancel()
        {
            if (busy) return;
            if (placed) Finish();
            else Close();
        }
    }
}
