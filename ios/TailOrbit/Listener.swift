import AVFoundation
import Network
import Observation

/// 收听电脑的声音。手机连着 Wi-Fi、戴着耳机时，一直连着电脑上 TailOrbit 的声音流（设置页“在手机上收听本机声音”那个网址后面加 pcm），
/// 电脑一出声就放，安静 8 秒就停下、让出耳机；摘了耳机、离开 Wi-Fi 就断开，不费电也不耗流量（声音不压缩，约 1.5Mbps）。
/// 声音流见 SoundShare.cs：一条一条，4 字节小端帧数，后面跟这么多帧 16 位立体声；帧数是 0 表示电脑没出声（也当心跳，2 秒一个）。
/// 界面看的几个属性在主线程上改，其余的都只在 queue 上动（网络回调也排在它上面），不用加锁。
@Observable
final class Listener {
    enum Phase: Equatable { case off, noAddress, noWifi, noHeadphones, connecting, failed(String), waiting, playing }

    private(set) var phase = Phase.connecting
    private(set) var needsLocation = false
    let computer: String   // 电脑的名字：网址的第一段
    let player = Player()
    var auto: Bool {
        didSet {
            UserDefaults.standard.set(auto, forKey: "auto")
            let on = auto
            Keeper.shared.hold("sound", on)
            queue.async { self.want = on; self.evaluate() }
        }
    }

    private let queue = DispatchQueue(label: "TailOrbit.listen")
    private let address: URL?
    private let path: NWPathMonitor
    @ObservationIgnored private var session: URLSession!
    @ObservationIgnored private var tick: DispatchSourceTimer?
    @ObservationIgnored private var want: Bool
    @ObservationIgnored private var wifi = false
    @ObservationIgnored private var phones = false
    @ObservationIgnored private var task: URLSessionDataTask?
    @ObservationIgnored private var code = 0               // 这次连接的 HTTP 状态，还没回来是 0
    @ObservationIgnored private var pending = [UInt8]()    // 收到了、还没凑成整条的
    @ObservationIgnored private var retry: DispatchWorkItem?
    @ObservationIgnored private var wait = 1.0             // 连不上时隔几秒再试，每次翻倍、最多 15
    @ObservationIgnored private var failing = false
    @ObservationIgnored private var problem = ""
    @ObservationIgnored private var playing = false
    @ObservationIgnored private var lastSound = -100.0     // 上次收到声音的时间（开机以来的秒数）
    @ObservationIgnored private var holdUntil = 0.0        // 正在打电话之类开不了声音：到这个时间前先不试
    @ObservationIgnored private var shown = Phase.connecting

    // 只在自己的手机上能听的那几种输出：插着、连着的耳机；手机自己的扬声器不算
    private static let ears: Set<AVAudioSession.Port> = [.bluetoothA2DP, .bluetoothLE, .bluetoothHFP, .headphones, .usbAudio]

    // 设置页“在手机上收听本机声音”给的网址：编进了 App（Config.xcconfig 的 TAILORBIT_URL）；启动参数 -url 可以临时换一个（试的时候用）。
    // 剪贴板互通的网址是把里面的 /a/ 换成 /c/（两个共用一个随机码）
    static let base: URL? = {
        let raw = UserDefaults.standard.string(forKey: "url") ?? Bundle.main.object(forInfoDictionaryKey: "TailOrbitURL") as? String ?? ""
        return URL(string: raw.trimmingCharacters(in: .whitespacesAndNewlines)).flatMap { $0.host() == nil ? nil : $0 }
    }()

