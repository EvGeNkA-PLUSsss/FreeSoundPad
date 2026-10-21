"""Генерирует Assets/app.ico для FreeSoundPad (тёмная плитка + мятный эквалайзер)."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "FreeSoundPad.App" / "Assets"
ASSETS.mkdir(parents=True, exist_ok=True)

SIZE = 256
img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# rounded dark tile
margin = 10
radius = 54
d.rounded_rectangle(
    (margin, margin, SIZE - margin, SIZE - margin),
    radius=radius,
    fill=(9, 13, 20, 255),
    outline=(63, 224, 176, 255),
    width=6,
)

# mint equalizer bars
bars = [0.30, 0.52, 0.80, 0.56, 0.36]
bar_w = 26
gap = 12
total = len(bars) * bar_w + (len(bars) - 1) * gap
start_x = (SIZE - total) // 2
for i, h in enumerate(bars):
    height = int((SIZE - 2 * margin) * 0.62 * h)
    x = start_x + i * (bar_w + gap)
    y = (SIZE - height) // 2
    d.rounded_rectangle((x, y, x + bar_w, y + height), radius=bar_w // 2, fill=(63, 224, 176, 255))

img.save(ASSETS / "app-icon.png")
img.save(ASSETS / "app.ico", format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("written:", ASSETS / "app.ico", (ASSETS / "app.ico").stat().st_size, "bytes")
