// 图标：微软 Fluent UI System Icons（MIT，Windows 11 自己用的那套），路径数据在 icons.txt，编译时嵌进程序。
// 每个图标按 12/16/20/24/32/48 各有一版重画过的，显示时挑最接近的那一版再缩放，小尺寸也清楚；
// 要加粗的地方（勾、铃铛、锁这些小标记）用实心版。程序里的名字和 Fluent 的名字对照见 Names
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;

namespace TailOrbit
{
    static class Icons
    {
        // 程序里叫什么 → Fluent 里叫什么
        static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "monitor", "desktop" }, { "phone", "phone" }, { "laptop", "laptop" }, { "server", "server" },
            { "check", "checkmark" }, { "x", "dismiss" }, { "send", "send" }, { "inbox", "arrow_download" },
            { "copy", "copy" }, { "image", "image" }, { "video", "video" }, { "music", "music_note_2" }, { "archive", "folder_zip" },
            { "file", "document" }, { "folder", "folder" }, { "stack", "document_multiple" }, { "gear", "settings" }, { "alert", "error_circle" },
            { "min", "subtract" }, { "max", "maximize" }, { "restore", "square_multiple" }, { "close", "dismiss" },
            { "terminal", "window_console" }, { "remote", "desktop_cursor" }, { "globe", "globe" }, { "bell", "alert" }, { "bell-off", "alert_off" },
            { "lock", "lock_closed" }, { "plus", "add" }, { "refresh", "arrow_clockwise" }, { "external", "open" }, { "drive", "hard_drive" },
            { "folder-plus", "folder_add" }, { "exit", "arrow_exit" }, { "port", "plug_connected" }, { "chevron-down", "chevron_down" },
            { "key", "key" }, { "link", "link" }, { "unlink", "link_off" }, { "up", "folder_arrow_up" },
            { "clipboard", "clipboard_paste" }, { "qr", "qr_code" },
        };

        // Fluent 名字 + 样式（r 线框 / f 实心）→ 尺寸 → 形状
        static readonly Dictionary<string, SortedDictionary<int, Geometry>> shapes = new Dictionary<string, SortedDictionary<int, Geometry>>();
        static readonly Dictionary<string, string> raw = new Dictionary<string, string>();   // "名字 尺寸 样式" → 原始路径（给分享网页用）

        static Icons()
        {
            using (var s = typeof(Icons).Assembly.GetManifestResourceStream("icons.txt"))
            using (var r = new StreamReader(s, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    var p = line.Split(new[] { ' ' }, 4);
                    if (p.Length < 4) continue;
                    int size = int.Parse(p[1], CultureInfo.InvariantCulture);
                    raw[p[0] + " " + p[1] + " " + p[2]] = p[3];
                    // 好几条路径的合成一个形状；F1 是非零填充，和 SVG 的默认一致（WPF 默认是奇偶，带洞的图标会画错）
                    Geometry g = null;
                    foreach (var d in p[3].Split('|'))
                    {
                        var one = Geometry.Parse("F1 " + d);
                        g = g == null ? one : Geometry.Combine(g, one, GeometryCombineMode.Union, null);
                    }
                    g.Freeze();
                    SortedDictionary<int, Geometry> sizes;
                    if (!shapes.TryGetValue(p[0] + " " + p[2], out sizes)) shapes[p[0] + " " + p[2]] = sizes = new SortedDictionary<int, Geometry>();
                    sizes[size] = g;
                }
            }
        }

        // 挑最接近显示大小的那一版（按比例算远近）；要实心但没有实心版的，用线框版
        public static Geometry Get(string name, double px, bool solid, out int size)
        {
            string fluent = Names[name];
            SortedDictionary<int, Geometry> sizes;
            if (!(solid && shapes.TryGetValue(fluent + " f", out sizes))) sizes = shapes[fluent + " r"];
            size = sizes.Keys.OrderBy(k => Math.Abs(Math.Log(k / px))).First();
            return sizes[size];
        }

        // 分享出去的网页里用同一套图标：20 那版的原始路径（可能好几条）
        public static string[] Data(string name)
        {
            return raw[Names[name] + " 20 r"].Split('|');
        }

        public static string ForOs(string os)
        {
            switch ((os ?? "").ToLowerInvariant())
            {
                case "ios": case "android": return "phone";
                case "macos": return "laptop";
                case "linux": case "freebsd": case "openbsd": return "server";
                default: return "monitor";
            }
        }

        public static string ForFiles(IList<string> paths)
        {
            if (paths.Count != 1) return "stack";
            if (Directory.Exists(paths[0])) return "folder";
            switch (System.IO.Path.GetExtension(paths[0]).ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": case ".png": case ".gif": case ".heic": case ".webp": case ".bmp": case ".tif": case ".tiff": case ".dng": case ".svg": return "image";
                case ".mp4": case ".mov": case ".mkv": case ".avi": case ".webm": case ".m4v": return "video";
                case ".mp3": case ".m4a": case ".wav": case ".flac": case ".aac": case ".ogg": return "music";
                case ".zip": case ".rar": case ".7z": case ".tar": case ".gz": return "archive";
                default: return "file";
            }
        }
    }

    // 一个图标：换形状只换几何，颜色随时能改；图标本身不接鼠标，点击交给外面的容器。
    // weight 沿用原来画线时的粗细：2 以上的用实心版（小勾、铃铛、锁这类要压得住的标记）
    class IconView : Viewbox
    {
        readonly System.Windows.Shapes.Path path = new System.Windows.Shapes.Path();
        readonly Canvas box = new Canvas();
        readonly double px;
        readonly bool solid;

        public IconView(string name, double size, Brush ink, double weight = 1.75)
        {
            px = size;
            solid = weight >= 2;
            path.Fill = ink;
            box.Children.Add(path);
            Child = box;
            Width = size;
            Height = size;
            IsHitTestVisible = false;
            Show(name);
        }

        public void Show(string name)
        {
            int size;
            path.Data = Icons.Get(name, px, solid, out size);
            box.Width = box.Height = size;
        }

        // 图标的颜色（沿用画线时的名字，调用的地方不用改）
        public Brush Stroke
        {
            get { return path.Fill; }
            set { path.Fill = value; }
        }
    }
}
