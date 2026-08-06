import AppKit

// Fixed cases for the snap geometry. Folded windows are 88×88 with the sun in the middle;
// expanded ones are 198 wide with the sun at x+99, y+height-42 (measured from the running apps).
func folded(_ x: CGFloat, _ y: CGFloat) -> (NSRect, NSPoint) {
    (NSRect(x: x, y: y, width: 88, height: 88), NSPoint(x: x + 44, y: y + 44))
}
func expanded(_ x: CGFloat, _ y: CGFloat, h: CGFloat = 118) -> (NSRect, NSPoint) {
    (NSRect(x: x, y: y, width: 198, height: h), NSPoint(x: x + 99, y: y + h - 42))
}

var failures = 0
func check(_ name: String, _ mine: (NSRect, NSPoint), _ theirs: (NSRect, NSPoint),
           expect: String) {
    let got = Neighbour.snapTarget(for: mine.0, sunCentre: mine.1,
                                   nextTo: theirs.0, theirSun: theirs.1)
    var desc = "no snap"
    if let g = got {
        // Where the two suns end up once the move is applied
        let newSun = NSPoint(x: mine.1.x + (g.x - mine.0.minX), y: mine.1.y + (g.y - mine.0.minY))
        let newFrame = NSRect(x: g.x, y: g.y, width: mine.0.width, height: mine.0.height)
        // Signed gap between the nearest edges, whichever side ended up on the left
        let edgeGap = max(newFrame.minX, theirs.0.minX) - min(newFrame.maxX, theirs.0.maxX)
        desc = String(format: "dx %.0f  dy %.0f  edges %.0f",
                      newSun.x - theirs.1.x, newSun.y - theirs.1.y, edgeGap)
    }
    let ok = desc == expect
    if !ok { failures += 1 }
    print("  \(ok ? "✓" : "✗") \(name.padding(toLength: 40, withPad: " ", startingAt: 0)) \(desc)"
          + (ok ? "" : "   (want \(expect))"))
}

print("=== snap geometry ===")
// Dropped just to the right of a folded neighbour, slightly high
check("folded, dropped right and 20 above", folded(1600, 620), folded(1521, 600),
      expect: "dx 76  dy 0  edges -12")
// Same, from the left
check("folded, dropped left", folded(1440, 610), folded(1521, 600),
      expect: "dx -76  dy 0  edges -12")
// The neighbour is expanded: edges must still meet, so the suns end up further apart
check("folded, neighbour expanded", folded(1740, 560), expanded(1521, 500),
      expect: "dx 131  dy 0  edges -12")
// This one expanded, neighbour folded
check("expanded, neighbour folded", expanded(1620, 560), folded(1521, 600),
      expect: "dx 131  dy 0  edges -12")
// Too far to the side
check("dropped 80pt clear", folded(1700, 600), folded(1521, 600), expect: "no snap")
// Too far vertically
check("dropped 90pt high", folded(1600, 690), folded(1521, 600), expect: "no snap")
// Right on top of it — still snaps, to whichever side the sun is leaning
check("dropped overlapping", folded(1540, 600), folded(1521, 600),
      expect: "dx 76  dy 0  edges -12")

print(failures == 0 ? "\nall pass" : "\n\(failures) FAILED")
exit(failures == 0 ? 0 : 1)
