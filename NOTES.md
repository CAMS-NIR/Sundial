# Sundial — maintenance notes

A macOS desktop pet: a small sun showing Claude Code usage allowances (the same
source as `/usage`) and the Claude Code sessions currently running. Native Swift
+ AppKit, every pixel drawn by hand, no third-party dependencies.

These are **maintenance notes**. User-facing install instructions live in
`dist/INSTALL.txt`; the Windows build is under `src-windows/` and has its own.

---

## 1. What the interface looks like now

### Two forms

| Form | Size | When |
|---|---|---|
| **Folded** | 88×88 pt, **no glass backing**, just a sun floating on the desktop | no session running, nothing unread, pointer elsewhere |
| **Expanded** | 198 pt wide, height follows content, 26 pt rounded Liquid Glass card | hover / pinned open from the menu / a session block exists / loading / error awaiting sign-in |

Between the two is a fixed-duration S-curve (0.40 s out, 0.62 s back), with the
window size interpolating on `PetView.expandProgress` every frame. Folding is
deliberately slower than unfolding: appearing may be brisk, but disappearing
needs to be slow enough that it does not look like the thing was erased.

### Expanded layout (top row 64 pt)

```
   ┌──────────────────────────────┐
   │  ◯ left dial   ☀ sun   ◯ right dial │  ← sun centred, dials on either side
   │                              │      (x = card width ×0.17 and ×0.83)
   │  ┌────────────────────────┐  │
   │  │ session title     ◔ dial│  │   ← session block, 44 pt tall, 6 pt apart, max 4
   │  │ Thinking · 3m 12s       │  │      inner arc = context, outer track = state
   │  │ Context 468.2k / 1.0M   │  │
   │  └────────────────────────┘  │
   │                              │
   │  Claude usage          Max   │   ← hover detail: one line per allowance
   │  ● 5 hours        62%   3h1m │
   │  ● Weekly · all   41%   Thu  │
   │  updated just now            │
   └──────────────────────────────┘
```

**It is no longer "two rings on the right"** — that was several iterations ago.

### The two dials

- **Left = the five-hour allowance**, label fixed as "5 hours".
- **Right = whichever weekly allowance is tightest.** The label is either
  "weekly" (all models) or a model name (`weeklyShortName` strips
  "Weekly · Fable" down to "Fable"). **This one changes identity** — Fable can be
  overtaken by "all models".
- Ring radius 21 pt, line width 5 pt, filling clockwise from the top, at most
  one full turn. An overrun (106%) is expressed by the figure in the middle.
- The colours are **fixed**: left = honey `ringLeft`, right = apricot
  `ringRight`. They do not change with usage.

### What the sun itself tells you

The sun is not merely a mascot: when folded it is **the only readout there is**
— no rings, no figures.

| Channel | Meaning |
|---|---|
| Body colour deepening continuously | The highest percentage across all allowances (`maxPercent`) |
| Eyebrows + mouth shape | <50% broad grin; 50–80% flat line; ≥80% inverted arc plus a frown |
| Rays reaching to one side and swaying | That side's allowance is under pressure (amplitude from 15% up; fuller means wider and faster) |
| **Ray-tip glow colour + brightness** | Colour = that side's fixed glow colour (gold left, pink right); **brightness = that side's usage** — fuller is brighter |
| Grey, eyes closed, drifting z's | No Claude Code session running, **or** usage data unavailable |
| Whole corona turning slowly | A session is thinking |
| Rays drawn out unevenly by the cursor | Decorative only (reversed while asleep — it shies away) |

While asleep the usage signal is **still preserved** (darkening plus glow
colour), only pulled a little towards the sleeping grey.

### Session blocks

One block per session that is running, or has finished but not been looked at.
The status lines read:

- `Waiting for you · 12s` — an AskUserQuestion was raised; the dial's outer halo
  breathes, and the glass takes on a warm tint
- `Background task · 1m 3s` — the main turn is over but the subagents directory
  is still being written
- `Thinking · 3m 12s` — a comet travelling the dial's outer track
- `Not responding · no update for 6 min` — timed out; **it does not claim the
  work finished**
- `Unread · just finished` — a breathing dot **in front of the word**; one click
  clears it

Blocks open and close continuously on their own progress value and are clipped
as they go, so they appear to roll away while the blocks below slide up in step.

---

## 2. Interaction

