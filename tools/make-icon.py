"""Draws the BDM brand mark (Farzonline style: near-black chassis, glowing logo-green glyph) and writes
src/BDM.App/Assets/bdm.ico (16-256 px) and extension/icon.png (128 px). Needs Pillow: py -m pip install pillow"""
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
N = 1024  # drawn at 4x the 256 design grid, then scaled down
k = N / 256


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def hexrgb(h):
    return tuple(int(h[i:i + 2], 16) for i in (1, 3, 5))


def vertical(c1, c2):
    g = Image.new('RGB', (1, N))
    for y in range(N):
        g.putpixel((0, y), lerp(c1, c2, y / (N - 1)))
    return g.resize((N, N))


def diagonal(stops):
    """Top-left to bottom-right gradient through (offset, colour) stops."""
    g = Image.new('RGB', (N, N))
    px = g.load()
    for y in range(N):
        for x in range(N):
            t = (x + y) / (2 * (N - 1))
            for (o1, c1), (o2, c2) in zip(stops, stops[1:]):
                if t <= o2:
                    px[x, y] = lerp(c1, c2, (t - o1) / (o2 - o1))
                    break
    return g


def rounded(box, r):
    m = Image.new('L', (N, N), 0)
    ImageDraw.Draw(m).rounded_rectangle([v * k for v in box], radius=r * k, fill=255)
    return m


def stroke(points, width):
    """A polyline with round caps and joins, as a mask."""
    m = Image.new('L', (N, N), 0)
    d = ImageDraw.Draw(m)
    pts = [(x * k, y * k) for x, y in points]
    w = width * k
    d.line(pts, fill=255, width=round(w), joint='curve')
    for x, y in pts:
        d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=255)
    return m


img = Image.new('RGBA', (N, N), (0, 0, 0, 0))
chassis = rounded((8, 8, 248, 248), 58)
img.paste(vertical(hexrgb('#1d2226'), hexrgb('#0b0c0d')), (0, 0), chassis)

# soft green glow behind the glyph
glow = Image.new('L', (N, N), 0)
ImageDraw.Draw(glow).ellipse([40 * k, 40 * k, 216 * k, 216 * k], fill=70)
glow = ImageChops.multiply(glow.filter(ImageFilter.GaussianBlur(34 * k)), chassis)
img.paste(Image.new('RGB', (N, N), hexrgb('#04fa73')), (0, 0), glow)

# hairline rim
rim = ImageChops.subtract(chassis, rounded((9.5, 9.5, 246.5, 246.5), 56.5))
img.paste(Image.new('RGB', (N, N), (255, 255, 255)), (0, 0), rim.point(lambda v: v * 23 // 255))

# glyph: a download arrow dropping into a tray
glyph = ImageChops.lighter(stroke([(128, 58), (128, 146)], 20), stroke([(88, 108), (128, 148), (168, 108)], 20))
glyph = ImageChops.lighter(glyph, stroke([(62, 152), (62, 186), (194, 186), (194, 152)], 18))
halo = glyph.filter(ImageFilter.GaussianBlur(7 * k)).point(lambda v: v * 110 // 255)
img.paste(Image.new('RGB', (N, N), hexrgb('#04fa73')), (0, 0), halo)
volt = diagonal([(0, hexrgb('#7dffb8')), (0.45, hexrgb('#04fa73')), (1, hexrgb('#00b856'))])
img.paste(volt, (0, 0), glyph)

ico = ROOT / 'src' / 'BDM.App' / 'Assets' / 'bdm.ico'
ico.parent.mkdir(parents=True, exist_ok=True)
big = img.resize((256, 256), Image.LANCZOS)
big.save(ico, sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
img.resize((128, 128), Image.LANCZOS).save(ROOT / 'extension' / 'icon.png')
print('wrote', ico, 'and extension/icon.png')
