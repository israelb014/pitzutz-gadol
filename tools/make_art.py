#!/usr/bin/env python3
"""make_art.py - draws ALL 2D art for pitzutz-gadol (see tools/ART_SPEC.md).

Run from the project root:  python3 tools/make_art.py [substring ...]
With substrings only matching sprites are saved (handy while iterating).
100% procedural (pycairo + PIL), deterministic, no third-party art.
Light always comes from the top left. Sprites are drawn at 4x and box-downscaled.
"""
import math
import os
import random
import sys

import cairo
from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ART = os.path.join(ROOT, "Assets", "Resources", "Art")
ICONS = os.path.join(ROOT, "Assets", "Icons")
VERIF = os.path.join(ROOT, "verification")
SS = 4
THEMES = ("city", "desert", "factory", "fortress")
TAU = math.pi * 2


# ------------------------------------------------------------------ colour
def H(s, a=None):
    s = s.lstrip("#")
    return tuple(int(s[i:i + 2], 16) / 255 for i in (0, 2, 4))


def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


WHITE = (1, 1, 1)
INK = H("#2b2a3d")
COOL = H("#231f4a")


def lt(c, t=.2):
    return mix(c, H("#fff6e0"), t)


def dk(c, t=.2):
    return mix(c, COOL, t)


# ------------------------------------------------------------------ canvas
class Cv:
    def __init__(self, w, h, ss=SS):
        self.w, self.h, self.ss = w, h, ss
        self.surf = cairo.ImageSurface(cairo.FORMAT_ARGB32, w * ss, h * ss)
        c = cairo.Context(self.surf)
        c.scale(ss, ss)
        c.set_line_join(cairo.LINE_JOIN_ROUND)
        c.set_line_cap(cairo.LINE_CAP_ROUND)
        self.c = c

    def big(self):
        self.surf.flush()
        return Image.frombuffer("RGBA", (self.w * self.ss, self.h * self.ss), bytes(self.surf.get_data()),
                                "raw", "BGRa", self.surf.get_stride(), 1)

    def image(self, outline=0, ol=INK):
        im = self.big()
        if outline:
            im = add_outline(im, outline * self.ss, ol)
        if self.ss == 1:
            return im.copy()
        return im.resize((self.w, self.h), Image.BOX)


def add_outline(im, width, col=INK):
    """Constant-width rounded silhouette outline made from a thresholded alpha blur."""
    a = im.getchannel("A")
    b = a.filter(ImageFilter.GaussianBlur(width * .6))
    m = b.point(lambda v: 0 if v < 7 else (255 if v > 17 else (v - 7) * 25))
    m = ImageChops.lighter(m, a)
    base = Image.new("RGBA", im.size, tuple(int(x * 255) for x in col) + (0,))
    base.putalpha(m)
    return Image.alpha_composite(base, im)


def setc(c, col, a=1.0):
    c.set_source_rgba(col[0], col[1], col[2], a)


def lin(x0, y0, x1, y1, *stops):
    g = cairo.LinearGradient(x0, y0, x1, y1)
    for s in stops:
        g.add_color_stop_rgba(s[0], s[1][0], s[1][1], s[1][2], s[2] if len(s) > 2 else 1)
    return g


def rad(x, y, r0, r1, *stops, fx=None, fy=None):
    g = cairo.RadialGradient(x if fx is None else fx, y if fy is None else fy, r0, x, y, r1)
    for s in stops:
        g.add_color_stop_rgba(s[0], s[1][0], s[1][1], s[1][2], s[2] if len(s) > 2 else 1)
    return g


# ------------------------------------------------------------------ paths (callables taking a context)
def E(cx, cy, rx, ry=None, rot=0):
    ry = rx if ry is None else ry

    def p(c):
        c.save()
        c.translate(cx, cy)
        if rot:
            c.rotate(rot)
        c.scale(rx, ry)
        c.new_sub_path()
        c.arc(0, 0, 1, 0, TAU)
        c.close_path()
        c.restore()
    return p


def R(x, y, w, h, r=6):
    rs = r if isinstance(r, (tuple, list)) else (r, r, r, r)

    def p(c):
        tl, tr, br, bl = rs
        c.new_sub_path()
        c.arc(x + w - tr, y + tr, tr, -math.pi / 2, 0)
        c.arc(x + w - br, y + h - br, br, 0, math.pi / 2)
        c.arc(x + bl, y + h - bl, bl, math.pi / 2, math.pi)
        c.arc(x + tl, y + tl, tl, math.pi, math.pi * 1.5)
        c.close_path()
    return p


def L(pts, closed=True):
    def p(c):
        c.new_sub_path()
        c.move_to(*pts[0])
        for q in pts[1:]:
            c.line_to(*q)
        if closed:
            c.close_path()
    return p


def S(pts, closed=True, k=1 / 6):
    """Smooth Catmull-Rom spline through pts."""
    n = len(pts)

    def p(c):
        c.new_sub_path()
        c.move_to(*pts[0])
        rng = range(n if closed else n - 1)
        for i in rng:
            if closed:
                p0, p1, p2, p3 = pts[(i - 1) % n], pts[i], pts[(i + 1) % n], pts[(i + 2) % n]
            else:
                p0, p1, p2, p3 = pts[max(i - 1, 0)], pts[i], pts[i + 1], pts[min(i + 2, n - 1)]
            c.curve_to(p1[0] + (p2[0] - p0[0]) * k, p1[1] + (p2[1] - p0[1]) * k,
                       p2[0] - (p3[0] - p1[0]) * k, p2[1] - (p3[1] - p1[1]) * k, p2[0], p2[1])
        if closed:
            c.close_path()
    return p


def multi(*ps):
    def p(c):
        for q in ps:
            q(c)
    return p


def fill(c, path, col, a=1.0):
    c.new_path()
    path(c)
    if isinstance(col, cairo.Pattern):
        c.set_source(col)
    else:
        setc(c, col, a)
    c.fill()


def stroke(c, path, col=INK, lw=2.5, a=1.0):
    c.new_path()
    path(c)
    setc(c, col, a)
    c.set_line_width(lw)
    c.stroke()


def line(c, pts, col=INK, lw=2.5, a=1.0, smooth=False):
    stroke(c, S(pts, closed=False) if smooth else L(pts, closed=False), col, lw, a)


def shape(c, path, col, lw=2.5, ol=INK, d=None, rim=.32, shade=1.0):
    """Soft-shaded filled path: stepped shade on the lower right, thin light rim on the upper left."""
    c.new_path()
    path(c)
    x0, y0, x1, y1 = c.path_extents()
    w, h = x1 - x0, y1 - y0
    if d is None:
        d = min(w, h) * .16
    c.save()
    c.new_path()
    path(c)
    c.clip()
    setc(c, dk(col, .34 * shade))
    c.paint()
    c.save()
    c.translate(-d * .45, -d * .55)
    fill(c, path, dk(col, .16 * shade))
    c.translate(-d * .4, -d * .45)
    fill(c, path, lin(x0, y0, x1, y1, (0, lt(col, .12)), (.7, col)))
    c.restore()
    if rim:
        c.push_group()
        setc(c, H("#fffbe8"))
        c.paint()
        c.set_operator(cairo.OPERATOR_DEST_OUT)
        c.translate(d * .2, d * .24)
        fill(c, path, WHITE)
        c.pop_group_to_source()
        c.paint_with_alpha(rim)
    c.restore()
    if lw:
        stroke(c, path, ol, lw)


def gloss(c, cx, cy, rx, ry, a=.55, rot=-.6):
    fill(c, E(cx, cy, rx, ry, rot), H("#fffdf0"), a)


def clip_to(c, path):
    c.new_path()
    path(c)
    c.clip()


def rot_about(c, ang, x, y):
    c.translate(x, y)
    c.rotate(ang)
    c.translate(-x, -y)


def soft_ellipse(c, cx, cy, rx, ry, col=COOL, a=.3):
    c.save()
    c.translate(cx, cy)
    c.scale(rx, ry)
    g = rad(0, 0, 0, 1, (0, col, a), (.55, col, a * .8), (1, col, 0))
    c.set_source(g)
    c.arc(0, 0, 1, 0, TAU)
    c.fill()
    c.restore()


# ------------------------------------------------------------------ output registry
OUT = {}
ICON_OUT = {}


def emit(name, im):
    OUT[name] = im


def done(cv, name, outline=0, ol=INK):
    OUT[name] = cv.image(outline, ol)


# ================================================================== floors
FLOOR = {
    "city": (H("#c9d2d3"), H("#bcc7cb")),
    "desert": (H("#f0dcae"), H("#e8d09c")),
    "factory": (H("#a3acb6"), H("#97a1ad")),
    "fortress": (H("#7d7088"), H("#72657e")),
}


def floor_city(c, base, seed):
    r = random.Random(seed)
    grout = dk(base, .09)
    fill(c, R(0, 0, 96, 96, 0), grout)
    for (x, y, w, h) in ((1, 1, 46, 46), (49, 1, 46, 46), (1, 49, 46, 46), (49, 49, 46, 46)):
        v = r.uniform(-.015, .015)
        col = lt(base, .05 + v) if v > 0 else dk(base, -v)
        fill(c, R(x, y, w, h, 5), lin(x, y, x + w, y + h, (0, lt(col, .08)), (1, col)))
        stroke(c, L([(x + 5, y + h - 1.2), (x + w - 5, y + h - 1.2)], closed=False), dk(base, .06), 1.4, .7)


def floor_desert(c, base, seed):
    r = random.Random(seed)
    fill(c, R(0, 0, 96, 96, 0), lin(0, 0, 96, 96, (0, lt(base, .08)), (1, base)))
    for i in range(3):
        y = 18 + i * 30 + r.uniform(-3, 3)
        x = r.uniform(4, 34)
        line(c, [(x, y), (x + 14, y - 3), (x + 28, y), (x + 42, y - 2)], dk(base, .09), 2, .7, True)
    for i in range(6):
        fill(c, E(r.uniform(6, 90), r.uniform(6, 90), r.uniform(1, 1.8)), dk(base, .14), .6)


def floor_factory(c, base, seed):
    fill(c, R(0, 0, 96, 96, 0), lin(0, 0, 96, 96, (0, lt(base, .07)), (1, base)))
    # calm tread plate
    for iy in range(4):
        for ix in range(4):
            x, y = 12 + ix * 24, 12 + iy * 24
            ang = .8 if (ix + iy) % 2 else -.8
            c.save()
            c.translate(x, y)
            c.rotate(ang)
            fill(c, R(-6, -1.5, 12, 3, 1.5), dk(base, .1))
            fill(c, R(-6, -2, 12, 2.2, 1.1), lt(base, .12), .7)
            c.restore()
    stroke(c, R(1, 1, 94, 94, 3), dk(base, .13), 2)
    stroke(c, R(2.5, 2.5, 91, 91, 2), lt(base, .16), 1)


def floor_fortress(c, base, seed):
    r = random.Random(seed)
    grout = dk(base, .16)
    fill(c, R(0, 0, 96, 96, 0), grout)
    stones = ((1, 1, 58, 30), (61, 1, 34, 30), (1, 33, 30, 30), (33, 33, 62, 30), (1, 65, 46, 30), (49, 65, 46, 30))
    for (x, y, w, h) in stones:
        v = r.uniform(0, .05)
        col = lt(base, v)
        fill(c, R(x, y, w, h, 6), lin(x, y, x + w, y + h, (0, lt(col, .07)), (1, col)))


FLOOR_FN = {"city": floor_city, "desert": floor_desert, "factory": floor_factory, "fortress": floor_fortress}


def make_floors():
    for t in THEMES:
        for i, s in enumerate("ab"):
            cv = Cv(96, 96)
            FLOOR_FN[t](cv.c, FLOOR[t][i], 11 + i)
            done(cv, f"floor_{t}_{s}")


# ================================================================== solid blocks (96x120: top face y<96, front face below)
def slab(c, top, front, x=2.5, y=2.5, w=91, h=115, fh=24, r=12, lw=4, seam=True):
    """Box with a top face and a 24 px front face, returns nothing. Lit from the top left."""
    body = R(x, y, w, h, r)
    ty = y + h - fh
    c.save()
    clip_to(c, body)
    fill(c, R(x - 2, y - 2, w + 4, h + 4, 0), lin(x, y, x + w, ty, (0, lt(top, .14)), (.55, top), (1, dk(top, .1))))
    fill(c, R(x - 2, ty, w + 4, fh + 4, 0), lin(0, ty, 0, ty + fh, (0, front), (1, dk(front, .24))))
    if seam:
        line(c, [(x, ty + 1.5), (x + w, ty + 1.5)], lt(top, .32), 2.6, .75)
        line(c, [(x, ty + 3.6), (x + w, ty + 3.6)], dk(front, .4), 2, .85)
    # light rim on the top face
    stroke(c, R(x + 2.4, y + 2.4, w - 4.8, h - 4.8, r - 2), lt(top, .5), 2.2, .45)
    c.restore()
    stroke(c, body, INK, lw)


def top_panel(c, col, x=15, y=15, w=66, h=66, r=8):
    """Recessed panel on a top face."""
    fill(c, R(x - 1.5, y - 1.5, w + 3, h + 3, r), dk(col, .28), .9)
    fill(c, R(x, y + .5, w, h, r), lin(x, y, x + w, y + h, (0, dk(col, .12)), (1, lt(col, .1))))
    line(c, [(x + r, y + h + 1.3), (x + w - r, y + h + 1.3)], lt(col, .45), 1.8, .7)


def rivet(c, x, y, r=3.2, col=H("#c7cfdb")):
    fill(c, E(x + .5, y + .8, r + .8), INK, .55)
    fill(c, E(x, y, r), rad(x - r * .3, y - r * .3, 0, r * 1.2, (0, lt(col, .5)), (1, dk(col, .25))))


def bricks(c, x, y, w, h, bw, bh, col, mortar, seed=3, r=1.5):
    rr = random.Random(seed)
    fill(c, R(x, y, w, h, 0), mortar)
    row = 0
    yy = y
    while yy < y + h:
        xx = x - (bw / 2 if row % 2 else 0)
        while xx < x + w:
            v = rr.uniform(-.1, .1)
            cc = lt(col, v) if v > 0 else dk(col, -v)
            fill(c, R(xx + .8, yy + .8, bw - 1.6, bh - 1.6, r), cc)
            xx += bw
        yy += bh
        row += 1


def wall_city(c):
    top, front = H("#62739b"), H("#47557c")
    slab(c, top, front)
    top_panel(c, top)
    # roof vent
    fill(c, E(49, 52, 19, 18), INK, .22)
    shape(c, E(47, 47, 18, 17), H("#a9b8d0"), lw=2.4, rim=.45)
    fill(c, E(47, 47, 10.5, 9.5), H("#4a5778"))
    for k in range(3):
        line(c, [(39 + 2, 43 + k * 4), (53, 43 + k * 4)], H("#a9b8d0"), 1.8)
    for (x, y) in ((11, 11), (85, 11), (11, 85), (85, 85)):
        rivet(c, x, y, 2.6)
    for x in (24, 48, 72):
        fill(c, R(x - 5, 102, 10, 9, 2), H("#ffd978"), .85)
        stroke(c, R(x - 5, 102, 10, 9, 2), INK, 1.6)


def edge_city(c):
    top, front = H("#a65445"), H("#7f3a38")
    fill(c, R(0, 0, 96, 96, 0), top)
    bricks(c, 0, 0, 96, 96, 32, 16, top, dk(top, .32), 5)
    fill(c, R(0, 0, 96, 96, 0), lin(0, 0, 96, 96, (0, H("#fff4dd"), .12), (1, COOL, .14)))
    fill(c, R(0, 96, 96, 24, 0), lin(0, 96, 0, 120, (0, front), (1, dk(front, .25))))
    bricks(c, 0, 99, 96, 21, 32, 11, front, dk(front, .35), 6)
    fill(c, R(0, 96, 96, 5, 0), H("#d9d3c6"))
    line(c, [(0, 97), (96, 97)], H("#fff8ea"), 1.4)
    line(c, [(0, 101.5), (96, 101.5)], INK, 2.4)
    line(c, [(0, 118.5), (96, 118.5)], INK, 3)


def block_city(c):
    # cardboard box with tape: light, soft, obviously breakable
    top, front = H("#ecc488"), H("#cf9c5e")
    slab(c, top, front, r=8, lw=3.6, seam=True)
    # flap seam + tape
    fill(c, R(39, 3, 18, 93, 0), H("#fbe7bd"), .9)
    line(c, [(39, 4), (39, 95)], H("#c9975b"), 1.5)
    line(c, [(57, 4), (57, 95)], H("#c9975b"), 1.5)
    line(c, [(48, 5), (48, 94)], H("#b98a52"), 1.8)
    fill(c, R(39, 96, 18, 21, 0), H("#efd3a0"), .9)
    # corrugation dashes + sticker
    for (x, y) in ((12, 18), (20, 74), (66, 14), (76, 72)):
        line(c, [(x, y), (x + 12, y)], H("#c79a60"), 2, .6)
    # fragile arrow mark on the front
    for x in (20, 76):
        line(c, [(x, 113), (x, 104)], H("#8d6238"), 2.4)
        line(c, [(x - 4, 107), (x, 102.5), (x + 4, 107)], H("#8d6238"), 2.4)


def wall_desert(c):
    top, front = H("#c77f47"), H("#9c5a34")
    slab(c, top, front, r=14)
    top_panel(c, top, 16, 16, 64, 64, 12)
    # carved sun spiral
    pts = []
    for i in range(38):
        a = i * .42
        rr = 3 + i * .62
        pts.append((48 + math.cos(a) * rr, 48 + math.sin(a) * rr))
    line(c, pts, dk(top, .38), 4.2, 1, True)
    line(c, [(p[0] - .9, p[1] + 1.2) for p in pts], lt(top, .35), 1.2, .6, True)
    for x in range(10, 90, 19):
        fill(c, L([(x, 104), (x + 7, 112), (x + 14, 104)]), dk(front, .32))


def edge_desert(c):
    r = random.Random(21)
    top, front = H("#a95f3b"), H("#7c4030")
    fill(c, R(0, 0, 96, 96, 0), lin(0, 0, 96, 96, (0, lt(top, .12)), (1, dk(top, .08))))
    # rock strata, tileable sideways
    for y, hh, v in ((10, 20, .1), (36, 15, -.08), (58, 24, .08), (0, 5, -.1), (84, 6, -.1)):
        col = lt(top, v) if v > 0 else dk(top, -v)
        fill(c, R(0, y, 96, hh, 0), col)
        line(c, [(0, y + hh), (24, y + hh + 2.5), (48, y + hh - 1), (72, y + hh + 2), (96, y + hh)], dk(top, .3), 2, .8, True)
    for i in range(7):
        x, y = r.uniform(8, 88), r.uniform(6, 88)
        line(c, [(x, y), (x + r.uniform(5, 11), y + r.uniform(-1, 1))], dk(top, .25), 1.8, .7)
    fill(c, R(0, 96, 96, 24, 0), lin(0, 96, 0, 120, (0, front), (1, dk(front, .3))))
    for x in (6, 34, 66):
        fill(c, L([(x, 120), (x + 11, 101), (x + 22, 120)]), dk(front, .22))
    line(c, [(0, 97.2), (96, 97.2)], lt(top, .3), 2.2)
    line(c, [(0, 99.4), (96, 99.4)], INK, 2.2)
    line(c, [(0, 118.5), (96, 118.5)], INK, 3)


def block_desert(c, light=0.0):
    # straw bale tied with twine
    top = lt(H("#e9b95a"), light)
    front = lt(H("#c98f3c"), light)
    slab(c, top, front, r=16, lw=3.6)
    r = random.Random(4)
    c.save()
    clip_to(c, R(3, 3, 90, 114, 15))
    for i in range(34):
        x, y = r.uniform(6, 86), r.uniform(8, 90)
        dx = r.uniform(8, 15)
        col = lt(top, .35) if i % 2 else dk(top, .25)
        line(c, [(x, y), (x + dx, y + r.uniform(-2.5, 2.5))], col, 1.8, .8)
    for i in range(10):
        x = r.uniform(6, 84)
        line(c, [(x, 103 + r.uniform(0, 9)), (x + 8, 103 + r.uniform(0, 9))], dk(front, .28), 1.6, .8)
    for x in (26, 64):
        fill(c, R(x, 0, 7, 120, 0), H("#8c5a2e"))
        line(c, [(x + 1.6, 3), (x + 1.6, 94)], H("#c28a4e"), 1.5)
    c.restore()


def wall_factory(c):
    top, front = H("#55617a"), H("#2f3648")
    slab(c, top, front, r=8)
    top_panel(c, top, 15, 15, 66, 66, 5)
    # cross brace
    line(c, [(19, 19), (77, 77)], lt(top, .22), 6)
    line(c, [(77, 19), (19, 77)], lt(top, .22), 6)
    line(c, [(19, 20.5), (77, 78.5)], dk(top, .3), 1.5, .7)
    line(c, [(77, 20.5), (19, 78.5)], dk(top, .3), 1.5, .7)
    shape(c, E(48, 48, 10), H("#8e9bb3"), lw=2.2)
    for (x, y) in ((9.5, 9.5), (86.5, 9.5), (9.5, 86.5), (86.5, 86.5), (48, 9.5), (48, 86.5), (9.5, 48), (86.5, 48)):
        rivet(c, x, y, 2.6)
    c.save()
    clip_to(c, R(4, 99, 88, 16, 0))
    fill(c, R(0, 99, 96, 18, 0), H("#f2b632"))
    for i in range(-1, 8):
        x = i * 16
        fill(c, L([(x, 118), (x + 8, 118), (x + 22, 98), (x + 14, 98)]), H("#2f3648"))
    c.restore()
    line(c, [(4, 115.5), (92, 115.5)], INK, 2)


def edge_factory(c):
    top, front = H("#3f4a60"), H("#262c3c")
    fill(c, R(0, 0, 96, 96, 0), lin(0, 0, 96, 96, (0, lt(top, .1)), (1, dk(top, .08))))
    fill(c, R(0, 30, 96, 36, 0), dk(top, .22))
    line(c, [(0, 29), (96, 29)], lt(top, .25), 2.4)
    line(c, [(0, 67), (96, 67)], lt(top, .18), 2)
    # pipe along the wall
    fill(c, R(0, 38, 96, 20, 0), lin(0, 38, 0, 58, (0, H("#c0cada")), (.45, H("#8796ad")), (1, H("#4a5670"))))
    line(c, [(0, 37.5), (96, 37.5)], INK, 2)
    line(c, [(0, 58.5), (96, 58.5)], INK, 2)
    fill(c, R(38, 34, 20, 28, 3), lin(38, 0, 58, 0, (0, H("#f5c94a")), (1, H("#c78e22"))))
    stroke(c, R(38, 34, 20, 28, 3), INK, 2.2)
    for x in (12, 84):
        rivet(c, x, 15, 3)
        rivet(c, x, 80, 3)
    fill(c, R(0, 96, 96, 24, 0), lin(0, 96, 0, 120, (0, front), (1, dk(front, .3))))
    for x in (12, 48, 84):
        rivet(c, x, 108, 2.6, H("#8e9bb3"))
    line(c, [(0, 97.2), (96, 97.2)], lt(top, .35), 2.2)
    line(c, [(0, 99.4), (96, 99.4)], INK, 2.2)
    line(c, [(0, 118.5), (96, 118.5)], INK, 3)


