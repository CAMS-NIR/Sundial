// Sundial (Windows 版) — 共享数据模型
//
// 这个文件是各模块之间的契约，先定死再分头实现，避免各写各的类型。
// 语义严格对齐 macOS 版的 Model.swift / Activity.swift，改这里要两边一起改。

namespace Sundial.Core;

/// <summary>一条用量限额。</summary>
/// <param name="Percent">已用百分比。<b>可能大于 100</b>（超限），画圆环时自己夹到一圈，
/// 但数字要如实显示，否则看不出超了多少。</param>
public sealed record UsageRow(string Label, int Percent, DateTimeOffset? ResetAt, int Priority);

/// <summary>一个 Claude Code 会话的实时状态。</summary>
public sealed record SessionActivity
{
    public required string Id { get; init; }          // sessionId
    public string Title { get; init; } = "";
    public bool Busy { get; init; }
    public bool Waiting { get; init; }                // 抛出了选项，正等用户选
    public DateTimeOffset? Since { get; init; }       // 本轮开始时间，用于计时
    public bool Unread { get; init; }                 // 已结束但用户还没看
    public DateTimeOffset? FinishedAt { get; init; }
    public int CtxTokens { get; init; }               // 上下文已用 token
    public int CtxLimit { get; init; }                // 上下文上限，0 = 未知
    public bool Background { get; init; }             // 主回合已结束，后台代理仍在跑
    public bool Stalled { get; init; }                // 长时间没有新记录，状态未知（≠ 已跑完）
}

/// <summary>界面读的全部状态。只在 UI 线程改。</summary>
public sealed class PetModel
{
    public const int MaxBlocks = 4;

    public List<UsageRow> Rows { get; set; } = new();
    public string Tier { get; set; } = "";
    public DateTimeOffset? LastFetch { get; set; }
    public string? ErrorMsg { get; set; }              // 有错误时置文案；成功后清空
    public bool Asleep { get; set; }                   // 拿不到数据时宠物睡觉
    public bool Loading { get; set; } = true;
    public bool NeedsLogin { get; set; }               // 没有可用令牌，等用户登录
    public List<SessionActivity> Sessions { get; set; } = new();
    public bool Hovered { get; set; }
    public bool DetailsPinned { get; set; }

    public bool AnyBusy => Sessions.Any(s => s.Busy);

    /// <summary>正在跑的 + 已完成但还没看的。未读会一直留着，直到点掉或该会话又开始工作。</summary>
    public IReadOnlyList<SessionActivity> VisibleSessions =>
        Sessions.Where(s => s.Busy || s.Unread).Take(MaxBlocks).ToList();

    public int MaxPercent => Rows.Count == 0 ? 0 : Rows.Max(r => r.Percent);

    /// <summary>
    /// 两个仪表：左=5 小时；右=用得最多的那条每周限额。
    /// 右边取「最紧」而不是固定某一条——某个模型的专属周限额更紧时必须显示它，否则会误导。
    /// </summary>
    public (UsageRow? Outer, UsageRow? Inner) RingRows
    {
        get
        {
            var outer = Rows.FirstOrDefault(r => r.Label.Contains("5 小时")) ?? Rows.FirstOrDefault();
            var weeklies = Rows.Where(r => r.Label.StartsWith("每周")).ToList();
            var inner = weeklies.Count > 0
                ? weeklies.Aggregate((a, b) => b.Percent > a.Percent ? b : a)
                : Rows.FirstOrDefault(r => r.Label != outer?.Label);
            return (outer, inner);
        }
    }
}

/// <summary>各模型的上下文窗口上限。</summary>
public static class ContextLimits
{
    /// <remarks>
    /// 只有确定是小窗口的机型才列进来，其余（含尚未出现的新型号）按 1M 估。
    /// 估错的方向很重要：分母估小了进度条会顶满，还会打印出「已用 992.9k / 200.0k」
    /// 这种自相矛盾的数字；估大了只是少报一点，不会骗人。
    /// macOS 版实测：claude-opus-4-8 单次上下文到过 992,897 token。
    /// </remarks>
    public static int For(string? model)
    {
        if (string.IsNullOrEmpty(model)) return 1_000_000;
        var m = model.ToLowerInvariant();
        if (m.Contains("haiku")) return 200_000;        // haiku 全系
        if (m.Contains("claude-3")) return 200_000;     // 3.x 全系
        // 4 系只有 4.5 及更早是 200k；4.6/4.7/4.8 与 5 系都是 1M
        if (m.Contains("opus-4-0") || m.Contains("opus-4-1") || m.Contains("opus-4-5")
            || m.Contains("sonnet-4-0") || m.Contains("sonnet-4-5")
            || m.StartsWith("claude-opus-4-2") || m.StartsWith("claude-sonnet-4-2"))
        {
            return 200_000;                             // 末两条是已退役的 claude-*-4-20250514
        }
        return 1_000_000;
    }
}

/// <summary>紧凑 token 数：468243 → "468.2k"</summary>
public static class Format
{
    public static string Tokens(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.0}M",
        >= 1_000 => $"{n / 1_000.0:0.0}k",
        _ => n.ToString(),
    };

    public static string Elapsed(DateTimeOffset? since)
    {
        if (since is null) return "";
        var secs = Math.Max(0, (int)(DateTimeOffset.Now - since.Value).TotalSeconds);
        if (secs < 60) return $"{secs} 秒";
        int m = secs / 60, s = secs % 60;
        return m < 60 ? $"{m} 分 {s} 秒" : $"{m / 60} 小时 {m % 60} 分";
    }

    public static string Ago(DateTimeOffset? date)
    {
        if (date is null) return "";
        var secs = Math.Max(0, (int)(DateTimeOffset.Now - date.Value).TotalSeconds);
        if (secs < 60) return "刚刚完成";
        var m = secs / 60;
        return m < 60 ? $"{m} 分钟前完成" : $"{m / 60} 小时前完成";
    }
}
