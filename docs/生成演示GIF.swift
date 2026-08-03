// 离屏渲染 README 用的演示 GIF。
// 不录屏——逐帧驱动 PetView.advance()，时间轴完全可控，重跑结果一致。
//
// 文件名必须是 main.swift 才允许顶层语句，所以先复制一份：
//
//   cp docs/生成演示GIF.swift /tmp/main.swift && cd /tmp
//   swiftc -O main.swift <仓库>/源码/{PetView,Theme,Model,Activity,Usage,Auth}.swift -o gifgen
//   ./gifgen demo.gif          # 浅色
//   ./gifgen demo-dark.gif dark # 深色
//
// 改了 PetView 的造型或 AppDelegate.expandedHeight() 之后，记得重跑一遍换掉 docs 里的图。

import AppKit
import ImageIO
import UniformTypeIdentifiers

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
view.clipsToBounds = true       // 真机是窗口在裁；离屏没有窗口边界，必须自己裁
// 离屏也要有窗口：NSColor.windowBackgroundColor 这类语义色要靠窗口的 appearance 解析
let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: winW, height: 400),
                      styleMask: [.borderless], backing: .buffered, defer: false)
// 第二个参数给 dark 就出深色版；README 用 <picture> 按读者主题切换
let darkMode = CommandLine.arguments.count > 2 && CommandLine.arguments[2] == "dark"
window.appearance = NSAppearance(named: darkMode ? .darkAqua : .aqua)
window.contentView = view
view.reduceTransparency = true   // 离屏没有 NSGlassEffectView，让 PetView 自己画卡片底

/// 复刻 AppDelegate.expandedHeight()：那边是 private，这里必须跟着它改
func expandedHeight() -> CGFloat {
    var h: CGFloat = 10 + PetView.topRowH + 2
    h += view.blocksHeight
    let p = view.hoverProgress
    if p > 0.001 {
        let detailH = PetView.blockGap + 2 + 19
            + CGFloat(min(model.rows.count, 5)) * 15 + 18
        h += detailH * p
    }
    return h + 10
}

func desiredSize() -> NSSize {
    let e = view.expandProgress
    return NSSize(width: compact + (winW - compact) * e,
                  height: compact + (expandedHeight() - compact) * e)
}

// MARK: - 时间轴

let fps: CGFloat = 20
let dt = 1 / fps

/// 每一拍做什么。首尾都是「收起 + 无会话」，所以循环处接得上
struct Beat { let at: CGFloat; let action: () -> Void }
let beats: [Beat] = [
    Beat(at: 0.0) { model.hovered = false; model.sessions = [] },
    Beat(at: 0.9) { model.hovered = true },                       // 鼠标移上来 → 展开
    Beat(at: 2.4) { model.sessions = [demoSession(elapsed: 95)] },// 会话开始 → 块卷入
    Beat(at: 5.4) {                                               // 跑完 → 未读
        var s = demoSession(elapsed: 95)
        s.busy = false; s.unread = true; s.finishedAt = Date()
        model.sessions = [s]
    },
    Beat(at: 6.9) { model.sessions = [] },                        // 看过了 → 块卷走
    Beat(at: 7.4) { model.hovered = false },                      // 鼠标移开 → 收起
]
let duration: CGFloat = 8.9

// 画布按「一个会话块 + 详情全展开」的最大高度定，公式与 expandedHeight() 对齐
let maxH = 10 + PetView.topRowH + 2 + PetView.blockH
    + (PetView.blockGap + 2 + 19 + CGFloat(min(model.rows.count, 5)) * 15 + 18) + 10

let pad: CGFloat = 14
let canvas = NSSize(width: winW + pad * 2, height: maxH + pad * 2)

// MARK: - 逐帧渲染

func renderFrame(_ size: NSSize, scale: CGFloat) -> NSImage? {
    let v = view
    v.frame = NSRect(x: 0, y: 0, width: size.width, height: size.height)
    v.needsDisplay = true

    let pw = Int(ceil(size.width * scale)), ph = Int(ceil(size.height * scale))
    guard pw > 0, ph > 0,
          let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pw, pixelsHigh: ph,
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                     isPlanar: false, colorSpaceName: .deviceRGB,
                                     bytesPerRow: 0, bitsPerPixel: 0)
    else { return nil }
    rep.size = size
    guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return nil }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = ctx
    v.displayIgnoringOpacity(v.bounds, in: ctx)
    NSGraphicsContext.restoreGraphicsState()

    let img = NSImage(size: size)
    img.addRepresentation(rep)
    return img
}

let scale: CGFloat = 2
let bg = NSColor(calibratedWhite: darkMode ? 0.086 : 0.918, alpha: 1)

var frames: [CGImage] = []
var t: CGFloat = 0
var nextBeat = 0
while t < duration {
    while nextBeat < beats.count, t >= beats[nextBeat].at {
        beats[nextBeat].action(); nextBeat += 1
    }
    view.advance(dt)

    let size = desiredSize()
    guard let petImg = renderFrame(size, scale: scale) else { break }

    // 合成到固定画布：水平居中，顶部对齐（卡片向下生长，太阳位置基本不动）
    let cw = Int(canvas.width * scale), ch = Int(canvas.height * scale)
    guard let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: cw, pixelsHigh: ch,
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                     isPlanar: false, colorSpaceName: .deviceRGB,
                                     bytesPerRow: 0, bitsPerPixel: 0) else { break }
    out.size = canvas
    guard let octx = NSGraphicsContext(bitmapImageRep: out) else { break }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = octx
    bg.setFill()
    NSRect(origin: .zero, size: canvas).fill()
    // 画布未翻转，原点在左下；顶部对齐 = y 从上往下量
    let x = (canvas.width - size.width) / 2
    let y = canvas.height - pad - size.height
    petImg.draw(in: NSRect(x: x, y: y, width: size.width, height: size.height))
    NSGraphicsContext.restoreGraphicsState()

    if let cg = out.cgImage { frames.append(cg) }
    t += dt
}

// MARK: - 写 GIF

let outPath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "demo.gif"
let url = URL(fileURLWithPath: outPath)
guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.gif.identifier as CFString,
                                                 frames.count, nil) else {
    FileHandle.standardError.write("无法创建 GIF\n".data(using: .utf8)!); exit(1)
}
CGImageDestinationSetProperties(dest, [
    kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]
] as CFDictionary)
let frameProps = [
    kCGImagePropertyGIFDictionary: [
        kCGImagePropertyGIFDelayTime: Double(dt),
        kCGImagePropertyGIFUnclampedDelayTime: Double(dt),
    ]
] as CFDictionary
for f in frames { CGImageDestinationAddImage(dest, f, frameProps) }
guard CGImageDestinationFinalize(dest) else {
    FileHandle.standardError.write("GIF 写入失败\n".data(using: .utf8)!); exit(1)
}
print("✓ \(frames.count) 帧 · \(Int(canvas.width * scale))×\(Int(canvas.height * scale)) px · \(outPath)")
