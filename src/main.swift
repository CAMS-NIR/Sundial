// Sundial — programme entry point (Swift only allows top-level statements in main.swift)

import AppKit

// MARK: - Entry point

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