def block_factory(c, eyes=False):
    # wooden crate: planks, braces, steel corners
    top, front = H("#dea866"), H("#b67c42")
    slab(c, top, front, r=7, lw=3.6)
    for x in (24, 48, 72):
        line(c, [(x, 15), (x, 83)], H("#a97743"), 1.6, .9)
    pl = H("#efc48a")
    for (y, h) in ((6, 12), (79, 12)):
        fill(c, R(6, y, 84, h, 2), pl)
        stroke(c, R(6, y, 84, h, 2), H("#9c6c3a"), 1.8)
    for (x, y) in ((6, 19), (78, 19)):
        fill(c, R(x, y, 12, 59, 2), pl)
        stroke(c, R(x, y, 12, 59, 2), H("#9c6c3a"), 1.8)
    c.save()
    clip_to(c, R(18, 18, 60, 61, 0))
    line(c, [(16, 20), (80, 78)], H("#9c6c3a"), 13)
    line(c, [(16, 20), (80, 78)], pl, 9.6)
    c.restore()
    for x in (14, 48, 82):
        line(c, [(x, 100), (x, 116)], H("#855a31"), 1.8, .9)
    for (x, y) in ((12, 12), (84, 12), (12, 85), (84, 85)):
        rivet(c, x, y, 2.4, H("#aeb7c4"))
    if eyes:
        # faint eyes in the gap of the plank: a mimic trying to hide
        for x in (35, 61):
            fill(c, E(x, 42, 6.2, 6.8), INK, .78)
            fill(c, E(x + 1, 43, 2.4, 2.8), H("#ffe066"), .95)


def wall_fortress(c):
    top, front = H("#4b4560"), H("#2f2a40")
    slab(c, top, front, r=10)
    c.save()
    clip_to(c, R(5, 5, 86, 88, 8))
    bricks(c, 5, 5, 86, 88, 43, 22, H("#544c6b"), H("#2f2a40"), 8, 3)
    fill(c, R(0, 0, 96, 96, 0), lin(0, 0, 96, 96, (0, H("#fff0d0"), .16), (1, COOL, .2)))
    c.restore()
    shape(c, E(48, 48, 15, 15), H("#8a8099"), lw=2.6, rim=.45)
    fill(c, L([(48, 37), (52, 46), (59, 48), (52, 50), (48, 59), (44, 50), (37, 48), (44, 46)]), H("#ff9a3c"))
    fill(c, E(48, 48, 3.4), H("#ffe27a"))
    for x in (14, 38, 62):
        fill(c, R(x, 101, 20, 12, (9, 9, 0, 0)), H("#1c1828"))
        fill(c, R(x + 3, 104, 14, 9, (6, 6, 0, 0)), H("#c8561e"), .55)


def edge_fortress(c):
    top, front = H("#3b3650"), H("#242034")
    fill(c, R(0, 0, 96, 96, 0), H("#1f1b2c"))
    bricks(c, 0, 0, 96, 96, 48, 24, top, H("#1f1b2c"), 9, 3)
    fill(c, R(0, 0, 96, 96, 0), lin(0, 0, 96, 96, (0, H("#fff0d0"), .1), (1, COOL, .15)))
    fill(c, R(0, 96, 96, 24, 0), lin(0, 96, 0, 120, (0, front), (1, dk(front, .3))))
    # battlement teeth on the front face
    for x in (0, 48):
        fill(c, R(x + 6, 100, 36, 12, 3), H("#3b3650"))
        line(c, [(x + 8, 101), (x + 40, 101)], H("#6a6285"), 1.6)
    line(c, [(0, 97.2), (96, 97.2)], H("#6a6285"), 2.2)
    line(c, [(0, 99.4), (96, 99.4)], INK, 2.2)
    line(c, [(0, 118.5), (96, 118.5)], INK, 3)


def block_fortress(c):
    # crumbling pink tuff with ember veins
    top, front = H("#c98f7e"), H("#975f5e")
    body = S([(6, 12), (24, 3.5), (52, 5), (78, 3.5), (92, 16), (93, 60), (93, 108), (84, 117), (48, 117.5), (12, 117), (3, 106), (3.5, 50)])
    c.save()
    clip_to(c, body)
    fill(c, R(0, 0, 96, 120, 0), lin(0, 0, 96, 96, (0, lt(top, .18)), (.6, top), (1, dk(top, .12))))
    fill(c, R(0, 96, 96, 24, 0), lin(0, 96, 0, 120, (0, front), (1, dk(front, .25))))
    line(c, [(0, 95), (24, 97), (48, 94.5), (72, 96.5), (96, 95)], lt(top, .3), 2.4, .8, True)
    line(c, [(0, 98), (24, 100), (48, 97.5), (72, 99.5), (96, 98)], dk(front, .4), 2, .9, True)
    r = random.Random(14)
    for i in range(9):
        x, y = r.uniform(10, 84), r.uniform(10, 86)
        fill(c, E(x, y, r.uniform(4, 9), r.uniform(3, 6), r.uniform(0, 3)), lt(top, .2) if i % 2 else dk(top, .14), .75)
    crack = [(18, 22), (34, 34), (30, 50), (48, 58), (46, 74), (58, 88)]
    line(c, crack, H("#6d2f2a"), 5.2)
    line(c, crack, H("#ff8c3a"), 2.6)
    line(c, [(48, 58), (66, 50), (74, 34)], H("#6d2f2a"), 4.4)
    line(c, [(48, 58), (66, 50), (74, 34)], H("#ffb054"), 2)
    c.restore()
    stroke(c, body, INK, 3.6)


def crack_lines(c, pts_list, base):
    for pts in pts_list:
        line(c, [(x + .7, y + 1) for x, y in pts], lt(base, .3), 1.2, .45)
        line(c, pts, H("#1d1a2a"), 1.7, .75)


CRACK = [[(84, 22), (75, 28), (78, 37), (70, 44)], [(75, 28), (68, 24)]]
WALL_FN = {"city": wall_city, "desert": wall_desert, "factory": wall_factory, "fortress": wall_fortress}
EDGE_FN = {"city": edge_city, "desert": edge_desert, "factory": edge_factory, "fortress": edge_fortress}
BLOCK_FN = {"city": block_city, "desert": block_desert, "factory": block_factory, "fortress": block_fortress}
WALL_TOP = {"city": H("#62739b"), "desert": H("#c77f47"), "factory": H("#55617a"), "fortress": H("#4b4560")}


def hedge(c):
    r = random.Random(6)
    lf, lf2, lf3 = H("#58b257"), H("#3f9450"), H("#86d06b")
    body = S([(6, 14), (20, 3.5), (38, 7), (56, 3), (76, 6), (91, 15), (94, 34), (91, 58), (94, 82), (92, 108), (78, 117), (48, 115),
              (18, 117), (4, 108), (2, 82), (5, 58), (2, 34)])
    fill(c, body, lf2)
    c.save()
    clip_to(c, body)
    fill(c, R(0, 94, 96, 26, 0), lin(0, 94, 0, 120, (0, H("#2f7a47")), (1, H("#1f5a3e"))))
    for i in range(46):
        x, y = r.uniform(4, 92), r.uniform(4, 92)
        s = r.uniform(7, 12)
        t = (x + y) / 190
        col = mix(lf3, lf, min(1, t * 1.5)) if i % 3 else mix(lf, lf2, t)
        fill(c, E(x + 1, y + 1.6, s, s * .8), dk(lf2, .3), .55)
        fill(c, E(x, y, s, s * .8), col)
    for i in range(14):
        x, y = r.uniform(8, 88), r.uniform(8, 88)
        fill(c, E(x, y, 3.4, 2.1, -.7), H("#c9f09a"), .75)
    for x in (16, 44, 74):
        fill(c, E(x, 104, 10, 7), H("#3f9450"), .9)
    c.restore()
    stroke(c, body, INK, 3.8)
    for (x, y) in ((26, 24), (68, 58), (36, 70)):
        for k in range(5):
            a = k * TAU / 5
            fill(c, E(x + math.cos(a) * 3, y + math.sin(a) * 3, 2.4), H("#fff4f0"))
        fill(c, E(x, y, 2), H("#ffc93c"))


def hedge_sprout(c):
    soft_ellipse(c, 50, 74, 26, 9, COOL, .3)
    fill(c, E(48, 70, 17, 6.5), H("#7a5a3c"))
    stroke(c, E(48, 70, 17, 6.5), INK, 2.6)
    line(c, [(48, 70), (48, 52)], INK, 6)
    line(c, [(48, 70), (48, 52)], H("#58b257"), 3)
    shape(c, S([(48, 54), (36, 50), (24, 38), (40, 36)]), H("#7cc96a"), lw=2.6)
    shape(c, S([(48, 54), (60, 46), (74, 32), (56, 30)]), H("#58b257"), lw=2.6)


def piston_h(c):
    top, front = H("#c3cedb"), H("#7a889e")
    slab(c, top, front, r=6)
    c.save()
    clip_to(c, R(3, 3, 90, 114, 5))
    for y in (8, 74):
        fill(c, R(0, y, 96, 14, 0), H("#f2b632"))
        for i in range(-1, 8):
            x = i * 18
            fill(c, L([(x, y + 14), (x + 9, y + 14), (x + 18, y), (x + 9, y)]), H("#2f3648"))
        line(c, [(0, y), (96, y)], INK, 1.8)
        line(c, [(0, y + 14), (96, y + 14)], INK, 1.8)
    c.restore()
    # hydraulic rod and double arrow, reads as "this slides sideways"
    fill(c, R(8, 36, 80, 24, 5), lin(0, 36, 0, 60, (0, H("#eff4fa")), (.5, H("#a5b3c6")), (1, H("#62708a"))))
    stroke(c, R(8, 36, 80, 24, 5), INK, 2.4)
    for sx in (1, -1):
        cx = 48 + sx * 22
        fill(c, L([(cx + sx * 15, 48), (cx + sx * 1, 39), (cx + sx * 1, 57)]), H("#e25b3c"))
        stroke(c, L([(cx + sx * 15, 48), (cx + sx * 1, 39), (cx + sx * 1, 57)]), INK, 2)
    for x in (14, 48, 82):
        rivet(c, x, 108, 3)


def keg(c):
    # barrel body seen from the top-front
    body = S([(14, 40), (20, 14), (48, 5), (76, 14), (82, 40), (84, 76), (78, 104), (48, 117), (18, 104), (12, 76)])
    c.save()
    clip_to(c, body)
    fill(c, R(0, 0, 96, 120, 0), lin(10, 0, 86, 0, (0, H("#c98a4e")), (.35, H("#e2a766")), (1, H("#8c5430"))))
    for x in (28, 48, 68):
        line(c, [(x, 0), (x, 120)], H("#7c4a2b"), 2, .9)
    c.restore()
    fill(c, E(48, 30, 31, 22), H("#6f3f28"))
    fill(c, E(48, 30, 27, 18.5), lin(22, 12, 76, 50, (0, H("#f0be80")), (1, H("#c78a50"))))
    for x in (34, 48, 62):
        line(c, [(x, 15), (x, 45)], H("#a87040"), 1.6)
    stroke(c, E(48, 30, 31, 22), INK, 2.6)
    for y, w in ((60, 36), (98, 33)):
        line(c, [(48 - w, y - 8), (48, y), (48 + w, y - 8)], INK, 8, 1, True)
        line(c, [(48 - w, y - 8), (48, y), (48 + w, y - 8)], H("#aeb7c4"), 4.6, 1, True)
    stroke(c, body, INK, 3.6)
    # warning mark
    tri = L([(48, 62), (65, 92), (31, 92)])
    fill(c, tri, H("#ffd23f"))
    stroke(c, tri, INK, 3)
    fill(c, R(46, 71, 4.4, 11, 2), INK)
    fill(c, E(48.2, 86.5, 2.5), INK)


def gate_up(c, col):
    slab(c, col, dk(col, .35), r=10)
    fill(c, R(16, 16, 64, 64, 8), dk(col, .25))
    fill(c, R(18, 19, 60, 60, 7), lin(18, 18, 78, 78, (0, dk(col, .12)), (1, lt(col, .12))))
    # bars
    for x in (32, 48, 64):
        line(c, [(x, 22), (x, 74)], lt(col, .55), 6)
        line(c, [(x + 1.6, 23), (x + 1.6, 74)], dk(col, .4), 1.6, .7)
    line(c, [(24, 48), (72, 48)], lt(col, .4), 6)
    for (x, y) in ((10, 10), (86, 10), (10, 86), (86, 86)):
        rivet(c, x, y, 2.6)
    fill(c, E(48, 108, 6), lt(col, .7))
    stroke(c, E(48, 108, 6), INK, 2.2)


def gate_down(c, col):
    fill(c, R(8, 8, 80, 80, 12), INK, .8)
    fill(c, R(11, 11, 74, 74, 10), dk(col, .55))
    fill(c, R(11, 14, 74, 71, 10), dk(col, .42))
    for x in (32, 48, 64):
        line(c, [(x, 20), (x, 76)], dk(col, .15), 4)
    line(c, [(13, 13.5), (83, 13.5)], COOL, 3, .4)
    stroke(c, R(9, 9, 78, 78, 11), lt(col, .35), 2, .85)


def make_solids():
    for t in THEMES:
        cv = Cv(96, 120); WALL_FN[t](cv.c); done(cv, f"wall_{t}")
        cv = Cv(96, 120); EDGE_FN[t](cv.c); done(cv, f"edge_{t}")
        cv = Cv(96, 120); BLOCK_FN[t](cv.c); done(cv, f"block_{t}")
        cv = Cv(96, 120); WALL_FN[t](cv.c); crack_lines(cv.c, CRACK, WALL_TOP[t]); done(cv, f"crack_{t}")
    cv = Cv(96, 120); hedge(cv.c); done(cv, "hedge")
    cv = Cv(96, 96); hedge_sprout(cv.c); done(cv, "hedge_sprout")
    cv = Cv(96, 120); block_desert(cv.c, .1)
    c = cv.c
    c.save()
    clip_to(c, R(3, 3, 90, 114, 15))
    for i, y in enumerate((20, 46, 72, 106)):   # heat shimmer
        line(c, [(0, y), (16, y - 3), (32, y + 3), (48, y - 3), (64, y + 3), (80, y - 3), (96, y)], H("#fffdf0"), 3.2, .4, True)
    c.restore()
    done(cv, "mirage")
    cv = Cv(96, 120); piston_h(cv.c); done(cv, "piston_h")
    cv = Cv(96, 120); keg(cv.c); done(cv, "keg")
    cv = Cv(96, 120); block_factory(cv.c, True); done(cv, "mimic_a")
    RED, BLUE = H("#e2483f"), H("#3f8fe2")
    cv = Cv(96, 120); gate_up(cv.c, RED); done(cv, "gate_red_up")
    cv = Cv(96, 120); gate_up(cv.c, BLUE); done(cv, "gate_blue_up")
    cv = Cv(96, 96); gate_down(cv.c, RED); done(cv, "gate_red_down")
    cv = Cv(96, 96); gate_down(cv.c, BLUE); done(cv, "gate_blue_down")


# ================================================================== floor overlays
def conveyor(c, ang, phase):
    """Belt running 'right' in local space, rotated by ang. phase 0/1 shifts chevrons half a period."""
    c.translate(48, 48)
    c.rotate(ang)
    c.translate(-48, -48)
    fill(c, R(-2, 8, 100, 80, 0), H("#3a4152"))
    for k in range(-1, 7):       # belt ribs
        x = k * 16 + (8 if phase else 0)
        line(c, [(x, 12), (x, 84)], H("#2e3444"), 2.2)
    for k in range(-1, 3):       # chevrons, period 48 so tiles chain
        x = k * 48 + (24 if phase else 0) + 12
        pts = [(x, 26), (x + 16, 48), (x, 70), (x + 13, 70), (x + 29, 48), (x + 13, 26)]
        fill(c, L([(q[0], q[1] + 1.6) for q in pts]), INK, .6)
        fill(c, L(pts), H("#ffc93c"))
    for y0 in (0, 84):           # rails
        fill(c, R(-2, y0, 100, 12, 0), lin(0, y0, 0, y0 + 12, (0, H("#b6c0cf")), (1, H("#6d7a90"))))
        line(c, [(-2, y0 + (12 if y0 == 0 else 0)), (98, y0 + (12 if y0 == 0 else 0))], INK, 2.2)
        for k in range(3):
            x = 16 + k * 32 + (16 if phase else 0)
            fill(c, E(x % 96, y0 + 6, 2.2), H("#505c72"))


def ice(c):
    fill(c, R(2, 2, 92, 92, 10), lin(0, 0, 96, 96, (0, H("#e8fbff"), .92), (.55, H("#b7eaf7"), .85), (1, H("#8ed3ec"), .85)))
    stroke(c, R(2.5, 2.5, 91, 91, 10), H("#6db7d6"), 2.4, .9)
    line(c, [(16, 34), (36, 14)], WHITE, 5, .85)
    line(c, [(24, 44), (48, 20)], WHITE, 3, .7)
    line(c, [(60, 78), (80, 58)], WHITE, 3.6, .6)
    line(c, [(50, 58), (60, 50), (58, 42), (68, 36)], H("#7cc4de"), 1.6, .8)


def sand_q(c):
    fill(c, E(48, 48, 44), rad(48, 48, 4, 44, (0, H("#8a6233")), (.45, H("#c79a58")), (.85, H("#e0bd7e")), (1, H("#e8cf9a"), 0)))
    for arm in range(3):
        pts = []
        for i in range(22):
            t = i / 21
            a = arm * TAU / 3 + t * 4.6
            rr = 4 + t * 36
            pts.append((48 + math.cos(a) * rr, 48 + math.sin(a) * rr * .92))
        line(c, pts, H("#6f4c28"), 3.4 - 0, .7, True)
        line(c, [(p[0] - 1, p[1] - 1.5) for p in pts], H("#f4dfae"), 1.6, .65, True)
    fill(c, E(48, 48, 6.5), H("#4a3220"))


def tele(c, col, frame):
    soft_ellipse(c, 48, 48, 47, 47, col, .6 if frame else .4)
    ring = E(48, 48, 39)
    fill(c, ring, INK)
    fill(c, E(48, 48, 36), H("#454e66"))
    fill(c, E(48, 48, 36), lin(14, 14, 82, 82, (0, H("#d9e0ea")), (1, H("#7787a0"))))
    fill(c, E(48, 48, 28), INK)
    fill(c, E(48, 48, 26), rad(48, 48, 2, 26, (0, lt(col, .85)), (.35, lt(col, .25)), (1, dk(col, .35))))
    c.save()
    clip_to(c, E(48, 48, 26))
    for arm in range(3):
        pts = []
        for i in range(16):
            t = i / 15
            a = arm * TAU / 3 + t * 3.4 + (.0 if frame == 0 else 1.05)
            rr = 2 + t * 26
            pts.append((48 + math.cos(a) * rr, 48 + math.sin(a) * rr))
        line(c, pts, WHITE, 3.8 - 0, .72, True)
    c.restore()
    for k in range(4):      # metro studs
        a = k * TAU / 4 + TAU / 8 + (0 if frame == 0 else TAU / 8)
        x, y = 48 + math.cos(a) * 32, 48 + math.sin(a) * 32
        fill(c, E(x, y, 3.2), col if frame else lt(col, .5))
        stroke(c, E(x, y, 3.2), INK, 1.4)


def oneway(c, ang):
    c.translate(48, 48)
    c.rotate(ang)
    c.translate(-48, -48)
    # frame posts at the two sides, arrow pointing "right" in local space
    fill(c, R(6, 10, 84, 76, 8), H("#2b2a3d"), .34)
    for y in (5, 79):
        fill(c, R(4, y, 88, 12, 5), lin(0, y, 0, y + 12, (0, H("#f2c14e")), (1, H("#b37f22"))))
        stroke(c, R(4, y, 88, 12, 5), INK, 2.6)
    pts = [(16, 36), (44, 36), (44, 22), (80, 48), (44, 74), (44, 60), (16, 60)]
    fill(c, L([(q[0] + 1, q[1] + 2) for q in pts]), INK, .5)
    shape(c, L(pts), H("#62d26f"), lw=3.2, d=6)


def crumble(c, n):
    col = H("#6b6470")
    if n == 0:
        pts = [[(22, 28), (38, 42), (34, 58)], [(38, 42), (56, 46)]]
    elif n == 1:
        pts = [[(14, 22), (32, 38), (28, 56), (40, 72)], [(32, 38), (52, 44), (70, 34), (84, 40)], [(52, 44), (56, 64), (72, 72)]]
    else:
        pts = [[(6, 18), (30, 36), (24, 58), (40, 90)], [(30, 36), (52, 42), (72, 24), (90, 30)], [(52, 42), (56, 66), (84, 80)],
               [(24, 58), (6, 66)], [(56, 66), (44, 88)], [(72, 24), (66, 6)]]
    if n == 2:
        # plates shifted, holes between them
        fill(c, L([(30, 36), (52, 42), (56, 66), (40, 74), (24, 58)]), H("#17141f"), .9)
        fill(c, L([(72, 24), (78, 36), (64, 46), (54, 40)]), H("#17141f"), .75)
    for p in pts:
        line(c, [(x + .8, y + 1.2) for x, y in p], WHITE, 1.6, .28)
        line(c, p, H("#262333"), 2.4 + n * .9, .85)
    if n >= 1:
        for (x, y, s) in ((76, 62, 3), (20, 76, 2.6)) + (((64, 12, 3), (88, 56, 2.5)) if n == 2 else ()):
            shape(c, E(x, y, s + 1, s), col, lw=1.6, rim=0)


def pit(c):
    fill(c, R(4, 4, 88, 88, 18), INK)
    fill(c, R(7, 7, 82, 82, 15), H("#171320"))
    c.save()
    clip_to(c, R(7, 7, 82, 82, 15))
    fill(c, R(7, 7, 82, 18, 0), lin(0, 7, 0, 30, (0, H("#0d0a14")), (1, H("#2b1a24"))))
    fill(c, E(50, 58, 38, 32), rad(50, 60, 2, 38, (0, H("#ffcf5c")), (.28, H("#ff7a2a")), (.65, H("#c7301f")), (1, H("#4a1420"), .6)))
    for (x, y, r_) in ((30, 52, 5), (62, 66, 6.5), (54, 44, 3.5), (38, 72, 3.4)):
        fill(c, E(x, y, r_), H("#ffe58a"))
        fill(c, E(x - r_ * .3, y - r_ * .3, r_ * .4), WHITE, .9)
    line(c, [(16, 62), (30, 66), (42, 58), (58, 62), (82, 54)], H("#3d1117"), 2.6, .7, True)
    c.restore()
    stroke(c, R(5, 5, 86, 86, 17), H("#7b6f85"), 3)
    stroke(c, R(3, 3, 90, 90, 19), INK, 2.4)


def jet(c, warn):
    fill(c, E(48, 48, 40), INK)
    fill(c, E(48, 48, 37), lin(10, 10, 86, 86, (0, H("#c7cfdb")), (1, H("#636f86"))))
    for k in range(8):
        a = k * TAU / 8 + .39
        rivet(c, 48 + math.cos(a) * 31.5, 48 + math.sin(a) * 31.5, 2.4, H("#aeb7c4"))
    fill(c, E(48, 48, 25), INK)
    if warn:
        soft_ellipse(c, 48, 48, 47, 47, H("#ff7a2a"), .7)
        fill(c, E(48, 48, 23), rad(48, 48, 2, 23, (0, H("#fff3b0")), (.4, H("#ffc23c")), (1, H("#e83d1e"))))
        for k in range(6):
            a = k * TAU / 6
            line(c, [(48 + math.cos(a) * 6, 48 + math.sin(a) * 6), (48 + math.cos(a) * 20, 48 + math.sin(a) * 20)], H("#a52417"), 3)
        stroke(c, E(48, 48, 25), H("#ffdf7a"), 2.6)
    else:
        fill(c, E(48, 48, 23), rad(44, 42, 2, 26, (0, H("#4c566b")), (1, H("#1c2030"))))
        for k in range(6):
            a = k * TAU / 6
            line(c, [(48 + math.cos(a) * 6, 48 + math.sin(a) * 6), (48 + math.cos(a) * 20, 48 + math.sin(a) * 20)], H("#6d7a90"), 3)
    fill(c, E(48, 48, 5.5), INK)


