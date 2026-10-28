// 在手机上收听本机声音：自己的手机打开 https://<本机>:7443/a/<随机码>/，点一下就用手机（和连着它的耳机）听这台电脑正在放的声音；
// 装了配套的 iPhone App（ios/）就不用开网页：它在后台连着，电脑一出声就自动放，电脑安静了就让出耳机。
// 有人连着时才开 Windows 的回环录音（WASAPI loopback）取系统混音，10 毫秒一包转成 16 位立体声直接推过去，不压缩（48kHz 约 1.5Mbps）；
// 电脑没出声时不发声音，只隔 2 秒发一个“安静”。网页用 AudioWorklet 边收边放，只攒几十毫秒，网络抖了才多攒一点、稳了再慢慢减回来。
// 转发和只认同一个账号的设备跟剪贴板互通（ClipShare.cs）一样，随机码也共用
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;

namespace TailOrbit
{
    static class SoundShare
    {
        static Host host;
        static readonly object gate = new object();   // 听众和格式都在它底下改；来了新的一段、定了格式、要断开时 PulseAll
        static readonly List<Listener> listeners = new List<Listener>();
        static readonly Dictionary<string, DateTime> heard = new Dictionary<string, DateTime>();   // 每台设备上次听完的时间：断线重连不算新来的
        static bool capturing, quiet;   // quiet：电脑这会儿没出声，新来的听众先告诉它
        static int rate;                // 正在推的采样率；声音设备没开起来是 0
        static string problem;          // 声音设备开不起来的原因

        static string Mount { get { return "/a/" + Settings.ClipKey; } }

        // 开着时手机要打开的网址；关着是 null
        public static string Url { get; private set; }

        // 一路听众：采集线程往 Chunks 里放，这条连接的线程取出来写出去
        class Listener
        {
            public readonly Queue<byte[]> Chunks = new Queue<byte[]>();
            public string Ip;
            public int Bytes, Rate;   // 排着的字节数；定下格式以前 Rate 是 0
            public bool Ended;        // 换了格式、关了收听：马上断开（网页、App 会重连）
        }

        // 启动时对齐转发，同剪贴板互通
        public static void Start(Host h)
        {
            host = h;
            Task.Run(() => Apply(Settings.SoundOn));
        }

        // 开、关；返回 null 表示成功，否则是开不成的原因。关的时候正在听的马上断开
        public static string Turn(bool on)
        {
            if (!on)
            {
                Settings.SoundOn = false;
                lock (gate)
                {
                    foreach (var l in listeners) l.Ended = true;
                    Monitor.PulseAll(gate);
                }
            }
            string why = Apply(on);
            if (on && why == null) Settings.SoundOn = true;
            return on ? why : null;
        }

        static string Apply(bool on)
        {
            string url;
            string why = ClipShare.Expose("/a/", Mount, on, out url);
            if (why == null) Url = url;
            return why;
        }

        // ---------- 网页：GET / 播放页，GET play.js 放声音的那段（AudioWorklet），GET pcm 声音流（App 也用它） ----------

