// Sundial (Windows 版) — 桌宠自己的 OAuth 登录、令牌持久化、凭证读取
//
// 移植自 macOS 版 Auth.swift。OAuth 的各项参数（client_id / 三个 URL / scope）
// 与 Swift 原文逐字一致，改动其中任何一个都会让服务端直接拒绝，别动。

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sundial.Core;

// MARK: - 桌宠自己的 OAuth 登录（PKCE，手动粘贴授权码）

/// <summary>OAuth 2.0 PKCE 的常量与 URL 构造。</summary>
public static class OAuth
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string RedirectUri = "https://console.anthropic.com/oauth/code/callback";
    public const string Scope = "org:create_api_key user:profile user:inference";
    public const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";
    public const string AuthorizeEndpoint = "https://claude.ai/oauth/authorize";

    /// <summary>base64url：去掉补位的 '='，'+' '/' 换成 '-' '_'。</summary>
    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");

    /// <summary>新的 code_verifier：32 字节强随机。</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>code_challenge = base64url(SHA256(verifier))，即 S256 方式。</summary>
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    /// <summary>授权页地址。state 与 verifier 同值，和官方客户端一致。</summary>
    public static string AuthorizeUrl(string verifier)
    {
        // 参数顺序照抄 Swift 原文。手工拼串而不是用 UriBuilder：
        // 这里每个值都必须按 application/x-www-form-urlencoded 转义（scope 里有空格），
        // Uri.EscapeDataString 是最确定的做法。
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

/// <summary>持久化的令牌。</summary>
/// <remarks>
/// JSON 字段名沿用 Swift 版 Codable 的默认命名（accessToken/refreshToken/expiresAt），
/// 这样两版的凭证文件格式互通，Mac 上做对照测试时不用改数据。
/// </remarks>
public sealed class StoredToken
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";

    /// <summary>秒级时间戳（不是毫秒——CLI 那边的 expiresAt 才是毫秒，别混）。</summary>
    [JsonPropertyName("expiresAt")] public double ExpiresAt { get; set; }

    /// <summary>留 300 秒余量，免得刚判定「还没过期」请求就发出去过期了。</summary>
    [JsonIgnore]
    public bool IsExpiring => ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;
}

public enum OAuthErrorKind
{
    Network,      // 连不上、超时——瞬时问题
    Server,       // 服务端返回了非 200
    BadResponse,  // 200 但内容解析不出来
    BadPaste,     // 用户粘贴的东西里认不出授权码
}

/// <remarks>
/// 必须显式声明实现 <see cref="ICredentialRejection"/>：UsageFetcher 是按接口认「凭证被否定」的，
/// C# 不做结构化匹配，光有个同名属性不算数。漏了这一句，「登录已失效」那条分支永远走不到，
/// refresh token 真死了也只会一直显示「网络暂时不可用」——对应 Swift 版 OAuthError.isCredentialRejection
/// 那条唯一允许作废令牌的判据，丢了它整套错误分级就废了。
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
    /// 只有服务端明确否定这份凭证（RFC 6749 §5.2 invalid_grant / 客户端认证失败）才算登录失效。
    /// 网络故障、429 限流、5xx 都是瞬时问题，绝不能删掉本地存着的 refresh token。
    /// </summary>
    public bool IsCredentialRejection =>
        Kind == OAuthErrorKind.Server && (StatusCode == 400 || StatusCode == 401);
}

public enum CredErrorKind
{
    /// <summary>凭证存储读取被拒（Windows 上一般是 DPAPI 解密失败），重试可能成功。</summary>
    StoreDenied,
    NotLoggedIn,
    MalformedData,
    NoOauth,
    /// <summary>条目在，但只有 mcpOAuth：登录令牌被 Claude Code 存到别处去了。</summary>
    TokenElsewhere,
    Expired,
}

public sealed class CredException(CredErrorKind kind) : Exception(kind.ToString())
{
    public CredErrorKind Kind { get; } = kind;
}

/// <summary>Claude Code CLI 自带的凭证（桌宠没自己登录时的回退来源）。</summary>
public sealed record Credentials(string AccessToken, string? SubscriptionType);

