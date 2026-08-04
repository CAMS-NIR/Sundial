// Sundial — a desktop pet that shows Claude Code usage and session state
// This file was split out of main.swift

import AppKit
import Foundation

// MARK: - Claude Code activity state

/// The context-window ceiling for each model
func contextLimit(for model: String?) -> Int {
    // Only models we know for certain have a small window are listed here; everything
    // else (including new models that don't exist yet) is estimated at 1M.
    // The direction of the error matters: underestimate the denominator and the bar pegs
    // at full and prints self-contradictory numbers like "used 992.9k / 200.0k";
    // overestimate and it merely under-reports a little, which doesn't mislead anyone.
    guard let m = model?.lowercased() else { return 1_000_000 }
    if m.contains("haiku") { return 200_000 }        // the whole haiku line
    if m.contains("claude-3") { return 200_000 }     // the whole 3.x line
    // In the 4 series only 4.5 and earlier are 200k; 4.6/4.7/4.8 and the 5 series are all 1M.
    // Measured from this machine's transcripts: claude-opus-4-8 has had 992,897 tokens of
    // context in a single request. An earlier version treated anything with "-4-" in it as
    // 200k, so it sat permanently on a red 100% bar.
    if m.contains("opus-4-0") || m.contains("opus-4-1") || m.contains("opus-4-5")
        || m.contains("sonnet-4-0") || m.contains("sonnet-4-5")
        || m.hasPrefix("claude-opus-4-2") || m.hasPrefix("claude-sonnet-4-2") {
        return 200_000                               // the last two are the retired claude-*-4-20250514
    }
    return 1_000_000
}

/// Compact token count: 468243 → "468.2k"
func tokenText(_ n: Int) -> String {
    if n >= 1_000_000 { return String(format: "%.1fM", Double(n) / 1_000_000) }
    if n >= 1_000 { return String(format: "%.1fk", Double(n) / 1_000) }
    return "\(n)"
}

struct SessionActivity {
    let id: String            // sessionId
    var title: String
    var busy: Bool
    var waiting: Bool         // it has put up a set of options and is waiting for the user to pick
    var since: Date?          // when this turn started, used for the timer
    var unread: Bool          // finished, but the user hasn't looked at it yet
    var finishedAt: Date?
    var ctxTokens: Int = 0    // tokens of context used
    var ctxLimit: Int = 0     // context ceiling, 0 = unknown
    var background: Bool = false  // the main turn has ended, a background agent is still running
    var stalled: Bool = false     // no new records for a long time, state unknown (≠ finished)
}

/// Where the data comes from:
///  1) ~/.claude/sessions/*.json — the registry of running sessions (pid + sessionId + title)
///  2) ~/.claude/projects/<project>/<sessionId>.jsonl — only the tail is read, to tell
///     busy from idle and to find where the turn started
/// Only the type / stop_reason / timestamp / title fields are looked at; the body of the
/// conversation is never read.
final class ActivityWatcher {
    private struct FState {
        var size: UInt64 = 0
        var mtime = Date.distantPast
        var customTitle = ""
        var aiTitle = ""
        var busy = false
        var pendingTool = false   // waiting for a tool to return, so a longer silence is allowed
        var waiting = false       // the last record is AskUserQuestion, waiting for the user to pick
        var since: Date?
        var unread = false
        var finishedAt: Date?
        var ctxTokens = 0
        var ctxLimit = 0
        var background = false    // the main turn has ended but a background agent is running
        var stalled = false       // gone quiet past the timeout: only out of contact, not finished
        var bgSince: Date?
        var bgProbedAt = Date.distantPast   // when the background directory was last scanned
        var bgNewest: Date?                 // the newest write time the last scan found
        var bgStaleHits = 0                 // how many probes in a row found the background quiet
        var title: String { customTitle.isEmpty ? aiTitle : customTitle }
    }