        public static void Serve(Stream s, string method, string target, Dictionary<string, string> headers)
        {
            int q = target.IndexOf('?');
            var parts = (q >= 0 ? target.Substring(0, q) : target).Split('/');   // "", "a", 随机码[, 后面的]
            string tail = parts.Length > 3 ? string.Join("/", parts.Skip(3)) : null;
            bool head = method == "HEAD", raw = tail == "pcm";   // 声音流出错时给纯文字，网页、App 直接显示
            if (!Settings.SoundOn || parts.Length < 3 || !ClipShare.Same(parts[2], Settings.ClipKey) || headers.ContainsKey("Tailscale-Funnel-Request") || (tail != null && tail != "" && tail != "play.js" && !raw))
            {
                Deny(s, 404, "声音收听未开启，或链接无效。", head, raw);
                return;
            }
            if (tail == null) { FileServer.Redirect(s, Uri.EscapeDataString(parts[2]) + "/"); return; }
            var status = ClipShare.Status();
            string login;
            if (!headers.TryGetValue("Tailscale-User-Login", out login) || !string.Equals(login, ClipShare.Owner(status), StringComparison.OrdinalIgnoreCase))
            {
                Deny(s, 403, "仅限已登录同一 Tailscale 账号的设备查看。", head, raw);
                return;
            }
            if (method != "GET" && (!head || raw)) { FileServer.Head(s, 405, "text/plain; charset=utf-8", 0, ""); return; }
            if (raw) { Play(s, status, headers); return; }
            byte[] body = Encoding.UTF8.GetBytes(tail == "" ? Page(status) : Worklet);
            FileServer.Head(s, 200, (tail == "" ? "text/html" : "text/javascript") + "; charset=utf-8", body.Length, "", "no-store");
            if (!head) s.Write(body, 0, body.Length);
        }

        static void Deny(Stream s, int code, string why, bool head, bool raw)
        {
            if (!raw) { ClipShare.Fail(s, code, why, head); return; }
            var body = Encoding.UTF8.GetBytes(FileServer.Say(why));
            FileServer.Head(s, code, "text/plain; charset=utf-8", body.Length, "", "no-store");
            if (!head) s.Write(body, 0, body.Length);
        }

        // 声音流：先等声音设备开起来、定下格式（最多 3 秒），然后一直往外写，直到对方断开、换了格式或关了收听。
        // 流里一条一条：4 字节小端帧数，后面跟这么多帧 16 位立体声；帧数是 0 表示电脑这会儿没出声（也当心跳）
        static void Play(Stream s, Dictionary<string, object> status, Dictionary<string, string> headers)
        {
            string ip;
            string from = ClipShare.Visitor(status, headers, out ip);
            var me = new Listener { Ip = ip };
            bool fresh;
            string why;
            lock (gate)
            {
                DateTime last;
                // App 是你自己装好、一直挂着的，不提示；网页第一次来听才提示
                fresh = !headers.ContainsKey("X-TailOrbit-App") && !listeners.Any(l => l.Ip == ip) && (!heard.TryGetValue(ip, out last) || (DateTime.UtcNow - last).TotalSeconds > 60);
                me.Rate = rate;
                if (quiet && rate != 0) { me.Chunks.Enqueue(Frame(null)); me.Bytes += 4; }
                listeners.Add(me);
                if (!capturing)
                {
                    capturing = true;
                    new Thread(Capture) { IsBackground = true, Name = "TailOrbit 声音" }.Start();
                }
                var wait = Stopwatch.StartNew();
                while (me.Rate == 0 && !me.Ended && wait.ElapsedMilliseconds < 3000) Monitor.Wait(gate, Math.Max(1, 3000 - (int)wait.ElapsedMilliseconds));
                why = problem;
            }
            try
            {
                if (me.Rate == 0) { Deny(s, 503, why ?? "无法打开本机声音", false, true); return; }
                if (fresh) Tell(from);
                s.WriteTimeout = 10000;   // 对方没了、网络卡死：写不出去 10 秒就断开
                var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nX-Rate: " + me.Rate +
                    "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");   // 不写长度：一直写到断开
                s.Write(h, 0, h.Length);
                while (true)
                {
                    byte[] chunk;
                    lock (gate)
                    {
                        while (me.Chunks.Count == 0 && !me.Ended) Monitor.Wait(gate);
                        if (me.Ended) return;
                        chunk = me.Chunks.Dequeue();
                        me.Bytes -= chunk.Length;
                    }
                    s.Write(chunk, 0, chunk.Length);
                }
            }
            finally
            {
                lock (gate)
                {
                    listeners.Remove(me);
                    if (me.Rate != 0) heard[ip] = DateTime.UtcNow;
                }
            }
        }