/// <summary>把异常翻译成给用户看的话。</summary>
public static class OAuthErrorText
{
    public static string Describe(Exception e)
    {
        if (e is OAuthException oe)
        {
            switch (oe.Kind)
            {
                case OAuthErrorKind.Network:
                    return $"网络问题：{oe.Message}";
                case OAuthErrorKind.BadPaste:
                    return oe.Message;
                case OAuthErrorKind.BadResponse:
                    return "返回内容无法解析";
                case OAuthErrorKind.Server:
                    if (oe.StatusCode is 400 or 401)
                    {
                        return """
                        授权码无效或已过期。常见原因：
                        1. 浏览器里留着旧的「Authentication code」标签页，从旧页复制了码
                           → 请关掉所有这类旧标签，重新点一次登录，只用最新那页的码
                        2. 同一个码用过一次了（每个码只能用一次）
                        3. 授权页放了太久（超过几分钟就会失效）
                        """;
                    }
                    if (oe.StatusCode == 429) return "请求过于频繁（429），请等一会儿再试。";
                    return $"服务器返回 {oe.StatusCode}：{oe.Body}";
            }
        }
        return e.Message;
    }
}

/// <summary>令牌端点的两个调用：用授权码换令牌、用 refresh token 续期。</summary>
public static class OAuthClient
{
    // 静态复用：每次 new HttpClient 会耗尽端口（经典坑），而且这里的调用频率本来就低。
    // 超时 25 秒对齐 Swift 版「请求 20 秒 + 信号量兜底 25 秒」的外层上限。
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
            throw;   // 调用方主动取消，不是错误
        }
        catch (OperationCanceledException)
        {
            // HttpClient 超时也走 OperationCanceledException（.NET 的历史包袱），
            // 靠 ct 有没有被触发来区分，不然会把超时当成用户取消吞掉。
            throw new OAuthException(OAuthErrorKind.Network, "请求超时");
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
                throw new OAuthException(OAuthErrorKind.BadResponse, "返回内容无法解析");
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
            throw new OAuthException(OAuthErrorKind.BadResponse, "返回内容无法解析");
        }

        return new StoredToken
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn,
        };
    }

    /// <summary>粘贴内容可能是 `code#state`、裸 code，或用户直接从地址栏复制的整条回调地址。</summary>
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
                // Uri.Fragment 带着开头的 '#'，去掉才是 state 本身
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
                "没能从粘贴的内容里认出授权码。请复制授权页面上显示的那段授权码，或浏览器地址栏里的整条回调地址。");
        }

        // 不再因 state 不符就直接拒绝：真正的安全绑定是 PKCE 的 code_verifier，
        // 服务端会校验。这里只在明显不符时给出可操作的提示，仍然照常提交，
        // 让服务端做最终判断——否则浏览器里留着的旧授权页会让人反复失败。
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
        // 服务端不一定回新的 refresh_token；不接着用旧的就等于把续期能力丢了
        if (string.IsNullOrEmpty(fresh.RefreshToken)) fresh.RefreshToken = old.RefreshToken;
        return fresh;
    }

    /// <summary>只解析 query 串，不引 System.Web：这里的值都要百分号解码。</summary>
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
                // 只做百分号解码，不把 '+' 当空格：Swift 的 URLComponents 就是这么干的
                // （RFC 3986 里 query 中的 '+' 是普通字符，只有 HTML 表单编码才当空格）。
                // 别自作聪明改回去：授权码里真出现一个 '+' 时，转成空格就把码改坏了，
                // 用户会得到一个永远「授权码无效」、还查不出原因的死局。
                var k = Uri.UnescapeDataString(key);
                // 重复键取第一个，和 Swift 的 items.first { $0.name == ... } 一致
                if (!result.ContainsKey(k)) result[k] = Uri.UnescapeDataString(val);
            }
            catch (UriFormatException)
            {
                // 粘错内容导致的畸形转义：这一项跳过，让上层报「认不出授权码」
            }
        }
        return result;
    }
}

// MARK: - 令牌存储

public enum TokenLoadStatus
{
    Ok,
    None,    // 确认没有这一项
    Failed,  // 读取被拒/解密异常，重试可能成功
}

public readonly record struct TokenLoadOutcome(TokenLoadStatus Status, StoredToken? Token);

/// <summary>
/// 令牌落地。Windows 上用 DPAPI 加密后写 %LOCALAPPDATA%\Sundial\credentials.dat，
/// 其它平台（Mac 上跑测试）写明文 JSON + 0600 权限位。
/// </summary>
public static class TokenStore
{
    // DPAPI 用 CurrentUser 作用域：只有当前 Windows 账户能解开。
    //
    // macOS 版的教训必须记住：那边一开始把令牌放钥匙串，钥匙串 ACL 认代码签名，
    // 每次重新编译签名都变，于是 App 读不了自己写的令牌（错误 260/EPERM），
    // 表现为用户莫名其妙要重新登录。DPAPI 的 CurrentUser 作用域不绑定可执行文件，
    // 重新编译、换路径、升级版本都照样解得开——所以除了下面这段固定 entropy 之外，
    // 不要再加任何形式的绑定（别用 LocalMachine，也别把路径/版本号掺进 entropy）。
    //
    // entropy 只是做域隔离，不是密钥，明文写在这里没问题；但它一旦改动，
    // 所有已登录用户的令牌都会解不开、被迫重新登录，所以永远不要动这个常量。
    private static readonly byte[] Entropy = "Sundial.credentials.v1"u8.ToArray();

