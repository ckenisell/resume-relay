"""Makes the screen apps' demo slides (original artwork, invented titles) for store reviewers and anyone trying the
app without Resume Relay. Each slide is one of the built-in marquee backgrounds with a made-up poster behind its
window, at 720x1280 (a third of the 4K canvas: poster 667x1000 at 27,250).

    python3 tools/make_demo.py      (writes apps/roku/images/demo/ and the Android app's assets/demo/)
"""
import math, os, random
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BG = os.path.join(ROOT, "src/ResumeRelay.Core/Live/Backgrounds")
OUT = [os.path.join(ROOT, "apps/roku/images/demo"), os.path.join(ROOT, "apps/android/app/src/main/assets/demo")]
W, H, PX, PY, PW, PH = 720, 1280, 27, 250, 667, 1000
SERIF = "/usr/share/fonts/truetype/dejavu/DejaVuSerif-Bold.ttf"
SANS = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
SANS_L = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"

def gradient(top, bottom):
    img = Image.new("RGB", (PW, PH))
    d = ImageDraw.Draw(img)
    for y in range(PH):
        t = y / PH
        d.line([(0, y), (PW, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(top, bottom)))
    return img

def text_center(d, y, s, font, fill, spacing=0):
    w = d.textlength(s, font=font) + spacing * (len(s) - 1)
    x = (PW - w) / 2
    for ch in s:
        d.text((x, y), ch, font=font, fill=fill)
        x += d.textlength(ch, font=font) + spacing

def billing(d, y, fill):
    f = ImageFont.truetype(SANS_L, 13)
    for line in ["A RESUME RELAY DEMO PICTURE", "AN INVENTED TITLE  ·  FOR SHOWING HOW LIVE POSTER LOOKS"]:
        text_center(d, y, line, f, fill, 1); y += 20

def poster(seed, title, tagline, top, bottom, accent, scene):
    rnd = random.Random(seed)
    img = gradient(top, bottom)
    d = ImageDraw.Draw(img, "RGBA")
    if scene in ("stars", "canyon"):
        for _ in range(160):
            x, y, r = rnd.randrange(PW), rnd.randrange(int(PH * .55)), rnd.choice([1, 1, 1, 2])
            d.ellipse([x - r, y - r, x + r, y + r], fill=(255, 255, 240, rnd.randrange(120, 255)))
    if scene == "canyon":
        for i in range(5):   # falling stars
            x, y = rnd.randrange(80, PW - 80), rnd.randrange(60, 380)
            d.line([(x, y), (x - 90, y - 50)], fill=(255, 230, 180, 200), width=3)
        pts = [(0, 640)] + [(x, 600 - 140 * abs(math.sin(x / 90)) - rnd.randrange(30)) for x in range(0, PW + 40, 40)] + [(PW, PH), (0, PH)]
        d.polygon(pts, fill=(60, 24, 20))
        d.polygon([(0, 760), (220, 690), (420, 760), (PW, 700), (PW, PH), (0, PH)], fill=(35, 14, 12))
    if scene == "sun":
        cx, cy = PW // 2, 470
        for r in range(260, 120, -20):
            d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=accent + (18,))
        d.ellipse([cx - 120, cy - 120, cx + 120, cy + 120], fill=accent + (255,))
        for i, y0 in enumerate(range(560, 760, 40)):
            d.rectangle([0, y0, PW, y0 + 14 + i * 3], fill=bottom + (255,))
        for row in range(4):   # orchard rows
            for k in range(9):
                x = 40 + k * 75 + (row % 2) * 35; y = 790 + row * 55
                d.ellipse([x - 26 - row * 4, y - 26 - row * 4, x + 26 + row * 4, y + 22 + row * 4], fill=(20, 60 + row * 8, 30, 255))
    if scene == "harbor":
        for i in range(14):
            x = 30 + i * 47; h = rnd.randrange(120, 360)
            d.rectangle([x, 640 - h, x + 36, 640], fill=(20, 40, 60, 255))
            for wy in range(640 - h + 12, 630, 22):
                if rnd.random() < .5: d.rectangle([x + 8, wy, x + 14, wy + 8], fill=accent + (230,))
        for y in range(650, 900, 8):
            d.line([(rnd.randrange(-50, 100), y), (PW - rnd.randrange(0, 150), y)], fill=accent + (rnd.randrange(30, 90),), width=2)
    if scene == "key":
        cx, cy = PW // 2, 470
        d.ellipse([cx - 110, cy - 230, cx + 110, cy - 10], outline=accent + (255,), width=34)
        d.rectangle([cx - 17, cy - 20, cx + 17, cy + 330], fill=accent + (255,))
        d.rectangle([cx + 17, cy + 230, cx + 90, cy + 265], fill=accent + (255,))
        d.rectangle([cx + 17, cy + 295, cx + 70, cy + 330], fill=accent + (255,))
        img = img.filter(ImageFilter.GaussianBlur(0.6)); d = ImageDraw.Draw(img, "RGBA")
    if scene == "ridge":
        for layer in range(5):
            base = 420 + layer * 90
            col = tuple(int(c * (1 - layer * .15)) for c in accent)
            pts = [(0, PH)] + [(x, base - 80 * math.sin(x / (70 + layer * 25) + layer) - rnd.randrange(20)) for x in range(0, PW + 30, 30)] + [(PW, PH)]
            d.polygon(pts, fill=col + (255,))
    if scene == "lantern":
        for _ in range(26):
            x, y, s = rnd.randrange(40, PW - 40), rnd.randrange(80, 760), rnd.randrange(14, 40)
            d.ellipse([x - s * 2, y - s * 2, x + s * 2, y + s * 2], fill=accent + (25,))
            d.rounded_rectangle([x - s * .6, y - s, x + s * .6, y + s], radius=int(s * .4), fill=accent + (235,))
    d.rectangle([0, 820, PW, PH], fill=(0, 0, 0, 120))
    # The title as big as fits (up to 72 px), then the tagline and the small print under it.
    size = 72
    while size > 30 and d.textlength(title, font=ImageFont.truetype(SERIF, size)) + 4 * (len(title) - 1) > PW - 70: size -= 2
    tf = ImageFont.truetype(SERIF, size)
    top = 875 - size // 2
    text_center(d, top, title, tf, (250, 244, 230), 4)
    text_center(d, top + size + 16, tagline, ImageFont.truetype(SANS, 17), accent, 3)
    billing(d, top + size + 52, (200, 196, 190))
    return img

