// Sundial (Windows version) — the pet's own OAuth login, token persistence, credential reading
//
// Ported from the macOS version's Auth.swift. Every OAuth parameter (client_id / the three URLs / scope)
// is word-for-word identical to the Swift original; change any one of them and the server rejects you
// outright, so leave them alone.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sundial.Core;

// MARK: - The pet's own OAuth login (PKCE, authorisation code pasted by hand)

/// <summary>OAuth 2.0 PKCE constants and URL construction.</summary>
public static class OAuth
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string RedirectUri = "https://console.anthropic.com/oauth/code/callback";
    public const string Scope = "org:create_api_key user:profile user:inference";
    public const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";
    public const string AuthorizeEndpoint = "https://claude.ai/oauth/authorize";

    /// <summary>base64url: drop the '=' padding, swap '+' and '/' for '-' and '_'.</summary>
    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");

    /// <summary>A new code_verifier: 32 bytes of strong randomness.</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>code_challenge = base64url(SHA256(verifier)), i.e. the S256 method.</summary>
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    /// <summary>The authorisation page URL. state holds the same value as verifier, just like the official client.</summary>
    public static string AuthorizeUrl(string verifier)
    {
        // Parameter order copied straight from the Swift original. Built by hand as a string rather than
        // with UriBuilder: every value here has to be escaped as application/x-www-form-urlencoded
        // (scope contains spaces), and Uri.EscapeDataString is the most predictable way of doing it.
        var q = new (string Key, string Value)[]
        {
            ("code", "true"),
            ("client_id", ClientId),
            ("response_type", "code"),
            ("redirect_uri", RedirectUri),
            ("scope", Scope),
            ("code_challenge", Challenge(verifier)),
            ("code_challenge_method", "S256"),
            ("state", verifier),
        };
        var sb = new StringBuilder(AuthorizeEndpoint);
        for (int i = 0; i < q.Length; i++)
        {
            sb.Append(i == 0 ? '?' : '&')
              .Append(Uri.EscapeDataString(q[i].Key))
              .Append('=')
              .Append(Uri.EscapeDataString(q[i].Value));
        }
        return sb.ToString();
    }
}

/// <summary>The persisted token.</summary>
/// <remarks>
/// The JSON field names keep the Swift version's default Codable naming (accessToken/refreshToken/expiresAt),
/// so the credential file format is interchangeable between the two versions and no data has to be touched
/// when running side-by-side tests on a Mac.
/// </remarks>
public sealed class StoredToken
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";

    /// <summary>A timestamp in seconds (not milliseconds — it is the CLI's expiresAt that is in milliseconds, don't mix the two up).</summary>
    [JsonPropertyName("expiresAt")] public double ExpiresAt { get; set; }

    /// <summary>Leave 300 seconds of slack, so a token just judged "not expired yet" doesn't expire in the gap before the request goes out.</summary>
    [JsonIgnore]
    public bool IsExpiring => ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;
}

public enum OAuthErrorKind
{
    Network,      // can't connect, timed out — transient problems
    Server,       // the server returned something other than 200
    BadResponse,  // 200, but the body won't parse
    BadPaste,     // no authorisation code could be recognised in what the user pasted
}

/// <remarks>
/// Implementing <see cref="ICredentialRejection"/> has to be declared explicitly: UsageFetcher recognises
/// "the credential was rejected" through the interface, and C# does not do structural matching — merely
/// having a property of the same name counts for nothing. Leave this out and the "login has expired" branch
/// is never reached: even with a genuinely dead refresh token it will just go on showing "network temporarily
/// unavailable". This is the counterpart of the Swift version's OAuthError.isCredentialRejection, the one and
/// only test that permits discarding a token; lose it and the whole error-grading scheme is ruined.
/// </remarks>
public sealed class OAuthException : Exception, ICredentialRejection
{
    public OAuthErrorKind Kind { get; }
    public int StatusCode { get; }
    public string Body { get; }

