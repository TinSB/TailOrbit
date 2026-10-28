// 分享页：本机在正中；里圈“我的设备”是只有自己设备能打开的分享，外圈“所有人”是公开到互联网的（带倒计时，到点自动关）。
// 拖文件或文件夹进窗口就新建一条；点一颗看链接、改成公开或停掉。用户自己用命令挂的分享也排在上面，背后的程序停了会标红
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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TailOrbit
{
    class SharePage : Page
    {
        const string Plus = "+";   // 选中“新建”时 selected 的值
        static readonly TimeSpan[] Ttls = { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromDays(1) };
        static readonly string[] TtlNames = { "15 分钟", "1 小时", "1 天" };

        // 一颗卫星：一条分享
        class Sat
        {
            public ServeEntry E;
            public readonly Canvas View = new Canvas();   // 原点就是卫星中心，整颗靠 At 挪
            public readonly TranslateTransform At = new TranslateTransform();
            public readonly ScaleTransform Pop = new ScaleTransform(1, 1);
            public SolidColorBrush Face, Ink, Edge;
            public IconView Icon;
            public Ellipse Badge, Track;
            public Arc Clock;
            public Line Link, Flow;
            public StackPanel Label;
            public TextBlock Name, Note;
            public Point Pos;
            public double Angle;
            public bool Flowing;
        }

        readonly Canvas field = new Canvas { ClipToBounds = true, Margin = new Thickness(0, 44, 432, 0) };
        readonly Canvas lines = new Canvas { IsHitTestVisible = false };
        readonly Canvas layer = new Canvas();
        readonly Ellipse inner, halo;
        readonly System.Windows.Shapes.Path outer, innerLine;
        readonly ScaleTransform outerScale = new ScaleTransform(1, 1), innerScale = new ScaleTransform(1, 1), innerLineScale = new ScaleTransform(1, 1), haloScale = new ScaleTransform(1, 1);
        readonly TextBlock outerTag, innerTag;
        readonly Grid core;
        readonly ScaleTransform corePop = new ScaleTransform(1, 1);
        readonly IconView coreIcon;
        readonly Canvas plus = new Canvas();
        readonly TranslateTransform plusAt = new TranslateTransform();
        readonly ScaleTransform plusPop = new ScaleTransform(1, 1);
        readonly Ellipse plusRing;
        readonly IconView plusIcon;
        readonly SolidColorBrush plusFill, innerFill;
        readonly Border panel;
        readonly Brush line, lineHot, lineBad, track;
        readonly double innerAlpha;
        readonly Dictionary<string, Sat> sats = new Dictionary<string, Sat>();
        readonly DispatcherTimer reload = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        readonly DispatcherTimer clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        Point o;
        double rIn = 190, rOut = 300;
        string selected = Plus, error, failure, pending, confirm, shown, shownKey;
        int pendingTtl = 1, loads;
        bool loaded, busy, lifted, sweeping, visible, roomy = true;
        Grid dropZone;
        IconView dropIcon;

        public SharePage(Host host) : base(host)
        {
            line = Frozen(Theme.Fade(Th.FgColor, 0.22));
            lineHot = Frozen(Theme.Fade(Th.FgColor, 0.38));
            lineBad = Frozen(Theme.Fade(Th.BadColor, 0.5));
            track = Frozen(Theme.Fade(Th.FgColor, 0.12));
            innerAlpha = Th.Dark ? 0.025 : 0.04;
            innerFill = new SolidColorBrush(Theme.Fade(Th.FgColor, innerAlpha));
            // 两个圈：里圈是一块淡淡的底加一圈细线，外圈是点线；圈线在顶上名字那里断开
            inner = new Ellipse { Fill = innerFill, IsHitTestVisible = false, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = innerScale };
            innerLine = new System.Windows.Shapes.Path { Stroke = Frozen(Theme.Fade(Th.FgColor, 0.18)), StrokeThickness = 1, IsHitTestVisible = false, Opacity = 0, RenderTransform = innerLineScale };
            outer = new System.Windows.Shapes.Path { Stroke = Frozen(Theme.Fade(Th.FgColor, 0.22)), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 0, 3 }, StrokeDashCap = PenLineCap.Round, IsHitTestVisible = false, Opacity = 0, RenderTransform = outerScale };
            innerTag = ZoneLabel(L.T("我的设备"));
            outerTag = ZoneLabel(L.T("所有人"));

            // 本机；拖着文件进来时外面一圈一圈往外荡
            halo = new Ellipse { Stroke = Th.Fg, StrokeThickness = 1.5, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = haloScale };
            coreIcon = new IconView("monitor", 32, Th.Ink);
            core = new Grid { Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = corePop };
            core.Children.Add(new Ellipse { Fill = Th.Fg });
            core.Children.Add(coreIcon);

            // 新建：虚线圈里一个加号
            plusFill = new SolidColorBrush(Theme.Fade(Th.FgColor, 0));
            plusRing = new Ellipse { Fill = plusFill, Stroke = Th.Rim, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2.5, 2 } };
            plusIcon = new IconView("plus", 20, Th.Mute);
            var pg = new Grid { Width = 56, Height = 56, Background = Brushes.Transparent, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = plusPop, ToolTip = Ui.Tip(Th, L.T("新建分享")) };
            pg.Children.Add(plusRing);
            pg.Children.Add(plusIcon);
            Canvas.SetLeft(pg, -28);
            Canvas.SetTop(pg, -28);
            pg.MouseEnter += delegate { if (!lifted) Ui.ScaleTo(plusPop, 1.07, 200, Ui.Spring); };
            pg.MouseLeave += delegate { if (!lifted) Ui.ScaleTo(plusPop, 1, 200); };
            pg.MouseLeftButtonUp += delegate { Select(Plus); };
            plus.Children.Add(pg);
            plus.RenderTransform = plusAt;
            plus.Opacity = 0;
            layer.Children.Add(plus);

            foreach (var e in new UIElement[] { outer, inner, innerLine, outerTag, innerTag, lines, halo, core, layer }) field.Children.Add(e);
            panel = new Border
            {
                Width = 404, Margin = new Thickness(0, 66, 28, 32), HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(18),
                Background = Th.Pop, BorderBrush = Th.Hair, BorderThickness = new Thickness(1), Padding = new Thickness(28, 28, 28, 26), Opacity = 0
            };
            Children.Add(field);
            Children.Add(panel);
            field.SizeChanged += delegate { Layout(false, null); };
            // 窗口窄时面板跟着收窄，左边的圈留够地方；矮到放不下大二维码时换小的
            SizeChanged += delegate
            {
                double w = Math.Max(340, Math.Min(404, ActualWidth * 0.36));
                panel.Width = w;
                field.Margin = new Thickness(0, 44, w + 28, 0);
                bool r = ActualHeight >= 720;
                if (r == roomy) return;
                roomy = r;
                if (loaded) RenderPanel(false);
            };
            reload.Tick += delegate { if (!Away) Load(); };
            clock.Tick += delegate { if (!Away) Tick(); };
        }

        public override void Shown()
        {
            visible = true;
            reload.Start();
            clock.Start();
            foreach (var s in sats.Values) Flow(s, Flows(s.E));
            Load();
        }

        public override void Hidden()
        {
            visible = false;
            reload.Stop();
            clock.Stop();
            foreach (var s in sats.Values) Flow(s, false);
        }

        async void Load()
        {
            int n = ++loads;
            var list = await Task.Run(() => Read());
            if (n == loads) Apply(list);
        }

        static List<ServeEntry> Read()
        {
            try { return Shares.Load(); }
            catch (Win32Exception) { return null; }   // 没装 Tailscale
        }

        void Apply(List<ServeEntry> list)
        {
            error = list == null ? L.T("无法读取") : null;
            var seen = new HashSet<string>();
            var added = new List<Sat>();
            foreach (var e in list ?? new List<ServeEntry>())
            {
                seen.Add(e.Key);
                Sat s;
                if (!sats.TryGetValue(e.Key, out s))
                {
                    s = Make();
                    sats[e.Key] = s;
                    added.Add(s);
                }
                s.E = e;
            }
            foreach (var s in sats.Values.Where(x => !seen.Contains(x.E.Key)).ToList()) Vanish(s);
            bool intro = !loaded;
            loaded = true;
            if (intro && sats.Count > 0) selected = Order().First().E.Key;
            if (selected != Plus && !sats.ContainsKey(selected)) selected = Plus;
            foreach (var s in sats.Values) Paint(s);
            PaintPlus();
            Layout(!intro, added);
            if (intro) Intro();
            else foreach (var s in added) Launch(s);
            if (Sig() != shown) RenderPanel(!intro && selected != shownKey);
        }

        // ---------- 星图 ----------

        Sat Make()
        {
            var s = new Sat();
            s.Face = new SolidColorBrush(ChipColor);
            s.Ink = new SolidColorBrush(Th.FgColor);
            s.Edge = new SolidColorBrush(Theme.Fade(Th.FgColor, 0.18));
            // 公开的外面一圈倒计时：亮的那段是还剩多少
            s.Track = new Ellipse { Width = 70, Height = 70, Stroke = track, StrokeThickness = 3, Opacity = 0, IsHitTestVisible = false };
            Canvas.SetLeft(s.Track, -35);
            Canvas.SetTop(s.Track, -35);
            s.Clock = new Arc(Th.FgColor, 33.5, 3) { Opacity = 0 };
            Canvas.SetLeft(s.Clock, -s.Clock.Width / 2);
            Canvas.SetTop(s.Clock, -s.Clock.Height / 2);
            var disc = new Grid { Width = 56, Height = 56, Background = Brushes.Transparent, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = s.Pop };
            disc.Children.Add(new Ellipse { Fill = s.Face, Stroke = s.Edge, StrokeThickness = 1 });
            s.Icon = new IconView("file", 20, s.Ink);
            disc.Children.Add(s.Icon);
            s.Badge = new Ellipse { Width = 14, Height = 14, Fill = Th.Bad, Stroke = Th.Bg, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
            disc.Children.Add(s.Badge);
            Canvas.SetLeft(disc, -28);
            Canvas.SetTop(disc, -28);
            disc.MouseEnter += delegate { Ui.ScaleTo(s.Pop, 1.07, 200, Ui.Spring); };
            disc.MouseLeave += delegate { Ui.ScaleTo(s.Pop, 1, 200); };
            disc.MouseLeftButtonUp += delegate { Select(s.E.Key); };
            s.Name = Kit.Text("", 13.5, Th.Fg);
            s.Note = Kit.Text("", 12.5, Th.Mute);
            s.Note.Margin = new Thickness(0, 1, 0, 0);
            s.Label = new StackPanel { IsHitTestVisible = false };
            s.Label.Children.Add(s.Name);
            s.Label.Children.Add(s.Note);
            foreach (var e in new UIElement[] { s.Track, s.Clock, disc, s.Label }) s.View.Children.Add(e);
            s.View.RenderTransform = s.At;
            s.Link = new Line { StrokeThickness = 1.5, Stroke = line };
            // 公开的：一串光点从本机往外流
            s.Flow = new Line { StrokeThickness = 3.2, Stroke = Th.Fg, StrokeDashArray = new DoubleCollection { 0, 22 / 3.2 }, StrokeDashCap = PenLineCap.Round, Opacity = 0 };
            lines.Children.Add(s.Link);
            lines.Children.Add(s.Flow);
            layer.Children.Insert(layer.Children.Count - 1, s.View);   // 加号一直在最上面
            return s;
        }

        void Paint(Sat s)
        {
            var e = s.E;
            bool on = e.Key == selected, dead = e.Dead != "", timed = Timed(e);
            Ui.Tint(s.Face, on ? Th.FgColor : ChipColor, 200);
            Ui.Tint(s.Ink, on ? Th.InkColor : Th.FgColor, 200);
            Ui.Tint(s.Edge, Theme.Fade(Th.FgColor, on ? 0 : 0.18), 200);
            s.Icon.Show(IconOf(e));
            s.Badge.Visibility = dead ? Visibility.Visible : Visibility.Collapsed;
            s.Link.Stroke = dead ? lineBad : e.Public ? lineHot : line;
            s.Track.Opacity = s.Clock.Opacity = timed ? 1 : 0;
            s.Name.Text = e.Label;
            Flow(s, visible && Flows(e));
            Countdown(s);
            PlaceLabel(s);
        }

        static bool Flows(ServeEntry e)
        {
            return e.Public && e.Dead == "";
        }

        static bool Timed(ServeEntry e)
        {
            return Flows(e) && e.Mine != null && e.Mine.Expires != null;
        }

        void Flow(Sat s, bool on)
        {
            if (on == s.Flowing) return;
            s.Flowing = on;
            if (on)
            {
                Ui.Loop(s.Flow, s.Flow, Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -22 / 3.2, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
                Ui.AnimTo(s.Flow, OpacityProperty, 0.9, 300);
                return;
            }
            Ui.Unloop(s.Flow, Shape.StrokeDashOffsetProperty);
            s.Flow.BeginAnimation(OpacityProperty, null);
            s.Flow.Opacity = 0;
        }

        void Countdown(Sat s)
        {
            if (!Timed(s.E)) { s.Note.Text = ""; return; }
            var m = s.E.Mine;
            var left = m.Expires.Value - DateTime.UtcNow;
            double total = (m.Expires.Value - (m.Since ?? m.Expires.Value)).TotalSeconds;
            s.Note.Text = Left(left);
            s.Clock.Value = total > 0 ? Math.Max(0, Math.Min(1, left.TotalSeconds / total)) : 0;
        }

        void Tick()
        {
            bool due = false;
            foreach (var s in sats.Values.Where(x => Timed(x.E)))
            {
                string before = s.Note.Text;
                Countdown(s);
                if (s.Note.Text != before) PlaceLabel(s);
                if (s.E.Mine.Expires.Value <= DateTime.UtcNow) due = true;
            }
            if (due) SweepNow();
        }

        // 页面开着时到点的不用等托盘那边半分钟一次的清理
        async void SweepNow()
        {
            if (sweeping) return;
            sweeping = true;
            try { await Task.Run(() => Shares.Sweep()); }
            finally { sweeping = false; }
            Load();
        }

        // 位置：里圈的从左上开始等分（加号排最后），外圈的从右上开始等分
        void Layout(bool animate, ICollection<Sat> fresh)
        {
            double w = field.ActualWidth, h = field.ActualHeight;
            if (w < 50 || h < 50) return;
            o = new Point(w * 0.47, h / 2 + 8);
            rOut = Math.Max(120, Math.Min(Math.Min(o.X, w - o.X), Math.Min(o.Y - 20, h - o.Y)) - 30);
            rIn = rOut * 0.64;
            Center(inner, rIn * 2);
            outer.Data = GapRing(rOut, outerTag);
            innerLine.Data = GapRing(rIn, innerTag);
            outerScale.CenterX = innerLineScale.CenterX = o.X;
            outerScale.CenterY = innerLineScale.CenterY = o.Y;
            double d = Math.Max(72, Math.Min(94, rIn * 0.49));
            Center(core, d);
            Center(halo, d);
            coreIcon.Width = coreIcon.Height = d * 0.34;
            var all = Order().ToList();
            var priv = all.Where(s => !s.E.Public).ToList();
            var pub = all.Where(s => s.E.Public).ToList();
            double slots = priv.Count + 1, rp = rIn * 0.64, rq = (rIn + rOut) / 2;
            var taken = new List<double>();
            for (int i = 0; i < priv.Count; i++)
            {
                taken.Add(-125 + i * 360 / slots);
                Move(priv[i], taken[i], rp, animate && (fresh == null || !fresh.Contains(priv[i])));
            }
            taken.Add(-125 + priv.Count * 360 / slots);
            var pp = Polar(taken[priv.Count], rp);
            Go(plusAt, TranslateTransform.XProperty, pp.X, animate);
            Go(plusAt, TranslateTransform.YProperty, pp.Y, animate);
            // 外圈的从右上开始等分；连线要穿过里圈，往两边挪到离里圈每颗都有 30° 以上的空当
            for (int j = 0; j < pub.Count; j++)
            {
                double a = Clear(-25 + j * 360.0 / pub.Count, taken);
                taken.Add(a);
                Move(pub[j], a, rq, animate && (fresh == null || !fresh.Contains(pub[j])));
            }
        }

        static double Clear(double want, List<double> taken)
        {
            for (int d = 0; d <= 180; d += 2)
                foreach (int sign in new[] { 1, -1 })
                {
                    double a = want + sign * d;
                    if (taken.All(t => Math.Abs(((a - t) % 360 + 540) % 360 - 180) >= 30)) return a;
                }
            return want;
        }

        void Move(Sat s, double angle, double r, bool animate)
        {
            var p = Polar(angle, r);
            s.Link.X1 = s.Flow.X1 = o.X;
            s.Link.Y1 = s.Flow.Y1 = o.Y;
            bool same = (p - s.Pos).Length < 0.5;
            s.Angle = angle;
            s.Pos = p;
            if (!(animate && same))
            {
                Go(s.At, TranslateTransform.XProperty, p.X, animate);
                Go(s.At, TranslateTransform.YProperty, p.Y, animate);
                foreach (var l in new[] { s.Link, s.Flow })
                {
                    Go(l, Line.X2Property, p.X, animate);
                    Go(l, Line.Y2Property, p.Y, animate);
                }
            }
            PlaceLabel(s);
        }

        static void Go(DependencyObject target, DependencyProperty prop, double to, bool animate)
        {
            if (animate)
            {
                Ui.AnimTo(target, prop, to, 620, Ui.Spring);
                return;
            }
            ((IAnimatable)target).BeginAnimation(prop, null);
            target.SetValue(prop, to);
        }

        // 名字放在卫星外侧：左半边的靠右对齐放左边，其余放右边；右边离边缘太近就放到正下方
        void PlaceLabel(Sat s)
        {
            bool left = Math.Cos(s.Angle * Math.PI / 180) < -0.2;
            double room = left ? s.Pos.X - 48 : field.ActualWidth - s.Pos.X - 48;
            bool below = room < 120;
            s.Label.MaxWidth = below ? 160 : Math.Max(40, room);
            s.Name.TextAlignment = s.Note.TextAlignment = below ? TextAlignment.Center : left ? TextAlignment.Right : TextAlignment.Left;
            s.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = s.Label.DesiredSize;
            Canvas.SetLeft(s.Label, below ? -size.Width / 2 : left ? -40 - size.Width : 40);
            Canvas.SetTop(s.Label, below ? (Timed(s.E) ? 42 : 34) : -size.Height / 2);
        }

        void Center(FrameworkElement e, double size)
        {
            e.Width = e.Height = size;
            Canvas.SetLeft(e, o.X - size / 2);
            Canvas.SetTop(e, o.Y - size / 2);
        }

        Point Polar(double deg, double r)
        {
            double a = deg * Math.PI / 180;
            return new Point(o.X + r * Math.Cos(a), o.Y + r * Math.Sin(a));
        }

        IEnumerable<Sat> Order()
        {
            return sats.Values.OrderBy(s => s.E.Mine != null ? 1 : 0).ThenBy(s => s.E.Port).ThenBy(s => s.E.Path, StringComparer.Ordinal);
        }

        // 第一次读到：两圈荡开、本机弹出来、卫星一颗颗亮起
        void Intro()
        {
            Ui.AnimFrom(inner, OpacityProperty, 0, 1, 900, 0, MainEase.Settle);
            Ui.ScaleFrom(innerScale, 0.55, 1, 900, 0, MainEase.Settle);
            Ui.AnimFrom(innerLine, OpacityProperty, 0, 1, 900, 0, MainEase.Settle);
            Ui.ScaleFrom(innerLineScale, 0.55, 1, 900, 0, MainEase.Settle);
            // 外圈是三四百个圆点的虚线，缩放时每帧都要重新描一遍：放大的这一秒先当成贴图缩放，放完换回矢量（终态和以前一模一样）
            outer.CacheMode = new BitmapCache(1.0);
            Ui.After(1100, () => outer.CacheMode = null);
            Ui.AnimFrom(outer, OpacityProperty, 0, 1, 900, 110, MainEase.Settle);
            Ui.ScaleFrom(outerScale, 0.55, 1, 900, 110, MainEase.Settle);
            Ui.AnimFrom(innerTag, OpacityProperty, 0, 1, 400, 350);
            Ui.AnimFrom(outerTag, OpacityProperty, 0, 1, 400, 450);
            core.Opacity = 1;
            Ui.ScaleFrom(corePop, 0, 1, 640, 0, Ui.Spring);
            Ui.AnimFrom(panel, OpacityProperty, 0, 1, 360, 150);
            int i = 0;
            foreach (var s in Order())
            {
                int delay = 260 + 80 * i++;
                Ui.ScaleFrom(s.Pop, 0.3, 1, 460, delay, Ui.Spring);
                Ui.AnimFrom(s.View, OpacityProperty, 0, 1, 220, delay);
                Ui.AnimFrom(s.Link, OpacityProperty, 0, 1, 400, delay);
            }
            Ui.ScaleFrom(plusPop, 0.3, 1, 460, 260 + 80 * i, Ui.Spring);
            Ui.AnimFrom(plus, OpacityProperty, 0, 1, 220, 260 + 80 * i);
        }

        // 新建的从本机里弹出去
        void Launch(Sat s)
        {
            Ui.AnimFrom(s.At, TranslateTransform.XProperty, o.X, s.Pos.X, 620, 0, Ui.Spring);
            Ui.AnimFrom(s.At, TranslateTransform.YProperty, o.Y, s.Pos.Y, 620, 0, Ui.Spring);
            foreach (var l in new[] { s.Link, s.Flow })
            {
                Ui.AnimFrom(l, Line.X2Property, o.X, s.Pos.X, 620, 0, Ui.Spring);
                Ui.AnimFrom(l, Line.Y2Property, o.Y, s.Pos.Y, 620, 0, Ui.Spring);
            }
            Ui.ScaleFrom(s.Pop, 0.3, 1, 520, 0, Ui.Spring);
            Ui.AnimFrom(s.View, OpacityProperty, 0, 1, 200);
            Ui.ScaleFrom(corePop, 0.9, 1, 420, 0, Ui.Spring);
        }

        // 停掉的缩回本机里
        void Vanish(Sat s)
        {
            sats.Remove(s.E.Key);
            Flow(s, false);
            Ui.ScaleTo(s.Pop, 0.2, 300, MainEase.Sink);
            Ui.AnimTo(s.View, OpacityProperty, 0, 300, MainEase.Sink);
            Ui.AnimTo(s.At, TranslateTransform.XProperty, o.X, 320, MainEase.Sink);
            Ui.AnimTo(s.At, TranslateTransform.YProperty, o.Y, 320, MainEase.Sink);
            Ui.AnimTo(s.Link, Line.X2Property, o.X, 320, MainEase.Sink);
            Ui.AnimTo(s.Link, Line.Y2Property, o.Y, 320, MainEase.Sink);
            Ui.After(360, () =>
            {
                layer.Children.Remove(s.View);
                lines.Children.Remove(s.Link);
                lines.Children.Remove(s.Flow);
            });
        }

        void PaintPlus()
        {
            bool on = selected == Plus || lifted;
            plusRing.Stroke = on ? Th.Fg : Th.Rim;
            plusIcon.Stroke = on ? Th.Fg : Th.Mute;
            Ui.Tint(plusFill, Theme.Fade(Th.FgColor, on ? 0.08 : 0), 200);
        }

        void Select(string key)
        {
            if (key == selected) return;
            selected = key;
            pending = confirm = failure = null;
            foreach (var s in sats.Values) Paint(s);
            PaintPlus();
            RenderPanel(true);
        }

        // ---------- 拖文件进来 ----------

        public override bool CanTake(List<string> paths, Point p)
        {
            bool ok = loaded && error == null && FileServer.Error == null && !busy && AllExist(paths, x => File.Exists(x) || Directory.Exists(x));
            if (ok != lifted) Lift(ok);
            return ok;
        }

        public override void Take(List<string> paths, Point p)
        {
            Lift(false);
            AddMany(paths);
        }

        public override void DragEnd()
        {
            if (lifted) Lift(false);
        }

        // 拿着文件进来：加号亮起、里圈变亮、本机外面一圈圈荡开
        void Lift(bool on)
        {
            lifted = on;
            PaintPlus();
            PaintDrop();
            Ui.Tint(innerFill, Theme.Fade(Th.FgColor, on ? innerAlpha + 0.045 : innerAlpha), 220);
            Ui.ScaleTo(plusPop, on ? 1.14 : 1, 260, on ? Ui.Spring : Ui.Ease);
            if (on)
            {
                var grow = new DoubleAnimation(1, 1.7, TimeSpan.FromMilliseconds(1200)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ui.Ease };
                Ui.Loop(halo, haloScale, ScaleTransform.ScaleXProperty, grow);
                Ui.Loop(halo, haloScale, ScaleTransform.ScaleYProperty, grow);
                Ui.Loop(halo, halo, OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(1200)) { RepeatBehavior = RepeatBehavior.Forever });
                return;
            }
            Ui.Unloop(halo, OpacityProperty);
            halo.Opacity = 0;
            Ui.Unloop(haloScale, ScaleTransform.ScaleXProperty);
            Ui.Unloop(haloScale, ScaleTransform.ScaleYProperty);
        }

        void PaintDrop()
        {
            if (dropZone == null) return;
            var rect = (Rectangle)dropZone.Children[0];
            rect.Stroke = lifted ? Th.Fg : Th.Rim;
            rect.Fill = lifted ? Th.Wash : Brushes.Transparent;
            dropIcon.Stroke = lifted ? Th.Fg : Th.Mute;
        }

        // ---------- 动作 ----------

        async void AddMany(List<string> paths)
        {
            if (busy) return;
            Busy();
            string err = null, key = null;
            await Task.Run(() =>
            {
                foreach (var p in paths)
                {
                    string k;
                    string r = Shares.Add(p, out k);
                    if (r != null) err = r;
                    else key = k;
                }
            });
            busy = false;
            failure = err;
            if (key != null)
            {
                selected = key;
                pending = confirm = null;
            }
            Load();
        }

        async void PublishShare(ServeEntry e, int ttl)
        {
            if (busy) return;
            Busy();
            string err = await Task.Run(() => Shares.Publish(e, Ttls[ttl]));
            busy = false;
            failure = err;
            if (err == null) pending = null;
            Load();
        }

        async void UnpublishShare(ServeEntry e)
        {
            if (busy) return;
            Busy();
            failure = await Task.Run(() => Shares.Unpublish(e));
            busy = false;
            Load();
        }

        async void StopShare(ServeEntry e)
        {
            if (busy) return;
            Busy();
            failure = await Task.Run(() => Shares.Stop(e));
            busy = false;
            confirm = null;
            Load();
        }

        // 正在改：详情先变淡、不能点，等读回新的状态再换
        void Busy()
        {
            busy = true;
            failure = null;
            RenderPanel(false);
        }

        void PickFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.T("选择要分享的文件") };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddMany(dlg.FileNames.ToList());
        }

        void PickFolder()
        {
            string dir = FolderDialog.Pick(Window.GetWindow(this), L.T("选择要分享的文件夹"));
            if (dir != null) AddMany(new List<string> { dir });
        }

        // ---------- 右边的详情 ----------

        string Sig()
        {
            Sat s;
            string e = sats.TryGetValue(selected, out s) ? s.E.Key + "|" + s.E.Dead + "|" + s.E.Public + "|" + s.E.Program + "|" + (s.E.Mine != null ? s.E.Mine.Source + s.E.Mine.Expires : "") : "";
            return selected + "|" + e + "|" + error + "|" + failure + "|" + busy + "|" + pending + "|" + pendingTtl + "|" + confirm + "|" + FileServer.Error;
        }

        void RenderPanel(bool animate)
        {
            Sat s;
            FrameworkElement content = error != null ? ErrorView() : sats.TryGetValue(selected, out s) ? Detail(s.E) : NewView();
            if (busy)
            {
                content.IsHitTestVisible = false;
                content.Opacity = 0.55;
            }
            panel.Child = content;
            shown = Sig();
            shownKey = selected;
            if (!animate) return;
            var shift = new TranslateTransform();
            content.RenderTransform = shift;
            Ui.AnimFrom(content, OpacityProperty, 0, 1, 220);
            Ui.AnimFrom(shift, TranslateTransform.YProperty, 10, 0, 320, 0, MainEase.Settle);
        }

        FrameworkElement Detail(ServeEntry e)
        {
            bool dead = e.Dead != "";
            var dock = new DockPanel();
            var stop = new Border();
            FillStop(stop, e);
            DockPanel.SetDock(stop, Dock.Bottom);
            dock.Children.Add(stop);
            var top = new StackPanel();
            top.Children.Add(Header(IconOf(e), e.Title, Sub(e), dead ? Th.Bad : Th.Mute, !dead && e.Program != ""));
            top.Children.Add(Gap(UrlRow(e), 22));
            var qr = dead || e.Kind == "tcp" ? null : QrRow(e);
            if (qr != null) top.Children.Add(Gap(qr, 18));
            if (dead) top.Children.Add(Gap(new BrokenChain(Th, e.Mine == null && e.Kind == "proxy" ? "port" : IconOf(e), e.Mine == null && e.LocalPort > 0 ? ":" + e.LocalPort : ""), 26));
            else if (e.Mine != null) top.Children.Add(Gap(Access(e), 22));
            if (e.Mine == null) top.Children.Add(Gap(Facts(e), 18));
            if (failure != null) top.Children.Add(Gap(Fail(failure), 14));
            dock.Children.Add(top);
            return dock;
        }

        FrameworkElement Header(string icon, string title, string sub, Brush subInk, bool running)
        {
            var box = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(13), Background = Th.Chip, Child = new IconView(icon, 20, Th.Fg) };
            var texts = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Kit.Text(title, 17, Th.Fg, true));
            if (sub != "")
            {
                var s = Kit.Text(sub, 13, subInk);
                s.Margin = new Thickness(0, 3, 0, 0);
                texts.Children.Add(s);
            }
            if (!running) return Ui.Columns(1, box, texts);
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Th.Ok, VerticalAlignment = VerticalAlignment.Center, ToolTip = Ui.Tip(Th, L.T("运行中")) };
            return Ui.Columns(1, box, texts, dot);
        }

        // 链接：复制、在浏览器里打开；打不开的划掉
        FrameworkElement UrlRow(ServeEntry e)
        {
            bool dead = e.Dead != "";
            string url = e.Url;
            // 点后面允许折行（零宽空格），长链接不会把“https://”单独甩成一行；复制的还是原样
            var text = new TextBlock { Text = url.Replace(".", ".​"), FontFamily = Ui.MonoFont, FontSize = 12.5, Foreground = dead ? Th.Mute : Th.Fg, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
            if (dead) text.TextDecorations = TextDecorations.Strikethrough;
            var row = new Border { MinHeight = 52, Padding = new Thickness(14, 6, 6, 6), CornerRadius = new CornerRadius(12), Background = Th.Bg };
            if (dead || e.Kind == "tcp")
            {
                row.Child = text;
                return row;
            }
            var copy = Square("copy", L.T("复制链接"), g =>
            {
                if (!Ui.CopyText(url)) return;
                g.Show("check");
                g.Stroke = Th.Ok;
                Ui.After(1200, () => { g.Show("copy"); g.Stroke = Th.Fg; });
            });
            var open = Square("external", L.T("在浏览器中打开"), g => Open(url));
            open.Margin = new Thickness(6, 0, 0, 0);
            row.Child = Ui.Columns(0, text, copy, open);
            return row;
        }

        // 手机扫码打开：二维码一直是浅底深点，深色主题下也扫得出来
        FrameworkElement QrRow(ServeEntry e)
        {
            var m = Qr.Encode(e.Url);
            if (m == null) return null;
            double side = roomy ? 132 : 100;
            var tile = new Border
            {
                Width = side, Height = side, Padding = new Thickness(side * 0.08), CornerRadius = new CornerRadius(14), Background = QrPaper,
                BorderBrush = Th.Hair, BorderThickness = new Thickness(Th.Dark ? 0 : 1), Child = new QrView(m, QrInk)
            };
            var t = new TextBlock
            {
                Text = e.Public ? L.T("使用手机扫码即可打开") : L.T("使用已登录 Tailscale 的手机扫码即可打开"), FontSize = 13.5, LineHeight = 22, Foreground = Th.Mute,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0)
            };
            return Ui.Columns(1, tile, t);
        }

        Border Square(string icon, string tip, Action<IconView> click)
        {
            var glyph = new IconView(icon, 17, Th.Fg);
            var b = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(9), Background = Th.Fill, Cursor = Cursors.Hand, Child = glyph, ToolTip = Ui.Tip(Th, tip), VerticalAlignment = VerticalAlignment.Center };
            b.MouseEnter += delegate { b.Background = Th.Rim; };
            b.MouseLeave += delegate { b.Background = Th.Fill; };
            b.MouseLeftButtonUp += delegate { click(glyph); };
            return b;
        }

        // 谁能打开：我的设备 / 所有人。选“所有人”先挑多久，再按“公开”才真的公开；收回不用确认
        FrameworkElement Access(ServeEntry e)
        {
            var seg = new Segmented(Th, new[] { L.T("我的设备"), L.T("所有人") }, e.Public || pending == e.Key ? 1 : 0, true, true, 38);
            var below = new StackPanel();
            Action fill = null;
            fill = () =>
            {
                below.Children.Clear();
                if (!e.Public && pending != e.Key)
                {
                    below.Children.Add(Note(L.T("仅限已登录你的 Tailscale 账号的设备访问")));
                    return;
                }
                int cur = e.Public ? TtlIndex(e.Mine) : pendingTtl;
                var chips = new UniformGrid { Columns = Ttls.Length, Margin = new Thickness(0, 12, 0, 0) };
                for (int i = 0; i < Ttls.Length; i++)
                {
                    int k = i;
                    chips.Children.Add(Chip(L.T(TtlNames[i]), i == cur, i, () =>
                    {
                        if (e.Public) { if (k != cur) PublishShare(e, k); return; }
                        pendingTtl = k;
                        fill();
                        shown = Sig();
                    }));
                }
                below.Children.Add(chips);
                below.Children.Add(Note(L.T("任何获得链接的人都可以访问，到期后自动关闭。")));
                if (e.Public) return;
                var go = Kit.Button(Th, L.T("公开 {0}", L.T(TtlNames[pendingTtl])), true, () => PublishShare(e, pendingTtl));
                go.Margin = new Thickness(0, 16, 0, 0);
                below.Children.Add(go);
            };
            seg.Picked += i =>
            {
                failure = null;
                if (i == 0 && e.Public) { UnpublishShare(e); return; }
                pending = i == 1 ? e.Key : null;
                pendingTtl = 1;
                fill();
                Ui.AnimFrom(below, OpacityProperty, 0, 1, 220);
                shown = Sig();
            };
            fill();
            var stack = new StackPanel();
            stack.Children.Add(seg);
            stack.Children.Add(below);
            return stack;
        }

        Border Chip(string text, bool on, int index, Action click)
        {
            var t = new TextBlock { Text = text, FontSize = 13.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = on ? Th.Fg : Th.Mute, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var edge = Frozen(Theme.Fade(Th.FgColor, on ? 0.45 : 0.14));
            var b = new Border
            {
                Height = 36, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = edge, Background = on ? Th.Fill : Brushes.Transparent, Cursor = Cursors.Hand, Child = t,
                Margin = new Thickness(index == 0 ? 0 : 4, 0, index == Ttls.Length - 1 ? 0 : 4, 0)
            };
            if (!on)
            {
                var hover = Frozen(Theme.Fade(Th.FgColor, 0.32));
                b.MouseEnter += delegate { b.BorderBrush = hover; t.Foreground = Th.Fg; };
                b.MouseLeave += delegate { b.BorderBrush = edge; t.Foreground = Th.Mute; };
            }
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }

        // 用户自己挂的：只看、能停，不在这里改
        FrameworkElement Facts(ServeEntry e)
        {
            var stack = new StackPanel();
            stack.Children.Add(Kit.InfoRow(Th, L.T("访问权限"), Kit.Text(e.Public ? L.T("所有人") : L.T("我的设备"), 14, Th.Fg)));
            stack.Children.Add(Kit.InfoRow(Th, L.T("端口"), Kit.Text(e.Port.ToString(), 14, Th.Fg)));
            var target = Kit.Mono(e.Target, 12.5, Th.Fg);
            target.MaxWidth = 250;
            stack.Children.Add(Kit.InfoRow(Th, e.Kind == "path" ? L.T("文件") : e.Kind == "text" ? L.T("内容") : L.T("转发到"), target));
            return stack;
        }

        // 停止：TailOrbit 建的直接停；用户自己挂的先问一句
        void FillStop(Border host, ServeEntry e)
        {
            if (e.Mine != null || confirm != e.Key)
            {
                host.Child = Kit.Danger(Th, L.T("停止分享"), () =>
                {
                    if (e.Mine != null) { StopShare(e); return; }
                    confirm = e.Key;
                    FillStop(host, e);
                    shown = Sig();
                });
                return;
            }
            var yes = new Border
            {
                Height = 42, Margin = new Thickness(0, 0, 5, 0), CornerRadius = new CornerRadius(12), Background = Th.Bad, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = L.T("确认停止"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Th.Bg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            yes.MouseEnter += delegate { yes.Opacity = 0.88; };
            yes.MouseLeave += delegate { yes.Opacity = 1; };
            yes.MouseLeftButtonUp += delegate { StopShare(e); };
            var no = Kit.Button(Th, L.T("取消"), false, () =>
            {
                confirm = null;
                FillStop(host, e);
                shown = Sig();
            });
            no.Height = 42;
            no.Margin = new Thickness(5, 0, 0, 0);
            var g = new UniformGrid { Columns = 2 };
            g.Children.Add(yes);
            g.Children.Add(no);
            host.Child = g;
            Ui.AnimFrom(g, OpacityProperty, 0, 1, 180);
        }

        // 新建：一大块虚线框接文件，下面两个按钮挑文件、挑文件夹
        FrameworkElement NewView()
        {
            bool can = FileServer.Error == null;
            var dock = new DockPanel();
            var note = Note(L.T("新建的分享仅限你的设备访问"));
            DockPanel.SetDock(note, Dock.Bottom);
            dock.Children.Add(note);
            var files = Kit.Button(Th, L.T("选择文件"), true, PickFiles);
            files.Margin = new Thickness(0, 0, 5, 0);
            var folder = Kit.Button(Th, L.T("选择文件夹"), false, PickFolder);
            folder.Margin = new Thickness(5, 0, 0, 0);
            var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(files);
            buttons.Children.Add(folder);
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            var head = Header("plus", L.T("新建分享"), L.T("文件或文件夹"), Th.Mute, false);
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);
            string problem = can ? failure : FileServer.Error;
            if (problem != null)
            {
                var w = Gap(Fail(problem), 18);
                DockPanel.SetDock(w, Dock.Top);
                dock.Children.Add(w);
            }
            dropIcon = new IconView("folder-plus", 34, Th.Mute);
            var hint = Kit.Text(L.T("拖放到此处"), 15, Th.Fg);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, 14, 0, 0);
            var inside = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            inside.Children.Add(dropIcon);
            inside.Children.Add(hint);
            var area = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            area.Children.Add(inside);
            area.MouseLeftButtonUp += delegate { PickFiles(); };
            dropZone = Kit.Dashed(Th, area, 16);
            dropZone.Margin = new Thickness(0, 22, 0, 0);
            dock.Children.Add(dropZone);   // 最后一个填满剩下的高度
            if (!can)
            {
                dropZone.IsHitTestVisible = buttons.IsHitTestVisible = false;
                dropZone.Opacity = buttons.Opacity = 0.45;
            }
            PaintDrop();
            return dock;
        }

        FrameworkElement ErrorView()
        {
            var stack = new StackPanel();
            stack.Children.Add(Header("alert", L.T("无法读取分享设置"), L.T("Tailscale 未运行或未登录。"), Th.Mute, false));
            var retry = Kit.Button(Th, L.T("重试"), false, Load);
            retry.Margin = new Thickness(0, 22, 0, 0);
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(retry);
            return stack;
        }

        FrameworkElement Fail(string text)
        {
            return new TextBlock { Text = text, FontSize = 13, LineHeight = 20, Foreground = Th.Bad, TextWrapping = TextWrapping.Wrap };
        }

        TextBlock Note(string text)
        {
            return new TextBlock { Text = text, FontSize = 12.5, LineHeight = 20, Foreground = Th.Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        }

        TextBlock ZoneLabel(string text)
        {
            return new TextBlock { Text = text, FontSize = 12.5, Foreground = Th.Mute, IsHitTestVisible = false, Opacity = 0 };
        }

        // 圈线：从名字右边顺时针绕一圈到名字左边，名字那里留个口
        Geometry GapRing(double r, TextBlock tag)
        {
            tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = tag.DesiredSize;
            Canvas.SetLeft(tag, o.X - size.Width / 2);
            Canvas.SetTop(tag, o.Y - r - size.Height / 2);
            double half = Math.Asin(Math.Min(1, (size.Width / 2 + 8) / r)) * 180 / Math.PI;
            var f = new PathFigure { StartPoint = Polar(-90 + half, r) };
            f.Segments.Add(new ArcSegment(Polar(-90 - half, r), new Size(r, r), 0, true, SweepDirection.Clockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(f);
            g.Freeze();
            return g;
        }

        // ---------- 小工具 ----------

        Color ChipColor { get { return ((SolidColorBrush)Th.Chip).Color; } }

        public static readonly Brush QrPaper = Frozen(Color.FromRgb(0xF4, 0xF5, 0xF6)), QrInk = Frozen(Color.FromRgb(0x1A, 0x1C, 0x1F));   // 设置页的剪贴板二维码也用

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        static FrameworkElement Gap(FrameworkElement e, double top)
        {
            e.Margin = new Thickness(e.Margin.Left, top, e.Margin.Right, e.Margin.Bottom);
            return e;
        }

        static string IconOf(ServeEntry e)
        {
            if (e.Mine != null) return e.Mine.Source != "" ? Icons.ForFiles(new[] { e.Mine.Source }) : "file";
            if (e.Kind == "path") return Icons.ForFiles(new[] { e.Target });
            if (e.Kind == "tcp") return "port";
            return e.Kind == "text" ? "file" : "globe";
        }

        static string Sub(ServeEntry e)
        {
            if (e.Dead != "") return e.Dead;
            if (e.Mine != null) return Size(e.Mine.Source);
            if (e.Kind == "path") return Size(e.Target);
            if (e.Kind == "tcp") return L.T("转发到 {0}", e.Target);
            return e.Kind == "proxy" && e.Program != "" && e.LocalPort > 0 ? L.T("本机端口 {0}", e.LocalPort) : "";
        }

        static string Size(string path)
        {
            if (Directory.Exists(path)) return L.T("文件夹");
            try { return Ui.FormatSize(new FileInfo(path).Length); }
            catch (IOException) { return ""; }
        }

        static string Left(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return L.T("即将关闭");
            if (t.TotalMinutes < 60) return L.T("{0} 分钟后关闭", Math.Ceiling(t.TotalMinutes));
            if (t.TotalHours < 23.5) return L.T("{0} 小时后关闭", Math.Max(1, Math.Round(t.TotalHours)));
            return L.T("1 天后关闭");
        }

        // 公开了多久：离哪个选项最近就算哪个
        static int TtlIndex(Share m)
        {
            if (m == null || m.Expires == null || m.Since == null) return 1;
            double total = (m.Expires.Value - m.Since.Value).TotalMinutes;
            int best = 0;
            for (int i = 1; i < Ttls.Length; i++)
                if (Math.Abs(Ttls[i].TotalMinutes - total) < Math.Abs(Ttls[best].TotalMinutes - total)) best = i;
            return best;
        }

        static void Open(string url)
        {
            Ui.Launch(() => Process.Start(url));
        }
    }

    // 选文件夹：系统新式的“选择文件夹”窗口（IFileDialog + FOS_PICKFOLDERS）
    static class FolderDialog
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialog { }

        // 用不到的方法只占位，顺序不能乱
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes();
            void SetFileTypeIndex();
            void GetFileTypeIndex();
            void Advise();
            void Unadvise();
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder();
            void SetFolder();
            void GetFolder();
            void GetCurrentSelection();
            void SetFileName();
            void GetFileName();
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel();
            void SetFileNameLabel();
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler();
            void GetParent();
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }

        // 取消了返回 null
        public static string Pick(Window owner, string title)
        {
            var dlg = (IFileDialog)new FileOpenDialog();
            try
            {
                uint fos;
                dlg.GetOptions(out fos);
                dlg.SetOptions(fos | 0x20 | 0x40);   // FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                dlg.SetTitle(title);
                if (dlg.Show(owner == null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle) != 0) return null;
                IShellItem item;
                dlg.GetResult(out item);
                string path;
                item.GetDisplayName(0x80058000, out path);   // SIGDN_FILESYSPATH
                Marshal.ReleaseComObject(item);
                return path;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }
    }

    // 打不开的分享：链接 → 本机 → 背后的程序（或文件），最后一段断开。光点一颗颗流过来，撞到断口散掉、断口跳一下。
    // 只在看得见时跑动画（切走、换掉详情就停），不让看不见的东西每帧重画
    class BrokenChain : Canvas
    {
        const double Disc = 40, Period = 1800;
        readonly Grid[] discs = new Grid[3];
        readonly Line good, bad;
        readonly Grid cut;
        readonly ScaleTransform cutPop = new ScaleTransform(1, 1);
        readonly TextBlock tag;
        readonly List<Ellipse> dots = new List<Ellipse>();
        double x0, xCut;
        bool running;

        public BrokenChain(Theme th, string endIcon, string endLabel)
        {
            Height = Disc + (endLabel != "" ? 26 : 4);
            good = new Line { Stroke = new SolidColorBrush(Theme.Fade(th.FgColor, 0.22)), StrokeThickness = 1.5 };
            bad = new Line { Stroke = new SolidColorBrush(Theme.Fade(th.BadColor, 0.6)), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 3 }, StrokeDashCap = PenLineCap.Round };
            Children.Add(good);
            Children.Add(bad);
            // 光点垫在圆盘下面：从本机里穿过去，不压在本机图标上
            for (int i = 0; i < 3; i++)
            {
                var tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(1, 1));
                tg.Children.Add(new TranslateTransform());
                var d = new Ellipse { Width = 5, Height = 5, Fill = th.Fg, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = tg };
                dots.Add(d);
                Children.Add(d);
            }
            string[] icons = { "globe", "monitor", endIcon };
            for (int i = 0; i < 3; i++)
            {
                var g = new Grid { Width = Disc, Height = Disc };
                bool end = i == 2;
                g.Children.Add(new Ellipse { Fill = end ? th.Bg : th.Chip, Stroke = end ? th.Dim : th.Hair, StrokeThickness = end ? 1.5 : 1, StrokeDashArray = end ? new DoubleCollection { 2, 2 } : null });
                g.Children.Add(new IconView(icons[i], 17, end ? th.Mute : th.Fg));
                discs[i] = g;
                Children.Add(g);
            }
            // 断口：一个小红叉，光点撞上来时跳一下
            cut = new Grid { Width = 18, Height = 18, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = cutPop };
            cut.Children.Add(new Ellipse { Fill = th.Bg });
            cut.Children.Add(new IconView("x", 12, th.Bad, 2.6));
            Children.Add(cut);
            tag = new TextBlock { Text = endLabel, FontFamily = Ui.MonoFont, FontSize = 12, Foreground = th.Mute };
            Children.Add(tag);
            // 宽度变了：光点的起点终点跟着换，重新起一遍（看不见时 Ui.Loop 先不跑，看得见了再跑）
            SizeChanged += delegate
            {
                Place();
                Run(false);
                Run(true);
            };
        }

        void Place()
        {
            double w = ActualWidth, cy = Disc / 2;
            double[] xs = { Disc / 2, w / 2, w - Disc / 2 };
            for (int i = 0; i < 3; i++)
            {
                SetLeft(discs[i], xs[i] - Disc / 2);
                SetTop(discs[i], 0);
            }
            good.X1 = xs[0] + Disc / 2 + 6;
            good.X2 = xs[1] - Disc / 2 - 6;
            bad.X1 = xs[1] + Disc / 2 + 6;
            bad.X2 = xs[2] - Disc / 2 - 6;
            good.Y1 = good.Y2 = bad.Y1 = bad.Y2 = cy;
            xCut = (bad.X1 + bad.X2) / 2;
            SetLeft(cut, xCut - 9);
            SetTop(cut, cy - 9);
            tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(tag, xs[2] - tag.DesiredSize.Width / 2);
            SetTop(tag, Disc + 6);
            x0 = good.X1;
            foreach (var d in dots) SetTop(d, cy - 2.5);
        }

        // 三颗光点错开三分之一周期，从链接那头一路走到断口跟前，撞上去胀开、散掉
        void Run(bool on)
        {
            if (on == running) return;
            running = on;
            for (int i = 0; i < dots.Count; i++)
            {
                var d = dots[i];
                var tg = (TransformGroup)d.RenderTransform;
                var scale = (ScaleTransform)tg.Children[0];
                var move = (TranslateTransform)tg.Children[1];
                if (!on)
                {
                    Ui.Unloop(move, TranslateTransform.XProperty);
                    Ui.Unloop(d, OpacityProperty);
                    Ui.Unloop(scale, ScaleTransform.ScaleXProperty);
                    Ui.Unloop(scale, ScaleTransform.ScaleYProperty);
                    d.Opacity = 0;
                    continue;
                }
                var begin = TimeSpan.FromMilliseconds(Period * i / dots.Count);
                var x = Loop(begin);
                x.KeyFrames.Add(new LinearDoubleKeyFrame(x0, KeyTime.FromPercent(0)));
                x.KeyFrames.Add(new LinearDoubleKeyFrame(xCut - 13, KeyTime.FromPercent(0.8)));
                x.KeyFrames.Add(new DiscreteDoubleKeyFrame(xCut - 13, KeyTime.FromPercent(1)));
                Ui.Loop(d, move, TranslateTransform.XProperty, x);
                var o = Loop(begin);
                o.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                o.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.1)));
                o.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.8)));
                o.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.95)));
                o.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                Ui.Loop(d, d, OpacityProperty, o);
                var k = Loop(begin);
                k.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                k.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0.8)));
                k.KeyFrames.Add(new EasingDoubleKeyFrame(2.6, KeyTime.FromPercent(0.95), Ui.Ease));
                k.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(1)));
                Ui.Loop(d, scale, ScaleTransform.ScaleXProperty, k);
                Ui.Loop(d, scale, ScaleTransform.ScaleYProperty, k);
            }
            if (!on)
            {
                Ui.Unloop(cutPop, ScaleTransform.ScaleXProperty);
                Ui.Unloop(cutPop, ScaleTransform.ScaleYProperty);
                return;
            }
            // 每颗光点撞到断口时红叉跳一下：第一颗在 0.8 个周期时到，之后每三分之一周期到一颗
            var p = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(Period / dots.Count), BeginTime = TimeSpan.FromMilliseconds(Period * 0.8), RepeatBehavior = RepeatBehavior.Forever };
            p.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
            p.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromPercent(0.18), Ui.Ease));
            p.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.7), Ui.Ease));
            Ui.Loop(cut, cutPop, ScaleTransform.ScaleXProperty, p);
            Ui.Loop(cut, cutPop, ScaleTransform.ScaleYProperty, p);
        }

        static DoubleAnimationUsingKeyFrames Loop(TimeSpan begin)
        {
            return new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(Period), BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever };
        }
    }
}