        static void Tell(string from)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                host.Notify(L.T("{0} 正在收听本机声音", from), L.T("可以在“设置”页关闭。"), false, () => host.ShowPage("settings"))));
        }

        // ---------- 采集：有人连着就一直采；默认播放设备换了、拔了就重开；最后一个听众走了就关 ----------

        static void Capture()
        {
            Loopback dev = null;
            bool done = false;
            try
            {
                var clock = Stopwatch.StartNew();
                long looked = 0, sound = -1000, got = 0, beat = 0;
                bool hush = false;
                while (true)
                {
                    lock (gate)
                        if (listeners.Count == 0) { capturing = false; rate = 0; done = true; return; }
                    if (dev == null)
                    {
                        string why;
                        dev = Loopback.Open(out why);
                        Opened(dev == null ? 0 : dev.Rate, why);
                        if (dev == null) { Thread.Sleep(1000); continue; }
                        looked = got = clock.ElapsedMilliseconds;
                    }
                    try
                    {
                        var chunk = dev.Read(20);
                        long now = clock.ElapsedMilliseconds;
                        if (chunk != null)
                        {
                            got = now;
                            if (!Silent(chunk)) sound = now;
                        }
                        // 有声音、或者刚停不到 0.2 秒（歌里的停顿）：照常发。停久了，或者根本没在放（Windows 这时不送包）：
                        // 只发一个“安静”，之后每 2 秒一个
                        if (chunk != null && now - sound < 200) { hush = false; Send(Frame(chunk), false); }
                        else if (!hush && (chunk != null || now - got > 40)) { hush = true; beat = now; Send(Frame(null), true); }
                        else if (hush && now - beat >= 2000) { beat = now; Send(Frame(null), true); }
                        // 每秒看一眼默认播放设备换没换（插上耳机、在系统里改了输出）
                        if (now - looked >= 1000)
                        {
                            looked = now;
                            if (dev.Moved()) { dev.Dispose(); dev = null; }
                        }
                    }
                    catch (COMException)   // 设备拔了、被别的程序独占了：重开
                    {
                        dev.Dispose();
                        dev = null;
                    }
                }
            }
            catch (Exception e) { Log.Write(e); }   // 后台线程漏出去的异常会让整个程序退出
            finally
            {
                if (dev != null) dev.Dispose();
                if (!done)
                    lock (gate)
                    {
                        capturing = false;
                        rate = 0;
                        foreach (var l in listeners) l.Ended = true;
                        Monitor.PulseAll(gate);
                    }
            }
        }

        // 声音设备开起来了（没开成时 r 是 0）：在等的定下格式，格式变了的断开重连
        static void Opened(int r, string why)
        {
            lock (gate)
            {
                problem = why;
                rate = r;
                foreach (var l in listeners)
                    if (l.Rate == 0) l.Rate = r;
                    else if (l.Rate != r) l.Ended = true;
                Monitor.PulseAll(gate);
            }
        }

        // 一条交给每路听众；网络卡住、对方没在收的那路，攒过 1 秒就扔掉最旧的（网页那边跳过去追上）
        static void Send(byte[] record, bool hush)
        {
            lock (gate)
            {
                quiet = hush;
                foreach (var l in listeners)
                {
                    if (l.Ended || l.Rate != rate) continue;
                    l.Chunks.Enqueue(record);
                    l.Bytes += record.Length;
                    while (l.Bytes > rate * 4) l.Bytes -= l.Chunks.Dequeue().Length;
                }
                Monitor.PulseAll(gate);
            }
        }

        // 一条记录：4 字节小端帧数，后面跟这么多帧；pcm 是 null 时帧数为 0，表示电脑没出声
        static byte[] Frame(byte[] pcm)
        {
            int n = pcm == null ? 0 : pcm.Length / 4;
            var r = new byte[4 + n * 4];
            r[0] = (byte)n;
            r[1] = (byte)(n >> 8);
            r[2] = (byte)(n >> 16);
            r[3] = (byte)(n >> 24);
            if (n > 0) Buffer.BlockCopy(pcm, 0, r, 4, n * 4);
            return r;
        }

        // 16 位采样全在 ±4 以内（-78dB，听不见）算没出声：有的程序暂停时还在送极小的噪声
        static bool Silent(byte[] pcm)
        {
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                int v = (short)(pcm[i] | pcm[i + 1] << 8);
                if (v > 4 || v < -4) return false;
            }
            return true;
        }

        // 默认播放设备上的一路回环录音：有程序在放声音时 10 毫秒来一包，都没在放时 Windows 就不送了
        sealed class Loopback : IDisposable
        {
            const int Loop = 0x20000, Events = 0x40000;   // AUDCLNT_STREAMFLAGS_LOOPBACK、_EVENTCALLBACK
            const int SilentFlag = 2;                      // AUDCLNT_BUFFERFLAGS_SILENT
            const long Depth = 1000000;                    // 缓冲 100 毫秒（单位 100 纳秒）
            static int lastError;

            public string Id;
            public int Rate;   // 推出去的采样率：高过 48kHz 的减半再减半
            IMMDeviceEnumerator devices;
            IMMDevice device;
            IAudioClient capture;
            IAudioCaptureClient reader;
            IntPtr format;
            int channels, step;
            float[] left, right, samples = new float[0];
            readonly float[] acc = new float[3];
            readonly AutoResetEvent ready = new AutoResetEvent(false);

            public static Loopback Open(out string why)
            {
                var l = new Loopback();
                try
                {
                    l.Start();
                    why = null;
                    return l;
                }
                catch (COMException e)
                {
                    l.Dispose();
                    why = unchecked((uint)e.ErrorCode) == 0x80070490 ? "未找到可用的音频输出设备" : "无法打开本机声音";
                    if (e.ErrorCode != lastError) Log.Write(e);   // 每秒重试一次，同一个错只记一回
                    lastError = e.ErrorCode;
                    return null;
                }
            }

            void Start()
            {
                devices = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
                devices.GetDefaultAudioEndpoint(0, 0, out device);   // 播放、默认设备
                device.GetId(out Id);
                object o;
                device.Activate(typeof(IAudioClient).GUID, 0x17, IntPtr.Zero, out o);   // CLSCTX_ALL
                capture = (IAudioClient)o;
                capture.GetMixFormat(out format);
                int tag = (ushort)Marshal.ReadInt16(format, 0), rateIn = Marshal.ReadInt32(format, 4);
                bool extensible = tag == 0xFFFE;
                // 共享模式下系统混音都是 32 位浮点；万一不是，宁可不放也别把乱码当声音推进耳机
                if ((extensible ? Marshal.ReadInt32(format, 24) : tag) != 3 || Marshal.ReadInt16(format, 14) != 32) throw new COMException("mix format", unchecked((int)0x88890008));
                channels = Marshal.ReadInt16(format, 2);
                left = new float[channels];
                right = new float[channels];
                Gains(channels, extensible ? Marshal.ReadInt32(format, 20) : 0, left, right);
                step = 1;
                while (rateIn / step > 48000 && rateIn % (step * 2) == 0) step *= 2;
                Rate = rateIn / step;
                capture.Initialize(0, Loop | Events, Depth, 0, format, IntPtr.Zero);
                capture.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                capture.GetService(typeof(IAudioCaptureClient).GUID, out o);
                reader = (IAudioCaptureClient)o;
                capture.Start();
            }

            // 等下一包（最多 wait 毫秒），攒着的全取出来转成 16 位立体声；一包都没有是 null
            public byte[] Read(int wait)
            {
                ready.WaitOne(wait);   // 老版本 Windows 回环不发信号，就按这个间隔来取
                byte[] dst = null;
                int at = 0;
                uint n;
                reader.GetNextPacketSize(out n);
                while (n > 0)
                {
                    IntPtr data;
                    uint frames, flags;
                    ulong position, qpc;
                    reader.GetBuffer(out data, out frames, out flags, out position, out qpc);
                    int count = (int)frames * channels;
                    if (samples.Length < count) samples = new float[count];
                    if ((flags & SilentFlag) != 0) Array.Clear(samples, 0, count);
                    else Marshal.Copy(data, samples, 0, count);
                    reader.ReleaseBuffer(frames);
                    int need = at + ((int)frames / step + 1) * 4;
                    if (dst == null) dst = new byte[need];
                    else if (dst.Length < need) Array.Resize(ref dst, need);
                    at = Mix(samples, (int)frames, channels, left, right, step, acc, dst, at);
                    reader.GetNextPacketSize(out n);
                }
                if (at == 0) return null;
                if (at < dst.Length) Array.Resize(ref dst, at);
                return dst;
            }

            // 默认播放设备换了
            public bool Moved()
            {
                IMMDevice now;
                devices.GetDefaultAudioEndpoint(0, 0, out now);
                string id;
                now.GetId(out id);
                Marshal.ReleaseComObject(now);
                return id != Id;
            }

            public void Dispose()
            {
                if (capture != null)
                    try { capture.Stop(); }
                    catch (COMException) { }   // 还没开始就出错了
                foreach (var o in new object[] { reader, capture, device, devices })
                    if (o != null) Marshal.ReleaseComObject(o);
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                ready.Dispose();
            }

            // 每个声道往左右各分多少：中置、后面和侧面的分一点给两边，低音声道不要；单声道两边都放。
            // 声道按位置掩码从低位往高位排：左前、右前、中置、低音、左后、右后、左中前、右中前、正后、左侧、右侧
            static void Gains(int channels, int mask, float[] left, float[] right)
            {
                if (channels == 1) { left[0] = right[0] = 1; return; }
                if (mask == 0) mask = (1 << channels) - 1;   // 没写位置：按标准顺序
                const float h = 0.7071f;
                float[] l = { 1, 0, h, 0, h, 0, 1, 0, 0.5f, h, 0 }, r = { 0, 1, h, 0, 0, h, 0, 1, 0.5f, 0, h };
                for (int bit = 0, c = 0; bit < 32 && c < channels; bit++)
                {
                    if ((mask & (1 << bit)) == 0) continue;
                    if (bit < l.Length) { left[c] = l[bit]; right[c] = r[bit]; }
                    c++;
                }
            }

            // 浮点交错采样 → 16 位立体声（小端，左右交错）；step 个采样平均成一个，凑不满的留在 acc 里等下一包
            static int Mix(float[] src, int frames, int channels, float[] left, float[] right, int step, float[] acc, byte[] dst, int at)
            {
                for (int f = 0, i = 0; f < frames; f++)
                {
                    float l = 0, r = 0;
                    for (int c = 0; c < channels; c++, i++)
                    {
                        l += src[i] * left[c];
                        r += src[i] * right[c];
                    }
                    acc[0] += l;
                    acc[1] += r;
                    if (++acc[2] < step) continue;
                    at = Put(dst, at, acc[0] / step);
                    at = Put(dst, at, acc[1] / step);
                    acc[0] = acc[1] = acc[2] = 0;
                }
                return at;
            }

            static int Put(byte[] dst, int at, float v)
            {
                int s = (int)(v * 32767 + (v < 0 ? -0.5f : 0.5f));
                if (s > 32767) s = 32767;
                else if (s < -32768) s = -32768;
                dst[at] = (byte)s;
                dst[at + 1] = (byte)(s >> 8);
                return at + 2;
            }
        }

        // ---------- 给手机看的网页：中间一个大圆钮，四周一圈跟着声音跳的刻度，底下一行状态 ----------

        static string Page(Dictionary<string, object> status)
        {
            string pc = Tailscale.ShortName((Tailscale.Get(status, "Self") as Dictionary<string, object>) ?? new Dictionary<string, object>());
            string title = FileServer.Say("{0} 的声音", pc);
            var words = new Dictionary<string, string>
            {
                { "tap", FileServer.Say("点按开始收听") }, { "wait", FileServer.Say("正在连接…") }, { "buf", FileServer.Say("缓冲 {0} 毫秒", "#") },
                { "idle", FileServer.Say("电脑没有在放声音") }, { "lost", FileServer.Say("连接中断，正在重试…") },
                { "old", FileServer.Say("此浏览器无法播放，请更新到最新版本。") }, { "play", FileServer.Say("播放") }, { "pause", FileServer.Say("暂停") }
            };
            var js = new JavaScriptSerializer();   // 会把 < > & 转义，放进 <script> 里也安全
            return FileServer.Page(title, null, Style + Markup + "<script>(function(){var T=" + js.Serialize(title) + ",S=" + js.Serialize(words) + ";" + Script + "})()</script>");
        }

        const string Style = @"<style>main{min-height:100vh;min-height:100dvh;box-sizing:border-box;display:flex;flex-direction:column;padding-bottom:calc(28px + env(safe-area-inset-bottom))}
