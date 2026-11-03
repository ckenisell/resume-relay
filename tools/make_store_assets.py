"""Store listing pictures for the screen apps (Amazon Appstore, Google Play, Roku), written to docs/store/.
Uses the apps' poster mark (navy, amber frame and play mark) and the demo slides (tools/make_demo.py).

    python3 tools/make_demo.py && python3 tools/make_store_assets.py
"""
import os, textwrap
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEMO = os.path.join(ROOT, "apps/roku/images/demo")
OUT = os.path.join(ROOT, "docs/store")
NAVY, AMBER, WINE, TEXT, MUTED = (21, 26, 46), (240, 180, 76), (123, 45, 63), (242, 239, 233), (200, 196, 216)
BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
REG = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
os.makedirs(OUT, exist_ok=True)

def mark(d, x, y, s):
    """The poster mark in a box s wide (as in the Android icon: a framed poster with a play triangle)."""
    k = s / 40
    d.rounded_rectangle([x, y, x + 40 * k, y + 52 * k], radius=4 * k, fill=AMBER)
    d.rectangle([x + 3 * k, y + 5 * k, x + 37 * k, y + 47 * k], fill=NAVY)
    d.polygon([(x + 15 * k, y + 16 * k), (x + 29 * k, y + 26 * k), (x + 15 * k, y + 36 * k)], fill=AMBER)

def icon(size):
    """The tablet icons: "Resume Relay" across the top, the poster mark, "Live Poster" across the bottom (the TV
    banner's colors and font)."""
    im = Image.new("RGB", (size, size), NAVY); d = ImageDraw.Draw(im)
    f = ImageFont.truetype(BOLD, int(size * 0.15))   # one size for both lines: as big as "Resume Relay" fits
    while d.textlength("Resume Relay", font=f) > size * 0.88: f = ImageFont.truetype(BOLD, f.size - 1)
    def line(text, y, fill):
        d.text(((size - d.textlength(text, font=f)) / 2, y), text, font=f, fill=fill)
    line("Resume Relay", size * 0.06, TEXT)
    s = size * 0.36; mark(d, (size - s) / 2, size * 0.27, s)
    line("Live Poster", size * 0.78, AMBER)
    return im

def wrap(d, x, y, text, font, fill, width, gap=10):
    for line in textwrap.wrap(text, width):
        d.text((x, y), line, font=font, fill=fill); y += font.size + gap
    return y

def slide(i, h):
    im = Image.open(os.path.join(DEMO, f"{i}.jpg")); return im.resize((round(h * 720 / 1280), h), Image.LANCZOS)

def poster(w, h):
    """The Roku store's app poster: the tablet icons' layout on a wider card."""
    im = Image.new("RGB", (w, h), NAVY); d = ImageDraw.Draw(im)
    f = ImageFont.truetype(BOLD, int(h * 0.15))
    while d.textlength("Resume Relay", font=f) > w * 0.84: f = ImageFont.truetype(BOLD, f.size - 1)
    for text, y, fill in (("Resume Relay", h * 0.07, TEXT), ("Live Poster", h * 0.77, AMBER)):
        d.text(((w - d.textlength(text, font=f)) / 2, y), text, font=f, fill=fill)
    s = h * 0.36; mark(d, (w - s) / 2, h * 0.28, s)
    return im

# Icons
poster(540, 405).save(os.path.join(OUT, "roku-poster-540x405.png"))
icon(512).save(os.path.join(OUT, "icon-512.png"))
icon(114).save(os.path.join(OUT, "icon-114.png"))

# Android TV / Fire TV banner and Google Play feature graphic: the mark and the name.
for name, (w, h) in {"tv-banner-1280x720.png": (1280, 720), "feature-1024x500.png": (1024, 500), "firetv-background-1920x1080.png": (1920, 1080)}.items():
    im = Image.new("RGB", (w, h), NAVY); d = ImageDraw.Draw(im)
    mark(d, w * 0.08, h * 0.2, h * 0.46)
    d.text((w * 0.08 + h * 0.6, h * 0.30), "Resume Relay", font=ImageFont.truetype(BOLD, int(h * 0.13)), fill=TEXT)
    d.text((w * 0.08 + h * 0.6, h * 0.48), "Live Poster", font=ImageFont.truetype(BOLD, int(h * 0.11)), fill=AMBER)
    d.rectangle([0, h - h * 0.035, w, h], fill=WINE)
    im.save(os.path.join(OUT, name))

# Screenshots (1920×1080): what the demo shows on a TV standing normally, and the pairing screen.
captions = {1: "Now Playing: the poster of what's on in your theater", 3: "Recently Watched", 4: "Highly Acclaimed", 2: "Coming Soon"}
for n, i in enumerate([1, 2, 3, 4], 1):
    im = Image.new("RGB", (1920, 1080), (0, 0, 0)); d = ImageDraw.Draw(im)
    p = slide(i, 1080); im.paste(p, ((1920 - p.width) // 2, 0))
    d.text((70, 330), "Resume Relay", font=ImageFont.truetype(BOLD, 44), fill=TEXT)
    d.text((70, 385), "Live Poster", font=ImageFont.truetype(BOLD, 44), fill=AMBER)
    wrap(d, 70, 480, captions[i] + ".", ImageFont.truetype(REG, 30), MUTED, 30)
    wrap(d, 1320, 330, "Hang a TV on its side and each poster fills the screen, in 4K.", ImageFont.truetype(REG, 30), MUTED, 30)
    wrap(d, 1320, 520, "Posters shown are invented, from the app's demo.", ImageFont.truetype(REG, 22), (156, 153, 181), 36)
    im.save(os.path.join(OUT, f"screenshot-{n}.png"))
im = Image.new("RGB", (1920, 1080), NAVY); d = ImageDraw.Draw(im)
f = ImageFont.truetype(BOLD, 56); t = "Add this screen"; d.text(((1920 - d.textlength(t, font=f)) / 2, 330), t, font=f, fill=TEXT)
f = ImageFont.truetype(BOLD, 150); t = "4 7 1 9"; d.text(((1920 - d.textlength(t, font=f)) / 2, 420), t, font=f, fill=AMBER)
f = ImageFont.truetype(REG, 34)
for k, line in enumerate(textwrap.wrap("On a computer or phone, open Resume Relay, go to the Live Poster tab, and enter this code.", 60)):
    d.text(((1920 - d.textlength(line, font=f)) / 2, 640 + k * 48), line, font=f, fill=MUTED)
im.save(os.path.join(OUT, "screenshot-5.png"))
print("store pictures in", OUT)
