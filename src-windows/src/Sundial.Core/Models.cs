// Sundial (Windows version) — shared data models
//
// This file is the contract between the modules: pin it down first, then implement separately, so nobody
// ends up writing their own version of the same type.
// The semantics line up strictly with the macOS version's Model.swift / Activity.swift; change anything
// here and both sides have to be changed together.

using System.Globalization;

namespace Sundial.Core;

/// <summary>A single usage limit.</summary>
/// <param name="Percent">Percentage used. <b>May be greater than 100</b> (over the limit); clamp it to one
/// full turn yourself when drawing the ring, but show the number truthfully, otherwise there's no telling
/// by how much it has been exceeded.</param>
public sealed record UsageRow(string Label, int Percent, DateTimeOffset? ResetAt, int Priority);

/// <summary>The live state of one Claude Code session.</summary>
public sealed record SessionActivity
{
    public required string Id { get; init; }          // sessionId
    public string Title { get; init; } = "";
    public bool Busy { get; init; }
    public bool Waiting { get; init; }                // has put up choices and is waiting for the user to pick
    public DateTimeOffset? Since { get; init; }       // when this round started, used for timing
    public bool Unread { get; init; }                 // finished, but the user hasn't looked at it yet
    public DateTimeOffset? FinishedAt { get; init; }
    public int CtxTokens { get; init; }               // context tokens used
    public int CtxLimit { get; init; }                // context ceiling, 0 = unknown
    public bool Background { get; init; }             // the main turn has finished, background agents are still running
    public bool Stalled { get; init; }                // no new records for a long time, state unknown (≠ finished)
}

/// <summary>All the state the interface reads. Only ever modified on the UI thread.</summary>
public sealed class PetModel
{
    public const int MaxBlocks = 4;

    public List<UsageRow> Rows { get; set; } = new();
    public string Tier { get; set; } = "";
    public DateTimeOffset? LastFetch { get; set; }
    public string? ErrorMsg { get; set; }              // set to the wording when there's an error; cleared once it succeeds
    public bool Asleep { get; set; }                   // the pet sleeps when no data can be fetched
    public bool Loading { get; set; } = true;
    public bool NeedsLogin { get; set; }               // no usable token, waiting for the user to log in
    public List<SessionActivity> Sessions { get; set; } = new();
    public bool Hovered { get; set; }
    public bool DetailsPinned { get; set; }

    /// <summary>Folded down to just the sun and kept there. Unlike the ordinary folded state this
    /// one overrides hover — otherwise the pointer would pop the card straight back open and the
    /// button would appear to do nothing.</summary>
    public bool Minimised { get; set; }

    public bool AnyBusy => Sessions.Any(s => s.Busy);

    /// <summary>The ones currently running + the ones that have finished but haven't been looked at. Unread entries stay put until they are clicked away or that session starts working again.</summary>
    public IReadOnlyList<SessionActivity> VisibleSessions =>
        Sessions.Where(s => s.Busy || s.Unread).Take(MaxBlocks).ToList();

    public int MaxPercent => Rows.Count == 0 ? 0 : Rows.Max(r => r.Percent);

    /// <summary>
    /// Two gauges: left = the 5 hour one; right = whichever weekly limit is the most used.
    /// The right-hand one takes the "tightest" rather than one fixed row — when some model's own weekly
    /// limit is tighter, that is the one that has to be shown, otherwise it misleads.
    /// </summary>
    public (UsageRow? Outer, UsageRow? Inner) RingRows
    {
        get
        {
            var outer = Rows.FirstOrDefault(r => r.Label.Contains("5 hours")) ?? Rows.FirstOrDefault();
            var weeklies = Rows.Where(r => r.Label.StartsWith("Weekly")).ToList();
            var inner = weeklies.Count > 0
                ? weeklies.Aggregate((a, b) => b.Percent > a.Percent ? b : a)
                : Rows.FirstOrDefault(r => r.Label != outer?.Label);
            return (outer, inner);
        }
    }
}

/// <summary>The context window ceiling for each model.</summary>
public static class ContextLimits
{
    /// <remarks>
    /// Only models known for certain to have a small window are listed here; everything else (including
    /// new models that don't exist yet) is estimated at 1M.
    /// The direction in which the estimate is wrong matters: guess the denominator too small and the
    /// progress bar pins at full, and it prints self-contradictory numbers like "992.9k of 200.0k used";
    /// guess too large and it merely under-reports a little, which doesn't deceive anyone.
    /// Measured on the macOS version: claude-opus-4-8 reached 992,897 tokens of context in a single go.
    /// </remarks>
    public static int For(string? model)
    {
        if (string.IsNullOrEmpty(model)) return 1_000_000;
        var m = model.ToLowerInvariant();
        if (m.Contains("haiku")) return 200_000;        // the whole haiku line
        if (m.Contains("claude-3")) return 200_000;     // the whole 3.x line
        // In the 4 series only 4.5 and earlier are 200k; 4.6/4.7/4.8 and the 5 series are all 1M
        if (m.Contains("opus-4-0") || m.Contains("opus-4-1") || m.Contains("opus-4-5")
            || m.Contains("sonnet-4-0") || m.Contains("sonnet-4-5")
            || m.StartsWith("claude-opus-4-2") || m.StartsWith("claude-sonnet-4-2"))
        {
            return 200_000;                             // the last two entries are the retired claude-*-4-20250514
        }
        return 1_000_000;
    }
}

/// <summary>Compact token counts: 468243 → "468.2k"</summary>
public static class Format
{
    /// <summary>
    /// InvariantCulture is deliberate. Plain interpolation follows the system locale, so a German or
    /// French machine renders "823,9k / 1,0M" — a comma where the interface everywhere else uses a
    /// full stop. The macOS side never had this because it builds the string by hand.
    /// </summary>
    public static string Tokens(int n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    public static string Elapsed(DateTimeOffset? since)
    {
        if (since is null) return "";
        var secs = Math.Max(0, (int)(DateTimeOffset.Now - since.Value).TotalSeconds);
        if (secs < 60) return $"{secs}s";
        int m = secs / 60, s = secs % 60;
        return m < 60 ? $"{m}m {s}s" : $"{m / 60}h {m % 60}m";
    }

    public static string Ago(DateTimeOffset? date)
    {
        if (date is null) return "";
        var secs = Math.Max(0, (int)(DateTimeOffset.Now - date.Value).TotalSeconds);
        if (secs < 60) return "just now";
        var m = secs / 60;
        return m < 60 ? $"{m} min ago" : $"{m / 60} h ago";
    }
}
