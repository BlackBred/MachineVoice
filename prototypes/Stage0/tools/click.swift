// Usage: swift click.swift <x> <y>
// Posts a left click at global screen coordinates (points, top-left origin) and prints which app
// was frontmost before and after. Requires the Accessibility permission for the calling process.
import AppKit

let args = CommandLine.arguments
guard args.count == 3, let x = Double(args[1]), let y = Double(args[2]) else {
    print("usage: click.swift <x> <y>")
    exit(2)
}

func frontmost() -> String { NSWorkspace.shared.frontmostApplication?.localizedName ?? "—" }

print("доверие Accessibility: \(AXIsProcessTrusted())")
print("до клика: \(frontmost())")

let point = CGPoint(x: x, y: y)
let source = CGEventSource(stateID: .hidSystemState)
for type in [CGEventType.mouseMoved, .leftMouseDown, .leftMouseUp] {
    CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: .left)?
        .post(tap: .cghidEventTap)
    usleep(60_000)
}

RunLoop.current.run(until: Date().addingTimeInterval(0.5))
print("после клика: \(frontmost())")
