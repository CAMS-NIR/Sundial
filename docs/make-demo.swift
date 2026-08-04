// Off-screen rendering of the demo assets: the GIF used in the README, plus the portrait video for social media.
// No screen recording — this drives PetView.advance() frame by frame, so the timeline is completely under control
// and re-running it gives the same result.
//
// Top-level statements are only allowed in a file called main.swift, so copy it somewhere first:
//
//   cp docs/make-demo.swift /tmp/main.swift && cd /tmp
//   swiftc -O main.swift <repo>/src/{PetView,Theme,Model,Activity,Usage,Auth}.swift -o gifgen
//   ./gifgen demo.gif             # light GIF
//   ./gifgen demo-dark.gif dark   # dark GIF
//   ./gifgen portrait.mp4              # portrait video (1080×1920, 60fps)
//
// When the output is a .mp4 it switches to a portrait video automatically: a GIF cannot carry "big and long and
// high refresh rate" all at once — each of the three multiplies the file size directly; H.264 does not have that
// problem, so the full duration and the high frame rate go into the video.
//
// The card's edge light is drawn by PetView.drawCardEdge(); the app itself has it too.
// What is added here on top is the glass itself: on a real machine NSGlassEffectView blurs out the desktop behind it,
// off-screen there is no such layer, so we have to lay down a background ourselves and then Gaussian-blur the card area,
// otherwise the "glass" looks exactly like an opaque card and you cannot see the material at all.

import AppKit
import AVFoundation
import CoreImage
import ImageIO
import UniformTypeIdentifiers

let outPath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "demo.gif"
let isVideo = outPath.lowercased().hasSuffix(".mp4") || outPath.lowercased().hasSuffix(".mov")
let darkMode = CommandLine.arguments.contains("dark")   // videos default to light mode
/// "Dodge" mode: no session at all from start to finish, the sun stays asleep, and when the cursor comes close it
/// pulls its rays back in and leans away.
/// The difference from the default timeline is not only the content — being asleep the whole way through means the
/// breathing rate is a constant 1.0 rad/s, so the "how long it stays awake" adjustment knob is gone and the only way
/// to close the loop is to search for an approximate recurrence point (see findLoopLength)
let dodge = CommandLine.arguments.contains("dodge")

// MARK: - Demo data

/// **These three percentages must not be changed casually**: the angular frequency of the gauge tug is
/// rate = 0.9 + 0.011×usage%, and for it to turn a whole number of revolutions within one loop, the usage% is
/// solved backwards out of loopT.
/// Across 5–99 only 21% is near-exact (phase residual 0.00044 rad = 2% of how far a normal frame moves),
/// and the next best, 55%, is 0.049 rad ≈ two frames. Seam as measured: 21% on both rings gives 1.17× the baseline,
/// switching to 21/55 drops it to 2.07× — so both rings take 21%.
/// The price is that the sun in the demo wears its relaxed expression and the rings look rather empty; that is what
/// closing the loop cost.
/// The "weekly · all models" row gets no ring and takes no part in the tug, so it can be anything at all as long as
/// it is smaller than Fable.
func demoRows() -> [UsageRow] {
    [
        UsageRow(label: "5 hours", percent: 21,
                 resetAt: Date().addingTimeInterval(66 * 60), priority: 0),
        UsageRow(label: "Weekly · all models", percent: 12,
                 resetAt: Date().addingTimeInterval(3 * 86400), priority: 1),
        UsageRow(label: "Weekly · Fable", percent: 21,
                 resetAt: Date().addingTimeInterval(3 * 86400 - 10000), priority: 2),
    ]
}

func demoSession(elapsed: TimeInterval) -> SessionActivity {
    SessionActivity(id: "demo", title: "Example session", busy: true, waiting: false,
                    since: Date().addingTimeInterval(-elapsed), unread: false,
                    finishedAt: nil, ctxTokens: 393_000, ctxLimit: 1_000_000)
}

// MARK: - Scene

let winW: CGFloat = 198          // matches AppDelegate.winW
let compact = PetView.compactSide

