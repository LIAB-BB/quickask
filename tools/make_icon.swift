import AppKit

func drawIcon(size: CGFloat) -> Data? {
    let px = Int(size)
    guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px,
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                     isPlanar: false, colorSpaceName: .deviceRGB,
                                     bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let rect = NSRect(x: 0, y: 0, width: size, height: size)
    let radius = size * 0.225
    let path = NSBezierPath(roundedRect: rect.insetBy(dx: size * 0.01, dy: size * 0.01),
                            xRadius: radius, yRadius: radius)
    path.addClip()
    let gradient = NSGradient(colors: [
        NSColor(srgbRed: 0.42, green: 0.47, blue: 0.98, alpha: 1),
        NSColor(srgbRed: 0.60, green: 0.39, blue: 0.96, alpha: 1)
    ])!
    gradient.draw(in: rect, angle: -70)

    // 柔和高光
    let hl = NSGradient(colors: [NSColor.white.withAlphaComponent(0.16), NSColor.white.withAlphaComponent(0.0)])!
    hl.draw(fromCenter: NSPoint(x: size * 0.28, y: size * 0.88), radius: 0,
            toCenter: NSPoint(x: size * 0.28, y: size * 0.88), radius: size * 0.75, options: [])

    let text = "问"
    let font = NSFont.systemFont(ofSize: size * 0.52, weight: .semibold)
    let attrs: [NSAttributedString.Key: Any] = [
        .font: font,
        .foregroundColor: NSColor.white
    ]
    let str = NSAttributedString(string: text, attributes: attrs)
    let ts = str.size()
    str.draw(at: NSPoint(x: (size - ts.width) / 2, y: (size - ts.height) / 2 + size * 0.01))

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])
}

let dir = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon.iconset"
try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
let specs: [(String, CGFloat)] = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32),
    ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256),
    ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024)
]
for (name, size) in specs {
    if let data = drawIcon(size: size) {
        try? data.write(to: URL(fileURLWithPath: "\(dir)/\(name).png"))
    }
}
print("done")
