// Finding the other sun.
//
// Sundial and Sundial for Codex are separate processes from separate repositories, so neither can
// ask the other where it is. They shout instead: each posts its position on a distributed
// notification, and listens for the other doing the same.
//
// **Why distributed notifications and not a shared file.** A file means polling, and polling a
// position that changes 60 times a second during a drag means either a stale reading or a pointless
// disk write per frame. This is event-driven and never touches disk. The alternative with real
// guarantees — an App Group container — needs a paid team identifier to sign against, and these
// builds are ad-hoc signed, so it is not available.
//
// **What is broadcast is the sun's centre, not the window frame.** The sun does not sit in the
// middle of its window: folded it is centred in an 88pt square, expanded it is in the top row of a
// 198pt card, and in the Codex build it moves sideways again depending on how many dials there are.
// Aligning window edges would therefore align nothing you can see. Both are sent — the centre is
// what the eye lines up, the frame is what stops the two windows overlapping.
//
// The frame is carried for a second reason: ray gravity between the two suns, if that ever gets
// built, needs the neighbour's position every frame, not just at the moment of a drop. That is
// already the case here at no extra cost.

import AppKit

final class Neighbour {
    /// Presence: "I am here". Posted on every move, on every resize, and on a slow heartbeat.
    private static let presence = Notification.Name("com.sundial.pet.presence")
    /// "Anyone there?" — posted at launch, because presence alone would leave a newly started app
    /// blind until the other one happened to move.
    private static let ping = Notification.Name("com.sundial.pet.ping")

    /// After this long with no word, the neighbour is treated as gone. Three missed heartbeats:
    /// long enough to ride out a busy moment, short enough that quitting the other app does not
    /// leave a ghost to snap against.
    private static let expiry: TimeInterval = 12
    private static let heartbeat: TimeInterval = 3.5

    /// Which build this is. Only used to tell the two apart in the payload; snapping does not care
    /// which is which, so two copies of the same build would still find each other.
    private let me: String
    private let pid = ProcessInfo.processInfo.processIdentifier

    private(set) var sunCentre: NSPoint?   // the *other* sun, in screen coordinates
    private(set) var frame: NSRect?        // the other window, in screen coordinates
    private var seenAt: Date?

    /// Fired when the neighbour appears, moves or goes away, so the view can redraw if it is
    /// showing anything that depends on it.
    var onChange: (() -> Void)?

    private var timer: Timer?
    private var last: String?

    var isPresent: Bool {
        guard let seenAt else { return false }
        return Date().timeIntervalSince(seenAt) < Self.expiry
    }