    public OAuthException(OAuthErrorKind kind, string message, int statusCode = 0, string body = "")
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
        Body = body;
    }

    /// <summary>
    /// Only the server explicitly rejecting this credential (RFC 6749 §5.2 invalid_grant / client
    /// authentication failure) counts as the login having expired. Network failures, 429 rate limiting and
    /// 5xx are all transient problems — never delete the locally stored refresh token for those.
    /// </summary>
    public bool IsCredentialRejection =>
        Kind == OAuthErrorKind.Server && (StatusCode == 400 || StatusCode == 401);
}

public enum CredErrorKind
{
    /// <summary>Reading the credential store was refused (on Windows this is usually a DPAPI decryption failure); a retry may succeed.</summary>
    StoreDenied,
    NotLoggedIn,
    MalformedData,
    NoOauth,
    /// <summary>The entry is there, but holds only mcpOAuth: Claude Code has put the login token somewhere else.</summary>
    TokenElsewhere,
    Expired,
}

public sealed class CredException(CredErrorKind kind) : Exception(kind.ToString())
{
    public CredErrorKind Kind { get; } = kind;
}

/// <summary>The credentials that come with Claude Code CLI (the fallback source when the pet has no login of its own).</summary>
public sealed record Credentials(string AccessToken, string? SubscriptionType);

/// <summary>Translates exceptions into wording meant for the user.</summary>
public static class OAuthErrorText
{
    public static string Describe(Exception e)
    {
        if (e is OAuthException oe)
        {
            switch (oe.Kind)
            {
                case OAuthErrorKind.Network:
                    return $"Network problem: {oe.Message}";
                case OAuthErrorKind.BadPaste:
                    return oe.Message;
                case OAuthErrorKind.BadResponse:
                    return "Could not parse the response";
                case OAuthErrorKind.Server:
                    if (oe.StatusCode is 400 or 401)
                    {
                        return """
                        The authorisation code is invalid or has expired. The usual causes:
                        1. An old "Authentication code" tab is still open and the code came from it
                           → close every such tab, start the sign-in again, use only the newest page
                        2. The code has already been used once (each one works exactly once)
                        3. The authorisation page sat open too long (a few minutes is enough)
                        """;
                    }
                    if (oe.StatusCode == 429) return "Too many requests (429). Please wait a moment and try again.";
                    return $"Server returned {oe.StatusCode}: {oe.Body}";
            }
        }
        return e.Message;
    }
}

