// Sundial — a desktop pet that shows Claude Code usage and session status
// This file was split out of main.swift

import AppKit

// MARK: - Pet view

final class PetView: NSView {
    let model: PetModel
    var onRightClick: ((NSEvent) -> Void)?
    var onDoubleClick: (() -> Void)?
    var onHoverChange: ((Bool) -> Void)?
    var onMarkRead: ((String) -> Void)?
    /// The minimise button was pressed, or the sun was clicked while minimised. The window layer
    /// owns the flag and its persistence; this view only reports the gesture.
    var onToggleMinimised: (() -> Void)?

    private var t: CGFloat = 0                 // animation clock
    private var blinkUntil: CGFloat = -1
    private var nextBlinkAt: CGFloat = 2
    private var spinPhase: CGFloat = 0         // loops over 0–1, so the ends join seamlessly
    private var sunSpin: CGFloat = 0
    private var ringShown: [CGFloat] = [0, 0]   // the two rings' currently displayed values (outer/inner), eased towards the target
    private var blockRects: [(String, NSRect)] = []  // for hit testing
    private var loginButtonRect: NSRect = .zero
    private var minimiseButtonRect: NSRect = .zero
    /// Timed easing. Exponential smoothing (smoothStep) is always quick at the start and slow at the end:
    /// when collapsing, most of the distance is covered in the first 0.1 seconds and the last little bit
    /// grinds along slowly — it simply snaps out of existence rather than fading.
    /// Switched to a fixed-duration S-curve, so the pace is spread evenly and collapsing looks like collapsing.
    private struct Tween {
        private(set) var value: CGFloat = 0
        private var from: CGFloat = 0
        private var to: CGFloat = 0
        private var startedAt: CGFloat = -99
        /// Return value: did anything change this frame (used to decide whether to tell the window to re-lay out)
        mutating func step(to target: CGFloat, now: CGFloat,
                           dur: CGFloat, instant: Bool) -> Bool {
            if target != to { from = value; to = target; startedAt = now }
            if instant {
                let changed = value != target
                value = target
                return changed
            }
            guard value != to else { return false }
            let p = dur <= 0 ? 1 : min(1, (now - startedAt) / dur)
            let next = p >= 1 ? to : from + (to - from) * easeInOut(p)
            let changed = next != value
            value = next
            return changed
        }
    }
    private var hoverTween = Tween()
    private var expandTween = Tween()
    var hoverProgress: CGFloat { hoverTween.value }      // 0–1, how far the details have expanded
    var expandProgress: CGFloat { expandTween.value }    // 0 = only the sun left, 1 = the full card
    var reduceMotion = false        // the system's "Reduce motion"
    var reduceTransparency = false  // the system's "Reduce transparency": we draw an opaque backing ourselves
    // The system's "Increase contrast": the drawing code does not currently use it. It is kept because the
    // App layer writes it in every time it reads the accessibility settings, so deleting it means changing
    // that side too; if we ever do use it, this is where to thicken strokes and raise text contrast
    var increaseContrast = false


    /// Only interaction and transitions need the full frame rate; plain breathing and blinking do fine on a low one
    var needsFullFrameRate: Bool {
        if model.anyBusy { return true }                 // spinner / rotating rays
        if mousePoint != nil { return true }             // ray gravity
        if abs(hoverProgress - (model.hovered || model.detailsPinned ? 1 : 0)) > 0.001 { return true }
        if abs(expandProgress - expandTargetValue) > 0.001 { return true }
        return false
    }

    /// When the limit you have used up unlocks again. **This line is only drawn once a limit really is at
    /// its ceiling (the sun turns into ✖)** — while there is headroom left, "how long until it resets" is
    /// a pointless sentence that takes up a line and makes the card taller as well;
    /// once it is full, it becomes the one thing you still want to know.
    /// If several are over the limit at once, take the one that unlocks earliest. nil = not full, so this line is not drawn
    func soonestResetText() -> String? {
        let now = Date()
        let next = model.rows
            .filter { $0.percent >= 100 }
            .compactMap { r -> (Date, String)? in
                guard let d = r.resetAt, d > now else { return nil }
                return (d, r.label)
            }
            .min { $0.0 < $1.0 }
        guard let (date, label) = next else { return nil }
        // A long label such as "Weekly · All models" is cut down to its first segment; 198pt of width cannot fit the whole thing
        let short = label.components(separatedBy: " · ").first ?? label
        return "\(short) · resets in \(compactReset(date))"
    }

    /// For AppDelegate.expandedHeight(): does this line take up any room or not
    var resetLineHeight: CGFloat { soonestResetText() == nil ? 0 : PetView.resetLineH }

    private var expandTargetValue: CGFloat {
        if model.minimised { return 0 }   // minimised wins over everything, hover included
        // Use blocks rather than visibleSessions: while a block is still fading out the window must not
        // collapse ahead of it, otherwise the two animations pile on top of each other and it still looks like a snap
        return (model.hovered || model.detailsPinned || !blocks.isEmpty
            || model.loading || (model.rows.isEmpty && model.errorMsg != nil)) ? 1 : 0
    }
    var onHoverProgress: (() -> Void)?               // per-frame callback, drives the window height
    private var mousePoint: NSPoint?                 // mouse position inside the view (the source of the pull)
    /// For the off-screen-rendered demo GIF: hand it the cursor position directly and skip the real cursor. Always nil in normal operation
    var mouseOverride: NSPoint?
    private var petCenter: NSPoint = .zero           // the sun's centre on the previous frame
    private var rayPull = [CGFloat](repeating: 0, count: PetView.rayCount)  // how far each ray is extended
    private var bodyLean = NSPoint.zero              // the whole creature leans a little towards the mouse
    private var eyeShift = NSPoint.zero              // the pupils look towards the mouse
    private var perk: CGFloat = 0                    // 0–1, the little perk-up when something comes close
    /// 0 = awake, 1 = dozing. isSunAsleep used to be a hard boolean, so the colour / eyes / zzz / ray
    /// rotation angle all switched over within one frame — the moment a session stopped, the sun snapped
    /// grey. Changed to a continuous quantity that everything else interpolates against
    private var sleepT: CGFloat = 1
    /// 0 = not full, 1 = at the ceiling. At the ceiling the eyes turn into ✖, so you see at a glance that this one is used up
    private var deadT: CGFloat = 0
    /// The breathing phase is accumulated separately. Awake and asleep breathe at different rates, and
    /// changing the frequency of the sin directly makes the phase jump on the switching frame, which looks like a twitch
    private var breathPhase: CGFloat = 0
    /// Read-only, for docs/make-demo.swift to line the seamless loop up.
    /// The other oscillations (zzz, the tug from the gauges on either side) are all functions of absolute
    /// time, so an outside caller can work them out itself;
    /// breathing alone is an accumulated quantity — 1.6 awake, 1.0 asleep, with a transition stretch in between — and cannot be derived
    var breathPhaseSnapshot: CGFloat { breathPhase }
    /// How far a session block has appeared/disappeared. The window height has to be computed from this
    /// continuous value and not by counting blocks — the count is discrete, so the instant the last block
    /// goes the window drops 50pt within one frame and swallows all the easing.
    /// A block that is fading out has to hold on to its own data, otherwise there is no way to keep drawing it.
    private struct BlockAnim { var s: SessionActivity; var tw = Tween() }
    private var blocks: [BlockAnim] = []
    /// The height the session-block area currently occupies (varies continuously).
    /// Must be clamped to 0: when sum is very small, sum*56-6 is negative and the window shrinks past the target before springing back
    var blocksHeight: CGFloat {
        let sum = blocks.reduce(0) { $0 + $1.tw.value }
        return max(0, sum * (PetView.blockH + PetView.blockGap) - PetView.blockGap)
    }

    static let topRowH: CGFloat = 64
    static let blockH: CGFloat = 44        // title + status + context line (the bar folded into the ring)
    static let blockGap: CGFloat = 6
    static let maxBlocks = 4
    static let petScale: CGFloat = 0.44
    static let cardRadius: CGFloat = 26     // matches AppDelegate.expandedRadius
    static let compactSide: CGFloat = 88  // the window's side length when collapsed (only the sun left)
    static let resetLineH: CGFloat = 15   // the height of the "soonest reset" line
    static let rayCount = 9               // an odd number, which turns more naturally
    static let rayMaxPull: CGFloat = 13   // maximum extension when pointing straight at the mouse and close to it (pt)
    static let gaugeMaxPull: CGFloat = 9.5 // maximum extension towards a gauge's side when that gauge is full (pt)
    /// The cap once the two forces are added together: when collapsed the window is only 88pt square
    /// (radius 44), so a ray that reaches too far is simply clipped off by the window edge
    static let rayPullCap: CGFloat = 18