/// One complete timeline. It has to be reproducible — when hunting for the seamless loop point we run it once
/// cheaply, then, with the length settled, render it properly from the start again; both passes must walk through
/// exactly the same states
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
        view.clipsToBounds = true   // on a real machine the window clips; off-screen there is no window edge, so clip ourselves
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: winW, height: 400),
                          styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: darkMode ? .darkAqua : .aqua)
        window.contentView = view
    }

    /// A replica of AppDelegate.expandedHeight(): that one is private, so this one has to be changed alongside it
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
        retimeToAnimationClock()
    }

    /// Re-anchor every "displayed time worked out from the real clock" onto the animation clock.
    ///
    /// The words on screen — "running for 1 min 35 s", "just updated" — are worked out by subtracting since /
    /// lastFetch from Date(). Rendering one clip takes tens of seconds of real time, so the same animation moment
    /// lands on a different real time in two different renders and the seconds reading ends up one notch apart —
    /// the two renders are not reproducible, and **the light version and the dark version will also show different
    /// words at the same moment**.
    /// If, while editing, you cut between light and dark on a beat at a spot like that, that line of text jumps.
    /// Once it is re-anchored every frame, the displayed time follows only the animation clock and has nothing to
    /// do with how long rendering takes
    private func retimeToAnimationClock() {
        let now = Date()
        model.lastFetch = now
        // Land on "whole second + 0.5" rather than on the exact value: the interface only subtracts with Date() at
        // the moment it draws, and between re-anchoring here and the actual drawing sits the rendering cost (tens of
        // milliseconds, and not steady).
        // Anchored at the midpoint of a whole second there is half a second of slack, so the jitter cannot push the
        // value across a whole-second boundary
        func mid(_ v: Double) -> Double { max(0, v).rounded(.down) + 0.5 }
        for i in model.sessions.indices {
            if model.sessions[i].busy {
                model.sessions[i].since =
                    now.addingTimeInterval(-mid(95 + Double(t - (warmT + 1.60))))
            } else if model.sessions[i].finishedAt != nil {
                model.sessions[i].finishedAt =
                    now.addingTimeInterval(-mid(Double(t - (warmT + sessionEnd))))
            }
        }
    }

    /// Draw the pet on its own into a bitmap. scale = the points-to-pixels multiplier
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
        // size must be set before the context is created: the context works its scale out from "point size : pixel
        // size", and with the order the wrong way round it comes out 1:1, so the view draws itself at half scale into
        // the bottom-left corner of the canvas
        rep.size = size
        guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        view.displayIgnoringOpacity(view.bounds, in: ctx)
        NSGraphicsContext.restoreGraphicsState()
        return rep
    }
}

// MARK: - Timeline

// For the playback to loop end to end, every single animation must be back in its opening-moment state when the
// loop finishes. While idle there are four of them running at once, and their periods have nothing to do with
// one another:
//   ① ray rotation sunSpin —— freezes in place when it stops. This one is already dealt with inside PetView
//      (on stopping it snaps to the nearest 40° detent; with 9-fold symmetry you cannot tell, but the pose is
//      uniquely determined)
//   ② zzz drift —— fmod(t·0.42, 1), period 1/0.42 seconds, driven by absolute time
//   ③ the tug of the gauges on either side —— sin(t·rate), rate = 0.9 + 0.011×usage%, also driven by absolute time
//   ④ body breathing —— an accumulated phase, 1.6 rad/s awake and 1.0 asleep, so it depends on how the timeline
//      is arranged
//
// ② and ③ are functions of absolute time, so the loop length has to be a whole-number multiple of both their
// periods at once. Adding the "whole number of frames" constraint on top, the smallest solution is T = 50/3 seconds:
//   zzz: 0.42 × 50/3 = 7 cycles, a whole number ✓
//   60fps: 50/3 × 60 = 1000 frames, a whole number ✓
// With T fixed, ③ then solves the usage percentage backwards for us (see the note on demoRows).
// ④ has no analytical solution, so we bisect on the moment the "session finishes" until it fits (see solveBreath).
let loopT: CGFloat = 50.0 / 3.0
let loopFrames = isVideo ? 1000 : 400
let dt = loopT / CGFloat(loopFrames)
let fps = CGFloat(loopFrames) / loopT
// A GIF frame delay can only store whole multiples of 1/100 second. 400 frames works out at 0.04167 seconds, stored
// as 0.04 — playback runs 4% fast, but **the frame sequence itself is closed**, and whether it is seamless depends
// on the picture content, not on the playback speed
let gifDelay = 0.04

