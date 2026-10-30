// 网络记录：每分钟看一次 tailscale status，记下每台设备这一分钟收发了多少、走的直连还是中继；正在通信的顺带 ping 一次记下延迟。
// 存在 %LOCALAPPDATA%\TailOrbit\net\年-月-日.tsv（按本地日期分文件，只追加、从不删），网络页的流量账本和设备卡片里的延迟曲线都从这里读。
// 每行：Unix 秒、设备 ID、这一分钟收的字节、发的字节、路（d 直连，别的是中继所在地的代码或 peer，空是没在通信）、延迟毫秒（-1 没测）、在线 1/0；
// "#" 开头的行记设备的名字和系统。正在用的设备从直连掉到中继、或者延迟连着两次比平时高出一截，提醒一次；恢复正常后再变差才再提醒
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TailOrbit
{
    static class NetLog
    {
        // 一台设备一段时间里的收发，走直连的和走中继的分开记
        public class Usage
        {
            public string Id = "", Name = "", Os = "";
            public long RxDirect, TxDirect, RxRelay, TxRelay;
            public long Rx { get { return RxDirect + RxRelay; } }
            public long Tx { get { return TxDirect + TxRelay; } }
            public long Total { get { return Rx + Tx; } }
            public long Relay { get { return RxRelay + TxRelay; } }
        }

        // 记录里的一行
        public class Sample
        {
            public DateTime At;        // UTC
            public long Bytes;
            public string Link = "";
            public int Ms = -1;
            public bool Online;
        }

        // 这一次读到的、正在通信的设备（给变差提醒用）
        class Seen
        {
            public string Id, Name, Link;
            public int Ms;
        }

        // 每台设备上一次的样子
        class Last
        {
            public long Rx = -1, Tx = -1;
            public bool Online;
            public string Link = "", Named = "";
            public DateTime Day;                                   // Named 写在哪天的文件里
            public readonly List<int> Direct = new List<int>();     // 最近走直连时测到的延迟（算“平时”）
            public int Relayed, Slow;
            public bool Warned;
            public DateTime DirectAt;
        }

        static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailOrbit", "net");
        static readonly Regex Pong = new Regex(@"^pong from .* via (\S+) in ([\d.]+)ms");
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static readonly Dictionary<string, Last> last = new Dictionary<string, Last>();
        static readonly Dictionary<DateTime, Dictionary<string, Usage>> days = new Dictionary<DateTime, Dictionary<string, Usage>>();   // 读过的日子；今天的边写边加
        static readonly object gate = new object(), io = new object();   // gate 管 last；io 管记录文件和读进来的日子
        static readonly Dictionary<DateTime, string> unsaved = new Dictionary<DateTime, string>();   // 还没写进文件的（按哪天的文件分）
        static Host host;
        static bool busy;

        public static void Start(Host h)
        {
            host = h;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            t.Tick += delegate { Tick(); };
            t.Start();
            Tick();
        }

        static void Tick()
        {
            if (busy) return;
            busy = true;
            Task.Run(() => Record(DateTime.UtcNow)).ContinueWith(t =>
            {
                busy = false;
                if (t.IsFaulted) Log.Write(t.Exception.GetBaseException());
                else Warn(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 读一次、记一次。Tailscale 没在正常运行时不记（这时谁都像离线）
        static List<Seen> Record(DateTime now)
        {
            CmdResult r;
            try { r = Tailscale.Run("status --json"); }
            catch (Win32Exception) { return null; }   // 没装 Tailscale
            var root = r.Code == 0 ? Tailscale.Json(r.Out) : null;
            if (root == null || !"Running".Equals(Tailscale.Get(root, "BackendState"))) return null;
            var peers = Tailscale.Get(root, "Peer") as Dictionary<string, object>;
            var seen = new List<Seen>();
            if (peers == null) return seen;
            DateTime day = now.ToLocalTime().Date;
            long unix = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var text = new StringBuilder();
            foreach (var p in peers.Values.OfType<Dictionary<string, object>>())
            {
                string ip = Tailscale.FirstIp(p), id = (Tailscale.Get(p, "ID") as string) ?? "";
                if (id == "") id = ip;
                if (id == "") continue;
                string name = Clean(Tailscale.ShortName(p)), os = Clean((Tailscale.Get(p, "OS") as string) ?? "");
                bool online = true.Equals(Tailscale.Get(p, "Online")), active = true.Equals(Tailscale.Get(p, "Active"));
                long rx = Count(p, "RxBytes"), tx = Count(p, "TxBytes");
                // 正在通信的才说得出走哪条路；顺带 ping 一次，ping 回来的路更准
                string link = !active ? "" : ((Tailscale.Get(p, "CurAddr") as string) ?? "") != "" ? "d" : Clean((Tailscale.Get(p, "Relay") as string) ?? "");
                int ms = -1;
                if (active && online && ip != "") Ping(ip, ref link, ref ms);
                Last l;
                lock (gate) if (!last.TryGetValue(id, out l)) last[id] = l = new Last();
                // 头一次只记个底（之前的不知道是哪天传的）；计数变小是 Tailscale 重新开始计了
                long drx = l.Rx < 0 ? 0 : rx >= l.Rx ? rx - l.Rx : rx;
                long dtx = l.Tx < 0 ? 0 : tx >= l.Tx ? tx - l.Tx : tx;
                l.Rx = rx;
                l.Tx = tx;
                if (link == "" && drx + dtx > 0) link = l.Link != "" ? l.Link : "d";   // 这一分钟传过、这会儿停了：算刚才那条路
                if (link != "") l.Link = link;
                bool fresh = l.Day != day;
                if (fresh || l.Named != name + "\t" + os)
                {
                    text.Append("#\t" + id + "\t" + name + "\t" + os + "\n");
                    l.Named = name + "\t" + os;
                    l.Day = day;
                }
                if (fresh || drx + dtx > 0 || ms >= 0 || online != l.Online)
                    text.Append(unix.ToString(CultureInfo.InvariantCulture) + "\t" + id + "\t" + drx.ToString(CultureInfo.InvariantCulture) + "\t" + dtx.ToString(CultureInfo.InvariantCulture)
                        + "\t" + link + "\t" + ms.ToString(CultureInfo.InvariantCulture) + "\t" + (online ? "1" : "0") + "\n");
                l.Online = online;
                if (active && online && link != "") seen.Add(new Seen { Id = id, Name = name, Link = link, Ms = ms });
            }
            if (text.Length > 0) Append(day, text.ToString());
            return seen;
        }

        static long Count(Dictionary<string, object> p, string key)
        {
            return Convert.ToInt64(Tailscale.Get(p, key) ?? 0L, CultureInfo.InvariantCulture);
        }

        static void Ping(string ip, ref string link, ref int ms)
        {
            var r = Tailscale.Run("ping --c 1 --timeout 2s " + ip);
            foreach (var raw in r.Out.Split('\n'))
            {
                var m = Pong.Match(raw.Trim());
                if (!m.Success) continue;
                string via = m.Groups[1].Value;
                link = via.StartsWith("DERP(") ? via.Substring(5).TrimEnd(')') : via.StartsWith("peer-relay") ? "peer" : "d";
                ms = (int)Math.Round(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                return;
            }
        }

        static string Clean(string s)
        {
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        static string PathOf(DateTime day)
        {
            return Path.Combine(Folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".tsv");
        }

        // 追加写。写文件、读文件、改读进来的日子用同一把锁：一边在读那天的文件、一边写进新的一分钟时，这一分钟不会两边都漏掉。
        // 文件被别的程序占着（比如用 Excel 开着）写不进去的先留着，下一分钟连同新的一起写，一分钟都不丢
        static void Append(DateTime day, string text)
        {
            lock (io)
            {
                string before;
                unsaved[day] = unsaved.TryGetValue(day, out before) ? before + text : text;
                foreach (var d in unsaved.Keys.ToList())
                {
                    string all = unsaved[d];
                    try
                    {
                        Directory.CreateDirectory(Folder);
                        using (var f = new FileStream(PathOf(d), FileMode.Append, FileAccess.Write, FileShare.Read))
                        {
                            var b = Utf8.GetBytes(all);
                            f.Write(b, 0, b.Length);
                        }
                    }
                    catch (IOException e)
                    {
                        if (all == text) Log.Write(e);   // 占着的头一分钟记一次就够
                        continue;
                    }
                    unsaved.Remove(d);
                    Dictionary<string, Usage> map;
                    if (days.TryGetValue(d, out map)) Add(all, map);   // 那天已经读进来了：跟着加
                }
            }
        }

        static string Read(DateTime day)
        {
            string p = PathOf(day);
            lock (io)
            {
                if (!File.Exists(p)) return "";
                using (var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(f, Utf8)) return r.ReadToEnd();
            }
        }

        // 按行加进每台设备的合计
        static void Add(string text, Dictionary<string, Usage> map)
        {
            foreach (var raw in text.Split('\n'))
            {
                var f = raw.TrimEnd('\r').Split('\t');
                if (f.Length >= 4 && f[0] == "#")
                {
                    var n = Of(map, f[1]);
                    n.Name = f[2];
                    n.Os = f[3];
                    continue;
                }
                long rx, tx;
                if (f.Length < 7 || !long.TryParse(f[2], out rx) || !long.TryParse(f[3], out tx) || rx + tx == 0) continue;
                var u = Of(map, f[1]);
                if (f[4] == "d" || f[4] == "") { u.RxDirect += rx; u.TxDirect += tx; }
                else { u.RxRelay += rx; u.TxRelay += tx; }
            }
        }

        static Usage Of(Dictionary<string, Usage> map, string id)
        {
            Usage u;
            if (!map.TryGetValue(id, out u)) map[id] = u = new Usage { Id = id };
            return u;
        }

        // 某一天每台设备的合计：读一次就记住（调用的地方拿着 io）
        static Dictionary<string, Usage> Day(DateTime day)
        {
            Dictionary<string, Usage> map;
            if (days.TryGetValue(day, out map)) return map;
            map = new Dictionary<string, Usage>();
            Add(Read(day), map);
            days[day] = map;
            return map;
        }

        // from 到 to 这几天（本地日期，含首尾）每台设备的合计；名字用最近那天记的
        public static Dictionary<string, Usage> Range(DateTime from, DateTime to)
        {
            var all = new Dictionary<string, Usage>();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                lock (io)
                    foreach (var u in Day(d).Values)
                    {
                        var x = Of(all, u.Id);
                        if (u.Name != "") { x.Name = u.Name; x.Os = u.Os; }
                        x.RxDirect += u.RxDirect;
                        x.TxDirect += u.TxDirect;
                        x.RxRelay += u.RxRelay;
                        x.TxRelay += u.TxRelay;
                    }
            return all;
        }

        // 这台设备最近 n 天每天的合计，最后一个是今天
        public static List<Usage> Daily(string id, int n)
        {
            var list = new List<Usage>();
            for (int i = n - 1; i >= 0; i--)
            {
                Usage u;
                var d = DateTime.Today.AddDays(-i);
                list.Add(Range(d, d).TryGetValue(id, out u) ? u : new Usage { Id = id });   // 拿的是一份副本，后面接着记也不会改到它
            }
            return list;
        }

        // 这台设备最近这段时间记下的行，从旧到新
        public static List<Sample> Recent(string id, TimeSpan span)
        {
            var since = DateTime.UtcNow - span;
            var list = new List<Sample>();
            for (var d = since.ToLocalTime().Date; d <= DateTime.Today; d = d.AddDays(1))
                foreach (var raw in Read(d).Split('\n'))
                {
                    var f = raw.TrimEnd('\r').Split('\t');
                    long unix, rx, tx;
                    int ms;
                    if (f.Length < 7 || f[1] != id || !long.TryParse(f[0], out unix) || !long.TryParse(f[2], out rx) || !long.TryParse(f[3], out tx) || !int.TryParse(f[5], out ms)) continue;
                    var at = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix);
                    if (at >= since) list.Add(new Sample { At = at, Bytes = rx + tx, Link = f[4], Ms = ms, Online = f[6] == "1" });
                }
            return list;
        }

        // ---------- 变差提醒 ----------

        // 平时：这台最近走直连时延迟的中位数（少于 10 次不算）。直连掉到中继要连着两次、而且半小时内走过直连（一直走中继的不算变差）
        static void Warn(List<Seen> list)
        {
            if (list == null) return;
            foreach (var s in list)
            {
                Last l;
                lock (gate) if (!last.TryGetValue(s.Id, out l)) continue;
                if (s.Link == "d")
                {
                    l.Relayed = 0;
                    l.DirectAt = DateTime.UtcNow;
                    if (s.Ms < 0) continue;
                    int usual = Usual(l.Direct);
                    bool slow = usual > 0 && s.Ms > Math.Max(usual * 2, usual + 50);
                    if (!slow)
                    {
                        l.Direct.Add(s.Ms);
                        if (l.Direct.Count > 120) l.Direct.RemoveAt(0);
                        l.Slow = 0;
                        l.Warned = false;   // 恢复正常：下次变差再提醒
                        continue;
                    }
                    if (++l.Slow >= 2 && !l.Warned) Tell(l, L.T("到 {0} 的延迟变高了", s.Name), L.T("现在 {0} ms，平时约 {1} ms。", s.Ms, usual));
                    continue;
                }
                l.Slow = 0;
                if (l.Relayed == 0 && (DateTime.UtcNow - l.DirectAt).TotalMinutes > 30) continue;
                if (++l.Relayed >= 2 && !l.Warned)
                    Tell(l, L.T("{0} 改走中继了", s.Name), (s.Link == "peer" ? L.T("现在经其他设备中继") : L.T("现在经{0}中继", NetPage.City(s.Link))) + (s.Ms >= 0 ? L.T("，延迟 {0} ms。", s.Ms) : L.T("。")));
            }
        }

        static int Usual(List<int> direct)
        {
            if (direct.Count < 10) return -1;
            var sorted = direct.OrderBy(x => x).ToList();
            return sorted[sorted.Count / 2];
        }

        static void Tell(Last l, string title, string text)
        {
            l.Warned = true;
            if (Settings.NetAlerts) host.Notify(title, text, false, () => host.ShowPage("net"));
        }
    }
}
