// Renders the app icons: swift render.swift <icons-dir> <favicon.ico path>, run from tools/icons.
// icon.svg becomes the PWA and iPhone icons; favicon.svg, a simpler mark, becomes icon-32.png and favicon.ico.
import AppKit

let out = CommandLine.arguments[1]
let ico = CommandLine.arguments[2]

func png(_ file: String, _ size: Int) -> Data {
    guard let svg = NSImage(contentsOfFile: file) else { fatalError("cannot read \(file)") }
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: size, height: size)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current!.imageInterpolation = .high
    svg.draw(in: NSRect(x: 0, y: 0, width: size, height: size))
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for (file, name, size) in [("icon.svg", "icon-512.png", 512), ("icon.svg", "icon-192.png", 192),
                           ("icon.svg", "apple-touch-icon.png", 180), ("favicon.svg", "icon-32.png", 32)] {
    try! png(file, size).write(to: URL(fileURLWithPath: "\(out)/\(name)"))
    print(name, size)
}

// favicon.ico holds PNG-encoded 16 and 32 px images, which every current browser reads.
let images = [16, 32].map { ($0, png("favicon.svg", $0)) }
var data = Data()
func le16(_ v: Int) { data.append(contentsOf: [UInt8(v & 0xff), UInt8(v >> 8 & 0xff)]) }
func le32(_ v: Int) { le16(v & 0xffff); le16(v >> 16) }
le16(0); le16(1); le16(images.count)
var offset = 6 + 16 * images.count
for (size, bytes) in images {
    data.append(contentsOf: [UInt8(size), UInt8(size), 0, 0])
    le16(1); le16(32); le32(bytes.count); le32(offset)
    offset += bytes.count
}
for (_, bytes) in images { data.append(bytes) }
try! data.write(to: URL(fileURLWithPath: ico))
print("favicon.ico", images.map { $0.0 })