/// <summary>The two calls against the token endpoint: exchanging an authorisation code for a token, and renewing with a refresh token.</summary>
public static class OAuthClient
{
    // Reused statically: a fresh HttpClient per call exhausts ports (the classic trap), and the call rate
    // here is low to begin with.
    // The 25 second timeout lines up with the Swift version's outer limit of "20 seconds per request plus a
    // 25 second semaphore backstop".
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    public static async Task<StoredToken> PostAsync(
        IReadOnlyDictionary<string, string> body, CancellationToken ct)
    {
        string text;
        int status;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, OAuth.TokenEndpoint);
            req.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            status = (int)resp.StatusCode;
            text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the caller cancelled deliberately, this is not an error
        }
        catch (OperationCanceledException)
        {
            // An HttpClient timeout also comes through as OperationCanceledException (.NET's historical
            // baggage); we tell the two apart by whether ct was triggered, otherwise a timeout gets
            // swallowed as a user cancellation.
            throw new OAuthException(OAuthErrorKind.Network, "Request timed out");
        }
        catch (HttpRequestException e)
        {
            throw new OAuthException(OAuthErrorKind.Network, e.Message);
        }

        if (status != 200)
        {
            throw new OAuthException(OAuthErrorKind.Server, $"HTTP {status}", status,
                text.Length > 160 ? text[..160] : text);
        }

        string access, refresh = "";
        double expiresIn = 3600;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("access_token", out var at) ||
                at.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(at.GetString()))
            {
                throw new OAuthException(OAuthErrorKind.BadResponse, "Could not parse the response");
            }
            access = at.GetString()!;
            if (root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
                refresh = rt.GetString() ?? "";
            if (root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
                && ei.TryGetDouble(out var secs))
                expiresIn = secs;
        }
        catch (JsonException)
        {
            throw new OAuthException(OAuthErrorKind.BadResponse, "Could not parse the response");
        }

        return new StoredToken
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn,
        };
    }

    /// <summary>What gets pasted may be `code#state`, a bare code, or the whole callback URL copied straight out of the address bar.</summary>
    public static Task<StoredToken> ExchangeCodeAsync(string pasted, string verifier, CancellationToken ct)
    {
        var raw = (pasted ?? "").Trim();
        var code = raw;
        var state = verifier;

        var low = raw.ToLowerInvariant();
        if ((low.StartsWith("https://") || low.StartsWith("http://"))
            && Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            var items = ParseQuery(uri.Query);
            code = items.TryGetValue("code", out var c) ? c : "";
            if (items.TryGetValue("state", out var s) && s.Length > 0)
            {
                state = s;
            }
            else
            {
                // Uri.Fragment carries the leading '#'; strip it off and what's left is the state itself
                var frag = uri.Fragment.TrimStart('#');
                state = frag.Length > 0 ? frag : verifier;
            }
        }
        else
        {
            var h = raw.IndexOf('#');
            if (h >= 0)
            {
                code = raw[..h];
                var tail = raw[(h + 1)..];
                state = tail.Length > 0 ? tail : verifier;
            }
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new OAuthException(OAuthErrorKind.BadPaste,
                "No authorisation code was recognised in what you pasted. Copy the code shown on the authorisation page, or the whole callback URL from the address bar.");
        }

        // No longer rejected outright just because state doesn't match: the real security binding is PKCE's
        // code_verifier, which the server validates. Here we only give an actionable hint when there's an
        // obvious mismatch, and submit as usual regardless, letting the server make the final call —
        // otherwise an old authorisation page left open in the browser has people failing over and over.
        if (state != verifier) state = verifier;

        return PostAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["state"] = state,
            ["client_id"] = OAuth.ClientId,
            ["redirect_uri"] = OAuth.RedirectUri,
            ["code_verifier"] = verifier,
        }, ct);
    }

    public static async Task<StoredToken> RefreshAsync(StoredToken old, CancellationToken ct)
    {
        var fresh = await PostAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = old.RefreshToken,
            ["client_id"] = OAuth.ClientId,
        }, ct).ConfigureAwait(false);
        // The server doesn't always send back a new refresh_token; not carrying the old one forward amounts to throwing away the ability to renew
        if (string.IsNullOrEmpty(fresh.RefreshToken)) fresh.RefreshToken = old.RefreshToken;
        return fresh;
    }

    /// <summary>Parses the query string only, without pulling in System.Web: every value here needs percent-decoding.</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var val = eq < 0 ? "" : pair[(eq + 1)..];
            try
            {
                // Percent-decoding only, no treating '+' as a space: this is exactly what Swift's
                // URLComponents does (in RFC 3986 a '+' inside a query is an ordinary character; only HTML
                // form encoding treats it as a space).
                // Don't get clever and change it back: when a '+' really does turn up in an authorisation
                // code, converting it to a space corrupts the code, and the user ends up in a dead end where
                // the code is forever "invalid" with no way of finding out why.
                var k = Uri.UnescapeDataString(key);
                // On a duplicate key take the first, matching Swift's items.first { $0.name == ... }
                if (!result.ContainsKey(k)) result[k] = Uri.UnescapeDataString(val);
            }
            catch (UriFormatException)
            {
                // Malformed escaping caused by pasting the wrong thing: skip this item and let the layer above report that no authorisation code was recognised
            }
        }
        return result;
    }
}

// MARK: - Token storage

public enum TokenLoadStatus
{
    Ok,
    None,    // confirmed that there is no such item
    Failed,  // the read was refused / decryption blew up; a retry may succeed
}

public readonly record struct TokenLoadOutcome(TokenLoadStatus Status, StoredToken? Token);

