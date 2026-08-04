// Sundial (Windows edition) — monitoring Claude Code session activity
//
// Ported from Activity.swift in the macOS version. The measured numbers in the comments
// (1.35MB, 348 of 349 turns landing late, a median of 112 seconds, p95≈37 seconds…) are all
// real observations taken from the macOS version and carried over verbatim — they are the
// only justification for every "looks like overkill" bit of code in here, and once you delete
// them nobody will know why it was written this way.
//
// Data sources:
//  1) <user directory>\.claude\sessions\*.json — the registry of running sessions (pid + sessionId + title)
//  2) <user directory>\.claude\projects\<project>\<sessionId>.jsonl — tail-read only, to decide busy/idle and where the turn started
// Only the type / stop_reason / timestamp / title fields are looked at; the body of the conversation is never read.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sundial.Core;

public sealed class ActivityWatcher
{
    /// <summary>
    /// Parse state for a single session. C# has nothing like Swift's inout structs, so we stuff
    /// a mutable class into the dictionary and let ParseTail rewrite it in place.
    /// </summary>
    private sealed class FState
    {
        public long Size;
        public DateTimeOffset Mtime = DateTimeOffset.MinValue;
        public string CustomTitle = "";
        public string AiTitle = "";
        public bool Busy;
        public bool PendingTool;                    // Waiting on a tool to return, so a longer silence is allowed
        public bool Waiting;                        // The last entry is an AskUserQuestion, waiting for the user to pick
        public DateTimeOffset? Since;
        public bool Unread;
        public DateTimeOffset? FinishedAt;
        public int CtxTokens;
        public int CtxLimit;
        public bool Background;                     // The main turn has ended but a background agent is still running
        public bool Stalled;                        // Timed out with nothing happening — only out of contact, it does not mean it finished
        public DateTimeOffset? BgSince;
        public DateTimeOffset BgProbedAt = DateTimeOffset.MinValue;  // When the background directory was last scanned
        public DateTimeOffset? BgNewest;            // The newest write time seen by the last scan
        public int BgStaleHits;                     // How many probes in a row found the background quiet

        public string Title => CustomTitle.Length == 0 ? AiTitle : CustomTitle;
    }

    private sealed record LiveSession(string Id, string Name, DateTimeOffset Started);

    // Paths are always built from UserProfile. That holds on both Windows and macOS, which is
    // deliberate — the pure logic layer has to be able to run its tests straight on a Mac.
    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string SessionsDir => Path.Combine(Home, ".claude", "sessions");
    private static string ProjectsDir => Path.Combine(Home, ".claude", "projects");

    private const long TailBytes = 512 * 1024;
    private const long DeepBytes = 8 * 1024 * 1024;     // The deep-scan window used on a cold start

    // How long a background transcript can sit still before we call it stopped. Measured gaps
    // between consecutive writes from the same background agent: p95≈37 seconds, p99≈136 seconds.
    // The earlier value of 25 seconds kept deciding, part-way through a single run, that it had
    // "finished" — firing false notifications and resetting the timer.
    private const double BgFresh = 90;
    private const double UnreadExpiry = 600;            // An unread marker hangs around for 10 minutes at most
    private const double StaleAfter = 300;
    private const double ToolStaleAfter = 900;          // Bash caps a single call at 600 seconds; this leaves headroom for a retry

    private const byte Nl = (byte)'\n';

    private static readonly string[] BadPrefixes =
    {
        "<local-command", "<command-", "Caveat:", "<task-notification", "<system-reminder",
    };

    // _states is touched only by Poll (the background thread), so it takes no lock;
    // _readRequests and _sessions cross threads, so they must go through _lock.
    private Dictionary<string, FState> _states = new();
    private readonly object _lock = new();
    private readonly HashSet<string> _readRequests = new();   // The UI thread drops "mark as read" clicks in here
    private IReadOnlyList<SessionActivity> _sessions = Array.Empty<SessionActivity>();

    /// <summary>Read by the UI thread; Poll swaps it out wholesale on the background thread, so what you read is always one round's complete snapshot.</summary>
    public IReadOnlyList<SessionActivity> Sessions
    {
        get { lock (_lock) { return _sessions; } }
    }

