import Network
import Observation
import UIKit
import UniformTypeIdentifiers

/// 和电脑共用剪贴板（电脑那边是 ClipShare.cs，TailOrbit 设置页的“剪贴板互通”要开着）。
/// 一直连着电脑的 …/c/<随机码>/watch：电脑一复制，文字或图片就放进手机剪贴板，App 在后台也写得进去（10-28 真机实测）。
/// 万一哪次没写进去就记着，App 一回到前台马上补上；这期间手机上又复制了别的就不补，免得盖掉。
/// 手机这边由人点系统的“粘贴”按钮把手机剪贴板发给电脑：iOS 不许 App 在后台读剪贴板，这个按钮也不会弹“允许粘贴”。
/// 推送流一条一条：1 字节种类（0 心跳、1 文字、2 PNG）、1 字节标记（1 是密码、2 是刚连上时补的）、4 字节小端长度、内容。
/// 网络回调都排在 queue 上；界面看的属性和剪贴板只在主线程上碰。
@Observable
final class Clips {
    /// 电脑最近复制过来的那份（界面上显示；图片只留缩略图）
    struct Copy {
        let text: String?
        let image: UIImage?
        let secret: Bool
        let at: Date
    }

    enum Sending: Equatable { case idle, busy, done, failed(String) }

    private(set) var latest: Copy?
    private(set) var problem: String?
    private(set) var sending = Sending.idle
    var on: Bool {
        didSet {
            UserDefaults.standard.set(on, forKey: "clips")
            let v = on
            Keeper.shared.hold("clip", v)
            queue.async { self.want = v; self.evaluate() }
        }
    }

    private let queue = DispatchQueue(label: "TailOrbit.clips")
    private let address: URL?
    private let path = NWPathMonitor()
    @ObservationIgnored private var session: URLSession!
    @ObservationIgnored private var want: Bool
    @ObservationIgnored private var online = false
    @ObservationIgnored private var task: URLSessionDataTask?
    @ObservationIgnored private var code = 0
    @ObservationIgnored private var pending = [UInt8]()
    @ObservationIgnored private var retry: DispatchWorkItem?
    @ObservationIgnored private var wait = 1.0
    @ObservationIgnored private var shownProblem: String?
    @ObservationIgnored private var owed: (kind: UInt8, secret: Bool, body: Data, count: Int)?   // 主线程上：后台没写进去的那份

    init() {
        address = Listener.base.flatMap { URL(string: $0.absoluteString.replacingOccurrences(of: "/a/", with: "/c/")) }
        let on = UserDefaults.standard.object(forKey: "clips") as? Bool ?? true
        self.on = on
        want = on

        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60   // 心跳 25 秒一个：一分钟什么都没收到就当断了
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

        path.pathUpdateHandler = { [unowned self] p in self.network(p.status == .satisfied) }
        path.start(queue: queue)
        NotificationCenter.default.addObserver(forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main) { [unowned self] _ in
            self.settle()
        }
        Keeper.shared.hold("clip", on)
        #if targetEnvironment(simulator)
        // 模拟器上试“发到电脑”：没法替人点系统的粘贴按钮，启动参数 -send 给一段文字、-sendImage YES 发一张小图，照样走一遍
        if let text = UserDefaults.standard.string(forKey: "send") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { self.send([NSItemProvider(object: text as NSString)]) }
        }
        if UserDefaults.standard.bool(forKey: "sendImage") {
            let image = UIGraphicsImageRenderer(size: CGSize(width: 40, height: 30)).image { c in
                UIColor.systemTeal.setFill()
                c.fill(CGRect(x: 0, y: 0, width: 40, height: 30))
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { self.send([NSItemProvider(object: image)]) }
        }
        #endif
    }

