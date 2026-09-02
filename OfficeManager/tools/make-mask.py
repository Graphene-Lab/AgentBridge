"""Generate assets/office-mask.js — the walkable floor mask for the office game.

Run once from the OfficeManager folder:
    python tools/make-mask.py

The browser cannot read the PNG pixels from file:// (canvas tainting), so the
walkable mask is precomputed here from office-ground.png (alpha > 40) and
embedded in office-mask.js as bit-packed base64.
"""
import base64
from pathlib import Path
from PIL import Image

ASSETS = Path(__file__).resolve().parent.parent / "assets"
ground = Image.open(ASSETS / "office-ground.png").convert("RGBA")
W, H = ground.size
px = ground.load()

bits = bytearray((W * H + 7) >> 3)
for y in range(H):
    for x in range(W):
        if px[x, y][3] > 40:
            bits[(y * W + x) >> 3] |= 1 << (7 - ((y * W + x) & 7))

b64 = base64.b64encode(bytes(bits)).decode()
with open(ASSETS / "office-mask.js", "w", encoding="ascii") as f:
    f.write("// Walkable floor mask for office-ground.png (1 bit per pixel, row-major, base64).\n")
    f.write("// Generated from office-ground.png alpha>40; see tools/make-mask.py.\n")
    f.write(f'const OFFICE_MASK_B64 = "{b64}";\n')
    f.write(f"const OFFICE_MASK_W = {W};\nconst OFFICE_MASK_H = {H};\n")
print(f"wrote office-mask.js ({W}x{H}, {len(b64)} bytes base64)")
