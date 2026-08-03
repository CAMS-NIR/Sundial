// Sundial (Windows 版) — 吉祥物与仪表盘的绘制
//
// 移植自 macOS 版 PetView.swift，只搬**绘制与动画状态**；窗口、鼠标事件、托盘、
// 无障碍元素树是别的模块的事。这里不碰任何 Avalonia 控件，只对着一个
// DrawingContext 画，这样它既能挂在自绘控件上，也能离屏渲染出来逐帧比对。
//
// 坐标系：Avalonia 的 Y 轴本来就向下，和原版 NSView 的 isFlipped = true 完全一致，
// 所以几何数值（眼睛在 cy-2s、嘴在 cy+6.5s、眉毛内高外低）可以原样照搬，不用翻 y。

using Avalonia;
using Avalonia.Media;
using Sundial.Core;

namespace Sundial.App;

public sealed class PetRenderer
{
    private readonly PetModel _model;

    public PetRenderer(PetModel model) => _model = model;

    // MARK: 造型常量（macOS 版反复调过的数，别自作主张改）

    public const double TopRowH = 64;
    public const double BlockH = 50;          // 标题 + 状态 + 上下文进度条
    public const double BlockGap = 6;
    public const int MaxBlocks = PetModel.MaxBlocks;
    public const double PetScale = 0.44;
    public const double CardRadius = 26;      // 与窗口层的展开圆角一致
    public const double CompactSide = 88;     // 收起时的窗口边长（只剩太阳）
    public const int RayCount = 9;            // 奇数根，转起来更自然
    public const double RayMaxPull = 13;      // 正对鼠标且贴近时的最大伸长（pt）
    public const double GaugeMaxPull = 9.5;   // 仪表满格时朝它那侧的最大伸长（pt）
    /// <summary>两股力叠加后的封顶：收起时窗口只有 88pt 见方（半径 44），
    /// 光芒伸过头会被窗口边缘直接切掉。</summary>
    public const double RayPullCap = 18;
    public const double ResetLineH = 15;      // 「解封时间」那一行的高度

    // MARK: 动画状态

    private double _t;                        // 动画时钟
    private double _blinkUntil = -1;
    /// <summary>0 = 清醒，1 = 打盹。与 macOS 版 PetView.sleepT 对齐——
    /// 原来 IsSunAsleep 是硬布尔，颜色/眼睛/zzz/光芒转角全在一帧里瞬切</summary>
    private double _sleepT = 1;
    /// <summary>0 = 正常，1 = 用满了（眼睛变 ✖）</summary>
    private double _deadT;
    /// <summary>呼吸相位单独累积：醒着 1.6 睡着 1.0，直接改 sin 的频率会跳相</summary>
    private double _breathPhase;
    private double _nextBlinkAt = 2;
    private double _spinPhase;                // 0–1 循环，保证首尾无缝
    private double _sunSpin;
    private readonly double[] _ringShown = new double[2];   // 两个圆环当前显示值（外/内），向目标缓动
    private readonly List<(string Id, Rect Rect)> _blockRects = new(); // 命中测试用
    private Rect _loginButtonRect;

    private Point? _mouse;                    // 经过作用半径筛选后的引力源（视图坐标）
    private Point _petCenter;                 // 上一帧太阳中心
    private bool _hasPetCenter;
    private readonly double[] _rayPull = new double[RayCount];        // 各光芒伸长量
    private Point _bodyLean;                  // 整只往鼠标方向偏一点
    private Point _eyeShift;                  // 眼珠看向鼠标
    private double _perk;                     // 0–1，被靠近时的「精神一振」
    private readonly List<BlockAnim> _blocks = new();

    // 必须写 new()：结构体字段默认是全零，不会走无参构造，_startedAt 就拿不到 -99
    private Tween _hoverTween = new();
    private Tween _expandTween = new();

    // MARK: 对外

    /// <summary>全局光标位置（已换算成视图坐标）。窗口层每帧塞进来。
    /// 用**全局**光标而不是「鼠标进了窗口才算」：这样光标还在窗口外靠近时，
    /// 太阳就已经有反应了——「引力」本来就该是隔空的。</summary>
    public Point? MousePoint { get; set; }

    public double HoverProgress => _hoverTween.Value;    // 0–1，详情展开进度
    public double ExpandProgress => _expandTween.Value;  // 0=只剩太阳，1=完整卡片

    public bool ReduceMotion { get; set; }        // 系统「减弱动态效果」
    public bool ReduceTransparency { get; set; }  // 系统「降低透明度」：自己画不透明底

    /// <summary>尺寸/透明度这一帧变了，窗口需要重新布局。对应 Swift 的 onHoverProgress。</summary>
    public Action? OnLayoutChanged;

    /// <summary>会话块的命中矩形（视图坐标）。事件层做点击标记已读要用。</summary>
    public IReadOnlyList<(string Id, Rect Rect)> BlockRects => _blockRects;

    /// <summary>登录按钮的命中矩形；没画出来时是 default。</summary>
    public Rect LoginButtonRect => _loginButtonRect;

    /// <summary>用满了的那条限额什么时候解封。**只有真的到上限（太阳变 ✖）才画这一行**——
    /// 没满的时候「还有多久重置」是句废话，占一行还把卡片撑高；
    /// 满了之后它反过来是唯一还想知道的事。多条同时超限就取最早解封的那条。
    /// null = 没满，这一行不画。与 macOS 版 PetView.soonestResetText() 对齐。</summary>
    public string? SoonestResetText()
    {
        var now = DateTimeOffset.Now;
        DateTimeOffset? best = null;
        string label = "";
        foreach (var r in _model.Rows)
        {
            if (r.Percent < 100 || r.ResetAt is not { } d || d <= now) continue;
            if (best is null || d < best) { best = d; label = r.Label; }
        }
        if (best is null) return null;
        // 「每周 · 全部模型」这种长标签只取第一段，198pt 宽放不下整条
        var idx = label.IndexOf(" · ", StringComparison.Ordinal);
        var shortLabel = idx > 0 ? label[..idx] : label;
        return $"{shortLabel} · {Usage.CompactReset(best)} 后解封";
    }

    /// <summary>解封那一行占的高度；不画时为 0。窗口高度要算进去。</summary>
    public double ResetLineHeight => SoonestResetText() is null ? 0 : ResetLineH;

    /// <summary>有没有还在跑的动画。没有就不用每帧重绘，省电。
    /// 吉祥物的呼吸/眨眼/转动一直算「有动画」——停下来它就成了一张死图。</summary>
    public bool NeedsContinuousAnimation => true;

    /// <summary>只有交互与过渡才需要满帧；单纯的呼吸眨眼用低帧率就够。</summary>
    public bool NeedsFullFrameRate
    {
        get
        {
            if (_model.AnyBusy) return true;                     // 转圈 / 光芒转动
            if (_mouse is not null) return true;                 // 光芒引力
            if (Math.Abs(HoverProgress - (_model.Hovered || _model.DetailsPinned ? 1 : 0)) > 0.001) return true;
            if (Math.Abs(ExpandProgress - ExpandTargetValue) > 0.001) return true;
            return false;
        }
    }

    /// <summary>会话块区域当前占的高度（连续变化）。
    /// 必须夹到 0：sum 很小的时候 sum*56-6 是负的，窗口会先缩过头再弹回来。</summary>
    public double BlocksHeight
    {
        get
        {
            double sum = 0;
            foreach (var b in _blocks) sum += b.Tw.Value;
            return Math.Max(0, sum * (BlockH + BlockGap) - BlockGap);
        }
    }

