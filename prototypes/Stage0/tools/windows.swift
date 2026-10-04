// Usage: swift windows.swift <pid>
// Lists on-screen and off-screen windows of a process: layer, bounds, on-screen flag.
import CoreGraphics
import Foundation

guard CommandLine.arguments.count == 2, let pid = Int(CommandLine.arguments[1]) else {
    print("usage: windows.swift <pid>")
    exit(2)
}

let info = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] ?? []
for window in info where (window[kCGWindowOwnerPID as String] as? Int) == pid {
    let layer = window[kCGWindowLayer as String] as? Int ?? -1
    let bounds = window[kCGWindowBounds as String] as? [String: Double] ?? [:]
    let onScreen = window[kCGWindowIsOnscreen as String] as? Bool ?? false
    let alpha = window[kCGWindowAlpha as String] as? Double ?? -1
    print("layer \(layer) onScreen \(onScreen) alpha \(alpha) x \(bounds["X"] ?? 0) y \(bounds["Y"] ?? 0) w \(bounds["Width"] ?? 0) h \(bounds["Height"] ?? 0)")
}
