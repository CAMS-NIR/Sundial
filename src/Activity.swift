// Sundial — 桌面宠物，显示 Claude Code 用量与会话状态
// 本文件由 main.swift 拆分而来

import AppKit
import Foundation

// MARK: - Claude Code 活动状态

/// 各模型的上下文窗口上限
func contextLimit(for model: String?) -> Int {
    // 只有确定是小窗口的机型才列进来，其余（含尚未出现的新型号）按 1M 估。
    // 估错的方向很重要：分母估小了，进度条会顶满并打印出「已用 992.9k / 200.0k」
    // 这种自相矛盾的数字；估大了只是少报一点，不会骗人。
    guard let m = model?.lowercased() else { return 1_000_000 }
    if m.contains("haiku") { return 200_000 }        // haiku 全系
    if m.contains("claude-3") { return 200_000 }     // 3.x 全系
    // 4 系只有 4.5 及更早是 200k；4.6/4.7/4.8 与 5 系都是 1M。
    // 本机记录实测：claude-opus-4-8 单次上下文到过 992,897 token，
    // 早先那版把 "-4-" 整个当成 200k，于是它常年顶着 100% 红条。
    if m.contains("opus-4-0") || m.contains("opus-4-1") || m.contains("opus-4-5")
        || m.contains("sonnet-4-0") || m.contains("sonnet-4-5")
        || m.hasPrefix("claude-opus-4-2") || m.hasPrefix("claude-sonnet-4-2") {
        return 200_000                               // 末两条是已退役的 claude-*-4-20250514
    }
    return 1_000_000
}

/// 紧凑 token 数：468243 → "468.2k"
func tokenText(_ n: Int) -> String {
    if n >= 1_000_000 { return String(format: "%.1fM", Double(n) / 1_000_000) }
    if n >= 1_000 { return String(format: "%.1fk", Double(n) / 1_000) }
    return "\(n)"
}

struct SessionActivity {
    let id: String            // sessionId
    var title: String
    var busy: Bool
    var waiting: Bool         // 抛出了选项，正等用户选
    var since: Date?          // 本轮开始时间，用于计时
    var unread: Bool          // 已结束但用户还没看
    var finishedAt: Date?
    var ctxTokens: Int = 0    // 上下文已用 token
    var ctxLimit: Int = 0     // 上下文上限，0 = 未知
    var background: Bool = false  // 主回合已结束，后台代理仍在跑
    var stalled: Bool = false     // 长时间没有新记录，状态未知（≠ 已跑完）
}

/// 数据来源：
///  1) ~/.claude/sessions/*.json —— 运行中的会话注册表（pid + sessionId + 标题）
///  2) ~/.claude/projects/<项目>/<sessionId>.jsonl —— 只读尾部，判断忙/闲与回合起点
/// 只看 type / stop_reason / timestamp / 标题字段，不读对话正文。
final class ActivityWatcher {
    private struct FState {
        var size: UInt64 = 0
        var mtime = Date.distantPast
        var customTitle = ""
        var aiTitle = ""
        var busy = false
        var pendingTool = false   // 正在等工具返回，允许更长静默
        var waiting = false       // 最后一条是 AskUserQuestion，等用户选
        var since: Date?
        var unread = false
        var finishedAt: Date?
        var ctxTokens = 0
        var ctxLimit = 0
        var background = false    // 主回合结束但后台代理在跑
        var stalled = false       // 超时没动静，只是失联，不代表跑完了
        var bgSince: Date?
        var bgProbedAt = Date.distantPast   // 上次扫描后台目录的时间
        var bgNewest: Date?                 // 上次扫到的最新写入时间
        var bgStaleHits = 0                 // 连续几次探到后台没动静
        var title: String { customTitle.isEmpty ? aiTitle : customTitle }
    }

