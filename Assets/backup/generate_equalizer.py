"""EQ atlas: denser bars + continuous rainbow across all columns.

Each pool cell contains SUB_BARS thin bars packed side-by-side.
Hues are assigned on a global index so color flows red→…→purple
across the whole viz (not one solid color per cell).
"""
from __future__ import annotations

import json
import math
import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent

COLS = 10          # pool columns (Mezon cells)
SUB_BARS = 3       # thin bars packed inside each cell
HEIGHTS = 10
# IMPORTANT: Mezon renders each animation cell at 80x80 in practice.
# Keep the atlas native to 80 so preview == actual UI.
FRAME = 80
GLOW = 1
INNER_GAP = 1      # gap between sub-bars inside one cell
STEPS = 24

TOTAL_BARS = COLS * SUB_BARS  # 30

RAINBOW = [
    (255, 40, 50),    # red
    (255, 130, 25),   # orange
    (255, 215, 35),   # yellow
    (35, 200, 75),    # green
    (35, 180, 200),   # cyan
    (35, 120, 255),   # blue
    (120, 70, 255),   # indigo
    (165, 55, 255),   # purple
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


def fill_rect(buf, w, h, x0, y0, x1, y1, rgba, radius=0):
    for y in range(y0, y1):
        for x in range(x0, x1):
            if radius > 0:
                if x < x0 + radius and y < y0 + radius:
                    if (x - (x0 + radius)) ** 2 + (y - (y0 + radius)) ** 2 > radius * radius:
                        continue
                elif x >= x1 - radius and y < y0 + radius:
                    if (x - (x1 - 1 - radius)) ** 2 + (y - (y0 + radius)) ** 2 > radius * radius:
                        continue
                elif x < x0 + radius and y >= y1 - radius:
                    if (x - (x0 + radius)) ** 2 + (y - (y1 - 1 - radius)) ** 2 > radius * radius:
                        continue
                elif x >= x1 - radius and y >= y1 - radius:
                    if (x - (x1 - 1 - radius)) ** 2 + (y - (y1 - 1 - radius)) ** 2 > radius * radius:
                        continue
            set_px(buf, w, h, x, y, rgba)


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


def bar_color(global_index: int) -> tuple[int, int, int]:
    return rainbow_at(global_index / max(1, TOTAL_BARS - 1))


def bar_geometry():
    """Usable width split into SUB_BARS with INNER_GAP between them."""
    usable = FRAME - 2 * GLOW
    total_gaps = INNER_GAP * (SUB_BARS - 1)
    bar_w = (usable - total_gaps) // SUB_BARS
    # leftover pixels distributed as side pad for centering
    used = bar_w * SUB_BARS + total_gaps
    pad = GLOW + (usable - used) // 2
    return pad, bar_w


def draw_segment_gradient(buf, w, h, x, y, bw, bh, t0: float, t1: float, intensity=1.0):
    """Horizontal rainbow slice across the segment (connects visually to neighbors)."""
    for px in range(bw):
        t = lerp(t0, t1, px / max(1, bw - 1))
        rgb = rainbow_at(t)
        body = shade(rgb, 0.9)
        hi = lighten(rgb, 0.4)
        core = shade(rgb, 0.55)
        for py in range(bh):
            # top highlight row
            if py <= 1:
                col = (*hi, int(200 * intensity))
            elif py >= bh - 2:
                col = (*shade(rgb, 0.7), int(200 * intensity))
            elif 2 <= py < bh - 2 and 1 <= px < bw - 1:
                col = (*core, int(160 * intensity)) if False else (*body, int(230 * intensity))
            else:
                col = (*body, int(230 * intensity))
            set_px(buf, w, h, x + px, y + py, col)
        # soft side bleed toward Mezon gap
        if px == 0 or px == bw - 1:
            for py in range(bh):
                set_px(buf, w, h, x + px, y + py, (*lighten(rainbow_at(t), 0.15), int(70 * intensity)))


def paint_cell_frame(cell: int, cubes: int) -> bytearray:
    """One pool cell: SUB_BARS thin bars; hues are a continuous slice of the rainbow."""
    w = h = FRAME
    buf = bytearray(w * h * 4)
    cubes = max(1, min(HEIGHTS, cubes))
    pad, bar_w = bar_geometry()
    seg_h = 6
    pitch = seg_h + 1
    base_y = h - GLOW

    # This cell owns hue range [cell/COLS, (cell+1)/COLS], split across SUB_BARS.
    cell_t0 = cell / COLS
    cell_t1 = (cell + 1) / COLS

    for s in range(SUB_BARS):
        t0 = lerp(cell_t0, cell_t1, s / SUB_BARS)
        t1 = lerp(cell_t0, cell_t1, (s + 1) / SUB_BARS)
        x = pad + s * (bar_w + INNER_GAP)
        for k in range(cubes):
            y = base_y - (k + 1) * pitch
            intensity = 0.78 + 0.22 * (k / max(1, cubes - 1))
            draw_segment_gradient(buf, w, h, x, y, bar_w, seg_h, t0, t1, intensity)

    return buf


def height_at(step: int, col: int) -> int:
    t = step / STEPS * math.tau
    phase = col * 0.7
    wave = (
        0.42 * math.sin(t * 2.2 + phase)
        + 0.30 * math.sin(t * 3.8 + phase * 1.5)
        + 0.18 * math.sin(t * 5.5 + phase * 0.7)
        + 0.10 * math.sin(t * 7.5 + phase * 2.0)
    )
    wave = max(-1.0, min(1.0, wave))
    # Neighbor phase for traveling wave across fine bars
    wave += 0.08 * math.sin(t * 2.8 + (col + 0.5) * 0.7)
    wave = max(-1.0, min(1.0, wave))
    level = 0.05 + 0.95 * ((wave + 1) * 0.5)
    return max(1, min(HEIGHTS, int(round(level * HEIGHTS))))


def main() -> None:
    tile_n = COLS * HEIGHTS
    sheet_w = FRAME * tile_n
    sheet_h = FRAME
    sheet = bytearray(sheet_w * sheet_h * 4)
    frames: dict = {}

    print(f"cells={COLS} sub_bars={SUB_BARS} total_bars={TOTAL_BARS} frame={FRAME}")
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
        "version": "8.0-dense-gradient",
        "image": "equalizer.png",
        "format": "RGBA8888",
        "size": {"w": sheet_w, "h": sheet_h},
        "scale": "1",
    }
    (OUT / "equalizer.json").write_text(json.dumps({"frames": frames, "meta": meta}, indent=2), encoding="utf-8")
    (OUT / "equalizer_pool.json").write_text(json.dumps(pool), encoding="utf-8")

    # Preview: stitch all cells at height 6
    pw = FRAME * COLS
    prev = bytearray(pw * FRAME * 4)
    for c in range(COLS):
        tile = paint_cell_frame(c, 6)
        for y in range(FRAME):
            prev[(y * pw + c * FRAME) * 4 : (y * pw + c * FRAME) * 4 + FRAME * 4] = (
                tile[y * FRAME * 4 : (y + 1) * FRAME * 4]
            )
    write_png(OUT / "preview_frame0.png", pw, FRAME, prev)
    print(f"wrote equalizer.png ({(OUT / 'equalizer.png').stat().st_size} bytes)")


if __name__ == "__main__":
    main()
