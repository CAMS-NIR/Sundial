// Sundial (Windows 版) — Claude Code 会话活动监视
//
// 移植自 macOS 版 Activity.swift。注释里的实测数字（1.35MB、349 个回合里 348 个偏晚、
// 中位数 112 秒、p95≈37 秒……）都是 macOS 版跑出来的真实观测，原样搬过来——
// 它们是这里每一处「看着多此一举」的写法唯一的依据，删了就没人知道为什么这么写。
//
// 数据来源：
//  1) <用户目录>\.claude\sessions\*.json —— 运行中的会话注册表（pid + sessionId + 标题）
//  2) <用户目录>\.claude\projects\<项目>\<sessionId>.jsonl —— 只读尾部，判断忙/闲与回合起点
// 只看 type / stop_reason / timestamp / 标题字段，不读对话正文。

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sundial.Core;

public sealed class ActivityWatcher
{
    /// <summary>
    /// 单个会话的解析状态。C# 没有 Swift 的 inout 结构体，
    /// 用可变 class 塞进字典，交给 ParseTail 就地改写即可。
    /// </summary>
    private sealed class FState
    {
        public long Size;
        public DateTimeOffset Mtime = DateTimeOffset.MinValue;
        public string CustomTitle = "";
        public string AiTitle = "";
        public bool Busy;
        public bool PendingTool;                    // 正在等工具返回，允许更长静默
        public bool Waiting;                        // 最后一条是 AskUserQuestion，等用户选
        public DateTimeOffset? Since;
        public bool Unread;
        public DateTimeOffset? FinishedAt;
        public int CtxTokens;
        public int CtxLimit;
        public bool Background;                     // 主回合结束但后台代理在跑
        public bool Stalled;                        // 超时没动静，只是失联，不代表跑完了
        public DateTimeOffset? BgSince;
        public DateTimeOffset BgProbedAt = DateTimeOffset.MinValue;  // 上次扫描后台目录的时间
        public DateTimeOffset? BgNewest;            // 上次扫到的最新写入时间
        public int BgStaleHits;                     // 连续几次探到后台没动静

        public string Title => CustomTitle.Length == 0 ? AiTitle : CustomTitle;
    }

    private sealed record LiveSession(string Id, string Name, DateTimeOffset Started);

    // 路径一律从 UserProfile 拼。这个写法在 Windows 和 macOS 上都成立，
    // 是故意的——纯逻辑层要能直接在 Mac 上跑测试验证。
    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string SessionsDir => Path.Combine(Home, ".claude", "sessions");
    private static string ProjectsDir => Path.Combine(Home, ".claude", "projects");

    private const long TailBytes = 512 * 1024;
    private const long DeepBytes = 8 * 1024 * 1024;     // 冷启动时的深扫窗口

    // 后台记录多久没动就算停了。实测同一个后台代理的相邻写入间隔 p95≈37 秒、
    // p99≈136 秒，早先的 25 秒会在一次运行途中反复判「跑完了」，弹假提示、把计时清零。
    private const double BgFresh = 90;
    private const double UnreadExpiry = 600;            // 未读最多挂 10 分钟
    private const double StaleAfter = 300;
    private const double ToolStaleAfter = 900;          // Bash 单次上限 600 秒，再留出重试余量

    private const byte Nl = (byte)'\n';

    private static readonly string[] BadPrefixes =
    {
        "<local-command", "<command-", "Caveat:", "<task-notification", "<system-reminder",
    };

    // _states 只有 Poll（后台线程）碰，不上锁；
    // _readRequests 与 _sessions 是跨线程的，必须走 _lock。
    private Dictionary<string, FState> _states = new();
    private readonly object _lock = new();
    private readonly HashSet<string> _readRequests = new();   // UI 线程点「已读」放进来
    private IReadOnlyList<SessionActivity> _sessions = Array.Empty<SessionActivity>();

    /// <summary>UI 线程读；Poll 在后台线程整体替换，读到的永远是某一轮的完整快照。</summary>
    public IReadOnlyList<SessionActivity> Sessions
    {
        get { lock (_lock) { return _sessions; } }
    }

    /// <summary>UI 线程调用：把某个会话标记为已读。</summary>
    public void MarkRead(string id)
    {
        lock (_lock) { _readRequests.Add(id); }
    }

    /// <summary>会话重新开跑时解除「已读」抑制，否则它下次跑完永远不再提示。</summary>
    private void ClearRead(string id)
    {
        lock (_lock) { _readRequests.Remove(id); }
    }