    private let home = FileManager.default.homeDirectoryForCurrentUser
    private var sessionsDir: URL { home.appendingPathComponent(".claude/sessions") }
    private var projectsDir: URL { home.appendingPathComponent(".claude/projects") }
    private let tailBytes: UInt64 = 512 * 1024
    private let deepBytes: UInt64 = 8 * 1024 * 1024   // 冷启动时的深扫窗口
    // 后台记录多久没动就算停了。实测同一个后台代理的相邻写入间隔 p95≈37 秒、
    // p99≈136 秒，早先的 25 秒会在一次运行途中反复判「跑完了」，弹假提示、把计时清零。
    private let bgFresh: TimeInterval = 90
    private let unreadExpiry: TimeInterval = 600   // 未读最多挂 10 分钟
    private let staleAfter: TimeInterval = 300
    private let toolStaleAfter: TimeInterval = 900 // Bash 单次上限 600 秒，再留出重试余量

    private var states: [String: FState] = [:]
    private let lock = NSLock()
    private var readRequests = Set<String>()      // 主线程点「已读」放进来
    private(set) var sessions: [SessionActivity] = []

    private static let isoFrac: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private static let isoPlain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    private func parseTS(_ s: Any?) -> Date? {
        guard let str = s as? String else { return nil }
        return Self.isoFrac.date(from: str) ?? Self.isoPlain.date(from: str)
    }

    /// 主线程调用：把某个会话标记为已读
    func markRead(_ id: String) {
        lock.lock()
        readRequests.insert(id)
        lock.unlock()
    }

    /// 会话重新开跑时解除「已读」抑制，否则它下次跑完永远不再提示
    private func clearRead(_ id: String) {
        lock.lock()
        readRequests.remove(id)
        lock.unlock()
    }

    // MARK: 注册表

    private struct LiveSession {
        let id: String
        let name: String
        let started: Date
    }

