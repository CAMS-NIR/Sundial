// Sundial — 桌面宠物，显示 Claude Code 用量与会话状态
// 本文件由 main.swift 拆分而来

import AppKit
import CryptoKit
import Foundation
import Security

// MARK: - 桌宠自己的 OAuth 登录（PKCE，手动粘贴授权码）

enum OAuth {
    static let clientID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
    static let redirectURI = "https://console.anthropic.com/oauth/code/callback"
    static let scope = "org:create_api_key user:profile user:inference"
    static let tokenEndpoint = URL(string: "https://console.anthropic.com/v1/oauth/token")!

    static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    static func newVerifier() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return base64URL(Data(bytes))
    }

    static func challenge(for verifier: String) -> String {
        base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
    }

    // state 与 verifier 同值，和官方客户端一致
    static func authorizeURL(verifier: String) -> URL? {
        var c = URLComponents(string: "https://claude.ai/oauth/authorize")!
        c.queryItems = [
            URLQueryItem(name: "code", value: "true"),
            URLQueryItem(name: "client_id", value: clientID),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "redirect_uri", value: redirectURI),
            URLQueryItem(name: "scope", value: scope),
            URLQueryItem(name: "code_challenge", value: challenge(for: verifier)),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
            URLQueryItem(name: "state", value: verifier),
        ]
        return c.url
    }
}

struct StoredToken: Codable {
    var accessToken: String
    var refreshToken: String
    var expiresAt: Double   // 秒级时间戳
    var isExpiring: Bool { expiresAt < Date().timeIntervalSince1970 + 300 }
}

enum OAuthError: Error {
    case network(String)
    case server(Int, String)
    case badResponse
    case badPaste(String)
}

extension OAuthError {
    /// 只有服务端明确否定这份凭证（RFC 6749 §5.2 invalid_grant / 客户端认证失败）才算登录失效。
    /// 网络故障、429 限流、5xx 都是瞬时问题，绝不能删掉钥匙串里的 refresh token。
    var isCredentialRejection: Bool {
        if case .server(let code, _) = self { return code == 400 || code == 401 }
        return false
    }
}

func oauthErrorText(_ e: Error) -> String {
    switch e {
    case OAuthError.network(let m): return "网络问题：\(m)"
    case OAuthError.badPaste(let m): return m
    case OAuthError.server(let code, let body):
        if code == 400 || code == 401 {
            return """
            授权码无效或已过期。常见原因：
            1. 浏览器里留着旧的「Authentication code」标签页，从旧页复制了码
               → 请关掉所有这类旧标签，重新点一次登录，只用最新那页的码
            2. 同一个码用过一次了（每个码只能用一次）
            3. 授权页放了太久（超过几分钟就会失效）
            """
        }
        if code == 429 { return "请求过于频繁（429），请等一会儿再试。" }
        return "服务器返回 \(code)：\(body)"
    case OAuthError.badResponse: return "返回内容无法解析"
    default: return e.localizedDescription
    }
}

/// 同步 POST 到令牌端点（在后台线程调用）
func oauthPost(_ body: [String: String]) throws -> StoredToken {
    var req = URLRequest(url: OAuth.tokenEndpoint)
    req.httpMethod = "POST"
    req.timeoutInterval = 20
    req.setValue("application/json", forHTTPHeaderField: "Content-Type")
    req.setValue("application/json", forHTTPHeaderField: "Accept")
    req.httpBody = try? JSONSerialization.data(withJSONObject: body)

    let sem = DispatchSemaphore(value: 0)
    var data: Data?
    var status = 0
    var netErr: Error?
    let task = URLSession.shared.dataTask(with: req) { d, r, e in
        data = d
        status = (r as? HTTPURLResponse)?.statusCode ?? 0
        netErr = e
        sem.signal()
    }
    task.resume()
    if sem.wait(timeout: .now() + 25) == .timedOut {
        task.cancel()
        throw OAuthError.network("请求超时")
    }
    if let e = netErr { throw OAuthError.network(e.localizedDescription) }
    guard let d = data else { throw OAuthError.badResponse }
    guard status == 200 else {
        let body = String(data: d, encoding: .utf8) ?? ""
        throw OAuthError.server(status, String(body.prefix(160)))
    }
    guard let root = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any],
          let access = root["access_token"] as? String, !access.isEmpty
    else { throw OAuthError.badResponse }
    let refresh = (root["refresh_token"] as? String) ?? ""
    let expiresIn = (root["expires_in"] as? Double) ?? 3600
    return StoredToken(accessToken: access, refreshToken: refresh,
                       expiresAt: Date().timeIntervalSince1970 + expiresIn)
}

