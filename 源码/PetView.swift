// Sundial — 桌面宠物，显示 Claude Code 用量与会话状态
// 本文件由 main.swift 拆分而来

import AppKit

// MARK: - 桌宠视图

final class PetView: NSView {
    let model: PetModel
    var onRightClick: ((NSEvent) -> Void)?
    var onDoubleClick: (() -> Void)?
    var onHoverChange: ((Bool) -> Void)?
    var onMarkRead: ((String) -> Void)?

    private var t: CGFloat = 0                 // 动画时钟
    private var blinkUntil: CGFloat = -1
    private var nextBlinkAt: CGFloat = 2
    private var spinPhase: CGFloat = 0         // 0–1 循环，保证首尾无缝
    private var sunSpin: CGFloat = 0
    private var ringShown: [CGFloat] = [0, 0]   // 两个圆环当前显示值（外/内），向目标缓动
    private var blockRects: [(String, NSRect)] = []  // 命中测试用
    private var loginButtonRect: NSRect = .zero
    /// 定时缓动。指数平滑（smoothStep）总是头快尾慢：收起时前 0.1 秒就走完大半，
    /// 剩下的一点点慢慢磨——看着就是「啪」地消失，而不是渐变。
    /// 改成固定时长的 S 形曲线，快慢分布均匀，收起才像收起。
    private struct Tween {
        private(set) var value: CGFloat = 0
        private var from: CGFloat = 0
        private var to: CGFloat = 0
        private var startedAt: CGFloat = -99
        /// 返回值：这一帧有没有变化（用来决定要不要通知窗口重新布局）
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
    var hoverProgress: CGFloat { hoverTween.value }      // 0–1，详情展开进度
    var expandProgress: CGFloat { expandTween.value }    // 0=只剩太阳，1=完整卡片
    var reduceMotion = false        // 系统「减弱动态效果」
    var reduceTransparency = false  // 系统「降低透明度」：自己画不透明底
    // 系统「提高对比度」：目前绘制里没有用到它。留着是因为 App 层每次读无障碍设置
    // 都会写进来，删掉要一并改那边；真要用的话，该在这里加粗描边、提高文字对比
    var increaseContrast = false


    /// 只有交互与过渡才需要满帧；单纯的呼吸眨眼用低帧率就够
    var needsFullFrameRate: Bool {
        if model.anyBusy { return true }                 // 转圈 / 光芒转动
        if mousePoint != nil { return true }             // 光芒引力
        if abs(hoverProgress - (model.hovered || model.detailsPinned ? 1 : 0)) > 0.001 { return true }
        if abs(expandProgress - expandTargetValue) > 0.001 { return true }
        return false
    }

    private var expandTargetValue: CGFloat {
        // 用 blocks 而不是 visibleSessions：块还在淡出时窗口不能先收，
        // 否则两段动画叠在一起，看起来仍然是「啪」一下
        (model.hovered || model.detailsPinned || !blocks.isEmpty
            || model.loading || (model.rows.isEmpty && model.errorMsg != nil)) ? 1 : 0
    }
    var onHoverProgress: (() -> Void)?               // 每帧回调，驱动窗口高度
    private var mousePoint: NSPoint?                 // 视图内鼠标位置（引力源）
    private var petCenter: NSPoint = .zero           // 上一帧太阳中心
    private var rayPull = [CGFloat](repeating: 0, count: PetView.rayCount)  // 各光芒伸长量
    private var bodyLean = NSPoint.zero              // 整只往鼠标方向偏一点
    private var eyeShift = NSPoint.zero              // 眼珠看向鼠标
    private var perk: CGFloat = 0                    // 0–1，被靠近时的「精神一振」
    /// 会话块的出现/消失进度。窗口高度必须用这个连续值算，不能直接数块数——
    /// 块数是离散的，最后一块一消失窗口会在一帧里掉 50pt，把所有缓动都吃掉。
    /// 正在淡出的块要留着自己的数据，不然没法继续画。
    private struct BlockAnim { var s: SessionActivity; var tw = Tween() }
    private var blocks: [BlockAnim] = []
    /// 会话块区域当前占的高度（连续变化）。
    /// 必须夹到 0：sum 很小的时候 sum*56-6 是负的，窗口会先缩过头再弹回来
    var blocksHeight: CGFloat {
        let sum = blocks.reduce(0) { $0 + $1.tw.value }
        return max(0, sum * (PetView.blockH + PetView.blockGap) - PetView.blockGap)
    }

    static let topRowH: CGFloat = 64
    static let blockH: CGFloat = 50        // 标题 + 状态 + 上下文进度条
    static let blockGap: CGFloat = 6
    static let maxBlocks = 4
    static let petScale: CGFloat = 0.44
    static let cardRadius: CGFloat = 26     // 与 AppDelegate.expandedRadius 一致
    static let compactSide: CGFloat = 88  // 收起时的窗口边长（只剩太阳）
    static let rayCount = 9               // 奇数根，转起来更自然
    static let rayMaxPull: CGFloat = 13   // 正对鼠标且贴近时的最大伸长（pt）
    static let gaugeMaxPull: CGFloat = 9.5 // 仪表满格时朝它那侧的最大伸长（pt）
    /// 两股力叠加后的封顶：收起时窗口只有 88pt 见方（半径 44），
    /// 光芒伸过头会被窗口边缘直接切掉
    static let rayPullCap: CGFloat = 18

