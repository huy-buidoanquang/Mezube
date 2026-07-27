"""EQ atlas for Mezon Animation — native 80×80 cells, no vertical gaps inside a cell.

Mezon renders each pool item at 80×80 with `flex gap-2` between cells.
Any transparent vertical slit *inside* a cell shows as a black divider in UI.
Each frame is therefore a single full-width column with a continuous horizontal
rainbow gradient (slice of the full spectrum for that cell index).
"""
from __future__ import annotations

import json
import math
import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent

COLS = 10
HEIGHTS = 10
FRAME = 80  # Mezon small-cell size
STEPS = 24
# Vertical LED segment size (horizontal gaps between segments are fine).
SEG_H = 6
SEG_GAP = 1
PITCH = SEG_H + SEG_GAP

RAINBOW = [
    (255, 40, 50),
    (255, 130, 25),
    (255, 215, 35),
    (35, 200, 75),
    (35, 180, 200),
    (35, 120, 255),
    (120, 70, 255),
    (165, 55, 255),
]


def png_chunk(tag: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)


def write_png(path: Path, width: int, height: int, rgba: bytearray) -> None:
    raw = bytearray()
    stride = width * 4
    for y in range(height):
        raw.append(0)
        raw.extend(rgba[y * stride : (y + 1) * stride])
    png = bytearray(b"\x89PNG\r\n\x1a\n")
    png.extend(png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)))
    png.extend(png_chunk(b"IDAT", zlib.compress(bytes(raw), 9)))
    png.extend(png_chunk(b"IEND", b""))
    path.write_bytes(png)


def set_px(buf, w, h, x, y, rgba):
    if x < 0 or y < 0 or x >= w or y >= h:
        return
    i = (y * w + x) * 4
    r, g, b, a = rgba
    oa = buf[i + 3] / 255.0
    na = a / 255.0
    out_a = na + oa * (1 - na)
    if out_a <= 0:
        return
    buf[i] = int((r * na + buf[i] * oa * (1 - na)) / out_a)
    buf[i + 1] = int((g * na + buf[i + 1] * oa * (1 - na)) / out_a)
    buf[i + 2] = int((b * na + buf[i + 2] * oa * (1 - na)) / out_a)
    buf[i + 3] = int(out_a * 255)


def lerp(a, b, t):
    return a + (b - a) * t


def lerp_rgb(c0, c1, t):
    t = max(0.0, min(1.0, t))
    return tuple(int(lerp(c0[i], c1[i], t)) for i in range(3))


def lighten(rgb, amount):
    return tuple(max(0, min(255, int(lerp(c, 255, amount)))) for c in rgb)


def shade(rgb, factor):
    return tuple(max(0, min(255, int(c * factor))) for c in rgb)


def rainbow_at(t: float):
    t = max(0.0, min(1.0, t))
    segs = len(RAINBOW) - 1
    x = t * segs
    i = min(segs - 1, int(x))
    return lerp_rgb(RAINBOW[i], RAINBOW[i + 1], x - i)


def draw_full_width_segment(buf, w, h, y, bh, t0: float, t1: float, intensity=1.0):
    """Draw one LED row spanning the entire cell width — no vertical gaps."""
    for px in range(w):
        t = lerp(t0, t1, px / max(1, w - 1))
        rgb = rainbow_at(t)
        body = shade(rgb, 0.9)
        hi = lighten(rgb, 0.4)
        for py in range(bh):
            if py <= 1:
                col = (*hi, int(210 * intensity))
            elif py >= bh - 2:
                col = (*shade(rgb, 0.72), int(210 * intensity))
            else:
                col = (*body, int(235 * intensity))
            set_px(buf, w, h, px, y + py, col)