    init() {
        address = Listener.base
        computer = address?.host()?.split(separator: ".").first.map(String.init) ?? ""
        let on = UserDefaults.standard.object(forKey: "auto") as? Bool ?? true
        auto = on
        want = on
        #if targetEnvironment(simulator)
        path = NWPathMonitor()   // 模拟器走 Mac 的网，不分是不是 Wi-Fi
        #else
        path = NWPathMonitor(requiredInterfaceType: .wifi)
        #endif

        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 10   // 心跳 2 秒一个：10 秒什么都没收到就当断了
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.urlCache = nil
        let callbacks = OperationQueue()
        callbacks.maxConcurrentOperationCount = 1
        callbacks.underlyingQueue = queue
        let feed = Feed()
        feed.opened = { [unowned self] t, response in self.opened(t, response) }
        feed.received = { [unowned self] t, data in self.received(t, data) }
        feed.closed = { [unowned self] t, error in self.closed(t, error) }
        session = URLSession(configuration: config, delegate: feed, delegateQueue: callbacks)

        Keeper.shared.onDenied = { [unowned self] denied in self.needsLocation = denied }
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .default, options: [.duckOthers])
        let center = NotificationCenter.default
        center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: nil) { [unowned self] _ in
            self.queue.async { self.lookRoute() }
        }
        center.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: nil) { [unowned self] note in
            guard (note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt) == AVAudioSession.InterruptionType.began.rawValue else { return }
            self.queue.async { self.stopPlaying("interrupted") }   // 来电之类：系统已经停了声音，电脑再出声时重开
        }
        center.addObserver(forName: .AVAudioEngineConfigurationChange, object: nil, queue: nil) { [unowned self] _ in
            self.queue.async { self.stopPlaying("output changed", hold: 0.3) }   // 换了输出（采样率可能变了）：下一段声音来了按新的重开
        }
        center.addObserver(forName: AVAudioSession.mediaServicesWereResetNotification, object: nil, queue: nil) { [unowned self] _ in
            self.queue.async { self.stopPlaying("media services reset", hold: 0.3) }
        }
        path.pathUpdateHandler = { [unowned self] p in self.network(p.status == .satisfied) }
        path.start(queue: queue)
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2, leeway: .seconds(1))
        timer.setEventHandler { [unowned self] in self.check() }
        timer.resume()
        tick = timer
        Keeper.shared.hold("sound", on)
        queue.async { self.lookRoute(); self.evaluate() }
    }

    var status: String {
        switch phase {
        case .off: return String(localized: "自动收听已关闭")
        case .noAddress: return String(localized: "没有设置电脑的地址")
        case .noWifi: return String(localized: "连上 Wi-Fi 才会收听")
        case .noHeadphones: return String(localized: "戴上耳机就能听")
        case .connecting: return String(localized: "正在连接电脑…")
        case .failed(let why): return why.isEmpty ? String(localized: "连不上电脑") : why
        case .waiting: return String(localized: "等电脑出声")
        case .playing: return String(localized: "正在播放")
        }
    }

    // ---------- 下面都在 queue 上 ----------

    private var now: Double { ProcessInfo.processInfo.systemUptime }

    // 该连着就连，不该连就断
    private func evaluate() {
        if want && address != nil && wifi && phones {
            if task == nil && retry == nil { connect() }
        } else {
            retry?.cancel()
            retry = nil
            wait = 1
            failing = false
            if let t = task { task = nil; t.cancel() }
            stopPlaying("not listening")
        }
        show()
    }

    private func network(_ ok: Bool) {
        if ok && !wifi { retry?.cancel(); retry = nil; wait = 1 }   // 刚连上 Wi-Fi：马上连，不等重试
        wifi = ok
        evaluate()
    }

    private func lookRoute() {
        #if targetEnvironment(simulator)
        let on = true   // 模拟器只有 Mac 的扬声器：当成戴着耳机
        #else
        let on = AVAudioSession.sharedInstance().currentRoute.outputs.contains { Listener.ears.contains($0.portType) }
        #endif
        guard on != phones else { return }
        phones = on
        note(on ? "headphones on" : "headphones off")
        evaluate()
    }

    // 每 2 秒：耳机还在不在（后台时换耳机的通知未必都收得到）；电脑安静 8 秒了就停下、把耳机让出来
    private func check() {
        lookRoute()
        guard playing else { return }
        if !player.running || now - lastSound > 8 { stopPlaying("quiet") }
        let s = player.stats
        note(String(format: "buffer %.0f ms, target %.0f ms", s.buffered, s.target))
    }

    private func connect() {
        guard let address else { return }
        var request = URLRequest(url: address.appending(path: "pcm"))
        request.setValue("1", forHTTPHeaderField: "X-TailOrbit-App")   // 电脑那边就不弹“正在收听”的通知
        code = 0
        pending.removeAll(keepingCapacity: true)
        let t = session.dataTask(with: request)
        task = t
        t.resume()
    }

    private func opened(_ t: URLSessionDataTask, _ response: URLResponse) -> URLSession.ResponseDisposition {
        guard t === task else { return .cancel }
        let http = response as? HTTPURLResponse
        code = http?.statusCode ?? 0
        if code == 200 {
            let rate = Double(http?.value(forHTTPHeaderField: "X-Rate") ?? "") ?? 48000
            player.reset(rate: rate)
            wait = 1
            failing = false
            note("connected, \(Int(rate)) Hz")
        }
        show()
        return .allow
    }

    private func received(_ t: URLSessionDataTask, _ data: Data) {
        guard t === task else { return }
        pending.append(contentsOf: data)
        guard code == 200 else {   // 出错时正文是电脑那边说的原因，连接结束时显示
            if pending.count > 2048 { t.cancel() }
            return
        }
        var at = 0, sound = false, bad = false
        pending.withUnsafeBytes { raw in
            while raw.count - at >= 4 {
                let frames = Int(UInt32(littleEndian: raw.loadUnaligned(fromByteOffset: at, as: UInt32.self)))
                if frames == 0 { player.quiet(); at += 4; continue }
                if frames > 1 << 18 { bad = true; return }   // 一条 5 秒以上：不是声音流，别一直攒下去
                guard raw.count - at - 4 >= frames * 4 else { break }
                player.push(UnsafeRawBufferPointer(rebasing: raw[at + 4 ..< at + 4 + frames * 4]))
                at += 4 + frames * 4
                sound = true
            }
        }
        if bad { code = 0; t.cancel(); return }
        pending.removeFirst(at)
        if sound { heard() }
    }

    private func closed(_ t: URLSessionTask, _ error: Error?) {
        guard t === task else { return }
        task = nil
        let ok = code == 200   // 连着的断了（电脑换了声音设备、网络闪断）：马上重连
        if !ok {
            failing = true
            if code == 502 || code == 504 { problem = String(localized: "电脑上的 TailOrbit 没有运行") }
            else if code != 0 { problem = String(decoding: pending, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines) }
            else if let e = error as? URLError, e.code == .cannotFindHost || e.code == .dnsLookupFailed { problem = String(localized: "找不到电脑：手机上的 Tailscale 开着吗？") }
            else { problem = "" }
        }
        note("closed \(code): \(error.map { "\($0.localizedDescription)" } ?? "end")")
        let delay = ok ? 0.5 : wait
        if !ok { wait = min(wait * 2, 15) }
        let w = DispatchWorkItem { [unowned self] in self.retry = nil; self.evaluate() }
        retry = w
        queue.asyncAfter(deadline: .now() + delay, execute: w)
        show()
    }

    // 收到一段声音：没在放就开始放
    private func heard() {
        lastSound = now
        guard !playing, phones, now >= holdUntil else { return }
        do {
            try player.start()
            playing = true
            note("playing")
        } catch {
            holdUntil = now + 2
            note("can't play: \(error.localizedDescription)")
        }
        show()
    }

    private func stopPlaying(_ why: String, hold: Double = 0) {
        guard playing else { return }
        player.stop()
        playing = false
        holdUntil = now + hold
        note("stopped: \(why)")
        show()
    }

    private func show() {
        let p: Phase
        if !want { p = .off }
        else if address == nil { p = .noAddress }
        else if !wifi { p = .noWifi }
        else if !phones { p = .noHeadphones }
        else if playing { p = .playing }
        else if task != nil && code == 200 { p = .waiting }
        else if failing { p = .failed(problem) }
        else { p = .connecting }
        guard p != shown else { return }
        shown = p
        DispatchQueue.main.async { self.phase = p }
    }
}

/// 调试日志：模拟器、devicectl --console 里看得到
func note(_ text: String) {
    FileHandle.standardError.write(Data(String(format: "[TailOrbit %.2f] %@\n", ProcessInfo.processInfo.systemUptime, text).utf8))
}

/// URLSession 的回调转给闭包（Listener、Clips 不是 NSObject，隔一层）
final class Feed: NSObject, URLSessionDataDelegate {
    var opened: (URLSessionDataTask, URLResponse) -> URLSession.ResponseDisposition = { _, _ in .allow }
    var received: (URLSessionDataTask, Data) -> Void = { _, _ in }
    var closed: (URLSessionTask, Error?) -> Void = { _, _ in }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        completionHandler(opened(dataTask, response))
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        received(dataTask, data)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        closed(task, error)
    }
}
