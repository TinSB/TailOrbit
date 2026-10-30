// Tailscale 本身：连着没有、登录的是哪个账号、本机密钥哪天到期、Tailscale 自己报的问题。每 10 秒读一次 status，托盘菜单、设置页、星网都看这一份。
// 连接、断开、登录、换账号、退出登录也在这里，全是 tailscale 命令。Tailscale 自己的托盘程序（tailscale-ipn）没开着时，
// 替它把要紧的事弹成通知：要登录了、密钥快到期、Tailscale 报了问题、被断开了
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace TailOrbit
{
    static class Account
    {
        public class State
        {
            public string Backend = "";   // Running、Stopped（断开了）、Starting、NeedsLogin、NeedsMachineAuth（等管理员批准）；"" 是读不到（Tailscale 没在跑、没装）
            public bool HaveKey;          // 登录过：没登录、退出了是 false，密钥过期了是 true
            public string AuthUrl = "", Login = "";
            public DateTime? Expiry;      // 本机密钥哪天到期（本地时间）；关了到期是 null
            public List<string> Health = new List<string>();
            public bool Ipn;              // Tailscale 自己的托盘程序开着
        }

        public class Profile
        {
            public string Id = "", Login = "", Tailnet = "";
            public bool Selected;
        }

        public const string Machines = "https://login.tailscale.com/admin/machines";
        public static string IpnName = "tailscale-ipn";   // Tailscale 自己的托盘程序
        public static Action<string> Browse = url => Ui.Launch(() => Process.Start(url));
        static readonly Regex Link = new Regex(@"https://\S+");

        public static State Now = new State();
        public static event Action Changed;   // 状态、账号、登录中与否变了；在界面线程上
        public static bool Signing { get; private set; }
        static Host host;
        static Dispatcher ui;
        static State prev;
        static DateTime last, quietUntil;
        static bool reading, again, loginTold;
        static volatile bool cancel;
        static volatile Process signer;   // 正在跑的 tailscale login：取消时结束它
        static volatile string url;       // 这次登录打开过的网址
        static readonly Dictionary<string, DateTime> since = new Dictionary<string, DateTime>();   // Tailscale 报的问题：从什么时候起一直在
        static readonly HashSet<string> told = new HashSet<string>();

        public static void Start(Host h)
        {
            host = h;
            ui = Dispatcher.CurrentDispatcher;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            t.Tick += delegate { Refresh(); };
            t.Start();
            Refresh();
        }

        // 马上再读一次（自己连接、断开、换了账号，事件流里看到状态变了）；正在读就读完再来一次
        public static void Refresh()
        {
            if (reading) { again = true; return; }
            reading = true;
            Task.Run(() => Read()).ContinueWith(t =>
            {
                reading = false;
                if (!t.IsFaulted) Apply(t.Result, DateTime.Now);
                if (again) { again = false; Refresh(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 后台线程上跑完命令：回界面线程刷新
        static void Poke()
        {
            if (ui != null) ui.BeginInvoke(new Action(Refresh));
        }

        public static State Read()
        {
            var s = new State { Ipn = IpnOpen() };
            CmdResult r;
            try { r = Tailscale.Run("status --json"); }
            catch (Win32Exception) { return s; }   // 没装 Tailscale
            var root = r.Code == 0 ? Tailscale.Json(r.Out) : null;
            if (root == null) return s;
            s.Backend = (Tailscale.Get(root, "BackendState") as string) ?? "";
            s.HaveKey = true.Equals(Tailscale.Get(root, "HaveNodeKey"));
            s.AuthUrl = (Tailscale.Get(root, "AuthURL") as string) ?? "";
            var self = Tailscale.Get(root, "Self") as Dictionary<string, object>;
            DateTime at;
            if (DateTime.TryParse(Tailscale.Get(self, "KeyExpiry") as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)) s.Expiry = at.ToLocalTime();
            var users = Tailscale.Get(root, "User") as Dictionary<string, object>;
            var me = Tailscale.Get(users, Convert.ToString(Tailscale.Get(self, "UserID"), CultureInfo.InvariantCulture)) as Dictionary<string, object>;
            s.Login = (Tailscale.Get(me, "LoginName") as string) ?? "";
            var h = Tailscale.Get(root, "Health") as object[];
            if (h != null) s.Health = h.OfType<string>().Where(x => x.Trim() != "").ToList();
            return s;
        }

        static void Apply(State s, DateTime at)
        {
            bool changed = Key(s) != Key(Now);
            Now = s;
            Notice(s, at);
            if (changed) Raise();
        }

        static string Key(State s)
        {
            return s.Backend + "|" + s.HaveKey + "|" + s.Login;
        }

        static void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }

        // 托盘菜单按这个判断备好的那份还能不能用
        public static string MenuKey { get { return Now.Backend + "|" + Now.HaveKey + "|" + Signing; } }

        // ---------- 说法和按钮 ----------

        public static string Describe(string backend, bool haveKey)
        {
            switch (backend)
            {
                case "Running": return L.T("已连接");
                case "Stopped": return L.T("已断开");
                case "Starting": return L.T("正在连接…");
                case "NeedsLogin": return haveKey ? L.T("登录已过期") : L.T("未登录");
                case "NeedsMachineAuth": return L.T("等待管理员批准");
                default: return L.T("Tailscale 未运行");
            }
        }

        // 星网、网络页、设置页上给的那个按钮；null 是没有按钮
        public static string FixLabel(string backend, bool haveKey)
        {
            if (Signing) return L.T("正在等待登录…");
            switch (backend)
            {
                case "Stopped": return L.T("连接");
                case "NeedsLogin": return haveKey ? L.T("重新登录") : L.T("登录");
                case "NeedsMachineAuth": return L.T("打开管理控制台");
                default: return null;
            }
        }

        // 按下那个按钮：断开着就连上，没登录就登录，等批准就打开管理控制台。返回要告诉用户的原因（null 没事，"" 是取消了）
        public static async Task<string> Fix(string backend)
        {
            if (backend == "NeedsLogin" || Signing) return await SignIn(false);
            if (backend == "Stopped") return await Task.Run(() => Connect());
            if (backend == "NeedsMachineAuth") Browse(Machines);
            return null;
        }

        // ---------- 连接、断开 ----------

        // 连上（登录着、断开了的时候）。不带参数的 up 只是把网络重新打开、不改设置；带了任何参数，它就要求把所有改过的设置都写上，少一个就报错。
        // 它等到连上才退出：最多等 30 秒，还没连上就交给定时刷新去看
        public static string Connect()
        {
            Quiet();
            var r = Run("up");
            Poke();
            return r.Code == 0 || r.Code == -1 ? null : Tailscale.FirstLine(r.Err, L.T("连接失败"));
        }

        public static string Disconnect()
        {
            Quiet();
            var r = Run("down");
            Poke();
            return r.Code == 0 ? null : Tailscale.FirstLine(r.Err, L.T("断开失败"));
        }

        // up、down、switch、logout：等它做完，最多 30 秒。Tailscale 正在更新、一时找不到 tailscale.exe 也当成失败说出来，
        // 不往外抛（不然点的那个按钮一直停在“正在…”）
        static CmdResult Run(string args)
        {
            try { return Tailscale.Run(args, 30000); }
            catch (Win32Exception) { return new CmdResult { Code = 1, Out = "", Err = L.T("未安装 Tailscale") }; }
        }

        // 自己刚连接、断开、换账号、退出登录：这一会儿状态来回变，不当成别人干的去提醒
        static void Quiet()
        {
            quietUntil = DateTime.Now.AddSeconds(60);
        }

        // ---------- 登录 ----------

        // 登录。add：再添一个账号——tailscale login 总是先换到一个空账号再登录，登录过的电脑上它就是“添加账号”，没成（取消、超时）就换回原来那个。
        // 不是添加、这台电脑又登录过（密钥过期了）：像 Tailscale 自己的托盘那样叫它重新登录当前账号，设置都不动
        // （tailscale up --force-reauth 也行，可它要求把所有改过的设置都写上，少一个就报错）。
        // 网址出来就用浏览器打开，等到连上，最多 10 分钟。返回要告诉用户的原因：null 登录好了，"" 是取消了
        public static async Task<string> SignIn(bool add)
        {
            if (Signing)
            {
                var u = url;   // 已经在等了：再点一下是想重新打开那个网页
                if (u != null) Browse(u);
                return null;
            }
            Signing = true;
            cancel = false;
            url = null;
            Raise();
            bool reauth = !add && Now.HaveKey;
            string why;
            try { why = await Task.Run(() => reauth ? Reauth() : Login(add)); }
            catch (Win32Exception) { why = L.T("未安装 Tailscale"); }
            Signing = false;
            url = null;
            Quiet();
            Raise();
            Refresh();
            return why;
        }

        public static void Cancel()
        {
            cancel = true;
            var p = signer;
            if (p == null) return;
            try { p.Kill(); }
            catch (InvalidOperationException) { }   // 刚好自己退出了
        }

        static string Login(bool add)
        {
            string back = null;
            if (add)
            {
                var list = Profiles();
                var cur = list == null ? null : list.FirstOrDefault(p => p.Selected);
                if (cur != null) back = cur.Id;
            }
            var err = new StringBuilder();
            bool asked = false;
            using (var p = ChildJob.Start(Tailscale.Psi("login")))
            {
                signer = p;
                // To authenticate, visit:（空行）<网址>；要管理员批准时是另一句和管理控制台的网址，那个不打开
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    err.AppendLine(e.Data);
                    if (e.Data.Contains("To authenticate, visit:")) asked = true;
                    else if (e.Data.Contains("To approve your machine")) asked = false;
                    var m = Link.Match(e.Data);
                    if (asked && m.Success && url == null) { url = m.Value; Browse(m.Value); }
                };
                p.BeginErrorReadLine();
                p.StandardOutput.ReadToEndAsync();
                bool done = p.WaitForExit(10 * 60 * 1000);
                signer = null;
                if (!done) Cancel(p);
                p.WaitForExit();
                // 出错时原因在最后一行（前面是打出来的网址）
                string last = err.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.Contains("https://") && !l.StartsWith("To authenticate"));
                string why = cancel ? "" : !done ? L.T("登录超时，请重试。") : p.ExitCode == 0 ? null : last ?? L.T("登录失败");
                if (why != null && back != null) Run("switch " + back);
                return why;
            }
        }

        static void Cancel(Process p)
        {
            try { p.Kill(); }
            catch (InvalidOperationException) { }
        }

        // 重新登录当前账号：叫 Tailscale 去要登录网址（和它自己托盘上的“登录”一样），网址出现在 status 里就打开。
        // 只打开 https 网址：这串字是控制服务器给的，不是网址的不交给系统去“打开”
        static string Reauth()
        {
            string before = Read().AuthUrl;
            var r = Tailscale.Run("debug localapi POST login-interactive", 30000);
            if (r.Code != 0) return Tailscale.FirstLine(string.Join("\n", r.Err.Split('\n').Where(l => !l.StartsWith("#"))), L.T("登录失败"));
            // 网址要后台去跟 Tailscale 要，一般一两秒；原来那个还没过期时它就用原来那个，等 3 秒没换就是它
            for (int i = 0; i < 1260 && !cancel; i++)
            {
                Thread.Sleep(500);
                var s = Read();
                if (s.Backend == "Running") return null;
                if (url == null && s.AuthUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && (s.AuthUrl != before || i >= 6)) { url = s.AuthUrl; Browse(url); }
                if (url == null && i >= 60) return L.T("没有拿到登录网址，请稍后再试。");
            }
            return cancel ? "" : L.T("登录超时，请重试。");
        }

        // ---------- 账号 ----------

        // 这台电脑上登录过的账号（tailscale switch --list）；读不到是 null
        public static List<Profile> Profiles()
        {
            var r = Tailscale.Run("switch --list --json");
            if (r.Code != 0) return null;
            int i = r.Out.IndexOf('[');
            object[] list;
            try { list = i < 0 ? new object[0] : new JavaScriptSerializer().DeserializeObject(r.Out.Substring(i)) as object[]; }
            catch (ArgumentException) { return null; }
            return (list ?? new object[0]).OfType<Dictionary<string, object>>().Select(o => new Profile
            {
                Id = Convert.ToString(Tailscale.Get(o, "id"), CultureInfo.InvariantCulture) ?? "",
                Login = (Tailscale.Get(o, "account") as string) ?? "",
                Tailnet = (Tailscale.Get(o, "tailnet") as string) ?? "",
                Selected = true.Equals(Tailscale.Get(o, "selected")),
            }).ToList();
        }

        // 换到另一个账号：它等到连上（或说要登录）才退出
        public static string Switch(string id)
        {
            if (!Regex.IsMatch(id, "^[A-Za-z0-9]+$")) return L.T("切换失败");
            Quiet();
            var r = Run("switch " + id);
            Poke();
            return r.Code == 0 ? null : Tailscale.FirstLine(r.Err, Tailscale.FirstLine(r.Out, L.T("切换失败")));
        }

        // 退出登录：这台电脑离开网络，本机的密钥作废，账号从列表里去掉
        public static string Logout()
        {
            Quiet();
            var r = Run("logout");
            Poke();
            return r.Code == 0 ? null : Tailscale.FirstLine(r.Err, L.T("退出登录失败"));
        }

        // ---------- 替 Tailscale 提醒 ----------

        // Tailscale 自己的托盘程序开着吗（只看这个 Windows 用户的）：开着它自己会提醒、自己取收到的文件
        public static bool IpnOpen()
        {
            int me = Process.GetCurrentProcess().SessionId;
            var list = Process.GetProcessesByName(IpnName);
            bool open = list.Any(p => p.SessionId == me);
            foreach (var p in list) p.Dispose();
            return open;
        }

        // 第一次读只记下来；两次之间隔得太久（多半是刚睡醒）也只记不报
        static void Notice(State s, DateTime at)
        {
            bool woke = last != DateTime.MinValue && (at - last).TotalSeconds > 90;
            last = at;
            var was = prev;
            prev = s;
            if (s.Backend != "NeedsLogin") loginTold = false;
            var issues = s.Backend == "Running" ? s.Health : new List<string>();
            foreach (var gone in since.Keys.Where(k => !issues.Contains(k)).ToList()) { since.Remove(gone); told.Remove(gone); }
            foreach (var x in issues) if (woke || !since.ContainsKey(x)) since[x] = at;
            if (was == null || woke || s.Ipn) return;
            bool quiet = Signing || at < quietUntil;
            // 自己刚退出登录、取消登录、换到要登录的账号（quiet 里看到的）：当成已经知道了，那一分钟过去也不再弹
            if (s.Backend == "NeedsLogin" && !loginTold)
            {
                loginTold = true;
                if (!quiet) host.Notify(s.HaveKey ? L.T("Tailscale 登录已过期") : L.T("Tailscale 需要登录"), L.T("点此在浏览器中登录。"), false, () => Do(() => SignIn(false)));
            }
            if (was.Backend == "Running" && !quiet)
            {
                if (s.Backend == "Stopped") host.Notify(L.T("Tailscale 已断开"), L.T("这台电脑已离开 Tailscale 网络，点此重新连接。"), false, () => Do(() => Task.Run(() => Connect())));
                else if (s.Backend == "") host.Notify(L.T("Tailscale 没有在运行"), L.T("Tailscale 的后台服务停止了，收发文件、分享和剪贴板互通暂时都用不了。"));
            }
            if (s.Backend == "Running" && s.Expiry != null)
            {
                var left = s.Expiry.Value - at;
                string day = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (left > TimeSpan.Zero && left <= TimeSpan.FromDays(7) && Settings.KeyNotice != day)
                {
                    Settings.KeyNotice = day;   // 一天最多提醒一次
                    host.Notify(L.T("这台电脑的 Tailscale 密钥 {0}后到期", Left(left)), L.T("到期后会断开，需要重新登录。点此打开网络页，可以关闭密钥到期。"), false, () => host.ShowPage("net"));
                }
            }
            var fresh = issues.Where(x => (at - since[x]).TotalSeconds >= 60 && !told.Contains(x)).ToList();   // 一闪而过的不报
            foreach (var x in fresh) told.Add(x);
            if (fresh.Count > 0) host.Notify(L.T("Tailscale 提示"), string.Join("\n", fresh), false, () => host.ShowPage("net"));
        }

        // 点通知：界面线程上开始做，没成就再弹一句原因
        static void Do(Func<Task<string>> act)
        {
            ui.BeginInvoke(new Action(async () =>
            {
                string why = await act();
                if (!string.IsNullOrEmpty(why)) host.Notify(L.T("更改失败"), why);
            }));
        }

        static string Left(TimeSpan t)
        {
            if (t.TotalDays >= 1) return L.T("{0} 天", (int)t.TotalDays);
            if (t.TotalHours >= 1) return L.T("{0} 小时", (int)t.TotalHours);
            return L.T("{0} 分钟", Math.Max(1, (int)t.TotalMinutes));
        }
    }
}