// Warm-up: let it spin idle for a while before starting to record.
// The ray extension rayPull, the ring easing ringShown and the like are low-pass-filtered quantities; starting from
// an initial value of 0 they need several seconds to converge on their steady state; without a warm-up frame 0 is
// still on its way there while the last frame settled long ago, and they do not join up.
//
// How long the warm-up runs is free — the closure condition depends only on the loop length, not on which phase we
// start recording from. The default version idles for a full period, which also brings ② and ③ back to their t=0 phase.
//
// Dodge mode, on the other hand, uses it as a knob. Asleep the whole way through, the breathing rate is a constant
// 1.0 rad/s, so one round nets 16.667 rad, which is 2.183 rad away from a whole-number multiple of 2π, and
// **the phase cannot be made to line up whatever you do**.
// But the way breathing reaches the picture is sin(φ), and
//     sin(φ) − sin(φ + 2.183) = 1.778 · cos(φ + 1.0915)
// so as long as the starting phase lands where that cos is zero, the breathing **value** at the two ends is strictly
// identical — the phase does not line up, but the value can. The only price is that the breathing runs in opposite
// directions at the two ends, and that difference amounts to 0.016pt per frame, which is negligible
// **The two modes must use the same warm-up length**, otherwise the zzz / breathing / gauge phases at frame 0 are all
// different, and the two clips each loop on their own yet cannot be spliced to each other. The default version's
// warm-up was free anyway (its breathing is aligned via sessionEnd, which has nothing to do with the warm-up), so we
// settle on the value dodge mode needs and both are satisfied
// The warm-up is fixed at one loop length: that way frame 0 is pixel-for-pixel identical in both modes, and the four
// clips (plus the two already published earlier) can be joined end to end in any order.
//
// The price lands on dodge mode. Asleep the whole way through, the breathing rate is a constant 1.0 rad/s, there is
// no knob to turn, one round nets 16.667 rad, which is 2.183 rad away from a whole-number multiple of 2π — first
// frame, duration and being seamless in itself are three things that mathematically cannot all be had, so one of
// them has to be sacrificed.
// I tried moving the warm-up to where the breathing "value" lines up (24.65 seconds), which does bring the seam down
// from 22× to 2×, but then the first frame no longer matches the clips already published; I also tried stretching the
// dodge version to 50 seconds (breathing off by only 0.266 rad, seam 1.59×), but then the durations do not match.
// In the end I went with "same first frame + same duration" and put up with all the ray tips jumping about 4px at
// once at the loop point: you cannot see it in a single playback, but on loop it goes pop periodically
let warmT: CGFloat = loopT
let warmFrames = Int((warmT / dt).rounded())

/// The moment the session "finishes and turns unread". This is the adjustment knob left for the breathing phase —
/// 1.6 rad/s awake and 1.0 asleep, so moving this moment earlier or later changes the total time spent awake
var sessionEnd: CGFloat = 9.50

