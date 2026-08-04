using Sundial.Core;
using System.Diagnostics;

Console.WriteLine("=== 1. Context-window mapping ===");
foreach (var (m, want) in new (string, int)[] {
    ("claude-opus-4-8", 1_000_000), ("claude-opus-4-7", 1_000_000),
    ("claude-sonnet-4-6", 1_000_000), ("claude-opus-5", 1_000_000),
    ("claude-fable-5", 1_000_000), ("claude-haiku-4-5-20251001", 200_000),
    ("claude-3-5-haiku-20241022", 200_000), ("claude-opus-4-5-20251101", 200_000),
    ("claude-sonnet-4-5-20250929", 200_000), ("claude-opus-4-1-20250805", 200_000),
    ("claude-opus-4-20250514", 200_000), ("claude-sonnet-4-20250514", 200_000) })
{
    var got = ContextLimits.For(m);
    Console.WriteLine($"  {(got == want ? "✓" : "✗")} {m,-30} → {got:N0}");
}

Console.WriteLine("\n=== 2. ActivityWatcher against the real registry and transcripts ===");
var w = new ActivityWatcher();
var sw = Stopwatch.StartNew();
w.Poll();
sw.Stop();
Console.WriteLine($"  first poll took {sw.ElapsedMilliseconds} ms (includes the cold deep scan)");
var sessions = w.Sessions;
Console.WriteLine($"  found {sessions.Count} live session(s)");
foreach (var s in sessions)
{
    var pct = s.CtxLimit > 0 ? $"{100.0 * s.CtxTokens / s.CtxLimit:F0}%" : "unknown";
    Console.WriteLine($"    · title \"{s.Title}\"");
    Console.WriteLine($"      busy={s.Busy} waiting={s.Waiting} background={s.Background} stalled={s.Stalled} unread={s.Unread}");
    Console.WriteLine($"      turn started={s.Since?.ToString("HH:mm:ss") ?? "(none)"}  elapsed={Format.Elapsed(s.Since)}");
    Console.WriteLine($"      context {Format.Tokens(s.CtxTokens)} / {Format.Tokens(s.CtxLimit)} = {pct}");
}
sw.Restart(); w.Poll(); sw.Stop();
Console.WriteLine($"  second poll took {sw.ElapsedMilliseconds} ms (should be much faster)");

Console.WriteLine("\n=== 3. Usage endpoint parsing (real shape, invented numbers) ===");
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
if (parsed is null) Console.WriteLine("  ✗ parse failed");
else
{
    foreach (var r in parsed.Value.Rows)
        Console.WriteLine($"  · {r.Label,-18} {r.Percent,4}%   resets {r.ResetAt?.ToLocalTime():MM-dd HH:mm}");
    Console.WriteLine($"  plan = \"{parsed.Value.Tier}\" (the endpoint has no such field; empty is correct)");
    var over = parsed.Value.Rows.FirstOrDefault(r => r.Percent > 100);
    Console.WriteLine(over is null ? "  ✗ the 106% overrun was clamped" : $"  ✓ overrun shown honestly: {over.Label} {over.Percent}%");
    var bogus = parsed.Value.Rows.Where(r => r.Label.Contains("amber") || r.Label.Contains("tangelo")
                                          || r.Label.Contains("extra")).ToList();
    Console.WriteLine(bogus.Count == 0 ? "  ✓ neither the code-name keys nor extra_usage were mistaken for limits" : $"  ✗ {bogus.Count} bogus row(s) slipped in");
}
