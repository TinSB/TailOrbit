// 星网上的零件：一台设备（Node）、进度环（Arc）、在本机和设备之间流动的光点（FlowLayer）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace TailOrbit
{
    // 一台设备：圆盘（在线是实心圆，离线缩成空心圈）、发送进度环、送达时荡开的涟漪、名字，以及连回本机的线；
    // 鼠标指着、菜单开着时外面套一圈；正往本机发文件时一圈圈往外荡；要掉线提醒的右上角挂个铃铛
    class Node
    {
        public static readonly IEasingFunction Nudge = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut };
        const double OffScale = 62 / 80.0;   // 离线圆盘 62，在线 80

        public Device Dev;
        public string Ring = "";            // direct 内圈 / relay 中圈 / off 外圈
        public double Angle = double.NaN;   // 在圈上的角度：从正上方顺时针，单位度
        public SendJob Job;
        public bool Gone;
        public double LabelWidth;
        public Action<Node> Click, Copy;

        public readonly Canvas Root = new Canvas();
        public readonly Line Link = new Line { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeDashCap = PenLineCap.Round, StrokeThickness = 1.5, Opacity = 0, IsHitTestVisible = false };
        readonly Theme th;
        readonly TranslateTransform at = new TranslateTransform();
        readonly ScaleTransform lift = new ScaleTransform(1, 1);
        readonly Grid disc = new Grid { Width = 80, Height = 80, Background = Brushes.Transparent };
        readonly ScaleTransform size = new ScaleTransform(1, 1), fx = new ScaleTransform(1, 1), pulse = new ScaleTransform(1, 1);
        readonly TranslateTransform shake = new TranslateTransform();
        readonly SolidColorBrush face, rim, ink, nameInk, linkInk;
        readonly IconView os, kind, act, check;
        readonly TextBlock pct, name, ip;
        readonly Run pctNum = new Run("0");
        readonly Arc arc;
        readonly RotateTransform spin = new RotateTransform();
        readonly Ellipse ripple, halo, beacon;
        readonly ScaleTransform rippleScale = new ScaleTransform(1, 1), haloScale = new ScaleTransform(1, 1), beaconScale = new ScaleTransform(1, 1), badgeScale = new ScaleTransform(0, 0);
        readonly SolidColorBrush badgeInk;
        readonly Grid label;
        readonly TranslateTransform labelShift = new TranslateTransform();
        string state = "idle";   // idle 平时 / busy 发送中 / done 送到了 / fail 没送到
        bool placed, lifted, aimed, hovering, known, spinning, waiting, labelUp, marked, receiving, watched;
        int stamp, copyStamp, breath;

        public Node(Theme th, Device d)
        {
            this.th = th;
            Dev = d;
            face = new SolidColorBrush(d.Online ? th.FgColor : Theme.Fade(th.FgColor, 0));
            rim = new SolidColorBrush(d.Online ? Theme.Fade(th.DimColor, 0) : th.DimColor);
            ink = new SolidColorBrush(d.Online ? th.InkColor : th.DimColor);
            nameInk = new SolidColorBrush(d.Online ? th.MuteColor : th.DimColor);
            linkInk = new SolidColorBrush(th.FgColor);
            Link.Stroke = linkInk;

            os = new IconView(Icons.ForOs(d.Os), 30, ink);
            kind = new IconView("file", 26, ink);
            act = new IconView("x", 28, ink);   // 发送中指上去：取消
            check = new IconView("check", 32, ink, 2.4);
            pct = new TextBlock { FontFamily = Ui.DisplayFont, FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            pct.Inlines.Add(pctNum);
            pct.Inlines.Add(new Run("%") { FontSize = 12 });
            Typography.SetNumeralAlignment(pct, FontNumeralAlignment.Tabular);

            disc.Children.Add(new Ellipse { Fill = face, Stroke = rim, StrokeThickness = 1.5 / OffScale });   // 离线时整个圆盘缩小，描边看上去还是 1.5
            foreach (var e in new FrameworkElement[] { os, kind, pct, check, act })
            {
                e.RenderTransformOrigin = new Point(0.5, 0.5);
                e.RenderTransform = new ScaleTransform(1, 1);
                e.Tag = true;
                if (e != os) Swap(e, false, true);
                disc.Children.Add(e);
            }
            var tg = new TransformGroup();
            tg.Children.Add(size);
            tg.Children.Add(fx);
            tg.Children.Add(pulse);
            tg.Children.Add(shake);
            disc.RenderTransformOrigin = new Point(0.5, 0.5);
            disc.RenderTransform = tg;
            disc.Cursor = Cursors.Hand;
            size.ScaleX = size.ScaleY = d.Online ? 1 : OffScale;
            Canvas.SetLeft(disc, -40);
            Canvas.SetTop(disc, -40);
            disc.MouseEnter += delegate { hovering = true; Paint(); HaloStyle(); };
            disc.MouseLeave += delegate { hovering = false; Paint(); HaloStyle(); };
            // 掉线提醒的铃铛：在圆盘里跟着圆盘一起缩放，挂在右上 45° 的边上
            badgeInk = new SolidColorBrush(d.Online ? th.FgColor : th.MuteColor);
            disc.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = th.Bg, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 1, 0), Child = new IconView("bell", 12, badgeInk, 2.2), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = badgeScale, IsHitTestVisible = false
            });
            disc.MouseLeftButtonUp += delegate { if (Click != null) Click(this); };
            WindowChrome.SetIsHitTestVisibleInChrome(disc, true);

            arc = new Arc(th.FgColor) { Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = spin };
            Canvas.SetLeft(arc, -60);
            Canvas.SetTop(arc, -60);
            ripple = new Ellipse { Width = 84, Height = 84, Stroke = th.Ok, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rippleScale };
            Canvas.SetLeft(ripple, -42);
            Canvas.SetTop(ripple, -42);
            halo = new Ellipse { Width = 94, Height = 94, Stroke = th.Fg, StrokeThickness = 1.5, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = haloScale };
            Canvas.SetLeft(halo, -47);
            Canvas.SetTop(halo, -47);
            haloScale.ScaleX = haloScale.ScaleY = d.Online ? 1 : 0.8;
            beacon = new Ellipse { Width = 80, Height = 80, Stroke = th.Fg, StrokeThickness = 1.5, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = beaconScale };
            Canvas.SetLeft(beacon, -40);
            Canvas.SetTop(beacon, -40);

            label = Label(th, nameInk, labelShift, out name, out ip);
            name.Text = d.Name;
            ip.Text = d.Ip;
            label.Children[0].MouseLeftButtonUp += delegate { if (Copy != null) Copy(this); };
            Canvas.SetLeft(label, -140);
            Canvas.SetTop(label, LabelTop());
            Measure();

            Root.Children.Add(ripple);
            Root.Children.Add(beacon);
            Root.Children.Add(halo);
            Root.Children.Add(arc);
            Root.Children.Add(disc);
            Root.Children.Add(label);
            var rg = new TransformGroup();
            rg.Children.Add(lift);
            rg.Children.Add(at);
            Root.RenderTransform = rg;
            LinkStyle();
        }

        // 名字一行、IP 一行，从 20px 高的窗口里只露出一行；点一下复制 IP：名字往上滑走，露出 ✓ 和 IP
        public static Grid Label(Theme th, Brush nameInk, TranslateTransform shift, out TextBlock name, out TextBlock ip)
        {
            name = new TextBlock { FontSize = 14, Foreground = nameInk, Height = 20, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            ip = new TextBlock { FontFamily = Ui.MonoFont, FontSize = 13, Foreground = th.Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            var ipRow = new StackPanel { Orientation = Orientation.Horizontal, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            ipRow.Children.Add(new IconView("check", 13, th.Fg, 2.4) { VerticalAlignment = VerticalAlignment.Center });
            ipRow.Children.Add(ip);
            // 衬一块和底色一样的底：圈线从名字旁经过时在字前断开，不从字上穿过
            var rows = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Background = th.Bg, Cursor = Cursors.Hand, RenderTransform = shift };
            rows.Children.Add(name);
            rows.Children.Add(ipRow);
            WindowChrome.SetIsHitTestVisibleInChrome(rows, true);
            // 两行都排得下（不然会被布局裁掉、滑上来也看不见），只用 Clip 露出上面 20px
            return new Grid { Width = 280, Height = 40, Clip = new RectangleGeometry(new Rect(0, 0, 280, 20)), Children = { rows } };
        }

        // 换图层：要显示的放大淡入，其余缩小淡出
        public static void Swap(FrameworkElement e, bool on, bool instant = false)
        {
            if (Equals(e.Tag, on)) return;
            e.Tag = on;
            var s = (ScaleTransform)e.RenderTransform;
            if (instant)
            {
                e.BeginAnimation(UIElement.OpacityProperty, null);
                s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                e.Opacity = on ? 1 : 0;
                s.ScaleX = s.ScaleY = on ? 1 : 0.5;
                return;
            }
            Ui.AnimTo(e, UIElement.OpacityProperty, on ? 1 : 0, on ? 220 : 150);
            Ui.ScaleTo(s, on ? 1 : 0.5, on ? 320 : 200, on ? Ui.Spring : Ui.Ease);
        }

        public Point Center { get { return new Point(at.X, at.Y); } }
        public bool Busy { get { return Job != null; } }
        public double HitRadius { get { return Dev.Online ? 66 : 50; } }

        // 占地：圆盘加名字（名字在朝外的一侧），排位置时彼此不压
        public Rect Bounds(Point p, bool up)
        {
            double r = Dev.Online ? 56 : 31, l = Dev.Online ? 80 : 62;
            double half = Math.Max(r, LabelWidth / 2 + 4);
            return up ? new Rect(p.X - half, p.Y - l, 2 * half, l + r) : new Rect(p.X - half, p.Y - r, 2 * half, r + l);
        }

        double LabelTop()
        {
            return labelUp ? (Dev.Online ? -80 : -62) : (Dev.Online ? 60 : 42);   // 在线的让开进度环
        }

        void Measure()
        {
            label.Children[0].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            LabelWidth = label.Children[0].DesiredSize.Width;
        }

        public void Update(Device d)
        {
            bool was = Dev.Online;
            Dev = d;
            name.Text = d.Name;
            ip.Text = d.Ip;
            os.Show(Icons.ForOs(d.Os));
            Measure();
            if (was == d.Online) return;
            // 上线、掉线：实心圆和空心圈互相变过去
            bool on = d.Online;
            Ui.Tint(face, on ? th.FgColor : Theme.Fade(th.FgColor, 0), 450);
            Ui.Tint(rim, on ? Theme.Fade(th.DimColor, 0) : th.DimColor, 450);
            Ui.Tint(ink, on ? th.InkColor : th.DimColor, 450);
            Ui.Tint(nameInk, on ? th.MuteColor : th.DimColor, 450);
            Ui.Tint(badgeInk, on ? th.FgColor : th.MuteColor, 450);
            Ui.ScaleTo(size, on ? 1 : OffScale, 450, Ui.Glide);
            Ui.ScaleTo(haloScale, on ? 1 : 0.8, 450, Ui.Glide);
            Ui.AnimTo(label, Canvas.TopProperty, LabelTop(), 450, Ui.Glide);
            SetLift(lifted);
        }

        // 名字放在背向本机的一侧，连线就不会从字上穿过去
        public void SetLabelUp(bool up)
        {
            if (up == labelUp) return;
            labelUp = up;
            Ui.AnimTo(label, Canvas.TopProperty, LabelTop(), placed ? 450 : 0, Ui.Glide);
        }

        public void MoveTo(Point p, Point origin, bool animate)
        {
            Link.X1 = origin.X;
            Link.Y1 = origin.Y;
            if (animate && placed)
            {
                Ui.AnimTo(at, TranslateTransform.XProperty, p.X, 700, Ui.Glide);
                Ui.AnimTo(at, TranslateTransform.YProperty, p.Y, 700, Ui.Glide);
                Ui.AnimTo(Link, Line.X2Property, p.X, 700, Ui.Glide);
                Ui.AnimTo(Link, Line.Y2Property, p.Y, 700, Ui.Glide);
                return;
            }
            placed = true;
            at.BeginAnimation(TranslateTransform.XProperty, null);
            at.BeginAnimation(TranslateTransform.YProperty, null);
            Link.BeginAnimation(Line.X2Property, null);
            Link.BeginAnimation(Line.Y2Property, null);
            at.X = Link.X2 = p.X;
            at.Y = Link.Y2 = p.Y;
        }

        // 出场：在线的先从本机长出一条线，设备随后弹出来；离线的慢慢浮现
        public void Appear(int order, Point origin, bool intro)
        {
            if (Dev.Online)
            {
                int dl = intro ? 280 + order * 150 : 0, dp = intro ? 640 + order * 150 : 220;
                var c = Center;
                Ui.AnimFrom(Link, Line.X2Property, origin.X, c.X, 520, dl, Ui.Glide);
                Ui.AnimFrom(Link, Line.Y2Property, origin.Y, c.Y, 520, dl, Ui.Glide);
                Ui.AnimFrom(Root, UIElement.OpacityProperty, 0, 1, 160, dp);
                Ui.ScaleFrom(pulse, 0, 1, 640, dp, Ui.Spring);
            }
            else
            {
                int d = intro ? 980 + order * 120 : 0;
                Ui.AnimFrom(Root, UIElement.OpacityProperty, 0, lifted ? 0.3 : 1, 700, d);
                Ui.ScaleFrom(pulse, 0.6, 1, 700, d);
            }
        }

        public void Vanish(Action done)
        {
            Gone = true;
            Ui.AnimTo(Root, UIElement.OpacityProperty, 0, 300);
            Ui.ScaleTo(pulse, 0.6, 300);
            LinkStyle();
            Ui.After(320, done);
        }

        // 拖着文件进了窗口：在线的跟着呼吸等你放，离线的退到一边
        public void SetLift(bool on)
        {
            lifted = on;
            if (!on) aimed = false;
            bool fade = on && !Dev.Online;
            Ui.AnimTo(Root, UIElement.OpacityProperty, fade ? 0.3 : 1, 300);
            Ui.ScaleTo(lift, fade ? 0.86 : 1, 350, Ui.Spring);
            Breathe();
            LinkStyle();
            HaloStyle();
        }

        public void SetAim(bool on)
        {
            if (aimed == on) return;
            aimed = on;
            Breathe();
            LinkStyle();
        }

        void Breathe()
        {
            int k = ++breath;
            if (aimed) { Ui.ScaleTo(fx, 1.2, 400, Ui.Spring); return; }
            bool wanted = lifted && Dev.Online && !Busy;
            bool rest = Math.Abs(fx.ScaleX - 1) < 0.01;
            Ui.ScaleTo(fx, 1, rest ? 1 : 250);
            if (!wanted) return;
            Ui.After(rest ? 1 : 250, () =>
            {
                if (k != breath) return;
                var a = new DoubleAnimation(1, 1.07, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                fx.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                fx.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            });
        }

        // 不能往这儿放：摇头
        public void Refuse()
        {
            Ui.Shake(shake);
        }

        // 菜单开着：外面那圈亮起来；名字先隐去，给按钮让地方（菜单旁的牌子会说清楚）
        public void Mark(bool on)
        {
            if (on == marked) return;
            marked = on;
            if (on) Ui.ScaleFrom(haloScale, Dev.Online ? 0.86 : 0.7, Dev.Online ? 1 : 0.8, 380, 0, Ui.Spring);
            Ui.AnimTo(label, UIElement.OpacityProperty, on ? 0 : 1, on ? 120 : 250);
            HaloStyle();
        }

        void HaloStyle()
        {
            double op = marked ? 0.5 : hovering && !Busy && !lifted ? 0.16 : 0;
            Ui.AnimTo(halo, UIElement.OpacityProperty, op, marked ? 200 : 150);
        }

        // 正往本机发文件：一圈圈往外荡，连线亮一些
        public void Receiving(bool on)
        {
            if (on == receiving) return;
            receiving = on;
            if (on)
            {
                var grow = new DoubleAnimation(1, 1.55, TimeSpan.FromMilliseconds(1800)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = MainEase.Settle };
                beaconScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                beaconScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                beacon.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(1800)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = MainEase.Settle });
            }
            else
            {
                // 淡出后把一直在跑的那组放大动画也停掉，不然空转着让界面每帧都重画
                Ui.AnimTo(beacon, UIElement.OpacityProperty, 0, 300);
                Ui.After(320, () =>
                {
                    if (receiving) return;
                    beaconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    beaconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                });
            }
            LinkStyle();
        }

        public void SetWatched(bool on, bool animate)
        {
            if (on == watched) return;
            watched = on;
            if (animate) Ui.ScaleTo(badgeScale, on ? 1 : 0, on ? 420 : 200, on ? Ui.Spring : Ui.Ease);
            else badgeScale.ScaleX = badgeScale.ScaleY = on ? 1 : 0;
        }

        public void Copied()
        {
            int k = ++copyStamp;
            Ui.AnimTo(labelShift, TranslateTransform.YProperty, -20, 350, Nudge);
            Ui.After(1500, () => { if (k == copyStamp) Ui.AnimTo(labelShift, TranslateTransform.YProperty, 0, 350, Nudge); });
        }

        // 连线：平时淡淡一条；拖着文件时变成往外流的虚线；对准时实线加粗；发送中半亮；没送到变红
        void LinkStyle()
        {
            double op = 0.16, w = 1.5;
            bool dash = false;
            Color c = th.FgColor;
            if (!Dev.Online || Gone) op = 0;
            else if (state == "fail") { op = 1; w = 2; c = th.BadColor; }
            else if (Busy) { op = 0.3; w = 2; }
            else if (receiving) { op = 0.38; w = 1.75; }
            else if (aimed) { op = 1; w = 2.5; }
            else if (lifted) { op = 0.4; dash = true; }
            Ui.AnimTo(Link, UIElement.OpacityProperty, op, 250);
            Ui.AnimTo(Link, Shape.StrokeThicknessProperty, w, 250);
            Ui.Tint(linkInk, c, 250);
            if (dash && Link.StrokeDashArray.Count == 0)
            {
                Link.StrokeDashArray = new DoubleCollection { 2, 14 / 3.0 };   // 线宽 1.5 时是 3px 实、7px 空
                Link.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -20 / 3.0, TimeSpan.FromMilliseconds(700)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else if (!dash && Link.StrokeDashArray.Count > 0)
            {
                Link.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                Link.StrokeDashArray = new DoubleCollection();
            }
        }

        // 圆盘里显示什么：送到了是 ✓；发送中鼠标放上去是 ✕（点了取消），不然是百分比，不知道进度时是文件类型；平时是设备类型
        void Paint()
        {
            string show = state == "done" ? "check" : hovering && Busy ? "act" : state == "busy" ? (known ? "pct" : "kind") : "os";
            Swap(os, show == "os");
            Swap(kind, show == "kind");
            Swap(pct, show == "pct");
            Swap(check, show == "check");
            Swap(act, show == "act");
        }

        public void Start(SendJob j)
        {
            Job = j;
            stamp++;
            state = "busy";
            known = false;
            kind.Show(Icons.ForFiles(j.Paths));
            // 一缩一弹：接住了
            var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(460) };
            k.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0)));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(0.88, KeyTime.FromPercent(0.3), Ui.Ease));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromPercent(0.65), Ui.Ease));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), Ui.Ease));
            pulse.BeginAnimation(ScaleTransform.ScaleXProperty, k);
            pulse.BeginAnimation(ScaleTransform.ScaleYProperty, k);
            arc.Brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            arc.Brush.Color = th.FgColor;
            arc.BeginAnimation(Arc.ValueProperty, null);
            arc.Value = 0;
            Ui.AnimTo(arc, UIElement.OpacityProperty, 1, 200);
            Spin(true);
            Breathe();
            Paint();
            LinkStyle();
            HaloStyle();
        }

        // f：0～1 是进度；SendJob.Unknown 不知道进度（打包中）；SendJob.Keep 对方没回应、还在重试
        public void Progress(double f)
        {
            Wait(f == SendJob.Keep);
            if (f == SendJob.Keep) return;
            known = f >= 0;
            Spin(!known);
            if (known)
            {
                pctNum.Text = ((int)(f * 100)).ToString();
                arc.BeginAnimation(Arc.ValueProperty, new DoubleAnimation(Math.Max(f, 0.004), TimeSpan.FromMilliseconds(240)));
            }
            Paint();
        }

        // 不知道进度：一小段弧绕着转
        void Spin(bool on)
        {
            if (on == spinning) return;
            spinning = on;
            arc.BeginAnimation(Arc.ValueProperty, null);
            arc.Value = on ? 0.22 : 0;
            if (on) spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
            else spin.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        // 对方没回应、还在重试：进度环一明一暗地等
        void Wait(bool on)
        {
            if (on == waiting) return;
            waiting = on;
            if (on) arc.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            else Ui.AnimTo(arc, UIElement.OpacityProperty, 1, 200);
        }

        // result：null 送到了，"" 取消了，其他是失败原因
        public void Finish(string result)
        {
            Job = null;
            Wait(false);
            Spin(false);
            int k = ++stamp;
            if (result == null)
            {
                state = "done";
                arc.BeginAnimation(Arc.ValueProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
                Ui.Tint(arc.Brush, th.OkColor, 300);
                Ui.Tint(face, th.OkColor, 300);
                Ui.AnimFrom(ripple, UIElement.OpacityProperty, 0.9, 0, 1000, 0, MainEase.Settle);
                Ui.ScaleFrom(rippleScale, 1, 2.6, 1000, 0, MainEase.Settle);
            }
            else if (result != "")
            {
                state = "fail";
                Ui.Tint(arc.Brush, th.BadColor, 300);   // 停在断掉的地方变红；不知道进度时没有弧，只剩红线和摇头
                Ui.Shake(shake);
            }
            Breathe();
            Paint();
            LinkStyle();
            if (result == "") Reset();
            else Ui.After(result == null ? 1500 : 1800, () => { if (k == stamp) Reset(); });
        }

        void Reset()
        {
            state = "idle";
            int k = stamp;
            Ui.Tint(face, Dev.Online ? th.FgColor : Theme.Fade(th.FgColor, 0), 300);
            Ui.AnimTo(arc, UIElement.OpacityProperty, 0, 300);
            Ui.After(320, () =>
            {
                if (k != stamp) return;
                arc.BeginAnimation(Arc.ValueProperty, null);
                arc.Value = 0;
                arc.Brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                arc.Brush.Color = th.FgColor;
            });
            Paint();
            LinkStyle();
            HaloStyle();
        }
    }

    static class MainEase
    {
        public static readonly IEasingFunction Settle = new QuarticEase { EasingMode = EasingMode.EaseOut };   // 快出慢停
        public static readonly IEasingFunction Sink = new CubicEase { EasingMode = EasingMode.EaseIn };       // 被吸进去
    }

    // 进度环：从正上方顺时针画到 Value（0～1）；发送进度和公开分享的倒计时都用它
    class Arc : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(double), typeof(Arc), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public readonly SolidColorBrush Brush;
        readonly Pen pen;
        readonly double R;

        public Arc(Color c, double radius = 54, double thickness = 3)
        {
            Brush = new SolidColorBrush(c);
            pen = new Pen(Brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            R = radius;
            Width = Height = 2 * radius + 12;
            IsHitTestVisible = false;
        }

        public double Value
        {
            get { return (double)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double v = Math.Min(1, Value);
            if (v <= 0.001) return;
            var c = new Point(Width / 2, Height / 2);
            if (v >= 0.999) { dc.DrawEllipse(null, pen, c, R, R); return; }
            double a = v * 2 * Math.PI;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - R), false, false);
                ctx.ArcTo(new Point(c.X + R * Math.Sin(a), c.Y - R * Math.Cos(a)), new Size(R, R), 0, v > 0.5, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    // 发送时从本机流向设备的光点（inward 时反过来：收件时从设备流进本机）；只在有传输时跟着屏幕刷新跑
    class FlowLayer : FrameworkElement
    {
        class Stream
        {
            public Node To;
            public bool On = true;
            public double Last = double.NegativeInfinity;              // 上次放出光点的时刻（毫秒）
            public readonly List<double> Parts = new List<double>();   // 每个光点走到哪儿：0 在出发的圆心，1 在到达的圆心
        }

        readonly List<Stream> streams = new List<Stream>();
        readonly Brush dot;
        readonly Func<Point> origin;
        readonly bool inward;
        readonly double r0, r1;   // 从出发的圆盘边上出来，到到达的圆盘边上消失（本机圆盘大些）
        bool hooked;
        double prev = -1;

        public FlowLayer(Brush dot, Func<Point> origin, bool inward = false)
        {
            this.dot = dot;
            this.origin = origin;
            this.inward = inward;
            r0 = inward ? 46 : 52;
            r1 = inward ? 52 : 46;
            IsHitTestVisible = false;
            IsVisibleChanged += delegate { Hook(); };
        }

        Stream Find(Node n)
        {
            return streams.FirstOrDefault(s => s.To == n);
        }

        public void Start(Node n)
        {
            var s = Find(n);
            if (s == null) streams.Add(s = new Stream { To = n });
            s.On = true;
            Hook();
        }

        // 对方没回应在重试时先不放新的；已经在路上的走完
        public void Hold(Node n, bool hold)
        {
            var s = Find(n);
            if (s != null) s.On = !hold;
        }

        // 送到了：路上的走完再停；失败或取消：立刻收掉
        public void Stop(Node n, bool clear)
        {
            var s = Find(n);
            if (s == null) return;
            s.On = false;
            if (clear) streams.Remove(s);
            InvalidateVisual();
        }

        void Hook()
        {
            bool want = streams.Count > 0 && IsVisible;
            if (want == hooked) return;
            hooked = want;
            prev = -1;
            if (want) CompositionTarget.Rendering += OnFrame;
            else CompositionTarget.Rendering -= OnFrame;
        }

        void OnFrame(object sender, EventArgs e)
        {
            double now = ((RenderingEventArgs)e).RenderingTime.TotalMilliseconds;
            if (now == prev) return;   // 同一帧会通知不止一次
            double dt = prev < 0 ? 16 : Math.Min(48, now - prev);
            prev = now;
            var o = origin();
            for (int i = streams.Count - 1; i >= 0; i--)
            {
                var s = streams[i];
                double len = Math.Max(100, (s.To.Center - o).Length);
                if (s.On && now - s.Last >= 80)
                {
                    s.Last = now;
                    s.Parts.Add(r0 / len);
                }
                for (int k = s.Parts.Count - 1; k >= 0; k--)
                {
                    s.Parts[k] += dt * 0.22 / len;   // 每毫秒 0.22px
                    if (s.Parts[k] >= 1 - r1 / len) s.Parts.RemoveAt(k);
                }
                if (!s.On && s.Parts.Count == 0) streams.RemoveAt(i);
            }
            InvalidateVisual();
            Hook();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var o = origin();
            foreach (var s in streams)
            {
                Point from = inward ? s.To.Center : o, to = inward ? o : s.To.Center;
                var d = to - from;
                double len = Math.Max(100, d.Length), u0 = r0 / len, u1 = 1 - r1 / len;
                foreach (double u in s.Parts)
                {
                    double a = Math.Min(1, Math.Min((u - u0) * 10, (u1 - u) * 10));   // 两头淡入淡出
                    if (a <= 0) continue;
                    dc.PushOpacity(a);
                    dc.DrawEllipse(dot, null, from + d * u, 2.3, 2.3);
                    dc.Pop();
                }
            }
        }
    }
}
