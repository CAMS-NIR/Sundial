// Sundial (Windows version) — colour palette and small drawing helpers
//
// Ported from Theme.swift in the macOS version. Not one colour value has been changed: these RGBs
// were only settled on after measuring the contrast of each one under both the light and dark
// appearances, and changing a single digit could drop it below the WCAG line.

using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Sundial.App;

/// <summary>
/// The unified palette. There are only three groups in the whole thing:
/// (1) The coral family — the sun's body, the context bar, the waiting/unread dots, the refresh light wave
/// (2) The accent colours of the two gauges — honey gold (left) and apricot pink (right), plus their brighter glowing versions
/// (3) System semantic colours and neutral greys
/// There used to be a "sage green / amber / brick red" three-tier usage set as well; once the rings
/// were switched to fixed colours only two small elements were still using it, which amounted to
/// keeping a whole hue family alive just for them — it has been folded into the coral family and deleted.
/// </summary>
public static class Theme
{
    // The macOS version uses NSColor's dynamic colours, which re-resolve automatically when the
    // system switches appearance. Avalonia has no equivalent that can resolve in place inside a
    // DrawingContext, so all we can do is hoist "are we currently dark?" out into a single switch,
    // written by the window layer whenever the theme changes and consulted by everything that draws.
    public static bool IsDark { get; set; }

    // The system "increase contrast" setting. In the macOS version's PetView it is likewise only
    // stored and never used while drawing; it is kept so that the fields line up on both sides.
    public static bool IncreaseContrast { get; set; }

    // MARK: Mascot (does not change with appearance — the sun is this same warm coral on light and dark backgrounds alike)
    public static readonly Color CoralLight = Color.FromRgb(233, 152, 115); // #E99873
    public static readonly Color CoralDeep = Color.FromRgb(196, 103, 69);   // #C46745
    public static readonly Color SleepLight = Color.FromRgb(175, 169, 163);
    public static readonly Color SleepDeep = Color.FromRgb(140, 134, 128);
    // Target colour for darkening the body while asleep: a warm-ish dark grey. SunDeepen (deep brick
    // red) must not be used — mixing red into a grey body makes it look sickly, which is exactly the
    // pit we fell into before
    public static readonly Color SleepDeepen = Color.FromRgb(77, 71, 66);
    public static readonly Color FaceDark = Color.FromRgb(37, 27, 22);

    /// <summary>Target colour for deepening the body: a fixed deep brick red. It has to be a fixed
    /// value — an earlier version used a red that switched with light/dark, and in dark mode that red
    /// was actually the brighter of the two, so the more tense it got the lighter its body became:
    /// exactly backwards.</summary>
    public static Color SunDeepen => Color.FromRgb(139, 40, 29);

    public static Color ClaudeOrange => CoralDeep;

    // MARK: Semantic colours
    // macOS's labelColor / secondaryLabelColor / tertiaryLabelColor / windowBackgroundColor have no
    // counterpart on Windows, so they are copied across as fixed values matching what macOS actually
    // resolves to.
    // Note that these colours **carry their own alpha**, and the original's withAlphaComponent
    // "replaces" rather than "multiplies", so WithAlpha replaces here too.
    // These are deliberately a little more opaque than the macOS values they mirror.
    // The window is transparent, so Windows cannot use ClearType here — subpixel antialiasing needs
    // an opaque background, and on a layered window it produces colour fringes. That leaves greyscale
    // antialiasing, which draws noticeably thinner than Core Text does on macOS at the same alpha.
    // Copying the macOS numbers exactly therefore looked washed out on Windows, particularly the
    // secondary rows in the breakdown. Raised: secondary 128 -> 158, tertiary 66 -> 92 (light mode),
    // and correspondingly in dark mode. Primary text was already opaque enough to survive.
    public static Color LabelColor => IsDark ? Color.FromArgb(228, 255, 255, 255) : Color.FromArgb(228, 0, 0, 0);
    public static Color SecondaryLabelColor => IsDark ? Color.FromArgb(168, 255, 255, 255) : Color.FromArgb(158, 0, 0, 0);
    public static Color TertiaryLabelColor => IsDark ? Color.FromArgb(96, 255, 255, 255) : Color.FromArgb(92, 0, 0, 0);
    public static Color WindowBackground => IsDark ? Color.FromRgb(50, 50, 50) : Color.FromRgb(236, 236, 236);