    private double ExpandTargetValue =>
        // 用 blocks 而不是 VisibleSessions：块还在淡出时窗口不能先收，
        // 否则两段动画叠在一起，看起来仍然是「啪」一下
        (_model.Hovered || _model.DetailsPinned || _blocks.Count > 0
         || _model.Loading || (_model.Rows.Count == 0 && _model.ErrorMsg != null)) ? 1 : 0;

    /// <summary>太阳是否在打盹：绘制与引力必须用同一个判断，否则角度会错开一个 sunSpin。</summary>
    private bool IsSunAsleep => _model.Asleep || !_model.AnyBusy;

    // MARK: 定时缓动
    //
    // 指数平滑（SmoothStep）总是头快尾慢：收起时前 0.1 秒就走完大半，
    // 剩下的一点点慢慢磨——看着就是「啪」地消失，而不是渐变。
    // 改成固定时长的 S 形曲线，快慢分布均匀，收起才像收起。
    private struct Tween
    {
        public double Value;
        private double _from;
        private double _to;
        private double _startedAt = -99;

        public Tween() { }

        /// <summary>返回值：这一帧有没有变化（用来决定要不要通知窗口重新布局）。</summary>
        public bool Step(double target, double now, double dur, bool instant)
        {
            if (target != _to) { _from = Value; _to = target; _startedAt = now; }
            if (instant)
            {
                var jumped = Value != target;
                Value = target;
                return jumped;
            }
            if (Value == _to) return false;
            var p = dur <= 0 ? 1 : Math.Min(1, (now - _startedAt) / dur);
            var next = p >= 1 ? _to : _from + (_to - _from) * Theme.EaseInOut(p);
            var changed = next != Value;
            Value = next;
            return changed;
        }
    }

    /// <summary>会话块的出现/消失进度。窗口高度必须用这个连续值算，不能直接数块数——
    /// 块数是离散的，最后一块一消失窗口会在一帧里掉 50pt，把所有缓动都吃掉。
    /// 正在淡出的块要留着自己的数据，不然没法继续画。</summary>
    private sealed class BlockAnim
    {
        public required SessionActivity S;
        public Tween Tw = new();
    }

    // MARK: 推进一帧

    public void Advance(double dt)
    {
        _t += dt;
        _sleepT = Theme.SmoothStep(_sleepT, IsSunAsleep ? 1 : 0, dt, 3.2);
        _deadT = Theme.SmoothStep(_deadT, _model.MaxPercent >= 100 ? 1 : 0, dt, 3.0);
        _breathPhase += dt * (1.6 - 0.6 * _sleepT);
        // 转圈：归一化相位，wrap 时首尾严丝合缝
        if (_model.AnyBusy)
        {
            _spinPhase += dt * 0.55;
            while (_spinPhase >= 1) _spinPhase -= 1;
        }
        if (_model.AnyBusy && !_model.Asleep)
        {
            _sunSpin += dt * 0.9;
            while (_sunSpin > Math.PI * 2) _sunSpin -= Math.PI * 2;
        }
        else if (_sunSpin != 0)
        {
            // 停下时归到最近的一个「卡点」。光芒是 9 次对称，转到 40° 的任意
            // 整数倍看起来都一样，所以这一步看不见，但空闲姿态从此唯一确定
            var step = Math.PI * 2 / RayCount;
            var target = Math.Round(_sunSpin / step) * step;
            _sunSpin = Theme.SmoothStep(_sunSpin, target, dt, 4);
            if (Math.Abs(_sunSpin - target) < 0.0005) _sunSpin = target;
        }
        // 圆环数值缓动跟随。**按位置记，不按标签记**——右圈显示的是「最紧的那条周限额」，
        // 哪条最紧是会换人的（比如 Fable 被「全部模型」反超）。按标签记的话，换人时
        // 新标签没有历史值、要从 0 长起来，看着像用量突然清零了
        // （macOS 版实测：216° 一帧掉到 54°，再花半秒爬回 259°）。
        var (ringOuterT, ringInnerT) = _model.RingRows;
        var ringRowsT = new[] { ringOuterT, ringInnerT };
        for (int i = 0; i < 2; i++)
        {
            // 圆环最多画满一圈；超限的部分靠中间的数字（如 106%）说话
            var target = ringRowsT[i] is { } rr ? Math.Min(1, rr.Percent / 100.0) : 0;
            var cur = _ringShown[i];
            _ringShown[i] = Math.Abs(cur - target) > 0.0005
                ? Theme.SmoothStep(cur, target, dt, 5)
                : target;
        }
        UpdateMousePoint();

        // 光芒引力：朝鼠标的那几根被拉长，背对的缩回，离得越近越明显
        // 指针引力是跟手位移，Reduce Motion 时保持关闭；呼吸与转动不受影响
        var targets = ReduceMotion ? new double[RayCount] : RayPullTargets();
        for (int i = 0; i < RayCount; i++)
            _rayPull[i] = Theme.SmoothStep(_rayPull[i], targets[i], dt, 9);

        // 整只偏移 + 眼神跟随 + 精神一振：和光芒同一个「场」，一起缓动
        var field = ReduceMotion ? null : MouseField();
        // 醒着凑过去(+4.2)、睡着躲开(-3.0)，按 _sleepT 连续插值，中间经过 0
        double leanSign = 1;
        double leanMax = 4.2 * (1 - _sleepT) - 3.0 * _sleepT;
        var lean = field is { } lf
            ? new Point(lf.Ux * leanMax * lf.Proximity * leanSign,
                        lf.Uy * leanMax * lf.Proximity * leanSign)
            : default;

        // 有鼠标就看鼠标（比身体跟得更早也更满，离得还远就已经在看你了）；
        // 没人理它的时候，就时不时瞟一眼两侧的仪表盘
        Point eye;
        if (field is { } ef)
        {
            var k = 1.7 * (1 - _sleepT);      // 睡着时眼珠不再跟人走
            eye = new Point(ef.Ux * k * Math.Min(1, ef.Proximity * 2.4),
                            ef.Uy * k * Math.Min(1, ef.Proximity * 2.4));
        }
        else eye = default;   // 没鼠标就正视前方，不再自己乱瞟

        _bodyLean = new Point(Theme.SmoothStep(_bodyLean.X, lean.X, dt, 7),
                              Theme.SmoothStep(_bodyLean.Y, lean.Y, dt, 7));
        _eyeShift = new Point(Theme.SmoothStep(_eyeShift.X, eye.X, dt, 12),
                              Theme.SmoothStep(_eyeShift.Y, eye.Y, dt, 12));
        _perk = Theme.SmoothStep(_perk, (field?.Proximity ?? 0) * (1 - _sleepT), dt, 8);

        // 会话块的出现/消失：还在的**按 visible 的顺序重排**，走掉的插回原位淡出。
        // 之前是「按旧顺序遍历、新块一律 append」，于是 ActivityWatcher 精心排的
        // 「等你选的最前 → 在跑的 → 未读的」只在 blocks 从空建立那一次生效；
        // 之后某个会话抛出选项，它仍画在原来的格子里，5 个会话时甚至排到最后一格。
        var visible = _model.VisibleSessions;
        var nextBlocks = new List<BlockAnim>();
        var blocksChanged = false;
        foreach (var s in visible)
        {
            var old = _blocks.FirstOrDefault(b => b.S.Id == s.Id);
            if (old is not null)
            {
                old.S = s;
                blocksChanged = old.Tw.Step(1, _t, 0.34, ReduceMotion) || blocksChanged;
                nextBlocks.Add(old);
            }
            else
            {
                var b = new BlockAnim { S = s };
                b.Tw.Step(1, _t, 0.34, ReduceMotion);
                nextBlocks.Add(b);
                blocksChanged = true;
            }
        }
        // 已经不在 visible 里的插回它原来的相对位置，就地淡出，不要突然跳位
        for (int i = 0; i < _blocks.Count; i++)
        {
            var old = _blocks[i];
            if (visible.Any(x => x.Id == old.S.Id)) continue;
            blocksChanged = old.Tw.Step(0, _t, 0.5, ReduceMotion) || blocksChanged;
            if (old.Tw.Value > 0.004) nextBlocks.Insert(Math.Min(i, nextBlocks.Count), old);
        }
        _blocks.Clear();
        _blocks.AddRange(nextBlocks);

        // 悬停详情 + 收起/展开：定时缓动，窗口尺寸与内容透明度同步跟随。
        // 收起给的时间比展开长——「出现」可以利落，「消失」慢一点才不像被抹掉。
        // Reduce Motion 时尺寸变化直接到位（逐帧缩放才是会引起不适的那部分）
        double hoverTarget = (_model.Hovered || _model.DetailsPinned) ? 1 : 0;
        double expandTarget = ExpandTargetValue;
        var changed = _hoverTween.Step(hoverTarget, _t,
                                       hoverTarget > HoverProgress ? 0.30 : 0.42, ReduceMotion);
        changed = _expandTween.Step(expandTarget, _t,
                                    expandTarget > ExpandProgress ? 0.40 : 0.62, ReduceMotion) || changed;
        if (changed || blocksChanged) OnLayoutChanged?.Invoke();
        // 眨眼。之前连同「瞟仪表」一起删过，但那两件事不一样：
        // 瞟仪表是眼珠周期性左右移动（看着像在闪），眨眼只是一次高度收缩，不抢眼
        if (_t >= _nextBlinkAt)
        {
            _blinkUntil = _t + 0.16;
            _nextBlinkAt = _t + 2.4 + Random.Shared.NextDouble() * 3.6;   // 2.4–6.0 秒
        }

    }

