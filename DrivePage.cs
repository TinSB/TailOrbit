// 云盘页（Taildrive）：把文件夹共享给自己的其他设备，也能直接打开别的设备共享的文件夹。
// 没开通时（访问规则里没给 drive:share）一键开通：用户生成一把访问令牌、复制，程序替用户改好规则（Api.cs）；也可以自己复制那两段去贴。
// 开通后左边是本机共享出去的（拖文件夹进来就共享），
// 右边是别的设备共享的——经 Tailscale 内置的 WebDAV（100.100.100.100:8080）列出来，点“打开”在资源管理器里打开
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml;

namespace TailOrbit
{
    class DrivePage : Page
    {
        const string Dav = "http://100.100.100.100:8080";
        const string Acl =
            "\"nodeAttrs\": [\n" +
            "  { \"target\": [\"autogroup:member\"],\n" +
            "    \"attr\": [\"drive:share\", \"drive:access\"] }\n" +
            "],\n" +
            "\"grants\": [\n" +
            "  { \"src\": [\"autogroup:member\"], \"dst\": [\"autogroup:self\"],\n" +
            "    \"app\": { \"tailscale.com/cap/drive\":\n" +
            "      [{ \"shares\": [\"*\"], \"access\": \"rw\" }] } }\n" +
            "]";

        class Mine
        {
            public string Name = "", Path = "";
        }

        class Remote
        {
            public string Machine = "", Os = "", At = "";   // At：WebDAV 里这台设备的路径，/tailnet/设备/
            public List<string> Shares = new List<string>();
        }

        class Data
        {
            public bool Locked;
            public string Error, Away;   // Away：列不出别的设备的共享时的原因
            public List<Mine> Mine = new List<Mine>();
            public List<Remote> Remotes = new List<Remote>();
            public List<string> Icons = new List<string>();   // 没开通时画在轨道上的几台设备
        }

        readonly Grid body = new Grid { Margin = new Thickness(48, 76, 48, 36) };
        readonly RotateTransform orbitTurn = new RotateTransform();
        readonly DispatcherTimer clip = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        List<Mine> mine = new List<Mine>();
        Border drop;
        bool locked, lifted;
        int loads;

        // 没开通时的图和右边的步骤
        Canvas art, spokes;
        Border side, lockBadge;
        Grid keyDisc;
        SolidColorBrush coreFill, coreInk;
        readonly ScaleTransform corePop = new ScaleTransform(1, 1), lockPop = new ScaleTransform(1, 1), keyPop = new ScaleTransform(1, 1);
        readonly TranslateTransform keyAt = new TranslateTransform();
        readonly List<Grid> devices = new List<Grid>();
        string stage = "idle", failure, working;
        string shownSig;   // 现在画着的是哪份数据
        readonly HashSet<string> spent = new HashSet<string>();   // 用过的令牌（已经作废）：失败回到等待时别再拿一次
        readonly HashSet<string> stale = new HashSet<string>();   // 点开通前剪贴板里就有的：自动读时不拿（可能是别处在用的，拿了会被作废），手动粘贴进框照用

        public DrivePage(Host host) : base(host)
        {
            Children.Add(body);
            clip.Tick += delegate { PeekClipboard(); };
        }

