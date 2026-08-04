// Sundial (Windows edition) — usage API: parsing + fetch scheduling
//
// Ported function by function from the macOS version's Usage.swift. Every comment in the original
// that records a measured finding or the cause of a bug has been kept exactly as it was; those were
// earned the hard way and matter more than the code itself.
//
// Structural differences from the macOS version (see the individual comments): reading / refreshing /
// persisting the token are all gathered into ITokenSource, implemented by the Auth module. This file
// never touches the Keychain/DPAPI, and never fires an OAuth request of its own.

using System.Globalization;
using System.Text.Json;

namespace Sundial.Core;

/// <summary>
/// Token source. The real implementation lives in the Auth module (TokenStore / OAuthClient); this
/// only declares the contract.
/// The macOS version wrote "read the Keychain / work out whether it has expired / refresh / fall back
/// to the CLI credentials / remember that the user signed out deliberately" all inside UsageFetcher.
/// The Windows version moves that lot into the Auth module, leaving UsageFetcher with nothing but
/// fetching and scheduling.
/// </summary>
public interface ITokenSource
{
    /// <summary>
    /// Returns a usable access token; returns null when there is no usable token (this is the macOS
    /// version's "please sign in" branch).
    /// IsOwn=true means the token came from this program's own sign-in, false means we fell back to
    /// the Claude Code CLI's credentials — and that distinction matters: somebody else's credentials
    /// are not ours to invalidate.
    /// </summary>
    Task<(string Token, string? Tier, bool IsOwn)?> ResolveAsync(CancellationToken ct);

    /// <summary>Called when the server rejects the credentials. Only an explicit refusal from the server (400/401) is allowed to reach here.</summary>
    void Invalidate();

    // The four members below all come with a default implementation: the interface only ever grows, so
    // older implementations (including the stubs in the tests) still compile untouched — they simply
    // drop back to the degraded "none of these abilities" behaviour. The Auth module's TokenSource
    // provides every one of them.

    /// <summary>
    /// Whether that last <see cref="ResolveAsync"/> call already renewed the token once.
    /// This is justRefreshed from the macOS version's resolveToken return value: on a 401, if the
    /// token was only just swapped this round, the credentials really are dead — don't spin through
    /// another pointless refresh, declare the sign-in invalid straight away.
    /// </summary>
    bool LastResolveRefreshed => false;

    /// <summary>
    /// Why the most recent <see cref="ResolveAsync"/> returned null, used to pick the wording shown to
    /// the user; null = no distinction, treat everything as "not signed in". This is the CredError
    /// thrown by the macOS version's resolveToken.
    /// </summary>
    CredErrorKind? LastNoTokenReason => null;

    /// <summary>
    /// Renew the token once after a 401. true = a new token is now in place, false = there was no
    /// token to renew (or the user signed out part-way through the renewal, in which case the whole
    /// new token is written off).
    /// Network / server failures must still throw as usual, so the caller can sort them by "were the
    /// credentials actually refused" — swallowing the exception and returning false would turn a
    /// single dropped connection into a permanent sign-out.
    /// </summary>
    Task<bool> TryRenewAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// Called when the user refreshes by hand: lifts the block left behind by "reading the credential
    /// store failed last time".
    /// On macOS this rule was written for the Keychain authorisation prompt — the automatic poll must
    /// not retry, or a dialog would pop up every 60 seconds; on Windows a failed DPAPI decryption is
    /// the same story (and may come with a long block), so the rule is kept exactly as it was.
    /// </summary>
    void RetryBlockedRead() { }
}

/// <summary>
/// If an exception thrown by another module implements this interface, UsageFetcher can tell "the
/// server has explicitly refused the credentials" (RFC 6749 §5.2 invalid_grant / client
/// authentication failure, i.e. 400 and 401) apart from a network failure; this is
/// OAuthError.isCredentialRejection in the macOS version.
/// It runs fine without it: every exception is then treated as a retryable transient failure. The
/// bias has to lean this way — mistaking a network failure for expired credentials signs the user out
/// permanently, whereas a few extra retries only mean waiting a bit longer.
/// </summary>
/// <remarks>
/// The Auth module's OAuthException merely has a property of the same name; it never declares that it
/// implements this interface, and C# does not do structural matching, so UsageFetcher has to
/// recognise it a second time by its concrete type. Otherwise the "sign-in has expired" branch is
/// never reached, and even when the refresh token really is dead the display just keeps saying
/// "network temporarily unavailable" — which is precisely why that type check exists inside
/// <see cref="UsageFetcher"/>.
/// </remarks>
public interface ICredentialRejection
{
    bool IsCredentialRejection { get; }
}