    init(me: String) {
        self.me = me
        let centre = DistributedNotificationCenter.default()
        centre.addObserver(self, selector: #selector(heard(_:)),
                           name: Self.presence, object: nil)
        centre.addObserver(self, selector: #selector(pinged), name: Self.ping, object: nil)
        // Announce, then ask. Announcing first means an app that is already running hears about
        // this one immediately, rather than only after replying to the ping.
        centre.postNotificationName(Self.ping, object: nil, userInfo: nil, deliverImmediately: true)
        timer = Timer.scheduledTimer(withTimeInterval: Self.heartbeat, repeats: true) { [weak self] _ in
            self?.repost()
            self?.expireIfStale()
        }
    }

    deinit {
        timer?.invalidate()
        DistributedNotificationCenter.default().removeObserver(self)
    }

    // MARK: Sending

    /// Tell anyone listening where this sun is. Cheap to call — identical payloads are dropped, so
    /// wiring it to every window-moved notification does not flood the bus while a drag is idle.
    func announce(sunCentre: NSPoint, frame: NSRect) {
        // A single string rather than a dictionary: distributed notifications serialise their
        // userInfo through the notification daemon, which is only reliable for property-list types,
        // and a flat string is the one shape that cannot be got wrong.
        let payload = String(format: "%@|%d|%.1f|%.1f|%.1f|%.1f|%.1f|%.1f",
                             me, pid, sunCentre.x, sunCentre.y,
                             frame.minX, frame.minY, frame.width, frame.height)
        guard payload != last else { return }
        last = payload
        DistributedNotificationCenter.default().postNotificationName(
            Self.presence, object: payload, userInfo: nil, deliverImmediately: true)
    }

    /// Re-send the last payload on the heartbeat, unchanged. `announce` de-duplicates, so a sun that
    /// has not moved for an hour would otherwise fall silent and be declared gone.
    private func repost() {
        guard let last else { return }
        DistributedNotificationCenter.default().postNotificationName(
            Self.presence, object: last, userInfo: nil, deliverImmediately: true)
    }

    /// Somebody just started up. Answer with the current position, bypassing the de-duplication in
    /// `announce` — the payload has not changed, but the new arrival has never heard it.
    @objc private func pinged() { repost() }

    // MARK: Receiving

    @objc private func heard(_ note: Notification) {
        guard let payload = note.object as? String else { return }
        let f = payload.components(separatedBy: "|")
        guard f.count == 8, let theirPid = Int32(f[1]) else { return }
        guard theirPid != pid else { return }   // distnoted delivers to the sender as well
        let n = f[2...].compactMap { Double($0) }
        guard n.count == 6 else { return }

        sunCentre = NSPoint(x: n[0], y: n[1])
        frame = NSRect(x: n[2], y: n[3], width: n[4], height: n[5])
        seenAt = Date()
        onChange?()
    }

    private func expireIfStale() {
        guard seenAt != nil, !isPresent else { return }
        sunCentre = nil
        frame = nil
        seenAt = nil
        onChange?()
    }
}

// MARK: - Snapping

extension Neighbour {
    /// How far the two windows are allowed to overlap once snapped, in points.
    ///
    /// Zero would leave the two folded suns 88pt apart — the full width of a folded window — which
    /// reads as "two things near each other" rather than "two things together". The sun including
    /// its rays is about 54pt across, so a 12pt overlap of the transparent margins closes the gap to
    /// roughly 22pt of air between the ray tips. It is small enough that the strip where one window
    /// covers the other is empty in both, so neither can steal a click meant for the other.
    fileprivate static let overlap: CGFloat = 12

    /// How close a drop has to land before it is taken as "put it next to that one".
    fileprivate static let catchGap: CGFloat = 64      // between the nearest window edges
    fileprivate static let catchRise: CGFloat = 76     // between the two sun centres, vertically

    /// Where this window should end up if it was just dropped next to the neighbour, or nil to
    /// leave it exactly where the user put it.
    ///
    /// The two rules are deliberately different in kind. Horizontally the **window edges** are made
    /// to meet, because that is what stops an expanded card from swallowing the other sun — the card
    /// is 198pt wide and the sun sits in the middle of it, so aligning sun centres at a fixed
    /// distance would put the neighbour inside it. Vertically the **sun centres** are aligned,
    /// because that is the line the eye actually reads, and the sun's height within the window is
    /// not the same folded as expanded.
    func snapTarget(for frame: NSRect, sunCentre: NSPoint) -> NSPoint? {
        guard isPresent, let theirFrame = self.frame, let theirSun = self.sunCentre else { return nil }
        return Neighbour.snapTarget(for: frame, sunCentre: sunCentre,
                                    nextTo: theirFrame, theirSun: theirSun)
    }

    /// The geometry on its own, with no live state behind it, so it can be checked against fixed
    /// numbers instead of against two running apps.
    static func snapTarget(for frame: NSRect, sunCentre: NSPoint,
                           nextTo theirFrame: NSRect, theirSun: NSPoint) -> NSPoint? {
        let gap = theirFrame.minX > frame.maxX ? theirFrame.minX - frame.maxX
                : frame.minX > theirFrame.maxX ? frame.minX - theirFrame.maxX
                : 0                                   // already overlapping horizontally
        guard gap < catchGap else { return nil }
        guard abs(sunCentre.y - theirSun.y) < catchRise else { return nil }

        // Which side to settle on is decided by where the sun was dropped, not by which window edge
        // is nearer: the sun is the thing being aimed, and on an expanded card the two disagree.
        let onRight = sunCentre.x >= theirSun.x
        let x = onRight ? theirFrame.maxX - overlap
                        : theirFrame.minX - frame.width + overlap
        // The offset from the window's bottom edge up to the sun, preserved so that aligning the
        // sun centres can be expressed as a window origin.
        let sunAboveBottom = sunCentre.y - frame.minY
        return NSPoint(x: x, y: theirSun.y - sunAboveBottom)
    }
}