    /// <summary>数据目录。Windows: %LOCALAPPDATA%\Sundial；其它: ~/.local/share/Sundial。</summary>
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
            // 非 Windows 显式拼 ~/.local/share，不依赖 SpecialFolder 在各平台上的映射差异
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "Sundial");
        }
    }

    public static string FilePath => Path.Combine(
        DirectoryPath, OperatingSystem.IsWindows() ? "credentials.dat" : "credentials.json");

    /// <summary>「用户主动退出」的标记。Swift 版存在 UserDefaults，这里没有等价物，用一个空文件代替。</summary>
    private static string SignedOutFlagPath => Path.Combine(DirectoryPath, "signedout.flag");

    /// <summary>
    /// macOS 上 Swift 版的存放位置。只读、不搬不删——那是另一个程序的数据，
    /// 在 Mac 上跑测试时能直接复用已有登录，省得每次都重走一遍 OAuth。
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

            // 先写临时文件再改名：中途崩溃不会留下半截文件；
            // 权限位也在改名前就设好，避免出现「文件已存在但还是 0644」的窗口期。
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
            // 存不下不是致命错误：本次运行内存里还有令牌，照常工作，
            // 只是下次启动要重新登录。调用方据返回值提示用户。
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
                // 借读要让位给「已退出」标记：否则测试时点了退出登录、重启又自动登回去，
                // 排查半天才发现是 Swift 版的文件在起作用。
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
            return new TokenLoadOutcome(TokenLoadStatus.Failed, null);   // 占用/瞬时 IO 错，值得重试
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
                // 换了 Windows 账户、拷贝了别人的文件，或者文件坏了。
                // 归为 Failed 而不是 None：不确定的事不要当成「用户没登录」的结论。
                return new TokenLoadOutcome(TokenLoadStatus.Failed, null);
            }
        }

        var token = Deserialize(raw);
        // 文件坏了：重新登录即可修复，所以是 None 不是 Failed
        return token is null
            ? new TokenLoadOutcome(TokenLoadStatus.None, null)
            : new TokenLoadOutcome(TokenLoadStatus.Ok, token);
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>用户主动退出的标记：置位后不再回退到 CLI 凭证。</summary>
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

    /// <summary>非 Windows 的保护手段就是权限位：文件 0600、目录 0700。</summary>
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
            // 设不上权限位不阻断保存（比如目录在不支持 chmod 的挂载点上）。
            // 令牌能用比权限完美更重要，Windows 正式版本来也走不到这里。
        }
    }
}

// MARK: - Claude Code CLI 凭证（回退来源）

