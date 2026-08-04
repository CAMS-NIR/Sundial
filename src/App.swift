// Sundial — a desktop pet that shows Claude Code usage and session state
// This file was split out of main.swift

import AppKit
import Foundation
import ServiceManagement

// MARK: - Window

/// By default the glass view keeps hit testing to itself, which breaks dragging,
/// double-clicking and click-to-mark-read entirely.
/// So forward into contentView; fall back to super when that misses (blank areas still
/// return self, so performDrag keeps working).
@available(macOS 26.0, *)
final class PetGlassView: NSGlassEffectView {
    override func hitTest(_ point: NSPoint) -> NSView? {
        guard let content = contentView, let sv = content.superview else {
            return super.hitTest(point)
        }
        let local = convert(point, from: superview)
        if let hit = content.hitTest(convert(local, to: sv)) { return hit }
        return super.hitTest(point)
    }
}

final class PetWindow: NSWindow {
    // Never becomes key: clicking it doesn't steal focus from the current app, so it never draws that focus ring.
    // Dragging, double-clicking and the right-click menu don't depend on key state; the login field is a separate NSAlert window and is unaffected.
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let model = PetModel()
    var window: PetWindow!
    var petView: PetView!
    var fetcher: UsageFetcher!
    var animTimer: Timer?
    var fetchTimer: Timer?
    let watcher = ActivityWatcher()
    var activityTimer: Timer?
    private var activityPolling = false
    private var anchorTopY: CGFloat?       // top edge position the user chose for the window
    private var anchorLeftX: CGFloat?      // left edge position the user chose for the window
    private var hoverSince: Date?          // when the hover started; linger long enough and we count it as you having seen it
    private var seenWhileHovering = Set<String>()
    private var glassAny: NSView?          // NSGlassEffectView (macOS 26+)
    private var fallbackEffectView: NSVisualEffectView?
    private var statusItem: NSStatusItem?      // menu bar entry point: the way back when the window can't be found
    private var statusSpin: CGFloat = 0        // rotation angle of the little sun in the status bar
    private var statusTimer: Timer?

    static let expandedRadius: CGFloat = 26   // corner radius when expanded; when collapsed the radius turns it into a circle

    @available(macOS 26.0, *)
    private var glassView: NSGlassEffectView? {
        get { glassAny as? NSGlassEffectView }
        set { glassAny = newValue }
    }
    private var adjustingHeight = false    // don't treat a programmatic height change as a user drag

    static let winW: CGFloat = 198
    static let winH: CGFloat = 182   // initial (loading) height; after that it adapts to the content
    static let posKey = "PetWindowTopLeft"
    private var loginInProgress = false   // only read and written on the main thread, so concurrent logins can't overwrite each other

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        // Under .accessory there is no menu bar, but ⌘X/C/V/A need a main menu before they
        // can be routed to the first responder; without it you can't paste the authorisation
        // code into the login box. The target of these menu items must be left empty so they travel the responder chain.
        let mainMenu = NSMenu()
        mainMenu.addItem(NSMenuItem())
        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        mainMenu.addItem(editItem)
        NSApp.mainMenu = mainMenu

