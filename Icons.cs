// 线性图标：24×24 画布上 1.75 粗、圆头圆角的细线，主界面和快捷面板共用这一套
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;

namespace TailOrbit
{
    static class Icons
    {
        static readonly Dictionary<string, Geometry> shapes = new Dictionary<string, Geometry>();
        static readonly Dictionary<string, string> sources = new Dictionary<string, string>();

        static Icons()
        {
            Add("monitor", Box(2.5, 3.5, 19, 13, 2) + " M8.5,20.5 H15.5 M12,16.5 V20.5");
            Add("phone", Box(6.5, 2.5, 11, 19, 2.5) + " M11,18 H13");
            Add("laptop", Box(4, 4.5, 16, 10.5, 1.5) + " M2,19 H22");
            Add("server", Box(3, 3.5, 18, 7, 2) + Box(3, 13.5, 18, 7, 2) + " M7,7 L7.01,7 M7,17 L7.01,17");
            Add("check", "M5,12.5 L9.5,17 L19,7.5");
            Add("x", "M6.5,6.5 L17.5,17.5 M17.5,6.5 L6.5,17.5");
            Add("send", "M12,19.5 V4.5 M6,10.5 L12,4.5 L18,10.5");
            Add("inbox", "M12,3.5 V15 M7,10 L12,15 L17,10 M4.5,20.5 H19.5");
            Add("copy", Box(8, 8, 13, 13, 2) + " M16,8 V5.5 A2,2 0 0 0 14,3.5 H5.5 A2,2 0 0 0 3.5,5.5 V14 A2,2 0 0 0 5.5,16 H8");
            Add("image", Box(3.5, 3.5, 17, 17, 2.5) + Ring(9, 9, 1.8) + " M20.5,15 L15.9,10.4 A1.5,1.5 0 0 0 13.8,10.4 L4,20.2");
            Add("video", Box(2.5, 6, 13, 12, 2) + " M15.5,10.5 L21.5,7 V17 L15.5,13.5");
            Add("music", "M9,18 V5.5 L20,3.5 V16" + Ring(6, 18, 3) + Ring(17, 16, 3));
            Add("archive", Box(3, 3.5, 18, 5, 1.5) + " M4.5,8.5 V18.5 A2,2 0 0 0 6.5,20.5 H17.5 A2,2 0 0 0 19.5,18.5 V8.5 M10,12.5 H14");
            Add("file", "M14,2.5 H6.5 A2,2 0 0 0 4.5,4.5 V19.5 A2,2 0 0 0 6.5,21.5 H17.5 A2,2 0 0 0 19.5,19.5 V8 Z M14,2.5 V8 H19.5");
            Add("folder", "M3.5,6.5 A2,2 0 0 1 5.5,4.5 H9.5 L11.5,7 H18.5 A2,2 0 0 1 20.5,9 V17.5 A2,2 0 0 1 18.5,19.5 H5.5 A2,2 0 0 1 3.5,17.5 Z");
            Add("stack", "M12,3 L21,7.5 L12,12 L3,7.5 Z M3,12 L12,16.5 L21,12 M3,16.5 L12,21 L21,16.5");
            Add("gear", Ring(12, 12, 3) + " M19.4,15 a1.65,1.65 0 0 0 0.33,1.82 l0.06,0.06 a2,2 0 1 1 -2.83,2.83 l-0.06,-0.06 a1.65,1.65 0 0 0 -1.82,-0.33" +
                " a1.65,1.65 0 0 0 -1,1.51 V21 a2,2 0 0 1 -4,0 v-0.09 A1.65,1.65 0 0 0 9,19.4 a1.65,1.65 0 0 0 -1.82,0.33 l-0.06,0.06 a2,2 0 1 1 -2.83,-2.83" +
                " l0.06,-0.06 A1.65,1.65 0 0 0 4.68,15 a1.65,1.65 0 0 0 -1.51,-1 H3 a2,2 0 0 1 0,-4 h0.09 A1.65,1.65 0 0 0 4.6,9 a1.65,1.65 0 0 0 -0.33,-1.82" +
                " l-0.06,-0.06 a2,2 0 1 1 2.83,-2.83 l0.06,0.06 A1.65,1.65 0 0 0 9,4.68 a1.65,1.65 0 0 0 1,-1.51 V3 a2,2 0 0 1 4,0 v0.09 a1.65,1.65 0 0 0 1,1.51" +
                " a1.65,1.65 0 0 0 1.82,-0.33 l0.06,-0.06 a2,2 0 1 1 2.83,2.83 l-0.06,0.06 A1.65,1.65 0 0 0 19.4,9 a1.65,1.65 0 0 0 1.51,1 H21 a2,2 0 0 1 0,4 h-0.09 a1.65,1.65 0 0 0 -1.51,1 Z");
            Add("alert", Ring(12, 12, 9) + " M12,7.5 V12.5 M12,16 L12.01,16");
            Add("min", "M6,12 H18");
            Add("max", Box(6.5, 6.5, 11, 11, 1.5));
            Add("restore", Box(5.5, 8.5, 10, 10, 1.5) + " M8.5,5.5 H17 A1.5,1.5 0 0 1 18.5,7 V15.5");
            Add("close", "M7,7 L17,17 M17,7 L7,17");
            Add("terminal", "M4.5,17 L10,12 L4.5,7 M12,19 H19.5");
            Add("remote", Box(2.5, 3.5, 19, 13, 2) + " M8.5,20.5 H15.5 M12,16.5 V20.5 M9.5,10 L12,7.5 L14.5,10 M12,7.5 V13");
            Add("globe", Ring(12, 12, 9) + " M3,12 H21 M12,3 C14.5,5.6 15.8,8.6 15.8,12 C15.8,15.4 14.5,18.4 12,21 C9.5,18.4 8.2,15.4 8.2,12 C8.2,8.6 9.5,5.6 12,3 Z");
            Add("bell", "M6,9 A6,6 0 0 1 18,9 C18,15 20.5,16.5 20.5,16.5 H3.5 C3.5,16.5 6,15 6,9 Z M10.2,20 A2,2 0 0 0 13.8,20");
            Add("bell-off", "M6,9 A6,6 0 0 1 18,9 C18,15 20.5,16.5 20.5,16.5 H3.5 C3.5,16.5 6,15 6,9 Z M10.2,20 A2,2 0 0 0 13.8,20 M3,3 L21,21");
            Add("lock", Box(5, 11, 14, 10, 2) + " M8,11 V7.5 A4,4 0 0 1 16,7.5 V11");
            Add("plus", "M12,5 V19 M5,12 H19");
            Add("refresh", "M20,11 A8,8 0 1 0 17.7,16.6 M20,4.5 V11 H13.5");
            Add("external", "M14,4 H20 V10 M20,4 L11,13 M18,13.5 V18.5 A1.5,1.5 0 0 1 16.5,20 H5.5 A1.5,1.5 0 0 1 4,18.5 V7.5 A1.5,1.5 0 0 1 5.5,6 H10.5");
            Add("drive", Box(2.5, 13, 19, 7.5, 2) + " M5.5,13 L8,4.5 H16 L18.5,13 M6.5,16.75 L6.51,16.75 M10,16.75 L10.01,16.75");
            Add("folder-plus", "M3.5,6.5 A2,2 0 0 1 5.5,4.5 H9.5 L11.5,7 H18.5 A2,2 0 0 1 20.5,9 V17.5 A2,2 0 0 1 18.5,19.5 H5.5 A2,2 0 0 1 3.5,17.5 Z M12,10.5 V16.5 M9,13.5 H15");
            Add("exit", "M9,20.5 H5.5 A2,2 0 0 1 3.5,18.5 V5.5 A2,2 0 0 1 5.5,3.5 H9 M15.5,16.5 L20,12 L15.5,7.5 M20,12 H9");
            Add("port", Ring(12, 12, 9) + " M8,12 H16 M13,9 L16,12 L13,15");
        }