.stage{flex:1;display:grid;place-items:center;min-height:320px}.stage>*{grid-area:1/1}canvas{width:min(300px,86vw);height:min(300px,86vw)}
.btn{width:132px;height:132px;border-radius:50%;border:0;padding:0;background:var(--fg);color:var(--bg);display:grid;place-items:center;cursor:pointer;-webkit-tap-highlight-color:transparent}
.btn svg{width:46px;height:46px;fill:currentColor}.btn:disabled{opacity:.35}
.note{min-height:22px;text-align:center;color:var(--mute);font-size:15px;font-variant-numeric:tabular-nums}</style>";

        const string Markup = @"<div class=""stage""><canvas id=""c""></canvas><button class=""btn"" id=""b""><svg viewBox=""0 0 24 24"" aria-hidden=""true""><path id=""p""/></svg></button></div><div class=""note"" id=""n""></div>";

        // 点一下才建 AudioContext（手机浏览器要求）；声音流按条拆开，声音整帧交给 AudioWorklet，“安静”转告它（这时放空了不算网络卡），
        // 断了隔一两秒重连。四周 64 根刻度按频谱左右对称：上面是低音，往下是高音
        const string Script = @"
var $=function(i){return document.getElementById(i)},b=$('b'),n=$('n'),cv=$('c'),g=cv.getContext('2d'),
PLAY='M8 5.6v12.8c0 .8.9 1.3 1.6.9l10-6.4c.6-.4.6-1.3 0-1.7l-10-6.4C8.9 4.3 8 4.8 8 5.6z',
PAUSE='M7 5h3a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zm7 0h3a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1h-3a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z',
ctx,node,an,ready,ac,timer=0,on=false,live=false,tries=0,bins,lv=new Float32Array(32),fg='',dpr=Math.min(3,window.devicePixelRatio||1);
cv.width=cv.height=Math.round(300*dpr);
function say(t){n.textContent=t}
function face(){$('p').setAttribute('d',on?PAUSE:PLAY);b.setAttribute('aria-label',on?S.pause:S.play)}
face();say(S.tap);
if(!window.AudioWorkletNode||!window.AbortController){say(S.old);b.disabled=true;return}
b.onclick=function(){if(on)stop();else start()};
function start(){
on=true;face();say(S.wait);
try{if(navigator.audioSession)navigator.audioSession.type='playback'}catch(e){}
if(!ctx){
ctx=new AudioContext({latencyHint:'interactive'});
an=ctx.createAnalyser();an.fftSize=2048;an.smoothingTimeConstant=0.5;an.minDecibels=-85;an.maxDecibels=-20;an.connect(ctx.destination);
bins=new Uint8Array(an.frequencyBinCount);
ctx.onstatechange=wake;
ready=ctx.audioWorklet.addModule('play.js').then(function(){
node=new AudioWorkletNode(ctx,'pcm',{numberOfInputs:0,outputChannelCount:[2]});
node.connect(an);
node.port.onmessage=function(e){live=e.data.on;if(on&&ac)say(live?S.buf.replace('#',Math.round(e.data.ms)):e.data.idle||n.textContent===S.idle?S.idle:S.wait)};
});
}
ctx.resume();
ready.then(connect,function(){say(S.old)});
}
function stop(){on=false;live=false;face();clearTimeout(timer);timer=0;var a=ac;ac=null;if(a)a.abort();ctx.suspend();say(S.tap)}
function connect(){
timer=0;
if(!on||ac||!node)return;
var me=ac=new AbortController();
fetch('pcm',{cache:'no-store',signal:me.signal}).then(function(r){
if(!r.ok)return r.text().then(function(t){throw new Error(t)});
node.port.postMessage({rate:+r.headers.get('X-Rate')||48000});
tries=0;
var rd=r.body.getReader(),rest=new Uint8Array(0);
return (function pump(){return rd.read().then(function(x){
if(x.done)throw new Error('');
var v=x.value,at=0,k,m;
if(rest.length){m=new Uint8Array(rest.length+v.length);m.set(rest);m.set(v,rest.length);v=m}
while(v.length-at>=4){
k=v[at]|v[at+1]<<8|v[at+2]<<16|v[at+3]<<24;
if(!k){node.port.postMessage({quiet:1});at+=4;continue}
if(v.length-at-4<k*4)break;
m=v.slice(at+4,at+4+k*4).buffer;node.port.postMessage(m,[m]);at+=4+k*4;
}
rest=v.slice(at);
return pump();
})})();
}).catch(function(e){
if(me!==ac)return;
ac=null;live=false;
say(e instanceof TypeError||!e.message?S.lost:e.message);
timer=setTimeout(connect,Math.min(1000*++tries,5000));
});
}
function wake(){if(!on)return;if(ctx.state!=='running')ctx.resume();if(!ac&&!timer)connect()}
document.addEventListener('visibilitychange',function(){if(document.visibilityState==='visible')wake()});
try{navigator.mediaSession.metadata=new MediaMetadata({title:T});navigator.mediaSession.setActionHandler('play',function(){if(!on)start()});navigator.mediaSession.setActionHandler('pause',function(){if(on)stop()})}catch(e){}
var dark=window.matchMedia('(prefers-color-scheme: dark)');
if(dark.addEventListener)dark.addEventListener('change',function(){fg=''});
(function draw(){
requestAnimationFrame(draw);
if(!fg)fg=getComputedStyle(document.body).color;
var N=lv.length,W=cv.width,c=W/2,r0=78*dpr,len=56*dpr,k,j,v,e,a,x,y,d,has=on&&live;
if(has)an.getByteFrequencyData(bins);
for(k=0;k<N;k++){
v=0;
if(has){var hz=ctx.sampleRate/an.fftSize,lo=Math.floor(40*Math.pow(400,k/N)/hz),hi=Math.max(lo+1,Math.floor(40*Math.pow(400,(k+1)/N)/hz));
for(j=lo;j<hi&&j<bins.length;j++)if(bins[j]>v)v=bins[j];v/=255}
lv[k]+=(v-lv[k])*(v>lv[k]?0.6:0.12);
}
g.clearRect(0,0,W,W);g.strokeStyle=fg;g.lineCap='round';g.lineWidth=3.2*dpr;
for(k=0;k<2*N;k++){
e=lv[k<N?k:2*N-1-k];a=(k+0.5)*Math.PI/N-Math.PI/2;x=Math.cos(a);y=Math.sin(a);d=r0+3*dpr+e*len;
g.globalAlpha=0.2+0.8*e;g.beginPath();g.moveTo(c+x*r0,c+y*r0);g.lineTo(c+x*d,c+y*d);g.stroke();
}
b.style.transform='scale('+(1+0.07*(lv[0]+lv[1]+lv[2])/3).toFixed(3)+')';
})();";

        // 放声音的那段（跑在音频线程上）：收到的 16 位采样放进 2.7 秒的环形缓冲，攒够目标量才开始放。
        // 目标从 80 毫秒起：放空了（网络抖）加 30 毫秒、最多 400；稳了 20 秒减 10 毫秒、最少 50；电脑说了“安静”之后放空不算。
        // 按平均攒量微调播放速度（最多快慢 0.5%，听不出来）追平两边时钟的差；网络卡完一下子涌来一大堆，直接跳到目标位置。
        // 采样率和手机不一样时顺带换算（三次插值）
        const string Worklet = @"class P extends AudioWorkletProcessor{