    init(model: PetModel) {
        self.model = model
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    func advance(_ dt: CGFloat) {
        t += dt
        // 转圈：归一化相位，wrap 时首尾严丝合缝
        if model.anyBusy {
            spinPhase += dt * 0.55
            while spinPhase >= 1 { spinPhase -= 1 }
        }
        if model.anyBusy && !model.asleep {
            sunSpin += dt * 0.9
            while sunSpin > .pi * 2 { sunSpin -= .pi * 2 }
        }
        // 圆环数值缓动跟随。**按位置记，不按标签记**——右圈显示的是「最紧的那条周限额」，
        // 哪条最紧是会换人的（比如 Fable 被「全部模型」反超）。按标签记的话，换人时
        // 新标签没有历史值、要从 0 长起来，看着像用量突然清零了
        // （实测：216° 一帧掉到 54°，再花半秒爬回 259°）。
        // 按位置记就只是同一个环从旧值走到新值，符合直觉。
        let ringTargets = model.ringRows
        for (i, row) in [ringTargets.outer, ringTargets.inner].enumerated() {
            // 圆环最多画满一圈；超限的部分靠中间的数字（如 106%）说话
            let target = row.map { min(1, CGFloat($0.percent) / 100) } ?? 0
            let cur = ringShown[i]
            ringShown[i] = abs(cur - target) > 0.0005
                ? smoothStep(cur, toward: target, dt: dt, rate: 5) : target
        }
        updateMousePoint()
        // 光芒引力：朝鼠标的那几根被拉长，背对的缩回，离得越近越明显
        // 指针引力是跟手位移，Reduce Motion 时保持关闭；呼吸与转动不受影响
        let targets = reduceMotion ? [CGFloat](repeating: 0, count: PetView.rayCount)
            : rayPullTargets()
        for i in 0..<PetView.rayCount {
            rayPull[i] = smoothStep(rayPull[i], toward: targets[i], dt: dt, rate: 9)
        }
        // 整只偏移 + 眼神跟随 + 精神一振：和光芒同一个「场」，一起缓动
        let field = reduceMotion ? nil : mouseField()
        let asleep = isSunAsleep
        let leanSign: CGFloat = asleep ? -1 : 1        // 睡着时是躲，往反方向缩
        let leanMax: CGFloat = asleep ? 3.0 : 4.2
        let lean = field.map {
            NSPoint(x: $0.ux * leanMax * $0.proximity * leanSign,
                    y: $0.uy * leanMax * $0.proximity * leanSign)
        } ?? .zero
        // 有鼠标就看鼠标（比身体跟得更早也更满，离得还远就已经在看你了）；
        // 没人理它的时候，就时不时瞟一眼两侧的仪表盘
        let eye: NSPoint
        if asleep {
            eye = .zero
        } else if let f = field {
            eye = NSPoint(x: f.ux * 1.7 * min(1, f.proximity * 2.4),
                          y: f.uy * 1.7 * min(1, f.proximity * 2.4))
        } else {
            eye = .zero          // 没鼠标就正视前方，不再自己乱瞟
        }
        bodyLean = NSPoint(x: smoothStep(bodyLean.x, toward: lean.x, dt: dt, rate: 7),
                           y: smoothStep(bodyLean.y, toward: lean.y, dt: dt, rate: 7))
        eyeShift = NSPoint(x: smoothStep(eyeShift.x, toward: eye.x, dt: dt, rate: 12),
                           y: smoothStep(eyeShift.y, toward: eye.y, dt: dt, rate: 12))
        perk = smoothStep(perk, toward: (asleep || field == nil) ? 0 : field!.proximity,
                          dt: dt, rate: 8)
        // 会话块的出现/消失：还在的**按 visible 的顺序重排**，走掉的插回原位淡出。
        // 之前是「按旧顺序遍历、新块一律 append」，于是 Activity 精心排的
        // 「等你选的最前 → 在跑的 → 未读的」只在 blocks 从空建立那一次生效；
        // 之后某个会话抛出选项，它仍画在原来的格子里，5 个会话时甚至排到最后一格。
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
        // 已经不在 visible 里的插回它原来的相对位置，就地淡出，不要突然跳位
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

        // 悬停详情 + 收起/展开：定时缓动，窗口尺寸与内容透明度同步跟随。
        // 收起给的时间比展开长——「出现」可以利落，「消失」慢一点才不像被抹掉。
        // Reduce Motion 时尺寸变化直接到位（逐帧缩放才是会引起不适的那部分）
        let hoverTarget: CGFloat = (model.hovered || model.detailsPinned) ? 1 : 0
        let expandTarget = expandTargetValue
        var changed = hoverTween.step(to: hoverTarget, now: t,
                                      dur: hoverTarget > hoverProgress ? 0.30 : 0.42,
                                      instant: reduceMotion)
        changed = expandTween.step(to: expandTarget, now: t,
                                   dur: expandTarget > expandProgress ? 0.40 : 0.62,
                                   instant: reduceMotion) || changed
        if changed || blocksChanged { onHoverProgress?() }
        // 眨眼。之前连同「瞟仪表」一起删过，但那两件事不一样：
        // 瞟仪表是眼珠周期性左右移动（看着像在闪），眨眼只是一次高度收缩，不抢眼
        if t >= nextBlinkAt {
            blinkUntil = t + 0.16
            nextBlinkAt = t + CGFloat.random(in: 2.4...6.0)
        }
        needsDisplay = true
    }

    /// 太阳是否在打盹：绘制与引力必须用同一个判断，否则角度会错开一个 sunSpin
    private var isSunAsleep: Bool { model.asleep || !model.anyBusy }


    /// 引力源用**全局**光标位置，而不是「鼠标进了窗口才算」。
    /// 这样光标还在窗口外靠近时，太阳就已经有反应了——「引力」本来就该是隔空的。
    /// 超出作用半径就置空，免得一直按满帧重绘。
    private func updateMousePoint() {
        guard let win = window else { mousePoint = nil; return }
        let p = convert(win.convertPoint(fromScreen: NSEvent.mouseLocation), from: nil)
        guard petCenter != .zero else { mousePoint = p; return }
        let dx = p.x - petCenter.x, dy = p.y - petCenter.y
        mousePoint = dx * dx + dy * dy <= 230 * 230 ? p : nil
    }

    /// 鼠标相对太阳的方向与近度。光芒、身体偏移、眼神跟随都取自同一个场，
    /// 否则各算各的，切换状态时会出现互相错位
    private func mouseField() -> (ux: CGFloat, uy: CGFloat, proximity: CGFloat)? {
        guard let m = mousePoint, petCenter != .zero else { return nil }
        let dx = m.x - petCenter.x, dy = m.y - petCenter.y
        let dist = sqrt(dx * dx + dy * dy)
        guard dist > 0.001 else { return nil }
        // 近度：贴着身体最强，约 150pt 外基本消失
        let proximity = 1 / (1 + pow(max(0, dist - 26) / 62, 2))
        guard proximity > 0.02 else { return nil }
        return (dx / dist, dy / dist, proximity)
    }

    /// 第 i 根光芒的朝向，必须和 drawPet 里的算法完全一致，否则受力方向会错位
    private func rayAngle(_ i: Int, asleep: Bool) -> CGFloat {
        CGFloat(i) / CGFloat(PetView.rayCount) * 2 * .pi + .pi / 8 + (asleep ? 0 : sunSpin)
    }

    private func wrapPi(_ a: CGFloat) -> CGFloat {
        var d = a
        while d > .pi { d -= 2 * .pi }
        while d < -.pi { d += 2 * .pi }
        return d
    }

    /// 每根光芒的目标伸长量，两股力叠加：
    ///  ① 鼠标——醒着被吸过去，打盹时反过来躲开
    ///  ② 两侧的仪表盘——用得越满，朝那一侧的光芒被拽得越长
    private func rayPullTargets() -> [CGFloat] {
        var out = [CGFloat](repeating: 0, count: PetView.rayCount)
        let asleep = isSunAsleep

        if let f = mouseField() {
            let mAngle = atan2(f.uy, f.ux)
            let sign: CGFloat = asleep ? -1 : 1        // 睡着时反向：躲开鼠标
            let maxPull: CGFloat = asleep ? 6 : PetView.rayMaxPull
            // 背对的一侧反向变化。醒着时只是一点点缀；睡着时「躲」要看得出来——
            // 近的一侧缩回去的同时，远的一侧要明显探出去，才像整个身子被推开。
            // 原来这个系数是 0.28，远侧只长了不到两个点，肉眼根本看不出来
            let recoilK: CGFloat = asleep ? 1.05 : 0.28
            for i in 0..<PetView.rayCount {
                let delta = wrapPi(rayAngle(i, asleep: asleep) - mAngle)
                // cos 归一到 0–1 后取幂。指数从 2.2 降到 1.4：光芒减到 9 根后，
                // 太尖的衰减只有一根够得着，看不出「一片被拉过去」的感觉
                let alignment = pow(max(0, cos(delta)), 1.4)
                let recoil = -recoilK * pow(max(0, -cos(delta)), 1.8)
                out[i] += maxPull * f.proximity * (alignment + recoil) * sign
            }
        }

        // 仪表盘的拉扯：左仪表在正左（π），右仪表在正右（0）。
        // 从 50%（警戒线）起才开始拽，满格时最强——于是「哪边紧」直接长在造型上，
        // 不用等你去读数字。光芒转动时被拽的那几根不断换人，整圈像被扯成了椭圆。
        let rings = model.ringRows
        for (dirAngle, row) in [(CGFloat.pi, rings.outer), (CGFloat(0), rings.inner)] {
            guard let row else { continue }
            let pct = CGFloat(row.percent)
            // 幅度要有下限。原来是从 50% 起的线性斜坡，60% 的圈只拿到满力的 20%，
            // 摆幅 3.5pt，等于没动。现在最小也有满力的四成，
            // 但仍随用量增长——「哪边紧」照样能从摆幅大小读出来
            let k = 0.4 + 0.6 * min(1, max(0, (pct - 15) / 75))
            let u = max(0, min(1, pct / 100))
            // 「呼吸」不是强弱起伏，是**一吸一斥**：正半周把这一侧的光芒拽出去，
            // 负半周又收回来，来回摆动才看得出来（只在 0.55–1.0 之间变强弱，
            // 方向始终向外，几乎看不出在动）。
            // 快慢直接跟用量走（不跟带下限的幅度走，否则两边喘得一样快）：
            // 空闲时约 7 秒一轮，满格约 3 秒。
            // 两侧相位差半个周期，于是整圈光芒左右摇曳，而不是一起胀缩
            let rate = 0.9 + 1.1 * u
            let breath = 0.08 + 0.92 * sin(t * rate + (dirAngle == 0 ? .pi : 0))
            for i in 0..<PetView.rayCount {
                let delta = wrapPi(rayAngle(i, asleep: asleep) - dirAngle)
                out[i] += PetView.gaugeMaxPull * k * breath * pow(max(0, cos(delta)), 1.4)
            }
        }
        for i in 0..<PetView.rayCount { out[i] = min(out[i], PetView.rayPullCap) }
        return out
    }

    // MARK: 事件

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { onDoubleClick?(); return }
        // 点未读的会话块 = 标记已读，不触发拖动
        let p = convert(event.locationInWindow, from: nil)
        if model.needsLogin, loginButtonRect.contains(p) {
            onDoubleClick?()          // 与双击同一动作：开始登录
            return
        }
        for (id, rect) in blockRects where rect.contains(p) {
            if model.sessions.first(where: { $0.id == id })?.unread == true {
                onMarkRead?(id)
                return
            }
        }
        window?.performDrag(with: event)
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

    // MARK: 无障碍（整个界面是自绘的，必须手工搭出元素树）

    // 容器自身要可见，否则子元素会被挂到窗口上、label 也不会被读出
    override func isAccessibilityElement() -> Bool { true }
    override func accessibilityRole() -> NSAccessibility.Role? { .group }
    override func accessibilityLabel() -> String? { "Claude 用量与会话状态" }

    /// 可按下的无障碍元素：VoiceOver 按下时执行 action
    final class ActionElement: NSAccessibilityElement {
        var onPress: (() -> Void)?
        override func accessibilityPerformPress() -> Bool {
            guard let onPress else { return false }
            onPress()
            return true
        }
    }

    /// 必须由我们自己持有：AppKit 只弱引用 accessibilityParent，
    /// 现造现返的元素会立刻析构，辅助功能读到的全是失效元素（-25202）。
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
        // 收起状态下仪表没画出来，就不要报给辅助功能
        if expandProgress > 0.5 {
            for (row, name, cx) in [
                (rings.outer, "5 小时用量", card.minX + card.width * 0.17),
                (rings.inner, "每周用量", card.maxX - card.width * 0.17),
            ] {
                guard let row else { continue }
                var v = "已用 \(row.percent)%"
                if let d = row.resetAt { v += "，\(compactReset(d)) 后重置" }
                add("gauge:" + name, .levelIndicator, name, v,
                    accessibilityFrame(NSRect(x: cx - gaugeR, y: midY - gaugeR,
                                              width: gaugeR * 2, height: gaugeR * 2)))
            }
        }
        if model.needsLogin, loginButtonRect != .zero {
            add("login", .button, "登录 Claude 账号", nil,
                accessibilityFrame(loginButtonRect))
        }
        for (id, rect) in blockRects {
            guard let s = model.sessions.first(where: { $0.id == id }) else { continue }
            var v: String
            if s.waiting { v = "等待你选择" }
            else if s.background { v = "后台任务运行中" }
            else if s.busy { v = "正在思考" }
            else if s.stalled { v = "无响应，长时间没有新记录" }
            else { v = "已完成，未读" }
            if let since = s.since { v += "，已用时 \(elapsedText(since: since))" }
            if s.ctxLimit > 0, s.ctxTokens > 0 {
                let pct = min(100, max(0, Int(Double(s.ctxTokens) / Double(s.ctxLimit) * 100)))
                v += "，上下文已用 \(pct)%"
            }
            add("session:" + id, .button, s.title.isEmpty ? "Claude Code 会话" : s.title,
                v, accessibilityFrame(rect))
        }

        // 元素集合变了才重建（重建会把 VoiceOver 光标打回原点）；
        // 数值/位置变化就地更新，指针身份保持不变
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
            e.setAccessibilityFrame(d.frame)   // 窗口会被拖动，帧每次都要刷新
            if changed { NSAccessibility.post(element: e, notification: .valueChanged) }
        }
        return axKids
    }

