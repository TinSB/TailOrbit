// 主界面「星网」：本机在正中，设备按连接方式排在三圈上——内圈直连、中圈经中继、外圈离线。
// 把文件拖到设备上就发；点设备挑文件，发送中再点是取消；点名字复制 IP；点中间的本机打开收到的文件。
// 左下是最近发过的文件（点一下在文件夹里显示，拖到设备上再发一次），右下提示快捷面板的热键；关窗只是收到托盘。
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
        readonly FlowLayer flow;
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
        Device me;
        int selfCopy;

        readonly StackPanel recent = new StackPanel { Orientation = Orientation.Horizontal };
        readonly TranslateTransform recentShift = new TranslateTransform();
        readonly List<Border> keys = new List<Border>();
        IconView maxIcon;
        Border gear, popover, toggle;
        readonly ScaleTransform popScale = new ScaleTransform(1, 1);
        readonly TranslateTransform knob = new TranslateTransform();
        bool popOpen;

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
            for (int i = 0; i < 3; i++)
            {
                rings[i] = new Ellipse { Stroke = th.Hair, StrokeThickness = 1, StrokeDashCap = PenLineCap.Round, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = ringScale[i] };
                field.Children.Add(rings[i]);
            }
            field.Children.Add(links);
            field.Children.Add(flow);
            field.Children.Add(BuildSelf());
            field.Children.Add(stars);

            recent.HorizontalAlignment = HorizontalAlignment.Left;
            recent.VerticalAlignment = VerticalAlignment.Bottom;
            recent.Margin = new Thickness(36, 0, 0, 30);
            recent.RenderTransform = recentShift;

            root.Background = th.Bg;
            root.Children.Add(field);
            root.Children.Add(recent);
            root.Children.Add(Keys());
            root.Children.Add(TitleBar());
            root.Children.Add(Popover());
            root.Children.Add(overlay);
            Content = root;

            field.SizeChanged += delegate { Layout(false); };
            History.Changed += OnHistory;
            refresh.Tick += delegate { if (WindowState != WindowState.Minimized) Reload(); };
            IsVisibleChanged += delegate { if (!IsVisible) refresh.Stop(); };
            leave.Tick += delegate { leave.Stop(); EndDrag(null); };
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            // 在窗口里从一个元素拖到另一个元素也会先“离开”，过一会儿还没收到拖动才算真的拖出去了（或按了 Esc）
            DragLeave += delegate { leave.Start(); };
            Drop += OnDrop;
            PreviewMouseDown += delegate { if (popOpen && !popover.IsMouseOver && !gear.IsMouseOver) ShowPopover(false); };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && popOpen) { ShowPopover(false); e.Handled = true; } };
            SourceInitialized += delegate
            {
                int on = th.Dark ? 1 : 0;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4);   // 深色模式下窗口边框也用深色
            };
            StateChanged += delegate
            {
                // 自绘标题栏的窗口最大化时会超出屏幕一圈边框的宽度，往里收回来
                root.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
                maxIcon.Show(WindowState == WindowState.Maximized ? "restore" : "max");
            };
            RenderRecent(null);
        }

        public void Open()
        {
            bool hidden = !IsVisible;
            if (hidden) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            if (hidden) Intro();
            Reload();
            refresh.Start();
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
            if (popOpen) ShowPopover(false);
            Hide();
        }

        // ---------- 开屏 ----------

        // 三圈由内向外荡开，本机弹出来，线一条条长出去、设备依次亮起，最后把快捷键按一遍
        void Intro()
        {
            fresh = true;
            for (int i = 0; i < 3; i++)
            {
                Ui.AnimFrom(rings[i], OpacityProperty, 0, 1, 900, i * 110, MainEase.Settle);
                Ui.ScaleFrom(ringScale[i], 0.55, 1, 900, i * 110, MainEase.Settle);
            }
            Ui.ScaleFrom(selfPop, 0, 1, 640, 0, Ui.Spring);
            Ui.AnimFrom(selfLabel, OpacityProperty, 0, 1, 400, 300);
            Appear(nodes.Values.ToList());
            Ui.AnimFrom(recent, OpacityProperty, 0, 1, 500, 1100);
            Ui.AnimFrom(recentShift, TranslateTransform.YProperty, 8, 0, 500, 1100);
            Ui.After(1250, PressKeys);
        }

        void Appear(List<Node> list)
        {
            int on = 0, off = 0;
            foreach (var n in list.OrderBy(x => x.Dev.Online ? 0 : 1).ThenBy(x => x.Dev.Name, StringComparer.OrdinalIgnoreCase))
                n.Appear(n.Dev.Online ? on++ : off++, o, true);
        }

        // ---------- 本机 ----------

        FrameworkElement BuildSelf()
        {
            var self = new Canvas { RenderTransform = selfAt };
            var halo = new Ellipse { Width = 124, Height = 124, Stroke = th.Hair, StrokeThickness = 1, IsHitTestVisible = false };
            Canvas.SetLeft(halo, -62);
            Canvas.SetTop(halo, -62);
            selfFace = new SolidColorBrush(th.FgColor);
            selfRim = new SolidColorBrush(Theme.Fade(th.DimColor, 0));
            selfInk = new SolidColorBrush(th.InkColor);
            selfIcon = new IconView("monitor", 36, selfInk);
            selfHover = new IconView("inbox", 32, selfInk);
            var disc = new Grid { Width = 96, Height = 96, Background = Brushes.Transparent, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = selfPop };
            disc.Children.Add(new Ellipse { Fill = selfFace, Stroke = selfRim, StrokeThickness = 1.5 });
            foreach (var e in new FrameworkElement[] { selfIcon, selfHover })
            {
                e.RenderTransformOrigin = new Point(0.5, 0.5);
                e.RenderTransform = new ScaleTransform(1, 1);
                e.Tag = true;
                disc.Children.Add(e);
            }
            Node.Swap(selfHover, false, true);
            Canvas.SetLeft(disc, -48);
            Canvas.SetTop(disc, -48);
            // 鼠标放上去变成“收件”，点一下打开收到的文件
            disc.MouseEnter += delegate { Node.Swap(selfIcon, false); Node.Swap(selfHover, true); };
            disc.MouseLeave += delegate { Node.Swap(selfIcon, true); Node.Swap(selfHover, false); };
            disc.MouseLeftButtonUp += delegate
            {
                Ui.ScaleFrom(selfPop, 0.92, 1, 360, 0, Ui.Spring);
                Process.Start("explorer.exe", "shell:Downloads");   // Tailscale 在 Windows 上把收到的文件存在“下载”里
            };
            WindowChrome.SetIsHitTestVisibleInChrome(disc, true);

            selfLabel = Node.Label(th, th.Mute, selfLabelShift, out selfName, out selfIp);
            Canvas.SetLeft(selfLabel, -140);
            Canvas.SetTop(selfLabel, 64);
            selfLabel.Children[0].MouseLeftButtonUp += delegate { CopySelf(); };
            self.Children.Add(halo);
            self.Children.Add(disc);
            self.Children.Add(selfLabel);
            return self;
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
            string error = snap == null ? "找不到 Tailscale" : snap.Error;
            me = snap == null ? null : snap.Self;
            ShowSelf(error);
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
            }
            foreach (var n in nodes.Values.Where(x => !seen.Contains(x.Dev.Ip) && x.Job == null).ToList()) RemoveNode(n);
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
            nodes.Remove(n.Dev.Ip);
            n.Vanish(() => { links.Children.Remove(n.Link); stars.Children.Remove(n.Root); });
        }

        void OnNodeClick(Node n)
        {
            if (n.Busy) { n.Job.Cancel(); return; }
            if (!n.Dev.Online) { n.Refuse(); return; }
            var dlg = new OpenFileDialog { Multiselect = true, Title = "发到 " + n.Dev.Name };
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
            for (int i = 0; i < 3; i++)
            {
                double r = radius[i];
                rings[i].Width = rings[i].Height = 2 * r;
                Canvas.SetLeft(rings[i], o.X - r);
                Canvas.SetTop(rings[i], o.Y - r);
            }
            rings[1].StrokeDashArray = EvenDash(2 * Math.PI * radius[1], 2, 6);   // 中继：虚线
            rings[2].StrokeDashArray = EvenDash(2 * Math.PI * radius[2], 1, 9);   // 离线：一串点
            selfAt.X = o.X;
            selfAt.Y = o.Y;
            flow.Width = w;
            flow.Height = h;
            Place(w, h);
            foreach (var n in nodes.Values.Where(x => !x.Gone))
            {
                n.SetLabelUp(LabelUp(n.Angle));
                n.MoveTo(Spot(n), o, animate);
            }
            flow.InvalidateVisual();
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
            var paths = PathsOf(e.Data);
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
            if (popOpen) ShowPopover(false);
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
            var paths = PathsOf(e.Data);
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
            var j = new SendJob { Target = n.Dev, Paths = paths };
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
            if (away && result == null) host.Notify("已发送到 " + j.Target.Name, Ui.Describe(j.Paths));
            if (away && !string.IsNullOrEmpty(result)) host.Notify("没发出去：" + j.Target.Name, result);
            Reload();
        }

        // ---------- 左下：最近发过的 ----------

        void OnHistory()
        {
            var t = History.Items.LastOrDefault();
            if (t == null || t.Result == "cancel") return;
            Node n;
            nodes.TryGetValue(t.Ip, out n);
            RenderRecent(n != null && IsVisible ? (Point?)n.Center : null);
        }

        // 最近 5 次（不算取消的）；from 有值时，最新那条从设备那里飞进来，其余的往右让一格
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

        FrameworkElement Tile(Transfer t)
        {
            var icon = new IconView(Icons.ForFiles(t.Paths), 18, th.Mute);
            var box = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = th.Tile, Child = icon };
            var dot = new Ellipse { Width = 13, Height = 13, Fill = t.Result == "ok" ? th.Ok : th.Bad, Stroke = th.Bg, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -4, 0) };
            var shift = new TranslateTransform();
            string tip = Ui.Describe(t.Paths) + "  ·  " + t.Target + "  ·  " + Ui.When(t.Time) + (t.Result == "ok" ? "" : "\n" + t.Error);
            var g = new Grid { Width = 40, Height = 40, Background = Brushes.Transparent, Cursor = Cursors.Hand, RenderTransform = shift, ToolTip = Ui.Tip(th, tip) };
            g.Children.Add(box);
            g.Children.Add(dot);
            g.MouseEnter += delegate { box.Background = th.Chip; icon.Stroke = th.Fg; };
            g.MouseLeave += delegate { box.Background = th.Tile; icon.Stroke = th.Mute; };
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
                var p = t.Paths.FirstOrDefault(x => File.Exists(x) || Directory.Exists(x));
                if (p == null) { Ui.Shake(shift); return; }   // 原文件已经不在了
                Process.Start("explorer.exe", "/select,\"" + p + "\"");
            };
            return g;
        }

        // ---------- 右下：快捷面板的热键 ----------

        FrameworkElement Keys()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 36, 30), Background = Brushes.Transparent, ToolTip = Ui.Tip(th, "在资源管理器里选中文件，按下它就能发") };
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

        // ---------- 右上：设置和窗口按钮 ----------

        FrameworkElement TitleBar()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var gearIcon = new IconView("gear", 17, th.Mute);
            gear = new Border { Width = 40, Height = 36, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 4, 4, 4), Background = Brushes.Transparent, Child = gearIcon, Cursor = Cursors.Hand };
            WindowChrome.SetIsHitTestVisibleInChrome(gear, true);
            gear.MouseEnter += delegate { gear.Background = th.Hair; gearIcon.Stroke = th.Fg; };
            gear.MouseLeave += delegate { gear.Background = Brushes.Transparent; gearIcon.Stroke = th.Mute; };
            gear.MouseLeftButtonUp += delegate { ShowPopover(!popOpen); };
            IconView ignore;
            bar.Children.Add(gear);
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

        FrameworkElement Popover()
        {
            var dot = new Ellipse { Width = 16, Height = 16, Fill = th.Bg, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0), RenderTransform = knob };
            toggle = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), Child = dot };
            var stack = new StackPanel();
            stack.Children.Add(PopRow("开机自启", toggle, () => { Autostart.Set(!Autostart.Enabled); SyncToggle(true); }));
            stack.Children.Add(PopRow("退出", new IconView("close", 15, th.Mute), host.Quit));
            popover = new Border
            {
                Width = 230, Padding = new Thickness(6), CornerRadius = new CornerRadius(14), Background = th.Pop, BorderBrush = th.Hair, BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 48, 146, 0), Child = stack, Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(1, 0), RenderTransform = popScale,
                Effect = new DropShadowEffect { Color = Colors.Black, Direction = 270, ShadowDepth = 12, BlurRadius = 32, Opacity = th.Dark ? 0.35 : 0.16 }
            };
            return popover;
        }

        Border PopRow(string text, FrameworkElement end, Action click)
        {
            end.VerticalAlignment = VerticalAlignment.Center;
            var label = new TextBlock { Text = text, FontSize = 14, Foreground = th.Fg, VerticalAlignment = VerticalAlignment.Center };
            var row = new Border { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Ui.Columns(0, label, end) };
            row.MouseEnter += delegate { row.Background = th.Hair; };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += delegate { click(); };
            return row;
        }

        void ShowPopover(bool on)
        {
            popOpen = on;
            if (on)
            {
                SyncToggle(false);
                popover.Visibility = Visibility.Visible;
                Ui.AnimFrom(popover, OpacityProperty, 0, 1, 150);
                Ui.ScaleFrom(popScale, 0.94, 1, 220, 0, Ui.Spring);
                return;
            }
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            a.Completed += delegate { if (!popOpen) popover.Visibility = Visibility.Collapsed; };
            popover.BeginAnimation(OpacityProperty, a);
        }

        void SyncToggle(bool animate)
        {
            bool on = Autostart.Enabled;
            toggle.Background = on ? th.Fg : th.Rim;
            Ui.AnimTo(knob, TranslateTransform.XProperty, on ? 18 : 0, animate ? 300 : 1, Ui.Spring);
        }
    }
}
