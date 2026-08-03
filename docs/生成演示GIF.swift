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

let model = PetModel()
model.loading = false
model.tier = "Max"
model.lastFetch = Date()
model.rows = [
    UsageRow(label: "5 小时", percent: 70,
             resetAt: Date().addingTimeInterval(66 * 60), priority: 0),
    UsageRow(label: "每周 · 全部模型", percent: 43,
             resetAt: Date().addingTimeInterval(3 * 86400), priority: 1),
    UsageRow(label: "每周 · Fable", percent: 60,
             resetAt: Date().addingTimeInterval(3 * 86400 - 10000), priority: 2),
]

func demoSession(elapsed: TimeInterval) -> SessionActivity {
    SessionActivity(id: "demo", title: "示例会话", busy: true, waiting: false,
                    since: Date().addingTimeInterval(-elapsed), unread: false,
                    finishedAt: nil, ctxTokens: 393_000, ctxLimit: 1_000_000)
}

// MARK: - 视图

let winW: CGFloat = 198          // 与 AppDelegate.winW 一致
let compact = PetView.compactSide

let view = PetView(model: model)
view.clipsToBounds = true        // 真机是窗口在裁；离屏没有窗口边界，必须自己裁
let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: winW, height: 400),
                      styleMask: [.borderless], backing: .buffered, defer: false)
window.appearance = NSAppearance(named: darkMode ? .darkAqua : .aqua)
window.contentView = view

/// 复刻 AppDelegate.expandedHeight()：那边是 private，这里必须跟着它改
func expandedHeight() -> CGFloat {
    var h: CGFloat = 10 + PetView.topRowH + 2
    h += view.blocksHeight
    let p = view.hoverProgress
    if p > 0.001 {
        h += (PetView.blockGap + 2 + 19 + CGFloat(min(model.rows.count, 5)) * 15 + 18) * p
    }
    return h + 10
}

func desiredSize() -> NSSize {
    let e = view.expandProgress
    return NSSize(width: compact + (winW - compact) * e,
                  height: compact + (expandedHeight() - compact) * e)
}

// MARK: - 时间轴

// GIF 的帧延迟以 1/100 秒为单位，硬上限 100fps，浏览器普遍再夹到 50fps 左右。
// 视频没有这个限制，直接给 60
let fps: CGFloat = isVideo ? 60 : 25
let dt = 1 / fps
let duration: CGFloat = 12.0

struct Beat { let at: CGFloat; let action: () -> Void }
let beats: [Beat] = [
    Beat(at: 0.00) { model.hovered = false; model.sessions = [] },
    Beat(at: 0.70) { model.hovered = true },                       // 鼠标移上来 → 展开
    Beat(at: 1.60) { model.sessions = [demoSession(elapsed: 95)] },// 会话开始 → 块卷入
    Beat(at: 9.50) {                                               // 跑完 → 未读
        var s = demoSession(elapsed: 95)
        s.busy = false; s.unread = true; s.finishedAt = Date()
        model.sessions = [s]
    },
    Beat(at: 10.35) { model.hovered = false },
    Beat(at: 10.60) { model.sessions = [] },                       // 块卷走 → 收起
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

let maxH = 10 + PetView.topRowH + 2 + PetView.blockH
    + (PetView.blockGap + 2 + 19 + CGFloat(min(model.rows.count, 5)) * 15 + 18) + 10

/// 视频是竖屏 1080×1920；GIF 用贴着卡片的小画布，省体积
let pixelSize = isVideo ? CGSize(width: 1080, height: 1920)
                        : CGSize(width: (winW + 44) * 2.5, height: (maxH + 44) * 2.5)
/// 卡片在画布上的放大倍数（点 → 像素）
let cardPx: CGFloat = isVideo ? 4.3 : 2.5

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

func renderPet(_ size: NSSize, scale: CGFloat) -> NSImage? {
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
    let img = NSImage(size: size)
    img.addRepresentation(rep)
    return img
}

/// 画一帧完整画面（背景 + 玻璃 + 桌宠 + 光标），返回像素图
func renderFrame(_ t: CGFloat) -> CGImage? {
    let size = desiredSize()
    guard let petImg = renderPet(size, scale: cardPx) else { return nil }

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

    let e = view.expandProgress
    if e > 0.01 {                              // 完全收起时没有卡片，只剩太阳
        let r0 = min(cw, ch) / 2
        let rad = min(r0 + (PetView.cardRadius * cardPx - r0) * e, r0)
        drawGlass(in: card, radius: rad, alpha: ease(min(1, e / 0.45)), canvas: pixelSize)
    }
    petImg.draw(in: card)
    if let m = view.mouseOverride {
        // 视图坐标翻转（y 向下），画布不翻转，换算一下
        drawCursor(at: NSPoint(x: x + m.x * cardPx, y: y + ch - m.y * cardPx),
                   scale: cardPx * 0.62)
    }
    NSGraphicsContext.restoreGraphicsState()
    return out.cgImage
}

// MARK: - 逐帧

var frames: [CGImage] = []
var t: CGFloat = 0
var nextBeat = 0
let total = Int(duration * fps)
while frames.count < total {
    while nextBeat < beats.count, t >= beats[nextBeat].at {
        beats[nextBeat].action(); nextBeat += 1
    }
    view.mouseOverride = demoMouse(t)
    view.advance(dt)
    guard let f = renderFrame(t) else { break }
    frames.append(f)
    t += dt
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