    private static let procStartFmt: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "EEE MMM d HH:mm:ss yyyy"   // 即 `LC_ALL=C TZ=UTC ps -o lstart=`
        return f
    }()

    /// pid 会被系统回收，而下面连 EPERM（别人的进程）都当作「还活着」。
    /// 注册表里记了 procStart，比对进程真实启动时刻才能确认是同一个进程，
    /// 否则一个陌生进程会把早就结束的会话复活成幽灵方块。
    private func isSameProcess(pid: Int32, procStart: Any?) -> Bool {
        guard let s = procStart as? String, !s.isEmpty else { return true }  // 老版本没这字段，放行
        let norm = s.split(separator: " ", omittingEmptySubsequences: true).joined(separator: " ")
        guard let want = Self.procStartFmt.date(from: norm) else { return true }  // 格式看不懂，别误杀
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        guard sysctl(&mib, 4, &info, &size, nil, 0) == 0,
              size >= MemoryLayout<kinfo_proc>.stride,
              info.kp_proc.p_starttime.tv_sec > 0 else { return false }
        let tv = info.kp_proc.p_starttime
        let got = Date(timeIntervalSince1970: Double(tv.tv_sec) + Double(tv.tv_usec) / 1e6)
        return abs(got.timeIntervalSince(want)) < 1.5   // ps 只精确到秒
    }

    private func liveSessions() -> [LiveSession] {
        let fm = FileManager.default
        guard let files = try? fm.contentsOfDirectory(at: sessionsDir,
                                                      includingPropertiesForKeys: nil)
        else { return [] }
        var out: [LiveSession] = []
        for f in files where f.pathExtension == "json" {
            guard let d = try? Data(contentsOf: f),
                  let o = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any],
                  let sid = o["sessionId"] as? String, !sid.isEmpty,
                  let pid = o["pid"] as? Int32 else { continue }
            // 进程还在吗？（陈旧文件不算活跃会话）
            guard kill(pid, 0) == 0 || errno == EPERM else { continue }
            guard isSameProcess(pid: pid, procStart: o["procStart"]) else { continue }
            let started = (o["startedAt"] as? Double).map {
                Date(timeIntervalSince1970: $0 / 1000)
            } ?? Date.distantPast
            out.append(LiveSession(id: sid, name: (o["name"] as? String) ?? "", started: started))
        }
        return out
    }

    /// sessionId -> 会话记录文件
    private func transcriptIndex() -> [String: (URL, Date, UInt64)] {
        let fm = FileManager.default
        var map: [String: (URL, Date, UInt64)] = [:]
        guard let dirs = try? fm.contentsOfDirectory(at: projectsDir,
                                                     includingPropertiesForKeys: nil)
        else { return map }
        for dir in dirs {
            guard let files = try? fm.contentsOfDirectory(
                at: dir, includingPropertiesForKeys: [.contentModificationDateKey, .fileSizeKey])
            else { continue }
            for f in files where f.pathExtension == "jsonl" {
                guard let v = try? f.resourceValues(forKeys: [.contentModificationDateKey,
                                                              .fileSizeKey]),
                      let m = v.contentModificationDate else { continue }
                map[f.deletingPathExtension().lastPathComponent] =
                    (f, m, UInt64(v.fileSize ?? 0))
            }
        }
        return map
    }

    /// 后台子代理/工作流的记录写在 <会话ID>/subagents/... 里，主记录不会动。
    /// 取该目录下最新的写入时间，用来判断「主回合结束但后台还在跑」。
    private func backgroundActivity(sessionID: String, transcript: URL,
                                    after cutoff: Date) -> Date? {
        let dir = transcript.deletingLastPathComponent()
            .appendingPathComponent(sessionID)
        let fm = FileManager.default
        guard fm.fileExists(atPath: dir.path),
              let e = fm.enumerator(at: dir,
                                    includingPropertiesForKeys: [.contentModificationDateKey],
                                    options: [.skipsHiddenFiles])
        else { return nil }
        // 不能设「看够 N 个就停」——枚举顺序不定，可能正好漏掉最新那个文件，
        // 于是把在跑的会话误判成空闲。
        var newest: Date?
        let now = Date()
        for case let url as URL in e {
            guard let m = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?
                .contentModificationDate,
                  // 主回合结束前写的（同步子代理、工具返回）已经由主记录代表了，
                  // 再算一遍会让刚跑完的会话被当成「后台还在跑」，压掉完成提示
                  m > cutoff else { continue }
            if newest == nil || m > newest! { newest = m }
            if now.timeIntervalSince(m) < 3 { return m }   // 明显新鲜才早退，否则扫完取真正最新
        }
        return newest
    }

    // MARK: 轮询

    func poll() {
        lock.lock()
        let reads = readRequests
        readRequests.removeAll()
        lock.unlock()

        let live = liveSessions()
        guard !live.isEmpty else {
            states.removeAll()
            sessions = []
            return
        }
        let index = transcriptIndex()

        var out: [SessionActivity] = []
        var newStates: [String: FState] = [:]
        for s in live {
            var st = states[s.id] ?? FState()
            if reads.contains(s.id) {
                st.unread = false
                st.finishedAt = nil
            }
            if let (url, mtime, size) = index[s.id] {
                if st.mtime != mtime || st.size != size {
                    let wasBusy = st.busy
                    // 首次看到这个会话时多读一些，确保能找到「用户上次说话」的锚点
                    let firstSight = st.mtime == .distantPast
                    parseTail(url, into: &st, window: firstSight ? deepBytes : tailBytes)
                    st.mtime = mtime
                    st.size = size
                    st.background = false   // 主记录动了，busy 已重判，旧后台标记作废
                    st.stalled = false      // 记录又动了，撤销「无响应」
                    // 忙 -> 闲：本轮出结果了，在用户看过之前一直标为未读
                    if wasBusy && !st.busy && !reads.contains(s.id) {
                        st.unread = true
                        // 用记录文件自己的写入时刻，不是「我发现它的时刻」。
                        // 否则回合在夜里结束、早上唤醒电脑，会显示成「刚刚完成」
                        st.finishedAt = mtime
                    }
                    if st.busy {
                        st.unread = false
                        st.finishedAt = nil
                        clearRead(s.id)   // 又开始跑了：下次结束要能重新提示
                    }
                }
                // 等用户选择时不设时限——人可能过很久才回来
                let limit = st.pendingTool ? toolStaleAfter : staleAfter
                if st.busy, !st.waiting, Date().timeIntervalSince(mtime) > limit {
                    st.busy = false
                    st.since = nil
                    st.pendingTool = false
                    // 超时只说明失联，不等于跑完了。以前这里静悄悄把方块抹掉、
                    // 太阳去睡觉，而 Claude 可能还在想——现在明说「无响应」
                    st.stalled = true
                    if !reads.contains(s.id) {
                        st.unread = true
                        st.finishedAt = mtime
                    }
                }
                // 主回合已结束，但后台子代理/工作流还在写记录 = 仍在干活
                // background 为真时手里的 busy 是上一轮自己设的，不能当作
                // 「主回合在忙」的判据，必须重新探测后台是否还活着
                if !st.busy || st.background {
                    // 目录遍历较贵，3 秒内复用上次结果（bgFresh 是 90 秒，误差可忽略）。
                    // 计数必须放在这道门**里面**：轮询是 0.8 秒一次，放外面的话
                    // 「连续两次探空」实际只隔了 1.6 秒，而两次真正的探测要相隔 3 秒——
                    // 等于门形同虚设，后台断断续续写入时会被提前判成跑完了
                    var probed = false
                    if Date().timeIntervalSince(st.bgProbedAt) >= 3 {
                        st.bgNewest = backgroundActivity(sessionID: s.id, transcript: url,
                                                         after: mtime)
                        st.bgProbedAt = Date()
                        probed = true
                    }
                    // 新鲜度按「探测那一刻」算：bgNewest 是缓存值，拿它跟当前时间比，
                    // 会凭空多出最多 3 秒，正好把在跑的后台任务判成停了
                    if let bg = st.bgNewest,
                       st.bgProbedAt.timeIntervalSince(bg) < bgFresh {
                        if st.bgSince == nil { st.bgSince = bg }
                        st.busy = true
                        st.background = true
                        st.stalled = false
                        st.since = st.bgSince
                        st.unread = false
                        st.finishedAt = nil
                        st.bgStaleHits = 0
                    } else {
                        // 一次探空不算完：后台写入本来就断断续续，连续两次才认
                        if probed { st.bgStaleHits += 1 }
                        if st.bgStaleHits >= 2 {
                            // 后台任务刚跑完（上一轮还是 background）：也算一次「出结果」
                            if st.background, !reads.contains(s.id) {
                                st.unread = true
                                st.finishedAt = st.bgNewest ?? Date()
                            }
                            // 必须一并清掉「无响应」。进入 background 之前几乎总是先被
                            // 超时判成失联（本机真实记录里后台段开始时主记录从来不是
                            // end_turn），不清的话方块会一直写「无响应 · 已 X 无更新」，
                            // 而不是「未读 · 刚刚完成」——实测 88 段后台运行有 10 段会踩到
                            st.stalled = false
                            st.bgSince = nil
                            st.background = false
                            st.busy = false   // 否则下一轮进不来这里，探测彻底停摆
                            st.since = nil
                        }
                    }
                } else {
                    st.background = false
                    st.bgSince = nil
                    st.bgStaleHits = 0
                }
            }
            // 挂太久的未读自动消掉，别一直杵在那儿
            if st.unread, let f = st.finishedAt,
               Date().timeIntervalSince(f) > unreadExpiry {
                st.unread = false
                st.finishedAt = nil
            }
            if st.title.isEmpty { st.customTitle = s.name }
            newStates[s.id] = st
            out.append(SessionActivity(id: s.id, title: st.title, busy: st.busy,
                                       waiting: st.waiting, since: st.since,
                                       unread: st.unread, finishedAt: st.finishedAt,
                                       ctxTokens: st.ctxTokens, ctxLimit: st.ctxLimit,
                                       background: st.background, stalled: st.stalled))
        }
        states = newStates
        // 等你选的排最前；其次在跑的；再次未读（新完成的在前）
        sessions = out.sorted { a, b in
            if a.waiting != b.waiting { return a.waiting }
            if a.busy != b.busy { return a.busy }
            if a.busy { return (a.since ?? .distantPast) < (b.since ?? .distantPast) }
            if a.unread != b.unread { return a.unread }
            return (a.finishedAt ?? .distantPast) > (b.finishedAt ?? .distantPast)
        }
    }

    // MARK: 解析尾部

    private func parseTail(_ url: URL, into st: inout FState, window: UInt64) {
        guard let fh = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? fh.close() }
        let end = (try? fh.seekToEnd()) ?? 0
        let newline = UInt8(ascii: "\n")
        var len = min(end, window)
        var data = Data()
        // 单条记录可能比窗口还大（工具返回上百 KB 很常见，本机见过 1.35MB）。
        // 窗口整个落在一条记录内部时一行都解析不出来，会被判成「已完成」，
        // 于是弹出假的未读提示并把计时清零。逐步扩窗，直到至少装得下一条完整记录。
        while true {
            try? fh.seek(toOffset: end - len)
            guard let d = try? fh.readToEnd(), !d.isEmpty else { return }
            data = d
            if len >= end || len >= deepBytes { break }       // 已到文件头 / 已到深扫上限
            if let i = d.firstIndex(of: newline),
               d[d.index(after: i)...].contains(newline) { break }  // 两个换行 = 至少一条完整记录
            len = min(end, len * 4)
        }

        var lastKind: (isAssistant: Bool, stop: String?)?
        var sawTurnEnd = false
        var lastAsked = false
        // 本回合起点：上一次 end_turn 之后的**第一次**用户动作。
        // 取第一次而不是最后一次——中途插话（steering）不该把计时清零。
        var turnStart: Date?
        // 被合成记录（API 报错占位）清掉的起点。回合若自动重试续上了，还原它，
        // 别让计时从 0 重来
        var resumeStart: Date?
        // 本回合窗口内最早的时间戳。连锚点都找不到时的兜底，总比 Date() 靠谱
        var turnFloor: Date?
        var notificationTimes: [Date] = []

        for line in data.split(separator: UInt8(ascii: "\n")) {
            guard line.count > 2,
                  let obj = (try? JSONSerialization.jsonObject(with: Data(line)))
                    as? [String: Any] else { continue }
            let type = obj["type"] as? String ?? ""

            switch type {
            case "custom-title":
                if let t = obj["customTitle"] as? String, !t.isEmpty { st.customTitle = t }
            case "ai-title":
                if let t = obj["aiTitle"] as? String, !t.isEmpty { st.aiTitle = t }
            case "queue-operation":
                // 用户在 Claude 忙碌时发的消息会先入队；这是「中途插话」的时间锚点
                if (obj["operation"] as? String) == "enqueue",
                   let ts = parseTS(obj["timestamp"]), turnStart == nil {
                    turnStart = ts
                }
            default:
                break
            }

            guard type == "assistant" || type == "user" else { continue }
            if obj["isMeta"] as? Bool == true { continue }
            if turnFloor == nil, let ts = parseTS(obj["timestamp"]) { turnFloor = ts }
            let msg = obj["message"] as? [String: Any]

            if type == "user" {
                let itext = (msg?["content"] as? String) ??
                    ((msg?["content"] as? [[String: Any]])?
                        .compactMap { ($0["type"] as? String) == "text" ? $0["text"] as? String : nil }
                        .joined() ?? "")
                // Esc 中断：本轮强制结束
                if itext.hasPrefix("[Request interrupted") {
                    lastKind = (true, "end_turn")
                    sawTurnEnd = true
                    // 和真正的 end_turn 一样要把起点作废。漏掉这句，中途插话留下的
                    // 旧时间戳会被下一轮当成起点，实测出现过「刚开始就已用 9 分 32 秒」
                    turnStart = nil
                    resumeStart = nil
                    turnFloor = nil
                    continue
                }
                // 后台任务完成通知不是「用户说话」，记下时间用于排除同刻的 enqueue
                if itext.hasPrefix("<task-notification"), let ts = parseTS(obj["timestamp"]) {
                    notificationTimes.append(ts)
                }
                let isToolResult = (msg?["content"] as? [[String: Any]])?
                    .contains { ($0["type"] as? String) == "tool_result" } ?? false
                if !isRealPrompt(msg), !isToolResult { continue }
                // 直接锚在用户这条记录上。以前靠 last-prompt 记录来定位，可它是在
                // 用户消息之后才写的，锚点总是落到后面那条工具返回上——实测 349 个
                // 回合有 348 个偏晚，中位数晚 112 秒，于是刚提交的问题显示成「0 秒」
                if isRealPrompt(msg), let ts = parseTS(obj["timestamp"]), turnStart == nil {
                    turnStart = ts
                    resumeStart = nil
                }
            }

            let stop = msg?["stop_reason"] as? String
            lastKind = (type == "assistant", stop)
            if type == "assistant" {
                // 最后一条若是抛选项的工具调用，说明在等用户选
                lastAsked = (msg?["content"] as? [[String: Any]])?.contains {
                    ($0["type"] as? String) == "tool_use"
                        && ($0["name"] as? String) == "AskUserQuestion"
                } ?? false
                // 上下文占用 = 这次请求真正送进模型的 token（不含输出）
                if let u = msg?["usage"] as? [String: Any] {
                    let n = (u["input_tokens"] as? Int ?? 0)
                        + (u["cache_read_input_tokens"] as? Int ?? 0)
                        + (u["cache_creation_input_tokens"] as? Int ?? 0)
                    if n > 0 {
                        st.ctxTokens = n
                        st.ctxLimit = contextLimit(for: msg?["model"] as? String)
                    }
                }
                if let s = stop, s == "end_turn" || s == "stop_sequence" {
                    sawTurnEnd = true
                    // 合成记录（model 为 "<synthetic>"，API 报错的占位）未必是真结束，
                    // Claude 常会自动重试接着跑。先把起点存起来，回合真续上了就还原，
                    // 免得计时从 0 重新数。只有真 end_turn 才彻底作废。
                    resumeStart = (msg?["model"] as? String) == "<synthetic>"
                        ? (turnStart ?? resumeStart) : nil
                    turnStart = nil      // 一轮结束，下一次用户动作才是新起点
                    turnFloor = nil
                }
            } else {
                lastAsked = false   // 有用户/工具结果跟上来，说明已经答过了
            }
        }

        // 与后台通知同刻（±5 秒）的入队不算用户说话
        if let a = turnStart,
           notificationTimes.contains(where: { abs($0.timeIntervalSince(a)) < 5 }) {
            turnStart = nil
        }

        var busy = true
        if lastKind == nil { busy = false }
        if let k = lastKind, k.isAssistant,
           let s = k.stop, s == "end_turn" || s == "stop_sequence" {
            busy = false
        }

        if busy {
            if let a = turnStart ?? resumeStart {
                // 尾部确证过回合边界，a 就是新一轮真起点；否则尾部可能从半途开始，
                // 只能取更早的那个，防止把起点往后推
                st.since = sawTurnEnd ? a : min(st.since ?? a, a)
            } else if st.busy, let old = st.since {
                st.since = old
            } else {
                // 没有锚点就退到本回合窗口内最早的时间戳；连它都没有就留空，
                // UI 只显示「正在思考」。宁可不报时长，也别从 0 秒重新编一个
                st.since = turnFloor
            }
        } else {
            st.since = nil
        }
        st.busy = busy
        st.waiting = busy && lastAsked
        st.pendingTool = busy && (lastKind?.isAssistant ?? false) && lastKind?.stop == "tool_use"
    }

    /// 本地命令（/model 等）与系统注入不算「用户提问」
    private func isRealPrompt(_ msg: [String: Any]?) -> Bool {
        guard let msg else { return false }
        var text: String?
        if let s = msg["content"] as? String {
            text = s
        } else if let arr = msg["content"] as? [[String: Any]] {
            let texts = arr.compactMap { item -> String? in
                (item["type"] as? String) == "text" ? item["text"] as? String : nil
            }
            if texts.isEmpty {
                // 纯图片提问算真实提问；纯 tool_result 不算
                return arr.contains { ($0["type"] as? String) == "image" }
            }
            text = texts.joined()
        }
        guard let t = text?.trimmingCharacters(in: .whitespacesAndNewlines),
              !t.isEmpty else { return false }
        for bad in ["<local-command", "<command-", "Caveat:", "<task-notification",
                    "<system-reminder"] where t.hasPrefix(bad) {
            return false
        }
        return true
    }
}

func elapsedText(since date: Date?) -> String {
    guard let date else { return "" }
    let secs = max(0, Int(Date().timeIntervalSince(date)))
    if secs < 60 { return "\(secs) 秒" }
    let m = secs / 60, s = secs % 60
    if m < 60 { return "\(m) 分 \(s) 秒" }
    return "\(m / 60) 小时 \(m % 60) 分"
}

func agoText(_ date: Date?) -> String {
    guard let date else { return "" }
    let secs = max(0, Int(Date().timeIntervalSince(date)))
    if secs < 60 { return "刚刚完成" }
    let m = secs / 60
    if m < 60 { return "\(m) 分钟前完成" }
    return "\(m / 60) 小时前完成"
}

