// Sundial (Windows 版) — 桌宠窗口外壳
//
// 对应 macOS 版 App.swift 里 PetWindow / AppDelegate 的窗口部分：
// 无边框浮动窗、按住拖动、悬停展开、右键菜单、登录框、开机自启、动画节拍器。
// 绘制本身全部交给 PetRenderer，这里只管「窗口该多大、放哪儿、什么时候重绘」。
//
// 与 macOS 版最大的坐标系差异：AppKit 的屏幕坐标原点在左下角、Y 向上，所以那边
// 每次改高度都要拿 anchorTopY 反算 origin.y，否则窗口会从底边往上长。
// Win32 / Avalonia 的 Position 是左上角、Y 向下：高度增加时左上角不动，天然向下伸展。
// 但「锚点」这个概念必须保留（见 ApplyAnchoredPosition）：贴着屏幕底边时窗口要临时
// 上移让出空间，窗口收回去以后还得回到用户原来放的位置——只做单向夹取的话，
// 桌宠会一次次被顶上去再也下不来。

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Win32;
using Sundial.Core;

namespace Sundial.App;

/// <summary>换取令牌的结果。<paramref name="Message"/> 非空时要弹给用户看
/// （失败原因，或者「登录成功但令牌没存住」这类成功但要提醒的情况）。</summary>
public readonly record struct LoginOutcome(bool Ok, string? Message);

/// <summary>
/// 只干一件事：把 Avalonia 的 Render 转发给 PetRenderer，另外在它下面垫一层卡片底。
/// 单独抽一个 Control 而不是直接在 Window 上重写 Render，是为了让窗口保持
/// 「一个普通容器」的身份，拖动、右键菜单这些附着在窗口上的行为不受自绘影响。
/// </summary>
internal sealed class PetSurface : Control
{
    private readonly PetRenderer _renderer;
    private readonly PetModel _model;

    /// <summary>窗口层告诉我们「系统模糊已经生效」。生效时自绘底要让位，
    /// 否则半透明底叠在亚克力上，等于给模糊蒙了一层灰。</summary>
    public bool SystemBlurActive { get; set; }

    public PetSurface(PetRenderer renderer, PetModel model)
    {
        _renderer = renderer;
        _model = model;
        // 指针事件统一由窗口处理（窗口整块都是可点区域），这里让开，免得命中测试打架
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        // 「降低透明度」时不画玻璃底：PetRenderer 会自己补一块不透明背板，
        // 两层叠着画等于白画一遍，还会把它那块背板的颜色带偏
        // 完全收起时整块底退场，只剩一颗太阳浮在桌面上——闲着的时候没有信息
        // 要承载，那块底纯属多余
        if (!_renderer.ReduceTransparency && _renderer.ExpandProgress > 0.01)
            DrawBackdrop(context, bounds);
        _renderer.Render(context, bounds);
    }

    /// <summary>
    /// 卡片底 —— 这是 macOS 版「液态玻璃」(NSGlassEffectView) 的近似，<b>不是原生效果</b>。
    ///
    /// 为什么默认自绘：Windows 的 AcrylicBlur 是按窗口<b>整块矩形</b>生效的，而这个窗口
    /// 收起态是一颗直径 88 的圆、展开态是 26 圆角的卡片，形状每帧都在变。系统模糊不跟着
    /// 形状走，圆角外那一圈会露出方形的模糊底，比不开还难看。所以默认走自绘的半透明圆角底
    /// + 一圈边缘高光；想试原生模糊的，右键菜单里有「系统模糊背景（实验）」开关。
    /// 自绘缺的是折射与背景采样，只有「半透明 + 边缘高光」，观感比 macOS 版弱一档。
    /// </summary>
    private void DrawBackdrop(DrawingContext context, Rect bounds)
    {
        var e = Math.Clamp(_renderer.ExpandProgress, 0, 1);
        // 收起时半径 = 边长的一半（正圆），展开时收敛到 26，中间线性插值——
        // 这一步必须跟着 ExpandProgress 走，否则形状会和内容的展开动画脱节
        var compactR = Math.Min(bounds.Width, bounds.Height) / 2;
        var radius = compactR + (PetRenderer.CardRadius - compactR) * e;
        // 底要比窗口先退场：等窗口都收到只剩太阳大小了才开始淡，
        // 就会看见一个明显的圆形色块「啪」地不见。0.45 之前走完
        var backdropAlpha = Sundial.App.Theme.EaseInOut(Math.Clamp(e / 0.45, 0, 1));
        if (backdropAlpha < 0.004) return;

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        // 系统模糊开着时底色要退到几乎只剩一点点压暗，把亮度交还给亚克力
        var fillAlpha = (SystemBlurActive ? (dark ? 0x33 : 0x4D) : (dark ? 0x6E : 0xA8)) * backdropAlpha;
        var fill = dark
            ? new SolidColorBrush(Color.FromArgb((byte)fillAlpha, 0x1C, 0x1C, 0x1E))
            : new SolidColorBrush(Color.FromArgb((byte)fillAlpha, 0xFF, 0xFF, 0xFF));
        var edge = dark
            ? new SolidColorBrush(Color.FromArgb((byte)(0x2E * backdropAlpha), 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb((byte)(0x59 * backdropAlpha), 0xFF, 0xFF, 0xFF));

        // 描边往里收半个像素，否则 1px 的线有一半落在窗口外被切掉，看起来粗细不匀
        var r = bounds.Deflate(0.5);
        var rr = Math.Max(0, radius - 0.5);
        context.DrawRectangle(fill, new Pen(edge, 1), r, rr, rr);

        // 有会话在等你选择时给卡片染一点暖色，让它自己「亮」起来——对应 macOS 版
        // applyGlassShape 里给玻璃设 tintColor 的那一步。那边是玻璃材质带着这个色去折射，
        // 这里只能再叠一层半透明暖色，是近似；常态一律不染色，让它跟随系统明暗。
        // 写全名 Sundial.App.Theme：Control 自己有个 Theme 属性（ControlTheme），会挡住它
        if (_model.Sessions.Any(s => s.Waiting))
        {
            var tint = new SolidColorBrush(Sundial.App.Theme.WithAlpha(Sundial.App.Theme.CoralDeep, 0.20 * backdropAlpha));
            context.DrawRectangle(tint, null, r, rr, rr);
        }
    }
}

public sealed class MainWindow : Window
{
    // MARK: 布局常量
    //
    // 版式常量一律引 PetRenderer 的（TopRowH / BlockGap / CompactSide / CardRadius），
    // 不在这里另抄一份——抄一份就意味着改版式时必然有一处忘了改，窗口高度和内容对不上。
    // 只有下面这两个是纯窗口层的量，PetRenderer 用不到。
    private const double WinW = 198;
    private const double WinH = 182;           // 初始（加载态）高度，之后随内容自适应