def paint_cell_frame(cell: int, cubes: int) -> bytearray:
    """One 80×80 Mezon cell: solid full-width column, continuous rainbow slice."""
    w = h = FRAME
    buf = bytearray(w * h * 4)  # transparent; only LED rows are opaque
    cubes = max(1, min(HEIGHTS, cubes))
    base_y = h  # sit flush on bottom edge

    # Hue range owned by this cell — abuts neighbors so Mezon gap-2 still looks continuous.
    cell_t0 = cell / COLS
    cell_t1 = (cell + 1) / COLS

    for k in range(cubes):
        y = base_y - (k + 1) * PITCH
        if y < 0:
            break
        intensity = 0.78 + 0.22 * (k / max(1, cubes - 1))
        draw_full_width_segment(buf, w, h, y, SEG_H, cell_t0, cell_t1, intensity)

    return buf


def height_at(step: int, col: int) -> int:
    t = step / STEPS * math.tau
    phase = col * 0.7
    wave = (
        0.42 * math.sin(t * 2.2 + phase)
        + 0.30 * math.sin(t * 3.8 + phase * 1.5)
        + 0.18 * math.sin(t * 5.5 + phase * 0.7)
        + 0.10 * math.sin(t * 7.5 + phase * 2.0)
        + 0.08 * math.sin(t * 2.8 + (col + 0.5) * 0.7)
    )
    wave = max(-1.0, min(1.0, wave))
    level = 0.05 + 0.95 * ((wave + 1) * 0.5)
    return max(1, min(HEIGHTS, int(round(level * HEIGHTS))))


def main() -> None:
    tile_n = COLS * HEIGHTS
    sheet_w = FRAME * tile_n
    sheet_h = FRAME
    sheet = bytearray(sheet_w * sheet_h * 4)
    frames: dict = {}

    print(f"cells={COLS} frame={FRAME}x{FRAME} (no inner vertical gaps)")
    for c in range(COLS):
        for hi, height in enumerate(range(1, HEIGHTS + 1)):
            tile = paint_cell_frame(c, height)
            idx = c * HEIGHTS + hi
            for y in range(FRAME):
                src = y * FRAME * 4
                dst = (y * sheet_w + idx * FRAME) * 4
                sheet[dst : dst + FRAME * 4] = tile[src : src + FRAME * 4]
            name = f"c{c}_h{height}.png"
            frames[name] = {
                "frame": {"x": idx * FRAME, "y": 0, "w": FRAME, "h": FRAME},
                "rotated": False,
                "trimmed": False,
                "spriteSourceSize": {"x": 0, "y": 0, "w": FRAME, "h": FRAME},
                "sourceSize": {"w": FRAME, "h": FRAME},
            }
        print(f"cell {c + 1}/{COLS}")

    pool = [[f"c{c}_h{height_at(s, c)}.png" for s in range(STEPS)] for c in range(COLS)]

    write_png(OUT / "equalizer.png", sheet_w, sheet_h, sheet)
    meta = {
        "app": "Mezube",
        "version": "9.0-solid-cell-80",
        "image": "equalizer.png",
        "format": "RGBA8888",
        "size": {"w": sheet_w, "h": sheet_h},
        "scale": "1",
    }
    (OUT / "equalizer.json").write_text(json.dumps({"frames": frames, "meta": meta}, indent=2), encoding="utf-8")
    (OUT / "equalizer_pool.json").write_text(json.dumps(pool), encoding="utf-8")

    # Preview: stitch cells with Mezon-like 8px gap (gap-2) so preview matches UI
    mez_gap = 8
    pw = FRAME * COLS + mez_gap * (COLS - 1)
    prev = bytearray(pw * FRAME * 4)  # transparent gaps
    x = 0
    for c in range(COLS):
        tile = paint_cell_frame(c, 6)
        for y in range(FRAME):
            dst = (y * pw + x) * 4
            prev[dst : dst + FRAME * 4] = tile[y * FRAME * 4 : (y + 1) * FRAME * 4]
        x += FRAME + mez_gap
    write_png(OUT / "preview_frame0.png", pw, FRAME, prev)
    print(f"wrote equalizer.png ({(OUT / 'equalizer.png').stat().st_size} bytes)")
    print(f"preview={pw}x{FRAME} (includes {mez_gap}px Mezon gaps)")


if __name__ == "__main__":
    main()
