// 界面文字的多语言：代码里写的就是简体中文原文，别的语言按原文查表（i18n\<代码>.json，编译时嵌进程序）。
// 查不到的照原文显示；带 {0} 的用 string.Format 填。默认跟着系统语言，设置页可以手动换
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TailOrbit
{
    static class L
    {
        public static readonly string[] Codes = { "zh-Hans", "zh-Hant", "en", "ja", "ko", "de", "fr", "es" };
        public static readonly string[] Names = { "简体中文", "繁體中文", "English", "日本語", "한국어", "Deutsch", "Français", "Español" };
        static readonly Dictionary<string, Dictionary<string, string>> tables = new Dictionary<string, Dictionary<string, string>>();
        static Dictionary<string, string> table;

        public static string Code { get; private set; }
        public static CultureInfo Culture { get; private set; }

        static L()
        {
            Use("auto");
        }

        // setting："auto" 跟系统走，或者 Codes 里的一个
        public static void Use(string setting)
        {
            Code = Codes.Contains(setting) ? setting : FromCulture(CultureInfo.CurrentUICulture.Name);
            Culture = CultureInfo.GetCultureInfo(Code == "zh-Hans" ? "zh-CN" : Code == "zh-Hant" ? "zh-TW" : Code);
            table = Table(Code);
        }

        // 浏览器发来的 Accept-Language（分享出去的网页用）、系统语言 → 我们有的语言
        public static string FromCulture(string names)
        {
            foreach (var raw in (names ?? "").Split(','))
            {
                string n = raw.Split(';')[0].Trim().ToLowerInvariant();
                if (n.StartsWith("zh")) return n.Contains("hant") || n.EndsWith("-tw") || n.EndsWith("-hk") || n.EndsWith("-mo") ? "zh-Hant" : "zh-Hans";
                foreach (var c in new[] { "en", "ja", "ko", "de", "fr", "es" })
                    if (n == c || n.StartsWith(c + "-")) return c;
            }
            return "en";
        }

        public static string T(string zh)
        {
            return In(table, zh);
        }

        public static string T(string zh, params object[] args)
        {
            return string.Format(Culture, In(table, zh), args);
        }

        // 指定语言查一句（分享出去的网页按访客的语言显示）
        public static string For(string code, string zh, params object[] args)
        {
            string s = In(Table(code), zh);
            return args.Length == 0 ? s : string.Format(s, args);
        }

        static string In(Dictionary<string, string> t, string zh)
        {
            string v;
            return t != null && t.TryGetValue(zh, out v) && !string.IsNullOrEmpty(v) ? v : zh;
        }

        static Dictionary<string, string> Table(string code)
        {
            if (code == "zh-Hans") return null;
            lock (tables)
            {
                Dictionary<string, string> t;
                if (tables.TryGetValue(code, out t)) return t;
                t = new Dictionary<string, string>();
                using (var s = typeof(L).Assembly.GetManifestResourceStream("i18n." + code + ".json"))
                    if (s != null)
                    {
                        var o = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(new StreamReader(s, Encoding.UTF8).ReadToEnd()) as Dictionary<string, object>;
                        if (o != null) foreach (var kv in o) t[kv.Key] = kv.Value as string;
                    }
                tables[code] = t;
                return t;
            }
        }
    }
}