    // MARK: The fixed colours of the two gauges + the glow colour for the ray tips on that side
    //
    // The key lesson: the gradient along a ray must get **brighter the further out it goes**. It used
    // to fade into a deep wine red / deep purple, and that dark colour sitting on the warm body simply
    // looked like a bruise — "the sun is ill".
    // The sun is a light source; its rays ought to get brighter towards the tip. Hence two colours per side:
    // the ring uses the more saturated one (it has to manage 3:1 against the glass), and the ray tips use
    // the brighter one (drawn on the sun's body, so the background does not constrain it).
    //
    // Colour no longer changes with usage: that is reported by the number in the middle, the arc length
    // and the sun's expression / body darkness together.
    // One colour per side has instead become an identity marker — you know which side is which without reading the label.
    /// <summary>Left gauge (5 hours) — honey gold. The light variant is darkened to just past 3:1.</summary>
    public static Color RingLeft => IsDark ? Color.FromRgb(242, 186, 76) : Color.FromRgb(188, 118, 18);
    public static Color GlowLeft => IsDark ? Color.FromRgb(255, 218, 146) : Color.FromRgb(255, 206, 110);
    /// <summary>Right gauge (weekly) — apricot pink.</summary>
    public static Color RingRight => IsDark ? Color.FromRgb(236, 139, 150) : Color.FromRgb(198, 84, 96);
    public static Color GlowRight => IsDark ? Color.FromRgb(255, 182, 187) : Color.FromRgb(255, 158, 164);

    /// <summary>
    /// The context arc on a session block, at <paramref name="frac"/> of the window used.
    /// <para>Warm rather than neutral. It used to run grey → black, which was the one cold element on
    /// a card otherwise made of honey gold, apricot pink and terracotta; at a high figure it turned
    /// into a near-complete black circle and pulled the eye off everything else. Running it through
    /// the sun's own family keeps "deepens as it fills" while leaving it part of the card.</para>
    /// <para>"Deeper" cannot mean "darker" in both appearances: on a dark card a deep brick arc
    /// disappears exactly when it matters most. So dark mode deepens by getting brighter and more
    /// saturated, which is the same ramp read against its own background. Both ends are the sun's own
    /// colours — light mode runs CoralLight → SunDeepen, the pair the body itself darkens along.</para>
    /// </summary>
    public static Color ContextArc(double frac)
    {
        var f = Math.Clamp(frac, 0, 1);
        var (lo, hi) = IsDark
            ? (Color.FromRgb(196, 103, 69), Color.FromRgb(249, 191, 156))
            : (Color.FromRgb(233, 152, 115), Color.FromRgb(139, 40, 29));
        return Color.FromRgb((byte)Math.Round(lo.R + (hi.R - lo.R) * f),
                             (byte)Math.Round(lo.G + (hi.G - lo.G) * f),
                             (byte)Math.Round(lo.B + (hi.B - lo.B) * f));
    }


