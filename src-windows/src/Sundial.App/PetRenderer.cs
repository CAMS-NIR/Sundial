// Sundial (Windows edition) — drawing the mascot and the gauges
//
// Ported from the macOS PetView.swift; only the **drawing and animation state** came across. Windows,
// mouse events, the tray icon and the accessibility element tree are other modules' business. This
// touches no Avalonia control at all, it just draws into a DrawingContext, so it can hang off a
// custom-drawn control just as easily as it can be rendered off-screen for frame-by-frame comparison.
//
// Coordinate system: Avalonia's Y axis already points downwards, exactly like the original NSView with
// isFlipped = true, so the geometry numbers (eyes at cy-2s, mouth at cy+6.5s, brows high on the inside
// and low on the outside) can be copied across verbatim, with no need to flip y.

using Avalonia;
using Avalonia.Media;
using Sundial.Core;

namespace Sundial.App;

public sealed class PetRenderer
{
    private readonly PetModel _model;

    public PetRenderer(PetModel model) => _model = model;

    // MARK: Shape constants (numbers tuned over and over in the macOS version — don't change them on a whim)

    public const double TopRowH = 64;
    public const double BlockH = 50;          // title + status + context progress bar
    public const double BlockGap = 6;
    public const int MaxBlocks = PetModel.MaxBlocks;
    public const double PetScale = 0.44;
    public const double CardRadius = 26;      // matches the expanded corner radius in the window layer
    public const double CompactSide = 88;     // window side length when collapsed (nothing but the sun left)
    public const int RayCount = 9;            // an odd number, which looks more natural as it turns
    public const double RayMaxPull = 13;      // maximum extension when pointing straight at the mouse and close to it (pt)
    public const double GaugeMaxPull = 9.5;   // maximum extension towards a gauge's side when that gauge is full (pt)
    /// <summary>The cap once the two forces are added together: when collapsed the window is only 88pt
    /// square (radius 44), so a ray that reaches too far simply gets clipped by the window edge.</summary>
    public const double RayPullCap = 18;
    public const double ResetLineH = 15;      // height of the "when the limit lifts" line

    // MARK: Animation state

    private double _t;                        // animation clock
    private double _blinkUntil = -1;
    /// <summary>0 = awake, 1 = dozing. Matches PetView.sleepT in the macOS version —
    /// IsSunAsleep used to be a hard boolean, so the colour/eyes/zzz/ray rotation all snapped over within one frame</summary>
    private double _sleepT = 1;
    /// <summary>0 = normal, 1 = allowance used up (the eyes turn into ✖)</summary>
    private double _deadT;
    /// <summary>The breathing phase accumulates separately: 1.6 awake, 1.0 asleep — changing the frequency of the sin directly would jump the phase</summary>
    private double _breathPhase;
    private double _nextBlinkAt = 2;
    private double _spinPhase;                // loops 0–1, so the ends join up seamlessly
    private double _sunSpin;
    private readonly double[] _ringShown = new double[2];   // the two rings' currently displayed values (outer/inner), eased towards the target
    private readonly List<(string Id, Rect Rect)> _blockRects = new(); // for hit testing
    private Rect _loginButtonRect;

    private Point? _mouse;                    // the gravity source after filtering by the radius of effect (view coordinates)
    private Point _petCenter;                 // the sun's centre in the previous frame
    private bool _hasPetCenter;
    private readonly double[] _rayPull = new double[RayCount];        // how far each ray is extended
    private Point _bodyLean;                  // the whole creature leans a little towards the mouse
    private Point _eyeShift;                  // the pupils look towards the mouse
    private double _perk;                     // 0–1, the "perks up" reaction to being approached
    private readonly List<BlockAnim> _blocks = new();

    // new() is required: struct fields default to all zeroes and don't run the parameterless constructor, so _startedAt would never get its -99
    private Tween _hoverTween = new();
    private Tween _expandTween = new();

    // MARK: Public surface

    /// <summary>The global cursor position (already converted into view coordinates). The window layer pushes it in every frame.
    /// We use the **global** cursor rather than "it only counts once the mouse is inside the window": that way the sun
    /// already reacts while the cursor is still outside and merely getting closer — "gravity" is meant to act at a distance.</summary>
    public Point? MousePoint { get; set; }

    public double HoverProgress => _hoverTween.Value;    // 0–1, how far the details have expanded
    public double ExpandProgress => _expandTween.Value;  // 0 = nothing but the sun, 1 = the full card

    public bool ReduceMotion { get; set; }        // the system's "reduce motion" setting
    public bool ReduceTransparency { get; set; }  // the system's "reduce transparency" setting: we draw an opaque backing ourselves

    /// <summary>The size/opacity changed this frame, so the window needs re-laying out. Corresponds to onHoverProgress in the Swift version.</summary>
    public Action? OnLayoutChanged;

    /// <summary>Hit rectangles for the session blocks (view coordinates). The event layer needs them to mark a session read on click.</summary>
    public IReadOnlyList<(string Id, Rect Rect)> BlockRects => _blockRects;

    /// <summary>Hit rectangle for the login button; default when it hasn't been drawn.</summary>
    public Rect LoginButtonRect => _loginButtonRect;

