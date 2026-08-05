// Sundial — a desktop pet showing Claude Code usage and session state
// This file was split out of main.swift

import AppKit

// MARK: - Colours and small helpers

/// The unified palette. There are only three groups in the whole thing:
///   1. The coral family — the sun itself, the status bar icon, the context bar, the waiting/unread dots
///   2. The accent colours of the two gauges — honey gold (left) and apricot pink (right), plus their brighter glow versions
///   3. System semantic colours and neutral greys — text, tracks, and the rows that didn't get a ring
/// There used to be a three-tier usage palette of "sage green / amber / brick red" as well, but once the rings
/// were changed to fixed colours only the context bar and the waiting dot still used it, i.e. keeping a whole set
/// of hues alive for two small elements — it has been folded into the coral family and deleted.
extension NSColor {
    static let coralLight = NSColor(red: 0.914, green: 0.596, blue: 0.451, alpha: 1) // #E99873
    static let coralDeep  = NSColor(red: 0.769, green: 0.404, blue: 0.271, alpha: 1) // #C46745
    static let sleepLight = NSColor(red: 0.686, green: 0.663, blue: 0.639, alpha: 1)
    static let sleepDeep  = NSColor(red: 0.549, green: 0.525, blue: 0.502, alpha: 1)
    // Target colour the body is darkened towards while asleep: a warmish dark grey. It must not be
    // sunDeepen (deep brick red) — mixing red into a grey body looks sickly, which is exactly the trap
    // stepped into before
    static let sleepDeepen = NSColor(red: 0.302, green: 0.278, blue: 0.259, alpha: 1)
    static let faceDark   = NSColor(red: 0.145, green: 0.106, blue: 0.086, alpha: 1)
    // Target colour used to deepen the body: a fixed deep brick red. It has to be a fixed value — an earlier
    // version used a red that switched with light/dark mode, and in dark mode it was actually brighter, so the
    // more strained things got the lighter the body became, exactly backwards
    static let sunDeepen  = NSColor(red: 0.545, green: 0.157, blue: 0.114, alpha: 1)

}

/// The fixed colours of the two gauges, plus the **glow colour** the ray tips on that side gradate towards.
///
/// The key lesson: the ray gradient must get **brighter the further out it goes**. An earlier version had it
/// gradate to deep wine red / deep purple, and that dark colour pressed onto the warm body simply looked like
/// a bruise — "the sun is ill". The sun is a light source; its rays ought to get brighter towards the tips.
/// So there are two colours per side: the ring uses the more saturated one (it needs enough contrast on the
/// glass), and the ray tips use the brighter one (it is drawn on the sun's body, so it isn't bound by
/// background contrast).
///
/// Colour no longer changes with usage: that is reported jointly by the number in the middle, the arc length
/// and the sun's expression / body darkness. Once fixed, one colour per side became an identity marker — you
/// can tell which side is which without reading the labels.
extension NSColor {
    static let ringLeft   = dyn2((0.737, 0.463, 0.071), (0.949, 0.729, 0.298))  // honey gold (the light variant darkened to just clear 3:1)
    static let glowLeft   = dyn2((1.000, 0.808, 0.431), (1.000, 0.855, 0.573))
    static let ringRight  = dyn2((0.776, 0.329, 0.376), (0.925, 0.545, 0.588))  // apricot pink
    static let glowRight  = dyn2((1.000, 0.620, 0.643), (1.000, 0.714, 0.733))

    private static func dyn2(_ light: (CGFloat, CGFloat, CGFloat),
                             _ dark: (CGFloat, CGFloat, CGFloat)) -> NSColor {
        NSColor(name: nil) { ap in
            let c = ap.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: c.0, green: c.1, blue: c.2, alpha: 1)
        }
    }
}

/// Ease-in-out, used for every animation transition
func easeInOut(_ x: CGFloat) -> CGFloat {
    let t = max(0, min(1, x))
    return t < 0.5 ? 2 * t * t : 1 - pow(-2 * t + 2, 2) / 2
}

/// Exponential smoothing: lets a value follow along continuously instead of jumping in an instant
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


/// The little sun for the status bar. The menu bar is only 18pt, where the face and the gradients all smear
/// into a blur, so only the two things that make the silhouette recognisable get drawn: body + rays.
/// Not a template image (the system would paint that pure black and white and the coral would be gone);
/// coral is a mid tone, so it is visible on both light and dark menu bars.
func statusSunImage(spin: CGFloat, asleep: Bool) -> NSImage {
    let side: CGFloat = 18
    let img = NSImage(size: NSSize(width: side, height: side))
    img.lockFocus()
    let c = side / 2
    let body = asleep ? NSColor.sleepDeep : NSColor.coralDeep
    body.setFill()
    let rays = 9
    for i in 0..<rays {
        // This has to be a minus sign. Over on the pet side PetView.isFlipped = true, so a positive
        // rotation is clockwise on screen; NSImage.lockFocus(), however, hands you an unflipped context,
        // where the same + spin draws anticlockwise — the two suns would spin in opposite directions.
        // Negating it bends this one back into line
        let a = CGFloat(i) / CGFloat(rays) * 2 * .pi + .pi / 8 - spin
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
