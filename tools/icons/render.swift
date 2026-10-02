// Renders icon.svg to the PNG sizes the app needs: swift render.swift <out-dir>
import AppKit

let out = CommandLine.arguments[1]
guard let svg = NSImage(contentsOfFile: "icon.svg") else { fatalError("cannot read icon.svg") }
for (name, size) in [("icon-512.png", 512), ("icon-192.png", 192), ("apple-touch-icon.png", 180), ("icon-32.png", 32)] {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: size, height: size)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current!.imageInterpolation = .high
    svg.draw(in: NSRect(x: 0, y: 0, width: size, height: size))
    NSGraphicsContext.restoreGraphicsState()
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(out)/\(name)"))
    print(name, size)
}
