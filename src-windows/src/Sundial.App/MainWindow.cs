// Sundial (Windows version) — the desktop pet's window shell
//
// The counterpart of the window part of PetWindow / AppDelegate in the macOS App.swift:
// borderless floating window, press-and-drag, hover-to-expand, context menu, login box,
// launch at login, animation metronome.
// The drawing itself is left entirely to PetRenderer; this file only deals with "how big the
// window should be, where to put it, and when to redraw".
//
// The biggest coordinate-system difference from the macOS version: AppKit's screen coordinate
// origin is at the bottom left with Y pointing up, so over there every height change has to
// back-compute origin.y from anchorTopY, otherwise the window grows upwards from its bottom edge.
// Win32 / Avalonia's Position is the top-left corner with Y pointing down: when the height grows
// the top-left corner stays put, so it naturally extends downwards.
// But the concept of an "anchor" still has to be kept (see ApplyAnchoredPosition): when the
// window is hugging the bottom edge of the screen it has to move up temporarily to make room,
// and once it has collapsed again it must go back to where the user originally put it — if you
// only clamp in one direction, the pet gets shoved upwards again and again and can never come
// back down.

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

/// <summary>The result of exchanging for a token. When <paramref name="Message"/> is non-empty
/// it has to be shown to the user (the reason it failed, or a succeeded-but-worth-mentioning
/// case such as "the login worked but the token didn't get saved").</summary>
public readonly record struct LoginOutcome(bool Ok, string? Message);

/// <summary>
/// Does exactly one thing: forwards Avalonia's Render to PetRenderer, and lays a card backdrop
/// underneath it.
/// The reason for pulling this out into its own Control instead of overriding Render on the
/// Window directly is to let the window keep its identity as "an ordinary container", so that
/// the behaviours attached to the window — dragging, the context menu — are unaffected by the
/// custom drawing.
/// </summary>
internal sealed class PetSurface : Control
{
    private readonly PetRenderer _renderer;
    private readonly PetModel _model;

    /// <summary>The window layer telling us "the system blur has actually taken effect". When
    /// it has, the hand-drawn backdrop has to give way, otherwise a translucent backdrop stacked
    /// on top of the acrylic amounts to throwing a grey veil over the blur.</summary>
    public bool SystemBlurActive { get; set; }

    public PetSurface(PetRenderer renderer, PetModel model)
    {
        _renderer = renderer;
        _model = model;
        // Pointer events are all handled by the window (the whole window is a clickable area),
        // so this one steps aside to stop the hit testing from fighting
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        // Don't draw the glass backdrop under "reduce transparency": PetRenderer adds an opaque
        // backing plate of its own, so drawing both layers is wasted work and also skews the
        // colour of that plate
        // When fully collapsed the whole backdrop leaves the stage and only a sun is left
        // floating on the desktop — while it is idle there is no information to carry, so that
        // backdrop is pure surplus
        if (!_renderer.ReduceTransparency && _renderer.ExpandProgress > 0.01)
            DrawBackdrop(context, bounds);
        _renderer.Render(context, bounds);
    }

    /// <summary>
    /// The card backdrop — this is an approximation of the macOS version's "liquid glass"
    /// (NSGlassEffectView), <b>not the native effect</b>.
    ///
    /// Why it is hand-drawn by default: Windows' AcrylicBlur applies to the window's <b>whole
    /// rectangle</b>, whereas this window is a circle 88 across when collapsed and a card with a
    /// 26 corner radius when expanded — the shape changes every frame. The system blur doesn't
    /// follow the shape, so the ring outside the rounded corners shows a square blurred patch,
    /// which looks worse than not turning it on at all. Hence the default: a hand-drawn
    /// translucent rounded backdrop plus a ring of edge highlight. Anyone who wants to try the
    /// native blur will find a "system blur background (experimental)" toggle in the context menu.
    /// What the hand-drawn version lacks is refraction and background sampling; it only has
    /// "translucency + edge highlight", so it looks a notch weaker than the macOS version.
    /// </summary>
    private void DrawBackdrop(DrawingContext context, Rect bounds)
    {
        var e = Math.Clamp(_renderer.ExpandProgress, 0, 1);
        // Collapsed, the radius is half the side length (a perfect circle); expanded it converges
        // to 26, with linear interpolation in between — this step has to track ExpandProgress,
        // otherwise the shape comes adrift from the content's expand animation
        var compactR = Math.Min(bounds.Width, bounds.Height) / 2;
        var radius = compactR + (PetRenderer.CardRadius - compactR) * e;
        // The backdrop has to leave the stage before the window does: if it only starts to fade
        // once the window has already shrunk to sun size, you see a distinctly circular patch of
        // colour simply snap out of existence. It is done by 0.45
        var backdropAlpha = Sundial.App.Theme.EaseInOut(Math.Clamp(e / 0.45, 0, 1));
        if (backdropAlpha < 0.004) return;

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        // With the system blur on, the fill colour has to retreat to barely more than a slight
        // darkening, handing the brightness back to the acrylic
        var fillAlpha = (SystemBlurActive ? (dark ? 0x33 : 0x4D) : (dark ? 0x6E : 0xA8)) * backdropAlpha;
        var fill = dark
            ? new SolidColorBrush(Color.FromArgb((byte)fillAlpha, 0x1C, 0x1C, 0x1E))
            : new SolidColorBrush(Color.FromArgb((byte)fillAlpha, 0xFF, 0xFF, 0xFF));
        var edge = dark
            ? new SolidColorBrush(Color.FromArgb((byte)(0x2E * backdropAlpha), 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb((byte)(0x59 * backdropAlpha), 0xFF, 0xFF, 0xFF));

        // Inset the stroke by half a pixel, otherwise half of the 1px line falls outside the
        // window and gets clipped, and the thickness looks uneven
        var r = bounds.Deflate(0.5);
        var rr = Math.Max(0, radius - 0.5);
        context.DrawRectangle(fill, new Pen(edge, 1), r, rr, rr);

        // When a session is waiting for you to choose, tint the card slightly warm so that it
        // "lights up" by itself — the counterpart of the step in the macOS applyGlassShape that
        // sets tintColor on the glass. Over there the glass material carries that colour into its
        // refraction; here all we can do is stack another translucent warm layer on top, which is
        // an approximation. In the normal state there is never any tint, so it follows the
        // system's light/dark setting.
        // Spell out Sundial.App.Theme in full: Control has a Theme property of its own
        // (ControlTheme) which would shadow it
        if (_model.Sessions.Any(s => s.Waiting))
        {
            var tint = new SolidColorBrush(Sundial.App.Theme.WithAlpha(Sundial.App.Theme.CoralDeep, 0.20 * backdropAlpha));
            context.DrawRectangle(tint, null, r, rr, rr);
        }
    }
}

public sealed class MainWindow : Window
{
    // MARK: Layout constants
    //
    // Layout constants are always taken from PetRenderer (TopRowH / BlockGap / CompactSide /
    // CardRadius); no second copy is kept here — a copy would guarantee that when the layout
    // changes one of the two gets forgotten, and the window height stops matching the content.
    // Only the two below are purely window-layer quantities that PetRenderer never needs.
    private const double WinW = 198;
    private const double WinH = 182;           // The initial (loading state) height; adapts to the content afterwards