        public override void Shown()
        {
            Load();
            orbitTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(60)) { RepeatBehavior = RepeatBehavior.Forever });
            if (stage == "wait") Waiting(true);
        }

        public override void Hidden()
        {
            orbitTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            clip.Stop();
            keyPop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            keyPop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        async void Load()
        {
            int n = ++loads;
            var d = await Task.Run(() => Fetch());
            if (n != loads || stage != "idle") return;   // 正在开通：别把步骤冲掉
            Show(d);
        }

        void Show(Data d)
        {
            // 每次切到这页都会重读：内容跟上次一样就不拆了重建（重建正好落在页面滑入的动画中间）
            string sig = string.Join("\n", new[] { d.Locked.ToString(), d.Error, d.Away, string.Join("|", d.Icons) }
                .Concat(d.Mine.Select(m => m.Name + "\t" + m.Path))
                .Concat(d.Remotes.Select(r => r.Machine + "\t" + r.Os + "\t" + string.Join("|", r.Shares))));
            if (sig == shownSig && body.Children.Count > 0) return;
            shownSig = sig;
            bool first = body.Children.Count == 0 || locked != d.Locked;
            locked = d.Locked;
            mine = d.Mine;
            body.Children.Clear();
            body.ColumnDefinitions.Clear();
            if (d.Error != null) body.Children.Add(Kit.Text(d.Error, 15, Th.Mute));
            else if (d.Locked) Locked(d);
            else Opened(d);
            if (first) Ui.AnimFrom(body, OpacityProperty, 0, 1, 300);
        }

        static Data Fetch()
        {
            var d = new Data();
            var r = Tailscale.Run("drive list");
            if (r.Code != 0)
            {
                string err = Tailscale.FirstLine(r.Err, L.T("读不到云盘"));
                if (err.Contains("drive:share") || err.Contains("not enabled")) d.Locked = true;
                else d.Error = err;
            }
            else d.Mine = ParseList(r.Out);
            var s = Tailscale.Run("status --json");
            var peers = s.Code == 0 ? Tailscale.Get(Tailscale.Json(s.Out), "Peer") as Dictionary<string, object> : null;
            var os = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // 设备短名 → 系统
            if (peers != null)
            {
                var list = peers.Values.OfType<Dictionary<string, object>>().Where(p => ((Tailscale.Get(p, "OS") as string) ?? "") != "").ToList();
                foreach (var p in list) os[Tailscale.ShortName(p)] = (string)Tailscale.Get(p, "OS");
                d.Icons = list.OrderBy(p => true.Equals(Tailscale.Get(p, "Online")) ? 0 : 1).Take(3).Select(p => Icons.ForOs((string)Tailscale.Get(p, "OS"))).ToList();
            }
            if (!d.Locked && d.Error == null) d.Away = ListRemotes(d.Remotes, os);
            return d;
        }

        // drive list 打的是对齐的表：name、path、as 三列，第二行一排 - 标出每列多宽（路径里可能有空格，只能按宽度切）
        static List<Mine> ParseList(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            var list = new List<Mine>();
            if (lines.Length < 2) return list;
            var cols = Regex.Matches(lines[1], "-+").Cast<Match>().Select(m => m.Index).ToList();
            if (cols.Count < 2) return list;
            int pathAt = cols[1], asAt = cols.Count > 2 ? cols[2] : int.MaxValue;
            foreach (var line in lines.Skip(2).Where(l => l.Trim() != ""))
            {
                string name = line.Substring(0, Math.Min(line.Length, pathAt)).Trim();
                string path = line.Length > pathAt ? line.Substring(pathAt, Math.Min(line.Length, asAt) - pathAt).Trim() : "";
                list.Add(new Mine { Name = name, Path = path });
            }
            return list;
        }

        // 别的设备共享的：WebDAV 根下面一层是 tailnet，再一层是设备，再一层是共享
        static string ListRemotes(List<Remote> into, Dictionary<string, string> os)
        {
            try
            {
                foreach (var net in Below("/"))
                    foreach (var machine in Below(net))
                    {
                        var r = new Remote { Machine = Last(machine), At = machine };
                        os.TryGetValue(r.Machine, out r.Os);
                        // 不支持云盘的设备（比如 iPhone 回 404“taildrive not supported on platform”）、离线的：跳过这一台，别的照常列
                        try { r.Shares = Below(machine).Select(Last).ToList(); }
                        catch (WebException) { continue; }
                        if (r.Shares.Count > 0) into.Add(r);
                    }
                return null;
            }
            catch (WebException e) { return e.Message; }
            catch (XmlException e) { return e.Message; }
        }

        // WebDAV 里这一层下面的东西（PROPFIND，深度 1）
        static List<string> Below(string path)
        {
            var req = (HttpWebRequest)WebRequest.Create(Dav + path);
            req.Method = "PROPFIND";
            req.Headers["Depth"] = "1";
            req.Timeout = 5000;
            req.Proxy = null;   // 系统代理会把 100.100.100.100 也转出去
            using (var resp = req.GetResponse())
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.Load(resp.GetResponseStream());
                var list = new List<string>();
                foreach (XmlNode h in doc.GetElementsByTagName("href", "DAV:"))
                {
                    string href = Uri.UnescapeDataString(h.InnerText.Trim());
                    if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) href = new Uri(href).AbsolutePath;
                    if (!href.EndsWith("/")) href += "/";
                    if (href != path) list.Add(href);   // 第一条是它自己
                }
                return list;
            }
        }

        static string Last(string path)
        {
            return path.TrimEnd('/').Split('/').Last();
        }

        // ---------- 还没开通 ----------

        // 左边的图：上了锁的云盘，自己的设备在外圈转着等；开通时钥匙飞进锁里、锁弹开、设备一台台连上
        void Locked(Data d)
        {
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            art = new Canvas { Width = 440, Height = 440 };
            var orbit = new Ellipse { Width = 400, Height = 400, Stroke = Th.Rim, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 4 }, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = orbitTurn };
            Canvas.SetLeft(orbit, 20);
            Canvas.SetTop(orbit, 20);
            art.Children.Add(orbit);
            spokes = new Canvas();
            art.Children.Add(spokes);
            var ring = new Ellipse { Width = 220, Height = 220, Stroke = Th.Hair, StrokeThickness = 1 };
            Canvas.SetLeft(ring, 110);
            Canvas.SetTop(ring, 110);
            art.Children.Add(ring);
            coreFill = new SolidColorBrush(((SolidColorBrush)Th.Chip).Color);
            coreInk = new SolidColorBrush(Th.MuteColor);
            var core = new Grid { Width = 120, Height = 120, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = corePop };
            core.Children.Add(new Ellipse { Fill = coreFill });
            core.Children.Add(new IconView("drive", 46, coreInk, 1.5));
            lockBadge = new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Th.Fg, BorderBrush = Th.Bg, BorderThickness = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 4),
                Child = new IconView("lock", 15, Th.Ink, 2.2), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = lockPop
            };
            core.Children.Add(lockBadge);
            Canvas.SetLeft(core, 160);
            Canvas.SetTop(core, 160);
            art.Children.Add(core);
            devices.Clear();
            double[] angles = { -90, 30, 150 };
            for (int i = 0; i < d.Icons.Count; i++)
            {
                double a = angles[i] * Math.PI / 180;
                var rim = new SolidColorBrush(Th.DimColor);
                var dev = new Grid { Width = 62, Height = 62, Tag = new Point(220 + 200 * Math.Cos(a), 220 + 200 * Math.Sin(a)) };
                dev.Children.Add(new Ellipse { Stroke = rim, StrokeThickness = 1.5, Fill = Th.Bg });
                dev.Children.Add(new IconView(d.Icons[i], 24, Th.Mute));
                Canvas.SetLeft(dev, 220 + 200 * Math.Cos(a) - 31);
                Canvas.SetTop(dev, 220 + 200 * Math.Sin(a) - 31);
                art.Children.Add(dev);
                devices.Add(dev);
                Ui.AnimFrom(dev, OpacityProperty, 0, 1, 500, 200 + 120 * i);
            }
            // 等令牌时：一把钥匙停在外圈正下方
            keyDisc = new Grid { Width = 48, Height = 48, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new TransformGroup { Children = { keyPop, keyAt } } };
            keyDisc.Children.Add(new Ellipse { Fill = Th.Fg });
            keyDisc.Children.Add(new IconView("key", 20, Th.Ink, 2));
            Canvas.SetLeft(keyDisc, 220 - 24);
            Canvas.SetTop(keyDisc, 220 - 24);
            art.Children.Add(keyDisc);
            body.Children.Add(new Viewbox { Child = art, MaxWidth = 440, MaxHeight = 440, Margin = new Thickness(0, 0, 24, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

            side = new Border { VerticalAlignment = VerticalAlignment.Center, Width = 440 };
            Grid.SetColumn(side, 1);
            body.Children.Add(side);
            Step(stage, false);
        }

        // 右边按进度换内容：idle 一键开通 / wait 等你复制令牌 / work 正在改规则 / done 开通了
        void Step(string to, bool animate)
        {
            stage = to;
            var s = new StackPanel();
            var title = new TextBlock { Text = to == "done" ? L.T("云盘开通了") : L.T("云盘还没开"), FontFamily = Ui.DisplayFont, FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg };
            s.Children.Add(title);
            if (to == "idle")
            {
                s.Children.Add(Para(L.T("开通后能把文件夹共享给自己的其他设备，也能直接打开它们共享的。")));
                if (failure != null) s.Children.Add(FailText(failure));
                var go = Kit.Button(Th, L.T("一键开通"), true, Begin);
                go.Height = 44;
                go.MinWidth = 140;
                go.HorizontalAlignment = HorizontalAlignment.Left;
                go.Margin = new Thickness(0, 22, 0, 0);
                s.Children.Add(go);
                s.Children.Add(Manual());
            }
            else if (to == "wait" || to == "work")
            {
                bool work = to == "work";
                var steps = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
                steps.Children.Add(Numbered(1, work, L.T("在打开的网页上点 Generate access token"), work ? null : Kit.Button(Th, L.T("再打开一次"), false, () => OpenUrl(Api.KeysPage))));
                steps.Children.Add(Numbered(2, work, L.T("点 Copy 复制生成的令牌"), null));
                steps.Children.Add(Numbered(3, false, work ? working : L.T("回到这里，自动开通"), null, work));
                s.Children.Add(steps);
                if (!work)
                {
                    s.Children.Add(PasteBox());
                    s.Children.Add(Para(L.T("令牌只用这一次，用完马上作废。"), 13));
                }
                if (failure != null) s.Children.Add(FailText(failure));
                if (!work)
                {
                    var back = Kit.Button(Th, L.T("先不开"), false, () => { failure = null; Waiting(false); Step("idle", true); Load(); });
                    back.HorizontalAlignment = HorizontalAlignment.Left;
                    back.Margin = new Thickness(0, 18, 0, 0);
                    s.Children.Add(back);
                }
            }
            else s.Children.Add(Para(L.T("文件夹拖进来就能共享。")));
            side.Child = s;
            if (!animate) return;
            var shift = new TranslateTransform();
            s.RenderTransform = shift;
            Ui.AnimFrom(s, OpacityProperty, 0, 1, 220);
            Ui.AnimFrom(shift, TranslateTransform.YProperty, 10, 0, 320, 0, MainEase.Settle);
        }

        TextBlock Para(string text, double size = 15)
        {
            return new TextBlock { Text = text, FontSize = size, LineHeight = size + 10, Foreground = Th.Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, size > 14 ? 14 : 12, 0, 0) };
        }

        TextBlock FailText(string text)
        {
            return new TextBlock { Text = text, FontSize = 13.5, LineHeight = 21, Foreground = Th.Bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
        }

        // 一步：序号圆点（做完了变成勾，正在做外面转一圈弧）、一句话、右边可选一个按钮
        FrameworkElement Numbered(int n, bool done, string text, FrameworkElement end, bool busy = false)
        {
            // 宽高定在圆上、不定在格子上：格子定了宽高，WPF 会把比它大的弧裁成方块
            var num = new Grid { VerticalAlignment = VerticalAlignment.Center };
            num.Children.Add(new Ellipse { Width = 26, Height = 26, Fill = done ? Th.Fg : Th.Fill });
            if (busy)
            {
                // 弧比圆点大一圈，放在不裁剪的 Canvas 里居中
                var spin = new RotateTransform();
                var arc = new Arc(Th.FgColor, 16, 2) { Value = 0.3, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = spin };
                Canvas.SetLeft(arc, 13 - arc.Width / 2);
                Canvas.SetTop(arc, 13 - arc.Height / 2);
                num.Children.Add(new Canvas { Children = { arc } });
                spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            if (done) num.Children.Add(new IconView("check", 13, Th.Ink, 2.6));
            else num.Children.Add(new TextBlock { Text = n.ToString(), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var t = Kit.Text(text, 14.5, done ? Th.Mute : Th.Fg);
            // 要点的按钮名（Generate access token）不能被截掉：放不下就换行
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            t.LineHeight = 21;
            t.Margin = new Thickness(14, 6, 12, 6);
            if (end != null) ((Border)end).Height = 36;
            var row = end == null ? Ui.Columns(1, num, t) : Ui.Columns(1, num, t, end);
            row.MinHeight = 48;
            return row;
        }

        // 剪贴板读不到时（比如别的程序占着）也能手动粘贴
        FrameworkElement PasteBox()
        {
            var box = new TextBox { FontFamily = Ui.MonoFont, FontSize = 13, Foreground = Th.Fg, CaretBrush = Th.Fg, Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
            var hint = new TextBlock { Text = L.T("或者粘贴到这里"), FontSize = 13, Foreground = Th.Dim, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            box.TextChanged += delegate
            {
                hint.Visibility = box.Text == "" ? Visibility.Visible : Visibility.Collapsed;
                if (Api.Token.IsMatch(box.Text)) Got(box.Text);
            };
            return new Border { Height = 44, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(12), Background = Th.Tile, Child = new Grid { Children = { hint, box } } };
        }

        // 不想给令牌的：复制那两段、打开规则编辑页自己贴
        FrameworkElement Manual()
        {
            var label = new TextBlock { Text = L.T("自己改规则"), FontSize = 13.5, Foreground = Th.Mute, Cursor = Cursors.Hand, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = Ui.Tip(Th, L.T("复制要加的两段，打开访问规则自己贴进去")) };
            label.MouseEnter += delegate { label.TextDecorations = TextDecorations.Underline; label.Foreground = Th.Fg; };
            label.MouseLeave += delegate { label.TextDecorations = null; label.Foreground = Th.Mute; };
            label.MouseLeftButtonUp += delegate
            {
                if (!Ui.CopyText(Acl)) { label.Text = L.T("复制没成功，再点一次"); return; }   // 别贴进去别的东西
                label.Text = L.T("已复制，贴进打开的规则里");
                OpenUrl(Api.RulesPage);
            };
            return label;
        }

        // ---------- 一键开通 ----------

        void Begin()
        {
            failure = null;
            // 剪贴板里原来就有的令牌是别处用的：不拿来开通（拿了会被作废）
            try
            {
                var old = Api.Token.Match(Clipboard.ContainsText() ? Clipboard.GetText() : "");
                if (old.Success) stale.Add(old.Value);
            }
            catch (System.Runtime.InteropServices.COMException) { }
            OpenUrl(Api.KeysPage);
            Step("wait", true);
            Waiting(true);
        }

        // 等令牌：钥匙出现在外圈正下方的空位上一呼一吸；每 0.6 秒看一眼剪贴板（只认 tskey-api- 开头的，别的不看不存）
        void Waiting(bool on)
        {
            if (on)
            {
                // 上一次飞进锁里、淡出的动画还停在终点，先撤掉，不然钥匙回不到外圈
                keyAt.BeginAnimation(TranslateTransform.XProperty, null);
                keyAt.BeginAnimation(TranslateTransform.YProperty, null);
                keyDisc.BeginAnimation(OpacityProperty, null);
                keyAt.X = 0;
                keyAt.Y = 200;   // 设备在上、左下、右下，正下方空着
                keyDisc.Opacity = 1;
                Ui.ScaleFrom(keyPop, 0.3, 1, 460, 0, Ui.Spring);
                var swing = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1600), RepeatBehavior = RepeatBehavior.Forever };
                swing.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0), Ui.Glide));
                swing.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromPercent(0.5), Ui.Glide));
                swing.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), Ui.Glide));
                Ui.After(470, () =>
                {
                    if (stage != "wait") return;
                    keyPop.BeginAnimation(ScaleTransform.ScaleXProperty, swing);
                    keyPop.BeginAnimation(ScaleTransform.ScaleYProperty, swing);
                });
                clip.Start();
                return;
            }
            clip.Stop();
            keyPop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            keyPop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            keyPop.ScaleX = keyPop.ScaleY = 1;   // 撤掉呼吸后底值还是弹出时的 0.3，不改回 1 钥匙会突然缩小
            if (stage != "work") Ui.AnimTo(keyDisc, OpacityProperty, 0, 200);
        }

        void PeekClipboard()
        {
            if (stage != "wait" || !IsVisible || Away) return;
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (System.Runtime.InteropServices.COMException) { return; }   // 别的程序正开着剪贴板：下次再看
            var m = Api.Token.Match(text);
            if (m.Success && !stale.Contains(m.Value)) Got(m.Value);
        }

        async void Got(string token)
        {
            if (stage != "wait") return;
            var m = Api.Token.Match(token ?? "");
            if (!m.Success || spent.Contains(m.Value)) return;
            token = m.Value;
            spent.Add(token);   // 失败回到等待时，剪贴板里这把已经作废的别再拿一次
            // 拿到就从剪贴板里清掉（不管是读到的还是粘贴进框里的），令牌不留在外面
            try { if (Clipboard.ContainsText() && Clipboard.GetText().Contains(token)) Clipboard.Clear(); }
            catch (System.Runtime.InteropServices.COMException) { }
            failure = null;
            working = L.T("正在读访问规则");
            Step("work", false);   // 先进 work 再停等待：Waiting(false) 看到 work 就不会把钥匙提前淡掉
            Waiting(false);
            // 钥匙飞进锁里
            Ui.AnimTo(keyAt, TranslateTransform.XProperty, 30, 520, MainEase.Sink);
            Ui.AnimTo(keyAt, TranslateTransform.YProperty, 30, 520, MainEase.Sink);
            Ui.ScaleTo(keyPop, 0.4, 520, MainEase.Sink);
            Ui.After(500, () =>
            {
                if (stage != "work") return;   // 很快就失败、已经回到等待：别把刚放回外圈的钥匙淡掉
                Ui.AnimTo(keyDisc, OpacityProperty, 0, 120);
                Ui.ScaleFrom(corePop, 0.92, 1, 420, 0, Ui.Spring);
            });
            Action<int> progress = k => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (stage != "work") return;
                working = k == 1 ? L.T("正在读访问规则") : k == 2 ? L.T("正在加上云盘那两段") : L.T("正在作废这把令牌");
                Step("work", false);
            }));
            string err;
            bool revoked = false;
            try { err = await Task.Run(() => { bool r; string e = Api.EnableDrive(token, progress, out r); revoked = r; return e; }); }
            catch (Exception e) { err = e.Message; }   // 没想到的错也要回到能重来的状态，不能卡在转圈
            string keep = revoked ? null : L.T("令牌没能自动作废，请到 Tailscale 后台的密钥页手动删掉。");
            if (err != null)
            {
                failure = keep == null ? err : err + "\n" + keep;
                Step("wait", false);
                Waiting(true);
                return;
            }
            if (keep != null) Host.Notify(L.T("云盘开通了"), keep);
            // 规则存上了：Tailscale 把新权限推到本机要几秒，等 drive list 不再报没开通
            working = L.T("等本机拿到新权限");
            Step("work", false);
            for (int i = 0; i < 20; i++)
            {
                var d = await Task.Run(() => Fetch());
                if (!d.Locked && d.Error == null) { Unlock(d); return; }   // 读出错不算开通了
                await Task.Delay(1500);
            }
            // 不用再要令牌了：回到开头那一步（写着原因），再进这页会重新读，开通了就直接显示
            failure = L.T("规则已经改好了，本机还没拿到新权限。过一会儿再打开这页看看。");
            Step("idle", false);
        }

        // 开通了：锁弹开、云盘变实心、设备一台台连上，然后换成正常的页面
        void Unlock(Data d)
        {
            Step("done", true);
            Ui.ScaleTo(lockPop, 0, 300, MainEase.Sink);
            Ui.Tint(coreFill, Th.FgColor, 400);
            Ui.Tint(coreInk, Th.InkColor, 400);
            Ui.ScaleFrom(corePop, 0.85, 1, 640, 0, Ui.Spring);
            for (int i = 0; i < devices.Count; i++)
            {
                var p = (Point)devices[i].Tag;
                var line = new Line { X1 = 220, Y1 = 220, X2 = 220, Y2 = 220, Stroke = Th.Fg, StrokeThickness = 1.5, Opacity = 0.35 };
                spokes.Children.Add(line);
                Ui.AnimFrom(line, Line.X2Property, 220, p.X, 520, 250 + 150 * i, Ui.Glide);
                Ui.AnimFrom(line, Line.Y2Property, 220, p.Y, 520, 250 + 150 * i, Ui.Glide);
                var rim = (Ellipse)devices[i].Children[0];
                int k = i;
                Ui.After(700 + 150 * i, () => { rim.Stroke = Th.Fg; Ui.ScaleFrom(Pop(devices[k]), 0.85, 1, 460, 0, Ui.Spring); });
            }
            Ui.After(2400, () => { stage = "idle"; Show(d); });
        }

        static ScaleTransform Pop(FrameworkElement e)
        {
            var s = e.RenderTransform as ScaleTransform;
            if (s == null)
            {
                e.RenderTransformOrigin = new Point(0.5, 0.5);
                e.RenderTransform = s = new ScaleTransform(1, 1);
            }
            return s;
        }

        // ---------- 开通了 ----------

        void Opened(Data d)
        {
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左边：本机共享出去的
            var left = new StackPanel();
            left.Children.Add(Head(L.T("我共享出去的")));
            foreach (var m in d.Mine)
            {
                var item = m;
                var stop = Kit.Button(Th, L.T("停止共享"), false, () => Unshare(item.Name));
                stop.Height = 36;
                left.Children.Add(Row(Tile("folder"), Lines(item.Name, item.Path), stop, 64));
            }
            var hint = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            hint.Children.Add(new IconView("folder-plus", 26, Th.Mute) { HorizontalAlignment = HorizontalAlignment.Center });
            hint.Children.Add(new TextBlock { Text = L.T("把文件夹拖到这里共享"), FontSize = 14, Foreground = Th.Mute, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            drop = new Border { Height = 132, Margin = new Thickness(0, d.Mine.Count > 0 ? 4 : 0, 0, 0), CornerRadius = new CornerRadius(14), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Kit.Dashed(Th, hint, 14) };
            drop.MouseEnter += delegate { if (!lifted) drop.Background = Th.Wash; };
            drop.MouseLeave += delegate { if (!lifted) drop.Background = Brushes.Transparent; };
            drop.MouseLeftButtonUp += delegate
            {
                string path = FolderDialog.Pick(Window.GetWindow(this), L.T("选一个文件夹共享出去"));
                if (path != null) Share(new List<string> { path });
            };
            left.Children.Add(drop);
            body.Children.Add(left);

            // 右边：别的设备共享的
            var right = new StackPanel();
            right.Children.Add(Head(L.T("别的设备的")));
            foreach (var r in d.Remotes)
            {
                var card = new StackPanel();
                var head = Ui.Columns(1, new IconView(Icons.ForOs(r.Os ?? ""), 16, Th.Mute), Kit.Text(r.Machine, 14, Th.Mute));
                ((FrameworkElement)head.Children[1]).Margin = new Thickness(10, 0, 0, 0);
                head.Height = 44;
                card.Children.Add(head);
                foreach (var share in r.Shares)
                {
                    string at = r.At + share + "/";
                    var open = Kit.Button(Th, L.T("打开"), false, () => OpenShare(at));
                    open.Height = 36;
                    var row = Row(Tile("folder"), Kit.Text(share, 15, Th.Fg), open, 58);
                    row.Background = Brushes.Transparent;
                    row.Padding = new Thickness(0);
                    row.BorderBrush = Th.Hair;
                    row.BorderThickness = new Thickness(0, 1, 0, 0);
                    row.CornerRadius = new CornerRadius(0);
                    card.Children.Add(row);
                }
                right.Children.Add(new Border { Padding = new Thickness(16, 4, 10, 6), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(16), Background = Th.Tile, Child = card });
            }
            if (d.Remotes.Count == 0)
            {
                string why = d.Away == null ? L.T("别的设备还没共享文件夹") : L.T("列不出别的设备的共享：{0}", d.Away);
                var t = Kit.Text(why, 14, Th.Mute);
                t.Margin = new Thickness(18, 0, 18, 0);
                t.TextWrapping = TextWrapping.Wrap;
                right.Children.Add(Kit.Dashed(Th, new Grid { MinHeight = 56, Children = { t } }, 14));
            }
            Grid.SetColumn(right, 2);
            body.Children.Add(right);
        }

        FrameworkElement Head(string s)
        {
            var t = Kit.Title(Th, s);
            t.Margin = new Thickness(0, 0, 0, 14);
            return t;
        }

        Border Tile(string icon)
        {
            return new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = Th.Chip, Child = new IconView(icon, 18, Th.Fg) };
        }

        FrameworkElement Lines(string top, string bottom)
        {
            var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 12, 0) };
            s.Children.Add(Kit.Text(top, 15, Th.Fg));
            s.Children.Add(Kit.Mono(bottom, 12, Th.Mute));
            return s;
        }

        Border Row(FrameworkElement icon, FrameworkElement middle, FrameworkElement end, double height)
        {
            if (!(middle is StackPanel)) middle.Margin = new Thickness(14, 0, 12, 0);
            return new Border { Height = height, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 0, 10, 0), CornerRadius = new CornerRadius(14), Background = Th.Tile, Child = Ui.Columns(1, icon, middle, end) };
        }

        // ---------- 共享、停止共享、打开 ----------

        public override bool CanTake(List<string> paths, Point p)
        {
            bool ok = !locked && drop != null && AllExist(paths, Directory.Exists);
            Lift(ok);
            return ok;
        }

        public override void Take(List<string> paths, Point p)
        {
            Lift(false);
            Share(paths);
        }

        public override void DragEnd()
        {
            Lift(false);
        }

        void Lift(bool on)
        {
            if (on == lifted || drop == null) return;
            lifted = on;
            drop.Background = on ? Th.Wash : Brushes.Transparent;
        }

        async void Share(List<string> paths)
        {
            foreach (var path in paths)
            {
                string name = NameFor(path), dir = path;
                var r = await Task.Run(() => Tailscale.Run("drive share " + Quote(name) + " " + Quote(dir)));
                if (r.Code != 0) Host.Notify(L.T("没共享成：{0}", System.IO.Path.GetFileName(path.TrimEnd('\\'))), Tailscale.FirstLine(r.Err, L.T("tailscale drive share 失败")));
                else mine.Add(new Mine { Name = name, Path = path });
            }
            Load();
        }

        async void Unshare(string name)
        {
            var r = await Task.Run(() => Tailscale.Run("drive unshare " + Quote(name)));
            if (r.Code != 0) Host.Notify(L.T("没停掉共享：{0}", name), Tailscale.FirstLine(r.Err, L.T("tailscale drive unshare 失败")));
            Load();
        }

        // 共享名只能用小写字母、数字、下划线、括号和空格（Tailscale 的规矩）：别的字换成下划线，换完什么都不剩就叫 share，重名的后面加数字
        string NameFor(string path)
        {
            var sb = new StringBuilder();
            foreach (char c in System.IO.Path.GetFileName(path.TrimEnd('\\')).ToLowerInvariant())
                sb.Append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == ' ' || c == '(' || c == ')' ? c : '_');
            string name = sb.ToString().Trim().Trim('_');
            if (name == "") name = "share";
            string unique = name;
            for (int n = 2; mine.Any(m => m.Name == unique); n++) unique = name + "_" + n;
            return unique;
        }

        // 命令行参数加引号；结尾的反斜杠要成对，不然会把后面的引号吃掉（比如 D:\）
        static string Quote(string s)
        {
            return "\"" + (s.EndsWith("\\") ? s + "\\" : s) + "\"";
        }

        // 资源管理器打开 WebDAV 要写成 \\主机@端口\DavWWWRoot\路径
        static void OpenShare(string at)
        {
            string dir = "\"\\\\100.100.100.100@8080\\DavWWWRoot" + at.TrimEnd('/').Replace('/', '\\') + "\"";
            Ui.Launch(() => Process.Start("explorer.exe", dir));
        }

        static void OpenUrl(string url)
        {
            Ui.Launch(() => Process.Start(url));
        }
    }
}
