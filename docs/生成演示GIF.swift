// 离屏渲染演示素材：README 用的 GIF，以及发社媒用的竖屏视频。
// 不录屏——逐帧驱动 PetView.advance()，时间轴完全可控，重跑结果一致。
//
// 文件名必须是 main.swift 才允许顶层语句，所以先复制一份：
//
//   cp docs/生成演示GIF.swift /tmp/main.swift && cd /tmp
//   swiftc -O main.swift <仓库>/源码/{PetView,Theme,Model,Activity,Usage,Auth}.swift -o gifgen
//   ./gifgen demo.gif             # 浅色 GIF
//   ./gifgen demo-dark.gif dark   # 深色 GIF
//   ./gifgen 竖屏.mp4              # 竖屏视频（1080×1920，60fps）
//
// 输出为 .mp4 时自动切成竖屏视频：GIF 撑不住「又大又长又高刷」——
// 三者都直接乘文件体积；H.264 没这个问题，完整时长和高帧率放在视频里。
//
// 卡片的边缘映光由 PetView.drawCardEdge() 画，App 本体也有。
// 这里额外补的是玻璃本身：真机上是 NSGlassEffectView 把背后的桌面糊掉，
// 离屏没有那一层，得自己铺一张背景再把卡片区域做高斯模糊，
// 否则「玻璃」和一块不透明卡片长得一模一样，质感完全看不出来。

import AppKit
import AVFoundation
import CoreImage
import ImageIO
import UniformTypeIdentifiers

let outPath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "demo.gif"
let isVideo = outPath.lowercased().hasSuffix(".mp4") || outPath.lowercased().hasSuffix(".mov")
let darkMode = CommandLine.arguments.contains("dark")   // 视频默认白天模式

// MARK: - 演示数据

func demoRows() -> [UsageRow] {
    [
        UsageRow(label: "5 小时", percent: 70,
                 resetAt: Date().addingTimeInterval(66 * 60), priority: 0),
        UsageRow(label: "每周 · 全部模型", percent: 43,
                 resetAt: Date().addingTimeInterval(3 * 86400), priority: 1),
        UsageRow(label: "每周 · Fable", percent: 60,
                 resetAt: Date().addingTimeInterval(3 * 86400 - 10000), priority: 2),
    ]
}

func demoSession(elapsed: TimeInterval) -> SessionActivity {
    SessionActivity(id: "demo", title: "示例会话", busy: true, waiting: false,
                    since: Date().addingTimeInterval(-elapsed), unread: false,
                    finishedAt: nil, ctxTokens: 393_000, ctxLimit: 1_000_000)
}

// MARK: - 场景

let winW: CGFloat = 198          // 与 AppDelegate.winW 一致
let compact = PetView.compactSide

/// 一次完整的时间轴。要能重建——找无缝循环点时先低成本跑一遍，
/// 定下长度后再从头正式渲染一遍，两遍必须走出完全一样的状态
final class Scene {
    let model = PetModel()
    let view: PetView
    private let window: NSWindow
    private(set) var t: CGFloat = 0
    private var nextBeat = 0

    init() {
        model.loading = false
        model.tier = "Max"
        model.lastFetch = Date()
        model.rows = demoRows()
        view = PetView(model: model)
        view.clipsToBounds = true   // 真机是窗口在裁；离屏没有窗口边界，必须自己裁
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: winW, height: 400),
                          styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: darkMode ? .darkAqua : .aqua)
        window.contentView = view
    }

    /// 复刻 AppDelegate.expandedHeight()：那边是 private，这里必须跟着它改
    func expandedHeight() -> CGFloat {
        var h: CGFloat = 10 + PetView.topRowH + 2
        h += view.resetLineHeight
        h += view.blocksHeight
        if view.hoverProgress > 0.001 {
            h += (PetView.blockGap + 2 + 19 + CGFloat(min(model.rows.count, 5)) * 15 + 18)
                * view.hoverProgress
        }
        return h + 10
    }

    func desiredSize() -> NSSize {
        let e = view.expandProgress
        return NSSize(width: compact + (winW - compact) * e,
                      height: compact + (expandedHeight() - compact) * e)
    }

    func step(_ dt: CGFloat) {
        while nextBeat < beats.count, t >= beats[nextBeat].at {
            beats[nextBeat].action(self); nextBeat += 1
        }
        view.mouseOverride = demoMouse(t)
        view.advance(dt)
        t += dt
    }

    /// 把桌宠单独画进位图。scale = 点到像素的倍数
    func petBitmap(scale: CGFloat) -> NSBitmapImageRep? {
        let size = desiredSize()
        view.frame = NSRect(x: 0, y: 0, width: size.width, height: size.height)
        view.needsDisplay = true
        let pw = Int(ceil(size.width * scale)), ph = Int(ceil(size.height * scale))
        guard pw > 0, ph > 0,
              let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pw, pixelsHigh: ph,
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                         isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        // 必须先设 size 再建上下文：上下文按「点尺寸 : 像素尺寸」定缩放，
        // 顺序反了就成 1:1，视图会以一半比例画进画布左下角
        rep.size = size
        guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        view.displayIgnoringOpacity(view.bounds, in: ctx)
        NSGraphicsContext.restoreGraphicsState()
        return rep
    }
}

