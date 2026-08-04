// Sundial — a desktop pet that shows Claude Code usage and session status
// This file was split out of main.swift

import AppKit
import CryptoKit
import Foundation
import Security

// MARK: - The pet's own OAuth sign-in (PKCE, with the authorisation code pasted in by hand)

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

    // state holds the same value as the verifier, matching the official client
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
    var expiresAt: Double   // timestamp in seconds
    var isExpiring: Bool { expiresAt < Date().timeIntervalSince1970 + 300 }
}

enum OAuthError: Error {
    case network(String)
    case server(Int, String)
    case badResponse
    case badPaste(String)
}

extension OAuthError {
    /// Only an explicit rejection of these credentials by the server (RFC 6749 §5.2 invalid_grant / client authentication failure) counts as the sign-in having gone stale.
    /// Network failures, 429 rate limiting and 5xx are all transient problems — on no account delete the refresh token from the keychain for those.
    var isCredentialRejection: Bool {
        if case .server(let code, _) = self { return code == 400 || code == 401 }
        return false
    }
}

func oauthErrorText(_ e: Error) -> String {
    switch e {
    case OAuthError.network(let m): return "Network problem: \(m)"
    case OAuthError.badPaste(let m): return m
    case OAuthError.server(let code, let body):
        if code == 400 || code == 401 {
            return """
            The authorisation code is invalid or has expired. The usual causes:
            1. An old "Authentication code" tab is still open and the code was copied from it
               → close every such tab, start the sign-in again, and use only the newest page
            2. The code has already been used once (each one works exactly once)
            3. The authorisation page sat open too long (a few minutes is enough to expire it)
            """
        }
        if code == 429 { return "Too many requests (429). Please wait a moment and try again." }
        return "Server returned \(code): \(body)"
    case OAuthError.badResponse: return "Could not parse the response"
    default: return e.localizedDescription
    }
}

/// Synchronous POST to the token endpoint (call this on a background thread)
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
        throw OAuthError.network("Request timed out")
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

/// What gets pasted may be `code#state`, a bare code, or the whole callback URL the user copied straight out of the address bar
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
        throw OAuthError.badPaste("No authorisation code was recognised in what you pasted. Copy the code shown on the authorisation page, or the whole callback URL from the address bar.")
    }
    // We no longer reject outright just because state doesn't match: the real security
    // binding is PKCE's code_verifier, and the server checks that. Here we only give an
    // actionable hint when it clearly doesn't match, and submit as normal anyway,
    // leaving the final judgement to the server — otherwise an old authorisation page
    // left open in the browser makes people fail over and over.
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

// MARK: - Token storage (a local file, 0600)

enum TokenStore {
    enum LoadOutcome {
        case ok(StoredToken)
        case none      // confirmed that there is no such item
        case failed    // the read was denied / the keychain misbehaved; a retry might succeed
    }

    // The token goes in a local file (0600, readable only by yourself) rather than in the keychain:
    // the signature changes on every rebuild, so the keychain ACL doesn't recognise the new build
    // and asks for a password to re-authorise.
    // A file is unaffected by the signature — however many times you rebuild, there are no more prompts.
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
            // We can't use .completeFileProtection: that's iOS data protection, and on macOS it
            // ties access rights to the code signature of whoever wrote the file. This app's
            // signature changes on every rebuild, and the upshot is that it can't read the token
            // it wrote itself (error 260/EPERM), which shows up as being inexplicably asked to
            // sign in again.
            // The protection comes from the 0600 permission bits + the 0700 directory.
            try data.write(to: fileURL, options: [.atomic])
            try? FileManager.default.setAttributes([.posixPermissions: 0o600],
                                                   ofItemAtPath: fileURL.path)
            return true
        } catch {
            return false
        }
    }

    /// The directory used before the rename; if anything is found there, move it across, so that users who are already signed in aren't forced to sign in again
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
            return .none   // the file is corrupt: signing in again puts it right
        }
        return .none
    }

    static func clear() {
        try? FileManager.default.removeItem(at: fileURL)
    }
}

// MARK: - Reading the credentials

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
        // some installations keep the credentials in a file; even when the keychain refuses us, the file may still be good
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
        // the entry exists but only holds mcpOAuth (the Claude Code desktop app keeps the sign-in token somewhere else, not in this keychain entry)
        if root["claudeAiOauth"] == nil && root["mcpOAuth"] != nil {
            throw CredError.tokenElsewhere
        }
        throw CredError.noOauth
    }

    if let expiresAt = oauth["expiresAt"] as? Double {
        // expiresAt is a millisecond timestamp; leave 60 seconds of headroom
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
    // when nobody deals with the keychain authorisation prompt, `security` just hangs there indefinitely, so we can't wait forever
    if done.wait(timeout: .now() + 15) == .timedOut {
        p.terminate()
        _ = done.wait(timeout: .now() + 2)
        return .denied
    }
    let data = out.fileHandleForReading.readDataToEndOfFile()
    guard p.terminationStatus == 0 else {
        // 44 = there is no such item in the keychain
        return p.terminationStatus == 44 ? .notFound : .denied
    }
    guard var s = String(data: data, encoding: .utf8)?
        .trimmingCharacters(in: .whitespacesAndNewlines), !s.isEmpty else { return .notFound }
    // when the password contains special characters, `security` prints it as hex
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