def switch(c, col):
    fill(c, R(9, 12, 78, 76, 12), INK, .35)
    fill(c, R(8, 8, 80, 78, 12), H("#6d7a90"))
    fill(c, R(8, 8, 80, 78, 12), lin(8, 8, 88, 86, (0, H("#d5dce6")), (1, H("#7d8aa0"))))
    stroke(c, R(8, 8, 80, 78, 12), INK, 3.2)
    fill(c, R(19, 19, 58, 56, 8), dk(col, .55))
    fill(c, R(21, 23, 54, 50, 7), dk(col, .25))
    stroke(c, R(19, 19, 58, 56, 8), INK, 2.4)
    # lever
    line(c, [(48, 62), (64, 32)], INK, 11)
    line(c, [(48, 62), (64, 32)], H("#dfe5ee"), 6)
    fill(c, E(48, 62, 8.5, 6.5), INK)
    shape(c, E(66, 30, 12), col, lw=3, rim=.5)
    gloss(c, 62, 25.5, 4, 2.5, .8)
    for (x, y) in ((14, 14), (82, 14), (14, 80), (82, 80)):
        rivet(c, x, y, 2.2, H("#8e9bb3"))


def road(c):
    fill(c, R(0, 0, 96, 96, 0), lin(0, 0, 0, 96, (0, H("#5a6072")), (1, H("#4d5364"))))
    r = random.Random(2)
    for i in range(40):
        fill(c, E(r.uniform(0, 96), r.uniform(10, 86), r.uniform(.7, 1.6)), H("#6f768a") if i % 2 else H("#40465a"), .7)
    fill(c, R(0, 0, 96, 7, 0), H("#c5ccd4"))
    fill(c, R(0, 89, 96, 7, 0), H("#aeb6c0"))
    line(c, [(0, 7), (96, 7)], INK, 2.2)
    line(c, [(0, 89), (96, 89)], INK, 2.2)
    fill(c, R(24, 44, 48, 8, 2), H("#ffd978"))


def secret_tile(c):
    # very quiet flower mosaic: only a sharp eye notices
    col = H("#fff6dc")
    drk = H("#4a3a5a")
    for k in range(8):
        a = k * TAU / 8
        x, y = 48 + math.cos(a) * 21, 48 + math.sin(a) * 21
        fill(c, E(x, y, 12, 6, a), col if k % 2 else drk, .16)
    for k in range(12):
        a = k * TAU / 12
        fill(c, E(48 + math.cos(a) * 38, 48 + math.sin(a) * 38, 3.2), col, .16)
    fill(c, E(48, 48, 8), H("#ffd978"), .22)
    stroke(c, E(48, 48, 32), drk, 1.6, .14)


RUBBLE = {"city": (H("#ecc488"), H("#cf9c5e")), "desert": (H("#e9b95a"), H("#a87033")),
          "factory": (H("#dea866"), H("#9c6c3a")), "fortress": (H("#c98f7e"), H("#7d4a48"))}


def rubble(c, t):
    r = random.Random(30 + THEMES.index(t))
    a, b = RUBBLE[t]
    soft_ellipse(c, 50, 66, 36, 15, COOL, .2)
    bits = ((26, 60, 11), (66, 66, 9), (46, 72, 13), (72, 44, 6), (22, 38, 5), (52, 44, 6.5))
    for (x, y, s) in bits:
        n = 5 + (int(s) % 2)
        pts = []
        for i in range(n):
            ang = i * TAU / n + r.uniform(-.25, .25)
            q = s * r.uniform(.75, 1.15)
            pts.append((x + math.cos(ang) * q, y + math.sin(ang) * q * .8))
        shape(c, L(pts), a if s > 7 else b, lw=2.4, d=s * .5)


def exit_door(c, open_):
    # bunker hatch: chunky steel frame with a pomegranate-star emblem
    fr = H("#8f9bb0")
    fill(c, R(6, 5, 84, 110, 14), INK)
    fill(c, R(9, 8, 78, 104, 12), lin(9, 8, 87, 112, (0, H("#d6dde7")), (1, fr)))
    for (x, y) in ((16, 15), (80, 15), (16, 105), (80, 105)):
        rivet(c, x, y, 2.8, H("#6d7a90"))
    if not open_:
        door = R(19, 20, 58, 80, 9)
        shape(c, door, H("#b65a3a"), lw=3, d=9)
        line(c, [(19, 60), (77, 60)], INK, 2.6)
        fill(c, R(38, 53, 20, 14, 3), H("#ffd23f"))
        stroke(c, R(38, 53, 20, 14, 3), INK, 2.6)
        for y in (32, 88):
            for x in (30, 48, 66):
                rivet(c, x, y, 2.4, H("#e0a083"))
        # padlock
        stroke(c, R(42, 45, 12, 12, 5), INK, 3)
        fill(c, E(48, 61, 2.4), INK)
    else:
        soft = R(19, 20, 58, 80, 9)
        fill(c, soft, H("#14283a"))
        c.save()
        clip_to(c, soft)
        fill(c, E(48, 72, 44, 50), rad(48, 74, 4, 52, (0, H("#f2ffd8")), (.35, H("#7dff8f")), (1, H("#129a5a"))))
        for k in range(5):      # stairs going down into the light
            y = 76 + k * 9
            line(c, [(19, y), (77, y)], H("#0f6a4a"), 2, .55)
        c.restore()
        stroke(c, soft, INK, 3)
        # door leaf swung open to the left
        shape(c, L([(19, 20), (5, 28), (5, 92), (19, 100)]), H("#b65a3a"), lw=3, d=4)
        line(c, [(48, 56), (48, 40)], H("#eaffd9"), 7)
        fill(c, L([(48, 28), (60, 44), (36, 44)]), H("#eaffd9"))
        stroke(c, L([(48, 28), (60, 44), (36, 44), (42, 44), (42, 58), (54, 58), (54, 44)]), H("#0f6a4a"), 2)


def shadow(c):
    soft_ellipse(c, 48, 24, 46, 22, H("#14122a"), .55)


def make_overlays():
    for d, ang in (("r", 0), ("d", math.pi / 2), ("l", math.pi), ("u", -math.pi / 2)):
        for i, s in enumerate("ab"):
            cv = Cv(96, 96); conveyor(cv.c, ang, i); done(cv, f"conv_{d}_{s}")
        cv = Cv(96, 96); oneway(cv.c, ang); done(cv, f"oneway_{d}")
    cv = Cv(96, 96); ice(cv.c); done(cv, "ice")
    cv = Cv(96, 96); sand_q(cv.c); done(cv, "sand_q")
    for s, col in (("a", H("#2fc6e8")), ("b", H("#f05fa8")), ("c", H("#8fd13f"))):
        for i in (1, 2):
            cv = Cv(96, 96); tele(cv.c, col, i - 1); done(cv, f"tele_{s}{i}")
    for n in range(3):
        cv = Cv(96, 96); crumble(cv.c, n); done(cv, f"crumble_{n}")
    cv = Cv(96, 96); pit(cv.c); done(cv, "pit")
    cv = Cv(96, 96); jet(cv.c, False); done(cv, "jet_off")
    cv = Cv(96, 96); jet(cv.c, True); done(cv, "jet_warn")
    cv = Cv(96, 96); switch(cv.c, H("#e2483f")); done(cv, "switch_red")
    cv = Cv(96, 96); switch(cv.c, H("#3f8fe2")); done(cv, "switch_blue")
    cv = Cv(96, 96); road(cv.c); done(cv, "road")
    cv = Cv(96, 96); secret_tile(cv.c); done(cv, "secret_tile")
    for t in THEMES:
        cv = Cv(96, 96); rubble(cv.c, t); done(cv, f"rubble_{t}")
    cv = Cv(96, 120); exit_door(cv.c, False); done(cv, "exit_closed")
    cv = Cv(96, 120); exit_door(cv.c, True); done(cv, "exit_open")
    cv = Cv(96, 48); shadow(cv.c); done(cv, "shadow")


# ================================================================== hero: Pitzi the mole demolition worker
PAL = {
    "hero": dict(fur=H("#9a6a44"), muz=H("#e2bd8e"), ov=H("#2f7fd6")),
    "p2": dict(fur=H("#8d93a3"), muz=H("#dfe3ea"), ov=H("#dd3f3f")),
    "p3": dict(fur=H("#d9b777"), muz=H("#f6e6c0"), ov=H("#38ad57")),
    "p4": dict(fur=H("#f3f0ea"), muz=H("#ffffff"), ov=H("#8d55d6")),
}
HAT = H("#ffc62e")
NOSE = H("#ff8fa6")
BOOT = H("#d9622b")
CLAW = H("#fff1da")


def hard_hat(c, cx, top, w, side=0, back=False, lamp_on=True):
    """Yellow hard hat. side=+1 faces right, -1 left, 0 front. (cx, top) top of the dome, w half width."""
    by = top + w * .86      # brim line
    dome = S([(cx - w * .92, by), (cx - w * .8, top + w * .34), (cx - w * .36, top + w * .03), (cx + w * .36, top + w * .03),
              (cx + w * .8, top + w * .34), (cx + w * .92, by), (cx, by + w * .1)])
    shape(c, dome, HAT, lw=2.6, d=w * .22)
    # centre ridge
    rx = cx + side * w * .12
    fill(c, S([(rx - w * .14, top + 1), (rx + w * .14, top + 1), (rx + w * .17, by + 1), (rx - w * .17, by + 1)]), lt(HAT, .4), .9)
    line(c, [(rx - w * .14, top + 2.5), (rx - w * .17, by)], dk(HAT, .3), 1.5, .8)
    line(c, [(rx + w * .14, top + 2.5), (rx + w * .17, by)], dk(HAT, .3), 1.5, .8)
    bx = cx + side * w * .22
    shape(c, E(bx, by + 1.5, w * 1.1, w * .19), lt(HAT, .08), lw=2.6, d=w * .1, rim=.45)
    if back:
        return
    # lamp
    lx = cx + side * w * .92 if side else cx
    ly = top + w * .5
    if side:
        shape(c, R(lx - w * .3, ly - w * .22, w * .4, w * .44, 3), H("#6c7890"), lw=2.2, d=3)
        lx += side * w * .1
    r_ = w * .26
    shape(c, E(lx, ly, r_ * (.72 if side else 1.14), r_ * 1.14), H("#6c7890"), lw=2.2, d=3)
    fill(c, E(lx, ly, r_ * (.5 if side else .82), r_ * .82), rad(lx - 1, ly - 1, 0, r_, (0, H("#fffbe0")), (.7, H("#ffe36e")), (1, H("#ffb52e"))))
    gloss(c, lx - r_ * .28, ly - r_ * .3, r_ * .24, r_ * .16, .9)


def face_front(c, cx, cy, s, pal, mood="ok"):
    """Mole face on the head circle centred (cx,cy). s scales from r=36."""
    muz = pal["muz"]
    fill(c, E(cx, cy + 16 * s, 25 * s, 18 * s), muz)
    fill(c, E(cx - 24 * s, cy + 13 * s, 6 * s, 4 * s), H("#ff9a9a"), .55)
    fill(c, E(cx + 24 * s, cy + 13 * s, 6 * s, 4 * s), H("#ff9a9a"), .55)
    ey = cy + 1 * s
    ex = 17 * s
    if mood == "ok":
        for sx in (-1, 1):
            fill(c, E(cx + sx * ex, ey, 5.2 * s, 6 * s), INK)
            fill(c, E(cx + sx * ex - 1.6 * s, ey - 2.2 * s, 2 * s), WHITE)
    elif mood == "joy":
        for sx in (-1, 1):
            line(c, [(cx + sx * ex - 6 * s, ey + 2 * s), (cx + sx * ex, ey - 4 * s), (cx + sx * ex + 6 * s, ey + 2 * s)], INK, 3.2 * s, 1, True)
    else:
        for sx in (-1, 1):
            x = cx + sx * ex
            line(c, [(x - 5 * s, ey - 5 * s), (x + 5 * s, ey + 5 * s)], INK, 3.2 * s)
            line(c, [(x + 5 * s, ey - 5 * s), (x - 5 * s, ey + 5 * s)], INK, 3.2 * s)
    # mouth under the nose
    ny = cy + 12 * s
    if mood == "ok":
        line(c, [(cx - 10 * s, ny + 13 * s), (cx - 4 * s, ny + 17 * s), (cx, ny + 13 * s), (cx + 4 * s, ny + 17 * s), (cx + 10 * s, ny + 13 * s)], INK, 2.4 * s, 1, True)
        fill(c, R(cx - 3.6 * s, ny + 15.8 * s, 7.2 * s, 5.4 * s, 1.6 * s), WHITE)
        stroke(c, R(cx - 3.6 * s, ny + 15.8 * s, 7.2 * s, 5.4 * s, 1.6 * s), INK, 1.5 * s)
    elif mood == "joy":
        m = S([(cx - 13 * s, ny + 11 * s), (cx + 13 * s, ny + 11 * s), (cx + 7 * s, ny + 23 * s), (cx - 7 * s, ny + 23 * s)])
        fill(c, m, H("#8c2f3f"))
        c.save(); clip_to(c, m)
        fill(c, E(cx, ny + 24 * s, 8 * s, 5 * s), H("#ff7f8f"))
        fill(c, R(cx - 4 * s, ny + 10 * s, 8 * s, 5 * s, 1), WHITE)
        c.restore()
        stroke(c, m, INK, 2.4 * s)
    else:
        line(c, [(cx - 11 * s, ny + 18 * s), (cx - 5 * s, ny + 14 * s), (cx, ny + 18 * s), (cx + 5 * s, ny + 14 * s), (cx + 11 * s, ny + 18 * s)], INK, 2.6 * s, 1, True)
    # big pink nose
    shape(c, S([(cx - 14 * s, ny + 1 * s), (cx - 8 * s, ny - 7.5 * s), (cx + 8 * s, ny - 7.5 * s), (cx + 14 * s, ny + 1 * s), (cx + 6 * s, ny + 8.5 * s), (cx - 6 * s, ny + 8.5 * s)]),
          NOSE, lw=2.6 * s, d=5 * s, rim=.4)
    gloss(c, cx - 5 * s, ny - 3 * s, 4.2 * s, 2.2 * s, .8)


def boot(c, x, y, w=15, h=9.5, side=0):
    sole = H("#5a3320")
    if side:
        shape(c, E(x + w * .15, y, w * 1.1, h), BOOT, lw=0, d=4)
        stroke(c, E(x + w * .15, y, w * 1.1, h), INK, 2.4)
        return
    shape(c, E(x, y, w * .86, h), BOOT, lw=2.4, d=4)
    line(c, [(x - w * .6, y + h * .55), (x + w * .6, y + h * .55)], sole, 2.2)


def paw(c, x, y, s=1.0, ang=0, pal=None):
    """Big digging paw with three claws."""
    c.save()
    c.translate(x, y)
    c.rotate(ang)
    for k in (-1, 0, 1):
        q = L([(k * 6.2 * s - 3 * s, 7 * s), (k * 7.6 * s, 15 * s), (k * 6.2 * s + 3 * s, 7 * s)])
        fill(c, q, CLAW)
        stroke(c, q, INK, 1.9)
    shape(c, E(0, 0, 10.5 * s, 11 * s), pal["fur"], lw=2.4, d=4)
    fill(c, E(-.6 * s, 2.5 * s, 5.5 * s, 4.8 * s), H("#ffb3b8"), .9)
    c.restore()


def bib_front(c, ov, cx, by):
    # overall bib, straps and a yellow pocket button
    bib = R(cx - 18, by, 36, 26, 5)
    shape(c, bib, lt(ov, .08), lw=2.2, d=5, rim=.3)
    line(c, [(cx - 15, by + 2), (cx - 22, by - 12)], INK, 7.5)
    line(c, [(cx + 15, by + 2), (cx + 22, by - 12)], INK, 7.5)
    line(c, [(cx - 15, by + 2), (cx - 22, by - 12)], lt(ov, .08), 4)
    line(c, [(cx + 15, by + 2), (cx + 22, by - 12)], lt(ov, .08), 4)
    for sx in (-1, 1):
        fill(c, E(cx + sx * 13, by + 6, 3), H("#ffd23f"))
        stroke(c, E(cx + sx * 13, by + 6, 3), INK, 1.5)
    fill(c, R(cx - 8, by + 12, 16, 10, 3), dk(ov, .22))
    stroke(c, R(cx - 8, by + 12, 16, 10, 3), INK, 1.6)


def mole_front(c, f, pal, mood="ok", dy=0):
    fur, ov = pal["fur"], pal["ov"]
    bob = 0 if f == 0 else -2.5
    fl, fr = {0: (146, 146), 1: (147, 139), 2: (139, 147)}[f]
    boot(c, 45, fl - 1, 14)
    boot(c, 83, fr - 1, 14)
    pl, pr = {0: (0, 0), 1: (-5, 5), 2: (5, -5)}[f]
    body = S([(32, 108 + bob), (40, 90 + bob), (88, 90 + bob), (96, 108 + bob), (92, 132 + bob), (76, 143), (52, 143), (36, 132 + bob)])
    shape(c, body, ov, lw=2.6, d=9)
    # belly fur peek above bib
    bib_front(c, ov, 64, 103 + bob)
    paw(c, 30, 114 + bob + pl, 1, .35, pal)
    paw(c, 98, 114 + bob + pr, 1, -.35, pal)
    hy = 72 + bob
    shape(c, E(64, hy, 37, 35), fur, lw=2.6, d=9)
    face_front(c, 64, hy, 1, pal, mood)
    hard_hat(c, 64, 15 + bob, 39)


def mole_back(c, f, pal):
    fur, ov = pal["fur"], pal["ov"]
    bob = 0 if f == 0 else -2.5
    fl, fr = {0: (146, 146), 1: (139, 147), 2: (147, 139)}[f]
    boot(c, 45, fl - 1, 14)
    boot(c, 83, fr - 1, 14)
    pl, pr = {0: (0, 0), 1: (5, -5), 2: (-5, 5)}[f]
    body = S([(32, 108 + bob), (40, 90 + bob), (88, 90 + bob), (96, 108 + bob), (92, 132 + bob), (76, 143), (52, 143), (36, 132 + bob)])
    shape(c, body, ov, lw=2.6, d=9)
    # crossed straps
    for a, b in (((44, 92), (78, 124)), ((84, 92), (50, 124))):
        line(c, [(a[0], a[1] + bob), (b[0], b[1] + bob)], INK, 7.5)
        line(c, [(a[0], a[1] + bob), (b[0], b[1] + bob)], lt(ov, .15), 4)
    fill(c, E(64, 108 + bob, 5), H("#ffd23f"))
    stroke(c, E(64, 108 + bob, 5), INK, 1.8)
    # stubby tail
    shape(c, E(64, 132 + bob, 7, 6), fur, lw=2.4, d=3)
    paw(c, 30, 112 + bob + pl, 1, .35, pal)
    paw(c, 98, 112 + bob + pr, 1, -.35, pal)
    hy = 72 + bob
    shape(c, E(64, hy, 37, 35), fur, lw=2.6, d=9)
    # fur tufts on the neck
    for x in (50, 64, 78):
        line(c, [(x - 5, hy + 22), (x, hy + 28), (x + 5, hy + 22)], dk(fur, .28), 2.2, .9)
    hard_hat(c, 64, 15 + bob, 39, back=True)
    # hat strap adjuster at the back
    fill(c, R(52, 46 + bob, 24, 11, 4), H("#6c7890"))
    stroke(c, R(52, 46 + bob, 24, 11, 4), INK, 2.2)
    fill(c, E(64, 51.5 + bob, 3), H("#d5dce6"))


def mole_side(c, f, pal):
    """Facing right."""
    fur, ov, muz = pal["fur"], pal["ov"], pal["muz"]
    bob = 0 if f == 0 else -2.5
    fa, fb = {0: (0, 0), 1: (13, -11), 2: (-11, 13)}[f]
    ya, yb = {0: (0, 0), 1: (0, -5), 2: (-5, 0)}[f]
    # far foot, far paw
    boot(c, 62 + fb, 144 + yb, 13, 9, 1)
    paw(c, 74 - fa * .6, 112 + bob, .85, -.5, pal)
    body = S([(38, 112 + bob), (46, 92 + bob), (82, 92 + bob), (90, 112 + bob), (86, 134 + bob), (72, 143), (50, 143), (40, 134 + bob)])
    shape(c, body, ov, lw=2.6, d=9)
    # stubby tail
    shape(c, E(36, 126 + bob, 7, 5.5, -.4), fur, lw=2.4, d=3)
    # strap, button
    line(c, [(70, 94 + bob), (62, 112 + bob)], INK, 7.5)
    line(c, [(70, 94 + bob), (62, 112 + bob)], lt(ov, .15), 4)
    fill(c, E(62, 113 + bob, 3.4), H("#ffd23f"))
    stroke(c, E(62, 113 + bob, 3.4), INK, 1.6)
    fill(c, R(68, 118 + bob, 15, 12, 3), dk(ov, .2))
    stroke(c, R(68, 118 + bob, 15, 12, 3), INK, 1.6)
    boot(c, 58 + fa, 145 + ya, 14, 9.5, 1)
    hy = 72 + bob
    head = S([(28, 70 + bob), (32, 46 + bob), (60, 37 + bob), (88, 48 + bob), (106, 76 + bob), (102, 90 + bob), (84, 100 + bob), (58, 106 + bob), (36, 96 + bob)])
    shape(c, head, fur, lw=2.6, d=9)
    fill(c, S([(70, 92 + bob), (84, 72 + bob), (104, 76 + bob), (102, 90 + bob), (84, 99 + bob)]), muz)
    # eye
    fill(c, E(74, 68 + bob, 5, 6), INK)
    fill(c, E(72.6, 65.8 + bob, 2), WHITE)
    fill(c, E(70, 84 + bob, 6, 4), H("#ff9a9a"), .55)
    line(c, [(86, 92 + bob), (94, 94 + bob), (100, 90 + bob)], INK, 2.4, 1, True)
    shape(c, E(107, 79 + bob, 11.5, 9.5), NOSE, lw=2.6, d=5, rim=.4)
    gloss(c, 104, 75.5 + bob, 3.8, 2, .8)
    paw(c, 56 - fa * .6, 113 + bob, 1, .15, pal)
    hard_hat(c, 62, 15 + bob, 38, side=1)


def mole_cheer(c, pal, stars=True):
    fur, ov = pal["fur"], pal["ov"]
    boot(c, 42, 146, 14)
    boot(c, 86, 146, 14)
    body = S([(32, 104), (40, 86), (88, 86), (96, 104), (92, 128), (76, 140), (52, 140), (36, 128)])
    shape(c, body, ov, lw=2.6, d=9)
    bib_front(c, ov, 64, 99)
    # arms up
    for sx, x in ((-1, 13), (1, 115)):
        line(c, [(64 + sx * 30, 102), (x, 62)], INK, 15)
        line(c, [(64 + sx * 30, 102), (x, 62)], fur, 10)
        paw(c, x, 58, 1, math.pi + sx * -.3, pal)
    shape(c, E(64, 68, 37, 35), fur, lw=2.6, d=9)
    face_front(c, 64, 68, 1, pal, "joy")
    hard_hat(c, 64, 8, 39)
    for (x, y, s, col) in ((14, 26, 5, H("#ffd23f")), (112, 22, 4.5, H("#ff8fa6")), (22, 8, 3, H("#7dd3ff"))) if stars else ():
        star(c, x, y, s, col)


