// 本机开放了什么：其他设备经 Tailscale 连得进来的端口——有程序在所有地址或本机 Tailscale 地址上监听、Windows 防火墙在 Tailscale 网卡所在的网络上放行，
// 外加 tailscale serve 分享出去的（Funnel 是公开到整个互联网）。只在本机读监听列表、进程和防火墙规则，不连任何地方。
// 防火墙照规则大致判断：入站、启用、配置文件和协议对得上，程序是任意或就是它，端口包含它，本地和远程地址是任意或含 Tailscale 的地址段；对得上的阻止规则优先。
// 按服务限定的规则不细分是哪个服务；Windows 内置的服务加固规则不在规则列表里，看不到。
// Tailscale 自己装了一条 Tailscale-In（专用和域网络上任意程序、任意端口都放行），它的网卡又是专用网络，所以通常在听的都连得进来，挡人靠的是访问控制
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace TailOrbit
{
    class Listen
    {
        public IPAddress Address;
        public int Port, Pid;
    }

    static class Exposure
    {
        // 开放的一项：一个程序（系统服务按端口认是哪项）和它的端口；Share 不是 null 的是 serve 分享
        public class Item
        {
            public string Name = "", Path = "";
            public List<int> Ports = new List<int>();
            public ServeEntry Share;
        }

        public class Result
        {
            public List<Item> Items = new List<Item>();
            public bool Shields;   // Tailscale 挡着所有传入连接：程序在听也连不进来
            public bool Wide;      // 防火墙在这个网络上关着或默认放行：在听的都连得进来
        }

        class Rule
        {
            public string App, Ports, Local, Remote;   // App 是 null：任意程序
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        public static Result Scan(bool shields, IEnumerable<ServeEntry> shares)
        {
            var res = new Result { Shields = shields };
            foreach (var s in shares ?? new ServeEntry[0]) res.Items.Add(new Item { Name = s.Label, Ports = { s.Port }, Share = s });
            if (shields) return res;
            var allow = new List<Rule>();
            var block = new List<Rule>();
            int mode = Firewall(Profile(), allow, block);   // 0 照规则、1 全放行、2 全挡住
            if (mode == 2) return res;
            res.Wide = mode == 1;
            var paths = new Dictionary<int, string>();
            var open = new Dictionary<string, Item>();
            foreach (var l in Ports.Listening().Where(l => Reachable(l.Address)))
            {
                string path;
                if (!paths.TryGetValue(l.Pid, out path)) paths[l.Pid] = path = PathOf(l.Pid);
                if (path == "" || System.IO.Path.GetFileNameWithoutExtension(path).Equals("tailscaled", StringComparison.OrdinalIgnoreCase)) continue;   // Tailscale 自己收文件那些
                if (mode == 0 && (!allow.Any(r => Match(r, path, l.Port)) || block.Any(r => Match(r, path, l.Port)))) continue;
                string name = NameOf(path, l.Port);
                Item item;
                if (!open.TryGetValue(name, out item)) open[name] = item = new Item { Name = name, Path = path };
                if (!item.Ports.Contains(l.Port)) item.Ports.Add(l.Port);
            }
            foreach (var item in open.Values.OrderBy(i => i.Ports.Min()))
            {
                item.Ports.Sort();
                res.Items.Add(item);
            }
            return res;
        }

        // 在所有地址或本机 Tailscale 地址上听的，其他设备才连得到
        static bool Reachable(IPAddress a)
        {
            if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return true;
            var b = a.GetAddressBytes();
            return b.Length == 4 ? b[0] == 100 && (b[1] & 0xC0) == 64 : b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
        }

        // 程序的完整路径；以系统账号跑的服务（sshd、Sunshine、远程桌面）不提权打不开，只拿得到名字：返回“名字.exe”
        static string PathOf(int pid)
        {
            if (pid == 4) return "System";
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h != IntPtr.Zero)
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
                }
                finally { CloseHandle(h); }
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName + ".exe"; }
            catch (ArgumentException) { return ""; }   // 刚好退出了
            catch (InvalidOperationException) { return ""; }   // 拿到以后、读名字之前退出了
        }

        // Windows 自己的服务进程（名字看不出是什么，按端口认）
        static readonly string[] Windows = { "System", "svchost.exe", "lsass.exe", "wininit.exe", "services.exe", "spoolsv.exe" };

        // 常见的按端口起名；别的程序用它自己的说明（拿不到路径时用进程名）
        static string NameOf(string path, int port)
        {
            string exe = System.IO.Path.GetFileName(path);
            if (Windows.Contains(exe, StringComparer.OrdinalIgnoreCase))
                switch (port)
                {
                    case 3389: return L.T("远程桌面");
                    case 445: case 139: return L.T("文件和打印机共享");
                    case 5985: case 5986: return L.T("Windows 远程管理");
                    case 7680: return L.T("Windows 更新分发");
                    default: return L.T("Windows 系统服务");
                }
            switch (port)
            {
                case 22: return "SSH";
                case 5900: return "VNC";
                case 47984: case 47989: case 47990: case 48010: return L.T("Sunshine 串流");
            }
            string about = null;
            if (System.IO.Path.IsPathRooted(path))
                try { about = FileVersionInfo.GetVersionInfo(path).FileDescription; }
                catch (FileNotFoundException) { }
            return string.IsNullOrWhiteSpace(about) ? System.IO.Path.GetFileNameWithoutExtension(path) : about.Trim();
        }

        // Tailscale 网卡所在网络的类别 → 防火墙的配置文件：公用 4、专用 2、域 1
        static int Profile()
        {
            int category = AutoExit.TailscaleCategory();
            return category == 1 ? 2 : category == 2 ? 1 : 4;   // 找不到按公用（最严）算
        }

        // 读防火墙：这个配置文件上要不要照规则判断，入站的放行、阻止规则（只要 TCP 和任意协议的）
        static int Firewall(int profile, List<Rule> allow, List<Rule> block)
        {
            dynamic fw = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
            if (!fw.FirewallEnabled[profile] || fw.DefaultInboundAction[profile] == 1) return 1;
            if (fw.BlockAllInboundTraffic[profile]) return 2;
            foreach (dynamic r in fw.Rules)
            {
                if (r.Direction != 1 || !r.Enabled || (r.Profiles & profile) == 0) continue;
                string owner = r.LocalUserOwner;   // 应用商店那类应用的规则（程序、端口都是任意）只管那个应用
                if (!string.IsNullOrEmpty(owner)) continue;
                int protocol = r.Protocol;
                if (protocol != 6 && protocol != 256) continue;
                string types = r.InterfaceTypes;   // Tailscale 的网卡算有线网卡（Lan）
                if (types != null && types != "All" && !types.Contains("Lan")) continue;
                string app = r.ApplicationName;
                var rule = new Rule { App = app == null ? null : Environment.ExpandEnvironmentVariables(app), Ports = protocol == 6 ? (string)r.LocalPorts : "*", Local = r.LocalAddresses, Remote = r.RemoteAddresses };
                (r.Action == 1 ? allow : block).Add(rule);
            }
            return 0;
        }

        // 只拿到进程名的，按规则里程序的文件名比
        static bool Match(Rule r, string path, int port)
        {
            bool app = r.App == null || (System.IO.Path.IsPathRooted(path) || path == "System" ? r.App.Equals(path, StringComparison.OrdinalIgnoreCase)
                : System.IO.Path.GetFileName(r.App).Equals(path, StringComparison.OrdinalIgnoreCase));
            return app && PortIn(r.Ports, port) && Tailnet(r.Local) && Tailnet(r.Remote);
        }

        // 规则的本地端口：*、80、80,443、5000-5010，还有几个关键字（RPC 是动态 RPC 端口）
        static bool PortIn(string spec, int port)
        {
            if (string.IsNullOrEmpty(spec) || spec == "*") return true;
            foreach (var raw in spec.Split(','))
            {
                string p = raw.Trim();
                int a, b, dash = p.IndexOf('-');
                if (p == "RPC") { if (port >= 49152) return true; }
                else if (p == "RPC-EPMap") { if (port == 135) return true; }
                else if (p == "IPHTTPS") { if (port == 443) return true; }
                else if (dash > 0 && int.TryParse(p.Substring(0, dash), out a) && int.TryParse(p.Substring(dash + 1), out b)) { if (port >= a && port <= b) return true; }
                else if (int.TryParse(p, out a) && a == port) return true;
            }
            return false;
        }

        // 规则的地址条件（本地、远程都这么看）盖不盖得到 Tailscale 的地址：*，或者跟 100.64.0.0/10、fd7a:115c:a1e0::/48 有交集的地址、网段、范围。
        // “本地子网”之类的关键字不算：Tailscale 网卡的地址是 /32，别的设备都不在它的子网里
        static bool Tailnet(string spec)
        {
            if (string.IsNullOrEmpty(spec) || spec == "*") return true;
            foreach (var raw in spec.Split(','))
            {
                byte[] lo, hi;
                if (Range(raw.Trim(), out lo, out hi) && (Overlap(lo, hi, new byte[] { 100, 64, 0, 0 }, new byte[] { 100, 127, 255, 255 })
                    || Overlap(lo, hi, Ts6(0), Ts6(0xff)))) return true;
            }
            return false;
        }

        static byte[] Ts6(byte fill)
        {
            var b = Enumerable.Repeat(fill, 16).ToArray();
            new byte[] { 0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0 }.CopyTo(b, 0);
            return b;
        }

        // 一个地址、地址/掩码、地址/前缀长度、或者 起-止 → 起止两个地址
        static bool Range(string s, out byte[] lo, out byte[] hi)
        {
            lo = hi = null;
            IPAddress a, b;
            int dash = s.IndexOf('-'), slash = s.IndexOf('/');
            if (dash > 0)
            {
                if (!IPAddress.TryParse(s.Substring(0, dash), out a) || !IPAddress.TryParse(s.Substring(dash + 1), out b)) return false;
                lo = a.GetAddressBytes();
                hi = b.GetAddressBytes();
                return lo.Length == hi.Length;
            }
            if (!IPAddress.TryParse(slash > 0 ? s.Substring(0, slash) : s, out a)) return false;   // 关键字
            lo = a.GetAddressBytes();
            hi = (byte[])lo.Clone();
            if (slash < 0) return true;
            string m = s.Substring(slash + 1);
            int bits;
            byte[] mask;
            if (int.TryParse(m, out bits)) mask = Enumerable.Range(0, lo.Length).Select(i => (byte)(bits >= 8 * (i + 1) ? 0xff : bits <= 8 * i ? 0 : 0xff << (8 - (bits - 8 * i)))).ToArray();
            else if (IPAddress.TryParse(m, out b)) mask = b.GetAddressBytes();
            else return false;
            if (mask.Length != lo.Length) return false;
            for (int i = 0; i < lo.Length; i++)
            {
                lo[i] = (byte)(lo[i] & mask[i]);
                hi[i] = (byte)(hi[i] | ~mask[i]);
            }
            return true;
        }

        static bool Overlap(byte[] lo, byte[] hi, byte[] from, byte[] to)
        {
            return lo.Length == from.Length && Compare(lo, to) <= 0 && Compare(hi, from) >= 0;
        }

        static int Compare(byte[] x, byte[] y)
        {
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
            return 0;
        }
    }
}
