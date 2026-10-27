// 网络页：本机在中间，直连的设备在里圈、经中继的在中圈、离线的在外圈，方向跟星网那页一致；
// 连线上标着延迟，正在传东西的那条变粗、光点顺着传的方向流，名字下面写速度。
// 右边是体检（打洞、IPv6、NAT、端口映射、中继、版本、密钥到期）和要处理的事，底下是这次开机以来的流量走了哪条路
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TailOrbit
{
    class NetPage : Page
    {
        static readonly Dictionary<string, string> Cities = new Dictionary<string, string>
        {
            { "nyc", "纽约" }, { "sfo", "旧金山" }, { "sin", "新加坡" }, { "fra", "法兰克福" }, { "syd", "悉尼" }, { "blr", "班加罗尔" }, { "tok", "东京" },
            { "lhr", "伦敦" }, { "dfw", "达拉斯" }, { "sea", "西雅图" }, { "sao", "圣保罗" }, { "ord", "芝加哥" }, { "den", "丹佛" }, { "ams", "阿姆斯特丹" },
            { "jnb", "约翰内斯堡" }, { "mia", "迈阿密" }, { "lax", "洛杉矶" }, { "par", "巴黎" }, { "mad", "马德里" }, { "hkg", "香港" }, { "tor", "多伦多" },
            { "waw", "华沙" }, { "dbi", "迪拜" }, { "hnl", "檀香山" }, { "nai", "内罗毕" }, { "nue", "纽伦堡" }, { "iad", "阿什本" }, { "hel", "赫尔辛基" },
        };
        static readonly Regex Pong = new Regex(@"^pong from .* via (\S+) in ([\d.]+)ms");
        static readonly Regex Metric = new Regex(@"^tailscaled_(inbound|outbound)_bytes_total\{path=""(\w+)""\} (\d+)");
        const double Busy = 8 * 1024;   // 每秒超过这么多字节才算在传东西

        // 地图上的一台设备
        class Dot
        {
            public string Ip = "", Name = "", Os = "", Addr = "", Relay = "";
            public bool Online, Active;
            public DateTime? LastSeen, KeyExpiry;
            public long Rx, Tx;
            public readonly Canvas View = new Canvas();
            public readonly TranslateTransform At = new TranslateTransform();
            public readonly ScaleTransform Pop = new ScaleTransform(1, 1);
            public Grid Disc;
            public SolidColorBrush Face, Rim, Ink;
            public IconView Icon;
            public TextBlock Title, Note;
            public StackPanel Label;
            public Line Link, Flow;
            public Grid Chip;
            public TextBlock ChipText;
            public Rectangle ChipEdge;
            public double Angle = double.NaN, Down, Up;
            public string Ring = "";
            public Point Pos;
            public long LastRx = -1, LastTx = -1;
            public DateTime LastAt;
            public int Ms = -1, FlowDir;
            public string Via = "";   // 探到的路：空是直连，否则是中继所在地的代码
            public DateTime PingAt = DateTime.MinValue;
            public bool Pinging;
        }

        // 后台读到的一份
        class Snap
        {
            public Dictionary<string, object> Status;
            public Dictionary<string, long> Traffic;
            public List<ServeEntry> Shares;
            public string Upstream;
            public DateTime At = DateTime.Now;
        }

        readonly Func<string, double> homeAngle;
        readonly Canvas field = new Canvas { ClipToBounds = true, Margin = new Thickness(0, 44, 432, 0) };
        readonly Canvas lines = new Canvas { IsHitTestVisible = false }, chips = new Canvas { IsHitTestVisible = false }, layer = new Canvas();
        readonly Ellipse[] rings = new Ellipse[3];
        readonly ScaleTransform[] ringScale = { new ScaleTransform(1, 1), new ScaleTransform(1, 1), new ScaleTransform(1, 1) };
        readonly Sweep sweep;
        readonly RotateTransform sweepTurn = new RotateTransform();
        readonly Grid core;
        readonly ScaleTransform corePop = new ScaleTransform(1, 1);
        readonly IconView coreIcon;
        readonly TextBlock coreName;
        readonly Border panel;
        readonly TextBlock when, down, up;
        readonly StackPanel checks = new StackPanel(), issues = new StackPanel();
        readonly Grid bar = new Grid { Height = 10, Margin = new Thickness(0, 14, 0, 0) };
        readonly WrapPanel legend = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        readonly Brush line, lineHot, chipEdge, upInk;
        readonly Dictionary<string, Dot> dots = new Dictionary<string, Dot>();
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        readonly DispatcherTimer clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        Point o;
        double r1 = 150, r2 = 250, r3 = 330, coreSize = 94, dotSize = 78;
        bool visible, loaded, polling, checking;
        string checksShown, issuesShown, trafficShown;   // 右边三块上次画的内容：没变就不重建
        int ticks;
        Dictionary<string, object> net;
        DateTime netAt = DateTime.MinValue, sharesAt = DateTime.MinValue;
        string ver = "", upstream, selfRelay = "", selfName = "";
        DateTime? selfExpiry;
        List<string> health = new List<string>();
        List<ServeEntry> shares = new List<ServeEntry>();
        Dictionary<string, long> traffic = new Dictionary<string, long>();

        public NetPage(Host host, Func<string, double> homeAngle) : base(host)
        {
            this.homeAngle = homeAngle;
            line = Frozen(Theme.Fade(Th.FgColor, 0.24));
            lineHot = Frozen(Theme.Fade(Th.FgColor, 0.45));
            chipEdge = Frozen(Theme.Fade(Th.FgColor, 0.14));
            upInk = Frozen(Theme.Fade(Th.FgColor, 0.72));

            // 三圈：里圈直连（实线）、中圈经中继（虚线）、外圈离线（点）
            double[] alpha = { 0.10, 0.13, 0.16 };
            for (int i = 0; i < 3; i++)
            {
                rings[i] = new Ellipse { Stroke = Frozen(Theme.Fade(Th.FgColor, alpha[i])), StrokeThickness = i == 2 ? 1.5 : 1, StrokeDashCap = PenLineCap.Round, IsHitTestVisible = false, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = ringScale[i] };
                field.Children.Add(rings[i]);
            }
            // 体检时扫一圈的雷达光
            sweep = new Sweep(Th.FgColor) { Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = sweepTurn };
            field.Children.Add(sweep);
            field.Children.Add(lines);
            field.Children.Add(chips);
            coreIcon = new IconView("monitor", 32, Th.Ink);
            core = new Grid { Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = corePop };
            core.Children.Add(new Ellipse { Fill = Th.Fg });
            core.Children.Add(coreIcon);
            coreName = new TextBlock { FontSize = 14, Foreground = Th.Mute, Opacity = 0, IsHitTestVisible = false };
            field.Children.Add(core);
            field.Children.Add(coreName);
            field.Children.Add(layer);

            // 右边：体检、要处理的事，底下是流量
            when = Kit.Text("", 13, Th.Mute);
            when.Margin = new Thickness(10, 2, 0, 0);
            var refresh = Kit.IconButton(Th, "refresh", L.T("重新检测"), Check, 40);
            refresh.Background = Th.Hair;
            var head = Ui.Columns(1, Kit.Title(Th, L.T("网络检测")), when, refresh);
            head.Margin = new Thickness(0, 0, 0, 8);
            down = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg };
            up = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = upInk, Margin = new Thickness(18, 0, 0, 0) };
            var totals = new StackPanel { Orientation = Orientation.Horizontal };
            totals.Children.Add(down);
            totals.Children.Add(up);
            var foot = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            foot.Children.Add(totals);
            foot.Children.Add(bar);
            foot.Children.Add(legend);
            var footBox = new Border { BorderBrush = Th.Hair, BorderThickness = new Thickness(0, 1, 0, 0), Child = foot, ToolTip = Ui.Tip(Th, L.T("自 Tailscale 本次启动以来的收发流量")) };
            var dock = new DockPanel();
            DockPanel.SetDock(footBox, Dock.Bottom);
            dock.Children.Add(footBox);
            var top = new StackPanel();
            top.Children.Add(head);
            top.Children.Add(checks);
            top.Children.Add(issues);
            dock.Children.Add(top);
            panel = new Border
            {
                Width = 404, Margin = new Thickness(0, 66, 28, 32), HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(18),
                Background = Th.Pop, BorderBrush = Th.Hair, BorderThickness = new Thickness(1), Padding = new Thickness(24, 22, 24, 22), Opacity = 0, Child = dock
            };
            Children.Add(field);
            Children.Add(panel);
            field.SizeChanged += delegate { Layout(false); };
            SizeChanged += delegate
            {
                double w = Math.Max(340, Math.Min(404, ActualWidth * 0.36));
                panel.Width = w;
                field.Margin = new Thickness(0, 44, w + 28, 0);
                Dispatcher.BeginInvoke(new Action(RenderIssues), DispatcherPriority.Loaded);   // 面板高度变了，重算放得下几条
            };
            poll.Tick += delegate { if (!Away) Poll(); };
            clock.Tick += delegate { if (!Away) RenderChecks(); };
            RenderChecks();
        }

        public override void Shown()
        {
            visible = true;
            poll.Start();
            clock.Start();
            foreach (var d in dots.Values) Paint(d);
            Poll();
            if ((DateTime.Now - netAt).TotalSeconds > 60) Check();
        }

        public override void Hidden()
        {
            visible = false;
            poll.Stop();
            clock.Stop();
            foreach (var d in dots.Values) SetFlow(d, 0);
        }

        // ---------- 读 ----------

        async void Poll()
        {
            if (polling) return;
            polling = true;
            bool full = ticks++ % 4 == 0;                                   // 流量每 6 秒读一次
            bool wantShares = (DateTime.Now - sharesAt).TotalSeconds > 30;  // 分享半分钟读一次
            bool wantUpstream = upstream == null;
            Snap s;
            try { s = await Task.Run(() => Read(full, wantShares, wantUpstream)); }
            finally { polling = false; }
            Apply(s);
        }

        static Snap Read(bool full, bool wantShares, bool wantUpstream)
        {
            var s = new Snap();
            try
            {
                var r = Tailscale.Run("status --json");
                if (r.Code == 0) s.Status = Tailscale.Json(r.Out);
                if (full)
                {
                    var m = Tailscale.Run("metrics print");
                    if (m.Code == 0) s.Traffic = ParseMetrics(m.Out);
                }
                if (wantShares) s.Shares = Shares.Load();
                if (wantUpstream) s.Upstream = Tailscale.Upstream() ?? "";
            }
            catch (Win32Exception) { }   // 没装 Tailscale
            return s;
        }

        static Dictionary<string, long> ParseMetrics(string text)
        {
            var map = new Dictionary<string, long>();
            foreach (var raw in text.Split('\n'))
            {
                var m = Metric.Match(raw.Trim());
                long n;
                if (m.Success && long.TryParse(m.Groups[3].Value, out n)) map[m.Groups[1].Value + "/" + m.Groups[2].Value] = n;
            }
            return map;
        }

        void Apply(Snap s)
        {
            if (s.Traffic != null) traffic = s.Traffic;
            if (s.Shares != null) { shares = s.Shares; sharesAt = DateTime.Now; }
            if (s.Upstream != null) upstream = s.Upstream;
            if (s.Status == null) { RenderChecks(); return; }
            var self = Tailscale.Get(s.Status, "Self") as Dictionary<string, object>;
            selfName = Tailscale.ShortName(self);
            selfRelay = (Tailscale.Get(self, "Relay") as string) ?? "";
            selfExpiry = Time(Tailscale.Get(self, "KeyExpiry"));
            ver = ((Tailscale.Get(s.Status, "Version") as string) ?? "").Split('-')[0];
            var h = Tailscale.Get(s.Status, "Health") as object[];
            health = h == null ? new List<string>() : h.OfType<string>().ToList();

            var seen = new HashSet<string>();
            var added = new List<Dot>();
            var peers = Tailscale.Get(s.Status, "Peer") as Dictionary<string, object>;
            if (peers != null)
                foreach (var p in peers.Values.OfType<Dictionary<string, object>>())
                {
                    string ip = Tailscale.FirstIp(p);
                    if (ip == "" || !seen.Add(ip)) continue;
                    Dot d;
                    if (!dots.TryGetValue(ip, out d)) { d = Make(ip); dots[ip] = d; added.Add(d); }
                    string name = Tailscale.ShortName(p);
                    d.Name = name == "device-of-shared-to-user" ? L.T("共享设备") : name;
                    d.Os = (Tailscale.Get(p, "OS") as string) ?? "";
                    d.Online = true.Equals(Tailscale.Get(p, "Online"));
                    d.Active = true.Equals(Tailscale.Get(p, "Active"));
                    d.Addr = (Tailscale.Get(p, "CurAddr") as string) ?? "";
                    d.Relay = (Tailscale.Get(p, "Relay") as string) ?? "";
                    d.LastSeen = Time(Tailscale.Get(p, "LastSeen"));
                    d.KeyExpiry = Time(Tailscale.Get(p, "KeyExpiry"));
                    d.Rx = Convert.ToInt64(Tailscale.Get(p, "RxBytes") ?? 0L, CultureInfo.InvariantCulture);
                    d.Tx = Convert.ToInt64(Tailscale.Get(p, "TxBytes") ?? 0L, CultureInfo.InvariantCulture);
                    Speed(d, s.At);
                }
            foreach (var d in dots.Values.Where(x => !seen.Contains(x.Ip)).ToList()) Vanish(d);
            bool intro = !loaded;
            loaded = true;
            foreach (var d in dots.Values)
            {
                d.Ring = RingOf(d);
                Paint(d);
                if (d.Online && !d.Pinging && (DateTime.Now - d.PingAt).TotalSeconds > 15) Ping(d);
            }
            coreName.Text = selfName;
            Layout(!intro, added);
            if (intro) Intro();
            else foreach (var d in added) Appear(d, 0);
            RenderChecks();
        }

        // 跟上一次比：这段时间收了多少、发了多少
        static void Speed(Dot d, DateTime at)
        {
            if (d.LastRx >= 0 && d.Rx >= d.LastRx && d.Tx >= d.LastTx)
            {
                double dt = Math.Max(0.2, (at - d.LastAt).TotalSeconds);
                d.Down = (d.Rx - d.LastRx) / dt;
                d.Up = (d.Tx - d.LastTx) / dt;
            }
            else d.Down = d.Up = 0;   // 头一次读、或者计数从头开始了
            d.LastRx = d.Rx;
            d.LastTx = d.Tx;
            d.LastAt = at;
        }

        // 走哪条路：正在通信的看 status，没在通信的看最近一次 ping
        static string RingOf(Dot d)
        {
            if (!d.Online) return "off";
            if (d.Active) return d.Addr != "" ? "direct" : "relay";
            return d.Via == "" ? "direct" : "relay";
        }

        async void Ping(Dot d)
        {
            d.Pinging = true;
            d.PingAt = DateTime.Now;
            string ip = d.Ip;
            CmdResult r;
            try { r = await Task.Run(() => Tailscale.Run("ping --c 3 --timeout 2s " + ip)); }
            finally { d.Pinging = false; }
            Match last = null;
            foreach (var raw in r.Out.Split('\n'))
            {
                var m = Pong.Match(raw.Trim());
                if (m.Success) last = m;
            }
            if (last == null) d.Ms = -1;
            else
            {
                string via = last.Groups[1].Value;
                d.Via = via.StartsWith("DERP(") ? via.Substring(5).TrimEnd(')') : via.StartsWith("peer-relay") ? "peer" : "";
                d.Ms = (int)Math.Round(double.Parse(last.Groups[2].Value, CultureInfo.InvariantCulture));
            }
            if (!dots.ContainsKey(ip)) return;
            string ring = RingOf(d);
            Paint(d);
            if (ring == d.Ring) return;
            d.Ring = ring;   // 探出来换了路：沿着自己那个方向滑到对的圈
            Layout(true, null);
        }

        // ---------- 体检 ----------

        async void Check()
        {
            if (checking) return;
            checking = true;
            Scan(true);
            RenderChecks();
            CmdResult r;
            try { r = await Task.Run(() => Tailscale.Run("netcheck --format json")); }
            catch (Win32Exception) { r = new CmdResult { Code = -1, Out = "", Err = "" }; }
            finally { checking = false; }
            Scan(false);
            if (r.Code == 0) { net = Tailscale.Json(r.Out); netAt = DateTime.Now; }
            RenderChecks();
        }

        void Scan(bool on)
        {
            if (on)
            {
                Ui.AnimTo(sweep, OpacityProperty, 1, 300);
                Ui.Loop(sweep, sweepTurn, RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1400)) { RepeatBehavior = RepeatBehavior.Forever });
                return;
            }
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            fade.Completed += delegate { if (!checking) Ui.Unloop(sweepTurn, RotateTransform.AngleProperty); };
            sweep.BeginAnimation(OpacityProperty, fade);
        }

        void RenderChecks()
        {
            when.Text = checking ? L.T("正在检测…") : netAt == DateTime.MinValue ? "" : Ago(DateTime.Now - netAt);
            var rows = new List<string[]>();   // 每行：状态、名目、结果
            Action<string, string, string> add = (a, b, c) => rows.Add(new[] { a, b, c });
            if (net == null)
            {
                foreach (var k in new[] { L.T("UDP 穿透"), "IPv6", L.T("NAT 类型"), L.T("端口映射"), L.T("最近的中继") }) add("wait", k, "…");
            }
            else
            {
                bool udp = true.Equals(Tailscale.Get(net, "UDP"));
                add(udp ? "ok" : "bad", L.T("UDP 穿透"), udp ? L.T("正常") : L.T("不可用，仅能通过中继连接"));
                bool v6 = true.Equals(Tailscale.Get(net, "IPv6"));
                add(v6 ? "ok" : "none", "IPv6", v6 ? L.T("可用") : L.T("无"));
                object varies = Tailscale.Get(net, "MappingVariesByDestIP");
                add(varies == null ? "none" : true.Equals(varies) ? "bad" : "ok", L.T("NAT 类型"), varies == null ? L.T("未知") : true.Equals(varies) ? L.T("严格") : L.T("开放"));
                string map = true.Equals(Tailscale.Get(net, "PMP")) ? "NAT-PMP" : true.Equals(Tailscale.Get(net, "UPnP")) ? "UPnP" : true.Equals(Tailscale.Get(net, "PCP")) ? "PCP" : "";
                add(map != "" ? "ok" : "none", L.T("端口映射"), map != "" ? map : L.T("无"));
                var lat = Tailscale.Get(net, "RegionLatency") as Dictionary<string, object>;
                object ns = Tailscale.Get(lat, Convert.ToString(Tailscale.Get(net, "PreferredDERP"), CultureInfo.InvariantCulture));
                string relay = City(selfRelay) + (ns != null ? " · " + Math.Round(Convert.ToDouble(ns, CultureInfo.InvariantCulture) / 1e6) + " ms" : "");
                add("none", L.T("最近的中继"), relay);
            }
            if (ver != "")
            {
                bool newer = !string.IsNullOrEmpty(upstream) && upstream != ver;
                add(newer ? "none" : upstream == ver ? "ok" : "none", L.T("版本"), ver + (newer ? " · " + L.T("有可用更新：{0}", upstream) : upstream == ver ? " · " + L.T("已是最新版本") : ""));
            }
            // 密钥最早什么时候到期（本机关了到期的不算）
            var keys = dots.Values.Where(d => d.KeyExpiry != null).Select(d => new { d.Name, At = d.KeyExpiry.Value }).ToList();
            if (selfExpiry != null) keys.Add(new { Name = selfName, At = selfExpiry.Value });
            if (keys.Count > 0)
            {
                var k = keys.OrderBy(x => x.At).First();
                int days = (int)Math.Floor((k.At - DateTime.UtcNow).TotalDays);
                add(days < 14 ? "bad" : "none", L.T("最早的密钥到期时间"), k.Name + " · " + (days < 0 ? L.T("已过期") : days == 0 ? L.T("今天") : L.T("{0} 天后", days)));
            }
            string sig = string.Join("\n", rows.Select(r => string.Join("\t", r)));
            if (sig != checksShown)
            {
                checksShown = sig;
                checks.Children.Clear();
                foreach (var r in rows) checks.Children.Add(Row(r[0], r[1], r[2]));
            }
            RenderIssues();
            RenderTraffic();
        }

        // 要处理的事：Tailscale 自己报的问题、背后程序停了的分享、好久没上线的设备；面板放不下的收成一行“还有几件”
        void RenderIssues()
        {
            var all = new List<Func<FrameworkElement>>();   // 先只记要建什么，内容没变就不建
            var texts = new List<string>();
            foreach (var h in health) { var t = h; texts.Add(t); all.Add(() => Issue(t, null, null)); }
            foreach (var e in shares.Where(x => x.Dead != ""))
            {
                string t = L.T("分享 {0} 无法打开", e.Label);
                texts.Add(t);
                all.Add(() => Issue(t, L.T("查看"), () => Host.ShowPage("share")));
            }
            foreach (var d in dots.Values.Where(x => !x.Online && x.LastSeen != null && (DateTime.UtcNow - x.LastSeen.Value).TotalDays >= 7).OrderBy(x => x.LastSeen))
            {
                string t = L.T("{0} 已离线 {1} 天", d.Name, Math.Floor((DateTime.UtcNow - d.LastSeen.Value).TotalDays));
                texts.Add(t);
                all.Add(() => Issue(t, L.T("前往管理控制台"), () => Open("https://login.tailscale.com/admin/machines")));
            }
            int fit = 3;
            if (panel.ActualHeight > 0)
            {
                var dock = (DockPanel)panel.Child;
                double foot = dock.Children[0].DesiredSize.Height, used = 48 + checks.Children.Count * 40;
                fit = Math.Max(1, (int)((panel.ActualHeight - panel.Padding.Top - panel.Padding.Bottom - 2 - foot - used - 8) / 48));
            }
            string sig = fit + "\n" + string.Join("\n", texts);
            if (sig == issuesShown) return;
            issuesShown = sig;
            issues.Children.Clear();
            int show = all.Count <= fit ? all.Count : fit - 1;
            for (int i = 0; i < show; i++) issues.Children.Add(all[i]());
            if (show < all.Count) issues.Children.Add(Issue(L.T("还有 {0} 项待处理", all.Count - show), null, null));
        }

        void RenderTraffic()
        {
            long rx = Sum("inbound"), tx = Sum("outbound");
            down.Text = "↓ " + Ui.FormatSize(rx);
            up.Text = "↑ " + Ui.FormatSize(tx);
            long v6 = Both("direct_ipv6"), v4 = Both("direct_ipv4"), relay = Both("derp") + Both("peer_relay_ipv4") + Both("peer_relay_ipv6"), all = v6 + v4 + relay;
            // 按显示出来的比例比（千分之一），字节数在涨但比例没变时不重画
            string sig = all == 0 ? "0" : Math.Round(v6 * 1000.0 / all) + "/" + Math.Round(v4 * 1000.0 / all) + "/" + Math.Round(relay * 1000.0 / all);
            if (sig == trafficShown) return;
            trafficShown = sig;
            bar.ColumnDefinitions.Clear();
            bar.Children.Clear();
            legend.Children.Clear();
            if (all == 0) return;
            var parts = new List<Tuple<string, long, Brush>>
            {
                Tuple.Create(L.T("IPv6 直连"), v6, (Brush)Th.Fg),
                Tuple.Create(L.T("IPv4 直连"), v4, (Brush)Th.Mute),
                Tuple.Create(L.T("中继"), relay, Stripes()),
            }.Where(x => x.Item2 > 0).ToList();
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                // 再小的一段也至少看得见：按比例分，但不少于总宽的 1%
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(p.Item2, all / 100.0), GridUnitType.Star) });
                var seg = new Border
                {
                    Background = p.Item3, Margin = new Thickness(i == 0 ? 0 : 1, 0, i == parts.Count - 1 ? 0 : 1, 0),
                    CornerRadius = new CornerRadius(i == 0 ? 5 : 0, i == parts.Count - 1 ? 5 : 0, i == parts.Count - 1 ? 5 : 0, i == 0 ? 5 : 0)
                };
                Grid.SetColumn(seg, i);
                bar.Children.Add(seg);
                double pct = p.Item2 * 100.0 / all;
                var key = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = p.Item3, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
                item.Children.Add(key);
                item.Children.Add(Kit.Text(p.Item1 + " " + (pct < 0.1 ? "<0.1" : pct < 10 ? pct.ToString("0.#", CultureInfo.InvariantCulture) : Math.Round(pct).ToString(CultureInfo.InvariantCulture)) + "%", 12.5, Th.Mute));
                legend.Children.Add(item);
            }
        }

        long Sum(string dir)
        {
            return traffic.Where(kv => kv.Key.StartsWith(dir + "/")).Sum(kv => kv.Value);
        }

        long Both(string path)
        {
            long a, b;
            traffic.TryGetValue("inbound/" + path, out a);
            traffic.TryGetValue("outbound/" + path, out b);
            return a + b;
        }

        Brush Stripes()
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(3, 0), MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat };
            b.GradientStops.Add(new GradientStop(Th.MuteColor, 0));
            b.GradientStops.Add(new GradientStop(Th.MuteColor, 0.5));
            b.GradientStops.Add(new GradientStop(Colors.Transparent, 0.5));
            b.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            b.Freeze();
            return b;
        }

        // 一行体检：左边一个点（绿＝好、红＝要管、空心＝说明一下），名目、结果
        FrameworkElement Row(string state, string key, string value)
        {
            var k = Kit.Text(key, 14, Th.Mute);
            k.Margin = new Thickness(12, 0, 12, 0);
            var v = Kit.Text(value, 14, Th.Fg);
            return new Border { Height = 40, BorderBrush = Th.Hair, BorderThickness = new Thickness(0, 1, 0, 0), Child = Ui.Columns(1, Mark(state), k, v) };
        }

        FrameworkElement Issue(string text, string action, Action click)
        {
            var t = new TextBlock { Text = text, FontSize = 14, Foreground = Th.Fg, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 8, 12, 8) };
            var row = new Border { MinHeight = 48, BorderBrush = Th.Hair, BorderThickness = new Thickness(0, 1, 0, 0) };
            if (action == null) { row.Child = Ui.Columns(1, Mark("bad"), t); return row; }
            var b = new Border
            {
                Height = 32, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(9), Background = Th.Hair, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = action, FontSize = 13, Foreground = Th.Fg, VerticalAlignment = VerticalAlignment.Center }
            };
            b.MouseEnter += delegate { b.Background = Th.Fill; };
            b.MouseLeave += delegate { b.Background = Th.Hair; };
            b.MouseLeftButtonUp += delegate { click(); };
            row.Child = Ui.Columns(1, Mark("bad"), t, b);
            return row;
        }

        FrameworkElement Mark(string state)
        {
            var e = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            if (state == "ok") e.Fill = Th.Ok;
            else if (state == "bad") e.Fill = Th.Bad;
            else { e.Stroke = state == "wait" ? Th.Dim : Th.Fg; e.StrokeThickness = 1.5; }
            return e;
        }

        // ---------- 地图 ----------

        Dot Make(string ip)
        {
            var d = new Dot { Ip = ip };
            d.Face = new SolidColorBrush(Th.FgColor);
            d.Rim = new SolidColorBrush(Theme.Fade(Th.DimColor, 0));
            d.Ink = new SolidColorBrush(Th.InkColor);
            d.Disc = new Grid { Width = 78, Height = 78, Background = Brushes.Transparent, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = d.Pop };
            d.Disc.Children.Add(new Ellipse { Fill = d.Face, Stroke = d.Rim, StrokeThickness = 1.5 });
            d.Icon = new IconView("monitor", 26, d.Ink);
            d.Disc.Children.Add(d.Icon);
            d.Disc.MouseEnter += delegate { Ui.ScaleTo(d.Pop, 1.06, 200, Ui.Spring); };
            d.Disc.MouseLeave += delegate { Ui.ScaleTo(d.Pop, 1, 200); };
            // 点一下马上再测一次延迟
            d.Disc.MouseLeftButtonUp += delegate
            {
                Ui.ScaleFrom(d.Pop, 0.9, 1.06, 360, 0, Ui.Spring);
                if (d.Online && !d.Pinging) Ping(d);
            };
            d.Title = new TextBlock { FontSize = 14, Foreground = Th.Mute, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            d.Note = new TextBlock { FontSize = 12.5, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            d.Label = new StackPanel { Width = 160, IsHitTestVisible = false };
            d.Label.Children.Add(d.Title);
            d.Label.Children.Add(d.Note);
            Canvas.SetLeft(d.Label, -80);
            d.View.Children.Add(d.Disc);
            d.View.Children.Add(d.Label);
            d.View.RenderTransform = d.At;
            d.Link = new Line { StrokeThickness = 1.5, Stroke = line };
            d.Flow = new Line { StrokeThickness = 3.4, Stroke = Th.Fg, StrokeDashArray = new DoubleCollection { 0, 13 / 3.4 }, StrokeDashCap = PenLineCap.Round, Opacity = 0 };
            lines.Children.Add(d.Link);
            lines.Children.Add(d.Flow);
            d.ChipEdge = new Rectangle { RadiusX = 12, RadiusY = 12, Fill = Th.Chip, Stroke = chipEdge, StrokeThickness = 1 };
            d.ChipText = new TextBlock { FontSize = 12, Foreground = Th.Fg, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            d.Chip = new Grid { Height = 24, Opacity = 0 };
            d.Chip.Children.Add(d.ChipEdge);
            d.Chip.Children.Add(d.ChipText);
            chips.Children.Add(d.Chip);
            layer.Children.Add(d.View);
            return d;
        }

        void Paint(Dot d)
        {
            bool on = d.Online, relay = d.Ring == "relay";
            Ui.Tint(d.Face, on ? Th.FgColor : Theme.Fade(Th.FgColor, 0), 300);
            Ui.Tint(d.Rim, on ? Theme.Fade(Th.DimColor, 0) : Th.DimColor, 300);
            Ui.Tint(d.Ink, on ? Th.InkColor : Th.DimColor, 300);
            d.Icon.Show(Icons.ForOs(d.Os));
            d.Title.Text = d.Name;
            d.Title.Foreground = on ? Th.Mute : Th.Dim;
            d.Disc.ToolTip = Ui.Tip(Th, Describe(d));
            // 名字下面：在传东西写速度，离线写多久了
            int dir = 0;
            double rate = Math.Max(d.Down, d.Up);
            if (on && rate >= Busy)
            {
                dir = d.Down >= d.Up ? 1 : -1;
                d.Note.Text = (dir == 1 ? "↓ " : "↑ ") + Ui.FormatSize((long)rate) + "/s";
                d.Note.FontFamily = Ui.MonoFont;
                d.Note.Foreground = Th.Fg;
            }
            else if (!on && d.LastSeen != null)
            {
                var gone = DateTime.UtcNow - d.LastSeen.Value;
                d.Note.Text = L.T("已离线 {0}", Span(gone));
                d.Note.FontFamily = Ui.TextFont;
                d.Note.Foreground = gone.TotalDays >= 7 ? Th.Bad : Th.Dim;
            }
            else d.Note.Text = "";
            // 连线：离线的不连；中继是虚线；在传东西的加粗
            d.Link.Opacity = on ? 1 : 0;
            d.Link.StrokeThickness = dir != 0 ? 3 : 1.5;
            d.Link.Stroke = dir != 0 ? lineHot : line;
            d.Link.StrokeDashArray = relay ? new DoubleCollection { 3, 3 } : null;
            SetFlow(d, visible && on ? dir : 0, rate);
            // 延迟标签：中继的写是哪里的中继，边框是虚线
            if (on && d.Ms >= 0)
            {
                string via = d.Active ? d.Relay : d.Via;
                d.ChipText.Text = (relay ? (via == "peer" ? L.T("节点中继") : L.T("{0}中继", City(via))) + " " : "") + d.Ms + " ms";
                d.ChipEdge.StrokeDashArray = relay ? new DoubleCollection { 3, 2 } : null;
                d.ChipEdge.Stroke = relay ? Th.Rim : chipEdge;
                Ui.AnimTo(d.Chip, OpacityProperty, 1, 250);
            }
            else Ui.AnimTo(d.Chip, OpacityProperty, 0, 200);
            Fit(d);
        }

        // 大小跟着窗口缩放；上半圈的名字写在圆盘上面（背向本机），其余写在下面
        void Fit(Dot d)
        {
            double size = d.Online ? dotSize : Math.Round(dotSize * 0.8);
            d.Disc.Width = d.Disc.Height = size;
            Canvas.SetLeft(d.Disc, -size / 2);
            Canvas.SetTop(d.Disc, -size / 2);
            d.Icon.Width = d.Icon.Height = Math.Round(size * 0.34);
            d.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bool up = Math.Cos(d.Angle * Math.PI / 180) > 0.35;
            Canvas.SetTop(d.Label, up ? -size / 2 - 6 - d.Label.DesiredSize.Height : size / 2 + 8);
            PlaceChip(d);
        }

        void SetFlow(Dot d, int dir, double rate = 0)
        {
            if (dir == d.FlowDir) return;
            d.FlowDir = dir;
            if (dir == 0)
            {
                Ui.Unloop(d.Flow, Shape.StrokeDashOffsetProperty);
                Ui.AnimTo(d.Flow, OpacityProperty, 0, 200);
                return;
            }
            // 连线从本机画向设备：虚线往回挪就是光点往本机流（在下载）
            double period = 13 / 3.4;
            int ms = rate > 10 * 1024 * 1024 ? 260 : rate > 1024 * 1024 ? 380 : 520;
            Ui.Loop(d.Flow, d.Flow, Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, dir == 1 ? period : -period, TimeSpan.FromMilliseconds(ms)) { RepeatBehavior = RepeatBehavior.Forever });
            Ui.AnimTo(d.Flow, OpacityProperty, 0.95, 250);
        }

        static string Describe(Dot d)
        {
            var text = new List<string> { d.Name + "  " + d.Ip };
            string via = d.Active ? d.Relay : d.Via;
            if (!d.Online) text.Add(d.LastSeen != null ? L.T("上次在线：{0}", Ui.When(d.LastSeen.Value.ToLocalTime())) : L.T("离线"));
            else if (d.Ring == "direct") text.Add(L.T("直连") + (d.Addr != "" ? "  " + d.Addr : ""));
            else text.Add(via == "peer" ? L.T("经其他设备中继") : L.T("经{0}中继", City(via)));
            if (d.Rx + d.Tx > 0) text.Add(L.T("本次连接：接收 {0} · 发送 {1}", Ui.FormatSize(d.Rx), Ui.FormatSize(d.Tx)));
            return string.Join("\n", text);
        }

        void Layout(bool animate, ICollection<Dot> fresh = null)
        {
            double w = field.ActualWidth, h = field.ActualHeight;
            if (w < 50 || h < 50) return;
            // 外圈下面要放离线设备的两行字，圆心往上提一点；里圈至少要在本机和设备之间留出放延迟标签的空
            o = new Point(w * 0.5, h / 2 - 18);
            r3 = Math.Max(150, Math.Min(Math.Min(o.X, w - o.X) - 50, Math.Min(o.Y - 45, h - o.Y - 82)));
            coreSize = Math.Max(72, Math.Min(94, Math.Round(r3 * 0.31)));
            dotSize = Math.Max(58, Math.Min(78, Math.Round(r3 * 0.26)));
            r1 = Math.Max(r3 * 0.5, coreSize / 2 + dotSize / 2 + 66);
            r2 = Math.Max(r3 * 0.77, r1 + 60);
            double[] r = { r1, r2, r3 };
            for (int i = 0; i < 3; i++)
            {
                rings[i].Width = rings[i].Height = 2 * r[i];
                Canvas.SetLeft(rings[i], o.X - r[i]);
                Canvas.SetTop(rings[i], o.Y - r[i]);
            }
            rings[1].StrokeDashArray = EvenDash(2 * Math.PI * r2, 3, 4);
            rings[2].StrokeDashArray = EvenDash(2 * Math.PI * r3 / 1.5, 0, 3);
            sweep.Width = sweep.Height = 2 * r3;
            Canvas.SetLeft(sweep, o.X - r3);
            Canvas.SetTop(sweep, o.Y - r3);
            core.Width = core.Height = coreSize;
            Canvas.SetLeft(core, o.X - coreSize / 2);
            Canvas.SetTop(core, o.Y - coreSize / 2);
            coreIcon.Width = coreIcon.Height = Math.Round(coreSize * 0.34);
            coreName.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(coreName, o.X - coreName.DesiredSize.Width / 2);
            Canvas.SetTop(coreName, o.Y + coreSize / 2 + 9);
            Place();
            foreach (var d in dots.Values) Move(d, animate && (fresh == null || !fresh.Contains(d)));
        }

        // 方向先用星网那页的；没有、或者跟别人挤在一起时，找离大家最远的地方（在线的偏上，离线的偏两侧）
        void Place()
        {
            var placed = new List<double>();
            foreach (var d in dots.Values.OrderBy(x => x.Ring == "direct" ? 0 : x.Ring == "relay" ? 1 : 2).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                double want = !double.IsNaN(d.Angle) ? d.Angle : homeAngle(d.Ip);
                if (double.IsNaN(want) || placed.Any(a => Apart(a, want) < 34))
                {
                    double best = 0, bestScore = double.NegativeInfinity;
                    for (int a = -180; a < 180; a += 5)
                    {
                        double gap = placed.Count == 0 ? 180 : placed.Min(x => Apart(x, a));
                        double c = Math.Cos(a * Math.PI / 180);
                        double lean = d.Online ? (1 - c) / 2 : Math.Abs(c);
                        double score = Math.Min(gap, 90) - 30 * lean;
                        if (score > bestScore) { bestScore = score; best = a; }
                    }
                    want = best;
                }
                d.Angle = want;
                placed.Add(want);
            }
        }

        static double Apart(double a, double b)
        {
            return Math.Abs(((a - b) % 360 + 540) % 360 - 180);
        }

        void Move(Dot d, bool animate)
        {
            double r = d.Ring == "direct" ? r1 : d.Ring == "relay" ? r2 : r3, a = d.Angle * Math.PI / 180;
            var p = new Point(o.X + r * Math.Sin(a), o.Y - r * Math.Cos(a));
            d.Link.X1 = d.Flow.X1 = o.X;
            d.Link.Y1 = d.Flow.Y1 = o.Y;
            bool same = (p - d.Pos).Length < 0.5;
            d.Pos = p;
            if (!(animate && same))
            {
                Go(d.At, TranslateTransform.XProperty, p.X, animate);
                Go(d.At, TranslateTransform.YProperty, p.Y, animate);
                foreach (var l in new[] { d.Link, d.Flow })
                {
                    Go(l, Line.X2Property, p.X, animate);
                    Go(l, Line.Y2Property, p.Y, animate);
                }
            }
            Fit(d);
        }

        // 延迟标签放在连线上本机和设备之间那段空当的正中
        void PlaceChip(Dot d)
        {
            var v = d.Pos - o;
            double len = v.Length;
            if (len < 1) return;
            double r = d.Disc.Width / 2, at = coreSize / 2 + (len - coreSize / 2 - r) / 2;
            var c = o + v / len * at;
            d.Chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(d.Chip, c.X - d.Chip.DesiredSize.Width / 2);
            Canvas.SetTop(d.Chip, c.Y - 12);
        }

        static void Go(DependencyObject target, DependencyProperty prop, double to, bool animate)
        {
            if (animate)
            {
                Ui.AnimTo(target, prop, to, 620, MainEase.Settle);
                return;
            }
            ((IAnimatable)target).BeginAnimation(prop, null);
            target.SetValue(prop, to);
        }

        void Intro()
        {
            for (int i = 0; i < 3; i++)
            {
                Ui.AnimFrom(rings[i], OpacityProperty, 0, 1, 900, i * 110, MainEase.Settle);
                Ui.ScaleFrom(ringScale[i], 0.55, 1, 900, i * 110, MainEase.Settle);
            }
            core.Opacity = 1;
            Ui.ScaleFrom(corePop, 0, 1, 640, 0, Ui.Spring);
            Ui.AnimFrom(coreName, OpacityProperty, 0, 1, 400, 300);
            Ui.AnimFrom(panel, OpacityProperty, 0, 1, 360, 150);
            int k = 0;
            foreach (var d in dots.Values.OrderBy(x => x.Online ? 0 : 1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) Appear(d, 260 + 80 * k++);
        }

        void Appear(Dot d, int delay)
        {
            Ui.ScaleFrom(d.Pop, 0.3, 1, 460, delay, Ui.Spring);
            Ui.AnimFrom(d.View, OpacityProperty, 0, 1, 220, delay);
            if (d.Online) Ui.AnimFrom(d.Link, OpacityProperty, 0, 1, 400, delay);
        }

        void Vanish(Dot d)
        {
            dots.Remove(d.Ip);
            SetFlow(d, 0);
            Ui.ScaleTo(d.Pop, 0.3, 260, MainEase.Sink);
            Ui.AnimTo(d.View, OpacityProperty, 0, 260, MainEase.Sink);
            Ui.AnimTo(d.Link, OpacityProperty, 0, 260);
            Ui.AnimTo(d.Chip, OpacityProperty, 0, 200);
            Ui.After(300, () =>
            {
                layer.Children.Remove(d.View);
                lines.Children.Remove(d.Link);
                lines.Children.Remove(d.Flow);
                chips.Children.Remove(d.Chip);
            });
        }

        // ---------- 小工具 ----------

        static string City(string code)
        {
            string name;
            return Cities.TryGetValue(code ?? "", out name) ? L.T(name) : (code ?? "").ToUpperInvariant();
        }

        static string Span(TimeSpan t)
        {
            if (t.TotalHours < 1) return L.T("{0} 分钟", Math.Max(1, Math.Floor(t.TotalMinutes)));
            if (t.TotalDays < 1) return L.T("{0} 小时", Math.Floor(t.TotalHours));
            return L.T("{0} 天", Math.Floor(t.TotalDays));
        }

        static string Ago(TimeSpan t)
        {
            return t.TotalMinutes < 1 ? L.T("刚刚") : L.T("{0}前", Span(t));
        }

        static DateTime? Time(object v)
        {
            DateTime t;
            if (!(v is string) || !DateTime.TryParse((string)v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return null;
            t = t.ToUniversalTime();
            return t.Year < 2000 ? (DateTime?)null : t;   // 在线的设备 LastSeen 是 0001 年
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // 虚线按周长微调，一圈正好排满整数段
        static DoubleCollection EvenDash(double length, double on, double off)
        {
            double n = Math.Max(1, Math.Round(length / (on + off)));
            double k = length / (n * (on + off));
            return new DoubleCollection { on * k, off * k };
        }

        static void Open(string url)
        {
            Ui.Launch(() => Process.Start(url));
        }
    }

    // 雷达扫过的光：最亮的那条边在正上方，往逆时针方向淡下去；转起来靠外面的 RotateTransform
    class Sweep : FrameworkElement
    {
        readonly Brush[] fades = new Brush[30];

        public Sweep(Color c)
        {
            IsHitTestVisible = false;
            for (int i = 0; i < fades.Length; i++)
            {
                var b = new SolidColorBrush(Theme.Fade(c, 0.16 * (1 - i / (double)fades.Length)));
                b.Freeze();
                fades[i] = b;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double r = ActualWidth / 2;
            if (r < 1) return;
            var c = new Point(r, r);
            for (int i = 0; i < fades.Length; i++)
            {
                double a0 = -i * 2 * Math.PI / 180, a1 = -(i + 1) * 2 * Math.PI / 180;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(c, true, true);
                    ctx.LineTo(new Point(c.X + r * Math.Sin(a0), c.Y - r * Math.Cos(a0)), false, false);
                    ctx.ArcTo(new Point(c.X + r * Math.Sin(a1), c.Y - r * Math.Cos(a1)), new Size(r, r), 0, false, SweepDirection.Counterclockwise, false, false);
                }
                g.Freeze();
                dc.DrawGeometry(fades[i], null, g);
            }
        }
    }
}