    // MARK: 注册表

    /// <remarks>
    /// pid 会被系统回收。只看 pid 在不在，会把早就结束的会话复活成幽灵方块——
    /// 一个毫不相干的新进程恰好占用了同一个 pid 就够了。
    /// 注册表里记了 procStart，比对进程真实启动时刻才能确认是同一个进程。
    ///
    /// procStart 是 <c>LC_ALL=C TZ=UTC ps -o lstart=</c> 的输出，形如
    /// "Fri Jul 31 08:45:53 2026"，<b>是 UTC 而不是本地时间</b>。日期为个位数时
    /// ps 会补成两个空格（"Fri Jul  4 ..."），所以先把连续空格压成一个再解析。
    /// 容差 1.5 秒——ps 只精确到秒。
    ///
    /// 取不到启动时刻（权限不足）时一律放行：宁可多显示一个方块，也别把正在跑的会话误杀。
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
            // 「系统里没有这个 pid」——.NET 只在查不到进程时抛这一种，对应 Swift 那边
            // kill(pid,0) 返回 ESRCH 的分支：陈旧的注册表文件，跳过。
            return false;
        }
        catch (Exception)
        {
            // 其它任何异常都是「查不动」，不是「不存在」。Swift 版把 kill 的 EPERM
            // （别人的进程、没权限）当作还活着，这里必须同向：一刀切返回 false 的话，
            // 只要 Windows 上冒出一种没预料到的异常，所有会话会一起消失（宁可多显示
            // 一个方块，也别把正在跑的会话整片误杀）。
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
                // 查不到退出状态就别当它死了，继续往下比启动时刻
            }

            if (string.IsNullOrEmpty(procStart)) return true;   // 老版本没这字段，放行

            var norm = string.Join(' ', procStart.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (!DateTime.TryParseExact(norm, "ddd MMM d HH:mm:ss yyyy",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var want))
            {
                return true;            // 格式看不懂，别误杀
            }

            DateTime got;
            try
            {
                got = proc.StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                return true;            // 没权限读别人的进程：放行
            }
            return Math.Abs((got - want).TotalSeconds) < 1.5;
        }
    }

    /// <remarks>
    /// 不能用 File.ReadAllBytes：它按 FileShare.Read 打开，而注册表文件正被 Claude Code
    /// 拿着写句柄。macOS 上无所谓（只有劝告锁），Windows 上会直接撞共享冲突抛 IOException，
    /// 于是这一轮 Poll 把整个会话当成不存在——方块无缘无故闪一下。
    /// 和 ParseTail 一样给足 ReadWrite | Delete 的共享位。
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
                continue;               // 正被写、或者只落了半截
            }
            using (doc)
            {
                var o = doc.RootElement;
                if (o.ValueKind != JsonValueKind.Object) continue;
                var sid = Str(o, "sessionId");
                if (string.IsNullOrEmpty(sid)) continue;
                if (!TryInt(o, "pid", out var pid)) continue;
                // 进程还在、且确实是当初那个进程吗？（陈旧文件不算活跃会话）
                if (!IsSameProcess(pid, Str(o, "procStart"))) continue;
                result.Add(new LiveSession(sid, Str(o, "name") ?? "", UnixMillis(o, "startedAt")));
            }
        }
        return result;
    }

    /// <summary>sessionId -&gt; (会话记录文件, 最后写入时刻, 字节数)</summary>
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
                // 不用 "*.jsonl" 通配符筛：Windows 的通配符会连带匹配 8.3 短名，
                // 后缀相近的临时文件可能被捞进来。显式比后缀最稳。
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
                    continue;           // 枚举到一半文件没了
                }
                map[Path.GetFileNameWithoutExtension(f.Name)] = (f.FullName, m, len);
            }
        }
        return map;
    }

    /// <remarks>
    /// 千万别用 <c>SearchOption.AllDirectories</c> 那个重载：它内部套的是
    /// EnumerationOptions.CompatibleRecursive，那套预设里 IgnoreInaccessible = false、
    /// AttributesToSkip = 0。一个读不动的子目录就会把异常抛到遍历外面，整轮扫描当场中断，
    /// 只能拿半截结果 —— 后台活动少报，正在跑的会话被判成「跑完了」。
    /// Swift 的 FileManager.enumerator 是跳过该条继续扫，所以这里显式给一份对齐的选项。
    ///
    /// AttributesToSkip = Hidden 对应 Swift 的 <c>.skipsHiddenFiles</c>：Windows 的隐藏是
    /// 文件属性而不是命名约定，光看名字是否以 "." 开头拦不住；反过来 Unix 上 .NET 会给
    /// 点开头的名字补上 Hidden 属性，所以一个条件两边通吃。名字判断留着当第二道保险。
    /// </remarks>
    private static readonly EnumerationOptions BgScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden,
    };

    /// <summary>
    /// 后台子代理/工作流的记录写在 &lt;记录文件所在目录&gt;\&lt;会话ID&gt;\... 里，主记录不会动。
    /// 取该目录下最新的写入时间，用来判断「主回合结束但后台还在跑」。
    /// </summary>
    private static DateTimeOffset? BackgroundActivity(string sessionId, string transcript,
                                                      DateTimeOffset cutoff)
    {
        var parent = Path.GetDirectoryName(transcript);
        if (string.IsNullOrEmpty(parent)) return null;
        var dir = Path.Combine(parent, sessionId);
        if (!Directory.Exists(dir)) return null;

        // 不能设「看够 N 个就停」——枚举顺序不定，可能正好漏掉最新那个文件，
        // 于是把在跑的会话误判成空闲。
        DateTimeOffset? newest = null;
        var now = DateTimeOffset.Now;
        try
        {
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", BgScanOptions))
            {
                var name = fsi.Name;
                if (name.Length > 0 && name[0] == '.') continue;   // 隐藏项（.DS_Store 之类）不算数
                DateTimeOffset m;
                // 枚举时已经把元数据带回来了，取属性不再走系统调用——
                // 对齐 Swift 那边直接读 enumerator 缓存的 contentModificationDate
                try { m = fsi.LastWriteTimeUtc; }
                catch (Exception) { continue; }
                // 主回合结束前写的（同步子代理、工具返回）已经由主记录代表了，
                // 再算一遍会让刚跑完的会话被当成「后台还在跑」，压掉完成提示
                if (m <= cutoff) continue;
                if (newest is null || m > newest.Value) newest = m;
                if ((now - m).TotalSeconds < 3) return m;   // 明显新鲜才早退，否则扫完取真正最新
            }
        }
        catch (Exception)
        {
            // 目录被删：拿已经扫到的最新值凑合，别让整轮 Poll 挂掉
        }
        return newest;
    }

    // MARK: 轮询

    /// <summary>后台线程调用。</summary>
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
                    // 首次看到这个会话时多读一些，确保能找到「用户上次说话」的锚点
                    var firstSight = st.Mtime == DateTimeOffset.MinValue;
                    ParseTail(path, st, firstSight ? DeepBytes : TailBytes);
                    st.Mtime = mtime;
                    st.Size = size;
                    st.Background = false;   // 主记录动了，busy 已重判，旧后台标记作废
                    st.Stalled = false;      // 记录又动了，撤销「无响应」
                    // 忙 -> 闲：本轮出结果了，在用户看过之前一直标为未读
                    if (wasBusy && !st.Busy && !reads.Contains(s.Id))
                    {
                        st.Unread = true;
                        // 用记录文件自己的写入时刻，不是「我发现它的时刻」。
                        // 否则回合在夜里结束、早上开机，会显示成「刚刚完成」
                        st.FinishedAt = mtime;
                    }
                    if (st.Busy)
                    {
                        st.Unread = false;
                        st.FinishedAt = null;
                        ClearRead(s.Id);     // 又开始跑了：下次结束要能重新提示
                    }
                }

                // 等用户选择时不设时限——人可能过很久才回来
                var limit = st.PendingTool ? ToolStaleAfter : StaleAfter;
                if (st.Busy && !st.Waiting && (DateTimeOffset.Now - mtime).TotalSeconds > limit)
                {
                    st.Busy = false;
                    st.Since = null;
                    st.PendingTool = false;
                    // 超时只说明失联，不等于跑完了。以前这里静悄悄把方块抹掉、
                    // 太阳去睡觉，而 Claude 可能还在想——现在明说「无响应」
                    st.Stalled = true;
                    if (!reads.Contains(s.Id))
                    {
                        st.Unread = true;
                        st.FinishedAt = mtime;
                    }
                }

                // 主回合已结束，但后台子代理/工作流还在写记录 = 仍在干活。
                // Background 为真时手里的 Busy 是上一轮自己设的，不能当作
                // 「主回合在忙」的判据，必须重新探测后台是否还活着
                if (!st.Busy || st.Background)
                {
                    // 目录遍历较贵，3 秒内复用上次结果（相对 90 秒的 BgFresh，误差可忽略）。
                    // 计数必须放在这道门**里面**：轮询是 0.8 秒一次，放外面的话
                    // 「连续两次探空」实际只隔 1.6 秒，而两次真正的探测要相隔 3 秒——
                    // 等于门形同虚设，后台断断续续写入时会被提前判成跑完了
                    var probed = false;
                    if ((DateTimeOffset.Now - st.BgProbedAt).TotalSeconds >= 3)
                    {
                        st.BgNewest = BackgroundActivity(s.Id, path, mtime);
                        st.BgProbedAt = DateTimeOffset.Now;
                        probed = true;
                    }
                    // 新鲜度按「探测那一刻」算：BgNewest 是缓存值，拿它跟当前时间比，
                    // 会凭空多出最多 3 秒，正好把在跑的后台任务判成停了
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
                        // 一次探空不算完：后台写入本来就断断续续，连续两次才认
                        if (probed) st.BgStaleHits += 1;
                        if (st.BgStaleHits >= 2)
                        {
                            // 后台任务刚跑完（上一轮还是 Background）：也算一次「出结果」
                            if (st.Background && !reads.Contains(s.Id))
                            {
                                st.Unread = true;
                                st.FinishedAt = st.BgNewest ?? DateTimeOffset.Now;
                            }
                            // 必须一并清掉「无响应」。进入 Background 之前几乎总是先被
                            // 超时判成失联，不清的话方块会一直写「无响应 · 已 X 无更新」，
                            // 而不是「未读 · 刚刚完成」
                            st.Stalled = false;
                            st.BgSince = null;
                            st.Background = false;
                            st.Busy = false;   // 否则下一轮进不来这里，探测彻底停摆
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

            // 挂太久的未读自动消掉，别一直杵在那儿
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

    /// <summary>等你选的排最前；其次在跑的（开跑早的在前）；再次未读（新完成的在前）。</summary>
    private static int CompareSessions(SessionActivity a, SessionActivity b)
    {
        if (a.Waiting != b.Waiting) return a.Waiting ? -1 : 1;
        if (a.Busy != b.Busy) return a.Busy ? -1 : 1;
        // Nullable.Compare 把 null 排在最小，等价于 Swift 里的 ?? .distantPast
        if (a.Busy) return Nullable.Compare(a.Since, b.Since);
        if (a.Unread != b.Unread) return a.Unread ? -1 : 1;
        return Nullable.Compare(b.FinishedAt, a.FinishedAt);
    }

    // MARK: 解析尾部

    private static void ParseTail(string path, FState st, long window)
    {
        byte[]? data = null;
        try
        {
            // Claude Code 正开着这个文件往里追加。不给 ReadWrite | Delete 的共享位，
            // Windows 上不但我们打不开，还可能反过来让写入方的打开失败。
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            var end = fs.Length;
            if (end <= 0) return;
            var len = Math.Min(end, window);

            // 单条记录可能比窗口还大（工具返回上百 KB 很常见，macOS 版见过 1.35MB）。
            // 窗口整个落在一条记录内部时一行都解析不出来，会被判成「已完成」，
            // 于是弹出假的未读提示并把计时清零。逐步扩窗，直到至少装得下一条完整记录。
            while (true)
            {
                fs.Seek(end - len, SeekOrigin.Begin);
                var buf = new byte[(int)len];
                var off = 0;
                while (off < buf.Length)
                {
                    var n = fs.Read(buf, off, buf.Length - off);
                    if (n <= 0) break;      // 读的过程中文件被截短了，用已经拿到的部分
                    off += n;
                }
                if (off == 0) return;
                data = off == buf.Length ? buf : buf[..off];

                if (len >= end || len >= DeepBytes) break;   // 已到文件头 / 已到深扫上限
                var first = Array.IndexOf(data, Nl);
                // 两个换行 = 中间夹着至少一条完整记录
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

        var haveLast = false;           // Swift 里是 lastKind 这个可选元组
        var lastIsAssistant = false;
        string? lastStop = null;
        var sawTurnEnd = false;
        var lastAsked = false;
        // 本回合起点：上一次 end_turn 之后的**第一次**用户动作。
        // 取第一次而不是最后一次——中途插话（steering）不该把计时清零。
        DateTimeOffset? turnStart = null;
        // 被合成记录（API 报错占位）清掉的起点。回合若自动重试续上了，还原它，
        // 别让计时从 0 重来
        DateTimeOffset? resumeStart = null;
        // 本回合窗口内最早的时间戳。连锚点都找不到时的兜底，总比「现在」靠谱
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
                continue;   // 窗口起点多半切在某条记录中间，头一行解析不了是正常的
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
                        // 用户在 Claude 忙碌时发的消息会先入队；这是「中途插话」的时间锚点
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
                    // Esc 中断：本轮强制结束
                    if (itext.StartsWith("[Request interrupted", StringComparison.Ordinal))
                    {
                        haveLast = true;
                        lastIsAssistant = true;
                        lastStop = "end_turn";
                        sawTurnEnd = true;
                        // 和真正的 end_turn 一样要把起点作废。漏掉这句，中途插话留下的
                        // 旧时间戳会被下一轮当成起点，实测出现过「刚开始就已用 9 分 32 秒」
                        turnStart = null;
                        resumeStart = null;
                        turnFloor = null;
                        continue;
                    }
                    // 后台任务完成通知不是「用户说话」，记下时间用于排除同刻的 enqueue
                    if (itext.StartsWith("<task-notification", StringComparison.Ordinal)
                        && ParseTs(Str(obj, "timestamp")) is { } nts)
                    {
                        notificationTimes.Add(nts);
                    }
                    var isToolResult = ContentHasType(msg, "tool_result");
                    var real = IsRealPrompt(msg);
                    if (!real && !isToolResult) continue;
                    // 直接锚在用户这条记录上。以前靠 last-prompt 记录来定位，可它是在
                    // 用户消息之后才写的，锚点总是落到后面那条工具返回上——实测 349 个
                    // 回合有 348 个偏晚，中位数晚 112 秒，于是刚提交的问题显示成「0 秒」
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
                    // 最后一条若是抛选项的工具调用，说明在等用户选
                    lastAsked = HasAskUserQuestion(msg);
                    // 上下文占用 = 这次请求真正送进模型的 token（不含输出）
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
                        // 合成记录（model 为 "<synthetic>"，API 报错的占位）未必是真结束，
                        // Claude 常会自动重试接着跑。先把起点存起来，回合真续上了就还原，
                        // 免得计时从 0 重新数。只有真 end_turn 才彻底作废。
                        resumeStart = Str(msg, "model") == "<synthetic>"
                            ? turnStart ?? resumeStart
                            : null;
                        turnStart = null;      // 一轮结束，下一次用户动作才是新起点
                        turnFloor = null;
                    }
                }
                else
                {
                    lastAsked = false;   // 有用户/工具结果跟上来，说明已经答过了
                }
            }
        }

        // 与后台通知同刻（±5 秒）的入队不算用户说话
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
                // 尾部确证过回合边界，a 就是新一轮真起点；否则尾部可能从半途开始，
                // 只能取更早的那个，防止把起点往后推
                var cur = st.Since ?? a;
                st.Since = sawTurnEnd ? a : (cur < a ? cur : a);
            }
            else if (st.Busy && st.Since is not null)
            {
                // 上一轮已经算出过起点，原样留着（Swift 那边写的是 st.since = old，
                // 效果就是「什么都别动」，这里只保留分支结构以便和原文逐行对照）
            }
            else
            {
                // 没有锚点就退到本回合窗口内最早的时间戳；连它都没有就留 null，
                // UI 只显示「正在思考」。宁可不报时长，也别从 0 秒重新编一个
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

    /// <summary>本地命令（/model 等）与系统注入不算「用户提问」。</summary>
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
            // 纯图片提问算真实提问；纯 tool_result 不算
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

    /// <summary>content 是字符串就直接取，是数组就把所有 text 片段拼起来。</summary>
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

    // MARK: JSON 小工具
    //
    // 全部先判 ValueKind 再取值，缺字段/类型不对一律当「没有」——
    // 对齐 Swift 那边 `as?` 的语义，也保证半截文件不会把整轮 Poll 掀翻。

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

    /// <summary>毫秒 Unix 时间戳；缺字段或超范围都退回 MinValue（对齐 Swift 的 distantPast）。</summary>
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
    /// 记录里的时间戳是 ISO8601，带不带毫秒都见过（"…T08:45:53Z" / "…T08:45:53.412Z"）。
    /// AssumeUniversal 只在字符串没写时区时兜底，写了 Z 或 ±hh:mm 就以它为准。
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