/// 粘贴内容可能是 `code#state`、裸 code，或用户直接从地址栏复制的整条回调地址
func exchangeCode(_ pasted: String, verifier: String) throws -> StoredToken {
    let raw = pasted.trimmingCharacters(in: .whitespacesAndNewlines)
    var code = raw
    var state = verifier

    let low = raw.lowercased()
    if low.hasPrefix("https://") || low.hasPrefix("http://"),
       let c = URLComponents(string: raw) {
        let items = c.queryItems ?? []
        code = items.first { $0.name == "code" }?.value ?? ""
        state = items.first { $0.name == "state" }?.value
            ?? c.fragment.flatMap { $0.isEmpty ? nil : $0 }
            ?? verifier
    } else if let h = raw.firstIndex(of: "#") {
        code = String(raw[raw.startIndex..<h])
        let tail = String(raw[raw.index(after: h)...])
        state = tail.isEmpty ? verifier : tail
    }

    guard !code.isEmpty else {
        throw OAuthError.badPaste("没能从粘贴的内容里认出授权码。请复制授权页面上显示的那段授权码，或浏览器地址栏里的整条回调地址。")
    }
    // 不再因 state 不符就直接拒绝：真正的安全绑定是 PKCE 的 code_verifier，
    // 服务端会校验。这里只在明显不符时给出可操作的提示，仍然照常提交，
    // 让服务端做最终判断——否则浏览器里留着的旧授权页会让人反复失败。
    if state != verifier {
        state = verifier
    }

    return try oauthPost([
        "grant_type": "authorization_code",
        "code": code,
        "state": state,
        "client_id": OAuth.clientID,
        "redirect_uri": OAuth.redirectURI,
        "code_verifier": verifier,
    ])
}

func refreshToken(_ old: StoredToken) throws -> StoredToken {
    var new = try oauthPost([
        "grant_type": "refresh_token",
        "refresh_token": old.refreshToken,
        "client_id": OAuth.clientID,
    ])
    if new.refreshToken.isEmpty { new.refreshToken = old.refreshToken }
    return new
}

// MARK: - 令牌存储（本地文件，0600）

enum TokenStore {
    enum LoadOutcome {
        case ok(StoredToken)
        case none      // 确认没有这一项
        case failed    // 读取被拒/钥匙串异常，重试可能成功
    }

    // 令牌存本地文件（0600，仅本人可读）而不是钥匙串：
    // 每次重新编译签名都会变，钥匙串 ACL 就认不出新版本、要求输密码重新授权。
    // 文件不受签名影响，改多少次都不会再弹窗。
    static var fileURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Sundial/credentials.json")
    }

    @discardableResult
    static func save(_ t: StoredToken) -> Bool {
        guard let data = try? JSONEncoder().encode(t) else { return false }
        let dir = fileURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(
            at: dir, withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        do {
            // 不能用 .completeFileProtection：那是 iOS 的数据保护，在 macOS 上会把
            // 访问权绑到写入方的代码签名；本 App 每次重新编译签名都变，
            // 结果就是读不了自己写的令牌（错误 260/EPERM），表现为莫名其妙要重新登录。
            // 保护靠 0600 权限位 + 0700 目录。
            try data.write(to: fileURL, options: [.atomic])
            try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                                   ofItemAtPath: fileURL.path)
            return true
        } catch {
            return false
        }
    }

    /// 改名前的目录，读到就搬过来，免得已登录的用户被迫重新登录
    private static var legacyFileURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Solaris/credentials.json")
    }

    static func load() -> LoadOutcome {
        if !FileManager.default.fileExists(atPath: fileURL.path),
           let old = try? Data(contentsOf: legacyFileURL),
           let t = try? JSONDecoder().decode(StoredToken.self, from: old) {
            if save(t) { try? FileManager.default.removeItem(at: legacyFileURL) }
            return .ok(t)
        }
        if let d = try? Data(contentsOf: fileURL) {
            if let t = try? JSONDecoder().decode(StoredToken.self, from: d) { return .ok(t) }
            return .none   // 文件坏了：重新登录即可修复
        }
        return .none
    }

    static func clear() {
        try? FileManager.default.removeItem(at: fileURL)
    }
}

