// Sundial — 程序入口（Swift 只允许 main.swift 出现顶层语句）

import AppKit

// MARK: - 入口

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
