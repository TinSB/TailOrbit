// 管理接口：用户在后台生成一把访问令牌交给程序，程序拿它做几件只有后台能做的事——删掉不再用的设备、关掉设备的密钥到期、
// 批准本机共享的出口节点和局域网、改本机在网络里的名字。令牌用 Windows 数据保护加密（只有这个 Windows 账号在这台电脑上解得开），
// 存在 %LOCALAPPDATA%\TailOrbit\admin.key，只发给 Tailscale 后台；断开时删掉。令牌有到期时间（后台生成时选，最长 90 天），过期了要重新连
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TailOrbit
{
    static class Admin
    {
        public static string Base = "https://api.tailscale.com/api/v2/";   // 测试时换成本机的假后台
        public static string KeyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailOrbit", "admin.key");
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TailOrbit admin key");
        static string token;
        static DateTime? expires;   // UTC；null 是读不到
        static bool loaded;

        static Admin()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;   // 同 Api：不开 TLS1.2 连不上后台
        }

        // 存着令牌、还没过期
        public static bool Connected
        {
            get
            {
                Load();
                return token != null && (expires == null || expires > DateTime.UtcNow);
            }
        }

        // 存过令牌、但已经过期了（要重新连）
        public static bool Expired
        {
            get
            {
                Load();
                return token != null && expires != null && expires <= DateTime.UtcNow;
            }
        }

        public static DateTime? Expires { get { Load(); return expires; } }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            if (!File.Exists(KeyFile)) return;
            try
            {
                var lines = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, DataProtectionScope.CurrentUser)).Split('\n');
                token = lines[0];
                DateTime at;
                if (lines.Length > 1 && DateTime.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at)) expires = at;
            }
            catch (CryptographicException e) { Log.Write(e); }   // 换了 Windows 账号或者文件坏了：当成没连
            catch (IOException e) { Log.Write(e); }
            catch (UnauthorizedAccessException e) { Log.Write(e); }
        }

        // 连上：先列一次设备证明令牌能用、有管理设备的权限，再读这把令牌哪天到期，然后加密存起来。返回 null 是连上了
        public static string Connect(string text)
        {
            var m = Api.Token.Match(text ?? "");
            if (!m.Success) return L.T("这不是有效的访问令牌。");
            string key = m.Value;
            DateTime? until = null;
            try
            {
                Call(key, "GET", "tailnet/-/devices", null);
                try
                {
                    DateTime at;
                    var info = Call(key, "GET", "tailnet/-/keys/" + m.Groups[1].Value, null);
                    if (DateTime.TryParse(Tailscale.Get(info, "expires") as string, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out at)) until = at;
                }
                catch (WebException e) { Log.Write(e); }   // 只是读不到到期时间：照样能用，到期了后台会回 401
            }
            catch (WebException e) { return Api.Explain(e, L.T("当前账号无权管理设备，需要所有者、管理员或 IT 管理员权限。")); }
            catch (IOException e) { return L.T("无法连接 Tailscale 管理控制台：{0}", e.Message); }   // 回到一半断了
            Directory.CreateDirectory(Path.GetDirectoryName(KeyFile));
            string plain = key + "\n" + (until == null ? "" : until.Value.ToString("o", CultureInfo.InvariantCulture));
            File.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
            token = key;
            expires = until;
            loaded = true;
            return null;
        }

        // 断开：删掉存着的令牌（令牌本身在后台的密钥页里删）。文件被占着删不掉也照样算断开，记进日志
        public static void Disconnect()
        {
            token = null;
            expires = null;
            loaded = true;
            try { File.Delete(KeyFile); }
            catch (IOException e) { Log.Write(e); }
            catch (UnauthorizedAccessException e) { Log.Write(e); }
        }

        // 下面几件事都返回 null 是做成了，否则是讲给用户听的原因。id 是设备的 ID（status 里的 ID，n 开头）

        public static string Delete(string id)
        {
            return Do("DELETE", "device/" + id, null);
        }

        public static string NoExpiry(string id)
        {
            return Do("POST", "device/" + id + "/key", new Dictionary<string, object> { { "keyExpiryDisabled", true } });
        }

        // 批准这台设备共享的这些网段（出口节点是 0.0.0.0/0 和 ::/0）：已经批准的照留；读不出已经批准了哪些就不写，写了会把它们冲掉
        public static string Approve(string id, IEnumerable<string> routes)
        {
            string odd = null;
            return Do(() =>
            {
                var now = Call(token, "GET", "device/" + id + "/routes", null);
                if (now == null) { odd = L.T("管理控制台返回的内容无法识别，请稍后再试。"); return; }
                var on = ((Tailscale.Get(now, "enabledRoutes") as object[]) ?? new object[0]).OfType<string>();
                Call(token, "POST", "device/" + id + "/routes", new Dictionary<string, object> { { "routes", on.Union(routes).ToList() } });
            }) ?? odd;
        }

        // 改这台设备在网络里的名字（后台改的名字优先于本机的主机名）
        public static string Rename(string id, string name)
        {
            return Do("POST", "device/" + id + "/name", new Dictionary<string, object> { { "name", name } });
        }

        static string Do(string method, string path, Dictionary<string, object> body)
        {
            return Do(() => Call(token, method, path, body));
        }

        static string Do(Action work)
        {
            if (!Connected) return L.T("未连接管理接口。");
            try { work(); return null; }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Disconnect();   // 后台不认这把令牌了（被撤销了）：留着没用，当成断开，界面上就是“未连接”
                    return L.T("管理接口的访问令牌已失效，请重新连接。");
                }
                return Api.Explain(e, L.T("当前账号无权管理设备，需要所有者、管理员或 IT 管理员权限。"));
            }
            catch (IOException e) { return L.T("无法连接 Tailscale 管理控制台：{0}", e.Message); }   // 回到一半断了
        }

        static Dictionary<string, object> Call(string key, string method, string path, Dictionary<string, object> body)
        {
            var req = (HttpWebRequest)WebRequest.Create(Base + path);
            req.Method = method;
            req.Headers["Authorization"] = "Bearer " + key;
            req.Accept = "application/json";
            req.Timeout = 20000;
            if (body != null)
            {
                req.ContentType = "application/json";
                var bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(body));
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return Tailscale.Json(r.ReadToEnd());   // 有的回空：null
        }
    }
}
