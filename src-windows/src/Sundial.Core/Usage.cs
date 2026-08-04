// Sundial (Windows 版) — 用量接口：解析 + 取数调度
//
// 逐函数移植自 macOS 版 Usage.swift。原文里标注了实测结论和 bug 成因的注释一律原样保留，
// 那些是踩坑换来的，比代码本身重要。
//
// 与 macOS 版的结构差异（详见各处注释）：令牌的读取／刷新／落盘全部收进 ITokenSource，
// 由 Auth 模块实现，本文件不碰钥匙串/DPAPI，也不自己发 OAuth 请求。

using System.Globalization;
using System.Text.Json;

namespace Sundial.Core;

/// <summary>
/// 令牌来源。真正的实现在 Auth 模块（TokenStore / OAuthClient），这里只声明契约。
/// macOS 版把「读钥匙串 / 判断过期 / 刷新 / 回退到 CLI 凭证 / 记住用户主动退出」都写在
/// UsageFetcher 里，Windows 版把这些搬去 Auth 模块，UsageFetcher 只管取数与调度。
/// </summary>
public interface ITokenSource
{
    /// <summary>
    /// 返回可用的 access token；没有可用令牌时返回 null（对应 macOS 版的「请登录」分支）。
    /// IsOwn=true 表示是本程序自己登录拿的令牌，false 表示回退用了 Claude Code CLI 的凭证——
    /// 这个区分很要紧：别人的凭证轮不到我们作废。
    /// </summary>
    Task<(string Token, string? Tier, bool IsOwn)?> ResolveAsync(CancellationToken ct);

    /// <summary>凭证被服务端拒绝时调用。只有服务端明确否定（400/401）才允许走到这里。</summary>
    void Invalidate();

    // 下面四个成员都带默认实现：接口是加法，老的实现（含测试里的桩）不改也能编过，
    // 只是退回到「没有这些能力」的降级行为。Auth 模块的 TokenSource 全都提供了。

    /// <summary>
    /// 刚才那次 <see cref="ResolveAsync"/> 里是否已经续过一次期。
    /// 对应 macOS 版 resolveToken 返回值里的 justRefreshed：401 时若本轮刚换过令牌，
    /// 说明这份凭证是真的死了，别再刷一次空转，直接判定登录失效。
    /// </summary>
    bool LastResolveRefreshed => false;

    /// <summary>
    /// 最近一次 <see cref="ResolveAsync"/> 返回 null 的原因，用来挑提示文案；
    /// null = 不区分，一律按「未登录」处理。对应 macOS 版 resolveToken 抛的 CredError。
    /// </summary>
    CredErrorKind? LastNoTokenReason => null;

    /// <summary>
    /// 收到 401 后主动续一次期。true = 已换上新令牌，false = 没有可续的令牌
    /// （或续期途中用户退出了登录，这份新令牌整单作废）。
    /// 网络／服务端故障要照常抛异常，由调用方按「是不是凭证被否定」分流——
    /// 吞掉异常返回 false 会让一次断网变成永久登出。
    /// </summary>
    Task<bool> TryRenewAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// 用户手动刷新时调用：解除「上次读取凭证存储失败」造成的封锁。
    /// macOS 版这条规则是为钥匙串授权弹窗定的——自动轮询不重试，免得每 60 秒弹一次框；
    /// Windows 上 DPAPI 解密失败同理（可能伴随长时间阻塞），所以规则原样保留。
    /// </summary>
    void RetryBlockedRead() { }
}