/// <summary>
/// Getting the token onto disk. On Windows it is encrypted with DPAPI and written to
/// %LOCALAPPDATA%\Sundial\credentials.dat; on other platforms (running tests on a Mac) it is plain JSON
/// plus 0600 permission bits.
/// </summary>
public static class TokenStore
{
    // DPAPI with the CurrentUser scope: only the current Windows account can unlock it.
    //
    // The lesson from the macOS version must not be forgotten: over there the token started out in the
    // keychain, keychain ACLs go by the code signature, and the signature changes with every rebuild — so
    // the app couldn't read the token it had written itself (error 260/EPERM), which showed up as users
    // being inexplicably asked to log in again. DPAPI's CurrentUser scope is not tied to the executable:
    // rebuild it, move it, upgrade the version and it still decrypts — so apart from the fixed entropy
    // below, do not add any further form of binding (don't use LocalMachine, and don't mix the path or the
    // version number into the entropy).
    //
    // The entropy is only there for domain separation, it isn't a key, so having it in plain sight here is
    // fine; but change it just once and every logged-in user's token stops decrypting and they are forced to
    // log in again, so never touch this constant.
    private static readonly byte[] Entropy = "Sundial.credentials.v1"u8.ToArray();

    /// <summary>The data directory. Windows: %LOCALAPPDATA%\Sundial; elsewhere: ~/.local/share/Sundial.</summary>
    public static string DirectoryPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Sundial");
            }
            // Off Windows we spell out ~/.local/share explicitly rather than relying on how SpecialFolder maps differently from platform to platform
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "Sundial");
        }
    }

    public static string FilePath => Path.Combine(
        DirectoryPath, OperatingSystem.IsWindows() ? "credentials.dat" : "credentials.json");

    /// <summary>The "the user signed out deliberately" marker. The Swift version keeps it in UserDefaults; there's no equivalent here, so an empty file stands in for it.</summary>
    private static string SignedOutFlagPath => Path.Combine(DirectoryPath, "signedout.flag");

    /// <summary>
    /// Where the Swift version keeps things on macOS. Read-only, never moved and never deleted — that is
    /// another programme's data; it just means tests on a Mac can reuse an existing login instead of walking
    /// through the whole OAuth flow every time.
    /// </summary>
    private static string? MacSwiftFilePath => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       "Library", "Application Support", "Sundial", "credentials.json")
        : null;

    public static bool Save(StoredToken token)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(token);
            EnsureDirectory();

            // Write a temporary file first and then rename it: a crash part-way through won't leave half a
            // file behind; the permission bits are set before the rename as well, which avoids a window in
            // which "the file already exists but is still 0644".
            var tmp = FilePath + ".tmp";
            if (OperatingSystem.IsWindows())
            {
                var blob = System.Security.Cryptography.ProtectedData.Protect(
                    json, Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(tmp, blob);
            }
            else
            {
                File.WriteAllBytes(tmp, json);
                TrySetOwnerOnly(tmp);
            }
            File.Move(tmp, FilePath, overwrite: true);
            if (!OperatingSystem.IsWindows()) TrySetOwnerOnly(FilePath);
            return true;
        }
        catch (Exception)
        {
            // Failing to save isn't fatal: the token is still in memory for this run and everything carries
            // on working, it's just that the next start will need a fresh login. The caller uses the return
            // value to tell the user.
            return false;
        }
    }

    public static TokenLoadOutcome Load()
    {
        byte[] raw;
        try
        {
            if (File.Exists(FilePath))
            {
                raw = File.ReadAllBytes(FilePath);
            }
            else
            {
                // Borrowing has to give way to the "signed out" marker: otherwise, while testing, you click
                // sign out, restart, and get logged straight back in again — it took ages of digging to
                // realise it was the Swift version's file doing it.
                var legacy = UserSignedOut ? null : MacSwiftFilePath;
                if (legacy is not null && File.Exists(legacy))
                {
                    var borrowed = Deserialize(File.ReadAllBytes(legacy));
                    return borrowed is null
                        ? new TokenLoadOutcome(TokenLoadStatus.None, null)
                        : new TokenLoadOutcome(TokenLoadStatus.Ok, borrowed);
                }
                return new TokenLoadOutcome(TokenLoadStatus.None, null);
            }
        }
        catch (IOException)
        {
            return new TokenLoadOutcome(TokenLoadStatus.Failed, null);   // file in use / transient IO error, worth retrying
        }
        catch (UnauthorizedAccessException)
        {
            return new TokenLoadOutcome(TokenLoadStatus.Failed, null);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                raw = System.Security.Cryptography.ProtectedData.Unprotect(
                    raw, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // A different Windows account, someone else's file copied over, or the file is corrupt.
                // Classed as Failed rather than None: don't turn something uncertain into the conclusion
                // that "the user isn't logged in".
                return new TokenLoadOutcome(TokenLoadStatus.Failed, null);
            }
        }

        var token = Deserialize(raw);
        // The file is corrupt: logging in again fixes it, so this is None and not Failed
        return token is null
            ? new TokenLoadOutcome(TokenLoadStatus.None, null)
            : new TokenLoadOutcome(TokenLoadStatus.Ok, token);
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The marker for the user having signed out deliberately: once it is set we no longer fall back to the CLI credentials.</summary>
    public static bool UserSignedOut
    {
        get
        {
            try { return File.Exists(SignedOutFlagPath); }
            catch (IOException) { return false; }
        }
        set
        {
            try
            {
                if (value)
                {
                    EnsureDirectory();
                    File.WriteAllBytes(SignedOutFlagPath, Array.Empty<byte>());
                }
                else
                {
                    File.Delete(SignedOutFlagPath);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static StoredToken? Deserialize(byte[] data)
    {
        try
        {
            var t = JsonSerializer.Deserialize<StoredToken>(data);
            return string.IsNullOrEmpty(t?.AccessToken) ? null : t;
        }
        catch (JsonException) { return null; }
    }

    private static void EnsureDirectory()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows()) TrySetOwnerOnly(DirectoryPath, isDirectory: true);
    }

    /// <summary>Off Windows the only protection is the permission bits: 0600 for files, 0700 for directories.</summary>
    private static void TrySetOwnerOnly(string path, bool isDirectory = false)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // Failing to set the permission bits doesn't block the save (for instance when the directory
            // sits on a mount point that doesn't support chmod). A usable token matters more than perfect
            // permissions, and the proper Windows build never gets here in the first place.
        }
    }
}