| Action | Effect |
|---|---|
| Hold and drag | Move it (what is stored is the **top-left** corner) |
| Double-click | Not signed in = start sign-in; signed in = refresh now |
| Click an unread block | Mark it read (blocks that are running are not intercepted, so dragging still works) |
| Click the "double-click to sign in" button | Same as double-click |
| Right-click | Menu |
| Hover | Expand the detail; hovering for 1.2 s then leaving also clears anything unread |
| Menu-bar sun | The same menu, plus "bring the pet back to the centre of the screen" |

Right-click menu: sign in / sign in again · sign out ｜ refresh now · show usage
breakdown · open the web usage page ｜ clearer glass · keep above other windows ·
launch at login ｜ quit.

---

## 3. Source layout

Seven Swift files under `src/`, about 3,200 lines in total.

| File | Lines | Contents |
|---|---|---|
| `main.swift` | 10 | Entry point (Swift allows top-level statements in this file only, and it must be compiled last) |
| `Model.swift` | 48 | `UsageRow` / `PetModel`; `ringRows` decides which allowance each ring shows |
| `Theme.swift` | 100 | Palette (including colours that follow light/dark), `easeInOut`, `smoothStep`, `drawText` |
| `Auth.swift` | 346 | OAuth (PKCE, code pasted by hand), token storage, fallback to Claude Code CLI credentials |
| `Usage.swift` | 445 | `/api/oauth/usage` parsing plus fetch scheduling (refresh / back-off / sign-out policy) |
| `Activity.swift` | 560 | Session watcher: reads Claude Code transcripts for busy/idle, turn timing, context consumption |
| `PetView.swift` | 1023 | All drawing and animation state (sun, dials, session blocks, hover detail, accessibility element tree) |
| `App.swift` | 723 | Window, Liquid Glass, menus, sign-in flow, power and accessibility switches |

`src/icon/` is a standalone icon generator (`make-icons.sh` + `main.swift`). Its
geometry is copied from the sun part of `drawPet`, so changing the shape of the
sun means re-running it by hand.

### Key constants (read section 5 before changing any of these)

```swift
PetView.compactSide  = 88     // folded window side length
AppDelegate.winW     = 198    // expanded window width
PetView.topRowH      = 64     // top row (sun + both dials)
PetView.blockH       = 44     // session block height; blockGap = 6; maxBlocks = 4
PetView.cardRadius   = 26     // matches AppDelegate.expandedRadius
PetView.rayCount     = 9      // number of rays, odd
PetView.rayMaxPull   = 13     // maximum extension from cursor gravity
PetView.gaugeMaxPull = 9.5    // maximum extension from dial pull
PetView.rayPullCap   = 18     // cap once both forces are summed
```

### Cadence

- Usage: a timer ticks every 15 s, but a request only actually goes out every
  60 s. Failures back off by error type (timeout 90 s / network 90 s /
  rate-limited 300 s / 403 ten minutes / awaiting sign-in 3600 s).
- Sessions: the disk is polled every 0.8 s while the window is visible, every
  5 s when it is not.
- Animation: 60 fps while busy or interacting, 24 fps for plain breathing and
  blinking; everything stops when the display sleeps, disk polling included.

---

## 4. Building and releasing

```bash
src/build.sh            # debug: host architecture only + ad-hoc signature → Sundial.app beside the repo
src/build.sh share      # distribution without a developer account: universal binary + ad-hoc → dist/Sundial.zip
src/build.sh release    # distribution with one: universal + Developer ID + notarisation + stapled ticket
```

The script collects every `.swift` automatically (`main.swift` last). Minimum
macOS 13.0.

**The current `dist/Sundial.zip` is a `share` build**: universal, ad-hoc signed,
**not notarised**, which is why `dist/INSTALL.txt` explains how to clear the
quarantine flag. If that ever changes to `release`, remember to delete that
passage.

`release` needs a one-off setup: Xcode › Settings › Accounts › Manage
Certificates › **+** › Developer ID Application; then store notarisation
credentials with
`xcrun notarytool store-credentials solaris-notary --apple-id … --team-id …`
(using an app-specific password generated at appleid.apple.com).

---

## 5. Design decisions, and the traps hit along the way

This section is the most valuable part of the file. Read it before changing the
interface.

### Rings and rays

