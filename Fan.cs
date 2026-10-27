// 点星网上的设备弹出的一圈按钮：终端、网页、发文件、复制域名、掉线提醒。
// 按钮从设备里飞出来，排在背向本机的一侧（贴边时整串转进来）；旁边一块小牌子说指着的那个按钮会做什么，终端写出实际执行的 ssh 命令。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace TailOrbit
{
    class FanItem
    {
        public string Icon = "", Label = "", Detail = "";
        public bool Primary;   // 最常用的那个：实心
        public bool Lit;       // 开着的开关（掉线提醒）：右上角一个绿点
        public bool Good;      // 刚做完（复制好了）：图标变绿
        public Action Act;             // 点了先收起菜单再做
        public Func<FanItem> Toggle;   // 点了原地换个样子给回应，过一会儿再收起
    }

    class Fan
    {
        const double Step = 36 * Math.PI / 180;   // 相邻两个按钮隔 36°

        public readonly Node Node;
        public readonly Point At;   // 设备中心
        readonly Theme th;
        readonly Canvas layer, root = new Canvas();
        readonly Size area;
        readonly Action<Fan> close;
        readonly List<FanItem> items;
        readonly List<Border> buttons = new List<Border>();
        readonly List<Point> spots = new List<Point>();   // 各按钮相对设备中心的位置
        readonly Border tag;
        readonly TextBlock tagLabel, tagDetail;
        readonly TranslateTransform tagAt = new TranslateTransform();
        int said = -1;
        bool held, closing;

        public Fan(Theme th, Canvas layer, Node node, Point origin, Size area, List<FanItem> items, Action<Fan> close)
        {
            this.th = th;
            this.layer = layer;
            this.area = area;
            this.items = items;
            this.close = close;
            Node = node;
            At = node.Center;
            root.RenderTransform = new TranslateTransform(At.X, At.Y);
            Arrange(origin);
            for (int i = 0; i < items.Count; i++)
            {
                var b = Button(i);
                buttons.Add(b);
                root.Children.Add(b);
            }
            tagLabel = new TextBlock { FontSize = 12.5, Foreground = th.Fg, VerticalAlignment = VerticalAlignment.Center };
            tagDetail = new TextBlock { FontFamily = Ui.MonoFont, FontSize = 12, Foreground = th.Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { tagLabel, tagDetail } };
            tag = new Border { Height = 28, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8), Background = th.Pop, BorderBrush = th.Hair, BorderThickness = new Thickness(1), Child = row, IsHitTestVisible = false, Opacity = 0, RenderTransform = tagAt };
            root.Children.Add(tag);
            layer.Children.Add(root);
            Pop();
        }

        public bool IsMouseOver { get { return root.IsMouseOver; } }

        // 整串按钮的朝向：先试正对外侧，放不下就左右转着找最近的能放下的角度
        void Arrange(Point origin)
        {
            var d = At - origin;
            double phi = d.Length < 1 ? -Math.PI / 2 : Math.Atan2(d.Y, d.X);
            double r = (Node.Dev.Online ? 40 : 31) + 36;
            int m = items.Count;
            double turn = 0;
            foreach (int t in new[] { 0, 15, -15, 30, -30, 45, -45, 60, -60, 75, -75, 90, -90, 120, -120, 150, -150, 180 })
            {
                double a = phi + t * Math.PI / 180;
                if (Enumerable.Range(0, m).All(k => Inside(At + Spot(a + (k - (m - 1) / 2.0) * Step, r)))) { turn = a - phi; break; }
            }
            double mid = phi + turn;
            // 第一个（最常用的）放在上面那头；整串差不多横着时放左头
            Vector first = Spot(mid - (m - 1) / 2.0 * Step, r), last = Spot(mid + (m - 1) / 2.0 * Step, r);
            bool flip = Math.Abs(first.Y - last.Y) > 8 ? first.Y > last.Y : first.X > last.X;
            for (int k = 0; k < m; k++) spots.Add((Point)Spot(mid + (k - (m - 1) / 2.0) * Step * (flip ? -1 : 1), r));
        }

        static Vector Spot(double a, double r)
        {
            return new Vector(r * Math.Cos(a), r * Math.Sin(a));
        }

        // 按钮整个在窗口里，并且让开顶上的标签栏
        bool Inside(Point p)
        {
            return p.X >= 28 && p.X <= area.Width - 28 && p.Y >= 68 && p.Y <= area.Height - 28;
        }

        Border Button(int i)
        {
            var b = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5) };
            var tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform(0.3, 0.3));
            tg.Children.Add(new TranslateTransform());
            b.RenderTransform = tg;
            Canvas.SetLeft(b, -20);
            Canvas.SetTop(b, -20);
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            Style(b, items[i], false);
            b.MouseEnter += delegate { Style(b, items[i], true); if (!held) Say(i); };
            b.MouseLeave += delegate { Style(b, items[i], false); if (!held) Say(0); };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; Hit(i); };
            return b;
        }

        void Style(Border b, FanItem it, bool hover)
        {
            var glyph = new IconView(it.Icon, 18, it.Good ? th.Ok : it.Primary ? th.Ink : th.Fg, it.Good ? 2.4 : 1.75);
            var dot = new Ellipse { Width = 11, Height = 11, Fill = th.Ok, Stroke = th.Bg, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, -1, 0), Visibility = it.Lit ? Visibility.Visible : Visibility.Collapsed };
            b.Child = new Grid { Children = { glyph, dot } };
            b.Background = it.Primary ? th.Fg : hover ? th.Chip : th.Pop;
            b.BorderBrush = it.Primary ? null : th.Hair;
            b.BorderThickness = new Thickness(it.Primary ? 0 : 1);
            b.Opacity = it.Primary && hover ? 0.86 : 1;
        }

        // 从设备中心飞出去，一个接一个
        void Pop()
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var tg = (TransformGroup)buttons[i].RenderTransform;
                var s = (ScaleTransform)tg.Children[0];
                var t = (TranslateTransform)tg.Children[1];
                int delay = 30 * i;
                Ui.AnimFrom(t, TranslateTransform.XProperty, 0, spots[i].X, 420, delay, Ui.Spring);
                Ui.AnimFrom(t, TranslateTransform.YProperty, 0, spots[i].Y, 420, delay, Ui.Spring);
                Ui.ScaleFrom(s, 0.3, 1, 420, delay, Ui.Spring);
                Ui.AnimFrom(buttons[i], UIElement.OpacityProperty, 0, 1, 160, delay);
            }
            Ui.After(170, () => { if (!closing && said < 0) Say(0); });
        }

        // 小牌子：放在按钮外侧——偏左的放左边、偏右的放右边、朝上朝下的放上下；左右放不下时改放上下，再出界就往里收
        void Say(int i)
        {
            if (closing || i >= items.Count) return;
            bool moved = i != said;
            said = i;
            var it = items[i];
            tagLabel.Text = it.Label;
            tagDetail.Text = it.Detail;
            tagDetail.Visibility = it.Detail == "" ? Visibility.Collapsed : Visibility.Visible;
            tagDetail.MaxWidth = double.PositiveInfinity;
            tag.UpdateLayout();   // 刚换了字：直接 Measure 会拿到上次的宽度
            double w = tag.DesiredSize.Width;
            var p = spots[i];
            var u = (Vector)p;
            u.Normalize();
            int side = u.X < -0.45 ? -1 : u.X > 0.45 ? 1 : 0;
            bool up = u.Y < 0;
            double room = side < 0 ? At.X + p.X - 36 : area.Width - 8 - (At.X + p.X + 28);
            if (side != 0 && w > room)
            {
                // 这一侧放不下：先把后面那串（多半是长域名）截短；还不够就改放上下——竖着的一串上半截放上面、下半截放下面，不压着别的按钮
                double cut = tagDetail.DesiredSize.Width - tagDetail.Margin.Left - (w - room);
                if (it.Detail != "" && cut >= 60)
                {
                    tagDetail.MaxWidth = cut;
                    tag.UpdateLayout();
                    w = tag.DesiredSize.Width;
                }
                else
                {
                    side = 0;
                    up = 2 * i < items.Count - 1 || (2 * i == items.Count - 1 && u.Y < 0);
                }
            }
            double x, y;
            if (side != 0) { x = side < 0 ? p.X - 28 - w : p.X + 28; y = p.Y - 14; }
            else { x = p.X - w / 2; y = up ? p.Y - 56 : p.Y + 28; }
            x = Math.Max(8 - At.X, Math.Min(area.Width - 8 - w - At.X, x));
            y = Math.Max(52 - At.Y, Math.Min(area.Height - 36 - At.Y, y));
            Canvas.SetLeft(tag, x);
            Canvas.SetTop(tag, y);
            if (!moved) return;
            Ui.AnimFrom(tag, UIElement.OpacityProperty, 0, 1, 140);
            Ui.AnimFrom(tagAt, TranslateTransform.XProperty, u.X * -6, 0, 220, 0, Ui.Ease);
        }

        void Hit(int i)
        {
            if (closing || held) return;
            var it = items[i];
            if (it.Act != null)
            {
                close(this);
                it.Act();
                return;
            }
            if (it.Toggle == null) return;
            var next = it.Toggle();
            if (next == null) { Ui.Shake((TranslateTransform)((TransformGroup)buttons[i].RenderTransform).Children[1]); return; }
            items[i] = next;
            Style(buttons[i], next, true);
            held = true;   // 牌子停在这个按钮上把回应说完
            said = -1;
            Say(i);
            Ui.ScaleFrom((ScaleTransform)((TransformGroup)buttons[i].RenderTransform).Children[0], 0.8, 1, 360, 0, Ui.Spring);
            Ui.After(900, () => close(this));
        }

        // 收回设备里
        public void Close()
        {
            if (closing) return;
            closing = true;
            root.IsHitTestVisible = false;
            foreach (var b in buttons)
            {
                var tg = (TransformGroup)b.RenderTransform;
                Ui.AnimTo(tg.Children[1], TranslateTransform.XProperty, 0, 200, MainEase.Sink);
                Ui.AnimTo(tg.Children[1], TranslateTransform.YProperty, 0, 200, MainEase.Sink);
                Ui.ScaleTo((ScaleTransform)tg.Children[0], 0.3, 200, MainEase.Sink);
                Ui.AnimTo(b, UIElement.OpacityProperty, 0, 180, MainEase.Sink);
            }
            Ui.AnimTo(tag, UIElement.OpacityProperty, 0, 100);
            Ui.After(220, () => layer.Children.Remove(root));
        }
    }

    // 用你现有的 ssh 配置连过去：~/.ssh/config 里 HostName 指向这台设备（Tailscale IP、MagicDNS 名或短名）的别名
    static class Ssh
    {
        // 没有别名时，Linux 和开了 Tailscale SSH 的设备直接用域名；其他的（手机、没开远程登录的 Mac）不给终端
        public static string Target(Device d)
        {
            string alias = Alias(d);
            if (alias != null) return alias;
            if (d.Ssh || d.Os == "linux" || d.Os == "freebsd") return d.Dns != "" ? d.Dns : d.Ip;
            return null;
        }

        static string Alias(Device d)
        {
            string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            if (!File.Exists(path)) return null;
            var names = new[] { d.Ip, d.Dns, d.Name }.Where(x => x != "").ToList();
            string alias = null, host = null;
            Func<string> match = () => alias != null && names.Any(x => string.Equals(x, (host ?? alias).TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) ? alias : null;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int sp = line.IndexOfAny(new[] { ' ', '\t', '=' });
                if (line.StartsWith("#") || sp < 0) continue;
                string key = line.Substring(0, sp), value = line.Substring(sp + 1).Trim().TrimStart('=').Trim().Trim('"');
                bool isHost = key.Equals("Host", StringComparison.OrdinalIgnoreCase);
                if (isHost || key.Equals("Match", StringComparison.OrdinalIgnoreCase))
                {
                    string hit = match();
                    if (hit != null) return hit;
                    // 一行可以写几个名字，取第一个不带通配符的；Match 块不认
                    alias = isHost ? value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.IndexOfAny(new[] { '*', '?', '!' }) < 0) : null;
                    host = null;
                }
                else if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase)) host = value;
            }
            return match();
        }

        // 有 Windows Terminal 就在里面开，没有就用命令提示符
        public static void Open(string target)
        {
            try { Process.Start("wt.exe", "ssh " + target); }
            catch (Win32Exception) { Process.Start("cmd.exe", "/k ssh " + target); }
        }
    }

    // 设备上开着的网页：点菜单时把常见的几个端口同时敲一下
    static class Web
    {
        static readonly int[] Ports = { 443, 80, 8443, 8080, 3000, 8000, 5000, 8888, 9000 };

        // 按上面的顺序第一个开着的端口；都没开返回 0
        public static int Find(string ip, int timeout)
        {
            var tasks = Ports.Select(p => Task.Run(() => Answers(ip, p, timeout))).ToArray();
            Task.WaitAll(tasks);
            for (int i = 0; i < Ports.Length; i++) if (tasks[i].Result) return Ports[i];
            return 0;
        }

        static bool Answers(string ip, int port, int timeout)
        {
            using (var c = new TcpClient())
            {
                try
                {
                    var ar = c.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeout)) return false;
                    c.EndConnect(ar);
                    return true;
                }
                catch (SocketException) { return false; }
            }
        }

        // 用 MagicDNS 名打开（https 的证书是发给这个名字的）；牌子上只写短名
        public static string Url(Device d, int port, bool full)
        {
            bool tls = port == 443 || port == 8443;
            string host = full ? (d.Dns != "" ? d.Dns : d.Ip) : d.Name;
            return (tls ? "https://" : "http://") + host + (port == 443 || port == 80 ? "" : ":" + port) + (full ? "/" : "");
        }
    }
}
