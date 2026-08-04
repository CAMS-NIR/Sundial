// Sundial — a desktop pet showing Claude Code usage and session state
// This file was split out of main.swift

import AppKit
import Foundation

// MARK: - Data model

struct UsageRow {
    let label: String
    let percent: Int          // percentage used; may be >100 (over the limit), the ring clamps itself to one turn
    let resetAt: Date?
    let priority: Int
}

final class PetModel {
    var rows: [UsageRow] = []
    var tier: String = ""
    var lastFetch: Date?
    var errorMsg: String?      // set to a message when there is an error; cleared once it succeeds
    var asleep: Bool = false   // the pet sleeps when the data can't be fetched
    var loading: Bool = true
    var needsLogin: Bool = false  // no usable token, waiting for the user to log in
    var sessions: [SessionActivity] = []
    var hovered: Bool = false     // expand the details while the mouse hovers
    var detailsPinned: Bool = false  // pinned open from the menu (doesn't depend on the mouse)

    var anyBusy: Bool { sessions.contains { $0.busy } }

    /// The ones currently running + the ones finished but not yet looked at (an unread one stays around
    /// until it is clicked away or that session starts working again)
    var visibleSessions: [SessionActivity] {
        Array(sessions.filter { $0.busy || $0.unread }.prefix(PetView.maxBlocks))
    }

    var maxPercent: Int { rows.map { $0.percent }.max() ?? 0 }

    /// For the rings: outer ring = 5 hours, inner ring = weekly (all models); if they can't be found it
    /// falls back to source order
    /// Rings: outer ring = 5 hours; inner ring = whichever weekly limit is used the most (it may be the
    /// all-models one, or a weekly limit specific to one model — when the latter is tighter it has to be
    /// shown, otherwise it misleads)
    var ringRows: (outer: UsageRow?, inner: UsageRow?) {
        let outer = rows.first { $0.label.contains("5 hours") } ?? rows.first
        let weeklies = rows.filter { $0.label.hasPrefix("Weekly") }
        let inner = weeklies.max { $0.percent < $1.percent }
            ?? rows.first { $0.label != outer?.label }
        return (outer, inner)
    }
}