// MARK: - 时间轴

// GIF 的帧延迟以 1/100 秒为单位，硬上限 100fps，浏览器普遍再夹到 50fps 左右。
// 视频没有这个限制，直接给 60
let fps: CGFloat = isVideo ? 60 : 25
let dt = 1 / fps
// 循环长度不写死，在这个区间里挑一个首末最接近的（见 findLoopLength）
let minLoop: CGFloat = 12.0
let maxLoop: CGFloat = 14.0
/// 收尾交叉淡入的长度（秒）。见下方 findLoopLength 的说明
let tailFade: CGFloat = 0.45

struct Beat { let at: CGFloat; let action: (Scene) -> Void }
let beats: [Beat] = [
    Beat(at: 0.00) { $0.model.hovered = false; $0.model.sessions = [] },
    Beat(at: 0.70) { $0.model.hovered = true },                       // 鼠标移上来 → 展开
    Beat(at: 1.60) { $0.model.sessions = [demoSession(elapsed: 95)] },// 会话开始 → 块卷入
    Beat(at: 9.50) { s in                                             // 跑完 → 未读
        var x = demoSession(elapsed: 95)
        x.busy = false; x.unread = true; x.finishedAt = Date()
        s.model.sessions = [x]
    },
    Beat(at: 10.35) { $0.model.hovered = false },
    Beat(at: 10.60) { $0.model.sessions = [] },                       // 块卷走 → 收起
]

// 鼠标引力段：光标从右下进场，贴着太阳绕过去，再从左下离场。
// 两端快、中间慢——停留久一点才看得清光芒被拽长、身体前倾、眼珠跟着转。
// 之后留一段无人打扰的安静时间（5.4s→9.5s，约 4 秒），
// 正好够看完一轮「被两侧仪表一吸一斥」的呼吸：满格那侧周期约 3.8 秒
let mouseFrom: CGFloat = 2.30, mouseTo: CGFloat = 5.40
func demoMouse(_ t: CGFloat) -> NSPoint? {
    guard t >= mouseFrom, t <= mouseTo else { return nil }
    let u = (t - mouseFrom) / (mouseTo - mouseFrom)
    let uu = min(1, max(0, u + 0.12 * sin(2 * .pi * u)))   // 中段放慢
    let a = (12 + 168 * uu) * .pi / 180
    let r = 118 - 84 * sin(.pi * uu)
    // 太阳中心：展开态固定在顶行中线（视图坐标翻转，y 向下）
    return NSPoint(x: winW / 2 + cos(a) * r,
                   y: 10 + PetView.topRowH / 2 + sin(a) * r)
}

// MARK: - 画布

// 演示数据没到上限，「解封时间」那一行不会出现，画布不给它留位
let maxH = 10 + PetView.topRowH + 2 + PetView.blockH
    + (PetView.blockGap + 2 + 19 + CGFloat(demoRows().count) * 15 + 18) + 10

/// 视频是竖屏 1080×1920；GIF 用贴着卡片的小画布，省体积
let pixelSize = isVideo ? CGSize(width: 1080, height: 1920)
                        : CGSize(width: (winW + 44) * 2.5, height: (maxH + 44) * 2.5)
/// 卡片在画布上的放大倍数（点 → 像素）
let cardPx: CGFloat = isVideo ? 4.8 : 2.5   // 视频里卡片贴满宽度，观感与 GIF 一致

// MARK: - 背景（让玻璃有东西可透）