    // MARK: 数据层接线口
    //
    // 取数、会话轮询、OAuth 都在 Sundial.Core 里，由 App.OnFrameworkInitializationCompleted
    // 一次性接上（见 App.axaml.cs 的接线区）。窗口只认这几个委托，不直接 new 任何数据层对象——
    // 这样整个外壳能脱离网络与磁盘单独跑起来看动画，接线没接上也不会崩。

    /// <summary>
    /// 返回授权页 URL。<b>同一次登录期间必须返回同一个 URL</b>（也就是同一个 PKCE verifier）：
    /// 每次点登录都换新的话，用户从上一个授权页（浏览器很容易留着旧标签）复制的码就永远
    /// 对不上，表现为「反复登录失败」。登录成功之后才允许换新的。
    /// </summary>
    public Func<string?>? AuthorizeUrlProvider;

    /// <summary>拿授权码换令牌。失败不要抛异常，把给用户看的原因放进 <see cref="LoginOutcome.Message"/>。</summary>
    public Func<string, Task<LoginOutcome>>? ExchangeCodeAsync;

    /// <summary>当前有没有可用令牌（决定菜单里显示「登录」还是「重新登录 / 退出登录」）。</summary>
    public Func<bool>? HasTokenProvider;

    public Action? SignOutRequested;
    public Action? ForceRefreshRequested;
    public Func<Task>? FetchUsageAsync;      // 15 秒一次
    public Func<Task>? PollActivityAsync;    // 0.8 秒一次，读 ~/.claude 下的会话记录

    /// <summary>把某个会话标记为已读。必须落到数据层，否则下一轮轮询它又会冒出来。</summary>
    public Action<string>? MarkReadRequested;

    private readonly PetModel _model;
    private readonly PetRenderer _renderer;
    private readonly PetSurface _surface;
    private readonly ShellSettings _settings;

    private readonly DispatcherTimer _animTimer = new();
    private readonly DispatcherTimer _fetchTimer = new();
    private readonly DispatcherTimer _activityTimer = new();
    private readonly DispatcherTimer _saveTimer = new();   // 位置落盘的防抖
    private double _animFps;
    private bool _activityPolling;   // 上一轮磁盘轮询还没回来就别再压一轮（对应 Swift 的 activityPolling）
    private bool _adjusting;         // 程序化改位置时不当成用户拖动
    private bool _dragging;          // 用户正按住窗口拖，这期间不许跟他抢位置
    private bool _loginInProgress;   // 只在 UI 线程读写，防止并发登录互相覆盖
    private LoginWindow? _loginWindow;
    private int _dialogs;            // 打开着的模态提示数量，见 PushDialog
    private PixelPoint? _anchor;     // 用户放桌宠的位置（左上角），伸缩时以它为准
    private PixelPoint? _selfMoved;  // 最近一次由我们自己发起的移动，用来识别回声事件
    private Point? _pointerInWindow;             // 指针在窗口内的位置（非 Windows 平台的退路）
    private DateTimeOffset? _hoverSince;         // 悬停起点，停留够久就当你看过了
    private readonly HashSet<string> _seenWhileHovering = new();

    private readonly List<MenuEntry> _entries;
    private readonly List<(NativeMenuItem Item, MenuEntry Entry)> _nativeItems = new();
    private readonly List<(MenuItem Item, MenuEntry Entry)> _contextItems = new();

    public MainWindow(PetModel model, PetRenderer renderer)
    {
        _model = model;
        _renderer = renderer;
        _settings = ShellSettings.Load();
        _model.DetailsPinned = _settings.DetailsPinned;

        Title = "Sundial";
        // 界面整个是自绘的，读屏软件从可视树里什么也看不到。原文在 PetView 上手工搭了一棵
        // 无障碍元素树（两个仪表 / 登录按钮 / 每个会话块各是一个可按下的元素，见
        // accessibilityChildren），那一套 AutomationPeer 这边还没有移植；至少先把窗口自己的
        // 名字报出去，对应原文的 accessibilityLabel「Claude 用量与会话状态」。
        AutomationProperties.SetName(this, "Claude 用量与会话状态");
        SystemDecorations = SystemDecorations.None;   // 无边框：整块窗口就是那只太阳
        Background = Brushes.Transparent;             // 圆角外必须是真透明，否则露出黑/白方块
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;         // 尺寸完全由 ApplyDesiredSize 接管
        ShowActivated = false;                        // 启动时不抢焦点；点击时的不抢焦点见 ApplyNoActivate
        WindowStartupLocation = WindowStartupLocation.Manual;
        // macOS 版特意关掉了系统投影：窗口一直在伸缩变形，投影会残留成一圈方框黑边。
        // Avalonia 的 SystemDecorations.None 本来就没有投影，这里不用额外处理。

        ApplyTransparencyHint();
        ApplyAppearance();

        _surface = new PetSurface(renderer, model);
        // 窗口整块都要能点（包括圆角外的空白：那里也属于窗口矩形，拖动时手感更连贯），
        // 所以垫一个透明背景的 Panel —— Avalonia 里 Background=null 的容器不参与命中测试。
        Content = new Panel
        {
            Background = Brushes.Transparent,
            Children = { _surface },
        };

        _entries = BuildMenuEntries();
        ContextMenu = BuildContextMenu();

        PointerPressed += OnPointerPressed;
        PointerEntered += (_, _) => SetHovered(true);
        PointerExited += (_, _) => { _pointerInWindow = null; SetHovered(false); };
        PointerMoved += (_, e) => _pointerInWindow = e.GetPosition(this);
        PositionChanged += OnPositionChanged;
        ActualThemeVariantChanged += (_, _) => ApplyAppearance();

        // 渲染器每帧算完缓动就回调一次，窗口跟着改尺寸——这是「连续伸缩」的来源之一。
        // 定时器里也调了一次 ApplyDesiredSize，两处都调是有意的：这个回调只在
        // 「这一帧真的变了」时触发，不能指望它替代节拍。
        _renderer.OnLayoutChanged = ApplyDesiredSize;

        Width = WinW;
        Height = WinH;
        RestorePosition();
        RefreshAccessibilitySettings();

        _animTimer.Tick += (_, _) => OnAnimationTick();
        _fetchTimer.Interval = TimeSpan.FromSeconds(15);
        _fetchTimer.Tick += async (_, _) =>
        {
            // 顺带跟一次系统外观 / 无障碍设置。macOS 版是订阅通知的，Windows 这边
            // 没有等价的跨平台通知源（主题变化有 ActualThemeVariantChanged，
            // 「减弱动态效果」没有），15 秒轮一次足够：用户改完最多等一轮就生效。
            RefreshAccessibilitySettings();
            await RunHookAsync(FetchUsageAsync);
        };
        _activityTimer.Interval = TimeSpan.FromSeconds(0.8);
        _activityTimer.Tick += async (_, _) => await PollActivityTick();
        // 位置防抖：拖动过程中 PositionChanged 会以鼠标事件的频率狂发，
        // 每次都写文件就是上百次/秒的磁盘 I/O。停手 1 秒后再落一次盘
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settings.Save(); };
    }