// MARK: - 凭证读取

enum CredError: Error {
    case keychainDenied
    case notLoggedIn
    case malformedData
    case noOauth
    case tokenElsewhere
    case expired
}

struct Credentials {
    let accessToken: String
    let subscriptionType: String?
}

enum KeychainResult {
    case ok(Data)
    case notFound
    case denied
}

func loadCredentials() throws -> Credentials {
    let kc = keychainRead()
    var raw: Data?
    if case .ok(let data) = kc { raw = data }
    if raw == nil {
        // 部分安装把凭证放在文件里；钥匙串被拒时文件仍可能有效
        let fileURL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".claude/.credentials.json")
        raw = try? Data(contentsOf: fileURL)
    }
    guard let data = raw else {
        if case .denied = kc { throw CredError.keychainDenied }
        throw CredError.notLoggedIn
    }
    guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
        if case .denied = kc { throw CredError.keychainDenied }
        throw CredError.malformedData
    }
    guard let oauth = root["claudeAiOauth"] as? [String: Any],
          let token = oauth["accessToken"] as? String, !token.isEmpty
    else {
        // 条目存在但只有 mcpOAuth（Claude Code 桌面版把登录令牌存在别处，不在此钥匙串条目里）
        if root["claudeAiOauth"] == nil && root["mcpOAuth"] != nil {
            throw CredError.tokenElsewhere
        }
        throw CredError.noOauth
    }

    if let expiresAt = oauth["expiresAt"] as? Double {
        // expiresAt 为毫秒时间戳；留 60 秒余量
        if expiresAt / 1000.0 < Date().timeIntervalSince1970 + 60 {
            throw CredError.expired
        }
    }
    return Credentials(accessToken: token,
                       subscriptionType: oauth["subscriptionType"] as? String)
}

func keychainRead() -> KeychainResult {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/security")
    p.arguments = ["find-generic-password", "-s", "Claude Code-credentials", "-w"]
    let out = Pipe(), err = Pipe()
    p.standardOutput = out
    p.standardError = err
    let done = DispatchSemaphore(value: 0)
    p.terminationHandler = { _ in done.signal() }
    do { try p.run() } catch { return .denied }
    // 钥匙串授权弹窗无人处理时 security 会一直挂着，不能无限等
    if done.wait(timeout: .now() + 15) == .timedOut {
        p.terminate()
        _ = done.wait(timeout: .now() + 2)
        return .denied
    }
    let data = out.fileHandleForReading.readDataToEndOfFile()
    guard p.terminationStatus == 0 else {
        // 44 = 钥匙串里没有这一项
        return p.terminationStatus == 44 ? .notFound : .denied
    }
    guard var s = String(data: data, encoding: .utf8)?
        .trimmingCharacters(in: .whitespacesAndNewlines), !s.isEmpty else { return .notFound }
    // 密码含特殊字符时 security 会输出十六进制
    if !s.hasPrefix("{"), s.count % 2 == 0,
       s.allSatisfy({ $0.isHexDigit }) {
        var bytes: [UInt8] = []
        var idx = s.startIndex
        while idx < s.endIndex {
            let next = s.index(idx, offsetBy: 2)
            guard let b = UInt8(s[idx..<next], radix: 16) else { break }
            bytes.append(b)
            idx = next
        }
        if let decoded = String(bytes: bytes, encoding: .utf8) { s = decoded }
    }
    guard let result = s.data(using: .utf8) else { return .notFound }
    return .ok(result)
}