/// <summary>读 Claude Code CLI 自己写的凭证文件。</summary>
public static class CredentialsFile
{
    /// <summary>&lt;用户目录&gt;\.claude\.credentials.json —— 这个写法在 Windows 和 macOS 上都成立。</summary>
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
                // 条目存在但只有 mcpOAuth（Claude Code 把登录令牌存在别处，不在这个文件里）
                if (!hasOauth && root.TryGetProperty("mcpOAuth", out _))
                    throw new CredException(CredErrorKind.TokenElsewhere);
                throw new CredException(CredErrorKind.NoOauth);
            }

            if (oauth.TryGetProperty("expiresAt", out var exp)
                && exp.ValueKind == JsonValueKind.Number
                && exp.TryGetDouble(out var expiresAt))
            {
                // expiresAt 为毫秒时间戳；留 60 秒余量
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

// MARK: - 令牌来源（ITokenSource 实现）

/// <summary>
/// 取一个可用的 access token：优先桌宠自己登录的，其次已有的 Claude Code CLI 凭证。
/// </summary>
/// <remarks>
/// 接口只有「返回 null」这一个表达失败的通道，所以约定：
/// <list type="bullet">
/// <item>返回 null = 确实没有可用凭证，请引导用户登录；具体原因看 <see cref="LastNoTokenReason"/>。</item>
/// <item>抛 <see cref="OAuthException"/> = 续期过程中出的错。调用方必须看
/// <see cref="OAuthException.IsCredentialRejection"/>：为真才是登录失效（该 Invalidate + 要求重新登录），
/// 否则是网络/限流/5xx，稍后重试即可，千万别把令牌删了。</item>
/// </list>
/// 令牌只在启动后读一次盘，之后走内存缓存，和 macOS 版一样避免反复触发系统层面的授权检查。
/// </remarks>
public sealed class TokenSource : ITokenSource
{
    private readonly object _lock = new();
    private StoredToken? _cached;
    private bool _didLoad;
    private bool _storeBlocked;          // 上次读取失败，等用户手动刷新再试
    private int _epoch;                  // 每次退出登录 +1，作废在途的刷新
    private bool _lastResolveRefreshed;
    private CredErrorKind? _lastNoTokenReason;

    /// <summary>每次退出登录都会变。用来判断一次在途的续期是否已经过期作废。</summary>
    public int TokenGeneration { get { lock (_lock) return _epoch; } }

    /// <summary>本次 <see cref="ResolveAsync"/> 里是否刚续过期。紧跟 ResolveAsync 之后在同一线程读。</summary>
    /// <remarks>
    /// Swift 版是 resolveToken 返回值里的 justRefreshed，接口签名里没有它的位置，
    /// 只好挂成属性。用途：401 时判断「本轮已经刷过就别再刷」，避免无限轮转。
    /// 依赖上层保证同一时刻只有一个取数在跑（inFlight）。
    /// </remarks>
    public bool LastResolveRefreshed { get { lock (_lock) return _lastResolveRefreshed; } }

    /// <summary>最近一次返回 null 的原因，供上层挑合适的提示文案。</summary>
    public CredErrorKind? LastNoTokenReason { get { lock (_lock) return _lastNoTokenReason; } }

    /// <summary>主线程（菜单）用：只看内存缓存，绝不碰磁盘/解密，避免卡 UI。</summary>
    public bool HasToken { get { lock (_lock) return _cached is not null; } }

    /// <summary>登录成功后由 UI 线程写入。</summary>
    public void AdoptToken(StoredToken t)
    {
        TokenStore.UserSignedOut = false;   // 登录成功才解除「已退出」
        lock (_lock)
        {
            _cached = t;
            _didLoad = true;
            _storeBlocked = false;
            _lastNoTokenReason = null;
        }
    }

    /// <summary>内部失效（令牌被服务端否定）：清掉自己的令牌，但仍允许回退到 CLI 凭证。</summary>
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

    /// <summary>用户主动退出：额外记住「已退出」，不再自动回退到 CLI 凭证。</summary>
    public void SignOutByUser()
    {
        TokenStore.UserSignedOut = true;
        Invalidate();
    }

    /// <summary>
    /// 手动刷新时调用：解除上次读取失败造成的封锁，下次 Resolve 会重新读盘。
    /// 自动轮询不做这件事——读取失败往往会伴随系统弹窗/长时间阻塞，60 秒一次地重试是骚扰。
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
                var renewed = await OAuthClient.RefreshAsync(t, ct).ConfigureAwait(false);  // 失败抛 OAuthException
                if (CommitToken(renewed, epoch))
                {
                    t = renewed;
                    lock (_lock) _lastResolveRefreshed = true;
                }
                else
                {
                    // 续期期间用户退出登录了，这份新令牌整单作废。
                    // Swift 版此处抛 StaleSignOut 让调用方静默收尾；接口没有这个通道，
                    // 于是按「自己没有令牌」继续往下走（主动退出的话下面会拦住，
                    // 只是内部失效的话本来就允许回退到 CLI 凭证）。
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
        if (TokenStore.UserSignedOut) return NoToken(CredErrorKind.NotLoggedIn);  // 主动退出后不回退到 CLI 凭证

        try
        {
            var creds = CredentialsFile.Load();
            lock (_lock) _lastNoTokenReason = null;
            return (creds.AccessToken, creds.SubscriptionType, false);
        }
        catch (CredException e)
        {
            // CLI 凭证不存在／无 claudeAiOauth／已过期，都归结为「请登录」
            return NoToken(e.Kind);
        }
    }

    /// <summary>
    /// 401 之后主动续一次期。返回 false = 没有可续的令牌，或续完发现已被退出登录（该整单丢弃）。
    /// 网络/服务端错误照样抛 <see cref="OAuthException"/>，由调用方按 IsCredentialRejection 分流。
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

    /// <summary>后台线程用：首次会读盘并解密，读取期间不持锁。</summary>
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

        var outcome = TokenStore.Load();   // 不持锁，别让磁盘 IO 卡住 HasToken 这类 UI 查询
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
                        // 不把失败当成结论（不代表用户没登录），但也别让 60 秒轮询反复重试
                        _cached = null; _didLoad = true; _storeBlocked = true;
                        break;
                }
            }
            return _cached;
        }
    }

    /// <summary>唯一允许写令牌的入口：纪元不符（期间用户退出登录了）就整单丢弃。</summary>
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
            Invalidate();   // 落盘期间又退了，撤销
            return false;
        }
        return true;
    }
}
