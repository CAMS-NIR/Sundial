// Sundial — a desktop pet that shows Claude Code usage and session status
// This file was split out of main.swift

import AppKit
import Foundation

// MARK: - Usage API

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
    if k.contains("extra") || k.contains("overage") { return nil } // extra paid-for usage, not shown for now
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
        // cope with both second and millisecond timestamps
        return Date(timeIntervalSince1970: n > 4_000_000_000 ? n / 1000 : n)
    }
    return nil
}


/// Compact reset time used on the right-hand side of a limit row: "4h32m" / "Thu 14:00"
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
        // the top-level object uses utilization; the limits array uses percent
        var util: Double?
        for f in ["utilization", "percent"] {
            if let u = obj[f] as? Double { util = u; break }
            if let u = obj[f] as? Int { util = Double(u); break }
        }
        guard let u = util else { return }
        let reset = parseResetDate(obj["resets_at"] ?? obj["resetsAt"])
        // Don't clamp to 100: when you're over the limit you ought to see "106%".
        // Clamping just shows it as exactly used up, which hides that you've gone
        // over. The 999 upper bound is only there to stop dirty data blowing the layout apart
        rows.append(UsageRow(label: mapped.0,
                             percent: Int(max(0, min(999, u)).rounded()),
                             resetAt: reset,
                             priority: mapped.1))
    }
    // Walk the top-level keys in sorted name order: Swift dictionaries are unordered,
    // so without a fixed order, when two keys collide on the same label, which one
    // you end up with varies at random
    for k in root.keys.sorted() {
        if let d = root[k] as? [String: Any] { consider(key: k, obj: d) }
    }
    // limits array: build the name out of kind + scope.model.display_name
    if let limits = root["limits"] as? [[String: Any]] {
        for item in limits {
            var key = (item["kind"] as? String) ?? (item["type"] as? String)
                ?? (item["name"] as? String) ?? ""
            if key == "weekly_scoped",
               let scope = item["scope"] as? [String: Any],
               let model = scope["model"] as? [String: Any],
               let name = model["display_name"] as? String, !name.isEmpty {
                key = "seven_day_" + name          // hand it to labelFor to classify
            }
            consider(key: key, obj: item,
                     activeFlag: (item["is_active"] as? Int ?? 0) == 1)
        }
    }
    // de-duplicate (on a name clash keep the one that appeared first), then sort by priority
    var seen = Set<String>()
    rows = rows.filter { seen.insert($0.label).inserted }
        .sorted { $0.priority < $1.priority }
    if rows.count > 5 { rows = Array(rows.prefix(5)) }

    // Plan name: this originally recognised only the one hard-coded rate_limit_tier key,
    // and the API had long since stopped returning it — so the badge had actually been
    // empty the whole time and nobody noticed. Changed to recognise a few common
    // spellings, so it can still catch it if the API renames the field
    var rawTier: String?
    outer: for k in ["rate_limit_tier", "tier", "subscription_type",
                     "subscription", "plan", "plan_type"] {
        if let s = root[k] as? String, !s.isEmpty { rawTier = s; break }
        if let d = root[k] as? [String: Any] {          // also accept the form wrapped in one extra layer
            for f in ["display_name", "name", "type", "id"] {
                if let s = d[f] as? String, !s.isEmpty { rawTier = s; break outer }
            }
        }
    }
    return (rows, prettyTier(rawTier))
}

// MARK: - Fetch scheduling

final class UsageFetcher {
    var onUpdate: (() -> Void)?
    private let model: PetModel
    private var nextFetchAt = Date.distantPast
    private var inFlight = false      // only one request allowed at any one time
    private var forcePending = false  // a manual refresh that arrived mid-request; run one more as soon as it finishes
    private let normalInterval: TimeInterval = 60

    // Token cache: read the keychain only once after launch, to avoid triggering the system authorisation prompt over and over
    private let tokenLock = NSLock()
    private var cachedToken: StoredToken?
    private var didLoadToken = false
    private var keychainBlocked = false   // the last keychain read failed; wait for a manual refresh before trying again
    private var tokenEpoch = 0            // +1 on every sign-out, invalidating any refresh still in flight
    // only read and written on the background fetch thread (inFlight guarantees there is only ever one fetch at a time)
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

