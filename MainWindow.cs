// 主界面「星网」：本机在正中，设备按连接方式排在三圈上——内圈直连、中圈经中继、外圈离线。
// 把文件拖到设备上就发；点设备弹出一圈按钮（终端、网页、发文件、复制域名、掉线提醒），发送中再点是取消；点名字复制 IP；
// 别的设备发来文件时，光点从那台设备流进本机、本机上走进度；点中间的本机打开收到的文件。
// 左下是最近发过和收到的文件（点一下在文件夹里显示，拖到设备上再发一次），右下提示快捷面板的热键；关窗只是收到托盘。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TailOrbit
{
    class MainWindow : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // 每台设备上次探到的连接方式
        class Probe
        {
            public string Link = "";
            public DateTime At = DateTime.MinValue;
            public bool Running;
        }

        readonly Host host;
        readonly Theme th;
        public bool Quitting;
        readonly Grid root = new Grid();
        readonly Canvas field = new Canvas { ClipToBounds = true };
        readonly Canvas links = new Canvas { IsHitTestVisible = false };
        readonly Canvas stars = new Canvas();
        readonly Canvas overlay = new Canvas { IsHitTestVisible = false };
        readonly Canvas menuLayer = new Canvas();
        readonly FlowLayer flow, flowIn;

        // 分页：星网是首页，其余几页第一次切过去时才建
        static readonly string[] PageKeys = { "home", "share", "net", "drive", "device" };
        readonly Grid home = new Grid();
        readonly Dictionary<string, FrameworkElement> pages = new Dictionary<string, FrameworkElement>();
        Segmented tabs;
        string current = "home";
        readonly Ellipse[] rings = new Ellipse[3];
        readonly ScaleTransform[] ringScale = { new ScaleTransform(1, 1), new ScaleTransform(1, 1), new ScaleTransform(1, 1) };
        readonly double[] radius = new double[3];
        Point o;   // 本机所在的圆心
        readonly Dictionary<string, Node> nodes = new Dictionary<string, Node>();   // 按 IP
        readonly Dictionary<string, Probe> probes = new Dictionary<string, Probe>();

        readonly TranslateTransform selfAt = new TranslateTransform(), selfLabelShift = new TranslateTransform();
        readonly ScaleTransform selfPop = new ScaleTransform(1, 1);
        SolidColorBrush selfFace, selfRim, selfInk;
        IconView selfIcon, selfHover;
        Grid selfLabel;
        TextBlock selfName, selfIp;
        FrameworkElement setupBox;  // 没装 Tailscale：本机下面的“安装 Tailscale”
        FrameworkElement absent;    // 没装 Tailscale：其余各页整页盖上安装提示
        bool missing;
        Device me;
        int selfCopy;
        // 收件：本机外圈走进度，圆盘里是百分比，收完打个勾
        Arc selfArc;
        TextBlock selfPct;
        readonly Run selfPctNum = new Run("0");
        IconView selfCheck;
        Ellipse selfRipple;
        readonly ScaleTransform selfRippleScale = new ScaleTransform(1, 1);
        readonly RotateTransform selfSpin = new RotateTransform();
        string selfState = "idle";   // idle 平时 / recv 收件中 / done 收到了
        bool selfHovering, selfKnown, selfSpinning;
        int selfStamp;
        Node inFrom;   // 正往本机发文件的设备

        // 点设备弹出的菜单
        Fan fan;
        Node pending;   // 点了、正在敲网页端口，敲完再弹
        readonly Dictionary<string, KeyValuePair<DateTime, int>> webPorts = new Dictionary<string, KeyValuePair<DateTime, int>>();   // 按 IP：上次敲到的网页端口（0 是没有）
        readonly Dictionary<string, Task<int>> webProbes = new Dictionary<string, Task<int>>();   // 按 IP：正在敲的

        readonly StackPanel recent = new StackPanel { Orientation = Orientation.Horizontal };
        readonly TranslateTransform recentShift = new TranslateTransform();
        readonly List<Border> keys = new List<Border>();
        IconView maxIcon;

        bool lifted, locked;   // 正拖着文件在窗口上 / 小方块吸在了某台设备上
        Node aim;
        Canvas chip;
        TranslateTransform chipAt;
        ScaleTransform chipScale;
        SolidColorBrush chipFill, chipInk;
        readonly DispatcherTimer leave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };

        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        int loads;
        bool fresh;   // 刚打开窗口：这次读到的设备按开屏的节奏依次出场

        public MainWindow(Host host)
        {
            this.host = host;
            th = host.Theme;
            Title = "TailOrbit";
            Icon = Ui.WindowIcon();
            Width = 1140;
            Height = 760;
            MinWidth = 900;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = th.Bg;
            Foreground = th.Fg;
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;
            AllowDrop = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 44, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });

            flow = new FlowLayer(th.Fg, () => o);
            flowIn = new FlowLayer(th.Fg, () => o, true);
            for (int i = 0; i < 3; i++)
            {
                rings[i] = new Ellipse { Stroke = th.Hair, StrokeThickness = 1, StrokeDashCap = PenLineCap.Round, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = ringScale[i] };
                field.Children.Add(rings[i]);
            }
            field.Children.Add(links);
            field.Children.Add(flow);
            field.Children.Add(flowIn);
            field.Children.Add(BuildSelf());
            field.Children.Add(stars);
            field.Children.Add(menuLayer);

            recent.HorizontalAlignment = HorizontalAlignment.Left;
            recent.VerticalAlignment = VerticalAlignment.Bottom;
            recent.Margin = new Thickness(36, 0, 0, 30);
            recent.RenderTransform = recentShift;

            home.Children.Add(field);
            home.Children.Add(recent);
            home.Children.Add(Keys());
            pages["home"] = home;
            root.Background = th.Bg;
            root.Children.Add(home);
            absent = new Grid { Background = th.Bg, Visibility = Visibility.Collapsed, Children = { TailscaleSetup.View(th, Installed) } };
            root.Children.Add(absent);   // 各页第一次建时插在星网后面，都压在它下面
            root.Children.Add(Brand());
            root.Children.Add(TitleBar());
            root.Children.Add(Tabs());
            root.Children.Add(overlay);
            Content = root;

            field.SizeChanged += delegate { Layout(false); };
            History.Changed += OnHistory;
            Inbox.Changed += OnInbox;
            Closed += delegate { History.Changed -= OnHistory; Inbox.Changed -= OnInbox; };   // 换语言时旧窗口关掉，别再收事件
            refresh.Tick += delegate { if (WindowState != WindowState.Minimized) Reload(); };
            IsVisibleChanged += delegate
            {
                if (IsVisible) return;
                refresh.Stop();
                CloseFan();
                var page = CurrentPage;
                if (page != null) page.Hidden();
            };
            leave.Tick += delegate
            {
                leave.Stop();
                dragData = null;
                EndDrag(null);
                var page = CurrentPage;
                if (page != null) page.DragEnd();
            };
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            // 在窗口里从一个元素拖到另一个元素也会先“离开”，过一会儿还没收到拖动才算真的拖出去了（或按了 Esc）
            DragLeave += delegate { leave.Start(); };
            Drop += OnDrop;
            PreviewMouseDown += (s, e) =>
            {
                // 点在菜单外面就收起；点的是菜单那台设备时交给设备自己（再点一下是收起）
                var hit = current == "home" ? HitTest(e.GetPosition(field)) : null;
                if (fan != null && !fan.IsMouseOver && hit != fan.Node) CloseFan();
                if (pending != null && hit != pending) { pending.Mark(false); pending = null; }
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && fan != null) { CloseFan(); e.Handled = true; }
                // Ctrl+1～5 换页
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key < Key.D1 + PageKeys.Length) { Switch(PageKeys[e.Key - Key.D1]); e.Handled = true; }
            };
            SourceInitialized += delegate
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int on = th.Dark ? 1 : 0, round = 2;
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);      // 深色模式下窗口边框也用深色
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);   // Win11 的圆角窗口（最大化时系统自己改回直角）
            };
            StateChanged += delegate { FitState(); };
            RenderRecent(null);
        }

        // 自绘标题栏的窗口最大化时会超出屏幕一圈边框的宽度，往里收回来；中间按钮换成“还原”
        void FitState()
        {
            root.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            maxIcon.Show(WindowState == WindowState.Maximized ? "restore" : "max");
        }

        public void Open()
        {
            bool hidden = !IsVisible;
            if (hidden) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            if (hidden)
            {
                Intro();
                var page = CurrentPage;
                if (page != null) page.Shown();
            }
            Reload();
            refresh.Start();
        }

        // 程序起来、空闲时先读一遍设备，把星网上的设备建好：第一次打开主界面时开屏动画不会被中途建设备卡住
        public void Preload()
        {
            Reload();
        }

        // 换语言时顶替旧窗口：同样的位置、大小，直接停在指定那页，不放开屏动画
        public void Replace(MainWindow old, string page)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var r = old.RestoreBounds.IsEmpty ? new Rect(old.Left, old.Top, old.Width, old.Height) : old.RestoreBounds;
            Left = r.Left;
            Top = r.Top;
            Width = r.Width;
            Height = r.Height;
            bool shown = old.IsVisible;
            if (shown) WindowState = old.WindowState == WindowState.Minimized ? WindowState.Normal : old.WindowState;
            Switch(page, false);
            FitState();   // 显示前就设好的最大化不会发 StateChanged
            if (!shown) return;
            Show();
            Activate();
            var p = CurrentPage;
            if (p != null) p.Shown();
            Reload();
            refresh.Start();
        }

        public bool Busy
        {
            get { return nodes.Values.Any(n => n.Job != null); }
        }

        public void Abort()
        {
            foreach (var n in nodes.Values) if (n.Job != null) n.Job.Cancel();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (Quitting) return;
            e.Cancel = true;   // 关窗只是收到托盘，热键和快捷面板照常能用
            Hide();
        }

        // ---------- 开屏 ----------

        // 三圈由内向外荡开，本机弹出来，线一条条长出去、设备依次亮起，最后把快捷键按一遍。
        // 整体晚 Lead 毫秒才动：窗口刚显示的头一帧要排版、画整个窗口（实测 30ms 上下），先停在起点等它画完，
        // 不然一开头就跳过去一截（圈是快出慢停的，头 30ms 就走完了一成多）
        const int Lead = 90;

        void Intro()
        {
            fresh = true;
            for (int i = 0; i < 3; i++)
            {
                // 两圈是虚线、点线，缩放时每帧都要重新描一遍：荡开的这一秒先当成贴图缩放，放完换回矢量（终态不变）
                rings[i].CacheMode = new BitmapCache(1.0);
                Ui.AnimFrom(rings[i], OpacityProperty, 0, 1, 900, Lead + i * 110, MainEase.Settle);
                Ui.ScaleFrom(ringScale[i], 0.55, 1, 900, Lead + i * 110, MainEase.Settle);
            }
            Ui.After(Lead + 1150, () => { foreach (var r in rings) r.CacheMode = null; });
            Ui.ScaleFrom(selfPop, 0, 1, 640, Lead, Ui.Spring);
            Ui.AnimFrom(selfLabel, OpacityProperty, 0, 1, 400, Lead + 300);
            Appear(nodes.Values.ToList(), Lead);
            Ui.AnimFrom(recent, OpacityProperty, 0, 1, 500, Lead + 1100);
            Ui.AnimFrom(recentShift, TranslateTransform.YProperty, 8, 0, 500, Lead + 1100);
            Ui.After(Lead + 1250, PressKeys);
        }

        void Appear(List<Node> list, int lead = 0)
        {
            int on = 0, off = 0;
            foreach (var n in list.OrderBy(x => x.Dev.Online ? 0 : 1).ThenBy(x => x.Dev.Name, StringComparer.OrdinalIgnoreCase))
                n.Appear(n.Dev.Online ? on++ : off++, o, true, lead);
        }

        // ---------- 本机 ----------

        FrameworkElement BuildSelf()
        {
            var self = new Canvas { RenderTransform = selfAt };
            var halo = new Ellipse { Width = 124, Height = 124, Stroke = th.Hair, StrokeThickness = 1, IsHitTestVisible = false };
            Canvas.SetLeft(halo, -62);
            Canvas.SetTop(halo, -62);
            selfArc = new Arc(th.FgColor, 62) { Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = selfSpin };   // 收件进度画在这圈上
            Canvas.SetLeft(selfArc, -68);
            Canvas.SetTop(selfArc, -68);
            selfRipple = new Ellipse { Width = 100, Height = 100, Stroke = th.Ok, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = selfRippleScale };
            Canvas.SetLeft(selfRipple, -50);
            Canvas.SetTop(selfRipple, -50);
            selfFace = new SolidColorBrush(th.FgColor);
            selfRim = new SolidColorBrush(Theme.Fade(th.DimColor, 0));
            selfInk = new SolidColorBrush(th.InkColor);
            selfIcon = new IconView("monitor", 36, selfInk);
            selfHover = new IconView("inbox", 32, selfInk);
            selfCheck = new IconView("check", 38, selfInk, 2.4);
            selfPct = new TextBlock { FontFamily = Ui.DisplayFont, FontSize = 25, FontWeight = FontWeights.SemiBold, Foreground = selfInk, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            selfPct.Inlines.Add(selfPctNum);
            selfPct.Inlines.Add(new Run("%") { FontSize = 13 });
            Typography.SetNumeralAlignment(selfPct, FontNumeralAlignment.Tabular);
            var disc = new Grid { Width = 96, Height = 96, Background = Brushes.Transparent, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = selfPop };
            disc.Children.Add(new Ellipse { Fill = selfFace, Stroke = selfRim, StrokeThickness = 1.5 });
            foreach (var e in new FrameworkElement[] { selfIcon, selfHover, selfPct, selfCheck })
            {
                e.RenderTransformOrigin = new Point(0.5, 0.5);
                e.RenderTransform = new ScaleTransform(1, 1);
                e.Tag = true;
                if (e != selfIcon) Node.Swap(e, false, true);
                disc.Children.Add(e);
            }
            Canvas.SetLeft(disc, -48);
            Canvas.SetTop(disc, -48);
            // 鼠标放上去变成“收件”，点一下打开收到的文件
            disc.MouseEnter += delegate { selfHovering = true; PaintSelf(); };
            disc.MouseLeave += delegate { selfHovering = false; PaintSelf(); };
            disc.MouseLeftButtonUp += delegate
            {
                Ui.ScaleFrom(selfPop, 0.92, 1, 360, 0, Ui.Spring);
                Ui.Launch(() => Process.Start("explorer.exe", "shell:Downloads"));   // Tailscale 在 Windows 上把收到的文件存在“下载”里
            };
            WindowChrome.SetIsHitTestVisibleInChrome(disc, true);

            selfLabel = Node.Label(th, th.Mute, selfLabelShift, out selfName, out selfIp);
            Canvas.SetLeft(selfLabel, -140);
            Canvas.SetTop(selfLabel, 64);
            selfLabel.Children[0].MouseLeftButtonUp += delegate { CopySelf(); };
            // 外面衬一圈和底色一样的底：圈线从旁边经过时在按钮前断开
            var setup = new InstallButton(th, Installed);
            setupBox = new Grid { Width = 280, Visibility = Visibility.Collapsed, Children = { new Border { Background = th.Bg, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(18), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Child = setup } } };
            Canvas.SetLeft(setupBox, -140);
            Canvas.SetTop(setupBox, 92);
            WindowChrome.SetIsHitTestVisibleInChrome(setupBox, true);
            self.Children.Add(halo);
            self.Children.Add(selfRipple);
            self.Children.Add(selfArc);
            self.Children.Add(disc);
            self.Children.Add(selfLabel);
            self.Children.Add(setupBox);
            return self;
        }

        // 圆盘里显示什么：收完是 ✓；收件中是百分比（不知道总大小时照常显示电脑）；平时鼠标放上去是“收件”
        void PaintSelf()
        {
            string show = selfState == "done" ? "check" : selfState == "recv" ? (selfKnown ? "pct" : "icon") : selfHovering ? "hover" : "icon";
            Node.Swap(selfIcon, show == "icon");
            Node.Swap(selfHover, show == "hover");
            Node.Swap(selfPct, show == "pct");
            Node.Swap(selfCheck, show == "check");
        }

        // ---------- 收件 ----------

        void OnInbox(Incoming s)
        {
            Node from = null;
            if (s.From != null && !s.Done) nodes.TryGetValue(s.From, out from);
            if (from != inFrom)
            {
                if (inFrom != null) { inFrom.Receiving(false); flowIn.Stop(inFrom, false); }
                inFrom = from;
                if (from != null) { from.Receiving(true); flowIn.Start(from); }
            }
            if (s.Done) SelfFinish(s.Ok);
            else SelfProgress(s.Fraction);
        }

        // 剪贴板经过这台设备：读走的光点往外流一小段；发来的往里流，第一颗到的时候本机圆盘一缩一弹。正在传文件的那台不打扰
        public void ClipFlow(string ip, bool inward)
        {
            Node n;
            if (!IsVisible || WindowState == WindowState.Minimized || ip == null || !nodes.TryGetValue(ip, out n) || n.Busy || n == inFrom) return;
            var layer = inward ? flowIn : flow;
            layer.Start(n);
            Ui.After(600, () => layer.Stop(n, false));
            if (inward) Ui.After((int)(Math.Max(100, (n.Center - o).Length) / 0.22), () => Ui.ScaleFrom(selfPop, 0.9, 1, 460, 0, Ui.Spring));   // 光点每毫秒走 0.22px
        }

        void SelfProgress(double f)
        {
            if (selfState != "recv")
            {
                selfState = "recv";
                selfStamp++;
                selfArc.Brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                selfArc.Brush.Color = th.FgColor;
                selfArc.BeginAnimation(Arc.ValueProperty, null);
                selfArc.Value = 0;
                Ui.AnimTo(selfArc, OpacityProperty, 1, 200);
                Ui.ScaleFrom(selfPop, 0.9, 1, 460, 0, Ui.Spring);   // 一缩一弹：有东西进来了
            }
            selfKnown = f >= 0;
            SelfSpin(!selfKnown);
            if (selfKnown)
            {
                selfPctNum.Text = ((int)(f * 100)).ToString();
                selfArc.BeginAnimation(Arc.ValueProperty, new DoubleAnimation(Math.Max(f, 0.004), TimeSpan.FromMilliseconds(240)));
            }
            PaintSelf();
        }

        // 收完：进度环合上变绿、荡开一圈、打个勾，一会儿恢复；对方取消了就直接收起
        void SelfFinish(bool ok)
        {
            if (selfState != "recv") return;
            SelfSpin(false);
            int k = ++selfStamp;
            if (ok)
            {
                selfState = "done";
                selfArc.BeginAnimation(Arc.ValueProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
                Ui.Tint(selfArc.Brush, th.OkColor, 300);
                Ui.Tint(selfFace, th.OkColor, 300);
                Ui.AnimFrom(selfRipple, OpacityProperty, 0.9, 0, 1000, 0, MainEase.Settle);
                Ui.ScaleFrom(selfRippleScale, 1, 2.4, 1000, 0, MainEase.Settle);
            }
            PaintSelf();
            Ui.After(ok ? 1500 : 1, () =>
            {
                if (k != selfStamp) return;
                selfState = "idle";
                Ui.Tint(selfFace, th.FgColor, 300);
                Ui.AnimTo(selfArc, OpacityProperty, 0, 300);
                PaintSelf();
            });
        }

        // 不知道总大小：一小段弧绕着转
        void SelfSpin(bool on)
        {
            if (on == selfSpinning) return;
            selfSpinning = on;
            selfArc.BeginAnimation(Arc.ValueProperty, null);
            selfArc.Value = on ? 0.22 : 0;
            if (on) Ui.Loop(selfArc, selfSpin, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
            else Ui.Unloop(selfSpin, RotateTransform.AngleProperty);
        }

        // Tailscale 没在跑、没登录：本机变成空心圈，打个红色感叹号，名字的位置写原因
        void ShowSelf(string error)
        {
            bool ok = error == null;
            Ui.Tint(selfFace, ok ? th.FgColor : Theme.Fade(th.FgColor, 0), 300);
            Ui.Tint(selfRim, ok ? Theme.Fade(th.DimColor, 0) : th.DimColor, 300);
            Ui.Tint(selfInk, ok ? th.InkColor : th.BadColor, 300);
            selfIcon.Show(ok ? "monitor" : "alert");
            selfName.Text = ok ? (me != null ? me.Name : "") : error;
            selfIp.Text = me != null ? me.Ip : "";
        }

        // 没装 Tailscale：星网上本机下面出“安装 Tailscale”，其余各页整页盖上安装提示；装上了都收起
        void ShowMissing()
        {
            if (missing && setupBox.Visibility != Visibility.Visible)
            {
                setupBox.Visibility = Visibility.Visible;
                Ui.AnimFrom(setupBox, OpacityProperty, 0, 1, 300);
            }
            else if (!missing) setupBox.Visibility = Visibility.Collapsed;
            absent.Visibility = missing && current != "home" ? Visibility.Visible : Visibility.Collapsed;
        }

        // 刚装好 Tailscale：重新读设备，当前这一页也重新加载
        void Installed()
        {
            missing = false;
            ShowMissing();
            Reload();
            ReloadPage();
        }

        void ReloadPage()
        {
            var p = CurrentPage;
            if (p != null) { p.Hidden(); p.Shown(); }
        }

        void CopySelf()
        {
            if (me == null || !Ui.CopyText(me.Ip)) return;
            int k = ++selfCopy;
            Ui.AnimTo(selfLabelShift, TranslateTransform.YProperty, -20, 350, Node.Nudge);
            Ui.After(1500, () => { if (k == selfCopy) Ui.AnimTo(selfLabelShift, TranslateTransform.YProperty, 0, 350, Node.Nudge); });
        }

        // ---------- 设备 ----------

        void Reload()
        {
            int n = ++loads;
            Task.Run(() => Tailscale.Load()).ContinueWith(t => { if (n == loads) Apply(t.IsFaulted ? null : t.Result); }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Apply(Snapshot snap)
        {
            bool was = missing;
            missing = snap == null && TailscaleSetup.Missing;
            string error = snap == null ? (missing ? L.T("未安装 Tailscale") : L.T("无法读取设备列表")) : snap.Error;
            me = snap == null ? null : snap.Self;
            ShowSelf(error);
            ShowMissing();
            if (was && !missing) ReloadPage();   // 在别处装上了 Tailscale：盖着的那一页重新加载
            var list = error == null ? snap.Devices : new List<Device>();
            var added = new List<Node>();
            var seen = new HashSet<string>();
            foreach (var d in list)
            {
                seen.Add(d.Ip);
                d.Link = LinkOf(d);
                Node n;
                if (nodes.TryGetValue(d.Ip, out n)) n.Update(d);
                else added.Add(n = AddNode(d));
                n.Ring = RingOf(d);
                n.SetWatched(Settings.Watched(d), false);
            }
            foreach (var n in nodes.Values.Where(x => !seen.Contains(x.Dev.Ip) && x.Job == null).ToList()) RemoveNode(n);
            // 在线设备的网页端口先在后台敲好，点设备时菜单马上就能弹
            var probe = webProbes.Count > 0 ? null : list.FirstOrDefault(x => x.Online && !webPorts.ContainsKey(x.Ip));
            if (probe != null) ProbeWeb(probe.Ip);   // 一次只敲一台：每台要并发敲九个端口，一起敲会占满线程池，拖文件发送也要用它
            bool intro = fresh;
            fresh = false;
            Layout(true);
            if (intro) Appear(added);
            else foreach (var n in added) n.Appear(0, o, false);
        }

        static string RingOf(Device d)
        {
            return !d.Online ? "off" : d.Link == "direct" ? "direct" : "relay";
        }

        // 连接方式：正在通信的以 status 为准；没在通信的用 5 分钟内探到的结果，过期了就再探一次
        string LinkOf(Device d)
        {
            Probe p;
            probes.TryGetValue(d.Ip, out p);
            if (d.Link != "")
            {
                if (p == null) probes[d.Ip] = p = new Probe();
                p.Link = d.Link;
                p.At = DateTime.Now;
                return d.Link;
            }
            if (!d.Online) return "";
            if (p == null || (DateTime.Now - p.At).TotalMinutes >= 5) StartProbe(d.Ip);
            return p != null ? p.Link : "";
        }

        void StartProbe(string ip)
        {
            Probe p;
            if (!probes.TryGetValue(ip, out p)) probes[ip] = p = new Probe();
            if (p.Running) return;
            p.Running = true;
            Task.Run(() => Tailscale.Probe(ip)).ContinueWith(t =>
            {
                p.Running = false;
                p.At = DateTime.Now;
                p.Link = t.IsFaulted ? "" : t.Result;
                Node n;
                if (!nodes.TryGetValue(ip, out n) || !n.Dev.Online) return;
                n.Dev.Link = p.Link;
                string ring = RingOf(n.Dev);
                if (ring == n.Ring) return;
                n.Ring = ring;   // 探到直连：沿着自己那条线滑进内圈
                Layout(true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        Node AddNode(Device d)
        {
            var n = new Node(th, d);
            n.Click = OnNodeClick;
            n.Copy = x => { if (Ui.CopyText(x.Dev.Ip)) x.Copied(); };
            nodes[d.Ip] = n;
            links.Children.Add(n.Link);
            stars.Children.Add(n.Root);
            return n;
        }

        void RemoveNode(Node n)
        {
            if (fan != null && fan.Node == n) CloseFan();
            if (pending == n) pending = null;
            if (inFrom == n) { flowIn.Stop(n, true); inFrom = null; }
            nodes.Remove(n.Dev.Ip);
            n.Vanish(() => { links.Children.Remove(n.Link); stars.Children.Remove(n.Root); });
        }

        // 点设备：发送中是取消；不然弹出菜单（再点一下收起）。在线的先敲一下常见的网页端口，有开着的就多一个“网页”
        void OnNodeClick(Node n)
        {
            if (n.Busy) { n.Job.Cancel(); return; }
            bool same = fan != null && fan.Node == n;
            CloseFan();
            if (same || pending == n) { if (pending == n) { n.Mark(false); pending = null; } return; }
            if (pending != null) pending.Mark(false);
            n.Mark(true);
            string ip = n.Dev.Ip;
            if (!n.Dev.Online || WebKnown(ip)) { OpenFan(n, n.Dev.Online ? webPorts[ip].Value : 0); return; }
            // 还没敲过（刚打开窗口那一下）：等它敲完，最多 0.4 秒
            pending = n;
            ProbeWeb(ip).ContinueWith(t =>
            {
                if (pending != n) return;
                pending = null;
                OpenFan(n, t.IsFaulted ? 0 : t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 敲过的结果：找到网页的记 2 分钟，没找到的只记 30 秒（刚开机的设备网页服务可能还没起来）
        bool WebKnown(string ip)
        {
            KeyValuePair<DateTime, int> known;
            return webPorts.TryGetValue(ip, out known) && (DateTime.Now - known.Key).TotalSeconds < (known.Value > 0 ? 120 : 30);
        }

        Task<int> ProbeWeb(string ip)
        {
            Task<int> t;
            if (webProbes.TryGetValue(ip, out t)) return t;
            t = Task.Run(() => Web.Find(ip, 400));
            webProbes[ip] = t;
            t.ContinueWith(x =>
            {
                webProbes.Remove(ip);
                webPorts[ip] = new KeyValuePair<DateTime, int>(DateTime.Now, x.IsFaulted ? 0 : x.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return t;
        }

        void OpenFan(Node n, int webPort)
        {
            if (n.Gone || current != "home") { n.Mark(false); return; }
            fan = new Fan(th, menuLayer, n, o, new Size(field.ActualWidth, field.ActualHeight), MenuFor(n, webPort), f => { if (fan == f) CloseFan(); else f.Close(); });
        }

        void CloseFan()
        {
            if (fan == null) return;
            fan.Node.Mark(false);
            fan.Close();
            fan = null;
        }

        // 在线：[终端] [读剪贴板（Mac）] [网页] 发文件 复制域名 掉线提醒，第一个是实心的；离线只剩复制域名和掉线提醒
        List<FanItem> MenuFor(Node n, int webPort)
        {
            var d = n.Dev;
            var list = new List<FanItem>();
            if (d.Online)
            {
                string ssh = Ssh.Target(d);
                if (ssh != null) list.Add(new FanItem { Icon = "terminal", Label = L.T("终端"), Detail = "ssh " + ssh, Act = () => Ssh.Open(ssh) });
                if (ClipShare.Readable(d)) list.Add(new FanItem { Icon = "clipboard", Label = L.T("读取剪贴板"), Detail = ClipShare.Hotkey ?? "", Act = () => ClipShare.Pull(d) });
                if (webPort > 0)
                {
                    string url = Web.Url(d, webPort, true);
                    list.Add(new FanItem { Icon = "globe", Label = L.T("网页"), Detail = Web.Url(d, webPort, false), Act = () => Ui.Launch(() => Process.Start(url)) });
                }
                list.Add(new FanItem { Icon = "send", Label = L.T("发送文件"), Act = () => PickAndSend(n) });
                list[0].Primary = true;
            }
            string name = d.Dns != "" ? d.Dns : d.Ip;
            list.Add(new FanItem
            {
                Icon = "copy", Label = L.T("复制域名"), Detail = name,
                Toggle = () => Ui.CopyText(name) ? new FanItem { Icon = "check", Label = L.T("已复制"), Detail = name, Good = true } : null
            });
            list.Add(Bell(n, null));
            return list;
        }

        // 掉线提醒的开关；said 是刚点过之后牌子上的回应
        FanItem Bell(Node n, string said)
        {
            bool on = Settings.Watched(n.Dev);
            return new FanItem
            {
                Icon = on ? "bell" : "bell-off", Label = said ?? (on ? L.T("离线提醒已开启") : L.T("离线时提醒我")), Lit = on,
                Toggle = () =>
                {
                    Settings.SetWatched(n.Dev, !on);
                    Alerts.Seed(n.Dev);
                    n.SetWatched(!on, true);
                    return Bell(n, !on ? L.T("设备离线或恢复在线时都会通知你") : L.T("已关闭提醒"));
                }
            };
        }

        void PickAndSend(Node n)
        {
            var dlg = new OpenFileDialog { Multiselect = true, Title = L.T("发送到 {0}", n.Dev.Name) };
            if (dlg.ShowDialog(this) == true) Send(n, dlg.FileNames.ToList());
        }

        // ---------- 排位置 ----------

        void Layout(bool animate)
        {
            double w = field.ActualWidth, h = field.ActualHeight;
            if (w < 1 || h < 1) return;
            // 1140×760 时三圈是 150 / 250 / 360；窗口变大变小跟着缩放，但内圈至少要让开本机
            double u = Math.Max(600, Math.Min(900, Math.Min(h, w / 1.5)));
            o = new Point(Math.Round(w / 2), Math.Round(h / 2 + 0.027 * u));
            radius[0] = Math.Max(132, Math.Round(0.197 * u));
            radius[1] = Math.Max(radius[0] + 92, Math.Round(0.329 * u));
            radius[2] = Math.Max(radius[1] + 100, Math.Round(0.474 * u));
            // 圈没变大小就不重设虚线：每次刷新都换一份虚线，两圈都得整圈重画
            bool resized = rings[1].Width != 2 * radius[1] || rings[2].Width != 2 * radius[2];
            for (int i = 0; i < 3; i++)
            {
                double r = radius[i];
                rings[i].Width = rings[i].Height = 2 * r;
                Canvas.SetLeft(rings[i], o.X - r);
                Canvas.SetTop(rings[i], o.Y - r);
            }
            if (resized)
            {
                rings[1].StrokeDashArray = EvenDash(2 * Math.PI * radius[1], 2, 6);   // 中继：虚线
                rings[2].StrokeDashArray = EvenDash(2 * Math.PI * radius[2], 1, 9);   // 离线：一串点
            }
            selfAt.X = o.X;
            selfAt.Y = o.Y;
            flow.Width = flowIn.Width = w;
            flow.Height = flowIn.Height = h;
            Place(w, h);
            foreach (var n in nodes.Values.Where(x => !x.Gone))
            {
                n.SetLabelUp(LabelUp(n.Angle));
                n.MoveTo(Spot(n), o, animate);
            }
            if (fan != null && (fan.At - Spot(fan.Node)).Length > 1) CloseFan();
            flow.InvalidateVisual();
            flowIn.InvalidateVisual();
        }

        // 给每台设备在它那一圈上挑个角度：原来的位置还放得下就不动（换圈时只沿着那条线进出），
        // 放不下再找离别人最远的地方——在线的偏上方，离线的偏两侧；避开右上的按钮、左下的最近记录和右下的键帽
        void Place(double w, double h)
        {
            var taken = new List<Rect> { new Rect(o.X - 70, o.Y - 62, 140, 146) };   // 本机和它的名字
            var keepOut = new[] { new Rect(w - 200, 0, 200, 52), new Rect(0, h - 84, 300, 84), new Rect(w - 190, h - 74, 190, 74) };
            var order = nodes.Values.Where(x => !x.Gone).OrderBy(x => x.Ring == "direct" ? 0 : x.Ring == "relay" ? 1 : 2).ThenBy(x => x.Dev.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var n in order)
            {
                double r = Radius(n.Ring), best = double.NaN, bestScore = double.NegativeInfinity;
                if (!double.IsNaN(n.Angle))
                {
                    var b = n.Bounds(PointOn(r, n.Angle), LabelUp(n.Angle));
                    if (Fits(b, w, h, keepOut) && Gap(b, taken) >= 24) best = n.Angle;
                }
                if (double.IsNaN(best))
                {
                    for (int a = -180; a < 180; a += 5)
                    {
                        var b = n.Bounds(PointOn(r, a), LabelUp(a));
                        double c = Math.Cos(a * Math.PI / 180);
                        double lean = n.Dev.Online ? (1 - c) / 2 : Math.Abs(c);
                        double score = Math.Min(Gap(b, taken), 120) - 60 * lean - (Fits(b, w, h, keepOut) ? 0 : 1000);
                        if (score > bestScore) { bestScore = score; best = a; }
                    }
                }
                n.Angle = best;
                taken.Add(n.Bounds(PointOn(r, best), LabelUp(best)));
            }
        }

        static bool LabelUp(double angle)
        {
            return Math.Cos(angle * Math.PI / 180) > 0.35;   // 上半圈的设备名字写在上面，背向本机
        }

        double Radius(string ring)
        {
            return ring == "direct" ? radius[0] : ring == "relay" ? radius[1] : radius[2];
        }

        Point PointOn(double r, double angle)
        {
            double a = angle * Math.PI / 180;
            return new Point(o.X + r * Math.Sin(a), o.Y - r * Math.Cos(a));
        }

        Point Spot(Node n)
        {
            return PointOn(Radius(n.Ring), n.Angle);
        }

        static bool Fits(Rect b, double w, double h, Rect[] keepOut)
        {
            if (b.Left < 12 || b.Top < 8 || b.Right > w - 12 || b.Bottom > h - 8) return false;
            return !keepOut.Any(k => k.IntersectsWith(b));
        }

        // 和已经放下的最近那个隔多远；压在一起时是负的
        static double Gap(Rect b, List<Rect> taken)
        {
            double g = double.PositiveInfinity;
            foreach (var t in taken)
            {
                double dx = Math.Max(t.Left - b.Right, b.Left - t.Right), dy = Math.Max(t.Top - b.Bottom, b.Top - t.Bottom);
                g = Math.Min(g, dx < 0 && dy < 0 ? Math.Max(dx, dy) : dx < 0 ? dy : dy < 0 ? dx : Math.Sqrt(dx * dx + dy * dy));
            }
            return g;
        }

        // 虚线按周长微调，让一圈正好排满整数段，接头处不留一截不齐的
        static DoubleCollection EvenDash(double length, double on, double off)
        {
            double n = Math.Max(1, Math.Round(length / (on + off)));
            double k = length / (n * (on + off));
            return new DoubleCollection { on * k, off * k };
        }

        // ---------- 拖文件进来 ----------

        IDataObject dragData;      // 正在拖进来的那份数据，和从它取出的文件列表
        List<string> dragPaths;

        static List<string> PathsOf(IDataObject data)
        {
            var a = data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;
            return a == null || a.Length == 0 ? null : a.ToList();
        }

        static bool Accepts(Node n)
        {
            return n.Dev.Online && !n.Busy && !n.Gone;
        }

        Node HitTest(Point p)
        {
            Node best = null;
            double bestD = double.MaxValue;
            foreach (var n in nodes.Values)
            {
                double d = (n.Center - p).Length;
                if (!n.Gone && d <= n.HitRadius && d < bestD) { best = n; bestD = d; }
            }
            return best;
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            leave.Stop();
            e.Handled = true;
            // 同一次拖动 WPF 给的是同一个数据对象：文件列表只取一次（每次取都要跨进程问资源管理器）
            if (!ReferenceEquals(e.Data, dragData)) { dragData = e.Data; dragPaths = PathsOf(e.Data); }
            var paths = dragPaths;
            var page = CurrentPage;
            if (page != null)
            {
                e.Effects = paths != null && page.CanTake(paths, e.GetPosition(page)) ? DragDropEffects.Copy : DragDropEffects.None;
                return;
            }
            if (paths == null) { e.Effects = DragDropEffects.None; return; }
            var p = e.GetPosition(field);
            if (!lifted) BeginDrag(paths, p);
            var hit = HitTest(p);
            if (hit != aim)
            {
                if (aim != null) aim.SetAim(false);
                aim = hit;
                if (hit != null) { if (Accepts(hit)) hit.SetAim(true); else hit.Refuse(); }
            }
            bool on = aim != null && Accepts(aim);
            Lock(on);
            // 对准了：小方块被设备吸过去三分之一
            var c = on ? aim.Center : p;
            chipAt.X = p.X + (c.X - p.X) * 0.32;
            chipAt.Y = p.Y + (c.Y - p.Y) * 0.32;
            e.Effects = on ? DragDropEffects.Copy : DragDropEffects.None;
        }

        // 文件一进窗口：手上多了个代表它的小方块，在线设备开始呼吸，连线流动起来
        void BeginDrag(List<string> paths, Point p)
        {
            lifted = true;
            locked = false;
            CloseFan();
            foreach (var n in nodes.Values) n.SetLift(true);
            chipFill = new SolidColorBrush(((SolidColorBrush)th.Chip).Color);
            chipInk = new SolidColorBrush(th.FgColor);
            chipScale = new ScaleTransform(1, 1);
            // 一圈底色描边：吸到同色的设备圆盘上时还看得出是两样东西
            var tile = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(15), Background = chipFill, BorderBrush = th.Bg, BorderThickness = new Thickness(2), Child = new IconView(Icons.ForFiles(paths), 26, chipInk), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = chipScale };
            Canvas.SetLeft(tile, -28);
            Canvas.SetTop(tile, -28);
            chipAt = new TranslateTransform(p.X, p.Y);
            chip = new Canvas { RenderTransform = chipAt, Children = { tile } };
            overlay.Children.Add(chip);
            Ui.ScaleFrom(chipScale, 0.5, 1, 240, 0, Ui.Spring);
            Ui.AnimFrom(tile, OpacityProperty, 0, 1, 160);
        }

        void Lock(bool on)
        {
            if (on == locked) return;
            locked = on;
            Ui.Tint(chipFill, on ? th.FgColor : ((SolidColorBrush)th.Chip).Color, 200);
            Ui.Tint(chipInk, on ? th.BgColor : th.FgColor, 200);
            Ui.ScaleTo(chipScale, on ? 0.86 : 1, 250, Ui.Spring);
        }

        void EndDrag(Node target)
        {
            if (!lifted) return;
            lifted = false;
            locked = false;
            leave.Stop();
            if (aim != null) aim.SetAim(false);
            aim = null;
            foreach (var n in nodes.Values) n.SetLift(false);
            var c = chip;
            if (target != null)
            {
                // 小方块被吸进设备里
                Ui.AnimTo(chipAt, TranslateTransform.XProperty, target.Center.X, 280, MainEase.Sink);
                Ui.AnimTo(chipAt, TranslateTransform.YProperty, target.Center.Y, 280, MainEase.Sink);
                Ui.ScaleTo(chipScale, 0.25, 280, MainEase.Sink);
                Ui.AnimTo(c, OpacityProperty, 0, 280, MainEase.Sink);
            }
            else
            {
                Ui.ScaleTo(chipScale, 0.4, 260, MainEase.Sink);
                Ui.AnimTo(c, OpacityProperty, 0, 260, MainEase.Sink);
            }
            chip = null;
            Ui.After(300, () => overlay.Children.Remove(c));
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            dragData = null;
            var paths = PathsOf(e.Data);
            var page = CurrentPage;
            if (page != null)
            {
                var at = e.GetPosition(page);
                bool take = paths != null && page.CanTake(paths, at);
                e.Effects = take ? DragDropEffects.Copy : DragDropEffects.None;
                if (take) page.Take(paths, at);
                else page.DragEnd();
                return;
            }
            var target = HitTest(e.GetPosition(field));
            bool ok = paths != null && target != null && Accepts(target);
            // 明确告诉资源管理器是“复制”：不写的话会把它允许的“移动”原样还回去，有删掉原文件的风险
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (!ok)
            {
                if (target != null) target.Refuse();
                EndDrag(null);
                return;
            }
            EndDrag(target);
            Ui.After(260, () => Send(target, paths));
        }

        // ---------- 发送 ----------

        void Send(Node n, List<string> paths)
        {
            if (n.Busy || !n.Dev.Online) return;
            Begin(n, new SendJob { Target = n.Dev, Paths = paths });
        }

        // 环礁上拖来的：那台设备在星网上、手头没在发，就照样画光点；否则返回 false
        public bool Send(SendJob j)
        {
            Node n;
            if (!nodes.TryGetValue(j.Target.Ip, out n) || n.Busy) return false;
            Begin(n, j);
            return true;
        }

        void Begin(Node n, SendJob j)
        {
            n.Start(j);
            flow.Start(n);
            j.Progress = (f, text) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (n.Job != j) return;
                flow.Hold(n, f == SendJob.Keep);
                n.Progress(f);
            }));
            Task.Run(() => j.Run()).ContinueWith(t => Finish(n, j, t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result), TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Finish(Node n, SendJob j, string result)
        {
            flow.Stop(n, result != null);
            n.Finish(result);
            History.Record(j, result);
            bool away = !IsVisible || WindowState == WindowState.Minimized;
            if (away && result == null) host.Notify(L.T("已发送到 {0}", j.Target.Name), Ui.Describe(j.Paths), true);
            if (away && !string.IsNullOrEmpty(result)) host.Notify(L.T("发送到 {0} 失败", j.Target.Name), L.T(result), true);
            Reload();
        }

        // ---------- 左下：最近发过的 ----------

        void OnHistory()
        {
            var t = History.Items.LastOrDefault();
            if (t == null || t.Result == "cancel") return;
            Node n;
            nodes.TryGetValue(t.Ip, out n);
            Point? from = t.In ? o : n != null ? (Point?)n.Center : null;   // 收到的从本机飞出来
            RenderRecent(IsVisible ? from : null);
        }

        // 最近 5 次（不算取消的）；from 有值时，最新那条从设备（或本机）那里飞进来，其余的往右让一格
        void RenderRecent(Point? from)
        {
            recent.Children.Clear();
            var items = Enumerable.Reverse(History.Items).Where(t => t.Result != "cancel").Take(5).ToList();
            for (int i = 0; i < items.Count; i++)
            {
                var tile = Tile(items[i]);
                tile.Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
                recent.Children.Add(tile);
            }
            if (from == null || items.Count == 0) return;
            var first = (FrameworkElement)recent.Children[0];
            first.Opacity = 0;
            for (int i = 1; i < recent.Children.Count; i++) Ui.AnimFrom(recent.Children[i].RenderTransform, TranslateTransform.XProperty, -48, 0, 360, 0, Ui.Glide);
            recent.UpdateLayout();
            Point to = first.TranslatePoint(new Point(20, 20), overlay);
            var ghost = Tile(items[0]);
            ghost.IsHitTestVisible = false;
            var scale = new ScaleTransform(1, 1, 20, 20);
            var move = new TranslateTransform();
            var tg = new TransformGroup();
            tg.Children.Add(scale);
            tg.Children.Add(move);
            ghost.RenderTransform = tg;
            overlay.Children.Add(ghost);
            var f = from.Value;
            Ui.ScaleFrom(scale, 0.5, 1, 185, 0, Ui.Spring);
            Ui.AnimFrom(ghost, OpacityProperty, 0, 1, 185);
            Ui.AnimFrom(move, TranslateTransform.XProperty, f.X - 20, to.X - 20, 655, 185, Ui.Glide);
            Ui.AnimFrom(move, TranslateTransform.YProperty, f.Y - 20, to.Y - 20, 655, 185, Ui.Glide);
            Ui.After(850, () => { overlay.Children.Remove(ghost); first.Opacity = 1; });
        }

        // 一个文件方块：发出去的右上角是绿点（送到了）或红点（没送到）；收到的是一个实心的收件标，刚收到还没看的整块亮着
        FrameworkElement Tile(Transfer t)
        {
            var icon = new IconView(Icons.ForFiles(t.Paths), 18, t.Fresh ? th.Fg : th.Mute);
            var box = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = t.Fresh ? th.Chip : th.Tile, Child = icon };
            FrameworkElement mark = new Ellipse { Width = 13, Height = 13, Fill = t.Result == "ok" ? th.Ok : th.Bad, Stroke = th.Bg, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -4, 0) };
            if (t.In) mark = new Border { Width = 17, Height = 17, CornerRadius = new CornerRadius(8.5), Background = th.Fg, BorderBrush = th.Bg, BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -5, -5, 0), Child = new IconView("inbox", 9, th.Ink, 2.8) };
            var shift = new TranslateTransform();
            string who = t.In ? L.T("来自 {0}", t.Target != "" ? t.Target : L.T("另一台设备")) : t.Target;
            string tip = Ui.Describe(t.Paths) + "  ·  " + who + "  ·  " + Ui.When(t.Time) + (t.Result == "ok" ? "" : "\n" + L.T(t.Error));
            var g = new Grid { Width = 40, Height = 40, Background = Brushes.Transparent, Cursor = Cursors.Hand, RenderTransform = shift, ToolTip = Ui.Tip(th, tip) };
            g.Children.Add(box);
            g.Children.Add(mark);
            g.MouseEnter += delegate { box.Background = th.Chip; icon.Stroke = th.Fg; };
            g.MouseLeave += delegate { box.Background = t.Fresh ? th.Chip : th.Tile; icon.Stroke = t.Fresh ? th.Fg : th.Mute; };
            // 点一下：在文件夹里显示；按住拖到设备上：再发一次（也能拖进别的程序）
            bool pressed = false;
            var down = new Point();
            g.MouseLeftButtonDown += (s, e) => { pressed = true; down = e.GetPosition(g); g.CaptureMouse(); e.Handled = true; };
            g.MouseMove += (s, e) =>
            {
                if (!pressed) return;
                var v = e.GetPosition(g) - down;
                if (Math.Abs(v.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(v.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = false;
                g.ReleaseMouseCapture();
                var paths = t.Paths.Where(x => File.Exists(x) || Directory.Exists(x)).ToArray();
                if (paths.Length == 0) { Ui.Shake(shift); return; }
                DragDrop.DoDragDrop(g, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
            };
            g.MouseLeftButtonUp += delegate
            {
                if (!pressed) return;
                pressed = false;
                g.ReleaseMouseCapture();
                t.Fresh = false;
                var p = t.Paths.FirstOrDefault(x => File.Exists(x) || Directory.Exists(x));
                if (p == null && t.In) { Ui.Launch(() => Process.Start("explorer.exe", "shell:Downloads")); return; }   // 收到的被改了名或挪走了：打开“下载”
                if (p == null) { Ui.Shake(shift); return; }   // 原文件已经不在了
                Ui.Launch(() => Process.Start("explorer.exe", "/select,\"" + p + "\""));
            };
            return g;
        }

        // ---------- 右下：快捷面板的热键 ----------

        FrameworkElement Keys()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 36, 30), Background = Brushes.Transparent, ToolTip = Ui.Tip(th, L.T("在文件资源管理器中选中文件，然后按下此快捷键即可发送。")) };
            foreach (var k in new[] { "Ctrl", "Alt", "A" })
            {
                var b = new Border
                {
                    Height = 30, MinWidth = 30, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8), BorderBrush = th.Rim, BorderThickness = new Thickness(1),
                    Background = Brushes.Transparent, Margin = new Thickness(keys.Count == 0 ? 0 : 6, 0, 0, 0), RenderTransform = new TranslateTransform(),
                    Child = new TextBlock { Text = k, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = th.Mute, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                keys.Add(b);
                row.Children.Add(b);
            }
            return row;
        }

        // 依次按下 Ctrl、Alt、A，再一起松开
        void PressKeys()
        {
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                Ui.After(1 + 140 * i, () => Press(k, true));
            }
            Ui.After(140 * keys.Count + 260, () => { foreach (var k in keys) Press(k, false); });
        }

        void Press(Border k, bool down)
        {
            k.Background = down ? th.Fg : Brushes.Transparent;
            k.BorderBrush = down ? th.Fg : th.Rim;
            ((TextBlock)k.Child).Foreground = down ? th.Bg : th.Mute;
            Ui.AnimTo(k.RenderTransform, TranslateTransform.YProperty, down ? 2 : 0, 100);
        }

        // ---------- 顶上：分页标签 ----------

        FrameworkElement Tabs()
        {
            tabs = new Segmented(th, new[] { L.T("星网"), L.T("分享"), L.T("网络"), L.T("云盘"), L.T("本机") }, 0, false, false, 32, true) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0) };
            tabs.Picked += i => Switch(PageKeys[i]);
            return tabs;
        }

        Page CurrentPage { get { return pages[current] as Page; } }

        FrameworkElement MakePage(string key)
        {
            switch (key)
            {
                case "share": return new SharePage(host);
                case "net": return new NetPage(host, ip => { Node n; return nodes.TryGetValue(ip, out n) ? n.Angle : double.NaN; });   // 设备方向跟星网一致
                case "drive": return new DrivePage(host);
                case "device": return new DevicePage(host);
                default: return null;
            }
        }

        // 换页：旧的一页往反方向淡出，新的一页从这边滑进来
        public void Switch(string key)
        {
            Switch(key, true);
        }

        void Switch(string key, bool animate)
        {
            if (key == current) return;
            int to = Array.IndexOf(PageKeys, key), from = Array.IndexOf(PageKeys, current);
            FrameworkElement next;
            if (!pages.TryGetValue(key, out next))
            {
                next = MakePage(key);
                if (next == null) { tabs.Select(from, true); return; }
                next.Visibility = Visibility.Collapsed;
                pages[key] = next;
                root.Children.Insert(root.Children.IndexOf(home) + 1, next);
            }
            var prev = pages[current];
            var leaving = CurrentPage;
            if (leaving != null) leaving.Hidden();
            CloseFan();
            int dir = to > from ? 1 : -1;
            current = key;
            ShowMissing();
            if (tabs.Index != to) tabs.Select(to, animate);
            if (!animate)
            {
                prev.Visibility = Visibility.Collapsed;
                next.Visibility = Visibility.Visible;
                return;   // 窗口还没显示：Shown 等显示出来再调
            }
            Ui.AnimTo(prev, OpacityProperty, 0, 150);
            Ui.AnimTo(Shift(prev), TranslateTransform.XProperty, -24 * dir, 200);
            Ui.After(160, () => { if (pages[current] != prev) prev.Visibility = Visibility.Collapsed; });
            next.Visibility = Visibility.Visible;
            Ui.AnimFrom(next, OpacityProperty, 0, 1, 240, 70);
            Ui.AnimFrom(Shift(next), TranslateTransform.XProperty, 24 * dir, 0, 420, 70, MainEase.Settle);
            var entering = next as Page;
            if (entering != null) entering.Shown();
        }

        static TranslateTransform Shift(FrameworkElement e)
        {
            var t = e.RenderTransform as TranslateTransform;
            if (t == null) e.RenderTransform = t = new TranslateTransform();
            return t;
        }

        // ---------- 右上：窗口按钮 ----------

        // 左上：程序图标和名字，和中间的页签齐平；不接鼠标，按住这里照样能拖窗口
        FrameworkElement Brand()
        {
            var icon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            Action pick = () => icon.Source = Ui.IconFrame(20 * VisualTreeHelper.GetDpi(this).DpiScaleX);   // 按屏幕缩放取最贴近的那一帧，小图才清楚
            Loaded += delegate { pick(); };
            DpiChanged += delegate { pick(); };
            var name = new TextBlock { Text = "TailOrbit", FontFamily = Ui.DisplayFont, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = th.Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
            return new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Height = 38, Margin = new Thickness(18, 7, 0, 0), IsHitTestVisible = false, Children = { icon, name } };   // 和页签同一个框（上边距 7、高 32＋上下各 3 的内边距）
        }

        FrameworkElement TitleBar()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            IconView ignore;
            bar.Children.Add(CaptionButton("min", 16, false, () => WindowState = WindowState.Minimized, out ignore));
            bar.Children.Add(CaptionButton("max", 14, false, () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized, out maxIcon));
            bar.Children.Add(CaptionButton("close", 16, true, Close, out ignore));
            return bar;
        }

        Border CaptionButton(string icon, double size, bool danger, Action click, out IconView glyph)
        {
            var g = new IconView(icon, size, th.Mute);
            glyph = g;
            var b = new Border { Width = 46, Height = 44, Background = Brushes.Transparent, Child = g };
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            b.MouseEnter += delegate { b.Background = danger ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : th.Hair; g.Stroke = danger ? Brushes.White : th.Fg; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; g.Stroke = th.Mute; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }
    }
}
