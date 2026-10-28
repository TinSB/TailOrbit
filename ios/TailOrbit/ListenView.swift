import SwiftUI
import UIKit

/// 一屏：中间是图标那颗“轨道”，四周一圈刻度跟着声音跳，底下一行状态；再往下是剪贴板和两个开关
struct ListenView: View {
    @Bindable var listener: Listener
    @Bindable var clips: Clips
    @Environment(\.colorScheme) private var scheme
    @Environment(\.openURL) private var openURL
    @State private var orbit = Orbit()

    var body: some View {
        let ink = Ink(dark: scheme == .dark)
        let lively = listener.phase == .playing || listener.phase == .waiting
        ZStack {
            RadialGradient(colors: [ink.glow, ink.stage], center: UnitPoint(x: 0.5, y: 0.42), startRadius: 0, endRadius: 560)
                .ignoresSafeArea()
            VStack(spacing: 0) {
                if !listener.computer.isEmpty {
                    Text("\(listener.computer) 的声音")
                        .font(.headline)
                        .foregroundStyle(ink.ring)
                        .padding(.top, 16)
                }
                Spacer(minLength: 0)
                TimelineView(.animation(minimumInterval: 1.0 / 60, paused: listener.phase == .off)) { timeline in
                    Canvas { context, size in
                        orbit.draw(&context, size: size, now: timeline.date, phase: listener.phase, level: listener.player.takeLevel(), ink: ink)
                    }
                }
                .frame(minWidth: 220, maxWidth: 340, minHeight: 220, maxHeight: 340)
                .aspectRatio(1, contentMode: .fit)
                Text(listener.status)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(lively ? ink.dot : ink.ring)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 32)
                    .frame(minHeight: 64, alignment: .top)
                    .animation(.easeOut(duration: 0.2), value: listener.status)
                Spacer(minLength: 0)
                if listener.needsLocation {
                    Button {
                        if let url = URL(string: UIApplication.openSettingsURLString) { openURL(url) }
                    } label: {
                        Label("允许定位，才能在后台等电脑出声", systemImage: "location.fill")
                            .font(.subheadline.weight(.medium))
                            .padding(.horizontal, 16)
                            .padding(.vertical, 10)
                            .background(ink.dot.opacity(0.1), in: Capsule())
                    }
                    .foregroundStyle(ink.dot)
                    .padding(.bottom, 14)
                }
                VStack(spacing: 12) {
                    if clips.on { ClipCard(clips: clips, ink: ink) }
                    VStack(spacing: 0) {
                        Toggle("自动收听", isOn: $listener.auto)
                            .padding(.horizontal, 20)
                            .padding(.vertical, 13)
                        Divider().padding(.leading, 20)
                        Toggle("共用剪贴板", isOn: $clips.on)
                            .padding(.horizontal, 20)
                            .padding(.vertical, 13)
                    }
                    .font(.body.weight(.medium))
                    .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
                }
                .padding(.horizontal, 20)
                .padding(.bottom, 12)
                .animation(.spring(duration: 0.35), value: clips.on)
            }
        }
    }
}

/// 剪贴板：上面一行是电脑刚复制过来的（已经放进手机剪贴板了），下面一行把手机剪贴板发到电脑（系统的粘贴按钮，不弹“允许粘贴”）
private struct ClipCard: View {
    let clips: Clips
    let ink: Ink