    /// <summary>数据层更新完模型后调用：重算尺寸并重绘。等价于 Swift 里的 fetcher.onUpdate。</summary>
    public void NotifyModelChanged()
    {
        ApplyDesiredSize();
        _surface.InvalidateVisual();
        RefreshMenus();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Position 在 Show 之前设，有的平台会被启动位置覆盖，show 之后补一次最稳
        RestorePosition();
        ApplyNoActivate();
        UpdateAnimationState();
        _fetchTimer.Start();
        _activityTimer.Start();
        // 别等第一个 15 秒：启动就该有数据
        _ = RunHookAsync(FetchUsageAsync);
        _ = PollActivityTick();
        RefreshMenus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _animTimer.Stop();
        _fetchTimer.Stop();
        _activityTimer.Stop();
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            _settings.Save();       // 防抖还没到点就退出：这次别丢
        }
        base.OnClosed(e);
    }

    // MARK: 动画节拍

    /// <summary>
    /// 可见就让它动；交互 / 忙碌时满帧，单纯呼吸眨眼用 24fps 省电。
    /// macOS 版还会在「显示器休眠」「窗口被完全遮挡」时停表（NSApp.occlusionState /
    /// screensDidSleep）。Avalonia 没有跨平台的遮挡与休眠通知，这里只能退化成
    /// 「窗口不可见 / 最小化就停」——黑屏时仍然会白烧一点电，这是移植的差距。
    /// </summary>
    private void UpdateAnimationState()
    {
        var visible = IsVisible && WindowState != WindowState.Minimized;
        // 看不见时把磁盘轮询放慢到 5 秒，别白读 ~/.claude 下那堆 jsonl
        // （macOS 版是在 applicationDidChangeOcclusionState 里做的同一件事）
        var wantPoll = TimeSpan.FromSeconds(visible ? 0.8 : 5.0);
        if (_activityTimer.Interval != wantPoll) _activityTimer.Interval = wantPoll;

        if (!visible)
        {
            SetAnimating(0);
            return;
        }
        // 呼吸眨眼一直算「有动画」，停下来它就成了一张死图；真到了不需要的那天，
        // NeedsContinuousAnimation 会告诉我们可以整表停掉
        if (!_renderer.NeedsContinuousAnimation && !_renderer.NeedsFullFrameRate)
        {
            SetAnimating(0);
            return;
        }
        SetAnimating(_renderer.NeedsFullFrameRate ? 60 : 24);
    }

    private void SetAnimating(double fps)
    {
        if (Math.Abs(fps - _animFps) < 0.001) return;
        _animFps = fps;
        _animTimer.Stop();
        if (fps <= 0) return;
        _animTimer.Interval = TimeSpan.FromSeconds(1.0 / fps);
        _animTimer.Start();
    }

    private void OnAnimationTick()
    {
        var dt = _animFps > 0 ? 1.0 / _animFps : 1.0 / 60;

        // 窗口缩小后光标可能已经不在窗口上了，但收缩不一定补发 PointerExited
        // （macOS 版栽过这个：setFrame 不给静止的光标补 mouseExited，悬停态会卡住不放）。
        // 每帧拿 IsPointerOver 兜一次底，比事件可靠。
        if (_model.Hovered != IsPointerOver) SetHovered(IsPointerOver);

        UpdateMousePoint();
        NoteSeenWhileHovering();
        _renderer.Advance(dt);
        // 每帧跟着展开进度调窗口尺寸，做出连续的伸缩动画
        ApplyDesiredSize();
        _surface.InvalidateVisual();

        // 状态变化时自动切帧率（比如开始 / 结束一轮思考）
        var want = _renderer.NeedsFullFrameRate ? 60 : 24;
        if (Math.Abs(want - _animFps) > 0.001) UpdateAnimationState();
    }

    private async Task PollActivityTick()
    {
        if (_activityPolling) return;
        _activityPolling = true;
        try
        {
            await RunHookAsync(PollActivityAsync);
            ApplyDesiredSize();   // 会话块数量变化会改高度
            _surface.InvalidateVisual();
        }
        finally
        {
            _activityPolling = false;
        }
    }

    /// <summary>
    /// 每帧把光标位置喂给渲染器，太阳的光芒才会朝鼠标伸长。
    /// PetRenderer 要的是<b>全局</b>光标（换算成视图坐标）：光标还在窗口外靠近时它就该有反应，
    /// 「引力」本来就是隔空的。所以 Windows 上直接问系统要 GetCursorPos，
    /// 而不是只用窗口内的 PointerMoved。超出 230pt 由渲染器自己置空，不用这边过滤。
    /// </summary>
    private void UpdateMousePoint()
    {
        if (OperatingSystem.IsWindows() && Win32Cursor.TryGetPosition(out var x, out var y))
        {
            // this. 不能省：PointToClient 是 Avalonia.VisualExtensions 上的扩展方法，
            // 不是 Window 的实例方法，省掉接收者编译不过
            _renderer.MousePoint = this.PointToClient(new PixelPoint(x, y));
            return;
        }
        // 非 Windows（我们在 Mac 上跑验证时）退化成只认窗口内的指针：
        // 光标一出窗口引力就断，隔空那一段效果没有。
        _renderer.MousePoint = _pointerInWindow;
    }

    /// <summary>
    /// 鼠标在桌宠上停够 1.2 秒 = 你看到了这些通知；先记下，等鼠标移开再清，
    /// 免得块在你眼皮底下消失。
    /// </summary>
    private void NoteSeenWhileHovering()
    {
        if (!_model.Hovered || _hoverSince is null) return;
        if ((DateTimeOffset.Now - _hoverSince.Value).TotalSeconds < 1.2) return;
        foreach (var s in _model.Sessions)
        {
            if (s.Unread) _seenWhileHovering.Add(s.Id);
        }
    }

    /// <summary>鼠标离开后统一清掉刚才看过的未读。</summary>
    private void FlushSeen()
    {
        if (_seenWhileHovering.Count == 0) return;
        foreach (var id in _seenWhileHovering) MarkRead(id, refresh: false);
        _seenWhileHovering.Clear();
        NotifyModelChanged();
    }

    private void MarkRead(string id, bool refresh = true)
    {
        MarkReadRequested?.Invoke(id);
        for (var i = 0; i < _model.Sessions.Count; i++)
        {
            // SessionActivity 是 record，属性只读；改一条要整条换掉
            if (_model.Sessions[i].Id == id) _model.Sessions[i] = _model.Sessions[i] with { Unread = false };
        }
        if (refresh) NotifyModelChanged();
    }

    private static async Task RunHookAsync(Func<Task>? hook)
    {
        if (hook is null) return;
        try
        {
            await hook();
        }
        catch (Exception ex)
        {
            // 数据层抛异常不能把桌宠一起带走：它的职责是自己把错误写进 PetModel.ErrorMsg
            Debug.WriteLine($"[Sundial] 数据钩子异常：{ex}");
        }
    }

    // MARK: 尺寸

    /// <summary>完全展开时的高度：顶行（太阳 + 仪表）+ 会话块 +（悬停）详情行。</summary>
    private double ExpandedHeight()
    {
        var h = 10 + PetRenderer.TopRowH + 2;   // 卡片顶部内边距 + 顶行
        if (_model.Loading)
        {
            h += 28;
        }
        else if (_model.Rows.Count == 0 && _model.ErrorMsg is not null)
        {
            h += 56 + (_model.NeedsLogin ? 36 : 0);
        }
        else
        {
            // 用渲染器里那个连续变化的高度，绝对不能直接数块数：块数是离散的，
            // 最后一块一消失窗口会在一帧里掉 50pt，所有缓动全白做，看着就是「啪」地消失
            h += _renderer.BlocksHeight;
            // 详情区高度按展开进度连续插值，窗口才能平滑伸缩
            var p = _renderer.HoverProgress;
            if (p > 0.001)
            {
                var detailH = PetRenderer.BlockGap + 2 + 19 + Math.Min(_model.Rows.Count, 5) * 15 + 18;
                h += detailH * p;
            }
        }
        return h + 10;              // 卡片底部内边距
    }

    /// <summary>
    /// 实际窗口尺寸：在「只剩太阳」与「完整卡片」之间按展开进度插值。
    /// 名字里带 Compute 是为了不撞 Layoutable.DesiredSize（那是布局系统的量测结果，两码事）。
    /// </summary>
    private Size ComputeDesiredSize()
    {
        var e = _renderer.ExpandProgress;
        var side = PetRenderer.CompactSide;
        return new Size(side + (WinW - side) * e,
                        side + (ExpandedHeight() - side) * e);
    }

    private void ApplyDesiredSize()
    {
        var want = ComputeDesiredSize();
        if (Math.Abs(Width - want.Width) > 0.25 || Math.Abs(Height - want.Height) > 0.25)
        {
            Width = want.Width;
            Height = want.Height;
        }
        ApplyAnchoredPosition(want);
    }

    /// <summary>
    /// 每帧按锚点重新落位。锚点是用户拖出来的左上角，Y 向下所以长高就是向下长，
    /// 正常情况下一动不动；只有撑出工作区时才临时往回收——贴底时向上撑开，贴右时向左让出。
    /// <b>每帧都从锚点重算</b>而不是「当前位置夹一下」：只夹取的话，窗口在屏幕底边展开时
    /// 被顶上去，收回去以后就停在被顶上去的位置，反复几次桌宠会一路爬到屏幕中间。
    /// </summary>
    private void ApplyAnchoredPosition(Size sizeDip)
    {
        if (_anchor is not { } anchor) return;
        // 用户正拖着窗口时一律不插手。原文 adjustWindowHeight 在尺寸没变时直接 return，
        // 所以拖动全程根本不会走到夹取那一步；这边是每帧从锚点重算的，不加这道闸门的话，
        // 把桌宠往工作区边缘（任务栏、屏幕左右边）拖时，每帧都会被夹回来一次，
        // 手感就是窗口跟不上光标、还一直往回弹。松手后的下一帧照常夹取，落位结果不变。
        if (_dragging) return;
        var x = anchor.X;
        var y = anchor.Y;

        var screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary;
        if (screen is not null)
        {
            // WorkingArea 是物理像素，Width/Height 是 DIP，必须先换算再比
            var scale = RenderScaling > 0 ? RenderScaling : screen.Scaling;
            var wa = screen.WorkingArea;
            var wPx = (int)Math.Round(sizeDip.Width * scale);
            var hPx = (int)Math.Round(sizeDip.Height * scale);
            x = Math.Max(wa.X, Math.Min(x, wa.X + wa.Width - wPx));
            y = Math.Max(wa.Y, Math.Min(y, wa.Y + wa.Height - hPx));
        }

        if (x == Position.X && y == Position.Y) return;
        MoveTo(new PixelPoint(x, y));
    }

    // MARK: 位置记忆

    /// <summary>由程序发起的移动。记下目标点，好把随后回来的 PositionChanged 认出来。</summary>
    private void MoveTo(PixelPoint p)
    {
        _selfMoved = p;
        _adjusting = true;
        Position = p;
        _adjusting = false;
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_adjusting) return;                       // 程序化伸缩不算拖动
        // 有的后端把移动通知排到下一轮消息循环才发，那时 _adjusting 已经复位了。
        // 不认出这种「回声」的话，一次夹取就会被当成用户拖动写进锚点，桌宠开始漂移。
        if (_selfMoved is { } self && self == e.Point) return;

        _anchor = e.Point;
        if (_settings.WindowX == e.Point.X && _settings.WindowY == e.Point.Y) return;
        // 存左上角。macOS 版在这里踩过坑：存左下角的话，窗口高度随内容变化，
        // 每次重启都会往上飘一截。Win32 的原点本来就在左上角，天然没这个问题。
        _settings.WindowX = e.Point.X;
        _settings.WindowY = e.Point.Y;
        _saveTimer.Stop();          // 防抖：拖动停下来 1 秒后才真正写文件
        _saveTimer.Start();
    }

    private void RestorePosition()
    {
        if (_settings.WindowX is int sx && _settings.WindowY is int sy && IsOnAnyScreen(sx, sy))
        {
            _anchor = new PixelPoint(sx, sy);
            MoveTo(_anchor.Value);
            return;
        }
        // 存的位置不在任何一块屏上（外接显示器拔了），或者第一次启动：
        // 默认落在右下角上方一点，别压住任务栏
        var wa = (Screens.Primary ?? Screens.All.FirstOrDefault())?.WorkingArea;
        if (wa is null) return;
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        var w = (int)Math.Round(WinW * scale);
        var h = (int)Math.Round(WinH * scale);
        _anchor = new PixelPoint(wa.Value.X + wa.Value.Width - w - (int)(24 * scale),
                                 wa.Value.Y + wa.Value.Height - h - (int)(60 * scale));
        MoveTo(_anchor.Value);
    }

    /// <summary>存下的位置还在不在某块屏幕上（拔掉外接显示器后必须能找回来）。</summary>
    private bool IsOnAnyScreen(int x, int y)
    {
        // 只判左上角这一个点是否落在某块屏的工作区里。矩形相交判断更精确，
        // 但这里用最基础的算术就够，也不赌 PixelRect 各版本的相交 API。
        foreach (var s in Screens.All)
        {
            var wa = s.WorkingArea;
            if (x >= wa.X && x < wa.X + wa.Width && y >= wa.Y && y < wa.Y + wa.Height) return true;
        }
        return false;
    }

    /// <summary>把桌宠拽回主屏可见处。托盘左键和菜单都用它——
    /// 窗口被拖到已拔掉的显示器上时，这是唯一的退路。</summary>
    /// <summary>只把窗口调到前面来，**不挪位置**。托盘左键用这个。
    /// 早先托盘左键直接调 EnsureVisible，结果点一下桌宠就瞬移到右下角，
    /// 还把新坐标同步写进配置——你摆好的位置就这么没了，重启也回不来。</summary>
    public void BringToFront()
    {
        if (_dialogs == 0) Topmost = true;   // 有对话框开着时不许重新置顶，否则会压住它
        Show();
    }

    /// <summary>把窗口搬回屏幕右下角并记住新位置。只该由菜单里那一条显式触发。</summary>
    public void EnsureVisible()
    {
        var wa = (Screens.Primary ?? Screens.All.FirstOrDefault())?.WorkingArea;
        if (wa is null) return;
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        _anchor = new PixelPoint(wa.Value.X + wa.Value.Width - w - (int)(24 * scale),
                                 wa.Value.Y + wa.Value.Height - h - (int)(60 * scale));
        MoveTo(_anchor.Value);
        _settings.WindowX = _anchor.Value.X;
        _settings.WindowY = _anchor.Value.Y;
        _settings.Save();
        if (_dialogs == 0) Topmost = true;   // 有对话框开着时不许重新置顶，否则会压住它
        Show();
    }

    // MARK: 指针

    private void SetHovered(bool hovering)
    {
        if (_model.Hovered == hovering) return;
        _model.Hovered = hovering;
        _hoverSince = hovering ? DateTimeOffset.Now : null;
        if (!hovering) FlushSeen();   // 移开时把刚看过的清成已读
        _surface.InvalidateVisual();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;   // 右键交给 ContextMenu 自己处理

        if (e.ClickCount >= 2)
        {
            // 双击 = 立即刷新 / 未登录时登录。
            // 必须在这里判，不能用 DoubleTapped：单击那一下已经调了 BeginMoveDrag，
            // Win32 会进入自己的拖动消息循环，后续的双击手势根本传不回来。
            if (_model.NeedsLogin) StartLogin();
            else ForceRefreshRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // 先做命中测试再决定拖不拖：登录按钮和会话块是「控件」，其余整块都是拖动把手。
        // 顺序不能反——一旦调了 BeginMoveDrag 就进了系统的拖动循环，这一下点击就没了。
        var p = e.GetPosition(this);
        var loginRect = _renderer.LoginButtonRect;
        // Width > 0 这一条不能省：没画登录按钮时 LoginButtonRect 是 default，而 Avalonia 的
        // Rect.Contains 两端都是闭区间，空矩形照样「包含」原点——点窗口左上角那一个点就会
        // 莫名其妙开始登录。AppKit 的 NSRect.contains 对空矩形恒为 false，原文不用管这件事。
        if (_model.NeedsLogin && loginRect.Width > 0 && loginRect.Contains(p))
        {
            StartLogin();
            e.Handled = true;
            return;
        }
        foreach (var (id, rect) in _renderer.BlockRects)
        {
            if (!rect.Contains(p)) continue;
            // 只有「未读」的块才吃掉这一下点击，正在跑的块必须放过去继续拖动——
            // 对应原文 mouseDown 里那句 `if ...unread == true`（不满足就不 return，
            // 一路落到 performDrag）。拦下来有两处后果：一是 MarkRead 写进的是
            // ActivityWatcher 的抑制集合，要等这个会话下一轮重新开跑才解除，
            // 于是它这次跑完的「未读」提示被提前吞掉；二是会话一多，块几乎铺满整张卡片，
            // 桌宠就再也拖不动了。
            var hit = _model.Sessions.FirstOrDefault(s => s.Id == id);
            if (hit is null || !hit.Unread) continue;
            MarkRead(id);          // 点掉一个未读通知
            e.Handled = true;
            return;
        }

        // 按住整块窗口就能拖（macOS 版是 performDrag）。
        // 开拖之前先把「回声位置」清掉：用户完全可能把窗口拖回我们上一次夹取到的那个点，
        // 留着它就会把这次真实拖动当成回声吞掉，锚点不更新，下一帧窗口自己弹回去
        _selfMoved = null;
        _dragging = true;
        try
        {
            // Win32 上这一句会进系统的拖动消息循环，直到用户松手才返回；期间定时器照跑，
            // 所以必须有 _dragging 这个闸门（见 ApplyAnchoredPosition）
            BeginMoveDrag(e);
        }
        finally
        {
            _dragging = false;
        }
    }

    // MARK: 菜单
    //
    // 托盘的原生菜单和窗口右键菜单是两套控件（NativeMenu vs ContextMenu），
    // 条目却必须一模一样，所以先描述成 MenuEntry 列表，再分别生成两份。

    private sealed record MenuEntry(
        string Text,
        Action? Invoke = null,
        bool IsSeparator = false,
        Func<bool>? Checked = null,
        Func<bool>? Enabled = null,
        Func<string>? DynamicText = null);

    private List<MenuEntry> BuildMenuEntries() => new()
    {
        new MenuEntry("登录 Claude 账号…", StartLogin,
            DynamicText: () => LoggedIn ? "重新登录 Claude 账号…" : "登录 Claude 账号…"),
        new MenuEntry("退出登录", SignOut, Enabled: () => LoggedIn),
        new MenuEntry("", IsSeparator: true),
        new MenuEntry("立即刷新", () => ForceRefreshRequested?.Invoke()),
        // 悬停之外的等价入口：不用把鼠标停在窗口上也能看明细
        new MenuEntry("固定展开用量明细", ToggleDetails, Checked: () => _model.DetailsPinned),
        new MenuEntry("打开网页版用量", () => OpenUrl("https://claude.ai/settings/usage")),
        new MenuEntry("把桌宠移回屏幕右下角", EnsureVisible),
        new MenuEntry("", IsSeparator: true),
        new MenuEntry("系统模糊背景（实验）", ToggleSystemBlur, Checked: () => _settings.SystemBlur),
        new MenuEntry("开机自动启动", ToggleAutostart, Checked: () => AutoStart.IsEnabled,
            Enabled: () => OperatingSystem.IsWindows()),
        new MenuEntry("", IsSeparator: true),
        new MenuEntry("退出 Sundial", QuitApp),
    };

    private bool LoggedIn => HasTokenProvider?.Invoke() ?? false;

    /// <summary>
    /// 勾选状态用文本前缀表示，不用 ToggleType / IsChecked。
    /// 原因很实际：NativeMenuItem 与 MenuItem 的勾选属性在 Avalonia 各小版本里名字和行为
    /// 不完全一致，而前缀在两套菜单上表现完全一样，也不会有平台差异。
    /// </summary>
    private static string Decorate(MenuEntry e)
    {
        var text = e.DynamicText?.Invoke() ?? e.Text;
        if (e.Checked is null) return text;
        return e.Checked() ? "✓ " + text : "  " + text;
    }

    public NativeMenu BuildTrayMenu()
    {
        var menu = new NativeMenu();
        _nativeItems.Clear();
        foreach (var entry in _entries)
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }
            var item = new NativeMenuItem(Decorate(entry));
            var captured = entry;
            item.Click += (_, _) => captured.Invoke?.Invoke();
            menu.Items.Add(item);
            _nativeItems.Add((item, entry));
        }
        return menu;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        _contextItems.Clear();
        foreach (var entry in _entries)
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = Decorate(entry) };
            var captured = entry;
            item.Click += (_, _) => captured.Invoke?.Invoke();
            menu.Items.Add(item);
            _contextItems.Add((item, entry));
        }
        // 菜单弹出前把文案刷一遍：登录状态可能在两次打开之间变了
        menu.Opening += (_, _) => RefreshMenus();
        return menu;
    }

    /// <summary>
    /// 刷新两套菜单的文案与可用状态。
    /// macOS 版是在 menuNeedsUpdate 里现场重建条目的；Avalonia 的托盘菜单没有可靠的
    /// 「即将弹出」回调，所以改成状态一变就主动刷——反正只是几次字符串赋值。
    /// </summary>
    public void RefreshMenus()
    {
        foreach (var (item, entry) in _nativeItems)
        {
            item.Header = Decorate(entry);
            item.IsEnabled = entry.Enabled?.Invoke() ?? true;
        }
        foreach (var (item, entry) in _contextItems)
        {
            item.Header = Decorate(entry);
            item.IsEnabled = entry.Enabled?.Invoke() ?? true;
        }
    }

    private void ToggleDetails()
    {
        _model.DetailsPinned = !_model.DetailsPinned;
        _settings.DetailsPinned = _model.DetailsPinned;
        _settings.Save();
        ApplyDesiredSize();
        _surface.InvalidateVisual();
        RefreshMenus();
    }

    private void ToggleSystemBlur()
    {
        _settings.SystemBlur = !_settings.SystemBlur;
        _settings.Save();
        ApplyTransparencyHint();
        RefreshMenus();
    }

    private void ApplyTransparencyHint()
    {
        // 打开时申请 [AcrylicBlur, Transparent]：Windows 11 会给亚克力模糊，
        // 拿不到就退到纯透明（视觉全部由 PetSurface 自绘的圆角底承担）。
        // 关闭时只申请 Transparent，避免圆角外露出一圈方形模糊（见 PetSurface 的说明）。
        TransparencyLevelHint = _settings.SystemBlur
            ? new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent }
            : new[] { WindowTransparencyLevel.Transparent };
        // 系统到底给没给亚克力，只有 ActualTransparencyLevel 说了算——
        // 拿它去决定自绘底的浓度，而不是拿「用户开了开关」这个愿望
        _surface?.InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 注意：属性变化在基类构造期间就会来，那时候 ctor 体还没跑完，
        // 所以每个分支都要先确认 _surface 已经建好了（它是 ctor 体里最早赋值的那批之一）
        if (_surface is null) return;

        if (change.Property == ActualTransparencyLevelProperty)
        {
            _surface.SystemBlurActive = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
            _surface.InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
        {
            // 必须挂在属性上：一旦因为不可见把动画表停了，就没有别的东西会再来叫醒它，
            // 桌宠会永远定格（只靠 OnAnimationTick 自己调 UpdateAnimationState 是个死循环缺口）
            UpdateAnimationState();
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            // UseShellExecute = true 是关键：.NET Core 之后默认是 false，
            // 那样等于要 CreateProcess 一个 http 链接，直接抛「找不到文件」
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Sundial] 打开链接失败：{ex.Message}");
        }
    }

    private void QuitApp()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
    }

    // MARK: 登录

    private void SignOut()
    {
        SignOutRequested?.Invoke();
        _model.NeedsLogin = true;
        _model.Rows = new List<UsageRow>();
        _model.Tier = "";
        _model.ErrorMsg = "已退出登录\n双击我重新登录";
        _model.Asleep = true;
        _model.Loading = false;
        NotifyModelChanged();
    }

    private void StartLogin()
    {
        if (_loginInProgress)
        {
            _loginWindow?.Activate();   // 已经开着就把它拎到前面，别弹第二个
            return;
        }

        // 同一次登录里 URL 必须稳定，理由见 AuthorizeUrlProvider 的说明
        var url = AuthorizeUrlProvider?.Invoke();
        if (string.IsNullOrEmpty(url)) return;
        string authorizeUrl = url;

        _loginInProgress = true;
        // 先替用户把授权页打开（对应原文 startLogin 里的 NSWorkspace.shared.open）。
        // 漏了这一步，用户点完「登录」只会看到一个要他粘贴授权码的框，而码从哪来没人告诉他。
        OpenUrl(authorizeUrl);

        // 隔 1 秒再弹输入框（对应原文的 asyncAfter(.now() + 1.0)）：浏览器还在起，
        // 这时候把一个置顶窗口推到最前会把刚给出去的焦点抢回来，授权页反倒被压在下面。
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            if (!_loginInProgress) return;   // 这一秒里状态已经被收拾掉了
            PushDialog();
            var win = new LoginWindow(authorizeUrl, OnLoginDialogClosed);
            _loginWindow = win;
            win.Show();
        };
        delay.Start();
    }

    private void OnLoginDialogClosed(string? code)
    {
        _loginWindow = null;
        PopDialog();

        var pasted = code?.Trim();
        if (string.IsNullOrEmpty(pasted))
        {
            _loginInProgress = false;
            return;
        }

        _model.Loading = true;
        _model.ErrorMsg = null;
        NotifyModelChanged();
        _ = FinishLoginAsync(pasted);
    }

    /// <summary>
    /// 整个方法都跑在 UI 线程上：起点是 UI 事件，await 默认捕获 Avalonia 的同步上下文，
    /// 网络等待期间线程是让出去的。所以下面改模型不用再 Post 回来。
    /// </summary>
    private async Task FinishLoginAsync(string pasted)
    {
        LoginOutcome outcome;
        try
        {
            outcome = ExchangeCodeAsync is null
                ? new LoginOutcome(false, "登录模块没有接上。")
                : await ExchangeCodeAsync(pasted);
        }
        catch (Exception ex)
        {
            // 约定上钩子不该抛，抛了也不能让桌宠一起死
            Debug.WriteLine($"[Sundial] 换取令牌失败：{ex}");
            outcome = new LoginOutcome(false, ex.Message);
        }

        _loginInProgress = false;
        if (outcome.Ok)
        {
            _model.NeedsLogin = false;
            _model.ErrorMsg = null;
            _model.Asleep = false;
            ForceRefreshRequested?.Invoke();
        }
        else
        {
            _model.Loading = false;
            if (!LoggedIn)             // 别把已经成功的登录改回未登录
            {
                _model.NeedsLogin = true;
                _model.Rows = new List<UsageRow>();   // 不清空则登录卡片和按钮不会渲染
                _model.Tier = "";
                _model.ErrorMsg = "登录失败\n双击我重试";
                _model.Asleep = true;
            }
        }
        NotifyModelChanged();

        // 失败原因必须原样端到用户面前：那段文案里写着「浏览器留着旧授权页」这类
        // 只有看到才可能自己解决的问题，吞掉它用户就只剩「又失败了」四个字
        if (!string.IsNullOrEmpty(outcome.Message))
        {
            ShowNotice(outcome.Ok ? "提示" : "登录失败", outcome.Message!);
        }
    }

    /// <summary>弹窗期间把桌宠降回普通层级，否则置顶的它会压在对话框上面
    /// （对应 macOS 版的 withLoweredWindow）。计数是因为登录框和提示框可能叠着开。</summary>
    private void PushDialog()
    {
        _dialogs++;
        Topmost = false;
    }

    private void PopDialog()
    {
        _dialogs = Math.Max(0, _dialogs - 1);
        if (_dialogs == 0) Topmost = true;
    }

    private void ShowNotice(string title, string message)
    {
        PushDialog();
        var win = new NoticeWindow(title, message, PopDialog);
        win.Show();
    }

    /// <summary>
    /// 登录框：一个「打开授权页」按钮 + 一个粘贴授权码的输入框。
    /// macOS 版的教训就是粘贴：那边 .accessory 模式下没有主菜单，⌘V 没有响应者可路由，
    /// 用户根本粘不进去，只能手打两百多位的码（后来是靠补一整套 Edit 菜单救回来的）。
    /// Windows 的 TextBox 自带 Ctrl+V，但为了绝不重蹈覆辙，这里再放一个「粘贴」按钮
    /// 直接读剪贴板——不依赖任何快捷键路由。
    /// </summary>
    private sealed class LoginWindow : Window
    {
        private readonly TextBox _input;
        private readonly Action<string?> _onDone;
        private bool _reported;

        public LoginWindow(string authorizeUrl, Action<string?> onDone)
        {
            _onDone = onDone;

            Title = "连接 Claude 账号";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = true;   // 桌宠是置顶的，登录框不置顶就可能被压在下面

            _input = new TextBox
            {
                Watermark = "在此粘贴授权码",
                AcceptsReturn = false,
                MinWidth = 300,
            };

            // 授权页在弹这个框之前就已经替用户打开了（见 StartLogin），这颗按钮是退路：
            // 默认浏览器没配好、或者用户手滑把标签页关了，还能再开一次
            var openBtn = new Button { Content = "重新打开授权页" };
            openBtn.Click += (_, _) => OpenUrl(authorizeUrl);

            var pasteBtn = new Button { Content = "粘贴" };
            pasteBtn.Click += async (_, _) =>
            {
                var clip = Clipboard;   // TopLevel 自带的剪贴板，不用再去找 TopLevel
                if (clip is null) return;
                var text = await clip.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text)) _input.Text = text.Trim();
            };

            var okBtn = new Button { Content = "完成登录", IsDefault = true };
            okBtn.Click += (_, _) => Finish(_input.Text);

            var cancelBtn = new Button { Content = "取消", IsCancel = true };
            cancelBtn.Click += (_, _) => Finish(null);

            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "浏览器已打开 Claude 授权页面，请在浏览器里登录并点击授权。\n"
                             + "授权后把页面给出的授权码粘贴到下面（直接复制浏览器地址栏也行）。\n\n"
                             + "注意：如果浏览器里还留着以前的授权页，请用刚打开的这一页，"
                             + "旧页面上的码是无效的。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    openBtn,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { _input, pasteBtn },
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelBtn, okBtn },
                    },
                },
            };

            Opened += (_, _) => _input.Focus();
        }

        private void Finish(string? code)
        {
            if (_reported) return;
            _reported = true;
            _onDone(code);
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            // 用户直接点右上角关掉时也要回收状态，否则 _loginInProgress 永远为 true，再也登不了
            if (!_reported)
            {
                _reported = true;
                _onDone(null);
            }
            base.OnClosed(e);
        }
    }

    /// <summary>对应 macOS 版的 warn()：一句话 + 一个「好」。
    /// 文字必须可选中复制——登录失败的说明里有让用户照做的步骤。</summary>
    private sealed class NoticeWindow : Window
    {
        private readonly Action _onClosed;
        private bool _reported;

        public NoticeWindow(string title, string message, Action onClosed)
        {
            _onClosed = onClosed;

            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var ok = new Button
            {
                Content = "好",
                IsDefault = true,
                IsCancel = true,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            ok.Click += (_, _) => Close();

            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                    new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    ok,
                },
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            if (!_reported)
            {
                _reported = true;
                _onClosed();
            }
            base.OnClosed(e);
        }
    }

    private void ToggleAutostart()
    {
        var err = AutoStart.Set(!AutoStart.IsEnabled);
        RefreshMenus();
        // 改不动就必须说一声（对应原文 toggleAutostart 里 catch 到异常就
        // warn「无法修改开机自启设置：…」）。企业机器上 HKCU\...\Run 常被组策略锁住，
        // 静默失败的话用户看到的是「勾了一下，勾没上，也没人告诉我为什么」
        if (err is not null) ShowNotice("设置失败", "无法修改开机自启设置：" + err);
    }

    // MARK: 外观与无障碍

    /// <summary>
    /// 把「现在是深色还是浅色」写给 Theme。macOS 版用的是 NSColor 动态颜色，系统一切换
    /// 就自动重解析；Avalonia 在 DrawingContext 里没有等价物，只能由窗口层把开关塞过去
    /// （Theme.IsDark 的注释里写的就是这件事）。漏了这一步，深色桌面下整套语义色会用错档，
    /// 文字对比度直接掉到线下。
    /// </summary>
    private void ApplyAppearance()
    {
        // 必须写全名：StyledElement 自己有个 Theme 属性（ControlTheme），
        // 在 Window 里直接写 Theme 会解析到它，编译期就报错——写全名一次说清
        Sundial.App.Theme.IsDark = ActualThemeVariant == ThemeVariant.Dark;
        _surface?.InvalidateVisual();
    }

    /// <summary>
    /// 把系统的「减弱动态效果 / 关闭透明效果 / 高对比度」同步给渲染器。
    /// macOS 版读的是 NSWorkspace.accessibilityDisplay* 系列，Windows 上没有统一入口：
    /// 动画偏好走 SystemParametersInfo，透明效果走「个性化」注册表键，
    /// 高对比度走 Avalonia 自己的 PlatformSettings。
    /// 非 Windows 一律按「都没开」处理——桌宠动画照跑，不影响 Mac 上的验证。
    /// </summary>
    private void RefreshAccessibilitySettings()
    {
        ApplyAppearance();

        try
        {
            // ContrastPreference 是 Avalonia 抽好的跨平台项，能用就用，不自己去读 HIGHCONTRAST 结构体
            var colors = PlatformSettings?.GetColorValues();
            if (colors is not null)
            {
                Sundial.App.Theme.IncreaseContrast =
                    colors.ContrastPreference == Avalonia.Platform.ColorContrastPreference.High;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Sundial] 读系统配色偏好失败：{ex.Message}");
        }

        var reduceMotion = SystemA11y.ReduceMotion;
        var reduceTransparency = SystemA11y.ReduceTransparency;
        if (_renderer.ReduceMotion == reduceMotion
            && _renderer.ReduceTransparency == reduceTransparency)
        {
            return;
        }
        _renderer.ReduceMotion = reduceMotion;
        _renderer.ReduceTransparency = reduceTransparency;
        // 关掉透明效果时也别再向系统申请模糊，否则圆角外会留一圈方形模糊
        if (reduceTransparency) TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        else ApplyTransparencyHint();
        NotifyModelChanged();
    }

    /// <summary>
    /// 点桌宠不该把用户正在写的编辑器切到后台——对应 macOS 版 PetWindow.canBecomeKey = false。
    /// Avalonia 没有暴露这个能力（ShowActivated 只管第一次显示），只能自己给窗口加
    /// WS_EX_NOACTIVATE。窗口里没有任何需要键盘输入的东西（登录框是独立窗口），所以不激活
    /// 不会丢失什么；拖动走的是 WM_NCLBUTTONDOWN，右键菜单是独立的弹出窗口，都不依赖激活态。
    /// 非 Windows 平台什么也不做。
    /// </summary>
    private void ApplyNoActivate()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero) return;
            Win32Style.AddExStyle(handle.Handle, Win32Style.WsExNoActivate);
        }
        catch (Exception ex)
        {
            // 失败只是「点它会抢一下焦点」，不值得为此崩掉
            Debug.WriteLine($"[Sundial] 设置 WS_EX_NOACTIVATE 失败：{ex.Message}");
        }
    }

    private static class SystemA11y
    {
        // SPI_GETCLIENTAREAANIMATION：对应「设置 › 辅助功能 › 视觉效果 › 动画效果」。
        // 用 SystemParametersInfo 而不是去猜 UserPreferencesMask 里的某个 bit——
        // 那个掩码是未公开布局，改版就会读错。
        private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam,
                                                         ref int pvParam, uint fWinIni);

        public static bool ReduceMotion
        {
            get
            {
                if (!OperatingSystem.IsWindows()) return false;
                try
                {
                    var enabled = 1;
                    if (!SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0)) return false;
                    return enabled == 0;   // 系统说「不要动画」= 我们的 ReduceMotion
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Sundial] 读动画偏好失败：{ex.Message}");
                    return false;
                }
            }
        }

        public static bool ReduceTransparency
        {
            get
            {
                if (!OperatingSystem.IsWindows()) return false;
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    // 没有这个值就当透明效果是开着的（Windows 默认开）
                    return key?.GetValue("EnableTransparency") is int v && v == 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Sundial] 读透明度偏好失败：{ex.Message}");
                    return false;
                }
            }
        }
    }

    private static class Win32Cursor
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        /// <summary>屏幕物理像素坐标。调用前必须自己保证是在 Windows 上。</summary>
        public static bool TryGetPosition(out int x, out int y)
        {
            x = 0;
            y = 0;
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                if (!GetCursorPos(out var p)) return false;
                x = p.X;
                y = p.Y;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Sundial] 取光标位置失败：{ex.Message}");
                return false;
            }
        }
    }

    private static class Win32Style
    {
        private const int GWL_EXSTYLE = -20;
        public const long WsExNoActivate = 0x08000000;

        // 只有 64 位进程才有 GetWindowLongPtrW/SetWindowLongPtrW 这两个导出
        // （32 位上头文件把它们宏定义成了 ...LongW，DLL 里并不存在）。
        // 我们只发 x64；万一将来出 32 位版，这里会抛 EntryPointNotFoundException，
        // 调用方已经 try 住了，退化成「点它会抢焦点」，不会崩。
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static void AddExStyle(IntPtr hWnd, long bits)
        {
            var cur = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            if ((cur & bits) == bits) return;
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(cur | bits));
        }
    }

    // MARK: 开机自启
    //
    // macOS 用的是 SMAppService；Windows 这边最省事也最稳的是 HKCU 的 Run 键：
    // 不需要管理员权限，用户能在「任务管理器 › 启动应用」里自己看到和关掉。
    // 计划任务（schtasks）能绕过启动项管理，反而不透明，不采用。
    private static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Sundial";

        public static bool IsEnabled
        {
            get
            {
                // 非 Windows 直接返回 false：开发期要能在 Mac 上把整个界面跑起来验证
                if (!OperatingSystem.IsWindows()) return false;
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(ValueName) is string s && s.Length > 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Sundial] 读开机自启失败：{ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>成功返回 null；失败返回一句能端给用户看的原因。</summary>
        public static string? Set(bool on)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key is null) return "打不开注册表项 HKCU\\" + RunKey + "。";
                if (!on)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    return null;
                }
                // Environment.ProcessPath 指向真正的 exe（单文件发布也对）；
                // 不能用 Assembly.Location——单文件发布时它是空串，写进去就是个死项。
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return "取不到本程序的可执行文件路径。";
                // 路径几乎一定带空格（Program Files / 用户名），不加引号会被拆成两段参数
                key.SetValue(ValueName, "\"" + exe + "\"");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Sundial] 写开机自启失败：{ex.Message}");
                return ex.Message;
            }
        }
    }
}

