"""Resume Relay's icon, drawn large and sharp (the same design as the Windows app icon): an amber arrow going round a
play button, on navy. Writes docs/brand/:
  resume-relay-icon-1024.png / -512.png   full square (Discord crops it to a circle; everything sits inside that circle)
  resume-relay-icon-rounded-1024.png      a rounded-square tile with see-through corners (app icons, the web page)
"""
import math, os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "docs/brand")
NAVY, AMBER, PLAY = (30, 34, 64), (242, 180, 74), (236, 234, 248)

def draw(size, rounded):
    k = 4                                   # drawn 4x, then scaled down, for smooth edges
    S = size * k
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    if rounded: d.rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=NAVY)
    else: d.rectangle([0, 0, S, S], fill=NAVY)
    cx = cy = S / 2
    r, w = S * 0.285, S * 0.07                 # the arrow's circle and thickness
    start, end = -58, 247                      # degrees, clockwise from 3 o'clock: a gap at the top right
    d.arc([cx - r - w / 2, cy - r - w / 2, cx + r + w / 2, cy + r + w / 2], start=start, end=end, fill=AMBER, width=int(w))
    # Rounded end at the tail.
    a = math.radians(start)
    tx, ty = cx + r * math.cos(a), cy + r * math.sin(a)
    d.ellipse([tx - w / 2, ty - w / 2, tx + w / 2, ty + w / 2], fill=AMBER)
    # The arrowhead at the other end, pointing on round the circle (clockwise).
    a = math.radians(end)
    hx, hy = cx + r * math.cos(a), cy + r * math.sin(a)
    tang = (-math.sin(a), math.cos(a))         # clockwise direction
    norm = (math.cos(a), math.sin(a))
    L, B = w * 2.0, w * 1.45
    tip = (hx + tang[0] * L * 0.9, hy + tang[1] * L * 0.9)
    left = (hx + norm[0] * B - tang[0] * L * 0.1, hy + norm[1] * B - tang[1] * L * 0.1)
    right = (hx - norm[0] * B - tang[0] * L * 0.1, hy - norm[1] * B - tang[1] * L * 0.1)
    d.polygon([tip, left, right], fill=AMBER)
    # The play button, a touch right of centre so it looks centred.
    p = S * 0.155
    d.polygon([(cx - p * 0.62, cy - p), (cx - p * 0.62, cy + p), (cx + p * 1.05, cy)], fill=PLAY)
    return im.resize((size, size), Image.LANCZOS)

os.makedirs(OUT, exist_ok=True)
draw(1024, False).convert("RGB").save(os.path.join(OUT, "resume-relay-icon-1024.png"))
draw(512, False).convert("RGB").save(os.path.join(OUT, "resume-relay-icon-512.png"))
draw(1024, True).save(os.path.join(OUT, "resume-relay-icon-rounded-1024.png"))
print("icons in", OUT)
