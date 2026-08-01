// Sundial — 桌面宠物，显示 Claude Code 用量与会话状态
// 本文件由 main.swift 拆分而来

import AppKit
import Foundation

// MARK: - 用量接口

func labelFor(key: String) -> (String, Int)? {
    let k = key.lowercased()
    if k.contains("five_hour") || k == "session" { return ("5 小时", 0) }
    if k == "seven_day" || k == "weekly" || k == "weekly_all" {
        return ("每周 · 全部模型", 1)
    }
    if k.contains("fable") { return ("每周 · Fable", 2) }
    if k.contains("mythos") { return ("每周 · Mythos", 2) }
    if k.contains("opus") { return ("每周 · Opus", 3) }
    if k.contains("sonnet") { return ("每周 · Sonnet", 4) }
    if k.contains("cowork") { return ("每周 · Cowork", 5) }
    if k.contains("routine") { return ("每周 · Routines", 6) }
    if k.contains("extra") || k.contains("overage") { return nil } // 额外付费用量，暂不展示
    if k.contains("seven_day") {
        let name = k.replacingOccurrences(of: "seven_day_", with: "").capitalized
        return ("每周 · \(name)", 7)
    }
    return nil
}

func parseResetDate(_ v: Any?) -> Date? {
    if let s = v as? String {
        let f1 = ISO8601DateFormatter()
        f1.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = f1.date(from: s) { return d }
        let f2 = ISO8601DateFormatter()
        f2.formatOptions = [.withInternetDateTime]
        if let d = f2.date(from: s) { return d }
    }
    if let n = v as? Double {
        // 兼容秒 / 毫秒时间戳
        return Date(timeIntervalSince1970: n > 4_000_000_000 ? n / 1000 : n)
    }
    return nil
}


/// 限额行右侧用的紧凑重置时间："4h32m" / "周四 14:00"
func compactReset(_ d: Date?) -> String {
    guard let d else { return "" }
    let secs = d.timeIntervalSinceNow
    if secs <= 0 { return "即将" }
    if secs < 24 * 3600 {
        let h = Int(secs) / 3600, m = (Int(secs) % 3600) / 60
        return h > 0 ? "\(h)h\(m)m" : "\(m)m"
    }
    let fmt = DateFormatter()
    fmt.locale = Locale(identifier: "zh_CN")
    fmt.dateFormat = "EEE HH:mm"
    return fmt.string(from: d)
}

func prettyTier(_ raw: String?) -> String {
    guard let t = raw?.lowercased(), !t.isEmpty else { return "" }
    let mult = ["20x", "5x", "2x"].first { t.contains($0) }
    if t.contains("max") { return mult != nil ? "Max (\(mult!))" : "Max" }
    if t.contains("pro") { return "Pro" }
    if t.contains("team") { return "Team" }
    if t.contains("enterprise") { return "Enterprise" }
    if t.contains("free") { return "Free" }
    return raw ?? ""
}

