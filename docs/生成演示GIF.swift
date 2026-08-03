// 离屏渲染 README 用的演示 GIF。
// 不录屏——逐帧驱动 PetView.advance()，时间轴完全可控，重跑结果一致。
//
// 文件名必须是 main.swift 才允许顶层语句，所以先复制一份：
//
//   cp docs/生成演示GIF.swift /tmp/main.swift && cd /tmp
//   swiftc -O main.swift <仓库>/源码/{PetView,Theme,Model,Activity,Usage,Auth}.swift -o gifgen
//   ./gifgen demo.gif           # 浅色
//   ./gifgen demo-dark.gif dark # 深色
//
// 改了 PetView 的造型或 AppDelegate.expandedHeight() 之后，重跑一遍换掉 docs 里的图。
// 卡片的边缘映光由 PetView.drawCardEdge() 画，这里只补一层半透明卡片底
// （真机上那层是 NSGlassEffectView，离屏没有）。

import AppKit
import ImageIO
import UniformTypeIdentifiers

let darkMode = CommandLine.arguments.count > 2 && CommandLine.arguments[2] == "dark"

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
// 50 就是实际能看到的上限，再高只会白白撑大文件
let fps: CGFloat = 50
let dt = 1 / fps
let duration: CGFloat = 8.0

struct Beat { let at: CGFloat; let action: () -> Void }
let beats: [Beat] = [
    Beat(at: 0.00) { model.hovered = false; model.sessions = [] },
    Beat(at: 0.55) { model.hovered = true },                       // 鼠标移上来 → 展开
    Beat(at: 1.55) { model.sessions = [demoSession(elapsed: 95)] },// 会话开始 → 块卷入
    Beat(at: 5.60) {                                               // 跑完 → 未读
        var s = demoSession(elapsed: 95)
        s.busy = false; s.unread = true; s.finishedAt = Date()
        model.sessions = [s]
    },
    Beat(at: 6.35) { model.hovered = false },
    Beat(at: 6.55) { model.sessions = [] },                        // 块卷走 → 收起
]

// 鼠标引力段：光标从右下进场，贴着太阳绕过去，再从左下离场。
// 两端快、中间慢——停留久一点才看得清光芒被拽长、身体前倾、眼珠跟着转
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
let pad: CGFloat = 14
let canvas = NSSize(width: winW + pad * 2, height: maxH + pad * 2)

// MARK: - 绘制

func ease(_ x: CGFloat) -> CGFloat {
    let p = min(1, max(0, x)); return p * p * (3 - 2 * p)
}

/// 卡片底。真机上是 NSGlassEffectView，离屏没有玻璃图层，补一层半透明底。
/// 边缘映光不在这里画——那是 PetView.drawCardEdge() 的事，App 里也要有
func drawCardBody(in rect: NSRect, expand e: CGFloat) {
    guard e > 0.01 else { return }               // 完全收起时没有卡片，只剩太阳
    let alpha = ease(min(1, e / 0.45))
    let r0 = min(rect.width, rect.height) / 2
    let rad = min(r0 + (PetView.cardRadius - r0) * e, r0)
    NSGraphicsContext.saveGraphicsState()
    let sh = NSShadow()
    sh.shadowBlurRadius = darkMode ? 14 : 12
    sh.shadowOffset = NSSize(width: 0, height: -3)
    sh.shadowColor = NSColor.black.withAlphaComponent((darkMode ? 0.5 : 0.18) * alpha)
    sh.set()
    (darkMode ? NSColor(calibratedWhite: 0.24, alpha: 0.94 * alpha)
              : NSColor(calibratedWhite: 1.0, alpha: 0.88 * alpha)).setFill()
    NSBezierPath(roundedRect: rect, xRadius: rad, yRadius: rad).fill()
    NSGraphicsContext.restoreGraphicsState()
}

/// 光标。不画的话，太阳对着空气做反应，看的人不知道发生了什么
func drawCursor(at p: NSPoint) {
    let s: CGFloat = 1.15
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
    sh.shadowBlurRadius = 3
    sh.shadowOffset = NSSize(width: 0, height: -1)
    sh.shadowColor = NSColor.black.withAlphaComponent(0.45)
    sh.set()
    NSColor.white.setStroke()
    path.lineWidth = 2.6
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

// MARK: - 逐帧

let scale: CGFloat = 2
let bg = NSColor(calibratedWhite: darkMode ? 0.086 : 0.918, alpha: 1)

var frames: [CGImage] = []
var t: CGFloat = 0
var nextBeat = 0
while frames.count < Int(duration * fps) {
    while nextBeat < beats.count, t >= beats[nextBeat].at {
        beats[nextBeat].action(); nextBeat += 1
    }
    view.mouseOverride = demoMouse(t)
    view.advance(dt)

    let size = desiredSize()
    guard let petImg = renderPet(size, scale: scale) else { break }

    let cw = Int(canvas.width * scale), ch = Int(canvas.height * scale)
    guard let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: cw, pixelsHigh: ch,
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                     isPlanar: false, colorSpaceName: .deviceRGB,
                                     bytesPerRow: 0, bitsPerPixel: 0),
          let octx = NSGraphicsContext(bitmapImageRep: out) else { break }
    out.size = canvas
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = octx
    bg.setFill()
    NSRect(origin: .zero, size: canvas).fill()

    // 水平居中、顶部对齐：卡片向下生长，太阳位置基本不动
    let x = (canvas.width - size.width) / 2
    let y = canvas.height - pad - size.height
    let cardRect = NSRect(x: x, y: y, width: size.width, height: size.height)
    drawCardBody(in: cardRect, expand: view.expandProgress)
    petImg.draw(in: cardRect)
    if let m = view.mouseOverride {
        // 视图坐标翻转（y 向下），画布不翻转，换算一下
        drawCursor(at: NSPoint(x: x + m.x, y: y + size.height - m.y))
    }
    NSGraphicsContext.restoreGraphicsState()

    if let cg = out.cgImage { frames.append(cg) }
    t += dt
}

// MARK: - 写 GIF

let outPath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "demo.gif"
guard let dest = CGImageDestinationCreateWithURL(
        URL(fileURLWithPath: outPath) as CFURL,
        UTType.gif.identifier as CFString, frames.count, nil) else {
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
print("✓ \(frames.count) 帧 · \(Int(fps))fps · \(Int(canvas.width * scale))×\(Int(canvas.height * scale)) px · \(outPath)")