def star(c, x, y, s, col, lw=1.8):
    pts = []
    for i in range(10):
        a = -math.pi / 2 + i * TAU / 10
        rr = s if i % 2 == 0 else s * .48
        pts.append((x + math.cos(a) * rr, y + math.sin(a) * rr))
    fill(c, L(pts), col)
    if lw:
        stroke(c, L(pts), INK, lw)


def mole_hurt(c, pal):
    fur, ov = pal["fur"], pal["ov"]
    boot(c, 42, 146, 14)
    boot(c, 86, 143, 14)
    body = S([(32, 108), (40, 90), (88, 90), (96, 108), (92, 132), (76, 143), (52, 143), (36, 132)])
    shape(c, body, ov, lw=2.6, d=9)
    bib_front(c, ov, 64, 103)
    paw(c, 24, 104, 1, 1.1, pal)
    paw(c, 104, 104, 1, -1.1, pal)
    shape(c, E(64, 74, 37, 35), fur, lw=2.6, d=9)
    # soot smudges
    for (x, y, rx, ry) in ((40, 62, 9, 6), (88, 84, 7, 5), (70, 50, 8, 4)):
        fill(c, E(x, y, rx, ry), INK, .32)
    face_front(c, 64, 74, 1, pal, "hurt")
    c.save()
    rot_about(c, .28, 64, 40)
    hard_hat(c, 70, 8, 37)
    c.restore()
    for k in range(3):      # dizzy stars
        a = k * TAU / 3 + .4
        star(c, 64 + math.cos(a) * 46, 22 + math.sin(a) * 11, 6, H("#ffd23f"))
    # smoke puff
    fill(c, E(26, 40, 8), H("#c9cbd6"), .85)
    stroke(c, E(26, 40, 8), INK, 2)


def mole_head(c, pal, mood="ok", s=1.0):
    """Face + hat, drawn around 48,50 for a 96x96 canvas."""
    c.save()
    c.translate(48, 48)
    c.scale(s, s)
    c.translate(-48, -48)
    c.translate(48 - 64, 60 - 72)
    c.save()
    shape(c, E(64, 72, 37, 35), pal["fur"], lw=2.6, d=9)
    face_front(c, 64, 72, 1, pal, mood)
    hard_hat(c, 64, 15, 39)
    c.restore()
    c.restore()


FEET_UP = -6      # art is drawn with soles at y=158; the contract wants them 8 px above the canvas bottom


def make_hero():
    for key, pal in PAL.items():
        for d in "dulr":
            for f in range(3):
                cv = Cv(128, 160)
                c = cv.c
                c.translate(0, FEET_UP)
                if d == "d":
                    mole_front(c, f, pal)
                elif d == "u":
                    mole_back(c, f, pal)
                elif d == "r":
                    mole_side(c, f, pal)
                else:
                    c.translate(128, 0)
                    c.scale(-1, 1)
                    mole_side(c, f, pal)
                done(cv, f"{key}_{d}_{f}", 2)
        cv = Cv(128, 160); cv.c.translate(0, FEET_UP); cv.c.translate(64, 152); cv.c.scale(.96, .96); cv.c.translate(-64, -152); mole_cheer(cv.c, pal); done(cv, f"{key}_cheer", 2)
        cv = Cv(128, 160); cv.c.translate(0, FEET_UP); cv.c.translate(64, 152); cv.c.scale(.96, .96); cv.c.translate(-64, -152); mole_hurt(cv.c, pal); done(cv, f"{key}_hurt", 2)
        cv = Cv(96, 96); mole_head(cv.c, pal, s=.9); done(cv, f"{key}_head", 2)


# ================================================================== enemies (128x128, facing left, feet at y=120)
def eye(c, x, y, r=7, look=(-1.5, .5), pupil=.58, lid=None):
    shape(c, E(x, y, r, r * 1.08), WHITE, lw=2.2, d=2, rim=0, shade=.5)
    fill(c, E(x + look[0], y + look[1], r * pupil, r * pupil * 1.1), INK)
    fill(c, E(x + look[0] - r * .2, y + look[1] - r * .24, r * .2), WHITE)
    if lid:
        line(c, [(x - r * 1.1, y - r * lid), (x + r * 1.1, y - r * (lid - .6))], INK, 3)


def snail(c, f):
    sq = 0 if f == 0 else 1
    bodyc, shellc = H("#ffd47a"), H("#e0568f")
    st = 6 if sq else 0     # stretch
    body = S([(14 - st, 116), (10 - st, 100), (20 - st, 88), (34, 92), (50, 104), (80, 106), (108 + st * .4, 108), (118 + st * .4, 118), (66, 120)])
    shape(c, body, bodyc, lw=2.6, d=8)
    # eye stalks
    for x, top, r_ in ((24 - st, 58 + sq * 3, 7.5), (42 - st, 62 + sq * 3, 6.5)):
        line(c, [(x + 4, 92), (x, top + 6)], INK, 8.5, 1, True)
        line(c, [(x + 4, 92), (x, top + 6)], bodyc, 4.6, 1, True)
        eye(c, x, top, r_)
    # smile
    line(c, [(13 - st, 104), (20 - st, 108), (27 - st, 105)], INK, 2.4, 1, True)
    # shell
    sx, sy = 74, 72
    shape(c, E(sx, sy, 36, 37), shellc, lw=2.6, d=11, rim=.4)
    pts = []
    for i in range(44):
        a = i * .34 + 2.2
        rr = 28 - i * .58
        pts.append((sx + 1 + math.cos(a) * rr, sy + 1 + math.sin(a) * rr))
    line(c, pts, H("#a12d6f"), 5, 1, True)
    line(c, [(p[0] - .8, p[1] - 1.4) for p in pts], H("#ffa3c7"), 1.8, .85, True)
    gloss(c, sx - 16, sy - 22, 8, 4, .55)


def beetle(c, f):
    shellc = H("#34b08a")
    legs = ((30, 104, 16, 116), (58, 110, 52, 118), (88, 106, 98, 117))
    for i, (x0, y0, x1, y1) in enumerate(legs):
        o = (5 if (i + f) % 2 else -5)
        k = [(x0, y0), ((x0 + x1) / 2 + o * .6, y0 + 4), (x1 + o, y1)]
        line(c, k, INK, 6.5, 1, True)
    # tiny antennae
    line(c, [(24, 68), (12, 48), (2, 50)], INK, 3.6, 1, True)
    line(c, [(34, 62), (28, 42), (17, 40)], INK, 3.6, 1, True)
    # head
    shape(c, E(30, 82, 22, 22), H("#3a3f6b"), lw=2.6, d=7)
    # shell: glossy dome with split
    dome = S([(36, 106), (34, 78), (56, 54), (88, 52), (114, 76), (118, 100), (102, 112), (60, 114)])
    shape(c, dome, shellc, lw=2.6, d=13, rim=.42)
    c.save()
    clip_to(c, dome)
    line(c, [(36, 92), (66, 86), (120, 88)], INK, 2.8, 1, True)
    for (x, y, r_) in ((64, 70, 7), (88, 68, 6), (100, 94, 5.5), (72, 100, 6)):
        fill(c, E(x, y, r_), H("#ffd23f"))
        stroke(c, E(x, y, r_), INK, 1.8)
    c.restore()
    gloss(c, 54, 66, 11, 5, .6)
    eye(c, 24, 80, 8)
    # mandibles
    line(c, [(12, 96), (8, 104), (16, 104)], INK, 4, 1, True)
    line(c, [(24, 102), (21, 110), (29, 109)], INK, 4, 1, True)


def smoke_puffs(c, puffs, col, ol=INK):
    """Merged cloud of circles with one shared outline."""
    for (x, y, r_) in puffs:
        fill(c, E(x, y, r_ + 1.2), ol)
    for (x, y, r_) in puffs:
        fill(c, E(x, y, r_), col)
    for (x, y, r_) in puffs:
        fill(c, E(x - r_ * .24, y - r_ * .28, r_ * .62), lt(col, .4), .8)


def ghostmoth(c, f):
    up = -7 if f else 0
    wc, wc2 = H("#9d97bd"), H("#736d99")
    # wide smoky wings
    w2 = S([(72, 62), (98, 20 + up * 2), (122, 24 + up * 2), (122, 54 + up), (104, 78), (80, 80)])
    shape(c, w2, wc2, lw=2.6, d=9)
    w1 = S([(56, 62), (30, 20 + up * 2), (6, 24 + up * 2), (6, 54 + up), (24, 78), (48, 80)])
    shape(c, w1, wc, lw=2.6, d=9, rim=.4)
    for (x, y, r_) in ((27, 46 + up, 9.5), (101, 46 + up, 9.5)):
        fill(c, E(x, y, r_), H("#efe9fb"))
        fill(c, E(x, y, r_ * .52), H("#4d4570"))
        stroke(c, E(x, y, r_), INK, 2)
    # it trails smoke instead of having a tail
    o = 3 if f else -3
    smoke_puffs(c, [(64, 92, 16), (50 + o, 100, 12), (78 - o, 101, 12), (60 - o, 111, 9), (72 + o, 113, 6.5), (44 + o, 113, 4.5)], H("#cfccdc"))
    # feather antennae behind the head
    for sx in (-1, 1):
        a = [(64 + sx * 6, 44), (64 + sx * 11, 28), (64 + sx * 24, 19)]
        line(c, a, INK, 3, 1, True)
        for (x, y) in ((64 + sx * 11, 28), (64 + sx * 16, 23), (64 + sx * 21, 20)):
            line(c, [(x - sx * 4, y - 5), (x + sx * 4, y + 5)], INK, 2.2)
    shape(c, E(64, 62, 21, 20), H("#e8e4f2"), lw=2.6, d=5)
    eye(c, 55, 61, 8, (-1.5, .5))
    eye(c, 73, 61, 7.2, (-1.5, .5))
    line(c, [(58, 74), (64, 76), (69, 74)], INK, 2.2, 1, True)


def fox(c, f):
    orange, cream = H("#f07f35"), H("#fff0d8")
    p = 5 if f else -5
    # tail
    tail = S([(88, 96), (104, 92), (118, 74), (124, 52), (112, 46), (100, 58), (84, 76)])
    shape(c, tail, orange, lw=2.6, d=7)
    c.save()
    clip_to(c, tail)
    fill(c, E(120, 46, 18, 16), cream)
    c.restore()
    stroke(c, tail, INK, 2.6)
    # far legs
    for x, o in ((46, p), (82, -p)):
        fill(c, R(x + o - 5, 100, 11, 18, 5), dk(orange, .3))
        stroke(c, R(x + o - 5, 100, 11, 18, 5), INK, 2.4)
    body = S([(40, 82), (66, 76), (92, 84), (98, 100), (86, 110), (54, 110), (38, 100)])
    shape(c, body, orange, lw=2.6, d=8)
    fill(c, S([(42, 100), (60, 96), (78, 104), (66, 110), (46, 108)]), cream)
    for x, o in ((58, -p), (92, p)):
        fill(c, R(x + o - 6, 102, 12, 16, 5), orange)
        fill(c, R(x + o - 6, 111, 12, 8, 4), H("#3a3147"))
        stroke(c, R(x + o - 6, 102, 12, 17, 5), INK, 2.4)
    # head: pointed snout left, two tall ears
    for pts, inner in (([(52, 56), (66, 18), (78, 58)], [(60, 52), (66, 30), (72, 53)]),
                       ([(26, 54), (28, 14), (54, 46)], [(33, 49), (33, 28), (47, 45)])):
        shape(c, L(pts), orange, lw=2.6, d=5)
        fill(c, L(inner), H("#3a3147"))
    head = S([(4, 76), (22, 62), (34, 46), (58, 44), (76, 56), (76, 76), (60, 90), (30, 92), (12, 86)])
    shape(c, head, orange, lw=2.6, d=8)
    c.save()
    clip_to(c, head)
    fill(c, S([(0, 76), (24, 70), (42, 78), (60, 92), (30, 98), (0, 90)]), cream)
    c.restore()
    stroke(c, head, INK, 2.6)
    shape(c, E(6, 78, 6.5, 5.5), H("#3a3147"), lw=2, d=2)
    gloss(c, 4.5, 76, 2, 1.2, .9)
    # sly eyes
    eye(c, 32, 64, 7, (-2, .5), lid=.7)
    eye(c, 54, 63, 6.4, (-2, .5), lid=.7)
    line(c, [(14, 86), (24, 88), (30, 85)], INK, 2.4, 1, True)
    fill(c, L([(19, 87), (22, 93), (25, 88)]), WHITE)


def frog(c, f):
    g, pale = H("#6fd14f"), H("#e6f7a8")
    if f == 0:      # crouched
        shape(c, E(96, 98, 26, 22), dk(g, .05), lw=2.6, d=7)
        fill(c, R(84, 111, 34, 9, 4.5), dk(g, .1))
        stroke(c, R(84, 111, 34, 9, 4.5), INK, 2.4)
        body = S([(18, 96), (26, 72), (58, 62), (92, 74), (104, 100), (90, 116), (40, 118), (20, 112)])
        shape(c, body, g, lw=2.6, d=10)
        fill(c, S([(20, 104), (40, 96), (74, 106), (64, 117), (30, 116)]), pale)
        leg = R(30, 108, 26, 11, 5)
        fill(c, leg, g)
        stroke(c, leg, INK, 2.4)
        ey = 0
    else:           # mid hop: stretched, legs dangling
        for (x, y) in ((96, 112), (72, 116)):
            line(c, [(84, 92), (x, y)], INK, 14, 1)
            line(c, [(84, 92), (x, y)], dk(g, .08), 8.6, 1)
        body = S([(22, 82), (30, 58), (58, 46), (92, 58), (102, 84), (84, 104), (42, 106), (24, 98)])
        shape(c, body, g, lw=2.6, d=10)
        fill(c, S([(26, 92), (44, 82), (74, 92), (66, 104), (34, 102)]), pale)
        line(c, [(40, 96), (28, 114)], INK, 13)
        line(c, [(40, 96), (28, 114)], g, 8)
        ey = -16
    # big bulging eyes on top
    for (x, r_) in ((30, 15), (66, 14)):
        shape(c, E(x, 62 + ey, r_ + 2, r_ + 1), g, lw=2.6, d=5)
        eye(c, x - 1, 60 + ey, r_ - 3, (-2.5, .5), .6)
    # wide mouth
    line(c, [(19, 92 + ey), (32, 99 + ey), (52, 98 + ey), (66, 90 + ey)], INK, 3, 1, True)
    fill(c, E(46, 78 + ey, 5, 3), H("#ffb0a8"), .6)
    fill(c, E(92, 90 + (ey * .8), 4.5), dk(g, .22))
    fill(c, E(82, 78 + (ey * .8), 3.2), dk(g, .22))


def turtle_shell_shape(c, cx=66, by=112, sc=1.0, spikes=True, col=H("#7f6bc6")):
    sx = lambda x: cx + x * sc
    sy = lambda y: by + y * sc
    if spikes:
        for x, y in ((-34, -38), (-10, -62), (20, -60), (42, -34)):
            pts = [(sx(x - 9), sy(y + 7)), (sx(x + (x / 10) * .8), sy(y - 15)), (sx(x + 9), sy(y + 7))]
            shape(c, L(pts), H("#ffe39a"), lw=2.6, d=4)
    dome = S([(sx(-56), sy(-6)), (sx(-46), sy(-38)), (sx(-16), sy(-58)), (sx(18), sy(-58)), (sx(48), sy(-38)), (sx(56), sy(-6)), (sx(30), sy(4)), (sx(-30), sy(4))])
    shape(c, dome, col, lw=2.6, d=13 * sc, rim=.42)
    c.save()
    clip_to(c, dome)
    plate = lt(col, .3)
    hexes = ((0, -38, 15), (-30, -24, 13), (30, -24, 13))
    for (x, y, r_) in hexes:
        pts = [(sx(x + math.cos(i * TAU / 6) * r_), sy(y + math.sin(i * TAU / 6) * r_ * .86)) for i in range(6)]
        fill(c, L(pts), plate)
        stroke(c, L(pts), INK, 2.2)
    c.restore()
    # steel rim band: armoured
    band = S([(sx(-57), sy(-7)), (sx(-30), sy(-1)), (sx(30), sy(-1)), (sx(57), sy(-7)), (sx(50), sy(6)), (sx(-50), sy(6))])
    shape(c, band, H("#c7cfdb"), lw=2.6, d=3, rim=.5)
    for x in (-36, -12, 12, 36):
        rivet(c, sx(x), sy(2), 2.2 * sc, H("#6c7890"))


def turtle(c, f):
    skin = H("#8ccf5e")
    p = 4 if f else -4
    for x, o in ((40, p), (92, -p)):
        fill(c, R(x + o - 8, 100, 18, 20, 7), skin)
        stroke(c, R(x + o - 8, 100, 18, 20, 7), INK, 2.4)
    shape(c, S([(102, 100), (118, 106), (104, 110)]), skin, lw=2.4, d=2)
    turtle_shell_shape(c, 66, 104, .96)
    # head out at the left
    shape(c, S([(6, 80), (14, 62), (34, 62), (44, 78), (38, 96), (14, 98)]), skin, lw=2.6, d=7)
    eye(c, 18, 74, 7, (-2, .5), lid=.75)
    line(c, [(5, 88), (16, 90), (26, 87)], INK, 2.6, 1, True)


def turtle_shell(c):
    turtle_shell_shape(c, 64, 110, 1.02)
    # peeking eyes in the shell mouth
    fill(c, R(38, 96, 52, 12, 6), H("#1b1830"))
    for x in (52, 72):
        fill(c, E(x, 102, 3.6, 3.2), H("#ffe066"))


def mimic(c, f):
    """Frame b: the crate with its lid lifted, full of teeth. Frame a is the 96x120 mimic_a block."""
    # base of the crate, same footprint and colours as block_factory, bottom-aligned (feet at y=120)
    wood, wood2, dark = H("#dea866"), H("#b67c42"), H("#3b1525")
    ox = 16
    # hinge at the back: lid tilts up from the far edge, mouth faces the viewer
    shape(c, R(ox + 2, 60, 92, 60, 7), wood2, lw=3.4, d=7, rim=.2)
    fill(c, R(ox + 8, 56, 80, 34, 8), dark)
    # tongue + lower teeth
    fill(c, S([(ox + 26, 88), (ox + 48, 70), (ox + 70, 88), (ox + 48, 92)]), H("#f0668a"))
    line(c, [(ox + 48, 74), (ox + 48, 88)], H("#b53b63"), 2)
    for k in range(6):
        x = ox + 13 + k * 13.2
        t = L([(x - 6, 92), (x, 76), (x + 6, 92)])
        fill(c, t, WHITE)
        stroke(c, t, INK, 2)
    for x in (ox + 16, ox + 48, ox + 80):
        line(c, [(x, 95), (x, 118)], H("#855a31"), 1.8, .9)
    for x in (ox + 10, ox + 86):
        rivet(c, x, 106, 3, H("#aeb7c4"))
    # lid raised (top face seen foreshortened)
    bounce = -3 if f else 0
    lid = R(ox, 10 + bounce, 96, 50, 8)
    shape(c, lid, wood, lw=3.4, d=7, rim=.4)
    for (y, h) in ((14 + bounce, 8), (46 + bounce, 8)):
        fill(c, R(ox + 6, y, 84, h, 2), H("#efc48a"))
        stroke(c, R(ox + 6, y, 84, h, 2), H("#9c6c3a"), 1.8)
    for k in range(6):
        x = ox + 11 + k * 14.6
        t = L([(x - 7, 58 + bounce), (x, 76 + bounce), (x + 7, 58 + bounce)])
        fill(c, t, WHITE)
        stroke(c, t, INK, 2)
    # angry eyes on the lid
    for x, s in ((ox + 32, 1), (ox + 64, -1)):
        fill(c, E(x, 35 + bounce, 10, 9.5), H("#ffe066"))
        stroke(c, E(x, 35 + bounce, 10, 9.5), INK, 2.4)
        fill(c, E(x - 2, 36 + bounce, 4.4, 5), INK)
        line(c, [(x - 12 * s, 22 + bounce), (x + 11 * s, 30 + bounce)], INK, 4.4)


def wasp(c, f):
    yel = H("#ffc62e")
    # wings, blur between two frames
    up = -5 if f else 8
    for (pts, a) in (([(70, 54), (90, 18 + up), (118, 14 + up), (112, 42 + up * .5), (84, 62)], .78),
                     ([(54, 54), (56, 12 + up), (84, 6 + up), (88, 34 + up * .5), (68, 60)], .88)):
        w = S(pts)
        fill(c, w, H("#d8f2ff"), a)
        stroke(c, w, INK, 2.6)
        line(c, [pts[0], pts[2]], H("#9fcfe6"), 1.8)
    # abdomen with a long angular stinger pointing back-right
    st = L([(106, 86), (127, 110), (98, 100)])
    shape(c, st, H("#3a3147"), lw=2.2, d=3)
    ab = S([(58, 66), (84, 60), (108, 78), (112, 98), (92, 106), (66, 96)])
    shape(c, ab, yel, lw=2.6, d=9)
    c.save()
    clip_to(c, ab)
    for x in (74, 92, 108):
        line(c, [(x - 8, 56), (x + 6, 112)], INK, 8.5)
    c.restore()
    stroke(c, ab, INK, 2.6)
    # thorax
    shape(c, E(50, 72, 17, 16), H("#e88b26"), lw=2.6, d=5)
    # legs tucked
    for x, y, q in ((42, 88, 34), (54, 90, 52), (64, 92, 68)):
        line(c, [(x, y), (q - 4, y + 13), (q - 10, y + 15)], INK, 3.6, 1, True)
    # head with mean eyes
    shape(c, E(26, 70, 20, 18), yel, lw=2.6, d=6)
    fill(c, S([(12, 66), (22, 60), (32, 70), (22, 78)]), H("#c8232c"))
    stroke(c, S([(12, 66), (22, 60), (32, 70), (22, 78)]), INK, 2.2)
    gloss(c, 19, 65, 3.5, 2, .85)
    line(c, [(8, 58), (32, 62)], INK, 3.6)
    line(c, [(20, 54), (10, 36), (2, 34)], INK, 3.2, 1, True)
    line(c, [(32, 54), (28, 36), (20, 30)], INK, 3.2, 1, True)
    line(c, [(9, 82), (6, 88)], INK, 3.2)
    line(c, [(19, 86), (17, 92)], INK, 3.2)


def make_enemies():
    fns = {"snail": snail, "beetle": beetle, "ghostmoth": ghostmoth, "fox": fox, "frog": frog, "turtle": turtle, "wasp": wasp}
    for n, fn in fns.items():
        for i, s in enumerate("ab"):
            cv = Cv(128, 128)
            c = cv.c
            # keep every creature inside the canvas with a little room for the outline and feet at y=120
            c.translate(64 + (2 if n == "fox" else 0), 117)
            c.scale(.92, .92)
            c.translate(-64, -120)
            if s == "b" and n in ("snail", "beetle", "fox", "turtle"):
                c.translate(0, -1.5)
            fn(c, i)
            done(cv, f"{n}_{s}", 1.8)
    cv = Cv(128, 128)
    c = cv.c
    c.translate(0, -2)
    mimic(c, 1)
    done(cv, "mimic_b", 0)
    cv = Cv(128, 128)
    c = cv.c
    c.translate(64, 117); c.scale(.94, .94); c.translate(-64, -120)
    turtle_shell(c)
    done(cv, "turtle_shell", 1.8)


