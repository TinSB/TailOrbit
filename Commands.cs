// 各页用到的 tailscale 命令输出解析
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TailOrbit
{
    static partial class Tailscale
    {
        static readonly Regex LogStamp = new Regex(@"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}(\.\d+)? ");

        // 命令打出来的 JSON；netcheck 这类会在前面先打几行日志，从第一个 { 开始读
        public static Dictionary<string, object> Json(string text)
        {
            int i = (text ?? "").IndexOf('{');
            if (i < 0) return null;
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try { return js.DeserializeObject(text.Substring(i)) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        // 报错信息的第一行，去掉日志时间戳；什么都没有时用 fallback
        public static string FirstLine(string err, string fallback)
        {
            var line = (err ?? "").Split('\n').Select(l => LogStamp.Replace(l, "").Trim()).FirstOrDefault(l => l.Length > 0);
            return line ?? fallback;
        }

        // 官网上最新的稳定版本号；查不到返回 null
        public static string Upstream()
        {
            var r = Run("version --upstream");
            if (r.Code != 0) return null;
            var line = r.Out.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("upstream:"));
            return line == null ? null : line.Substring(9).Trim();
        }

        public static string FirstIp(Dictionary<string, object> node)
        {
            var ips = Get(node, "TailscaleIPs") as object[];
            return ips != null && ips.Length > 0 ? (ips[0] as string) ?? "" : "";
        }

        // 设备的短名字：MagicDNS 名的第一段，没有就用主机名
        public static string ShortName(Dictionary<string, object> node)
        {
            string dns = (Get(node, "DNSName") as string) ?? "";
            return dns.Length > 0 ? dns.Split('.')[0] : (Get(node, "HostName") as string) ?? "";
        }

        // 本机收文件的端口（PeerAPI）：status 里 Self.PeerAPIURL 形如 http://100.x.y.z:33031，IPv4、IPv6 各一个
        public static int[] PeerApiPorts()
        {
            var r = Run("status --json");
            var self = r.Code == 0 ? Get(Json(r.Out), "Self") as Dictionary<string, object> : null;
            var urls = (Get(self, "PeerAPIURL") as object[]) ?? new object[0];
            var list = new List<int>();
            foreach (var u in urls.OfType<string>())
            {
                int port;
                if (int.TryParse(u.Substring(u.LastIndexOf(':') + 1).TrimEnd('/'), out port)) list.Add(port);
            }
            return list.ToArray();
        }

        // 在 status 的设备里找第一台符合条件的
        public static Device FindPeer(Func<Dictionary<string, object>, bool> match)
        {
            var r = Run("status --json");
            var peers = r.Code == 0 ? Get(Json(r.Out), "Peer") as Dictionary<string, object> : null;
            var p = peers == null ? null : peers.Values.OfType<Dictionary<string, object>>().FirstOrDefault(match);
            if (p == null) return null;
            return new Device { Ip = FirstIp(p), Name = ShortName(p), Os = (Get(p, "OS") as string) ?? "", StableId = (Get(p, "ID") as string) ?? "", Dns = ((Get(p, "DNSName") as string) ?? "").TrimEnd('.'), Online = true.Equals(Get(p, "Online")) };
        }
    }
}