/// <summary>
/// 别的模块抛出的异常若实现了这个接口，UsageFetcher 就能把「服务端明确否定凭证」
/// （RFC 6749 §5.2 invalid_grant / 客户端认证失败，即 400、401）与网络故障区分开，
/// 对应 macOS 版 OAuthError.isCredentialRejection。
/// 不实现也能跑：所有异常都按可重试的瞬时故障处理。方向必须是这一边——
/// 把网络故障误判成凭证失效，会把用户永久登出；多重试几次只是多等一会儿。
/// </summary>
/// <remarks>
/// Auth 模块的 OAuthException 只有同名属性、并没有声明实现这个接口，
/// C# 又不做结构化匹配，所以 UsageFetcher 里必须额外按具体类型认一次它，
/// 否则「登录已失效」这条分支永远走不到，refresh token 真死了也只会一直显示
/// 「网络暂时不可用」——这正是 <see cref="UsageFetcher"/> 里那段类型判断存在的理由。
/// </remarks>
public interface ICredentialRejection
{
    bool IsCredentialRejection { get; }
}

// MARK: - 用量接口解析

public static class Usage
{
    /// <summary>接口给的键名 → 界面标签 + 排序优先级；返回 null 表示这条不展示。</summary>
    public static (string Label, int Priority)? LabelFor(string key)
    {
        var k = key.ToLowerInvariant();
        if (k.Contains("five_hour") || k == "session") return ("5 小时", 0);
        if (k == "seven_day" || k == "weekly" || k == "weekly_all") return ("每周 · 全部模型", 1);
        if (k.Contains("fable")) return ("每周 · Fable", 2);
        if (k.Contains("mythos")) return ("每周 · Mythos", 2);
        if (k.Contains("opus")) return ("每周 · Opus", 3);
        if (k.Contains("sonnet")) return ("每周 · Sonnet", 4);
        if (k.Contains("cowork")) return ("每周 · Cowork", 5);
        if (k.Contains("routine")) return ("每周 · Routines", 6);
        if (k.Contains("extra") || k.Contains("overage")) return null; // 额外付费用量，暂不展示
        if (k.Contains("seven_day"))
        {
            var name = Capitalized(k.Replace("seven_day_", ""));
            return ($"每周 · {name}", 7);
        }
        return null;
    }

    /// <summary>
    /// 对应 Swift 的 String.capitalized：每个「词」首字母大写、其余小写。
    /// 不用 TextInfo.ToTitleCase——它的分词规则依赖 ICU，Windows 上可能开着
    /// InvariantGlobalization，行为会变；自己写死才确定。
    /// </summary>
    /// <remarks>
    /// 分词规则是在 macOS 上跑 Foundation 实测出来的，不是按空格切：只要前一个字符
    /// 不是字母就算新词的开头。实测样本——
    /// "foo_bar"→"Foo_Bar"、"opus-4-5"→"Opus-4-5"、"opus4x"→"Opus4X"、
    /// "20x_max"→"20X_Max"、"a1b_c2d"→"A1B_C2D"、"a  b"→"A  B"。
    /// 按空格切会得到 "Foo_bar"、"Opus4x"，和原版不一致。
    /// </remarks>
    private static string Capitalized(string s)
    {
        var buf = s.ToCharArray();
        for (var i = 0; i < buf.Length; i++)
        {
            var atWordStart = i == 0 || !char.IsLetter(buf[i - 1]);
            buf[i] = atWordStart ? char.ToUpperInvariant(buf[i]) : char.ToLowerInvariant(buf[i]);
        }
        return new string(buf);
    }

    /// <summary>重置时间：ISO8601 字符串，或秒/毫秒时间戳。</summary>
    public static DateTimeOffset? ParseResetDate(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            return s is null ? null : ParseResetDate(s);
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return FromEpoch(n);
        return null;
    }

    public static DateTimeOffset? ParseResetDate(string s)
    {
        if (s.Length == 0) return null;
        // 原文用了两个 ISO8601DateFormatter（先试带小数秒、再试不带）；
        // .NET 的 TryParse 两种写法都吃，一次就够。
        // AssumeUniversal：原文的 formatter 遇到没带时区的串会直接失败，
        // 这里选择当成 UTC 而不是本地时间——服务端给的就是 UTC，猜本地会偏好几个小时。
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
        {
            return d;
        }
        return null;
    }