    init(model: PetModel) {
        self.model = model
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    func advance(_ dt: CGFloat) {
        t += dt
        sleepT = smoothStep(sleepT, toward: isSunAsleep ? 1 : 0, dt: dt, rate: 3.2)
        deadT = smoothStep(deadT, toward: model.maxPercent >= 100 ? 1 : 0, dt: dt, rate: 3.0)
        breathPhase += dt * (1.6 - 0.6 * sleepT)
        // Spinner: normalised phase, so the ends fit together exactly where it wraps
        if model.anyBusy {
            spinPhase += dt * 0.55
            while spinPhase >= 1 { spinPhase -= 1 }
        }
        if model.anyBusy && !model.asleep {
            sunSpin += dt * 0.9
            while sunSpin > .pi * 2 { sunSpin -= .pi * 2 }
        } else if sunSpin != 0 {
            // On stopping, settle onto the nearest detent. The rays have 9-fold symmetry, so any integer
            // multiple of 40° looks the same, which makes this step invisible — but the idle pose is now
            // uniquely determined rather than "wherever it happened to stop last time". The one in the
            // menu bar already returned to zero when it stopped (statusSpin = 0), so the two now agree
            let step = CGFloat.pi * 2 / CGFloat(PetView.rayCount)
            let target = (sunSpin / step).rounded() * step
            sunSpin = smoothStep(sunSpin, toward: target, dt: dt, rate: 4)
            if abs(sunSpin - target) < 0.0005 { sunSpin = target }
        }
        // The rings ease towards their values. **Keyed by position, not by label** — the right-hand ring
        // shows "the tightest weekly limit", and which one that is changes hands (Fable being overtaken by
        // "all models", say). Keyed by label, a handover means the new label has no history and has to grow
        // up from 0, which looks as though usage suddenly reset to zero
        // (measured: 216° dropping to 54° in one frame, then taking half a second to climb back to 259°).
        // Keyed by position it is just the same ring travelling from the old value to the new one, which matches intuition.
        let ringTargets = model.ringRows
        for (i, row) in [ringTargets.outer, ringTargets.inner].enumerated() {
            // A ring is drawn at most one full turn; anything past the limit is left for the number in the middle (106%, say) to say
            let target = row.map { min(1, CGFloat($0.percent) / 100) } ?? 0
            let cur = ringShown[i]
            ringShown[i] = abs(cur - target) > 0.0005
                ? smoothStep(cur, toward: target, dt: dt, rate: 5) : target
        }
        updateMousePoint()
        // Ray gravity: the ones facing the mouse stretch out, the ones facing away pull back, and the closer it is the more obvious it gets
        // Pointer gravity is movement that tracks the hand, so it stays off under Reduce Motion; breathing and rotation are unaffected
        let targets = reduceMotion ? [CGFloat](repeating: 0, count: PetView.rayCount)
            : rayPullTargets()
        for i in 0..<PetView.rayCount {
            rayPull[i] = smoothStep(rayPull[i], toward: targets[i], dt: dt, rate: 9)
        }
        // Whole-body lean + gaze following + perk-up: the same field as the rays, eased together
        let field = reduceMotion ? nil : mouseField()
        // Awake it leans in (+4.2), asleep it shies away (-3.0). Interpolated continuously by sleepT, so it
        // passes through 0 on the way — "neither shying away nor leaning in, then slowly reversing" — which is more natural than flipping the sign instantly
        let leanMax: CGFloat = 4.2 * (1 - sleepT) - 3.0 * sleepT
        let lean = field.map {
            NSPoint(x: $0.ux * leanMax * $0.proximity,
                    y: $0.uy * leanMax * $0.proximity)
        } ?? .zero
        // If there is a mouse, look at the mouse (the eyes follow sooner and further than the body does — it
        // is already watching you from a fair way off);
        // when nobody is paying it any attention, it glances now and then at the gauges on either side
        let eye: NSPoint
        if let f = field {
            let k = 1.7 * (1 - sleepT)      // asleep, the pupils stop following people
            eye = NSPoint(x: f.ux * k * min(1, f.proximity * 2.4),
                          y: f.uy * k * min(1, f.proximity * 2.4))
        } else {
            eye = .zero          // with no mouse it just looks straight ahead, no more glancing about
        }
        bodyLean = NSPoint(x: smoothStep(bodyLean.x, toward: lean.x, dt: dt, rate: 7),
                           y: smoothStep(bodyLean.y, toward: lean.y, dt: dt, rate: 7))
        eyeShift = NSPoint(x: smoothStep(eyeShift.x, toward: eye.x, dt: dt, rate: 12),
                           y: smoothStep(eyeShift.y, toward: eye.y, dt: dt, rate: 12))
        perk = smoothStep(perk, toward: (field?.proximity ?? 0) * (1 - sleepT),
                          dt: dt, rate: 8)
        // Session blocks appearing/disappearing: the ones still there are **reordered to follow the order of
        // visible**, and the ones that have gone are put back in place to fade out.
        // It used to iterate in the old order and append every new block, so the ordering Activity had
        // carefully arranged — "waiting on you first → running → unread" — only took effect on the one
        // occasion blocks was built up from empty; after that, a session that threw up a prompt was still
        // drawn in its old slot, and with 5 sessions it could even end up in the last one.
        let visible = model.visibleSessions
        var nextBlocks: [BlockAnim] = []
        var blocksChanged = false
        for s in visible {
            if var old = blocks.first(where: { $0.s.id == s.id }) {
                old.s = s
                blocksChanged = old.tw.step(to: 1, now: t, dur: 0.34,
                                            instant: reduceMotion) || blocksChanged
                nextBlocks.append(old)
            } else {
                var b = BlockAnim(s: s)
                _ = b.tw.step(to: 1, now: t, dur: 0.34, instant: reduceMotion)
                nextBlocks.append(b)
                blocksChanged = true
            }
        }
        // Anything no longer in visible goes back at its old relative position and fades out in place, rather than suddenly jumping somewhere else
        for (i, old0) in blocks.enumerated()
        where !visible.contains(where: { $0.id == old0.s.id }) {
            var fading = old0
            blocksChanged = fading.tw.step(to: 0, now: t, dur: 0.5,
                                           instant: reduceMotion) || blocksChanged
            if fading.tw.value > 0.004 {
                nextBlocks.insert(fading, at: min(i, nextBlocks.count))
            }
        }
        blocks = nextBlocks

        // Hover details + collapse/expand: timed easing, with the window size and the content opacity
        // following in step. Collapsing is given longer than expanding — appearing can be brisk, but
        // disappearing has to be slower or it looks like it was wiped away.
        // Under Reduce Motion the size change lands immediately (the per-frame scaling is the part that causes discomfort)
        let hoverTarget: CGFloat = (model.hovered || model.detailsPinned) ? 1 : 0
        let expandTarget = expandTargetValue
        var changed = hoverTween.step(to: hoverTarget, now: t,
                                      dur: hoverTarget > hoverProgress ? 0.30 : 0.42,
                                      instant: reduceMotion)
        changed = expandTween.step(to: expandTarget, now: t,
                                   dur: expandTarget > expandProgress ? 0.40 : 0.62,
                                   instant: reduceMotion) || changed
        if changed || blocksChanged { onHoverProgress?() }
        // Blinking. It was once deleted along with the "glance at the gauges" behaviour, but the two are not
        // the same thing: glancing at the gauges moves the pupils left and right periodically (which reads
        // as flickering), whereas a blink is just one contraction in height and does not grab the eye
        if t >= nextBlinkAt, deadT < 0.5 {      // ✖ eyes do not blink
            blinkUntil = t + 0.16
            nextBlinkAt = t + CGFloat.random(in: 2.4...6.0)
        }
        needsDisplay = true
    }

    /// Whether the sun is dozing: the drawing and the gravity must use the same test, or the angles end up out by one sunSpin
    private var isSunAsleep: Bool { model.asleep || !model.anyBusy }


    /// The source of the pull is the **global** cursor position, not "it only counts once the mouse is
    /// inside the window". That way the sun is already reacting while the cursor is approaching from
    /// outside the window — gravity is supposed to act at a distance.
    /// Beyond the radius of effect it is cleared, so we do not keep redrawing at the full frame rate.
    private func updateMousePoint() {
        let p: NSPoint
        if let o = mouseOverride {
            p = o
        } else if let win = window {
            p = convert(win.convertPoint(fromScreen: NSEvent.mouseLocation), from: nil)
        } else {
            mousePoint = nil; return
        }
        guard petCenter != .zero else { mousePoint = p; return }
        let dx = p.x - petCenter.x, dy = p.y - petCenter.y
        mousePoint = dx * dx + dy * dy <= 230 * 230 ? p : nil
    }

    /// The mouse's direction and closeness relative to the sun. The rays, the body lean and the gaze
    /// following all come from the same field; otherwise each works it out for itself and they end up out
    /// of alignment with one another when the state changes
    private func mouseField() -> (ux: CGFloat, uy: CGFloat, proximity: CGFloat)? {
        guard let m = mousePoint, petCenter != .zero else { return nil }
        let dx = m.x - petCenter.x, dy = m.y - petCenter.y
        let dist = sqrt(dx * dx + dy * dy)
        guard dist > 0.001 else { return nil }
        // Closeness: strongest right up against the body, essentially gone about 150pt out
        let proximity = 1 / (1 + pow(max(0, dist - 26) / 62, 2))
        guard proximity > 0.02 else { return nil }
        return (dx / dist, dy / dist, proximity)
    }

    /// The direction of ray i, which has to match the algorithm in drawPet exactly or the direction the
    /// force acts in ends up misaligned.
    /// sunSpin already stops accumulating while nothing is busy, so it can simply be included here unconditionally.
    /// It used to be forced to zero while asleep, which amounted to spinning the whole ring of rays back to its starting position within a single frame
    private func rayAngle(_ i: Int) -> CGFloat {
        CGFloat(i) / CGFloat(PetView.rayCount) * 2 * .pi + .pi / 8 + sunSpin
    }

    private func wrapPi(_ a: CGFloat) -> CGFloat {
        var d = a
        while d > .pi { d -= 2 * .pi }
        while d < -.pi { d += 2 * .pi }
        return d
    }

    /// The target extension of each ray, two forces added together:
    ///  ① the mouse — awake it is drawn towards it, dozing it shies away instead
    ///  ② the gauges on either side — the fuller they are used, the further the rays on that side are tugged out
    private func rayPullTargets() -> [CGFloat] {
        var out = [CGFloat](repeating: 0, count: PetView.rayCount)

        if let f = mouseField() {
            let mAngle = atan2(f.uy, f.ux)
            let sign: CGFloat = 1 - 2 * sleepT         // awake it leans in, asleep it shies away, with a continuous transition in between
            let maxPull: CGFloat = PetView.rayMaxPull * (1 - sleepT) + 6 * sleepT
            // The side facing away moves the other way. Awake this is only a small garnish; asleep the
            // shying away has to be visible — as the near side pulls back, the far side has to reach out
            // noticeably, so it reads as the whole body being shoved aside.
            // This coefficient used to be 0.28, and the far side grew by less than two points, which the naked eye simply cannot see
            let recoilK: CGFloat = 0.28 * (1 - sleepT) + 1.05 * sleepT
            for i in 0..<PetView.rayCount {
                let delta = wrapPi(rayAngle(i) - mAngle)
                // cos normalised to 0–1 and then raised to a power. The exponent went from 2.2 down to 1.4:
                // once the rays were cut down to 9, too sharp a falloff means only one of them ever reaches, and you lose the sense of a whole swathe being pulled across
                let alignment = pow(max(0, cos(delta)), 1.4)
                let recoil = -recoilK * pow(max(0, -cos(delta)), 1.8)
                out[i] += maxPull * f.proximity * (alignment + recoil) * sign
            }
        }

        // The tug from the gauges: the left gauge sits due left (π), the right gauge due right (0).
        // The pull only starts at 50% (the warning line) and is strongest when full — so "which side is
        // tight" grows straight into the shape and you do not have to go and read a number. As the rays
        // rotate, the ones being tugged keep changing hands, and the whole ring looks stretched into an ellipse.
        let rings = model.ringRows
        for (dirAngle, row) in [(CGFloat.pi, rings.outer), (CGFloat(0), rings.inner)] {
            guard let row else { continue }
            let pct = CGFloat(row.percent)
            // The amplitude needs a floor. It used to be a linear ramp starting at 50%, so a ring at 60%
            // only got 20% of the full force — a 3.5pt swing, which is as good as no movement at all. Now
            // the minimum is four tenths of the full force, but it still grows with usage, so "which side
            // is tight" can just as well be read off the size of the swing
            let k = 0.4 + 0.6 * min(1, max(0, (pct - 15) / 75))
            let u = max(0, min(1, pct / 100))
            // "Breathing" is not a swelling and fading in strength, it is **a pull and a push**: the
            // positive half-cycle tugs this side's rays outwards and the negative half draws them back, and
            // only that swinging to and fro is visible (varying in strength only between 0.55 and 1.0 with
            // the direction always outwards, you can barely tell it is moving).
            // The pace follows usage directly (not the floored amplitude, otherwise both sides pant at the
            // same speed): about 7 seconds per cycle when idle, about 3 seconds when full.
            // The two sides are half a cycle apart in phase, so the whole ring of rays sways left and right instead of swelling and shrinking together
            let rate = 0.9 + 1.1 * u
            let breath = 0.08 + 0.92 * sin(t * rate + (dirAngle == 0 ? .pi : 0))
            for i in 0..<PetView.rayCount {
                let delta = wrapPi(rayAngle(i) - dirAngle)
                out[i] += PetView.gaugeMaxPull * k * breath * pow(max(0, cos(delta)), 1.4)
            }
        }
        for i in 0..<PetView.rayCount { out[i] = min(out[i], PetView.rayPullCap) }
        return out
    }

    // MARK: Events

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { onDoubleClick?(); return }
        // Clicking an unread session block = mark it read, and does not start a drag
        let p = convert(event.locationInWindow, from: nil)
        if model.needsLogin, loginButtonRect.contains(p) {
            onDoubleClick?()          // the same action as a double click: start logging in
            return
        }
        if !minimiseButtonRect.isEmpty, minimiseButtonRect.contains(p) {
            onToggleMinimised?()
            return
        }
        for (id, rect) in blockRects where rect.contains(p) {
            if model.sessions.first(where: { $0.id == id })?.unread == true {
                onMarkRead?(id)
                return
            }
        }
        // While minimised, a single click on the sun brings the card back. performDrag blocks until
        // the drag finishes, so comparing the window origin across it is what separates a click from
        // a drag: a press that moved the window was a drag and must not also restore.
        let before = window?.frame.origin
        window?.performDrag(with: event)
        if model.minimised, let b = before, let a = window?.frame.origin,
           abs(a.x - b.x) < 2, abs(a.y - b.y) < 2 {
            onToggleMinimised?()
        }
    }
    override func rightMouseDown(with event: NSEvent) { onRightClick?(event) }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(
            rect: .zero,
            options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect],
            owner: self, userInfo: nil))
    }
    override func mouseEntered(with event: NSEvent) {
        mousePoint = convert(event.locationInWindow, from: nil)
        onHoverChange?(true)
    }
    override func mouseMoved(with event: NSEvent) {
        mousePoint = convert(event.locationInWindow, from: nil)
    }
    override func mouseExited(with event: NSEvent) {
        mousePoint = nil
        onHoverChange?(false)
    }
    override func mouseDragged(with event: NSEvent) {
        mousePoint = convert(event.locationInWindow, from: nil)
    }

    // MARK: Accessibility (the whole UI is custom-drawn, so the element tree has to be built by hand)

    // The container itself has to be visible, otherwise the children get hung off the window and the label is never read out either
    override func isAccessibilityElement() -> Bool { true }
    override func accessibilityRole() -> NSAccessibility.Role? { .group }
    override func accessibilityLabel() -> String? { "Claude usage and session activity" }

    /// A pressable accessibility element: runs the action when VoiceOver presses it
    final class ActionElement: NSAccessibilityElement {
        var onPress: (() -> Void)?
        override func accessibilityPerformPress() -> Bool {
            guard let onPress else { return false }
            onPress()
            return true
        }
    }

    /// We have to hold on to these ourselves: AppKit only holds a weak reference to accessibilityParent, so
    /// elements built and returned on the spot are deallocated straight away and assistive technology only
    /// ever reads dead elements (-25202).
    private var axKids: [NSAccessibilityElement] = []
    private var axKeys: [String] = []

    override func accessibilityChildren() -> [Any]? {
        typealias Desc = (key: String, role: NSAccessibility.Role,
                          label: String, value: String?, frame: NSRect)
        var descs: [Desc] = []
        func add(_ key: String, _ role: NSAccessibility.Role,
                 _ label: String, _ value: String?, _ frame: NSRect) {
            descs.append((key, role, label, value, frame))
        }

        let card = bounds
        let midY = card.minY + 10 + PetView.topRowH / 2
        let gaugeR: CGFloat = 26
        let rings = model.ringRows
        // In the collapsed state the gauges are not drawn, so do not report them to assistive technology
        if expandProgress > 0.5 {
            for (row, name, cx) in [
                (rings.outer, "Five-hour usage", card.minX + card.width * 0.17),
                (rings.inner, "Weekly usage", card.maxX - card.width * 0.17),
            ] {
                guard let row else { continue }
                var v = "\(row.percent)% used"
                if let d = row.resetAt { v += ", resets in \(compactReset(d))" }
                add("gauge:" + name, .levelIndicator, name, v,
                    accessibilityFrame(NSRect(x: cx - gaugeR, y: midY - gaugeR,
                                              width: gaugeR * 2, height: gaugeR * 2)))
            }
        }
        if model.needsLogin, loginButtonRect != .zero {
            add("login", .button, "Sign in to Claude account", nil,
                accessibilityFrame(loginButtonRect))
        }
        for (id, rect) in blockRects {
            guard let s = model.sessions.first(where: { $0.id == id }) else { continue }
            var v: String
            if s.waiting { v = "Waiting for you to choose" }
            else if s.background { v = "Background task running" }
            else if s.busy { v = "Thinking" }
            else if s.stalled { v = "Not responding — no new records for some time" }
            else { v = "Finished, unread" }
            if let since = s.since { v += ", running for \(elapsedText(since: since))" }
            if s.ctxLimit > 0, s.ctxTokens > 0 {
                let pct = min(100, max(0, Int(Double(s.ctxTokens) / Double(s.ctxLimit) * 100)))
                v += ", context \(pct)% used"
            }
            add("session:" + id, .button, s.title.isEmpty ? "Claude Code sessions" : s.title,
                v, accessibilityFrame(rect))
        }

        // Only rebuild when the set of elements changes (rebuilding knocks the VoiceOver cursor back to the
        // start); value/position changes are updated in place so the pointer identities stay the same
        let keys = descs.map { $0.key }
        if keys != axKeys {
            axKeys = keys
            axKids = descs.map { d in
                let e = ActionElement()
                e.setAccessibilityRole(d.role)
                e.setAccessibilityParent(self)
                if d.role == .button {
                    let key = d.key
                    e.onPress = { [weak self] in
                        guard let self else { return }
                        if key == "login" { self.onDoubleClick?() }
                        else if key.hasPrefix("session:") {
                            self.onMarkRead?(String(key.dropFirst("session:".count)))
                        }
                    }
                }
                return e
            }
            NSAccessibility.post(element: self, notification: .layoutChanged)
        }
        for (e, d) in zip(axKids, descs) {
            let changed = (e.accessibilityValue() as? String) != d.value
            e.setAccessibilityLabel(d.label)
            e.setAccessibilityValue(d.value)
            e.setAccessibilityFrame(d.frame)   // the window gets dragged about, so the frame has to be refreshed every time
            if changed { NSAccessibility.post(element: e, notification: .valueChanged) }
        }
        return axKids
    }

    /// View coordinates (flipped) → screen coordinates
    private func accessibilityFrame(_ r: NSRect) -> NSRect {
        let inWindow = convert(r, to: nil)
        return window?.convertToScreen(inWindow) ?? inWindow
    }

    // MARK: Drawing

    /// Run a piece of drawing at a given opacity
    private func withAlpha(_ a: CGFloat, _ body: () -> Void) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { body(); return }
        ctx.saveGState()
        ctx.setAlpha(a)
        body()
        ctx.restoreGState()
    }

    /// Light catching the card edge: an inner stroke that is bright at the top left and faint at the bottom right.
    /// The highlight the system glass comes with is very weak, and in dark mode the card almost smears into
    /// the desktop with no visible boundary; only once this ring is added does it stand up.
    /// It fades in along with the expand progress and is not drawn when collapsed.
    private func drawCardEdge(_ rect: NSRect, expand e: CGFloat) {
        guard e > 0.01, rect.width > 2, rect.height > 2 else { return }
        let a = easeInOut(min(1, e / 0.45))
        let r0 = min(rect.width, rect.height) / 2
        let rad = min(r0 + (PetView.cardRadius - r0) * e, r0)
        let w: CGFloat = 1.4
        let band = NSBezierPath()
        band.append(NSBezierPath(roundedRect: rect, xRadius: rad, yRadius: rad))
        band.append(NSBezierPath(roundedRect: rect.insetBy(dx: w, dy: w),
                                 xRadius: max(0, rad - w), yRadius: max(0, rad - w)))
        band.windingRule = .evenOdd
        let dark = effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        NSGraphicsContext.saveGraphicsState()
        band.setClip()
        // The view is flipped: +y points down, so 45° points to the bottom right and the gradient starts at the top-left corner
        NSGradient(colors: [NSColor(calibratedWhite: 1, alpha: (dark ? 0.55 : 0.95) * a),
                            NSColor(calibratedWhite: dark ? 1 : 0.55,
                                    alpha: (dark ? 0.03 : 0.14) * a)],
                   atLocations: [0, 0.72], colorSpace: .deviceRGB)?
            .draw(in: rect, angle: 45)
        NSGraphicsContext.restoreGraphicsState()
    }

    override func draw(_ dirtyRect: NSRect) {
        blockRects.removeAll()
        minimiseButtonRect = .zero
        loginButtonRect = .zero   // without resetting it, the moment a session block appears it steps on the login hot zone left over from the previous frame
        // The glass has been hidden, so add an opaque backing panel here to keep things readable.
        // But likewise do not draw it when fully collapsed — sitting idle there is only a sun, and no content that needs a panel behind it
        let e0 = expandProgress
        if reduceTransparency, e0 > 0.01 {
            let r0 = min(bounds.width, bounds.height) / 2
            let radius0 = r0 + (PetView.cardRadius - r0) * e0
            NSColor.windowBackgroundColor
                .withAlphaComponent(easeInOut(min(1, e0 / 0.45))).setFill()
            NSBezierPath(roundedRect: bounds,
                         xRadius: min(radius0, bounds.width / 2),
                         yRadius: min(radius0, bounds.height / 2)).fill()
        }
        drawCardEdge(bounds, expand: e0)
        // The card's base is handled by NSGlassEffectView (real Liquid Glass); only the content is drawn here
        let card = bounds
        let e = expandProgress

        let rowMidY = card.minY + 10 + PetView.topRowH / 2
        // The sun always stays centred, with the two gauges sitting to either side
        let petY = card.midY + (rowMidY - card.midY) * e
        drawPet(center: NSPoint(x: card.midX, y: petY))

        // The gauges have to fade out before the window does: if they are still there once the window has
        // nearly narrowed down to just the sun, they get sliced clean off by the window edge, which looks
        // like snapping out of existence rather than fading away.
        // They also shrink slightly, so it reads as "pulled back in" rather than "cropped off"
        let g = easeInOut(max(0, (e - 0.34) / 0.66))
        // With no usage data at all (not logged in / no subscription) do not draw those two empty rings;
        // leaving two empty tracks sitting there only makes people think it is broken
        if g > 0.004, !model.rows.isEmpty {
            withAlpha(g) { drawGauges(in: card, midY: rowMidY, scale: 0.84 + 0.16 * g) }
        }
        // Minimise button, top-right. It fades in with the hover detail rather than sitting there
        // permanently: the top row is already tight, and a control needed only occasionally should
        // not compete with the two dials for attention. Discoverability is covered by the
        // right-click menu, which carries the same item.
        if e > 0.5, hoverProgress > 0.02, !model.minimised {
            let r: CGFloat = 7.5
            let c = NSPoint(x: card.maxX - 15, y: card.minY + 12)
            let btn = NSRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2)
            minimiseButtonRect = btn.insetBy(dx: -3, dy: -3)   // a slightly generous hit target
            withAlpha(hoverProgress) {
                NSColor.labelColor.withAlphaComponent(0.10).setFill()
                NSBezierPath(ovalIn: btn).fill()
                let bar = NSBezierPath()
                bar.move(to: NSPoint(x: c.x - 3.6, y: c.y))
                bar.line(to: NSPoint(x: c.x + 3.6, y: c.y))
                bar.lineWidth = 1.6
                bar.lineCapStyle = .round
                NSColor.labelColor.withAlphaComponent(0.62).setStroke()
                bar.stroke()
            }
        }

        guard e > 0.01 else { return }   // when fully collapsed only the sun is left

        var y = card.minY + 10 + PetView.topRowH + 2

        if let soon = soonestResetText() {
            drawText(soon, in: NSRect(x: card.minX + 10, y: y,
                                      width: card.width - 20, height: 13),
                     font: .systemFont(ofSize: 10), color: .secondaryLabelColor,
                     align: .center)
            y += PetView.resetLineH
        }

        if model.loading {
            drawText("Fetching usage…", in: NSRect(x: card.minX, y: y + 6,
                                            width: card.width, height: 16),
                     font: .systemFont(ofSize: 11),
                     color: .secondaryLabelColor, align: .center)
            return
        }

        // When usage cannot be fetched, the message takes over the whole card **only when there are no
        // sessions to show**. The session-status half reads local record files and has nothing to do with
        // logging in or with subscriptions — someone without Max/Pro (the authorisation page turns them away
        // outright) should still get to see what they have running and how much context it has used.
        // This used to return unconditionally, which amounted to switching off the one feature that still worked.
        if model.rows.isEmpty, let msg = model.errorMsg, blocks.isEmpty {
            drawText(msg, in: NSRect(x: card.minX + 13, y: y + 4,
                                     width: card.width - 26, height: 46),
                     font: .systemFont(ofSize: 10.5),
                     color: .secondaryLabelColor,
                     align: .center, lineBreak: .byWordWrapping)
            if model.needsLogin {
                // at least 28pt tall, which meets the macOS minimum for a clickable area
                let btn = NSRect(x: card.midX - 60, y: y + 52, width: 120, height: 30)
                loginButtonRect = btn
                NSColor.coralDeep.setFill()
                NSBezierPath(roundedRect: btn, xRadius: 13, yRadius: 13).fill()
                drawText("Double-click to sign in", in: NSRect(x: btn.minX, y: btn.minY + 6,
                                            width: btn.width, height: 16),
                         font: .systemFont(ofSize: 11, weight: .semibold),
                         color: .white, align: .center)
            }
            return
        }

        // Sessions that are running + ones that have finished but are unread.
        // The height each block takes up grows and shrinks with its own appearance progress, and the block
        // is clipped into that height — so it disappears by rolling up, with the blocks below sliding up in
        // step, rather than a whole block vanishing into thin air
        for b in blocks {
            let slotH = (PetView.blockH + PetView.blockGap) * b.tw.value
            if b.tw.value > 0.995 {
                drawSessionBlock(b.s, at: y, in: card)
            } else if slotH > 0.5 {
                NSGraphicsContext.saveGraphicsState()
                NSBezierPath(rect: NSRect(x: card.minX, y: y,
                                          width: card.width,
                                          height: max(0, slotH - PetView.blockGap * b.tw.value))).setClip()
                withAlpha(b.tw.value) { drawSessionBlock(b.s, at: y, in: card) }
                NSGraphicsContext.restoreGraphicsState()
            }
            y += slotH
        }

        // The details fade in and out with hoverProgress and slide up slightly, in step with the window height
        if hoverProgress > 0.01 {
            NSGraphicsContext.saveGraphicsState()
            // The height the window reserves for the details is interpolated by hoverProgress, whereas what
            // is drawn here is the content at full size. Without clipping, during the 0.30 seconds of
            // expanding and the 0.42 seconds of collapsing the last two lines plus the "updated x minutes
            // ago" line stick out beyond the window and get chopped off. Clip to the card's actual bounds so it looks as though it is being unrolled.
            NSBezierPath(rect: NSRect(x: card.minX, y: y,
                                      width: card.width,
                                      height: max(0, card.maxY - y))).setClip()
            NSGraphicsContext.current?.compositingOperation = .sourceOver
            let ctx = NSGraphicsContext.current?.cgContext
            ctx?.saveGState()
            ctx?.setAlpha(hoverProgress)
            ctx?.translateBy(x: 0, y: (1 - hoverProgress) * 6)
            drawDetails(from: y + 2, in: card)
            ctx?.restoreGState()
            NSGraphicsContext.restoreGraphicsState()
        }
    }

    // MARK: Mascot

    private func drawPet(center: NSPoint) {
        let s = PetView.petScale
        let cx0 = center.x, cy0 = center.y
        let stress = CGFloat(model.maxPercent) / 100.0
        // Someone is waiting on an answer and there is no card to say so. When expanded the glass
        // takes a warm tint for this; folded (which is the whole point of minimising) that channel
        // does not exist, so the sun itself has to carry it — a slow brightening pulse. Gated on the
        // card being closed: while it is open the tinted glass already says it, and two signals for
        // one state is just noise.
        let waitingPulse: CGFloat = (expandProgress < 0.5 && model.sessions.contains { $0.waiting })
            ? 0.5 + 0.5 * sin(t * 2.6) : 0
        // With no session running it dozes: drab and grey, eyes shut, zzz drifting off
        let sT = sleepT                       // 0 = awake, 1 = dozing; everything below interpolates by it
        let breathe = 1 + 0.022 * sin(breathPhase)

        var light = NSColor.coralLight.blended(withFraction: sT, of: .sleepLight)
            ?? NSColor.coralLight
        var deep = NSColor.coralDeep.blended(withFraction: sT, of: .sleepDeep)
            ?? NSColor.coralDeep
        if waitingPulse > 0.001 {
            // Pulse towards the glow colour rather than towards white: white would read as the sun
            // being washed out, whereas brightening within its own family reads as it lighting up
            light = light.blended(withFraction: waitingPulse * 0.55, of: .glowLeft) ?? light
            deep = deep.blended(withFraction: waitingPulse * 0.40, of: .glowLeft) ?? deep
        }
        // The body darkens continuously with usage. It used to change abruptly only past 75%, which meant
        // there were really only two steps; now it deepens all the way along, so a glance at the colour
        // tells you roughly how much has been used without reading a number.
        // Raised to the power of 1.5: at low usage the colour barely changes, and only high up does it darken noticeably
        // The usage signal is kept while asleep too. **The moment there is nothing but a sun left is exactly
        // the moment there is nothing else to look at** — this used to switch the colour off entirely, which
        // meant nothing could be read in the very situation that needed it most (measured: 10% and 99%
        // rendered identically). So it still darkens while asleep, just by a slightly smaller amount and
        // towards a warm dark grey, so it still looks as though it is sleeping rather than ill
        let tint = pow(max(0, min(1, stress)), 1.2) * (0.62 + 0.13 * sT)
        // The top half darkens by four tenths and the bottom half by the full amount: the body is already a
        // light-at-the-top, dark-at-the-bottom gradient, and the face sits fairly high up.
        // Darkening the whole thing at once puts dark brown features on a dark red base and the contrast
        // falls to 2.5:1 (the minimum for graphics is 3:1), which smudges the expression. This way we keep
        // the impression of "the whole thing going darker" and the face is still legible
        let deepenTo: NSColor = NSColor.sunDeepen.blended(withFraction: sT, of: .sleepDeepen)
            ?? .sunDeepen
        let bodyLight = light.blended(withFraction: tint * 0.4, of: deepenTo) ?? light
        let bodyDeep = deep.blended(withFraction: tint, of: deepenTo) ?? deep
        let grad = NSGradient(starting: bodyLight, ending: bodyDeep)

        petCenter = center   // used by the next frame's gravity calculation (it has to be the un-shifted centre, otherwise it self-oscillates)
        // Shift the whole creature a little towards the mouse. Placed after petCenter is assigned, so the offset only affects the picture and not the field calculation
        let cx = cx0 + bodyLean.x, cy = cy0 + bodyLean.y

        // Whichever side a ray faces, it takes on that gauge's colour, with the depth following that gauge's
        // usage. So "the sun is being tugged to the left, and that left half is red" = the left-hand limit
        // is nearly full, and looking at the sun is enough — no need to read the numbers inside the two rings.
        // The tint follows usage only, never the breathing: the colour is the state and the swaying is how
        // the state shows itself, and mixing the two together flickers enough to dazzle you.
        // A fixed glow colour per side + an intensity decided by that side's usage (see Theme.swift).
        // The angular falloff is loosened to 0.5 so that **a whole half** takes on the tint, rather than
        // only the one or two rays pointing straight at it — that is too thin to spot at a glance
        let rings = model.ringRows
        let tintSides: [(angle: CGFloat, color: NSColor, amount: CGFloat)] =
            [(CGFloat.pi, rings.outer), (CGFloat(0), rings.inner)]
                .compactMap { pair -> (angle: CGFloat, color: NSColor, amount: CGFloat)? in
                    guard let row = pair.1 else { return nil }
                    // pair.0 == .pi is the left-hand side
                    let glow = pair.0 > 1 ? NSColor.glowLeft : NSColor.glowRight
                    // Asleep, pull the glow colour in towards the sleep grey — you can still tell gold from pink, but it does not glare
                    let c = glow.blended(withFraction: 0.25 * sT, of: .sleepDeep) ?? glow
                    // **The glow intensity follows this side's usage**: the fuller, the brighter.
                    // This is the only channel left for reading usage in the idle state — with just a sun
                    // there is no ring and no number, and the small difference the darkened grey body makes
                    // simply cannot be seen inside an 88pt square (measured: 10% and 99% look almost the same).
                    // "Fuller is brighter" is also more intuitive than "fuller is darker", and it will not repeat the dark-bruise mistake.
                    let u = max(0, min(1, CGFloat(row.percent) / 100))
                    return (pair.0, c, pow(u, 0.75))
                }

        // Rays: short round-ended bars; the whole ring turns slowly while it is thinking, and as the mouse comes near they get pulled to differing lengths
        let rayCount = PetView.rayCount
        for i in 0..<rayCount {
            let angle = CGFloat(i) / CGFloat(rayCount) * 2 * .pi + .pi / 8 + sunSpin
            let wobble = (1 - sT) * 2.2 * s * sin(t * 1.9 + CGFloat(i) * 1.3)
            let inner: CGFloat = 21 * s
            // The reverse repulsion must not shrink a ray away to nothing, so keep a minimum length
            let outer = max(inner + 4 * s, (49 * s + wobble) * breathe + rayPull[i])
            // The stretched ones also thicken slightly; reaching out for something reads as more effortful than simply getting longer
            let w = 16.5 * s * (1 + 0.2 * max(0, rayPull[i]) / PetView.rayMaxPull)
            let ray = NSBezierPath(roundedRect: NSRect(x: inner, y: -w / 2,
                                                       width: outer - inner, height: w),
                                   xRadius: w / 2, yRadius: w / 2)
            ray.transform(using: AffineTransform(rotationByRadians: angle))
            ray.transform(using: AffineTransform(translationByX: cx, byY: cy))
            // The tint goes on only at the **far end**, with the root keeping its own colour: the colour has
            // been rubbed off from the gauge over on that side, and tinting the whole ray evenly actually
            // hides that relationship. The further it reaches, the denser the tip — so when the breathing
            // pushes a ray towards a gauge the tip lights up, and it fades again as the ray draws back
            var tipColor = bodyDeep
            for side in tintSides {
                let a = pow(max(0, cos(wrapPi(angle - side.angle))), 0.5) * side.amount
                guard a > 0.01 else { continue }
                let reach = max(0, min(1, rayPull[i] / PetView.gaugeMaxPull))
                tipColor = tipColor.blended(withFraction: min(0.95, a * (0.72 + 0.28 * reach)),
                                            of: side.color) ?? tipColor
            }
            if tipColor == bodyDeep {
                bodyDeep.setFill()
                ray.fill()
            } else if let g = NSGradient(colors: [bodyDeep, bodyDeep, tipColor],
                                         atLocations: [0, 0.32, 1],
                                         colorSpace: .deviceRGB) {
                // The inner three tenths keep their own colour before the transition begins, so the colour
                // gathers at the tip rather than gradating along the whole ray; besides, the root is hidden
                // behind the body anyway.
                // The angle is simply the ray's own direction: measured, -angle paints the colour onto the root at 90°/270°
                g.draw(in: ray, angle: angle * 180 / .pi)
            }
        }

        // Body: a gradient that is light at the top and dark at the bottom, giving a fluffy dumpling its sense of volume
        let r = 30 * s * breathe
        let bodyRect = NSRect(x: cx - r, y: cy - r, width: r * 2, height: r * 2)
        let body = NSBezierPath(ovalIn: bodyRect)
        grad?.draw(in: body, angle: -90)

        // Mood: 0 = relaxed, 1 = nearly used up. The eyebrows and the mouth shape follow it, because at this
        // size the bit of curve at the corner of the mouth on its own simply cannot be seen
        let worry = max(0, min(1, (stress - 0.5) / 0.35))

        // Bean eyes
        let eyeY = cy - 2 * s
        let blinkT = blinkUntil - t
        let lidClose = blinkT > 0 ? easeInOut(1 - abs(blinkT / 0.16 - 0.5) * 2) : 0
        NSColor.faceDark.setFill()
        NSColor.faceDark.setStroke()
        // Blinking and falling asleep are folded into a single closedness. Over the 0.6 seconds of falling
        // asleep the eyes shut gradually rather than suddenly switching to an arc, so the flattening of the
        // ellipse and the fading in of the arc overlap for a stretch
        let lid = max(lidClose, sT)
        let arcAlpha = easeInOut(max(0, (lid - 0.62) / 0.38))
        for dx in [-12.0 * s, 12.0 * s] {
            // The pupils look towards the mouse; no offset while the eyes are shut, or the arc ends up crooked
            let ex = cx + dx + eyeShift.x * (1 - sT)
            let eyeY = eyeY + eyeShift.y * (1 - sT)
            let h = 6 * s * (1 - lid)
            let dT = deadT                      // at the ceiling the whole living set of eyes gives way to the ✖
            if h > 0.2, arcAlpha < 1, dT < 1 {
                // Plain bean eyes, no catchlight: at this size that speck of white is only 0.6pt, which is
                // not a highlight but a grain of noise, and it dirties an otherwise clean silhouette
                NSColor.faceDark.withAlphaComponent((1 - arcAlpha) * (1 - dT)).setFill()
                NSBezierPath(ovalIn: NSRect(x: ex - 2.4 * s, y: eyeY - h / 2,
                                            width: 4.8 * s, height: h)).fill()
            }
            if arcAlpha > 0.01, dT < 1 {
                let p = NSBezierPath()
                p.move(to: NSPoint(x: ex - 3 * s, y: eyeY))
                p.curve(to: NSPoint(x: ex + 3 * s, y: eyeY),
                        controlPoint1: NSPoint(x: ex - 1.5 * s, y: eyeY + 2.4 * s),
                        controlPoint2: NSPoint(x: ex + 1.5 * s, y: eyeY + 2.4 * s))
                p.lineWidth = 1.6 * s
                p.lineCapStyle = .round
                NSColor.faceDark.withAlphaComponent(arcAlpha * (1 - dT)).setStroke()
                p.stroke()
            }
            // Used up: the eyes turn into ✖. This sits on top of the sleeping state — when no session is
            // running, "it is already used up" is the thing you ought to read first, ahead of "it is dozing"
            if dT > 0.01 {
                let r = 3.2 * s
                let x = NSBezierPath()
                x.move(to: NSPoint(x: ex - r, y: eyeY - r))
                x.line(to: NSPoint(x: ex + r, y: eyeY + r))
                x.move(to: NSPoint(x: ex - r, y: eyeY + r))
                x.line(to: NSPoint(x: ex + r, y: eyeY - r))
                x.lineWidth = 1.9 * s
                x.lineCapStyle = .round
                NSColor.faceDark.withAlphaComponent(dT).setStroke()
                x.stroke()
            }
        }
        NSColor.faceDark.setFill()
        NSColor.faceDark.setStroke()

        // Eyebrows: they only grow in once it starts getting tense, high on the inside and low on the
        // outside ("/ \") = a worried look.
        // This is the most instantly recognisable difference between the three moods
        if worry * (1 - sT) > 0.02 {
            let lift = 2.4 * s * worry
            let browY = eyeY - 6.5 * s
            for dx in [-12.0 * s, 12.0 * s] {
                let ex = cx + dx
                let innerX = dx < 0 ? ex + 3.2 * s : ex - 3.2 * s
                let outerX = dx < 0 ? ex - 3.2 * s : ex + 3.2 * s
                let b = NSBezierPath()
                b.move(to: NSPoint(x: outerX, y: browY + lift))
                b.line(to: NSPoint(x: innerX, y: browY - lift))
                b.lineWidth = 1.7 * s
                b.lineCapStyle = .round
                NSColor.faceDark.withAlphaComponent(worry * (1 - sT)).setStroke()
                b.stroke()
            }
            NSColor.faceDark.setStroke()
        }

        // Mouth: happy is a wide open arc, tense is pressed flat, used up is a pronounced inverted arc
        let mouth = NSBezierPath()
        let my = cy + 6.5 * s
        if sT > 0.01 {
            let o = NSBezierPath(ovalIn: NSRect(x: cx - 2 * s, y: my - 0.5 * s,
                                                width: 4 * s, height: 5 * s))
            o.lineWidth = 1.4 * s
            NSColor.faceDark.withAlphaComponent(sT).setStroke()
            o.stroke()
        }
        if stress < 0.5 {
            // The control points moved from ±2.6 out to ±4.8: too close together and the curve gets dragged
            // into a sharp-bottomed V, whereas moving them outwards gives a rounded U. The depth is pulled
            // in a little at the same time, so the wider shape keeps the same opening
            let grin = 4.9 * s + 1.8 * s * perk
            mouth.move(to: NSPoint(x: cx - 6.4 * s, y: my - 1.2 * s))
            mouth.curve(to: NSPoint(x: cx + 6.4 * s, y: my - 1.2 * s),
                        controlPoint1: NSPoint(x: cx - 4.8 * s, y: my + grin),
                        controlPoint2: NSPoint(x: cx + 4.8 * s, y: my + grin))
        } else if stress < 0.8 {
            mouth.move(to: NSPoint(x: cx - 4.2 * s, y: my + 1.6 * s))
            mouth.line(to: NSPoint(x: cx + 4.2 * s, y: my + 1.6 * s))   // pressed into a line
        } else {
            // Same reasoning: the inverted arc's control points move outwards too, so the chin does not come to a point
            mouth.move(to: NSPoint(x: cx - 5.6 * s, y: my + 4.0 * s))
            mouth.curve(to: NSPoint(x: cx + 5.6 * s, y: my + 4.0 * s),
                        controlPoint1: NSPoint(x: cx - 4.2 * s, y: my - 0.6 * s),
                        controlPoint2: NSPoint(x: cx + 4.2 * s, y: my - 0.6 * s))
        }
        mouth.lineWidth = 1.7 * s
        mouth.lineCapStyle = .round
        NSColor.faceDark.withAlphaComponent(1 - sT).setStroke()
        mouth.stroke()
        NSColor.faceDark.setStroke()

        if sT > 0.01 {
            for i in 0..<3 {
                let phase = fmod(t * 0.42 + CGFloat(i) * 0.33, 1.0)
                let fade = easeInOut(phase < 0.5 ? phase * 2 : (1 - phase) * 2)
                let size = 9 + CGFloat(i) * 2
                let zx = cx + 26 * s + CGFloat(i) * 9 * s + phase * 6
                let zy = cy - 24 * s - phase * 18 - CGFloat(i) * 6 * s
                let rect = NSRect(x: zx, y: zy, width: 20, height: size + 6)
                let font = NSFont.systemFont(ofSize: size, weight: .bold)
                drawText("z", in: rect.offsetBy(dx: 1, dy: 1), font: font,
                         color: NSColor.faceDark.withAlphaComponent(fade * 0.55 * sT))
                drawText("z", in: rect, font: font,
                         color: NSColor.labelColor.withAlphaComponent(fade * 0.8 * sT))
            }
        }
    }

    /// The small label under the inner ring: the all-models limit shows "Weekly", a model-specific limit shows the model name
    private func weeklyShortName(_ row: UsageRow?) -> String {
        guard let l = row?.label else { return "Weekly" }
        if l.contains("all models") { return "Weekly" }
        return l.replacingOccurrences(of: "Weekly · ", with: "")
    }

    // MARK: The two side-by-side gauges (proportion used)


    /// The ring uses that side's fixed accent colour (see Theme.swift for why the colour no longer changes with usage)
    private func gaugeAccent(isLeft: Bool) -> NSColor {
        isLeft ? .ringLeft : .ringRight
    }

    private func drawGauges(in card: NSRect, midY: CGFloat, scale: CGFloat = 1) {
        let r: CGFloat = 21 * scale
        let lw: CGFloat = 5 * scale
        let rings = model.ringRows
        // left gauge — sun — right gauge, centred in three equal parts
        let gauges: [(UsageRow?, String, CGFloat)] = [
            (rings.outer, "5 hours", card.minX + card.width * 0.17),
            (rings.inner, weeklyShortName(rings.inner), card.maxX - card.width * 0.17),
        ]
        for (k, g) in gauges.enumerated() {
            let (row, name, cx) = g
            let center = NSPoint(x: cx, y: midY)
            guard let row else {
                drawArc(center: center, radius: r, lineWidth: lw,
                        from: 0, to: 360, color: NSColor.labelColor.withAlphaComponent(0.14))
                continue
            }
            let shown = ringShown[k]
            drawArc(center: center, radius: r, lineWidth: lw,
                    from: 0, to: 360, color: NSColor.labelColor.withAlphaComponent(0.14))
            if shown > 0.002 {
                // Fill clockwise starting from straight up. This view is isFlipped, and flipping the canvas
                // vertically flips the direction of rotation with it, which is why increasing angles are
                // what reads as clockwise on screen (checked frame by frame with off-screen rendering)
                drawArc(center: center, radius: r, lineWidth: lw,
                        from: -90, to: -90 + 360 * Double(shown),
                        color: gaugeAccent(isLeft: cx < card.midX), round: true)
            }
            // 11pt text has a line height of about 13pt, and the number box used to start at midY-10 with
            // the label box at midY+3, so they met exactly end to end and the two lines of text were stuck
            // together. Moved the whole thing up and left a 2.6pt gap
            drawText("\(row.percent)%",
                     in: NSRect(x: cx - 22, y: midY - 13, width: 44, height: 14),
                     font: .monospacedDigitSystemFont(ofSize: 11, weight: .semibold),
                     color: .labelColor, align: .center)
            drawText(name, in: NSRect(x: cx - 22, y: midY + 2.6, width: 44, height: 11),
                     font: .systemFont(ofSize: 9),
                     color: .secondaryLabelColor, align: .center)
        }
    }

    /// One shared arc helper: this view is flipped, so clockwise:true + decreasing angles = visually clockwise
    private func drawArc(center: NSPoint, radius: CGFloat, lineWidth: CGFloat,
                         from: Double, to: Double, color: NSColor, round: Bool = false) {
        let p = NSBezierPath()
        p.appendArc(withCenter: center, radius: radius,
                    startAngle: from, endAngle: to, clockwise: to < from)
        p.lineWidth = lineWidth
        if round { p.lineCapStyle = .round }
        color.setStroke()
        p.stroke()
    }

    // MARK: Session blocks

    private func drawSessionBlock(_ s: SessionActivity, at y: CGFloat, in card: NSRect) {
        let box = NSRect(x: card.minX + 9, y: y,
                         width: card.width - 18, height: PetView.blockH)
        blockRects.append((s.id, box))

        NSColor.labelColor.withAlphaComponent(s.busy ? 0.09 : 0.06).setFill()
        NSBezierPath(roundedRect: box, xRadius: 10, yRadius: 10).fill()

        let title = s.title.isEmpty ? "Claude Code" : s.title
        drawText(title, in: NSRect(x: box.minX + 10, y: box.minY + 4,
                                   width: box.width - 40, height: 14),
                 font: .systemFont(ofSize: 10.5, weight: .semibold),
                 color: s.busy ? .labelColor : .secondaryLabelColor)

        let sub: String
        var subColor: NSColor = .secondaryLabelColor
        if s.waiting {
            let e = elapsedText(since: s.since)
            sub = e.isEmpty ? "Waiting for you" : "Waiting for you · \(e)"
            subColor = .labelColor        // grabbing attention is left to the breathing dot on the right; the text only has to stay readable
        } else if s.background {
            let e = elapsedText(since: s.since)
            sub = e.isEmpty ? "Background task running" : "Background task · \(e)"
        } else if s.busy {
            let e = elapsedText(since: s.since)
            sub = e.isEmpty ? "Thinking" : "Thinking · \(e)"
        } else if s.stalled {
            // It has just been a long time with no new records; we do not know whether it finished, so do not falsely report "done"
            let e = elapsedText(since: s.finishedAt)
            sub = e.isEmpty ? "Not responding" : "Not responding · no update for \(e)"
        } else {
            sub = "Unread · " + agoText(s.finishedAt)
        }
        drawText(sub, in: NSRect(x: box.minX + 10, y: box.minY + 18,
                                 width: box.width - 40, height: 13),
                 font: .systemFont(ofSize: 9, weight: s.waiting ? .semibold : .regular),
                 color: subColor)

        // Context usage is now carried by the ring on the right; all that is left here is the
        // absolute figure. The percentage used to be repeated as text next to it, but the ring
        // already says it — and the ring says it at a glance, which the number never did.
        if s.ctxLimit > 0, s.ctxTokens > 0 {
            drawText("Context \(tokenText(s.ctxTokens)) / \(tokenText(s.ctxLimit))",
                     in: NSRect(x: box.minX + 10, y: box.minY + 30, width: box.width - 40, height: 12),
                     font: .systemFont(ofSize: 9.5), color: .labelColor)
        }

        drawSessionRing(s, center: NSPoint(x: box.maxX - 17, y: box.midY), radius: 9)
    }

    /// The ring on the right of a session block. It carries **two** things at once, which is only
    /// legible because they use different channels:
    ///
    ///   · how much of the context window is used — a **static** arc from twelve o'clock, in a
    ///     neutral colour that deepens with the figure
    ///   · what the session is doing — **motion** and **colour**: a coral comet travelling the ring
    ///     while it thinks, or a dot in the middle when it is waiting or unread
    ///
    /// The previous spinner could not have absorbed the context reading: its readability came from
    /// the arc length itself oscillating between 26° and 290°, so length was already taken. Freeing
    /// length for the context figure means motion has to carry "thinking" on its own, and the comet
    /// does that without ever being mistaken for the fill — it is short, it moves, and it is coral
    /// where the fill is neutral.
    private func drawSessionRing(_ s: SessionActivity, center: NSPoint, radius r: CGFloat) {
        let lw: CGFloat = 2.6
        drawArc(center: center, radius: r, lineWidth: lw,
                from: 0, to: 360, color: NSColor.labelColor.withAlphaComponent(0.12))

        if s.ctxLimit > 0, s.ctxTokens > 0 {
            let frac = min(1, CGFloat(s.ctxTokens) / CGFloat(s.ctxLimit))
            // Grey towards the primary text colour. Written this way rather than "grey to black" so
            // that dark mode takes care of itself: labelColor is near-white there, so the same
            // expression reads as grey → white instead of fading into the background.
            // The 0.8 power lifts the low end — at 10% a nearly invisible arc would look like a fault.
            let c = NSColor.labelColor.withAlphaComponent(0.30 + 0.70 * pow(frac, 0.8))
            drawArc(center: center, radius: r, lineWidth: lw,
                    from: -90, to: -90 + 360 * Double(frac), color: c, round: true)
        }

        if s.waiting {
            // Waiting for you: a solid breathing dot in the middle. Distinct from the comet by being
            // still, central, and a deeper coral
            let pulse = 0.55 + 0.45 * (0.5 + 0.5 * sin(t * 3.4))
            NSColor.coralDeep.withAlphaComponent(pulse).setFill()
            let rr: CGFloat = 3.6
            NSBezierPath(ovalIn: NSRect(x: center.x - rr, y: center.y - rr,
                                        width: rr * 2, height: rr * 2)).fill()
        } else if s.busy {
            // The comet. Drawn last so it passes over the context fill rather than under it
            let head = -90 + Double(spinPhase) * 360
            drawArc(center: center, radius: r, lineWidth: lw,
                    from: head - 38, to: head, color: .coralLight, round: true)
        } else if !s.stalled {
            let pulse = 0.55 + 0.45 * easeInOut((sin(t * 1.6) + 1) / 2)
            NSColor.coralLight.withAlphaComponent(pulse).setFill()
            NSBezierPath(ovalIn: NSRect(x: center.x - 3.2, y: center.y - 3.2,
                                        width: 6.4, height: 6.4)).fill()
        }
    }

    // MARK: Hover details

    private func drawDetails(from startY: CGFloat, in card: NSRect) {
        let innerX = card.minX + 13
        let innerW = card.width - 26
        var y = startY

        drawText("Claude usage", in: NSRect(x: innerX, y: y, width: innerW * 0.6, height: 13),
                 font: .systemFont(ofSize: 9.5, weight: .semibold),
                 color: .labelColor)
        if !model.tier.isEmpty {
            drawText(model.tier, in: NSRect(x: innerX + innerW * 0.4, y: y,
                                            width: innerW * 0.6, height: 13),
                     font: .systemFont(ofSize: 9.5),
                     color: .secondaryLabelColor, align: .right)
        }
        y += 19

        // The dot's colour marks **which gauge this row belongs to**, not how high the usage is — the same
        // rule as the rings. This used to still switch colour across three bands at 50/80 while the rings
        // had already changed to fixed colours, so the two rules fought each other: at one and the same
        // 60%, the ring was apricot pink but the list entry was amber, which looks like two different
        // systems. The rows that did not make it onto a gauge (an unselected weekly limit, say) get a
        // neutral grey, so you can see at a glance that this one is not drawn as a ring.
        let shownRows = model.ringRows
        if model.rows.isEmpty {
            drawText(model.needsLogin ? "Not signed in — session activity only" : "Usage unavailable",
                     in: NSRect(x: innerX, y: y, width: innerW, height: 14),
                     font: .systemFont(ofSize: 9.5),
                     color: .secondaryLabelColor)
            y += 15
        }
        for row in model.rows {
            let c: NSColor
            if row.label == shownRows.outer?.label { c = .ringLeft }
            else if row.label == shownRows.inner?.label { c = .ringRight }
            else { c = .tertiaryLabelColor }
            NSBezierPath(ovalIn: NSRect(x: innerX, y: y + 4, width: 6, height: 6)).setClip()
            c.setFill()
            NSBezierPath(ovalIn: NSRect(x: innerX, y: y + 4, width: 6, height: 6)).fill()
            NSBezierPath(rect: bounds).setClip()
            // 86pt of columns on the right, 80 of which is the two numbers plus a 4pt gap between
            // them. An earlier pass squeezed them to 81 and the gap vanished: both columns are right
            // aligned and adjacent, so "Sat 09:38" filling its box ran straight into "12%".
            // 80pt for the label, not 65. The Chinese labels fitted; "Weekly · Fable" did not, and truncating to
            // "Weekly · Fa…" loses the one thing that row is there to tell you — which model it is.
            // The width comes out of the two number columns, which had spare room: "100%" needs 26pt
            // of the 40 it had, and "Fri 18:27" needs 42 of 54
            drawText(row.label,
                     in: NSRect(x: innerX + 11, y: y, width: innerW - 11 - 86, height: 14),
                     font: .systemFont(ofSize: 9.5),
                     color: .secondaryLabelColor)
            // The numbers no longer change colour with usage: colour no longer carries the "how full"
            // information, that is the job of the arc length and of the number itself
            drawText("\(row.percent)%",
                     in: NSRect(x: innerX + innerW - 86, y: y, width: 32, height: 14),
                     font: .monospacedDigitSystemFont(ofSize: 9.5, weight: .medium),
                     color: .labelColor, align: .right)
            drawText(compactReset(row.resetAt),
                     in: NSRect(x: innerX + innerW - 52, y: y, width: 52, height: 14),
                     font: .systemFont(ofSize: 9.5),
                     color: .secondaryLabelColor, align: .right)
            y += 15
        }

        let footer: String
        // The line above has already said "not logged in, showing session status only", so do not repeat it at the bottom
        if let msg = model.errorMsg, !model.rows.isEmpty {
            footer = "⚠︎ " + (msg.components(separatedBy: "\n").first ?? msg)
        } else if let last = model.lastFetch {
            let mins = Int(-last.timeIntervalSinceNow / 60)
            footer = mins <= 0 ? "updated just now" : "updated \(mins) min ago"
        } else {
            footer = ""
        }
        drawText(footer, in: NSRect(x: innerX, y: y + 3, width: innerW, height: 12),
                 font: .systemFont(ofSize: 9.5),
                 color: model.errorMsg == nil ? .tertiaryLabelColor : .secondaryLabelColor)
    }
}

