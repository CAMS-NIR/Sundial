// Sundial (Windows version) — process entry point
//
// Corresponds to main.swift in the macOS version: over there it is NSApplication.shared +
// AppDelegate + run(), over here it is Avalonia's AppBuilder + the classic desktop lifetime.

using Avalonia;

namespace Sundial.App;

internal static class Program
{
    // [STAThread] is a hard requirement on Windows: the tray icon, the clipboard and drag-and-drop all
    // go through COM/OLE, and without STA you get anything from paste quietly not working to tray
    // registration throwing outright. On macOS the attribute is a no-op.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // This is a tray program with no console window: when it crashes you see nothing at all,
            // and all the user will say is "I clicked it and nothing happened". So start-up exceptions
            // have to be written to disk, otherwise there is no way to start investigating problems in
            // the field.
            WriteCrashLog(ex);
            throw;
        }
    }

    // The name and signature are fixed as BuildAvaloniaApp(): Avalonia's designer / previewer looks it
    // up by reflection using that convention.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()   // Windows goes via Win32 + Skia; when verifying on a Mac it switches to the macOS backend automatically
            .LogToTrace();
    // Note: .WithInterFont() has not been added. That would need the extra Avalonia.Fonts.Inter
    // package, and the Chinese text in the interface was never going to be carried by Inter anyway, so
    // leaving it to the system default font (the Microsoft YaHei family on Windows) is a better fit.

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
            // If writing the log itself fails as well then all we can do is give up; it must not be
            // allowed to mask the real exception
        }
    }
}