/// 一张抽象的「桌面壁纸」。纯色底会让玻璃和不透明卡片长得一样，
/// 必须有内容透上来，模糊才看得出是玻璃
func makeBackdrop(_ px: CGSize) -> CGImage {
    let w = Int(px.width), h = Int(px.height)
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h,
                              bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                              isPlanar: false, colorSpaceName: .deviceRGB,
                              bytesPerRow: 0, bitsPerPixel: 0)!
    let ctx = NSGraphicsContext(bitmapImageRep: rep)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = ctx
    let r = NSRect(x: 0, y: 0, width: px.width, height: px.height)
    let base = darkMode
        ? [NSColor(calibratedRed: 0.07, green: 0.08, blue: 0.13, alpha: 1),
           NSColor(calibratedRed: 0.13, green: 0.10, blue: 0.17, alpha: 1)]
        : [NSColor(calibratedRed: 0.93, green: 0.94, blue: 0.97, alpha: 1),
           NSColor(calibratedRed: 0.87, green: 0.90, blue: 0.95, alpha: 1)]
    NSGradient(colors: base)?.draw(in: r, angle: 72)
    // 几团柔和的色斑：给玻璃一点可透的色彩层次，也让模糊有东西可糊
    let blobs: [(CGFloat, CGFloat, CGFloat, NSColor)] = darkMode
        ? [(0.24, 0.78, 0.46, NSColor(calibratedRed: 0.36, green: 0.22, blue: 0.62, alpha: 0.55)),
           (0.80, 0.62, 0.40, NSColor(calibratedRed: 0.85, green: 0.36, blue: 0.32, alpha: 0.34)),
           (0.58, 0.20, 0.44, NSColor(calibratedRed: 0.13, green: 0.40, blue: 0.55, alpha: 0.42))]
        : [(0.22, 0.80, 0.46, NSColor(calibratedRed: 1.00, green: 0.80, blue: 0.55, alpha: 0.52)),
           (0.82, 0.60, 0.42, NSColor(calibratedRed: 0.62, green: 0.74, blue: 0.98, alpha: 0.50)),
           (0.55, 0.18, 0.44, NSColor(calibratedRed: 0.98, green: 0.66, blue: 0.72, alpha: 0.42))]
    for (bx, by, br, c) in blobs {
        let rad = br * max(px.width, px.height)
        let cen = NSPoint(x: bx * px.width, y: by * px.height)
        NSGradient(colors: [c, c.withAlphaComponent(0)])?
            .draw(fromCenter: cen, radius: 0, toCenter: cen, radius: rad, options: [])
    }
    NSGraphicsContext.restoreGraphicsState()
    return rep.cgImage!
}

let backdrop = makeBackdrop(pixelSize)
/// 预先糊好整张背景：玻璃只是把它「照」出来，背景不动，糊一次就够
let ciContext = CIContext()
let blurred: CGImage = {
    let input = CIImage(cgImage: backdrop)
    // clampedToExtent 防止边缘被透明像素拖淡，糊完再裁回原尺寸
    let f = input.clampedToExtent()
        .applyingFilter("CIGaussianBlur", parameters: ["inputRadius": cardPx * 11])
        .cropped(to: input.extent)
    return ciContext.createCGImage(f, from: input.extent) ?? backdrop
}()

// MARK: - 绘制

func ease(_ x: CGFloat) -> CGFloat {
    let p = min(1, max(0, x)); return p * p * (3 - 2 * p)
}

/// 玻璃卡片底：把糊过的背景按卡片形状「照」出来，再压一层淡淡的白/黑。
/// 边缘映光不在这里画——那是 PetView.drawCardEdge() 的事，App 本体也要有
func drawGlass(in rect: NSRect, radius rad: CGFloat, alpha a: CGFloat, canvas: CGSize) {
    let clip = NSBezierPath(roundedRect: rect, xRadius: rad, yRadius: rad)
    NSGraphicsContext.saveGraphicsState()
    let sh = NSShadow()
    sh.shadowBlurRadius = cardPx * 9
    sh.shadowOffset = NSSize(width: 0, height: -cardPx * 3)
    sh.shadowColor = NSColor.black.withAlphaComponent((darkMode ? 0.55 : 0.24) * a)
    sh.set()
    NSColor.black.withAlphaComponent(0.001).setFill()   // 只为投影，本身不可见
    clip.fill()
    NSGraphicsContext.restoreGraphicsState()

    NSGraphicsContext.saveGraphicsState()
    clip.setClip()
    NSGraphicsContext.current?.cgContext.setAlpha(a)
    NSGraphicsContext.current?.cgContext
        .draw(blurred, in: CGRect(origin: .zero, size: canvas))
    (darkMode ? NSColor(calibratedWhite: 0.22, alpha: 0.62)
              : NSColor(calibratedWhite: 1.0, alpha: 0.60)).setFill()
    NSBezierPath(rect: rect).fill()
    NSGraphicsContext.restoreGraphicsState()
}

