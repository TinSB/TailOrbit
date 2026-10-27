// 快捷面板：按 Ctrl+Alt+A 弹出，列出自己的设备；按数字键或点一下，就把资源管理器里选中的文件发过去
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace TailOrbit
{
    class Row
    {
        public Device Device;
        public Border Root, Fill, ActionBtn;
        public TextBlock Status;
        public IconView Action;
        public readonly ScaleTransform FillScale = new ScaleTransform(0, 1);
        public readonly TranslateTransform Shift = new TranslateTransform();
        public readonly ScaleTransform Pop = new ScaleTransform(1, 1);
        public TranslateTransform Shimmer;   // 不知道进度时流动的光
    }

    class QuickPanel : Window
    {
        readonly Host host;
        readonly Theme th;
        readonly Border card, headerTile;
        readonly IconView headerIcon;
        readonly TextBlock headerTitle, headerSize;
        readonly StackPanel list = new StackPanel();
        readonly ScaleTransform cardScale = new ScaleTransform(1, 1);
        readonly TranslateTransform cardShift = new TranslateTransform();
        readonly List<Row> rows = new List<Row>();
        List<Device> devices = new List<Device>();
        List<string> files = new List<string>();
        int selected = -1;
        bool picking;      // 正在选文件，面板失焦也别收起
        SendJob job;
        int jobIndex = -1;
        int version;       // 每次弹出/收起加一，过期的动画回调不再生效
        int loads;         // 只认最后一次读设备的结果

        public QuickPanel(Host host)
        {
            this.host = host;
            th = host.Theme;
            Title = L.T("TailOrbit · 快速发送");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.Height;
            Width = 488;
            UseLayoutRounding = true;
            FontFamily = Ui.TextFont;

            headerIcon = new IconView("file", 22, th.Mute);
            headerTile = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(13), Child = headerIcon };
            headerTitle = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = th.Fg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            headerSize = new TextBlock { FontSize = 13, Foreground = th.Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
            var header = new Border { Child = Ui.Columns(1, headerTile, headerTitle, headerSize), Padding = new Thickness(6), CornerRadius = new CornerRadius(13), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            header.MouseEnter += delegate { header.Background = th.Wash; };
            header.MouseLeave += delegate { header.Background = Brushes.Transparent; };
            header.MouseLeftButtonUp += delegate { if (job == null && PickFiles()) UpdateHeader(); };

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(new Border { Height = 1, Background = th.Hair, Margin = new Thickness(6, 10, 6, 8) });
            stack.Children.Add(list);

            var tg = new TransformGroup();
            tg.Children.Add(cardScale);
            tg.Children.Add(cardShift);
            card = new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = th.Pop,
                BorderBrush = th.Hair,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(24, 12, 24, 36),
                Child = stack,
                RenderTransformOrigin = new Point(0.5, 0),
                RenderTransform = tg,
                Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 12, Direction = 270, Opacity = th.Dark ? 0.5 : 0.2, Color = Colors.Black }
            };
            Content = card;
        }

        public void ShowFor(List<string> selection)
        {
            version++;
            files = job == null ? selection : job.Paths;
            UpdateHeader();
            if (job == null) BuildRows(true);
            Place();
            Show();
            Activate();
            Focus();
            Ui.AnimFrom(card, OpacityProperty, 0, 1, 150);
            Ui.AnimFrom(cardScale, ScaleTransform.ScaleXProperty, 0.96, 1, 240);
            Ui.AnimFrom(cardScale, ScaleTransform.ScaleYProperty, 0.96, 1, 240);
            Ui.AnimFrom(cardShift, TranslateTransform.YProperty, -10, 0, 240);
            Reload();
        }

        public void HidePanel()
        {
            if (!IsVisible) return;
            int v = ++version;
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            a.Completed += delegate { if (v == version) Hide(); };
            card.BeginAnimation(OpacityProperty, a);
        }

        public void Abort()
        {
            if (job != null) job.Cancel();
        }

        public bool Busy { get { return job != null; } }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            if (!picking) HidePanel();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            int n = -1;
            if (k >= Key.D1 && k <= Key.D9) n = k - Key.D1;
            else if (k >= Key.NumPad1 && k <= Key.NumPad9) n = k - Key.NumPad1;
            if (k == Key.Escape) HidePanel();
            else if (n >= 0) { if (n < rows.Count) { selected = n; Highlight(); Choose(n); } }
            else if (k == Key.Down || k == Key.Up) { if (rows.Count > 0) { selected = (Math.Max(selected, 0) + (k == Key.Down ? 1 : rows.Count - 1)) % rows.Count; Highlight(); } }
            else if (k == Key.Enter) { if (selected >= 0) Choose(selected); else if (job == null && PickFiles()) UpdateHeader(); }
            else if (k == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { if (selected >= 0) CopyIp(selected); }
            else { base.OnPreviewKeyDown(e); return; }
            e.Handled = true;
        }

        void Place()
        {
            var area = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
            new WindowInteropHelper(this).EnsureHandle();
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = area.Left / dpi.DpiScaleX + (area.Width / dpi.DpiScaleX - Width) / 2;
            Top = area.Top / dpi.DpiScaleY + area.Height / dpi.DpiScaleY * 0.2;
        }

        void Reload()
        {
            int n = ++loads;
            Task.Run(() => Tailscale.Load()).ContinueWith(t =>
            {
                if (n != loads || job != null) return;
                if (t.IsFaulted) { devices = new List<Device>(); Missing(); return; }
                var snap = t.Result;
                if (snap.Error != null) { devices = snap.Devices; Message(snap.Error, true); return; }
                if (snap.Devices.Count == 0) { devices = snap.Devices; Message(L.T("没有可接收文件的设备"), false); return; }
                bool same = snap.Devices.Select(d => d.Key).SequenceEqual(devices.Select(d => d.Key)) && rows.Count == snap.Devices.Count;
                bool wasEmpty = rows.Count == 0;
                devices = snap.Devices;
                if (!same) BuildRows(wasEmpty);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 读不到设备：没装 Tailscale 就在提示下面放“安装 Tailscale”，装好接着读
        void Missing()
        {
            bool missing = TailscaleSetup.Missing;
            Message(missing ? L.T("未安装 Tailscale") : L.T("无法读取设备列表"), true);
            if (missing) list.Children.Add(new InstallButton(th, Reload) { Margin = new Thickness(14, 0, 14, 14) });
        }

        void Message(string text, bool error)
        {
            list.Children.Clear();
            rows.Clear();
            selected = -1;
            var g = Ui.Columns(1, new IconView(error ? "alert" : "monitor", 18, error ? th.Bad : th.Mute),
                new TextBlock { Text = text, FontSize = 14, Foreground = error ? th.Bad : th.Mute, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            g.Margin = new Thickness(14, 12, 14, 12);
            list.Children.Add(g);
        }

        void BuildRows(bool animate)
        {
            if (devices.Count == 0) { Message(L.T("正在读取设备…"), false); return; }
            list.Children.Clear();
            rows.Clear();
            for (int i = 0; i < devices.Count; i++)
            {
                var r = BuildRow(devices[i], i);
                rows.Add(r);
                list.Children.Add(r.Root);
                double o = devices[i].Online ? 1 : 0.45;
                if (animate)
                {
                    Ui.AnimFrom(r.Root, OpacityProperty, 0, o, 240, 60 + 45 * i);
                    Ui.AnimFrom(r.Shift, TranslateTransform.XProperty, 18, 0, 340, 60 + 45 * i);
                }
                else r.Root.Opacity = o;
            }
            selected = devices.FindIndex(d => d.Online);
            Highlight();
        }

        Row BuildRow(Device d, int i)
        {
            var r = new Row { Device = d };
            var badge = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(7), Background = th.Chip, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = i < 9 ? (i + 1).ToString() : "", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = th.Mute, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var icon = new IconView(Icons.ForOs(d.Os), 20, th.Fg) { Margin = new Thickness(14, 0, 12, 0) };
            var name = new TextBlock { Text = d.Name, FontSize = 15, Foreground = th.Fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            r.Status = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis };
            r.Action = new IconView("copy", 16, th.Mute);
            r.ActionBtn = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Child = r.Action, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = r.Pop };
            var content = Ui.Columns(2, badge, icon, name, r.Status, r.ActionBtn);
            content.Margin = new Thickness(10, 7, 6, 7);
            r.Fill = new Border { CornerRadius = new CornerRadius(12), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = r.FillScale, Opacity = 0, IsHitTestVisible = false };
            var layers = new Grid();
            layers.Children.Add(r.Fill);
            layers.Children.Add(content);
            r.Root = new Border { CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Child = layers, Margin = new Thickness(0, 2, 0, 2), RenderTransform = r.Shift, Cursor = Cursors.Hand };
            Idle(r);

            int index = i;
            r.Root.MouseEnter += delegate { selected = index; Highlight(); };
            r.Root.MouseLeftButtonUp += delegate { Choose(index); };
            r.ActionBtn.MouseEnter += delegate { r.ActionBtn.Background = th.Hair; };
            r.ActionBtn.MouseLeave += delegate { r.ActionBtn.Background = Brushes.Transparent; };
            r.ActionBtn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (job != null && index == jobIndex) { job.Cancel(); r.Status.Text = L.T("正在取消…"); }
                else CopyIp(index);
            };
            return r;
        }

        void Highlight()
        {
            for (int k = 0; k < rows.Count; k++) rows[k].Root.Background = k == selected ? th.Wash : Brushes.Transparent;
        }

        void Choose(int i)
        {
            if (i < 0 || i >= rows.Count) return;
            var r = rows[i];
            if (job != null || !r.Device.Online)
            {
                if (i != jobIndex) Ui.Shake(r.Shift);
                return;
            }
            if (files.Count == 0)
            {
                if (!PickFiles()) return;
                UpdateHeader();
            }
            StartSend(i);
        }

        bool PickFiles()
        {
            var dlg = new OpenFileDialog { Multiselect = true, Title = L.T("选择要发送的文件") };
            picking = true;
            bool ok;
            try { ok = dlg.ShowDialog(this) == true; }
            finally { picking = false; }
            Activate();
            if (ok) files = dlg.FileNames.ToList();
            return ok;
        }

        void StartSend(int i)
        {
            var r = rows[i];
            var j = new SendJob { Target = r.Device, Paths = new List<string>(files) };
            job = j;
            jobIndex = i;
            j.Progress = (f, text) => Dispatcher.BeginInvoke(new Action(() => { if (job == j) SetProgress(r, f, text); }));
            SetProgress(r, SendJob.Unknown, L.T("正在准备…"));
            r.Action.Show("x");
            r.ActionBtn.ToolTip = Ui.Tip(th, L.T("取消发送"));
            for (int k = 0; k < rows.Count; k++) if (k != i) Ui.AnimTo(rows[k].Root, OpacityProperty, 0.35, 180);
            Task.Run(() => j.Run()).ContinueWith(t => Finish(r, j, t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result), TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Finish(Row r, SendJob j, string result)
        {
            job = null;
            jobIndex = -1;
            History.Record(j, result);
            for (int k = 0; k < rows.Count; k++) Ui.AnimTo(rows[k].Root, OpacityProperty, rows[k].Device.Online ? 1 : 0.45, 180);
            if (result == null)
            {
                files = new List<string>();
                Done(r);
                if (!IsVisible) { host.Notify(L.T("已发送到 {0}", j.Target.Name), Ui.Describe(j.Paths), true); return; }
                int v = version;
                Ui.After(1100, () => { if (v == version) HidePanel(); });
            }
            else if (result == "") Idle(r);
            else
            {
                Fail(r, L.T(result));
                if (!IsVisible) host.Notify(L.T("发送到 {0} 失败", j.Target.Name), L.T(result), true);
            }
        }

        void CopyIp(int i)
        {
            var r = rows[i];
            if (!Ui.CopyText(r.Device.Ip)) { r.Status.Text = L.T("剪贴板正被其他程序占用，请重试。"); return; }
            r.Status.Text = L.T("{0} 已复制", r.Device.Ip);
            r.Status.Foreground = th.Ok;
            r.Action.Show("check");
            r.Action.Stroke = th.Ok;
            Ui.Pop(r.Pop);
            Ui.After(1500, () => { if (jobIndex != i) Idle(r); });
        }

        void UpdateHeader()
        {
            if (files.Count == 0)
            {
                headerIcon.Show("file");
                headerIcon.Stroke = th.Mute;
                headerTile.Background = th.Chip;
                headerTitle.Text = L.T("选择要发送的文件");
                headerSize.Text = "";
                return;
            }
            bool dirs = files.Any(Directory.Exists);
            headerIcon.Show(Icons.ForFiles(files));
            headerIcon.Stroke = th.Ink;
            headerTile.Background = th.Fg;
            headerTitle.Text = files.Count > 1 ? files.Count + (dirs ? L.T(" 个项目") : L.T(" 个文件")) : System.IO.Path.GetFileName(files[0]);
            headerSize.Text = dirs ? "" : Ui.FormatSize(files.Sum(f => new FileInfo(f).Length));
        }

        void Idle(Row r)
        {
            Ui.StopShimmer(ref r.Shimmer);
            Ui.AnimTo(r.Fill, OpacityProperty, 0, 200);
            r.Status.Text = r.Device.Online ? "" : L.T("离线");
            r.Status.Foreground = th.Mute;
            r.Status.ToolTip = null;
            r.Action.Show("copy");
            r.Action.Stroke = th.Mute;
            r.ActionBtn.ToolTip = Ui.Tip(th, L.T("复制 IP 地址"));
        }

        void SetProgress(Row r, double f, string text)
        {
            r.Status.Text = text;
            r.Status.Foreground = th.Mute;
            r.Status.ToolTip = null;
            if (f == SendJob.Keep) return;
            Ui.AnimTo(r.Fill, OpacityProperty, 1, 150);
            if (f < 0)
            {
                if (r.Shimmer == null) r.Fill.Background = Ui.Shimmer(r.Fill, th.FgColor, out r.Shimmer);
                Ui.AnimTo(r.FillScale, ScaleTransform.ScaleXProperty, 1, 200);
            }
            else
            {
                Ui.StopShimmer(ref r.Shimmer);
                r.Fill.Background = th.Fill;
                Ui.AnimTo(r.FillScale, ScaleTransform.ScaleXProperty, Math.Max(0.03, f), 280);
            }
        }

        void Done(Row r)
        {
            Ui.StopShimmer(ref r.Shimmer);
            r.Fill.Background = th.OkSoft;
            Ui.AnimTo(r.Fill, OpacityProperty, 1, 150);
            Ui.AnimTo(r.FillScale, ScaleTransform.ScaleXProperty, 1, 260);
            r.Status.Text = L.T("已发送");
            r.Status.Foreground = th.Ok;
            r.Action.Show("check");
            r.Action.Stroke = th.Ok;
            r.ActionBtn.ToolTip = null;
            Ui.Pop(r.Pop);
        }

        void Fail(Row r, string msg)
        {
            Ui.StopShimmer(ref r.Shimmer);
            r.Fill.Background = th.BadSoft;
            Ui.AnimTo(r.Fill, OpacityProperty, 1, 150);
            Ui.AnimTo(r.FillScale, ScaleTransform.ScaleXProperty, 1, 200);
            r.Status.Text = msg;
            r.Status.ToolTip = Ui.Tip(th, msg);
            r.Status.Foreground = th.Bad;
            r.Action.Show("copy");
            r.Action.Stroke = th.Mute;
            r.ActionBtn.ToolTip = Ui.Tip(th, L.T("复制 IP 地址"));
            Ui.Shake(r.Shift);
        }
    }
}