# ================================================================== pomegranate bomb
POM = H("#d42a44")


def pom_body(cx, cy, r):
    pts = []
    for i in range(14):
        a = i * TAU / 14 - math.pi / 2
        bulge = 1 + .035 * math.cos(a * 3 + 1.57)
        pts.append((cx + math.cos(a) * r * 1.02 * bulge, cy + math.sin(a) * r * .9 * bulge))
    return S(pts)


def pom_crown(cx, top, r):
    cw = r * .42
    return L([(cx - cw * .8, top + r * .16), (cx - cw * 1.34, top - r * .4), (cx - cw * .6, top - r * .16), (cx - cw * .34, top - r * .54), (cx, top - r * .2),
              (cx + cw * .34, top - r * .54), (cx + cw * .6, top - r * .16), (cx + cw * 1.34, top - r * .4), (cx + cw * .8, top + r * .16), (cx, top + r * .26)])


def pomegranate(c, cx, cy, r, pulse=0, col=POM, crown=H("#f4a93a"), fuse=True, spark=True, glow=0, seed=H("#ff4a6e")):
    """Round fruit with a toothed crown, a little window of seeds, short lit fuse from the crown. Original look."""
    if glow:
        soft_ellipse(c, cx, cy, r * 1.18, r * 1.18, H("#ff8a3c"), glow)
    body = pom_body(cx, cy, r)
    shape(c, body, col, lw=r * .1, d=r * .5, rim=.4)
    c.save()
    clip_to(c, body)
    for sx in (-1, 1):
        line(c, [(cx + sx * r * .4, cy - r * .9), (cx + sx * r * .68, cy), (cx + sx * r * .38, cy + r * .9)], dk(col, .4), r * .05, .5, True)
    fill(c, E(cx - r * .1, cy + r * .5, r * .55, r * .3), H("#ff7a4a"), .22)
    c.restore()
    # seed window on the lower right
    wx, wy = cx + r * .36, cy + r * .28
    win = E(wx, wy, r * .4, r * .31, -.55)
    fill(c, win, H("#ffe9c9"))
    stroke(c, win, INK, r * .075)
    c.save()
    clip_to(c, E(wx, wy, r * .34, r * .25, -.55))
    fill(c, E(wx, wy, r * .5), H("#b81f3c"))
    for (dx, dy) in ((-.2, .1), (-.03, -.08), (.15, -.2), (0, .17), (.18, .02), (-.24, -.14), (.33, -.08), (.17, .22)):
        x, y = wx + dx * r, wy + dy * r
        fill(c, E(x, y, r * .1, r * .115), seed)
        fill(c, E(x - r * .03, y - r * .035, r * .03), WHITE, .9)
    c.restore()
    gloss(c, cx - r * .48, cy - r * .42, r * .26, r * .13, .85)
    fill(c, E(cx - r * .14, cy - r * .63, r * .07), H("#fffdf0"), .85)
    top = cy - r * .86
    shape(c, pom_crown(cx, top, r), crown, lw=r * .09, d=r * .14, rim=.45)
    if fuse:
        fx, fy = cx + r * .56, top - r * .5
        line(c, [(cx, top - r * .04), (cx + r * .14, top - r * .34), (fx, fy)], INK, r * .17, 1, True)
        line(c, [(cx, top - r * .04), (cx + r * .14, top - r * .34), (fx, fy)], H("#e8d3a0"), r * .08, 1, True)
        if spark:
            s_ = r * (.25 + pulse * .08)
            pts = []
            for i in range(14):
                a = i * TAU / 14 + pulse * .2
                rr = s_ * (1.3 if i % 2 == 0 else .62)
                pts.append((fx + math.cos(a) * rr, fy + math.sin(a) * rr))
            fill(c, L(pts), H("#ff8a2a"))
            stroke(c, L(pts), INK, r * .06)
            fill(c, E(fx, fy, s_ * .46), H("#fff3b0"))


def make_bomb():
    for i, s in enumerate("ab"):
        cv = Cv(96, 96)
        c = cv.c
        soft_ellipse(c, 48, 85, 31, 7.5, COOL, .34)
        if i == 0:
            pomegranate(c, 46, 55, 30, 0)
        else:
            pomegranate(c, 46, 58.5, 31.5, 1, col=H("#ee3a50"), glow=.32)
        done(cv, f"bomb_{s}")
    cv = Cv(48, 48, 8)
    pomegranate(cv.c, 23, 28, 15, 0)
    done(cv, "bomb_mini")


# ================================================================== flames
FL = (H("#e8401e"), H("#ff9226"), H("#ffd84a"), H("#fff8d0"))     # rim, body, inner, core
HALF = (33, 25, 16, 8)        # half-width of each layer at the tile edge


def wave(x, phase, amp):
    # period 48 divides the tile, so tile edges always line up
    return amp * math.sin(TAU * x / 48 + phase) + amp * .45 * math.sin(TAU * x / 32 + phase * 2.1)


def band_poly(layer, phase, x0=-8, x1=104, taper=None):
    """Horizontal band polygon around y=48. taper(x) in 0..1 scales the half width."""
    h = HALF[layer]
    amp = (3.4, 3.0, 2.4, 1.6)[layer]
    up, dn = [], []
    n = int((x1 - x0) / 2)
    for i in range(n + 1):
        x = x0 + (x1 - x0) * i / n
        s = 1 if taper is None else taper(x)
        up.append((x, 48 - s * (h + wave(x, phase + layer * .7, amp))))
        dn.append((x, 48 + s * (h + wave(x, phase + 1.9 + layer * .7, amp))))
    return up + dn[::-1]


def tip_taper(end):
    """Band keeps its full width until x=30, then rounds off to 0 at x=end."""
    def t(x):
        if x <= 30:
            return 1.0
        u = min(1, (x - 30) / (end - 30))
        return max(0, (1 - u ** 2.2)) ** .6
    return t


def flame_tile(c, kind, frame):
    ph = 0 if frame == 0 else 2.4
    polys = []
    for layer in range(4):
        if kind == "h":
            polys.append([band_poly(layer, ph)])
        elif kind == "tip":
            end = 92 - layer * 9 + frame * 3
            polys.append([band_poly(layer, ph, -8, end, tip_taper(end))])
        else:   # centre: horizontal + vertical band + hub
            polys.append([band_poly(layer, ph), "v", band_poly(layer, ph)])
    if kind == "c":
        r_list = (43, 34, 24, 13)
        for layer in range(4):
            col = FL[layer]
            draw = []
            for k, ang in enumerate((0, math.pi / 2)):
                draw.append((band_poly(layer, ph), ang))
            if layer == 0:
                for pts, ang in draw:
                    c.save(); rot_about(c, ang, 48, 48)
                    stroke(c, L(pts), H("#a81f1a"), 4)
                    c.restore()
                pts = hub(r_list[0], ph)
                stroke(c, L(pts), H("#a81f1a"), 4)
            for pts, ang in draw:
                c.save(); rot_about(c, ang, 48, 48)
                fill(c, L(pts), col)
                c.restore()
            fill(c, L(hub(r_list[layer], ph + layer)), col)
    else:
        stroke(c, L(polys[0][0]), H("#a81f1a"), 4)
        for layer in range(4):
            fill(c, L(polys[layer][0]), FL[layer])
    # sparks
    r = random.Random(40 + frame + (7 if kind == "c" else 0) + (3 if kind == "tip" else 0))
    for i in range(5):
        x, y = r.uniform(10, 86), r.uniform(26, 70)
        fill(c, E(x, y, r.uniform(1.2, 2.2)), H("#fffbe0"), .9)


def hub(r_, ph):
    pts = []
    for i in range(32):
        a = i * TAU / 32
        rr = r_ * (1 + .1 * math.sin(a * 8 + ph * 2))
        pts.append((48 + math.cos(a) * rr, 48 + math.sin(a) * rr))
    return pts


def make_flames():
    for i, s in enumerate("ab"):
        cv = Cv(96, 96); flame_tile(cv.c, "c", i); done(cv, f"flame_c_{s}")
        cv = Cv(96, 96); flame_tile(cv.c, "h", i); done(cv, f"flame_h_{s}")
        cv = Cv(96, 96); c = cv.c; rot_about(c, math.pi / 2, 48, 48); flame_tile(c, "h", i); done(cv, f"flame_v_{s}")
        # a tip's open end must meet the neighbouring tile: tip pointing right is open at the left edge
        # l and u are mirrors, not rotations: a rotation would swap the wave profile at the open end
        for d in "rldu":
            cv = Cv(96, 96)
            c = cv.c
            if d == "l":
                c.translate(96, 0); c.scale(-1, 1)
            elif d == "u":
                c.translate(0, 96); c.scale(1, -1)
            if d in "du":
                rot_about(c, math.pi / 2, 48, 48)
            flame_tile(c, "tip", i)
            done(cv, f"flame_{d}_{s}")


# ================================================================== icons shared by power-ups and HUD
def ic_flame(c, cx, cy, s):
    outer = S([(cx, cy - 34 * s), (cx + 11 * s, cy - 14 * s), (cx + 26 * s, cy - 6 * s), (cx + 27 * s, cy + 14 * s), (cx + 14 * s, cy + 31 * s),
               (cx - 14 * s, cy + 31 * s), (cx - 27 * s, cy + 14 * s), (cx - 24 * s, cy - 8 * s), (cx - 14 * s, cy - 4 * s), (cx - 12 * s, cy - 20 * s)])
    shape(c, outer, H("#ff7a2a"), lw=3 * s, d=9 * s, rim=.3)
    inner = S([(cx + 1 * s, cy - 8 * s), (cx + 13 * s, cy + 8 * s), (cx + 11 * s, cy + 23 * s), (cx - 10 * s, cy + 23 * s), (cx - 13 * s, cy + 9 * s), (cx - 4 * s, cy + 4 * s)])
    fill(c, inner, H("#ffd84a"))
    fill(c, E(cx, cy + 16 * s, 6 * s, 7 * s), H("#fff8d0"))


def ic_speed(c, cx, cy, s):
    # a running sneaker with a little wing
    for k, y in enumerate((-12, 0, 12)):
        line(c, [(cx - (37 - k * 3) * s, cy + y * s), (cx - (20 + k * 2) * s, cy + y * s)], INK, 6.5 * s)
        line(c, [(cx - (37 - k * 3) * s, cy + y * s), (cx - (20 + k * 2) * s, cy + y * s)], H("#fff4c9"), 3 * s)
    shoe = S([(cx - 15 * s, cy - 14 * s), (cx + 6 * s, cy - 15 * s), (cx + 12 * s, cy - 2 * s), (cx + 30 * s, cy + 6 * s), (cx + 33 * s, cy + 19 * s), (cx - 16 * s, cy + 19 * s)], k=1 / 8)
    shape(c, shoe, H("#ff5a5f"), lw=3 * s, d=7 * s)
    sole = R(cx - 18 * s, cy + 15 * s, 52 * s, 9 * s, 4 * s)
    shape(c, sole, H("#fff4dc"), lw=3 * s, d=2 * s, rim=0)
    for k in range(3):
        line(c, [(cx + (2 + k * 7) * s, cy - (5 - k * 3.4) * s), (cx + (9 + k * 7) * s, cy - (9 - k * 4) * s)], WHITE, 2.6 * s)
    w = S([(cx - 15 * s, cy - 6 * s), (cx - 24 * s, cy - 24 * s), (cx - 6 * s, cy - 30 * s), (cx + 4 * s, cy - 15 * s)])
    shape(c, w, H("#fffdf0"), lw=2.8 * s, d=4 * s, rim=0)
    line(c, [(cx - 14 * s, cy - 12 * s), (cx - 12 * s, cy - 22 * s)], H("#9fb4d8"), 1.8 * s)
    line(c, [(cx - 8 * s, cy - 12 * s), (cx - 5 * s, cy - 22 * s)], H("#9fb4d8"), 1.8 * s)


def ic_kick(c, cx, cy, s):
    # work boot kicking a mini pomegranate
    star(c, cx + 19 * s, cy - 7 * s, 26 * s, H("#ffd23f"), 2.6 * s)
    pomegranate(c, cx + 22 * s, cy - 4 * s, 15 * s, fuse=False)
    c.save()
    c.translate(cx - 15 * s, cy + 8 * s)
    c.rotate(-.42)
    leg = R(-20 * s, -26 * s, 22 * s, 30 * s, 4 * s)
    shape(c, leg, H("#2f7fd6"), lw=3 * s, d=5 * s)
    b = S([(-22 * s, -8 * s), (2 * s, -8 * s), (8 * s, 0), (26 * s, 4 * s), (29 * s, 16 * s), (-22 * s, 16 * s)], k=1 / 8)
    shape(c, b, BOOT, lw=3 * s, d=6 * s)
    shape(c, R(-24 * s, 13 * s, 55 * s, 9 * s, 3 * s), H("#5a3320"), lw=3 * s, d=2 * s, rim=0)
    c.restore()


def shield_path(cx, cy, s):
    return S([(cx - 28 * s, cy - 26 * s), (cx, cy - 34 * s), (cx + 28 * s, cy - 26 * s), (cx + 28 * s, cy + 2 * s), (cx + 16 * s, cy + 24 * s), (cx, cy + 36 * s),
              (cx - 16 * s, cy + 24 * s), (cx - 28 * s, cy + 2 * s)], k=1 / 7)


def ic_shield(c, cx, cy, s):
    shape(c, shield_path(cx, cy, s), H("#fff0c4"), lw=3 * s, d=4 * s, rim=.2)
    shape(c, shield_path(cx, cy, s * .74), H("#35a4ea"), lw=2.4 * s, d=7 * s, rim=.4)
    star(c, cx, cy, 14 * s, H("#ffe066"), 2.2 * s)


def ic_bomb(c, cx, cy, s):
    pomegranate(c, cx - 2 * s, cy + 6 * s, 28 * s)


def ic_clock(c, cx, cy, s):
    for sx in (-1, 1):
        line(c, [(cx + sx * 18 * s, cy + 22 * s), (cx + sx * 24 * s, cy + 33 * s)], INK, 6 * s)
        shape(c, E(cx + sx * 21 * s, cy - 27 * s, 11 * s, 8 * s, sx * .6), H("#ffc62e"), lw=2.8 * s, d=3 * s)
    shape(c, E(cx, cy + 2 * s, 31 * s), H("#e8483f"), lw=3 * s, d=7 * s)
    fill(c, E(cx, cy + 2 * s, 23 * s), H("#fff9ec"))
    stroke(c, E(cx, cy + 2 * s, 23 * s), INK, 2.4 * s)
    for k in range(4):
        a = k * TAU / 4
        line(c, [(cx + math.cos(a) * 18 * s, cy + 2 * s + math.sin(a) * 18 * s), (cx + math.cos(a) * 20.5 * s, cy + 2 * s + math.sin(a) * 20.5 * s)], INK, 2.4 * s)
    line(c, [(cx, cy + 2 * s), (cx, cy - 12 * s)], INK, 3.6 * s)
    line(c, [(cx, cy + 2 * s), (cx + 11 * s, cy + 8 * s)], H("#e8483f"), 3.6 * s)
    fill(c, E(cx, cy + 2 * s, 3 * s), INK)


def heart_path(cx, cy, s):
    return S([(cx, cy - 16 * s), (cx + 15 * s, cy - 29 * s), (cx + 32 * s, cy - 20 * s), (cx + 31 * s, cy + 2 * s), (cx + 14 * s, cy + 20 * s), (cx, cy + 32 * s),
              (cx - 14 * s, cy + 20 * s), (cx - 31 * s, cy + 2 * s), (cx - 32 * s, cy - 20 * s), (cx - 15 * s, cy - 29 * s)], k=1 / 6.5)


def ic_heart(c, cx, cy, s):
    shape(c, heart_path(cx, cy, s), H("#ff4d6a"), lw=3.2 * s, d=11 * s, rim=.4)
    gloss(c, cx - 15 * s, cy - 14 * s, 8 * s, 4.5 * s, .8)


def ic_star(c, cx, cy, s):
    pts = []
    for i in range(10):
        a = -math.pi / 2 + i * TAU / 10
        rr = 37 * s if i % 2 == 0 else 18.5 * s
        pts.append((cx + math.cos(a) * rr, cy + 2 * s + math.sin(a) * rr))
    c.save()
    c.set_line_join(cairo.LINE_JOIN_ROUND)
    stroke(c, L(pts), INK, 7 * s)
    shape(c, L(pts), H("#ffd23f"), lw=0, d=9 * s, rim=.45)
    stroke(c, L(pts), H("#ffd23f"), 3 * s)
    c.restore()


def ic_lock(c, cx, cy, s):
    sh = R(cx - 18 * s, cy - 32 * s, 36 * s, 46 * s, 17 * s)
    stroke(c, sh, INK, 13 * s)
    stroke(c, sh, H("#c7cfdb"), 7 * s)
    body = R(cx - 29 * s, cy - 8 * s, 58 * s, 42 * s, 8 * s)
    shape(c, body, H("#ffc62e"), lw=3.2 * s, d=8 * s)
    fill(c, E(cx, cy + 8 * s, 6.5 * s), INK)
    fill(c, L([(cx - 3.5 * s, cy + 9 * s), (cx + 3.5 * s, cy + 9 * s), (cx + 5 * s, cy + 24 * s), (cx - 5 * s, cy + 24 * s)]), INK)


def ic_check(c, cx, cy, s):
    pts = [(cx - 30 * s, cy + 2 * s), (cx - 10 * s, cy + 24 * s), (cx + 31 * s, cy - 24 * s)]
    line(c, pts, INK, 21 * s)
    line(c, pts, H("#3fd05c"), 13 * s)
    line(c, [(pts[0][0] + 1, pts[0][1] - 3 * s), (pts[1][0], pts[1][1] - 4 * s), (pts[2][0] - 1, pts[2][1] - 3 * s)], H("#b6f7a8"), 3.6 * s, .85)


def badge(c, col):
    soft_ellipse(c, 49, 52, 45, 45, COOL, .35)
    fill(c, E(48, 48, 43), INK)
    fill(c, E(48, 48, 40), lin(10, 10, 86, 86, (0, H("#fff2b8")), (.5, H("#ffc93c")), (1, H("#d58a1e"))))
    fill(c, E(48, 48, 33), INK)
    fill(c, E(48, 48, 31), rad(40, 36, 2, 42, (0, lt(col, .3)), (.6, col), (1, dk(col, .3))))
    c.save()
    clip_to(c, E(48, 48, 31))
    fill(c, E(40, 24, 34, 20), WHITE, .14)
    c.restore()


def make_items():
    pu = (("bomb", H("#17a398"), ic_bomb, .74), ("flame", H("#6a4bd6"), ic_flame, .74), ("speed", H("#2a86e0"), ic_speed, .68),
          ("kick", H("#3bb04f"), ic_kick, .68), ("shield", H("#ef7d32"), ic_shield, .72))
    for n, col, fn, s in pu:
        cv = Cv(96, 96); badge(cv.c, col); fn(cv.c, 48, 48, s); done(cv, f"pu_{n}")
    hud = (("bomb", ic_bomb, 1.08), ("flame", ic_flame, 1.15), ("speed", ic_speed, 1.08), ("kick", ic_kick, 1.02), ("shield", ic_shield, 1.1),
           ("clock", ic_clock, 1.1), ("heart", ic_heart, 1.15), ("star", ic_star, 1.1), ("lock", ic_lock, 1.1), ("check", ic_check, 1.1))
    for n, fn, s in hud:
        cv = Cv(96, 96); fn(cv.c, 48, 48, s); done(cv, f"hud_{n}", 1)
    # golden secret token
    cv = Cv(96, 96)
    c = cv.c
    soft_ellipse(c, 48, 50, 46, 46, H("#ffe27a"), .75)
    pomegranate(c, 48, 56, 29, col=H("#f2b92e"), crown=H("#fff0a8"), fuse=False, seed=H("#ffef9e"))
    for (x, y, s) in ((16, 26, 7), (80, 22, 9), (82, 72, 6), (14, 72, 5)):
        sparkle(c, x, y, s)
    done(cv, "secret_token")
    # shield bubble
    cv = Cv(128, 160)
    c = cv.c
    b = E(64, 80, 59, 71)
    fill(c, b, rad(64, 80, 20, 71, (0, H("#7fd4ff"), .08), (.7, H("#6cc8ff"), .16), (1, H("#2f9df0"), .46)))
    stroke(c, b, H("#2f8fe0"), 3.4, .85)
    stroke(c, E(64, 80, 55.5, 67.5), H("#d8f4ff"), 2, .7)
    c.save()
    clip_to(c, b)
    line(c, [(22, 66), (28, 42), (46, 24)], WHITE, 7, .75, True)
    fill(c, E(55, 20, 4, 3), WHITE, .75)
    line(c, [(108, 102), (100, 120), (86, 132)], WHITE, 3.6, .4, True)
    c.restore()
    done(cv, "shield")
    # projectiles
    cv = Cv(96, 96); shot_trash(cv.c); done(cv, "shot_trash", 1.4)
    cv = Cv(96, 96); shot_sting(cv.c); done(cv, "shot_sting", 1.4)
    cv = Cv(96, 96); shot_gear(cv.c); done(cv, "shot_gear", 1.4)
    cv = Cv(96, 96); shot_ember(cv.c); done(cv, "shot_ember", 1.4)
    # telegraph
    cv = Cv(96, 96)
    c = cv.c
    fill(c, R(3, 3, 90, 90, 12), H("#ff2d2d"), .32)
    c.save()
    clip_to(c, R(3, 3, 90, 90, 12))
    for i in range(-6, 8):
        x = i * 22
        fill(c, L([(x, 96), (x + 11, 96), (x + 107, 0), (x + 96, 0)]), H("#ff2d2d"), .24)
    c.restore()
    stroke(c, R(4.5, 4.5, 87, 87, 11), H("#ff2d2d"), 5, .9)
    stroke(c, R(4.5, 4.5, 87, 87, 11), H("#ffd0c0"), 1.4, .75)
    fill(c, R(43, 22, 10, 34, 5), H("#ffefe6"), .95)
    fill(c, E(48, 69, 6.5), H("#ffefe6"), .95)
    stroke(c, R(43, 22, 10, 34, 5), H("#a81414"), 2, .8)
    stroke(c, E(48, 69, 6.5), H("#a81414"), 2, .8)
    done(cv, "warn_cell")


def sparkle(c, x, y, s, col=WHITE):
    pts = []
    for i in range(8):
        a = i * TAU / 8 - math.pi / 2
        rr = s if i % 2 == 0 else s * .26
        pts.append((x + math.cos(a) * rr, y + math.sin(a) * rr))
    stroke(c, L(pts), H("#b5791a"), 2.4)
    fill(c, L(pts), col)