/// 光标。不画的话，太阳对着空气做反应，看的人不知道发生了什么
func drawCursor(at p: NSPoint, scale s: CGFloat) {
    let pts: [(CGFloat, CGFloat)] = [(0, 0), (0, 16.6), (4.3, 12.7), (7.0, 18.9),
                                     (9.9, 17.6), (7.2, 11.6), (12.0, 11.3)]
    let path = NSBezierPath()
    for (i, q) in pts.enumerated() {
        let v = NSPoint(x: p.x + q.0 * s, y: p.y - q.1 * s)   // 画布未翻转，箭头朝下
        i == 0 ? path.move(to: v) : path.line(to: v)
    }
    path.close()
    NSGraphicsContext.saveGraphicsState()
    let sh = NSShadow()
    sh.shadowBlurRadius = 3 * s
    sh.shadowOffset = NSSize(width: 0, height: -s)
    sh.shadowColor = NSColor.black.withAlphaComponent(0.5)
    sh.set()
    NSColor.white.setStroke()
    path.lineWidth = 2.4 * s
    path.lineJoinStyle = .round
    path.stroke()
    NSGraphicsContext.restoreGraphicsState()
    NSColor.black.setFill()
    path.fill()
}

/// 画一帧完整画面（背景 + 玻璃 + 桌宠 + 光标），返回像素图
func renderFrame(_ sc: Scene) -> CGImage? {
    let size = sc.desiredSize()
    guard let petRep = sc.petBitmap(scale: cardPx) else { return nil }
    let petImg = NSImage(size: size); petImg.addRepresentation(petRep)

    let w = Int(pixelSize.width), h = Int(pixelSize.height)
    guard let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h,
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                     isPlanar: false, colorSpaceName: .deviceRGB,
                                     bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
    out.size = pixelSize                       // 1:1，直接按像素画
    guard let octx = NSGraphicsContext(bitmapImageRep: out) else { return nil }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = octx
    octx.cgContext.draw(backdrop, in: CGRect(origin: .zero, size: pixelSize))

    // 卡片：水平居中；GIF 顶部对齐（卡片向下生长，太阳基本不动），视频整体居中
    let cw = size.width * cardPx, ch = size.height * cardPx
    let x = (pixelSize.width - cw) / 2
    let y = isVideo ? (pixelSize.height - ch) / 2
                    : pixelSize.height - 22 * cardPx - ch
    let card = NSRect(x: x, y: y, width: cw, height: ch)

    let e = sc.view.expandProgress
    if e > 0.01 {                              // 完全收起时没有卡片，只剩太阳
        let r0 = min(cw, ch) / 2
        let rad = min(r0 + (PetView.cardRadius * cardPx - r0) * e, r0)
        drawGlass(in: card, radius: rad, alpha: ease(min(1, e / 0.45)), canvas: pixelSize)
    }
    petImg.draw(in: card)
    if let m = sc.view.mouseOverride {
        // 视图坐标翻转（y 向下），画布不翻转，换算一下
        drawCursor(at: NSPoint(x: x + m.x * cardPx, y: y + ch - m.y * cardPx),
                   scale: cardPx * 0.62)
    }
    NSGraphicsContext.restoreGraphicsState()
    return out.cgImage
}

// MARK: - 找无缝循环点

