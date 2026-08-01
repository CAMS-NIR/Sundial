// Sundial — 桌面宠物，显示 Claude Code 用量与会话状态
// 本文件由 main.swift 拆分而来

import AppKit

// MARK: - 颜色与小工具

/// 统一配色。整套只有三组：
///   ① 珊瑚族——太阳本体、状态栏图标、上下文条、等待/未读的圆点
///   ② 两个仪表的强调色——蜜金（左）与杏粉（右），以及它们更亮的发光版
///   ③ 系统语义色与中性灰——文字、轨道、没上圈的那几条
/// 原先还有一套「鼠尾草绿／琥珀／砖红」的用量三档，但圆环改成固定色之后
/// 它只剩上下文条和等待圆点在用，等于为两处小元素养着一整套色相——已并入珊瑚族删除。
extension NSColor {
    static let coralLight = NSColor(red: 0.914, green: 0.596, blue: 0.451, alpha: 1) // #E99873
    static let coralDeep  = NSColor(red: 0.769, green: 0.404, blue: 0.271, alpha: 1) // #C46745
    static let sleepLight = NSColor(red: 0.686, green: 0.663, blue: 0.639, alpha: 1)
    static let sleepDeep  = NSColor(red: 0.549, green: 0.525, blue: 0.502, alpha: 1)
    // 睡着时身体压暗的目标色：偏暖的深灰，不能用 sunDeepen（深砖红）——
    // 灰身体掺红会显得病恹恹，正是之前踩过的坑
    static let sleepDeepen = NSColor(red: 0.302, green: 0.278, blue: 0.259, alpha: 1)
    static let faceDark   = NSColor(red: 0.145, green: 0.106, blue: 0.086, alpha: 1)
    // 身体加深用的目标色：固定的深砖红。必须是固定值——早先用过一个随明暗切换的红，
    // 深色模式下它反而更亮，于是越紧张身体越浅，正好反了
    static let sunDeepen  = NSColor(red: 0.545, green: 0.157, blue: 0.114, alpha: 1)

}

/// 两个仪表的固定色，以及那一侧光芒尖端要渐变过去的**发光色**。
///
/// 关键教训：光芒的渐变必须**越往外越亮**。之前让它渐变到深酒红/深紫，
/// 深色压在暖色身体上，看着就是一块淤青——「太阳生病了」。太阳是发光体，
/// 光芒越往尖上越亮才对。所以每侧有两个色：环用饱和一点的（要在玻璃上够对比度），
/// 光芒尖用更亮的（它画在太阳身上，不受背景对比度约束）。
///
/// 不再按用量换色：那件事由中间的数字、弧长和太阳的表情/身体深浅一起报。
/// 固定下来后左右各一个色成了身份标识——不用读标签就知道哪边是哪个。
extension NSColor {
    static let ringLeft   = dyn2((0.737, 0.463, 0.071), (0.949, 0.729, 0.298))  // 蜜金（浅色档压暗到刚好过 3:1）
    static let glowLeft   = dyn2((1.000, 0.808, 0.431), (1.000, 0.855, 0.573))
    static let ringRight  = dyn2((0.776, 0.329, 0.376), (0.925, 0.545, 0.588))  // 杏粉
    static let glowRight  = dyn2((1.000, 0.620, 0.643), (1.000, 0.714, 0.733))

    private static func dyn2(_ light: (CGFloat, CGFloat, CGFloat),
                             _ dark: (CGFloat, CGFloat, CGFloat)) -> NSColor {
        NSColor(name: nil) { ap in
            let c = ap.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: c.0, green: c.1, blue: c.2, alpha: 1)
        }
    }
}

/// 缓入缓出，用于所有动画过渡
func easeInOut(_ x: CGFloat) -> CGFloat {
    let t = max(0, min(1, x))
    return t < 0.5 ? 2 * t * t : 1 - pow(-2 * t + 2, 2) / 2
}

/// 指数平滑：让数值变化连续跟随而不是瞬间跳变
func smoothStep(_ current: CGFloat, toward target: CGFloat, dt: CGFloat, rate: CGFloat = 6) -> CGFloat {
    let k = 1 - exp(-rate * dt)
    return current + (target - current) * k
}


func drawText(_ text: String, in rect: NSRect, font: NSFont, color: NSColor,
              align: NSTextAlignment = .left,
              lineBreak: NSLineBreakMode = .byTruncatingTail) {
    let style = NSMutableParagraphStyle()
    style.alignment = align
    style.lineBreakMode = lineBreak
    let attrs: [NSAttributedString.Key: Any] = [
        .font: font, .foregroundColor: color, .paragraphStyle: style,
    ]
    (text as NSString).draw(in: rect, withAttributes: attrs)
}


/// 状态栏用的小太阳。菜单栏只有 18pt，脸和渐变都糊成一团，所以只画
/// 身体 + 光芒这两样最能认出剪影的东西。
/// 不用模板图（那会被系统涂成纯黑白，珊瑚色就没了）；珊瑚是中间调，
/// 浅色和深色菜单栏上都看得见。
func statusSunImage(spin: CGFloat, asleep: Bool) -> NSImage {
    let side: CGFloat = 18
    let img = NSImage(size: NSSize(width: side, height: side))
    img.lockFocus()
    let c = side / 2
    let body = asleep ? NSColor.sleepDeep : NSColor.coralDeep
    body.setFill()
    let rays = 9
    for i in 0..<rays {
        let a = CGFloat(i) / CGFloat(rays) * 2 * .pi + .pi / 8 + spin
        let inner: CGFloat = 3.4, outer: CGFloat = 8.2, w: CGFloat = 2.5
        let r = NSBezierPath(roundedRect: NSRect(x: inner, y: -w / 2,
                                                 width: outer - inner, height: w),
                             xRadius: w / 2, yRadius: w / 2)
        r.transform(using: AffineTransform(rotationByRadians: a))
        r.transform(using: AffineTransform(translationByX: c, byY: c))
        r.fill()
    }
    let br: CGFloat = 4.6
    NSBezierPath(ovalIn: NSRect(x: c - br, y: c - br, width: br * 2, height: br * 2)).fill()
    img.unlockFocus()
    img.isTemplate = false
    return img
}