    /// 视图坐标（翻转）→ 屏幕坐标
    private func accessibilityFrame(_ r: NSRect) -> NSRect {
        let inWindow = convert(r, to: nil)
        return window?.convertToScreen(inWindow) ?? inWindow
    }

    // MARK: 绘制

    /// 以指定不透明度执行一段绘制
    private func withAlpha(_ a: CGFloat, _ body: () -> Void) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { body(); return }
        ctx.saveGState()
        ctx.setAlpha(a)
        body()
        ctx.restoreGState()
    }

    override func draw(_ dirtyRect: NSRect) {
        blockRects.removeAll()
        // 玻璃已被隐藏，这里补一个不透明背板，保证可读。
        // 但完全收起时同样不画——闲着只剩一颗太阳，没有内容需要背板托底
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
        // 卡片底由 NSGlassEffectView 负责（真正的 Liquid Glass），这里只画内容
        let card = bounds
        let e = expandProgress

        let rowMidY = card.minY + 10 + PetView.topRowH / 2
        // 太阳始终居中，两个仪表分居左右
        let petY = card.midY + (rowMidY - card.midY) * e
        drawPet(center: NSPoint(x: card.midX, y: petY))

        // 仪表要比窗口先淡出：等窗口都快收窄到只剩太阳了它还在，
        // 就会被窗口边缘生生切掉，看着像「啪」地消失而不是渐隐。
        // 同时略微缩小，读起来是「收回去了」而不是「被裁掉了」
        let g = easeInOut(max(0, (e - 0.34) / 0.66))
        // 没有任何用量数据（未登录 / 无订阅）就不画那两个空圈，
        // 留两个空轨道在那儿只会让人以为是坏了
        if g > 0.004, !model.rows.isEmpty {
            withAlpha(g) { drawGauges(in: card, midY: rowMidY, scale: 0.84 + 0.16 * g) }
        }
        guard e > 0.01 else { return }   // 完全收起时只剩太阳

        var y = card.minY + 10 + PetView.topRowH + 2

        if model.loading {
            drawText("正在获取用量…", in: NSRect(x: card.minX, y: y + 6,
                                            width: card.width, height: 16),
                     font: .systemFont(ofSize: 11),
                     color: .secondaryLabelColor, align: .center)
            return
        }

        // 拿不到用量时**只有在没有会话可显示**的情况下才独占整张卡片。
        // 会话状态那半边读的是本地记录文件，跟登录和订阅都没关系——
        // 没有 Max/Pro 的人（授权页会直接拒绝）照样该看得到自己在跑什么、
        // 上下文用了多少。早先这里无条件 return，等于把唯一还能用的功能也关掉了。
        if model.rows.isEmpty, let msg = model.errorMsg, blocks.isEmpty {
            drawText(msg, in: NSRect(x: card.minX + 13, y: y + 4,
                                     width: card.width - 26, height: 46),
                     font: .systemFont(ofSize: 10.5),
                     color: .secondaryLabelColor,
                     align: .center, lineBreak: .byWordWrapping)
            if model.needsLogin {
                // 至少 28pt 高，符合 macOS 可点区域下限
                let btn = NSRect(x: card.midX - 60, y: y + 52, width: 120, height: 30)
                loginButtonRect = btn
                NSColor.coralDeep.setFill()
                NSBezierPath(roundedRect: btn, xRadius: 13, yRadius: 13).fill()
                drawText("双击登录", in: NSRect(x: btn.minX, y: btn.minY + 6,
                                            width: btn.width, height: 16),
                         font: .systemFont(ofSize: 11, weight: .semibold),
                         color: .white, align: .center)
            }
            return
        }

        // 正在运转的 + 已完成但未读的会话。
        // 每块占的高度按自己的出现进度收放，并裁进这个高度里——于是它是「卷起来」
        // 消失的，下面的块同步上滑，而不是整块凭空不见
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

        // 详情随 hoverProgress 淡入淡出，并轻微上滑，跟窗口高度同步
        if hoverProgress > 0.01 {
            NSGraphicsContext.saveGraphicsState()
            // 窗口给详情预留的高度是按 hoverProgress 插值的，而这里画的是全尺寸内容。
            // 不裁的话，展开的 0.30 秒和收起的 0.42 秒里，末两行加「x 分钟前更新」
            // 会露到窗口外面被硬切掉。裁进卡片实际范围，让它像被卷出来一样。
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

    // MARK: 吉祥物

    private func drawPet(center: NSPoint) {
        let s = PetView.petScale
        let cx0 = center.x, cy0 = center.y
        let stress = CGFloat(model.maxPercent) / 100.0
        // 没有会话在跑就打盹：灰扑扑、闭眼、飘 zzz
        let asleep = isSunAsleep
        let breathe = 1 + 0.022 * sin(t * (asleep ? 1.0 : 1.6))

        let light = asleep ? NSColor.sleepLight : NSColor.coralLight
        let deep = asleep ? NSColor.sleepDeep : NSColor.coralDeep
        // 身体随用量连续加深。原来是过了 75% 才突然变，等于只有两档；
        // 改成一路渐深，扫一眼颜色就知道大概用了多少，不用去读数字。
        // 取 1.5 次幂：用量低时几乎不变色，高位才明显压暗
        // 睡着时也保留用量信号。**只剩一颗太阳的时候，恰恰是没有别的东西可看的时候**——
        // 原来这里把颜色全关掉，等于在最需要它的场合什么都读不到（实测：10% 和 99%
        // 渲染出来一模一样）。所以睡着照样压暗，只是幅度收一点、目标色换成暖深灰，
        // 让它仍然像在睡觉而不是生病
        let tint = pow(max(0, min(1, stress)), 1.2) * (asleep ? 0.75 : 0.62)
        // 上半只加深四成、下半加满：身体本来就是上浅下深的渐变，脸长在偏上的位置。
        // 全身一起压暗的话，深色红底配深褐五官，对比度会掉到 2.5:1（图形下限是 3:1），
        // 表情就糊了。这样既保住了「整体变深」的观感，脸也还看得清
        let deepenTo: NSColor = asleep ? .sleepDeepen : .sunDeepen
        let bodyLight = light.blended(withFraction: tint * 0.4, of: deepenTo) ?? light
        let bodyDeep = deep.blended(withFraction: tint, of: deepenTo) ?? deep
        let grad = NSGradient(starting: bodyLight, ending: bodyDeep)

        petCenter = center   // 供下一帧的引力计算使用（必须是未偏移的中心，否则会自激）
        // 整只朝鼠标挪一点。放在 petCenter 赋值之后，偏移只影响画面不影响算力场
        let cx = cx0 + bodyLean.x, cy = cy0 + bodyLean.y

        // 朝哪一侧的光芒，就染上那个仪表的颜色，深浅跟着它的用量走。
        // 于是「太阳往左边被拽过去、而且左边那半是红的」＝ 左边那条限额快满了，
        // 光看太阳就够了，不用去读两个圈里的数字。
        // 染色只跟用量走、不跟呼吸走：颜色是状态，摆动是状态的表现，
        // 两者混在一起会闪得人眼花。
        // 每侧的固定发光色 + 按该侧用量决定的强度（见 Theme.swift）。
        // 角度衰减放宽到 0.5，是为了让**一整个半边**都染上，
        // 而不是只有正对着的那一两根变色——那样太细，扫一眼根本看不出来
        let rings = model.ringRows
        let tintSides: [(angle: CGFloat, color: NSColor, amount: CGFloat)] =
            [(CGFloat.pi, rings.outer), (CGFloat(0), rings.inner)]
                .compactMap { pair -> (angle: CGFloat, color: NSColor, amount: CGFloat)? in
                    guard let row = pair.1 else { return nil }
                    // pair.0 == .pi 是朝左那一侧
                    let glow = pair.0 > 1 ? NSColor.glowLeft : NSColor.glowRight
                    // 睡着时把发光色往睡眠灰里收——还认得出是金还是粉，但不刺眼
                    let c = asleep ? (glow.blended(withFraction: 0.25, of: .sleepDeep) ?? glow) : glow
                    // **发光强度跟着这一侧的用量走**：越满越亮。
                    // 这是空闲态唯一还能读出用量的通道——只剩一颗太阳时没有圈也没有数字，
                    // 而灰身体压暗那点差别在 88pt 见方里根本看不出来（实测 10% 和 99% 几乎一样）。
                    // 「越满越亮」也比「越满越暗」符合直觉，且不会重蹈深色淤青的覆辙。
                    let u = max(0, min(1, CGFloat(row.percent) / 100))
                    return (pair.0, c, pow(u, 0.75))
                }

        // 光芒：圆头短棒，思考时整圈缓慢转动；鼠标靠近时被「吸」得有长有短
        let rayCount = PetView.rayCount
        for i in 0..<rayCount {
            let angle = CGFloat(i) / CGFloat(rayCount) * 2 * .pi + .pi / 8
                + (asleep ? 0 : sunSpin)
            let wobble = asleep ? 0 : 2.2 * s * sin(t * 1.9 + CGFloat(i) * 1.3)
            let inner: CGFloat = 21 * s
            // 反向排斥时不能把光芒缩没了，留个最短长度
            let outer = max(inner + 4 * s, (49 * s + wobble) * breathe + rayPull[i])
            // 被拉长的那几根同时略微变粗，「伸手去够」比单纯变长更像有劲
            let w = 16.5 * s * (1 + 0.2 * max(0, rayPull[i]) / PetView.rayMaxPull)
            let ray = NSBezierPath(roundedRect: NSRect(x: inner, y: -w / 2,
                                                       width: outer - inner, height: w),
                                   xRadius: w / 2, yRadius: w / 2)
            ray.transform(using: AffineTransform(rotationByRadians: angle))
            ray.transform(using: AffineTransform(translationByX: cx, byY: cy))
            // 染色只上在**远端**，根部保持本色：颜色是从仪表盘那边「蹭」过来的，
            // 整根均匀上色反而看不出这层关系。伸得越长尖上越浓——
            // 于是呼吸把光芒推向仪表时尖端亮起来，收回来时又褪掉
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
                // 内侧三成保持本色再开始过渡，颜色才是「聚在尖上」而不是整根渐变；
                // 何况根部本来就被身体挡住了。
                // 角度直接取光芒朝向：实测 -angle 在 90°/270° 会把颜色画到根部
                g.draw(in: ray, angle: angle * 180 / .pi)
            }
        }

        // 身体：上浅下深的渐变，毛绒团子的体积感
        let r = 30 * s * breathe
        let bodyRect = NSRect(x: cx - r, y: cy - r, width: r * 2, height: r * 2)
        let body = NSBezierPath(ovalIn: bodyRect)
        grad?.draw(in: body, angle: -90)

        // 心情：0 = 轻松，1 = 快用满了。眉毛与嘴形跟着它走，
        // 光靠嘴角那点弧度在这个尺寸下根本看不出来
        let worry = max(0, min(1, (stress - 0.5) / 0.35))

        // 豆豆眼
        let eyeY = cy - 2 * s
        let blinkT = blinkUntil - t
        let lidClose = blinkT > 0 ? easeInOut(1 - abs(blinkT / 0.16 - 0.5) * 2) : 0
        NSColor.faceDark.setFill()
        NSColor.faceDark.setStroke()
        for dx in [-12.0 * s, 12.0 * s] {
            // 眼珠看向鼠标；闭眼时不偏，免得弧线歪掉
            let ex = cx + dx + (asleep ? 0 : eyeShift.x)
            let eyeY = eyeY + (asleep ? 0 : eyeShift.y)
            if asleep || lidClose > 0.75 {
                let p = NSBezierPath()
                p.move(to: NSPoint(x: ex - 3 * s, y: eyeY))
                p.curve(to: NSPoint(x: ex + 3 * s, y: eyeY),
                        controlPoint1: NSPoint(x: ex - 1.5 * s, y: eyeY + 2.4 * s),
                        controlPoint2: NSPoint(x: ex + 1.5 * s, y: eyeY + 2.4 * s))
                p.lineWidth = 1.6 * s
                p.lineCapStyle = .round
                p.stroke()
            } else {
                // 纯豆豆眼，不点高光：这个尺寸下那点白只有 0.6pt，
                // 不是高光而是一粒噪点，把干净的剪影搞脏了。
                // 眨眼是高度收缩，不是突然切换
                let h = 6 * s * (1 - lidClose)
                NSBezierPath(ovalIn: NSRect(x: ex - 2.4 * s, y: eyeY - h / 2,
                                            width: 4.8 * s, height: h)).fill()
            }
        }

        // 眉毛：只在开始紧张后才长出来，内高外低（「/ \」）＝担心的样子。
        // 这是三种心情里最一眼能认出来的差别
        if !asleep, worry > 0.02 {
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
                NSColor.faceDark.withAlphaComponent(worry).setStroke()
                b.stroke()
            }
            NSColor.faceDark.setStroke()
        }

        // 嘴：开心是咧开的大弧，紧张是抿平，用满了是明显的倒弧
        let mouth = NSBezierPath()
        let my = cy + 6.5 * s
        if asleep {
            let o = NSBezierPath(ovalIn: NSRect(x: cx - 2 * s, y: my - 0.5 * s,
                                                width: 4 * s, height: 5 * s))
            o.lineWidth = 1.4 * s
            o.stroke()
        } else if stress < 0.5 {
            // 控制点从 ±2.6 移到 ±4.8：靠得太近会把曲线拽成尖底的 V，
            // 往外挪才是圆润的 U。同时把深度收一点，配合变宽保持同样的开口
            let grin = 4.9 * s + 1.8 * s * perk
            mouth.move(to: NSPoint(x: cx - 6.4 * s, y: my - 1.2 * s))
            mouth.curve(to: NSPoint(x: cx + 6.4 * s, y: my - 1.2 * s),
                        controlPoint1: NSPoint(x: cx - 4.8 * s, y: my + grin),
                        controlPoint2: NSPoint(x: cx + 4.8 * s, y: my + grin))
        } else if stress < 0.8 {
            mouth.move(to: NSPoint(x: cx - 4.2 * s, y: my + 1.6 * s))
            mouth.line(to: NSPoint(x: cx + 4.2 * s, y: my + 1.6 * s))   // 抿成一条线
        } else {
            // 同理，倒弧的控制点也往外挪，免得下巴尖成一个角
            mouth.move(to: NSPoint(x: cx - 5.6 * s, y: my + 4.0 * s))
            mouth.curve(to: NSPoint(x: cx + 5.6 * s, y: my + 4.0 * s),
                        controlPoint1: NSPoint(x: cx - 4.2 * s, y: my - 0.6 * s),
                        controlPoint2: NSPoint(x: cx + 4.2 * s, y: my - 0.6 * s))
        }
        mouth.lineWidth = 1.7 * s
        mouth.lineCapStyle = .round
        mouth.stroke()

        if asleep {
            for i in 0..<3 {
                let phase = fmod(t * 0.42 + CGFloat(i) * 0.33, 1.0)
                let fade = easeInOut(phase < 0.5 ? phase * 2 : (1 - phase) * 2)
                let size = 9 + CGFloat(i) * 2
                let zx = cx + 26 * s + CGFloat(i) * 9 * s + phase * 6
                let zy = cy - 24 * s - phase * 18 - CGFloat(i) * 6 * s
                let rect = NSRect(x: zx, y: zy, width: 20, height: size + 6)
                let font = NSFont.systemFont(ofSize: size, weight: .bold)
                drawText("z", in: rect.offsetBy(dx: 1, dy: 1), font: font,
                         color: NSColor.faceDark.withAlphaComponent(fade * 0.55))
                drawText("z", in: rect, font: font,
                         color: NSColor.labelColor.withAlphaComponent(fade * 0.8))
            }
        }
    }

    /// 内环下方的小标签：全部模型显示「每周」，专属限额显示模型名
    private func weeklyShortName(_ row: UsageRow?) -> String {
        guard let l = row?.label else { return "每周" }
        if l.contains("全部模型") { return "每周" }
        return l.replacingOccurrences(of: "每周 · ", with: "")
    }

    // MARK: 两个并排仪表（已用比例）


    /// 圆环用该侧的固定强调色（见 Theme.swift 里为什么不再按用量换色）
    private func gaugeAccent(isLeft: Bool) -> NSColor {
        isLeft ? .ringLeft : .ringRight
    }

    private func drawGauges(in card: NSRect, midY: CGFloat, scale: CGFloat = 1) {
        let r: CGFloat = 21 * scale
        let lw: CGFloat = 5 * scale
        let rings = model.ringRows
        // 左仪表 — 太阳 — 右仪表，三等分居中
        let gauges: [(UsageRow?, String, CGFloat)] = [
            (rings.outer, "5小时", card.minX + card.width * 0.17),
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
                // 从正上方顺时针填充。本视图 isFlipped，画布上下翻转会把旋向也翻过来，
                // 所以「角度递增」在屏幕上才是顺时针（离屏渲染逐格核对过）
                drawArc(center: center, radius: r, lineWidth: lw,
                        from: -90, to: -90 + 360 * Double(shown),
                        color: gaugeAccent(isLeft: cx < card.midX), round: true)
            }
            // 11pt 的行高约 13pt，原来数字框从 midY-10 起、标签框从 midY+3 起，
            // 正好首尾相接，两行字贴在一起。整体上移并留出 2.6pt 间距
            drawText("\(row.percent)%",
                     in: NSRect(x: cx - 22, y: midY - 13, width: 44, height: 14),
                     font: .monospacedDigitSystemFont(ofSize: 11, weight: .semibold),
                     color: .labelColor, align: .center)
            drawText(name, in: NSRect(x: cx - 22, y: midY + 2.6, width: 44, height: 11),
                     font: .systemFont(ofSize: 9),
                     color: .secondaryLabelColor, align: .center)
        }
    }

    /// 统一的画弧：本视图翻转，clockwise:true + 角度递减 = 视觉顺时针
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

    // MARK: 会话块

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
            sub = e.isEmpty ? "等你选择" : "等你选择 · \(e)"
            subColor = .labelColor        // 醒目交给右侧呼吸圆点，文字保证可读
        } else if s.background {
            let e = elapsedText(since: s.since)
            sub = e.isEmpty ? "后台任务运行中" : "后台任务 · \(e)"
        } else if s.busy {
            let e = elapsedText(since: s.since)
            sub = e.isEmpty ? "正在思考" : "正在思考 · \(e)"
        } else if s.stalled {
            // 只是很久没有新记录了，不确定跑没跑完，别谎报「已完成」
            let e = elapsedText(since: s.finishedAt)
            sub = e.isEmpty ? "无响应" : "无响应 · 已 \(e) 无更新"
        } else {
            sub = "未读 · " + agoText(s.finishedAt)
        }
        drawText(sub, in: NSRect(x: box.minX + 10, y: box.minY + 18,
                                 width: box.width - 40, height: 13),
                 font: .systemFont(ofSize: 9, weight: s.waiting ? .semibold : .regular),
                 color: subColor)

        // 上下文占用：一行文字 + 一条细进度条
        if s.ctxLimit > 0, s.ctxTokens > 0 {
            let frac = min(1, CGFloat(s.ctxTokens) / CGFloat(s.ctxLimit))
            let pct = Int((frac * 100).rounded())
            let barY = box.minY + PetView.blockH - 8
            let barX = box.minX + 10
            let barW = box.width - 20

            drawText("上下文 \(tokenText(s.ctxTokens)) / \(tokenText(s.ctxLimit))",
                     in: NSRect(x: barX, y: barY - 12, width: barW - 30, height: 11),
                     font: .systemFont(ofSize: 9.5), color: .labelColor)
            drawText("\(pct)%",
                     in: NSRect(x: barX + barW - 30, y: barY - 12, width: 30, height: 11),
                     font: .monospacedDigitSystemFont(ofSize: 9.5, weight: .medium),
                     color: .labelColor, align: .right)

            let track = NSBezierPath(roundedRect: NSRect(x: barX, y: barY, width: barW, height: 3),
                                     xRadius: 1.5, yRadius: 1.5)
            NSColor.labelColor.withAlphaComponent(0.14).setFill()
            track.fill()
            if frac > 0.004 {
                let fill = NSBezierPath(roundedRect: NSRect(x: barX, y: barY,
                                                            width: max(3, barW * frac), height: 3),
                                        xRadius: 1.5, yRadius: 1.5)
                // 上下文进度条并进珊瑚族，不再单独用一套绿/琥珀/红。
                // 过 60% 之后往深砖红压，仍然有「快满了」的提示，
                // 但用的是太阳身体加深那同一个色，不引入新色相
                let heat = max(0, min(1, (frac - 0.6) / 0.4))
                (NSColor.coralDeep.blended(withFraction: heat * 0.75, of: .sunDeepen)
                    ?? .coralDeep).setFill()
                fill.fill()
            }
        }

        let cx = box.maxX - 15
        let cy = box.minY + 15
        if s.waiting {
            // 等待输入：呼吸的实心圆点，比转圈更像「在等你」
            let pulse = 0.55 + 0.45 * (0.5 + 0.5 * sin(t * 3.4))
            // 等待输入也用珊瑚族：它和「在跑」的区别靠形状（实心呼吸点 vs 转圈），
            // 不必再多一个色相
            NSColor.coralDeep.withAlphaComponent(pulse).setFill()
            let rr: CGFloat = 5
            NSBezierPath(ovalIn: NSRect(x: cx - rr, y: cy - rr,
                                        width: rr * 2, height: rr * 2)).fill()
        } else if s.busy {
            drawSpinner(center: NSPoint(x: cx, y: cy), radius: 7)
        } else {
            // 未读圆点，缓慢呼吸；点一下即消
            let pulse = 0.55 + 0.45 * easeInOut((sin(t * 1.6) + 1) / 2)
            NSColor.coralLight.withAlphaComponent(pulse).setFill()
            NSBezierPath(ovalIn: NSRect(x: cx - 4, y: cy - 4, width: 8, height: 8)).fill()
        }
    }

    /// 首尾无缝的转圈：弧长在生长与收缩之间循环，相位归一化，wrap 处完全连续
    private func drawSpinner(center: NSPoint, radius: CGFloat) {
        drawArc(center: center, radius: radius, lineWidth: 2.2,
                from: 0, to: 360, color: NSColor.labelColor.withAlphaComponent(0.14))

        // 尾角每周期正好走满 360°，弧长按余弦在 26°–290° 之间振荡（首尾导数为 0），
        // 因此 phase 回绕处角度与弧长都完全连续，接得上。
        let p = Double(spinPhase)
        let sweep = 26 + 264 * (1 - cos(2 * .pi * p)) / 2
        let tail = -90 + p * 360        // 角度递增 = 屏幕上顺时针（本视图 isFlipped）
        drawArc(center: center, radius: radius, lineWidth: 2.2,
                from: tail, to: tail + sweep, color: .coralLight, round: true)
    }

    // MARK: 悬停详情

    private func drawDetails(from startY: CGFloat, in card: NSRect) {
        let innerX = card.minX + 13
        let innerW = card.width - 26
        var y = startY

        drawText("Claude 用量", in: NSRect(x: innerX, y: y, width: innerW * 0.6, height: 13),
                 font: .systemFont(ofSize: 9.5, weight: .semibold),
                 color: .labelColor)
        if !model.tier.isEmpty {
            drawText(model.tier, in: NSRect(x: innerX + innerW * 0.4, y: y,
                                            width: innerW * 0.6, height: 13),
                     font: .systemFont(ofSize: 9.5),
                     color: .secondaryLabelColor, align: .right)
        }
        y += 19

        // 圆点的颜色标的是**这一条对应哪个仪表**，不是用量高低——和圆环同一套规则。
        // 之前这里还按 50/80 三档换色，圆环却已经改成固定色了，两处规则打架：
        // 同一个 60%，圆环是杏粉、列表里却是琥珀，看着像两套系统。
        // 没上仪表的那几条（比如没被选中的周限额）给中性灰，一眼能看出「这条没画成圈」。
        let shownRows = model.ringRows
        if model.rows.isEmpty {
            drawText(model.needsLogin ? "未登录，只显示会话状态" : "暂时取不到用量",
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
            drawText(row.label,
                     in: NSRect(x: innerX + 11, y: y, width: innerW - 11 - 96, height: 14),
                     font: .systemFont(ofSize: 9.5),
                     color: .secondaryLabelColor)
            // 数字不再按用量换色：颜色已经不承担「多满」这个信息了，
            // 那是弧长和数字本身的事
            drawText("\(row.percent)%",
                     in: NSRect(x: innerX + innerW - 96, y: y, width: 40, height: 14),
                     font: .monospacedDigitSystemFont(ofSize: 9.5, weight: .medium),
                     color: .labelColor, align: .right)
            drawText(compactReset(row.resetAt),
                     in: NSRect(x: innerX + innerW - 54, y: y, width: 54, height: 14),
                     font: .systemFont(ofSize: 9.5),
                     color: .secondaryLabelColor, align: .right)
            y += 15
        }

        let footer: String
        // 上面已经写过「未登录，只显示会话状态」了，底部不再重复一遍
        if let msg = model.errorMsg, !model.rows.isEmpty {
            footer = "⚠︎ " + (msg.components(separatedBy: "\n").first ?? msg)
        } else if let last = model.lastFetch {
            let mins = Int(-last.timeIntervalSinceNow / 60)
            footer = mins <= 0 ? "刚刚更新" : "\(mins) 分钟前更新"
        } else {
            footer = ""
        }
        drawText(footer, in: NSRect(x: innerX, y: y + 3, width: innerW, height: 12),
                 font: .systemFont(ofSize: 9.5),
                 color: model.errorMsg == nil ? .tertiaryLabelColor : .secondaryLabelColor)
    }
}

