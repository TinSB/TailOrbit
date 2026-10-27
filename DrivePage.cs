// 云盘页（Taildrive）：把文件夹共享给自己的其他设备，也能直接打开别的设备共享的文件夹。
// 没开通时（访问规则里没给 drive:share）说清楚要加哪两段；开通后左边是本机共享出去的（拖文件夹进来就共享），
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
        List<Mine> mine = new List<Mine>();
        Border drop;
        bool locked, lifted;
        int loads;

        public DrivePage(Host host) : base(host)
        {
            Children.Add(body);
        }

        public override void Shown()
        {
            Load();
            orbitTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(60)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        public override void Hidden()
        {
            orbitTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        async void Load()
        {
            int n = ++loads;
            var d = await Task.Run(() => Fetch());
            if (n != loads) return;
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
                string err = Tailscale.FirstLine(r.Err, "读不到云盘");
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
                        r.Shares = Below(machine).Select(Last).ToList();
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

        void Locked(Data d)
        {
            // 右边按内容宽度排（规则代码不折行），左边的图在剩下的地方里缩放
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左边：上了锁的云盘，自己的设备在外圈转着等
            var art = new Canvas { Width = 440, Height = 440 };
            var orbit = new Ellipse { Width = 400, Height = 400, Stroke = Th.Rim, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 4 }, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = orbitTurn };
            Canvas.SetLeft(orbit, 20);
            Canvas.SetTop(orbit, 20);
            art.Children.Add(orbit);
            var ring = new Ellipse { Width = 220, Height = 220, Stroke = Th.Hair, StrokeThickness = 1 };
            Canvas.SetLeft(ring, 110);
            Canvas.SetTop(ring, 110);
            art.Children.Add(ring);
            var core = new Grid { Width = 120, Height = 120 };
            core.Children.Add(new Ellipse { Fill = Th.Chip });
            core.Children.Add(new IconView("drive", 46, Th.Mute, 1.5));
            core.Children.Add(new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Th.Fg, BorderBrush = Th.Bg, BorderThickness = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 2, 4), Child = new IconView("lock", 15, Th.Ink, 2.2)
            });
            Canvas.SetLeft(core, 160);
            Canvas.SetTop(core, 160);
            art.Children.Add(core);
            double[] angles = { -90, 30, 150 };
            for (int i = 0; i < d.Icons.Count; i++)
            {
                double a = angles[i] * Math.PI / 180;
                var dev = new Grid { Width = 62, Height = 62 };
                dev.Children.Add(new Ellipse { Stroke = Th.Dim, StrokeThickness = 1.5, Fill = Th.Bg });
                dev.Children.Add(new IconView(d.Icons[i], 24, Th.Mute));
                Canvas.SetLeft(dev, 220 + 200 * Math.Cos(a) - 31);
                Canvas.SetTop(dev, 220 + 200 * Math.Sin(a) - 31);
                art.Children.Add(dev);
                Ui.AnimFrom(dev, OpacityProperty, 0, 1, 500, 200 + 120 * i);
            }
            body.Children.Add(new Viewbox { Child = art, MaxWidth = 440, MaxHeight = 440, Margin = new Thickness(0, 0, 24, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

            // 右边：要在访问规则里加的两段
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            text.Children.Add(new TextBlock { Text = "云盘还没开", FontFamily = Ui.DisplayFont, FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg });
            text.Children.Add(new TextBlock { Text = "在访问规则里加上这两段（已经有 nodeAttrs、grants 的，把里面那一条加进去），就能把文件夹共享给自己的其他设备，也能直接打开它们共享的文件夹。", FontSize = 15, LineHeight = 25, Foreground = Th.Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 18) });
            text.Children.Add(new Border
            {
                Padding = new Thickness(18, 14, 18, 14), CornerRadius = new CornerRadius(14), Background = Th.Tile, BorderBrush = Th.Hair, BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = Acl, FontFamily = Ui.MonoFont, FontSize = 12.5, LineHeight = 21, Foreground = Th.Fg }
            });
            Border copy = null;
            copy = Kit.Button(Th, "复制这两段", true, () =>
            {
                if (!Ui.CopyText(Acl)) return;
                var label = (TextBlock)copy.Child;
                label.Text = "已复制";
                Ui.After(1400, () => label.Text = "复制这两段");
            });
            copy.Height = 44;
            copy.MinWidth = 128;
            var rules = Kit.Button(Th, "打开访问规则", false, () => OpenUrl("https://login.tailscale.com/admin/acls"));
            rules.Height = 44;
            rules.Margin = new Thickness(10, 0, 0, 0);
            text.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0), Children = { copy, rules } });
            Grid.SetColumn(text, 1);
            body.Children.Add(text);
        }

        // ---------- 开通了 ----------

        void Opened(Data d)
        {
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左边：本机共享出去的
            var left = new StackPanel();
            left.Children.Add(Head("我共享出去的"));
            foreach (var m in d.Mine)
            {
                var item = m;
                var stop = Kit.Button(Th, "停止共享", false, () => Unshare(item.Name));
                stop.Height = 36;
                left.Children.Add(Row(Tile("folder"), Lines(item.Name, item.Path), stop, 64));
            }
            var hint = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            hint.Children.Add(new IconView("folder-plus", 26, Th.Mute) { HorizontalAlignment = HorizontalAlignment.Center });
            hint.Children.Add(new TextBlock { Text = "把文件夹拖到这里共享", FontSize = 14, Foreground = Th.Mute, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            drop = new Border { Height = 132, Margin = new Thickness(0, d.Mine.Count > 0 ? 4 : 0, 0, 0), CornerRadius = new CornerRadius(14), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Kit.Dashed(Th, hint, 14) };
            drop.MouseEnter += delegate { if (!lifted) drop.Background = Th.Wash; };
            drop.MouseLeave += delegate { if (!lifted) drop.Background = Brushes.Transparent; };
            drop.MouseLeftButtonUp += delegate
            {
                string path = FolderDialog.Pick(Window.GetWindow(this), "选一个文件夹共享出去");
                if (path != null) Share(new List<string> { path });
            };
            left.Children.Add(drop);
            body.Children.Add(left);

            // 右边：别的设备共享的
            var right = new StackPanel();
            right.Children.Add(Head("别的设备的"));
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
                    var open = Kit.Button(Th, "打开", false, () => OpenShare(at));
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
                string why = d.Away == null ? "别的设备还没共享文件夹" : "列不出别的设备的共享：" + d.Away;
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
            bool ok = !locked && drop != null && paths.All(Directory.Exists);
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
                if (r.Code != 0) Host.Notify("没共享成：" + System.IO.Path.GetFileName(path.TrimEnd('\\')), Tailscale.FirstLine(r.Err, "tailscale drive share 失败"));
                else mine.Add(new Mine { Name = name, Path = path });
            }
            Load();
        }

        async void Unshare(string name)
        {
            var r = await Task.Run(() => Tailscale.Run("drive unshare " + Quote(name)));
            if (r.Code != 0) Host.Notify("没停掉共享：" + name, Tailscale.FirstLine(r.Err, "tailscale drive unshare 失败"));
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
            Process.Start("explorer.exe", "\"\\\\100.100.100.100@8080\\DavWWWRoot" + at.TrimEnd('/').Replace('/', '\\') + "\"");
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(url); }
            catch (Win32Exception) { }   // 没有默认浏览器
        }
    }
}