    /// 系统“粘贴”按钮给的手机剪贴板发到电脑：有图片发图片（一律转成 PNG），没有就发文字（链接也当文字）
    @MainActor
    func send(_ providers: [NSItemProvider]) {
        guard let address, let provider = providers.first, sending != .busy else { return }
        sending = .busy
        Task {
            let result: Sending
            do {
                let (body, type) = try await Clips.load(provider)
                var request = URLRequest(url: address.appending(path: "raw"))
                request.httpMethod = "POST"
                request.setValue(type, forHTTPHeaderField: "Content-Type")
                request.timeoutInterval = 60
                let (_, response) = try await URLSession.shared.upload(for: request, from: body)
                let code = (response as? HTTPURLResponse)?.statusCode ?? 0
                result = code == 200 || code == 204 ? .done : .failed(Clips.reason(code, nil))
                note("clipboard out: \(type) \(body.count) bytes -> \(code)")
            } catch {
                result = .failed(Clips.reason(0, error))
                note("clipboard out failed: \(error.localizedDescription)")
            }
            await MainActor.run { self.sending = result }
            try? await Task.sleep(nanoseconds: 2_500_000_000)
            await MainActor.run { if self.sending == result { self.sending = .idle } }
        }
    }

    private static func load(_ p: NSItemProvider) async throws -> (Data, String) {
        if p.hasItemConformingToTypeIdentifier(UTType.image.identifier) {
            let data: Data = try await withCheckedThrowingContinuation { c in
                _ = p.loadDataRepresentation(for: .image) { d, e in
                    if let d { c.resume(returning: d) } else { c.resume(throwing: e ?? CocoaError(.fileReadUnknown)) }
                }
            }
            if data.starts(with: [0x89, 0x50, 0x4E, 0x47]) { return (data, "image/png") }
            guard let png = UIImage(data: data)?.pngData() else { throw CocoaError(.fileReadCorruptFile) }
            return (png, "image/png")
        }
        if p.canLoadObject(ofClass: String.self) {
            let text: String = try await withCheckedThrowingContinuation { c in
                _ = p.loadObject(ofClass: String.self) { s, e in
                    if let s { c.resume(returning: s) } else { c.resume(throwing: e ?? CocoaError(.fileReadUnknown)) }
                }
            }
            return (Data(text.utf8), "text/plain; charset=utf-8")
        }
        let url: URL = try await withCheckedThrowingContinuation { c in
            _ = p.loadObject(ofClass: URL.self) { u, e in
                if let u { c.resume(returning: u) } else { c.resume(throwing: e ?? CocoaError(.fileReadUnknown)) }
            }
        }
        return (Data(url.absoluteString.utf8), "text/plain; charset=utf-8")
    }

    private static func reason(_ code: Int, _ error: Error?) -> String {
        switch code {
        case 403: return String(localized: "只有同一个 Tailscale 账号的设备能用")
        case 404: return String(localized: "电脑上的剪贴板互通没打开")
        case 413: return String(localized: "太大了，电脑不收")
        case 415: return String(localized: "电脑认不出这张图")
        case 502, 504: return String(localized: "电脑上的 TailOrbit 没有运行")
        case 503: return String(localized: "电脑的剪贴板正忙，再试一次")
        default: break
        }
        if let e = error as? URLError, e.code == .cannotFindHost || e.code == .dnsLookupFailed {
            return String(localized: "找不到电脑：手机上的 Tailscale 开着吗？")
        }
        return String(localized: "连不上电脑")
    }

    // ---------- 主线程：剪贴板 ----------

    // 手机剪贴板。模拟器上换成一个自己起名的：模拟器会把系统剪贴板同步给 Mac，Mac 又可能和电脑共用剪贴板，试的内容别传到电脑上
    private static let board: UIPasteboard = {
        #if targetEnvironment(simulator)
        return UIPasteboard(name: UIPasteboard.Name("TailOrbit.test"), create: true) ?? .general
        #else
        return .general
        #endif
    }()

    // 电脑复制来的：放进手机剪贴板，界面上显示一下
    private func take(_ kind: UInt8, _ secret: Bool, _ body: Data) {
        let before = Clips.board.changeCount
        Clips.write(kind, secret, body)
        if Clips.board.changeCount == before {
            owed = (kind, secret, body, before)
            note("clipboard in: can't write now, will on return")
        } else {
            owed = nil
            note("clipboard in: \(kind == 1 ? "text" : "image") \(body.count) bytes")
        }
        latest = Copy(text: kind == 1 ? String(decoding: body, as: UTF8.self) : nil, image: kind == 2 ? Clips.thumbnail(body) : nil,
                      secret: secret, at: Date())
    }

