#!/usr/bin/env python3
"""
Generates the mod's PNG textures procedurally.

Like the audio, everything here is made from scratch so the mod carries no
asset from any other source (PLAN.md §0.4). Re-run after editing:
    python3 scripts/generate_textures.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEX = os.path.join(ROOT, "assets", "shinimodori", "textures")


# ------------------------------------------------------------------ png output

def write_png(path, pixels, w, h):
    """pixels: flat list of (r,g,b,a) tuples, row-major."""
    raw = bytearray()
    for y in range(h):
        raw.append(0)                                   # filter type 0
        for x in range(w):
            r, g, b, a = pixels[y * w + x]
            raw += bytes((r & 255, g & 255, b & 255, a & 255))

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(png)
    return len(png)


# ------------------------------------------------------------------- utilities

class Rng:
    """Small deterministic PRNG, so the textures are identical on every run."""

    def __init__(self, seed):
        self.s = seed & 0xFFFFFFFF

    def next(self):
        self.s = (1103515245 * self.s + 12345) & 0x7FFFFFFF
        return self.s

    def f(self):
        return self.next() / 0x7FFFFFFF

    def between(self, a, b):
        return a + (b - a) * self.f()


def clamp(v, lo=0, hi=255):
    return int(max(lo, min(hi, v)))


def value_noise(rng, w, h, cells):
    """Smooth 2D noise by bilinear interpolation over a coarse grid."""
    gw, gh = cells + 1, cells + 1
    grid = [[rng.f() for _ in range(gw)] for _ in range(gh)]
    out = []
    for y in range(h):
        gy = y / h * cells
        y0 = int(gy)
        fy = gy - y0
        y1 = min(y0 + 1, gh - 1)
        for x in range(w):
            gx = x / w * cells
            x0 = int(gx)
            fx = gx - x0
            x1 = min(x0 + 1, gw - 1)
            # smoothstep for a softer blend than plain linear
            sx = fx * fx * (3 - 2 * fx)
            sy = fy * fy * (3 - 2 * fy)
            top = grid[y0][x0] * (1 - sx) + grid[y0][x1] * sx
            bot = grid[y1][x0] * (1 - sx) + grid[y1][x1] * sx
            out.append(top * (1 - sy) + bot * sy)
    return out


def fbm(rng, w, h, octaves=4, base_cells=3):
    total = [0.0] * (w * h)
    amp, norm, cells = 1.0, 0.0, base_cells
    for _ in range(octaves):
        layer = value_noise(rng, w, h, cells)
        for i in range(w * h):
            total[i] += layer[i] * amp
        norm += amp
        amp *= 0.5
        cells *= 2
    return [v / norm for v in total]


# -------------------------------------------------------------------- textures

def mabeast(w=72, h=72):
    """
    Black fur. Deliberately close to uniform so it reads correctly however the
    borrowed wolf shape lays out its UVs, with enough grain that it is not a
    flat silhouette in daylight.
    """
    rng = Rng(9001)
    coarse = fbm(rng, w, h, octaves=4, base_cells=4)
    fine = fbm(Rng(9002), w, h, octaves=3, base_cells=16)

    px = []
    for y in range(h):
        for x in range(w):
            i = y * w + x
            v = coarse[i] * 0.7 + fine[i] * 0.3
            # A very dark blue-black; the highlights never get far off the floor.
            base = 10 + v * 26
            r = clamp(base * 1.02)
            g = clamp(base * 0.94)
            b = clamp(base * 1.18)
            # Faint reddish undertone in the deepest parts of the coat.
            if v < 0.28:
                r = clamp(r + (0.28 - v) * 70)
                b = clamp(b - (0.28 - v) * 14)
            px.append((r, g, b, 255))
    return px, w, h


def white_flower(w=16, h=16):
    """A small pale bloom on a transparent field, for the tea party's meadow."""
    rng = Rng(4242)
    n = fbm(rng, w, h, octaves=3, base_cells=4)
    px = []
    for y in range(h):
        for x in range(w):
            i = y * w + x
            cx, cy = x - 7.5, y - 8.5
            d = math.hypot(cx, cy)

            # Five petals around the centre.
            ang = math.atan2(cy, cx)
            petal = abs(math.cos(ang * 2.5))
            reach = 3.2 + petal * 3.4

            if d <= reach:
                shade = 226 + n[i] * 28
                a = 255 if d < reach - 1 else 165
                px.append((clamp(shade), clamp(shade * 0.99), clamp(shade * 0.96), a))
            elif y > 10 and abs(x - 8) <= 0:
                px.append((150, 176, 140, 255))       # a single stem
            else:
                px.append((0, 0, 0, 0))
    return px, w, h