**Ring easing is kept per position, not per label.**
The right ring shows whichever weekly allowance is tightest, and **which one
that is changes over time**. Keeping state by label means a newly promoted label
has no history and has to grow from zero, which reads as usage suddenly
collapsing — measured: 216° dropping to 54° in a single frame, then half a
second climbing back to 259°. Keeping it by position is simply one ring moving
from an old value to a new one.

**A ray's gradient must get brighter towards the tip.**
An earlier version graduated towards deep wine and plum. Dark laid over the warm
body reads as a bruise — "the sun looks ill". A sun is a light source. So each
side keeps two colours: the ring uses the more saturated `ring*` (which has to
clear 3:1 against glass), and the ray tip uses the brighter `glow*` (drawn on
the sun itself, so unconstrained by background contrast). Picking colours from a
Pantone spectrum was also tried, and withdrawn.

**The tinting sits only at the far end** (gradient stops `[0, 0.32, 1]`, the
inner third keeping the body colour): the colour reads as having been picked up
*from* the dial, and tinting the whole ray evenly loses that relationship — the
root is hidden behind the body anyway. The gradient angle is **the ray's own
bearing**: measured, `-angle` paints the colour onto the root at 90° and 270°.

**Angular falloff was relaxed to `pow(cos, 0.8)`**: a whole half of the corona
needs to take the colour. With only the one or two rays pointing straight at it
changing, the effect is too thin to notice at a glance.

**Glow intensity follows usage (`pow(u, 0.75)`); colour does not.**
Colour is the state, movement is the expression of the state; mixing the two
makes it flicker distractingly. Intensity is **the only channel left that can
convey usage while idle** — folded down there are no rings and no figures, and
the darkening of a grey body is invisible at 88 pt square (measured: 10% and 99%
look practically identical). "Brighter as it fills" is also more intuitive than
"darker", and does not repeat the bruise problem.

**Rings no longer switch colour across three usage bands.**
That information is already carried by the figure in the middle, the arc length,
the sun's expression and its body depth. Once fixed, one colour per side became
an identity marker — you know which is which without reading the label. The dots
in the hover detail were later brought into line: **a dot's colour says which
dial that row corresponds to** (gold = left ring, pink = right ring, grey = not
on a ring), not how high the usage is; the percentage uses ordinary text colour.
Previously the two rules contradicted each other — the same 60% was drawn
apricot on the ring but amber in the list, as if two systems were at work.

`barColor` and the whole sage-green / amber / brick three-band palette have
**been deleted**. It ended up used in only two places — the context bar and the
waiting dot — which meant maintaining an entire hue family for two small
elements. Both now live in the coral family: the context bar uses coral, pushed
towards `sunDeepen` past 60%; the waiting dot uses `coralDeep`, distinguished
from "running" by shape (solid breathing dot vs spinner) rather than hue.

**Dial pull needs a floor on its amplitude**: `k = 0.4 + 0.6 × clamp((pct−15)/75)`.
The original was a linear ramp starting at 50%, which gave a 60% ring only 20%
of full strength — a 3.5 pt sway, indistinguishable from stillness.

**The "breath" both attracts and repels; it is not a swell in intensity**:
`breath = 0.08 + 0.92·sin(...)`, ranging from −0.84 to 1.00 and therefore
**crossing zero** — the positive half-cycle pulls that side's rays out, the
negative draws them back. Varying only between 0.55 and 1.0, always outwards, is
almost impossible to see as motion. The two sides are half a period apart, so
the corona sways from side to side rather than pulsing as a whole. Speed follows
usage directly (not the floored amplitude, or both sides would breathe at the
same rate): roughly 7 s per cycle when idle, roughly 3 s when full.

**Once the rays were reduced to nine**, the alignment exponent had to come down
from 2.2 to 1.4 — too sharp a falloff leaves only one ray within reach, and the
sense of "a whole side being drawn across" is lost. The sleeping recoil constant
`recoilK` went from 0.28 to 1.05 for the same reason: the far side was extending
by less than two points, which the eye simply does not register.

**`rayPullCap = 18`**: folded, the window is only 88 pt square (radius 44), so
anything reaching further is cut off flat by the window edge.

### Body and expression

**The target colour for body darkening must not be `gaugeAlert`** — that one
follows light/dark, and in dark mode it is *brighter*, so the body would grow
paler as things got tighter, exactly backwards. Use the fixed deep brick
`sunDeepen`.