def shot_trash(c):
    col = H("#4f5b74")
    bag = S([(48, 32), (70, 42), (82, 64), (72, 84), (48, 88), (24, 84), (14, 64), (26, 42)])
    shape(c, bag, col, lw=2.8, d=13, rim=.42)
    line(c, [(40, 44), (34, 62)], lt(col, .3), 2.4, .85)
    line(c, [(58, 46), (66, 64)], dk(col, .35), 2.4, .85)
    gloss(c, 30, 54, 7, 3.4, .5)
    t = L([(42, 34), (34, 12), (48, 22), (62, 12), (54, 34)])
    shape(c, t, col, lw=2.8, d=3)
    fill(c, R(38, 28, 20, 8, 3), H("#ffc62e"))
    stroke(c, R(38, 28, 20, 8, 3), INK, 2.4)
    # banana peel sticking out of a tear
    shape(c, S([(66, 68), (82, 60), (88, 72), (76, 80)]), H("#ffe05a"), lw=2.4, d=3)
    line(c, [(6, 40), (14, 34), (10, 28)], H("#9aa4b6"), 2.4, 1, True)
    line(c, [(84, 30), (90, 24), (84, 18)], H("#9aa4b6"), 2.4, 1, True)


def shot_sting(c):
    c.save()
    rot_about(c, .75, 48, 48)
    for k, a in enumerate((.5, .32, .18)):
        fill(c, E(48 - 0, 76 + k * 8, 11 - k * 3), H("#b48cff"), a)
    bulb = S([(48, 6), (58, 34), (66, 56), (58, 72), (38, 72), (30, 56), (38, 34)])
    shape(c, bulb, H("#e3a33f"), lw=2.8, d=9, rim=.42)
    tip = S([(48, 4), (55, 26), (41, 26)])
    fill(c, tip, H("#8f4bd8"))
    stroke(c, tip, INK, 2.2)
    for y in (46, 58):
        line(c, [(35, y), (48, y + 4), (61, y)], H("#a26a22"), 2.4, 1, True)
    c.restore()
    fill(c, E(26, 20, 3.5), H("#c58cff"))
    fill(c, E(16, 34, 2.4), H("#c58cff"))


def gear_path(cx, cy, r_, teeth=8, depth=.2):
    pts = []
    n = teeth * 4
    for i in range(n):
        a = i * TAU / n
        rr = r_ if i % 4 in (0, 1) else r_ * (1 - depth)
        pts.append((cx + math.cos(a) * rr, cy + math.sin(a) * rr))
    return L(pts)


def shot_gear(c):
    shape(c, gear_path(48, 48, 36, 8), H("#b7c2d2"), lw=2.8, d=11, rim=.45)
    fill(c, E(48, 48, 19), H("#7b889f"))
    stroke(c, E(48, 48, 19), INK, 2.6)
    fill(c, E(48, 48, 8.5), H("#2f3648"))
    stroke(c, E(48, 48, 8.5), INK, 2.2)
    for k in range(4):
        a = k * TAU / 4 + .4
        fill(c, E(48 + math.cos(a) * 14, 48 + math.sin(a) * 14, 2), H("#dfe5ee"))


def shot_ember(c):
    tail = S([(72, 18), (86, 8), (82, 28), (92, 34), (72, 46), (56, 66), (34, 40)])
    shape(c, tail, H("#ff7a2a"), lw=2.8, d=7, rim=.2)
    fill(c, S([(66, 30), (76, 22), (74, 34), (62, 56), (46, 42)]), H("#ffd84a"))
    rock = S([(20, 46), (34, 28), (56, 32), (70, 52), (62, 76), (38, 84), (18, 70)], k=1 / 8)
    shape(c, rock, H("#47405a"), lw=2.8, d=11, rim=.3)
    for pts in ([(30, 44), (42, 54), (36, 72)], [(42, 54), (58, 52)], [(58, 52), (56, 66)]):
        line(c, pts, H("#ffb03c"), 4)
        line(c, pts, H("#fff0a0"), 1.6)


# ================================================================== cars
def car(c, col):
    soft_ellipse(c, 96, 100, 90, 10, COOL, .34)
    body = S([(10, 66), (22, 50), (62, 46), (82, 18), (130, 18), (152, 46), (178, 54), (186, 72), (182, 90), (10, 90)], k=1 / 8)
    # wheels first (body overlaps them)
    shape(c, body, col, lw=3.4, d=12, rim=.42)
    cab = S([(70, 46), (88, 25), (124, 25), (142, 46)], k=1 / 12)
    fill(c, cab, H("#26324d"))
    fill(c, S([(74, 46), (90, 27), (104, 27), (104, 46)], k=1 / 14), lin(70, 25, 110, 46, (0, H("#d8f4ff")), (1, H("#7fc4ec"))))
    fill(c, S([(110, 46), (110, 27), (122, 27), (138, 46)], k=1 / 14), lin(110, 25, 140, 46, (0, H("#d8f4ff")), (1, H("#7fc4ec"))))
    line(c, [(84, 42), (96, 30)], WHITE, 4, .8)
    stroke(c, cab, INK, 2.8)
    line(c, [(107, 26), (107, 46)], INK, 3)
    # door, trim
    line(c, [(107, 48), (107, 86)], dk(col, .5), 2.2)
    fill(c, R(114, 56, 14, 5, 2.5), H("#e6ecf3"))
    stroke(c, R(114, 56, 14, 5, 2.5), INK, 1.6)
    line(c, [(14, 72), (184, 72)], lt(col, .4), 2.4, .7)
    # lamps and bumpers
    shape(c, E(176, 66, 9, 7), H("#fff3b0"), lw=2.6, d=2, rim=0)
    shape(c, R(10, 62, 9, 10, 3), H("#ff4d4d"), lw=2.4, d=2, rim=0)
    for (x, w) in ((6, 28), (160, 28)):
        shape(c, R(x, 82, w, 10, 4), H("#c7cfdb"), lw=2.6, d=2.5)
    for x in (52, 144):
        fill(c, E(x, 86, 23, 22), INK)
        shape(c, E(x, 88, 19), H("#3a3f55"), lw=0, d=5, rim=.2)
        fill(c, E(x, 88, 10), H("#d5dce6"))
        stroke(c, E(x, 88, 10), INK, 2.2)
        fill(c, E(x, 88, 3), H("#6c7890"))


def make_cars():
    for n, col in (("red", H("#e8483f")), ("blue", H("#3a8fe6")), ("yellow", H("#ffc62e"))):
        cv = Cv(192, 112)
        cv.c.translate(96, 106)
        cv.c.scale(.96, .96)
        cv.c.translate(-96, -108)
        car(cv.c, col)
        done(cv, f"car_{n}")


# ================================================================== bosses (288x288, feet near bottom centre)
def tint(im, col, a):
    """Flash colour over the opaque area, alpha preserved."""
    ov = Image.new("RGBA", im.size, tuple(int(x * 255) for x in col) + (int(a * 255),))
    out = Image.alpha_composite(im, ov)
    out.putalpha(im.getchannel("A"))
    return out


def hazard(c, x, y, w, h, step=22):
    c.save()
    clip_to(c, R(x, y, w, h, 3))
    fill(c, R(x, y, w, h, 0), H("#ffc62e"))
    i = -2
    while i * step < w + h:
        xx = x + i * step
        fill(c, L([(xx, y + h), (xx + step / 2, y + h), (xx + step / 2 + h, y), (xx + h, y)]), H("#2b2a3d"))
        i += 1
    c.restore()
    stroke(c, R(x, y, w, h, 3), INK, 3)


def angry_eye(c, x, y, r_, sx, col=H("#ffe066"), hurt=False, pupil=True):
    """sx=+1 for the eye on the viewer's left. Heavy slanted brow."""
    shape(c, E(x, y, r_, r_ * .96), col, lw=3.4, d=5, rim=.3)
    if hurt:
        line(c, [(x - r_ * .55, y - r_ * .55), (x + r_ * .55, y + r_ * .55)], INK, 6)
        line(c, [(x + r_ * .55, y - r_ * .55), (x - r_ * .55, y + r_ * .55)], INK, 6)
        return
    if pupil:
        fill(c, E(x + sx * r_ * .22, y + r_ * .16, r_ * .4, r_ * .46), INK)
        fill(c, E(x + sx * r_ * .22 - r_ * .14, y + r_ * .02, r_ * .12), WHITE)


def truck_arm(c, sx, st):
    """sx -1 left arm, +1 right. st: 'idle_a','idle_b','attack','hurt'"""
    sh = (144 + sx * 96, 150)
    if st == "attack":
        el, hand = (144 + sx * 128, 106), (144 + sx * 118, 46)
    elif st == "hurt":
        el, hand = (144 + sx * 118, 176), (144 + sx * 122, 214)
    else:
        o = 6 if st == "idle_b" else 0
        el, hand = (144 + sx * 128, 170 + o), (144 + sx * 116, 222 - o)
    for a, b in ((sh, el), (el, hand)):
        line(c, [a, b], INK, 21)
        line(c, [a, b], H("#c7cfdb"), 14)
        line(c, [(a[0] - 2, a[1] - 3), (b[0] - 2, b[1] - 3)], WHITE, 3, .55)
    shape(c, E(el[0], el[1], 13), H("#ffc62e"), lw=3, d=4)
    ang = math.atan2(hand[1] - el[1], hand[0] - el[0])
    c.save()
    c.translate(*hand)
    c.rotate(ang - math.pi / 2)
    for q in (-1, 1):
        cl = S([(q * 5, 0), (q * 20, 8), (q * 18, 28), (q * 6, 36), (q * 11, 22), (q * 7, 10)], k=1 / 7)
        shape(c, cl, H("#ffc62e"), lw=3, d=4)
    shape(c, E(0, 0, 13), H("#6c7890"), lw=3, d=4)
    c.restore()


def boss1(c, st):
    g = H("#3fae6a")
    bob = {"idle_a": 0, "idle_b": 5, "attack": -6, "hurt": 8}[st]
    for x in (72, 216):     # wheels
        fill(c, E(x, 246, 37), INK)
        shape(c, E(x, 246, 32), H("#3a3f55"), lw=0, d=8, rim=.2)
        fill(c, E(x, 246, 16), H("#d5dce6"))
        stroke(c, E(x, 246, 16), INK, 3)
        for k in range(5):
            a = k * TAU / 5 + (.5 if st == "idle_b" else 0)
            fill(c, E(x + math.cos(a) * 9, 246 + math.sin(a) * 9, 2.4), H("#6c7890"))
    c.save()
    c.translate(0, bob)
    truck_arm(c, -1, st)
    truck_arm(c, 1, st)
    # heap of rubbish in the hopper
    heap = [(82, 86, 30, H("#54607a")), (206, 88, 30, H("#4a7f92")), (118, 70, 32, H("#6d5a8a")), (172, 72, 32, H("#54607a"))]
    for (x, y, r_, col) in heap:
        shape(c, E(x, y, r_, r_ * .86), col, lw=3.2, d=9, rim=.4)
        line(c, [(x - 9, y - 14), (x + 4, y - 3)], lt(col, .35), 2.6, .7)
    # tin can, fish bone, banana
    shape(c, R(202, 40, 28, 38, 4), H("#e8483f"), lw=3, d=6)
    fill(c, R(202, 52, 28, 12, 0), H("#fff4dc"))
    stroke(c, R(202, 40, 28, 38, 4), INK, 3)
    shape(c, S([(50, 74), (58, 46), (74, 34), (70, 54), (66, 76)]), H("#ffe05a"), lw=3, d=4)
    # crown of the King of Rubbish
    cr = L([(112, 62), (104, 16), (128, 40), (144, 8), (160, 40), (184, 16), (176, 62)])
    c.save()
    rot_about(c, -.08 if st != "hurt" else .3, 144, 60)
    shape(c, cr, H("#ffd23f"), lw=3.4, d=9, rim=.5)
    for (x, y, col) in ((120, 52, H("#e8483f")), (144, 50, H("#3fd0e0")), (168, 52, H("#e8483f"))):
        fill(c, E(x, y, 5), col)
        stroke(c, E(x, y, 5), INK, 2)
    c.restore()
    # hopper rim + body
    body = R(46, 92, 196, 146, 22)
    shape(c, body, g, lw=4, d=18, rim=.4)
    shape(c, R(36, 84, 216, 24, 10), lt(g, .2), lw=4, d=6, rim=.5)
    for x in (58, 230):
        rivet(c, x, 96, 4, H("#d5dce6"))
    # brow plate (angry visor)
    hurt = st == "hurt"
    mad = st == "attack"
    eyecol = H("#ff5a3c") if mad else H("#fff0a0")
    angry_eye(c, 98, 150, 27, 1, eyecol, hurt)
    angry_eye(c, 190, 150, 27, -1, eyecol, hurt)
    if not hurt:
        fill(c, L([(62, 112), (138, 142), (138, 122), (66, 104)]), dk(g, .55))
        fill(c, L([(226, 112), (150, 142), (150, 122), (222, 104)]), dk(g, .55))
        stroke(c, L([(62, 112), (138, 142), (138, 122), (66, 104)]), INK, 3)
        stroke(c, L([(226, 112), (150, 142), (150, 122), (222, 104)]), INK, 3)
    # grille mouth
    mh = 56 if mad else 40
    my = 186
    m = R(76, my, 136, mh, 14)
    fill(c, m, H("#1a1826"))
    c.save()
    clip_to(c, m)
    if mad:
        fill(c, E(144, my + mh + 2, 44, 26), H("#c7425f"))
    for k in range(7):      # chrome teeth
        x = 84 + k * 19
        t = L([(x - 8, my - 2), (x + 9, my - 2), (x, my + (20 if not hurt else 12))])
        fill(c, t, H("#eef2f7"))
        stroke(c, t, INK, 2.2)
        t2 = L([(x - 8, my + mh + 2), (x + 9, my + mh + 2), (x, my + mh - 16)])
        fill(c, t2, H("#c7cfdb"))
        stroke(c, t2, INK, 2.2)
    c.restore()
    stroke(c, m, INK, 4)
    if mad:
        shape(c, E(144, my + 32, 20, 16), H("#4f5b74"), lw=3, d=5)
    c.restore()
    # bumper
    hazard(c, 40, 226 + bob, 208, 18)


def scorp_claw(c, sx, st):
    col = H("#f0a24a")
    if st == "attack":
        sh, el, hd = (144 + sx * 66, 186), (144 + sx * 112, 150), (144 + sx * 104, 84)
        op = .62
    elif st == "hurt":
        sh, el, hd = (144 + sx * 66, 196), (144 + sx * 104, 206), (144 + sx * 112, 160)
        op = .05
    else:
        o = 7 if st == "idle_b" else 0
        sh, el, hd = (144 + sx * 66, 190), (144 + sx * 112, 170 + o), (144 + sx * 102, 112 + o)
        op = .3 if st == "idle_a" else .2
    for a, b, w in ((sh, el, 24), (el, hd, 22)):
        line(c, [a, b], INK, w + 7)
        line(c, [a, b], dk(col, .1), w)
        line(c, [(a[0] - 2, a[1] - 5), (b[0] - 2, b[1] - 5)], lt(col, .4), 4, .7)
    shape(c, E(el[0], el[1], 17), col, lw=3.2, d=5)
    ang = math.atan2(hd[1] - el[1], hd[0] - el[0]) + math.pi / 2
    c.save()
    c.translate(*hd)
    c.rotate(ang)
    # pincers: two crescent blades
    c.save()
    c.rotate(-op * sx)
    outer = S([(-32 * sx * -1 * -1, 8), (-38 * sx, -30), (-14 * sx, -68), (-2 * sx, -44), (-10 * sx, -14), (6 * sx, 10)], k=1 / 7)
    shape(c, outer, col, lw=3.2, d=9, rim=.42)
    c.restore()
    c.save()
    c.rotate(op * sx)
    inner = S([(30 * sx, 10), (34 * sx, -24), (10 * sx, -52), (6 * sx, -30), (6 * sx, -10), (-4 * sx, 8)], k=1 / 7)
    shape(c, inner, dk(col, .1), lw=3.2, d=7, rim=.3)
    c.restore()
    shape(c, E(0, 6, 25, 22), col, lw=3.2, d=7, rim=.42)
    c.restore()


def boss2(c, st):
    col, lite = H("#e89a48"), H("#ffd089")
    bob = {"idle_a": 0, "idle_b": 4, "attack": -4, "hurt": 8}[st]
    # legs
    for sx in (-1, 1):
        for k, (x0, y0, x1, y1, x2, y2) in enumerate(((66, 214, 34, 216, 16, 258), (84, 232, 54, 244, 44, 274), (108, 244, 92, 258, 88, 278))):
            q = 3 if (st == "idle_b" and k % 2) else 0
            a = (144 + sx * (144 - x0), y0 + bob)
            b = (144 + sx * (144 - x1), y1 + bob - q)
            d_ = (144 + sx * (144 - x2), y2 - q)
            line(c, [a, b, d_], INK, 16)
            line(c, [a, b, d_], dk(col, .22), 9)
    # tail: segments arching over the back
    if st == "attack":
        pts = [(144, 150), (172, 120), (196, 86), (190, 44), (152, 24), (118, 40)]
        rot = math.pi * 1.06
    elif st == "hurt":
        pts = [(144, 150), (182, 136), (212, 110), (228, 80), (232, 54), (226, 34)]
        rot = -.15
    else:
        o = 5 if st == "idle_b" else 0
        pts = [(144, 150), (164, 118), (186, 90 + o), (204, 58 + o), (196, 26 + o), (168, 18 + o)]
        rot = math.pi * 1.3
    c.save()
    c.translate(0, bob)
    sizes = (34, 30, 27, 24, 21, 18)
    for (x, y), r_ in zip(pts, sizes):
        shape(c, E(x, y, r_), col, lw=3.4, d=8, rim=.42)
        line(c, [(x - r_ * .6, y + r_ * .1), (x, y + r_ * .4), (x + r_ * .6, y + r_ * .1)], dk(col, .35), 2.2, .9, True)
    # stinger
    x, y = pts[-1]
    c.save()
    c.translate(x, y)
    c.rotate(rot)
    st_ = S([(-15, 4), (-11, -14), (0, -22), (12, -12), (15, 4), (0, 12)])
    shape(c, st_, H("#b05cd6"), lw=3.2, d=5)
    t = L([(-6, -18), (0, -46), (7, -18)])
    shape(c, t, H("#3a3147"), lw=3, d=3)
    c.restore()
    c.restore()
    c.save()
    c.translate(0, bob)
    scorp_claw(c, -1, st)
    scorp_claw(c, 1, st)
    # back plates
    for y, rx, ry in ((158, 54, 28), (176, 66, 30)):
        shape(c, E(144, y, rx, ry), dk(col, .06), lw=3.4, d=8, rim=.42)
    head = S([(70, 212), (80, 178), (120, 164), (168, 164), (208, 178), (218, 212), (192, 244), (144, 254), (96, 244)])
    shape(c, head, col, lw=4, d=16, rim=.42)
    fill(c, S([(96, 226), (118, 214), (170, 214), (192, 226), (176, 244), (144, 250), (112, 244)]), lite)
    hurt, mad = st == "hurt", st == "attack"
    angry_eye(c, 112, 196, 18, 1, H("#fff4dc") if not mad else H("#ffb03c"), hurt)
    angry_eye(c, 176, 196, 18, -1, H("#fff4dc") if not mad else H("#ffb03c"), hurt)
    for x in (88, 200):
        fill(c, E(x, 186, 5.5), INK)
        fill(c, E(x - 1.5, 184, 1.8), WHITE)
    if not hurt:
        line(c, [(90, 172), (134, 190)], INK, 7)
        line(c, [(198, 172), (154, 190)], INK, 7)
    # fangs
    for sx in (-1, 1):
        f_ = S([(144 + sx * 30, 226), (144 + sx * 10, 226), (144 + sx * 12, 262 if mad else 250), (144 + sx * 25, 244)], k=1 / 8)
        shape(c, f_, H("#fff4dc"), lw=3.2, d=4)
    if mad:
        fill(c, R(122, 222, 44, 20, 9), H("#5c1f33"))
        stroke(c, R(122, 222, 44, 20, 9), INK, 3)
    elif hurt:
        line(c, [(118, 232), (130, 226), (144, 232), (158, 226), (170, 232)], INK, 4, 1, True)
    else:
        line(c, [(122, 226), (144, 232), (166, 226)], INK, 4, 1, True)
    c.restore()


def press_fist(c, x, y, sx, col):
    fist = R(x - 46, y - 36, 92, 76, 16)
    shape(c, fist, col, lw=4, d=14, rim=.42)
    for k in range(3):
        kx = x - 30 + k * 30
        shape(c, R(kx - 13, y + 18, 26, 30, 8), lt(col, .1), lw=3.2, d=6, rim=.4)
    # thumb
    shape(c, R(x - sx * 56, y - 14, 26, 36, 10), col, lw=3.2, d=5)
    hazard(c, x - 38, y - 26, 76, 14, 16)
    rivet(c, x - 34, y - 2, 3.4)
    rivet(c, x + 34, y - 2, 3.4)


def boss3(c, st):
    steel, orange = H("#6a7a99"), H("#ef7d32")
    bob = {"idle_a": 0, "idle_b": 5, "attack": -3, "hurt": 8}[st]
    # treads
    for x in (58, 164):
        t = R(x, 226, 66, 50, 20)
        shape(c, t, H("#3a3f55"), lw=4, d=8, rim=.2)
        for k in range(5):
            line(c, [(x + 8 + k * 13, 232), (x + 8 + k * 13, 270)], H("#20222f"), 3.4)
        for yy in (250,):
            for xx in (x + 18, x + 48):
                shape(c, E(xx, yy, 11), H("#aeb7c4"), lw=3, d=3)
    c.save()
    c.translate(0, bob)
    # exhaust stacks with steam
    for x in (92, 196):
        shape(c, R(x - 10, 44, 20, 54, 4), H("#4b5568"), lw=3.4, d=5)
        fill(c, R(x - 13, 40, 26, 9, 3), H("#aeb7c4"))
        stroke(c, R(x - 13, 40, 26, 9, 3), INK, 3)
    p = 1 if st == "idle_b" else 0
    if st != "hurt":
        smoke_puffs(c, [(92 - p * 3, 28, 10), (82 + p * 3, 14, 7)], H("#e6e8ef"))
        smoke_puffs(c, [(196 + p * 3, 28, 10), (206 - p * 3, 14, 7)], H("#e6e8ef"))
    else:
        smoke_puffs(c, [(92, 26, 13), (104, 12, 9), (196, 26, 13), (184, 12, 9)], H("#5c5f70"))
    # torso
    torso = R(74, 104, 140, 130, 18)
    shape(c, torso, steel, lw=4, d=18, rim=.42)
    hazard(c, 76, 208, 136, 18)
    fill(c, R(102, 124, 84, 70, 10), H("#2f3648"))
    fill(c, R(106, 128, 76, 62, 8), lin(0, 128, 0, 190, (0, H("#46536e")), (1, H("#2f3648"))))
    stroke(c, R(102, 124, 84, 70, 10), INK, 3.4)
    # chest gauge
    mad = st == "attack"
    hurt = st == "hurt"
    fill(c, E(144, 160, 25), H("#fff9ec"))
    stroke(c, E(144, 160, 25), INK, 3.4)
    for k in range(7):
        a = math.pi * (1 + k / 6)
        line(c, [(144 + math.cos(a) * 17, 160 + math.sin(a) * 17), (144 + math.cos(a) * 21, 160 + math.sin(a) * 21)], INK, 2.4)
    na = {"idle_a": -2.2, "idle_b": -1.7, "attack": -.35, "hurt": -3}[st]
    line(c, [(144, 160), (144 + math.cos(na) * 18, 160 + math.sin(na) * 18)], H("#e8483f"), 4.4)
    fill(c, E(144, 160, 4.5), INK)
    # head block
    head = R(92, 52, 104, 62, 12)
    shape(c, head, orange, lw=4, d=12, rim=.42)
    fill(c, R(104, 66, 80, 34, 8), H("#1a1826"))
    ec = H("#ff4d3c") if mad else H("#7dfff0")
    if hurt:
        for x in (124, 164):
            line(c, [(x - 9, 74), (x + 9, 92)], ec, 6)
            line(c, [(x + 9, 74), (x - 9, 92)], ec, 6)
    else:
        for x, q in ((124, 1), (164, -1)):
            ey = L([(x - 15, 74 + (0 if q > 0 else 9)), (x + 15, 74 + (9 if q > 0 else 0)), (x + 13, 92), (x - 13, 92)])
            fill(c, ey, ec)
            fill(c, E(x + q * 4, 86, 5), H("#1a1826"))
    stroke(c, R(104, 66, 80, 34, 8), INK, 3.4)
    # arms: chrome hydraulic rods
    for sx in (-1, 1):
        shx = 144 + sx * 84
        shape(c, R(shx - 26, 108, 52, 46, 12), orange, lw=4, d=9, rim=.42)
        if st == "attack":
            fy = 44
        elif st == "hurt":
            fy = 214 - (6 if sx > 0 else -6)
        else:
            fy = 196 + (5 if st == "idle_b" else 0)
        fx = 144 + sx * 96
        y0, y1 = (150, fy + 36) if fy > 130 else (fy + 30, 112)
        fill(c, R(fx - 11, min(y0, y1), 22, abs(y1 - y0), 3), lin(fx - 11, 0, fx + 11, 0, (0, H("#eff4fa")), (.45, H("#a5b3c6")), (1, H("#62708a"))))
        stroke(c, R(fx - 11, min(y0, y1), 22, abs(y1 - y0), 3), INK, 3.2)
        c.save()
        if st == "attack":
            rot_about(c, sx * .1, fx, fy)
        press_fist(c, fx, fy, sx, H("#ffa23c"))
        c.restore()
    c.restore()


