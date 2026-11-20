"""Regenerates the tray icons from the Mac menu bar mark (app/DesignSystem.swift Logo.bars).
Pixel-snapped bars, so the glyph is crisp at every size. Usage: python3 -I make_tray_icons.py <out-dir>"""
import io, struct, sys
from PIL import Image, ImageDraw

HEIGHTS = [24, 68, 36, 52, 20]            # bar heights on the 96-unit grid; every bar is centred on 48
LAYOUT = {16: (2, 1), 20: (2, 2), 24: (3, 2), 32: (4, 2), 48: (6, 3), 64: (8, 4)}  # size: (bar width, gap) in px

def frame(size, color):
    bw, gap = LAYOUT[size]
    total = 5 * bw + 4 * gap
    top = size - 2 * max(1, size // 16)   # tallest bar
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    x = (size - total) // 2
    for h in HEIGHTS:
        px = max(2, round(h / 68 * top / 2) * 2)
        y = (size - px) // 2
        d.rectangle([x, y, x + bw - 1, y + px - 1], fill=color)
        x += bw + gap
    return img

def write_ico(path, color):
    pngs = []
    for size in LAYOUT:
        b = io.BytesIO(); frame(size, color).save(b, "PNG"); pngs.append((size, b.getvalue()))
    out = struct.pack("<HHH", 0, 1, len(pngs)); offset = 6 + 16 * len(pngs); body = b""
    for size, data in pngs:
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset + len(body)); body += data
    open(path, "wb").write(out + body)

out = sys.argv[1]
write_ico(f"{out}/tray-light.ico", (0, 0, 0, 255))        # for a light taskbar
write_ico(f"{out}/tray-dark.ico", (255, 255, 255, 255))   # for a dark taskbar