/// <summary>
/// 外壳自己的配置（窗口位置、两个开关）。
/// 手写 JSON 读写而不是 JsonSerializer：只有四个标量字段，手写没有反射、不受裁剪 / AOT 影响，
/// 也不会因为类型可见性出幺蛾子。
/// 放在 %APPDATA%\Sundial\ 下；这个路径在 macOS 上会落到 ~/.config/Sundial，同一套代码两边都能跑
/// （用户目录下的 .claude 是 Claude Code 的记录，不往里写我们的东西）。
/// </summary>
internal sealed class ShellSettings
{
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool DetailsPinned { get; set; }
    public bool SystemBlur { get; set; }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sundial");

    private static string FilePath => Path.Combine(Dir, "shell.json");

    public static ShellSettings Load()
    {
        var s = new ShellSettings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return s;
            if (root.TryGetProperty("windowX", out var x) && x.ValueKind == JsonValueKind.Number
                && x.TryGetInt32(out var xv)) s.WindowX = xv;
            if (root.TryGetProperty("windowY", out var y) && y.ValueKind == JsonValueKind.Number
                && y.TryGetInt32(out var yv)) s.WindowY = yv;
            if (root.TryGetProperty("detailsPinned", out var d)
                && d.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                s.DetailsPinned = d.GetBoolean();
            }
            if (root.TryGetProperty("systemBlur", out var b)
                && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                s.SystemBlur = b.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            // 配置文件坏了就当没有：桌宠必须能起来，位置丢了顶多是回到默认角落
            Debug.WriteLine($"[Sundial] 读配置失败：{ex.Message}");
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json =
                "{\n" +
                $"  \"windowX\": {(WindowX?.ToString(CultureInfo.InvariantCulture) ?? "null")},\n" +
                $"  \"windowY\": {(WindowY?.ToString(CultureInfo.InvariantCulture) ?? "null")},\n" +
                $"  \"detailsPinned\": {(DetailsPinned ? "true" : "false")},\n" +
                $"  \"systemBlur\": {(SystemBlur ? "true" : "false")}\n" +
                "}\n";
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Sundial] 写配置失败：{ex.Message}");
        }
    }
}