// MARK: - Usage API parsing

public static class Usage
{
    /// <summary>Key name as given by the API → UI label + sort priority; returning null means this one is not displayed.</summary>
    public static (string Label, int Priority)? LabelFor(string key)
    {
        var k = key.ToLowerInvariant();
        if (k.Contains("five_hour") || k == "session") return ("5 hours", 0);
        if (k == "seven_day" || k == "weekly" || k == "weekly_all") return ("Weekly · all models", 1);
        if (k.Contains("fable")) return ("Weekly · Fable", 2);
        if (k.Contains("mythos")) return ("Weekly · Mythos", 2);
        if (k.Contains("opus")) return ("Weekly · Opus", 3);
        if (k.Contains("sonnet")) return ("Weekly · Sonnet", 4);
        if (k.Contains("cowork")) return ("Weekly · Cowork", 5);
        if (k.Contains("routine")) return ("Weekly · Routines", 6);
        if (k.Contains("extra") || k.Contains("overage")) return null; // extra paid-for usage, not displayed for now
        if (k.Contains("seven_day"))
        {
            var name = Capitalized(k.Replace("seven_day_", ""));
            return ($"Weekly · {name}", 7);
        }
        return null;
    }

    /// <summary>
    /// The counterpart of Swift's String.capitalized: the first letter of every "word" upper-case, the
    /// rest lower-case.
    /// Not TextInfo.ToTitleCase — its word-splitting rules depend on ICU, and Windows may be running
    /// with InvariantGlobalization switched on, which changes the behaviour; hard-coding it ourselves
    /// is the only way to be sure.
    /// </summary>
    /// <remarks>
    /// The word-splitting rule was measured by running Foundation on macOS, and it does not split on
    /// spaces: any character whose predecessor is not a letter counts as the start of a new word.
    /// Measured samples —
    /// "foo_bar"→"Foo_Bar", "opus-4-5"→"Opus-4-5", "opus4x"→"Opus4X",
    /// "20x_max"→"20X_Max", "a1b_c2d"→"A1B_C2D", "a  b"→"A  B".
    /// Splitting on spaces would give "Foo_bar" and "Opus4x", which does not match the original.
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

