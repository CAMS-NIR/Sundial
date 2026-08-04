// Sundial (Windows version) — Application subclass / composition root
//
// Corresponds to AppDelegate.applicationDidFinishLaunching in the macOS version's App.swift:
// build the model, build the window, build the tray entry point, wire the data layer up.
//
// There is deliberately no .axaml markup file here; the entire interface is built in C#:
// 1) one fewer XAML compiler stage means one fewer class of "passes at design time, blows up at
//    runtime" pitfall;
// 2) this program has only one borderless window and two small dialogs — there is no template
//    hierarchy worth expressing in XAML;
// 3) being all-C# also lets the whole UI be run and verified directly on a Mac.
// The price: no Avalonia previewer.

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
        // A theme has to be attached, otherwise controls such as Button / TextBox have no template
        // and the login box comes out completely blank.
        // RequestedThemeVariant is deliberately not set: follow the system's light/dark setting,
        // matching the macOS version's "don't lock the appearance" behaviour (the window layer then
        // writes the result into Theme.IsDark, which is how the drawing code gets at it).
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Crucial: the default is "quit as soon as the last window closes". Close the login box
            // and the pet disappears along with it. Quitting goes through that one menu item only.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var model = new PetModel();
            var renderer = new PetRenderer(model);
            var window = new MainWindow(model, renderer);

            // ───────────────────────── data layer wiring ─────────────────────────
            var tokens = new TokenSource();
            // post goes through the Dispatcher explicitly rather than letting UsageFetcher grab the
            // SynchronizationContext: grabbing the context relies on the implicit premise that we
            // happen to be on the UI thread at construction time, and the day this composition moves
            // somewhere else it turns into a background thread mutating the model — the symptom being
            // occasional torn frames or null references, which is extremely hard to track down.
            // Hard-coding it is the safest option.
            var fetcher = new UsageFetcher(model, tokens, post: a => Dispatcher.UIThread.Post(a));
            var watcher = new ActivityWatcher();
            _fetcher = fetcher;

            // New data arrives → recompute the window size + redraw (equivalent to fetcher.onUpdate in the macOS version)
            fetcher.OnUpdate = window.NotifyModelChanged;

            // Tick throttles itself (one round every 60 seconds + in-flight de-duplication), so
            // calling it every 15 seconds here is safe, and matches the macOS version's 15-second
            // timer: its job is "it's your turn now", not "you must go and make a request"
            window.FetchUsageAsync = () =>
            {
                fetcher.Tick();
                return Task.CompletedTask;
            };
            window.ForceRefreshRequested = fetcher.ForceRefresh;

            // Disk polling is pushed onto the thread pool: ActivityWatcher.Poll has to read a pile of
            // jsonl files under ~/.claude, which starts at tens of milliseconds once there are a lot
            // of sessions, and running that on the UI thread means the animation hitches every 0.8 seconds.
            // Once it comes back, the list is replaced wholesale on the UI thread — Sessions is itself
            // a complete snapshot of each round, so it can never be read half-finished.
            window.PollActivityAsync = async () =>
            {
                await Task.Run(watcher.Poll);
                model.Sessions = watcher.Sessions.ToList();
            };
            // The read state has to be recorded in the watcher, otherwise this unread item pops back up on the next poll
            window.MarkReadRequested = watcher.MarkRead;

            // HasToken only looks at the in-memory cache (reading from disk / decrypting has to happen
            // in the background; it must not stall the menu).
            // The consequence is that until the first fetch after start-up completes, the menu shows
            // "Log in to Claude account…" rather than "Log in again" — the macOS version's
            // fetcher.hasToken has the same temperament, and it counts as a known small inaccuracy.
            window.HasTokenProvider = () => tokens.HasToken;
            window.SignOutRequested = tokens.SignOutByUser;

            // The same verifier is reused within a single run: if a new one were generated on every
            // click of "log in", the code the user copied from the previous authorisation page (a
            // browser very easily leaves an old tab lying around) would never match, which presents as
            // "logging in fails over and over".
            // It is only invalidated and started afresh once login succeeds. verifier is read and
            // written on the UI thread only (both the authorisation-page button and the code-exchange
            // callback are on the UI thread), so no lock is needed.
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
                    return new LoginOutcome(false, "The sign-in flow lost its state. Please start it again.");
                }
                try
                {
                    // No ConfigureAwait(false) here: the whole chain stays on the UI thread, which is
                    // what lets the clearing of verifier below and the model changes over on the
                    // window side stop worrying about threads
                    var token = await OAuthClient.ExchangeCodeAsync(code, v, CancellationToken.None);
                    var saved = TokenStore.Save(token);
                    tokens.AdoptToken(token);
                    verifier = null;                  // only swap in a new one once it has succeeded
                    return new LoginOutcome(true, saved
                        ? null
                        : "Signed in, but the token could not be saved locally. You may have to sign in again next time.");
                }
                catch (Exception ex)
                {
                    // Describe spells out the actual reason, things like "there's an old authorisation
                    // page still sitting in the browser". Swap that for a bare "login failed" and all
                    // the user can do is keep trying the same bad code over and over
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
                // Without disposing explicitly, a ghost icon you cannot click is left behind in the
                // tray after the process exits, and it only vanishes once the user sweeps the mouse
                // across that patch — an old Windows affliction; we have to clean up after ourselves
                _tray?.Dispose();
                _tray = null;
                _fetcher?.Dispose();   // cancel in-flight requests; don't leave the process dragging an HTTP round trip behind it
                _fetcher = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The tray icon. Corresponds to macOS's NSStatusItem: with no Dock icon / taskbar button this is
    /// the only stable way in — whether the window has been dragged onto a monitor that has since been
    /// unplugged, or the user has forgotten what that little sun is, it can be recovered from here.
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
        // Left click: pull the pet back somewhere visible. Windows users have muscle memory for
        // "click the tray icon = get the window back"
        tray.Clicked += (_, _) => window.BringToFront();   // left click only summons it to the front, it doesn't move it

        // A TrayIcon only actually registers with the system tray once it is attached to the Application
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        _tray = tray;
    }

    /// <summary>
    /// The tray icon is an embedded 32×32 PNG (a nine-rayed sun) rather than an external resource file:
    /// one fewer AvaloniaResource entry to configure, one fewer "file not found after publishing"
    /// failure point.
    /// A proper release should swap in the multi-size .ico from design; this is a workable placeholder.
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
            System.Diagnostics.Debug.WriteLine($"[Sundial] Failed to load the tray icon: {ex.Message}");
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