func parseUsage(_ data: Data) -> (rows: [UsageRow], tier: String)? {
    guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    else { return nil }

    var rows: [UsageRow] = []
    func consider(key: String, obj: [String: Any], activeFlag: Bool = false) {
        guard let mapped = labelFor(key: key) else { return }
        // 顶层对象用 utilization；limits 数组用 percent
        var util: Double?
        for f in ["utilization", "percent"] {
            if let u = obj[f] as? Double { util = u; break }
            if let u = obj[f] as? Int { util = Double(u); break }
        }
        guard let u = util else { return }
        let reset = parseResetDate(obj["resets_at"] ?? obj["resetsAt"])
        // 不夹到 100：超限时就该看见「106%」，夹住只会显示成正好用完，
        // 反而看不出已经超了。上界 999 只是防脏数据把版面撑破
        rows.append(UsageRow(label: mapped.0,
                             percent: Int(max(0, min(999, u)).rounded()),
                             resetAt: reset,
                             priority: mapped.1))
    }
    // 顶层键按名字排序后再遍历：Swift 字典无序，撞名时若不定序，
    // 同一标签取到哪一条会随机变化
    for k in root.keys.sorted() {
        if let d = root[k] as? [String: Any] { consider(key: k, obj: d) }
    }
    // limits 数组：用 kind + scope.model.display_name 拼出名字
    if let limits = root["limits"] as? [[String: Any]] {
        for item in limits {
            var key = (item["kind"] as? String) ?? (item["type"] as? String)
                ?? (item["name"] as? String) ?? ""
            if key == "weekly_scoped",
               let scope = item["scope"] as? [String: Any],
               let model = scope["model"] as? [String: Any],
               let name = model["display_name"] as? String, !name.isEmpty {
                key = "seven_day_" + name          // 交给 labelFor 归类
            }
            consider(key: key, obj: item,
                     activeFlag: (item["is_active"] as? Int ?? 0) == 1)
        }
    }
    // 去重（同名保留先出现的），按优先级排序
    var seen = Set<String>()
    rows = rows.filter { seen.insert($0.label).inserted }
        .sorted { $0.priority < $1.priority }
    if rows.count > 5 { rows = Array(rows.prefix(5)) }

    // 套餐名：原先只认死 rate_limit_tier 这一个键，而接口早就不返回它了，
    // 徽章其实一直是空的也没人发现。改成认几个常见写法，接口换名字还能接得住
    var rawTier: String?
    outer: for k in ["rate_limit_tier", "tier", "subscription_type",
                     "subscription", "plan", "plan_type"] {
        if let s = root[k] as? String, !s.isEmpty { rawTier = s; break }
        if let d = root[k] as? [String: Any] {          // 也接受包了一层的写法
            for f in ["display_name", "name", "type", "id"] {
                if let s = d[f] as? String, !s.isEmpty { rawTier = s; break outer }
            }
        }
    }
    return (rows, prettyTier(rawTier))
}

// MARK: - 取数调度

final class UsageFetcher {
    var onUpdate: (() -> Void)?
    private let model: PetModel
    private var nextFetchAt = Date.distantPast
    private var inFlight = false      // 同一时刻只允许一个请求
    private var forcePending = false  // 请求进行中收到的手动刷新，完成后立即补一次
    private let normalInterval: TimeInterval = 60

    // 令牌缓存：只在启动后读一次钥匙串，避免重复触发系统授权弹窗
    private let tokenLock = NSLock()
    private var cachedToken: StoredToken?
    private var didLoadToken = false
    private var keychainBlocked = false   // 上次读钥匙串失败，等用户手动刷新再试
    private var tokenEpoch = 0            // 每次退出登录 +1，作废在途的刷新
    // 只在后台 fetch 线程读写（inFlight 保证同一时刻只有一个 fetch）
    private var refreshStreak = 0

    private static let signedOutKey = "petUserSignedOut"
    private var userSignedOut: Bool {
        get { UserDefaults.standard.bool(forKey: Self.signedOutKey) }
        set { UserDefaults.standard.set(newValue, forKey: Self.signedOutKey) }
    }

    struct StaleSignOut: Error {}

    init(model: PetModel) { self.model = model }

    var tokenGeneration: Int {
        tokenLock.lock()
        defer { tokenLock.unlock() }
        return tokenEpoch
    }

    /// 登录成功后由主线程写入
    func adoptToken(_ t: StoredToken) {
        userSignedOut = false   // 登录成功才解除「已退出」
        tokenLock.lock()
        cachedToken = t
        didLoadToken = true
        keychainBlocked = false
        tokenLock.unlock()
    }

    /// 内部失效（令牌被服务端否定）：清掉自己的令牌，但仍允许回退到 CLI 凭证
    func signOut() {
        tokenLock.lock()
        tokenEpoch &+= 1
        cachedToken = nil
        didLoadToken = true
        tokenLock.unlock()
        TokenStore.clear()
    }

    /// 用户主动退出：额外记住「已退出」，不再自动回退到 CLI 凭证
    func signOutByUser() {
        userSignedOut = true
        signOut()
    }

    /// 唯一允许写令牌的入口：纪元不符（期间用户退出登录了）就整单丢弃
    @discardableResult
    private func commitToken(_ t: StoredToken, epoch: Int) -> Bool {
        tokenLock.lock()
        let ok = (tokenEpoch == epoch)
        if ok {
            cachedToken = t
            didLoadToken = true
        }
        tokenLock.unlock()
        guard ok else { return false }
        TokenStore.save(t)
        if tokenGeneration != epoch { signOut(); return false }  // 落盘期间又退了，撤销
        return true
    }

