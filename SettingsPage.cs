// 设置页：左边是这台电脑在 Tailscale 里叫什么（点一下改名）、地址、账号和 Tailscale 版本（有新版本时一键更新）；右边一节一节往下排：出口节点（标出推荐的那台，可以在不信任的网络上自动使用）、
// Tailscale 的常用开关（tailscale set）、共享本机所在的局域网、管理接口（连上以后网络页能直接批准、删设备、关密钥到期）、外观（跟随系统 / 浅色 / 深色）、
// 常规（开机自启、界面语言、连接变差时提醒）、剪贴板（剪贴板互通、和 Mac 共用剪贴板）、声音（在手机上收听），最后是 TailOrbit 自己的版本和更新（检查更新、自动下载更新）。右边放不下，用滚轮往下翻
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TailOrbit
{
    class SettingsPage : Page
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
        static readonly string[] Looks = { "auto", "light", "dark" };
        static readonly string[] LookNames = { "跟随系统", "浅色", "深色" };
        // 家用路由器最常用的几个网段：别处的局域网也多半是它，共享出去容易撞
        static readonly string[] Common = { "192.168.0.0/24", "192.168.1.0/24", "192.168.2.0/24", "192.168.31.0/24", "10.0.0.0/24", "10.0.1.0/24" };
        // 外观小样和程序图标同一套颜色（底、轨道、圆点），各有浅色、深色一份
        static readonly Color[] Paper = { Color.FromRgb(0xE6, 0xE8, 0xEB), Color.FromRgb(0x1A, 0x1C, 0x1F) };
        static readonly Color[] Orbit = { Color.FromRgb(0x80, 0x86, 0x8E), Color.FromRgb(0x8C, 0x92, 0x9A) };
        static readonly Color[] Dot = { Color.FromRgb(0x1A, 0x1C, 0x1F), Color.FromRgb(0xE7, 0xE9, 0xEC) };
        static double scrolled;   // 右边翻到了哪：换语言、换深浅色时整页重建，翻回原处

        class Data
        {
            public Dictionary<string, object> Prefs, Status;
            public string Error, Suggest = "", Subnet = "";   // Suggest：Tailscale 推荐的出口节点（短名字）；Subnet：本机所在的局域网网段
        }

        readonly Ellipse shieldRing = new Ellipse { Width = 140, Height = 140, StrokeThickness = 2, Opacity = 0 };
        readonly ScaleTransform badgeScale = new ScaleTransform(0, 0);
        readonly TextBlock name, dns, ip, account, tsVersion, tsNote, version, note, renameNote;
        readonly TextBox nameBox;
        readonly StackPanel exits = new StackPanel { Margin = new Thickness(0, 14, 0, 30) };
        readonly Dictionary<string, Toggle> toggles = new Dictionary<string, Toggle>();
        readonly Border[] lookTiles = new Border[3];
        readonly TextBlock[] lookNames = new TextBlock[3];
        readonly ProgressButton update, tsUpdate;
        Dictionary<string, object> prefs = new Dictionary<string, object>();
        List<ExitNode> options = new List<ExitNode>();
        string current = "", ver = "", upstream;
        bool stepsOpen;
        int loads;
        Popup autoPop, qrPop, langs, adminPop;                     // 自动使用哪一台的列表、剪贴板或声音的二维码、语言列表、管理接口
        DateTime autoClosed, qrClosed, langsClosed, adminClosed;   // 和它们上次收起的时间
        string selfId = "", suggested = "", lanNet = "", lanError, adminError;   // lanNet：共享着的网段，没共享时是本机所在的局域网
        List<string> selfRoutes = new List<string>();   // 后台批准了本机共享的哪些网段
        Toggle lanToggle;
        Border lanRow;
        TextBlock lanNote, adminNote;
        StackPanel adminBody;
        bool lanBusy, connecting, renaming, warned;
        readonly DispatcherTimer clip = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        readonly HashSet<string> stale = new HashSet<string>();   // 打开连接框之前剪贴板里就有的令牌、连不上的令牌：不自动拿
        DateTime watchUntil;   // 点“打开”去生成令牌后的 10 分钟：切去浏览器时连接框会自己收起，收起了也接着看剪贴板

        public SettingsPage(Host host) : base(host)
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
            name = new TextBlock { FontFamily = Ui.DisplayFont, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 20), TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.IBeam, ToolTip = Ui.Tip(Th, L.T("点击修改名称")) };
            // 改名：点名字变成输入框，Enter 确定、Esc 取消
            nameBox = new TextBox
            {
                FontFamily = Ui.DisplayFont, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, CaretBrush = Th.Fg, Background = Brushes.Transparent, BorderBrush = Th.Rim, BorderThickness = new Thickness(0, 0, 0, 1.5),
                HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(0), Margin = new Thickness(20, 13, 20, 17), Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform()
            };
            renameNote = new TextBlock { FontSize = 12.5, LineHeight = 18, Foreground = Th.Mute, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, -10, 0, 16), Visibility = Visibility.Collapsed };
            name.MouseLeftButtonUp += delegate { StartRename(); };
            nameBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) Rename();
                else if (e.Key == Key.Escape) EndRename(true);
            };
            nameBox.LostKeyboardFocus += delegate { if (!renaming) EndRename(false); };
            dns = Kit.Mono("", 13, Th.Fg);
            ip = Kit.Mono("", 13, Th.Fg);
            account = Kit.Text("", 14, Th.Fg);
            tsVersion = Kit.Text("", 14, Th.Fg);
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(disc);
            left.Children.Add(new Grid { Children = { name, nameBox } });
            left.Children.Add(renameNote);
            var dnsRow = Kit.CopyRow(Th, dns);
            dnsRow.Margin = new Thickness(0, 0, 0, 8);
            left.Children.Add(dnsRow);
            left.Children.Add(Kit.CopyRow(Th, ip));
            var info = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            info.Children.Add(Kit.InfoRow(Th, L.T("账号"), account));
            info.Children.Add(Kit.InfoRow(Th, L.T("Tailscale 版本"), tsVersion));
            left.Children.Add(info);
            // Tailscale 比官网的稳定版旧时亮出来：一键更新，下面是没更新成的原因
            tsUpdate = new ProgressButton(Th, "", UpdateTailscale) { Margin = new Thickness(0, 18, 0, 0), Visibility = Visibility.Collapsed };
            left.Children.Add(tsUpdate);
            tsNote = new TextBlock { FontSize = 12, LineHeight = 18, Foreground = Th.Bad, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            left.Children.Add(tsNote);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            // 右边：先是 Tailscale 的出口节点和开关，再是 TailOrbit 自己的外观、常规、剪贴板、声音和更新
            var right = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Kit.Title(Th, L.T("出口节点")));
            var sub = Kit.Text(L.T("选择用于转发互联网流量的设备"), 13, Th.Mute);
            sub.Margin = new Thickness(12, 3, 0, 0);
            head.Children.Add(sub);
            right.Children.Add(head);
            right.Children.Add(exits);
            right.Children.Add(Kit.Title(Th, "Tailscale"));
            right.Children.Add(Spaced(Kit.Group(Th, Switches.Select(x => SwitchRow(x[0], L.T(x[1]))).Concat(new[] { LanRow(), AdminRow() }).ToArray()), 30));
            right.Children.Add(Kit.Title(Th, L.T("外观")));
            var looks = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 14, -4, 30) };
            for (int i = 0; i < Looks.Length; i++) looks.Children.Add(LookTile(i));
            PaintLooks(Array.IndexOf(Looks, Settings.Theme));
            right.Children.Add(looks);
            right.Children.Add(Kit.Title(Th, L.T("常规")));
            right.Children.Add(Spaced(Kit.Group(Th, AutostartRow(), LanguageRow(), AlertsRow()), 30));
            right.Children.Add(Kit.Title(Th, L.T("剪贴板")));
            right.Children.Add(Spaced(Kit.Group(Th, QrRow(L.T("剪贴板互通"), () => Settings.ClipOn, ClipShare.Turn, () => ClipShare.Url), SyncRow()), 30));
            right.Children.Add(Kit.Title(Th, L.T("声音")));
            right.Children.Add(Spaced(Kit.Group(Th, QrRow(L.T("在手机上收听本机声音"), () => Settings.SoundOn, SoundShare.Turn, () => SoundShare.Url)), 30));
            // TailOrbit 自己：版本、自动下载更新，下面是检查更新的按钮和没更新成的原因
            var icon = new Image { Source = Ui.IconFrame(48, !Th.Dark), Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            right.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, Kit.Title(Th, "TailOrbit") } });
            version = Kit.Text("", 14, Th.Mute);
            var versionRow = Kit.Row(Th, L.T("版本"), version);
            versionRow.IsHitTestVisible = false;   // 只是看的：悬停不变亮
            right.Children.Add(Spaced(Kit.Group(Th, versionRow, AutoUpdateRow()), 0));
            update = new ProgressButton(Th, "", Updates.Next) { Margin = new Thickness(0, 14, 0, 0) };
            right.Children.Add(update);
            note = new TextBlock { FontSize = 12, LineHeight = 18, Foreground = Th.Bad, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            right.Children.Add(note);

            // 放不下的部分在窗口下边缘截断，用滚轮往下翻：滚动区伸到窗口底边、内容底下垫同样高；左右各让出一点，行摇一下时别被切边
            right.Margin = new Thickness(10, 0, 10, 36);
            var scroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false, Margin = new Thickness(-10, 0, -10, -36) };
            scroll.ScrollToVerticalOffset(scrolled);   // 排好版后才生效
            scroll.ScrollChanged += delegate { scrolled = scroll.VerticalOffset; Kit.FadeEdges(scroll); };
            Grid.SetColumn(scroll, 2);
            grid.Children.Add(scroll);
            Children.Add(grid);
            RenderExits();

            // 换了网络、自动开关过出口节点：看着这一页时跟着刷新；更新走到哪一步也跟着变
            Action changed = () => { if (IsVisible) Load(); };
            Loaded += delegate { AutoExit.Changed += changed; Updates.Changed += ShowUpdate; ShowUpdate(); };
            Unloaded += delegate { AutoExit.Changed -= changed; Updates.Changed -= ShowUpdate; clip.Stop(); };
            clip.Tick += delegate { PeekClipboard(); };
        }

        // 一组设置和上下的空
        static Border Spaced(Border group, double bottom)
        {
            group.Margin = new Thickness(0, 14, 0, bottom);
            return group;
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
            // 推荐的出口节点：Suggested exit node: 名字.网络.ts.net.（没有推荐时是另一句，老版本没有这个命令）
            var x = Tailscale.Run("exit-node suggest");
            var line = x.Code == 0 ? x.Out.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Suggested exit node:")) : null;
            if (line != null) d.Suggest = line.Substring(20).Trim().TrimEnd('.').Split('.')[0];
            d.Subnet = AutoExit.Subnet();
            return d;
        }

        void Apply(Data d)
        {
            if (d.Error != null)
            {
                name.Text = d.Error;
                dns.Text = ip.Text = account.Text = tsVersion.Text = "";
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
            selfId = (Tailscale.Get(self, "ID") as string) ?? "";
            selfRoutes = ((Tailscale.Get(self, "AllowedIPs") as object[]) ?? new object[0]).Concat((Tailscale.Get(self, "PrimaryRoutes") as object[]) ?? new object[0]).OfType<string>().ToList();
            suggested = d.Suggest;
            string routes = (Tailscale.Get(prefs, "advertise-routes") as string) ?? "";
            lanNet = routes != "" ? routes : d.Subnet;
            if (!lanBusy) ShowLan(routes != "", true);
            ShowAdminNote();

            // 出口节点：所有声明了能当出口、后台也批准了的设备
            options = AutoExit.Exits(d.Status);
            var used = options.FirstOrDefault(x => x.InUse);
            current = used != null ? used.Ip : (Tailscale.Get(prefs, "exit-node") as string) ?? "";
            RenderExits();
        }

        // 比官网的新版旧：稳定版下面亮出“更新 Tailscale 到 …”，测试版（版本号第二段是单数，官网的稳定版装不上去）只在后面写一句
        void ShowVersion()
        {
            Version v;
            bool old = Older(ver, upstream), stable = Version.TryParse(ver, out v) && v.Minor % 2 == 0;
            tsVersion.Text = ver == "" ? "" : upstream == ver ? ver + " · " + L.T("已是最新版本") : old && !stable ? ver + " · " + L.T("有可用更新：{0}", upstream) : ver;
            if (tsUpdate.Busy) return;
            tsUpdate.Text = L.T("更新 Tailscale 到 {0}", upstream);
            tsUpdate.Visibility = old && stable ? Visibility.Visible : Visibility.Collapsed;
        }

        // a 比 b 旧（版本号一段一段比）；读不出来的不算
        static bool Older(string a, string b)
        {
            Version x, y;
            return Version.TryParse(a, out x) && Version.TryParse(b ?? "", out y) && x < y;
        }

        // 和没装时的安装同一条路：从官网下载最新稳定版、验签名、弹一次管理员确认后覆盖安装；装好等新版的服务起来再刷新
        async void UpdateTailscale()
        {
            tsNote.Visibility = Visibility.Collapsed;
            tsUpdate.Text = L.T("正在下载 Tailscale…");
            tsUpdate.Progress(0);
            var w = Window.GetWindow(this);
            string why = await TailscaleSetup.Install(new Progress<double>(p => { if (p < 0) tsUpdate.Text = L.T("正在安装 Tailscale…"); tsUpdate.Progress(p); }), w == null ? IntPtr.Zero : new WindowInteropHelper(w).Handle);
            if (why == null)
                for (int i = 0; i < 20 && await Task.Run(() => Tailscale.Run("status --json").Code) != 0; i++) await Task.Delay(1000);
            tsUpdate.Progress(null);
            if (why != null) { tsNote.Text = why; tsNote.Visibility = Visibility.Visible; }
            ShowVersion();
            if (why == null) Load();
        }

        // ---------- 出口节点 ----------

        void RenderExits()
        {
            exits.Children.Clear();
            exits.Children.Add(Radio(L.T("不使用（直接连接）"), "", current == "", true, () => PickExit("")));
            foreach (var x in options)
            {
                var e = x;
                string note = !e.Online ? L.T("离线") : e.Ip == current && e.Ip == Settings.AutoExitActive ? L.T("自动") : e.Name.Equals(suggested, StringComparison.OrdinalIgnoreCase) ? L.T("推荐") : "";
                exits.Children.Add(Radio(e.Name, note, current == e.Ip, e.Online, () => PickExit(e.Ip)));
            }
            if (options.Count == 0) RenderEmpty();
            if (options.Count == 0 && Settings.AutoExitNode == "") return;
            var group = ExitGroup();
            group.Margin = new Thickness(0, options.Count > 0 ? 6 : 14, 0, 0);
            exits.Children.Add(group);
        }

        // 一台能当出口的都没有：告诉他怎么开
        void RenderEmpty()
        {
            var label = Kit.Text(L.T("暂无可用的出口节点"), 14.5, Th.Mute);
            label.Margin = new Thickness(18, 0, 0, 0);
            var how = Kit.Button(Th, stepsOpen ? L.T("收起") : L.T("设置方法"), false, () => { stepsOpen = !stepsOpen; RenderExits(); });
            how.Height = 38;
            how.Margin = new Thickness(0, 0, 8, 0);
            var empty = Kit.Dashed(Th, new Grid { Height = 50, Children = { Ui.Columns(0, label, how) } }, 14);
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

        // 出口节点的设置：允许访问本地网络、在不信任的网络上自动用哪一台；开着自动使用时还有当前网络信不信任
        Border ExitGroup()
        {
            var lanRow = SwitchRow("exit-node-allow-lan-access", L.T("使用出口节点时允许访问本地网络"));
            var net = AutoExit.Current;
            return Settings.AutoExitNode != "" && net != null ? Kit.Group(Th, lanRow, AutoRow(), TrustRow(net)) : Kit.Group(Th, lanRow, AutoRow());
        }

        // 在不信任的网络上自动使用哪一台：点一下在下面弹出一列，第一项是“关”
        Border AutoRow()
        {
            string target = Settings.AutoExitNode;
            var list = options;
            int at = list.FindIndex(x => x.Ip == target);
            var chev = new IconView("chevron-down", 14, Th.Mute) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) };
            var value = Kit.Text(target == "" ? L.T("关") : at < 0 ? target : list[at].Name, 14, Th.Mute);
            value.MaxWidth = 160;
            var row = Kit.Row(Th, L.T("在不信任的网络上自动使用"), new StackPanel { Orientation = Orientation.Horizontal, Children = { value, chev } });
            row.MouseEnter += delegate { chev.Stroke = Th.Fg; };
            row.MouseLeave += delegate { chev.Stroke = Th.Mute; };
            row.MouseLeftButtonUp += delegate
            {
                // 列表开着时再点这一行是想收起：按下那一下已经把它关了，别马上又开一个
                if (autoPop != null && (autoPop.IsOpen || (DateTime.Now - autoClosed).TotalMilliseconds < 250)) { autoPop.IsOpen = false; return; }
                var names = new[] { L.T("关") }.Concat(list.Select(x => x.Name)).ToArray();
                var notes = new string[] { null }.Concat(list.Select(x => x.Online ? null : L.T("离线"))).ToArray();
                autoPop = Kit.Choices(Th, row, 250, names, notes, target == "" ? 0 : at < 0 ? -1 : at + 1, k =>
                {
                    Settings.AutoExitNode = k == 0 ? "" : list[k - 1].Ip;
                    RenderExits();
                    AutoExit.Check(true);   // 人就在看：不弹通知
                });
                autoPop.Closed += delegate { autoClosed = DateTime.Now; };
            };
            return row;
        }

        // 信任当前网络（家里、公司）：在信任的网络上不自动使用出口节点；右边是网络在 Windows 里的名字
        Border TrustRow(AutoExit.Net net)
        {
            var t = new Toggle(Th, Settings.Trusted(net.Id));
            var name = Kit.Text(net.Name, 14, Th.Mute);
            name.MaxWidth = 180;
            name.Margin = new Thickness(0, 0, 12, 0);
            var row = Kit.Row(Th, L.T("信任当前网络"), new StackPanel { Orientation = Orientation.Horizontal, Children = { name, t } });
            row.ToolTip = Ui.Tip(Th, L.T("在信任的网络上（例如家里和公司）不会自动使用出口节点。"));
            row.MouseLeftButtonUp += delegate
            {
                t.Set(!t.On, true);
                Settings.SetTrusted(net.Id, t.On);
                AutoExit.Check(true);
            };
            return row;
        }

        Border Radio(string text, string note, bool on, bool enabled, Action pick)
        {
            var dot = new Ellipse { Width = 18, Height = 18, Stroke = on ? Th.Fg : Th.Dim, StrokeThickness = on ? 5 : 1.5, VerticalAlignment = VerticalAlignment.Center };
            var t = Kit.Text(text, 14.5, enabled ? Th.Fg : Th.Mute);
            t.Margin = new Thickness(14, 0, 0, 0);
            var n = Kit.Text(note, 13, Th.Mute);
            var row = new Border { Height = 50, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(14), Background = Th.Tile, BorderBrush = on ? Th.Rim : Brushes.Transparent, BorderThickness = new Thickness(1), Cursor = enabled && !on ? Cursors.Hand : null, Child = Ui.Columns(1, dot, t, n) };
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
            else AutoExit.Check(true);   // 自己选过：自动打开的那台不再归 TailOrbit 管
            Load();
        }

        // 一步：序号、一句话（放不下换行）、右边可选一个按钮
        FrameworkElement Step(int n, string text, FrameworkElement end)
        {
            var num = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Th.Fill, Child = new TextBlock { Text = n.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var t = Kit.Text(text, 14, Th.Fg);
            t.Margin = new Thickness(12, 6, 12, 6);
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            t.LineHeight = 20;
            var row = end == null ? Ui.Columns(1, num, t) : Ui.Columns(1, num, t, end);
            row.MinHeight = 46;
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

        // ---------- Tailscale 开关 ----------

        // 一个 tailscale set 开关，整行都能点
        Border SwitchRow(string key, string text)
        {
            var t = new Toggle(Th, true.Equals(Tailscale.Get(prefs, key)));
            toggles[key] = t;
            var row = Kit.Row(Th, text, t);
            row.MouseLeftButtonUp += delegate { Flip(key, row, (TranslateTransform)row.RenderTransform); };
            return row;
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

        // ---------- 共享局域网 ----------

        // 让其他设备经本机访问本机所在的局域网（tailscale set --advertise-routes=网段）。右边写网段，后台还没批准时跟一句“等待批准”；
        // 连着管理接口的打开时顺手批准。常见网段悬停时提醒可能和别处的局域网撞
        Border LanRow()
        {
            lanToggle = new Toggle(Th, false);
            lanNote = Kit.Text("", 14, Th.Mute);
            lanNote.MaxWidth = 220;
            lanNote.Margin = new Thickness(0, 0, 12, 0);
            lanRow = Kit.Row(Th, L.T("允许其他设备通过本机访问局域网"), new StackPanel { Orientation = Orientation.Horizontal, Children = { lanNote, lanToggle } });
            lanRow.MouseLeftButtonUp += delegate { FlipLan(); };
            return lanRow;
        }

        void ShowLan(bool on, bool animate)
        {
            lanToggle.Set(on, animate);
            bool waiting = on && lanNet.Split(',').Any(n => !selfRoutes.Contains(n.Trim()));
            lanNote.Text = lanNet == "" ? L.T("未找到局域网") : lanNet + (waiting ? " · " + L.T("等待批准") : "");
            lanRow.ToolTip = lanError != null ? Ui.Tip(Th, lanError) : Common.Contains(lanNet) ? Ui.Tip(Th, L.T("这个网段很常见：如果其他设备自己所在的局域网也是它，访问时会冲突。")) : null;
        }

        // 只改共享的网段；出口节点照它现在的样子一起传，不让它被顺带改掉
        async void FlipLan()
        {
            if (lanBusy || lanNet == "") return;
            lanBusy = true;
            bool want = !lanToggle.On, exit = toggles["advertise-exit-node"].On;
            string net = lanNet;
            lanToggle.Set(want, true);
            string failed;
            try
            {
                var r = await Task.Run(() => Tailscale.Run("set --advertise-routes=" + (want ? net : "") + " --advertise-exit-node=" + (exit ? "true" : "false")));
                failed = r.Code != 0 ? Tailscale.FirstLine(r.Err, L.T("更改失败")) : null;
            }
            catch (Win32Exception) { failed = L.T("更改失败"); }   // 刚好在更新、卸载 Tailscale：开关别卡住
            lanError = failed;
            if (failed == null && want && Admin.Connected) lanError = await Task.Run(() => Admin.Approve(selfId, net.Split(',')));
            lanBusy = false;
            if (failed != null) lanToggle.Set(!want, true);
            if (lanError != null) Ui.Shake((TranslateTransform)lanRow.RenderTransform);
            ShowLan(lanToggle.On, true);
            Load();
        }

        // ---------- 管理接口 ----------

        // 右边写连没连、还有几天到期；点开弹出连接（生成令牌、复制，程序自动拿走）或断开
        Border AdminRow()
        {
            adminNote = Kit.Text("", 14, Th.Mute);
            var chev = new IconView("chevron-down", 14, Th.Mute) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) };
            var row = Kit.Row(Th, L.T("管理接口"), new StackPanel { Orientation = Orientation.Horizontal, Children = { adminNote, chev } });
            row.MouseEnter += delegate { chev.Stroke = Th.Fg; };
            row.MouseLeave += delegate { chev.Stroke = Th.Mute; };
            row.MouseLeftButtonUp += delegate
            {
                // 开着时再点这一行是想收起：按下那一下已经把它关了，别马上又开一个
                if (adminPop != null && (adminPop.IsOpen || (DateTime.Now - adminClosed).TotalMilliseconds < 250)) { adminPop.IsOpen = false; return; }
                ShowAdmin(row);
            };
            ShowAdminNote();
            return row;
        }

        void ShowAdminNote()
        {
            var at = Admin.Expires;
            adminNote.Text = Admin.Expired ? L.T("令牌已过期") : !Admin.Connected ? L.T("未连接") : at == null ? L.T("已连接")
                : L.T("已连接 · {0} 天后到期", Math.Max(0, (int)Math.Floor((at.Value - DateTime.UtcNow).TotalDays)));
        }

        void ShowAdmin(FrameworkElement anchor)
        {
            const double width = 380;
            adminError = null;
            adminBody = new StackPanel();
            adminPop = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = Ui.RightUnder(anchor, width), VerticalOffset = 4 };
            adminPop.Closed += delegate { adminClosed = DateTime.Now; if (DateTime.Now > watchUntil) clip.Stop(); };
            FillAdmin();
            Kit.Pop(Th, adminPop, adminBody, width, 16, new Thickness(20, 18, 20, 18), 16);
            if (Admin.Connected) return;
            // 剪贴板里原来就有的令牌是别处用的：不自动拿
            try
            {
                var old = Api.Token.Match(Clipboard.ContainsText() ? Clipboard.GetText() : "");
                if (old.Success) stale.Add(old.Value);
            }
            catch (COMException) { }
            clip.Start();
        }

        // 连着：到期时间和断开；没连：三步（打开密钥页、复制、回来自动连上），也能粘贴进框里
        void FillAdmin()
        {
            adminBody.Children.Clear();
            var title = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, Margin = new Thickness(0, 0, 0, 6) };
            adminBody.Children.Add(title);
            if (Admin.Connected)
            {
                title.Text = L.T("已连接管理接口");
                adminBody.Children.Add(Para(L.T("网络页可以直接批准本机共享的出口节点和局域网、删除离线设备、关闭密钥到期；修改本机名称也会改管理控制台里的名称。")));
                var at = Admin.Expires;
                if (at != null) adminBody.Children.Add(Kit.InfoRow(Th, L.T("令牌到期"), Kit.Text(Ui.When(at.Value.ToLocalTime()), 14, Th.Fg)));
                var off = Kit.Danger(Th, L.T("断开"), () =>
                {
                    Admin.Disconnect();
                    ShowAdminNote();
                    adminPop.IsOpen = false;
                });
                off.Margin = new Thickness(0, 14, 0, 0);
                adminBody.Children.Add(off);
                adminBody.Children.Add(Para(L.T("断开后，请在管理控制台的“Keys”页面中撤销这个令牌。")));
                return;
            }
            title.Text = L.T("连接管理接口");
            adminBody.Children.Add(Para(L.T("连接后，网络页可以直接批准路由、删除离线设备、关闭密钥到期，也能修改本机在管理控制台里的名称。")));
            var steps = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var open = Kit.Button(Th, L.T("打开"), false, () => { watchUntil = DateTime.Now.AddMinutes(10); OpenUrl(Api.KeysPage); });
            open.Height = 34;
            steps.Children.Add(Step(1, L.T("在打开的网页中点击“Generate access token”"), connecting ? null : open));
            steps.Children.Add(Step(2, L.T("点击“Copy”复制生成的令牌"), null));
            steps.Children.Add(Step(3, connecting ? L.T("正在连接…") : L.T("返回此处，将自动连接"), null));
            adminBody.Children.Add(steps);
            if (!connecting) adminBody.Children.Add(PasteBox());
            adminBody.Children.Add(Para(L.T("令牌加密保存在本机，只发送给 Tailscale。")));
            if (adminError != null) adminBody.Children.Add(new TextBlock { Text = adminError, FontSize = 13, LineHeight = 19, Foreground = Th.Bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        }

        TextBlock Para(string text)
        {
            return new TextBlock { Text = text, FontSize = 13, LineHeight = 19, Foreground = Th.Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        }

        // 剪贴板读不到时（比如别的程序占着）也能手动粘贴
        FrameworkElement PasteBox()
        {
            var box = new TextBox { FontFamily = Ui.MonoFont, FontSize = 13, Foreground = Th.Fg, CaretBrush = Th.Fg, Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
            var hint = new TextBlock { Text = L.T("或粘贴到此处"), FontSize = 13, Foreground = Th.Dim, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            box.TextChanged += delegate
            {
                hint.Visibility = box.Text == "" ? Visibility.Visible : Visibility.Collapsed;
                var m = Api.Token.Match(box.Text);
                if (m.Success) GotToken(m.Value);
            };
            return new Border { Height = 42, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(12), Background = Th.Bg, Child = new Grid { Children = { hint, box } } };
        }

        // 每 0.6 秒看一眼剪贴板（只认 tskey-api- 开头的，别的不看不存）
        void PeekClipboard()
        {
            if (connecting || Admin.Connected || adminPop == null || (!adminPop.IsOpen && DateTime.Now > watchUntil)) { clip.Stop(); return; }
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (COMException) { return; }   // 别的程序正开着剪贴板：下次再看
            var m = Api.Token.Match(text);
            if (m.Success && !stale.Contains(m.Value)) GotToken(m.Value);
        }

        async void GotToken(string token)
        {
            if (connecting) return;
            connecting = true;
            clip.Stop();
            // 拿到就从剪贴板里清掉（不管是读到的还是粘贴进框里的），令牌不留在外面
            try { if (Clipboard.ContainsText() && Clipboard.GetText().Contains(token)) Clipboard.Clear(); }
            catch (COMException) { }
            adminError = null;
            FillAdmin();
            string why;
            try { why = await Task.Run(() => Admin.Connect(token)); }
            catch (Exception e) { why = e.Message; }   // 存不下令牌这类没想到的错：也要回到能重来的状态，不能卡在“正在连接”
            connecting = false;
            adminError = why;
            ShowAdminNote();
            // 连接框已经收起了（还在浏览器里时从剪贴板拿到的）：结果用通知告诉
            if (why == null)
            {
                watchUntil = DateTime.MinValue;
                if (adminPop.IsOpen) adminPop.IsOpen = false;
                else Host.Notify(L.T("已连接管理接口"), L.T("网络页可以直接批准本机共享的出口节点和局域网、删除离线设备、关闭密钥到期；修改本机名称也会改管理控制台里的名称。"));
                Load();
                return;
            }
            stale.Add(token);   // 连不上的这把别再自动拿
            FillAdmin();
            if (!adminPop.IsOpen) Host.Notify(L.T("连接管理接口"), why);
            if (adminPop.IsOpen || DateTime.Now < watchUntil) clip.Start();
        }

        // ---------- 改名 ----------

        void StartRename()
        {
            if (selfId == "" || renaming) return;   // 还没读到本机
            warned = false;
            renameNote.Visibility = Visibility.Collapsed;
            nameBox.Text = name.Text;
            name.Visibility = Visibility.Hidden;
            nameBox.Visibility = Visibility.Visible;
            nameBox.Focus();
            nameBox.SelectAll();
        }

        void EndRename(bool clear)
        {
            nameBox.Visibility = Visibility.Collapsed;
            name.Visibility = Visibility.Visible;
            if (clear) renameNote.Visibility = Visibility.Collapsed;
        }

        void RenameNote(string text, bool bad)
        {
            renameNote.Text = text;
            renameNote.Foreground = bad ? Th.Bad : Th.Mute;
            renameNote.Visibility = Visibility.Visible;
        }

        // 连着管理接口改后台的名字；没连改本机的主机名（后台没改过名的设备，名字跟着主机名走）。开着剪贴板、收听时先提醒网址会变
        async void Rename()
        {
            string want = nameBox.Text.Trim();
            if (want == "" || want == name.Text) { EndRename(true); return; }
            if (!Regex.IsMatch(want, "^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$"))
            {
                RenameNote(L.T("名称只能包含字母、数字和连字符，且不能以连字符开头或结尾。"), true);
                Ui.Shake((TranslateTransform)nameBox.RenderTransform);
                return;
            }
            if ((Settings.ClipOn || Settings.SoundOn) && !warned)
            {
                warned = true;
                RenameNote(L.T("改名后，手机上存的网址和 iPhone App 里的网址都要换成新的。再按一次 Enter 确认。"), false);
                return;
            }
            renaming = true;
            nameBox.IsEnabled = false;
            string why = null;
            bool admin = Admin.Connected;
            try
            {
                if (admin) why = await Task.Run(() => Admin.Rename(selfId, want.ToLowerInvariant()));
                else
                {
                    var r = await Task.Run(() => Tailscale.Run("set --hostname=" + want));
                    if (r.Code != 0) why = Tailscale.FirstLine(r.Err, L.T("更改失败"));
                }
            }
            catch (Win32Exception) { why = L.T("更改失败"); }   // 刚好在更新、卸载 Tailscale：输入框别卡住
            nameBox.IsEnabled = true;
            if (why != null)
            {
                renaming = false;
                RenameNote(why, true);
                Ui.Shake((TranslateTransform)nameBox.RenderTransform);
                nameBox.Focus();
                return;
            }
            EndRename(true);
            // 后台把新名字推回本机要一两秒：等它出现；改了主机名名字却一直没变，是在管理控制台里改过名（不再跟着主机名走）
            string before = name.Text;
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(1000);
                var d = await Task.Run(() => Fetch());
                Apply(d);
                if (name.Text != before) break;
            }
            renaming = false;
            if (name.Text == before && !admin) RenameNote(L.T("名称没有变化：这台设备在管理控制台中改过名，需要在那里修改，或先连接管理接口。"), true);
        }

        // ---------- TailOrbit 的更新 ----------

        // 更新走到哪一步：版本那一行、按钮和下面的原因跟着变
        void ShowUpdate()
        {
            string s = Updates.State, v = Setup.Version;
            version.Text = s == "latest" ? v + " · " + L.T("已是最新版本") : Updates.Latest != "" ? v + " · " + L.T("有可用更新：{0}", Updates.Latest) : v;
            update.Text = s == "checking" ? L.T("正在检查…") : s == "available" ? L.T("立即更新") : s == "downloading" ? L.T("正在下载…")
                : s == "ready" ? L.T("重启并更新") : s == "installing" ? L.T("正在更新…") : s == "failed" ? L.T("重试") : L.T("检查更新");
            update.Progress(s == "downloading" ? Updates.Fraction : s == "checking" || s == "installing" ? -1 : (double?)null);
            note.Text = s == "failed" ? L.T(Updates.Error) : "";
            note.Visibility = s == "failed" ? Visibility.Visible : Visibility.Collapsed;
        }

        // 自动下载更新：整行都能点；打开时马上在后台看一次
        Border AutoUpdateRow()
        {
            var t = new Toggle(Th, Settings.AutoUpdate);
            var row = Kit.Row(Th, L.T("自动下载更新"), t);
            row.MouseLeftButtonUp += delegate
            {
                t.Set(!t.On, true);
                Updates.Auto(t.On);
            };
            return row;
        }

        // ---------- 外观 ----------

        // 三块小样：浅色、深色，跟随系统是左浅右深；选中的那块描一圈
        Border LookTile(int i)
        {
            var label = new TextBlock { Text = L.T(LookNames[i]), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 10, 0, 0) };
            var pop = new ScaleTransform(1, 1);
            var tile = new Border
            {
                Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(10, 10, 10, 11), CornerRadius = new CornerRadius(14), Background = Th.Tile, BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = pop, Child = new StackPanel { Children = { Sample(Looks[i]), label } }
            };
            lookTiles[i] = tile;
            lookNames[i] = label;
            tile.MouseEnter += delegate { tile.Background = Th.Chip; };
            tile.MouseLeave += delegate { tile.Background = Th.Tile; };
            string mode = Looks[i];
            tile.MouseLeftButtonUp += delegate
            {
                if (Settings.Theme == mode) return;
                PaintLooks(i);
                Ui.ScaleFrom(pop, 0.95, 1, 360, 0, Ui.Spring);
                Ui.After(200, () =>   // 让这一下先弹完，再整页换
                {
                    Host.SetTheme(mode);
                    if (Settings.Theme != mode) PaintLooks(Array.IndexOf(Looks, Settings.Theme));   // 有文件正在发：没换成，选回原来那块
                });
            };
            return tile;
        }

        void PaintLooks(int on)
        {
            for (int i = 0; i < lookTiles.Length; i++)
            {
                lookTiles[i].BorderBrush = i == on ? Th.Fg : Brushes.Transparent;
                lookNames[i].Foreground = i == on ? Th.Fg : Th.Mute;
                lookNames[i].FontWeight = i == on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // 一块小样：底色上一圈轨道、中间一个圆点、轨道上一颗卫星，和程序图标一个样子
        FrameworkElement Sample(string mode)
        {
            Func<Color[], Brush> paint = c => mode == "light" ? new SolidColorBrush(c[0]) : mode == "dark" ? new SolidColorBrush(c[1]) : Split(c[0], c[1]);
            var g = new Grid { Height = 50 };
            g.Children.Add(new Ellipse { Width = 34, Height = 34, Stroke = paint(Orbit), StrokeThickness = 1.5 });
            g.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = paint(Dot) });
            double a = 41 * Math.PI / 180;   // 卫星在右上，跟随系统时落在深色那半边
            g.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Dot[mode == "light" ? 0 : 1]), RenderTransform = new TranslateTransform(17 * Math.Sin(a), -17 * Math.Cos(a)) });
            return new Border { CornerRadius = new CornerRadius(9), Background = paint(Paper), BorderBrush = Th.Hair, BorderThickness = new Thickness(1), Child = g };
        }

        // 左半边一种颜色、右半边另一种，中间一条直边
        static Brush Split(Color left, Color right)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            b.GradientStops.Add(new GradientStop(left, 0.5));
            b.GradientStops.Add(new GradientStop(right, 0.5));
            return b;
        }

        // ---------- 常规：开机自启、语言、连接变差时提醒 ----------

        Border AlertsRow()
        {
            var t = new Toggle(Th, Settings.NetAlerts);
            var row = Kit.Row(Th, L.T("连接变差时提醒"), t);
            row.ToolTip = Ui.Tip(Th, L.T("正在通信的设备改走中继、或延迟比平时高出很多时发通知。"));
            row.MouseLeftButtonUp += delegate
            {
                t.Set(!t.On, true);
                Settings.NetAlerts = t.On;
            };
            return row;
        }

        Border AutostartRow()
        {
            var t = new Toggle(Th, Autostart.Enabled);
            var row = Kit.Row(Th, L.T("开机时自动启动"), t);
            row.MouseLeftButtonUp += delegate
            {
                string why = null;
                try { Autostart.Set(!t.On); }
                catch (UnauthorizedAccessException e) { why = e.Message; }   // 注册表被策略锁着：开关拨回原样、摇一下，悬停看原因
                catch (System.Security.SecurityException e) { why = e.Message; }
                t.Set(Autostart.Enabled, true);
                row.ToolTip = why == null ? null : Ui.Tip(Th, why);
                if (why != null) Ui.Shake((TranslateTransform)row.RenderTransform);
            };
            return row;
        }

        // 语言：点一下在下面弹出一列，选了马上换
        Border LanguageRow()
        {
            int cur = Array.IndexOf(L.Codes, Settings.Language);
            var chev = new IconView("chevron-down", 14, Th.Mute) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) };
            var value = new StackPanel { Orientation = Orientation.Horizontal, Children = { Kit.Text(cur < 0 ? L.T("跟随系统") : L.Names[cur], 14, Th.Mute), chev } };
            var row = Kit.Row(Th, L.T("语言"), value);
            row.MouseEnter += delegate { chev.Stroke = Th.Fg; };
            row.MouseLeave += delegate { chev.Stroke = Th.Mute; };
            row.MouseLeftButtonUp += delegate
            {
                // 列表开着时再点这一行是想收起：按下那一下已经把它关了，别马上又开一个
                if (langs != null && (langs.IsOpen || (DateTime.Now - langsClosed).TotalMilliseconds < 250)) { langs.IsOpen = false; return; }
                Languages(row);
            };
            return row;
        }

        void Languages(FrameworkElement anchor)
        {
            var names = new[] { L.T("跟随系统") }.Concat(L.Names).ToArray();
            langs = Kit.Choices(Th, anchor, 210, names, null, Array.IndexOf(L.Codes, Settings.Language) + 1, k =>
                Ui.After(120, () => Host.SetLanguage(k == 0 ? "auto" : L.Codes[k - 1])));   // 等弹出的列表收起再整页换
            langs.Closed += delegate { langsClosed = DateTime.Now; };
        }

        // ---------- 剪贴板 ----------

        // 剪贴板互通（开着时自己的手机、Mac 能读这台电脑的剪贴板，iPhone 能把它的发过来）、在手机上收听本机声音：
        // 开着时开关左边弹出一个二维码按钮；刚打开时直接把二维码亮出来给手机扫。current 读开没开，turn 去开关，url 是开着时给手机的网址
        Border QrRow(string text, Func<bool> current, Func<bool, string> turn, Func<string> url)
        {
            var t = new Toggle(Th, current());
            var scale = new ScaleTransform(t.On ? 1 : 0, t.On ? 1 : 0);
            var glyph = new IconView("qr", 17, Th.Mute);
            var qr = new Border
            {
                Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = glyph,
                ToolTip = Ui.Tip(Th, L.T("手机扫码")), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, IsHitTestVisible = t.On
            };
            qr.MouseEnter += delegate { qr.Background = Th.Hair; glyph.Stroke = Th.Fg; };
            qr.MouseLeave += delegate { qr.Background = Brushes.Transparent; glyph.Stroke = Th.Mute; };
            var row = Kit.Row(Th, text, new StackPanel { Orientation = Orientation.Horizontal, Children = { qr, t } });
            var shift = (TranslateTransform)row.RenderTransform;
            qr.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;   // 别让这一行当成拨开关
                // 二维码开着时再点是想收起：按下那一下已经把它关了，别马上又开一个
                if (qrPop != null && (qrPop.IsOpen || (DateTime.Now - qrClosed).TotalMilliseconds < 250)) { qrPop.IsOpen = false; return; }
                ShowQr(row, url());
            };
            bool busy = false;
            row.MouseLeftButtonUp += async delegate
            {
                if (busy) return;
                busy = true;
                bool want = !t.On;
                t.Set(want, true);
                string why = await Task.Run(() => turn(want));
                busy = false;
                if (why != null)
                {
                    t.Set(!want, true);   // 没开成、没关成：拨回原样、摇一下，悬停看原因
                    row.ToolTip = Ui.Tip(Th, why);
                    Ui.Shake(shift);
                    return;
                }
                row.ToolTip = null;
                qr.IsHitTestVisible = want;
                Ui.ScaleTo(scale, want ? 1 : 0, want ? 420 : 200, want ? Ui.Spring : Ui.Ease);
                if (want) ShowQr(row, url());
            };
            // 在托盘菜单里拨过：开关和二维码按钮跟着变
            Action sync = () =>
            {
                bool on = current();
                if (busy || t.On == on) return;
                t.Set(on, true);
                row.ToolTip = null;
                qr.IsHitTestVisible = on;
                Ui.ScaleTo(scale, on ? 1 : 0, on ? 420 : 200, on ? Ui.Spring : Ui.Ease);
            };
            row.Loaded += delegate { Host.Toggled += sync; };
            row.Unloaded += delegate { Host.Toggled -= sync; };
            return row;
        }

        // 和 Mac 共用剪贴板：开着时开关左边一台小电脑，连上 Mac 时亮着，Mac 睡着时暗着（悬停看连的是哪台、为什么没连上）
        Border SyncRow()
        {
            var t = new Toggle(Th, Settings.SyncMac);
            var glyph = new IconView("laptop", 18, Th.Mute) { Margin = new Thickness(0, 0, 12, 0), Opacity = t.On ? 1 : 0 };
            var row = Kit.Row(Th, L.T("与 Mac 共享剪贴板"), new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, t } });
            var shift = (TranslateTransform)row.RenderTransform;
            Action show = () =>
            {
                string peer = ClipSync.Peer;
                glyph.Stroke = peer != null ? Th.Fg : Th.Mute;
                glyph.IsHitTestVisible = t.On;
                glyph.ToolTip = Ui.Tip(Th, peer != null ? L.T("已连接 {0}", peer) : ClipSync.Problem ?? L.T("等待 Mac 唤醒"));
                Ui.AnimTo(glyph, OpacityProperty, t.On ? 1 : 0, 200);
            };
            bool busy = false;
            // 在托盘菜单里拨过：开关跟着变
            Action sync = () =>
            {
                if (busy || t.On == Settings.SyncMac) return;
                t.Set(Settings.SyncMac, true);
                row.ToolTip = null;
                show();
            };
            row.Loaded += delegate { ClipSync.Changed += show; Host.Toggled += sync; show(); };
            row.Unloaded += delegate { ClipSync.Changed -= show; Host.Toggled -= sync; };
            row.MouseLeftButtonUp += async delegate
            {
                if (busy) return;
                busy = true;
                bool want = !t.On;
                t.Set(want, true);
                string why = await Task.Run(() => ClipSync.Turn(want));
                busy = false;
                if (why != null)
                {
                    t.Set(!want, true);   // 没开成：拨回原样、摇一下，悬停看原因
                    row.ToolTip = Ui.Tip(Th, why);
                    Ui.Shake(shift);
                    return;
                }
                row.ToolTip = null;
                show();
            };
            return row;
        }

        // 二维码一直是浅底深点，深色主题下也扫得出来；下面是网址，可以复制了发给自己的 Mac
        void ShowQr(FrameworkElement anchor, string url)
        {
            var m = url == null ? null : Qr.Encode(url);
            if (m == null) return;
            const double width = 264, pad = 16;   // pad：给阴影留的边
            var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = Ui.RightUnder(anchor, width), VerticalOffset = 4 };
            qrPop = popup;
            popup.Closed += delegate { qrClosed = DateTime.Now; };
            var tile = new Border { Width = 212, Height = 212, Padding = new Thickness(17), CornerRadius = new CornerRadius(14), Background = SharePage.QrPaper, Child = new QrView(m, SharePage.QrInk) };
            var hint = new TextBlock { Text = L.T("使用已登录 Tailscale 的手机扫码即可打开"), FontSize = 13, LineHeight = 19, Foreground = Th.Mute, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(4, 14, 4, 12) };
            var link = Kit.CopyRow(Th, Kit.Mono(url, 12, Th.Fg));
            link.Background = Th.Bg;
            Kit.Pop(Th, popup, new StackPanel { Children = { tile, hint, link } }, width, pad, new Thickness(14, 16, 14, 14), 16);
        }
    }
}