**Nor can the sleeping darkening target be `sunDeepen`** — red mixed into a grey
body looks sickly. A separate warm dark grey, `sleepDeepen`, is used instead.

**The upper body darkens by only four tenths, the lower fully.**
The body is a light-to-dark gradient with the face sitting towards the top;
darkening it uniformly puts dark brown features on a dark red field and drops
contrast to 2.5:1 (the floor for graphics is 3:1), at which point the expression
turns to mush.

**No highlight dot in the eyes**: at this size that speck of white is 0.6 pt —
not a highlight, just a stray pixel.

**"Glancing at the dials" was removed; blinking was kept.**
They are not the same thing: glancing moved the pupils from side to side
periodically, which read as flickering. A blink is a single vertical contraction
(0.16 s, at random intervals of 2.4–6.0 s) and does not draw the eye.

**The mouth's control points moved from ±2.6 to ±4.8**: too close together and
the curve is dragged into a sharp-bottomed V; further out gives the rounded U.

### The session dial

**One dial, two readings, two tracks.** The inner arc is how much of the context
window is used: static, from twelve o'clock, deepening as it fills, with the
figure itself in the middle. The outer track is what the session is *actively*
doing: a comet travelling it while thinking, the whole halo pulsing while waiting
for an answer. Unread is not on the dial at all — see below.

The old spinner could not simply have gained a percentage. Its legibility came
from the arc length oscillating between 26° and 290°, so length was already the
"am I spinning" channel. Handing length to the context figure means motion has
to carry thinking on its own — and it can, because the comet is short, moves,
and runs on a ring the fill never touches. Had both stayed on length, 5% context
would have been indistinguishable from an ordinary spinner, and 86% from a
nearly closed ring wobbling.

The fill was first written as *grey towards `labelColor`* — a neutral that needs
no dark-mode special case. It read badly for a different reason: it was the one
cold element on a card otherwise made of honey gold, apricot pink and terracotta,
and at a high figure it became a near-complete black circle that pulled the eye
off everything else. It now runs through the sun's own pair, `coralLight` →
`sunDeepen`, which is the ramp the body itself darkens along when things get
tight. Dark mode runs the same pair reversed and brighter at the top, because
"deeper" cannot mean "darker" on a dark card — the arc would disappear exactly
when it matters most. The 0.8 power lifts the low end: at 10% a nearly invisible
arc looks like a fault rather than a reading.

The clear space inside the ring is 16.2 pt and `100` alone needs 16.4 at 8 pt,
so a `%` sign was never going to fit — `19%` needs 16.8 even at 7 pt. Three
digits drop to 7 pt and one or two stay at 8.

**Naming the dial is the row's job, not the dial's.** A caption underneath it —
the way the two dials at the top of the card are labelled — was tried and taken
out again. At 7.5 pt it is smaller than the value it labels, it hangs in the
block's bottom corner with nothing to line up against, and the same word already
sits at full size at the left of the very same row. There is nowhere else to put
it either: inside the ring `上下文` needs 16.5 pt against 16.2 of clear space,
and to the left of the dial it would take 30 pt off the title. So the row reads
left to right — what it is, how many tokens, and the dial closing it off with the
percentage.

**Unread lives in front of the word, not on the dial.** Pulsing the halo meant
the one element carrying a neutral reading kept flashing in the sun's colour for
a reason that had nothing to do with context; before that it was a single tick at
twelve o'clock, too small to notice on a block already dimmed for being finished.
A breathing dot in front of "unread" is next to the thing it qualifies, and it
costs the dial nothing.

The separate progress bar is gone, and with it the repeated percentage: the dial
says the same thing at a glance, which the number never did.

### Minimising

`model.minimised` overrides everything in `expandTargetValue`, hover included —
without that the pointer pops the card straight back open and the button appears
to do nothing. It is persisted (`PetMinimised`): deliberately getting the card
out of the way, only for it to return on the next launch, would be the app
overriding a decision already made.

**The button is not sized by eye.** A window's own miniaturise button is a 14 pt
square inset 9 pt from the frame edge — read straight off
`NSWindow.standardWindowButton` rather than looked up. The bar inside it is 49%
of the diameter long and 8.8% thick, measured off `minus.circle.fill` in SF
Symbols, which is Apple drawing this exact glyph. The first attempt was a 17 pt
disc with a bar 10% thick: larger than the control the platform ships, on a card
a quarter the width of a window, and heavier than the glyph it was imitating.