    private static DateTimeOffset? FromEpoch(double n)
    {
        // 兼容秒 / 毫秒时间戳
        var secs = n > 4_000_000_000 ? n / 1000 : n;
        // 脏数据别让 AddSeconds 抛越界异常，直接当没有重置时间
        if (!double.IsFinite(secs) || secs < -62_135_596_800 || secs > 253_402_300_799) return null;
        return DateTimeOffset.UnixEpoch.AddSeconds(secs);
    }

    private static readonly string[] WeekdayZh = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    /// <summary>限额行右侧用的紧凑重置时间："4h32m" / "周四 14:00"</summary>
    public static string CompactReset(DateTimeOffset? d)
    {
        if (d is null) return "";
        var secs = (d.Value - DateTimeOffset.Now).TotalSeconds;
        if (secs <= 0) return "即将";
        if (secs < 24 * 3600)
        {
            int total = (int)secs, h = total / 3600, m = total % 3600 / 60;
            return h > 0 ? $"{h}h{m}m" : $"{m}m";
        }
        // 原文是 DateFormatter + zh_CN 的 "EEE HH:mm"。这里写死中文星期，
        // 免得依赖 ICU：Windows 上可能开着 InvariantGlobalization，那时 zh-CN 会退化成英文
        var local = d.Value.ToLocalTime();
        return $"{WeekdayZh[(int)local.DayOfWeek]} {local.Hour:00}:{local.Minute:00}";
    }

    public static string PrettyTier(string? raw)
    {
        // 先落成非空 string 再往下走：可空引用类型的流分析进不了 lambda，
        // 留着 string? 会在下面那个闭包里报 CS8602
        if (string.IsNullOrEmpty(raw)) return "";
        var t = raw.ToLowerInvariant();
        var mult = Multipliers.FirstOrDefault(m => t.Contains(m));
        if (t.Contains("max")) return mult is not null ? $"Max ({mult})" : "Max";
        if (t.Contains("pro")) return "Pro";
        if (t.Contains("team")) return "Team";
        if (t.Contains("enterprise")) return "Enterprise";
        if (t.Contains("free")) return "Free";
        return raw;
    }

    // "20x" 排在最前：先匹配到长的那个，免得被短的截胡
    private static readonly string[] Multipliers = { "20x", "5x", "2x" };
    private static readonly string[] UtilFields = { "utilization", "percent" };
    private static readonly string[] TierKeys =
        { "rate_limit_tier", "tier", "subscription_type", "subscription", "plan", "plan_type" };
    private static readonly string[] TierNestedFields = { "display_name", "name", "type", "id" };

