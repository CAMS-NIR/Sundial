// Sundial (Windows 版) — 进程入口
//
// 对应 macOS 版的 main.swift：那边是 NSApplication.shared + AppDelegate + run()，
// 这边是 Avalonia 的 AppBuilder + 经典桌面生命周期。

using Avalonia;

namespace Sundial.App;

internal static class Program
{
    // [STAThread] 是 Windows 上的硬性要求：托盘图标、剪贴板、拖放都走 COM/OLE，
    // 不是 STA 的话轻则粘贴失效，重则托盘注册直接抛异常。macOS 上这个特性是空操作。
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // 这是个没有控制台窗口的托盘程序：崩了什么都看不见，用户只会说「点了没反应」。
            // 所以启动期异常必须落盘，否则线上问题无从查起。
            WriteCrashLog(ex);
            throw;
        }
    }

    // 命名与签名固定成 BuildAvaloniaApp()：Avalonia 的设计器 / 预览器按这个约定反射查找。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()   // Windows 走 Win32 + Skia；在 Mac 上跑验证时自动换成 macOS 后端
            .LogToTrace();
    // 说明：没有加 .WithInterFont()。那需要额外的 Avalonia.Fonts.Inter 包，
    // 而界面上的中文本来也不由 Inter 承担，交给系统默认字体（Windows 上是「微软雅黑」一系）更合适。

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sundial");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 写日志本身再失败就只能放弃了，不能让它掩盖真正的异常
        }
    }
}
