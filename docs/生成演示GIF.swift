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
/// 「躲」模式：全程没有会话，太阳一直睡着，光标凑近时它把光芒缩回去、身子往后躲。
/// 与默认那条时间轴的区别不只是内容——全程睡着意味着呼吸速率恒为 1.0 rad/s，
/// 「醒着多久」这个调节旋钮没有了，闭环只能靠搜近似复现点（见 findLoopLength）
let dodge = CommandLine.arguments.contains("dodge")

// MARK: - 演示数据

/// **这三个百分比不能随便改**：仪表拉扯的角频率是 rate = 0.9 + 0.011×用量%，
/// 要让它在一个循环里正好转整数圈，用量% 是被 loopT 反解出来的。
/// 在 5–99 里只有 21% 近乎精确（相位残差 0.00044 rad ＝ 正常一帧移动量的 2%），
/// 次好的 55% 是 0.049 rad ≈ 两帧。实测接缝：两个圈都用 21% 是 1.17×基准，
/// 换成 21/55 就掉到 2.07×——所以两个圈都取 21%。
/// 代价是演示里的太阳是放松表情、圆环也偏空，这是闭环换来的。
/// 「每周 · 全部模型」不上圆环、不参与拉扯，只要小于 Fable 即可随意。
func demoRows() -> [UsageRow] {
    [
        UsageRow(label: "5 小时", percent: 21,
                 resetAt: Date().addingTimeInterval(66 * 60), priority: 0),
        UsageRow(label: "每周 · 全部模型", percent: 12,
                 resetAt: Date().addingTimeInterval(3 * 86400), priority: 1),
        UsageRow(label: "每周 · Fable", percent: 21,
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
    private let schedule = beatList()

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
        while nextBeat < schedule.count, t >= schedule[nextBeat].at {
            schedule[nextBeat].action(self); nextBeat += 1
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

// 要能首尾相接地循环播放，收尾时每一项动画都必须回到开头那一刻的状态。
// 空闲时同时跑着四项，各自的周期互不相通：
//   ① 光芒转角 sunSpin —— 停下时冻在原地。这项在 PetView 里解掉了
//      （停下归到最近的 40° 卡点，9 次对称所以看不出来，但姿态唯一确定）
//   ② zzz 飘动 —— fmod(t·0.42, 1)，周期 1/0.42 秒，跟绝对时间走
//   ③ 两侧仪表拉扯 —— sin(t·rate)，rate = 0.9 + 0.011×用量%，也跟绝对时间走
//   ④ 身体呼吸 —— 累积相位，醒着 1.6 睡着 1.0 rad/s，跟时间轴的编排有关
//
// ②③ 是绝对时间的函数，所以循环长度必须同时是它们周期的整数倍。
// 再加上「整数帧」这个约束，最小解是 T = 50/3 秒：
//   zzz：0.42 × 50/3 = 7 周，整数 ✓
//   60fps：50/3 × 60 = 1000 帧，整数 ✓
// T 一定，③ 就把用量百分比反解出来了（见 demoRows 的说明）。
// ④ 没有解析解，用二分调「会话跑完」的时刻去凑（见 solveBreath）。
let loopT: CGFloat = 50.0 / 3.0
let loopFrames = isVideo ? 1000 : 400
let dt = loopT / CGFloat(loopFrames)
let fps = CGFloat(loopFrames) / loopT
// GIF 的帧延迟只能存 1/100 秒的整数倍。400 帧对应 0.04167 秒，存成 0.04——
// 播放会快 4%，但**帧序列本身是闭合的**，无缝与否只看画面内容不看播放速度
let gifDelay = 0.04

// 暖机：先空转一段再开始录。
// 光芒伸长量 rayPull、圆环缓动 ringShown 这些是低通滤波量，从初值 0 出发要
// 若干秒才收敛到稳态；不暖机的话第 0 帧还在收敛途中，而末帧早已稳态，接不上。
//
// 暖机多长是自由的——闭合条件只跟循环长度有关，与从哪个相位起录无关。
// 默认版空转整整一个周期，顺便让 ②③ 回到 t=0 的相位。
//
// 躲模式则拿它当旋钮用。全程睡着时呼吸速率恒为 1.0 rad/s，一轮下来净增
// 16.667 rad，离 2π 的整数倍差 2.183 rad，**相位无论如何对不上**。
// 但呼吸进画面的方式是 sin(φ)，而
//     sin(φ) − sin(φ + 2.183) = 1.778 · cos(φ + 1.0915)
// 只要起点相位落在让这个 cos 为零的地方，两端的呼吸**取值**就严格相同——
// 相位对不上，值可以对上。代价只是两端的呼吸方向相反，
// 而那点差异是每帧 0.016pt，可以忽略
// **两个模式必须用同一个暖机长度**，否则第 0 帧的 zzz / 呼吸 / 仪表相位各不相同，
// 两条片子各自能循环、却互相拼不上。默认版的暖机本来就是自由的（它的呼吸靠
// sessionEnd 对齐，与暖机无关），所以统一取躲模式需要的那个值，两边都满足
let warmT: CGFloat = {
    // 暖机期间两个模式的状态完全一样（没有会话、没有悬停、睡着），
    // 所以暖机长度相同 ⇒ 第 0 帧逐像素相同。
    // 全程睡着 → breathPhase(t) = t，直接在整数帧上扫最小值
    var best = (w: loopT, d: CGFloat.infinity)
    for f in stride(from: Int(10 * 60), through: Int(25 * 60), by: 1) {
        let w = CGFloat(f) / 60
        let d = abs(sin(w) - sin(w + loopT))
        if d < best.d { best = (w, d) }
    }
    print(String(format: "  躲模式暖机：%.4f 秒，两端呼吸取值差 %.2e（相位差 %.3f rad 对不上，但取值对上了）",
                 best.w, best.d, (16.6667).truncatingRemainder(dividingBy: 2 * .pi)))
    return best.w
}()
let warmFrames = Int((warmT / dt).rounded())

/// 会话「跑完转未读」的时刻。这是留给呼吸相位的调节旋钮——
/// 醒着 1.6、睡着 1.0 rad/s，把这个时刻挪早挪晚就改变了醒着的总时长
var sessionEnd: CGFloat = 9.50

struct Beat { let at: CGFloat; let action: (Scene) -> Void }
func beatList() -> [Beat] {
    if dodge {
        // 与默认版**完全相同的节奏**：同样的时长、同样的展开与收起时刻、
        // 同样的光标轨迹。唯一的差别是全程没有会话，于是太阳一直睡着，
        // 引力方向反过来——光标凑近时它把光芒缩回去、身子往后躲。
        // 收起的时刻沿用默认版解出来的 sessionEnd + 0.85，保证两条片子对得上
        return [
            Beat(at: warmT + 0.00) { $0.model.hovered = false; $0.model.sessions = [] },
            Beat(at: warmT + 0.70) { $0.model.hovered = true },
            Beat(at: warmT + sessionEnd + 0.85) { $0.model.hovered = false },
        ]
    }
    return [
        Beat(at: warmT + 0.00) { $0.model.hovered = false; $0.model.sessions = [] },
        Beat(at: warmT + 0.70) { $0.model.hovered = true },
        Beat(at: warmT + 1.60) { $0.model.sessions = [demoSession(elapsed: 95)] },
        Beat(at: warmT + sessionEnd) { s in
            var x = demoSession(elapsed: 95)
            x.busy = false; x.unread = true; x.finishedAt = Date()
            s.model.sessions = [x]
        },
        Beat(at: warmT + sessionEnd + 0.85) { $0.model.hovered = false },
        Beat(at: warmT + sessionEnd + 1.10) { $0.model.sessions = [] },
    ]
}

// 鼠标引力段：光标从右下进场，贴着太阳绕过去，再从左下离场。
// 两端快、中间慢——停留久一点才看得清光芒被拽长、身体前倾、眼珠跟着转。
// 之后留一段无人打扰的安静时间，看「被两侧仪表一吸一斥」的呼吸
let mouseFrom: CGFloat = 2.30, mouseTo: CGFloat = 5.40

// 躲模式复用同一条路径、同一个进出时刻——两个版本唯一的差别是有没有会话
func demoMouse(_ t: CGFloat) -> NSPoint? {
    let tt = t - warmT
    guard tt >= mouseFrom, tt <= mouseTo else { return nil }
    let u = (tt - mouseFrom) / (mouseTo - mouseFrom)
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

// MARK: - 对齐呼吸相位

/// 空转一个周期后再跑一整轮，返回呼吸相位在这一轮里的净增量。
/// 只推进不绘制——呼吸只跟 sleepT 和 dt 有关，与画不画无关，所以很快
func breathGain() -> CGFloat {
    let sc = Scene()
    for _ in 0..<warmFrames { sc.step(dt) }
    let a = sc.view.breathPhaseSnapshot
    for _ in 0..<loopFrames { sc.step(dt) }
    return sc.view.breathPhaseSnapshot - a
}

/// 呼吸相位没有解析解，二分「会话跑完」的时刻去凑：
/// 醒着 1.6、睡着 1.0 rad/s，把它挪晚一秒，一轮下来就多攒 0.6 rad。
/// 目标是让净增量落在 2π 的整数倍上
func solveBreath() {
    let twoPi = CGFloat.pi * 2
    var lo: CGFloat = 4.5, hi: CGFloat = 12.5
    sessionEnd = lo; let gLo = breathGain()
    sessionEnd = hi; let gHi = breathGain()
    // 取区间内可达的那个整数倍
    let target = (gLo / twoPi).rounded(.up) * twoPi
    guard target <= gHi else {
        print(String(format: "  呼吸：区间 [%.2f, %.2f] 内没有可达的 2π 整数倍（净增量 %.3f–%.3f），保持默认",
                     lo, hi, gLo, gHi))
        sessionEnd = 9.50
        return
    }
    for _ in 0..<28 {
        let mid = (lo + hi) / 2
        sessionEnd = mid
        if breathGain() < target { lo = mid } else { hi = mid }
    }
    sessionEnd = (lo + hi) / 2
    let g = breathGain()
    let residual = abs(g - (g / twoPi).rounded() * twoPi)
    print(String(format: "  呼吸：会话结束点 %.4f 秒，净增量 %.4f rad = %.3f 圈，残差 %.2e rad",
                 sessionEnd, g, g / twoPi, residual))
}
// 躲模式全程睡着，呼吸速率恒为 1.0 rad/s，「醒着多久」这个旋钮不存在，
// 呼吸相位无法对齐（zzz 要整周、呼吸要 2π 整数倍，而 0.42×2π 是无理数）。
// 时长既然要跟默认版一致，就只能如实承受这项残差——下面的自检会把它量出来
solveBreath()

// MARK: - 正式渲染

let scene = Scene()
for _ in 0..<warmFrames { scene.step(dt) }      // 暖机，不录
var frames: [CGImage] = []
var firstPetRep: NSBitmapImageRep?
while frames.count < loopFrames {
    scene.step(dt)
    if frames.isEmpty { firstPetRep = scene.petBitmap(scale: 1) }
    guard let f = renderFrame(scene) else { break }
    frames.append(f)
}

// 第 0 帧的指纹。两个模式跑出来必须一模一样，否则两条片子拼不上——
// 这是「各自能循环」之外的另一件事，不能靠推理，要量
if let f0 = firstPetRep, let d = f0.bitmapData {
    var h: UInt64 = 1469598103934665603
    for i in 0..<(f0.bytesPerRow * f0.pixelsHigh) {
        h = (h ^ UInt64(d[i])) &* 1099511628211
    }
    print(String(format: "  第 0 帧指纹 %016llx（%d×%d）", h, f0.pixelsWide, f0.pixelsHigh))
}

// 自检：末帧之后的那一帧应该与第 0 帧完全相同，这才叫接得上。
// 顺便量一下空闲段相邻两帧的差，作为「一帧正常步进」的基准线
if let seamRep = scene.petBitmap(scale: 1) {
    let probe = Scene()
    for _ in 0..<warmFrames { probe.step(dt) }
    probe.step(dt)
    if let firstRep = probe.petBitmap(scale: 1),
       firstRep.pixelsWide == seamRep.pixelsWide,
       firstRep.pixelsHigh == seamRep.pixelsHigh,
       let a = firstRep.bitmapData, let b = seamRep.bitmapData {
        let n = firstRep.bytesPerRow * firstRep.pixelsHigh
        var sum = 0
        for k in stride(from: 0, to: n, by: 4) {
            sum += abs(Int(a[k]) - Int(b[k])) + abs(Int(a[k+1]) - Int(b[k+1]))
                 + abs(Int(a[k+2]) - Int(b[k+2]))
        }
        let seam = Double(sum) / Double(n / 4 * 3)
        probe.step(dt)
        var base = Double.nan
        if let c = probe.petBitmap(scale: 1)?.bitmapData {
            var s2 = 0
            for k in stride(from: 0, to: n, by: 4) {
                s2 += abs(Int(a[k]) - Int(c[k])) + abs(Int(a[k+1]) - Int(c[k+1]))
                    + abs(Int(a[k+2]) - Int(c[k+2]))
            }
            base = Double(s2) / Double(n / 4 * 3)
        }
        print(String(format: "  接缝 %.4f/255；相邻帧基准 %.4f/255（比值 %.2f×）",
                     seam, base, seam / base))
    }
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
            AVVideoAverageBitRateKey: 40_000_000,
            // 全 I 帧。首帧本来是关键帧、末帧是预测帧，量化方式不同，
            // 于是内容完全相同的两帧解码出来仍有约 0.6/255 的差——加码率
            // 治不了（9→24→40 Mbps 基本没动）。每帧独立编码才对得上。
            // 代价是文件大好几倍，但这是发社媒用的素材，不在乎
            AVVideoMaxKeyFrameIntervalKey: 1,
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
        kCGImagePropertyGIFDelayTime: gifDelay,
        kCGImagePropertyGIFUnclampedDelayTime: gifDelay,
    ]] as CFDictionary
    for f in frames { CGImageDestinationAddImage(dest, f, props) }
    if !CGImageDestinationFinalize(dest) { fail("GIF 写入失败") }
}

let mb = (try? FileManager.default.attributesOfItem(atPath: outPath)[.size] as? Int) ?? 0
print("✓ \(frames.count) 帧 · \(Int(fps))fps · \(Int(pixelSize.width))×\(Int(pixelSize.height)) px"
      + " · \(String(format: "%.1f", Double(mb ?? 0) / 1_048_576))MB · \(outPath)")