    /// <summary>Replace the alpha (matching NSColor.withAlphaComponent's semantics: replace, not multiply).</summary>
    public static Color WithAlpha(Color c, double a) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(a * 255), 0, 255), c.R, c.G, c.B);

    /// <summary>Matches NSColor.blended(withFraction:of:): per-component linear interpolation.</summary>
    public static Color Blend(Color c, double fraction, Color other)
    {
        var f = Math.Clamp(fraction, 0, 1);
        static byte Mix(byte a, byte b, double f) => (byte)Math.Clamp(Math.Round(a + (b - a) * f), 0, 255);
        return Color.FromArgb(Mix(c.A, other.A, f), Mix(c.R, other.R, f),
                              Mix(c.G, other.G, f), Mix(c.B, other.B, f));
    }

    /// <summary>Ease in, ease out; used for every animated transition.</summary>
    public static double EaseInOut(double x)
    {
        var t = Math.Clamp(x, 0, 1);
        return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
    }

    /// <summary>Exponential smoothing: lets a value follow changes continuously instead of jumping instantly.</summary>
    public static double SmoothStep(double current, double target, double dt, double rate = 6)
    {
        var k = 1 - Math.Exp(-rate * dt);
        return current + (target - current) * k;
    }

    // MARK: Text
    // Monospaced digits: macOS uses monospacedDigitSystemFont (only the digits are monospaced, the
    // letters are still the system font). Windows has no such concept, so we settle for picking a
    // monospaced font outright; it is written as a comma-separated list of candidates so that
    // Avalonia does its own fallback (Consolas on Windows, Menlo when running the tests on a Mac).
    private static readonly FontFamily MonoFamily = new("Consolas, Menlo, Courier New");

    // The interface font used to be FontFamily.Default, i.e. whatever the platform happened to pick.
    // On macOS that lands on SF Pro and looks fine; on Windows it is unspecified and the small text
    // came out thin and hard to read.
    //
    // Windows 11 ships Segoe UI Variable in three optical sizes, and the choice matters at these
    // sizes: "Small" is drawn specifically for 8–12pt with more open counters and heavier stems,
    // while "Text" is tuned for 12–24pt. Almost all of this interface sits at 9–13pt, so most of it
    // wants Small. Each entry is a comma-separated fallback chain, so Windows 10 (no Variable) drops
    // to plain Segoe UI, and a Mac running the render tests drops to Helvetica Neue.
    //
    // ("-apple-system" was in this chain briefly. It is a CSS keyword, not a font family, so Avalonia
    // cannot resolve it — it silently did nothing.) Note the consequence for RenderCheck: on a Mac
    // these renders now use Helvetica Neue rather than the SF Pro the real macOS app draws with, so
    // they are good for checking layout and truncation but not for comparing letterforms.
    private static readonly FontFamily UiSmall =
        new("Segoe UI Variable Small, Segoe UI, Helvetica Neue, Arial");
    private static readonly FontFamily UiText =
        new("Segoe UI Variable Text, Segoe UI, Helvetica Neue, Arial");

    /// <summary>Optical size follows the point size; see the note on UiSmall.</summary>
    private static FontFamily UiFamily(double size) => size < 12 ? UiSmall : UiText;

    /// <summary>
    /// Matches the Swift version's drawText(_:in:font:color:align:lineBreak:):
    /// the text is drawn starting from the **top-left corner** of the rect (the original view is
    /// isFlipped, and Avalonia's y likewise points downwards, so the semantics agree), and the width
    /// is the wrapping/truncation width.
    /// </summary>
    public static void DrawText(DrawingContext ctx, string text, Rect rect,
                                double size, FontWeight weight, Color color,
                                TextAlignment align = TextAlignment.Left,
                                bool wrap = false, bool monoDigits = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(monoDigits ? MonoFamily : UiFamily(size), FontStyle.Normal, weight),
            size,
            new SolidColorBrush(color))
        {
            MaxTextWidth = Math.Max(1, rect.Width),
            TextAlignment = align,
            // Single-line truncation by default (matching NSLineBreakMode.byTruncatingTail); only the error text wraps
            Trimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        };
        if (!wrap)
        {
            ft.MaxLineCount = 1;          // Avalonia calls it MaxLineCount, not WPF's MaxLines
        }
        else
        {
            // NSString.draw(in:) clips whatever overflows the rect, whereas Avalonia by default
            // constrains only the width and not the height; once wrapped, the error text can grow to
            // 65pt and cover the "double-click to log in" button underneath.
            // The height is constrained only in the wrapping branch: the rect heights for the
            // single-line cases were hand-tuned to macOS line heights (several are only 11–12pt,
            // shorter than the line height of the Windows font), and constraining their height too
            // would swallow the whole line of text outright.
            ft.MaxTextHeight = Math.Max(1, rect.Height);
        }
        ctx.DrawText(ft, new Point(rect.X, rect.Y));
    }
}