// MARK: - Claude Code CLI credentials (the fallback source)

/// <summary>Reads the credential file that Claude Code CLI writes for itself.</summary>
public static class CredentialsFile
{
    /// <summary>&lt;home directory&gt;\.claude\.credentials.json — this layout holds on both Windows and macOS.</summary>
    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".credentials.json");

    public static Credentials Load()
    {
        byte[] data;
        try
        {
            if (!File.Exists(FilePath)) throw new CredException(CredErrorKind.NotLoggedIn);
            data = File.ReadAllBytes(FilePath);
        }
        catch (IOException)
        {
            throw new CredException(CredErrorKind.StoreDenied);
        }
        catch (UnauthorizedAccessException)
        {
            throw new CredException(CredErrorKind.StoreDenied);
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException) { throw new CredException(CredErrorKind.MalformedData); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new CredException(CredErrorKind.MalformedData);

            var hasOauth = root.TryGetProperty("claudeAiOauth", out var oauth)
                           && oauth.ValueKind == JsonValueKind.Object;
            string? token = null;
            if (hasOauth && oauth.TryGetProperty("accessToken", out var at)
                && at.ValueKind == JsonValueKind.String)
            {
                token = at.GetString();
            }

            if (string.IsNullOrEmpty(token))
            {
                // The entry exists but holds only mcpOAuth (Claude Code keeps the login token elsewhere, not in this file)
                if (!hasOauth && root.TryGetProperty("mcpOAuth", out _))
                    throw new CredException(CredErrorKind.TokenElsewhere);
                throw new CredException(CredErrorKind.NoOauth);
            }

            if (oauth.TryGetProperty("expiresAt", out var exp)
                && exp.ValueKind == JsonValueKind.Number
                && exp.TryGetDouble(out var expiresAt))
            {
                // expiresAt is a millisecond timestamp; leave 60 seconds of slack
                if (expiresAt / 1000.0 < DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60)
                    throw new CredException(CredErrorKind.Expired);
            }

            string? sub = null;
            if (oauth.TryGetProperty("subscriptionType", out var st) && st.ValueKind == JsonValueKind.String)
                sub = st.GetString();

            return new Credentials(token!, sub);
        }
    }
}

// MARK: - Token source (the ITokenSource implementation)