        static string Box(double x, double y, double w, double h, double r)
        {
            return string.Format(CultureInfo.InvariantCulture, " M{0},{1} H{2} A{3},{3} 0 0 1 {4},{5} V{6} A{3},{3} 0 0 1 {2},{7} H{0} A{3},{3} 0 0 1 {8},{6} V{5} A{3},{3} 0 0 1 {0},{1} Z",
                x + r, y, x + w - r, r, x + w, y + r, y + h - r, y + h, x);
        }

        static string Ring(double cx, double cy, double r)
        {
            return string.Format(CultureInfo.InvariantCulture, " M{0},{1} A{2},{2} 0 1 0 {3},{1} A{2},{2} 0 1 0 {0},{1} Z", cx - r, cy, r, cx + r);
        }

        static void Add(string name, string data)
        {
            var g = Geometry.Parse(data.Trim());
            g.Freeze();
            shapes[name] = g;
            sources[name] = data.Trim();
        }

        public static Geometry Get(string name)
        {
            return shapes[name];
        }

        // 原始路径数据：分享出去的网页里用同一套图标
        public static string Data(string name)
        {
            return sources[name];
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

    // 一个图标：换形状只换几何，颜色随时能改；图标本身不接鼠标，点击交给外面的容器
    class IconView : Viewbox
    {
        readonly System.Windows.Shapes.Path path;

        public IconView(string name, double size, Brush stroke, double weight = 1.75)
        {
            path = new System.Windows.Shapes.Path { Stroke = stroke, StrokeThickness = weight, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            var box = new Canvas { Width = 24, Height = 24 };
            box.Children.Add(path);
            Child = box;
            Width = size;
            Height = size;
            IsHitTestVisible = false;
            Show(name);
        }

        public void Show(string name)
        {
            path.Data = Icons.Get(name);
        }

        public Brush Stroke
        {
            get { return path.Stroke; }
            set { path.Stroke = value; }
        }
    }
}