// 要能循环播放，最后一帧的下一帧必须与第一帧完全一致。对不上的有三样：
// 光芒转角、zzz 相位、呼吸相位——都跟着绝对时间走，周期还互不相通
// （zzz 是 1/0.42 秒，呼吸醒着 1.6 睡着 1.0 rad/s，中间还有过渡段）。
// 与其去解这几个周期的最小公倍数，不如直接量：先用小尺寸跑一遍，
// 把候选长度那几帧逐像素跟第 0 帧比，挑差异最小的。
//
// 但**光挑长度是接不上的**。实测接缝差 1.9/255，而空闲段相邻帧只差 0.13，
// 差 15 倍；而且候选值在 ±5 帧内都是 1.9~2.1，说明有个调不掉的常数残差。
// 把差异图打出来看，问题出在 zzz 和部分光芒的尖端——空闲时同时跑着四个
// 互不通约的振荡：zzz 0.42 周/秒、呼吸 1.0 rad/s、左仪表拉扯 1.67、
// 右仪表拉扯 1.56（后两个的频率跟着各自的用量走）。四个周期凑不到一起，
// 没有任何时长能让它们同时回到原点。
//
// 所以最后一帧直接**取第 0 帧本身**，前面 tailFade 秒交叉淡过去。
// 首末严格相同，接缝在两张一模一样的图之间，而淡入发生在光芒本来就在
// 缓慢移动的时候，看不出来。挑长度这一步仍然保留——残差越小，淡入要吃的越少。
//
// 光芒转角那一项则是真的解掉了：它停下时冻在原地、之后不再变化，
// 所以 PetView 那边加了「停下归到最近的 40° 卡点」，让空闲姿态唯一确定
func findLoopLength() -> (frames: Int, diff: Double) {
    let probe = Scene()
    let maxN = Int(maxLoop * fps), minN = Int(minLoop * fps)
    var first: NSBitmapImageRep?
    var best = (n: minN, d: Double.infinity)
    var all: [(Int, Double)] = []
    for i in 0...maxN {
        probe.step(dt)
        guard let rep = probe.petBitmap(scale: 1) else { continue }
        if i == 0 { first = rep; continue }
        guard i >= minN, let f = first,
              rep.pixelsWide == f.pixelsWide, rep.pixelsHigh == f.pixelsHigh,
              let a = f.bitmapData, let b = rep.bitmapData else { continue }
        let n = f.bytesPerRow * f.pixelsHigh
        var sum = 0
        for k in stride(from: 0, to: n, by: 4) {   // 只比 alpha 之外的三个通道
            sum += abs(Int(a[k]) - Int(b[k])) + abs(Int(a[k+1]) - Int(b[k+1]))
                 + abs(Int(a[k+2]) - Int(b[k+2]))
        }
        let d = Double(sum) / Double(n / 4 * 3)
        all.append((i, d))
        if d < best.d { best = (i, d) }
    }
    // 光有绝对差值说明不了问题——得跟「相邻两帧的正常差异」比。
    // 接缝处的差异不大于普通一帧的步进，才算真的接得上
    if ProcessInfo.processInfo.environment["LOOPDEBUG"] != nil {
        let sorted = all.sorted { $0.1 < $1.1 }.prefix(6)
        for (i, d) in sorted {
            print(String(format: "    候选 %d 帧 = %.2f 秒   差 %.3f", i, Double(i) / Double(fps), d))
        }
    }
    return (best.n, best.d)
}

/// 空闲段里相邻两帧的平均像素差，作为「接得上」的基准线
func adjacentBaseline() -> Double {
    let probe = Scene()
    let n = Int(minLoop * fps)
    for _ in 0..<n { probe.step(dt) }
    guard let a = probe.petBitmap(scale: 1)?.bitmapData else { return .nan }
    let bytes = 88 * 4 * 88
    var prev = [UInt8](repeating: 0, count: bytes)
    memcpy(&prev, a, bytes)
    probe.step(dt)
    guard let b = probe.petBitmap(scale: 1)?.bitmapData else { return .nan }
    var sum = 0
    for k in stride(from: 0, to: bytes, by: 4) {
        sum += abs(Int(prev[k]) - Int(b[k])) + abs(Int(prev[k+1]) - Int(b[k+1]))
             + abs(Int(prev[k+2]) - Int(b[k+2]))
    }
    return Double(sum) / Double(bytes / 4 * 3)
}

let (loopFrames, loopDiff) = findLoopLength()
let baseline = adjacentBaseline()
print(String(format: "  无缝点：%d 帧（%.2f 秒），接缝差 %.3f/255；空闲段相邻帧差 %.3f/255",
             loopFrames, Double(loopFrames) / Double(fps), loopDiff, baseline))

// MARK: - 正式渲染

let scene = Scene()
var frames: [CGImage] = []
while frames.count < loopFrames {
    scene.step(dt)
    guard let f = renderFrame(scene) else { break }
    frames.append(f)
}

