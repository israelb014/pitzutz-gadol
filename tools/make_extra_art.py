#!/usr/bin/env python3
"""Small pictures added after the main art set. Run after tools/make_art.py."""
from pathlib import Path
from PIL import Image, ImageDraw
out = Path(__file__).resolve().parent.parent / 'Assets/Resources/Art'
S = 4; n = 192 * S
im = Image.new('RGBA', (n, n), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
def arrow(o, fill):
    p = [(20, 72), (112, 72), (112, 36), (176, 96), (112, 156), (112, 120), (20, 120)]
    d.polygon([((x + o * 0) * S, y * S) for x, y in p], fill=fill)
arrow(0, (32, 40, 64, 255))
p = [(30, 82), (122, 82), (122, 58), (162, 96), (122, 134), (122, 110), (30, 110)]
d.polygon([(x * S, y * S) for x, y in p], fill=(255, 255, 255, 255))
im.resize((192, 192), Image.LANCZOS).save(out / 'wind_arrow.png')