The 9 pt inset does not survive the trip to a 198 pt card, so the button is
placed by the corner instead. Centred at (13, 13) inside a 26 pt corner radius,
its outermost point sits 25.4 pt from the corner's centre of curvature against a
radius of 26 — it beds into the curve the way a traffic light beds into a title
bar, which is what the inset was buying in the first place.

Two things needed deciding rather than coding:

- **A session waiting on an answer.** Expanded, the glass takes a warm tint for
  this; minimised there is no glass. Rather than force the card open — which
  would make "minimised" mean nothing — the sun itself pulses towards its glow
  colour. The reminder still arrives; the decision is not overridden.
- **Click versus drag.** `performDrag` blocks until the drag ends, so on macOS
  the window origin is compared across it: unchanged means it was a click, and
  only then does the card come back. Windows cannot do this — `BeginMoveDrag`
  swallows everything once it has started — so there a press restores whether or
  not it turns into a drag. On a window this small that is the lesser evil
  against a click that appears to do nothing.

### Animation and window resizing

**Use a fixed-duration S-curve (`Tween`), not exponential smoothing (`smoothStep`).**
Exponential smoothing is always fast then slow: folding covers most of the
distance in the first 0.1 s, then grinds out the remainder — which simply reads
as snapping out of existence rather than easing. Now: hover 0.30/0.42 s, expand
0.40/0.62 s, session blocks 0.34/0.50 s, folding always slower than unfolding.
(`smoothStep` is still used for ring values, ray extension and body lean — the
*following* quantities, where it is the right tool.)

**Window height must use the continuous `blocksHeight`; never count blocks.**
Block count is discrete, so the last block disappearing drops the window 50 pt
in a single frame and eats every easing curve. Blocks that are fading out
therefore stay in the `blocks` array carrying their own data so they can still
be drawn. `blocksHeight` must also be clamped at 0 — when `sum` is small,
`sum×56−6` is negative and the window shrinks past its target before springing back.

**`expandTargetValue` uses `blocks`, not `visibleSessions`**: the window must not
start folding while a block is still fading, or the two animations overlap and
it once again looks like a snap.

**Both the dials and the glass have to leave before the window does.**
The dials only start appearing at `e = 0.34` (`(e−0.34)/0.66`) and scale slightly
with it; the glass has finished fading by `e/0.45`. If either is still there when
the window has almost narrowed to the sun alone, it gets sliced off by the window
edge, or a circular patch of colour vanishes on the spot.

**`setFrame` does not deliver a `mouseExited` to a stationary cursor**: after the
window shrinks, hover state has to be reconciled by hand, otherwise the pointer
never moved, the window moved out from under it, and `hovered` stays stuck on.

**Arc direction**: this view is `isFlipped`, and flipping the canvas vertically
flips the direction of rotation with it, so *increasing* angles are what render
clockwise on screen (verified frame by frame with offscreen renders). The
spinner's seamless wrap relies on the tail angle covering exactly 360° per cycle
while the arc length oscillates between 26° and 290° on a cosine (zero derivative
at both ends), so both angle and length are continuous where the phase wraps.

### Glass and windowing

**`NSGlassEffectView` is used without setting `contentView`.**
WWDC25 session 310 asks you to put content inside `contentView` and let AppKit
handle legibility. Measured, A/B against the same background: once it is set,
AppKit adds a legibility backing behind dense text covering the whole area, and
the glass is flattened into a nearly opaque dark panel — nothing shows through,
and the Liquid Glass look is gone. Sibling views stacked instead, with legibility
guaranteed by semantic colours (the `labelColor` family) and measured contrast.

**The glass view has to forward `hitTest`**, or it swallows hit testing by
default and dragging, double-clicking and click-to-mark-read all stop working.

**On the pre-macOS-26 path, `petView` must be a sibling of `NSVisualEffectView`**:
made a child, "Reduce Transparency" hides the blur view and takes the entire
interface with it, so the app simply disappears.

**`hasShadow = false`**: the window is constantly resizing, and the system shadow
leaves a rectangular black outline trailing behind it.

**Float using `.statusBar` (25), not `.popUpMenu` (101)**: the latter covers the
menu bar, and also covers this app's own modal sign-in dialogue (modal level is
only 8). While that dialogue is up, `withLoweredWindow` also drops the window to
`.normal` temporarily.

