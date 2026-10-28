// 待送：拖到离线设备上的文件先记下（只记位置，不复制、不占地方），对方一上线就照常用 Taildrop 发过去。
// 记在 %LOCALAPPDATA%\TailOrbit\outbox.json，重启程序还在；送到、没送到、被取消都从里面拿掉，结果照常进传输记录
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace TailOrbit
{
    // 攒给一台设备的一批文件
    class Pending
    {
        public string Id = "", Ip = "";   // 收件设备的 Tailscale 设备 ID（换了 IP 也认得），读不到 ID 时认排进来时的 IP
        public List<string> Paths = new List<string>();
        public SendJob Job;               // 正在发；不存盘
    }

    static class Outbox
    {
        static readonly string FilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailOrbit", "outbox.json");
        static readonly List<Pending> items = Load();
        static readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        static Host host;
        static bool reading, stopping;
        public static event Action Changed;   // 在界面线程上触发

        public static void Start(Host h)
        {
            host = h;
            SendJob.Changed += OnJob;
            tick.Tick += delegate { Check(); };
            if (items.Count > 0) tick.Start();
        }

        // 退出程序时被一起取消的那批不算取消：下次启动接着等
        public static void Stop()
        {
            stopping = true;
        }

        public static void Add(Device d, List<string> paths)
        {
            items.Add(new Pending { Id = d.StableId, Ip = d.Ip, Paths = paths });
            Save();
            tick.Start();
            Raise();
        }

        // 攒给这台设备、还没开始发的
        public static List<Pending> For(Device d)
        {
            return items.Where(i => i.Job == null && Is(i, d)).ToList();
        }

        public static void Cancel(Device d)
        {
            if (items.RemoveAll(i => i.Job == null && Is(i, d)) == 0) return;
            Save();
            Raise();
        }

        static bool Is(Pending i, Device d)
        {
            return i.Id != "" && d.StableId != "" ? i.Id == d.StableId : i.Ip == d.Ip;
        }

        // 读到了设备列表（主界面开着时它每 10 秒读一次，关着时这里每 15 秒读一次）：在线的设备有攒着的，合成一次发过去。
        // 文件已经不在原处的那批不发，记成没送到
        public static void Seen(List<Device> devices)
        {
            bool changed = false;
            foreach (var d in devices.Where(x => x.Online))
            {
                if (items.Any(i => i.Job != null && Is(i, d))) continue;   // 上一次的还没发完
                var due = For(d);
                if (due.Count == 0) continue;
                changed = true;
                foreach (var i in due.Where(x => !x.Paths.All(p => File.Exists(p) || Directory.Exists(p))).ToList())
                {
                    due.Remove(i);
                    items.Remove(i);
                    History.Record(new SendJob { Target = d, Paths = i.Paths }, "文件已被移动或删除");
                    host.Notify(L.T("发送到 {0} 失败", d.Name), L.T("文件已被移动或删除"));
                }
                if (due.Count == 0) continue;
                var j = new SendJob { Target = d, Paths = due.SelectMany(i => i.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Queued = true };
                foreach (var i in due) i.Job = j;
                host.Deliver(j);
            }
            if (!changed) return;
            Save();
            Raise();
        }

        static void Check()
        {
            if (items.Count == 0) { tick.Stop(); return; }
            if (reading || items.All(i => i.Job != null)) return;
            reading = true;
            Task.Run(() => Tailscale.Load()).ContinueWith(t =>
            {
                reading = false;
                if (!t.IsFaulted && t.Result.Error == null) Seen(t.Result.Devices);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 发完了（送到、没送到、取消了）就从待送里拿掉；在发送的后台线程上来
        static void OnJob(SendJob j)
        {
            if (j.State != "done" && j.State != "failed" && j.State != "canceled") return;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (stopping || items.RemoveAll(i => i.Job == j) == 0) return;
                Save();
                Raise();
            }));
        }

        static void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }

        static List<Pending> Load()
        {
            var list = new List<Pending>();
            if (!File.Exists(FilePath)) return list;
            try
            {
                var all = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath, Encoding.UTF8)) as object[];
                foreach (var o in (all ?? new object[0]).OfType<Dictionary<string, object>>())
                {
                    var paths = Tailscale.Get(o, "paths") as object[];
                    if (paths == null) continue;
                    list.Add(new Pending { Id = (Tailscale.Get(o, "id") as string) ?? "", Ip = (Tailscale.Get(o, "ip") as string) ?? "", Paths = paths.OfType<string>().ToList() });
                }
            }
            catch (ArgumentException e) { Log.Write(e); }   // 文件坏了：当成没有，下次保存时覆盖
            catch (IOException e) { Log.Write(e); }
            return list;
        }

        // 先写临时文件再换过去，写到一半断电也不会留下半个文件
        static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                string temp = FilePath + ".tmp";
                var all = items.Select(i => new Dictionary<string, object> { { "id", i.Id }, { "ip", i.Ip }, { "paths", i.Paths } }).ToList();
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(all), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch (IOException e) { Log.Write(e); }
            catch (UnauthorizedAccessException e) { Log.Write(e); }
        }
    }
}
