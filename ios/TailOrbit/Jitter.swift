import Foundation

/// 收到的声音先放进约 2.7 秒的环形缓冲，攒够目标量才开始放。照搬网页那段 AudioWorklet（SoundShare.cs 的 Worklet）：
/// 目标从 80 毫秒起，放空了（网络抖）加 30 毫秒、最多 400；稳了 20 秒减 10 毫秒、最少 50；电脑说了“安静”之后放空不算。
/// 按平均攒量微调播放速度（最多快慢 0.5%，听不出来）追平两边时钟的差；一下子涌来一大堆就直接跳到目标位置；
/// 采样率和手机不一样时顺带换算（三次插值）。和网页不同的是 iPhone 每次要的帧数不固定（锁屏后会大很多），
/// 所以目标至少比一次要的多 30 毫秒。不自己加锁：Player 锁好了再调。
final class Jitter {
    private let size = 1 << 17
    private let left: UnsafeMutablePointer<Float>
    private let right: UnsafeMutablePointer<Float>
    private var rate = 48000.0       // 电脑送来的采样率
    private var w = 0                // 写到第几帧
    private var r = 0.0              // 读到哪（带小数）
    private var on = false           // 正在放（攒够了）
    var idle = false                 // 电脑说了“安静”
    private(set) var target = 0.08   // 秒
    private var avg = 0.0
    private var calm = 0
    private var peak: Float = 0
    private(set) var buffered = 0.0  // 毫秒

    init() {
        left = .allocate(capacity: size)
        left.initialize(repeating: 0, count: size)
        right = .allocate(capacity: size)
        right.initialize(repeating: 0, count: size)
    }

    deinit {
        left.deallocate()
        right.deallocate()
    }

    func reset(rate: Double) {
        self.rate = rate
        w = 0
        r = 0
        on = false
        idle = false
    }

    func halt() {
        on = false
        peak = 0
    }

    func takePeak() -> Float {
        defer { peak = 0 }
        return peak
    }

    // 16 位立体声（小端、左右交错）
    func write(_ pcm: UnsafeRawBufferPointer) {
        idle = false
        let mask = size - 1
        for i in 0 ..< pcm.count / 4 {
            left[w & mask] = Float(Int16(littleEndian: pcm.loadUnaligned(fromByteOffset: i * 4, as: Int16.self))) / 32768
            right[w & mask] = Float(Int16(littleEndian: pcm.loadUnaligned(fromByteOffset: i * 4 + 2, as: Int16.self))) / 32768
            w += 1
        }
        if Double(w) - r > Double(size - 1024) { r = Double(w - size + 1024) }
    }

    // 填满这一次要的 q 帧（outRate 是手机这边的采样率）；没攒够就填静音
    func render(_ outL: UnsafeMutablePointer<Float>, _ outR: UnsafeMutablePointer<Float>, _ q: Int, _ outRate: Double) {
        let base = rate / outRate
        let t = max(target * rate, Double(q) * base + rate * 0.03)
        var have = Double(w) - r
        if !on && have >= t { on = true; avg = have }
        if on && have > t * 3 + rate * 0.05 { r = Double(w) - t; have = t; avg = t }
        avg += (have - avg) * (1 - pow(0.98, Double(q) / 128))   // 网页每 128 帧平滑 2%，这里按帧数折算
        let speed = base * (1 + min(0.005, max(-0.005, (avg - t) / t * 0.02)))
        buffered = have / rate * 1000
        if !on || have < Double(q) * speed + 4 {
            if on {
                on = false
                if !idle { target = min(0.4, target + 0.03); calm = 0 }
            }
            outL.update(repeating: 0, count: q)
            outR.update(repeating: 0, count: q)
            return
        }
        let mask = size - 1
        var p = r
        for j in 0 ..< q {
            let a = Int(p.rounded(.down)), f = Float(p - Double(a))
            let x = hermite(left, a, f, mask), y = hermite(right, a, f, mask)
            outL[j] = x
            outR[j] = y
            peak = max(peak, abs(x), abs(y))
            p += speed
        }
        r = p
        calm += q
        if Double(calm) > outRate * 20 { calm = 0; target = max(0.05, target - 0.01) }
    }
}

private func hermite(_ b: UnsafeMutablePointer<Float>, _ a: Int, _ f: Float, _ mask: Int) -> Float {
    let y0 = b[(a - 1) & mask], y1 = b[a & mask], y2 = b[(a + 1) & mask], y3 = b[(a + 2) & mask]
    let c1 = 0.5 * (y2 - y0), c2 = y0 - 2.5 * y1 + 2 * y2 - 0.5 * y3, c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2)
    return ((c3 * f + c2) * f + c1) * f + y1
}