    /// <summary>Reset time: an ISO8601 string, or a timestamp in seconds/milliseconds.</summary>
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
        // The original used two ISO8601DateFormatters (first trying it with fractional seconds, then
        // without); .NET's TryParse swallows both forms, so one go is enough.
        // AssumeUniversal: the original's formatter simply failed on a string with no time zone;
        // here we choose to read it as UTC rather than local time — the server hands out UTC, and
        // guessing local would be off by several hours.
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
        {
            return d;
        }
        return null;
    }

    private static DateTimeOffset? FromEpoch(double n)
    {
        // Accept timestamps in either seconds or milliseconds
        var secs = n > 4_000_000_000 ? n / 1000 : n;
        // Don't let dirty data make AddSeconds throw an out-of-range exception; just treat it as having no reset time
        if (!double.IsFinite(secs) || secs < -62_135_596_800 || secs > 253_402_300_799) return null;
        return DateTimeOffset.UnixEpoch.AddSeconds(secs);
    }

    private static readonly string[] WeekdayZh = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    /// <summary>The compact reset time shown on the right-hand side of a limit row: "4h32m" / "Thu 14:00"</summary>
    public static string CompactReset(DateTimeOffset? d)
    {
        if (d is null) return "";
        var secs = (d.Value - DateTimeOffset.Now).TotalSeconds;
        if (secs <= 0) return "soon";
        if (secs < 24 * 3600)
        {
            int total = (int)secs, h = total / 3600, m = total % 3600 / 60;
            return h > 0 ? $"{h}h{m}m" : $"{m}m";
        }
        // The original was a DateFormatter with zh_CN and "EEE HH:mm". Here the Chinese weekday names
        // are hard-coded to avoid depending on ICU: Windows may be running with InvariantGlobalization
        // switched on, and then zh-CN degrades to English
        var local = d.Value.ToLocalTime();
        return $"{WeekdayZh[(int)local.DayOfWeek]} {local.Hour:00}:{local.Minute:00}";
    }

    public static string PrettyTier(string? raw)
    {
        // Pin it down to a non-null string before going any further: the nullable-reference-type flow
        // analysis cannot get inside a lambda, so leaving it as string? makes the closure below
        // report CS8602
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

    // "20x" comes first: match the longer one before the shorter one can steal the hit
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
        catch (JsonException) { return null; }   // the counterpart of Swift's try?
    }

    /// <summary>String entry point, so tests can feed in sample JSON directly.</summary>
    public static (List<UsageRow> Rows, string Tier)? ParseUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseRoot(doc.RootElement);
        }
        catch (JsonException) { return null; }
    }

    // Private: a JsonElement's lifetime is tied to its JsonDocument, so don't let it leak outside
    private static (List<UsageRow> Rows, string Tier)? ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var rows = new List<UsageRow>();

        void Consider(string key, JsonElement obj)
        {
            var mapped = LabelFor(key);
            if (mapped is null) return;
            // Top-level objects use utilization; the limits array uses percent. Both sets of field
            // names have to be recognised — recognise only one and a change of API version leaves the
            // whole panel blank
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

            // Not clamped to 100: when you are over the limit you should see "106%"; clamping would
            // only show it as exactly used up, which hides the fact that you have already gone over.
            // The upper bound of 999 is purely to stop dirty data from bursting the layout
            rows.Add(new UsageRow(
                mapped.Value.Label,
                (int)Math.Round(Math.Clamp(u2, 0, 999), MidpointRounding.AwayFromZero),
                reset,
                mapped.Value.Priority));
        }

        // Sort the top-level keys by name before iterating: Swift dictionaries in the original are
        // unordered, so without a fixed order, which entry a colliding label picks up would vary at
        // random. System.Text.Json does enumerate in document order (nothing random about it), but
        // that order comes from the server, so the sort is needed all the same — otherwise the server
        // reshuffles its fields and the number on screen changes
        foreach (var p in root.EnumerateObject()
                     .Where(p => p.Value.ValueKind == JsonValueKind.Object)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            Consider(p.Name, p.Value);
        }

        // The limits array: build the name out of kind + scope.model.display_name
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
                    if (!string.IsNullOrEmpty(name)) key = "seven_day_" + name;   // let LabelFor classify it
                }
                // The original also passed an activeFlag (is_active) here, but consider never used it, so it is left out
                Consider(key, item);
            }
        }

        // De-duplicate (on a name collision keep the one that appeared first), then sort by priority.
        // OrderBy is a stable sort: entries on the same priority (Fable / Mythos are both 2, say) keep
        // their first-come order and won't swap places at random the way Swift's unstable sort does
        var seen = new HashSet<string>(StringComparer.Ordinal);
        rows = rows.Where(r => seen.Add(r.Label)).OrderBy(r => r.Priority).ToList();
        if (rows.Count > 5) rows = rows.Take(5).ToList();

        // Plan name: this used to recognise nothing but the single key rate_limit_tier, and the API
        // had long since stopped returning it — the badge had in fact been empty the whole time and
        // nobody noticed. Now it recognises several of the common spellings, so it can still catch the
        // value if the API renames the field
        string? rawTier = null;
        foreach (var k in TierKeys)
        {
            if (!root.TryGetProperty(k, out var e)) continue;
            if (e.ValueKind == JsonValueKind.String)
            {
                var s = e.GetString();
                if (!string.IsNullOrEmpty(s)) { rawTier = s; break; }
                continue;                       // an empty string doesn't count as a hit, carry on to the next key
            }
            if (e.ValueKind == JsonValueKind.Object)   // the wrapped-in-one-more-level form is accepted too
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

    /// <summary>Fetch a string field; returns null when it is absent or not a string. An empty string
    /// is still returned as an empty string — the counterpart of Swift's as? String, where ?? only
    /// drops through to the next candidate key on nil.</summary>
    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}

// MARK: - Fetch scheduling