    private let home = FileManager.default.homeDirectoryForCurrentUser
    private var sessionsDir: URL { home.appendingPathComponent(".claude/sessions") }
    private var projectsDir: URL { home.appendingPathComponent(".claude/projects") }
    private let tailBytes: UInt64 = 512 * 1024
    private let deepBytes: UInt64 = 8 * 1024 * 1024   // the deep-scan window used on a cold start
    // How long the background records have to sit still before we call it stopped. Measured:
    // the gap between consecutive writes from the same background agent is p95≈37 seconds,
    // p99≈136 seconds. The earlier value of 25 seconds kept deciding "it has finished" in the
    // middle of a single run, firing false notifications and resetting the timer to zero.
    private let bgFresh: TimeInterval = 90
    private let unreadExpiry: TimeInterval = 600   // an unread marker hangs about for 10 minutes at most
    private let staleAfter: TimeInterval = 300
    private let toolStaleAfter: TimeInterval = 900 // a single Bash call is capped at 600 seconds, plus slack for a retry

    private var states: [String: FState] = [:]
    private let lock = NSLock()
    private var readRequests = Set<String>()      // the main thread drops ids in here on "mark as read"
    private(set) var sessions: [SessionActivity] = []

    private static let isoFrac: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private static let isoPlain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    private func parseTS(_ s: Any?) -> Date? {
        guard let str = s as? String else { return nil }
        return Self.isoFrac.date(from: str) ?? Self.isoPlain.date(from: str)
    }

    /// Called from the main thread: mark a session as read
    func markRead(_ id: String) {
        lock.lock()
        readRequests.insert(id)
        lock.unlock()
    }

    /// When a session starts running again, lift the "already read" suppression, otherwise it
    /// would never notify again the next time it finishes
    private func clearRead(_ id: String) {
        lock.lock()
        readRequests.remove(id)
        lock.unlock()
    }

    // MARK: Registry

    private struct LiveSession {
        let id: String
        let name: String
        let started: Date
    }

