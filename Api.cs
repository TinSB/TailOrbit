// 云盘一键开通：用户在后台生成一把访问令牌、复制，程序拿它改访问规则——在 nodeAttrs、grants 里各加一条，带 ETag 存回去，
// 再把这把令牌作废。令牌只在内存里用这一次，不存盘、不留在剪贴板。规则文件是 HuJSON（JSON 加注释、允许结尾逗号），
// 只在原文里插字，用户写的注释和排版原样保留
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TailOrbit
{
    static class Api
    {
        const string Base = "https://api.tailscale.com/api/v2/tailnet/-/";
        public const string KeysPage = "https://login.tailscale.com/admin/settings/keys";
        public const string RulesPage = "https://login.tailscale.com/admin/acls/file";
        public static readonly Regex Token = new Regex(@"tskey-api-([A-Za-z0-9]+)-[A-Za-z0-9]+");

        // csc 直接编、没标目标框架：.NET 默认只开 SSL3/TLS1.0，Tailscale 后台要 TLS1.2，不打开握手就失败（10-27 实测）
        static Api()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        const string DriveAttr = "{\"target\": [\"autogroup:member\"], \"attr\": [\"drive:share\", \"drive:access\"]}";
        const string DriveGrant = "{\"src\": [\"autogroup:member\"], \"dst\": [\"autogroup:self\"], \"app\": {\"tailscale.com/cap/drive\": [{\"shares\": [\"*\"], \"access\": \"rw\"}]}}";

        // 返回 null 表示开通了；step 报进度：1 读规则、2 存规则、3 作废令牌；revoked 说令牌最后作废成没有
        public static string EnableDrive(string token, Action<int> step, out bool revoked)
        {
            revoked = false;
            var m = Token.Match(token ?? "");
            if (!m.Success) return L.T("这不是有效的访问令牌。");
            token = m.Value;
            try
            {
                step(1);
                string etag;
                string policy = Call(token, "GET", "acl", null, null, out etag);
                if (!(policy.Contains("drive:share") && policy.Contains("tailscale.com/cap/drive")))
                {
                    string next = Insert(Insert(policy, "nodeAttrs", DriveAttr), "grants", DriveGrant);
                    step(2);
                    string ignore;
                    Call(token, "POST", "acl", next, etag, out ignore);
                }
                return null;
            }
            catch (WebException e) { return Explain(e); }
            catch (FormatException) { return L.T("无法解析当前的访问控制策略，未做任何更改。"); }
            finally
            {
                // 不管成没成都作废：这把令牌不该比这一次用得更久
                step(3);
                try { string ignore; Call(token, "DELETE", "keys/" + m.Groups[1].Value, null, null, out ignore); revoked = true; }
                catch (Exception) { }   // 作废没成不影响开通，但要让用户知道，自己去密钥页删（revoked 还是 false）
            }
        }

        static string Call(string token, string method, string path, string body, string etag, out string newTag)
        {
            var req = (HttpWebRequest)WebRequest.Create(Base + path);
            req.Method = method;
            req.Headers["Authorization"] = "Bearer " + token;
            req.Accept = "application/hujson";
            req.Timeout = 20000;
            if (etag != null) req.Headers["If-Match"] = etag;
            if (body != null)
            {
                req.ContentType = "application/hujson";
                var bytes = new UTF8Encoding(false).GetBytes(body);
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                newTag = resp.Headers["ETag"];
                return r.ReadToEnd();
            }
        }

        static string Explain(WebException e)
        {
            var resp = e.Response as HttpWebResponse;
            if (resp == null) return L.T("无法连接 Tailscale 管理控制台：{0}", e.Message);
            int code = (int)resp.StatusCode;
            if (code == 401) return L.T("访问令牌无效。请确认已完整复制，且令牌未被撤销。");
            if (code == 403) return L.T("当前账号无权修改访问控制，需要所有者或管理员权限。");
            if (code == 412) return L.T("访问控制刚刚被修改，请重试。");
            string message = null;
            try
            {
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    message = Tailscale.Get(new JavaScriptSerializer().DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>, "message") as string;
            }
            catch (ArgumentException) { }   // 回的不是 JSON
            catch (IOException) { }
            return string.IsNullOrEmpty(message) ? L.T("管理控制台拒绝了此请求（{0}）", code) : L.T("管理控制台拒绝了此请求（{0}）：{1}", code, message);
        }

        // ---------- 在 HuJSON 里插一条 ----------

        // 最外层对象里 key 是个数组：插在数组开头；没有这个键：在对象开头加一个。HuJSON 允许结尾逗号，所以两种都不用管后面有没有东西
        public static string Insert(string text, string key, string entry)
        {
            var keys = new Dictionary<string, int>();
            int top = Scan(text, keys);
            if (top < 0) throw new FormatException();
            int at;
            if (!keys.TryGetValue(key, out at)) return text.Insert(top + 1, "\n\t\"" + key + "\": [\n\t\t" + entry + ",\n\t],");
            int b = Skip(text, at);
            if (b >= text.Length || text[b] != '[') throw new FormatException();
            return text.Insert(b + 1, "\n\t\t" + entry + ",");
        }

        // 走一遍原文（跳过注释和字符串）：返回最外层 { 的位置，keys 里记最外层每个键的值从哪儿开始
        static int Scan(string t, Dictionary<string, int> keys)
        {
            int depth = 0, top = -1, i = 0;
            string last = null;   // 刚读完、后面可能跟冒号的字符串
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '/' && i + 1 < t.Length && (t[i + 1] == '/' || t[i + 1] == '*'))
                {
                    i = Skip(t, i);
                    continue;
                }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    int j = i + 1;
                    for (; j < t.Length && t[j] != '"'; j++)
                    {
                        if (t[j] == '\\' && j + 1 < t.Length) j++;
                        sb.Append(t[j]);
                    }
                    if (j >= t.Length) throw new FormatException();   // 字符串没有收尾
                    last = sb.ToString();
                    i = j + 1;
                    continue;
                }
                if (c == ':' && depth == 1 && last != null) keys[last] = i + 1;
                else if (c == '{' || c == '[')
                {
                    if (depth == 0 && c == '{' && top < 0) top = i;
                    depth++;
                }
                else if (c == '}' || c == ']') depth--;
                if (!char.IsWhiteSpace(c)) last = null;
                i++;
            }
            if (depth != 0) throw new FormatException();
            return top;
        }

        // 跳过空白和注释，停在下一个有意义的字符上
        static int Skip(string t, int i)
        {
            while (i < t.Length)
            {
                if (char.IsWhiteSpace(t[i])) { i++; continue; }
                if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/')
                {
                    int n = t.IndexOf('\n', i);
                    i = n < 0 ? t.Length : n + 1;
                    continue;
                }
                if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '*')
                {
                    int n = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (n < 0) throw new FormatException();
                    i = n + 2;
                    continue;
                }
                break;
            }
            return i;
        }
    }
}