def tea_table(w=16, h=16):
    """Pale bleached wood, grained along one axis."""
    rng = Rng(777)
    n = fbm(rng, w, h, octaves=3, base_cells=3)
    px = []
    for y in range(h):
        for x in range(w):
            i = y * w + x
            grain = math.sin(y * 1.7 + n[i] * 5.5) * 0.5 + 0.5
            v = 198 + grain * 34 + n[i] * 14
            px.append((clamp(v), clamp(v * 0.985), clamp(v * 0.95), 255))
    return px, w, h


def shard_of_envy(w=16, h=16):
    """An obsidian-black crystal with a violet edge. Warm to hold."""
    px = [(0, 0, 0, 0)] * (w * h)
    px = list(px)
    rng = Rng(1313)
    n = fbm(rng, w, h, octaves=3, base_cells=4)

    # A tall irregular shard through the middle of the sprite.
    for y in range(h):
        t = y / (h - 1)
        half = 1.2 + 3.4 * math.sin(math.pi * min(1.0, t * 1.12)) ** 0.8
        cx = 8 + math.sin(t * 2.4) * 0.9
        for x in range(w):
            dx = x - cx
            if abs(dx) > half:
                continue
            i = y * w + x
            edge = 1.0 - abs(dx) / half
            v = 6 + n[i] * 22
            r = clamp(v + (1 - edge) * 44)
            g = clamp(v * 0.5)
            b = clamp(v + (1 - edge) * 78)
            # One bright facet, so it catches light in the hotbar.
            if 0.22 < t < 0.46 and -0.4 < dx < 1.3:
                r, g, b = clamp(r + 70), clamp(g + 26), clamp(b + 96)
            px[i] = (r, g, b, 255)
    return px, w, h


def mabeast_pelt(w=16, h=16):
    """A hide that will not lie flat."""
    rng = Rng(31337)
    n = fbm(rng, w, h, octaves=4, base_cells=4)
    px = []
    for y in range(h):
        for x in range(w):
            i = y * w + x
            # Rough hide outline: rounded rectangle with ragged edges.
            inset = 1.6 + n[i] * 1.8
            if x < inset or x > w - 1 - inset or y < inset or y > h - 1 - inset:
                px.append((0, 0, 0, 0))
                continue
            v = 16 + n[i] * 34
            px.append((clamp(v * 1.05), clamp(v * 0.95), clamp(v * 1.15), 255))
    return px, w, h


def mabeast_cloak(w=16, h=16):
    """The pelt, made into something you can carry against the scent."""
    rng = Rng(5150)
    n = fbm(rng, w, h, octaves=4, base_cells=5)
    px = []
    for y in range(h):
        for x in range(w):
            i = y * w + x
            t = y / (h - 1)
            half = 2.0 + t * 5.6                       # widens toward the hem
            dx = x - 7.5
            if abs(dx) > half or y < 2:
                px.append((0, 0, 0, 0))
                continue
            v = 20 + n[i] * 30
            # A cold rim along the shoulders.
            if y < 5:
                v += 24
            px.append((clamp(v * 1.02), clamp(v * 0.96), clamp(v * 1.22), 255))
    return px, w, h


def witch_factor(w=16, h=16):
    """Not an object. Rendered as one for convenience."""
    rng = Rng(66613)
    n = fbm(rng, w, h, octaves=3, base_cells=5)
    px = []
    for y in range(h):
        for x in range(w):
            i = y * w + x
            d = math.hypot(x - 7.5, y - 7.5)
            r = 5.4 + n[i] * 1.6
            if d > r:
                px.append((0, 0, 0, 0))
                continue
            core = max(0.0, 1.0 - d / r)
            v = 8 + n[i] * 18
            px.append((clamp(v + core * 96), clamp(v * 0.4), clamp(v + core * 150),
                       clamp(150 + core * 105)))
    return px, w, h


TEXTURES = [
    ("entity/mabeast-ulgarm.png", mabeast),
    ("block/witch-whiteflower.png", white_flower),
    ("block/witch-tea-table.png", tea_table),
    ("item/shard-of-envy.png", shard_of_envy),
    ("item/mabeast-pelt.png", mabeast_pelt),
    ("item/mabeast-cloak.png", mabeast_cloak),
    ("item/witch-factor.png", witch_factor),
]


if __name__ == "__main__":
    total = 0
    for rel, fn in TEXTURES:
        px, w, h = fn()
        size = write_png(os.path.join(TEX, rel), px, w, h)
        total += size
        print(f"  {rel:<38} {w}x{h}  {size:6d} B")
    print(f"\n{total} bytes total in {TEX}")
