// Sundial — 桌面宠物，显示 Claude Code 用量与会话状态
// 本文件由 main.swift 拆分而来

import AppKit
import Foundation
import ServiceManagement

// MARK: - 窗口

/// 玻璃视图默认把命中测试拦在自己身上，导致拖动/双击/点击已读全部失效。
/// 转发进 contentView；落空时回退 super（空白处仍返回自己，performDrag 可用）。
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
    // 不接受 key：点击时不抢当前 App 的焦点，也就不会画出那圈焦点边框。
    // 拖动、双击、右键菜单都不依赖 key 状态；登录输入框是独立的 NSAlert 窗口，不受影响。
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

// MARK: - 应用

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
    private var anchorTopY: CGFloat?       // 用户选定的窗口顶边位置
    private var anchorLeftX: CGFloat?      // 用户选定的窗口左边位置
    private var hoverSince: Date?          // 悬停起点，停留够久就当你看过了
    private var seenWhileHovering = Set<String>()
    private var glassAny: NSView?          // NSGlassEffectView（macOS 26+）
    private var fallbackEffectView: NSVisualEffectView?
    private var statusItem: NSStatusItem?      // 菜单栏入口：窗口找不到时的退路
    private var statusSpin: CGFloat = 0        // 状态栏小太阳的转角
    private var statusTimer: Timer?

    static let expandedRadius: CGFloat = 26   // 展开态圆角；收起态用半径变成圆形

    @available(macOS 26.0, *)
    private var glassView: NSGlassEffectView? {
        get { glassAny as? NSGlassEffectView }
        set { glassAny = newValue }
    }
    private var adjustingHeight = false    // 程序化改高度时不当成用户拖动

    static let winW: CGFloat = 198
    static let winH: CGFloat = 182   // 初始（加载态）高度，之后随内容自适应
    static let posKey = "PetWindowTopLeft"
    private var loginInProgress = false   // 只在主线程读写，防止并发登录互相覆盖

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        // .accessory 下不显示菜单栏，但 ⌘X/C/V/A 需要主菜单才能路由到第一响应者，
        // 否则登录框里粘贴不了授权码。这里的菜单项 target 必须留空，走响应链。
        let mainMenu = NSMenu()
        mainMenu.addItem(NSMenuItem())
        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "复制", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        mainMenu.addItem(editItem)
        NSApp.mainMenu = mainMenu

        window = PetWindow(contentRect: NSRect(x: 0, y: 0,
                                               width: Self.winW, height: Self.winH),
                           styleMask: [.borderless], backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        // 不要系统投影：窗口一直在伸缩变形，投影会残留成一圈方框黑边。
        // Liquid Glass 自带边缘高光与层次，不靠它也浮得起来。
        // 不用系统投影：窗口一直在伸缩变形，投影会残留成方框黑边；
        // 玻璃自带边缘高光，足够浮起来
        window.hasShadow = false
        // 不锁外观：跟随系统明暗切换，文字用 labelColor 系列自动翻转
        window.level = Self.abovePopups ? .statusBar : .floating
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        window.isMovableByWindowBackground = false

        petView = PetView(model: model)
        petView.frame = NSRect(x: 0, y: 0, width: Self.winW, height: Self.winH)
        petView.autoresizingMask = [.width, .height]
        petView.focusRingType = .none

        // 卡片本体 = 系统的 Liquid Glass。
        //
        // 关于 contentView：WWDC25 session 310 要求把内容放进 contentView，由 AppKit
        // 代做可读性处理。实测（同一背景 A/B）：一旦设了 contentView，AppKit 会为
        // 铺满整块的密集文字加一层可读性背衬，玻璃被压成几乎不透明的深色板——
        // 背后内容完全透不过来，失去折射，也就没有液态玻璃的观感。
        //
        // 因此这里改用兄弟视图叠放，换回真正的玻璃质感；代价是可读性要自己保证，
        // 这一点已通过语义色（labelColor 系列，随明暗自动翻转）+ 实测对比度解决。
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

            // petView 必须和毛玻璃平级：若做成子视图，「降低透明度」隐藏毛玻璃时
            // 会把整个界面一起隐藏，App 直接消失
            let root = NSView(frame: NSRect(x: 0, y: 0,
                                            width: Self.winW, height: Self.winH))
            root.autoresizingMask = [.width, .height]
            root.addSubview(ve)
            root.addSubview(petView, positioned: .above, relativeTo: ve)
            window.contentView = root
            fallbackEffectView = ve
        }

        setUpStatusItem()
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
        // 显示器休眠 / 唤醒时停开动画，别在黑屏时白烧电
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.screensDidSleepNotification, object: nil, queue: .main) {
                [weak self] _ in
                guard let self else { return }
                self.screensAsleep = true
                self.setAnimating(false, fps: 0)
                self.activityTimer?.invalidate()   // 黑屏时磁盘轮询也停
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
        fetch.tolerance = 3            // 允许系统合并唤醒，省电
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
        // 每帧跟随展开进度调整窗口高度，做出连续的伸缩动画
        petView.onHoverProgress = { [weak self] in self?.adjustWindowHeight() }
        petView.onHoverChange = { [weak self] hovering in
            guard let self, self.model.hovered != hovering else { return }
            self.model.hovered = hovering
            self.hoverSince = hovering ? Date() : nil
            if !hovering { self.flushSeen() }   // 移开时把刚看过的清成已读
            self.petView.needsDisplay = true
        }

        NotificationCenter.default.addObserver(self, selector: #selector(windowMoved),
                                               name: NSWindow.didMoveNotification,
                                               object: window)
    }

    /// 完全展开时的高度：顶行（太阳+仪表）+ 会话块 +（悬停）详情行
    private func expandedHeight() -> CGFloat {
        var h: CGFloat = 10 + PetView.topRowH + 2   // 卡片顶部内边距 + 顶行
        if model.loading {
            h += 28
        } else if model.rows.isEmpty, model.errorMsg != nil,
                  (petView?.blocksHeight ?? 0) <= 0 {
            // 只有在连会话块都没有时，错误提示才独占整张卡片
            h += 56 + (model.needsLogin ? 36 : 0)
        } else {
            // 用视图里那个连续变化的高度，不能直接数块数：块数是离散的，
            // 最后一块一消失窗口会在一帧里掉 50pt，缓动全白做
            h += petView?.blocksHeight ?? 0
            // 详情区高度按展开进度连续插值，窗口才能平滑伸缩
            let p = petView?.hoverProgress ?? 0
            if p > 0.001 {
                let detailH = PetView.blockGap + 2 + 19
                    + CGFloat(min(model.rows.count, 5)) * 15 + 18
                h += detailH * p
            }
        }
        return h + 10                               // 卡片底部内边距
    }

    /// 实际窗口尺寸：在「只剩太阳」与「完整卡片」之间按展开进度插值
    func desiredSize() -> NSSize {
        let e = petView?.expandProgress ?? 1
        let side = PetView.compactSide
        return NSSize(width: side + (Self.winW - side) * e,
                      height: side + (expandedHeight() - side) * e)
    }

    func adjustWindowHeight() {
        let size = desiredSize()
        let f = window.frame
        // 玻璃的形状与染色必须**先于** guard 更新：等待输入不改变窗口尺寸
        // （会话还在 busy，块不增不减），guard 会直接 return，于是「等你选择时
        // 玻璃亮起来」该亮时不亮；而一旦被某次尺寸变化刷上了，等待解除后同样
        // 退不回去，最长能挂到未读过期（10 分钟）。
        applyGlassShape(for: f.size)
        guard abs(f.height - size.height) > 0.25
            || abs(f.width - size.width) > 0.25 else { return }
        if anchorTopY == nil { anchorTopY = f.maxY }
        if anchorLeftX == nil { anchorLeftX = f.minX }
        var newY = anchorTopY! - size.height   // 顶边锚定在用户选的位置，向下伸缩
        var newX = anchorLeftX!                // 左边锚定，向右展开
        if let screen = window.screen ?? NSScreen.main {
            let vf = screen.visibleFrame
            newY = max(newY, vf.minY)                       // 贴底时临时向上撑开
            newX = min(newX, vf.maxX - size.width)          // 贴右时向左让出
            newX = max(newX, vf.minX)
        }
        adjustingHeight = true
        window.setFrame(NSRect(x: newX, y: newY,
                               width: size.width, height: size.height), display: true)
        adjustingHeight = false
        applyGlassShape(for: size)
        // setFrame 不会替静止的光标补发 mouseExited：窗口收缩后手动校准悬停态
        if model.hovered, !window.frame.contains(NSEvent.mouseLocation) {
            model.hovered = false
            petView.needsDisplay = true
            adjustWindowHeight()
        }
    }

    /// 鼠标在桌宠上停够 1.2 秒 = 你看到了这些通知；先记下，等鼠标移开再清，
    /// 免得块在你眼皮底下消失
    private func noteSeenWhileHovering() {
        guard model.hovered, let since = hoverSince,
              Date().timeIntervalSince(since) >= 1.2 else { return }
        for s in model.sessions where s.unread { seenWhileHovering.insert(s.id) }
    }

    /// 鼠标离开后统一清掉刚才看过的未读
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

    /// 展开时是连续圆角的玻璃卡片；完全收起时**整块玻璃退场**，只剩一颗太阳浮在桌面上。
    /// 闲着的时候没有任何信息要承载，那块底就纯属多余。
    /// 有会话在等你输入时给玻璃一点暖色，让它自己「亮」起来
    private func applyGlassShape(for size: NSSize) {
        let e = petView?.expandProgress ?? 1
        let compactR = min(size.width, size.height) / 2
        let radius = compactR + (Self.expandedRadius - compactR) * e
        let waiting = model.sessions.contains { $0.waiting }
        // 常态不染色，让玻璃跟随系统明暗；等待输入时染暖色提示
        let tint: NSColor? = waiting ? NSColor.coralDeep.withAlphaComponent(0.24) : nil
        // 玻璃比窗口先退场：等窗口都收到只剩太阳大小了它才开始淡，
        // 就会看见一个明显的圆形色块「啪」地不见。0.45 之前走完，和仪表淡出同一节奏
        let glassAlpha = easeInOut(max(0, min(1, e / 0.45)))
        let noGlass = petView?.reduceTransparency ?? false   // 「降低透明度」时玻璃本就该让位

        if #available(macOS 26.0, *) {
            guard let g = glassView else { return }
            g.cornerRadius = radius
            if g.tintColor != tint { g.tintColor = tint }
            if abs(g.alphaValue - glassAlpha) > 0.002 { g.alphaValue = glassAlpha }
            // 兜底：万一 NSGlassEffectView 不吃 alphaValue（它是 macOS 26 的新控件，
            // 不保证像普通 NSView 那样响应），至少保证最后是真的没了
            let gone = glassAlpha < 0.01
            if g.isHidden != (gone || noGlass) { g.isHidden = gone || noGlass }
        } else if let ve = fallbackEffectView {
            ve.layer?.cornerRadius = radius
            if abs(ve.alphaValue - glassAlpha) > 0.002 { ve.alphaValue = glassAlpha }
            let gone = glassAlpha < 0.01
            if ve.isHidden != (gone || noGlass) { ve.isHidden = gone || noGlass }
        }
    }

    // MARK: 无障碍与能耗

    private var animating = false

    /// 读取系统无障碍偏好，同步给视图并按需停掉动画
    func applyAccessibilitySettings() {
        let ws = NSWorkspace.shared
        petView.reduceMotion = ws.accessibilityDisplayShouldReduceMotion
        petView.increaseContrast = ws.accessibilityDisplayShouldIncreaseContrast
        let reduceTransparency = ws.accessibilityDisplayShouldReduceTransparency
        petView.reduceTransparency = reduceTransparency
        if #available(macOS 26.0, *) {
            // 「降低透明度」时玻璃必须让位给不透明背景
            glassView?.isHidden = reduceTransparency
        }
        fallbackEffectView?.isHidden = reduceTransparency
        updateAnimationState()
        adjustWindowHeight()
        petView.needsDisplay = true
    }

    /// 可见就让吉祥物动起来；交互/忙碌时满帧，单纯呼吸眨眼用低帧率省电
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
            // 状态变化时自动切换帧率（比如开始/结束一轮思考）
            let want: Double = self.petView.needsFullFrameRate ? 60 : 24
            if want != self.animFPS { self.updateAnimationState() }
        }
        anim.tolerance = dt / 4
        RunLoop.main.add(anim, forMode: .common)
        animTimer = anim
    }

    private func startActivityTimer() {
        activityTimer?.invalidate()
        // 窗口不可见时把轮询放慢到 5 秒，减少磁盘 I/O
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
                self.adjustWindowHeight()   // 会话块数量变化会改高度
                self.petView.needsDisplay = true
            }
        }
    }

    @objc func windowMoved() {
        guard !adjustingHeight else { return }   // 程序化伸缩不算拖动
        anchorTopY = window.frame.maxY
        anchorLeftX = window.frame.minX
        let f = window.frame
        // 存左上角：高度随内容变化，存底边会导致每次重启上移
        UserDefaults.standard.set([f.origin.x, f.maxY], forKey: Self.posKey)
    }

    func restorePosition() {
        var topLeft: NSPoint?
        if let arr = UserDefaults.standard.array(forKey: Self.posKey) as? [Double],
           arr.count == 2 {
            topLeft = NSPoint(x: arr[0], y: arr[1])
        } else if let arr = UserDefaults.standard.array(forKey: "PetWindowOrigin") as? [Double],
                  arr.count == 2 {
            topLeft = NSPoint(x: arr[0], y: arr[1] + 182)   // 旧版存的是左下角，迁移一次
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

    // MARK: 右键菜单

    /// 菜单栏图标：没有 Dock 图标时，这是唯一稳定的入口——
    /// 窗口被拖到已拔掉的显示器、或用户忘了那只太阳是什么，都能从这里找回来。
    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = statusSunImage(spin: 0, asleep: true)
        item.button?.image?.accessibilityDescription = "Sundial"
        item.button?.toolTip = "Sundial"
        item.menu = buildMenu(forStatusItem: true)
        // 有会话在跑时状态栏的太阳跟着转；空闲就停在原地，别白烧电。
        // 12fps 对菜单栏这么小的图标足够，再高看不出差别
        let t = Timer(timeInterval: 1.0 / 12, repeats: true) { [weak self] _ in
            guard let self, let btn = self.statusItem?.button else { return }
            let busy = self.model.anyBusy && !self.model.asleep
            guard busy || self.statusSpin != 0 else { return }
            if busy {
                self.statusSpin += 0.9 / 12
                while self.statusSpin > .pi * 2 { self.statusSpin -= .pi * 2 }
            } else {
                self.statusSpin = 0          // 停下时回正，免得歪着不动
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

    /// 把桌宠移回主屏可见区域
    @objc func recenterWindow() {
        guard let vf = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame else { return }
        let f = window.frame
        anchorLeftX = vf.maxX - f.width - 24
        window.setFrameOrigin(NSPoint(x: anchorLeftX!, y: vf.minY + 60))
        anchorTopY = window.frame.maxY   // 用移动后的真实顶边，避免下次伸缩跳位
        window.orderFrontRegardless()
        windowMoved()
    }

    /// 右键菜单与菜单栏菜单共用一套条目
    func buildMenu(forStatusItem: Bool) -> NSMenu {
        let menu = NSMenu()
        let loggedIn = fetcher?.hasToken ?? false
        menu.addItem(withTitle: loggedIn ? "重新登录 Claude 账号…" : "登录 Claude 账号…",
                     action: #selector(startLogin), keyEquivalent: "")
        if loggedIn {
            menu.addItem(withTitle: "退出登录", action: #selector(signOut), keyEquivalent: "")
        }
        menu.addItem(.separator())
        menu.addItem(withTitle: "立即刷新", action: #selector(refreshNow), keyEquivalent: "")
        // 悬停之外的等价入口：不用鼠标停在窗口上也能看到明细
        let det = NSMenuItem(title: "显示用量明细", action: #selector(toggleDetails),
                             keyEquivalent: "")
        det.state = model.detailsPinned ? .on : .off
        menu.addItem(det)
        menu.addItem(withTitle: "打开网页版用量", action: #selector(openWeb), keyEquivalent: "")
        if forStatusItem {
            menu.addItem(withTitle: "把桌宠移回屏幕中央", action: #selector(recenterWindow),
                         keyEquivalent: "")
        }
        menu.addItem(.separator())
        let cg = NSMenuItem(title: "更通透的玻璃", action: #selector(toggleClearGlass),
                            keyEquivalent: "")
        cg.state = Self.clearGlass ? .on : .off
        menu.addItem(cg)
        let top = NSMenuItem(title: "始终置于其他窗口之上", action: #selector(toggleAbovePopups),
                             keyEquivalent: "")
        top.state = Self.abovePopups ? .on : .off
        menu.addItem(top)
        let auto = NSMenuItem(title: "登录时自动启动", action: #selector(toggleAutostart),
                              keyEquivalent: "")
        auto.state = autostartEnabled ? .on : .off
        menu.addItem(auto)
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出 Sundial", action: #selector(quit), keyEquivalent: "")
        for item in menu.items { item.target = self }
        return menu
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === statusItem?.menu else { return }
        // 不能在这里替换 statusItem.menu（正在打开的就是它）——原地重建条目
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

    /// 置顶层级：默认 .floating；开启后升到 .statusBar(25)。
    /// 不用 .popUpMenu(101)——那会盖住菜单栏，也会盖住本 App 自己的模态登录框（模态层级只有 8）。
    /// 玻璃通透度：false = regular（磨砂，任何背景都清晰）；true = clear（更透，
    /// Apple 建议仅用于富媒体背景之上，文字多时可读性会下降）
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

    // MARK: 登录

    @objc func signOut() {
        fetcher.signOutByUser()
        model.needsLogin = true
        model.rows = []
        model.tier = ""
        model.errorMsg = "已退出登录\n双击我重新登录"
        model.asleep = true
        model.loading = false
        adjustWindowHeight()
        petView.needsDisplay = true
    }

    /// 同一次运行内复用同一个 verifier：每次点登录都换新的话，
    /// 用户从上一个授权页（浏览器很容易留着旧标签）复制的码就永远对不上，
    /// 表现为「反复登录失败」。登录成功后才作废重来。
    private var loginVerifier: String?

    @objc func startLogin() {
        guard !loginInProgress else { return }   // 只在主线程读写
        loginInProgress = true
        let verifier = loginVerifier ?? OAuth.newVerifier()
        loginVerifier = verifier
        guard let url = OAuth.authorizeURL(verifier: verifier) else {
            loginInProgress = false
            return
        }
        NSWorkspace.shared.open(url)
        // 等浏览器起来再弹输入框，避免抢焦点
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) {
            self.promptForCode(verifier: verifier)
        }
    }

    private func promptForCode(verifier: String) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "连接 Claude 账号"
        alert.informativeText = """
        浏览器已打开 Claude 授权页面，请在浏览器里登录并点击授权。
        授权后把页面给出的授权码粘贴到下面（直接复制浏览器地址栏也行）。

        注意：如果浏览器里还留着以前的授权页，请用刚打开的这一页，
        旧页面上的码是无效的。
        """
        alert.addButton(withTitle: "完成登录")
        alert.addButton(withTitle: "取消")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 330, height: 24))
        field.placeholderString = "在此粘贴授权码"
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        var response: NSApplication.ModalResponse = .cancel
        withLoweredWindow { response = alert.runModal() }   // 别压在自己的登录框上
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
                    self.loginVerifier = nil     // 成功了才换新的
                    self.fetcher.adoptToken(token)
                    self.model.needsLogin = false
                    self.model.errorMsg = nil
                    self.model.asleep = false
                    self.fetcher.forceRefresh()
                    if !saved { self.warn("登录成功，但令牌没能存进钥匙串，下次启动可能需要重新登录。") }
                }
            } catch {
                let text = oauthErrorText(error)
                DispatchQueue.main.async {
                    self.loginInProgress = false
                    self.model.loading = false
                    if !self.fetcher.hasToken {   // 别把已经成功的登录改回未登录
                        self.model.needsLogin = true
                        self.model.rows = []      // 不清空则登录卡片和按钮不会渲染
                        self.model.tier = ""
                        self.model.errorMsg = "登录失败\n双击我重试"
                        self.model.asleep = true
                        self.adjustWindowHeight()
                    }
                    self.petView.needsDisplay = true
                    self.warn(text, title: "登录失败")
                }
            }
        }
    }

    private func warn(_ text: String, title: String = "提示") {
        let a = NSAlert()
        a.alertStyle = .warning
        a.messageText = title
        a.informativeText = text
        a.addButton(withTitle: "好")
        NSApp.activate(ignoringOtherApps: true)
        withLoweredWindow { _ = a.runModal() }
    }

    /// 弹模态框期间把桌宠降回普通层级，否则置顶的它会压在对话框上面
    func withLoweredWindow(_ body: () -> Void) {
        let saved = window.level
        window.level = .normal
        defer { window.level = saved }
        body()
    }

    // MARK: 开机自启

    // 开机自启：用 SMAppService（macOS 13+ 官方接口），
    // 手写 LaunchAgent plist 会指向旧路径、App 移动后失效，也不受系统「登录项」管理
    var autostartEnabled: Bool { SMAppService.mainApp.status == .enabled }

    @objc func toggleAutostart() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
                if SMAppService.mainApp.status == .requiresApproval {
                    warn("请在「系统设置 › 通用 › 登录项」里允许 Sundial 开机启动。", title: "需要你确认")
                }
            }
        } catch {
            warn("无法修改开机自启设置：\(error.localizedDescription)", title: "设置失败")
        }
    }

}