**The window does not accept key** (`canBecomeKey = false`): clicking it does not
steal focus from the current app, so no focus ring is drawn. Dragging,
double-clicking and right-clicking do not depend on key state, and the sign-in
dialogue is a separate `NSAlert` window, unaffected.

**Store the top-left corner, not the bottom-left**: the height changes with
content, so storing the bottom edge makes the window creep upwards on every
restart. The old `PetWindowOrigin` key (bottom-left) is migrated once with a
+182 offset.

**Under `.accessory` an Edit menu has to be built by hand**, or ⌘V has no route
to the first responder and the authorisation code cannot be pasted into the
sign-in dialogue. Menu items must leave `target` empty so they travel the
responder chain.

**Accessibility elements must be retained by us**: AppKit only weakly references
`accessibilityParent`, so elements created and returned on the spot deallocate
immediately and assistive technology reads nothing but dead elements (-25202).
They may also only be rebuilt when the *set* of elements changes — rebuilding
sends the VoiceOver cursor back to the start — so value and position changes are
updated in place.

**`statusItem.menu` must not be replaced inside `menuNeedsUpdate`** (that is the
menu currently opening); rebuild its items in place.

### Sign-in and tokens

**The token is stored in a local file
(`~/Library/Application Support/Sundial/credentials.json`, mode 0600), not the
Keychain.**
Keychain ACLs are tied to the code signature, and this app's signature changes
on every rebuild, so the Keychain fails to recognise the new build and demands a
password to re-authorise. A file is unaffected by signing. The directory is 0700.
(The old `Solaris/` directory is migrated once.)

**`.completeFileProtection` must not be used**: that is iOS data protection, and
on macOS it binds access to the writing process's code signature — with the
result that the app cannot read the token it wrote itself (error 260 / EPERM),
which presents as being inexplicably asked to sign in again. Permission bits do
the protecting.

**One PKCE verifier is reused for the whole run.**
Generating a fresh one on every sign-in attempt means a code copied from a
previous authorisation page — and browsers keep those tabs around — can never
match, which presents as sign-in "always failing". It is only invalidated and
regenerated after a successful sign-in.

**A mismatched `state` is not rejected outright.**
The real security binding is PKCE's `code_verifier`, which the server verifies;
this submits as usual and lets the server decide, since otherwise a stale
authorisation page left open in the browser causes repeated failures.

**Only an explicit rejection of the credentials by the server (400/401) clears
the sign-in.** A dropped connection, a 429, or any 5xx keeps the refresh token
for a later attempt. On a 401 it renews at most three times, backing off
30 s → 120 s → 600 s. Tokens are written through `commitToken(_:epoch:)`, and a
mismatched epoch (the user signed out in the meantime) discards the whole thing.

**A failed Keychain read is recorded as `keychainBlocked`, not treated as a
conclusion** — otherwise the 60-second poll raises the authorisation dialogue
over and over. Only a manual refresh retries. The main thread's `hasToken` looks
solely at the in-memory cache and never touches the Keychain.

**An explicit "sign out" writes `petUserSignedOut`**, after which the Claude Code
CLI credentials are no longer used as a fallback. (An internal failure still
allows the fallback.)

### Usage parsing

- **Sort the top-level keys before iterating**: Swift dictionaries are unordered,
  so with duplicate names, which row a given label picks up would vary run to run.
- **Percentages are not clamped to 100** (the 999 ceiling exists only to stop bad
  data breaking the layout): an overrun *should* be visible as "106%".
- **The plan name is read from several possible keys**: originally only
  `rate_limit_tier` was accepted, and the endpoint had long stopped returning it
   — the badge had in fact been empty the whole time and nobody had noticed.
- **All three tier branches must assign**: missing the last one leaves the old
  plan name sitting next to the new account's figures after switching accounts.

### Session watching

There are only two data sources, and **only metadata is read, never the text of
the conversation**: `~/.claude/sessions/*.json` (the registry of running
sessions: pid + sessionId + title) and
`~/.claude/projects/<project>/<sessionId>.jsonl` (only the tail, to determine
busy/idle and where the turn began).