    public static (List<UsageRow> Rows, string Tier)? ParseUsage(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return ParseRoot(doc.RootElement);
        }
        catch (JsonException) { return null; }   // 对应 Swift 的 try?
    }

    /// <summary>字符串入口，方便测试直接喂样本 JSON。</summary>
    public static (List<UsageRow> Rows, string Tier)? ParseUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseRoot(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    // 私有：JsonElement 的生命周期绑在 JsonDocument 上，不让它漏到外面去
    private static (List<UsageRow> Rows, string Tier)? ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var rows = new List<UsageRow>();

        void Consider(string key, JsonElement obj)
        {
            var mapped = LabelFor(key);
            if (mapped is null) return;
            // 顶层对象用 utilization；limits 数组用 percent。两套字段名都得认，
            // 只认一套的话换个接口版本就整片空白
            double? util = null;
            foreach (var f in UtilFields)
            {
                if (obj.TryGetProperty(f, out var e) && e.ValueKind == JsonValueKind.Number
                    && e.TryGetDouble(out var u))
                {
                    util = u;
                    break;
                }
            }
            if (util is not { } u2 || !double.IsFinite(u2)) return;

            DateTimeOffset? reset = null;
            if (obj.TryGetProperty("resets_at", out var r) || obj.TryGetProperty("resetsAt", out r))
            {
                reset = ParseResetDate(r);
            }

            // 不夹到 100：超限时就该看见「106%」，夹住只会显示成正好用完，
            // 反而看不出已经超了。上界 999 只是防脏数据把版面撑破
            rows.Add(new UsageRow(
                mapped.Value.Label,
                (int)Math.Round(Math.Clamp(u2, 0, 999), MidpointRounding.AwayFromZero),
                reset,
                mapped.Value.Priority));
        }

        // 顶层键按名字排序后再遍历：原文里 Swift 字典无序，撞名时若不定序，
        // 同一标签取到哪一条会随机变化。System.Text.Json 虽然按文档顺序枚举（不会随机），
        // 但那顺序是服务端给的，排序照样必要——否则服务端调一下字段顺序，显示的数就变了
        foreach (var p in root.EnumerateObject()
                     .Where(p => p.Value.ValueKind == JsonValueKind.Object)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            Consider(p.Name, p.Value);
        }

        // limits 数组：用 kind + scope.model.display_name 拼出名字
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var key = Str(item, "kind") ?? Str(item, "type") ?? Str(item, "name") ?? "";
                if (key == "weekly_scoped"
                    && item.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                    && scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                {
                    var name = Str(model, "display_name");
                    if (!string.IsNullOrEmpty(name)) key = "seven_day_" + name;   // 交给 LabelFor 归类
                }
                // 原文这里还传了个 activeFlag（is_active），但 consider 根本没用它，这里就不带了
                Consider(key, item);
            }
        }

        // 去重（同名保留先出现的），按优先级排序。
        // OrderBy 是稳定排序：同优先级（比如 Fable / Mythos 都是 2）保持先来后到，
        // 不会像 Swift 的不稳定排序那样随机换位
        var seen = new HashSet<string>(StringComparer.Ordinal);
        rows = rows.Where(r => seen.Add(r.Label)).OrderBy(r => r.Priority).ToList();
        if (rows.Count > 5) rows = rows.Take(5).ToList();

        // 套餐名：原先只认死 rate_limit_tier 这一个键，而接口早就不返回它了，
        // 徽章其实一直是空的也没人发现。改成认几个常见写法，接口换名字还能接得住
        string? rawTier = null;
        foreach (var k in TierKeys)
        {
            if (!root.TryGetProperty(k, out var e)) continue;
            if (e.ValueKind == JsonValueKind.String)
            {
                var s = e.GetString();
                if (!string.IsNullOrEmpty(s)) { rawTier = s; break; }
                continue;                       // 空串不算命中，接着试下一个键
            }
            if (e.ValueKind == JsonValueKind.Object)   // 也接受包了一层的写法
            {
                foreach (var f in TierNestedFields)
                {
                    var s = Str(e, f);
                    if (!string.IsNullOrEmpty(s)) { rawTier = s; break; }
                }
                if (rawTier is not null) break;
            }
        }

        return (rows, PrettyTier(rawTier));
    }

    /// <summary>取字符串字段；不存在或不是字符串返回 null。空串照样返回空串——
    /// 对应 Swift 的 as? String，?? 只在 nil 时才落到下一个候选键。</summary>
    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}

// MARK: - 取数调度

