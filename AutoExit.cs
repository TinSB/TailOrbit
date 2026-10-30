// 出口节点自动开关：连上不信任的网络（咖啡店、机场）时自动改走选好的出口节点，回到信任的网络（家里、公司）再关掉。
// 信任哪些网络由用户在设置页标，按 Windows 给每个网络的编号记；Windows 自己的“公用/专用”不拿来判断——新网络默认就是“公用”，家里的也常是。
// 只关 TailOrbit 自己打开的；用户在这个网络上自己改过出口节点，这次连着就不再插手
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TailOrbit
{
    // 一台能当出口的设备
    class ExitNode
    {
        public string Name = "", Ip = "";
        public bool Online, InUse;
    }

    static class AutoExit
    {
        public class Net
        {
            public string Id = "", Name = "";
        }

        // 查一次的结果；出口节点都记名字，null 是没这回事
        class Outcome
        {
            public Net Net;
            public string Active;            // 查完以后 TailOrbit 开着的出口节点（IP，"" 是没有）
            public string On, Off, Fail, Lost; // 刚打开的、刚关掉的、想开没开成的、开着却断了线的
            public string Why;               // 没开成的原因（存简体中文原文，显示时再翻）
            public bool Taken;               // 用户自己改过出口节点
        }

        public static Net Current;            // 现在连着的网络（不算 Tailscale 自己那块），设置页显示用；没连网是 null
        public static event Action Changed;   // 在界面线程上触发：换了网络、开关过出口节点
        static Host host;
        static readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        static readonly DispatcherTimer soon = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        static bool checking, again, againQuiet;
        static string kept = "", warned = "";   // 用户自己改过出口节点的网络；已经提示过没开成、断了线的网络

        public static void Start(Host h)
        {
            host = h;
            var ui = Dispatcher.CurrentDispatcher;
            tick.Tick += delegate { if (Settings.AutoExitNode != "" || Settings.AutoExitActive != "") Check(false); };
            soon.Tick += delegate { soon.Stop(); Check(false); };
            // 换了网络：Windows 要过几秒才认出是哪个网络
            NetworkChange.NetworkAddressChanged += delegate { ui.BeginInvoke(new Action(() => { soon.Stop(); soon.Start(); })); };
            tick.Start();
            Check(false);
        }

        // quiet：设置页上点出来的，人就在看，不弹通知
        public static void Check(bool quiet)
        {
            if (checking) { again = true; againQuiet = quiet; return; }
            checking = true;
            string target = Settings.AutoExitNode, active = Settings.AutoExitActive, skip = kept;
            var trusted = new HashSet<string>(Settings.TrustedNets);
            Task.Run(() => Decide(target, active, trusted, skip)).ContinueWith(t =>
            {
                checking = false;
                if (t.IsFaulted) Log.Write(t.Exception.GetBaseException());
                else Apply(t.Result, quiet);
                if (!again) return;
                again = false;
                Check(againQuiet);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static Outcome Decide(string target, string active, HashSet<string> trusted, string skip)
        {
            var nets = Uplinks();
            var o = new Outcome { Net = nets.FirstOrDefault(), Active = active };
            if (target == "" && active == "") return o;
            CmdResult st;
            try { st = Tailscale.Run("status --json"); }
            catch (Win32Exception) { return o; }   // 没装 Tailscale
            var status = st.Code == 0 ? Tailscale.Json(st.Out) : null;
            if ((Tailscale.Get(status, "BackendState") as string) != "Running") return o;   // Tailscale 没在跑、没登录：等它起来，别把开着的出口节点当成被用户关了
            var exits = Exits(status);
            var used = exits.FirstOrDefault(x => x.InUse);
            string use = used == null ? "" : used.Ip;
            if (active != "" && use != active) { o.Active = ""; o.Taken = true; return o; }   // 用户自己改过：不再归 TailOrbit 管
            if (nets.Count == 0) return o;         // 没连网：等连上再说
            bool safe = nets.Any(n => trusted.Contains(n.Id));
            if (active != "")
            {
                if (target == "" || safe)          // 关了自动使用，或者回到了信任的网络
                {
                    if (Tailscale.Run("set --exit-node=").Code == 0) { o.Active = ""; if (safe && target != "") o.Off = used.Name; }
                }
                else if (!used.Online) o.Lost = used.Name;
                return o;
            }
            if (target == "" || safe || use != "" || o.Net.Id == skip) return o;   // 已经在用的出口节点是用户自己选的，不动
            var want = exits.FirstOrDefault(x => x.Ip == target);
            if (want == null || !want.Online) { o.Fail = NameOf(exits, target); o.Why = want == null ? "该设备已不再作为出口节点，仍使用直接连接。" : "该设备已离线，仍使用直接连接。"; return o; }
            var r = Tailscale.Run("set --exit-node=" + target);
            if (r.Code == 0) { o.Active = target; o.On = want.Name; }
            else { o.Fail = want.Name; o.Why = Tailscale.FirstLine(r.Err, "tailscale set 执行失败"); }
            return o;
        }

        static void Apply(Outcome o, bool quiet)
        {
            string id = o.Net == null ? "" : o.Net.Id, was = Current == null ? "" : Current.Id;
            Current = o.Net;
            bool moved = id != was || o.Active != Settings.AutoExitActive;
            if (o.Active != Settings.AutoExitActive) Settings.AutoExitActive = o.Active;
            if (o.Taken) kept = id;
            else if (id != "" && id != kept) kept = "";
            if (o.Fail == null && o.Lost == null) warned = "";   // 没事了：下次再出事再提示
            if (!quiet)
            {
                string net = o.Net == null ? "" : o.Net.Name;
                Action open = () => host.ShowPage("settings");
                if (o.On != null) host.Notify(L.T("已切换到出口节点 {0}", o.On), L.T("{0} 不是信任的网络。", net), false, open);
                if (o.Off != null) host.Notify(L.T("已停止使用出口节点"), L.T("已连接到信任的网络 {0}。", net), false, open);
                if ((o.Fail != null || o.Lost != null) && warned != id)
                {
                    warned = id;
                    if (o.Fail != null) host.Notify(L.T("无法切换到出口节点 {0}", o.Fail), L.T(o.Why), false, open);
                    else host.Notify(L.T("出口节点 {0} 已离线", o.Lost), L.T("可能无法访问互联网，可以在“设置”页改为直接连接。"), false, open);
                }
            }
            if (!moved) return;
            var h = Changed;
            if (h != null) h();
        }

        static string NameOf(List<ExitNode> exits, string ip)
        {
            var x = exits.FirstOrDefault(e => e.Ip == ip);
            return x == null ? ip : x.Name;
        }

        // status --json 里能当出口的设备（声明了、后台也批准了的），在线的在前
        public static List<ExitNode> Exits(Dictionary<string, object> status)
        {
            var list = new List<ExitNode>();
            var peers = Tailscale.Get(status, "Peer") as Dictionary<string, object>;
            if (peers != null)
                foreach (var p in peers.Values.OfType<Dictionary<string, object>>())
                {
                    if (!true.Equals(Tailscale.Get(p, "ExitNodeOption"))) continue;
                    var ips = Tailscale.Get(p, "TailscaleIPs") as object[];
                    string dns = ((Tailscale.Get(p, "DNSName") as string) ?? "").TrimEnd('.');
                    list.Add(new ExitNode
                    {
                        Name = dns.Length > 0 ? dns.Split('.')[0] : (Tailscale.Get(p, "HostName") as string) ?? "",
                        Ip = ips != null && ips.Length > 0 ? (ips[0] as string) ?? "" : "",
                        Online = true.Equals(Tailscale.Get(p, "Online")),
                        InUse = true.Equals(Tailscale.Get(p, "ExitNode"))
                    });
                }
            return list.OrderBy(x => x.Online ? 0 : 1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // 现在连着的网络：有默认网关的网卡上的（Tailscale 自己那块不算）。网络和网卡的编号要经 COM 接口本身取，按名字调取不出来
        static List<Net> Uplinks()
        {
            var nics = new HashSet<string>(NetworkInterface.GetAllNetworkInterfaces().Where(Uplink).Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
            var list = new List<Net>();
            dynamic nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));   // Windows 的网络列表
            foreach (object item in nlm.GetNetworkConnections())
            {
                var c = (INetworkConnection)item;
                if (!nics.Contains(c.GetAdapterId().ToString("B"))) continue;
                var n = (INetwork)c.GetNetwork();
                string id = n.GetNetworkId().ToString();
                if (list.All(x => x.Id != id)) list.Add(new Net { Id = id, Name = n.GetName() });
            }
            return list;
        }

        static bool Uplink(NetworkInterface n)
        {
            return n.OperationalStatus == OperationalStatus.Up && n.Description.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) < 0
                && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
        }

        // 本机所在的局域网网段（共享局域网用）：联网那块网卡上的私有 IPv4 地址按前缀长度算，比如 192.168.1.0/24；没有是 ""
        public static string Subnet()
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(Uplink))
                foreach (var u in n.GetIPProperties().UnicastAddresses)
                {
                    var b = u.Address.GetAddressBytes();
                    int bits = u.PrefixLength;
                    if (b.Length != 4 || bits < 8 || bits > 30 || !(b[0] == 10 || b[0] == 172 && (b[1] & 0xF0) == 16 || b[0] == 192 && b[1] == 168)) continue;
                    uint ip = (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3], net = ip & (uint.MaxValue << (32 - bits));
                    return (net >> 24) + "." + (net >> 16 & 255) + "." + (net >> 8 & 255) + "." + (net & 255) + "/" + bits;
                }
            return "";
        }

        // Tailscale 那块网卡所在网络的类别：0 公用、1 专用、2 域；找不到是 -1（本机开放了什么按它查防火墙）
        public static int TailscaleCategory()
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Description.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0);
            if (nic == null) return -1;
            dynamic nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
            foreach (object item in nlm.GetNetworkConnections())
            {
                var c = (INetworkConnection)item;
                if (c.GetAdapterId().ToString("B").Equals(nic.Id, StringComparison.OrdinalIgnoreCase)) return ((dynamic)c.GetNetwork()).GetCategory();   // INetwork 后面那几个方法没声明，经 IDispatch 调
            }
            return -1;
        }

        // 只声明到用得着的那个方法为止，顺序照 netlistmgr.h
        [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface INetworkConnection
        {
            [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork();
            bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
            bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
            int GetConnectivity();
            Guid GetConnectionId();
            Guid GetAdapterId();
        }

        [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface INetwork
        {
            [return: MarshalAs(UnmanagedType.BStr)] string GetName();
            void SetName([MarshalAs(UnmanagedType.BStr)] string name);
            [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
            void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
            Guid GetNetworkId();
        }
    }
}
