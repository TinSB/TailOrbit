import AVFoundation
import os

/// 把电脑送来的声音放出来：一个 AVAudioEngine，上面一个自己出声的节点，系统每次要多少帧就从缓冲（Jitter）里取多少。
/// 网络那边往缓冲里放、音频线程取、界面读响度，都在 lock 底下，每次只占一小会儿。start、stop 只在 Listener 的 queue 上调。
final class Player {
    private let lock = OSAllocatedUnfairLock()
    private let jitter = Jitter()
    private var engine: AVAudioEngine?
    #if targetEnvironment(simulator)
    private var clock: DispatchSourceTimer?
    #endif

    var running: Bool { engine?.isRunning ?? false }
    /// 攒着的、要攒的（毫秒）
    var stats: (buffered: Double, target: Double) { locked { (jitter.buffered, jitter.target * 1000) } }

    func reset(rate: Double) { locked { jitter.reset(rate: rate) } }
    func quiet() { locked { jitter.idle = true } }
    func push(_ pcm: UnsafeRawBufferPointer) { locked { jitter.write(pcm) } }
    /// 上次取到现在放出去的最大振幅（0～1），界面跟着跳
    func takeLevel() -> Float { locked { jitter.takePeak() } }

    /// 占用声音并开始放：手机上别的声音压低、不打断（像导航播报）
    func start() throws {
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playback, mode: .default, options: [.duckOthers])
        try? session.setPreferredIOBufferDuration(0.01)
        try session.setActive(true)
        let engine = AVAudioEngine()
        #if targetEnvironment(simulator)
        // 模拟器借 Mac 的声音设备，Mac 那边放不了声音时一碰就卡死；模拟器上不出声，按真实时间自己取（照样走缓冲、换算），只为试流程
        guard let format = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2) else { throw CocoaError(.featureUnsupported) }
        try engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: 4096)
        #else
        let hardware = engine.outputNode.outputFormat(forBus: 0).sampleRate
        guard let format = AVAudioFormat(standardFormatWithSampleRate: hardware > 0 ? hardware : 48000, channels: 2) else {
            throw CocoaError(.featureUnsupported)
        }
        #endif
        let rate = format.sampleRate, lock = lock, jitter = jitter
        let node = AVAudioSourceNode(format: format) { _, _, frames, list in
            let buffers = UnsafeMutableAudioBufferListPointer(list)
            guard buffers.count == 2, let left = buffers[0].mData?.assumingMemoryBound(to: Float.self),
                  let right = buffers[1].mData?.assumingMemoryBound(to: Float.self) else { return noErr }
            lock.lock()
            jitter.render(left, right, Int(frames), rate)
            lock.unlock()
            return noErr
        }
        engine.attach(node)
        engine.connect(node, to: engine.mainMixerNode, format: format)
        do {
            try engine.start()
        } catch {
            try? session.setActive(false, options: .notifyOthersOnDeactivation)
            throw error
        }
        self.engine = engine
        #if targetEnvironment(simulator)
        guard let sink = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: 4096) else { return }
        let timer = DispatchSource.makeTimerSource(queue: DispatchQueue(label: "TailOrbit.fake-output", qos: .userInteractive))
        var last = ProcessInfo.processInfo.systemUptime
        timer.schedule(deadline: .now(), repeating: .milliseconds(10))
        timer.setEventHandler {
            let now = ProcessInfo.processInfo.systemUptime
            let frames = AVAudioFrameCount(min(4096, max(0, (now - last) * rate)))
            last += Double(frames) / rate
            if frames > 0 { _ = try? engine.renderOffline(frames, to: sink) }
        }
        timer.resume()
        clock = timer
        #endif
    }

    /// 停下，把声音还给别的 App（被压低的恢复原来的音量）
    func stop() {
        #if targetEnvironment(simulator)
        clock?.cancel()
        clock = nil
        #endif
        engine?.stop()
        engine = nil
        locked { jitter.halt() }
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }
}