    /// 主线程（菜单）用：只看内存缓存，绝不触碰钥匙串，避免弹窗阻塞 UI
    var hasToken: Bool {
        tokenLock.lock()
        defer { tokenLock.unlock() }
        return cachedToken != nil
    }

    /// 后台线程用：首次会读钥匙串（可能弹窗），读取期间不持锁
    private func currentToken() -> StoredToken? {
        tokenLock.lock()
        let alreadyLoaded = didLoadToken
        let cached = cachedToken
        tokenLock.unlock()
        if alreadyLoaded { return cached }

        let outcome = TokenStore.load()
        tokenLock.lock()
        defer { tokenLock.unlock() }
        if !didLoadToken {
            switch outcome {
            case .ok(let t):
                cachedToken = t; didLoadToken = true; keychainBlocked = false
            case .none:
                cachedToken = nil; didLoadToken = true; keychainBlocked = false
            case .failed:
                // 不把失败当成结论，但也别让 60 秒轮询反复弹授权框
                cachedToken = nil; didLoadToken = true; keychainBlocked = true
            }
        }
        return cachedToken
    }

    private var isKeychainBlocked: Bool {
        tokenLock.lock()
        defer { tokenLock.unlock() }
        return keychainBlocked
    }

    // tick / forceRefresh / finish 只在主线程调用（定时器与 UI 事件）
    func tick() {
        guard !inFlight else { return }
        guard forcePending || Date() >= nextFetchAt else { return }
        forcePending = false
        inFlight = true
        nextFetchAt = Date().addingTimeInterval(normalInterval)
        DispatchQueue.global(qos: .utility).async { [weak self] in
            self?.fetch()
        }
    }

    /// 用户主动刷新：也是唯一重新尝试钥匙串读取的入口（自动轮询不重试，免得弹窗骚扰）
    func forceRefresh() {
        tokenLock.lock()
        if keychainBlocked {
            didLoadToken = false
            keychainBlocked = false
        }
        tokenLock.unlock()
        forcePending = true
        tick()
    }

    private func finish(retryAfter: TimeInterval) {
        inFlight = false
        nextFetchAt = Date().addingTimeInterval(retryAfter)
        if forcePending { tick() }
    }

    private func fail(_ msg: String, sleep: Bool, retryAfter: TimeInterval = 60) {
        DispatchQueue.main.async {
            self.model.errorMsg = msg
            self.model.asleep = sleep
            self.model.loading = false
            self.model.needsLogin = false  // 走到这里说明令牌已取到，属于可自动重试的失败
            self.finish(retryAfter: retryAfter)
            self.onUpdate?()
        }
    }

    /// 放弃本次取数（用户中途退出登录），但必须收尾，否则 inFlight 永远挂着
    private func abandon() {
        DispatchQueue.main.async {
            self.model.loading = false
            self.finish(retryAfter: 0)
            self.onUpdate?()
        }
    }

    /// 没有可用令牌：进入待登录状态，不再频繁重试
    private func needLogin(_ msg: String) {
        DispatchQueue.main.async {
            self.model.needsLogin = true
            self.model.rows = []
            self.model.errorMsg = msg
            self.model.asleep = true
            self.model.loading = false
            self.finish(retryAfter: 3600)
            self.onUpdate?()
        }
    }

    /// 取一个可用的 access token：优先桌宠自己登录的，其次已有的 Claude Code CLI 凭证
    private func resolveToken(epoch: Int) throws
        -> (token: String, tier: String?, isOwn: Bool, justRefreshed: Bool) {
        if var t = currentToken() {
            var refreshed = false
            if t.isExpiring, !t.refreshToken.isEmpty {
                t = try refreshToken(t)   // 失败时抛 OAuthError
                guard commitToken(t, epoch: epoch) else { throw StaleSignOut() }
                refreshed = true
            }
            return (t.accessToken, nil, true, refreshed)
        }
        if isKeychainBlocked { throw CredError.keychainDenied }
        if userSignedOut { throw CredError.notLoggedIn }  // 主动退出后不回退到 CLI 凭证
        let creds = try loadCredentials()
        return (creds.accessToken, creds.subscriptionType, false, false)
    }