    private static let procStartFmt: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "EEE MMM d HH:mm:ss yyyy"   // i.e. `LC_ALL=C TZ=UTC ps -o lstart=`
        return f
    }()

    /// pids get recycled by the system, and the check below counts even EPERM (someone else's
    /// process) as "still alive". The registry records procStart, and only by comparing it
    /// against the process's real start time can we confirm it is the same process; otherwise
    /// a stranger's process resurrects a long-finished session as a ghost block.
    private func isSameProcess(pid: Int32, procStart: Any?) -> Bool {
        guard let s = procStart as? String, !s.isEmpty else { return true }  // older versions lack this field, let it through
        let norm = s.split(separator: " ", omittingEmptySubsequences: true).joined(separator: " ")
        guard let want = Self.procStartFmt.date(from: norm) else { return true }  // can't make sense of the format, don't kill it by mistake
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        guard sysctl(&mib, 4, &info, &size, nil, 0) == 0,
              size >= MemoryLayout<kinfo_proc>.stride,
              info.kp_proc.p_starttime.tv_sec > 0 else { return false }
        let tv = info.kp_proc.p_starttime
        let got = Date(timeIntervalSince1970: Double(tv.tv_sec) + Double(tv.tv_usec) / 1e6)
        return abs(got.timeIntervalSince(want)) < 1.5   // ps is only accurate to the second
    }

    private func liveSessions() -> [LiveSession] {
        let fm = FileManager.default
        guard let files = try? fm.contentsOfDirectory(at: sessionsDir,
                                                      includingPropertiesForKeys: nil)
        else { return [] }
        var out: [LiveSession] = []
        for f in files where f.pathExtension == "json" {
            guard let d = try? Data(contentsOf: f),
                  let o = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any],
                  let sid = o["sessionId"] as? String, !sid.isEmpty,
                  let pid = o["pid"] as? Int32 else { continue }
            // is the process still there? (a stale file is not an active session)
            guard kill(pid, 0) == 0 || errno == EPERM else { continue }
            guard isSameProcess(pid: pid, procStart: o["procStart"]) else { continue }
            let started = (o["startedAt"] as? Double).map {
                Date(timeIntervalSince1970: $0 / 1000)
            } ?? Date.distantPast
            out.append(LiveSession(id: sid, name: (o["name"] as? String) ?? "", started: started))
        }
        return out
    }

    /// sessionId -> transcript file
    private func transcriptIndex() -> [String: (URL, Date, UInt64)] {
        let fm = FileManager.default
        var map: [String: (URL, Date, UInt64)] = [:]
        guard let dirs = try? fm.contentsOfDirectory(at: projectsDir,
                                                     includingPropertiesForKeys: nil)
        else { return map }
        for dir in dirs {
            guard let files = try? fm.contentsOfDirectory(
                at: dir, includingPropertiesForKeys: [.contentModificationDateKey, .fileSizeKey])
            else { continue }
            for f in files where f.pathExtension == "jsonl" {
                guard let v = try? f.resourceValues(forKeys: [.contentModificationDateKey,
                                                              .fileSizeKey]),
                      let m = v.contentModificationDate else { continue }
                map[f.deletingPathExtension().lastPathComponent] =
                    (f, m, UInt64(v.fileSize ?? 0))
            }
        }
        return map
    }

    /// Records from background subagents/workflows are written under <sessionID>/subagents/...,
    /// and the main transcript never moves. Take the newest write time in that directory, and
    /// use it to work out "the main turn has ended but the background is still running".
    private func backgroundActivity(sessionID: String, transcript: URL,
                                    after cutoff: Date) -> Date? {
        let dir = transcript.deletingLastPathComponent()
            .appendingPathComponent(sessionID)
        let fm = FileManager.default
        guard fm.fileExists(atPath: dir.path),
              let e = fm.enumerator(at: dir,
                                    includingPropertiesForKeys: [.contentModificationDateKey],
                                    options: [.skipsHiddenFiles])
        else { return nil }
        // We can't do "stop once you've seen N of them" — the enumeration order isn't fixed, so
        // it might miss exactly the newest file and mistake a running session for an idle one.
        var newest: Date?
        let now = Date()
        for case let url as URL in e {
            guard let m = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?
                .contentModificationDate,
                  // anything written before the main turn ended (synchronous subagents, tool
                  // results) is already represented by the main transcript; counting it again
                  // makes a session that has just finished look like "the background is still
                  // running", which swallows the completion notification
                  m > cutoff else { continue }
            if newest == nil || m > newest! { newest = m }
            if now.timeIntervalSince(m) < 3 { return m }   // only bail out early when it is plainly fresh, otherwise finish the scan and take the genuinely newest
        }
        return newest
    }

    // MARK: Polling

    func poll() {
        lock.lock()
        let reads = readRequests
        readRequests.removeAll()
        lock.unlock()

        let live = liveSessions()
        guard !live.isEmpty else {
            states.removeAll()
            sessions = []
            return
        }
        let index = transcriptIndex()

        var out: [SessionActivity] = []
        var newStates: [String: FState] = [:]
        for s in live {
            var st = states[s.id] ?? FState()
            if reads.contains(s.id) {
                st.unread = false
                st.finishedAt = nil
            }
            if let (url, mtime, size) = index[s.id] {
                if st.mtime != mtime || st.size != size {
                    let wasBusy = st.busy
                    // the first time we see this session, read a bit more, so we're sure to find
                    // the "when the user last spoke" anchor
                    let firstSight = st.mtime == .distantPast
                    parseTail(url, into: &st, window: firstSight ? deepBytes : tailBytes)
                    st.mtime = mtime
                    st.size = size
                    st.background = false   // the main transcript moved, busy has been re-decided, the old background flag is void
                    st.stalled = false      // the transcript moved again, so revoke "not responding"
                    // busy -> idle: this turn produced a result; keep it marked unread until the user has looked
                    if wasBusy && !st.busy && !reads.contains(s.id) {
                        st.unread = true
                        // use the transcript file's own write time, not "the moment I noticed it".
                        // Otherwise a turn that ended overnight shows as "just finished" when the
                        // machine is woken in the morning
                        st.finishedAt = mtime
                    }
                    if st.busy {
                        st.unread = false
                        st.finishedAt = nil
                        clearRead(s.id)   // it's running again: the next time it finishes it must be able to notify afresh
                    }
                }
                // no time limit while it is waiting for the user to choose — a person may not come back for ages
                let limit = st.pendingTool ? toolStaleAfter : staleAfter
                if st.busy, !st.waiting, Date().timeIntervalSince(mtime) > limit {
                    st.busy = false
                    st.since = nil
                    st.pendingTool = false
                    // a timeout only means we've lost contact, not that it has finished. This
                    // used to quietly wipe the block away and send the sun off to sleep while
                    // Claude might still have been thinking — now it says "not responding" outright
                    st.stalled = true
                    if !reads.contains(s.id) {
                        st.unread = true
                        st.finishedAt = mtime
                    }
                }
                // the main turn has ended but background subagents/workflows are still writing
                // records = still working.
                // When background is true, the busy we're holding was set by ourselves on the
                // previous round, so it can't be taken as evidence that "the main turn is busy";
                // we have to probe again to see whether the background is still alive
                if !st.busy || st.background {
                    // walking the directory is expensive, so reuse the last result for 3 seconds
                    // (bgFresh is 90 seconds, so the error is negligible).
                    // The counter has to sit **inside** this gate: polling runs every 0.8 seconds,
                    // so if it sat outside, "two empty probes in a row" would really be only 1.6
                    // seconds apart, while two actual probes are 3 seconds apart — the gate would
                    // be useless, and a background agent writing in fits and starts would be
                    // declared finished too early
                    var probed = false
                    if Date().timeIntervalSince(st.bgProbedAt) >= 3 {
                        st.bgNewest = backgroundActivity(sessionID: s.id, transcript: url,
                                                         after: mtime)
                        st.bgProbedAt = Date()
                        probed = true
                    }
                    // freshness is counted from "the moment of the probe": bgNewest is a cached
                    // value, and comparing it against the current time invents up to 3 extra
                    // seconds — just enough to declare a running background task stopped
                    if let bg = st.bgNewest,
                       st.bgProbedAt.timeIntervalSince(bg) < bgFresh {
                        if st.bgSince == nil { st.bgSince = bg }
                        st.busy = true
                        st.background = true
                        st.stalled = false
                        st.since = st.bgSince
                        st.unread = false
                        st.finishedAt = nil
                        st.bgStaleHits = 0
                    } else {
                        // one empty probe isn't enough: background writes come in fits and starts
                        // anyway, so it takes two in a row before we believe it
                        if probed { st.bgStaleHits += 1 }
                        if st.bgStaleHits >= 2 {
                            // the background task has just finished (it was still background last
                            // round): that counts as "a result came out" too
                            if st.background, !reads.contains(s.id) {
                                st.unread = true
                                st.finishedAt = st.bgNewest ?? Date()
                            }
                            // "not responding" has to be cleared at the same time. Before entering
                            // background it has nearly always been declared out of contact by the
                            // timeout first (in this machine's real transcripts, the main record is
                            // never end_turn where a background stretch begins); without clearing
                            // it the block would keep saying "not responding · no update for X"
                            // rather than "unread · just finished" — measured across 88 background
                            // stretches, 10 of them hit this
                            st.stalled = false
                            st.bgSince = nil
                            st.background = false
                            st.busy = false   // otherwise the next round can't get in here and probing seizes up completely
                            st.since = nil
                        }
                    }
                } else {
                    st.background = false
                    st.bgSince = nil
                    st.bgStaleHits = 0
                }
            }
            // an unread marker that has hung about too long clears itself; don't let it stand there forever
            if st.unread, let f = st.finishedAt,
               Date().timeIntervalSince(f) > unreadExpiry {
                st.unread = false
                st.finishedAt = nil
            }
            if st.title.isEmpty { st.customTitle = s.name }
            newStates[s.id] = st
            out.append(SessionActivity(id: s.id, title: st.title, busy: st.busy,
                                       waiting: st.waiting, since: st.since,
                                       unread: st.unread, finishedAt: st.finishedAt,
                                       ctxTokens: st.ctxTokens, ctxLimit: st.ctxLimit,
                                       background: st.background, stalled: st.stalled))
        }
        states = newStates
        // the ones waiting on you come first; then the running ones; then the unread ones (most recently finished first)
        sessions = out.sorted { a, b in
            if a.waiting != b.waiting { return a.waiting }
            if a.busy != b.busy { return a.busy }
            if a.busy { return (a.since ?? .distantPast) < (b.since ?? .distantPast) }
            if a.unread != b.unread { return a.unread }
            return (a.finishedAt ?? .distantPast) > (b.finishedAt ?? .distantPast)
        }
    }

    // MARK: Parsing the tail

    private func parseTail(_ url: URL, into st: inout FState, window: UInt64) {
        guard let fh = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? fh.close() }
        let end = (try? fh.seekToEnd()) ?? 0
        let newline = UInt8(ascii: "\n")
        var len = min(end, window)
        var data = Data()
        // A single record can be larger than the window (tool results of several hundred KB are
        // common; 1.35MB has been seen on this machine). When the window falls entirely inside
        // one record, not a single line parses, so it is judged "finished", a false unread
        // notification pops up and the timer is reset to zero. Widen the window step by step
        // until it holds at least one complete record.
        while true {
            try? fh.seek(toOffset: end - len)
            guard let d = try? fh.readToEnd(), !d.isEmpty else { return }
            data = d
            if len >= end || len >= deepBytes { break }       // reached the head of the file / reached the deep-scan ceiling
            if let i = d.firstIndex(of: newline),
               d[d.index(after: i)...].contains(newline) { break }  // two newlines = at least one complete record
            len = min(end, len * 4)
        }

        var lastKind: (isAssistant: Bool, stop: String?)?
        var sawTurnEnd = false
        var lastAsked = false
        // where this turn starts: the **first** user action after the previous end_turn.
        // The first rather than the last — chipping in halfway (steering) shouldn't reset the timer.
        var turnStart: Date?
        // a start point that was cleared by a synthetic record (the placeholder for an API error).
        // If the turn is retried automatically and carries on, restore it, so the timer doesn't
        // start over from 0
        var resumeStart: Date?
        // the earliest timestamp inside this turn's window. The fallback for when not even an
        // anchor can be found; still more reliable than Date()
        var turnFloor: Date?
        var notificationTimes: [Date] = []

        for line in data.split(separator: UInt8(ascii: "\n")) {
            guard line.count > 2,
                  let obj = (try? JSONSerialization.jsonObject(with: Data(line)))
                    as? [String: Any] else { continue }
            let type = obj["type"] as? String ?? ""

            switch type {
            case "custom-title":
                if let t = obj["customTitle"] as? String, !t.isEmpty { st.customTitle = t }
            case "ai-title":
                if let t = obj["aiTitle"] as? String, !t.isEmpty { st.aiTitle = t }
            case "queue-operation":
                // a message the user sends while Claude is busy gets queued first; this is the
                // time anchor for "chipping in halfway"
                if (obj["operation"] as? String) == "enqueue",
                   let ts = parseTS(obj["timestamp"]), turnStart == nil {
                    turnStart = ts
                }
            default:
                break
            }

            guard type == "assistant" || type == "user" else { continue }
            if obj["isMeta"] as? Bool == true { continue }
            if turnFloor == nil, let ts = parseTS(obj["timestamp"]) { turnFloor = ts }
            let msg = obj["message"] as? [String: Any]

            if type == "user" {
                let itext = (msg?["content"] as? String) ??
                    ((msg?["content"] as? [[String: Any]])?
                        .compactMap { ($0["type"] as? String) == "text" ? $0["text"] as? String : nil }
                        .joined() ?? "")
                // Esc interrupt: this turn is forcibly ended
                if itext.hasPrefix("[Request interrupted") {
                    lastKind = (true, "end_turn")
                    sawTurnEnd = true
                    // just like a real end_turn, the start point has to be voided. Leave this line
                    // out and the stale timestamp left behind by a mid-turn interjection gets taken
                    // as the next turn's start point — measured, it has produced "9 minutes 32
                    // seconds elapsed" on a turn that had only just begun
                    turnStart = nil
                    resumeStart = nil
                    turnFloor = nil
                    continue
                }
                // a background-task completion notification isn't "the user speaking"; note its
                // time so an enqueue at the same instant can be ruled out
                if itext.hasPrefix("<task-notification"), let ts = parseTS(obj["timestamp"]) {
                    notificationTimes.append(ts)
                }
                let isToolResult = (msg?["content"] as? [[String: Any]])?
                    .contains { ($0["type"] as? String) == "tool_result" } ?? false
                if !isRealPrompt(msg), !isToolResult { continue }
                // anchor directly onto the user's own record. This used to be located via the
                // last-prompt record, but that is written after the user's message, so the anchor
                // always landed on the tool result that came afterwards — measured over 349 turns,
                // 348 of them were late, the median 112 seconds late, so a question that had just
                // been submitted showed up as "0 seconds"
                if isRealPrompt(msg), let ts = parseTS(obj["timestamp"]), turnStart == nil {
                    turnStart = ts
                    resumeStart = nil
                }
            }

            let stop = msg?["stop_reason"] as? String
            lastKind = (type == "assistant", stop)
            if type == "assistant" {
                // if the last record is the tool call that puts up the options, it is waiting for the user to pick
                lastAsked = (msg?["content"] as? [[String: Any]])?.contains {
                    ($0["type"] as? String) == "tool_use"
                        && ($0["name"] as? String) == "AskUserQuestion"
                } ?? false
                // context usage = the tokens actually fed into the model on this request (output not included)
                if let u = msg?["usage"] as? [String: Any] {
                    let n = (u["input_tokens"] as? Int ?? 0)
                        + (u["cache_read_input_tokens"] as? Int ?? 0)
                        + (u["cache_creation_input_tokens"] as? Int ?? 0)
                    if n > 0 {
                        st.ctxTokens = n
                        st.ctxLimit = contextLimit(for: msg?["model"] as? String)
                    }
                }
                if let s = stop, s == "end_turn" || s == "stop_sequence" {
                    sawTurnEnd = true
                    // a synthetic record (model is "<synthetic>", the placeholder for an API error)
                    // isn't necessarily a real ending — Claude will often retry by itself and carry
                    // on. Stash the start point first and restore it if the turn really does
                    // continue, so the timer doesn't start counting from 0 again. Only a genuine
                    // end_turn voids it for good.
                    resumeStart = (msg?["model"] as? String) == "<synthetic>"
                        ? (turnStart ?? resumeStart) : nil
                    turnStart = nil      // the turn is over; only the next user action is a new start point
                    turnFloor = nil
                }
            } else {
                lastAsked = false   // a user message / tool result has followed, so it has already been answered
            }
        }

        // an enqueue at the same moment (±5 seconds) as a background notification doesn't count as the user speaking
        if let a = turnStart,
           notificationTimes.contains(where: { abs($0.timeIntervalSince(a)) < 5 }) {
            turnStart = nil
        }

        var busy = true
        if lastKind == nil { busy = false }
        if let k = lastKind, k.isAssistant,
           let s = k.stop, s == "end_turn" || s == "stop_sequence" {
            busy = false
        }

        if busy {
            if let a = turnStart ?? resumeStart {
                // the tail confirmed a turn boundary, so a really is the start of the new turn;
                // otherwise the tail may have begun halfway through, so we can only take the
                // earlier of the two, to stop the start point being pushed later
                st.since = sawTurnEnd ? a : min(st.since ?? a, a)
            } else if st.busy, let old = st.since {
                st.since = old
            } else {
                // with no anchor, fall back to the earliest timestamp inside this turn's window;
                // if even that is missing, leave it empty and the UI just shows the "thinking"
                // label. Better to report no duration at all than to invent one starting from 0 seconds
                st.since = turnFloor
            }
        } else {
            st.since = nil
        }
        st.busy = busy
        st.waiting = busy && lastAsked
        st.pendingTool = busy && (lastKind?.isAssistant ?? false) && lastKind?.stop == "tool_use"
    }

    /// Local commands (/model and the like) and system injections don't count as "the user asking something"
    private func isRealPrompt(_ msg: [String: Any]?) -> Bool {
        guard let msg else { return false }
        var text: String?
        if let s = msg["content"] as? String {
            text = s
        } else if let arr = msg["content"] as? [[String: Any]] {
            let texts = arr.compactMap { item -> String? in
                (item["type"] as? String) == "text" ? item["text"] as? String : nil
            }
            if texts.isEmpty {
                // an image-only prompt counts as a real prompt; a bare tool_result doesn't
                return arr.contains { ($0["type"] as? String) == "image" }
            }
            text = texts.joined()
        }
        guard let t = text?.trimmingCharacters(in: .whitespacesAndNewlines),
              !t.isEmpty else { return false }
        for bad in ["<local-command", "<command-", "Caveat:", "<task-notification",
                    "<system-reminder"] where t.hasPrefix(bad) {
            return false
        }
        return true
    }
}

func elapsedText(since date: Date?) -> String {
    guard let date else { return "" }
    let secs = max(0, Int(Date().timeIntervalSince(date)))
    if secs < 60 { return "\(secs)s" }
    let m = secs / 60, s = secs % 60
    if m < 60 { return "\(m)m \(s)s" }
    return "\(m / 60)h \(m % 60)m"
}

func agoText(_ date: Date?) -> String {
    guard let date else { return "" }
    let secs = max(0, Int(Date().timeIntervalSince(date)))
    if secs < 60 { return "just now" }
    let m = secs / 60
    if m < 60 { return "\(m) min ago" }
    return "\(m / 60) h ago"
}

