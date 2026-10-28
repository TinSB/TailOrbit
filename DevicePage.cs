// 本机页：这台电脑在 Tailscale 里叫什么、地址是多少；常用开关（tailscale set）和出口节点。TailOrbit 自己的设置在设置页
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TailOrbit
{
    class DevicePage : Page
    {
        // tailscale set 的参数名和界面上的说法
        static readonly string[][] Switches =
        {
            new[] { "shields-up", "阻止所有传入连接" },
            new[] { "accept-routes", "使用其他设备通告的子网路由" },
            new[] { "accept-dns", "使用 Tailscale DNS" },
            new[] { "unattended", "注销 Windows 后保持运行" },
            new[] { "auto-update", "自动更新" },
            new[] { "advertise-exit-node", "允许其他设备通过本机访问互联网" },
        };

        class Data
        {
            public Dictionary<string, object> Prefs, Status;
            public string Error;
        }

        class Exit
        {
            public string Name, Ip;
            public bool Online;
        }

        readonly Ellipse shieldRing = new Ellipse { Width = 140, Height = 140, StrokeThickness = 2, Opacity = 0 };
        readonly ScaleTransform badgeScale = new ScaleTransform(0, 0);
        readonly TextBlock name, dns, ip, account, version;
        readonly StackPanel exits = new StackPanel { Margin = new Thickness(0, 14, 0, 30) };
        readonly Dictionary<string, Toggle> toggles = new Dictionary<string, Toggle>();
        Dictionary<string, object> prefs = new Dictionary<string, object>();
        List<Exit> options = new List<Exit>();
        string current = "", ver = "", upstream;
        bool stepsOpen;
        int loads;

        public DevicePage(Host host) : base(host)
        {
            var grid = new Grid { Margin = new Thickness(48, 76, 48, 36) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左边：这台电脑
            var disc = new Grid { Width = 140, Height = 140, HorizontalAlignment = HorizontalAlignment.Center };
            shieldRing.Stroke = Th.Fg;
            disc.Children.Add(shieldRing);
            disc.Children.Add(new Ellipse { Width = 116, Height = 116, Fill = Th.Fg });
            disc.Children.Add(new IconView("monitor", 40, Th.Ink, 1.6));
            // 挡住所有连入时：外面多一圈，右下角挂一把锁
            disc.Children.Add(new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = Th.Fg, BorderBrush = Th.Bg, BorderThickness = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 8),
                Child = new IconView("lock", 15, Th.Ink, 2.2), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = badgeScale, ToolTip = Ui.Tip(Th, L.T("已阻止所有传入连接"))
            });
            name = new TextBlock { FontFamily = Ui.DisplayFont, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 20), TextTrimming = TextTrimming.CharacterEllipsis };
            dns = Kit.Mono("", 13, Th.Fg);
            ip = Kit.Mono("", 13, Th.Fg);
            account = Kit.Text("", 14, Th.Fg);
            version = Kit.Text("", 14, Th.Fg);
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(disc);
            left.Children.Add(name);
            var dnsRow = Kit.CopyRow(Th, dns);
            dnsRow.Margin = new Thickness(0, 0, 0, 8);
            left.Children.Add(dnsRow);
            left.Children.Add(Kit.CopyRow(Th, ip));
            var info = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            info.Children.Add(Kit.InfoRow(Th, L.T("账号"), account));
            info.Children.Add(Kit.InfoRow(Th, L.T("版本"), version));
            left.Children.Add(info);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            // 右边：出口节点和开关
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Kit.Title(Th, L.T("出口节点")));
            var sub = Kit.Text(L.T("选择用于转发互联网流量的设备"), 13, Th.Mute);
            sub.Margin = new Thickness(12, 3, 0, 0);
            head.Children.Add(sub);
            right.Children.Add(head);
            right.Children.Add(exits);
            right.Children.Add(Kit.Title(Th, L.T("选项")));
            var tiles = new UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 0) };
            for (int i = 0; i < Switches.Length; i++) tiles.Children.Add(SwitchTile(Switches[i][0], L.T(Switches[i][1]), i % 2 == 0));
            right.Children.Add(tiles);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
            Children.Add(grid);
            RenderExits();
        }

        public override void Shown()
        {
            Load();
        }

        async void Load()
        {
            int n = ++loads;
            var d = await Task.Run(() => Fetch());
            if (n != loads) return;
            Apply(d);
            if (upstream != null || d.Error != null) return;
            upstream = await Task.Run(() => Tailscale.Upstream()) ?? "";
            ShowVersion();
        }

        static Data Fetch()
        {
            var d = new Data();
            CmdResult g;
            try { g = Tailscale.Run("get --json"); }
            catch (Win32Exception) { d.Error = L.T("未安装 Tailscale"); return d; }   // 主界面在这一页上盖着安装提示
            if (g.Code != 0) { d.Error = Tailscale.FirstLine(g.Err, L.T("无法读取本机设置")); return d; }
            d.Prefs = Tailscale.Json(g.Out);
            var s = Tailscale.Run("status --json");
            if (s.Code == 0) d.Status = Tailscale.Json(s.Out);
            return d;
        }

        void Apply(Data d)
        {
            if (d.Error != null)
            {
                name.Text = d.Error;
                dns.Text = ip.Text = account.Text = version.Text = "";
                return;
            }
            prefs = d.Prefs ?? new Dictionary<string, object>();
            foreach (var t in toggles) t.Value.Set(true.Equals(Tailscale.Get(prefs, t.Key)), true);
            Shield(toggles["shields-up"].On, true);

            var self = Tailscale.Get(d.Status, "Self") as Dictionary<string, object>;
            string full = ((Tailscale.Get(self, "DNSName") as string) ?? "").TrimEnd('.');
            name.Text = full.Length > 0 ? full.Split('.')[0] : (Tailscale.Get(self, "HostName") as string) ?? "";
            dns.Text = full;
            var ips = Tailscale.Get(self, "TailscaleIPs") as object[];
            ip.Text = ips != null && ips.Length > 0 ? (ips[0] as string) ?? "" : "";
            var users = Tailscale.Get(d.Status, "User") as Dictionary<string, object>;
            var me = Tailscale.Get(users, Convert.ToString(Tailscale.Get(self, "UserID"), System.Globalization.CultureInfo.InvariantCulture)) as Dictionary<string, object>;
            account.Text = (Tailscale.Get(me, "LoginName") as string) ?? "";
            ver = ((Tailscale.Get(d.Status, "Version") as string) ?? "").Split('-')[0];
            ShowVersion();

            // 出口节点：所有声明了能当出口、后台也批准了的设备
            options = new List<Exit>();
            current = (Tailscale.Get(prefs, "exit-node") as string) ?? "";
            var peers = Tailscale.Get(d.Status, "Peer") as Dictionary<string, object>;
            if (peers != null)
                foreach (var p in peers.Values.OfType<Dictionary<string, object>>())
                {
                    if (!true.Equals(Tailscale.Get(p, "ExitNodeOption"))) continue;
                    var pips = Tailscale.Get(p, "TailscaleIPs") as object[];
                    string pdns = ((Tailscale.Get(p, "DNSName") as string) ?? "").TrimEnd('.');
                    var x = new Exit { Name = pdns.Length > 0 ? pdns.Split('.')[0] : (Tailscale.Get(p, "HostName") as string) ?? "", Ip = pips != null && pips.Length > 0 ? (pips[0] as string) ?? "" : "", Online = true.Equals(Tailscale.Get(p, "Online")) };
                    if (true.Equals(Tailscale.Get(p, "ExitNode"))) current = x.Ip;
                    options.Add(x);
                }
            options = options.OrderBy(x => x.Online ? 0 : 1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            RenderExits();
        }

        void ShowVersion()
        {
            if (ver == "") { version.Text = ""; return; }
            if (string.IsNullOrEmpty(upstream) || upstream == ver) version.Text = ver + (upstream == ver ? " · " + L.T("已是最新版本") : "");
            else version.Text = ver + " · " + L.T("有可用更新：{0}", upstream);
        }

        // ---------- 出口节点 ----------

        void RenderExits()
        {
            exits.Children.Clear();
            exits.Children.Add(Radio(L.T("不使用（直接连接）"), "", current == "", true, () => PickExit("")));
            foreach (var x in options)
            {
                var e = x;
                exits.Children.Add(Radio(e.Name, e.Online ? "" : L.T("离线"), current == e.Ip, e.Online, () => PickExit(e.Ip)));
            }
            if (options.Count > 0)
            {
                exits.Children.Add(SwitchTile("exit-node-allow-lan-access", L.T("使用出口节点时允许访问本地网络"), null));
                var t = toggles["exit-node-allow-lan-access"];
                t.Set(true.Equals(Tailscale.Get(prefs, "exit-node-allow-lan-access")), false);
                return;
            }
            // 一台能当出口的都没有：告诉他怎么开
            var label = Kit.Text(L.T("暂无可用的出口节点"), 14.5, Th.Mute);
            label.Margin = new Thickness(18, 0, 0, 0);
            var how = Kit.Button(Th, stepsOpen ? L.T("收起") : L.T("设置方法"), false, () => { stepsOpen = !stepsOpen; RenderExits(); });
            how.Height = 38;
            how.Margin = new Thickness(0, 0, 8, 0);
            var empty = Kit.Dashed(Th, new Grid { Height = 54, Children = { Ui.Columns(0, label, how) } }, 14);
            exits.Children.Add(empty);
            if (!stepsOpen) return;
            var steps = new StackPanel();
            steps.Children.Add(Step(1, L.T("在要作为出口节点的设备上运行"), Command("sudo tailscale set --advertise-exit-node")));
            steps.Children.Add(Step(2, L.T("在管理控制台中批准该设备作为出口节点"), Kit.Button(Th, L.T("打开管理控制台"), false, () => OpenUrl("https://login.tailscale.com/admin/machines"))));
            steps.Children.Add(Step(3, L.T("返回此处选择该设备"), null));
            var box = new Border { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(16, 8, 12, 8), CornerRadius = new CornerRadius(14), Background = Th.Tile, Child = steps };
            exits.Children.Add(box);
            Ui.AnimFrom(box, OpacityProperty, 0, 1, 220);
        }

        Border Radio(string text, string note, bool on, bool enabled, Action pick)
        {
            var dot = new Ellipse { Width = 18, Height = 18, Stroke = on ? Th.Fg : Th.Dim, StrokeThickness = on ? 5 : 1.5, VerticalAlignment = VerticalAlignment.Center };
            var t = Kit.Text(text, 14.5, enabled ? Th.Fg : Th.Mute);
            t.Margin = new Thickness(14, 0, 0, 0);
            var n = Kit.Text(note, 13, Th.Mute);
            var row = new Border { Height = 54, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(14), Background = Th.Tile, BorderBrush = on ? Th.Rim : Brushes.Transparent, BorderThickness = new Thickness(1), Cursor = enabled && !on ? Cursors.Hand : null, Child = Ui.Columns(1, dot, t, n) };
            if (!enabled || on) return row;
            row.MouseEnter += delegate { row.Background = Th.Chip; };
            row.MouseLeave += delegate { row.Background = Th.Tile; };
            row.MouseLeftButtonUp += delegate { pick(); };
            return row;
        }

        async void PickExit(string target)
        {
            string before = current;
            current = target;
            RenderExits();
            var r = await Task.Run(() => Tailscale.Run("set --exit-node=" + target));
            if (r.Code != 0)
            {
                current = before;
                RenderExits();
                Host.Notify(L.T("出口节点切换失败"), Tailscale.FirstLine(r.Err, L.T("tailscale set 执行失败")));
            }
            Load();
        }

        FrameworkElement Step(int n, string text, FrameworkElement end)
        {
            var num = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Th.Fill, Child = new TextBlock { Text = n.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var t = Kit.Text(text, 14, Th.Fg);
            t.Margin = new Thickness(12, 0, 12, 0);
            var row = end == null ? Ui.Columns(1, num, t) : Ui.Columns(1, num, t, end);
            row.Height = 46;
            return row;
        }

        // 一条命令：等宽字，旁边一个复制按钮
        FrameworkElement Command(string cmd)
        {
            var text = Kit.Mono(cmd, 12.5, Th.Fg);
            var row = Kit.CopyRow(Th, text);
            row.Height = 38;
            row.Background = Th.Bg;
            return row;
        }

        static void OpenUrl(string url)
        {
            Ui.Launch(() => Process.Start(url));
        }

        // ---------- 开关 ----------

        FrameworkElement SwitchTile(string key, string text, bool? leftColumn)
        {
            var t = new Toggle(Th, true.Equals(Tailscale.Get(prefs, key)));
            toggles[key] = t;
            var label = Kit.Text(text, 14.5, Th.Fg);
            label.Margin = new Thickness(0, 0, 12, 0);
            // 有的语言开关名长：放不下换到第二行，再放不下才截
            label.TextWrapping = TextWrapping.Wrap;
            label.LineHeight = 19;
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            label.MaxHeight = 40;   // 两行 38，留点余量：125% 缩放取整后会略超
            var shift = new TranslateTransform();
            var tile = new Border { Height = 62, Padding = new Thickness(18, 0, 16, 0), CornerRadius = new CornerRadius(14), Background = Th.Tile, Cursor = Cursors.Hand, Child = Ui.Columns(0, label, t), RenderTransform = shift };
            tile.Margin = leftColumn == null ? new Thickness(0, 0, 0, 8) : leftColumn.Value ? new Thickness(0, 0, 5, 10) : new Thickness(5, 0, 0, 10);
            tile.MouseEnter += delegate { tile.Background = Th.Chip; };
            tile.MouseLeave += delegate { tile.Background = Th.Tile; };
            tile.MouseLeftButtonUp += delegate { Flip(key, tile, shift); };
            return tile;
        }

        // 先把开关拨过去，再在后台真的改；没改成就拨回来、摇一下，悬停能看到原因
        async void Flip(string key, Border tile, TranslateTransform shift)
        {
            var t = toggles[key];
            bool want = !t.On;
            t.Set(want, true);
            if (key == "shields-up") Shield(want, true);
            var r = await Task.Run(() => Tailscale.Run("set --" + key + "=" + (want ? "true" : "false")));
            if (r.Code == 0)
            {
                prefs[key] = want;
                tile.ToolTip = null;
                return;
            }
            t.Set(!want, true);
            if (key == "shields-up") Shield(!want, true);
            Ui.Shake(shift);
            tile.ToolTip = Ui.Tip(Th, Tailscale.FirstLine(r.Err, L.T("更改失败")));
        }

        void Shield(bool on, bool animate)
        {
            Ui.AnimTo(shieldRing, OpacityProperty, on ? 1 : 0, animate ? 250 : 1);
            Ui.ScaleTo(badgeScale, on ? 1 : 0, animate ? 380 : 1, on ? Ui.Spring : Ui.Ease);
        }
    }
}