    // MARK: Data-layer wiring points
    //
    // Fetching, session polling and OAuth all live in Sundial.Core, and are wired up in one go by
    // App.OnFrameworkInitializationCompleted (see the wiring section in App.axaml.cs). The window
    // knows only these few delegates and never news up a data-layer object directly — that way
    // the whole shell can be run on its own, without network or disk, just to watch the
    // animation, and nothing crashes if the wiring was never connected.

    /// <summary>
    /// Returns the URL of the authorisation page. <b>It must return the same URL for the whole of
    /// one login attempt</b> (that is, the same PKCE verifier): if a new one is minted every time
    /// login is clicked, the code the user copies from the previous authorisation page (browsers
    /// very easily keep the old tab around) will never match, which shows up as "login keeps
    /// failing". Only after a successful login is a new one allowed.
    /// </summary>
    public Func<string?>? AuthorizeUrlProvider;

    /// <summary>Exchanges the authorisation code for a token. Don't throw on failure; put the
    /// reason meant for the user into <see cref="LoginOutcome.Message"/>.</summary>
    public Func<string, Task<LoginOutcome>>? ExchangeCodeAsync;

    /// <summary>Whether there is currently a usable token (decides whether the menu shows "log in"
    /// or "log in again / log out").</summary>
    public Func<bool>? HasTokenProvider;

    public Action? SignOutRequested;
    public Action? ForceRefreshRequested;
    public Func<Task>? FetchUsageAsync;      // Once every 15 seconds
    public Func<Task>? PollActivityAsync;    // Once every 0.8 seconds; reads the session records under ~/.claude

    /// <summary>Marks a session as read. This has to reach the data layer, otherwise the next
    /// round of polling brings it straight back up.</summary>
    public Action<string>? MarkReadRequested;

    private readonly PetModel _model;
    private readonly PetRenderer _renderer;
    private readonly PetSurface _surface;
    private readonly ShellSettings _settings;

    private readonly DispatcherTimer _animTimer = new();
    private readonly DispatcherTimer _fetchTimer = new();
    private readonly DispatcherTimer _activityTimer = new();
    private readonly DispatcherTimer _saveTimer = new();   // Debounce for writing the position to disk
    private double _animFps;
    private bool _activityPolling;   // Don't stack another round on while the previous disk poll hasn't come back (the counterpart of Swift's activityPolling)
    private bool _adjusting;         // A programmatic position change must not be taken as a user drag
    private bool _dragging;          // The user is holding the window and dragging it; don't fight them for the position meanwhile
    private bool _loginInProgress;   // Read and written on the UI thread only, so concurrent logins can't overwrite each other
    private LoginWindow? _loginWindow;
    private int _dialogs;            // How many modal notices are open; see PushDialog
    private PixelPoint? _anchor;     // Where the user put the pet (top-left corner); the reference point when it grows and shrinks
    private PixelPoint? _selfMoved;  // The most recent move we initiated ourselves, used to recognise echo events
    private Point? _pointerInWindow;             // The pointer's position inside the window (the fallback on non-Windows platforms)
    private DateTimeOffset? _hoverSince;         // When the hover started; stay long enough and we count it as seen
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
        // The interface is drawn entirely by hand, so screen readers see nothing at all in the
        // visual tree. The original builds a tree of accessibility elements by hand on PetView
        // (the two gauges / the login button / each session block is its own pressable element,
        // see accessibilityChildren); that whole set of AutomationPeers hasn't been ported over
        // here yet. At the very least, announce the window's own name, matching the original's
        // accessibilityLabel "Claude usage and session status".
        AutomationProperties.SetName(this, "Claude usage and session activity");
        SystemDecorations = SystemDecorations.None;   // Borderless: the whole window is that sun
        Background = Brushes.Transparent;             // Outside the rounded corners it must be genuinely transparent, or a black/white square shows through
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;         // The size is taken over entirely by ApplyDesiredSize
        ShowActivated = false;                        // Don't steal focus at startup; for not stealing focus on click see ApplyNoActivate
        WindowStartupLocation = WindowStartupLocation.Manual;
        // The macOS version deliberately turns the system drop shadow off: the window is
        // constantly growing and deforming, and the shadow leaves a square black outline behind.
        // Avalonia's SystemDecorations.None has no shadow to begin with, so nothing extra is
        // needed here.