    /// <summary>超出作用半径就置空，免得一直按满帧重绘。</summary>
    private void UpdateMousePoint()
    {
        var p = MousePoint;
        if (p is null) { _mouse = null; return; }
        if (!_hasPetCenter) { _mouse = p; return; }     // 还没画过第一帧，不知道太阳在哪
        var dx = p.Value.X - _petCenter.X;
        var dy = p.Value.Y - _petCenter.Y;
        _mouse = dx * dx + dy * dy <= 230 * 230 ? p : null;
    }


    /// <summary>鼠标相对太阳的方向与近度。光芒、身体偏移、眼神跟随都取自同一个场，
    /// 否则各算各的，切换状态时会出现互相错位。</summary>
    private (double Ux, double Uy, double Proximity)? MouseField()
    {
        if (_mouse is not { } m || !_hasPetCenter) return null;
        var dx = m.X - _petCenter.X;
        var dy = m.Y - _petCenter.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist <= 0.001) return null;
        // 近度：贴着身体最强，约 150pt 外基本消失
        var proximity = 1 / (1 + Math.Pow(Math.Max(0, dist - 26) / 62, 2));
        if (proximity <= 0.02) return null;
        return (dx / dist, dy / dist, proximity);
    }

    /// <summary>第 i 根光芒的朝向，必须和 DrawPet 里的算法完全一致，否则受力方向会错位。</summary>
    /// <summary>_sunSpin 在不忙时本来就停止累积，这里无条件带上即可。
    /// 原来睡着时强行归零，等于整圈光芒在一帧里转回原位</summary>
    private double RayAngle(int i) =>
        (double)i / RayCount * 2 * Math.PI + Math.PI / 8 + _sunSpin;

    private static double WrapPi(double a)
    {
        var d = a;
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    /// <summary>每根光芒的目标伸长量，两股力叠加：
    /// ① 鼠标——醒着被吸过去，打盹时反过来躲开；
    /// ② 两侧的仪表盘——用得越满，朝那一侧的光芒被拽得越长。</summary>
    private double[] RayPullTargets()
    {
        var outv = new double[RayCount];

        if (MouseField() is { } f)
        {
            var mAngle = Math.Atan2(f.Uy, f.Ux);
            double sign = 1 - 2 * _sleepT;                    // 醒着凑过去，睡着躲开，连续过渡
            double maxPull = RayMaxPull * (1 - _sleepT) + 6 * _sleepT;
            // 背对的一侧反向变化。醒着时只是一点点缀；睡着时「躲」要看得出来——
            // 近的一侧缩回去的同时，远的一侧要明显探出去，才像整个身子被推开。
            // 原来这个系数是 0.28，远侧只长了不到两个点，肉眼根本看不出来
            double recoilK = 0.28 * (1 - _sleepT) + 1.05 * _sleepT;
            for (int i = 0; i < RayCount; i++)
            {
                var delta = WrapPi(RayAngle(i) - mAngle);
                // cos 归一到 0–1 后取幂。指数从 2.2 降到 1.4：光芒减到 9 根后，
                // 太尖的衰减只有一根够得着，看不出「一片被拉过去」的感觉
                var alignment = Math.Pow(Math.Max(0, Math.Cos(delta)), 1.4);
                var recoil = -recoilK * Math.Pow(Math.Max(0, -Math.Cos(delta)), 1.8);
                outv[i] += maxPull * f.Proximity * (alignment + recoil) * sign;
            }
        }

        // 仪表盘的拉扯：左仪表在正左（π），右仪表在正右（0）。
        // 从 50%（警戒线）起才开始拽，满格时最强——于是「哪边紧」直接长在造型上，
        // 不用等你去读数字。光芒转动时被拽的那几根不断换人，整圈像被扯成了椭圆。
        var (ringOuter, ringInner) = _model.RingRows;
        foreach (var (dirAngle, row) in new (double, UsageRow?)[] { (Math.PI, ringOuter), (0, ringInner) })
        {
            if (row is null) continue;
            double pct = row.Percent;
            // 幅度要有下限。原来是从 50% 起的线性斜坡，60% 的圈只拿到满力的 20%，
            // 摆幅 3.5pt，等于没动。现在最小也有满力的四成，
            // 但仍随用量增长——「哪边紧」照样能从摆幅大小读出来
            var k = 0.4 + 0.6 * Math.Min(1, Math.Max(0, (pct - 15) / 75));
            var u = Math.Clamp(pct / 100, 0, 1);
            // 「呼吸」不是强弱起伏，是**一吸一斥**：正半周把这一侧的光芒拽出去，
            // 负半周又收回来，来回摆动才看得出来（只在 0.55–1.0 之间变强弱，
            // 方向始终向外，几乎看不出在动）。
            // 快慢直接跟用量走（不跟带下限的幅度走，否则两边喘得一样快）：
            // 空闲时约 7 秒一轮，满格约 3 秒。
            // 两侧相位差半个周期，于是整圈光芒左右摇曳，而不是一起胀缩
            var rate = 0.9 + 1.1 * u;
            var breath = 0.08 + 0.92 * Math.Sin(_t * rate + (dirAngle == 0 ? Math.PI : 0));
            for (int i = 0; i < RayCount; i++)
            {
                var delta = WrapPi(RayAngle(i) - dirAngle);
                outv[i] += GaugeMaxPull * k * breath * Math.Pow(Math.Max(0, Math.Cos(delta)), 1.4);
            }
        }
        for (int i = 0; i < RayCount; i++) outv[i] = Math.Min(outv[i], RayPullCap);
        return outv;
    }

    /// <summary>
    /// 卡片边缘映光：左上亮、右下淡的一圈描边。与 macOS 版 PetView.drawCardEdge() 对齐。
    /// 没有这一圈的话，深色下卡片几乎和桌面糊在一起、看不出边界在哪。
    /// 跟着展开进度一起淡入，收起时不画。
    /// </summary>
    private static void DrawCardEdge(DrawingContext ctx, Rect bounds, double e)
    {
        if (e <= 0.01 || bounds.Width <= 2 || bounds.Height <= 2) return;
        var a = Sundial.App.Theme.EaseInOut(Math.Clamp(e / 0.45, 0, 1));
        var r0 = Math.Min(bounds.Width, bounds.Height) / 2;
        var rad = Math.Min(r0 + (CardRadius - r0) * e, r0);
        const double w = 1.4;
        var hi = Sundial.App.Theme.WithAlpha(Colors.White, (Theme.IsDark ? 0.55 : 0.95) * a);
        var lo = Sundial.App.Theme.WithAlpha(
            Theme.IsDark ? Colors.White : Color.FromRgb(140, 140, 140),
            (Theme.IsDark ? 0.03 : 0.14) * a);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),   // 亮部压在左上角
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(hi, 0),
                new GradientStop(lo, 0.72),
            },
        };
        // 描边是骑在路径上的，往里收半个线宽，免得外侧半条被窗口边缘切掉
        var r = bounds.Deflate(w / 2);
        var rr = Math.Max(0, rad - w / 2);
        ctx.DrawRectangle(null, new Pen(brush, w), r, rr, rr);
    }

    // MARK: 画一帧

    public void Render(DrawingContext ctx, Rect bounds)
    {
        _blockRects.Clear();
        _loginButtonRect = default;
        // 与 macOS 版 PetView.drawCardEdge() 对齐

        // 玻璃已被隐藏，这里补一个不透明背板，保证可读。
        // 但完全收起时同样不画——闲着只剩一颗太阳，没有内容需要背板托底。
        // 少了这道门的话，空闲时桌面上会是一颗太阳垫在 88×88 的深灰实心圆盘上；
        // 而且 WindowBackground 是 FromRgb（alpha 恒 255），必须自己乘淡入系数，
        // 否则展开动画期间背板是「啪」地出现的
        var e0 = ExpandProgress;
        if (ReduceTransparency && e0 > 0.01)
        {
            var r0 = Math.Min(bounds.Width, bounds.Height) / 2;
            var radius0 = r0 + (CardRadius - r0) * e0;
            var backAlpha = Sundial.App.Theme.EaseInOut(Math.Clamp(e0 / 0.45, 0, 1));
            ctx.DrawRectangle(
                new SolidColorBrush(Sundial.App.Theme.WithAlpha(Theme.WindowBackground, backAlpha)),
                null, bounds,
                Math.Min(radius0, bounds.Width / 2),
                Math.Min(radius0, bounds.Height / 2));
        }
        DrawCardEdge(ctx, bounds, e0);

        // 卡片底由窗口层的半透明材质负责，这里只画内容
        var card = bounds;
        var e = ExpandProgress;
        var cardMidX = card.X + card.Width / 2;
        var cardMidY = card.Y + card.Height / 2;

        var rowMidY = card.Y + 10 + TopRowH / 2;
        // 太阳始终居中，两个仪表分居左右
        var petY = cardMidY + (rowMidY - cardMidY) * e;
        var sunAt = new Point(cardMidX, petY);
        DrawPet(ctx, sunAt);

        // 仪表要比窗口先淡出：等窗口都快收窄到只剩太阳了它还在，
        // 就会被窗口边缘生生切掉，看着像「啪」地消失而不是渐隐。
        // 同时略微缩小，读起来是「收回去了」而不是「被裁掉了」
        var g = Theme.EaseInOut(Math.Max(0, (e - 0.34) / 0.66));
        // 没有任何用量数据（未登录 / 无订阅）就不画那两个空圈，
        // 留两个空轨道在那儿只会让人以为是坏了
        if (g > 0.004 && _model.Rows.Count > 0)
        {
            using (ctx.PushOpacity(g))
                DrawGauges(ctx, card, rowMidY, 0.84 + 0.16 * g);
        }
        if (e <= 0.01) return;   // 完全收起时只剩太阳

        var y = card.Y + 10 + TopRowH + 2;

        if (SoonestResetText() is { } soon)
        {
            Theme.DrawText(ctx, soon, new Rect(card.X + 10, y, card.Width - 20, 13),
                           10, FontWeight.Normal, Theme.SecondaryLabelColor,
                           TextAlignment.Center);
            y += ResetLineH;
        }

        if (_model.Loading)
        {
            Theme.DrawText(ctx, "正在获取用量…", new Rect(card.X, y + 6, card.Width, 16),
                           11, FontWeight.Normal, Theme.SecondaryLabelColor, TextAlignment.Center);
            return;
        }

        // 拿不到用量时**只有在没有会话可显示**的情况下才独占整张卡片。
        // 会话状态那半边读的是本地记录文件，跟登录和订阅都没关系——
        // 没有 Max/Pro 的人（授权页会直接拒绝）照样该看得到自己在跑什么。
        if (_model.Rows.Count == 0 && _model.ErrorMsg is { } msg && _blocks.Count == 0)
        {
            Theme.DrawText(ctx, msg, new Rect(card.X + 13, y + 4, card.Width - 26, 46),
                           10.5, FontWeight.Normal, Theme.SecondaryLabelColor,
                           TextAlignment.Center, wrap: true);
            if (_model.NeedsLogin)
            {
                // 至少 28pt 高，符合可点区域下限
                var btn = new Rect(cardMidX - 60, y + 52, 120, 30);
                _loginButtonRect = btn;
                ctx.DrawRectangle(new SolidColorBrush(Theme.CoralDeep), null, btn, 13, 13);
                Theme.DrawText(ctx, "双击登录", new Rect(btn.X, btn.Y + 6, btn.Width, 16),
                               11, FontWeight.SemiBold, Colors.White, TextAlignment.Center);
            }
            return;
        }

        // 正在运转的 + 已完成但未读的会话。
        // 每块占的高度按自己的出现进度收放，并裁进这个高度里——于是它是「卷起来」
        // 消失的，下面的块同步上滑，而不是整块凭空不见
        foreach (var b in _blocks)
        {
            var slotH = (BlockH + BlockGap) * b.Tw.Value;
            if (b.Tw.Value > 0.995)
            {
                DrawSessionBlock(ctx, b.S, y, card);
            }
            else if (slotH > 0.5)
            {
                var clip = new Rect(card.X, y, card.Width,
                                    Math.Max(0, slotH - BlockGap * b.Tw.Value));
                using (ctx.PushClip(clip))
                using (ctx.PushOpacity(b.Tw.Value))
                    DrawSessionBlock(ctx, b.S, y, card);
            }
            y += slotH;
        }

        // 详情随 HoverProgress 淡入淡出，并轻微上滑，跟窗口高度同步
        if (HoverProgress > 0.01)
        {
            using (ctx.PushOpacity(HoverProgress))
            using (ctx.PushTransform(Matrix.CreateTranslation(0, (1 - HoverProgress) * 6)))
                DrawDetails(ctx, y + 2, card);
        }
    }

    // MARK: 吉祥物

    private void DrawPet(DrawingContext ctx, Point center)
    {
        const double s = PetScale;
        double cx0 = center.X, cy0 = center.Y;
        var stress = _model.MaxPercent / 100.0;
        // 没有会话在跑就打盹：灰扑扑、闭眼、飘 zzz
        var sT = _sleepT;                  // 0 = 清醒，1 = 打盹；以下全部按它插值
        var breathe = 1 + 0.022 * Math.Sin(_breathPhase);

        var light = Sundial.App.Theme.Blend(Theme.CoralLight, sT, Theme.SleepLight);
        var deep = Sundial.App.Theme.Blend(Theme.CoralDeep, sT, Theme.SleepDeep);
        // 身体随用量连续加深。原来是过了 75% 才突然变，等于只有两档；
        // 改成一路渐深，扫一眼颜色就知道大概用了多少，不用去读数字。
        // 取 1.5 次幂：用量低时几乎不变色，高位才明显压暗
        // 睡着时也保留用量信号。**只剩一颗太阳的时候，恰恰是没有别的东西可看的时候**——
        // 原来这里把颜色全关掉，等于在最需要它的场合什么都读不到
        // （macOS 版实测：10% 和 99% 渲染出来一模一样）
        var tint = Math.Pow(Math.Clamp(stress, 0, 1), 1.2) * (0.62 + 0.13 * sT);
        // 目标色用固定的深砖红，不能用 GaugeAlert——那个随明暗切换，
        // 深色模式下反而更亮，越紧张身体越浅，正好反了。
        // 上半只加深四成、下半加满：身体本来就是上浅下深，脸长在偏上的位置；
        // 全身一起压暗的话，深红底配深褐五官，对比度会掉到 2.5:1（图形下限 3:1）
        var deepenTo = Sundial.App.Theme.Blend(Theme.SunDeepen, sT, Theme.SleepDeepen);
        var bodyLight = Theme.Blend(light, tint * 0.4, deepenTo);
        var bodyDeep = Theme.Blend(deep, tint, deepenTo);

        _petCenter = center;   // 供下一帧的引力计算使用（必须是未偏移的中心，否则会自激）
        _hasPetCenter = true;
        // 整只朝鼠标挪一点。放在 _petCenter 赋值之后，偏移只影响画面不影响算力场
        double cx = cx0 + _bodyLean.X, cy = cy0 + _bodyLean.Y;

        // 朝哪一侧的光芒，就染上那个仪表的颜色，深浅跟着它的用量走。
        // 于是「太阳往左边被拽过去、而且左边那半是红的」＝ 左边那条限额快满了，
        // 光看太阳就够了，不用去读两个圈里的数字。
        // 各侧的光芒尖端渐变成那一侧仪表的固定强调色——太阳伸手「够」到仪表，
        // 颜色在那里接上。颜色不再跟用量走（见 Theme.cs 里的说明）
        var (tintOuter, tintInner) = _model.RingRows;
        var tintSides = new List<(double Angle, Color Color, double Amount)>(2);
        foreach (var (sideAngle, row) in new (double, UsageRow?)[] { (Math.PI, tintOuter), (0, tintInner) })
        {
            if (row is null) continue;
            // sideAngle == PI 是朝左那一侧
            var glow = sideAngle > 1 ? Sundial.App.Theme.GlowLeft : Sundial.App.Theme.GlowRight;
            // 睡着时把发光色往睡眠灰里收一点——还认得出是金还是粉，但不刺眼
            var col = Sundial.App.Theme.Blend(glow, 0.25 * sT, Theme.SleepDeep);
            // **发光强度跟着这一侧的用量走**：越满越亮。
            // 这是空闲态唯一还能读出用量的通道——只剩一颗太阳时没有圈也没有数字，
            // 而灰身体压暗那点差别在 88pt 见方里根本看不出来。
            // 「越满越亮」也比「越满越暗」符合直觉，且不会重蹈深色淤青的覆辙。
            var u = Math.Clamp(row.Percent / 100.0, 0, 1);
            tintSides.Add((sideAngle, col, Math.Pow(u, 0.75)));
        }

        // 光芒：圆头短棒，思考时整圈缓慢转动；鼠标靠近时被「吸」得有长有短
        for (int i = 0; i < RayCount; i++)
        {
            var angle = (double)i / RayCount * 2 * Math.PI + Math.PI / 8 + _sunSpin;
            var wobble = (1 - sT) * 2.2 * s * Math.Sin(_t * 1.9 + i * 1.3);
            const double inner = 21 * s;
            // 反向排斥时不能把光芒缩没了，留个最短长度
            var outer = Math.Max(inner + 4 * s, (49 * s + wobble) * breathe + _rayPull[i]);
            // 被拉长的那几根同时略微变粗，「伸手去够」比单纯变长更像有劲
            var w = 16.5 * s * (1 + 0.2 * Math.Max(0, _rayPull[i]) / RayMaxPull);
            // 染色只上在**远端**，根部保持本色：颜色是从仪表盘那边「蹭」过来的，
            // 整根均匀上色反而看不出这层关系。伸得越长尖上越浓——
            // 于是呼吸把光芒推向仪表时尖端亮起来，收回来时又褪掉。
            // 两侧是**依次**叠上去的（先左后右），换顺序中间那几根的颜色就会变
            // 染色只上在**远端**，根部保持本色：颜色是从仪表盘那边「蹭」过来的，
            // 整根均匀上色反而看不出这层关系。伸得越长尖上越浓。
            // 两侧是**依次**叠上去的（先左后右），换顺序中间那几根的颜色就会变
            var tipColor = bodyDeep;
            foreach (var side in tintSides)
            {
                var a = Math.Pow(Math.Max(0, Math.Cos(WrapPi(angle - side.Angle))), 0.5) * side.Amount;
                if (a <= 0.01) continue;
                var reach = Math.Clamp(_rayPull[i] / GaugeMaxPull, 0, 1);
                tipColor = Sundial.App.Theme.Blend(tipColor, Math.Min(0.95, a * (0.72 + 0.28 * reach)), side.Color);
            }
            // 光芒是先建横向矩形再旋转，所以在**未旋转**的局部坐标里，
            // 渐变沿 +x 从根部走到尖端；内侧三成保持本色再开始过渡，
            // 颜色才是「聚在尖上」而不是整根渐变（根部本来也被身体挡着）
            IBrush rayBrush = tipColor == bodyDeep
                ? new SolidColorBrush(bodyDeep)
                : new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(bodyDeep, 0),
                        new GradientStop(bodyDeep, 0.32),
                        new GradientStop(tipColor, 1),
                    },
                };
            // 先旋转再平移：Avalonia 是行向量约定，A * B = 先 A 后 B
            using (ctx.PushTransform(Matrix.CreateRotation(angle) * Matrix.CreateTranslation(cx, cy)))
                ctx.DrawRectangle(rayBrush, null,
                                  new Rect(inner, -w / 2, outer - inner, w), w / 2, w / 2);
        }

        // 身体：渐变出毛绒团子的体积感。
        // 原文注释写的是「上浅下深」，但那是**意图**不是结果。NSGradient.draw(in:angle:)
        // 的角度按当前用户坐标系逆时针算，而 PetView 是 isFlipped 的，方向跟着一起翻。
        // 在本机离屏实测过（翻转视图里画 红=starting → 蓝=ending、angle:-90）：
        // 顶部偏蓝、底部偏红，即 **starting(浅) 落在底、ending(深) 落在顶**。
        // 这里按 macOS 的实际渲染结果对齐，不按那句注释——两个平台看起来必须一样。
        // 若日后确认 macOS 版要改成注释描述的样子，把下面 StartPoint / EndPoint 换回来即可。
        var r = 30 * s * breathe;
        var grad = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),   // 浅色（bodyLight）在底
            EndPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),     // 深色（bodyDeep）在顶
            GradientStops = new GradientStops
            {
                new GradientStop(bodyLight, 0),
                new GradientStop(bodyDeep, 1),
            },
        };
        ctx.DrawEllipse(grad, null, new Point(cx, cy), r, r);

        // 心情：0 = 轻松，1 = 快用满了。眉毛与嘴形跟着它走，
        // 光靠嘴角那点弧度在这个尺寸下根本看不出来
        var worry = Math.Clamp((stress - 0.5) / 0.35, 0, 1);

        // 豆豆眼
        var eyeBaseY = cy - 2 * s;
        var blinkT = _blinkUntil - _t;
        var lidClose = blinkT > 0 ? Theme.EaseInOut(1 - Math.Abs(blinkT / 0.16 - 0.5) * 2) : 0;
        // 眨眼和入睡合成同一个「闭合度」：入睡那 0.6 秒里眼睛是慢慢阖上的，
        // 不是突然换成一条弧线，所以椭圆压扁与弧线淡入有一段重叠。
        // 用满了则整体让位给 ✖
        var lid2 = Math.Max(lidClose, sT);
        var arcAlpha = Theme.EaseInOut(Math.Clamp((lid2 - 0.62) / 0.38, 0, 1));
        var dT = _deadT;
        foreach (var dx in new[] { -12.0 * s, 12.0 * s })
        {
            // 眼珠看向鼠标；闭眼时不偏，免得弧线歪掉
            var ex = cx + dx + _eyeShift.X * (1 - sT);
            var ey = eyeBaseY + _eyeShift.Y * (1 - sT);
            var h = 6 * s * (1 - lid2);
            if (h > 0.2 && arcAlpha < 1 && dT < 1)
            {
                // 纯豆豆眼，不点高光：这个尺寸下那点白只有 0.6pt，
                // 不是高光而是一粒噪点，把干净的剪影搞脏了
                ctx.DrawEllipse(new SolidColorBrush(
                    Theme.WithAlpha(Theme.FaceDark, (1 - arcAlpha) * (1 - dT))),
                    null, new Point(ex, ey), 2.4 * s, h / 2);
            }
            if (arcAlpha > 0.01 && dT < 1)
            {
                var lidCurve = Curve(new Point(ex - 3 * s, ey),
                                     new Point(ex - 1.5 * s, ey + 2.4 * s),
                                     new Point(ex + 1.5 * s, ey + 2.4 * s),
                                     new Point(ex + 3 * s, ey));
                ctx.DrawGeometry(null, RoundPen(
                    Theme.WithAlpha(Theme.FaceDark, arcAlpha * (1 - dT)), 1.6 * s), lidCurve);
            }
            if (dT > 0.01)
            {
                var xr = 3.2 * s;
                var xPen = RoundPen(Theme.WithAlpha(Theme.FaceDark, dT), 1.9 * s);
                ctx.DrawLine(xPen, new Point(ex - xr, ey - xr), new Point(ex + xr, ey + xr));
                ctx.DrawLine(xPen, new Point(ex - xr, ey + xr), new Point(ex + xr, ey - xr));
            }
        }

        // 眉毛：只在开始紧张后才长出来，内高外低（「/ \」）＝担心的样子。
        // 这是三种心情里最一眼能认出来的差别
        if (worry * (1 - sT) > 0.02)
        {
            var lift = 2.4 * s * worry;
            var browY = eyeBaseY - 6.5 * s;
            var browPen = RoundPen(Theme.WithAlpha(Theme.FaceDark, worry * (1 - sT)), 1.7 * s);
            foreach (var dx in new[] { -12.0 * s, 12.0 * s })
            {
                var ex = cx + dx;
                var innerX = dx < 0 ? ex + 3.2 * s : ex - 3.2 * s;
                var outerX = dx < 0 ? ex - 3.2 * s : ex + 3.2 * s;
                ctx.DrawLine(browPen, new Point(outerX, browY + lift), new Point(innerX, browY - lift));
            }
        }

        // 嘴：开心是咧开的大弧，紧张是抿平，用满了是明显的倒弧
        var my = cy + 6.5 * s;
        var mouthPen = RoundPen(Theme.WithAlpha(Theme.FaceDark, 1 - sT), 1.7 * s);
        if (sT > 0.01)
        {
            // 原版 NSRect(x: cx-2s, y: my-0.5s, w: 4s, h: 5s) 的椭圆描边
            ctx.DrawEllipse(null, RoundPen(Theme.WithAlpha(Theme.FaceDark, sT), 1.4 * s),
                            new Point(cx, my + 2 * s), 2 * s, 2.5 * s);
        }
        if (stress < 0.5)
        {
            // 张得更开、弯得更深，还带两个上翘的嘴角；鼠标靠近时笑得更开
            // 控制点从 ±2.6 移到 ±4.8：靠得太近会把曲线拽成尖底的 V，
            // 往外挪才是圆润的 U
            var grin = 4.9 * s + 1.8 * s * _perk;
            ctx.DrawGeometry(null, mouthPen, Curve(
                new Point(cx - 6.4 * s, my - 1.2 * s),
                new Point(cx - 4.8 * s, my + grin),
                new Point(cx + 4.8 * s, my + grin),
                new Point(cx + 6.4 * s, my - 1.2 * s)));
        }
        else if (stress < 0.8)
        {
            ctx.DrawLine(mouthPen, new Point(cx - 4.2 * s, my + 1.6 * s),
                         new Point(cx + 4.2 * s, my + 1.6 * s));   // 抿成一条线
        }
        else
        {
            ctx.DrawGeometry(null, mouthPen, Curve(
                new Point(cx - 5.6 * s, my + 4.2 * s),
                new Point(cx - 2.4 * s, my - 1.4 * s),
                new Point(cx + 2.4 * s, my - 1.4 * s),
                new Point(cx + 5.6 * s, my + 4.2 * s)));
        }

        if (sT > 0.01)
        {
            for (int i = 0; i < 3; i++)
            {
                var phase = (_t * 0.42 + i * 0.33) % 1.0;
                var fade = Theme.EaseInOut(phase < 0.5 ? phase * 2 : (1 - phase) * 2);
                var size = 9 + i * 2;
                var zx = cx + 26 * s + i * 9 * s + phase * 6;
                var zy = cy - 24 * s - phase * 18 - i * 6 * s;
                var rect = new Rect(zx, zy, 20, size + 6);
                Theme.DrawText(ctx, "z", new Rect(rect.X + 1, rect.Y + 1, rect.Width, rect.Height),
                               size, FontWeight.Bold, Theme.WithAlpha(Theme.FaceDark, fade * 0.55 * sT));
                Theme.DrawText(ctx, "z", rect, size, FontWeight.Bold,
                               Theme.WithAlpha(Theme.LabelColor, fade * 0.8 * sT));
            }
        }
    }

    /// <summary>内环下方的小标签：全部模型显示「每周」，专属限额显示模型名。</summary>
    private static string WeeklyShortName(UsageRow? row)
    {
        var l = row?.Label;
        if (l is null) return "每周";
        if (l.Contains("全部模型")) return "每周";
        return l.Replace("每周 · ", "");
    }

    // MARK: 两个并排仪表（已用比例）


    private void DrawGauges(DrawingContext ctx, Rect card, double midY, double scale)
    {
        var r = 21 * scale;
        var lw = 5 * scale;
        var (ringOuter, ringInner) = _model.RingRows;
        // 左仪表 — 太阳 — 右仪表，三等分居中
        var gauges = new (UsageRow? Row, string Name, double Cx)[]
        {
            (ringOuter, "5小时", card.X + card.Width * 0.17),
            (ringInner, WeeklyShortName(ringInner), card.Right - card.Width * 0.17),
        };
        for (int k = 0; k < gauges.Length; k++)
        {
            var (row, name, cx) = gauges[k];
            var center = new Point(cx, midY);
            if (row is null)
            {
                DrawArc(ctx, center, r, lw, 0, 360, Theme.WithAlpha(Theme.LabelColor, 0.14));
                continue;
            }
            var shown = _ringShown[k];
            DrawArc(ctx, center, r, lw, 0, 360, Theme.WithAlpha(Theme.LabelColor, 0.14));
            if (shown > 0.002)
            {
                // 从正上方（-90°）顺时针填充。这个旋向是**刻意**选的：
                // Avalonia 的 y 轴向下，从 -90° 起角度递增，落点先向右再向下，
                // 屏幕上就是顺时针；配 SweepDirection.Clockwise 两者一致。
                // （macOS 版在 isFlipped 视图里踩过坑：那边 clockwise:true 画出来反而是逆时针，
                //  最后同样靠「角度递增」得到顺时针。结论一样，理由不一样，别照抄那边的参数。）
                DrawArc(ctx, center, r, lw, -90, -90 + 360 * shown,
                        k == 0 ? Sundial.App.Theme.RingLeft : Sundial.App.Theme.RingRight,
                        round: true);
            }
            // 11pt 的行高约 13pt，原来数字框从 midY-10 起、标签框从 midY+3 起，
            // 正好首尾相接，两行字贴在一起。整体上移并留出 2.6pt 间距
            Theme.DrawText(ctx, $"{row.Percent}%", new Rect(cx - 22, midY - 13, 44, 14),
                           11, FontWeight.SemiBold, Theme.LabelColor,
                           TextAlignment.Center, monoDigits: true);
            Theme.DrawText(ctx, name, new Rect(cx - 22, midY + 2.6, 44, 11),
                           9, FontWeight.Normal, Theme.SecondaryLabelColor, TextAlignment.Center);
        }
    }

    /// <summary>统一的画弧。角度是度数，0° 在正右，**递增 = 屏幕上顺时针**。</summary>

    private static void DrawArc(DrawingContext ctx, Point center, double radius, double lineWidth,
                                double fromDeg, double toDeg, Color color, bool round = false)
    {
        var pen = new Pen(new SolidColorBrush(color), lineWidth, null,
                          round ? PenLineCap.Round : PenLineCap.Flat, PenLineJoin.Round);
        var sweep = toDeg - fromDeg;
        // 整圈用 DrawEllipse：ArcTo 的起点终点重合时是退化情形，各后端表现不一
        if (Math.Abs(sweep) >= 359.99)
        {
            ctx.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(OnCircle(center, radius, fromDeg), false);
            // 拆成每段不超过 90°，就永远用不上 isLargeArc，省掉一处容易搞反的开关
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / 90.0));
            var dir = sweep >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise;
            for (int i = 1; i <= steps; i++)
            {
                var a = fromDeg + sweep * i / steps;
                c.ArcTo(OnCircle(center, radius, a), new Size(radius, radius), 0, false, dir);
            }
            c.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private static Point OnCircle(Point center, double radius, double deg)
    {
        var a = deg * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
    }

    private static Pen RoundPen(Color color, double thickness) =>
        new(new SolidColorBrush(color), thickness, null, PenLineCap.Round, PenLineJoin.Round);

    private static StreamGeometry Curve(Point from, Point c1, Point c2, Point to)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(from, false);
            c.CubicBezierTo(c1, c2, to);
            c.EndFigure(false);
        }
        return g;
    }

    // MARK: 会话块

    private void DrawSessionBlock(DrawingContext ctx, SessionActivity s, double y, Rect card)
    {
        var box = new Rect(card.X + 9, y, card.Width - 18, BlockH);
        _blockRects.Add((s.Id, box));

        ctx.DrawRectangle(new SolidColorBrush(Theme.WithAlpha(Theme.LabelColor, s.Busy ? 0.09 : 0.06)),
                          null, box, 10, 10);

        var title = string.IsNullOrEmpty(s.Title) ? "Claude Code" : s.Title;
        Theme.DrawText(ctx, title, new Rect(box.X + 10, box.Y + 4, box.Width - 40, 14),
                       10.5, FontWeight.SemiBold,
                       s.Busy ? Theme.LabelColor : Theme.SecondaryLabelColor);

        string sub;
        var subColor = Theme.SecondaryLabelColor;
        if (s.Waiting)
        {
            var el = Format.Elapsed(s.Since);
            sub = el.Length == 0 ? "等你选择" : $"等你选择 · {el}";
            subColor = Theme.LabelColor;        // 醒目交给右侧呼吸圆点，文字保证可读
        }
        else if (s.Background)
        {
            var el = Format.Elapsed(s.Since);
            sub = el.Length == 0 ? "后台任务运行中" : $"后台任务 · {el}";
        }
        else if (s.Busy)
        {
            var el = Format.Elapsed(s.Since);
            sub = el.Length == 0 ? "正在思考" : $"正在思考 · {el}";
        }
        else if (s.Stalled)
        {
            // 只是很久没有新记录了，不确定跑没跑完，别谎报「已完成」
            var el = Format.Elapsed(s.FinishedAt);
            sub = el.Length == 0 ? "无响应" : $"无响应 · 已 {el} 无更新";
        }
        else
        {
            sub = "未读 · " + Format.Ago(s.FinishedAt);
        }
        Theme.DrawText(ctx, sub, new Rect(box.X + 10, box.Y + 18, box.Width - 40, 13),
                       9, s.Waiting ? FontWeight.SemiBold : FontWeight.Normal, subColor);

        // 上下文占用：一行文字 + 一条细进度条
        if (s.CtxLimit > 0 && s.CtxTokens > 0)
        {
            var frac = Math.Min(1, (double)s.CtxTokens / s.CtxLimit);
            // Swift 的 .rounded() 是「四舍五入、遇 .5 远离零」；C# Math.Round 默认是银行家舍入
            // （.5 取偶），49.5% 那种整半的情形两边会差 1，必须显式指定
            var pct = (int)Math.Round(frac * 100, MidpointRounding.AwayFromZero);
            var barY = box.Y + BlockH - 8;
            var barX = box.X + 10;
            var barW = box.Width - 20;

            Theme.DrawText(ctx, $"上下文 {Format.Tokens(s.CtxTokens)} / {Format.Tokens(s.CtxLimit)}",
                           new Rect(barX, barY - 12, barW - 30, 11),
                           9.5, FontWeight.Normal, Theme.LabelColor);
            Theme.DrawText(ctx, $"{pct}%", new Rect(barX + barW - 30, barY - 12, 30, 11),
                           9.5, FontWeight.Medium, Theme.LabelColor,
                           TextAlignment.Right, monoDigits: true);

            ctx.DrawRectangle(new SolidColorBrush(Theme.WithAlpha(Theme.LabelColor, 0.14)), null,
                              new Rect(barX, barY, barW, 3), 1.5, 1.5);
            if (frac > 0.004)
            {
                // 上下文进度条并进珊瑚族，不再单独用一套绿/琥珀/红。
                // 过 60% 之后往深砖红压，仍然有「快满了」的提示，
                // 但用的是太阳身体加深那同一个色，不引入新色相
                var heat = Math.Clamp((frac - 0.6) / 0.4, 0, 1);
                var barCol = Sundial.App.Theme.Blend(Theme.CoralDeep, heat * 0.75, Theme.SunDeepen);
                ctx.DrawRectangle(new SolidColorBrush(barCol), null,
                                  new Rect(barX, barY, Math.Max(3, barW * frac), 3), 1.5, 1.5);
            }
        }

        var cx = box.Right - 15;
        var cy = box.Y + 15;
        if (s.Waiting)
        {
            // 等待输入：呼吸的实心圆点，比转圈更像「在等你」
            var pulse = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(_t * 3.4));
            // 等待输入也用珊瑚族：它和「在跑」的区别靠形状（实心呼吸点 vs 转圈），
            // 不必再多一个色相
            ctx.DrawEllipse(new SolidColorBrush(Theme.WithAlpha(Theme.CoralDeep, pulse)), null,
                            new Point(cx, cy), 5, 5);
        }
        else if (s.Busy)
        {
            DrawSpinner(ctx, new Point(cx, cy), 7);
        }
        else
        {
            // 未读圆点，缓慢呼吸；点一下即消
            var pulse = 0.55 + 0.45 * Theme.EaseInOut((Math.Sin(_t * 1.6) + 1) / 2);
            ctx.DrawEllipse(new SolidColorBrush(Theme.WithAlpha(Theme.CoralLight, pulse)), null,
                            new Point(cx, cy), 4, 4);
        }
    }

    /// <summary>首尾无缝的转圈：弧长在生长与收缩之间循环，相位归一化，wrap 处完全连续。</summary>
    private void DrawSpinner(DrawingContext ctx, Point center, double radius)
    {
        DrawArc(ctx, center, radius, 2.2, 0, 360, Theme.WithAlpha(Theme.LabelColor, 0.14));

        // 尾角每周期正好走满 360°，弧长按余弦在 26°–290° 之间振荡（首尾导数为 0），
        // 因此 phase 回绕处角度与弧长都完全连续，接得上。
        var p = _spinPhase;
        var sweep = 26 + 264 * (1 - Math.Cos(2 * Math.PI * p)) / 2;
        var tail = -90 + p * 360;        // 角度递增 = 屏幕上顺时针
        DrawArc(ctx, center, radius, 2.2, tail, tail + sweep, Theme.CoralLight, round: true);
    }

    // MARK: 悬停详情

    private void DrawDetails(DrawingContext ctx, double startY, Rect card)
    {
        var innerX = card.X + 13;
        var innerW = card.Width - 26;
        var y = startY;

        Theme.DrawText(ctx, "Claude 用量", new Rect(innerX, y, innerW * 0.6, 13),
                       9.5, FontWeight.SemiBold, Theme.LabelColor);
        if (_model.Tier.Length > 0)
        {
            Theme.DrawText(ctx, _model.Tier, new Rect(innerX + innerW * 0.4, y, innerW * 0.6, 13),
                           9.5, FontWeight.Normal, Theme.SecondaryLabelColor, TextAlignment.Right);
        }
        y += 19;

        // 圆点的颜色标的是**这一条对应哪个仪表**，不是用量高低——和圆环同一套规则。
        // 之前这里还按 50/80 三档换色，圆环却已经改成固定色了，两处规则打架：
        // 同一个 60%，圆环是杏粉、列表里却是琥珀，看着像两套系统。
        // 没上仪表的那几条给中性灰，一眼能看出「这条没画成圈」。
        var (shownOuter, shownInner) = _model.RingRows;
        if (_model.Rows.Count == 0)
        {
            Theme.DrawText(ctx, _model.NeedsLogin ? "未登录，只显示会话状态" : "暂时取不到用量",
                           new Rect(innerX, y, innerW, 14),
                           9.5, FontWeight.Normal, Theme.SecondaryLabelColor);
            y += 15;
        }
        foreach (var row in _model.Rows)
        {
            var c = row.Label == shownOuter?.Label ? Sundial.App.Theme.RingLeft
                  : row.Label == shownInner?.Label ? Sundial.App.Theme.RingRight
                  : Sundial.App.Theme.TertiaryLabelColor;
            // 原版这里先把 6×6 的圆设成剪裁区再填同一个圆，等价于直接画个实心圆点
            ctx.DrawEllipse(new SolidColorBrush(c), null, new Point(innerX + 3, y + 7), 3, 3);
            Theme.DrawText(ctx, row.Label, new Rect(innerX + 11, y, innerW - 11 - 96, 14),
                           9.5, FontWeight.Normal, Theme.SecondaryLabelColor);
            // 数字不再按用量换色：颜色已经不承担「多满」这个信息了
            Theme.DrawText(ctx, $"{row.Percent}%", new Rect(innerX + innerW - 96, y, 40, 14),
                           9.5, FontWeight.Medium, Theme.LabelColor, TextAlignment.Right, monoDigits: true);
            Theme.DrawText(ctx, Usage.CompactReset(row.ResetAt), new Rect(innerX + innerW - 54, y, 54, 14),
                           9.5, FontWeight.Normal, Theme.SecondaryLabelColor, TextAlignment.Right);
            y += 15;
        }

        string footer;
        if (_model.ErrorMsg is { } msg)
        {
            footer = "⚠︎ " + msg.Split('\n')[0];
        }
        else if (_model.LastFetch is { } last)
        {
            var mins = (int)(DateTimeOffset.Now - last).TotalMinutes;
            footer = mins <= 0 ? "刚刚更新" : $"{mins} 分钟前更新";
        }
        else
        {
            footer = "";
        }
        Theme.DrawText(ctx, footer, new Rect(innerX, y + 3, innerW, 12), 9.5, FontWeight.Normal,
                       _model.ErrorMsg is null ? Theme.TertiaryLabelColor : Theme.SecondaryLabelColor);
    }
    // 紧凑重置时间（"4h32m" / "周四 14:00"）原本在这里放了一份私有实现，
    // 现在 Sundial.Core 的 Usage.CompactReset 已经提供，改成直接调它——
    // 同一个格式留两份实现，早晚会各改各的。
}