/// <summary>
/// Gets hold of a usable access token: the pet's own login first, then any existing Claude Code CLI credentials.
/// </summary>
/// <remarks>
/// The interface has only one channel for expressing failure — returning null — so the convention is:
/// <list type="bullet">
/// <item>returning null = there really is no usable credential, so lead the user through logging in; for the
/// specific reason see <see cref="LastNoTokenReason"/>.</item>
/// <item>throwing <see cref="OAuthException"/> = something went wrong during renewal. The caller must look at
/// <see cref="OAuthException.IsCredentialRejection"/>: only when it is true has the login actually expired
/// (that means Invalidate + asking the user to log in again); otherwise it is network trouble / rate
/// limiting / 5xx, which just needs a retry later — whatever you do, don't delete the token.</item>
/// </list>
/// The token is read from disk only once after start-up and served from an in-memory cache after that, which,
/// as in the macOS version, avoids triggering system-level authorisation checks over and over.
/// </remarks>
public sealed class TokenSource : ITokenSource
{
    private readonly object _lock = new();
    private StoredToken? _cached;
    private bool _didLoad;
    private bool _storeBlocked;          // last read failed; wait for the user to refresh by hand before trying again
    private int _epoch;                  // +1 on every sign-out, voiding any refresh still in flight
    private bool _lastResolveRefreshed;
    private CredErrorKind? _lastNoTokenReason;

    /// <summary>Changes on every sign-out. Used to work out whether a renewal still in flight has already been superseded and is void.</summary>
    public int TokenGeneration { get { lock (_lock) return _epoch; } }

    /// <summary>Whether this <see cref="ResolveAsync"/> has just renewed the token. Read on the same thread immediately after ResolveAsync.</summary>
    /// <remarks>
    /// In the Swift version this is justRefreshed inside resolveToken's return value; there is nowhere for it
    /// in the interface signature here, so it has to hang off a property instead. What it's for: on a 401,
    /// deciding "we already refreshed this round, so don't refresh again", which avoids spinning forever.
    /// It relies on the layer above guaranteeing that only one fetch runs at any one time (inFlight).
    /// </remarks>
    public bool LastResolveRefreshed { get { lock (_lock) return _lastResolveRefreshed; } }

    /// <summary>Why null was returned most recently, so the layer above can pick suitable wording for the prompt.</summary>
    public CredErrorKind? LastNoTokenReason { get { lock (_lock) return _lastNoTokenReason; } }

    /// <summary>For the main thread (the menu): looks only at the in-memory cache, never touches the disk or decryption, so the UI can't stall.</summary>
    public bool HasToken { get { lock (_lock) return _cached is not null; } }

    /// <summary>Written by the UI thread after a successful login.</summary>
    public void AdoptToken(StoredToken t)
    {
        TokenStore.UserSignedOut = false;   // only a successful login lifts the "signed out" state
        lock (_lock)
        {
            _cached = t;
            _didLoad = true;
            _storeBlocked = false;
            _lastNoTokenReason = null;
        }
    }