        ApplyTransparencyHint();
        ApplyAppearance();

        _surface = new PetSurface(renderer, model);
        // The whole window has to be clickable (including the blank space outside the rounded
        // corners: that is part of the window rectangle too, and it makes dragging feel more
        // continuous), so a Panel with a transparent background is laid underneath — in Avalonia
        // a container with Background=null doesn't take part in hit testing.
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

        // The renderer calls back once per frame after working out the easing, and the window
        // resizes to follow — one of the sources of the "continuous growing and shrinking".
        // ApplyDesiredSize is also called from the timer; calling it in both places is
        // deliberate: this callback only fires when "something really changed this frame", so it
        // can't be relied on to replace the metronome.
        _renderer.OnLayoutChanged = ApplyDesiredSize;

        Width = WinW;
        Height = WinH;
        RestorePosition();
        RefreshAccessibilitySettings();

        _animTimer.Tick += (_, _) => OnAnimationTick();
        _fetchTimer.Interval = TimeSpan.FromSeconds(15);
        _fetchTimer.Tick += async (_, _) =>
        {
            // Take the chance to catch up with the system appearance / accessibility settings.
            // The macOS version subscribes to notifications; on Windows there is no equivalent
            // cross-platform notification source (theme changes have ActualThemeVariantChanged,
            // "reduce motion" has nothing), and polling once every 15 seconds is plenty: after
            // the user changes it they wait one round at most.
            RefreshAccessibilitySettings();
            await RunHookAsync(FetchUsageAsync);
        };
        _activityTimer.Interval = TimeSpan.FromSeconds(0.8);
        _activityTimer.Tick += async (_, _) => await PollActivityTick();
        // Position debounce: while dragging, PositionChanged fires away at the rate of the mouse
        // events, and writing the file every time means hundreds of disk I/Os per second. Write
        // to disk once, 1 second after the user stops
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settings.Save(); };
    }

    /// <summary>Called once the data layer has updated the model: recompute the size and redraw.
    /// Equivalent to fetcher.onUpdate in the Swift version.</summary>
    public void NotifyModelChanged()
    {
        ApplyDesiredSize();
        _surface.InvalidateVisual();
        RefreshMenus();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Setting Position before Show gets overridden by the startup location on some platforms;
        // doing it once more after show is the safest
        RestorePosition();
        ApplyNoActivate();
        UpdateAnimationState();
        _fetchTimer.Start();
        _activityTimer.Start();
        // Don't wait for the first 15 seconds: there should be data as soon as it starts
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
            _settings.Save();       // Quitting before the debounce fired: don't lose this one
        }
        base.OnClosed(e);
    }

    // MARK: Animation metronome

    /// <summary>
    /// If it can be seen, let it move; full frame rate while interacting or busy, and 24fps for
    /// plain breathing and blinking to save power.
    /// The macOS version also stops the clock when "the display sleeps" or "the window is fully
    /// occluded" (NSApp.occlusionState / screensDidSleep). Avalonia has no cross-platform
    /// occlusion or sleep notification, so this degrades to "stop when the window is invisible or
    /// minimised" — with the screen off it still burns a little power for nothing, which is a gap
    /// in the port.
    /// </summary>
    private void UpdateAnimationState()
    {
        var visible = IsVisible && WindowState != WindowState.Minimized;
        // When it can't be seen, slow the disk polling down to 5 seconds; no point reading that
        // pile of jsonl under ~/.claude for nothing
        // (the macOS version does the same thing in applicationDidChangeOcclusionState)
        var wantPoll = TimeSpan.FromSeconds(visible ? 0.8 : 5.0);
        if (_activityTimer.Interval != wantPoll) _activityTimer.Interval = wantPoll;

        if (!visible)
        {
            SetAnimating(0);
            return;
        }
        // Breathing and blinking always count as "there is animation"; stop them and it becomes a
        // dead picture. If the day ever comes when it isn't needed, NeedsContinuousAnimation will
        // tell us the whole clock can be stopped
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

        // After the window shrinks the cursor may no longer be over it, but shrinking doesn't
        // necessarily send a PointerExited afterwards (the macOS version came a cropper on this:
        // setFrame doesn't deliver a mouseExited to a stationary cursor, and the hover state gets
        // stuck on).
        // Falling back to IsPointerOver every frame is more reliable than the events.
        if (_model.Hovered != IsPointerOver) SetHovered(IsPointerOver);

        UpdateMousePoint();
        NoteSeenWhileHovering();
        _renderer.Advance(dt);
        // Adjust the window size to the expand progress every frame, which makes the growing and
        // shrinking animation continuous
        ApplyDesiredSize();
        _surface.InvalidateVisual();

        // Switch the frame rate automatically when the state changes (a round of thinking
        // starting or finishing, for example)
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
            ApplyDesiredSize();   // A change in the number of session blocks changes the height
            _surface.InvalidateVisual();
        }
        finally
        {
            _activityPolling = false;
        }
    }

    /// <summary>
    /// Feed the cursor position to the renderer every frame — that is what makes the sun's rays
    /// stretch towards the mouse.
    /// What PetRenderer wants is the <b>global</b> cursor (converted into view coordinates): it
    /// should already react while the cursor is still outside the window and approaching, because
    /// "gravity" acts at a distance by its very nature. So on Windows we ask the system directly
    /// with GetCursorPos rather than relying only on PointerMoved inside the window. Anything
    /// beyond 230pt is nulled out by the renderer itself, so there is no need to filter it here.
    /// </summary>
    private void UpdateMousePoint()
    {
        if (OperatingSystem.IsWindows() && Win32Cursor.TryGetPosition(out var x, out var y))
        {
            // The `this.` can't be dropped: PointToClient is an extension method on
            // Avalonia.VisualExtensions, not an instance method on Window, so leaving the
            // receiver out doesn't compile
            _renderer.MousePoint = this.PointToClient(new PixelPoint(x, y));
            return;
        }
        // On non-Windows (when we run the checks on a Mac) this degrades to only knowing about
        // the pointer inside the window: the moment the cursor leaves the window the gravity cuts
        // out, and the at-a-distance part of the effect is missing.
        _renderer.MousePoint = _pointerInWindow;
    }

    /// <summary>
    /// The mouse resting on the pet for 1.2 seconds = you have seen these notifications. Note
    /// them down first and only clear them once the mouse moves away, so that blocks don't
    /// disappear from under your nose.
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

    /// <summary>Once the mouse has left, clear all the unread items that were just seen.</summary>
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
            // SessionActivity is a record with read-only properties; changing one means replacing
            // the whole thing
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
            // An exception from the data layer must not take the pet down with it: its job is to
            // write the error into PetModel.ErrorMsg itself
            Debug.WriteLine($"[Sundial] Data hook threw: {ex}");
        }
    }

    // MARK: Sizing

    /// <summary>The height when fully expanded: top row (sun + gauges) + session blocks + (on
    /// hover) the detail rows.</summary>
    private double ExpandedHeight()
    {
        var h = 10 + PetRenderer.TopRowH + 2;   // Card top padding + top row
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
            // Use the continuously changing height from the renderer; never just count the
            // blocks. The block count is discrete, so the moment the last block disappears the
            // window drops 50pt in a single frame, all the easing is wasted, and it simply snaps
            // out of existence
            h += _renderer.ResetLineHeight;
            h += _renderer.BlocksHeight;
            // The detail area's height is interpolated continuously with the expand progress,
            // which is what lets the window grow and shrink smoothly
            var p = _renderer.HoverProgress;
            if (p > 0.001)
            {
                var detailH = PetRenderer.BlockGap + 2 + 19 + Math.Min(_model.Rows.Count, 5) * 15 + 18;
                h += detailH * p;
            }
        }
        return h + 10;              // Card bottom padding
    }

    /// <summary>
    /// The actual window size: interpolated by the expand progress between "nothing but the sun"
    /// and "the full card".
    /// The name has Compute in it so as not to collide with Layoutable.DesiredSize (that is the
    /// layout system's measurement result, an entirely different thing).
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
    /// Re-place the window from the anchor every frame. The anchor is the top-left corner the user
    /// dragged it to; Y points down, so growing taller means growing downwards, and under normal
    /// conditions it doesn't budge. Only when it would push out of the working area does it pull
    /// back temporarily — hugging the bottom it opens upwards, hugging the right it gives way to
    /// the left.
    /// <b>Recomputed from the anchor every frame</b> rather than "clamp the current position": if
    /// you only clamp, the window gets shoved up when it expands at the bottom edge of the screen
    /// and then stays at the shoved-up position after it collapses, and after a few rounds of
    /// that the pet has climbed all the way to the middle of the screen.
    /// </summary>
    private void ApplyAnchoredPosition(Size sizeDip)
    {
        if (_anchor is not { } anchor) return;
        // Never interfere while the user is dragging the window. The original's adjustWindowHeight
        // returns straight away when the size hasn't changed, so a drag never reaches the clamping
        // step at all; here everything is recomputed from the anchor every frame, and without this
        // gate, dragging the pet towards the edge of the working area (the taskbar, the left and
        // right edges of the screen) clamps it back once per frame — it feels like the window
        // can't keep up with the cursor and keeps springing back. The frame after the user lets go
        // clamps as usual, so where it ends up is unchanged.
        if (_dragging) return;
        var x = anchor.X;
        var y = anchor.Y;

        var screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary;
        if (screen is not null)
        {
            // WorkingArea is in physical pixels and Width/Height are DIPs, so they have to be
            // converted before they can be compared
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

    // MARK: Position memory

    /// <summary>A move initiated by the program. Note the target point down, so that the
    /// PositionChanged that comes back afterwards can be recognised.</summary>
    private void MoveTo(PixelPoint p)
    {
        _selfMoved = p;
        _adjusting = true;
        Position = p;
        _adjusting = false;
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_adjusting) return;                       // Programmatic resizing doesn't count as a drag
        // Some backends queue the move notification for the next round of the message loop, by
        // which time _adjusting has already been reset. Without recognising this kind of "echo",
        // a single clamp gets treated as a user drag and written into the anchor, and the pet
        // starts drifting.
        if (_selfMoved is { } self && self == e.Point) return;

        _anchor = e.Point;
        if (_settings.WindowX == e.Point.X && _settings.WindowY == e.Point.Y) return;
        // Store the top-left corner. The macOS version fell into a trap here: if you store the
        // bottom-left corner, then since the window height varies with the content it drifts up a
        // little on every restart. Win32's origin is at the top-left to begin with, so the problem
        // simply doesn't arise.
        _settings.WindowX = e.Point.X;
        _settings.WindowY = e.Point.Y;
        _saveTimer.Stop();          // Debounce: the file is only really written 1 second after the drag stops
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
        // The stored position isn't on any screen (the external monitor was unplugged), or this is
        // the first launch: land by default a little above the bottom-right corner, so it doesn't
        // sit on top of the taskbar
        var wa = (Screens.Primary ?? Screens.All.FirstOrDefault())?.WorkingArea;
        if (wa is null) return;
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        var w = (int)Math.Round(WinW * scale);
        var h = (int)Math.Round(WinH * scale);
        _anchor = new PixelPoint(wa.Value.X + wa.Value.Width - w - (int)(24 * scale),
                                 wa.Value.Y + wa.Value.Height - h - (int)(60 * scale));
        MoveTo(_anchor.Value);
    }

    /// <summary>Whether the stored position is still on some screen (it has to be findable again
    /// after an external monitor is unplugged).</summary>
    private bool IsOnAnyScreen(int x, int y)
    {
        // Only tests whether that single top-left point falls inside some screen's working area.
        // A rectangle-intersection test would be more precise, but the most basic arithmetic is
        // enough here, and it doesn't gamble on PixelRect's intersection API across versions.
        foreach (var s in Screens.All)
        {
            var wa = s.WorkingArea;
            if (x >= wa.X && x < wa.X + wa.Width && y >= wa.Y && y < wa.Y + wa.Height) return true;
        }
        return false;
    }

    /// <summary>Drags the pet back to somewhere visible on the main screen. Both the tray's left
    /// click and the menu use it — when the window has been dragged onto a monitor that has since
    /// been unplugged, this is the only way back.</summary>
    /// <summary>Only brings the window to the front, **without moving it**. This is what the
    /// tray's left click uses.
    /// Originally the tray's left click called EnsureVisible directly, and the result was that one
    /// click teleported the pet to the bottom-right corner, and wrote the new coordinates into the
    /// settings as well — the position you had arranged was just gone, and a restart wouldn't
    /// bring it back either.</summary>
    public void BringToFront()
    {
        if (_dialogs == 0) Topmost = true;   // Don't go topmost again while a dialog is open, or it would cover it
        Show();
    }

    /// <summary>Moves the window back to the bottom-right corner of the screen and remembers the
    /// new position. Should only ever be triggered explicitly by that one menu item.</summary>
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
        if (_dialogs == 0) Topmost = true;   // Don't go topmost again while a dialog is open, or it would cover it
        Show();
    }

    // MARK: Pointer

    private void SetHovered(bool hovering)
    {
        if (_model.Hovered == hovering) return;
        _model.Hovered = hovering;
        _hoverSince = hovering ? DateTimeOffset.Now : null;
        if (!hovering) FlushSeen();   // On leaving, mark everything just seen as read
        _surface.InvalidateVisual();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;   // The right button is left to the ContextMenu itself

        if (e.ClickCount >= 2)
        {
            // Double click = refresh now, or log in when not logged in.
            // It has to be decided here and can't use DoubleTapped: that single click has already
            // called BeginMoveDrag, Win32 enters its own drag message loop, and the double-click
            // gesture that follows never gets back to us at all.
            if (_model.NeedsLogin) StartLogin();
            else ForceRefreshRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // Hit-test first, then decide whether to drag: the login button and the session blocks are
        // "controls", and all the rest of it is a drag handle.
        // The order can't be reversed — once BeginMoveDrag has been called we are inside the
        // system's drag loop, and this click is gone.
        var p = e.GetPosition(this);
        var loginRect = _renderer.LoginButtonRect;
        // The Width > 0 test can't be left out: when no login button has been drawn,
        // LoginButtonRect is default, and Avalonia's Rect.Contains is closed at both ends, so an
        // empty rectangle still "contains" the origin — clicking that one point at the window's
        // top-left corner would inexplicably start a login. AppKit's NSRect.contains is always
        // false for an empty rectangle, so the original never has to worry about this.
        if (_model.NeedsLogin && loginRect.Width > 0 && loginRect.Contains(p))
        {
            StartLogin();
            e.Handled = true;
            return;
        }
        foreach (var (id, rect) in _renderer.BlockRects)
        {
            if (!rect.Contains(p)) continue;
            // Only an "unread" block swallows this click; a block that is still running has to let
            // it through so the drag can continue — this matches the `if ...unread == true` line
            // in the original's mouseDown (when it isn't satisfied it doesn't return, and falls
            // all the way through to performDrag). Intercepting it has two consequences: first,
            // what MarkRead writes into is ActivityWatcher's suppression set, which isn't released
            // until this session starts a new round, so the "unread" notice for the run it is
            // finishing now gets swallowed early; second, once there are a lot of sessions the
            // blocks cover almost the whole card, and the pet can't be dragged any more.
            var hit = _model.Sessions.FirstOrDefault(s => s.Id == id);
            if (hit is null || !hit.Unread) continue;
            MarkRead(id);          // Click away one unread notification
            e.Handled = true;
            return;
        }

        // Holding anywhere on the window drags it (performDrag in the macOS version).
        // Clear the "echo position" before the drag starts: the user could perfectly well drag the
        // window back to the exact point we last clamped it to, and keeping it around would make
        // this genuine drag get swallowed as an echo, the anchor wouldn't update, and the window
        // would spring back by itself on the next frame
        _selfMoved = null;
        _dragging = true;
        try
        {
            // On Win32 this line enters the system's drag message loop and doesn't return until
            // the user lets go; the timers keep running meanwhile, which is why the _dragging gate
            // is needed (see ApplyAnchoredPosition)
            BeginMoveDrag(e);
        }
        finally
        {
            _dragging = false;
        }
    }

    // MARK: Menus
    //
    // The tray's native menu and the window's context menu are two different sets of controls
    // (NativeMenu vs ContextMenu), yet the items have to be exactly the same, so they are first
    // described as a list of MenuEntry and then generated twice.

    private sealed record MenuEntry(
        string Text,
        Action? Invoke = null,
        bool IsSeparator = false,
        Func<bool>? Checked = null,
        Func<bool>? Enabled = null,
        Func<string>? DynamicText = null);

    /// <summary>The assembly version, passed in by build.sh from the VERSION file at the root of
    /// the repository (-p:Version). It shares the same source as the macOS version.</summary>
    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    private List<MenuEntry> BuildMenuEntries() => new()
    {
        // The version number goes at the very top of the menu: when someone reports a problem, the
        // first thing you can ask is "what does the menu say?"
        new MenuEntry($"Sundial {AppVersion}", Enabled: () => false),
        new MenuEntry("", IsSeparator: true),
        new MenuEntry("Sign in to Claude account…", StartLogin,
            DynamicText: () => LoggedIn ? "Sign in to Claude account again…" : "Sign in to Claude account…"),
        new MenuEntry("Sign out", SignOut, Enabled: () => LoggedIn),
        new MenuEntry("", IsSeparator: true),
        new MenuEntry("Refresh now", () => ForceRefreshRequested?.Invoke()),
        // An equivalent entry point besides hovering: you can look at the details without keeping
        // the mouse parked on the window
        new MenuEntry("Keep usage breakdown open", ToggleDetails, Checked: () => _model.DetailsPinned),
        new MenuEntry("Open the web usage page", () => OpenUrl("https://claude.ai/settings/usage")),
        new MenuEntry("Bring the pet back to the bottom-right", EnsureVisible),
        new MenuEntry("", IsSeparator: true),
        new MenuEntry("System blur background (experimental)", ToggleSystemBlur, Checked: () => _settings.SystemBlur),
        new MenuEntry("Launch at login", ToggleAutostart, Checked: () => AutoStart.IsEnabled,
            Enabled: () => OperatingSystem.IsWindows()),
        new MenuEntry("", IsSeparator: true),
        new MenuEntry("Quit Sundial", QuitApp),
    };

    private bool LoggedIn => HasTokenProvider?.Invoke() ?? false;

    /// <summary>
    /// The checked state is shown with a text prefix rather than with ToggleType / IsChecked.
    /// The reason is entirely practical: the checked properties of NativeMenuItem and MenuItem
    /// don't quite agree in name and behaviour across Avalonia's minor versions, whereas a prefix
    /// behaves exactly the same in both menus and has no platform differences either.
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
        // Refresh the wording just before the menu pops up: the login state may have changed
        // between two openings
        menu.Opening += (_, _) => RefreshMenus();
        return menu;
    }

    /// <summary>
    /// Refreshes the wording and enabled state of both menus.
    /// The macOS version rebuilds the items on the spot in menuNeedsUpdate; Avalonia's tray menu
    /// has no reliable "about to pop up" callback, so this refreshes eagerly whenever the state
    /// changes instead — it's only a handful of string assignments anyway.
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
        // When it's on, ask for [AcrylicBlur, Transparent]: Windows 11 gives acrylic blur, and if
        // we don't get it we fall back to plain transparency (the whole look is then carried by
        // the rounded backdrop PetSurface draws by hand).
        // When it's off, ask only for Transparent, to avoid a ring of square blur showing outside
        // the rounded corners (see the notes on PetSurface).
        TransparencyLevelHint = _settings.SystemBlur
            ? new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent }
            : new[] { WindowTransparencyLevel.Transparent };
        // Whether the system actually gave us acrylic is for ActualTransparencyLevel alone to say
        // — use that to decide how strong the hand-drawn backdrop is, not the wish that is "the
        // user turned the toggle on"
        _surface?.InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Note: property changes already arrive during the base class's construction, when the
        // ctor body hasn't finished running, so every branch has to confirm first that _surface
        // has been built (it is among the earliest things the ctor body assigns)
        if (_surface is null) return;

        if (change.Property == ActualTransparencyLevelProperty)
        {
            _surface.SystemBlurActive = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
            _surface.InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
        {
            // This has to hang off the property: once the animation clock has been stopped
            // because the window is invisible, nothing else would ever come along to wake it up
            // again, and the pet would be frozen forever (relying on OnAnimationTick calling
            // UpdateAnimationState itself leaves a hole in that loop)
            UpdateAnimationState();
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            // UseShellExecute = true is the key: since .NET Core the default has been false, which
            // amounts to asking CreateProcess to run an http link, and it throws "file not found"
            // straight away
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Sundial] Failed to open the link: {ex.Message}");
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

    // MARK: Login

    private void SignOut()
    {
        SignOutRequested?.Invoke();
        _model.NeedsLogin = true;
        _model.Rows = new List<UsageRow>();
        _model.Tier = "";
        _model.ErrorMsg = "Signed out\nDouble-click me to sign in again";
        _model.Asleep = true;
        _model.Loading = false;
        NotifyModelChanged();
    }

    private void StartLogin()
    {
        if (_loginInProgress)
        {
            _loginWindow?.Activate();   // Already open, so bring it to the front rather than popping up a second one
            return;
        }

        // The URL has to stay stable within one login; the reason is in the notes on
        // AuthorizeUrlProvider
        var url = AuthorizeUrlProvider?.Invoke();
        if (string.IsNullOrEmpty(url)) return;
        string authorizeUrl = url;

        _loginInProgress = true;
        // Open the authorisation page for the user first (matching NSWorkspace.shared.open in the
        // original's startLogin).
        // Miss this step and all the user sees after clicking "log in" is a box asking them to
        // paste an authorisation code, with nobody telling them where the code comes from.
        OpenUrl(authorizeUrl);

        // Wait 1 second before popping up the input box (matching the original's
        // asyncAfter(.now() + 1.0)): the browser is still starting up, and pushing a topmost window
        // to the front at that moment snatches back the focus we have just handed over, so the
        // authorisation page ends up buried underneath instead.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            if (!_loginInProgress) return;   // The state has already been cleaned up during that one second
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
    /// The whole method runs on the UI thread: it starts from a UI event, await captures
    /// Avalonia's synchronisation context by default, and the thread is yielded while waiting on
    /// the network. So the model changes below don't need to be Posted back.
    /// </summary>
    private async Task FinishLoginAsync(string pasted)
    {
        LoginOutcome outcome;
        try
        {
            outcome = ExchangeCodeAsync is null
                ? new LoginOutcome(false, "The sign-in module is not wired up.")
                : await ExchangeCodeAsync(pasted);
        }
        catch (Exception ex)
        {
            // By convention the hook shouldn't throw, and even if it does the pet mustn't die
            // along with it
            Debug.WriteLine($"[Sundial] Token exchange failed: {ex}");
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
            if (!LoggedIn)             // Don't turn an already successful login back into logged-out
            {
                _model.NeedsLogin = true;
                _model.Rows = new List<UsageRow>();   // Without clearing it the login card and button don't get rendered
                _model.Tier = "";
                _model.ErrorMsg = "Sign-in failed\nDouble-click me to retry";
                _model.Asleep = true;
            }
        }
        NotifyModelChanged();

        // The reason for the failure has to be put in front of the user verbatim: that text says
        // things like "the browser is still holding the old authorisation page" — problems the
        // user can only sort out for themselves if they actually see them. Swallow it and all
        // they are left with is "it failed again"
        if (!string.IsNullOrEmpty(outcome.Message))
        {
            ShowNotice(outcome.Ok ? "Notice" : "Sign-in failed", outcome.Message!);
        }
    }

    /// <summary>While a dialog is up, drop the pet back to the ordinary window level, otherwise,
    /// being topmost, it would sit on top of the dialog (the counterpart of the macOS version's
    /// withLoweredWindow). It's a count because the login box and a notice box can be open on top
    /// of each other.</summary>
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
    /// The login box: an "open the authorisation page" button plus an input box for pasting the
    /// authorisation code.
    /// The lesson from the macOS version was pasting: over there, in .accessory mode there is no
    /// main menu, ⌘V has no responder to route to, and the user simply couldn't paste anything in
    /// — they had to type a code of more than two hundred characters by hand (it was eventually
    /// rescued by adding a whole Edit menu).
    /// Windows' TextBox has Ctrl+V built in, but to make absolutely sure history doesn't repeat
    /// itself there is also a "paste" button here that reads the clipboard directly — it doesn't
    /// depend on any shortcut routing.
    /// </summary>
    private sealed class LoginWindow : Window
    {
        private readonly TextBox _input;
        private readonly Action<string?> _onDone;
        private bool _reported;

        public LoginWindow(string authorizeUrl, Action<string?> onDone)
        {
            _onDone = onDone;

            Title = "Connect your Claude account";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = true;   // The pet is topmost, so a login box that isn't could end up buried underneath it

            _input = new TextBox
            {
                Watermark = "Paste the authorisation code here",
                AcceptsReturn = false,
                MinWidth = 300,
            };

            // The authorisation page was already opened for the user before this box popped up
            // (see StartLogin); this button is the fallback: if the default browser isn't set up
            // properly, or the user's hand slipped and closed the tab, it can be opened again
            var openBtn = new Button { Content = "Reopen the authorisation page" };
            openBtn.Click += (_, _) => OpenUrl(authorizeUrl);

            var pasteBtn = new Button { Content = "Paste" };
            pasteBtn.Click += async (_, _) =>
            {
                var clip = Clipboard;   // The clipboard that comes with TopLevel; no need to go hunting for a TopLevel
                if (clip is null) return;
                var text = await clip.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text)) _input.Text = text.Trim();
            };

            var okBtn = new Button { Content = "Finish signing in", IsDefault = true };
            okBtn.Click += (_, _) => Finish(_input.Text);

            var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
            cancelBtn.Click += (_, _) => Finish(null);

            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Your browser has opened Claude's authorisation page. Sign in there and approve.\n"
                             + "Then paste the code it gives you below (the whole address-bar URL works too).\n\n"
                             + "Note: if an older authorisation page is still open, use the one that has just "
                             + "opened — a code from the old page will not work.",
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
            // The state also has to be reclaimed when the user just clicks the close button in the
            // top-right corner, otherwise _loginInProgress stays true forever and logging in is
            // impossible from then on
            if (!_reported)
            {
                _reported = true;
                _onDone(null);
            }
            base.OnClosed(e);
        }
    }

    /// <summary>The counterpart of the macOS version's warn(): one sentence plus an "OK".
    /// The text has to be selectable and copyable — the explanation of a login failure contains
    /// steps for the user to follow.</summary>
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
                Content = "OK",
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
        // If it can't be changed we have to say so (matching the original's toggleAutostart, which
        // warns "couldn't change the launch-at-login setting: …" as soon as it catches an
        // exception). On corporate machines HKCU\...\Run is often locked down by group policy, and
        // if it fails silently what the user sees is "I ticked it, it didn't tick, and nobody told
        // me why"
        if (err is not null) ShowNotice("Setting failed", "Could not change the launch-at-login setting: " + err);
    }

    // MARK: Appearance and accessibility

    /// <summary>
    /// Writes "is it dark or light right now" into Theme. The macOS version uses NSColor dynamic
    /// colours, which re-resolve automatically the moment the system switches; Avalonia has no
    /// equivalent inside a DrawingContext, so the window layer has to push the switch across
    /// (which is exactly what the comment on Theme.IsDark says). Miss this step and the whole set
    /// of semantic colours uses the wrong variant on a dark desktop, dropping the text contrast
    /// straight below the line.
    /// </summary>
    private void ApplyAppearance()
    {
        // The full name is required: StyledElement has a Theme property of its own (ControlTheme),
        // and writing Theme bare inside a Window resolves to that and fails at compile time —
        // spelling it out in full settles it once and for all
        Sundial.App.Theme.IsDark = ActualThemeVariant == ThemeVariant.Dark;
        _surface?.InvalidateVisual();
    }

    /// <summary>
    /// Syncs the system's "reduce motion / turn off transparency effects / high contrast" over to
    /// the renderer.
    /// The macOS version reads the NSWorkspace.accessibilityDisplay* family; on Windows there is
    /// no single entry point: the animation preference goes through SystemParametersInfo,
    /// transparency effects through the "Personalize" registry key, and high contrast through
    /// Avalonia's own PlatformSettings.
    /// On non-Windows everything is treated as "none of them are on" — the pet's animation runs as
    /// usual, which doesn't get in the way of checking things on a Mac.
    /// </summary>
    private void RefreshAccessibilitySettings()
    {
        ApplyAppearance();

        try
        {
            // ContrastPreference is a cross-platform item Avalonia has already abstracted; use it
            // where we can rather than reading the HIGHCONTRAST struct ourselves
            var colors = PlatformSettings?.GetColorValues();
            if (colors is not null)
            {
                Sundial.App.Theme.IncreaseContrast =
                    colors.ContrastPreference == Avalonia.Platform.ColorContrastPreference.High;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Sundial] Could not read the system colour preference: {ex.Message}");
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
        // With transparency effects off, stop asking the system for blur as well, otherwise a ring
        // of square blur is left outside the rounded corners
        if (reduceTransparency) TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        else ApplyTransparencyHint();
        NotifyModelChanged();
    }

    /// <summary>
    /// Clicking the pet shouldn't send the editor the user is writing in to the background — the
    /// counterpart of PetWindow.canBecomeKey = false in the macOS version.
    /// Avalonia doesn't expose this capability (ShowActivated only covers the first show), so we
    /// have to add WS_EX_NOACTIVATE to the window ourselves. There is nothing in the window that
    /// needs keyboard input (the login box is a separate window), so nothing is lost by not
    /// activating; dragging goes through WM_NCLBUTTONDOWN and the context menu is a separate popup
    /// window, neither of which depends on the activation state.
    /// Does nothing on non-Windows platforms.
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
            // Failure only means "clicking it steals focus for a moment", which isn't worth
            // crashing over
            Debug.WriteLine($"[Sundial] Could not set WS_EX_NOACTIVATE: {ex.Message}");
        }
    }

    private static class SystemA11y
    {
        // SPI_GETCLIENTAREAANIMATION: corresponds to Settings › Accessibility › Visual effects ›
        // Animation effects.
        // Use SystemParametersInfo rather than guessing at some bit inside UserPreferencesMask —
        // that mask has an undocumented layout, and a new release would have us reading it wrong.
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
                    return enabled == 0;   // The system saying "no animations" = our ReduceMotion
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Sundial] Could not read the animation preference: {ex.Message}");
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
                    // If the value isn't there, take transparency effects to be on (Windows has
                    // them on by default)
                    return key?.GetValue("EnableTransparency") is int v && v == 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Sundial] Could not read the transparency preference: {ex.Message}");
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

        /// <summary>Screen coordinates in physical pixels. The caller must make sure for itself
        /// that it is on Windows before calling.</summary>
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
                Debug.WriteLine($"[Sundial] Could not read the cursor position: {ex.Message}");
                return false;
            }
        }
    }

    private static class Win32Style
    {
        private const int GWL_EXSTYLE = -20;
        public const long WsExNoActivate = 0x08000000;

        // Only 64-bit processes have the GetWindowLongPtrW/SetWindowLongPtrW exports (on 32-bit
        // the headers #define them to ...LongW, and they don't exist in the DLL at all).
        // We only ship x64; if a 32-bit build ever appears, this would throw
        // EntryPointNotFoundException, and since the caller already has it wrapped in a try it
        // degrades to "clicking it steals focus" rather than crashing.
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

    // MARK: Launch at login
    //
    // macOS uses SMAppService; on Windows the least fuss and the most reliable is the Run key
    // under HKCU: no administrator rights are needed, and the user can see it and turn it off
    // themselves under Task Manager › Startup apps.
    // A scheduled task (schtasks) can get around startup-item management, which makes it less
    // transparent instead, so it isn't used.
    private static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Sundial";

        public static bool IsEnabled
        {
            get
            {
                // Return false outright on non-Windows: during development the whole interface has
                // to be runnable on a Mac so it can be checked
                if (!OperatingSystem.IsWindows()) return false;
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(ValueName) is string s && s.Length > 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Sundial] Could not read the launch-at-login setting: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>Returns null on success; on failure returns a one-line reason that can be put
        /// in front of the user.</summary>
        public static string? Set(bool on)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key is null) return "Could not open the registry key HKCU\\" + RunKey + "。";
                if (!on)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    return null;
                }
                // Environment.ProcessPath points at the real exe (correct for single-file
                // publishing too); Assembly.Location can't be used — with single-file publishing
                // it is an empty string, and writing that in gives you a dead entry.
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return "Could not determine this program's executable path.";
                // The path almost certainly contains spaces (Program Files / the user name), and
                // without quotes it gets split into two arguments
                key.SetValue(ValueName, "\"" + exe + "\"");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Sundial] Could not write the launch-at-login setting: {ex.Message}");
                return ex.Message;
            }
        }
    }
}

/// <summary>
/// The shell's own settings (the window position and the two toggles).
/// The JSON is read and written by hand rather than with JsonSerializer: there are only four
/// scalar fields, and doing it by hand involves no reflection, is unaffected by trimming / AOT,
/// and won't play up over type visibility either.
/// It lives under %APPDATA%\Sundial\; on macOS that path lands in ~/.config/Sundial, so the same
/// code runs on both (the .claude in the home directory is Claude Code's own records, and we don't
/// write our things into it).
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
            // A corrupt settings file is treated as no file at all: the pet has to be able to
            // start, and at worst losing the position means going back to the default corner
            Debug.WriteLine($"[Sundial] Could not read the settings file: {ex.Message}");
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
            Debug.WriteLine($"[Sundial] Could not write the settings file: {ex.Message}");
        }
    }
}
