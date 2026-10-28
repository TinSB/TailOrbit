import SwiftUI

// TailOrbit 的 iPhone 配套 App：在后台连着电脑上的 TailOrbit，电脑一出声就从戴着的耳机里放出来（Listener.swift），
// 电脑一复制就放进手机剪贴板（Clips.swift）
@main
struct TailOrbitApp: App {
    @State private var listener = Listener()
    @State private var clips = Clips()

    var body: some Scene {
        WindowGroup {
            ListenView(listener: listener, clips: clips)
        }
    }
}