/// <summary>
/// Fetches from /api/oauth/usage on a timer and writes the result into PetModel.
/// Tick / ForceRefresh must be called on the UI thread (timer and UI events), the HTTP runs on the
/// thread pool, and writing back to the model is always posted back to the UI thread — the same
/// convention as the macOS version's main queue.
/// </summary>
public sealed class UsageFetcher : IDisposable
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const double NormalInterval = 60;   // seconds

    /// <summary>Callback fired when the data has changed (on the UI thread).</summary>
    public Action? OnUpdate;

    private readonly PetModel _model;
    private readonly ITokenSource _tokens;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _cts = new();

    private DateTimeOffset _nextFetchAt = DateTimeOffset.MinValue;   // = Date.distantPast
    private bool _inFlight;        // only one request allowed at a time
    private bool _forcePending;    // a manual refresh that arrived mid-request; run one more the moment this finishes

    // Only read and written on the background fetch thread (_inFlight guarantees only one fetch at a time)
    private int _refreshStreak;

    /// <param name="post">
    /// The means of queueing a callback onto the UI thread. Leave it out and the SynchronizationContext
    /// present at construction time is grabbed instead (Avalonia installs one on the UI thread);
    /// failing that the callback runs on the spot, which lets tests run through synchronously.
    /// The Core layer references no UI framework at all, so it cannot just use Dispatcher.
    /// </param>
    public UsageFetcher(PetModel model, ITokenSource tokens,
                        HttpClient? http = null, Action<Action>? post = null)
    {
        _model = model;
        _tokens = tokens;
        _ownsHttp = http is null;
        // A client we build ourselves gets 20 seconds; one handed in from outside keeps its own
        // Timeout, since every request is wrapped in a 20-second CTS as a backstop anyway (this is the
        // outer of the two in the original's "per-request 15s + semaphore 20s")
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

    // Tick / ForceRefresh / Finish are only called on the UI thread (timer and UI events)
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

    /// <summary>A refresh the user asked for: also the only way in to retrying a read of the credential store (the automatic poll doesn't retry, so as not to pester).</summary>
    public void ForceRefresh()
    {
        // This is the two lines in the macOS version's forceRefresh that reset keychainBlocked. The
        // rule came out of the Keychain authorisation prompt; on Windows that becomes a failed DPAPI
        // decryption, which equally should not be retried every 60 seconds, so the rule is kept
        // exactly as it was — only the reset itself has moved into ITokenSource (that's the side that
        // knows what blocked it)
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
            _model.NeedsLogin = false;   // getting here means the token was obtained, so this is a failure we can retry automatically
            Finish(retryAfter);
            OnUpdate?.Invoke();
        });
    }

    /// <summary>Give up on this fetch (we are shutting down), but the tidy-up still has to happen or _inFlight stays stuck forever.</summary>
    private void Abandon()
    {
        _post(() =>
        {
            _model.Loading = false;
            Finish(0);
            OnUpdate?.Invoke();
        });
    }

    /// <summary>No usable token: go into the awaiting-sign-in state and stop retrying so often.</summary>
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
    /// "The server has explicitly refused these credentials" — the one and only case in which the
    /// token may be invalidated.
    /// OAuthException has to be listed separately and recognised by its concrete type: it has a
    /// property of the same name but never declares that it implements
    /// <see cref="ICredentialRejection"/>, so going by the interface alone this branch is always
    /// false, and the upshot is that even when the refresh token really has expired all you get is
    /// "network temporarily unavailable", never a prompt to sign in again.
    /// </summary>
    private static bool IsCredentialRejection(Exception e) => e switch
    {
        OAuthException oe => oe.IsCredentialRejection,
        ICredentialRejection c => c.IsCredentialRejection,
        _ => false,   // an exception that didn't announce itself counts as transient: better to wait longer than to sign the user out by mistake
    };

    private async Task RunFetchAsync()
    {
        try
        {
            await FetchAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Backstop: every exit from FetchAsync tidies up after itself, so getting here means an
            // exception we didn't think of. Better to tidy up one time too many than to let _inFlight
            // stay stuck — that would stop the pet ever refreshing again (this is exactly the pit the
            // original's abandon() comment warns about).
            // An exception during shutdown needn't be written back to the model: by then Tick has
            // already short-circuited, so a stuck _inFlight doesn't matter
            if (_cts.IsCancellationRequested) return;
            Fail($"Internal error: {e.Message}", sleep: true, retryAfter: 300);
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
            // Only tidy up silently if we really were shut down. Without this when clause, the
            // TaskCanceledException thrown by an HttpClient timeout would fall in here too → Abandon
            // sets retryAfter to 0 → the next timer tick fires straight away, and it turns into a hot
            // loop using the timeout as its metronome
            Abandon();
            return;
        }
        catch (Exception e) when (IsCredentialRejection(e))
        {
            // only sign out when the server has explicitly refused the credentials
            _tokens.Invalidate();
            NeedLogin("Sign-in expired\nDouble-click me to sign in again");
            return;
        }
        catch (Exception)
        {
            // Network / rate limiting / 5xx, and any exception that didn't announce itself, all keep
            // the token and retry later.
            // Never Invalidate here: one dropped connection would sign the user out permanently, and
            // that is the most expensive kind of bug there is
            Fail("Network unavailable — retrying shortly", sleep: true, retryAfter: 120);
            return;
        }

        // Grab it right after ResolveAsync rather than reading it in the 401 branch: by then a whole HTTP round trip has gone by
        var justRefreshed = _tokens.LastResolveRefreshed;

        if (resolved is not { } cred)
        {
            // No usable token: never signed in, the user signed out deliberately, or the CLI
            // credentials are missing or expired — the lot ends up here.
            // "reading the credential store was refused" needs saying separately: that one is
            // retryable, not really a signed-out state, and the wording has to tell the user to try
            // again rather than sending them through the whole sign-in once more (this is the
            // original's keychainDenied branch)
            NeedLogin(_tokens.LastNoTokenReason == CredErrorKind.StoreDenied
                ? "Credential read refused\nDouble-click to retry or sign in"
                : "Not signed in\nDouble-click to sign in");
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
            // a timeout is the only possibility left (both HttpClient.Timeout and the CTS surface as this exception)
            Fail("Request timed out — retrying shortly", sleep: true, retryAfter: 90);
            return;
        }
        catch (HttpRequestException)
        {
            Fail("Network unavailable — retrying shortly", sleep: true, retryAfter: 90);
            return;
        }
        catch (IOException)
        {
            // the stream broke while reading the response body
            Fail("Network unavailable — retrying shortly", sleep: true, retryAfter: 90);
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
                    Fail("The endpoint returned data I could not read\n(Anthropic may have changed the format)", sleep: true, retryAfter: 300);
                    return;
                }
                _post(() =>
                {
                    _model.Rows = ok.Rows;
                    // All three branches must assign: miss the last one and after switching accounts
                    // the old plan name keeps hanging around next to the new account's numbers
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
                // Token refused: try a refresh straight away. If this round has already refreshed
                // (justRefreshed), don't refresh again — a brand-new token being refused immediately
                // means the credentials really are dead and another refresh is just spinning; on top
                // of that consecutive refreshes are capped at 3, to avoid going round forever
                if (cred.IsOwn && !justRefreshed && _refreshStreak < 3)
                {
                    bool renewed;
                    try
                    {
                        // Use ct here, not the timeout above: those 20 seconds belonged to the usage
                        // request and have most likely been burnt through by now, so renewing with it
                        // amounts to a guaranteed timeout
                        renewed = await _tokens.TryRenewAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        Abandon();
                        return;
                    }
                    catch (Exception e) when (!IsCredentialRejection(e))
                    {
                        // Dropped connection / rate limiting / 5xx during renewal: don't touch a single character of the token, wait for the next round
                        Fail("Network unavailable — retrying shortly", sleep: true, retryAfter: 120);
                        return;
                    }
                    catch (Exception)
                    {
                        // The server has explicitly refused the refresh token — this is the one case that really is expired
                        _tokens.Invalidate();
                        NeedLogin("Sign-in expired\nDouble-click me to sign in again");
                        return;
                    }

                    if (renewed)
                    {
                        _refreshStreak++;
                        Fail("Renewing — retrying now", sleep: false,
                             retryAfter: new[] { 30.0, 120.0, 600.0 }[_refreshStreak - 1]);
                    }
                    else
                    {
                        // false arrives from two directions: there was no token to renew at all, or
                        // the user signed out part-way through the renewal. The original went to
                        // signOut+needLogin and abandon respectively; the interface cannot tell them
                        // apart, so both take the former.
                        // abandon is not an option: it sets retryAfter to 0, and against the first of
                        // those two directions that becomes a "401 → retry immediately → 401" hot loop
                        _tokens.Invalidate();
                        NeedLogin("Sign-in expired\nDouble-click me to sign in again");
                    }
                }
                else if (cred.IsOwn)
                {
                    _tokens.Invalidate();
                    NeedLogin("Sign-in expired\nDouble-click me to sign in again");
                }
                else
                {
                    // These are the Claude Code CLI's credentials, not ones we issued, so we have no right to invalidate them
                    NeedLogin("Not signed in\nDouble-click to sign in");
                }
                return;
            case 403:
                // Insufficient permissions (the scope doesn't line up, and the like); a refresh won't fix it, so don't spin
                Fail("Endpoint refused access (403)\nTry signing in again", sleep: true, retryAfter: 600);
                return;
            case 429:
                Fail("Endpoint rate-limited — retrying shortly", sleep: false, retryAfter: 300);
                return;
            default:
                Fail($"Endpoint error ({status}) — retrying shortly", sleep: true, retryAfter: 180);
                return;
        }
    }

    public void Dispose()
    {
        // Cancel only, don't Dispose: an in-flight FetchAsync still needs _cts.Token to build a linked
        // token source, and disposing it now would make that side throw ObjectDisposedException.
        // Leaving it to the GC is fine
        _cts.Cancel();
        if (_ownsHttp) _http.Dispose();
    }
}