struct Beat { let at: CGFloat; let action: (Scene) -> Void }
func beatList() -> [Beat] {
    if dodge {
        // **Exactly the same rhythm** as the default version: same duration, same moments of expanding and
        // collapsing, same cursor path. The only difference is that there is no session at all, so the sun stays
        // asleep and the direction of attraction is reversed — when the cursor comes close it pulls its rays back
        // in and leans away.
        // The collapse moment reuses the sessionEnd + 0.85 solved for in the default version, which guarantees the
        // two clips line up
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

// The mouse-attraction stretch: the cursor comes in from the bottom right, sweeps round close past the sun, then
// leaves at the bottom left.
// Fast at both ends and slow in the middle — it has to linger a little for you to see the rays being pulled long,
// the body leaning forward and the eyes following along.
// After that comes a quiet stretch with nobody bothering it, to watch the breathing as it is "pulled and pushed by
// the gauges on either side"
let mouseFrom: CGFloat = 2.30, mouseTo: CGFloat = 5.40

// Dodge mode reuses the same path and the same entry and exit moments — the only difference between the two versions
// is whether there is a session
func demoMouse(_ t: CGFloat) -> NSPoint? {
    let tt = t - warmT
    guard tt >= mouseFrom, tt <= mouseTo else { return nil }
    let u = (tt - mouseFrom) / (mouseTo - mouseFrom)
    let uu = min(1, max(0, u + 0.12 * sin(2 * .pi * u)))   // slow down through the middle
    let a = (12 + 168 * uu) * .pi / 180
    let r = 118 - 84 * sin(.pi * uu)
    // Centre of the sun: in the expanded state it is fixed on the mid-line of the top row (view coordinates are
    // flipped, y points down)
    return NSPoint(x: winW / 2 + cos(a) * r,
                   y: 10 + PetView.topRowH / 2 + sin(a) * r)
}

// MARK: - Canvas

// The demo data never reaches the limit, so the "unlock time" line never appears and the canvas leaves no room for it
let maxH = 10 + PetView.topRowH + 2 + PetView.blockH
    + (PetView.blockGap + 2 + 19 + CGFloat(demoRows().count) * 15 + 18) + 10

/// The video is portrait 1080×1920; the GIF uses a small canvas hugging the card, which saves size
let pixelSize = isVideo ? CGSize(width: 1080, height: 1920)
                        : CGSize(width: (winW + 44) * 2.5, height: (maxH + 44) * 2.5)
/// How much the card is magnified on the canvas (points → pixels)
let cardPx: CGFloat = isVideo ? 4.8 : 2.5   // in the video the card fills the width, so it reads the same as the GIF

// MARK: - Background (so the glass has something to show through)

/// An abstract "desktop wallpaper". A flat-colour backing would make the glass look exactly like an opaque card;
/// there has to be content coming through before the blur reads as glass
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
    // A few soft blobs of colour: gives the glass some colour depth to show through, and gives the blur something
    // to blur
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
/// Blur the whole background up front: the glass only "shines" it through, the background never moves, so blurring
/// it once is enough
let ciContext = CIContext()
let blurred: CGImage = {
    let input = CIImage(cgImage: backdrop)
    // clampedToExtent stops the edges being washed out by transparent pixels; crop back to the original size once
    // the blur is done
    let f = input.clampedToExtent()
        .applyingFilter("CIGaussianBlur", parameters: ["inputRadius": cardPx * 11])
        .cropped(to: input.extent)
    return ciContext.createCGImage(f, from: input.extent) ?? backdrop
}()

// MARK: - Drawing

func ease(_ x: CGFloat) -> CGFloat {
    let p = min(1, max(0, x)); return p * p * (3 - 2 * p)
}

/// The glass card base: "shine" the blurred background through in the shape of the card, then lay a faint white/black
/// on top of it.
/// The edge light is not drawn here — that is PetView.drawCardEdge()'s job, and the app itself needs it too
func drawGlass(in rect: NSRect, radius rad: CGFloat, alpha a: CGFloat, canvas: CGSize) {
    let clip = NSBezierPath(roundedRect: rect, xRadius: rad, yRadius: rad)
    NSGraphicsContext.saveGraphicsState()
    let sh = NSShadow()
    sh.shadowBlurRadius = cardPx * 9
    sh.shadowOffset = NSSize(width: 0, height: -cardPx * 3)
    sh.shadowColor = NSColor.black.withAlphaComponent((darkMode ? 0.55 : 0.24) * a)
    sh.set()
    NSColor.black.withAlphaComponent(0.001).setFill()   // purely for the drop shadow; invisible in itself
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

/// The cursor. Without it the sun reacts to thin air and the viewer has no idea what is going on
func drawCursor(at p: NSPoint, scale s: CGFloat) {
    let pts: [(CGFloat, CGFloat)] = [(0, 0), (0, 16.6), (4.3, 12.7), (7.0, 18.9),
                                     (9.9, 17.6), (7.2, 11.6), (12.0, 11.3)]
    let path = NSBezierPath()
    for (i, q) in pts.enumerated() {
        let v = NSPoint(x: p.x + q.0 * s, y: p.y - q.1 * s)   // the canvas is not flipped, so the arrow points down
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

/// Draw one complete frame (background + glass + pet + cursor) and return the pixel image
func renderFrame(_ sc: Scene) -> CGImage? {
    let size = sc.desiredSize()
    guard let petRep = sc.petBitmap(scale: cardPx) else { return nil }
    let petImg = NSImage(size: size); petImg.addRepresentation(petRep)

    let w = Int(pixelSize.width), h = Int(pixelSize.height)
    guard let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h,
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                     isPlanar: false, colorSpaceName: .deviceRGB,
                                     bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
    out.size = pixelSize                       // 1:1, drawing straight in pixels
    guard let octx = NSGraphicsContext(bitmapImageRep: out) else { return nil }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = octx
    octx.cgContext.draw(backdrop, in: CGRect(origin: .zero, size: pixelSize))

    // The card: centred horizontally; the GIF is top-aligned (the card grows downwards and the sun barely moves),
    // the video is centred as a whole
    let cw = size.width * cardPx, ch = size.height * cardPx
    let x = (pixelSize.width - cw) / 2
    let y = isVideo ? (pixelSize.height - ch) / 2
                    : pixelSize.height - 22 * cardPx - ch
    let card = NSRect(x: x, y: y, width: cw, height: ch)

    let e = sc.view.expandProgress
    if e > 0.01 {                              // fully collapsed there is no card, only the sun
        let r0 = min(cw, ch) / 2
        let rad = min(r0 + (PetView.cardRadius * cardPx - r0) * e, r0)
        drawGlass(in: card, radius: rad, alpha: ease(min(1, e / 0.45)), canvas: pixelSize)
    }
    petImg.draw(in: card)
    if let m = sc.view.mouseOverride {
        // view coordinates are flipped (y points down) while the canvas is not, so convert
        drawCursor(at: NSPoint(x: x + m.x * cardPx, y: y + ch - m.y * cardPx),
                   scale: cardPx * 0.62)
    }
    NSGraphicsContext.restoreGraphicsState()
    return out.cgImage
}

// MARK: - Aligning the breathing phase

/// Idle for one period, then run a full round and return the net gain in the breathing phase over that round.
/// It only advances and never draws — breathing depends only on sleepT and dt, not on whether anything is drawn,
/// so it is fast
func breathGain() -> CGFloat {
    let sc = Scene()
    for _ in 0..<warmFrames { sc.step(dt) }
    let a = sc.view.breathPhaseSnapshot
    for _ in 0..<loopFrames { sc.step(dt) }
    return sc.view.breathPhaseSnapshot - a
}

/// The breathing phase has no analytical solution, so we bisect on the moment the "session finishes" until it fits:
/// 1.6 rad/s awake and 1.0 asleep, so moving it one second later banks an extra 0.6 rad over a round.
/// The goal is to land the net gain on a whole-number multiple of 2π
func solveBreath() {
    let twoPi = CGFloat.pi * 2
    var lo: CGFloat = 4.5, hi: CGFloat = 12.5
    sessionEnd = lo; let gLo = breathGain()
    sessionEnd = hi; let gHi = breathGain()
    // take the whole-number multiple that is reachable inside the interval
    let target = (gLo / twoPi).rounded(.up) * twoPi
    guard target <= gHi else {
        print(String(format: "  breath: no reachable multiple of 2pi in [%.2f, %.2f] (net gain %.3f-%.3f); keeping the default",
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
    print(String(format: "  breath: session ends at %.4fs, net gain %.4f rad = %.3f turns, residual %.2e rad",
                 sessionEnd, g, g / twoPi, residual))
}
// In dodge mode it is asleep the whole way through, the breathing rate is a constant 1.0 rad/s, the "how long it
// stays awake" knob does not exist, and the breathing phase cannot be aligned (zzz wants whole cycles, breathing
// wants a whole-number multiple of 2π, and 0.42×2π is irrational).
// Since the duration has to match the default version, we simply have to live with this residual — the self-check
// below measures it
solveBreath()

// MARK: - The real render

let scene = Scene()
for _ in 0..<warmFrames { scene.step(dt) }      // warm-up, not recorded
var frames: [CGImage] = []
var firstPetRep: NSBitmapImageRep?
while frames.count < loopFrames {
    scene.step(dt)
    if frames.isEmpty { firstPetRep = scene.petBitmap(scale: 1) }
    guard let f = renderFrame(scene) else { break }
    frames.append(f)
}

// The fingerprint of frame 0. The two modes must produce exactly the same one, otherwise the two clips will not
// splice — this is a separate matter from "each of them loops on its own", and it cannot be reasoned about, it has
// to be measured
if let f0 = firstPetRep, let d = f0.bitmapData {
    var h: UInt64 = 1469598103934665603
    for i in 0..<(f0.bytesPerRow * f0.pixelsHigh) {
        h = (h ^ UInt64(d[i])) &* 1099511628211
    }
    print(String(format: "  frame 0 fingerprint %016llx (%dx%d)", h, f0.pixelsWide, f0.pixelsHigh))
}

// Self-check: the frame after the last frame should be exactly identical to frame 0 — that is what joining up means.
// While we are at it, measure the difference between two adjacent frames in the idle stretch, as a baseline for
// "one normal frame step"
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
        print(String(format: "  seam %.4f/255; adjacent-frame baseline %.4f/255 (ratio %.2fx)",
                     seam, base, seam / base))
    }
}

// MARK: - Output

func fail(_ s: String) -> Never {
    FileHandle.standardError.write((s + "\n").data(using: .utf8)!); exit(1)
}

let url = URL(fileURLWithPath: outPath)
try? FileManager.default.removeItem(at: url)

if isVideo {
    guard let writer = try? AVAssetWriter(outputURL: url, fileType: .mp4) else {
        fail("could not create the video writer")
    }
    let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
        AVVideoCodecKey: AVVideoCodecType.h264,
        AVVideoWidthKey: Int(pixelSize.width),
        AVVideoHeightKey: Int(pixelSize.height),
        AVVideoCompressionPropertiesKey: [
            AVVideoAverageBitRateKey: 40_000_000,
            // All I-frames. The first frame would otherwise be a key frame and the last a predicted frame, which
            // are quantised differently, so two frames with identical content still decode about 0.6/255 apart —
            // and raising the bitrate does not cure it (9→24→40 Mbps barely moved it). Only encoding every frame
            // independently makes them match.
            // The price is a file several times larger, but this is material for social media, so we do not care
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
        guard let pool = adaptor.pixelBufferPool else { fail("no pixel buffer pool") }
        var pb: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pb)
        guard let buf = pb else { fail("could not allocate a pixel buffer") }
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
    if writer.status != .completed { fail("video write failed: \(writer.error?.localizedDescription ?? "?")") }
} else {
    guard let dest = CGImageDestinationCreateWithURL(
            url as CFURL, UTType.gif.identifier as CFString, frames.count, nil) else {
        fail("could not create the GIF")
    }
    CGImageDestinationSetProperties(dest, [
        kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]
    ] as CFDictionary)
    let props = [kCGImagePropertyGIFDictionary: [
        kCGImagePropertyGIFDelayTime: gifDelay,
        kCGImagePropertyGIFUnclampedDelayTime: gifDelay,
    ]] as CFDictionary
    for f in frames { CGImageDestinationAddImage(dest, f, props) }
    if !CGImageDestinationFinalize(dest) { fail("GIF write failed") }
}

let mb = (try? FileManager.default.attributesOfItem(atPath: outPath)[.size] as? Int) ?? 0
print("✓ \(frames.count) frames · \(Int(fps))fps · \(Int(pixelSize.width))x\(Int(pixelSize.height)) px"
      + " · \(String(format: "%.1f", Double(mb ?? 0) / 1_048_576))MB · \(outPath)")