- **A pid is not enough; `procStart` has to be compared too.** Pids get recycled,
  and EPERM (someone else's process) is treated here as "still alive", so an
  unrelated process resurrects a long-finished session as a ghost block.
- **The tail window has to grow progressively.** A single record can be larger
  than the window (tool results of hundreds of KB are common; 1.35 MB has been
  seen on this machine). When the window lands entirely inside one record, not a
  single line parses, the session is judged finished, a false unread notice pops
  up and the timer resets. Grow until at least one complete record fits (two
  newlines).
- **The turn's start is anchored directly to the user's own record.** It used to
  be located via the `last-prompt` record, but that is written *after* the user's
  message, so the anchor always landed on the following tool result — measured
  across 349 turns, 348 were late, median 112 s, so a question just submitted
  displayed as "0s".
- **Take the first user action of the turn, not the last**: steering mid-turn
  should not reset the timer.
- **An Esc interruption must invalidate `turnStart`, `resumeStart` and
  `turnFloor` together**; miss one and the stale timestamp becomes the next
  turn's start — measured, this produced "9m 32s elapsed" on a turn that had just
  begun.
- **A synthetic record (`model == "<synthetic>"`, the placeholder left by an API
  error) does not necessarily mean the end**; Claude often retries by itself and
  carries on. Stash the start first and restore it if the turn resumes, so the
  timer does not begin again from zero.
- **The background directory must not stop after N entries**: enumeration order
  is unspecified, so it may skip exactly the newest file.
- **`bgFresh = 90` seconds.** Measured, the gap between consecutive writes by the
  same background agent is p95 ≈ 37 s and p99 ≈ 136 s; the earlier 25 s judged
  the task "finished" repeatedly mid-run, raising false notices and resetting the
  timer. Freshness must also be measured from *the moment of probing* — comparing
  a 3-second-old cached value against the current time invents up to 3 s of age,
  which is exactly enough to declare a running background task stopped. One empty
  probe is not enough either; two consecutive ones are required.
- **A timeout is not a completion.** It used to remove the block quietly and send
  the sun to sleep while Claude might still have been thinking; it now says "not
  responding". `staleAfter = 300 s`, relaxed to 900 s while waiting on a tool
  result (a single Bash call can run 600 s, plus room for a retry).
- **`finishedAt` uses the transcript file's own write time**, not the moment we
  noticed. Otherwise a turn that ended overnight shows as "just finished" when
  the machine wakes in the morning.
- **The direction of an error in the context limit matters.** Underestimate the
  denominator and the bar pins to full while printing self-contradictory figures
  like "992.9k used of 200.0k"; overestimate and it merely under-reports, which
  does not mislead. So only models known to be 200k are listed, and everything
  else — including models that do not exist yet — is assumed to be 1M. Measured
  on this machine's transcripts, `claude-opus-4-8` reached 992,897 tokens in a
  single context.

### Building

- **Compile and sign in `$TMPDIR`, never on the Desktop.**
  The Desktop has iCloud sync running, and the file provider asynchronously
  re-applies `com.apple.FinderInfo` to the `.app`, racing `codesign`. The result
  is an intermittent "resource fork, Finder information … not allowed", and no
  amount of clearing xattrs helps, because they are re-added *after* the clear.
  Sign first, then `ditto` the result out.
- **Traditional `.icns` cannot follow the system light/dark setting.**
  The format that can is macOS 26's `.icon`, which is only produced by Xcode's
  Icon Composer (GUI only). The asset-catalogue route is a dead end: `actool`
  accepts dark variants without a single warning, but the resulting `Assets.car`
  does not contain them (an appiconset with the mac idiom does not support
  light/dark variants — that is an iOS mechanism, and `assetutil` will confirm
  it). So the switch is manual: `icon/make-icons.sh dark`, then `build.sh`.

---

## 6. Data, privacy and where things are stored

| What | Where |
|---|---|
| Our own OAuth token | `~/Library/Application Support/Sundial/credentials.json` (0600) |
| CLI credentials used as fallback (read-only) | Keychain item `Claude Code-credentials`, or `~/.claude/.credentials.json` |
| Window position | `UserDefaults` → `PetWindowTopLeft` (old key `PetWindowOrigin` migrated once) |
| Interface toggles | `UserDefaults` → `PetClearGlass` / `PetAbovePopups` / `petUserSignedOut` |
| Session data | Not written to disk at all; held in memory |

Only three addresses are ever contacted: `claude.ai/oauth/authorize` (opened in
the browser), `console.anthropic.com/v1/oauth/token` (exchange and renewal) and
`api.anthropic.com/api/oauth/usage` (reading usage). Nothing passes through a
third party, nothing is logged, there are no analytics.

The usage endpoint is an undocumented one used by Claude Code's own client. If
Anthropic changes the format, the pet will say it received data it could not make
sense of and retry every five minutes.

---

### Degrading gracefully without a subscription

The authorisation page requires Claude Max/Pro, and an account without one simply
cannot connect. But **the session half reads local transcript files and has
nothing to do with signing in or subscribing**.

The line in `draw` — `if model.rows.isEmpty, let msg = model.errorMsg { … return }`
— used to be unconditional, which meant that failing to fetch usage also switched
off the one feature that still worked. It now also requires `blocks.isEmpty`, so
the error notice only takes over the whole card when there are no session blocks
either. Along with that: with no `rows`, the two empty rings are not drawn (two
empty tracks make people think it is broken), the detail area gains a line saying
usage is unavailable and only session state is shown, and the footer no longer
repeats it. `App.expandedHeight`'s error branch likewise now requires
`blocksHeight <= 0`.

### Menu bar / tray icon

On macOS this was the SF Symbol `sun.max`; it is now a real sun drawn by
`statusSunImage()` — body plus nine rays only, because at 18 pt the face and the
gradients turn to mush. It rotates at 12 fps while a session is running, and
stops and returns to true when idle. **A template image cannot be used** — the
system would paint it flat black and white, losing the coral entirely. Coral is a
mid-tone, and has been checked as visible on both light and dark menu bars.

On Windows, **left-clicking the tray icon** now calls `BringToFront()`, which only
raises the window. It used to call `EnsureVisible()`, so a single click teleported
the pet to the bottom-right **and wrote the new position to the config file** —
losing wherever the user had put it, permanently, restart included. Repositioning
is now triggered only by the explicit menu item.

### The refresh ripple has been removed

Each new fetch used to send a ripple out from the sun. Two problems: folded, its
radius (`max(w,h)×0.62 = 54.6`) exceeded the 88 pt window's inscribed radius of
44 and was cut into four corner arcs; and to a user it simply read as "something
inexplicably shooting out". The whole thing (`rippleStart` / `lastSeenFetch` /
`drawRefreshRipple`) is gone from both platforms.

