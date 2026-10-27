// 分享用的小文件服务：只听本机 127.0.0.1:46213，Tailscale 把 https://<本机>:7443/s/<随机码>/… 转发成 /<随机码>/…。
// 只认登记过的随机码；路径里有 ..、反斜杠、冒号的一律不给；隐藏文件和点开头的文件不给；
// 从 Funnel（互联网）进来的请求只给已经公开、还没到点的分享。TailOrbit 没在跑时链接打不开，文件也就不会被拿走。
// /c/<随机码>/ 是剪贴板互通（ClipShare.cs），整条交给它
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TailOrbit
{
    static class FileServer
    {
        public const int Port = 46213;
        static TcpListener listener;
        static string error = "分享服务没开";
        [ThreadStatic] static string visitor;   // 这次请求的访客用什么语言（按浏览器的 Accept-Language），网页上的字按它翻译

        // null 表示在跑；否则是开不起来的原因
        public static string Error { get { return error == null ? null : error.Contains("{0}") ? L.T(error, Port) : L.T(error); } }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);

        // 只听本机的端口。先关掉“子进程继承”：不然 ssh、tailscale 这些子进程会带走一份句柄，
        // 程序被关掉后它们还活着，端口就一直被占着，下次启动开不起来（10-27 分享服务就这样没开起来）
        public static TcpListener Listen(int port)
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            SetHandleInformation(l.Server.Handle, 1, 0);   // HANDLE_FLAG_INHERIT
            l.Start();
            return l;
        }

        public static void Start()
        {
            if (listener != null) return;
            try
            {
                listener = Listen(Port);
                error = null;
            }
            catch (SocketException)
            {
                error = "本机 {0} 端口被别的程序占着，分享服务开不起来";
                return;
            }
            new Thread(Accept) { IsBackground = true, Name = "TailOrbit 分享服务" }.Start();
        }

        static void Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (SocketException) { Thread.Sleep(200); continue; }
                new Thread(() => Serve(c)) { IsBackground = true }.Start();
            }
        }

        // 每个连接只答一个请求就关；后台线程里漏出去的异常会让整个程序退出，所以全在这里接住
        static void Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = 15000;
                    var s = c.GetStream();
                    byte[] rest;
                    string head = ReadHead(s, out rest);
                    if (head != null) Handle(s, head, rest);
                }
            }
            catch (IOException) { }               // 对方中途断开、下载到一半取消
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Log.Write(e); }
        }

        // 请求头：读到空行为止，最多 16KB；rest 是顺带读进来的正文开头
        public static string ReadHead(Stream s, out byte[] rest)
        {
            rest = null;
            var buf = new byte[16384];
            int n = 0;
            while (n < buf.Length)
            {
                int k = s.Read(buf, n, buf.Length - n);
                if (k <= 0) return null;
                for (int i = Math.Max(0, n - 3); i + 3 < n + k; i++)
                    if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                    {
                        rest = new byte[n + k - i - 4];
                        Buffer.BlockCopy(buf, i + 4, rest, 0, rest.Length);
                        return Encoding.UTF8.GetString(buf, 0, i);
                    }
                n += k;
            }
            return null;
        }

        // 请求行拆成 方法、路径、版本；不是三段时返回 null
        public static string[] Parse(string head, out Dictionary<string, string> headers)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            var first = lines[0].Split(' ');
            return first.Length == 3 ? first : null;
        }

        static void Handle(Stream s, string head, byte[] early)
        {
            Dictionary<string, string> headers;
            var first = Parse(head, out headers);
            if (first == null) { Reply(s, 400, false); return; }
            string accept;
            visitor = L.FromCulture(headers.TryGetValue("Accept-Language", out accept) ? accept : "");
            if (first[1].StartsWith("/c/")) { ClipShare.Serve(s, first[0], first[1], headers, early); return; }
            bool headOnly = first[0] == "HEAD";
            if (first[0] != "GET" && !headOnly) { Reply(s, 405, false); return; }
            var parts = Segments(first[1]);
            var share = parts == null || parts[0] == "" ? null : Shares.Find(parts[0]);
            if (share == null || share.Source == "") { Reply(s, 404, headOnly); return; }
            bool fromInternet = headers.ContainsKey("Tailscale-Funnel-Request");
            bool expired = share.Expires != null && share.Expires.Value <= DateTime.UtcNow;
            if ((fromInternet && !share.Public) || expired) { Reply(s, 404, headOnly); return; }

            string root = share.Source;
            if (File.Exists(root))
            {
                if (parts.Skip(1).Any(p => p != "")) Reply(s, 404, headOnly);
                else SendFile(s, root, headers, headOnly);
                return;
            }
            if (!Directory.Exists(root)) { Reply(s, 404, headOnly); return; }
            // 文件夹：/随机码 先跳到 /随机码/，里面的相对链接才对
            if (parts.Count == 1) { Redirect(s, Uri.EscapeDataString(parts[0]) + "/"); return; }
            bool slash = parts[parts.Count - 1] == "";
            var rest = parts.Skip(1).Where(p => p != "").ToList();
            string full = Path.Combine(new[] { root }.Concat(rest).ToArray());
            string rootFull = Path.GetFullPath(root).TrimEnd('\\');
            full = Path.GetFullPath(full);
            if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase)) { Reply(s, 404, headOnly); return; }
            for (int i = 1; i <= rest.Count; i++)
                if (Hidden(Path.Combine(new[] { root }.Concat(rest.Take(i)).ToArray()))) { Reply(s, 404, headOnly); return; }
            if (Directory.Exists(full))
            {
                if (!slash) Redirect(s, Uri.EscapeDataString(rest[rest.Count - 1]) + "/");
                else Listing(s, full, rest.Count == 0 ? Shares.NameOf(root) : rest[rest.Count - 1], rest.Count > 0, headOnly);
            }
            else if (File.Exists(full) && !slash) SendFile(s, full, headers, headOnly);
            else Reply(s, 404, headOnly);
        }

        // /a/b/ → ["a","b",""]；有空段（//）、. 开头、..、反斜杠、冒号、不能做文件名的字符，整条不认
        static List<string> Segments(string target)
        {
            int q = target.IndexOf('?');
            if (q >= 0) target = target.Substring(0, q);
            if (!target.StartsWith("/")) return null;
            var raw = target.Substring(1).Split('/');
            var bad = Path.GetInvalidFileNameChars();
            var list = new List<string>();
            for (int i = 0; i < raw.Length; i++)
            {
                string p = Uri.UnescapeDataString(raw[i]);
                if (p == "")
                {
                    if (i != raw.Length - 1 || i == 0) return null;   // 只许结尾一个斜杠
                    list.Add("");
                    continue;
                }
                if (p.StartsWith(".") || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(bad) >= 0) return null;
                list.Add(p);
            }
            return list;
        }

        static bool Hidden(string path)
        {
            try { return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return true; }
        }

        // ---------- 回应 ----------

        static void SendFile(Stream s, string path, Dictionary<string, string> headers, bool headOnly)
        {
            FileStream f;
            try { f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536); }
            catch (IOException) { Reply(s, 404, headOnly); return; }
            catch (UnauthorizedAccessException) { Reply(s, 404, headOnly); return; }
            using (f)
            {
                long len = f.Length, start = 0, end = len - 1;
                string range;
                headers.TryGetValue("Range", out range);
                int kind = Range(range, len, ref start, ref end);
                if (kind == 2)
                {
                    Head(s, 416, "text/plain; charset=utf-8", 0, "Content-Range: bytes */" + len + "\r\n");
                    return;
                }
                string name = Path.GetFileName(path);
                string ascii = new string(name.Select(ch => ch < 32 || ch > 126 || ch == '"' || ch == '\\' ? '_' : ch).ToArray());
                string extra = "Accept-Ranges: bytes\r\nLast-Modified: " + File.GetLastWriteTimeUtc(path).ToString("r", CultureInfo.InvariantCulture) + "\r\n" +
                    "Content-Disposition: inline; filename=\"" + ascii + "\"; filename*=UTF-8''" + Uri.EscapeDataString(name) + "\r\n" +
                    (kind == 1 ? "Content-Range: bytes " + start + "-" + end + "/" + len + "\r\n" : "");
                long count = len == 0 ? 0 : end - start + 1;
                Head(s, kind == 1 ? 206 : 200, Mime(path), count, extra);
                if (headOnly || count == 0) return;
                f.Seek(start, SeekOrigin.Begin);
                var buf = new byte[65536];
                while (count > 0)
                {
                    int k = f.Read(buf, 0, (int)Math.Min(buf.Length, count));
                    if (k <= 0) break;
                    s.Write(buf, 0, k);
                    count -= k;
                }
            }
        }

        // Range: bytes=a-b / a- / -n，只认一段。返回 0 整个给、1 给一段、2 要的那段不存在
        static int Range(string h, long len, ref long start, ref long end)
        {
            if (h == null || !h.StartsWith("bytes=") || h.Contains(",")) return 0;
            var p = h.Substring(6).Split('-');
            if (p.Length != 2) return 0;
            long a, b;
            bool hasA = long.TryParse(p[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out a);
            bool hasB = long.TryParse(p[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out b);
            if (hasA)
            {
                if (a >= len) return 2;
                if (hasB && b < a) return 0;
                start = a;
                end = hasB ? Math.Min(b, len - 1) : len - 1;
                return 1;
            }
            if (p[0].Trim() != "" || !hasB || b == 0) return 0;
            if (len == 0) return 2;
            start = Math.Max(0, len - b);
            end = len - 1;
            return 1;
        }

        // cache：剪贴板的内容用 no-store，浏览器连缓存都别留
        public static void Head(Stream s, int code, string type, long length, string extra, string cache = "no-cache")
        {
            string h = "HTTP/1.1 " + code + " " + Reason(code) + "\r\nContent-Type: " + type + "\r\nContent-Length: " + length + "\r\n" + extra +
                "Cache-Control: " + cache + "\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
            var b = Encoding.UTF8.GetBytes(h);
            s.Write(b, 0, b.Length);
        }

        static void Reply(Stream s, int code, bool headOnly)
        {
            var body = Encoding.UTF8.GetBytes(Page(code == 404 ? Say("找不到") : Say("打不开"), code == 404 ? Say("这个分享已经关了，或者文件不在了。") : Say("请求不对。"), ""));
            Head(s, code, "text/html; charset=utf-8", body.Length, "");
            if (!headOnly) s.Write(body, 0, body.Length);
        }

        public static void Redirect(Stream s, string to)
        {
            Head(s, 302, "text/plain; charset=utf-8", 0, "Location: " + to + "\r\n");
        }

        static string Reason(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 302: return "Found";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 416: return "Range Not Satisfiable";
                case 503: return "Service Unavailable";
                default: return "Not Found";
            }
        }

        // 文件夹里有什么：子文件夹在前，隐藏的不列
        static void Listing(Stream s, string dir, string title, bool sub, bool headOnly)
        {
            var rows = new StringBuilder();
            if (sub) rows.Append(Row("../", Say("上一级"), "", Icon(Icons.Data("up"))));
            List<FileSystemInfo> items;
            try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos().Where(x => !x.Name.StartsWith(".") && (x.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0).ToList(); }
            catch (IOException) { items = new List<FileSystemInfo>(); }
            catch (UnauthorizedAccessException) { items = new List<FileSystemInfo>(); }
            var cmp = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
            foreach (var d in items.OfType<DirectoryInfo>().OrderBy(x => x.Name, cmp))
                rows.Append(Row(Uri.EscapeDataString(d.Name) + "/", d.Name, d.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), Icon(Icons.Data("folder"))));
            foreach (var f in items.OfType<FileInfo>().OrderBy(x => x.Name, cmp))
                rows.Append(Row(Uri.EscapeDataString(f.Name), f.Name, Ui.FormatSize(f.Length) + " · " + f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), Icon(Icons.Data("file"))));
            if (items.Count == 0) rows.Append("<p class=\"empty\">" + WebUtility.HtmlEncode(Say("这个文件夹是空的")) + "</p>");
            var body = Encoding.UTF8.GetBytes(Page(title, null, rows.ToString()));
            Head(s, 200, "text/html; charset=utf-8", body.Length, "");
            if (!headOnly) s.Write(body, 0, body.Length);
        }

        static string Row(string href, string name, string meta, string icon)
        {
            return "<a class=\"row\" href=\"" + WebUtility.HtmlEncode(href) + "\">" + icon + "<span class=\"n\">" + WebUtility.HtmlEncode(name) + "</span><span class=\"m\">" + meta + "</span></a>";
        }

        static string Icon(string[] paths)
        {
            return "<svg viewBox=\"0 0 20 20\" width=\"20\" height=\"20\" aria-hidden=\"true\">" + string.Concat(paths.Select(d => "<path d=\"" + d + "\"/>")) + "</svg>";
        }

        // 按这次访客的语言
        public static string Say(string zh, params object[] args)
        {
            return L.For(visitor ?? "en", zh, args);
        }

        // 给打开链接的人看的页面：跟着系统深浅色走，手机上也好点
        public static string Page(string title, string text, string rows)
        {
            return "<!doctype html><html lang=\"" + (visitor ?? "en") + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<title>" + WebUtility.HtmlEncode(title) + "</title><style>" +
                ":root{--bg:#E6E8EB;--fg:#1A1C1F;--mute:#5B6169;--tile:#F4F5F6;--hair:rgba(26,28,31,.12)}" +
                "@media (prefers-color-scheme:dark){:root{--bg:#1A1C1F;--fg:#E7E9EC;--mute:#8C929A;--tile:#24272B;--hair:rgba(231,233,236,.1)}}" +
                "body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 'Segoe UI',system-ui,-apple-system,'PingFang SC','Microsoft YaHei',sans-serif}" +
                "main{max-width:760px;margin:0 auto;padding:32px 16px 48px}h1{font-size:22px;font-weight:600;margin:0 0 18px;word-break:break-all}" +
                "a.row{display:flex;align-items:center;gap:14px;padding:13px 16px;margin-bottom:6px;border-radius:12px;background:var(--tile);color:inherit;text-decoration:none}" +
                "a.row:hover{box-shadow:inset 0 0 0 1px var(--hair)}.n{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}" +
                ".m{color:var(--mute);font-size:13px;white-space:nowrap}svg{flex:0 0 20px;fill:currentColor}" +
                "p{color:var(--mute)}.empty{padding:20px 4px}</style></head><body><main><h1>" + WebUtility.HtmlEncode(title) + "</h1>" +
                (text != null ? "<p>" + WebUtility.HtmlEncode(text) + "</p>" : "") + rows + "</main></body></html>";
        }

        static string Mime(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".txt": case ".log": return "text/plain; charset=utf-8";
                case ".md": return "text/markdown; charset=utf-8";
                case ".csv": return "text/csv; charset=utf-8";
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".json": return "application/json";
                case ".pdf": return "application/pdf";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                case ".heic": return "image/heic";
                case ".bmp": return "image/bmp";
                case ".mp4": case ".m4v": return "video/mp4";
                case ".mov": return "video/quicktime";
                case ".webm": return "video/webm";
                case ".mkv": return "video/x-matroska";
                case ".mp3": return "audio/mpeg";
                case ".m4a": return "audio/mp4";
                case ".wav": return "audio/wav";
                case ".flac": return "audio/flac";
                case ".ogg": return "audio/ogg";
                case ".zip": return "application/zip";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                default: return "application/octet-stream";
            }
        }
    }
}