/// <summary>
/// 定时向 /api/oauth/usage 取数，把结果写进 PetModel。
/// Tick / ForceRefresh 必须在 UI 线程调用（定时器与 UI 事件），HTTP 在线程池上跑，
/// 回写模型时统一 post 回 UI 线程——和 macOS 版 main queue 的约定一致。
/// </summary>
public sealed class UsageFetcher : IDisposable
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const double NormalInterval = 60;   // 秒

    /// <summary>数据有变动时回调（在 UI 线程上）。</summary>
    public Action? OnUpdate;

    private readonly PetModel _model;
    private readonly ITokenSource _tokens;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _cts = new();

    private DateTimeOffset _nextFetchAt = DateTimeOffset.MinValue;   // = Date.distantPast
    private bool _inFlight;        // 同一时刻只允许一个请求
    private bool _forcePending;    // 请求进行中收到的手动刷新，完成后立即补一次

    // 只在后台 fetch 线程读写（_inFlight 保证同一时刻只有一个 fetch）
    private int _refreshStreak;

    /// <param name="post">
    /// 把回调排到 UI 线程的办法。不传就抓构造时的 SynchronizationContext
    /// （Avalonia 在 UI 线程上装了一个）；再没有就地执行，方便在测试里同步跑完。
    /// Core 层不引用任何 UI 框架，所以不能直接用 Dispatcher。
    /// </param>
    public UsageFetcher(PetModel model, ITokenSource tokens,
                        HttpClient? http = null, Action<Action>? post = null)
    {
        _model = model;
        _tokens = tokens;
        _ownsHttp = http is null;
        // 自己建的客户端设 20 秒；外面塞进来的不动它的 Timeout，反正每次请求还套了
        // 一个 20 秒的 CTS 兜底（对应原文「per-request 15s + 信号量 20s」里的外层那道）
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (post is not null)
        {
            _post = post;
        }
        else
        {
            var ctx = SynchronizationContext.Current;
            _post = ctx is null ? a => a() : a => ctx.Post(_ => a(), null);
        }
    }

    // Tick / ForceRefresh / Finish 只在 UI 线程调用（定时器与 UI 事件）
    public void Tick()
    {
        if (_cts.IsCancellationRequested) return;
        if (_inFlight) return;
        if (!_forcePending && DateTimeOffset.Now < _nextFetchAt) return;
        _forcePending = false;
        _inFlight = true;
        _nextFetchAt = DateTimeOffset.Now.AddSeconds(NormalInterval);
        _ = Task.Run(RunFetchAsync);
    }

    /// <summary>用户主动刷新：也是唯一重新尝试读取凭证存储的入口（自动轮询不重试，免得骚扰）。</summary>
    public void ForceRefresh()
    {
        // 对应 macOS 版 forceRefresh 里复位 keychainBlocked 那两行。规则的由来是钥匙串授权弹窗，
        // Windows 上换成 DPAPI 解密失败，一样不该每 60 秒重试一次，所以规则原样保留，
        // 只是复位动作挪进了 ITokenSource（那边才知道自己被什么挡住了）
        _tokens.RetryBlockedRead();
        _forcePending = true;
        Tick();
    }

    private void Finish(double retryAfter)
    {
        _inFlight = false;
        _nextFetchAt = DateTimeOffset.Now.AddSeconds(retryAfter);
        if (_forcePending) Tick();
    }

    private void Fail(string msg, bool sleep, double retryAfter = 60)
    {
        _post(() =>
        {
            _model.ErrorMsg = msg;
            _model.Asleep = sleep;
            _model.Loading = false;
            _model.NeedsLogin = false;   // 走到这里说明令牌已取到，属于可自动重试的失败
            Finish(retryAfter);
            OnUpdate?.Invoke();
        });
    }

    /// <summary>放弃本次取数（关停中），但必须收尾，否则 _inFlight 永远挂着。</summary>
    private void Abandon()
    {
        _post(() =>
        {
            _model.Loading = false;
            Finish(0);
            OnUpdate?.Invoke();
        });
    }

    /// <summary>没有可用令牌：进入待登录状态，不再频繁重试。</summary>
    private void NeedLogin(string msg)
    {
        _post(() =>
        {
            _model.NeedsLogin = true;
            _model.Rows = new List<UsageRow>();
            _model.ErrorMsg = msg;
            _model.Asleep = true;
            _model.Loading = false;
            Finish(3600);
            OnUpdate?.Invoke();
        });
    }

    /// <summary>
    /// 「服务端明确否定了这份凭证」——只有这一种情况才允许作废令牌。
    /// 必须把 OAuthException 单列出来按具体类型认：它有同名属性但没声明实现
    /// <see cref="ICredentialRejection"/>，光靠接口判断这条分支永远是 false，
    /// 结果就是 refresh token 真的失效了也只显示「网络暂时不可用」，永远不提示重新登录。
    /// </summary>
    private static bool IsCredentialRejection(Exception e) => e switch
    {
        OAuthException oe => oe.IsCredentialRejection,
        ICredentialRejection c => c.IsCredentialRejection,
        _ => false,   // 没自报家门的异常一律当瞬时故障：宁可多等，不可误登出
    };

    private async Task RunFetchAsync()
    {
        try
        {
            await FetchAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // 兜底：FetchAsync 的每条出口都会自己收尾，走到这儿说明有没想到的异常。
            // 宁可多收一次尾，也不能让 _inFlight 挂住——那样宠物就再也不刷新了
            // （原文 abandon() 的注释警告的正是这个坑）。
            // 关停途中的异常不必回写模型：那时 Tick 已经短路，_inFlight 挂着也无所谓
            if (_cts.IsCancellationRequested) return;
            Fail($"内部错误：{e.Message}", sleep: true, retryAfter: 300);
        }
    }

    private async Task FetchAsync()
    {
        var ct = _cts.Token;

        (string Token, string? Tier, bool IsOwn)? resolved;
        try
        {
            resolved = await _tokens.ResolveAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 只有真的被关停才静默收尾。不加这个 when 的话，HttpClient 超时抛的
            // TaskCanceledException 也会掉进来 → Abandon 把 retryAfter 设成 0 →
            // 下一个定时器 tick 立刻再来一次，变成拿超时当节拍器的热循环
            Abandon();
            return;
        }
        catch (Exception e) when (IsCredentialRejection(e))
        {
            // 只有服务端明确否定凭证才登出
            _tokens.Invalidate();
            NeedLogin("登录已失效\n双击我重新登录");
            return;
        }
        catch (Exception)
        {
            // 网络/限流/5xx，以及任何没自报家门的异常，一律保留令牌稍后重试。
            // 绝不能在这里 Invalidate：一次断网就把用户永久登出，这是最贵的那种 bug
            Fail("网络暂时不可用，稍后自动重试", sleep: true, retryAfter: 120);
            return;
        }

        // 紧跟 ResolveAsync 之后取走，别等到 401 分支再读：那时中间隔了一整个 HTTP 往返
        var justRefreshed = _tokens.LastResolveRefreshed;

        if (resolved is not { } cred)
        {
            // 没有可用令牌：没登录过、用户主动退出、CLI 凭证不存在或已过期，都归到这里。
            // 「凭证存储读取被拒」要单独说：那是可重试的，不是真的没登录，
            // 文案得告诉用户「再试一次」而不是让他重走一遍登录（对应原文的 keychainDenied 分支）
            NeedLogin(_tokens.LastNoTokenReason == CredErrorKind.StoreDenied
                ? "凭证读取被拒\n双击我重试或重新登录"
                : "未登录\n双击我登录 Claude 账号");
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cred.Token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        int status;
        byte[] body;
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token)
                                        .ConfigureAwait(false);
            status = (int)resp.StatusCode;
            body = await resp.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Abandon();
            return;
        }
        catch (OperationCanceledException)
        {
            // 只剩超时这一种可能（HttpClient.Timeout 与 CTS 都走这个异常）
            Fail("请求超时，稍后自动重试", sleep: true, retryAfter: 90);
            return;
        }
        catch (HttpRequestException)
        {
            Fail("网络不可用，稍后自动重试", sleep: true, retryAfter: 90);
            return;
        }
        catch (IOException)
        {
            // 读响应体时断流
            Fail("网络不可用，稍后自动重试", sleep: true, retryAfter: 90);
            return;
        }

        switch (status)
        {
            case 200:
            {
                _refreshStreak = 0;
                var parsed = Usage.ParseUsage(body);
                if (parsed is not { } ok || ok.Rows.Count == 0)
                {
                    Fail("接口返回了看不懂的数据\n（Anthropic 可能改了格式）", sleep: true, retryAfter: 300);
                    return;
                }
                _post(() =>
                {
                    _model.Rows = ok.Rows;
                    // 三个分支都要赋值：漏掉最后一个，换账号后旧套餐名会一直挂在
                    // 新账号的数字旁边
                    if (ok.Tier.Length > 0)
                    {
                        _model.Tier = ok.Tier;
                    }
                    else if (cred.Tier is { } sub)
                    {
                        _model.Tier = Usage.PrettyTier(sub);
                    }
                    else
                    {
                        _model.Tier = "";
                    }
                    _model.LastFetch = DateTimeOffset.Now;
                    _model.ErrorMsg = null;
                    _model.Asleep = false;
                    _model.Loading = false;
                    _model.NeedsLogin = false;
                    Finish(NormalInterval);
                    OnUpdate?.Invoke();
                });
                return;
            }
            case 401:
                // 令牌被拒：立刻试一次刷新。本轮已经刷过（justRefreshed）就不再刷——
                // 刚换的新令牌马上又被拒，说明凭证是真的死了，再刷只是空转；
                // 连续刷新还有 3 次上限，避免无限轮转
                if (cred.IsOwn && !justRefreshed && _refreshStreak < 3)
                {
                    bool renewed;
                    try
                    {
                        // 这里用 ct 而不是上面那个 timeout：那 20 秒是给 usage 请求的，
                        // 到这会儿多半已经烧掉了，拿它去续期等于必然超时
                        renewed = await _tokens.TryRenewAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        Abandon();
                        return;
                    }
                    catch (Exception e) when (!IsCredentialRejection(e))
                    {
                        // 续期时断网/限流/5xx：令牌一个字都别动，等下次
                        Fail("网络暂时不可用，稍后自动重试", sleep: true, retryAfter: 120);
                        return;
                    }
                    catch (Exception)
                    {
                        // 服务端明确否定了 refresh token，这才是真失效
                        _tokens.Invalidate();
                        NeedLogin("登录已失效\n双击我重新登录");
                        return;
                    }

                    if (renewed)
                    {
                        _refreshStreak++;
                        Fail("正在续期，马上重试", sleep: false,
                             retryAfter: new[] { 30.0, 120.0, 600.0 }[_refreshStreak - 1]);
                    }
                    else
                    {
                        // false 有两种来路：压根没有可续的令牌，或者续期途中用户退出了登录。
                        // 原文分别走 signOut+needLogin 与 abandon，接口分不出来，统一按前者。
                        // 不能选 abandon：它把 retryAfter 设成 0，碰上第一种来路就成了
                        // 「401 → 立刻重试 → 401」的热循环
                        _tokens.Invalidate();
                        NeedLogin("登录已失效\n双击我重新登录");
                    }
                }
                else if (cred.IsOwn)
                {
                    _tokens.Invalidate();
                    NeedLogin("登录已失效\n双击我重新登录");
                }
                else
                {
                    // 用的是 Claude Code CLI 的凭证，不是我们发的，无权作废它
                    NeedLogin("未登录\n双击我登录 Claude 账号");
                }
                return;
            case 403:
                // 权限不足（scope 不对等），刷新解决不了，别空转
                Fail("接口拒绝访问 (403)\n可尝试重新登录", sleep: true, retryAfter: 600);
                return;
            case 429:
                Fail("接口限流中，稍后自动重试", sleep: false, retryAfter: 300);
                return;
            default:
                Fail($"接口错误 ({status})，稍后自动重试", sleep: true, retryAfter: 180);
                return;
        }
    }

    public void Dispose()
    {
        // 只 Cancel 不 Dispose：在途的 FetchAsync 还要拿 _cts.Token 去建链接令牌源，
        // 这时把它 Dispose 掉会让那边抛 ObjectDisposedException。交给 GC 就行
        _cts.Cancel();
        if (_ownsHttp) _http.Dispose();
    }
}
