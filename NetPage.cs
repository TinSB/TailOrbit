// 网络页：本机在中间，直连的设备在里圈、经中继的在中圈、离线的在外圈，方向跟星网那页一致；
// 连线上标着延迟，正在传东西的那条变粗、光点顺着传的方向流，名字下面写速度（没在传、选了今天或几天时写这段时间的流量）。
// 点一台弹出它的卡片：现在走哪条路、近 24 小时的延迟、近 30 天每天的流量，连着管理接口时还能删掉它、关掉它的密钥到期。
// 右边先是要处理的事，再是体检（打洞、IPv6、NAT、端口映射、中继、版本、密钥到期、DNS、对外开放了什么），底下是流量走了哪条路（本次启动以来，或者 NetLog 记的今天、7 天、30 天）
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            public string Ip = "", Name = "", Os = "", Addr = "", Relay = "", Id = "", Dns = "";   // Id：Tailscale 的设备 ID（记录、管理接口按它认）
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
            public Dictionary<string, NetLog.Usage> Usage;
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
        readonly Border panel, footBox;
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
        static int range;   // 底下的流量看哪段：0 本次启动以来、1 今天、2 近 7 天、3 近 30 天；换语言、换深浅色整页重建后照旧
        Dictionary<string, NetLog.Usage> usage = new Dictionary<string, NetLog.Usage>();   // 选的那段时间每台设备的流量
        Dictionary<string, object> prefs;   // get --json，半分钟读一次：DNS、本机共享的出口节点和局域网、挡没挡传入连接
        Exposure.Result exposure;
        string dnsName;                     // 拿来试解析的那台；null 是还没试
        bool dnsOk, magic = true, surveying;
        DateTime surveyAt = DateTime.MinValue;
        string selfId = "";
        List<string> selfRoutes = new List<string>();   // 后台批准了本机共享的哪些网段
        Popup card, openList;                // 设备卡片、对外开放的列表
        DateTime cardClosed, openClosed;
        string cardIp, confirm, acting;      // 卡片开着的是哪台；等确认删除的设备；正在替用户调管理接口的那件事
        DateTime confirmAt;                  // 第一下“删除”是什么时候点的
        string scanError;                    // 开放检查上次记进日志的错：一直是同一个错就不每半分钟记一遍
        TextBlock cardPath;
        Border cardFoot;
        readonly StackPanel offBox;          // 断开了、没登录：整页换成一句话和“连接”“登录”
        readonly TextBlock offTitle, offNote;
        readonly IconView offIcon;
        readonly Border offButton;
        string offFor;                       // 现在是哪种状态（null 是连着）
        bool offBusy;

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
            var spans = new Segmented(Th, new[] { L.T("本次启动"), L.T("今天"), L.T("近 {0} 天", 7), L.T("近 {0} 天", 30) }, range, false, false, 28, false, 12.5) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            spans.Picked += k => { range = k; Poll(); };
            var foot = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            foot.Children.Add(spans);
            foot.Children.Add(totals);
            foot.Children.Add(bar);
            foot.Children.Add(legend);
            footBox = new Border { BorderBrush = Th.Hair, BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };
            // 要处理的事放在体检前面（体检行多，放后面会被挤到看不见）；放不下时中间这块用滚轮翻，头和底下的流量不动
            var list = new StackPanel();
            list.Children.Add(issues);
            list.Children.Add(checks);
            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false };
            scroll.ScrollChanged += delegate { Kit.FadeEdges(scroll); };
            var dock = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);
            DockPanel.SetDock(footBox, Dock.Bottom);
            dock.Children.Add(footBox);
            dock.Children.Add(scroll);
            panel = new Border
            {
                Width = 404, Margin = new Thickness(0, 66, 28, 32), HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(18),
                Background = Th.Pop, BorderBrush = Th.Hair, BorderThickness = new Thickness(1), Padding = new Thickness(24, 22, 24, 22), Opacity = 0, Child = dock
            };
            Children.Add(field);
            Children.Add(panel);
            offTitle = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 8) };
            offNote = new TextBlock { FontSize = 13.5, LineHeight = 20, Foreground = Th.Mute, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 360 };
            offButton = Kit.Button(Th, "", true, FixState);
            offButton.HorizontalAlignment = HorizontalAlignment.Center;
            offButton.Margin = new Thickness(0, 22, 0, 0);
            var disc = Kit.Disc(Th, "unlink", 96, false);
            disc.HorizontalAlignment = HorizontalAlignment.Center;
            offIcon = (IconView)disc.Children[1];
            offBox = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 44, 0, 0), Children = { disc, offTitle, offNote, offButton } };
            Children.Add(offBox);
            field.SizeChanged += delegate { Layout(false); };
            SizeChanged += delegate
            {
                double w = Math.Max(340, Math.Min(404, ActualWidth * 0.36));
                panel.Width = w;
                field.Margin = new Thickness(0, 44, w + 28, 0);
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
            int span = range;
            Snap s;
            try { s = await Task.Run(() => Read(full, wantShares, wantUpstream, span)); }
            finally { polling = false; }
            Apply(s);
            if ((DateTime.Now - surveyAt).TotalSeconds > 30) Survey();
        }

        // span：底下的流量看的那段（不是本次启动时从记录里加）
        static Snap Read(bool full, bool wantShares, bool wantUpstream, int span)
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
            if (span > 0) s.Usage = NetLog.Range(DateTime.Today.AddDays(span == 1 ? 0 : span == 2 ? -6 : -29), DateTime.Today);
            return s;
        }

        // 半分钟查一次的：本机设置（get --json）、对外开放了什么、DNS 好不好用（真的解析一台在线设备的名字）
        async void Survey()
        {
            if (surveying) return;
            surveying = true;
            surveyAt = DateTime.Now;
            var probe = dots.Values.Where(d => d.Online && d.Dns != "").OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            string host = probe == null ? null : probe.Dns, want = probe == null ? null : probe.Ip;
            var list = shares;
            Dictionary<string, object> p = null;
            Exposure.Result e = null;
            bool ok = false;
            try
            {
                await Task.Run(() =>
                {
                    var g = Tailscale.Run("get --json");
                    if (g.Code == 0) p = Tailscale.Json(g.Out);
                    // 防火墙服务停了、被别家防火墙接管、规则或进程读出了意外的东西：这一项不显示，DNS 和本机设置照常更新
                    try { e = Exposure.Scan(p != null && true.Equals(Tailscale.Get(p, "shields-up")), list); }
                    catch (Exception x)
                    {
                        if (x.Message != scanError) Log.Write(x);
                        scanError = x.Message;
                    }
                    if (host != null) ok = Resolves(host, want);
                });
            }
            catch (Win32Exception) { }   // 没装 Tailscale
            finally { surveying = false; }
            if (p != null) prefs = p;
            if (e != null) exposure = e;
            dnsName = host;
            dnsOk = ok;
            RenderChecks();
        }

        static bool Resolves(string host, string ip)
        {
            try { return Dns.GetHostAddresses(host).Any(a => a.ToString() == ip); }
            catch (SocketException) { return false; }
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
            string state = (Tailscale.Get(s.Status, "BackendState") as string) ?? "";
            ShowOff(state == "Running" ? null : state, true.Equals(Tailscale.Get(s.Status, "HaveNodeKey")));
            if (offFor != null) return;
            var self = Tailscale.Get(s.Status, "Self") as Dictionary<string, object>;
            selfName = Tailscale.ShortName(self);
            selfRelay = (Tailscale.Get(self, "Relay") as string) ?? "";
            selfExpiry = Time(Tailscale.Get(self, "KeyExpiry"));
            selfId = (Tailscale.Get(self, "ID") as string) ?? "";
            // 后台批准了的网段（出口节点是 0.0.0.0/0、::/0）会出现在本机的 AllowedIPs 里
            selfRoutes = ((Tailscale.Get(self, "AllowedIPs") as object[]) ?? new object[0]).Concat((Tailscale.Get(self, "PrimaryRoutes") as object[]) ?? new object[0]).OfType<string>().Distinct().ToList();
            magic = !false.Equals(Tailscale.Get(Tailscale.Get(s.Status, "CurrentTailnet") as Dictionary<string, object>, "MagicDNSEnabled"));
            if (s.Usage != null) usage = s.Usage;
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
                    d.Id = (Tailscale.Get(p, "ID") as string) ?? "";
                    d.Dns = ((Tailscale.Get(p, "DNSName") as string) ?? "").TrimEnd('.');
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

        // 断开了、没登录、等管理员批准：星图和右边的体检都没有意义，整页换成一句话和按钮；连上了换回来
        void ShowOff(string state, bool haveKey)
        {
            offFor = state;
            bool off = state != null;
            field.Visibility = panel.Visibility = off ? Visibility.Hidden : Visibility.Visible;
            if (!off) { offBox.Visibility = Visibility.Collapsed; return; }
            offIcon.Show(state == "Stopped" ? "unlink" : state == "Starting" ? "link" : "key");
            offTitle.Text = Account.Describe(state, haveKey);
            offNote.Text = state == "NeedsMachineAuth" ? L.T("管理员在管理控制台批准这台电脑后就能连上。") : state == "Starting" ? "" : L.T("连上以后，这里显示各台设备走的路、延迟和流量。");
            string label = Account.FixLabel(state, haveKey);
            if (label != null && !offBusy) ((TextBlock)offButton.Child).Text = label;
            offButton.Visibility = label != null ? Visibility.Visible : Visibility.Collapsed;
            if (offBox.Visibility == Visibility.Visible) return;
            offBox.Visibility = Visibility.Visible;
            Ui.AnimFrom(offBox, OpacityProperty, 0, 1, 300);
        }

        async void FixState()
        {
            if (offBusy || offFor == null) return;
            offBusy = true;
            string state = offFor;
            if (state == "Stopped") ((TextBlock)offButton.Child).Text = L.T("正在连接…");
            string why = await Account.Fix(state);
            offBusy = false;
            Poll();
            if (!string.IsNullOrEmpty(why)) Host.Notify(state == "Stopped" ? L.T("连接失败") : L.T("登录失败"), why);
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
            // DNS：本机用不用 Tailscale 的 DNS、MagicDNS 开没开、真的解析一台在线设备的名字对不对
            if (prefs != null)
            {
                string why = false.Equals(Tailscale.Get(prefs, "accept-dns")) ? L.T("本机未使用 Tailscale DNS") : !magic ? L.T("未开启 MagicDNS")
                    : dnsName != null && !dnsOk ? L.T("无法解析 {0}", dnsName.Split('.')[0]) : null;
                add(why == null ? "ok" : "bad", "DNS", why ?? L.T("正常"));
            }
            // 对外开放了什么：点开看列表；有公开到互联网的分享时标红
            if (exposure != null)
            {
                int n = exposure.Items.Count;
                bool open = exposure.Items.Any(x => x.Share != null && x.Share.Public);
                rows.Add(new[] { open ? "bad" : exposure.Shields ? "ok" : "none", L.T("对外开放"), n > 0 ? L.T("{0} 项", n) : exposure.Shields ? L.T("已阻止所有传入连接") : L.T("无"), n > 0 ? "list" : "" });
            }
            string sig = string.Join("\n", rows.Select(r => string.Join("\t", r)));
            if (sig != checksShown)
            {
                checksShown = sig;
                checks.Children.Clear();
                foreach (var r in rows) checks.Children.Add(Row(r[0], r[1], r[2], r.Length > 3 && r[3] == "list" ? (Action<FrameworkElement>)ShowExposure : null));
            }
            RenderIssues();
            RenderTraffic();
        }

        // 要处理的事：Tailscale 自己报的问题、背后程序停了的分享、本机共享的出口节点和局域网还没批准、两周内到期的密钥、好久没上线的设备。
        // 连着管理接口的就在这里批准、关掉到期、删掉；没连的打开管理控制台
        void RenderIssues()
        {
            var all = new List<Func<FrameworkElement>>();   // 先只记要建什么，内容没变就不建
            var texts = new List<string>();
            bool admin = Admin.Connected;
            Action<string, string, Action, string> add = (t, a, click, key) =>
            {
                bool busy = key != null && acting == key, sure = key != null && confirm == key;
                string label = busy ? L.T("正在处理…") : sure ? L.T("确认删除") : a;
                texts.Add(t + "\t" + label);
                all.Add(() => Issue(t, label, busy ? null : click, sure));
            };
            foreach (var h in health) add(h, null, null, null);
            foreach (var e in shares.Where(x => x.Dead != "")) add(L.T("分享 {0} 无法打开", e.Label), L.T("查看"), () => Host.ShowPage("share"), null);
            var wait = Waiting();
            if (wait.Count > 0)
            {
                bool exit = wait.Contains("0.0.0.0/0"), lan = wait.Any(x => x != "0.0.0.0/0" && x != "::/0");
                add(exit && lan ? L.T("本机共享的出口节点和局域网等待批准") : exit ? L.T("本机作为出口节点等待批准") : L.T("本机共享的局域网等待批准"),
                    admin ? L.T("批准") : L.T("前往管理控制台"), () => { var now = Waiting(); Act("approve", () => Admin.Approve(selfId, now)); }, "approve");   // 点的时候再看等批准的是哪些（文字没变时这一行不重建）
            }
            var keys = dots.Values.Where(d => d.KeyExpiry != null).Select(d => new { d.Id, d.Name, At = d.KeyExpiry.Value }).ToList();
            if (selfExpiry != null) keys.Add(new { Id = selfId, Name = selfName, At = selfExpiry.Value });
            foreach (var k in keys.Where(x => (x.At - DateTime.UtcNow).TotalDays < 14).OrderBy(x => x.At))
            {
                int days = (int)Math.Floor((k.At - DateTime.UtcNow).TotalDays);
                string id = k.Id;
                add(days < 0 ? L.T("{0} 的密钥已过期", k.Name) : L.T("{0} 的密钥 {1} 天后到期", k.Name, days), admin ? L.T("关闭到期") : L.T("前往管理控制台"), () => Act("key:" + id, () => Admin.NoExpiry(id)), "key:" + id);
            }
            foreach (var d in dots.Values.Where(x => !x.Online && x.LastSeen != null && (DateTime.UtcNow - x.LastSeen.Value).TotalDays >= 7).OrderBy(x => x.LastSeen))
            {
                string id = d.Id;
                add(L.T("{0} 已离线 {1} 天", d.Name, Math.Floor((DateTime.UtcNow - d.LastSeen.Value).TotalDays)), admin ? L.T("删除") : L.T("前往管理控制台"), () => Remove(id), "del:" + id);
            }
            string sig = string.Join("\n", texts);
            if (sig == issuesShown) return;
            issuesShown = sig;
            issues.Children.Clear();
            foreach (var f in all) issues.Children.Add(f());
        }

        // 本机共享了、后台还没批准的网段：出口节点是 0.0.0.0/0 和 ::/0
        List<string> Waiting()
        {
            var want = new List<string>();
            if (prefs == null) return want;
            if (true.Equals(Tailscale.Get(prefs, "advertise-exit-node"))) want.AddRange(new[] { "0.0.0.0/0", "::/0" });
            want.AddRange(((Tailscale.Get(prefs, "advertise-routes") as string) ?? "").Split(',').Select(x => x.Trim()).Where(x => x != ""));
            return want.Where(x => !selfRoutes.Contains(x)).ToList();
        }

        // 替用户调管理接口；没连就打开管理控制台。做着的时候那一行写“正在处理…”，没做成弹通知说原因，做成了马上重读
        async void Act(string key, Func<string> work)
        {
            if (!Admin.Connected) { Open("https://login.tailscale.com/admin/machines"); return; }
            if (acting != null) return;
            acting = key;
            confirm = null;
            RenderIssues();
            RefreshCard();
            string why;
            try { why = await Task.Run(work); }
            finally { acting = null; }
            if (why != null) Host.Notify(L.T("管理控制台未完成此操作"), why);
            RenderIssues();
            RefreshCard();
            Poll();
        }

        // 删除要点两下：第一下按钮变成“确认删除”，5 秒内不点就算了。跟第一下隔不到 0.6 秒的是双击带出来的，不算确认
        void Remove(string id)
        {
            if (!Admin.Connected) { Open("https://login.tailscale.com/admin/machines"); return; }
            string key = "del:" + id;
            if (confirm == key)
            {
                if ((DateTime.Now - confirmAt).TotalMilliseconds >= 600) Act(key, () => Admin.Delete(id));
                return;
            }
            confirm = key;
            confirmAt = DateTime.Now;
            RenderIssues();
            RefreshCard();
            Ui.After(5000, () =>
            {
                if (confirm != key) return;
                confirm = null;
                RenderIssues();
                RefreshCard();
            });
        }

        // 本次启动以来的看 Tailscale 自己的计数（分得出 IPv6、IPv4 直连）；今天、几天的看 NetLog 的记录（只分直连、中继）
        void RenderTraffic()
        {
            long rx, tx;
            List<Tuple<string, long, Brush>> parts;
            if (range == 0)
            {
                rx = Sum("inbound");
                tx = Sum("outbound");
                parts = new List<Tuple<string, long, Brush>>
                {
                    Tuple.Create(L.T("IPv6 直连"), Both("direct_ipv6"), (Brush)Th.Fg),
                    Tuple.Create(L.T("IPv4 直连"), Both("direct_ipv4"), (Brush)Th.Mute),
                    Tuple.Create(L.T("中继"), Both("derp") + Both("peer_relay_ipv4") + Both("peer_relay_ipv6"), Stripes()),
                };
            }
            else
            {
                rx = usage.Values.Sum(u => u.Rx);
                tx = usage.Values.Sum(u => u.Tx);
                parts = new List<Tuple<string, long, Brush>>
                {
                    Tuple.Create(L.T("直连"), usage.Values.Sum(u => u.RxDirect + u.TxDirect), (Brush)Th.Fg),
                    Tuple.Create(L.T("中继"), usage.Values.Sum(u => u.Relay), Stripes()),
                };
            }
            down.Text = "↓ " + Ui.FormatSize(rx);
            up.Text = "↑ " + Ui.FormatSize(tx);
            parts = parts.Where(x => x.Item2 > 0).ToList();
            long all = parts.Sum(x => x.Item2);
            // 按显示出来的比例比（千分之一），字节数在涨但比例没变时不重画
            string sig = range + ":" + string.Join("/", parts.Select(p => p.Item1 + Math.Round(p.Item2 * 1000.0 / all)));
            if (sig == trafficShown) return;
            trafficShown = sig;
            footBox.ToolTip = Ui.Tip(Th, range == 0 ? L.T("自 Tailscale 本次启动以来的收发流量") : L.T("TailOrbit 每分钟记录一次，记录只保存在本机"));
            bar.ColumnDefinitions.Clear();
            bar.Children.Clear();
            legend.Children.Clear();
            if (all == 0) return;
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

        // 一行体检：左边一个点（绿＝好、红＝要管、空心＝说明一下），名目、结果；open 不是 null 的整行能点开看详情
        FrameworkElement Row(string state, string key, string value, Action<FrameworkElement> open = null)
        {
            var k = Kit.Text(key, 14, Th.Mute);
            k.Margin = new Thickness(12, 0, 12, 0);
            var v = Kit.Text(value, 14, Th.Fg);
            var row = new Border { Height = 40, BorderBrush = Th.Hair, BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent };
            if (open == null)
            {
                row.Child = Ui.Columns(1, Mark(state), k, v);
                return row;
            }
            var chev = new IconView("chevron-down", 13, Th.Mute) { Margin = new Thickness(6, 1, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90) };
            row.Child = Ui.Columns(1, Mark(state), k, v, chev);
            row.Cursor = Cursors.Hand;
            row.MouseEnter += delegate { chev.Stroke = Th.Fg; };
            row.MouseLeave += delegate { chev.Stroke = Th.Mute; };
            row.MouseLeftButtonUp += delegate { open(row); };
            return row;
        }

        // 一件要处理的事；action 是按钮上的字，click 是 null 时按钮只显示不能点（正在做）；sure 是等确认删除，按钮变红
        FrameworkElement Issue(string text, string action, Action click, bool sure = false)
        {
            var t = new TextBlock { Text = text, FontSize = 14, Foreground = Th.Fg, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 8, 12, 8) };
            var row = new Border { MinHeight = 48, BorderBrush = Th.Hair, BorderThickness = new Thickness(0, 1, 0, 0) };
            if (action == null) { row.Child = Ui.Columns(1, Mark("bad"), t); return row; }
            row.Child = Ui.Columns(1, Mark("bad"), t, Small(action, click, sure));
            return row;
        }

        // 小按钮：淡底；等确认删除时红底白字
        Border Small(string text, Action click, bool sure)
        {
            Brush rest = sure ? Th.Bad : Th.Hair;
            var b = new Border
            {
                Height = 32, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(9), Background = rest, Cursor = click == null ? null : Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Opacity = click == null ? 0.6 : 1, Child = new TextBlock { Text = text, FontSize = 13, FontWeight = sure ? FontWeights.SemiBold : FontWeights.Normal, Foreground = sure ? Th.Bg : Th.Fg, VerticalAlignment = VerticalAlignment.Center }
            };
            if (click == null) return b;
            b.MouseEnter += delegate { if (sure) b.Opacity = 0.88; else b.Background = Th.Fill; };
            b.MouseLeave += delegate { b.Opacity = 1; b.Background = rest; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
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
            // 点一下马上再测一次延迟，弹出它的卡片
            d.Disc.MouseLeftButtonUp += delegate
            {
                Ui.ScaleFrom(d.Pop, 0.9, 1.06, 360, 0, Ui.Spring);
                if (d.Online && !d.Pinging) Ping(d);
                ShowCard(d);
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
            // 名字下面：在传东西写速度，离线写多久了，选了今天或几天时写这段时间的流量
            int dir = 0;
            NetLog.Usage used;
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
            else if (on && range > 0 && usage.TryGetValue(d.Id, out used) && used.Total > 0)
            {
                d.Note.Text = Ui.FormatSize(used.Total);
                d.Note.FontFamily = Ui.MonoFont;
                d.Note.Foreground = Th.Dim;
            }
            else d.Note.Text = "";
            if (card != null && card.IsOpen && cardIp == d.Ip) cardPath.Text = PathText(d);
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
            if (card != null && cardIp == d.Ip) card.IsOpen = false;   // 刚删掉的就是卡片上这台
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

        // ---------- 设备卡片 ----------

        // 点一台设备：现在走哪条路、近 24 小时的延迟、近 30 天每天的流量；连着管理接口时还能删掉它（离线的）、关掉它的密钥到期
        async void ShowCard(Dot d)
        {
            // 卡片开着时再点同一台是想收起：按下那一下已经把它关了，别马上又开一个
            if (card != null && cardIp == d.Ip && (card.IsOpen || (DateTime.Now - cardClosed).TotalMilliseconds < 250)) { card.IsOpen = false; return; }
            if (card != null) card.IsOpen = false;
            string id = d.Id, ip = d.Ip;
            cardIp = ip;
            var log = await Task.Run(() => Tuple.Create(NetLog.Recent(id, TimeSpan.FromHours(24)), NetLog.Daily(id, 30)));
            if (cardIp != ip || !dots.ContainsKey(ip) || !visible) return;   // 等的时候点了别的、它没了、换了页
            confirm = null;
            var body = CardBody(d, log.Item1, log.Item2);
            card = new Popup { PlacementTarget = d.Disc, Placement = PlacementMode.Right, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = 4, VerticalOffset = -60 };
            card.Closed += delegate { cardClosed = DateTime.Now; };
            Kit.Pop(Th, card, body, 344, 16, new Thickness(20, 18, 20, 18), 16);
        }

        FrameworkElement CardBody(Dot d, List<NetLog.Sample> recent, List<NetLog.Usage> days)
        {
            var name = new TextBlock { Text = d.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, TextTrimming = TextTrimming.CharacterEllipsis };
            cardPath = Kit.Mono(PathText(d), 12.5, Th.Mute);
            cardPath.Margin = new Thickness(0, 3, 0, 0);
            var who = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { name, cardPath } };
            var body = new StackPanel();
            body.Children.Add(Ui.Columns(1, Kit.Disc(Th, Icons.ForOs(d.Os), 44, d.Online), who));
            // 延迟：只在正在通信时测（不为了画图去叫醒手机）
            var samples = recent.Where(x => x.Ms >= 0).ToList();
            body.Children.Add(Heading(L.T("延迟 · 24 小时"), samples.Count > 0 ? L.T("最高 {0} ms", samples.Max(x => x.Ms)) : ""));
            var trend = new Grid { Height = 92 };
            trend.Children.Add(new Trend(Th, samples) { ToolTip = Ui.Tip(Th, L.T("实线为直连，虚线为经中继；空白处没有通信")) });
            if (samples.Count == 0) trend.Children.Add(new TextBlock { Text = L.T("近 24 小时没有通信"), FontSize = 13, Foreground = Th.Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(trend);
            // 流量：近 30 天每天一根，直连实心、中继斜纹叠在上面
            body.Children.Add(Heading(L.T("流量 · 30 天"), ""));
            body.Children.Add(Bars(days));
            var sums = new UniformGrid { Columns = 3, Margin = new Thickness(0, 12, 0, 0) };
            sums.Children.Add(Stat(L.T("今天"), days[days.Count - 1].Total));
            sums.Children.Add(Stat(L.T("近 {0} 天", 7), days.Skip(days.Count - 7).Sum(u => u.Total)));
            sums.Children.Add(Stat(L.T("近 {0} 天", 30), days.Sum(u => u.Total)));
            body.Children.Add(sums);
            cardFoot = new Border();
            body.Children.Add(cardFoot);
            CardActions(d);
            return body;
        }

        // 现在走哪条路：地址、直连还是经哪里中继、延迟；离线的写上次在线
        static string PathText(Dot d)
        {
            if (!d.Online) return d.LastSeen != null ? L.T("上次在线：{0}", Ui.When(d.LastSeen.Value.ToLocalTime())) : L.T("离线");
            string via = d.Active ? d.Relay : d.Via;
            string path = d.Ring == "direct" ? L.T("直连") : via == "peer" ? L.T("经其他设备中继") : L.T("经{0}中继", City(via));
            return d.Ip + " · " + path + (d.Ms >= 0 ? " · " + d.Ms + " ms" : "");
        }

        FrameworkElement Heading(string text, string end)
        {
            var row = Ui.Columns(0, Kit.Text(text, 13, Th.Mute), Kit.Mono(end, 12, Th.Dim));
            row.Margin = new Thickness(0, 18, 0, 8);
            return row;
        }

        FrameworkElement Stat(string label, long bytes)
        {
            return new StackPanel { Children = { Kit.Text(label, 12.5, Th.Mute), Kit.Text(Ui.FormatSize(bytes), 15, Th.Fg, true) } };
        }

        // 一天一根：高低按这 30 天里最多的那天比；悬停看那天的日期和多少
        FrameworkElement Bars(List<NetLog.Usage> days)
        {
            long max = Math.Max(1, days.Max(u => u.Total));
            var g = new Grid { Height = 64 };
            var f = L.Culture.DateTimeFormat;
            for (int i = 0; i < days.Count; i++)
            {
                var u = days[i];
                bool today = i == days.Count - 1;
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
                if (u.Total == 0) stack.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = Th.Hair });
                else
                {
                    double relay = 62.0 * u.Relay / max, direct = 62.0 * (u.Total - u.Relay) / max;
                    if (u.Relay > 0) stack.Children.Add(new Border { Height = Math.Max(2, relay), Background = Stripes(), CornerRadius = new CornerRadius(2, 2, 0, 0) });
                    if (u.Total > u.Relay) stack.Children.Add(new Border { Height = Math.Max(2, direct), Background = today ? Th.Fg : Th.Mute, CornerRadius = new CornerRadius(u.Relay > 0 ? 0 : 2, u.Relay > 0 ? 0 : 2, 0, 0) });
                }
                string when = DateTime.Today.AddDays(i - days.Count + 1).ToString(f.MonthDayPattern, L.Culture);
                var col = new Border
                {
                    Margin = new Thickness(1.5, 0, 1.5, 0), Background = Brushes.Transparent, Child = stack,
                    ToolTip = Ui.Tip(Th, when + " · " + Ui.FormatSize(u.Total) + (u.Relay > 0 ? " · " + L.T("中继 {0}", Ui.FormatSize(u.Relay)) : ""))
                };
                Grid.SetColumn(col, i);
                g.Children.Add(col);
            }
            return g;
        }

        // 卡片底下的按钮：连着管理接口时离线的能删、有到期的能关掉到期；没连的去管理控制台
        void CardActions(Dot d)
        {
            bool offline = !d.Online, expires = d.KeyExpiry != null;
            if (!offline && !expires) { cardFoot.Child = null; return; }
            var stack = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            string id = d.Id;
            if (!Admin.Connected) stack.Children.Add(Kit.Button(Th, L.T("前往管理控制台"), false, () => Open("https://login.tailscale.com/admin/machines")));
            else
            {
                if (expires)
                {
                    var key = Kit.Button(Th, acting == "key:" + id ? L.T("正在处理…") : L.T("关闭密钥到期"), false, () => Act("key:" + id, () => Admin.NoExpiry(id)));
                    stack.Children.Add(key);
                }
                if (offline)
                {
                    bool sure = confirm == "del:" + id;
                    var del = Small(acting == "del:" + id ? L.T("正在处理…") : sure ? L.T("确认删除") : L.T("删除这台设备"), acting == "del:" + id ? null : (Action)(() => Remove(id)), sure);
                    del.Height = 40;
                    del.CornerRadius = new CornerRadius(11);
                    ((TextBlock)del.Child).HorizontalAlignment = HorizontalAlignment.Center;
                    if (!sure) ((TextBlock)del.Child).Foreground = Th.Bad;
                    del.Margin = new Thickness(0, expires ? 8 : 0, 0, 0);
                    stack.Children.Add(del);
                }
            }
            cardFoot.Child = stack;
        }

        void RefreshCard()
        {
            Dot d;
            if (card != null && card.IsOpen && cardIp != null && dots.TryGetValue(cardIp, out d)) CardActions(d);
        }

        // ---------- 对外开放 ----------

        // 其他设备能连进来的：哪个程序在哪个端口，tailscale serve 的分享（公开到互联网的标红）；底下去设置页（阻止所有传入连接在那里）
        void ShowExposure(FrameworkElement anchor)
        {
            if (openList != null && (openList.IsOpen || (DateTime.Now - openClosed).TotalMilliseconds < 250)) { openList.IsOpen = false; return; }
            openList = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Left, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = -6 };
            openList.Closed += delegate { openClosed = DateTime.Now; };
            Kit.Pop(Th, openList, new ScrollViewer { Content = ExposureBody(), MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false }, 340, 16, new Thickness(18, 16, 18, 16), 16);
        }

        FrameworkElement ExposureBody()
        {
            var list = new StackPanel();
            list.Children.Add(new TextBlock { Text = L.T("其他设备可以连接的端口"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, Margin = new Thickness(0, 0, 0, 10) });
            if (exposure.Wide) list.Children.Add(new TextBlock { Text = L.T("Windows 防火墙没有过滤此网络"), FontSize = 13, Foreground = Th.Bad, Margin = new Thickness(0, 0, 0, 10) });
            foreach (var it in exposure.Items)
            {
                bool funnel = it.Share != null && it.Share.Public;
                var name = Kit.Text(it.Name, 14, Th.Fg);
                name.Margin = new Thickness(0, 0, 12, 0);
                // 端口多的（Windows 系统服务那一堆）只写前三个，悬停看全部
                string all = string.Join(", ", it.Ports);
                var ports = Kit.Mono(funnel ? L.T("公开到互联网") + " · " + it.Ports[0] : it.Ports.Count > 4 ? string.Join(", ", it.Ports.Take(3)) + " +" + (it.Ports.Count - 3) : all, 12.5, funnel ? Th.Bad : Th.Mute);
                list.Children.Add(new Border
                {
                    Height = 40, BorderBrush = Th.Hair, BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent, Child = Ui.Columns(0, name, ports),
                    ToolTip = Ui.Tip(Th, it.Share != null ? it.Share.Url : it.Path + "\n" + all)
                });
            }
            var go = Kit.Button(Th, L.T("前往设置"), false, () => { openList.IsOpen = false; Host.ShowPage("settings"); });
            go.Margin = new Thickness(0, 14, 0, 0);
            list.Children.Add(go);
            return list;
        }

        // ---------- 小工具 ----------

        public static string City(string code)
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

    // 设备卡片上近 24 小时的延迟：横着是时间、竖着是毫秒；直连实线、经中继虚线，前后隔了 10 分钟以上就断开（没在通信、离线），孤零零的一次画个点
    class Trend : FrameworkElement
    {
        readonly List<NetLog.Sample> points;
        readonly Pen solid, dashed, floor;
        readonly Brush dot;

        public Trend(Theme th, List<NetLog.Sample> samples)
        {
            points = samples;
            solid = new Pen(th.Fg, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dashed = new Pen(th.Mute, 1.6) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0), StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            floor = new Pen(th.Hair, 1);
            dot = th.Fg;
            foreach (var p in new[] { solid, dashed, floor }) p.Freeze();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // 整块都能悬停出说明
            dc.DrawLine(floor, new Point(0, h - 0.5), new Point(w, h - 0.5));
            if (points.Count == 0) return;
            double top = Math.Max(20, points.Max(x => x.Ms)) * 1.12;
            var end = DateTime.UtcNow;
            Func<NetLog.Sample, Point> at = s => new Point(Math.Max(0, Math.Min(w, w - (end - s.At).TotalHours / 24 * w)), h - 3 - (h - 10) * s.Ms / top);
            for (int i = 0; i < points.Count; i++)
            {
                bool before = i > 0 && (points[i].At - points[i - 1].At).TotalMinutes <= 10;
                bool after = i + 1 < points.Count && (points[i + 1].At - points[i].At).TotalMinutes <= 10;
                if (before) dc.DrawLine(points[i].Link == "d" ? solid : dashed, at(points[i - 1]), at(points[i]));
                if (!before && !after) dc.DrawEllipse(dot, null, at(points[i]), 1.8, 1.8);
            }
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