    var body: some View {
        VStack(spacing: 0) {
            if let problem = clips.problem {
                Label(problem, systemImage: "exclamationmark.triangle.fill")
                    .font(.subheadline)
                    .foregroundStyle(ink.ring)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 20)
                    .padding(.vertical, 12)
                Divider().padding(.leading, 20)
            } else if let copy = clips.latest {
                HStack(spacing: 12) {
                    if let image = copy.image {
                        Image(uiImage: image)
                            .resizable()
                            .scaledToFill()
                            .frame(width: 40, height: 40)
                            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                    } else {
                        Image(systemName: "desktopcomputer")
                            .font(.title3)
                            .foregroundStyle(ink.ring)
                            .frame(width: 40, height: 40)
                    }
                    Text(copy.secret ? "••••••••" : copy.text.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) } ?? String(localized: "图片"))
                        .font(.subheadline)
                        .foregroundStyle(ink.dot)
                        .lineLimit(2)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 10)
                .id(copy.at)
                .transition(.asymmetric(insertion: .move(edge: .top).combined(with: .opacity), removal: .opacity))
                Divider().padding(.leading, 20)
            }
            HStack(spacing: 12) {
                state
                    .font(.body.weight(.medium))
                    .frame(maxWidth: .infinity, alignment: .leading)
                PasteButton(supportedContentTypes: [.image, .plainText, .url]) { providers in
                    clips.send(providers)
                }
                .buttonBorderShape(.capsule)
                .labelStyle(.titleAndIcon)
                .disabled(clips.sending == .busy)
            }
            .padding(.leading, 20)
            .padding(.trailing, 14)
            .padding(.vertical, 10)
        }
        .clipped()
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        .animation(.spring(duration: 0.4), value: clips.latest?.at)
        .animation(.easeOut(duration: 0.2), value: clips.sending)
    }

    @ViewBuilder private var state: some View {
        switch clips.sending {
        case .idle:
            Label("发到电脑", systemImage: "iphone.and.arrow.forward")
        case .busy:
            HStack(spacing: 8) {
                ProgressView()
                Text("正在发到电脑…")
            }
        case .done:
            Label("已发到电脑", systemImage: "checkmark.circle.fill")
                .foregroundStyle(.green)
        case .failed(let why):
            Label(why, systemImage: "exclamationmark.circle.fill")
                .foregroundStyle(.orange)
                .lineLimit(2)
        }
    }
}

/// 和程序图标同一套灰：深色模式深底亮点，浅色模式浅底黑点；halo 是放声音时中间那圈光的浓淡（浅底上淡一些，不然像脏阴影）
struct Ink {
    let stage, glow, ring, dot: Color
    let halo: Double

    init(dark: Bool) {
        halo = dark ? 0.35 : 0.16
        stage = Color(hex: dark ? 0x0E0F11 : 0xDDE0E4)
        glow = Color(hex: dark ? 0x24272C : 0xF7F8FA)
        ring = Color(hex: dark ? 0x8C929A : 0x80868E)
        dot = Color(hex: dark ? 0xE7E9EC : 0x1A1C1F)
    }
}