def boss4(c, st):
    steel, dark = H("#4d4664"), H("#2f2a40")
    glow, glow2 = H("#ff8a2a"), H("#ffe27a")
    bob = {"idle_a": 0, "idle_b": 4, "attack": -5, "hurt": 8}[st]
    hurt, mad = st == "hurt", st == "attack"
    # ember cape behind
    c.save()
    c.translate(0, bob * .6)
    cape = S([(78, 130), (56, 190), (50, 248), (88, 256), (118, 240), (144, 258), (172, 240), (200, 256), (238, 248), (232, 190), (210, 130)], k=1 / 7)
    fill(c, cape, lin(0, 130, 0, 260, (0, H("#6d1f2e")), (.6, H("#c7301f")), (1, H("#ff8a2a"))))
    stroke(c, cape, INK, 3.6)
    c.restore()
    # sword in the right hand (viewer right) or raised overhead
    def sword(x, y, ang):
        c.save()
        c.translate(x, y)
        c.rotate(ang)
        shape(c, L([(-11, -150), (0, -176), (11, -150), (11, -18), (-11, -18)]), H("#3a3550"), lw=3.6, d=5, rim=.3)
        line(c, [(0, -160), (-2, -120), (2, -80), (-1, -24)], glow, 5)
        line(c, [(0, -160), (-2, -120), (2, -80), (-1, -24)], glow2, 2)
        shape(c, R(-27, -20, 54, 13, 5), H("#ffb03c"), lw=3.2, d=3)
        shape(c, R(-6, -8, 12, 34, 4), H("#6d4a2f"), lw=3, d=3)
        c.restore()
    c.save()
    c.translate(0, bob)
    # legs
    for x in (108, 180):
        shape(c, R(x - 25, 214, 50, 48 - bob, 12), steel, lw=4, d=8, rim=.42)
        fill(c, R(x - 21, 232, 42, 6, 2), glow)
        shape(c, R(x - 31, 250 - bob, 62, 24, 11), dark, lw=4, d=5, rim=.3)
    # torso
    torso = S([(84, 142), (144, 128), (204, 142), (214, 190), (196, 232), (144, 240), (92, 232), (74, 190)], k=1 / 7)
    shape(c, torso, steel, lw=4, d=18, rim=.42)
    # glowing seams + heart ember
    for pts in ([(100, 158), (118, 192), (108, 224)], [(188, 158), (170, 192), (180, 224)], [(118, 192), (170, 192)]):
        line(c, pts, INK, 8)
        line(c, pts, glow, 4.6)
        line(c, pts, glow2, 1.8)
    soft_ellipse(c, 144, 176, 34, 34, glow, .8)
    dia = L([(144, 152), (164, 178), (144, 204), (124, 178)])
    fill(c, dia, rad(144, 178, 2, 26, (0, WHITE), (.4, glow2), (1, glow)))
    stroke(c, dia, INK, 3.4)
    # arms
    if mad:
        la, ra = [(84, 158), (46, 190), (60, 232)], [(204, 158), (252, 150), (246, 104)]
    else:
        o = 5 if st == "idle_b" else 0
        la, ra = [(84, 158), (52, 186), (56, 226 - o)], [(204, 158), (238, 186), (238, 226 - o)]
    for arm in (la, ra):
        line(c, arm, INK, 35)
        line(c, arm, steel, 27)
        line(c, [(q[0] - 3, q[1] - 5) for q in arm], lt(steel, .28), 4, .7)
    if not mad:
        sword(238, 228 - (5 if st == "idle_b" else 0), .1 if not hurt else .2)
    for arm in (la, ra):
        shape(c, E(arm[2][0], arm[2][1], 19), dark, lw=3.6, d=5, rim=.3)
    # pauldrons with spikes
    for sx in (-1, 1):
        x = 144 + sx * 72
        sp = L([(x - 14, 124), (x + sx * 30, 88), (x + 16, 128)])
        shape(c, sp, H("#c7cfdb"), lw=3.2, d=4)
        shape(c, E(x, 148, 38, 30), dark, lw=4, d=8, rim=.42)
        line(c, [(x - 26, 158), (x, 166), (x + 26, 158)], glow, 3.4, 1, True)
    # horned helm
    tilt = .12 if hurt else 0
    c.save()
    rot_about(c, tilt, 144, 120)
    for sx in (-1, 1):
        horn = S([(144 + sx * 44, 86), (144 + sx * 92, 74), (144 + sx * 118, 28), (144 + sx * 96, 4 + 0), (144 + sx * 96, 40), (144 + sx * 66, 54), (144 + sx * 38, 56)], k=1 / 7)
        shape(c, horn, H("#e6dcc8"), lw=4, d=9, rim=.42)
        c.save()
        clip_to(c, horn)
        fill(c, E(144 + sx * 112, 8, 34, 30), glow)
        fill(c, E(144 + sx * 106, 0, 20, 19), glow2)
        c.restore()
        stroke(c, horn, INK, 4)
    helm = S([(92, 96), (102, 50), (144, 34), (186, 50), (196, 96), (186, 138), (144, 152), (102, 138)], k=1 / 7)
    shape(c, helm, steel, lw=4, d=16, rim=.42)
    # crest
    fill(c, L([(134, 38), (144, 18), (154, 38), (150, 80), (138, 80)]), dark)
    stroke(c, L([(134, 38), (144, 18), (154, 38), (150, 80), (138, 80)]), INK, 3.2)
    # visor
    vis = S([(102, 84), (144, 96), (186, 84), (178, 122), (144, 132), (110, 122)], k=1 / 8)
    fill(c, vis, H("#17131f"))
    stroke(c, vis, INK, 3.6)
    ec = H("#fff0a0") if mad else glow2
    if hurt:
        for x in (122, 166):
            line(c, [(x - 8, 102), (x + 8, 116)], glow, 5.6)
            line(c, [(x + 8, 102), (x - 8, 116)], glow, 5.6)
    else:
        for x, q in ((122, 1), (166, -1)):
            soft_ellipse(c, x, 108, 24, 18, glow, .75)
            fill(c, L([(x - 15, 100 - (q < 0) * -8 - (q < 0) * 8 + (0 if q > 0 else 8)), (x + 15, 100 + (8 if q > 0 else 0)), (x + 11, 116), (x - 11, 116)]), ec)
    for x in (130, 144, 158):
        line(c, [(x, 122), (x, 130)], H("#4d4664"), 2.6)
    c.restore()
    c.restore()
    if mad:
        c.save()
        c.translate(0, bob)
        arc = [(286, 60), (238, 8), (150, -8), (64, 20)]
        line(c, arc, glow, 13, .55, True)
        line(c, arc, glow2, 5, .85, True)
        sword(246, 104, -.82)
        shape(c, E(246, 104, 19), dark, lw=3.6, d=5, rim=.3)
        c.restore()
    # floating embers
    r = random.Random(77 + ("idle_a", "idle_b", "attack", "hurt").index(st))
    for i in range(9):
        x, y = r.uniform(20, 268), r.uniform(30, 250)
        s = r.uniform(2, 4.5)
        fill(c, E(x, y, s + 1.2), H("#7c2a14"), .9)
        fill(c, E(x, y, s), glow if i % 2 else glow2)


def make_bosses():
    for n, fn in ((1, boss1), (2, boss2), (3, boss3), (4, boss4)):
        for st in ("idle_a", "idle_b", "attack", "hurt"):
            cv = Cv(288, 288)
            c = cv.c
            c.translate(144, 277)        # shrink about the feet so raised limbs stay on the canvas
            c.scale(.9, .9)
            c.translate(-144, -278)
            fn(c, st)
            im = cv.image(1.6)
            if st == "hurt":
                im = tint(im, H("#ff5a4a"), .26)
            sh = Cv(288, 288)
            soft_ellipse(sh.c, 144, 268, 120, 16, COOL, .4)
            out = sh.image()
            out.alpha_composite(im)
            emit(f"boss{n}_{st}", out)


# ================================================================== backgrounds (1920x1080, drawn at 2x)
BW, BH = 1920, 1080


def cloud(c, x, y, s, col, a=1.0):
    c.push_group()
    for (dx, dy, r_) in ((-70, 10, 42), (-20, -18, 58), (44, -6, 48), (92, 14, 32)):
        fill(c, E(x + dx * s, y + dy * s, r_ * s), col)
    fill(c, R(x - 108 * s, y + 10 * s, 226 * s, 40 * s, 20 * s), col)
    c.pop_group_to_source()
    c.paint_with_alpha(a)


def skyline(c, base, seed, col, hmin, hmax, win=None, wmin=70, wmax=150):
    r = random.Random(seed)
    x = -20
    while x < BW + 20:
        w = r.uniform(wmin, wmax)
        h = r.uniform(hmin, hmax)
        fill(c, R(x, base - h, w, h + 400, (10, 10, 0, 0)), col)
        kind = r.randint(0, 3)
        if kind == 0:
            fill(c, R(x + w * .4, base - h - 34, 8, 40, 3), col)
        elif kind == 1:
            fill(c, R(x + w * .15, base - h - 16, w * .3, 22, 3), col)
        if win:
            for yy in range(int(base - h + 22), int(base), 34):
                for xx in range(int(x + 14), int(x + w - 18), 26):
                    if r.random() < .34:
                        fill(c, R(xx, yy, 13, 17, 2), win)
        x += w + r.uniform(-6, 14)


def rolling(c, base, amp, wl, ph, col):
    pts = [(-20, BH + 20)]
    x = -20
    while x <= BW + 40:
        pts.append((x, base + amp * math.sin(x / wl + ph) + amp * .45 * math.sin(x / wl * 2.3 + ph * 1.7)))
        x += 40
    pts.append((BW + 40, BH + 20))
    fill(c, L(pts), col)


def bg_city(c):
    fill(c, R(0, 0, BW, BH, 0), lin(0, 0, 0, BH, (0, H("#6f9fc9")), (.55, H("#b4d2de")), (.8, H("#f6dfc2"))))
    fill(c, E(1640, 300, 260), rad(1640, 300, 40, 260, (0, H("#fff5d6"), .95), (.32, H("#fff0c0"), .75), (1, H("#fff0c0"), 0)))
    for (x, y, s) in ((180, 190, 1.3), (520, 120, .9), (1500, 150, 1.1), (1780, 420, .9), (120, 470, .8)):
        cloud(c, x, y, s, H("#eef5f8"), .7)
    skyline(c, 860, 3, H("#94afc6"), 200, 480, H("#b5c9d9"))
    skyline(c, 900, 5, H("#6f8cab"), 120, 340, H("#f3dfa8"))
    skyline(c, 960, 8, H("#4f6a8c"), 60, 200, H("#ffdf8e"), 90, 170)
    rolling(c, 960, 10, 300, 1, H("#4a8d6e"))
    r = random.Random(4)
    for i in range(28):         # park trees
        x = i * 72 + r.uniform(-20, 20)
        s = r.uniform(30, 58)
        y = 975 + r.uniform(-10, 18)
        fill(c, R(x - 5, y, 10, 44, 3), H("#3f4f5e"))
        fill(c, E(x, y - s * .3, s, s * .95), H("#3e8a66") if i % 2 else H("#35785f"))
        fill(c, E(x - s * .28, y - s * .55, s * .5, s * .42), H("#5aa97a"), .8)
    fill(c, R(0, 1010, BW, 70, 0), H("#3d5569"))
    fill(c, R(0, 1008, BW, 7, 0), H("#8fa3b5"))


def bg_desert(c):
    fill(c, R(0, 0, BW, BH, 0), lin(0, 0, 0, BH, (0, H("#f0a673")), (.42, H("#ffd09a")), (.7, H("#ffeac2"))))
    fill(c, E(250, 340, 300), rad(250, 340, 90, 300, (0, H("#fff6d8")), (.3, H("#ffeeb8"), .85), (1, H("#ffeeb8"), 0)))
    for (x, y, s) in ((1660, 170, 1.1), (1760, 330, .7), (420, 110, .8)):
        cloud(c, x, y, s, H("#ffe9d0"), .6)

    def mesa(x, w, top, col, base=860):
        fill(c, L([(x - w * .6, base), (x - w * .46, top + 26), (x - w * .3, top), (x + w * .34, top + 4), (x + w * .46, top + 34), (x + w * .6, base)]), col)
        for k in range(3):
            y = top + 40 + k * 44
            line(c, [(x - w * .48, y), (x + w * .46, y + 6)], dk(col, .14), 5, .7)
    mesa(180, 340, 500, H("#d48e66"))
    mesa(1720, 420, 450, H("#d48e66"))
    mesa(1420, 260, 590, H("#e0a079"))
    mesa(560, 240, 610, H("#e0a079"))
    rolling(c, 760, 26, 340, .5, H("#f1c183"))
    rolling(c, 850, 30, 280, 2.4, H("#e5ab6b"))
    rolling(c, 965, 28, 360, 4.1, H("#d3935a"))
    for (x, y, s) in ((130, 930, 1.2), (1790, 900, 1.4), (1650, 985, .8), (300, 1000, .9)):
        col = H("#4b8a6a")
        fill(c, R(x - 18 * s, y - 130 * s, 36 * s, 150 * s, 18 * s), col)
        stroke(c, S([(x - 16 * s, y - 40 * s), (x - 56 * s, y - 50 * s), (x - 58 * s, y - 90 * s)], closed=False), col, 22 * s)
        stroke(c, S([(x + 16 * s, y - 60 * s), (x + 52 * s, y - 70 * s), (x + 54 * s, y - 112 * s)], closed=False), col, 22 * s)
        fill(c, R(x - 12 * s, y - 124 * s, 8 * s, 120 * s, 4 * s), H("#7cb98f"), .8)


def bg_factory(c):
    fill(c, R(0, 0, BW, BH, 0), lin(0, 0, 0, BH, (0, H("#33445a")), (1, H("#4c6276"))))
    # wall panels
    for x in range(0, BW, 240):
        line(c, [(x, 0), (x, BH)], H("#2a384b"), 6)
        line(c, [(x + 4, 0), (x + 4, BH)], H("#5b7188"), 2, .6)
        for y in (60, 340, 620):
            fill(c, E(x + 26, y, 7), H("#2a384b"))
            fill(c, E(x + 214, y, 7), H("#2a384b"))
    # tall windows with soft light
    for x in (70, 1630):
        for k in range(2):
            wx = x + k * 120
            fill(c, R(wx, 110, 100, 300, 10), H("#2a384b"))
            fill(c, R(wx + 8, 118, 84, 284, 6), lin(0, 110, 0, 410, (0, H("#b7e4ea")), (1, H("#7cc0cc"))))
            line(c, [(wx + 50, 118), (wx + 50, 400)], H("#2a384b"), 5)
            for y in (210, 306):
                line(c, [(wx + 8, y), (wx + 92, y)], H("#2a384b"), 5)
            fill(c, L([(wx + 8, 400), (wx + 92, 400), (wx + 180, 1080), (wx - 60, 1080)]), H("#b7e4ea"), .05)

    def gear(x, y, r_, col):
        fill(c, gear_path(x, y, r_, 12, .13), col)
        fill(c, E(x, y, r_ * .62), lt(col, .07))
        for k in range(6):
            a = k * TAU / 6
            fill(c, E(x + math.cos(a) * r_ * .42, y + math.sin(a) * r_ * .42, r_ * .1), col)
        fill(c, E(x, y, r_ * .16), H("#2a384b"))
    gear(150, 690, 150, H("#3a4e64"))
    gear(322, 790, 76, H("#34475c"))
    gear(1770, 680, 160, H("#3a4e64"))
    gear(1590, 786, 80, H("#34475c"))
    # pipes
    for y, col in ((470, H("#7a8ea4")), (520, H("#c78a3e"))):
        fill(c, R(0, y, BW, 26, 0), lin(0, y, 0, y + 26, (0, lt(col, .25)), (1, dk(col, .35))))
        for x in range(90, BW, 330):
            fill(c, R(x, y - 5, 28, 36, 4), dk(col, .3))
    # catwalk
    fill(c, R(0, 960, BW, 120, 0), H("#28344a"))
    fill(c, R(0, 950, BW, 14, 0), H("#f2b632"))
    for i in range(0, BW, 56):
        fill(c, L([(i, 964), (i + 26, 964), (i + 40, 950), (i + 14, 950)]), H("#28344a"))
    for x in range(30, BW, 120):
        line(c, [(x, 950), (x, 860)], H("#5b7188"), 7)
    line(c, [(0, 860), (BW, 860)], H("#5b7188"), 8)


def bg_fortress(c):
    fill(c, R(0, 0, BW, BH, 0), lin(0, 0, 0, BH, (0, H("#241a3a")), (.5, H("#4a2545")), (.85, H("#a33f33"))))
    r = random.Random(9)
    for i in range(70):
        fill(c, E(r.uniform(0, BW), r.uniform(0, 520), r.uniform(1, 2.6)), H("#d8cfee"), r.uniform(.3, .8))
    c.push_group()
    fill(c, E(1640, 210, 96), H("#e7ddc8"))
    c.set_operator(cairo.OPERATOR_DEST_OUT)
    fill(c, E(1602, 190, 86), WHITE)
    c.pop_group_to_source()
    c.paint_with_alpha(.85)
    for (x, y, s) in ((300, 230, 1.5), (1250, 170, 1.2), (1760, 420, 1.1)):
        cloud(c, x, y, s, H("#5a3a5e"), .5)
    # volcanoes at both sides
    for (x, w, top) in ((170, 620, 330), (1800, 680, 400)):
        fill(c, L([(x - w, 1000), (x - 80, top), (x + 80, top), (x + w, 1000)]), lin(0, top, 0, 1000, (0, H("#5f3a5e")), (1, H("#3a2545"))))
        fill(c, E(x, top + 4, 84, 20), H("#ff8a2a"))
        fill(c, E(x, top - 30, 170, 130), rad(x, top, 10, 170, (0, H("#ff8a2a"), .55), (1, H("#ff8a2a"), 0)))
        for sx, ln in ((-1, 300), (1, 200)):     # lava runs down the slope, one smooth tapering stream
            run = lambda t, wd: (x + sx * (46 + ln * .62 * t) + wd, top + 6 + ln * t + 10 * math.sin(t * 7))
            left = [run(t, -13 * (1 - t) - 1) for t in (0, .3, .6, .85)]
            right = [run(t, 13 * (1 - t) + 1) for t in (.85, .6, .3, 0)]
            fill(c, S(left + [run(1, 0)] + right), H("#ff7a2a"), .9)
            core = [(p[0] * .55 + q[0] * .45, p[1]) for p, q in zip(left, right[::-1])]
            line(c, core, H("#ffd05a"), 5, .85, True)

    def tower(x, w, top, col, win=True):
        fill(c, R(x, top, w, 900, 0), col)
        n = int(w / 26)
        for k in range(n):
            if k % 2 == 0:
                fill(c, R(x - 8 + k * (w + 16) / (n - .5), top - 22, (w + 16) / (n - .5), 24, 2), col)
        fill(c, R(x - 8, top, w + 16, 16, 0), col)
        if win:
            for y in (top + 70, top + 180, top + 290):
                fill(c, R(x + w / 2 - 12, y, 24, 44, (12, 12, 0, 0)), H("#ffb03c"))
    tower(40, 130, 640, H("#2e2240"))
    tower(290, 100, 720, H("#2e2240"))
    fill(c, R(0, 810, 420, 300, 0), H("#2e2240"))
    tower(1540, 110, 700, H("#2e2240"))
    tower(1740, 140, 610, H("#2e2240"))
    fill(c, R(1500, 800, 420, 300, 0), H("#2e2240"))
    rolling(c, 985, 12, 240, 2, H("#231a31"))
    fill(c, R(0, 1020, BW, 60, 0), lin(0, 1020, 0, 1080, (0, H("#ff7a2a")), (1, H("#c7301f"))))
    line(c, [(0, 1020), (BW, 1020)], H("#ffd05a"), 5)


def bg_title(c, W=BW, Hh=BH):
    fill(c, R(0, 0, W, Hh, 0), lin(0, 0, 0, Hh, (0, H("#37b4f0")), (.6, H("#a6e6fb")), (.85, H("#eefbe0"))))
    # sun rays from the hero
    ox, oy = 470, 640
    for k in range(18):
        a0 = k * TAU / 18
        a1 = a0 + TAU / 36
        fill(c, L([(ox, oy), (ox + math.cos(a0) * 2400, oy + math.sin(a0) * 2400), (ox + math.cos(a1) * 2400, oy + math.sin(a1) * 2400)]), WHITE, .13)
    for (x, y, s) in ((1120, 190, 1.5), (1720, 330, 1.2), (760, 110, 1), (1500, 90, .8), (90, 150, 1)):
        cloud(c, x + 6, y + 10, s, H("#8fd0ee"), .5)
        cloud(c, x, y, s, WHITE, 1)
    rolling(c, 800, 34, 300, 1, H("#8fdf7a"))
    rolling(c, 880, 32, 240, 3, H("#63c95e"))
    rolling(c, 975, 22, 320, 5, H("#3fae57"))
    r = random.Random(12)
    for i in range(46):
        x, y = r.uniform(0, W), r.uniform(1000, 1072)
        for k in range(5):
            a = k * TAU / 5
            fill(c, E(x + math.cos(a) * 7, y + math.sin(a) * 7, 5.5), H("#fff4f0") if i % 2 else H("#ffd23f"))
        fill(c, E(x, y, 4.5), H("#ff9a3c"))