        window = PetWindow(contentRect: NSRect(x: 0, y: 0,
                                               width: Self.winW, height: Self.winH),
                           styleMask: [.borderless], backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        // No system shadow: the window is constantly stretching and reshaping, and the shadow
        // leaves behind a black rectangular outline.
        // Liquid Glass comes with its own edge highlight and sense of depth, so it floats fine without one.
        // No system shadow: the window is constantly stretching and reshaping, and the shadow
        // leaves behind a black rectangular outline;
        // the glass has its own edge highlight, which is enough to make it float
        window.hasShadow = false
        // Don't pin the appearance: follow the system light/dark switch, and let the text use the labelColor family so it flips automatically
        window.level = Self.abovePopups ? .statusBar : .floating
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        window.isMovableByWindowBackground = false

        petView = PetView(model: model)
        petView.frame = NSRect(x: 0, y: 0, width: Self.winW, height: Self.winH)
        petView.autoresizingMask = [.width, .height]
        petView.focusRingType = .none

        // The card itself = the system's Liquid Glass.
        //
        // About contentView: WWDC25 session 310 asks you to put the content inside
        // contentView and let AppKit handle legibility for you. Measured (A/B over the
        // same background): the moment contentView is set, AppKit adds a legibility
        // backing behind the dense text that fills the whole card, and the glass is
        // flattened into an almost opaque dark slab — nothing behind it comes through
        // at all, the refraction is gone, and with it any sense of liquid glass.
        //
        // So we stack sibling views instead and get the real glass look back; the price is
        // that legibility becomes our own problem, which is solved by semantic colours
        // (the labelColor family, which flips automatically with light/dark) plus measured contrast.
        if #available(macOS 26.0, *) {
            let root = NSView(frame: NSRect(x: 0, y: 0,
                                            width: Self.winW, height: Self.winH))
            root.autoresizingMask = [.width, .height]

            let glass = PetGlassView(
                frame: NSRect(x: 0, y: 0, width: Self.winW, height: Self.winH))
            glass.style = Self.clearGlass ? .clear : .regular
            glass.cornerRadius = Self.expandedRadius
            glass.autoresizingMask = [.width, .height]
            root.addSubview(glass)
            root.addSubview(petView, positioned: .above, relativeTo: glass)
            glassView = glass
            window.contentView = root
        } else {
            let ve = NSVisualEffectView(
                frame: NSRect(x: 0, y: 0, width: Self.winW, height: Self.winH))
            ve.material = .hudWindow
            ve.blendingMode = .behindWindow
            ve.state = .active
            ve.wantsLayer = true
            ve.layer?.cornerRadius = Self.expandedRadius
            ve.layer?.cornerCurve = .continuous
            ve.layer?.masksToBounds = true
            ve.autoresizingMask = [.width, .height]

            // petView must be a sibling of the blur view: as a subview, hiding the blur for
            // "Reduce transparency" would hide the whole interface with it and the app would simply disappear
            let root = NSView(frame: NSRect(x: 0, y: 0,
                                            width: Self.winW, height: Self.winH))
            root.autoresizingMask = [.width, .height]
            root.addSubview(ve)
            root.addSubview(petView, positioned: .above, relativeTo: ve)
            window.contentView = root
            fallbackEffectView = ve
        }

        applyStatusIcon()
        restorePosition()
        window.orderFrontRegardless()

        fetcher = UsageFetcher(model: model)
        fetcher.onUpdate = { [weak self] in
            self?.adjustWindowHeight()
            self?.petView.needsDisplay = true
        }

