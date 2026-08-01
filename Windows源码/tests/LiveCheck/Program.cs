using Sundial.Core;
using System.Diagnostics;

Console.WriteLine("=== 1. 上下文窗口映射 ===");
foreach (var (m, want) in new (string, int)[] {
    ("claude-opus-4-8", 1_000_000), ("claude-opus-4-7", 1_000_000),
    ("claude-sonnet-4-6", 1_000_000), ("claude-opus-5", 1_000_000),
    ("claude-fable-5", 1_000_000), ("claude-haiku-4-5-20251001", 200_000),
    ("claude-3-5-haiku-20241022", 200_000), ("claude-opus-4-5-20251101", 200_000),
    ("claude-sonnet-4-5-20250929", 200_000), ("claude-opus-4-1-20250805", 200_000),
    ("claude-opus-4-20250514", 200_000), ("claude-sonnet-4-20250514", 200_000) })
{
    var got = ContextLimits.For(m);
    Console.WriteLine($"  {(got == want ? "✓" : "✗ 错")} {m,-30} → {got:N0}");
}

Console.WriteLine("\n=== 2. 用真实注册表 + 会话记录跑 ActivityWatcher ===");
var w = new ActivityWatcher();
var sw = Stopwatch.StartNew();
w.Poll();
sw.Stop();
Console.WriteLine($"  首次 Poll 耗时 {sw.ElapsedMilliseconds} ms（含冷启动深扫）");
var sessions = w.Sessions;
Console.WriteLine($"  发现 {sessions.Count} 个活跃会话");
foreach (var s in sessions)
{
    var pct = s.CtxLimit > 0 ? $"{100.0 * s.CtxTokens / s.CtxLimit:F0}%" : "未知";
    Console.WriteLine($"    · 标题「{s.Title}」");
    Console.WriteLine($"      忙={s.Busy} 等待={s.Waiting} 后台={s.Background} 失联={s.Stalled} 未读={s.Unread}");
    Console.WriteLine($"      本轮起点={s.Since?.ToString("HH:mm:ss") ?? "(无)"}  已用时={Format.Elapsed(s.Since)}");
    Console.WriteLine($"      上下文 {Format.Tokens(s.CtxTokens)} / {Format.Tokens(s.CtxLimit)} = {pct}");
}
sw.Restart(); w.Poll(); sw.Stop();
Console.WriteLine($"  第二次 Poll 耗时 {sw.ElapsedMilliseconds} ms（应该快很多）");

Console.WriteLine("\n=== 3. 用量接口解析（真实返回结构，数值是编的） ===");
var json = """
{"five_hour":{"limit_dollars":50,"remaining_dollars":32,"resets_at":"2026-07-31T16:00:00Z","used_dollars":18,"utilization":36},
 "seven_day":{"utilization":31,"resets_at":"2026-08-04T06:00:00Z"},
 "seven_day_opus":null,"seven_day_sonnet":null,"extra_usage":{"utilization":99},
 "amber_ladder":{"utilization":77},"tangelo":{"utilization":88},
 "limits":[{"kind":"session","percent":36,"is_active":0,"resets_at":"2026-07-31T16:00:00Z"},
           {"kind":"weekly_all","percent":31,"is_active":0,"resets_at":"2026-08-04T06:00:00Z"},
           {"kind":"weekly_scoped","percent":106,"is_active":1,"resets_at":"2026-08-04T05:59:00Z",
            "scope":{"model":{"display_name":"Fable"}}}]}
""";
var parsed = Usage.ParseUsage(json);
if (parsed is null) Console.WriteLine("  ✗ 解析失败");
else
{
    foreach (var r in parsed.Value.Rows)
        Console.WriteLine($"  · {r.Label,-18} {r.Percent,4}%   重置 {r.ResetAt?.ToLocalTime():MM-dd HH:mm}");
    Console.WriteLine($"  套餐名 = 「{parsed.Value.Tier}」（接口没有这个字段，空是对的）");
    var over = parsed.Value.Rows.FirstOrDefault(r => r.Percent > 100);
    Console.WriteLine(over is null ? "  ✗ 超限的 106% 被夹掉了" : $"  ✓ 超限如实显示：{over.Label} {over.Percent}%");
    var bogus = parsed.Value.Rows.Where(r => r.Label.Contains("amber") || r.Label.Contains("tangelo")
                                          || r.Label.Contains("额外")).ToList();
    Console.WriteLine(bogus.Count == 0 ? "  ✓ 代号键与 extra_usage 都没被误认成限额" : $"  ✗ 混进了 {bogus.Count} 条");
}