private extension Color {
    init(hex: UInt32) {
        self.init(red: Double(hex >> 16 & 0xFF) / 255, green: Double(hex >> 8 & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }
}

/// 中间那颗“轨道”每一帧的样子。放声音：开始的那一下两圈波纹往外扩，卫星绕着转、越响越快，中间的点跟着响度鼓起来，
/// 四周刻度把响度从顶上往下流。等电脑出声：卫星停回右上（和图标一样），中间的点慢慢呼吸。正在连接：卫星慢慢转。
/// 其余状态整体变暗。
final class Orbit {
    private var history = [Float](repeating: 0, count: 36)   // 最近每一帧的响度，最新的在前
    private var energy: Float = 0
    private var angle = Orbit.park                            // 卫星的位置：从正上方顺时针转过的弧度
    private var light = 0.4
    private var last: Date?
    private var burst: Date?
    private var wasPlaying = false
    private static let park = 41.0 * Double.pi / 180

    func draw(_ ctx: inout GraphicsContext, size: CGSize, now: Date, phase: Listener.Phase, level: Float, ink: Ink) {
        let dt = min(0.1, last.map { now.timeIntervalSince($0) } ?? 0)
        last = now
        let playing = phase == .playing
        if playing && !wasPlaying { burst = now }
        wasPlaying = playing

        // 振幅换成 0～1 的响度：-50dB 以下算 0，-6dB 以上算 1；涨得快、落得慢
        let loud: Float = playing && level > 0 ? max(0, min(1, (20 * log10(level) + 50) / 44)) : 0
        energy += (loud - energy) * (loud > energy ? 0.5 : 0.1)
        history.removeLast()
        history.insert(energy, at: 0)
        light += ((playing || phase == .waiting ? 1 : 0.38) - light) * min(1, dt * 6)

        switch phase {
        case .playing:
            angle += dt * (2 * .pi / 6) * (1 + 3 * Double(energy))
        case .connecting:
            angle += dt * (2 * .pi / 2.4)
        default:   // 顺着转回右上，越近越慢
            var ahead = (Orbit.park - angle).truncatingRemainder(dividingBy: 2 * .pi)
            if ahead < 0 { ahead += 2 * .pi }
            angle += min(ahead, max(0.6, ahead * 4) * dt)
        }
        angle = angle.truncatingRemainder(dividingBy: 2 * .pi)

        let side = min(size.width, size.height)
        let cx = size.width / 2, cy = size.height / 2
        let ring = side * 0.27, ringWidth = side * 0.024
        let satellite = side * 0.045, gap = side * 0.02
        let e = CGFloat(energy)
        ctx.opacity = light

        // 开始放的那一下：两圈波纹从轨道往外扩
        if let start = burst {
            let age = now.timeIntervalSince(start)
            if age > 1.6 { burst = nil }
            for k in 0 ..< 2 {
                let t = (age - Double(k) * 0.25) / 1.2
                guard t > 0 && t < 1 else { continue }
                let eased = 1 - pow(1 - t, 3)
                let radius = ring + (side * 0.5 - ring) * eased
                let circle = Path(ellipseIn: CGRect(x: cx - radius, y: cy - radius, width: 2 * radius, height: 2 * radius))
                ctx.stroke(circle, with: .color(ink.dot.opacity(0.5 * (1 - t))), lineWidth: ringWidth * (1 - t) + 1)
            }
        }

        // 四周刻度：左右对称，最新的响度在顶上、往下流
        let n = history.count
        let inner = ring + side * 0.08, reach = side * 0.15
        for k in 0 ..< 2 * n {
            let v = CGFloat(history[k < n ? k : 2 * n - 1 - k])
            let a = (CGFloat(k) + 0.5) * .pi / CGFloat(n) - .pi / 2
            let outer = inner + side * 0.012 + v * reach
            var tick = Path()
            tick.move(to: CGPoint(x: cx + cos(a) * inner, y: cy + sin(a) * inner))
            tick.addLine(to: CGPoint(x: cx + cos(a) * outer, y: cy + sin(a) * outer))
            ctx.stroke(tick, with: .color(ink.dot.opacity(0.14 + 0.86 * v)), style: StrokeStyle(lineWidth: side * 0.009, lineCap: .round))
        }

        // 轨道：卫星那里断开一截（和图标一样）
        let span = (satellite + gap) / ring
        var orbitPath = Path()
        orbitPath.addArc(center: CGPoint(x: cx, y: cy), radius: ring, startAngle: .radians(angle - .pi / 2 + span),
                         endAngle: .radians(angle - .pi / 2 + 2 * .pi - span), clockwise: false)
        ctx.stroke(orbitPath, with: .color(ink.ring), style: StrokeStyle(lineWidth: ringWidth, lineCap: .round))
        let sx = cx + ring * sin(angle), sy = cy - ring * cos(angle)
        ctx.fill(Path(ellipseIn: CGRect(x: sx - satellite, y: sy - satellite, width: 2 * satellite, height: 2 * satellite)), with: .color(ink.dot))

        // 中间的点：放声音时跟着响度鼓起来、带一圈光，等着时慢慢呼吸
        let breathe = phase == .waiting ? 0.05 * sin(now.timeIntervalSinceReferenceDate * 2 * .pi / 3.2) : 0
        let disc = side * 0.1 * (1 + 0.35 * e + breathe)
        if e > 0.02 {
            ctx.drawLayer { layer in
                layer.addFilter(.blur(radius: side * 0.06))
                let glow = disc * 1.9
                layer.fill(Path(ellipseIn: CGRect(x: cx - glow, y: cy - glow, width: 2 * glow, height: 2 * glow)), with: .color(ink.dot.opacity(ink.halo * Double(e))))
            }
        }
        ctx.fill(Path(ellipseIn: CGRect(x: cx - disc, y: cy - disc, width: 2 * disc, height: 2 * disc)), with: .color(ink.dot))
    }
}
