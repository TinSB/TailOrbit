// 右键托盘图标弹出的菜单：和主界面的弹出列表一个样子（圆角卡片、悬停淡底、同一套图标），放常用的几件事——
// 打开主界面、发文件、看收到的文件、开终端（指着它列出能连的设备）、两个剪贴板开关、退出。
// 菜单和系统菜单一样不抢前台，托盘“隐藏图标”的弹窗不会因为它先收起；点到菜单外面、按 Esc 或 Win 键就收起，
// 靠的是菜单开着时才装上的全局鼠标、键盘钩子，收起就卸掉
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace TailOrbit
{
    class TrayMenu : Window
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

        const double Pad = 20;    // 卡片四周给阴影留的边
        const double Inset = 7;   // 卡片内边距 6 加描边 1：子菜单第一行和“终端”那行对齐用
        const double Gap = 4;     // 子菜单和菜单之间
        readonly Host host;
        readonly Theme th;
        readonly Border card, termRow, subCard;
        readonly StackPanel list = new StackPanel(), subList = new StackPanel();
        readonly ScaleTransform cardScale = new ScaleTransform(1, 1);
        readonly TranslateTransform subShift = new TranslateTransform();
        readonly Image appIcon = new Image { Width = 18, Height = 18 };
        Popup sub;
        Rect area;     // 鼠标所在屏幕的工作区（不含任务栏），和窗口位置一样按与 DPI 无关的单位
        bool closing, subOnRight;
        int hover;     // 子菜单的延时开、关：只认最后一次
        List<KeyValuePair<Device, string>> terms;   // 能开终端的在线设备和它的 ssh 目标；null 表示还在读
        IntPtr mouseHook, keyHook;
        HookProc mouseProc, keyProc;   // 系统拿着钩子回调：这边也得一直拿着，被回收后系统再调过来就崩

        public TrayMenu(Host host)
        {
            this.host = host;
            th = host.Theme;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            UseLayoutRounding = true;
            FontFamily = Ui.TextFont;

            RenderOptions.SetBitmapScalingMode(appIcon, BitmapScalingMode.HighQuality);
            var open = Item(appIcon, null, L.T("打开主界面"), null, true);
            Act(open, host.OpenFromTray);
            var send = Glyph("send");
            var sendRow = Item(send, send, L.T("发文件"), host.HotkeyOk ? Keys() : null, false);
            Act(sendRow, host.SendFromTray);
            var inbox = Glyph("inbox");
            var got = Item(inbox, inbox, L.T("收到的文件"), null, false);
            Act(got, OpenReceived);
            var term = Glyph("terminal");
            termRow = Item(term, term, L.T("终端"), new IconView("chevron-down", 12, th.Mute) { LayoutTransform = new RotateTransform(-90) }, false);
            termRow.MouseLeftButtonUp += delegate { hover++; OpenSub(); };
            var exit = Glyph("exit");
            var quit = Item(exit, exit, L.T("退出"), null, false);
            Act(quit, host.Quit);
            var rows = new[] { open, sendRow, got, termRow, ClipSwitch(), SyncSwitch(), quit };
            // 指到“终端”稍等一下就弹出子菜单；指到别的行，子菜单稍等一下再关（斜着移过去的路上会擦过别的行）
            foreach (var r in rows)
            {
                var row = r;
                row.MouseEnter += delegate
                {
                    if (row == termRow) Later(150, OpenSub);
                    else if (sub != null) Later(260, CloseSub);
                    else hover++;
                };
            }
            foreach (var e in new FrameworkElement[] { rows[0], Line(), rows[1], rows[2], rows[3], Line(), rows[4], rows[5], Line(), rows[6] }) list.Children.Add(e);
            card = Card(list);
            card.MinWidth = 236;
            card.MaxWidth = 380;
            card.RenderTransform = cardScale;
            Content = card;
            subCard = Card(subList);
            subCard.MinWidth = 180;
            subCard.RenderTransform = subShift;
            subCard.MouseEnter += delegate { hover++; };   // 移进了子菜单：别再按“指到了别的行”把它关掉
        }

        // 在鼠标那里弹出：卡片的一角对着鼠标，右边、下边放不下就往左、往上开（托盘在屏幕底部时总是往上）
        public void Open()
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x80);   // GWL_EXSTYLE：WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            var dpi = VisualTreeHelper.GetDpi(this);
            appIcon.Source = Ui.IconFrame(18 * dpi.DpiScaleX);
            var cur = WinForms.Cursor.Position;
            var wa = WinForms.Screen.FromPoint(cur).WorkingArea;
            area = new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = card.DesiredSize.Width - 2 * Pad, h = card.DesiredSize.Height - 2 * Pad;
            double cx = cur.X / dpi.DpiScaleX, cy = cur.Y / dpi.DpiScaleY;
            bool left = cx + w > area.Right, up = cy + h > area.Bottom;
            double x = Math.Max(area.Left, Math.Min(area.Right - w, left ? cx - w : cx));
            double y = Math.Max(area.Top, Math.Min(area.Bottom - h, up ? cy - h : cy));
            Width = w + 2 * Pad;
            Height = h + 2 * Pad;
            Left = x - Pad;
            Top = y - Pad;
            card.RenderTransformOrigin = new Point(left ? 1 : 0, up ? 1 : 0);
            Show();
            mouseProc = OnMouse;
            keyProc = OnKey;
            IntPtr module = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(14, mouseProc, module, 0);   // WH_MOUSE_LL
            keyHook = SetWindowsHookEx(13, keyProc, module, 0);       // WH_KEYBOARD_LL

            // 从鼠标那一角张开，一行行冒出来
            Ui.AnimFrom(card, OpacityProperty, 0, 1, 120);
            Ui.ScaleFrom(cardScale, 0.94, 1, 260, 0, MainEase.Settle);
            int n = list.Children.Count;
            for (int i = 0; i < n; i++)
            {
                var e = (FrameworkElement)list.Children[up ? n - 1 - i : i];
                Ui.AnimFrom(e, OpacityProperty, 0, 1, 160, 20 + 14 * i);
                Ui.AnimFrom(e.RenderTransform, TranslateTransform.YProperty, up ? 6 : -6, 0, 280, 20 + 14 * i, MainEase.Settle);
            }
            LoadTerms();
        }

        public void Dismiss()
        {
            if (closing) return;
            closing = true;
            Unhook();
            CloseSub();
            IsHitTestVisible = false;
            Ui.AnimTo(card, OpacityProperty, 0, 100);
            Ui.ScaleTo(cardScale, 0.97, 100);
            Ui.After(110, Close);
        }

        protected override void OnClosed(EventArgs e)
        {
            Unhook();
            CloseSub();
            base.OnClosed(e);
        }

        // ---------- 一行一行 ----------

        IconView Glyph(string name)
        {
            return new IconView(name, 17, th.Mute);
        }

        // 一行：左边图标、中间的字、右边可有可无的键帽 / 箭头 / 开关；指着时淡底、图标变深
        Border Item(FrameworkElement icon, IconView glyph, string text, FrameworkElement tail, bool bold)
        {
            icon.Margin = new Thickness(0, 0, 12, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            var label = new TextBlock { Text = text, FontSize = 14, Foreground = th.Fg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var cells = new List<UIElement> { icon, label };
            if (tail != null)
            {
                tail.Margin = new Thickness(18, 0, 0, 0);
                tail.VerticalAlignment = VerticalAlignment.Center;
                cells.Add(tail);
            }
            var row = new Border { Height = 36, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Child = Ui.Columns(1, cells.ToArray()), RenderTransform = new TranslateTransform(), Tag = glyph };
            row.MouseEnter += delegate { Paint(row, true); };
            row.MouseLeave += delegate { if (row != termRow || sub == null) Paint(row, false); };   // 子菜单开着时“终端”那行一直亮着
            return row;
        }

        void Paint(Border row, bool on)
        {
            row.Background = on ? th.Hair : Brushes.Transparent;
            var g = row.Tag as IconView;
            if (g != null) g.Stroke = on ? th.Fg : th.Mute;
        }

        // 点了先收起菜单再做
        void Act(Border row, Action a)
        {
            row.MouseLeftButtonUp += delegate { Dismiss(); a(); };
        }

        Border Line()
        {
            return new Border { Height = 1, Background = th.Hair, Margin = new Thickness(8, 4, 8, 4), RenderTransform = new TranslateTransform() };
        }

        Border Card(UIElement child)
        {
            var c = new Border
            {
                Margin = new Thickness(Pad), Padding = new Thickness(6), CornerRadius = new CornerRadius(14), Background = th.Pop, BorderBrush = th.Hair, BorderThickness = new Thickness(1), Child = child,
                Effect = new DropShadowEffect { Color = Colors.Black, Direction = 270, ShadowDepth = 10, BlurRadius = 28, Opacity = th.Dark ? 0.35 : 0.16 }
            };
            TextElement.SetFontFamily(c, Ui.TextFont);
            return c;
        }

        // 快捷面板的热键：和主界面右下角一样的键帽，小一号
        FrameworkElement Keys()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var k in new[] { "Ctrl", "Alt", "A" })
                row.Children.Add(new Border
                {
                    Height = 20, MinWidth = 20, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(row.Children.Count == 0 ? 0 : 3, 0, 0, 0), CornerRadius = new CornerRadius(5), BorderBrush = th.Rim, BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = k, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = th.Mute, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                });
            return row;
        }

        // 开关：先拨过去，后台真去开、关；没成就拨回来、摇一下，托盘弹一句原因。成了告诉本机页那一行跟着变
        Border Switch(FrameworkElement icon, IconView glyph, string text, bool on, Func<bool, string> turn)
        {
            var t = new Toggle(th, on) { LayoutTransform = new ScaleTransform(0.78, 0.78) };
            var row = Item(icon, glyph, text, t, false);
            bool busy = false;
            row.MouseLeftButtonUp += async delegate
            {
                if (busy) return;
                busy = true;
                bool want = !t.On;
                t.Set(want, true);
                string why = await Task.Run(() => turn(want));
                busy = false;
                if (why == null) { host.RaiseToggled(); return; }
                t.Set(!want, true);
                Ui.Shake((TranslateTransform)row.RenderTransform);
                host.Notify(L.T("没改成"), why);
            };
            return row;
        }

        Border ClipSwitch()
        {
            var g = Glyph("clipboard");
            return Switch(g, g, L.T("剪贴板互通"), Settings.ClipOn, ClipShare.Turn);
        }

        // 和 Mac 共用剪贴板：连着 Mac 时图标右上角一个绿点
        Border SyncSwitch()
        {
            var g = Glyph("laptop");
            var dot = new Ellipse { Width = 8, Height = 8, Fill = th.Ok, Stroke = th.Pop, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0) };
            Action show = () => dot.Visibility = Settings.SyncMac && ClipSync.Peer != null ? Visibility.Visible : Visibility.Collapsed;
            show();
            ClipSync.Changed += show;
            Closed += delegate { ClipSync.Changed -= show; };
            return Switch(new Grid { Children = { g, dot } }, g, L.T("和 Mac 共用剪贴板"), Settings.SyncMac, ClipSync.Turn);
        }

        // 收到的文件：在资源管理器里选中最近收到的那个；它被挪走、改了名就打开“下载”
        void OpenReceived()
        {
            var last = History.Items.LastOrDefault(x => x.In);
            string p = last == null ? null : last.Paths.FirstOrDefault(x => File.Exists(x) || Directory.Exists(x));
            Ui.Launch(() => Process.Start("explorer.exe", p != null ? "/select,\"" + p + "\"" : "shell:Downloads"));
        }

        // ---------- 终端的子菜单 ----------

        // 能开终端的在线设备：菜单一弹出就在后台读，指到“终端”时多半已经读好了
        void LoadTerms()
        {
            Task.Run(() => Tailscale.Load().Devices.Where(d => d.Online).Select(d => new KeyValuePair<Device, string>(d, Ssh.Target(d))).Where(x => x.Value != null).ToList())
                .ContinueWith(t =>
                {
                    terms = t.IsFaulted ? new List<KeyValuePair<Device, string>>() : t.Result;   // 找不到 Tailscale：当成没有
                    if (sub != null) FillSub();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void OpenSub()
        {
            if (closing || sub != null) return;
            sub = new Popup { PlacementTarget = termRow, Placement = PlacementMode.Relative, AllowsTransparency = true, Child = subCard };
            FillSub();
            sub.IsOpen = true;
            Paint(termRow, true);
            Ui.AnimFrom(subCard, OpacityProperty, 0, 1, 120);
            Ui.AnimFrom(subShift, TranslateTransform.XProperty, subOnRight ? -6 : 6, 0, 240, 0, MainEase.Settle);
        }

        void CloseSub()
        {
            if (sub == null) return;
            sub.IsOpen = false;
            sub.Child = null;   // 卡片从这个弹窗上拿下来，下次放进新的
            sub = null;
            Paint(termRow, termRow.IsMouseOver && !closing);
        }

        void FillSub()
        {
            subList.Children.Clear();
            if (terms == null || terms.Count == 0)
                subList.Children.Add(new TextBlock { Text = terms == null ? L.T("读取设备中…") : L.T("没有能开终端的设备"), FontSize = 13.5, Foreground = th.Mute, Margin = new Thickness(10, 9, 10, 9) });
            else
                foreach (var x in terms)
                {
                    string target = x.Value;
                    var g = new IconView(Icons.ForOs(x.Key.Os), 16, th.Mute);
                    var row = Item(g, g, x.Key.Name, null, false);
                    Act(row, () => Ssh.Open(target));
                    subList.Children.Add(row);
                }
            PlaceSub();
        }

        // 放在菜单右边，放不下就放左边；第一行和“终端”那行对齐，下面放不下就往上挪
        void PlaceSub()
        {
            subCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = subCard.DesiredSize.Width - 2 * Pad, h = subCard.DesiredSize.Height - 2 * Pad;
            var at = termRow.TranslatePoint(new Point(0, 0), this);
            double rowX = Left + at.X, rowY = Top + at.Y;
            double cardLeft = Left + Pad, cardRight = Left + Width - Pad;
            subOnRight = cardRight + Gap + w <= area.Right;
            double x = subOnRight ? cardRight + Gap : cardLeft - Gap - w;
            double y = Math.Max(area.Top, Math.Min(area.Bottom - h, rowY - Inset));
            // 弹出层对齐的是卡片本身（不算外边距）；Windows 设成“惯用右手”时它拿卡片右边去对齐（见 Ui.RightUnder）
            sub.HorizontalOffset = (SystemParameters.MenuDropAlignment ? x + w : x) - rowX;
            sub.VerticalOffset = y - rowY;
        }

        void Later(int ms, Action a)
        {
            int k = ++hover;
            Ui.After(ms, () => { if (k == hover && !closing) a(); });
        }

        // ---------- 点到外面、按 Esc 收起 ----------

        // 鼠标在菜单、子菜单以外的地方按下：收起（这一下照常落到底下的窗口上）
        IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            int m = wParam.ToInt32();
            if (code >= 0 && (m == 0x201 || m == 0x204 || m == 0x207 || m == 0x20B))   // 左、右、中、侧键按下
            {
                var p = new Point(Marshal.ReadInt32(lParam), Marshal.ReadInt32(lParam, 4));
                Dispatcher.BeginInvoke(new Action(() => { if (!Over(card, p) && !(sub != null && Over(subCard, p))) Dismiss(); }));
            }
            return CallNextHookEx(mouseHook, code, wParam, lParam);
        }

        // Esc、Win 键：收起（按键照常传下去）
        IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && wParam.ToInt32() == 0x100)   // WM_KEYDOWN
            {
                int vk = Marshal.ReadInt32(lParam);
                if (vk == 0x1B || vk == 0x5B || vk == 0x5C) Dispatcher.BeginInvoke(new Action(Dismiss));
            }
            return CallNextHookEx(keyHook, code, wParam, lParam);
        }

        static bool Over(FrameworkElement e, Point p)
        {
            if (PresentationSource.FromVisual(e) == null) return false;
            Point a = e.PointToScreen(new Point(0, 0)), b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
            return p.X >= a.X && p.X < b.X && p.Y >= a.Y && p.Y < b.Y;
        }

        void Unhook()
        {
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
            mouseHook = keyHook = IntPtr.Zero;
        }
    }
}