    /// Written by the main thread after a successful sign-in
    func adoptToken(_ t: StoredToken) {
        userSignedOut = false   // only a successful sign-in lifts the "signed out" flag
        tokenLock.lock()
        cachedToken = t
        didLoadToken = true
        keychainBlocked = false
        tokenLock.unlock()
    }

    /// Internal invalidation (the server rejected the token): drop our own token, but still allow falling back to the CLI credentials
    func signOut() {
        tokenLock.lock()
        tokenEpoch &+= 1
        cachedToken = nil
        didLoadToken = true
        tokenLock.unlock()
        TokenStore.clear()
    }

    /// A sign-out the user asked for: additionally remember that they are "signed out", and stop falling back to the CLI credentials automatically
    func signOutByUser() {
        userSignedOut = true
        signOut()
    }

    /// The only entry point allowed to write the token: if the epoch doesn't match (the user signed out in the meantime), throw the whole lot away
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
        if tokenGeneration != epoch { signOut(); return false }  // they signed out again while it was being written to disk, so undo it
        return true
    }

    /// For the main thread (the menu): looks only at the in-memory cache and never touches the keychain, so a prompt can't block the UI
    var hasToken: Bool {
        tokenLock.lock()
        defer { tokenLock.unlock() }
        return cachedToken != nil
    }

    /// For the background thread: the first call reads the keychain (which may raise a prompt); the lock is not held while reading
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
                // don't treat a failure as the final answer, but don't let the 60-second poll keep throwing up the authorisation dialog either
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

    // tick / forceRefresh / finish are only ever called on the main thread (timer and UI events)
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

    /// A refresh the user asked for: also the only place that retries the keychain read (the automatic poll doesn't retry, so it can't pester you with prompts)
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
            self.model.needsLogin = false  // getting here means we did obtain a token, so this is a failure we can retry automatically
            self.finish(retryAfter: retryAfter)
            self.onUpdate?()
        }
    }

    /// Give up on this fetch (the user signed out midway), but we still have to tidy up, otherwise inFlight stays stuck forever
    private func abandon() {
        DispatchQueue.main.async {
            self.model.loading = false
            self.finish(retryAfter: 0)
            self.onUpdate?()
        }
    }

    /// No usable token: go into the awaiting-sign-in state and stop retrying so often
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

    /// Get a usable access token: prefer the one the pet signed in with itself, then any existing Claude Code CLI credentials
    private func resolveToken(epoch: Int) throws
        -> (token: String, tier: String?, isOwn: Bool, justRefreshed: Bool) {
        if var t = currentToken() {
            var refreshed = false
            if t.isExpiring, !t.refreshToken.isEmpty {
                t = try refreshToken(t)   // throws OAuthError on failure
                guard commitToken(t, epoch: epoch) else { throw StaleSignOut() }
                refreshed = true
            }
            return (t.accessToken, nil, true, refreshed)
        }
        if isKeychainBlocked { throw CredError.keychainDenied }
        if userSignedOut { throw CredError.notLoggedIn }  // after a deliberate sign-out, don't fall back to the CLI credentials
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
            // only sign out when the server has explicitly rejected the credentials; for network errors / rate limiting / 5xx always keep the token and retry later
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
            // no CLI credentials / no claudeAiOauth / already expired — they all come down to "please sign in"
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
        // only once wait has returned successfully is it safe to read the captured variables (signal supplies the memory ordering)
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
                // all three branches have to assign: miss the last one out and, after
                // switching accounts, the old plan name keeps hanging around next to
                // the new account's numbers
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
            // Token rejected: try refreshing once. If this round has already refreshed, don't refresh again, and cap the number of consecutive refreshes so it can't spin forever.
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
            // not enough permission (the scopes don't line up); refreshing won't fix it, so don't spin on it
            fail("接口拒绝访问 (403)\n可尝试重新登录", sleep: true, retryAfter: 600)
        case 429:
            fail("接口限流中，稍后自动重试", sleep: false, retryAfter: 300)
        default:
            fail("接口错误 (\(status))，稍后自动重试", sleep: true, retryAfter: 180)
        }
    }
}

