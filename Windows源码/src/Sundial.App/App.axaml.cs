// Sundial (Windows 版) — Application 子类 / 组装根
//
// 对应 macOS 版 App.swift 里 AppDelegate.applicationDidFinishLaunching：
// 建模型、建窗口、建托盘入口、把数据层接上去。
//
// 这里故意不配 .axaml 标记文件，界面全部用 C# 构建：
// 1) 少一个 XAML 编译器环节，就少一类「设计时能过、运行时炸」的坑；
// 2) 这个程序只有一个无边框窗口和两个小对话框，没有值得用 XAML 表达的模板层级；
// 3) 全 C# 也让整个 UI 能在 Mac 上直接跑起来验证。
// 代价：用不了 Avalonia 预览器。

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Sundial.Core;

namespace Sundial.App;

public sealed class App : Application
{
    private TrayIcon? _tray;
    private UsageFetcher? _fetcher;

    public override void Initialize()
    {
        // 必须挂一套主题，否则 Button / TextBox 这些控件没有模板，登录框会是一片空白。
        // 不设 RequestedThemeVariant：跟随系统明暗，和 macOS 版「不锁外观」的行为一致
        // （窗口层再把结果写进 Theme.IsDark，绘制才拿得到）。
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 关键：默认是「最后一个窗口关掉就退出」。登录框一关，桌宠就跟着没了。
            // 退出只走菜单里的那一项。
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var model = new PetModel();
            var renderer = new PetRenderer(model);
            var window = new MainWindow(model, renderer);

            // ───────────────────────── 数据层接线区 ─────────────────────────
            var tokens = new TokenSource();
            // post 显式走 Dispatcher，而不是让 UsageFetcher 去抓 SynchronizationContext：
            // 抓上下文依赖「构造时正好在 UI 线程」这个隐含前提，一旦哪天组装挪了地方就会
            // 变成后台线程改模型，症状是偶发的画面撕裂或空引用，极难查。写死最稳。
            var fetcher = new UsageFetcher(model, tokens, post: a => Dispatcher.UIThread.Post(a));
            var watcher = new ActivityWatcher();
            _fetcher = fetcher;

            // 取到新数据 → 重算窗口尺寸 + 重绘（等价于 macOS 版的 fetcher.onUpdate）
            fetcher.OnUpdate = window.NotifyModelChanged;

            // Tick 自己带节流（60 秒一轮 + 在途去重），所以这里 15 秒喊一次是安全的，
            // 与 macOS 版的 15 秒定时器一致：它负责的是「该轮到你了」，不是「必须去请求」
            window.FetchUsageAsync = () =>
            {
                fetcher.Tick();
                return Task.CompletedTask;
            };
            window.ForceRefreshRequested = fetcher.ForceRefresh;

            // 磁盘轮询放到线程池上：ActivityWatcher.Poll 要读 ~/.claude 下一堆 jsonl，
            // 会话多的时候几十毫秒起步，压在 UI 线程上就是每 0.8 秒卡一下动画。
            // 回来之后在 UI 线程整体替换列表——Sessions 本身是每轮的完整快照，不会读到半截。
            window.PollActivityAsync = async () =>
            {
                await Task.Run(watcher.Poll);
                model.Sessions = watcher.Sessions.ToList();
            };
            // 已读要记进 watcher，否则下一轮轮询这条未读又会冒出来
            window.MarkReadRequested = watcher.MarkRead;

            // HasToken 只看内存缓存（读盘/解密要在后台做，不能卡菜单）。
            // 后果是启动后第一次取数完成之前，菜单显示的是「登录 Claude 账号…」而不是
            // 「重新登录」——macOS 版的 fetcher.hasToken 也是这个性子，属于已知的小误差。
            window.HasTokenProvider = () => tokens.HasToken;
            window.SignOutRequested = tokens.SignOutByUser;

            // 同一次运行内复用同一个 verifier：每次点登录都换新的话，用户从上一个授权页
            // （浏览器很容易留着旧标签）复制的码就永远对不上，表现为「反复登录失败」。
            // 登录成功后才作废重来。verifier 只在 UI 线程上读写（授权页按钮与换码回调
            // 都在 UI 线程），所以不用加锁。
            string? verifier = null;
            window.AuthorizeUrlProvider = () =>
            {
                verifier ??= OAuth.NewVerifier();
                return OAuth.AuthorizeUrl(verifier);
            };
            window.ExchangeCodeAsync = async code =>
            {
                if (verifier is not { } v)
                {
                    return new LoginOutcome(false, "登录流程状态丢失，请重新点一次登录。");
                }
                try
                {
                    // 这里不写 ConfigureAwait(false)：整条链路留在 UI 线程上，
                    // 下面对 verifier 的清零和窗口那边改模型才不用再考虑线程
                    var token = await OAuthClient.ExchangeCodeAsync(code, v, CancellationToken.None);
                    var saved = TokenStore.Save(token);
                    tokens.AdoptToken(token);
                    verifier = null;                  // 成功了才换新的
                    return new LoginOutcome(true, saved
                        ? null
                        : "登录成功，但令牌没能存到本机，下次启动可能需要重新登录。");
                }
                catch (Exception ex)
                {
                    // Describe 会把「浏览器里留着旧授权页」这类真实原因讲清楚。
                    // 换成一句「登录失败」，用户就只能反复试同一个坏码
                    return new LoginOutcome(false, OAuthErrorText.Describe(ex));
                }
            };
            // ───────────────────────────────────────────────────────────────

            SetUpTray(window);

            desktop.MainWindow = window;
            window.Show();
            window.NotifyModelChanged();

            desktop.Exit += (_, _) =>
            {
                // 不显式释放的话，进程退出后托盘里会留一个点不掉的幽灵图标，
                // 直到用户把鼠标划过那块区域才消失——Windows 上的老毛病，必须自己收尾
                _tray?.Dispose();
                _tray = null;
                _fetcher?.Dispose();   // 取消在途请求，别让进程拖着一个 HTTP 往返不退
                _fetcher = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 托盘图标。对应 macOS 的 NSStatusItem：没有 Dock 图标 / 任务栏按钮时，这是唯一稳定的
    /// 入口——窗口被拖到已拔掉的显示器、或者用户忘了那只太阳是什么，都能从这里找回来。
    /// </summary>
    private void SetUpTray(MainWindow window)
    {
        var tray = new TrayIcon
        {
            ToolTipText = "Sundial",
            Menu = window.BuildTrayMenu(),
            IsVisible = true,
            Icon = LoadTrayIcon(),
        };
        // 左键单击：把桌宠拉回可见处。Windows 用户对「点托盘图标 = 把窗口找回来」有肌肉记忆
        tray.Clicked += (_, _) => window.EnsureVisible();

        // TrayIcon 要挂到 Application 上才会真正注册进系统托盘
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        _tray = tray;
    }

    /// <summary>
    /// 托盘图标是内嵌的 32×32 PNG（一颗九芒的太阳），不走外部资源文件：
    /// 少一个 AvaloniaResource 配置、少一处「发布后找不到文件」的失败点。
    /// 正式发版应换成设计给的多尺寸 .ico，这是能用的占位实现。
    /// </summary>
    private static WindowIcon? LoadTrayIcon()
    {
        try
        {
            var bytes = Convert.FromBase64String(TrayIconPngBase64);
            return new WindowIcon(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Sundial] 托盘图标加载失败：{ex.Message}");
            return null;
        }
    }

    private const string TrayIconPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAA+UlEQVR42mP4v8+NYSAxw3BxQCEQRwCxMa0cIA3EKTjk/iPh67Rw" +
        "AMhnG6AWEHLAf1o44AkBC8i2nJQowGaJGBBrEOmALiDmp5YDQHg6EO8D4kto4nFQh2HTW0tpLvhPBP4FxKuA2AeLnifQ9ITXAdI4" +
        "LI/D4lt8+BKxiRNdIAWLBh8SLf9PSs5A5jAD8WI0jWLQICXX4lVY0gROBxhDC5L/VMS/oNFHcjakpiOmk1sOiEGzGqUO2IcUDTCx" +
        "QmIcoEGFxIcL1w4JBwxoFAxIIhzwbDjgBdGAF8WDojIa8Op40DZIBrRJNuCN0gFvlg94x2RQdM2Gb+8YALB4hoppdlXvAAAAAElF" +
        "TkSuQmCC";
}