    /// <summary>Called from the UI thread: mark a session as read.</summary>
    public void MarkRead(string id)
    {
        lock (_lock) { _readRequests.Add(id); }
    }

    /// <summary>Lift the "already read" suppression when a session starts running again, otherwise it would never notify you again once it next finishes.</summary>
    private void ClearRead(string id)
    {
        lock (_lock) { _readRequests.Remove(id); }
    }

    // MARK: Registry

    /// <remarks>
    /// pids get recycled by the system. Going purely by whether the pid exists resurrects
    /// long-finished sessions as ghost tiles — all it takes is one completely unrelated new
    /// process happening to land on the same pid.
    /// The registry records procStart, and only by comparing against the process's real start
    /// time can we confirm it is the same process.
    ///
    /// procStart is the output of <c>LC_ALL=C TZ=UTC ps -o lstart=</c>, shaped like
    /// "Fri Jul 31 08:45:53 2026", and it is <b>UTC, not local time</b>. When the day of the
    /// month is a single digit, ps pads it out with two spaces ("Fri Jul  4 ..."), so squash
    /// runs of spaces down to one before parsing.
    /// Tolerance is 1.5 seconds — ps is only accurate to the second.
    ///
    /// Whenever the start time cannot be obtained (not enough permissions), always let it
    /// through: better to show one tile too many than to wrongly kill off a running session.
    /// </remarks>
    private static bool IsSameProcess(int pid, string? procStart)
    {
        Process proc;
        try
        {
            proc = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // "there is no such pid on the system" — .NET throws this one only when the process
            // cannot be found, matching the branch on the Swift side where kill(pid,0) returns
            // ESRCH: a stale registry file, skip it.
            return false;
        }
        catch (Exception)
        {
            // Any other exception means "cannot look it up", not "does not exist". The Swift
            // version treats EPERM from kill (someone else's process, no permission) as still
            // alive, and this has to go the same way: with a blanket return of false, one
            // unanticipated exception on Windows would be enough to make every session vanish
            // at once (better to show one tile too many than to wrongly wipe out a whole batch
            // of running sessions).
            return true;
        }

        using (proc)
        {
            try
            {
                if (proc.HasExited) return false;
            }
            catch (Exception)
            {
                // If the exit status cannot be read, don't take it as dead; carry on and compare start times
            }

            if (string.IsNullOrEmpty(procStart)) return true;   // Older versions lack this field, let it through

            var norm = string.Join(' ', procStart.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (!DateTime.TryParseExact(norm, "ddd MMM d HH:mm:ss yyyy",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var want))
            {
                return true;            // Can't make sense of the format, so don't kill it off by mistake
            }

            DateTime got;
            try
            {
                got = proc.StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                return true;            // No permission to read someone else's process: let it through
            }
            return Math.Abs((got - want).TotalSeconds) < 1.5;
        }
    }

    /// <remarks>
    /// File.ReadAllBytes cannot be used: it opens with FileShare.Read, while Claude Code is
    /// holding a write handle on the registry file. On macOS that does not matter (the locks are
    /// only advisory), but on Windows it runs straight into a sharing violation and throws
    /// IOException, so that round of Poll treats the whole session as non-existent — and the tile
    /// flickers for no apparent reason.
    /// Grant the full ReadWrite | Delete share flags, just as ParseTail does.
    /// </remarks>
    private static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static List<LiveSession> LiveSessions()
    {
        var result = new List<LiveSession>();
        FileInfo[] files;
        try
        {
            var d = new DirectoryInfo(SessionsDir);
            if (!d.Exists) return result;
            files = d.GetFiles();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var f in files)
        {
            if (!string.Equals(f.Extension, ".json", StringComparison.OrdinalIgnoreCase)) continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(ReadAllBytesShared(f.FullName)));
            }
            catch (Exception)
            {
                continue;               // Being written to right now, or only half of it has landed
            }
            using (doc)
            {
                var o = doc.RootElement;
                if (o.ValueKind != JsonValueKind.Object) continue;
                var sid = Str(o, "sessionId");
                if (string.IsNullOrEmpty(sid)) continue;
                if (!TryInt(o, "pid", out var pid)) continue;
                // Is the process still around, and is it genuinely the one from back then? (a stale file is not a live session)
                if (!IsSameProcess(pid, Str(o, "procStart"))) continue;
                result.Add(new LiveSession(sid, Str(o, "name") ?? "", UnixMillis(o, "startedAt")));
            }
        }
        return result;
    }

    /// <summary>sessionId -&gt; (session transcript file, last write time, size in bytes)</summary>
    private static Dictionary<string, (string Path, DateTimeOffset Mtime, long Size)> TranscriptIndex()
    {
        var map = new Dictionary<string, (string Path, DateTimeOffset Mtime, long Size)>();
        DirectoryInfo[] dirs;
        try
        {
            var root = new DirectoryInfo(ProjectsDir);
            if (!root.Exists) return map;
            dirs = root.GetDirectories();
        }
        catch (Exception)
        {
            return map;
        }

        foreach (var dir in dirs)
        {
            FileInfo[] files;
            try { files = dir.GetFiles(); }
            catch (Exception) { continue; }

            foreach (var f in files)
            {
                // Don't filter with a "*.jsonl" wildcard: on Windows the wildcard also matches
                // 8.3 short names, so temporary files with a similar extension can get scooped
                // up. Comparing the extension explicitly is the safest thing.
                if (!string.Equals(f.Extension, ".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                DateTimeOffset m;
                long len;
                try
                {
                    m = f.LastWriteTimeUtc;
                    len = f.Length;
                }
                catch (Exception)
                {
                    continue;           // The file went away halfway through the enumeration
                }
                map[Path.GetFileNameWithoutExtension(f.Name)] = (f.FullName, m, len);
            }
        }
        return map;
    }

    /// <remarks>
    /// Whatever you do, don't use the <c>SearchOption.AllDirectories</c> overload: internally it
    /// wraps EnumerationOptions.CompatibleRecursive, and in that preset IgnoreInaccessible = false
    /// and AttributesToSkip = 0. One subdirectory it cannot read is then enough to throw the
    /// exception out of the traversal, the whole scan is cut short on the spot, and you are left
    /// with half a result — background activity is under-reported and a running session gets
    /// judged to have "finished".
    /// Swift's FileManager.enumerator skips that entry and carries on scanning, so here we hand
    /// it an explicit set of options that lines up with it.
    ///
    /// AttributesToSkip = Hidden corresponds to Swift's <c>.skipsHiddenFiles</c>: on Windows,
    /// hidden is a file attribute rather than a naming convention, so merely checking whether the
    /// name starts with "." does not stop it; conversely, on Unix .NET adds the Hidden attribute
    /// to names beginning with a dot, so this one condition covers both sides. The name check
    /// stays on as a second line of defence.
    /// </remarks>
    private static readonly EnumerationOptions BgScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden,
    };

    /// <summary>
    /// The transcripts of background subagents/workflows are written under
    /// &lt;directory holding the transcript file&gt;\&lt;session ID&gt;\..., and the main transcript never moves.
    /// Take the newest write time under that directory to work out "the main turn has ended but the background is still running".
    /// </summary>
    private static DateTimeOffset? BackgroundActivity(string sessionId, string transcript,
                                                      DateTimeOffset cutoff)
    {
        var parent = Path.GetDirectoryName(transcript);
        if (string.IsNullOrEmpty(parent)) return null;
        var dir = Path.Combine(parent, sessionId);
        if (!Directory.Exists(dir)) return null;

        // No "stop once you have seen N of them" — the enumeration order is not fixed, so it may
        // miss precisely the newest file, and a running session then gets mistaken for an idle one.
        DateTimeOffset? newest = null;
        var now = DateTimeOffset.Now;
        try
        {
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", BgScanOptions))
            {
                var name = fsi.Name;
                if (name.Length > 0 && name[0] == '.') continue;   // Hidden entries (.DS_Store and the like) do not count
                DateTimeOffset m;
                // The enumeration has already brought the metadata back, so reading the attribute
                // no longer costs a system call — this matches the Swift side, which reads the
                // contentModificationDate cached by the enumerator directly
                try { m = fsi.LastWriteTimeUtc; }
                catch (Exception) { continue; }
                // Anything written before the main turn ended (synchronous subagents, tool results)
                // is already represented by the main transcript; counting it a second time would
                // make a session that has only just finished look like "the background is still
                // running" and would suppress the completion notification
                if (m <= cutoff) continue;
                if (newest is null || m > newest.Value) newest = m;
                if ((now - m).TotalSeconds < 3) return m;   // Bail out early only when it is clearly fresh; otherwise finish the scan and take the genuinely newest
            }
        }
        catch (Exception)
        {
            // The directory was deleted: make do with the newest value scanned so far, rather than bringing the whole round of Poll down
        }
        return newest;
    }

    // MARK: Polling

    /// <summary>Called from the background thread.</summary>
    public void Poll()
    {
        HashSet<string> reads;
        lock (_lock)
        {
            reads = new HashSet<string>(_readRequests);
            _readRequests.Clear();
        }

        var live = LiveSessions();
        if (live.Count == 0)
        {
            _states.Clear();
            lock (_lock) { _sessions = Array.Empty<SessionActivity>(); }
            return;
        }
        var index = TranscriptIndex();

        var result = new List<SessionActivity>();
        var newStates = new Dictionary<string, FState>();
        foreach (var s in live)
        {
            var st = _states.TryGetValue(s.Id, out var prev) ? prev : new FState();
            if (reads.Contains(s.Id))
            {
                st.Unread = false;
                st.FinishedAt = null;
            }

            if (index.TryGetValue(s.Id, out var t))
            {
                var (path, mtime, size) = t;
                if (st.Mtime != mtime || st.Size != size)
                {
                    var wasBusy = st.Busy;
                    // Read a bit more the first time we see this session, to make sure the "when the user last spoke" anchor can be found
                    var firstSight = st.Mtime == DateTimeOffset.MinValue;
                    ParseTail(path, st, firstSight ? DeepBytes : TailBytes);
                    st.Mtime = mtime;
                    st.Size = size;
                    st.Background = false;   // The main transcript moved, busy has been re-judged, the old background flag is void
                    st.Stalled = false;      // The transcript moved again, so revoke "not responding"
                    // busy -> idle: this round produced a result, so keep it flagged unread until the user has looked at it
                    if (wasBusy && !st.Busy && !reads.Contains(s.Id))
                    {
                        st.Unread = true;
                        // Use the transcript file's own write time, not "the moment I noticed it".
                        // Otherwise a turn that ended in the night would show up as "just finished"
                        // when the machine is switched on in the morning
                        st.FinishedAt = mtime;
                    }
                    if (st.Busy)
                    {
                        st.Unread = false;
                        st.FinishedAt = null;
                        ClearRead(s.Id);     // Off and running again: it has to be able to notify afresh when it next finishes
                    }
                }

                // No time limit while waiting for the user to choose — a person may not come back for ages
                var limit = st.PendingTool ? ToolStaleAfter : StaleAfter;
                if (st.Busy && !st.Waiting && (DateTimeOffset.Now - mtime).TotalSeconds > limit)
                {
                    st.Busy = false;
                    st.Since = null;
                    st.PendingTool = false;
                    // A timeout only says we lost contact, it does not mean it finished. This used
                    // to quietly wipe the tile away and send the sun off to sleep while Claude
                    // might still have been thinking — now it says "not responding" outright
                    st.Stalled = true;
                    if (!reads.Contains(s.Id))
                    {
                        st.Unread = true;
                        st.FinishedAt = mtime;
                    }
                }

                // The main turn has ended, but a background subagent/workflow is still writing to
                // its transcript = still hard at work.
                // When Background is true, the Busy we are holding was set by this very code on the
                // previous round, so it cannot serve as evidence that "the main turn is busy"; we
                // have to probe again to see whether the background is still alive
                if (!st.Busy || st.Background)
                {
                    // Walking the directory is on the expensive side, so reuse the previous result
                    // for 3 seconds (against a BgFresh of 90 seconds the error is negligible).
                    // The counter must sit **inside** this gate: polling runs every 0.8 seconds, so
                    // if it sat outside, "two empty probes in a row" would in fact be only 1.6
                    // seconds apart, while two real probes are 3 seconds apart — which makes the
                    // gate no more than window dressing, and a background that writes in fits and
                    // starts gets judged finished too early
                    var probed = false;
                    if ((DateTimeOffset.Now - st.BgProbedAt).TotalSeconds >= 3)
                    {
                        st.BgNewest = BackgroundActivity(s.Id, path, mtime);
                        st.BgProbedAt = DateTimeOffset.Now;
                        probed = true;
                    }
                    // Freshness is reckoned from "the moment of the probe": BgNewest is a cached
                    // value, and comparing it against the current time conjures up as much as 3
                    // extra seconds — just enough to judge a running background task as stopped
                    if (st.BgNewest is { } bg && (st.BgProbedAt - bg).TotalSeconds < BgFresh)
                    {
                        st.BgSince ??= bg;
                        st.Busy = true;
                        st.Background = true;
                        st.Stalled = false;
                        st.Since = st.BgSince;
                        st.Unread = false;
                        st.FinishedAt = null;
                        st.BgStaleHits = 0;
                    }
                    else
                    {
                        // One empty probe settles nothing: background writes come in fits and starts anyway, so only two in a row count
                        if (probed) st.BgStaleHits += 1;
                        if (st.BgStaleHits >= 2)
                        {
                            // The background task has only just finished (last round it was still Background): that counts as "a result came out" too
                            if (st.Background && !reads.Contains(s.Id))
                            {
                                st.Unread = true;
                                st.FinishedAt = st.BgNewest ?? DateTimeOffset.Now;
                            }
                            // "Not responding" has to be cleared at the same time. Before entering
                            // Background it is almost always judged out of contact by the timeout
                            // first, and if it is not cleared the tile keeps reading "not responding
                            // · no update for X" instead of "unread · just finished"
                            st.Stalled = false;
                            st.BgSince = null;
                            st.Background = false;
                            st.Busy = false;   // Otherwise the next round never gets in here and probing seizes up altogether
                            st.Since = null;
                        }
                    }
                }
                else
                {
                    st.Background = false;
                    st.BgSince = null;
                    st.BgStaleHits = 0;
                }
            }

            // An unread marker that has hung around too long clears itself, rather than just standing there
            if (st.Unread && st.FinishedAt is { } fin
                && (DateTimeOffset.Now - fin).TotalSeconds > UnreadExpiry)
            {
                st.Unread = false;
                st.FinishedAt = null;
            }
            if (st.Title.Length == 0) st.CustomTitle = s.Name;

            newStates[s.Id] = st;
            result.Add(new SessionActivity
            {
                Id = s.Id,
                Title = st.Title,
                Busy = st.Busy,
                Waiting = st.Waiting,
                Since = st.Since,
                Unread = st.Unread,
                FinishedAt = st.FinishedAt,
                CtxTokens = st.CtxTokens,
                CtxLimit = st.CtxLimit,
                Background = st.Background,
                Stalled = st.Stalled,
            });
        }

        _states = newStates;
        result.Sort(CompareSessions);
        lock (_lock) { _sessions = result; }
    }

    /// <summary>The ones waiting on you come first; then the running ones (earliest start first); then the unread ones (most recently finished first).</summary>
    private static int CompareSessions(SessionActivity a, SessionActivity b)
    {
        if (a.Waiting != b.Waiting) return a.Waiting ? -1 : 1;
        if (a.Busy != b.Busy) return a.Busy ? -1 : 1;
        // Nullable.Compare sorts null lowest, which is equivalent to ?? .distantPast in Swift
        if (a.Busy) return Nullable.Compare(a.Since, b.Since);
        if (a.Unread != b.Unread) return a.Unread ? -1 : 1;
        return Nullable.Compare(b.FinishedAt, a.FinishedAt);
    }

    // MARK: Parsing the tail

    private static void ParseTail(string path, FState st, long window)
    {
        byte[]? data = null;
        try
        {
            // Claude Code has this file open and is appending to it. Without the ReadWrite | Delete
            // share flags, not only can we not open it on Windows, we may also make the writer's
            // own open fail in return.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            var end = fs.Length;
            if (end <= 0) return;
            var len = Math.Min(end, window);

            // A single record can be larger than the window (tool results of hundreds of KB are
            // very common; the macOS version has seen 1.35MB). When the window falls entirely
            // inside one record, not a single line can be parsed, it gets judged "finished", and
            // so a false unread notification pops up and the timer is reset. Widen the window step
            // by step until it holds at least one complete record.
            while (true)
            {
                fs.Seek(end - len, SeekOrigin.Begin);
                var buf = new byte[(int)len];
                var off = 0;
                while (off < buf.Length)
                {
                    var n = fs.Read(buf, off, buf.Length - off);
                    if (n <= 0) break;      // The file was truncated while we were reading, so use the part we already have
                    off += n;
                }
                if (off == 0) return;
                data = off == buf.Length ? buf : buf[..off];

                if (len >= end || len >= DeepBytes) break;   // Reached the start of the file / reached the deep-scan ceiling
                var first = Array.IndexOf(data, Nl);
                // Two newlines = at least one complete record sandwiched between them
                if (first >= 0 && Array.IndexOf(data, Nl, first + 1) >= 0) break;
                len = Math.Min(end, len * 4);
            }
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        if (data is null || data.Length == 0) return;

        var haveLast = false;           // In Swift this is the optional tuple lastKind
        var lastIsAssistant = false;
        string? lastStop = null;
        var sawTurnEnd = false;
        var lastAsked = false;
        // Where this turn started: the **first** user action after the previous end_turn.
        // The first rather than the last — chipping in part-way through (steering) should not reset the timer.
        DateTimeOffset? turnStart = null;
        // A start point that was cleared by a synthetic record (the placeholder for an API error).
        // If the turn picks up again through an automatic retry, restore it rather than letting
        // the timer start over from 0
        DateTimeOffset? resumeStart = null;
        // The earliest timestamp within this turn's window. The fallback for when not even an anchor can be found — still more trustworthy than "now"
        DateTimeOffset? turnFloor = null;
        var notificationTimes = new List<DateTimeOffset>();

        var pos = 0;
        while (pos < data.Length)
        {
            var nl = Array.IndexOf(data, Nl, pos);
            var lineEnd = nl < 0 ? data.Length : nl;
            var lineStart = pos;
            var lineLen = lineEnd - lineStart;
            pos = lineEnd + 1;
            if (lineLen <= 2) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(data, lineStart, lineLen));
            }
            catch (JsonException)
            {
                continue;   // The start of the window most likely cuts through the middle of some record, so the first line failing to parse is normal
            }

            using (doc)
            {
                var obj = doc.RootElement;
                if (obj.ValueKind != JsonValueKind.Object) continue;
                var type = Str(obj, "type") ?? "";

                switch (type)
                {
                    case "custom-title":
                    {
                        var v = Str(obj, "customTitle");
                        if (!string.IsNullOrEmpty(v)) st.CustomTitle = v;
                        break;
                    }
                    case "ai-title":
                    {
                        var v = Str(obj, "aiTitle");
                        if (!string.IsNullOrEmpty(v)) st.AiTitle = v;
                        break;
                    }
                    case "queue-operation":
                    {
                        // A message the user sends while Claude is busy gets queued first; this is the time anchor for "chipping in part-way through"
                        if (Str(obj, "operation") == "enqueue" && turnStart is null
                            && ParseTs(Str(obj, "timestamp")) is { } qts)
                        {
                            turnStart = qts;
                        }
                        break;
                    }
                }

                if (type != "assistant" && type != "user") continue;
                if (Bool(obj, "isMeta")) continue;
                if (turnFloor is null && ParseTs(Str(obj, "timestamp")) is { } floor) turnFloor = floor;
                var msg = Obj(obj, "message");

                if (type == "user")
                {
                    var itext = ContentText(msg);
                    // Esc interrupt: this round is forcibly ended
                    if (itext.StartsWith("[Request interrupted", StringComparison.Ordinal))
                    {
                        haveLast = true;
                        lastIsAssistant = true;
                        lastStop = "end_turn";
                        sawTurnEnd = true;
                        // Just like a real end_turn, the start point has to be voided. Leave this
                        // line out and the old timestamp left behind by a mid-run interjection
                        // gets taken as the next round's start point — measured in practice as
                        // "9 min 32 s already spent" the moment it began
                        turnStart = null;
                        resumeStart = null;
                        turnFloor = null;
                        continue;
                    }
                    // A background-task completion notification is not "the user speaking"; note the time so an enqueue at the same instant can be excluded
                    if (itext.StartsWith("<task-notification", StringComparison.Ordinal)
                        && ParseTs(Str(obj, "timestamp")) is { } nts)
                    {
                        notificationTimes.Add(nts);
                    }
                    var isToolResult = ContentHasType(msg, "tool_result");
                    var real = IsRealPrompt(msg);
                    if (!real && !isToolResult) continue;
                    // Anchor directly on the user's own record. This used to be located via the
                    // last-prompt record, but that one is written only after the user's message,
                    // so the anchor always landed on the tool result that came afterwards —
                    // measured over 349 turns, 348 of them were late, by a median of 112 seconds,
                    // so a question that had only just been submitted showed up as "0 seconds"
                    if (real && turnStart is null && ParseTs(Str(obj, "timestamp")) is { } uts)
                    {
                        turnStart = uts;
                        resumeStart = null;
                    }
                }

                var stop = Str(msg, "stop_reason");
                haveLast = true;
                lastIsAssistant = type == "assistant";
                lastStop = stop;

                if (type == "assistant")
                {
                    // If the last entry is the tool call that puts options up, it means it is waiting for the user to pick
                    lastAsked = HasAskUserQuestion(msg);
                    // Context usage = the tokens actually sent into the model for this request (output not included)
                    var usage = Obj(msg, "usage");
                    if (usage.ValueKind == JsonValueKind.Object)
                    {
                        var n = Int(usage, "input_tokens")
                                + Int(usage, "cache_read_input_tokens")
                                + Int(usage, "cache_creation_input_tokens");
                        if (n > 0)
                        {
                            st.CtxTokens = n;
                            st.CtxLimit = ContextLimits.For(Str(msg, "model"));
                        }
                    }
                    if (stop == "end_turn" || stop == "stop_sequence")
                    {
                        sawTurnEnd = true;
                        // A synthetic record (model is "<synthetic>", the placeholder for an API
                        // error) is not necessarily a real ending; Claude will often retry
                        // automatically and carry straight on. Stash the start point first and
                        // restore it if the turn really does resume, so the timer does not start
                        // counting from 0 again. Only a genuine end_turn voids it for good.
                        resumeStart = Str(msg, "model") == "<synthetic>"
                            ? turnStart ?? resumeStart
                            : null;
                        turnStart = null;      // A round has ended; only the next user action counts as the new start
                        turnFloor = null;
                    }
                }
                else
                {
                    lastAsked = false;   // A user/tool result followed on, which means it has already been answered
                }
            }
        }

        // An enqueue at the same instant as a background notification (±5 seconds) does not count as the user speaking
        if (turnStart is { } anchor
            && notificationTimes.Any(x => Math.Abs((x - anchor).TotalSeconds) < 5))
        {
            turnStart = null;
        }

        var busy = true;
        if (!haveLast) busy = false;
        if (haveLast && lastIsAssistant && (lastStop == "end_turn" || lastStop == "stop_sequence"))
        {
            busy = false;
        }

        if (busy)
        {
            if ((turnStart ?? resumeStart) is { } a)
            {
                // The tail confirmed a turn boundary, so a really is the start of the new round;
                // otherwise the tail may begin part-way through, so we can only take the earlier
                // of the two, to stop the start point being pushed later
                var cur = st.Since ?? a;
                st.Since = sawTurnEnd ? a : (cur < a ? cur : a);
            }
            else if (st.Busy && st.Since is not null)
            {
                // The previous round already worked out a start point, so leave it exactly as it is
                // (the Swift side writes st.since = old, the effect of which is "don't touch
                // anything"; the branch structure is kept here only so it can be compared line by
                // line with the original)
            }
            else
            {
                // With no anchor, fall back to the earliest timestamp within this turn's window;
                // if even that is missing, leave it null and the UI just shows "thinking".
                // Better to report no duration at all than to invent a fresh one starting at 0 seconds
                st.Since = turnFloor;
            }
        }
        else
        {
            st.Since = null;
        }
        st.Busy = busy;
        st.Waiting = busy && lastAsked;
        st.PendingTool = busy && lastIsAssistant && lastStop == "tool_use";
    }

    /// <summary>Local commands (/model and the like) and system injections do not count as "the user asking something".</summary>
    private static bool IsRealPrompt(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object) return false;
        if (!msg.TryGetProperty("content", out var c)) return false;

        string? text = null;
        if (c.ValueKind == JsonValueKind.String)
        {
            text = c.GetString();
        }
        else if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            var anyText = false;
            var hasImage = false;
            foreach (var item in c.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var t = Str(item, "type");
                if (t == "image") hasImage = true;
                if (t != "text") continue;
                var s = Str(item, "text");
                if (s is null) continue;
                sb.Append(s);
                anyText = true;
            }
            // An image-only prompt counts as a real prompt; a bare tool_result does not
            if (!anyText) return hasImage;
            text = sb.ToString();
        }

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        foreach (var bad in BadPrefixes)
        {
            if (trimmed.StartsWith(bad, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>If content is a string, take it as is; if it is an array, join all the text fragments together.</summary>
    private static string ContentText(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object) return "";
        if (!msg.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var item in c.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (Str(item, "type") != "text") continue;
            sb.Append(Str(item, "text") ?? "");
        }
        return sb.ToString();
    }

    private static bool ContentHasType(JsonElement msg, string type)
    {
        if (msg.ValueKind != JsonValueKind.Object) return false;
        if (!msg.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in c.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && Str(item, "type") == type) return true;
        }
        return false;
    }

    private static bool HasAskUserQuestion(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object) return false;
        if (!msg.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in c.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && Str(item, "type") == "tool_use"
                && Str(item, "name") == "AskUserQuestion")
            {
                return true;
            }
        }
        return false;
    }

    // MARK: Little JSON helpers
    //
    // Every one of them checks ValueKind before taking the value, and a missing field or the wrong
    // type is always treated as "not there" — this lines up with the semantics of `as?` on the
    // Swift side, and also guarantees that a half-written file cannot overturn a whole round of Poll.

    private static string? Str(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object
           && o.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static bool TryInt(JsonElement o, string name, out int v)
    {
        v = 0;
        return o.ValueKind == JsonValueKind.Object
               && o.TryGetProperty(name, out var p)
               && p.ValueKind == JsonValueKind.Number
               && p.TryGetInt32(out v);
    }

    private static int Int(JsonElement o, string name) => TryInt(o, name, out var v) ? v : 0;

    private static bool Bool(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object
           && o.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.True;

    private static JsonElement Obj(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object
           && o.TryGetProperty(name, out var p)
           && p.ValueKind == JsonValueKind.Object
            ? p
            : default;

    /// <summary>A Unix timestamp in milliseconds; a missing field or an out-of-range value both fall back to MinValue (matching Swift's distantPast).</summary>
    private static DateTimeOffset UnixMillis(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object
            || !o.TryGetProperty(name, out var p)
            || p.ValueKind != JsonValueKind.Number
            || !p.TryGetDouble(out var ms))
        {
            return DateTimeOffset.MinValue;
        }
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)ms); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MinValue; }
    }

    /// <summary>
    /// Timestamps in the transcript are ISO8601, and both with and without milliseconds have been
    /// seen ("…T08:45:53Z" / "…T08:45:53.412Z").
    /// AssumeUniversal only steps in as a fallback when the string carries no time zone; if a Z or
    /// a ±hh:mm is written, that takes precedence.
    /// </summary>
    private static DateTimeOffset? ParseTs(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;
    }
}