    /// <summary>When the exhausted limit lifts again. **This line is only drawn once a limit really is maxed out (the sun turns into ✖)** —
    /// while there is headroom left, "how long until it resets" is a pointless thing to say: it costs a line and makes the card taller;
    /// once it is maxed out, it is conversely the one thing you still want to know. If several limits are over at once, take the one that lifts soonest.
    /// null = nothing is maxed out, so this line isn't drawn. Matches PetView.soonestResetText() in the macOS version.</summary>
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
        // A long label such as "Weekly · All models" is cut down to its first segment; 198pt of width can't fit the whole thing
        var idx = label.IndexOf(" · ", StringComparison.Ordinal);
        var shortLabel = idx > 0 ? label[..idx] : label;
        return $"{shortLabel} · resets in {Usage.CompactReset(best)}";
    }

    /// <summary>The height taken by the reset line; 0 when it isn't drawn. The window height has to account for it.</summary>
    public double ResetLineHeight => SoonestResetText() is null ? 0 : ResetLineH;

    /// <summary>Whether any animation is still running. If not, there's no need to redraw every frame, which saves power.
    /// The mascot's breathing/blinking/turning always counts as "animating" — stop it and it becomes a dead picture.</summary>
    public bool NeedsContinuousAnimation => true;

    /// <summary>Only interaction and transitions need the full frame rate; plain breathing and blinking are fine at a low one.</summary>
    public bool NeedsFullFrameRate
    {
        get
        {
            if (_model.AnyBusy) return true;                     // spinner / rays turning
            if (_mouse is not null) return true;                 // ray gravity
            if (Math.Abs(HoverProgress - (_model.Hovered || _model.DetailsPinned ? 1 : 0)) > 0.001) return true;
            if (Math.Abs(ExpandProgress - ExpandTargetValue) > 0.001) return true;
            return false;
        }
    }

    /// <summary>The height the session block area currently takes (varies continuously).
    /// It has to be clamped to 0: when sum is very small, sum*56-6 is negative and the window overshoots inwards before springing back.</summary>
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
        // Use blocks rather than VisibleSessions: the window mustn't collapse while blocks are still fading out,
        // otherwise the two animations pile up and it still looks like a snap
        (_model.Hovered || _model.DetailsPinned || _blocks.Count > 0
         || _model.Loading || (_model.Rows.Count == 0 && _model.ErrorMsg != null)) ? 1 : 0;

    /// <summary>Whether the sun is dozing: the drawing and the gravity must use the same test, otherwise the angles end up a whole sunSpin apart.</summary>
    private bool IsSunAsleep => _model.Asleep || !_model.AnyBusy;

    // MARK: Timed tween
    //
    // Exponential smoothing (SmoothStep) is always fast at the head and slow at the tail: while collapsing,
    // most of the distance is covered in the first 0.1 seconds and the little that's left grinds along slowly —
    // it simply snaps out of existence rather than fading. Switching to a fixed-duration S curve spreads the
    // fast and slow parts evenly, and only then does collapsing look like collapsing.
    private struct Tween
    {
        public double Value;
        private double _from;
        private double _to;
        private double _startedAt = -99;

        public Tween() { }

        /// <summary>Return value: whether anything changed this frame (used to decide whether to tell the window to re-lay out).</summary>
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

    /// <summary>How far a session block has appeared/disappeared. The window height must be computed from this
    /// continuous value, not by simply counting blocks — the count is discrete, so the moment the last block goes
    /// the window drops 50pt within a single frame and swallows every bit of easing.
    /// A block that is fading out has to keep its own data, otherwise there's nothing left to draw it from.</summary>
    private sealed class BlockAnim
    {
        public required SessionActivity S;
        public Tween Tw = new();
    }

    // MARK: Advance one frame

    public void Advance(double dt)
    {
        _t += dt;
        _sleepT = Theme.SmoothStep(_sleepT, IsSunAsleep ? 1 : 0, dt, 3.2);
        _deadT = Theme.SmoothStep(_deadT, _model.MaxPercent >= 100 ? 1 : 0, dt, 3.0);
        _breathPhase += dt * (1.6 - 0.6 * _sleepT);
        // Spinner: a normalised phase, so the ends meet exactly where it wraps
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
            // When it stops, settle onto the nearest detent. The rays have 9-fold symmetry, so any whole
            // multiple of 40° looks the same — this step is invisible, but the idle pose is now uniquely determined
            var step = Math.PI * 2 / RayCount;
            var target = Math.Round(_sunSpin / step) * step;
            _sunSpin = Theme.SmoothStep(_sunSpin, target, dt, 4);
            if (Math.Abs(_sunSpin - target) < 0.0005) _sunSpin = target;
        }
        // The ring values ease towards their targets. **Keyed by position, not by label** — the right-hand ring shows
        // "the tightest of the weekly limits", and which one is tightest changes hands (Fable being overtaken by the
        // all-models limit, say). Keyed by label, the new label has no history when the handover happens and has to
        // grow up from 0, which reads as the usage suddenly being wiped
        // (measured on the macOS version: 216° dropped to 54° in one frame, then took half a second to climb back to 259°).
        var (ringOuterT, ringInnerT) = _model.RingRows;
        var ringRowsT = new[] { ringOuterT, ringInnerT };
        for (int i = 0; i < 2; i++)
        {
            // The ring draws at most one full turn; anything past the limit is left to the number in the middle (106%, say) to say
            var target = ringRowsT[i] is { } rr ? Math.Min(1, rr.Percent / 100.0) : 0;
            var cur = _ringShown[i];
            _ringShown[i] = Math.Abs(cur - target) > 0.0005
                ? Theme.SmoothStep(cur, target, dt, 5)
                : target;
        }
        UpdateMousePoint();

        // Ray gravity: the rays facing the mouse stretch out, the ones facing away pull back, and the closer it is the more obvious it gets
        // Pointer gravity is motion that tracks the hand, so it stays off under Reduce Motion; breathing and turning are unaffected
        var targets = ReduceMotion ? new double[RayCount] : RayPullTargets();
        for (int i = 0; i < RayCount; i++)
            _rayPull[i] = Theme.SmoothStep(_rayPull[i], targets[i], dt, 9);

        // Whole-body lean + gaze following + perking up: they share the same field as the rays and ease together
        var field = ReduceMotion ? null : MouseField();
        // Awake it moves closer (+4.2), asleep it shrinks away (-3.0), interpolated continuously by _sleepT and passing through 0 on the way
        double leanSign = 1;
        double leanMax = 4.2 * (1 - _sleepT) - 3.0 * _sleepT;
        var lean = field is { } lf
            ? new Point(lf.Ux * leanMax * lf.Proximity * leanSign,
                        lf.Uy * leanMax * lf.Proximity * leanSign)
            : default;

        // If there's a mouse it looks at the mouse (it follows earlier and further than the body does, so it's already
        // watching you from a distance); when nobody is paying it any attention, it glances at the gauges on either side now and then
        Point eye;
        if (field is { } ef)
        {
            var k = 1.7 * (1 - _sleepT);      // asleep, the pupils stop following anyone
            eye = new Point(ef.Ux * k * Math.Min(1, ef.Proximity * 2.4),
                            ef.Uy * k * Math.Min(1, ef.Proximity * 2.4));
        }
        else eye = default;   // no mouse means it looks straight ahead; no more glancing about on its own

        _bodyLean = new Point(Theme.SmoothStep(_bodyLean.X, lean.X, dt, 7),
                              Theme.SmoothStep(_bodyLean.Y, lean.Y, dt, 7));
        _eyeShift = new Point(Theme.SmoothStep(_eyeShift.X, eye.X, dt, 12),
                              Theme.SmoothStep(_eyeShift.Y, eye.Y, dt, 12));
        _perk = Theme.SmoothStep(_perk, (field?.Proximity ?? 0) * (1 - _sleepT), dt, 8);

        // Session blocks appearing/disappearing: the ones still around are **reordered to follow visible**, the ones that
        // have gone are put back in their old slot to fade out there. It used to be "walk the old order and always append
        // new blocks", so the ordering ActivityWatcher carefully worked out — "waiting on you first → running → unread" —
        // only took effect the one time blocks was built up from empty; after that, when a session threw up a prompt it
        // was still drawn in its original slot, and with 5 sessions it could even end up in the last one.
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
        // Anything no longer in visible goes back at its old relative position and fades out in place, rather than suddenly jumping somewhere else
        for (int i = 0; i < _blocks.Count; i++)
        {
            var old = _blocks[i];
            if (visible.Any(x => x.Id == old.S.Id)) continue;
            blocksChanged = old.Tw.Step(0, _t, 0.5, ReduceMotion) || blocksChanged;
            if (old.Tw.Value > 0.004) nextBlocks.Insert(Math.Min(i, nextBlocks.Count), old);
        }
        _blocks.Clear();
        _blocks.AddRange(nextBlocks);

        // Hover details + collapse/expand: timed tweens, with the window size and the content opacity following in step.
        // Collapsing is given more time than expanding — appearing can be brisk, but disappearing has to be slower or it
        // looks as if it were wiped out.
        // Under Reduce Motion the size change lands immediately (frame-by-frame resizing is the part that causes discomfort)
        double hoverTarget = (_model.Hovered || _model.DetailsPinned) ? 1 : 0;
        double expandTarget = ExpandTargetValue;
        var changed = _hoverTween.Step(hoverTarget, _t,
                                       hoverTarget > HoverProgress ? 0.30 : 0.42, ReduceMotion);
        changed = _expandTween.Step(expandTarget, _t,
                                    expandTarget > ExpandProgress ? 0.40 : 0.62, ReduceMotion) || changed;
        if (changed || blocksChanged) OnLayoutChanged?.Invoke();
        // Blinking. It was once deleted along with the "glance at the gauges" behaviour, but the two aren't the same thing:
        // glancing moves the pupils left and right periodically (which reads as flickering), whereas a blink is just one
        // contraction in height and doesn't grab the eye
        if (_t >= _nextBlinkAt)
        {
            _blinkUntil = _t + 0.16;
            _nextBlinkAt = _t + 2.4 + Random.Shared.NextDouble() * 3.6;   // 2.4–6.0 seconds
        }

    }

    /// <summary>Clear it once outside the radius of effect, so we don't keep redrawing at the full frame rate.</summary>
    private void UpdateMousePoint()
    {
        var p = MousePoint;
        if (p is null) { _mouse = null; return; }
        if (!_hasPetCenter) { _mouse = p; return; }     // the first frame hasn't been drawn yet, so we don't know where the sun is
        var dx = p.Value.X - _petCenter.X;
        var dy = p.Value.Y - _petCenter.Y;
        _mouse = dx * dx + dy * dy <= 230 * 230 ? p : null;
    }


    /// <summary>The mouse's direction and proximity relative to the sun. The rays, the body lean and the gaze all come
    /// from the same field; otherwise each works it out on its own and they end up out of step whenever the state changes.</summary>
    private (double Ux, double Uy, double Proximity)? MouseField()
    {
        if (_mouse is not { } m || !_hasPetCenter) return null;
        var dx = m.X - _petCenter.X;
        var dy = m.Y - _petCenter.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist <= 0.001) return null;
        // Proximity: strongest right up against the body, essentially gone beyond about 150pt
        var proximity = 1 / (1 + Math.Pow(Math.Max(0, dist - 26) / 62, 2));
        if (proximity <= 0.02) return null;
        return (dx / dist, dy / dist, proximity);
    }

    /// <summary>The direction of ray i; it must match the algorithm in DrawPet exactly, or the direction of the force will be misaligned.</summary>
    /// <summary>_sunSpin already stops accumulating when nothing is busy, so it can simply be included unconditionally here.
    /// It used to be forced to zero while asleep, which meant the whole ring of rays span back to its original position within one frame</summary>
    private double RayAngle(int i) =>
        (double)i / RayCount * 2 * Math.PI + Math.PI / 8 + _sunSpin;

    private static double WrapPi(double a)
    {
        var d = a;
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    /// <summary>The target extension of each ray, two forces added together:
    /// ① the mouse — awake it is drawn towards it, dozing it shrinks away instead;
    /// ② the gauges on either side — the fuller they get, the further the rays on that side are pulled.</summary>
    private double[] RayPullTargets()
    {
        var outv = new double[RayCount];

        if (MouseField() is { } f)
        {
            var mAngle = Math.Atan2(f.Uy, f.Ux);
            double sign = 1 - 2 * _sleepT;                    // awake it moves closer, asleep it shrinks away, with a continuous transition
            double maxPull = RayMaxPull * (1 - _sleepT) + 6 * _sleepT;
            // The side facing away moves the other way. Awake this is just a little garnish; asleep, the shrinking away
            // has to be visible — as the near side pulls back, the far side has to reach out noticeably, so it looks as
            // if the whole body were being pushed away.
            // This coefficient used to be 0.28, and the far side grew by less than two points, which the naked eye simply can't pick up
            double recoilK = 0.28 * (1 - _sleepT) + 1.05 * _sleepT;
            for (int i = 0; i < RayCount; i++)
            {
                var delta = WrapPi(RayAngle(i) - mAngle);
                // The cos is normalised to 0–1 and then raised to a power. The exponent came down from 2.2 to 1.4: once
                // the rays were cut to 9, too sharp a falloff left only one of them able to reach, and you lost the
                // sense of a whole swathe being pulled across
                var alignment = Math.Pow(Math.Max(0, Math.Cos(delta)), 1.4);
                var recoil = -recoilK * Math.Pow(Math.Max(0, -Math.Cos(delta)), 1.8);
                outv[i] += maxPull * f.Proximity * (alignment + recoil) * sign;
            }
        }

        // The pull from the gauges: the left gauge sits due left (π), the right one due right (0).
        // The pull only starts at 50% (the warning line) and is strongest when full — so "which side is tight" grows
        // straight into the shape and you don't have to go and read the numbers. As the rays turn, whichever ones are
        // being pulled keeps changing hands, and the whole ring looks as if it were stretched into an ellipse.
        var (ringOuter, ringInner) = _model.RingRows;
        foreach (var (dirAngle, row) in new (double, UsageRow?)[] { (Math.PI, ringOuter), (0, ringInner) })
        {
            if (row is null) continue;
            double pct = row.Percent;
            // The amplitude needs a floor. It used to be a linear ramp starting at 50%, so a ring at 60% only got 20%
            // of the full force — a swing of 3.5pt, which is as good as not moving. Now the minimum is four tenths of
            // the full force, but it still grows with usage, so "which side is tight" can still be read off the size of the swing
            var k = 0.4 + 0.6 * Math.Min(1, Math.Max(0, (pct - 15) / 75));
            var u = Math.Clamp(pct / 100, 0, 1);
            // "Breathing" is not a swell in strength, it is **a pull and a push**: the positive half of the cycle drags
            // this side's rays outwards and the negative half pulls them back in, and only that swinging back and forth
            // is visible (varying the strength between 0.55 and 1.0 with the direction always outwards is all but
            // impossible to see moving).
            // The rate follows usage directly (not the floored amplitude, or both sides would pant at the same speed):
            // roughly 7 seconds a cycle when idle, about 3 when full.
            // The two sides are half a cycle out of phase, so the whole ring of rays sways from side to side instead of swelling as one
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
    /// Light catching the card edge: an outline that is bright at the top left and faint at the bottom right. Matches
    /// PetView.drawCardEdge() in the macOS version.
    /// Without this outline, in dark mode the card all but smears into the desktop and you can't tell where its boundary is.
    /// It fades in along with the expand progress, and isn't drawn when collapsed.
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
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),   // the bright part is pinned to the top-left corner
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(hi, 0),
                new GradientStop(lo, 0.72),
            },
        };
        // The stroke straddles the path, so pull in by half a line width, otherwise the outer half of it gets clipped by the window edge
        var r = bounds.Deflate(w / 2);
        var rr = Math.Max(0, rad - w / 2);
        ctx.DrawRectangle(null, new Pen(brush, w), r, rr, rr);
    }

    // MARK: Draw one frame

    public void Render(DrawingContext ctx, Rect bounds)
    {
        _blockRects.Clear();
        _loginButtonRect = default;
        // Matches PetView.drawCardEdge() in the macOS version

        // The glass has been hidden, so we add an opaque backing plate here to keep things readable.
        // But it likewise isn't drawn when fully collapsed — idling, all that's left is a sun, and there's no content
        // that needs a plate underneath it. Without that guard, an idle desktop would show a sun sitting on an 88×88
        // dark grey solid disc; and since WindowBackground is a FromRgb (alpha is always 255), we have to multiply in
        // the fade-in factor ourselves, otherwise the backing plate simply snaps into existence during the expand animation
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

        // The card's base is the window layer's translucent material's job; here we only draw the content
        var card = bounds;
        var e = ExpandProgress;
        var cardMidX = card.X + card.Width / 2;
        var cardMidY = card.Y + card.Height / 2;

        var rowMidY = card.Y + 10 + TopRowH / 2;
        // The sun always stays centred, with the two gauges sitting to left and right
        var petY = cardMidY + (rowMidY - cardMidY) * e;
        var sunAt = new Point(cardMidX, petY);
        DrawPet(ctx, sunAt);

        // The gauges have to fade out before the window does: if they're still there once the window has nearly
        // narrowed down to just the sun, they get bluntly clipped by the window edge and it simply looks as if they
        // snap out of existence rather than fading.
        // They also shrink slightly, so it reads as "pulled back in" rather than "cropped off"
        var g = Theme.EaseInOut(Math.Max(0, (e - 0.34) / 0.66));
        // With no usage data at all (not logged in / no subscription) we don't draw those two empty rings;
        // leaving two empty tracks sitting there only makes people think it's broken
        if (g > 0.004 && _model.Rows.Count > 0)
        {
            using (ctx.PushOpacity(g))
                DrawGauges(ctx, card, rowMidY, 0.84 + 0.16 * g);
        }
        if (e <= 0.01) return;   // fully collapsed leaves nothing but the sun

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
            Theme.DrawText(ctx, "Fetching usage…", new Rect(card.X, y + 6, card.Width, 16),
                           11, FontWeight.Normal, Theme.SecondaryLabelColor, TextAlignment.Center);
            return;
        }

        // When usage can't be fetched, the message takes over the whole card **only if there are no sessions to show**.
        // The session-status half reads local log files and has nothing to do with logging in or with subscriptions —
        // someone without Max/Pro (the authorisation page turns them away outright) should still get to see what they have running.
        if (_model.Rows.Count == 0 && _model.ErrorMsg is { } msg && _blocks.Count == 0)
        {
            Theme.DrawText(ctx, msg, new Rect(card.X + 13, y + 4, card.Width - 26, 46),
                           10.5, FontWeight.Normal, Theme.SecondaryLabelColor,
                           TextAlignment.Center, wrap: true);
            if (_model.NeedsLogin)
            {
                // At least 28pt tall, which meets the minimum clickable-area size
                var btn = new Rect(cardMidX - 60, y + 52, 120, 30);
                _loginButtonRect = btn;
                ctx.DrawRectangle(new SolidColorBrush(Theme.CoralDeep), null, btn, 13, 13);
                Theme.DrawText(ctx, "Double-click to sign in", new Rect(btn.X, btn.Y + 6, btn.Width, 16),
                               11, FontWeight.SemiBold, Colors.White, TextAlignment.Center);
            }
            return;
        }

        // Sessions that are running, plus ones that have finished but are unread.
        // The height each block takes grows and shrinks with its own appearance progress, and it is clipped to that
        // height — so it disappears by rolling up, with the blocks below sliding up in step, rather than the whole
        // block vanishing into thin air
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

        // The details fade in and out with HoverProgress and slide up slightly, in step with the window height
        if (HoverProgress > 0.01)
        {
            using (ctx.PushOpacity(HoverProgress))
            using (ctx.PushTransform(Matrix.CreateTranslation(0, (1 - HoverProgress) * 6)))
                DrawDetails(ctx, y + 2, card);
        }
    }

    // MARK: The mascot

    private void DrawPet(DrawingContext ctx, Point center)
    {
        const double s = PetScale;
        double cx0 = center.X, cy0 = center.Y;
        var stress = _model.MaxPercent / 100.0;
        // With no session running it dozes off: drab and grey, eyes shut, zzz drifting up
        var sT = _sleepT;                  // 0 = awake, 1 = dozing; everything below is interpolated by it
        var breathe = 1 + 0.022 * Math.Sin(_breathPhase);

        var light = Sundial.App.Theme.Blend(Theme.CoralLight, sT, Theme.SleepLight);
        var deep = Sundial.App.Theme.Blend(Theme.CoralDeep, sT, Theme.SleepDeep);
        // The body darkens continuously with usage. It used to change abruptly only past 75%, which is really just two steps;
        // now it deepens all the way along, so a glance at the colour tells you roughly how much has gone, without reading numbers.
        // Raised to the power 1.5: at low usage the colour barely shifts, and only high up does it darken noticeably
        // The usage signal is kept while asleep too. **The moment when only a sun is left is precisely the moment when
        // there is nothing else to look at** — the colour used to be switched off entirely here, which meant nothing
        // could be read off it in the very situation that needed it most
        // (measured on the macOS version: 10% and 99% rendered exactly the same)
        var tint = Math.Pow(Math.Clamp(stress, 0, 1), 1.2) * (0.62 + 0.13 * sT);
        // The target colour is a fixed deep brick red; GaugeAlert can't be used — that one switches with light/dark and
        // is actually brighter in dark mode, so the more strained it got the paler the body became, exactly backwards.
        // The top half is darkened by four tenths and the bottom by the full amount: the body is pale on top and dark
        // below to begin with, and the face sits fairly high up; darkening the whole body at once puts dark brown
        // features on a deep red background and the contrast drops to 2.5:1 (the minimum for graphics is 3:1)
        var deepenTo = Sundial.App.Theme.Blend(Theme.SunDeepen, sT, Theme.SleepDeepen);
        var bodyLight = Theme.Blend(light, tint * 0.4, deepenTo);
        var bodyDeep = Theme.Blend(deep, tint, deepenTo);

        _petCenter = center;   // used by the next frame's gravity calculation (it must be the un-leaned centre, otherwise it self-oscillates)
        _hasPetCenter = true;
        // The whole creature shifts a little towards the mouse. This comes after _petCenter is assigned, so the offset only affects the picture, not the field calculation
        double cx = cx0 + _bodyLean.X, cy = cy0 + _bodyLean.Y;

        // Rays pointing towards a side take on that gauge's colour, with the depth following its usage.
        // So "the sun is being dragged over to the left, and that left half of it is red" = the left-hand limit is
        // nearly full; looking at the sun is enough, and you needn't read the numbers inside the two rings.
        // The tips of the rays on each side blend into that side's fixed accent colour — the sun reaches out and touches
        // the gauge, and the colour joins up there. The colour no longer follows usage (see the notes in Theme.cs)
        var (tintOuter, tintInner) = _model.RingRows;
        var tintSides = new List<(double Angle, Color Color, double Amount)>(2);
        foreach (var (sideAngle, row) in new (double, UsageRow?)[] { (Math.PI, tintOuter), (0, tintInner) })
        {
            if (row is null) continue;
            // sideAngle == PI is the left-hand side
            var glow = sideAngle > 1 ? Sundial.App.Theme.GlowLeft : Sundial.App.Theme.GlowRight;
            // While asleep, pull the glow colour a little towards the sleep grey — you can still tell gold from pink, but it isn't glaring
            var col = Sundial.App.Theme.Blend(glow, 0.25 * sT, Theme.SleepDeep);
            // **The glow strength follows this side's usage**: the fuller, the brighter.
            // This is the only channel left for reading usage in the idle state — with just a sun there are no rings and
            // no numbers, and that slight darkening of the grey body simply can't be seen within 88pt square.
            // "Fuller = brighter" is also more intuitive than "fuller = darker", and it doesn't repeat the dark-bruise mistake.
            var u = Math.Clamp(row.Percent / 100.0, 0, 1);
            tintSides.Add((sideAngle, col, Math.Pow(u, 0.75)));
        }

        // The rays: short round-ended bars; while thinking, the whole ring turns slowly, and as the mouse comes near they get pulled to different lengths
        for (int i = 0; i < RayCount; i++)
        {
            var angle = (double)i / RayCount * 2 * Math.PI + Math.PI / 8 + _sunSpin;
            var wobble = (1 - sT) * 2.2 * s * Math.Sin(_t * 1.9 + i * 1.3);
            const double inner = 21 * s;
            // The reverse repulsion mustn't shrink a ray away to nothing, so keep a minimum length
            var outer = Math.Max(inner + 4 * s, (49 * s + wobble) * breathe + _rayPull[i]);
            // The stretched rays also thicken slightly; reaching out looks stronger than merely getting longer
            var w = 16.5 * s * (1 + 0.2 * Math.Max(0, _rayPull[i]) / RayMaxPull);
            // The tint is applied only at the **far end**, with the root keeping its own colour: the colour is rubbed
            // off from the gauge over there, and tinting the whole ray evenly hides that relationship. The further it
            // stretches, the denser the tip — so when breathing pushes a ray towards its gauge the tip lights up, and
            // it fades again as the ray comes back.
            // The two sides are layered on **in sequence** (left first, then right); swap the order and the colour of the middle rays changes
            // The tint is applied only at the **far end**, with the root keeping its own colour: the colour is rubbed
            // off from the gauge over there, and tinting the whole ray evenly hides that relationship. The further it stretches, the denser the tip.
            // The two sides are layered on **in sequence** (left first, then right); swap the order and the colour of the middle rays changes
            var tipColor = bodyDeep;
            foreach (var side in tintSides)
            {
                var a = Math.Pow(Math.Max(0, Math.Cos(WrapPi(angle - side.Angle))), 0.5) * side.Amount;
                if (a <= 0.01) continue;
                var reach = Math.Clamp(_rayPull[i] / GaugeMaxPull, 0, 1);
                tipColor = Sundial.App.Theme.Blend(tipColor, Math.Min(0.95, a * (0.72 + 0.28 * reach)), side.Color);
            }
            // A ray is built as a horizontal rectangle and then rotated, so in the **unrotated** local coordinates the
            // gradient runs along +x from the root out to the tip; the inner three tenths keep the base colour before
            // the transition starts, so the colour gathers at the tip instead of graduating along the whole ray (the
            // root is hidden behind the body anyway)
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
            // Rotate first, then translate: Avalonia uses the row-vector convention, so A * B means A first, then B
            using (ctx.PushTransform(Matrix.CreateRotation(angle) * Matrix.CreateTranslation(cx, cy)))
                ctx.DrawRectangle(rayBrush, null,
                                  new Rect(inner, -w / 2, outer - inner, w), w / 2, w / 2);
        }

        // The body: a gradient that gives the fluffy dumpling some volume.
        // The original comment says "pale on top, dark below", but that was the **intent**, not the result. The angle of
        // NSGradient.draw(in:angle:) is measured anticlockwise in the current user coordinate system, and PetView is
        // isFlipped, so the direction flips along with it.
        // Measured off-screen on this machine (drawing red=starting → blue=ending with angle:-90 in a flipped view):
        // the top came out bluish and the bottom reddish, i.e. **starting (pale) lands at the bottom and ending (dark) at the top**.
        // This follows what macOS actually renders rather than what that comment says — the two platforms have to look the same.
        // If it is one day settled that the macOS version should be changed to match the comment, just swap the StartPoint / EndPoint below back.
        var r = 30 * s * breathe;
        var grad = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),   // the pale colour (bodyLight) at the bottom
            EndPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),     // the dark colour (bodyDeep) at the top
            GradientStops = new GradientStops
            {
                new GradientStop(bodyLight, 0),
                new GradientStop(bodyDeep, 1),
            },
        };
        ctx.DrawEllipse(grad, null, new Point(cx, cy), r, r);

        // Mood: 0 = relaxed, 1 = nearly out of allowance. The brows and the mouth shape follow it;
        // at this size the little bit of curve at the corners of the mouth simply can't be seen on its own
        var worry = Math.Clamp((stress - 0.5) / 0.35, 0, 1);

        // Bean eyes
        var eyeBaseY = cy - 2 * s;
        var blinkT = _blinkUntil - _t;
        var lidClose = blinkT > 0 ? Theme.EaseInOut(1 - Math.Abs(blinkT / 0.16 - 0.5) * 2) : 0;
        // Blinking and falling asleep are combined into a single "closedness": over the 0.6 seconds of falling asleep
        // the eyes shut slowly rather than suddenly turning into an arc, so the flattening of the ellipse and the
        // fade-in of the arc overlap for a stretch.
        // Once the allowance is used up, the whole lot gives way to ✖
        var lid2 = Math.Max(lidClose, sT);
        var arcAlpha = Theme.EaseInOut(Math.Clamp((lid2 - 0.62) / 0.38, 0, 1));
        var dT = _deadT;
        foreach (var dx in new[] { -12.0 * s, 12.0 * s })
        {
            // The pupils look towards the mouse; no offset while the eyes are shut, or the arc would end up skewed
            var ex = cx + dx + _eyeShift.X * (1 - sT);
            var ey = eyeBaseY + _eyeShift.Y * (1 - sT);
            var h = 6 * s * (1 - lid2);
            if (h > 0.2 && arcAlpha < 1 && dT < 1)
            {
                // Plain bean eyes, no catchlight: at this size that speck of white is only 0.6pt, which isn't a
                // highlight but a speck of noise, and it dirties an otherwise clean silhouette
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

        // Brows: they only grow in once it starts getting strained, high on the inside and low on the outside ("/ \") = a worried look.
        // Of the three moods, this is the difference you recognise most immediately
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

        // Mouth: happy is a wide grinning arc, strained is pressed flat, and used up is a pronounced upside-down arc
        var my = cy + 6.5 * s;
        var mouthPen = RoundPen(Theme.WithAlpha(Theme.FaceDark, 1 - sT), 1.7 * s);
        if (sT > 0.01)
        {
            // The stroked ellipse from the original's NSRect(x: cx-2s, y: my-0.5s, w: 4s, h: 5s)
            ctx.DrawEllipse(null, RoundPen(Theme.WithAlpha(Theme.FaceDark, sT), 1.4 * s),
                            new Point(cx, my + 2 * s), 2 * s, 2.5 * s);
        }
        if (stress < 0.5)
        {
            // Opened wider, curved deeper, and with two upturned corners; it grins more broadly as the mouse comes near
            // The control points moved from ±2.6 to ±4.8: too close together and they drag the curve into a
            // sharp-bottomed V, and only moving them outwards gives a rounded U
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
                         new Point(cx + 4.2 * s, my + 1.6 * s));   // pressed into a single line
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

    /// <summary>The small label under the inner ring: the all-models limit shows "weekly", a model-specific limit shows the model's name.</summary>
    private static string WeeklyShortName(UsageRow? row)
    {
        var l = row?.Label;
        if (l is null) return "Weekly";
        if (l.Contains("all models")) return "Weekly";
        return l.Replace("Weekly · ", "");
    }

    // MARK: The two side-by-side gauges (proportion used)


    private void DrawGauges(DrawingContext ctx, Rect card, double midY, double scale)
    {
        var r = 21 * scale;
        var lw = 5 * scale;
        var (ringOuter, ringInner) = _model.RingRows;
        // left gauge — sun — right gauge, centred and evenly spaced in thirds
        var gauges = new (UsageRow? Row, string Name, double Cx)[]
        {
            (ringOuter, "5 hours", card.X + card.Width * 0.17),
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
                // Filled clockwise starting from straight up (-90°). That direction of rotation is a **deliberate** choice:
                // Avalonia's y axis points down, so as the angle increases from -90° the point moves right first and
                // then down, which is clockwise on screen; that agrees with SweepDirection.Clockwise.
                // (The macOS version tripped over this in an isFlipped view: over there clockwise:true actually drew
                //  anticlockwise, and clockwise was in the end likewise obtained by increasing the angle. Same
                //  conclusion, different reason — don't just copy that side's parameters across.)
                DrawArc(ctx, center, r, lw, -90, -90 + 360 * shown,
                        k == 0 ? Sundial.App.Theme.RingLeft : Sundial.App.Theme.RingRight,
                        round: true);
            }
            // The line height for 11pt is about 13pt; the number box used to start at midY-10 and the label box at
            // midY+3, so they met exactly end to end and the two lines of text were stuck together. Everything was
            // moved up and a 2.6pt gap left between them
            Theme.DrawText(ctx, $"{row.Percent}%", new Rect(cx - 22, midY - 13, 44, 14),
                           11, FontWeight.SemiBold, Theme.LabelColor,
                           TextAlignment.Center, monoDigits: true);
            Theme.DrawText(ctx, name, new Rect(cx - 22, midY + 2.6, 44, 11),
                           9, FontWeight.Normal, Theme.SecondaryLabelColor, TextAlignment.Center);
        }
    }

    /// <summary>The single arc-drawing routine. Angles are in degrees, 0° is due right, and **increasing = clockwise on screen**.</summary>

    private static void DrawArc(DrawingContext ctx, Point center, double radius, double lineWidth,
                                double fromDeg, double toDeg, Color color, bool round = false)
    {
        var pen = new Pen(new SolidColorBrush(color), lineWidth, null,
                          round ? PenLineCap.Round : PenLineCap.Flat, PenLineJoin.Round);
        var sweep = toDeg - fromDeg;
        // A full circle uses DrawEllipse: when ArcTo's start and end points coincide it is a degenerate case, and the backends don't behave the same
        if (Math.Abs(sweep) >= 359.99)
        {
            ctx.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(OnCircle(center, radius, fromDeg), false);
            // Split into segments of at most 90° and isLargeArc is never needed, which saves one switch that is easy to get backwards
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

    // MARK: Session blocks

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
            sub = el.Length == 0 ? "Waiting for you" : $"Waiting for you · {el}";
            subColor = Theme.LabelColor;        // grabbing attention is the breathing dot on the right's job; the text just has to stay readable
        }
        else if (s.Background)
        {
            var el = Format.Elapsed(s.Since);
            sub = el.Length == 0 ? "Background task running" : $"Background task · {el}";
        }
        else if (s.Busy)
        {
            var el = Format.Elapsed(s.Since);
            sub = el.Length == 0 ? "Thinking" : $"Thinking · {el}";
        }
        else if (s.Stalled)
        {
            // It's just that there has been no new log entry for a long while; we don't know whether it finished, so don't falsely claim it is done
            var el = Format.Elapsed(s.FinishedAt);
            sub = el.Length == 0 ? "Not responding" : $"Not responding · no update for {el}";
        }
        else
        {
            sub = "Unread · " + Format.Ago(s.FinishedAt);
        }
        Theme.DrawText(ctx, sub, new Rect(box.X + 10, box.Y + 18, box.Width - 40, 13),
                       9, s.Waiting ? FontWeight.SemiBold : FontWeight.Normal, subColor);

        // Context usage: one line of text plus a thin progress bar
        if (s.CtxLimit > 0 && s.CtxTokens > 0)
        {
            var frac = Math.Min(1, (double)s.CtxTokens / s.CtxLimit);
            // Swift's .rounded() rounds half away from zero; C#'s Math.Round defaults to banker's rounding
            // (half to even), so an exact half such as 49.5% differs by 1 between the two — it has to be specified explicitly
            var pct = (int)Math.Round(frac * 100, MidpointRounding.AwayFromZero);
            var barY = box.Y + BlockH - 8;
            var barX = box.X + 10;
            var barW = box.Width - 20;

            Theme.DrawText(ctx, $"Context {Format.Tokens(s.CtxTokens)} / {Format.Tokens(s.CtxLimit)}",
                           new Rect(barX, barY - 12, barW - 30, 11),
                           9.5, FontWeight.Normal, Theme.LabelColor);
            Theme.DrawText(ctx, $"{pct}%", new Rect(barX + barW - 30, barY - 12, 30, 11),
                           9.5, FontWeight.Medium, Theme.LabelColor,
                           TextAlignment.Right, monoDigits: true);

            ctx.DrawRectangle(new SolidColorBrush(Theme.WithAlpha(Theme.LabelColor, 0.14)), null,
                              new Rect(barX, barY, barW, 3), 1.5, 1.5);
            if (frac > 0.004)
            {
                // The context progress bar has been folded into the coral family; it no longer has its own green/amber/red set.
                // Past 60% it is pushed towards deep brick red, so there is still a "nearly full" cue,
                // but it uses the same colour the sun's body darkens towards, rather than introducing a new hue
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
            // Waiting for input: a breathing solid dot, which reads more like "waiting for you" than a spinner does
            var pulse = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(_t * 3.4));
            // Waiting for input is coral as well: what separates it from "running" is the shape (a solid breathing dot
            // vs a spinner), so there's no need for yet another hue
            ctx.DrawEllipse(new SolidColorBrush(Theme.WithAlpha(Theme.CoralDeep, pulse)), null,
                            new Point(cx, cy), 5, 5);
        }
        else if (s.Busy)
        {
            DrawSpinner(ctx, new Point(cx, cy), 7);
        }
        else
        {
            // The unread dot, breathing slowly; one click and it's gone
            var pulse = 0.55 + 0.45 * Theme.EaseInOut((Math.Sin(_t * 1.6) + 1) / 2);
            ctx.DrawEllipse(new SolidColorBrush(Theme.WithAlpha(Theme.CoralLight, pulse)), null,
                            new Point(cx, cy), 4, 4);
        }
    }

    /// <summary>A spinner that joins up seamlessly: the arc length cycles between growing and shrinking, the phase is normalised, and it is perfectly continuous where it wraps.</summary>
    private void DrawSpinner(DrawingContext ctx, Point center, double radius)
    {
        DrawArc(ctx, center, radius, 2.2, 0, 360, Theme.WithAlpha(Theme.LabelColor, 0.14));

        // The tail angle covers exactly 360° per cycle and the arc length oscillates between 26° and 290° along a
        // cosine (derivative 0 at both ends), so where the phase wraps both the angle and the arc length are perfectly
        // continuous and join up.
        var p = _spinPhase;
        var sweep = 26 + 264 * (1 - Math.Cos(2 * Math.PI * p)) / 2;
        var tail = -90 + p * 360;        // increasing angle = clockwise on screen
        DrawArc(ctx, center, radius, 2.2, tail, tail + sweep, Theme.CoralLight, round: true);
    }

    // MARK: Hover details

    private void DrawDetails(DrawingContext ctx, double startY, Rect card)
    {
        var innerX = card.X + 13;
        var innerW = card.Width - 26;
        var y = startY;

        Theme.DrawText(ctx, "Claude usage", new Rect(innerX, y, innerW * 0.6, 13),
                       9.5, FontWeight.SemiBold, Theme.LabelColor);
        if (_model.Tier.Length > 0)
        {
            Theme.DrawText(ctx, _model.Tier, new Rect(innerX + innerW * 0.4, y, innerW * 0.6, 13),
                           9.5, FontWeight.Normal, Theme.SecondaryLabelColor, TextAlignment.Right);
        }
        y += 19;

        // The dot's colour marks **which gauge this row corresponds to**, not how high the usage is — the same rule as the rings.
        // This spot was still switching colour across three bands at 50/80 while the rings had already moved to fixed
        // colours, so the two rules were at odds: at the same 60% the ring was apricot pink while the list was amber,
        // which looked like two different systems.
        // Rows that didn't make it onto a gauge get a neutral grey, so you can see at a glance that this one isn't drawn as a ring.
        var (shownOuter, shownInner) = _model.RingRows;
        if (_model.Rows.Count == 0)
        {
            Theme.DrawText(ctx, _model.NeedsLogin ? "Not signed in — session activity only" : "Usage unavailable",
                           new Rect(innerX, y, innerW, 14),
                           9.5, FontWeight.Normal, Theme.SecondaryLabelColor);
            y += 15;
        }
        foreach (var row in _model.Rows)
        {
            var c = row.Label == shownOuter?.Label ? Sundial.App.Theme.RingLeft
                  : row.Label == shownInner?.Label ? Sundial.App.Theme.RingRight
                  : Sundial.App.Theme.TertiaryLabelColor;
            // The original set a 6×6 circle as the clip region here and then filled the same circle, which is equivalent to just drawing a solid dot
            ctx.DrawEllipse(new SolidColorBrush(c), null, new Point(innerX + 3, y + 7), 3, 3);
            Theme.DrawText(ctx, row.Label, new Rect(innerX + 11, y, innerW - 11 - 81, 14),
                           9.5, FontWeight.Normal, Theme.SecondaryLabelColor);
            // The numbers no longer change colour with usage: colour no longer carries the "how full" information
            Theme.DrawText(ctx, $"{row.Percent}%", new Rect(innerX + innerW - 81, y, 34, 14),
                           9.5, FontWeight.Medium, Theme.LabelColor, TextAlignment.Right, monoDigits: true);
            Theme.DrawText(ctx, Usage.CompactReset(row.ResetAt), new Rect(innerX + innerW - 47, y, 47, 14),
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
            footer = mins <= 0 ? "updated just now" : $"updated {mins} min ago";
        }
        else
        {
            footer = "";
        }
        Theme.DrawText(ctx, footer, new Rect(innerX, y + 3, innerW, 12), 9.5, FontWeight.Normal,
                       _model.ErrorMsg is null ? Theme.TertiaryLabelColor : Theme.SecondaryLabelColor);
    }
    // The compact reset time ("4h32m" / "Thu 14:00") used to have a private implementation of its own here;
    // Sundial.Core's Usage.CompactReset now provides it, so this calls that directly —
    // keep two implementations of the same format around and sooner or later they each get changed on their own.
}