    private func fetch() {
        let epoch = tokenGeneration
        let resolved: (token: String, tier: String?, isOwn: Bool, justRefreshed: Bool)
        do {
            resolved = try resolveToken(epoch: epoch)
        } catch is StaleSignOut {
            abandon()
            return
        } catch let e as OAuthError {
            // 只有服务端明确否定凭证才登出；网络/限流/5xx 一律保留令牌稍后重试
            guard e.isCredentialRejection else {
                fail("网络暂时不可用，稍后自动重试", sleep: true, retryAfter: 120)
                return
            }
            signOut()
            needLogin("登录已失效\n双击我重新登录")
            return
        } catch CredError.keychainDenied {
            needLogin("钥匙串读取被拒\n双击我重试或重新登录")
            return
        } catch {
            // CLI 凭证不存在／无 claudeAiOauth／已过期，都归结为「请登录」
            needLogin("未登录\n双击我登录 Claude 账号")
            return
        }

        var req = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
        req.httpMethod = "GET"
        req.timeoutInterval = 15
        req.setValue("Bearer \(resolved.token)", forHTTPHeaderField: "Authorization")
        req.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        req.setValue("application/json", forHTTPHeaderField: "Accept")

        let sem = DispatchSemaphore(value: 0)
        var resultData: Data?
        var status = 0
        var netError: Error?
        let task = URLSession.shared.dataTask(with: req) { data, resp, err in
            resultData = data
            status = (resp as? HTTPURLResponse)?.statusCode ?? 0
            netError = err
            sem.signal()
        }
        task.resume()
        // 只有 wait 成功返回后才能安全读取捕获变量（signal 提供内存序）
        if sem.wait(timeout: .now() + 20) == .timedOut {
            task.cancel()
            fail("请求超时，稍后自动重试", sleep: true, retryAfter: 90)
            return
        }

        if netError != nil {
            fail("网络不可用，稍后自动重试", sleep: true, retryAfter: 90)
            return
        }
        switch status {
        case 200:
            refreshStreak = 0
            guard let data = resultData, let parsed = parseUsage(data),
                  !parsed.rows.isEmpty else {
                fail("接口返回了看不懂的数据\n（Anthropic 可能改了格式）", sleep: true, retryAfter: 300)
                return
            }
            DispatchQueue.main.async {
                self.model.rows = parsed.rows
                // 三个分支都要赋值：漏掉最后一个，换账号后旧套餐名会一直挂在
                // 新账号的数字旁边
                if !parsed.tier.isEmpty {
                    self.model.tier = parsed.tier
                } else if let sub = resolved.tier {
                    self.model.tier = prettyTier(sub)
                } else {
                    self.model.tier = ""
                }
                self.model.lastFetch = Date()
                self.model.errorMsg = nil
                self.model.asleep = false
                self.model.loading = false
                self.model.needsLogin = false
                self.finish(retryAfter: self.normalInterval)
                self.onUpdate?()
            }
        case 401:
            // 令牌被拒：试一次刷新。本轮已经刷过就不再刷，且连续刷新有上限，避免无限轮转。
            if resolved.isOwn, !resolved.justRefreshed, refreshStreak < 3,
               let t = currentToken(), !t.refreshToken.isEmpty {
                do {
                    let renewed = try refreshToken(t)
                    guard commitToken(renewed, epoch: epoch) else { abandon(); return }
                    refreshStreak += 1
                    fail("正在续期，马上重试", sleep: false,
                         retryAfter: [30.0, 120.0, 600.0][refreshStreak - 1])
                } catch let e as OAuthError where !e.isCredentialRejection {
                    fail("网络暂时不可用，稍后自动重试", sleep: true, retryAfter: 120)
                } catch {
                    signOut()
                    needLogin("登录已失效\n双击我重新登录")
                }
            } else if resolved.isOwn {
                signOut()
                needLogin("登录已失效\n双击我重新登录")
            } else {
                needLogin("未登录\n双击我登录 Claude 账号")
            }
        case 403:
            // 权限不足（scope 不对等），刷新解决不了，别空转
            fail("接口拒绝访问 (403)\n可尝试重新登录", sleep: true, retryAfter: 600)
        case 429:
            fail("接口限流中，稍后自动重试", sleep: false, retryAfter: 300)
        default:
            fail("接口错误 (\(status))，稍后自动重试", sleep: true, retryAfter: 180)
        }
    }
}