SLIDES = [
    ("nowshowing", poster(1, "STARFALL CANYON", "ONE NIGHT. EVERY WISH.", (12, 14, 40), (110, 50, 60), (255, 190, 120), "canyon")),
    ("comingsoon", poster(2, "THE COPPER KEY", "SOME DOORS STAY CLOSED", (40, 20, 12), (12, 8, 6), (210, 130, 60), "key")),
    ("recent", poster(3, "ORCHARD ROAD", "SUMMER IS SWEETER HERE", (250, 150, 90), (120, 60, 70), (255, 220, 120), "sun")),
    ("acclaimed", poster(4, "GLASS HARBOR", "THE CITY NEVER SLEEPS", (6, 16, 30), (8, 30, 50), (120, 200, 255), "harbor")),
    ("comingsoon", poster(5, "ECHO RIDGE", "LISTEN TO THE MOUNTAIN", (230, 140, 90), (30, 20, 40), (120, 80, 110), "ridge")),
    ("recent", poster(6, "LANTERN SEASON", "LIGHT THE WAY HOME", (20, 10, 40), (60, 20, 50), (255, 180, 80), "lantern")),
    ("acclaimed", poster(7, "NIGHT OF FALLING STARS", "A FESTIVAL FAVORITE", (5, 5, 20), (40, 30, 80), (200, 180, 255), "stars")),
]

for o in OUT: os.makedirs(o, exist_ok=True)
for i, (bg, p) in enumerate(SLIDES, 1):
    back = Image.open(os.path.join(BG, bg + ".png")).convert("RGBA").resize((W, H), Image.LANCZOS)
    canvas = Image.new("RGBA", (W, H), (0, 0, 0, 255))
    canvas.paste(p, (PX, PY))
    canvas = Image.alpha_composite(canvas, back).convert("RGB")
    for o in OUT: canvas.save(os.path.join(o, f"{i}.jpg"), quality=86, optimize=True)
print("made", len(SLIDES), "slides")