// 收尾：把最后 fadeN 帧交叉淡到第 0 帧，最末一帧就是第 0 帧本身
let fadeN = max(2, Int(tailFade * fps))
if frames.count > fadeN + 2 {
    let target = frames[0]
    let w = Int(pixelSize.width), h = Int(pixelSize.height)
    for k in 0..<fadeN {
        let j = frames.count - fadeN + k
        let a = CGFloat(k + 1) / CGFloat(fadeN)     // 最后一帧 a = 1，完全等于第 0 帧
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h,
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                         isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0),
              let ctx = NSGraphicsContext(bitmapImageRep: rep) else { continue }
        rep.size = pixelSize
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        let r = CGRect(origin: .zero, size: pixelSize)
        ctx.cgContext.draw(frames[j], in: r)
        ctx.cgContext.setAlpha(a)
        ctx.cgContext.draw(target, in: r)
        NSGraphicsContext.restoreGraphicsState()
        if let cg = rep.cgImage { frames[j] = cg }
    }
    print(String(format: "  收尾 %d 帧交叉淡到第 0 帧；末帧与首帧完全相同", fadeN))
}

// MARK: - 输出

func fail(_ s: String) -> Never {
    FileHandle.standardError.write((s + "\n").data(using: .utf8)!); exit(1)
}

let url = URL(fileURLWithPath: outPath)
try? FileManager.default.removeItem(at: url)

if isVideo {
    guard let writer = try? AVAssetWriter(outputURL: url, fileType: .mp4) else {
        fail("无法创建视频写入器")
    }
    let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
        AVVideoCodecKey: AVVideoCodecType.h264,
        AVVideoWidthKey: Int(pixelSize.width),
        AVVideoHeightKey: Int(pixelSize.height),
        AVVideoCompressionPropertiesKey: [
            AVVideoAverageBitRateKey: 9_000_000,
            AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
        ],
    ])
    input.expectsMediaDataInRealTime = false
    let adaptor = AVAssetWriterInputPixelBufferAdaptor(
        assetWriterInput: input,
        sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: Int(pixelSize.width),
            kCVPixelBufferHeightKey as String: Int(pixelSize.height),
        ])
    writer.add(input)
    writer.startWriting()
    writer.startSession(atSourceTime: .zero)

    let scaleT = CMTimeScale(600)
    for (i, cg) in frames.enumerated() {
        while !input.isReadyForMoreMediaData { usleep(2000) }
        guard let pool = adaptor.pixelBufferPool else { fail("拿不到像素缓冲池") }
        var pb: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pb)
        guard let buf = pb else { fail("分配像素缓冲失败") }
        CVPixelBufferLockBaseAddress(buf, [])
        if let c = CGContext(data: CVPixelBufferGetBaseAddress(buf),
                             width: Int(pixelSize.width), height: Int(pixelSize.height),
                             bitsPerComponent: 8,
                             bytesPerRow: CVPixelBufferGetBytesPerRow(buf),
                             space: CGColorSpaceCreateDeviceRGB(),
                             bitmapInfo: CGImageAlphaInfo.noneSkipFirst.rawValue
                                 | CGBitmapInfo.byteOrder32Little.rawValue) {
            c.draw(cg, in: CGRect(origin: .zero, size: pixelSize))
        }
        CVPixelBufferUnlockBaseAddress(buf, [])
        adaptor.append(buf, withPresentationTime:
            CMTime(value: CMTimeValue(Double(i) / Double(fps) * Double(scaleT)),
                   timescale: scaleT))
    }
    input.markAsFinished()
    let done = DispatchSemaphore(value: 0)
    writer.finishWriting { done.signal() }
    done.wait()
    if writer.status != .completed { fail("视频写入失败：\(writer.error?.localizedDescription ?? "?")") }
} else {
    guard let dest = CGImageDestinationCreateWithURL(
            url as CFURL, UTType.gif.identifier as CFString, frames.count, nil) else {
        fail("无法创建 GIF")
    }
    CGImageDestinationSetProperties(dest, [
        kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]
    ] as CFDictionary)
    let props = [kCGImagePropertyGIFDictionary: [
        kCGImagePropertyGIFDelayTime: Double(dt),
        kCGImagePropertyGIFUnclampedDelayTime: Double(dt),
    ]] as CFDictionary
    for f in frames { CGImageDestinationAddImage(dest, f, props) }
    if !CGImageDestinationFinalize(dest) { fail("GIF 写入失败") }
}

let mb = (try? FileManager.default.attributesOfItem(atPath: outPath)[.size] as? Int) ?? 0
print("✓ \(frames.count) 帧 · \(Int(fps))fps · \(Int(pixelSize.width))×\(Int(pixelSize.height)) px"
      + " · \(String(format: "%.1f", Double(mb ?? 0) / 1_048_576))MB · \(outPath)")