    /// <summary>Internal invalidation (the server rejected the token): drop our own token, but still allow falling back to the CLI credentials.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            unchecked { _epoch++; }
            _cached = null;
            _didLoad = true;
        }
        TokenStore.Clear();
    }

    /// <summary>The user signing out deliberately: additionally remembers the "signed out" state and no longer falls back to the CLI credentials automatically.</summary>
    public void SignOutByUser()
    {
        TokenStore.UserSignedOut = true;
        Invalidate();
    }

    /// <summary>
    /// Called on a manual refresh: lifts the block left behind by the last failed read, so the next Resolve
    /// goes back to the disk. Automatic polling does not do this — a failed read often comes with a system
    /// dialogue or a long block, and retrying that once every 60 seconds is harassment.
    /// </summary>
    public void RetryBlockedRead()
    {
        lock (_lock)
        {
            if (!_storeBlocked) return;
            _didLoad = false;
            _storeBlocked = false;
        }
    }

    public async Task<(string Token, string? Tier, bool IsOwn)?> ResolveAsync(CancellationToken ct)
    {
        int epoch;
        lock (_lock)
        {
            epoch = _epoch;
            _lastResolveRefreshed = false;
        }

        var t = CurrentToken();
        if (t is not null)
        {
            if (t.IsExpiring && t.RefreshToken.Length > 0)
            {
                var renewed = await OAuthClient.RefreshAsync(t, ct).ConfigureAwait(false);  // throws OAuthException on failure
                if (CommitToken(renewed, epoch))
                {
                    t = renewed;
                    lock (_lock) _lastResolveRefreshed = true;
                }
                else
                {
                    // The user signed out while the renewal was in flight, so this new token is void, the
                    // whole lot of it.
                    // The Swift version throws StaleSignOut here so the caller can wind things up quietly;
                    // the interface has no such channel, so we carry on as though "we have no token of our
                    // own" (a deliberate sign-out gets caught further down, and if it was only an internal
                    // invalidation then falling back to the CLI credentials was allowed anyway).
                    t = null;
                }
            }
            if (t is not null)
            {
                lock (_lock) _lastNoTokenReason = null;
                return (t.AccessToken, null, true);
            }
        }

        bool blocked;
        lock (_lock) blocked = _storeBlocked;
        if (blocked) return NoToken(CredErrorKind.StoreDenied);
        if (TokenStore.UserSignedOut) return NoToken(CredErrorKind.NotLoggedIn);  // after a deliberate sign-out we don't fall back to the CLI credentials

        try
        {
            var creds = CredentialsFile.Load();
            lock (_lock) _lastNoTokenReason = null;
            return (creds.AccessToken, creds.SubscriptionType, false);
        }
        catch (CredException e)
        {
            // No CLI credentials / no claudeAiOauth / already expired — they all come down to "please log in"
            return NoToken(e.Kind);
        }
    }

    /// <summary>
    /// Renew once, off our own bat, after a 401. Returns false = there is no token to renew, or the renewal
    /// finished only to find the user had signed out (in which case the whole lot should be thrown away).
    /// Network and server errors still throw <see cref="OAuthException"/>, which the caller routes by
    /// IsCredentialRejection.
    /// </summary>
    public async Task<bool> TryRenewAsync(CancellationToken ct)
    {
        int epoch;
        StoredToken? t;
        lock (_lock)
        {
            epoch = _epoch;
            t = _cached;
        }
        if (t is null || t.RefreshToken.Length == 0) return false;

        var renewed = await OAuthClient.RefreshAsync(t, ct).ConfigureAwait(false);
        return CommitToken(renewed, epoch);
    }

    private (string Token, string? Tier, bool IsOwn)? NoToken(CredErrorKind reason)
    {
        lock (_lock) _lastNoTokenReason = reason;
        return null;
    }

    /// <summary>For background threads: the first call reads from disk and decrypts, and holds no lock while it does so.</summary>
    private StoredToken? CurrentToken()
    {
        bool alreadyLoaded;
        StoredToken? cached;
        lock (_lock)
        {
            alreadyLoaded = _didLoad;
            cached = _cached;
        }
        if (alreadyLoaded) return cached;

        var outcome = TokenStore.Load();   // no lock held; don't let disk IO stall UI queries such as HasToken
        lock (_lock)
        {
            if (!_didLoad)
            {
                switch (outcome.Status)
                {
                    case TokenLoadStatus.Ok:
                        _cached = outcome.Token; _didLoad = true; _storeBlocked = false;
                        break;
                    case TokenLoadStatus.None:
                        _cached = null; _didLoad = true; _storeBlocked = false;
                        break;
                    case TokenLoadStatus.Failed:
                        // Don't treat a failure as a conclusion (it doesn't mean the user isn't logged in), but don't let the 60 second polling retry it over and over either
                        _cached = null; _didLoad = true; _storeBlocked = true;
                        break;
                }
            }
            return _cached;
        }
    }

    /// <summary>The only entry point allowed to write the token: if the epoch doesn't match (the user signed out in the meantime) the whole lot is thrown away.</summary>
    private bool CommitToken(StoredToken t, int epoch)
    {
        lock (_lock)
        {
            if (_epoch != epoch) return false;
            _cached = t;
            _didLoad = true;
        }
        TokenStore.Save(t);
        if (TokenGeneration != epoch)
        {
            Invalidate();   // they signed out again while it was being written to disk, so undo it
            return false;
        }
        return true;
    }
}