## 7. Current state and outstanding work

### Found and fixed

A four-angle regression sweep (with per-finding adversarial verification)
confirmed and fixed:

- The warm glass tint while waiting for input never applied (`applyGlassShape`
  sat after the "only continue if the size changed" guard, and waiting does not
  change the size); and once applied it could not be undone.
- Session block order was frozen at first appearance, defeating
  `ActivityWatcher`'s ordering of "waiting for you" to the top.
- The refresh ripple was cut into four corner arcs when folded (removed entirely
  along with the feature).
- The hover detail was clipped hard by the window's lower edge during the transition.
- `stalled` was not cleared when the background probe expired, so the block kept
  saying "not responding" instead of "unread · just finished" (in this machine's
  real transcripts, 10 of 88 background runs hit this).
- `bgStaleHits` was incremented outside the probe cache guard, so "two consecutive
  empty probes" were in fact only 1.6 s apart.
- On Windows with Reduce Transparency, an 88×88 dark grey disc appeared behind
  the idle state.
- On Windows, a left-click on the tray icon teleported the pet to the
  bottom-right and wrote that to disk.

The fifteen shape and behaviour constants have been compared across both
platforms and all agree.

### Still unverified

- `dist/Sundial.zip` is an ad-hoc signed `share` build and is **not notarised**
  (only a free Apple Development certificate is available, which cannot produce a
  Developer ID signature).
- macOS: VoiceOver actually speaking the interface, Intel hardware, and macOS
  13/14/15 have none of them been run on real machines.
- Windows: **the sign-in dialogue has been verified on real hardware** (confirmed
  by photograph: the authorisation page, the paste box, and reopening the
  authorisation page all behaved). The tray icon and its menu, launch-at-login via
  the registry, and Windows 11 acrylic blur remain unverified item by item.
- `src-windows/tests/Sundial.Tests` exists but is still empty. The assertions used
  for this round of verification lived in throwaway scripts and are gone. The core
  layer can be tested on a Mac, and that is worth making permanent — otherwise
  keeping the two platforms in step depends on comparing by hand, which nearly
  missed a constant drifting this time.
