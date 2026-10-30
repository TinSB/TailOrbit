// 主界面除星网以外的几页（分享、网络、云盘、本机、设置）共用的底板和小零件：开关、分段选择、按钮、复制行、设置分组、弹出列表
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace TailOrbit
{
    // 一页：切过来时 Shown，切走时 Hidden（停掉自己的刷新）；拖文件进窗口时由当前页决定接不接
    abstract class Page : Grid
    {
        protected readonly Host Host;
        protected readonly Theme Th;

        protected Page(Host host)
        {
            Host = host;
            Th = host.Theme;
            Background = Brushes.Transparent;
        }

        public virtual void Shown() { }
        public virtual void Hidden() { }

        // 窗口最小化了：各页的定时刷新先歇着，不在后台反复起 tailscale（最小化时 WPF 不发 Hidden，IsVisible 仍是 true）
        protected bool Away
        {
            get { var w = Window.GetWindow(this); return w == null || w.WindowState == WindowState.Minimized; }
        }
        public virtual bool CanTake(List<string> paths, Point p) { return false; }   // 拖着文件经过；p 相对本页
        public virtual void Take(List<string> paths, Point p) { }
        public virtual void DragEnd() { }

        // 拖着经过时每动一下都会问能不能放；同一次拖动的文件列表是同一个对象，磁盘查过一遍就记住
        List<string> checkedFor;
        bool checkedOk;

        protected bool AllExist(List<string> paths, Func<string, bool> exists)
        {
            if (!ReferenceEquals(paths, checkedFor)) { checkedFor = paths; checkedOk = paths.All(exists); }
            return checkedOk;
        }
    }

    static class Kit
    {
        public static TextBlock Text(string s, double size, Brush fg, bool bold = false)
        {
            return new TextBlock { Text = s, FontSize = size, Foreground = fg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        }

        public static TextBlock Mono(string s, double size, Brush fg)
        {
            var t = Text(s, size, fg);
            t.FontFamily = Ui.MonoFont;
            return t;
        }

        // 一页最多一个实心按钮，其余是淡底的
        public static Border Button(Theme th, string text, bool primary, Action click)
        {
            var label = new TextBlock { Text = text, FontSize = 14, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, Foreground = primary ? th.Ink : th.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Height = 40, MinWidth = 40, Padding = new Thickness(16, 0, 16, 0), CornerRadius = new CornerRadius(11), Background = primary ? th.Fg : th.Hair, Cursor = Cursors.Hand, Child = label };
            b.MouseEnter += delegate { if (primary) b.Opacity = 0.86; else b.Background = th.Fill; };
            b.MouseLeave += delegate { b.Opacity = 1; if (!primary) b.Background = th.Hair; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }

        // 停止、删除这类：红色描边，不抢眼但一看就知道会拿掉东西
        public static Border Danger(Theme th, string text, Action click)
        {
            var label = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = th.Bad, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Height = 42, CornerRadius = new CornerRadius(12), BorderBrush = new SolidColorBrush(Theme.Fade(th.BadColor, 0.55)), BorderThickness = new Thickness(1), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = label };
            b.MouseEnter += delegate { b.Background = th.BadSoft; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }

        public static Border IconButton(Theme th, string icon, string tip, Action click, double size = 36)
        {
            var glyph = new IconView(icon, 16, th.Mute);
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = glyph, ToolTip = Ui.Tip(th, tip) };
            b.MouseEnter += delegate { b.Background = th.Hair; glyph.Stroke = th.Fg; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; glyph.Stroke = th.Mute; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }

        // 一行可复制的字：点右边的按钮复制，按钮变成 ✓ 一会儿
        public static Border CopyRow(Theme th, TextBlock text)
        {
            var glyph = new IconView("copy", 15, th.Mute);
            var btn = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = glyph, ToolTip = Ui.Tip(th, L.T("复制")) };
            btn.MouseEnter += delegate { btn.Background = th.Hair; };
            btn.MouseLeave += delegate { btn.Background = Brushes.Transparent; };
            int stamp = 0;
            btn.MouseLeftButtonUp += delegate
            {
                if (string.IsNullOrEmpty(text.Text) || !Ui.CopyText(text.Text)) return;
                int k = ++stamp;
                glyph.Show("check");
                glyph.Stroke = th.Ok;
                Ui.After(1200, () => { if (k != stamp) return; glyph.Show("copy"); glyph.Stroke = th.Mute; });
            };
            return new Border { Height = 44, Padding = new Thickness(14, 0, 5, 0), CornerRadius = new CornerRadius(12), Background = th.Tile, Child = Ui.Columns(0, text, btn) };
        }

        // 左边名目、右边数值的一行，上面一条细线隔开
        public static Border InfoRow(Theme th, string key, FrameworkElement value)
        {
            var k = Text(key, 14, th.Mute);
            k.Margin = new Thickness(0, 0, 12, 0);   // 名目长时别贴着右边的值
            value.HorizontalAlignment = HorizontalAlignment.Right;
            return new Border { Height = 42, BorderBrush = th.Hair, BorderThickness = new Thickness(0, 1, 0, 0), Child = Ui.Columns(0, k, value) };
        }

        // 一组设置：一块圆角底板，一行一项，中间细线隔开
        public static Border Group(Theme th, params Border[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Height = 1, Background = th.Hair, Margin = new Thickness(18, 0, 18, 0) });
                double top = i == 0 ? 14 : 0, bottom = i == rows.Length - 1 ? 14 : 0;   // 悬停时的底色别从圆角里漏出来
                rows[i].CornerRadius = new CornerRadius(top, top, bottom, bottom);
                stack.Children.Add(rows[i]);
            }
            return new Border { CornerRadius = new CornerRadius(14), Background = th.Tile, Child = stack };
        }

        // 一行设置：左边说法，右边开关或当前值，悬停变亮；没改成时拿它的 RenderTransform 摇一下
        public static Border Row(Theme th, string text, FrameworkElement end)
        {
            var label = Text(text, 14.5, th.Fg);
            label.Margin = new Thickness(0, 0, 12, 0);
            // 有的语言说法长：放不下换到第二行，再放不下才截
            label.TextWrapping = TextWrapping.Wrap;
            label.LineHeight = 19;
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            label.MaxHeight = 40;
            var row = new Border { Height = 56, Padding = new Thickness(18, 0, 16, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Ui.Columns(0, label, end), RenderTransform = new TranslateTransform() };
            row.MouseEnter += delegate { row.Background = th.Chip; };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
            return row;
        }

        // 点一行在它下面弹出的一列选项：第一项（“跟随系统”“关”）下面隔一道细线，选中的打勾，notes 是右边的灰字；点了先收起再回调。
        // 太长（比如开了 Mullvad 有上百个出口节点）就用滚轮翻
        public static Popup Choices(Theme th, FrameworkElement anchor, double width, string[] items, string[] notes, int selected, Action<int> picked)
        {
            var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = Ui.RightUnder(anchor, width), VerticalOffset = 4 };
            var list = new StackPanel();
            for (int i = 0; i < items.Length; i++)
            {
                int k = i;
                var note = Text(notes == null ? "" : notes[i] ?? "", 13, th.Mute);
                note.Margin = new Thickness(10, 0, 8, 0);
                FrameworkElement mark = i == selected ? (FrameworkElement)new IconView("check", 15, th.Fg, 2.2) : new Border { Width = 15 };
                var item = new Border { Height = 38, Padding = new Thickness(12, 0, 10, 0), CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Ui.Columns(0, Text(items[i], 14, th.Fg), note, mark) };
                item.MouseEnter += delegate { item.Background = th.Hair; };
                item.MouseLeave += delegate { item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += delegate
                {
                    popup.IsOpen = false;
                    if (k != selected) picked(k);
                };
                list.Children.Add(item);
                if (i == 0) list.Children.Add(new Border { Height = 1, Background = th.Hair, Margin = new Thickness(8, 4, 8, 4) });
            }
            Pop(th, popup, new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false }, width, 16, new Thickness(6), 14);
            return popup;
        }

        // 弹出的卡片：浮在页面上、带阴影，淡入时往下落一点。pad 是给阴影留的边
        public static void Pop(Theme th, Popup popup, UIElement content, double width, double pad, Thickness padding, double radius)
        {
            var shift = new TranslateTransform();
            var card = new Border
            {
                Width = width, Margin = new Thickness(pad), Padding = padding, CornerRadius = new CornerRadius(radius), Background = th.Pop, BorderBrush = th.Hair, BorderThickness = new Thickness(1),
                Child = content, RenderTransform = shift, Effect = new DropShadowEffect { Color = Colors.Black, Direction = 270, ShadowDepth = 10, BlurRadius = 28, Opacity = th.Dark ? 0.35 : 0.16 }
            };
            TextElement.SetFontFamily(card, Ui.TextFont);
            popup.Child = card;
            popup.IsOpen = true;
            Ui.AnimFrom(card, UIElement.OpacityProperty, 0, 1, 150);
            Ui.AnimFrom(shift, TranslateTransform.YProperty, -6, 0, 260, 0, MainEase.Settle);
        }

        // 能翻的一块：上边、下边还有内容藏着的那一边淡出一截，不是一刀切
        public static void FadeEdges(ScrollViewer sv)
        {
            bool above = sv.VerticalOffset > 0.5, below = sv.VerticalOffset < sv.ScrollableHeight - 0.5;
            if (!above && !below) { sv.OpacityMask = null; return; }
            double edge = Math.Min(0.25, 32 / Math.Max(sv.ActualHeight, 1));
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(above ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
            mask.GradientStops.Add(new GradientStop(below ? Colors.Transparent : Colors.Black, 1));
            sv.OpacityMask = mask;
        }

        // 圆角虚线框：还没有、还没开的东西
        public static Grid Dashed(Theme th, UIElement child, double radius)
        {
            var g = new Grid();
            g.Children.Add(new Rectangle { RadiusX = radius, RadiusY = radius, Stroke = th.Rim, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 3 }, SnapsToDevicePixels = true });
            g.Children.Add(child);
            return g;
        }

        public static TextBlock Title(Theme th, string s)
        {
            return new TextBlock { Text = s, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = th.Fg };
        }

        // 圆形底的图标：设备、文件夹这些
        public static Grid Disc(Theme th, string icon, double size, bool solid)
        {
            var g = new Grid { Width = size, Height = size };
            g.Children.Add(new Ellipse { Fill = solid ? th.Fg : th.Chip, Stroke = solid ? null : th.Hair, StrokeThickness = 1 });
            g.Children.Add(new IconView(icon, size * 0.34, solid ? th.Ink : th.Fg));
            return g;
        }
    }

    // 开关：滑块弹过去，开着是实心的
    class Toggle : Border
    {
        readonly Theme th;
        readonly Ellipse knob;
        readonly TranslateTransform shift = new TranslateTransform();
        public bool On { get; private set; }

        public Toggle(Theme th, bool on)
        {
            this.th = th;
            Width = 46;
            Height = 28;
            CornerRadius = new CornerRadius(14);
            knob = new Ellipse { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), RenderTransform = shift };
            Child = knob;
            Set(on, false);
        }

        public void Set(bool on, bool animate)
        {
            On = on;
            Background = on ? th.Fg : th.Rim;
            knob.Fill = on ? th.Bg : th.Mute;
            Ui.AnimTo(shift, TranslateTransform.XProperty, on ? 18 : 0, animate ? 300 : 1, Ui.Spring);
        }
    }

    // 分段选择：底下一块滑动的板子跟着选中的那段走。标签栏和“谁能打开”都用它
    class Segmented : Border
    {
        readonly Theme th;
        readonly bool solid;
        readonly Border pill;
        readonly TranslateTransform pillAt = new TranslateTransform();
        readonly Panel row;
        readonly List<Border> items = new List<Border>();
        readonly List<TextBlock> labels = new List<TextBlock>();
        bool placed;
        public int Index { get; private set; }
        public event Action<int> Picked;

        // solid：选中的那段是实心亮底（页面里的选择）；否则是淡底（标签栏）。uniform：各段等宽、撑满。字比 14 小的段与段挨得紧一些
        public Segmented(Theme th, string[] options, int index, bool solid, bool uniform, double height, bool inChrome = false, double fontSize = 14)
        {
            this.th = th;
            this.solid = solid;
            Index = index;
            CornerRadius = new CornerRadius(12);
            Padding = new Thickness(3);
            Background = solid ? th.Bg : th.Wash;
            pill = new Border { CornerRadius = new CornerRadius(9), Background = solid ? th.Fg : th.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, RenderTransform = pillAt };
            row = uniform ? (Panel)new UniformGrid { Rows = 1 } : new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < options.Length; i++)
            {
                var t = new TextBlock { Text = options[i], FontSize = fontSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                // 底下垫一份看不见的粗体字：选中变粗时这段不变宽，板子滑过去的终点不会在半路被重新排版打断
                var ghost = new TextBlock { Text = options[i], FontSize = fontSize, FontWeight = FontWeights.SemiBold, Visibility = Visibility.Hidden, HorizontalAlignment = HorizontalAlignment.Center };
                double gap = fontSize < 14 ? 10 : 16;
                var b = new Border { Height = height, Padding = new Thickness(gap, 0, gap, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = new Grid { Children = { ghost, t } } };
                if (inChrome) WindowChrome.SetIsHitTestVisibleInChrome(b, true);
                int k = i;
                b.MouseEnter += delegate { if (k != Index) t.Foreground = th.Fg; };
                b.MouseLeave += delegate { Paint(); };
                b.MouseLeftButtonUp += delegate
                {
                    if (k == Index) return;
                    Select(k, true);
                    var h = Picked;
                    if (h != null) h(k);
                };
                items.Add(b);
                labels.Add(t);
                row.Children.Add(b);
            }
            var g = new Grid();
            g.Children.Add(pill);
            g.Children.Add(row);
            Child = g;
            row.SizeChanged += delegate { Place(false); };
            Paint();
        }

        public void Select(int i, bool animate)
        {
            Index = i;
            Paint();
            Place(animate);
        }

        void Paint()
        {
            for (int i = 0; i < labels.Count; i++)
            {
                bool on = i == Index;
                labels[i].Foreground = on ? (solid ? th.Ink : th.Fg) : th.Mute;
                labels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        void Place(bool animate)
        {
            var b = items[Index];
            if (b.ActualWidth < 1) return;
            double x = b.TranslatePoint(new Point(0, 0), row).X;
            pill.Height = b.ActualHeight;
            if (!animate || !placed)
            {
                placed = true;
                pill.Width = b.ActualWidth;
                pillAt.BeginAnimation(TranslateTransform.XProperty, null);
                pillAt.X = x;
                return;
            }
            Ui.AnimTo(pill, WidthProperty, b.ActualWidth, 320, Ui.Glide);
            Ui.AnimTo(pillAt, TranslateTransform.XProperty, x, 320, Ui.Glide);
        }
    }
}
