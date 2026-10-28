// 设置页：TailOrbit 自己的设置。左边是程序本身和它的更新（检查更新、自动下载更新），
// 右边是外观（跟随系统 / 浅色 / 深色）、常规（开机自启、界面语言）和剪贴板（剪贴板互通、和 Mac 共用剪贴板）
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace TailOrbit
{
    class SettingsPage : Page
    {
        static readonly string[] Looks = { "auto", "light", "dark" };
        static readonly string[] LookNames = { "跟随系统", "浅色", "深色" };
        // 外观小样和程序图标同一套颜色（底、轨道、圆点），各有浅色、深色一份
        static readonly Color[] Paper = { Color.FromRgb(0xE6, 0xE8, 0xEB), Color.FromRgb(0x1A, 0x1C, 0x1F) };
        static readonly Color[] Orbit = { Color.FromRgb(0x80, 0x86, 0x8E), Color.FromRgb(0x8C, 0x92, 0x9A) };
        static readonly Color[] Dot = { Color.FromRgb(0x1A, 0x1C, 0x1F), Color.FromRgb(0xE7, 0xE9, 0xEC) };

        readonly Border[] lookTiles = new Border[3];
        readonly TextBlock[] lookNames = new TextBlock[3];
        readonly TextBlock version, note;
        readonly ProgressButton update;
        Popup qrPop, langs;               // 剪贴板二维码、语言列表
        DateTime qrClosed, langsClosed;   // 和它们上次收起的时间

        public SettingsPage(Host host) : base(host)
        {
            var grid = new Grid { Margin = new Thickness(48, 76, 48, 36) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左边：TailOrbit 自己和它的更新
            var icon = new Image { Source = Ui.IconFrame(256, !Th.Dark), Width = 116, Height = 116, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var name = new TextBlock { Text = "TailOrbit", FontFamily = Ui.DisplayFont, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Th.Fg, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 20) };
            version = Kit.Text("", 14, Th.Fg);
            update = new ProgressButton(Th, "", Updates.Next) { Margin = new Thickness(0, 18, 0, 0) };
            note = new TextBlock { FontSize = 12, LineHeight = 18, Foreground = Th.Bad, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(icon);
            left.Children.Add(name);
            left.Children.Add(Kit.InfoRow(Th, L.T("版本"), version));
            left.Children.Add(AutoUpdateRow());
            left.Children.Add(update);
            left.Children.Add(note);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            // 右边：外观、常规、剪贴板
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(Kit.Title(Th, L.T("外观")));
            var looks = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 14, -4, 30) };
            for (int i = 0; i < Looks.Length; i++) looks.Children.Add(LookTile(i));
            PaintLooks(Array.IndexOf(Looks, Settings.Theme));
            right.Children.Add(looks);
            right.Children.Add(Kit.Title(Th, L.T("常规")));
            right.Children.Add(Group(AutostartRow(), LanguageRow()));
            right.Children.Add(Kit.Title(Th, L.T("剪贴板")));
            var clip = Group(ClipRow(), SyncRow());
            clip.Margin = new Thickness(0, 14, 0, 0);
            right.Children.Add(clip);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
            Children.Add(grid);

            Loaded += delegate { Updates.Changed += ShowUpdate; ShowUpdate(); };
            Unloaded += delegate { Updates.Changed -= ShowUpdate; };
        }

        // 一组设置：一块圆角底板，一行一项，中间细线隔开
        Border Group(params Border[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Th.Hair, Margin = new Thickness(18, 0, 18, 0) });
                double top = i == 0 ? 14 : 0, bottom = i == rows.Length - 1 ? 14 : 0;   // 悬停时的底色别从圆角里漏出来
                rows[i].CornerRadius = new CornerRadius(top, top, bottom, bottom);
                stack.Children.Add(rows[i]);
            }
            return new Border { CornerRadius = new CornerRadius(14), Background = Th.Tile, Margin = new Thickness(0, 14, 0, 30), Child = stack };
        }

        // 一行设置：左边说法，右边开关或当前值，悬停变亮；没改成时拿它的 RenderTransform 摇一下
        Border Row(string text, FrameworkElement end)
        {
            var label = Kit.Text(text, 14.5, Th.Fg);
            label.Margin = new Thickness(0, 0, 12, 0);
            var row = new Border { Height = 56, Padding = new Thickness(18, 0, 16, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Ui.Columns(0, label, end), RenderTransform = new TranslateTransform() };
            row.MouseEnter += delegate { row.Background = Th.Chip; };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            return row;
        }

        // ---------- 更新 ----------

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
        FrameworkElement AutoUpdateRow()
        {
            var t = new Toggle(Th, Settings.AutoUpdate);
            var row = Kit.InfoRow(Th, L.T("自动下载更新"), t);
            row.Background = Brushes.Transparent;
            row.Cursor = Cursors.Hand;
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

        // ---------- 常规：开机自启、语言 ----------

        Border AutostartRow()
        {
            var t = new Toggle(Th, Autostart.Enabled);
            var row = Row(L.T("开机时自动启动"), t);
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
            var row = Row(L.T("语言"), value);
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
            const double width = 210, pad = 16;   // pad：给阴影留的边
            var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = Ui.RightUnder(anchor, width), VerticalOffset = 4 };
            langs = popup;
            popup.Closed += delegate { langsClosed = DateTime.Now; };
            var list = new StackPanel();
            foreach (var code in new[] { "auto" }.Concat(L.Codes))
            {
                string pick = code;
                bool on = code == Settings.Language;
                var text = Kit.Text(code == "auto" ? L.T("跟随系统") : L.Names[Array.IndexOf(L.Codes, code)], 14, Th.Fg);
                FrameworkElement mark = on ? (FrameworkElement)new IconView("check", 15, Th.Fg, 2.2) : new Border();
                var item = new Border { Height = 38, Padding = new Thickness(12, 0, 10, 0), CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Ui.Columns(0, text, mark) };
                item.MouseEnter += delegate { item.Background = Th.Hair; };
                item.MouseLeave += delegate { item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += delegate
                {
                    popup.IsOpen = false;
                    if (!on) Ui.After(120, () => Host.SetLanguage(pick));   // 等弹出的列表收起再整页换
                };
                list.Children.Add(item);
                if (code == "auto") list.Children.Add(new Border { Height = 1, Background = Th.Hair, Margin = new Thickness(8, 4, 8, 4) });
            }
            Pop(popup, list, width, pad, new Thickness(6), 14);
        }

        // 弹出的卡片：浮在页面上、带阴影，淡入时往下落一点
        void Pop(Popup popup, UIElement content, double width, double pad, Thickness padding, double radius)
        {
            var shift = new TranslateTransform();
            var card = new Border
            {
                Width = width, Margin = new Thickness(pad), Padding = padding, CornerRadius = new CornerRadius(radius), Background = Th.Pop, BorderBrush = Th.Hair, BorderThickness = new Thickness(1),
                Child = content, RenderTransform = shift, Effect = new DropShadowEffect { Color = Colors.Black, Direction = 270, ShadowDepth = 10, BlurRadius = 28, Opacity = Th.Dark ? 0.35 : 0.16 }
            };
            TextElement.SetFontFamily(card, Ui.TextFont);
            popup.Child = card;
            popup.IsOpen = true;
            Ui.AnimFrom(card, OpacityProperty, 0, 1, 150);
            Ui.AnimFrom(shift, TranslateTransform.YProperty, -6, 0, 260, 0, MainEase.Settle);
        }

        // ---------- 剪贴板 ----------

        // 剪贴板互通：开着时自己的手机、Mac 能读这台电脑的剪贴板，iPhone 能把它的发过来。
        // 开着时开关左边弹出一个二维码按钮；刚打开时直接把二维码亮出来给手机扫
        Border ClipRow()
        {
            var t = new Toggle(Th, Settings.ClipOn);
            var scale = new ScaleTransform(t.On ? 1 : 0, t.On ? 1 : 0);
            var glyph = new IconView("qr", 17, Th.Mute);
            var qr = new Border
            {
                Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = glyph,
                ToolTip = Ui.Tip(Th, L.T("手机扫码")), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, IsHitTestVisible = t.On
            };
            qr.MouseEnter += delegate { qr.Background = Th.Hair; glyph.Stroke = Th.Fg; };
            qr.MouseLeave += delegate { qr.Background = Brushes.Transparent; glyph.Stroke = Th.Mute; };
            var row = Row(L.T("剪贴板互通"), new StackPanel { Orientation = Orientation.Horizontal, Children = { qr, t } });
            var shift = (TranslateTransform)row.RenderTransform;
            qr.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;   // 别让这一行当成拨开关
                // 二维码开着时再点是想收起：按下那一下已经把它关了，别马上又开一个
                if (qrPop != null && (qrPop.IsOpen || (DateTime.Now - qrClosed).TotalMilliseconds < 250)) { qrPop.IsOpen = false; return; }
                ShowQr(row);
            };
            bool busy = false;
            row.MouseLeftButtonUp += async delegate
            {
                if (busy) return;
                busy = true;
                bool want = !t.On;
                t.Set(want, true);
                string why = await Task.Run(() => ClipShare.Turn(want));
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
                if (want) ShowQr(row);
            };
            // 在托盘菜单里拨过：开关和二维码按钮跟着变
            Action sync = () =>
            {
                bool on = Settings.ClipOn;
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
            var row = Row(L.T("与 Mac 共享剪贴板"), new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, t } });
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
        void ShowQr(FrameworkElement anchor)
        {
            string url = ClipShare.Url;
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
            Pop(popup, new StackPanel { Children = { tile, hint, link } }, width, pad, new Thickness(14, 16, 14, 14), 16);
        }
    }
}