constructor(){super();
this.n=1<<17;this.m=this.n-1;this.L=new Float32Array(this.n);this.R=new Float32Array(this.n);
this.rate=48000;this.w=0;this.r=0;this.on=false;this.idle=false;this.t=0.08;this.avg=0;this.calm=0;this.tick=0;
this.port.onmessage=e=>{const d=e.data;
if(d.rate){this.rate=d.rate;this.w=this.r=0;this.on=false;this.idle=false;return}
if(d.quiet){this.idle=true;return}
this.idle=false;
const s=new Int16Array(d),k=s.length>>1,m=this.m;let w=this.w;
for(let i=0;i<k;i++,w++){this.L[w&m]=s[2*i]/32768;this.R[w&m]=s[2*i+1]/32768}
this.w=w;if(w-this.r>this.n-1024)this.r=w-this.n+1024}}
process(i,o){const L=o[0][0],R=o[0][1],q=L.length,t=this.t*this.rate,m=this.m;
let have=this.w-this.r;
if(!this.on&&have>=t){this.on=true;this.avg=have}
if(this.on&&have>t*3+this.rate*0.05){this.r=this.w-t;have=t;this.avg=t}
this.avg+=(have-this.avg)*0.02;
const sp=this.rate/sampleRate*(1+Math.max(-0.005,Math.min(0.005,(this.avg-t)/t*0.02)));
if(!this.on||have<q*sp+4){
if(this.on){this.on=false;if(!this.idle){this.t=Math.min(0.4,this.t+0.03);this.calm=0}}
L.fill(0);R.fill(0);this.report(have);return true}
let p=this.r;
for(let j=0;j<q;j++){const a=Math.floor(p),f=p-a;L[j]=h(this.L,a,f,m);R[j]=h(this.R,a,f,m);p+=sp}
this.r=p;
if((this.calm+=q)>sampleRate*20){this.calm=0;this.t=Math.max(0.05,this.t-0.01)}
this.report(have);return true}
report(have){if((this.tick+=128)<sampleRate/4)return;this.tick=0;this.port.postMessage({ms:have/this.rate*1000,on:this.on,idle:this.idle&&!this.on})}}
function h(b,a,f,m){const y0=b[(a-1)&m],y1=b[a&m],y2=b[(a+1)&m],y3=b[(a+2)&m],c1=0.5*(y2-y0),c2=y0-2.5*y1+2*y2-0.5*y3,c3=0.5*(y3-y0)+1.5*(y1-y2);return((c3*f+c2)*f+c1)*f+y1}
registerProcessor('pcm',P);";

        // ---------- Windows 声音接口：只声明到用得着的那个方法为止，顺序照 mmdeviceapi.h、audioclient.h ----------

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
            void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            void Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object face);
            void OpenPropertyStore(int access, out IntPtr store);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            void Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
            void GetBufferSize(out uint frames);
            void GetStreamLatency(out long latency);
            void GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            void GetMixFormat(out IntPtr format);
            void GetDevicePeriod(out long period, out long minimum);
            void Start();
            void Stop();
            void Reset();
            void SetEventHandle(IntPtr handle);
            void GetService([MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);
            void ReleaseBuffer(uint frames);
            void GetNextPacketSize(out uint frames);
        }
    }
}
