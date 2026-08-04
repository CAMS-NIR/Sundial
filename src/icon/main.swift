import AppKit
// A standalone icon generator: the geometry and colours are copied straight off the sun part of
// PetView.drawPet (the icons are static assets, they don't follow the code, so if the sun is changed
// this script has to be run again)
let coralLight = NSColor(srgbRed: 0.914, green: 0.596, blue: 0.451, alpha: 1)
let coralDeep  = NSColor(srgbRed: 0.769, green: 0.404, blue: 0.271, alpha: 1)
let faceDark   = NSColor(srgbRed: 0.145, green: 0.106, blue: 0.086, alpha: 1)

func render(size: Int, small: Bool, dark: Bool, path: String) {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)!
    let U = CGFloat(size) / 1024.0            // 1024 is the design baseline

    // The macOS icon canvas: an 824×824 rounded square, centred, with padding all round
    let plate = NSRect(x: 100 * U, y: 100 * U, width: 824 * U, height: 824 * U)
    let plateShape = NSBezierPath(roundedRect: plate, xRadius: 185 * U, yRadius: 185 * U)
    // The dark version's plate uses a warm dark brown rather than a neutral black: the sun is a warm coral,
    // and a cold black next to it looks muddy
    let plateTop = dark ? NSColor(srgbRed: 0.180, green: 0.137, blue: 0.122, alpha: 1)
                        : NSColor(srgbRed: 0.992, green: 0.965, blue: 0.937, alpha: 1)
    let plateBot = dark ? NSColor(srgbRed: 0.106, green: 0.078, blue: 0.067, alpha: 1)
                        : NSColor(srgbRed: 0.949, green: 0.886, blue: 0.827, alpha: 1)
    NSGradient(starting: plateTop, ending: plateBot)!.draw(in: plateShape, angle: -90)

    let cx = plate.midX, cy = plate.midY
    let s = (small ? 7.5 : 7.05) * U                          // overall scale of the sun (it is 0.44 in PetView)
    let rayCount = 9
    (dark ? NSColor(srgbRed: 0.831, green: 0.463, blue: 0.325, alpha: 1) : coralDeep).setFill()
    for i in 0..<rayCount {
        let angle = CGFloat(i) / CGFloat(rayCount) * 2 * .pi + .pi / 8
        let inner = 21 * s, outer = (small ? 44.0 : 49.0) * s, w = (small ? 21.0 : 16.5) * s
        let ray = NSBezierPath(roundedRect: NSRect(x: inner, y: -w / 2,
                                                   width: outer - inner, height: w),
                               xRadius: w / 2, yRadius: w / 2)
        ray.transform(using: AffineTransform(rotationByRadians: angle))
        ray.transform(using: AffineTransform(translationByX: cx, byY: cy))
        ray.fill()
    }
    let r = 30 * s
    let sunTop = dark ? NSColor(srgbRed: 0.957, green: 0.663, blue: 0.518, alpha: 1) : coralLight
    let sunBot = dark ? NSColor(srgbRed: 0.831, green: 0.463, blue: 0.325, alpha: 1) : coralDeep
    NSGradient(starting: sunTop, ending: sunBot)!
        .draw(in: NSBezierPath(ovalIn: NSRect(x: cx - r, y: cy - r, width: r * 2, height: r * 2)),
              angle: 90)   // unflipped coordinate system: light at the top and dark at the bottom needs +90

    // The face (unflipped coordinate system, y points up, so a +y in PetView becomes -y here)
    faceDark.setFill()
    for dx in [-12.0 * s, 12.0 * s] {
        NSBezierPath(ovalIn: NSRect(x: cx + dx - 2.4 * s, y: cy + 2 * s - 3 * s,
                                    width: 4.8 * s, height: 6 * s)).fill()
        NSColor.white.withAlphaComponent(small ? 0 : 0.7).setFill()
        NSBezierPath(ovalIn: NSRect(x: cx + dx - 1.4 * s, y: cy + 2 * s + 0.8 * s,
                                    width: 1.4 * s, height: 1.4 * s)).fill()
        faceDark.setFill()
    }
    let my = cy - 6.5 * s
    let mouth = NSBezierPath()
    mouth.move(to: NSPoint(x: cx - 6.4 * s, y: my + 1.2 * s))
    mouth.curve(to: NSPoint(x: cx + 6.4 * s, y: my + 1.2 * s),
                controlPoint1: NSPoint(x: cx - 2.6 * s, y: my - 5.6 * s),
                controlPoint2: NSPoint(x: cx + 2.6 * s, y: my - 5.6 * s))
    mouth.lineWidth = (small ? 2.6 : 1.7) * s
    mouth.lineCapStyle = .round
    faceDark.setStroke()
    mouth.stroke()

    NSGraphicsContext.restoreGraphicsState()
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path))
}
// `small` is decided by the "logical point count" rather than the pixel count: 32x32@2x is 64 pixels but
// only 32 points, so it should use the simplified version too
let entries: [(Int, Bool, String)] = [
    (16, true, "icon_16x16"), (32, true, "icon_16x16@2x"),
    (32, true, "icon_32x32"), (64, true, "icon_32x32@2x"),
    (128, false, "icon_128x128"), (256, false, "icon_128x128@2x"),
    (256, false, "icon_256x256"), (512, false, "icon_256x256@2x"),
    (512, false, "icon_512x512"), (1024, false, "icon_512x512@2x"),
]
for (px, small, name) in entries {
    render(size: px, small: small, dark: false, path: "Sundial.iconset/\(name).png")
    render(size: px, small: small, dark: true,  path: "Sundial-dark.iconset/\(name).png")
}
print("ok")