        applyAccessibilitySettings()
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification,
            object: nil, queue: .main) { [weak self] _ in
                self?.applyAccessibilitySettings()
            }
        // Stop and restart the animation when the displays sleep / wake; don't burn power for nothing on a black screen
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.screensDidSleepNotification, object: nil, queue: .main) {
                [weak self] _ in
                guard let self else { return }
                self.screensAsleep = true
                self.setAnimating(false, fps: 0)
                self.activityTimer?.invalidate()   // stop the disk polling too while the screen is off
                self.activityTimer = nil
            }
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main) {
                [weak self] _ in
                guard let self else { return }
                self.screensAsleep = false
                self.updateAnimationState()
                self.startActivityTimer()
            }

        updateAnimationState()

        let fetch = Timer(timeInterval: 15, repeats: true) { [weak self] _ in
            self?.fetcher.tick()
        }
        fetch.tolerance = 3            // let the system coalesce wake-ups, which saves power
        RunLoop.main.add(fetch, forMode: .common)
        fetchTimer = fetch
        fetcher.tick()

        startActivityTimer()
        pollActivity()

        petView.onRightClick = { [weak self] event in self?.showMenu(event) }
        petView.onDoubleClick = { [weak self] in
            guard let self else { return }
            if self.model.needsLogin { self.startLogin() } else { self.fetcher.forceRefresh() }
        }
        petView.onMarkRead = { [weak self] id in
            guard let self else { return }
            self.watcher.markRead(id)
            if let i = self.model.sessions.firstIndex(where: { $0.id == id }) {
                self.model.sessions[i].unread = false
            }
            self.adjustWindowHeight()
            self.petView.needsDisplay = true
        }
        // Adjust the window height every frame to follow the expansion progress, which gives a continuous stretch animation
        petView.onHoverProgress = { [weak self] in self?.adjustWindowHeight() }
        petView.onHoverChange = { [weak self] hovering in
            guard let self, self.model.hovered != hovering else { return }
            self.model.hovered = hovering
            self.hoverSince = hovering ? Date() : nil
            if !hovering { self.flushSeen() }   // when the mouse moves away, mark what you just looked at as read
            self.petView.needsDisplay = true
        }

        NotificationCenter.default.addObserver(self, selector: #selector(windowMoved),
                                               name: NSWindow.didMoveNotification,
                                               object: window)
    }

    /// Height when fully expanded: top row (sun + gauges) + session blocks + (on hover) detail rows
    private func expandedHeight() -> CGFloat {
        var h: CGFloat = 10 + PetView.topRowH + 2   // card top padding + top row
        if model.loading {
            h += 28
        } else if model.rows.isEmpty, model.errorMsg != nil,
                  (petView?.blocksHeight ?? 0) <= 0 {
            // Only when there aren't even any session blocks does the error message get the whole card to itself
            h += 56 + (model.needsLogin ? 36 : 0)
        } else {
            // Use the continuously varying height from the view; you can't just count the
            // blocks, because the count is discrete — the moment the last block disappears
            // the window drops 50pt in a single frame and all the easing is wasted
            h += petView?.resetLineHeight ?? 0
            h += petView?.blocksHeight ?? 0
            // The detail area's height is interpolated continuously against the expansion progress, so the window can stretch smoothly
            let p = petView?.hoverProgress ?? 0
            if p > 0.001 {
                let detailH = PetView.blockGap + 2 + 19
                    + CGFloat(min(model.rows.count, 5)) * 15 + 18
                h += detailH * p
            }
        }
        return h + 10                               // card bottom padding
    }

    /// The actual window size: interpolated by expansion progress between "just the sun" and "the full card"
    func desiredSize() -> NSSize {
        let e = petView?.expandProgress ?? 1
        let side = PetView.compactSide
        return NSSize(width: side + (Self.winW - side) * e,
                      height: side + (expandedHeight() - side) * e)
    }

    func adjustWindowHeight() {
        let size = desiredSize()
        let f = window.frame
        // The glass shape and tint must be updated **before** the guard: waiting for input
        // doesn't change the window size (the session is still busy, so blocks are neither
        // added nor removed), the guard would return straight away, and so "the glass lights
        // up while it waits for you to choose" wouldn't light up when it should; and once
        // some other size change had painted it on, it would equally fail to go back after
        // the wait ended, potentially hanging around until the unread entry expires (10 minutes).
        applyGlassShape(for: f.size)
        guard abs(f.height - size.height) > 0.25
            || abs(f.width - size.width) > 0.25 else { return }
        if anchorTopY == nil { anchorTopY = f.maxY }
        if anchorLeftX == nil { anchorLeftX = f.minX }
        var newY = anchorTopY! - size.height   // top edge anchored where the user put it, stretches downwards
        var newX = anchorLeftX!                // left edge anchored, expands to the right
        if let screen = window.screen ?? NSScreen.main {
            let vf = screen.visibleFrame
            newY = max(newY, vf.minY)                       // when up against the bottom, push upwards for the time being
            newX = min(newX, vf.maxX - size.width)          // when up against the right, give way to the left
            newX = max(newX, vf.minX)
        }
        adjustingHeight = true
        window.setFrame(NSRect(x: newX, y: newY,
                               width: size.width, height: size.height), display: true)
        adjustingHeight = false
        applyGlassShape(for: size)
        // setFrame won't send a make-up mouseExited on behalf of a stationary cursor: recalibrate the hover state by hand after the window shrinks
        if model.hovered, !window.frame.contains(NSEvent.mouseLocation) {
            model.hovered = false
            petView.needsDisplay = true
            adjustWindowHeight()
        }
    }

    /// The mouse resting on the pet for a full 1.2 seconds = you have seen these notifications;
    /// note them down first and only clear them once the mouse moves away, so that blocks don't vanish right under your nose
    private func noteSeenWhileHovering() {
        guard model.hovered, let since = hoverSince,
              Date().timeIntervalSince(since) >= 1.2 else { return }
        for s in model.sessions where s.unread { seenWhileHovering.insert(s.id) }
    }

    /// Once the mouse has left, clear all the unread items you just looked at in one go
    private func flushSeen() {
        guard !seenWhileHovering.isEmpty else { return }
        for id in seenWhileHovering {
            watcher.markRead(id)
            if let i = model.sessions.firstIndex(where: { $0.id == id }) {
                model.sessions[i].unread = false
            }
        }
        seenWhileHovering.removeAll()
        adjustWindowHeight()
        petView.needsDisplay = true
    }

    /// Expanded, it's a glass card with continuous corners; fully collapsed, **the whole slab of glass leaves the stage**
    /// and only a sun is left floating on the desktop.
    /// When it's idle there's no information to carry, so that backing is pure surplus.
    /// When a session is waiting for your input, give the glass a touch of warm colour so it "lights up" by itself
    private func applyGlassShape(for size: NSSize) {
        let e = petView?.expandProgress ?? 1
        let compactR = min(size.width, size.height) / 2
        let radius = compactR + (Self.expandedRadius - compactR) * e
        let waiting = model.sessions.contains { $0.waiting }
        // No tint normally, so the glass follows the system light/dark; tint it warm as a hint while waiting for input
        let tint: NSColor? = waiting ? NSColor.coralDeep.withAlphaComponent(0.24) : nil
        // The glass leaves the stage before the window does: if it only began to fade once the
        // window had already shrunk to sun size, you'd see an obvious round patch of colour
        // simply snap out of existence. Finish it before 0.45, in the same rhythm as the gauges fading out
        let glassAlpha = easeInOut(max(0, min(1, e / 0.45)))
        let noGlass = petView?.reduceTransparency ?? false   // with "Reduce transparency" the glass ought to step aside anyway

        if #available(macOS 26.0, *) {
            guard let g = glassView else { return }
            g.cornerRadius = radius
            if g.tintColor != tint { g.tintColor = tint }
            if abs(g.alphaValue - glassAlpha) > 0.002 { g.alphaValue = glassAlpha }
            // Belt and braces: in case NSGlassEffectView doesn't honour alphaValue (it's a new
            // control in macOS 26 and isn't guaranteed to respond the way an ordinary NSView
            // does), at least make sure it really is gone at the end
            let gone = glassAlpha < 0.01
            if g.isHidden != (gone || noGlass) { g.isHidden = gone || noGlass }
        } else if let ve = fallbackEffectView {
            ve.layer?.cornerRadius = radius
            if abs(ve.alphaValue - glassAlpha) > 0.002 { ve.alphaValue = glassAlpha }
            let gone = glassAlpha < 0.01
            if ve.isHidden != (gone || noGlass) { ve.isHidden = gone || noGlass }
        }
    }

    // MARK: Accessibility and power use

    private var animating = false

    /// Read the system accessibility preferences, pass them on to the view and stop the animation where needed
    func applyAccessibilitySettings() {
        let ws = NSWorkspace.shared
        petView.reduceMotion = ws.accessibilityDisplayShouldReduceMotion
        petView.increaseContrast = ws.accessibilityDisplayShouldIncreaseContrast
        let reduceTransparency = ws.accessibilityDisplayShouldReduceTransparency
        petView.reduceTransparency = reduceTransparency
        if #available(macOS 26.0, *) {
            // with "Reduce transparency" the glass must give way to an opaque background
            glassView?.isHidden = reduceTransparency
        }
        fallbackEffectView?.isHidden = reduceTransparency
        updateAnimationState()
        adjustWindowHeight()
        petView.needsDisplay = true
    }

    /// Animate the mascot whenever it's visible; full frame rate while interacting or busy, a lower frame rate for plain breathing and blinking to save power
    func updateAnimationState() {
        let visible = NSApp.occlusionState.contains(.visible) && !screensAsleep
        guard visible else { setAnimating(false, fps: 0); return }
        setAnimating(true, fps: petView.needsFullFrameRate ? 60 : 24)
    }

    private var animFPS: Double = 0
    private var screensAsleep = false

    func setAnimating(_ on: Bool, fps: Double) {
        guard on != animating || fps != animFPS else { return }
        animating = on
        animFPS = on ? fps : 0
        animTimer?.invalidate()
        animTimer = nil
        guard on, fps > 0 else { return }
        let dt = 1.0 / fps
        let anim = Timer(timeInterval: dt, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.petView.advance(CGFloat(dt))
            // Switch frame rate automatically when the state changes (a round of thinking starting or finishing, say)
            let want: Double = self.petView.needsFullFrameRate ? 60 : 24
            if want != self.animFPS { self.updateAnimationState() }
        }
        anim.tolerance = dt / 4
        RunLoop.main.add(anim, forMode: .common)
        animTimer = anim
    }

    private func startActivityTimer() {
        activityTimer?.invalidate()
        // When the window isn't visible, slow the polling to 5 seconds to cut down disk I/O
        let visible = NSApp.occlusionState.contains(.visible)
        let t = Timer(timeInterval: visible ? 0.8 : 5.0, repeats: true) { [weak self] _ in
            self?.pollActivity()
        }
        t.tolerance = visible ? 0.2 : 1.5
        RunLoop.main.add(t, forMode: .common)
        activityTimer = t
    }

    func applicationDidChangeOcclusionState(_ notification: Notification) {
        updateAnimationState()
        startActivityTimer()
    }

    private func pollActivity() {
        noteSeenWhileHovering()
        guard !activityPolling else { return }
        activityPolling = true
        DispatchQueue.global(qos: .utility).async { [weak self] in
            guard let self else { return }
            self.watcher.poll()
            let s = self.watcher.sessions
            DispatchQueue.main.async {
                self.activityPolling = false
                self.model.sessions = s
                self.adjustWindowHeight()   // a change in the number of session blocks changes the height
                self.petView.needsDisplay = true
            }
        }
    }

    @objc func windowMoved() {
        guard !adjustingHeight else { return }   // programmatic resizing doesn't count as a drag
        anchorTopY = window.frame.maxY
        anchorLeftX = window.frame.minX
        let f = window.frame
        // Store the top-left corner: the height varies with the content, so storing the bottom edge would make it creep upwards on every restart
        UserDefaults.standard.set([f.origin.x, f.maxY], forKey: Self.posKey)
    }

    func restorePosition() {
        var topLeft: NSPoint?
        if let arr = UserDefaults.standard.array(forKey: Self.posKey) as? [Double],
           arr.count == 2 {
            topLeft = NSPoint(x: arr[0], y: arr[1])
        } else if let arr = UserDefaults.standard.array(forKey: "PetWindowOrigin") as? [Double],
                  arr.count == 2 {
            topLeft = NSPoint(x: arr[0], y: arr[1] + 182)   // the old version stored the bottom-left corner; migrate it once
            UserDefaults.standard.removeObject(forKey: "PetWindowOrigin")
        }
        var origin: NSPoint?
        if let tl = topLeft {
            let candidate = NSRect(x: tl.x, y: tl.y - Self.winH,
                                   width: Self.winW, height: Self.winH)
            if NSScreen.screens.contains(where: { $0.visibleFrame.intersects(candidate) }) {
                origin = candidate.origin
                anchorTopY = tl.y
                anchorLeftX = tl.x
            }
        }
        if origin == nil, let screen = NSScreen.main {
            let vf = screen.visibleFrame
            origin = NSPoint(x: vf.maxX - Self.winW - 24, y: vf.minY + 60)
        }
        window.setFrameOrigin(origin ?? NSPoint(x: 100, y: 100))
        if anchorTopY == nil { anchorTopY = window.frame.maxY }
        if anchorLeftX == nil { anchorLeftX = window.frame.minX }
    }

    // MARK: Right-click menu

    /// Menu bar icon: with no Dock icon, this is the only reliable way in —
    /// whether the window got dragged onto a display that has since been unplugged, or the user forgot what that sun was, this brings it back.
    static var showStatusIcon: Bool {
        // On by default: it's the only fallback when the window can't be found, and nobody should be left with no way in the moment they install it
        get { UserDefaults.standard.object(forKey: "PetShowStatusIcon") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "PetShowStatusIcon") }
    }

    @objc func toggleStatusIcon() {
        Self.showStatusIcon.toggle()
        // Act on the next turn of the run loop: this item may well have been clicked from the
        // status bar's own menu, and pulling the host statusItem apart before the menu has finished closing is not safe
        DispatchQueue.main.async { [weak self] in self?.applyStatusIcon() }
    }

    /// Set up or tear down the menu bar icon. Stop the timer along with it when tearing down —
    /// leave it running and every 1/12 second it's still working out the rotation angle and still redrawing the icon, only with nobody able to see it
    private func applyStatusIcon() {
        if Self.showStatusIcon {
            if statusItem == nil { setUpStatusItem() }
        } else if let item = statusItem {
            statusTimer?.invalidate()
            statusTimer = nil
            NSStatusBar.system.removeStatusItem(item)
            statusItem = nil
            statusSpin = 0
        }
    }

    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = statusSunImage(spin: 0, asleep: true)
        item.button?.image?.accessibilityDescription = "Sundial"
        item.button?.toolTip = "Sundial"
        item.menu = buildMenu(forStatusItem: true)
        // The sun in the status bar spins along while a session is running; when idle it stays
        // put rather than burning power for nothing.
        // 12fps is plenty for an icon this small in the menu bar; any higher and you can't tell the difference
        let t = Timer(timeInterval: 1.0 / 12, repeats: true) { [weak self] _ in
            guard let self, let btn = self.statusItem?.button else { return }
            let busy = self.model.anyBusy && !self.model.asleep
            guard busy || self.statusSpin != 0 else { return }
            if busy {
                self.statusSpin += 0.9 / 12
                while self.statusSpin > .pi * 2 { self.statusSpin -= .pi * 2 }
            } else {
                self.statusSpin = 0          // straighten it up when it stops, so it isn't left frozen at an angle
            }
            btn.image = statusSunImage(spin: self.statusSpin, asleep: !busy)
            btn.image?.accessibilityDescription = "Sundial"
        }
        RunLoop.main.add(t, forMode: .common)
        t.tolerance = 0.02
        statusTimer = t
        item.menu?.delegate = self
        statusItem = item
    }

    /// Move the pet back into the visible area of the main screen
    @objc func recenterWindow() {
        guard let vf = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame else { return }
        let f = window.frame
        anchorLeftX = vf.maxX - f.width - 24
        window.setFrameOrigin(NSPoint(x: anchorLeftX!, y: vf.minY + 60))
        anchorTopY = window.frame.maxY   // use the real top edge after the move, so the next resize doesn't jump
        window.orderFrontRegardless()
        windowMoved()
    }

    /// The right-click menu and the menu bar menu share one set of items
    func buildMenu(forStatusItem: Bool) -> NSMenu {
        let menu = NSMenu()
        // Version number right at the top of the menu: when someone reports a problem, the first thing you can ask is "what does the menu say".
        // Taken from Info.plist, which build.sh in turn fills in from VERSION at the repo root — a single source of truth
        let ver = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString")
            as? String ?? "?"
        let title = NSMenuItem(title: "Sundial \(ver)", action: nil, keyEquivalent: "")
        title.isEnabled = false
        menu.addItem(title)
        menu.addItem(.separator())
        let loggedIn = fetcher?.hasToken ?? false
        menu.addItem(withTitle: loggedIn ? "Sign in to Claude account again…" : "Sign in to Claude account…",
                     action: #selector(startLogin), keyEquivalent: "")
        if loggedIn {
            menu.addItem(withTitle: "Sign out", action: #selector(signOut), keyEquivalent: "")
        }
        menu.addItem(.separator())
        menu.addItem(withTitle: "Refresh now", action: #selector(refreshNow), keyEquivalent: "")
        // The equivalent of hovering by another route: see the breakdown without holding the mouse over the window
        let det = NSMenuItem(title: "Keep usage breakdown open", action: #selector(toggleDetails),
                             keyEquivalent: "")
        det.state = model.detailsPinned ? .on : .off
        menu.addItem(det)
        menu.addItem(withTitle: "Open the web usage page", action: #selector(openWeb), keyEquivalent: "")
        if forStatusItem {
            menu.addItem(withTitle: "Bring the pet back to the centre", action: #selector(recenterWindow),
                         keyEquivalent: "")
        }
        menu.addItem(.separator())
        let cg = NSMenuItem(title: "Clearer glass", action: #selector(toggleClearGlass),
                            keyEquivalent: "")
        cg.state = Self.clearGlass ? .on : .off
        menu.addItem(cg)
        let top = NSMenuItem(title: "Keep above other windows", action: #selector(toggleAbovePopups),
                             keyEquivalent: "")
        top.state = Self.abovePopups ? .on : .off
        menu.addItem(top)
        let sb = NSMenuItem(title: "Show the menu bar icon", action: #selector(toggleStatusIcon),
                            keyEquivalent: "")
        sb.state = Self.showStatusIcon ? .on : .off
        menu.addItem(sb)
        let auto = NSMenuItem(title: "Launch at login", action: #selector(toggleAutostart),
                              keyEquivalent: "")
        auto.state = autostartEnabled ? .on : .off
        menu.addItem(auto)
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit Sundial", action: #selector(quit), keyEquivalent: "")
        for item in menu.items { item.target = self }
        return menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === statusItem?.menu else { return }
        // We can't swap out statusItem.menu here (it's the very one being opened) — rebuild the items in place
        menu.removeAllItems()
        for item in buildMenu(forStatusItem: true).items {
            item.menu?.removeItem(item)
            menu.addItem(item)
        }
    }

    @objc func toggleDetails() {
        model.detailsPinned.toggle()
        adjustWindowHeight()
        petView.needsDisplay = true
    }

    func showMenu(_ event: NSEvent) {
        NSMenu.popUpContextMenu(buildMenu(forStatusItem: false), with: event, for: petView)
    }

    /// Always-on-top level: .floating by default; raised to .statusBar (25) once enabled.
    /// Not .popUpMenu (101) — that would cover the menu bar, and would also cover this app's own modal login box (the modal level is only 8).
    /// Glass clarity: false = regular (frosted, legible over any background); true = clear (more
    /// transparent; Apple recommends it only over rich media backgrounds, and legibility drops when there's a lot of text)
    static var clearGlass: Bool {
        get { UserDefaults.standard.bool(forKey: "PetClearGlass") }
        set { UserDefaults.standard.set(newValue, forKey: "PetClearGlass") }
    }

    @objc func toggleClearGlass() {
        Self.clearGlass.toggle()
        if #available(macOS 26.0, *) {
            glassView?.style = Self.clearGlass ? .clear : .regular
        }
        petView.needsDisplay = true
    }

    static var abovePopups: Bool {
        get { UserDefaults.standard.bool(forKey: "PetAbovePopups") }
        set { UserDefaults.standard.set(newValue, forKey: "PetAbovePopups") }
    }

    @objc func toggleAbovePopups() {
        Self.abovePopups.toggle()
        window.level = Self.abovePopups ? .statusBar : .floating
        window.orderFrontRegardless()
    }

    @objc func refreshNow() { fetcher.forceRefresh() }

    @objc func openWeb() {
        NSWorkspace.shared.open(URL(string: "https://claude.ai/settings/usage")!)
    }

    @objc func quit() { NSApp.terminate(nil) }

    // MARK: Login

    @objc func signOut() {
        fetcher.signOutByUser()
        model.needsLogin = true
        model.rows = []
        model.tier = ""
        model.errorMsg = "Signed out\nDouble-click me to sign in again"
        model.asleep = true
        model.loading = false
        adjustWindowHeight()
        petView.needsDisplay = true
    }

    /// Reuse the same verifier for the whole run: if a new one were generated on every click of
    /// Log in, a code the user copied from the previous authorisation page (and browsers very
    /// readily keep old tabs around) would never match, which shows up as "login keeps failing".
    /// Only discard it and start afresh once login has succeeded.
    private var loginVerifier: String?

    @objc func startLogin() {
        guard !loginInProgress else { return }   // only read and written on the main thread
        loginInProgress = true
        let verifier = loginVerifier ?? OAuth.newVerifier()
        loginVerifier = verifier
        guard let url = OAuth.authorizeURL(verifier: verifier) else {
            loginInProgress = false
            return
        }
        NSWorkspace.shared.open(url)
        // Wait for the browser to come up before showing the input box, to avoid stealing focus
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) {
            self.promptForCode(verifier: verifier)
        }
    }

    private func promptForCode(verifier: String) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Connect your Claude account"
        alert.informativeText = """
        Your browser has opened Claude's authorisation page. Sign in there and approve.
        Then paste the code it gives you below (the whole address-bar URL works too).

        Note: if an older authorisation page is still open, use the one that has just
        opened — a code from the old page will not work.
        """
        alert.addButton(withTitle: "Finish signing in")
        alert.addButton(withTitle: "Cancel")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 330, height: 24))
        field.placeholderString = "Paste the authorisation code here"
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        var response: NSApplication.ModalResponse = .cancel
        withLoweredWindow { response = alert.runModal() }   // don't sit on top of our own login box
        guard response == .alertFirstButtonReturn else {
            loginInProgress = false
            return
        }

        let pasted = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !pasted.isEmpty else {
            loginInProgress = false
            return
        }

        model.loading = true
        model.errorMsg = nil
        adjustWindowHeight()
        petView.needsDisplay = true
        DispatchQueue.global(qos: .userInitiated).async {
            do {
                let token = try exchangeCode(pasted, verifier: verifier)
                let saved = TokenStore.save(token)
                DispatchQueue.main.async {
                    self.loginInProgress = false
                    self.loginVerifier = nil     // only swap in a new one once it has succeeded
                    self.fetcher.adoptToken(token)
                    self.model.needsLogin = false
                    self.model.errorMsg = nil
                    self.model.asleep = false
                    self.fetcher.forceRefresh()
                    if !saved { self.warn("Signed in, but the token could not be saved to the Keychain. You may have to sign in again next time.") }
                }
            } catch {
                let text = oauthErrorText(error)
                DispatchQueue.main.async {
                    self.loginInProgress = false
                    self.model.loading = false
                    if !self.fetcher.hasToken {   // don't flip a login that already succeeded back to logged-out
                        self.model.needsLogin = true
                        self.model.rows = []      // without clearing this, the login card and its button won't render
                        self.model.tier = ""
                        self.model.errorMsg = "Sign-in failed\nDouble-click me to retry"
                        self.model.asleep = true
                        self.adjustWindowHeight()
                    }
                    self.petView.needsDisplay = true
                    self.warn(text, title: "Sign-in failed")
                }
            }
        }
    }

    private func warn(_ text: String, title: String = "Notice") {
        let a = NSAlert()
        a.alertStyle = .warning
        a.messageText = title
        a.informativeText = text
        a.addButton(withTitle: "OK")
        NSApp.activate(ignoringOtherApps: true)
        withLoweredWindow { _ = a.runModal() }
    }

    /// Drop the pet back to the normal window level while a modal is up, otherwise, being always on top, it sits over the dialog
    func withLoweredWindow(_ body: () -> Void) {
        let saved = window.level
        window.level = .normal
        defer { window.level = saved }
        body()
    }

    // MARK: Launch at login

    // Launch at login: use SMAppService (the official API, macOS 13+),
    // a hand-written LaunchAgent plist points at the old path, breaks once the app is moved, and isn't managed by the system's Login Items either
    var autostartEnabled: Bool { SMAppService.mainApp.status == .enabled }

    @objc func toggleAutostart() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
                if SMAppService.mainApp.status == .requiresApproval {
                    warn("Allow Sundial to open at login under System Settings › General › Login Items.", title: "One thing to confirm")
                }
            }
        } catch {
            warn("Could not change the launch-at-login setting: \(error.localizedDescription)", title: "Setting failed")
        }
    }

}