def make_backgrounds():
    for t, fn in (("city", bg_city), ("desert", bg_desert), ("factory", bg_factory), ("fortress", bg_fortress)):
        cv = Cv(BW, BH, 2)
        fn(cv.c)
        emit(f"bg_{t}", cv.image().convert("RGBA"))
    cv = Cv(BW, BH, 2)
    bg_title(cv.c)
    base = cv.image()
    sh = Cv(BW, BH, 2)
    soft_ellipse(sh.c, 330, 962, 340, 34, H("#1f6a3e"), .55)
    base.alpha_composite(sh.image())
    fg = Cv(BW, BH, 2)
    c = fg.c
    pomegranate(c, 200, 776, 176, 0, crown=H("#ffb43c"))
    c.save()
    c.translate(240, 500)
    c.scale(3.1, 3.1)
    mole_cheer(c, PAL["hero"], stars=False)
    c.restore()
    base.alpha_composite(fg.image(5))
    emit("bg_title", base)


# ================================================================== world map nodes, joystick, bomb button
def world_badge(c, col):
    soft_ellipse(c, 132, 138, 122, 122, COOL, .35)
    fill(c, E(128, 128, 118), INK)
    fill(c, E(128, 128, 110), lin(30, 30, 226, 226, (0, H("#fff2b8")), (.5, H("#ffc93c")), (1, H("#d58a1e"))))
    fill(c, E(128, 128, 96), INK)


def world_city(c):
    world_badge(c, 0)
    sky = E(128, 128, 92)
    clip_to(c, sky)
    fill(c, sky, lin(0, 36, 0, 220, (0, H("#6fc2ee")), (1, H("#e1f6fb"))))
    fill(c, E(186, 76, 22), H("#fff0a8"))
    cloud(c, 88, 86, .42, WHITE)
    for (x, w, top, col) in ((46, 40, 118, H("#7f93bb")), (150, 44, 104, H("#62739b")), (86, 44, 78, H("#e8483f")), (126, 40, 128, H("#ffc62e")), (186, 34, 136, H("#7f93bb"))):
        shape(c, R(x, top, w, 170, 5), col, lw=3.4, d=9, rim=.4)
        for yy in range(top + 12, 186, 20):
            for xx in (x + 9, x + w - 17):
                fill(c, R(xx, yy, 8, 11, 1.5), H("#fff3b8"))
                stroke(c, R(xx, yy, 8, 11, 1.5), INK, 1.4)
    fill(c, R(0, 186, 256, 70, 0), H("#58b257"))
    line(c, [(0, 186), (256, 186)], INK, 3.4)


def world_desert(c):
    world_badge(c, 0)
    sky = E(128, 128, 92)
    clip_to(c, sky)
    fill(c, sky, lin(0, 36, 0, 220, (0, H("#ffb36b")), (1, H("#ffeec4"))))
    fill(c, E(128, 112, 44), H("#fff0a8"))
    stroke(c, E(128, 112, 44), H("#ffc66b"), 5)
    shape(c, S([(20, 180), (72, 132), (130, 170), (190, 138), (240, 168), (240, 240), (20, 240)]), H("#efbf73"), lw=3.4, d=12)
    shape(c, S([(20, 210), (100, 172), (180, 208), (240, 186), (240, 250), (20, 250)]), H("#d8984f"), lw=3.4, d=12)
    col = H("#46a86c")
    line(c, [(76, 196), (76, 122)], INK, 25)
    line(c, [(76, 158), (50, 150), (50, 128)], INK, 19, 1, True)
    line(c, [(76, 170), (102, 162), (102, 140)], INK, 19, 1, True)
    line(c, [(76, 158), (50, 150), (50, 128)], col, 12, 1, True)
    line(c, [(76, 170), (102, 162), (102, 140)], col, 12, 1, True)
    line(c, [(76, 196), (76, 122)], col, 18)
    line(c, [(71, 190), (71, 124)], H("#8fdc9f"), 3.6, .8)


def world_factory(c):
    world_badge(c, 0)
    sky = E(128, 128, 92)
    clip_to(c, sky)
    fill(c, sky, lin(0, 36, 0, 220, (0, H("#7f93a8")), (1, H("#d5dce6"))))
    smoke_puffs(c, [(176, 68, 15), (156, 56, 11), (196, 50, 9)], H("#f4f5f8"))
    shape(c, R(166, 86, 28, 110, 3), H("#b5533f"), lw=3.4, d=6)
    fill(c, R(166, 104, 28, 10, 0), H("#fff4dc"))
    stroke(c, R(166, 86, 28, 110, 3), INK, 3.4)
    fact = L([(30, 200), (30, 134), (72, 158), (72, 134), (114, 158), (114, 134), (156, 158), (156, 142), (230, 142), (230, 200)])
    shape(c, fact, H("#55617a"), lw=3.4, d=10)
    shape(c, gear_path(82, 148, 46, 9, .2), H("#ffc62e"), lw=3.6, d=12, rim=.45)
    fill(c, E(82, 148, 18), INK)
    fill(c, E(82, 148, 12), H("#d5dce6"))
    hazard(c, 20, 198, 216, 16, 20)
    fill(c, R(0, 214, 256, 40, 0), H("#3a4152"))


def world_fortress(c):
    world_badge(c, 0)
    sky = E(128, 128, 92)
    clip_to(c, sky)
    fill(c, sky, lin(0, 36, 0, 220, (0, H("#2c1e47")), (1, H("#c7421f"))))
    for (x, y) in ((70, 66), (100, 52), (182, 70), (150, 54)):
        fill(c, E(x, y, 2.6), H("#fff4dc"))
    w = H("#544c6b")
    for (x, wd, top) in ((58, 46, 110), (152, 46, 110)):
        shape(c, R(x, top, wd, 110, 3), w, lw=3.4, d=9)
        for k in range(3):
            shape(c, R(x - 3 + k * 19, top - 14, 13, 18, 2), w, lw=3.2, d=3, rim=.3)
    keep = R(92, 136, 72, 80, 3)
    shape(c, keep, H("#62597e"), lw=3.4, d=10)
    for k in range(4):
        shape(c, R(90 + k * 20, 122, 14, 18, 2), H("#62597e"), lw=3.2, d=3, rim=.3)
    fill(c, R(112, 170, 32, 46, (16, 16, 0, 0)), H("#ffb03c"))
    stroke(c, R(112, 170, 32, 46, (16, 16, 0, 0)), INK, 3.2)
    for x in (70, 170):
        fill(c, R(x, 134, 16, 24, (8, 8, 0, 0)), H("#ffb03c"))
        stroke(c, R(x, 134, 16, 24, (8, 8, 0, 0)), INK, 2.6)
    line(c, [(128, 122), (128, 76)], INK, 5)
    shape(c, L([(130, 76), (164, 88), (130, 100)]), H("#ff5a3c"), lw=3, d=4)
    fill(c, R(0, 212, 256, 40, 0), H("#ff7a2a"))
    line(c, [(0, 212), (256, 212)], H("#ffe27a"), 4)
    line(c, [(0, 209), (256, 209)], INK, 3.4)


def joy_base(c):
    fill(c, E(128, 128, 122), H("#1d2233"), .5)
    stroke(c, E(128, 128, 119), WHITE, 6, .75)
    fill(c, E(128, 128, 70), H("#1d2233"), .32)
    stroke(c, E(128, 128, 72), WHITE, 2.5, .3)
    for k in range(4):
        a = k * TAU / 4
        c.save()
        c.translate(128, 128)
        c.rotate(a)
        fill(c, L([(106, 0), (84, -18), (84, 18)]), WHITE, .8)
        c.restore()


def joy_knob(c):
    soft_ellipse(c, 134, 140, 110, 110, COOL, .45)
    shape(c, E(128, 128, 84), H("#ffc62e"), lw=7, d=26, rim=.5)
    fill(c, E(128, 128, 54), H("#e89a1e"))
    stroke(c, E(128, 128, 54), INK, 4.5)
    fill(c, E(128, 128, 48), rad(114, 110, 4, 66, (0, H("#ffe27a")), (1, H("#ffb52e"))))
    for k in range(4):
        a = k * TAU / 4
        fill(c, E(128 + math.cos(a) * 26, 128 + math.sin(a) * 26, 6.5), H("#e89a1e"))
    gloss(c, 92, 84, 22, 10, .75)


def btn_bomb(c):
    """Plain white silhouette (tintable in code): details are cut out of the alpha."""
    r_ = 82
    cx, cy = 118, 154
    fill(c, pom_body(cx, cy, r_), WHITE)
    top = cy - r_ * .86
    fill(c, pom_crown(cx, top, r_), WHITE)
    fx, fy = cx + r_ * .62, top - r_ * .52
    line(c, [(cx, top), (cx + r_ * .14, top - r_ * .34), (fx, fy)], WHITE, 11, 1, True)
    pts = []
    for i in range(14):
        a = i * TAU / 14
        rr = 25 * (1.25 if i % 2 == 0 else .62)
        pts.append((fx + math.cos(a) * rr, fy + math.sin(a) * rr))
    fill(c, L(pts), WHITE)
    c.set_operator(cairo.OPERATOR_DEST_OUT)
    fill(c, E(cx + r_ * .36, cy + r_ * .28, r_ * .39, r_ * .3, -.55), WHITE)
    fill(c, E(cx - r_ * .48, cy - r_ * .42, r_ * .26, r_ * .12, -.6), WHITE)
    line(c, [(cx - r_ * .2, top + 15), (cx + r_ * .2, top + 15)], WHITE, 4.5)
    c.set_operator(cairo.OPERATOR_OVER)
    wx, wy = cx + r_ * .36, cy + r_ * .28
    for (dx, dy) in ((-.18, .09), (.0, -.09), (.16, -.14), (.04, .15), (.2, .03)):
        fill(c, E(wx + dx * r_, wy + dy * r_, r_ * .085, r_ * .1), WHITE)


def make_ui():
    for t, fn in (("city", world_city), ("desert", world_desert), ("factory", world_factory), ("fortress", world_fortress)):
        cv = Cv(256, 256); fn(cv.c); done(cv, f"world_{t}")
    cv = Cv(256, 256); joy_base(cv.c); done(cv, "joy_base")
    cv = Cv(256, 256); joy_knob(cv.c); done(cv, "joy_knob")
    cv = Cv(256, 256); btn_bomb(cv.c); done(cv, "btn_bomb")


def icon_back(c, S_):
    fill(c, R(0, 0, S_, S_, 0), lin(0, 0, S_, S_, (0, H("#35c4c0")), (1, H("#2478c8"))))
    ox = oy = S_ / 2
    for k in range(14):
        a0 = k * TAU / 14
        fill(c, L([(ox, oy), (ox + math.cos(a0) * S_, oy + math.sin(a0) * S_), (ox + math.cos(a0 + TAU / 28) * S_, oy + math.sin(a0 + TAU / 28) * S_)]), WHITE, .11)


def icon_front(c, S_, sc):
    """Hero face with a pomegranate at his side, centred for a square of side S_; sc scales the group."""
    c.save()
    c.translate(S_ / 2, S_ / 2)
    c.scale(sc, sc)
    pomegranate(c, 52, 42, 54, 0)
    c.save()
    c.translate(-104, -92)
    c.scale(1.72, 1.72)
    mole_head(c, PAL["hero"])
    c.restore()
    c.restore()


def make_icons():
    cv = Cv(432, 432)
    icon_back(cv.c, 432)
    ICON_OUT["icon_bg"] = cv.image().convert("RGBA")
    cv = Cv(432, 432)
    icon_front(cv.c, 432, 1.16)     # inside the adaptive safe circle (264 px dia. guaranteed, 288 usual)
    ICON_OUT["icon_fg"] = cv.image(3)
    cv = Cv(512, 512)
    c = cv.c
    clip_to(c, R(8, 8, 496, 496, 96))
    icon_back(c, 512)
    full = cv.image()
    cv = Cv(512, 512)
    icon_front(cv.c, 512, 1.86)
    full.alpha_composite(cv.image(3.5))
    cv = Cv(512, 512)
    stroke(cv.c, R(10, 10, 492, 492, 94), INK, 6)
    full.alpha_composite(cv.image())
    ICON_OUT["icon_full"] = full


# ================================================================== contract (tools/ART_SPEC.md) as data
def contract():
    n = {}

    def add(size, *names):
        for x in names:
            n[x] = size
    for t in THEMES:
        add((96, 96), f"floor_{t}_a", f"floor_{t}_b", f"rubble_{t}")
        add((96, 120), f"wall_{t}", f"edge_{t}", f"block_{t}", f"crack_{t}")
        add((1920, 1080), f"bg_{t}")
        add((256, 256), f"world_{t}")
    add((96, 120), "hedge", "mirage", "piston_h", "keg", "mimic_a", "gate_red_up", "gate_blue_up", "exit_closed", "exit_open")
    add((96, 96), "hedge_sprout", "gate_red_down", "gate_blue_down", "ice", "sand_q", "crumble_0", "crumble_1", "crumble_2", "pit", "jet_off", "jet_warn",
        "switch_red", "switch_blue", "road", "secret_tile", "bomb_a", "bomb_b", "secret_token",
        "shot_trash", "shot_sting", "shot_gear", "shot_ember", "warn_cell")
    for d in "udlr":
        add((96, 96), f"conv_{d}_a", f"conv_{d}_b", f"oneway_{d}")
    for s in "abc":
        add((96, 96), f"tele_{s}1", f"tele_{s}2")
    add((96, 48), "shadow")
    for p in ("hero", "p2", "p3", "p4"):
        for d in "dulr":
            for f in range(3):
                add((128, 160), f"{p}_{d}_{f}")
        add((128, 160), f"{p}_cheer", f"{p}_hurt")
        add((96, 96), f"{p}_head")
    for e in ("snail", "beetle", "ghostmoth", "fox", "frog", "turtle", "wasp"):
        add((128, 128), f"{e}_a", f"{e}_b")
    add((128, 128), "mimic_b", "turtle_shell")
    add((192, 112), "car_red", "car_blue", "car_yellow")
    add((48, 48), "bomb_mini")
    for f in "chvlrud":
        add((96, 96), f"flame_{f}_a", f"flame_{f}_b")
    for p in ("bomb", "flame", "speed", "kick", "shield"):
        add((96, 96), f"pu_{p}", f"hud_{p}")
    add((96, 96), "hud_clock", "hud_heart", "hud_star", "hud_lock", "hud_check")
    add((128, 160), "shield")
    for b in range(1, 5):
        add((288, 288), f"boss{b}_idle_a", f"boss{b}_idle_b", f"boss{b}_attack", f"boss{b}_hurt")
    add((1920, 1080), "bg_title")
    add((256, 256), "joy_base", "joy_knob", "btn_bomb")
    return n


def check_contract():
    want = contract()
    bad = []
    for k, sz in want.items():
        if k not in OUT:
            bad.append(f"MISSING {k}")
        elif OUT[k].size != sz:
            bad.append(f"SIZE {k} {OUT[k].size} != {sz}")
    extra = sorted(set(OUT) - set(want))
    if extra:
        bad.append(f"EXTRA {extra}")
    for k, sz in (("icon_bg", (432, 432)), ("icon_fg", (432, 432)), ("icon_full", (512, 512))):
        if k not in ICON_OUT or ICON_OUT[k].size != sz:
            bad.append(f"ICON {k}")
    return bad


# ================================================================== verification images
def label_font(sz=11):
    try:
        from PIL import ImageFont
        return ImageFont.truetype(os.path.join(ROOT, "Assets", "Resources", "Fonts", "Rubik.ttf"), sz)
    except OSError:
        from PIL import ImageFont
        return ImageFont.load_default()


def build_sheet(path=None):
    groups = (
        ("Floors, solids", [k for k in OUT if k.startswith(("floor_", "wall_", "edge_", "block_", "crack_", "hedge", "mirage", "piston", "keg", "gate_"))] + ["mimic_a"]),
        ("Overlays", [k for k in OUT if k.startswith(("conv_", "ice", "sand_q", "tele_", "oneway_", "crumble_", "pit", "jet_", "switch_", "road", "secret_tile", "rubble_", "exit_", "shadow"))]),
        ("Hero and rivals", [k for k in OUT if k.startswith(("hero_", "p2_", "p3_", "p4_"))]),
        ("Enemies, cars", [k for k in OUT if k.startswith(("snail", "beetle", "ghostmoth", "fox", "frog", "turtle", "mimic_b", "wasp", "car_"))]),
        ("Bomb, flames, items", [k for k in OUT if k.startswith(("bomb_", "flame_", "pu_", "secret_token", "shield", "shot_", "warn_", "hud_"))]),
        ("Bosses", [k for k in OUT if k.startswith("boss")]),
        ("UI", [k for k in OUT if k.startswith(("world_", "joy_", "btn_"))]),
    )
    cell, cols = 124, 18
    font, big = label_font(10), label_font(20)
    rows = []
    y = 8
    for title, names in groups:
        n = len(names)
        c_ = cell * (2 if title == "Bosses" else 1)
        k = cols // (2 if title == "Bosses" else 1)
        rows.append((title, names, c_, k, y))
        y += 34 + ((n + k - 1) // k) * (c_ + 16)
    bgs = ["bg_city", "bg_desert", "bg_factory", "bg_fortress", "bg_title"]
    sheet = Image.new("RGBA", (cols * cell + 16, y + 34 + 200 + 8), (205, 209, 214, 255))
    d = ImageDraw.Draw(sheet)
    for title, names, c_, k, yy in rows:
        d.text((8, yy), title, font=big, fill=(43, 42, 61, 255))
        for i, nm in enumerate(names):
            im = OUT[nm]
            sc = min(1, (c_ - 6) / im.width, (c_ - 6) / im.height)
            if sc < 1:
                im = im.resize((int(im.width * sc), int(im.height * sc)), Image.LANCZOS)
            x0 = 8 + (i % k) * c_
            y0 = yy + 30 + (i // k) * (c_ + 16)
            # checker behind so alpha is visible
            d.rectangle((x0 + 1, y0 + 1, x0 + c_ - 2, y0 + c_ - 2), fill=(170, 178, 188, 255))
            sheet.alpha_composite(im, (x0 + (c_ - im.width) // 2, y0 + (c_ - im.height) // 2))
            d.text((x0 + 2, y0 + c_), nm, font=font, fill=(43, 42, 61, 255))
    yy = y
    d.text((8, yy), "Backdrops", font=big, fill=(43, 42, 61, 255))
    for i, nm in enumerate(bgs):
        im = OUT[nm].resize((352, 198), Image.LANCZOS)
        sheet.alpha_composite(im, (8 + i * 358, yy + 30))
    x = 8 + 5 * 358
    full = ICON_OUT["icon_full"].resize((198, 198), Image.LANCZOS)
    sheet.alpha_composite(full, (x, yy + 30))
    sheet.convert("RGB").save(path or os.path.join(VERIF, "art_sheet.png"))


MOCK_MAP = [
    "EEEEEEEEEEEEEEE",
    "E..b.b...b.b..E",
    "E.W.WbW.W.WbW.E",
    "Eb.b...b...b..E",
    "E.WbW.W.WbW.W.E",
    "E...b.....b.b.E",
    "EbW.W.WbW.W.WbE",
    "E.b...b.b...b.E",
    "E.W.WbW.W.WbW.E",
    "E..b.....b....E",
    "EbW.W.WbW.WbW.E",
    "E...b.b.....b.E",
    "EEEEEEEEEEEEEEE",
]


def build_mock(theme, path):
    S_ = 80
    k = S_ / 96
    W_, H_ = 1920, 1080
    ox, oy = (W_ - 15 * S_) // 2, (H_ - 13 * S_) // 2
    scr = OUT[f"bg_{theme}"].copy()
    fa, fb = (OUT[f"floor_{theme}_{s}"].resize((S_, S_), Image.LANCZOS) for s in "ab")

    def sc(n):
        im = OUT[n]
        return im.resize((round(im.width * k), round(im.height * k)), Image.LANCZOS)

    for y in range(13):
        for x in range(15):
            scr.paste(fa if (x + y) % 2 == 0 else fb, (ox + x * S_, oy + y * S_))

    def put_flat(n, x, y):
        im = sc(n)
        scr.alpha_composite(im, (ox + x * S_ + (S_ - im.width) // 2, oy + y * S_ + (S_ - im.height) // 2))

    # floor things
    put_flat("flame_c_a", 7, 5)
    for x in (5, 6, 8, 9):
        put_flat("flame_h_a", x, 5)
    put_flat("flame_l_a", 4, 5)
    put_flat("flame_r_a", 10, 5)
    put_flat("flame_v_a", 7, 4)
    put_flat("flame_u_a", 7, 3)
    put_flat("flame_v_a", 7, 6)
    put_flat("flame_d_a", 7, 7)
    put_flat("pu_flame", 3, 9)
    put_flat("pu_bomb", 11, 1)
    put_flat(f"rubble_{theme}", 1, 7)
    put_flat("crumble_1", 11, 9)
    put_flat("secret_tile", 13, 11)
    actors = {(2, 3): ("hero_d_1", 0), (3, 1): ("beetle_a", 1), (9, 3): ("snail_a", 1), (11, 3): ("fox_a", 1), (5, 9): ("frog_a", 1), (1, 9): ("turtle_a", 1),
              (9, 7): ("wasp_a", 1), (5, 7): ("ghostmoth_a", 1), (13, 5): ("p2_l_1", 0), (8, 9): ("p3_r_0", 0), (13, 9): ("mimic_b", 1), (6, 1): ("p4_u_0", 0)}
    bombs = {(1, 1), (3, 5), (12, 7)}
    shadow = sc("shadow")
    for y in range(13):
        for x in range(15):
            ch = MOCK_MAP[y][x]
            if (x, y) in actors or (x, y) in bombs:
                ch = "."
            px, py = ox + x * S_, oy + y * S_
            name = {"E": f"edge_{theme}", "W": f"wall_{theme}", "b": f"block_{theme}"}.get(ch)
            if name:
                im = sc(name)
                scr.alpha_composite(im, (px, py + S_ - im.height + 0) if py + S_ - im.height >= 0 else (px, 0))
            if (x, y) in bombs:
                scr.alpha_composite(sc("bomb_a"), (px, py))
            if (x, y) in actors:
                n, is_enemy = actors[(x, y)]
                im = sc(n)
                scr.alpha_composite(shadow, (px, py + S_ - 34))
                scr.alpha_composite(im, (px + (S_ - im.width) // 2, py + S_ - im.height + round(8 * k) - 10))
    # HUD samples on the side panel
    for i, n in enumerate(("hero_head", "hud_bomb", "hud_flame", "hud_speed", "hud_clock")):
        im = OUT[n]
        scr.alpha_composite(im, (120, 90 + i * 110))
    scr.convert("RGB").save(path)


# ================================================================== main
def main():
    want = sys.argv[1:]
    for fn in (make_floors, make_solids, make_overlays, make_hero, make_enemies, make_bomb, make_flames, make_items,
               make_cars, make_bosses, make_backgrounds, make_ui, make_icons):
        fn()
    bad = check_contract()
    os.makedirs(ART, exist_ok=True)
    os.makedirs(ICONS, exist_ok=True)
    os.makedirs(VERIF, exist_ok=True)
    n = 0
    for k, im in OUT.items():
        if want and not any(w in k for w in want):
            continue
        im.save(os.path.join(ART, k + ".png"), optimize=not k.startswith("bg_"))
        n += 1
    for k, im in ICON_OUT.items():
        if want and not any(w in k for w in want):
            continue
        im.save(os.path.join(ICONS, k + ".png"))
        n += 1
    build_sheet()
    build_mock("city", os.path.join(VERIF, "art_mock.png"))
    for t in THEMES[1:]:
        build_mock(t, os.path.join(VERIF, f"art_mock_{t}.png"))
    print(f"saved {n} files; contract has {len(contract())} + 3 icons")
    for b in bad:
        print("!!", b)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