    // 回到前台：后台没写进去的补上；这期间手机上又复制了别的就算了
    private func settle() {
        guard let o = owed else { return }
        owed = nil
        guard Clips.board.changeCount == o.count else { return }
        Clips.write(o.kind, o.secret, o.body)
        note("clipboard in: written on return")
    }

    // 密码只放这台手机（不经“通用剪贴板”同步到别的苹果设备），两分钟后自动清掉
    private static func write(_ kind: UInt8, _ secret: Bool, _ body: Data) {
        let item: [String: Any] = kind == 1 ? [UTType.utf8PlainText.identifier: String(decoding: body, as: UTF8.self)] : [UTType.png.identifier: body]
        board.setItems([item], options: secret ? [.localOnly: true, .expirationDate: Date(timeIntervalSinceNow: 120)] : [:])
    }

    private static func thumbnail(_ png: Data) -> UIImage? {
        guard let image = UIImage(data: png), image.size.width > 0, image.size.height > 0 else { return nil }
        let scale = min(1, 160 / max(image.size.width, image.size.height))
        return image.preparingThumbnail(of: CGSize(width: image.size.width * scale, height: image.size.height * scale))
    }

    // ---------- 下面都在 queue 上：推送流 ----------

    private func evaluate() {
        if want && address != nil && online {
            if task == nil && retry == nil { connect() }
        } else {
            retry?.cancel()
            retry = nil
            wait = 1
            if let t = task { task = nil; t.cancel() }
            report(nil)
        }
    }

    private func network(_ ok: Bool) {
        if ok && !online { retry?.cancel(); retry = nil; wait = 1 }   // 刚连上网：马上连，不等重试
        online = ok
        evaluate()
    }

    private func connect() {
        guard let address else { return }
        code = 0
        pending.removeAll(keepingCapacity: true)
        let t = session.dataTask(with: URLRequest(url: address.appending(path: "watch")))
        task = t
        t.resume()
    }

    private func opened(_ t: URLSessionDataTask, _ response: URLResponse) -> URLSession.ResponseDisposition {
        guard t === task else { return .cancel }
        code = (response as? HTTPURLResponse)?.statusCode ?? 0
        if code == 200 {
            wait = 1
            report(nil)
            note("clipboard connected")
        }
        return .allow
    }

    private func received(_ t: URLSessionDataTask, _ data: Data) {
        guard t === task else { return }
        pending.append(contentsOf: data)
        guard code == 200 else {   // 出错时的正文用不上，等连接结束
            if pending.count > 4096 { t.cancel() }
            return
        }
        var at = 0
        while pending.count - at >= 6 {
            let kind = pending[at], flags = pending[at + 1]
            let size = Int(pending[at + 2]) | Int(pending[at + 3]) << 8 | Int(pending[at + 4]) << 16 | Int(pending[at + 5]) << 24
            if kind > 2 || size > 64 << 20 { code = 0; t.cancel(); return }   // 不像是推送流：别一直攒下去
            guard pending.count - at - 6 >= size else { break }
            if kind != 0 {
                let body = Data(pending[at + 6 ..< at + 6 + size])
                DispatchQueue.main.async { self.take(kind, flags & 1 != 0, body) }
            }
            at += 6 + size
        }
        pending.removeFirst(at)
    }

    private func closed(_ t: URLSessionTask, _ error: Error?) {
        guard t === task else { return }
        task = nil
        let ok = code == 200   // 连着的断了（换网、电脑重启 TailOrbit）：马上重连
        if !ok { report(Clips.reason(code, error)) }
        note("clipboard closed \(code): \(error.map { $0.localizedDescription } ?? "end")")
        let delay = ok ? 0.5 : wait
        if !ok { wait = min(wait * 2, 30) }
        let w = DispatchWorkItem { [unowned self] in self.retry = nil; self.evaluate() }
        retry = w
        queue.asyncAfter(deadline: .now() + delay, execute: w)
    }

    private func report(_ p: String?) {
        guard p != shownProblem else { return }
        shownProblem = p
        DispatchQueue.main.async { self.problem = p }
    }
}
