#!/usr/bin/env python3
"""All audio for the game, generated from scratch (no samples, no third-party sound).

Usage (from anywhere):
    python3 tools/make_music.py            render music + effects, then verify
    python3 tools/make_music.py music      render the 8 music files
    python3 tools/make_music.py sfx        render the effects
    python3 tools/make_music.py verify     measure what is in Assets/Resources/Audio
    python3 tools/make_music.py m_city boom ...   render only the named files

Output: Assets/Resources/Audio/*.wav, 44.1 kHz, 16-bit, mono.
Deterministic: same code, same bytes. numpy lives in .pylibs/ (pip install --target).
Loop safety: every note, echo and filter of a looping track is computed on a circle,
so the end of a track flows into its start exactly like any other point of the track.
"""
import math
import os
import re
import sys
import wave

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, ".pylibs"))
import numpy as np  # noqa: E402

SR = 44100
OUT = os.path.join(ROOT, "Assets", "Resources", "Audio")
PEAK_DB = -3.0
TAU = 2 * math.pi


def db(v):
    return 10 ** (v / 20)


def write_wav(name, x):
    os.makedirs(OUT, exist_ok=True)
    pcm = np.round(np.clip(x, -1, 1) * 32767).astype("<i2")
    with wave.open(os.path.join(OUT, name + ".wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


def read_wav(path):
    with wave.open(path, "rb") as w:
        info = (w.getframerate(), w.getnchannels(), w.getsampwidth())
        raw = w.readframes(w.getnframes())
    return np.frombuffer(raw, "<i2").astype(np.float64) / 32768, info


# ---------------------------------------------------------------- oscillators
TBL = 4096
_tables = {}


def table(wave_name, f0, maxh=None):
    """One band-limited cycle: no harmonic above 18 kHz, so no aliasing."""
    nh = max(1, int(18000 / f0))
    if maxh:
        nh = min(nh, maxh)
    key = (wave_name, nh)
    if key in _tables:
        return _tables[key]
    ph = np.arange(TBL) / TBL
    y = np.zeros(TBL)
    if wave_name == "sin":
        y = np.sin(TAU * ph)
    elif wave_name == "tri":
        for k in range(1, nh + 1, 2):
            y += ((-1) ** ((k - 1) // 2)) * np.sin(TAU * k * ph) / (k * k)
    elif wave_name == "saw":
        for k in range(1, nh + 1):
            y += np.sin(TAU * k * ph) / k
    else:  # p12, p25, p50: pulse with that duty in percent
        d = int(wave_name[1:]) / 100
        for k in range(1, nh + 1):
            y += math.sin(math.pi * k * d) / k * np.cos(TAU * k * (ph - d / 2))
    y = y - y.mean()
    y /= np.abs(y).max()
    _tables[key] = y
    return y


def lookup(tb, phase):
    p = (phase % 1.0) * TBL
    i = p.astype(np.int64)
    fr = p - i
    return tb[i % TBL] * (1 - fr) + tb[(i + 1) % TBL] * fr


def osc(wave_name, f, maxh=None):
    """f: array of frequency per sample."""
    return lookup(table(wave_name, float(np.max(f)), maxh), np.cumsum(f) / SR)


def n_of(sec):
    return int(round(sec * SR))


def tt(sec):
    return np.arange(n_of(sec)) / SR


def sweep(f0, f1, sec, total=None):
    """Exponential pitch glide over sec, then hold, length total."""
    n = n_of(total if total else sec)
    k = min(n, n_of(sec))
    f = np.full(n, float(f1))
    f[:k] = f0 * (f1 / f0) ** (np.arange(k) / max(1, k - 1))
    return f


def decay(tau, sec):
    return np.exp(-tt(sec) / tau)


def noise(sec, seed):
    return np.random.default_rng(seed).uniform(-1, 1, n_of(sec))


def svf(x, fc, q=0.7, mode="lp"):
    """Zero-delay state-variable filter; fc may change per sample."""
    fc = np.broadcast_to(np.minimum(np.asarray(fc, float), SR * 0.45), x.shape)
    g = np.tan(np.pi * fc / SR)
    k = 1.0 / q
    a1 = 1 / (1 + g * (g + k))
    a2 = g * a1
    a3 = g * a2
    ic1 = ic2 = 0.0
    out = [0.0] * len(x)
    for i, (v0, b1, b2, b3) in enumerate(zip(x.tolist(), a1.tolist(), a2.tolist(), a3.tolist())):
        v3 = v0 - ic2
        v1 = b1 * ic1 + b2 * v3
        v2 = ic2 + b2 * ic1 + b3 * v3
        ic1 = 2 * v1 - ic1
        ic2 = 2 * v2 - ic2
        if mode == "lp":
            out[i] = v2
        elif mode == "bp":
            out[i] = v1
        else:
            out[i] = v0 - k * v1 - v2
    return np.array(out)


def metal(freqs, taus, sec):
    t = tt(sec)
    return sum(np.sin(TAU * f * t) * np.exp(-t / a) for f, a in zip(freqs, taus)) / len(freqs)


def mixat(sec, *parts):
    """parts: (start_seconds, array, gain)."""
    y = np.zeros(n_of(sec))
    for start, a, g in parts:
        s = n_of(start)
        m = min(len(a), len(y) - s)
        if m > 0:
            y[s:s + m] += g * a[:m]
    return y


# ---------------------------------------------------------------- instruments
PATCH = {
    # w: wave, w2 + pwm: slow crossfade to a second wave, det: detuned twin (cents)
    "lead":  dict(w="p25", w2="p50", pwm=2.3, a=.004, d=.14, s=.72, r=.05, vib=16, det=7),
    "reed":  dict(w="p12", w2="p25", pwm=1.7, a=.006, d=.16, s=.70, r=.05, vib=26, det=5),
    "hero":  dict(w="p50", w2="p25", pwm=1.1, a=.005, d=.18, s=.78, r=.06, vib=14, det=8),
    "brass": dict(w="saw", maxh=26, a=.012, d=.20, s=.74, r=.07, vib=12, det=9),
    "stacc": dict(w="p25", a=.002, d=.07, s=.22, r=.03, vib=0, det=6),
    "bell":  dict(w="p12", a=.002, d=.20, s=.0, r=.08, vib=0, det=0, oct=.5),
    "harm":  dict(w="p50", a=.006, d=.16, s=.6, r=.05, vib=0, det=0),
    "arp":   dict(w="p12", a=.002, d=.09, s=.0, r=.03, vib=0, det=0),
    "stab":  dict(w="p25", a=.002, d=.06, s=.12, r=.03, vib=0, det=8),
    "pad":   dict(w="saw", maxh=10, a=.09, d=.4, s=.8, r=.14, vib=0, det=11),
    "bass":  dict(w="p50", maxh=14, w3="tri", a=.003, d=.12, s=.68, r=.03, vib=0, det=0),
    "bpop":  dict(w="p25", maxh=30, w3="tri", dull=.05, a=.002, d=.10, s=.45, r=.03, vib=0, det=0),
    "bsaw":  dict(w="saw", maxh=20, w3="tri", dull=.09, a=.003, d=.14, s=.6, r=.04, vib=0, det=0),
}
_voices = {}


def voice(patch, m, n_gate):
    key = (patch, m, n_gate)
    if key in _voices:
        return _voices[key]
    p = PATCH[patch]
    n = n_gate + n_of(p["r"])
    t = np.arange(n) / SR
    f0 = 440 * 2 ** ((m - 69) / 12)
    f = np.full(n, f0)
    if p["vib"]:
        ramp = np.clip((t - .13) / .12, 0, 1)
        f = f * 2 ** (p["vib"] * ramp * np.sin(TAU * 5.6 * t) / 1200)
    mh = p.get("maxh")
    ph = np.cumsum(f) / SR
    x = lookup(table(p["w"], f0, mh), ph)
    if "w2" in p:
        mix = .5 + .5 * np.sin(TAU * p["pwm"] * t)
        x = x * (1 - mix) + lookup(table(p["w2"], f0, mh), ph) * mix
    if p["det"]:
        x = (x + .6 * lookup(table(p["w"], f0, mh), ph * 2 ** (p["det"] / 1200))) / 1.6
    if "oct" in p:
        x = x + p["oct"] * np.sin(TAU * 2 * ph)
    if "w3" in p:
        soft = lookup(table(p["w3"], f0, 9), ph)
        if "dull" in p:  # bright attack that closes into a soft body
            b = np.exp(-t / p["dull"])
            x = x * b + soft * (1 - b)
        else:
            x = .6 * x + .6 * soft
    e = p["s"] + (1 - p["s"]) * np.exp(-np.maximum(t - p["a"], 0) / p["d"])
    e = np.minimum(e, t / p["a"]) if p["a"] else e
    g = n_gate / SR
    rel = np.clip(1 - (t - g) / p["r"], 0, 1)
    y = x * e * rel
    _voices[key] = y
    return y


def _drums():
    d = {}
    f = sweep(175, 48, .10, .22)
    d["k"] = np.tanh(2.2 * np.sin(TAU * np.cumsum(f) / SR) * decay(.075, .22)) \
        + .25 * svf(noise(.22, 1), 3000, 1, "hp") * decay(.003, .22)
    body = np.sin(TAU * np.cumsum(sweep(240, 175, .03, .18)) / SR) * decay(.04, .18)
    d["s"] = .55 * body + .8 * svf(noise(.18, 2), 3200, .8, "bp") * decay(.055, .18) \
        + .35 * svf(noise(.18, 3), 6000, .8, "hp") * decay(.03, .18)
    # hats: held-sample noise for a hard chip edge
    nz = np.repeat(noise(.2, 4)[::2], 2)
    d["h"] = svf(nz[:n_of(.06)], 7500, .8, "hp") * decay(.012, .06)
    d["o"] = svf(nz[:n_of(.2)], 6500, .8, "hp") * decay(.06, .2)
    d["t"] = np.sin(TAU * np.cumsum(sweep(210, 95, .09, .26)) / SR) * decay(.09, .26) \
        + .15 * svf(noise(.26, 5), 900, 1, "bp") * decay(.02, .26)
    d["u"] = np.sin(TAU * np.cumsum(sweep(330, 165, .07, .18)) / SR) * decay(.06, .18)
    d["c"] = metal([830, 1370, 2140, 3170], [.05, .04, .03, .02], .14) * 1.6
    d["x"] = svf(noise(1.0, 6), 5000, .8, "hp") * decay(.32, 1.0)
    d["z"] = svf(noise(.05, 7), 5500, 1.2, "bp") * decay(.011, .05)
    return {k: v / np.abs(v).max() for k, v in d.items()}


DRUM = _drums()

# ---------------------------------------------------------------- notation
NOTE = re.compile(r"^([a-g])([#b]?)(\d):?(\d+)?$")
PC = dict(c=0, d=2, e=4, f=5, g=7, a=9, b=11)
CHORD = re.compile(r"^([A-G])([#b]?)(m7|m|7|dim)?$")
QUAL = {None: (0, 4, 7), "m": (0, 3, 7), "7": (0, 4, 7, 10), "m7": (0, 3, 7, 10), "dim": (0, 3, 6)}


def parse_mel(s, bar=16):
    """'c5:2 .:2 e5' -> [(start16, dur16, midi|None)]. Duration in 16ths; each bar must add up."""
    ev, pos, dur = [], 0, 4
    for i, b in enumerate(s.split("|")):
        used = 0
        for tok in b.split():
            if tok.startswith("."):
                dur = int(tok[2:]) if ":" in tok else dur
                m = None
            else:
                g = NOTE.match(tok)
                if not g:
                    raise ValueError("bad note " + tok)
                dur = int(g[4]) if g[4] else dur
                m = 12 * (int(g[3]) + 1) + PC[g[1]] + {"#": 1, "b": -1, "": 0}[g[2]]
            if m is not None:
                ev.append((pos + used, dur, m))
            used += dur
        if bar and used != bar:
            raise ValueError(f"bar {i + 1} has {used} sixteenths: {b}")
        pos += used
    return ev


def parse_chords(s):
    """'G | Am D | C' -> list per bar of [(start16 in bar, root pc, intervals)]."""
    bars = []
    for b in s.split("|"):
        names = b.split()
        row = []
        for i, nm in enumerate(names):
            g = CHORD.match(nm)
            if not g:
                raise ValueError("bad chord " + nm)
            pc = PC[g[1].lower()] + {"#": 1, "b": -1, "": 0}[g[2]]
            row.append((i * 16 // len(names), pc % 12, QUAL[g[3]]))
        bars.append(row)
    return bars


def chord_at(row, step):
    cur = row[0]
    for c in row:
        if c[0] <= step:
            cur = c
    return cur


def S(mel, chords, bass, arp, drums, lead="lead", oct=0, harm=False, pad=False, gain=1.0):
    return dict(mel=parse_mel(mel), chords=parse_chords(chords), bass=bass.split(), arp=arp.split(),
                drums=drums, lead=lead, oct=oct, harm=harm, pad=pad, gain=gain)


def replace_bars(mel, new_tail):
    """Same phrase, new last bars (a variation of an 8-bar phrase)."""
    a = mel.split("|")
    b = new_tail.split("|")
    return "|".join(a[:len(a) - len(b)] + b)


# ---------------------------------------------------------------- the composer's score
ARP_UP = "0 1 2 3 2 1 0 1 0 1 2 3 2 1 2 1"
ARP_8 = "0 . 1 . 2 . 1 . 3 . 2 . 1 . 2 ."
ARP_OFF = ". . 2 . . . 1 . . 2 . . 1 . . ."
ARP_RUN = "0 1 2 3 0 1 2 3 1 2 3 2 1 2 3 2"
NONE = ". " * 16


def title():
    A = ("d5:2 g5:2 b5:3 a5:1 g5:2 a5:2 b5:4 | e5:2 g5:2 b5:3 a5:1 g5:4 e5:4 |"
         "c5:2 e5:2 g5:3 f#5:1 e5:2 g5:2 c6:4 | a5:3 f#5:3 d5:2 e5:2 f#5:2 a5:4 |"
         "b5:2 d6:2 b5:2 g5:2 a5:2 b5:2 g5:4 | e5:2 g5:2 e5:2 b4:2 c5:2 d5:2 e5:4 |"
         "a4:2 c5:2 e5:2 a5:2 f#5:2 e5:2 d5:2 f#5:2 | g5:6 d5:2 g5:8")
    A2 = replace_bars(A, "c6:2 b5:2 a5:2 g5:2 f#5:3 g5:1 a5:4 | g5:4 .:2 d5:2 g5:2 b5:2 d6:4")
    B = ("e6:6 d6:2 c6:4 g5:4 | b5:6 a5:2 g5:4 d5:4 | c6:6 b5:2 a5:4 e5:4 | g5:4 b5:4 e6:8 |"
         "f5:2 a5:2 c6:2 f6:2 e6:2 c6:2 a5:4 | g5:2 c6:2 e6:2 g6:2 f6:2 e6:2 c6:4 |"
         "d6:3 c6:3 b5:2 a5:3 g5:3 f#5:2 | a5:2 f#5:2 a5:2 c6:2 d6:8")
    cA = "G | Em | C | D | G | Em | Am D | G"
    cB = "C | G | Am | Em | F | C | D | D7"
    d = dict(k="x.....x.x.....x.", s="....x.......x...", h="x-x-x-x-x-x-x-x-")
    d2 = dict(k="x.....x.x.x...x.", s="....x.......x...", h="x-x-x-x-x-x-x-x-", o="..............x.")
    bs = "R . R . O . R . R . R . O . 5 ."
    return dict(name="m_title", bpm=138, key="G major", swing=0, fill=dict(s="....x...x.x.xxxx"),
                sections=[S(A, cA, bs, ARP_8, d, "hero"),
                          S(A2, cA, bs, ARP_UP, d2, "hero", harm=True),
                          S(B, cB, "R . . R . . R . 5 . . 5 . . O .", ARP_UP, d, "lead", pad=True),
                          S(A2, cA, bs, ARP_RUN, d2, "hero", oct=12, harm=True)])


def city():
    A = ("c5:2 .:1 f5:1 .:2 a5:2 .:2 g5:2 f5:2 d5:2 | e5:2 .:1 a5:1 .:2 c6:2 .:2 b5:2 a5:2 g5:2 |"
         "f5:2 d5:2 f5:2 bb5:2 a5:2 f5:2 d5:2 f5:2 | g5:3 e5:3 c5:2 g5:6 .:2 |"
         "c6:2 .:1 a5:1 .:2 f5:2 .:2 a5:2 c6:2 d6:2 | c6:2 .:1 a5:1 .:2 e5:2 .:2 e5:2 g5:2 a5:2 |"
         "bb5:2 a5:2 g5:2 f5:2 e5:2 f5:2 g5:2 e5:2 | f5:4 .:2 c5:2 f5:4 .:4")
    B = ("a5:3 a5:3 f5:2 d5:4 e5:2 f5:2 | bb5:3 bb5:3 f5:2 d5:4 f5:2 g5:2 |"
         "g5:3 bb5:3 d6:2 c6:2 bb5:2 a5:2 g5:2 | e5:2 g5:2 c6:4 bb5:4 g5:4 |"
         "a5:3 a5:3 c6:2 d6:4 c6:2 a5:2 | bb5:3 d6:3 f6:2 d6:4 bb5:4 |"
         "b5:2 d6:2 g6:2 f6:2 d6:2 b5:2 g5:2 b5:2 | c6:2 .:2 g5:2 .:2 e5:2 f5:2 g5:2 bb5:2")
    C = ("d6:4 c6:2 bb5:2 f5:4 d5:4 | c6:4 a5:2 g5:2 f5:4 c5:4 |"
         "d6:2 f6:2 d6:2 c6:2 bb5:2 d6:2 bb5:2 a5:2 | g5:4 c6:4 e6:4 g6:4 |"
         "f6:4 d6:2 c6:2 bb5:4 f5:4 | a5:4 c6:2 a5:2 f5:4 a5:4 |"
         "g5:2 a5:2 bb5:2 c6:2 d6:2 c6:2 bb5:2 a5:2 | g5:4 e5:4 g5:2 a5:2 bb5:2 g5:2")
    cA = "F | Am | Bb | C | F | Am | Bb C | F"
    cB = "Dm | Bb | Gm | C | Dm | Bb | G | C"
    cC = "Bb | F | Bb | C | Bb | F | Gm | C"
    d = dict(k="x..x..x...x..x..", s="....x.......x..-", h="x.x.x.x.x.x.x.x.")
    bs = "R . . O . . R . 5 . . O . . 5 ."
    return dict(name="m_city", bpm=150, key="F major", swing=.22, fill=dict(s="....x..x.x..x.xx"), gains=dict(lead=.56, bass=.15, k=.30),
                sections=[S(A, cA, bs, ARP_OFF, d, "stacc"),
                          S(B, cB, bs, ARP_8, d, "lead"),
                          S(A, cA, bs, ARP_OFF, d, "stacc", harm=True),
                          S(C, cC, "R . . . O . . . 5 . . . O . 5 .", ARP_8, d, "bell", pad=True)])


def desert():
    A = ("e5:3 f5:1 g5:4 b5:3 a5:1 g5:4 | a5:3 g5:1 f5:2 e5:2 f5:4 c5:4 |"
         "e5:3 f5:1 g5:2 a5:2 b5:4 e6:4 | d6:3 c6:1 b5:2 a5:2 f5:4 d5:4 |"
         "c5:2 e5:2 g5:2 c6:2 b5:2 c6:2 g5:4 | a5:2 c6:2 f6:4 e6:2 d6:2 c6:4 |"
         "b5:2 c6:2 b5:2 a5:2 g#5:4 f5:4 | e5:8 .:2 b4:2 e5:2 f5:2")
    A2 = replace_bars(A, "g#5:3 a5:1 b5:4 c6:2 b5:2 a5:2 g#5:2 | e6:8 b5:4 e5:4")
    B = ("a5:2 c6:2 e6:4 d6:2 c6:2 b5:2 a5:2 | g5:2 b5:2 d6:4 c6:2 b5:2 a5:2 g5:2 |"
         "f5:2 a5:2 c6:4 b5:2 a5:2 g5:2 f5:2 | e5:1 f5:1 g#5:2 b5:4 a5:1 g#5:1 f5:2 e5:4 |"
         "e6:3 d6:1 c6:2 e6:2 a6:4 e6:4 | d6:3 c6:1 b5:2 d6:2 g6:4 d6:4 |"
         "c6:3 b5:1 a5:2 c6:2 f6:4 c6:4 | b5:2 g#5:2 e5:2 f5:2 g#5:2 b5:2 e6:4")
    cA = "Em | F | Em | Dm | C | F | E | E"
    cB = "Am | G | F | E | Am | G | F | E"
    d = dict(k="x..x...x..x.....", s="......x....x..-.", t="....x......x....", u="..x.....x.....x.",
             z="x-x-x-x-x-x-x-x-")
    bs = "R . . R . . 5 . R . . O . 5 . ."
    return dict(name="m_desert", bpm=116, key="E Phrygian / Phrygian dominant", swing=0,
                fill=dict(t="x.x.x.x.x.xxx.x.", s="...............x"),
                sections=[S(A, cA, bs, ARP_8, d, "reed", pad=True),
                          S(A2, cA, bs, ARP_UP, d, "reed", harm=True),
                          S(B, cB, "R . R . 5 . R . O . R . 5 . R .", ARP_RUN, d, "lead", oct=-12),
                          S(A2, cA, bs, ARP_UP, d, "reed", harm=True, pad=True)])


def factory():
    A = ("a5:1 .:1 c6:1 .:2 a5:1 .:2 d6:2 .:2 c6:1 a5:1 g5:2 | f5:1 .:1 g5:1 .:2 a5:2 .:3 f5:1 g5:1 f5:1 d5:1 .:2 |"
         "b5:1 .:1 d6:1 .:2 b5:1 .:2 f6:2 .:2 d6:1 b5:1 a5:2 | g5:1 .:1 a5:1 .:2 b5:2 .:3 a5:1 g5:1 f5:1 g5:1 .:2 |"
         "d6:1 .:1 f6:1 .:2 d6:1 .:2 a6:2 .:2 g6:1 f6:1 d6:2 | c6:1 .:1 d6:1 .:2 f6:2 .:3 d6:1 c6:1 a5:1 c6:1 .:2 |"
         "bb5:2 .:1 d6:1 .:2 f6:2 e6:2 .:1 c#6:1 .:2 a5:2 | d6:2 .:2 a5:1 c6:1 d6:1 .:1 .:8")
    B = ("bb5:4 a5:2 g5:2 d6:6 .:2 | c6:4 bb5:2 g5:2 e5:6 .:2 |"
         "a5:2 c6:2 f6:2 e6:2 c6:2 a5:2 f5:2 a5:2 | bb5:2 d6:2 f6:2 d6:2 bb5:4 .:4 |"
         "g5:1 a5:1 bb5:2 d6:2 g6:4 f6:2 d6:2 bb5:2 | c6:1 d6:1 e6:2 g6:2 bb6:4 g6:2 e6:2 c6:2 |"
         "b5:2 g#5:2 e5:2 g#5:2 d6:4 b5:4 | c#6:2 e6:2 a5:2 c#6:2 e6:2 g6:2 e6:2 c#6:2")
    cA = "Dm7 | Dm7 | G7 | G7 | Dm7 | Dm7 | Bb7 A7 | Dm7"
    cB = "Gm7 | C7 | F | Bb | Gm7 | C7 | E7 | A7"
    d = dict(k="x..x..x..x.x....", s="....x..-.-..x...", h="x-x-x-x-x-x-x-x-", c="..x...x...x..x..")
    bs = "R . . R . . 7 . R . 5 . O . 7 ."
    return dict(name="m_factory", bpm=108, key="D Dorian", swing=.16, fill=dict(s="....x..-.x.-x.xx"),
                gains=dict(arp=.16, lead=.56, bass=.17), arp_patch="stab",
                sections=[S(A, cA, bs, ARP_OFF, d, "stacc", oct=-12),
                          S(B, cB, "R . . O . . R . . 5 . 7 . O . .", ARP_OFF, d, "lead", oct=-12, pad=True),
                          S(A, cA, bs, ARP_OFF, d, "stacc", harm=True)])


def fortress():
    A = ("g4:6 g4:1 g4:1 bb4:4 d5:4 | d5:3 c5:1 bb4:2 a4:2 g4:8 |"
         "eb5:6 eb5:1 eb5:1 d5:4 c5:4 | d5:3 eb5:1 d5:2 c5:2 a4:4 f#4:4 |"
         "g5:6 g5:1 g5:1 f5:4 d5:4 | f5:3 d5:1 bb4:4 d5:4 f5:4 |"
         "eb5:3 d5:1 c5:2 eb5:2 g5:4 c5:4 | d5:3 c5:1 a4:2 f#4:2 d5:8")
    B = ("g5:4 bb5:4 eb6:6 d6:1 c6:1 | d6:4 bb5:4 f5:8 | eb6:4 c6:4 g5:6 ab5:1 bb5:1 | b5:4 g5:4 d5:8 |"
         "c6:4 eb6:4 ab6:6 g6:1 f6:1 | g6:4 eb6:4 bb5:8 | c6:3 c6:1 eb6:4 a5:4 c6:4 | d6:3 d6:1 f#5:4 a5:4 c6:4")
    cA = "Gm | Gm | Eb | D | Gm | Bb | Cm | D"
    cB = "Eb | Bb | Cm | G | Ab | Eb | Adim | D"
    d = dict(k="x.......x.......", s="x--x-x-xx--x-x--", t="x...x...x...x...", h="....x.......x...")
    bs = "R . . . R . . . 5 . . . R . 5 ."
    return dict(name="m_fortress", bpm=100, key="G minor", swing=0,
                fill=dict(s="x-x-x-x-xxxxxxxx", t="x...x...x.x.x.x."), gains=dict(pad=.16, s=.24, arp=.08),
                sections=[S(A, cA, bs, NONE, d, "brass", oct=12, pad=True),
                          S(B, cB, "R . . R R . . . 5 . . 5 R . . .", ARP_8, d, "hero", pad=True),
                          S(A, cA, bs, ARP_8, d, "brass", oct=12, harm=True, pad=True)])


def boss():
    A = ("b5:2 b5:1 b5:1 d6:2 b5:2 f#6:3 e6:1 d6:2 c#6:2 | d6:2 d6:1 d6:1 c#6:2 b5:2 a5:4 f#5:4 |"
         "g5:2 g5:1 g5:1 b5:2 g5:2 d6:3 c#6:1 b5:2 a5:2 | a#5:2 a#5:1 a#5:1 c#6:2 a#5:2 f#6:8 |"
         "b5:2 b5:1 b5:1 d6:2 b5:2 f#6:3 g6:1 f#6:2 e6:2 | d6:2 e6:2 f#6:2 d6:2 b5:4 f#5:4 |"
         "g5:2 b5:2 d6:2 g6:2 a5:2 c#6:2 e6:2 a6:2 | b6:4 f#6:2 d6:2 b5:8")
    B = ("a5:4 f#6:4 e6:2 d6:2 c#6:2 d6:2 | e6:4 c#6:4 a5:8 | g5:4 e6:4 d6:2 b5:2 g5:2 b5:2 |"
         "f#5:2 b5:2 d6:2 f#6:2 b6:8 | a6:2 f#6:2 d6:2 a5:2 b5:2 d6:2 f#6:4 |"
         "e6:2 c#6:2 a5:2 e5:2 f#5:2 a5:2 c#6:4 | d6:2 b5:2 g5:2 b5:2 d6:2 g6:2 b6:4 | a#6:4 f#6:4 c#6:4 a#5:4")
    C = ("f#5:2 b5:2 f#6:4 e6:2 d6:2 c#6:2 d6:2 | g5:2 c6:2 g6:4 f6:2 e6:2 d6:2 e6:2 |"
         "f#5:2 b5:2 f#6:4 a6:2 f#6:2 d6:2 f#6:2 | g5:2 c6:2 g6:4 b6:2 g6:2 e6:2 g6:2 |"
         "d6:1 e6:1 d6:1 b5:1 g5:2 b5:2 d6:4 g6:4 | e6:1 f#6:1 e6:1 c#6:1 a5:2 c#6:2 e6:4 a6:4 |"
         "f#6:2 a#6:2 c#7:4 a#6:2 f#6:2 c#6:2 a#5:2 | f#6:4 e6:4 d6:4 c#6:4")
    cA = "Bm | Bm | G | F# | Bm | Bm | G A | Bm"
    cB = "D | A | Em | Bm | D | A | G | F#"
    cC = "Bm | C | Bm | C | G | A | F# | F#"
    d = dict(k="x..x..x.x..x..x.", s="....x.......x...", h="xxxxxxxxxxxxxxxx")
    d2 = dict(k="x.x.x.x.x.x.x.x.", s="....x.......x..x", h="x-x-x-x-x-x-x-x-", o="..x...x...x...x.")
    bs = "R R . R R . R . R R . R 5 . O ."
    return dict(name="m_boss", bpm=172, key="B minor", swing=0, fill=dict(s="..x..x..x.x.xxxx", t="............x.x."),
                gains=dict(bass=.14, k=.28),
                sections=[S(A, cA, bs, ARP_RUN, d, "lead", oct=-12),
                          S(B, cB, bs, ARP_UP, d2, "brass", oct=-12, pad=True),
                          S(A, cA, bs, ARP_RUN, d, "lead", oct=-12, harm=True),
                          S(C, cC, "R . R . R . R . O . O . 5 . 5 .", ARP_RUN, d2, "brass", oct=-12),
                          S(A, cA, bs, ARP_RUN, d2, "hero", harm=True)])


def battle():
    A = ("e5:1 a5:1 c#6:2 e6:2 c#6:2 a5:2 c#6:2 e6:4 | f#6:2 e6:2 c#6:2 a5:2 f#5:2 a5:2 c#6:4 |"
         "d6:1 e6:1 f#6:2 a6:2 f#6:2 d6:2 f#6:2 a6:4 | g#6:2 f#6:2 e6:2 b5:2 g#5:2 b5:2 e6:4 |"
         "a5:2 c#6:2 e6:2 a6:2 g#6:2 e6:2 c#6:2 e6:2 | g#5:2 c#6:2 e6:2 g#6:2 f#6:2 e6:2 c#6:2 b5:2 |"
         "a5:2 d6:2 f#6:2 a6:2 g#6:2 e6:2 f#6:2 g#6:2 | a6:4 e6:2 c#6:2 a5:4 .:4")
    B = ("c#6:4 f#6:4 e6:2 c#6:2 a5:4 | d6:4 f#6:4 e6:2 d6:2 a5:4 | e6:4 a6:4 g#6:2 e6:2 c#6:4 |"
         "b5:4 e6:4 g#6:2 f#6:2 e6:4 | a6:2 g#6:2 f#6:2 c#6:2 a5:2 c#6:2 f#6:4 |"
         "f#6:2 e6:2 d6:2 a5:2 f#5:2 a5:2 d6:4 | d#6:2 f#6:2 b6:4 a6:2 f#6:2 d#6:4 |"
         "e6:2 g#6:2 b6:4 g#6:2 e6:2 b5:2 d6:2")
    C = ("a5:2 c6:2 f6:4 e6:2 f6:2 a6:4 | b5:2 d6:2 g6:4 f6:2 g6:2 b6:4 |"
         "c#6:2 e6:2 a6:4 g#6:2 a6:2 c#7:4 | c#7:2 b6:2 a6:2 e6:2 c#6:2 e6:2 a6:4 |"
         "c7:2 a6:2 f6:2 c6:2 a5:2 c6:2 f6:4 | d7:2 b6:2 g6:2 d6:2 b5:2 d6:2 g6:4 |"
         "e6:1 f#6:1 g#6:2 b6:2 g#6:2 e6:2 g#6:2 b6:4 | e7:4 d7:2 b6:2 g#6:2 e6:2 b5:2 g#5:2")
    cA = "A | F#m | D | E | A | C#m | D E | A"
    cB = "F#m | D | A | E | F#m | D | B | E"
    cC = "F | G | A | A | F | G | E | E"
    d = dict(k="x...x...x...x...", s="....x.......x.x.", h="x.x.x.x.x.x.x.x.", o="..x...x...x...x.")
    bs = "R . O . R . O . R . O . 5 . O ."
    return dict(name="m_battle", bpm=184, key="A major", swing=0, fill=dict(s="....x.x.x.x.xxxx"),
                sections=[S(A, cA, bs, ARP_8, d, "lead", oct=-12),
                          S(B, cB, bs, ARP_UP, d, "hero", oct=-12),
                          S(A, cA, bs, ARP_RUN, d, "lead", oct=-12, harm=True),
                          S(C, cC, bs, ARP_UP, d, "hero", oct=-12, pad=True),
                          S(A, cA, bs, ARP_RUN, d, "lead", harm=True)])


def ending():
    A = ("a5:2 d6:2 f#6:6 e6:2 d6:4 | e6:2 c#6:2 a5:8 e5:2 a5:2 | b5:2 d6:2 f#6:6 e6:2 d6:4 |"
         "d6:2 b5:2 g5:8 b5:2 d6:2 | f#6:4 a6:4 f#6:2 e6:2 d6:4 | e6:4 a6:4 e6:2 d6:2 c#6:4 |"
         "d6:2 e6:2 f#6:2 g6:2 a6:2 g6:2 f#6:2 e6:2 | d6:6 a5:2 d6:2 f#6:2 a6:4 |"
         "b6:4 a6:2 g6:2 d6:4 g6:4 | a6:4 g6:2 f#6:2 e6:2 c#6:2 e6:2 a6:2 |"
         "f#6:2 a6:2 d7:4 a6:2 f#6:2 a6:2 c#7:2 | .:16")
    ch = "D | A | Bm | G | D | A | G A | D | G | A | D | D"
    d = dict(k="x.......x.......", s="....x.......x...", h="x.x.x.x.x.x.x.x.")
    return dict(name="m_ending", bpm=120, key="D major", swing=0, fill={}, loop=False, tail=1.6, final=dict(lead=74, tones=(69, 66), bass=(38, 50), pad=(62, 66, 69)),
                sections=[S(A, ch, "R . . R . . R . 5 . . 5 . . O .", ARP_8, d, "hero", oct=-12, harm=True, pad=True)])


TRACKS = dict(m_title=title, m_city=city, m_desert=desert, m_factory=factory, m_fortress=fortress,
              m_boss=boss, m_battle=battle, m_ending=ending)

# ---------------------------------------------------------------- arrangement -> audio
GAINS = dict(lead=.42, harm=.15, arp=.13, bass=.19, pad=.10, k=.36, s=.30, h=.13, o=.11, t=.26, u=.20,
             c=.17, x=.11, z=.10)


def render_track(tr):
    bpm, sw = tr["bpm"], tr["swing"]
    sp16 = 60 / bpm / 4
    bars = sum(len(s["chords"]) for s in tr["sections"])
    loop = tr.get("loop", True)
    L = int(round(bars * 16 * sp16 * SR))
    size = L if loop else L + n_of(tr.get("tail", 2))
    gains = dict(GAINS, **tr.get("gains", {}))
    stems = {k: np.zeros(size) for k in ("lead", "harm", "arp", "bass", "pad", "drum")}

    def at(step):
        if int(step) % 2 == 1:
            step = step + sw
        return int(round(step * sp16 * SR))

    def put(stem, a, start, g):
        start %= size
        m = min(len(a), size - start)
        stem[start:start + m] += g * a[:m]
        if m < len(a):  # wraps past the loop point: lands on the start of the track
            if not loop:
                return
            stem[:len(a) - m] += g * a[m:]

    def gate(s0, dur, frac=.92):
        return max(n_of(.02), int((at(s0 + dur) - at(s0)) * frac))

    bar0 = 0
    notes_on_beat = chord_tones = 0
    for sec in tr["sections"]:
        nb = len(sec["chords"])
        # melody and harmony
        for s0, dur, m in sec["mel"]:
            bar, step = divmod(s0, 16)
            pos = (bar0 * 16) + s0
            mm = m + sec["oct"]
            acc = 1.0 if step % 4 == 0 else .86
            put(stems["lead"], voice(sec["lead"], mm, gate(pos, dur)), at(pos), acc * sec["gain"])
            c = chord_at(sec["chords"][bar], step)
            pcs = {(c[1] + i) % 12 for i in c[2]}
            if step % 4 == 0:
                notes_on_beat += 1
                chord_tones += m % 12 in pcs
            if sec["harm"]:
                h = mm - 3
                while h % 12 not in pcs:
                    h -= 1
                put(stems["harm"], voice("harm", h, gate(pos, dur)), at(pos), acc)
        # bass, arpeggio, pad, drums per bar
        for b in range(nb):
            base = (bar0 + b) * 16
            row = sec["chords"][b]
            toks = sec["bass"] if not ("final" in tr and b == nb - 1) else ["."] * 16
            for st, tok in enumerate(toks):
                if tok == ".":
                    continue
                nxt = next((j for j in range(st + 1, 16) if toks[j] != "."), 16)
                c = chord_at(row, st)
                root = 36 + (c[1] - 36) % 12
                if root > 43:
                    root -= 12
                m = root + {"R": 0, "O": 12, "5": 7, "3": c[2][1], "7": 10}[tok]
                put(stems["bass"], voice(tr.get("bass_patch", "bass" if bpm > 125 else "bpop"), m,
                                         gate(base + st, nxt - st, .86)), at(base + st), 1.0 if st % 4 == 0 else .85)
            for st, tok in enumerate(sec["arp"]):
                if tok == "." or ("final" in tr and b == nb - 1):
                    continue
                c = chord_at(row, st)
                root = 60 + (c[1] - 60) % 12
                tones = [root + i for i in c[2][:3]] + [root + 12]
                put(stems["arp"], voice(tr.get("arp_patch", "arp"), tones[int(tok)], gate(base + st, 1.5)),
                    at(base + st), 1.0 if st % 4 == 0 else .75)
            if sec["pad"] and not ("final" in tr and b == nb - 1):
                for i, c in enumerate(row):
                    end = row[i + 1][0] if i + 1 < len(row) else 16
                    root = 55 + (c[1] - 55) % 12
                    for iv in c[2][:3]:
                        put(stems["pad"], voice("pad", root + iv, gate(base + c[0], end - c[0], 1.0)),
                            at(base + c[0]), 1.0)
            pats = dict(sec["drums"])
            if "final" in tr and b == nb - 1:
                continue
            if b == nb - 1:
                pats.update(tr["fill"])
            for inst, pat in pats.items():
                for st, ch in enumerate(pat):
                    if ch != ".":
                        put(stems["drum"], DRUM[inst], at(base + st), gains[inst] * (1 if ch == "x" else .45))
        put(stems["drum"], DRUM["x"], at(bar0 * 16), gains["x"] * 1.6)
        bar0 += nb
    final_at = None
    if "final" in tr:  # one held chord instead of the last bar
        fin = tr["final"]
        final_at = at((bars - 1) * 16)
        hold = size - final_at - n_of(.1)
        put(stems["drum"], DRUM["x"], final_at, gains["x"] * 2.4)
        put(stems["drum"], DRUM["k"], final_at, gains["k"])
        put(stems["lead"], voice("hero", fin["lead"], hold), final_at, 1)
        for m in fin["tones"]:
            put(stems["harm"], voice("harm", m, hold), final_at, 1)
        for m in fin["bass"]:
            put(stems["bass"], voice("bass", m, hold), final_at, 1)
        for m in fin["pad"]:
            put(stems["pad"], voice("pad", m, hold), final_at, 1)

    def echo(x, d, fb, n=3):
        y = x.copy()
        for i in range(1, n + 1):
            y += fb ** i * np.roll(x, i * d)
        return y

    d3 = int(round(3 * sp16 * SR))
    mix = (gains["lead"] * echo(stems["lead"], d3, .24) + gains["harm"] * stems["harm"]
           + gains["arp"] * echo(stems["arp"], d3, .2, 2) + gains["bass"] * stems["bass"]
           + gains["pad"] * stems["pad"] + stems["drum"])
    # Circular high-pass (the whole track is one period), then a gentle saturating limiter.
    sp = np.fft.rfft(mix)
    fr = np.fft.rfftfreq(len(mix), 1 / SR)
    sp *= 1 / (1 + (30 / np.maximum(fr, .1)) ** 4)
    sp[0] = 0
    mix = np.fft.irfft(sp, len(mix))
    mix /= np.abs(mix).max()
    mix = np.tanh(1.5 * mix) / math.tanh(1.5)
    if final_at is not None:
        n_fade = len(mix) - final_at
        mix[final_at:] *= np.linspace(1, 0, n_fade) ** 1.6
    mix *= db(PEAK_DB) / np.abs(mix).max()
    print(f"  {tr['name']}: {bars} bars at {bpm} bpm, {len(mix) / SR:.1f} s, "
          f"{chord_tones}/{notes_on_beat} on-beat melody notes are chord tones")
    return mix


# ---------------------------------------------------------------- effects
def jingle(parts, bpm, tail=.4, fb=.22):
    """parts: (patch, melody, gain). Same instruments as the music."""
    sp16 = 60 / bpm / 4
    lens = [max(s + d for s, d, _ in parse_mel(m, None)) for _, m, _ in parts]
    y = np.zeros(n_of(max(lens) * sp16 + tail))
    for patch, m, g in parts:
        for s, d, p in parse_mel(m, None):
            v = voice(patch, p, n_of(d * sp16 * .95))
            i = n_of(s * sp16)
            k = min(len(v), len(y) - i)
            y[i:i + k] += g * v[:k]
    d3 = n_of(3 * sp16)
    e = y.copy()
    e[d3:] += fb * y[:-d3]
    return e


def explosion(seed, dur=1.15, f0=125, f1=36, bright=5200, size=1.0):
    t = tt(dur)
    sub = np.sin(TAU * np.cumsum(sweep(f0, f1, .35 * size, dur)) / SR) * np.exp(-t / (.30 * size))
    fc = 170 + (bright - 170) * np.exp(-t / (.09 * size))
    body = svf(noise(dur, seed), fc, .9) * np.exp(-t / (.24 * size))
    crack = svf(noise(dur, seed + 1), 2500, .8, "hp") * np.exp(-t / .012)
    rumble = svf(noise(dur, seed + 2), 110, .9) * np.exp(-t / (.45 * size))
    x = 1.1 * sub + 2.0 * body + .7 * crack + 1.8 * rumble
    return np.tanh(1.6 * x / np.abs(x).max())


def grains(sec, count, seed, f_lo, f_hi, tau, q=4, front=2.0):
    rng = np.random.default_rng(seed)
    y = np.zeros(n_of(sec))
    for _ in range(count):
        start = rng.uniform(0, 1) ** front * sec * .85
        dur = tau * 5
        g = svf(noise(dur, int(rng.integers(1 << 30))), rng.uniform(f_lo, f_hi), q, "bp")
        g = g * decay(tau, dur) * rng.uniform(.4, 1)
        i = n_of(start)
        k = min(len(g), len(y) - i)
        y[i:i + k] += g[:k]
    return y


def thud(f0=190, f1=62, tau=.06, sec=.3):
    return np.sin(TAU * np.cumsum(sweep(f0, f1, .08, sec)) / SR) * decay(tau, sec)


def s_place():
    return mixat(.22, (0, thud(270, 95, .05, .22), 1), (0, svf(noise(.05, 11), 1800, 2, "bp") * decay(.008, .05), .9))


def s_fuse():
    return metal([2400, 3900], [.008, .005], .05) + .3 * svf(noise(.05, 12), 6000, 1, "hp") * decay(.003, .05)


def s_boom():
    return explosion(20)


def s_boom2():
    a = explosion(30, .95, 150, 44, 3600, .85)
    return mixat(1.1, (0, a, 1), (.07, explosion(40, .8, 95, 34, 2400, .7), .65))


def s_brick():
    return mixat(.42, (0, thud(200, 75, .05, .25), .8), (0, grains(.42, 11, 50, 700, 3000, .018), 1.5))


def s_kick():
    n = n_of(.2)
    wh = svf(noise(.2, 60), sweep(700, 4200, .17, .2), 1.5, "bp") * np.sin(np.linspace(0, np.pi, n)) ** 2
    return mixat(.2, (0, thud(340, 95, .04, .16), 1), (0, wh, .9))


def s_power():
    a = jingle([("bell", "c6:1 e6:1 g6:1 c7:4", 1), ("arp", "g6:1 c7:1 e7:1 g7:4", .4)], 300, .35, .3)
    return a


def s_power_bad():
    t = tt(.55)
    sizzle = svf(noise(.55, 70), 3500, .8, "hp") * (.55 + .45 * np.sign(np.sin(TAU * 31 * t))) * np.exp(-t / .16)
    down = osc("p25", sweep(760, 110, .42, .55)) * np.exp(-t / .2)
    return .8 * sizzle + down


def s_hurt():
    gl = metal([1830, 2790, 3910, 5230, 6890], [.10, .08, .06, .04, .03], .5)
    t = tt(.5)
    fall = osc("p50", sweep(980, 190, .24, .5)) * np.exp(-t / .1)
    return mixat(.5, (0, gl, 1.3), (0, fall, .55), (.02, grains(.48, 7, 80, 3500, 7500, .012, 3, 1.5), 1.0))


def s_die():
    t = tt(.8)
    f = sweep(900, 105, .74, .8) * (1 + .07 * np.sin(TAU * 13 * t))
    x = osc("p25", f) * np.clip((.8 - t) / .12, 0, 1) ** .5
    return mixat(1.05, (0, x, .8), (.74, thud(140, 50, .09, .31), 1), (.74, grains(.31, 5, 90, 400, 1200, .02), .7))


def s_enemy_die():
    f = np.concatenate([sweep(500, 1450, .06), sweep(1450, 190, .15)])
    t = np.arange(len(f)) / SR
    return osc("p50", f) * np.clip((t[-1] - t) / .05, 0, 1) * .8 + .5 * mixat(
        t[-1] + 1 / SR, (0, svf(noise(.08, 100), 2400, 1.2, "bp") * decay(.02, .08), 1))[:len(f)]


def s_exit_open():
    a = jingle([("bell", "g5:1 d6:1 g6:1 a6:1 d7:5", 1), ("harm", "g4:4 d5:5", .25)], 260, .45, .3)
    b = svf(noise(len(a) / SR, 110), 170, 1) * np.sin(np.linspace(0, np.pi, len(a)))
    return a + 1.2 * b


def s_stage_clear():
    return jingle([("hero", "g5:1 c6:1 e6:1 g6:1 e6:2 g6:2 a6:1 b6:1 c7:6", 1),
                   ("harm", "e5:2 g5:2 c6:4 c6:2 e6:8", .45),
                   ("bass", "c3:2 g3:2 c3:2 g3:2 f3:2 c3:6", .8),
                   ("arp", ".:10 c6:1 e6:1 g6:1 c7:1 e7:1 g7:3", .3)], 170, .55)


def s_gameover():
    return jingle([("brass", "e5:2 d5:2 c5:2 b4:2 a4:3 g#4:3 a4:8", 1),
                   ("harm", "c5:4 a4:4 e4:6 c4:8", .4),
                   ("bass", "a2:4 f2:4 e2:6 a2:8", .4)], 118, .6)


def s_teleport():
    n = n_of(.62)
    t = np.arange(n) / SR
    f = sweep(190, 2400, .55, .62) * (1 + .22 * np.sin(TAU * 37 * t))
    e = np.sin(np.linspace(0, np.pi, n)) ** 1.2
    x = osc("p50", f) * e + .6 * svf(noise(.62, 120), sweep(500, 6500, .55, .62), 3, "bp") * e
    return mixat(.8, (0, x, 1), (.09, x, .35), (.18, x, .15))


def s_wind():
    sec = 1.9
    n = n_of(sec)
    t = np.arange(n) / SR
    gust = .5 + .5 * np.sin(TAU * .9 * t - 1.2) * np.sin(TAU * .37 * t + .4)
    e = np.sin(np.linspace(0, np.pi, n)) ** 1.5
    a = svf(noise(sec, 130), 320 + 900 * gust, 1.3, "bp")
    b = svf(noise(sec, 131), 900 + 1500 * gust, 9, "bp")
    return (a + .5 * b) * e


def s_car():
    t = tt(.3)
    horn = (osc("saw", np.full(len(t), 392.0), 24) + osc("saw", np.full(len(t), 494.0), 24)) * np.clip(t / .008, 0, 1) \
        * np.clip((.3 - t) / .02, 0, 1)
    sec = .85
    n = n_of(sec)
    u = np.linspace(0, 1, n)
    pass_ = np.exp(-((u - .42) / .2) ** 2)
    wh = svf(noise(sec, 140), 500 + 2400 * pass_ * np.where(u < .42, 1, .6), 1.2, "bp") * pass_
    return mixat(.98, (0, svf(horn, 2600, .8), .55), (.13, wh, 1.8))


def s_hurry():
    y = []
    for i in range(6):
        f = 1175 if i % 2 == 0 else 880
        n = n_of(.11)
        b = osc("p50", np.full(n, float(f))) * np.clip(np.arange(n) / 90, 0, 1) * np.clip((n - np.arange(n)) / 200, 0, 1)
        y += [b, np.zeros(n_of(.1 if i % 2 else .03))]
    return np.concatenate(y)


def s_switch():
    a = metal([1500, 2300, 3600], [.006, .005, .003], .03)
    b = metal([900, 1700, 2700], [.012, .008, .005], .08)
    return mixat(.16, (0, a, 1), (.055, b, 1.2), (.055, thud(300, 150, .015, .06), .7))


def s_jet():
    sec = .85
    n = n_of(sec)
    t = np.arange(n) / SR
    e = np.clip(t / .05, 0, 1) * np.clip((sec - t) / .28, 0, 1)
    fl = 1 + .3 * np.sin(TAU * 17 * t) + .15 * np.sin(TAU * 41 * t)
    a = svf(noise(sec, 150), 1300 * fl, .9)
    b = svf(noise(sec, 151), 3400, 1.2, "bp") * .3
    return (a + b) * e * fl + .3 * np.sin(TAU * np.cumsum(sweep(120, 80, .6, sec)) / SR) * e


def s_crumble():
    t = tt(1.05)
    rum = svf(noise(1.05, 160), 140, 1) * np.exp(-t / .35)
    return mixat(1.05, (0, rum, 2.2), (0, grains(1.05, 34, 161, 350, 1900, .022, 3, 1.6), 1.4),
                 (0, thud(150, 60, .08, .3), .8))


def s_secret():
    return jingle([("bell", "c6:1 e6:1 g#6:1 b6:1 d7:1 f#7:5", 1), ("harm", "c5:3 d5:3 e5:5", .25)], 200, .7, .35)


def s_boss_hit():
    m = metal([310, 523, 890, 1480, 2330], [.2, .16, .12, .08, .05], .5)
    return mixat(.5, (0, m, 2.2), (0, thud(210, 62, .07, .3), 1),
                 (0, svf(noise(.1, 170), 3500, 1, "hp") * decay(.012, .1), .6))


def s_boss_roar():
    sec = 1.4
    n = n_of(sec)
    t = np.arange(n) / SR
    u = t / sec
    f = 66 * (1 + .1 * np.sin(TAU * 7 * t) + .22 * np.sin(np.pi * u))
    fc = 260 + 800 * np.sin(np.pi * np.clip(u * 1.3, 0, 1)) ** 2
    saw = osc("saw", f, 60) * (.7 + .3 * np.sign(np.sin(TAU * 29 * t)))
    x = svf(saw, fc, 3, "bp") + .9 * svf(saw, 150, 1) + .35 * svf(noise(sec, 180), fc * 1.5, 2, "bp")
    return x * np.clip(t / .1, 0, 1) * np.clip((sec - t) / .5, 0, 1) ** 1.3


def s_boss_die():
    y = mixat(3.3, (0, explosion(200, .8, 170, 50, 4500, .7), .6), (.28, explosion(210, .8, 190, 55, 4200, .7), .6),
              (.52, explosion(220, .8, 150, 48, 4000, .7), .7), (.82, explosion(230, .9, 140, 45, 4000, .8), .75),
              (1.12, explosion(240, .9, 130, 42, 3500, .9), .8), (1.5, explosion(250, 1.8, 110, 30, 5200, 1.7), 1.25))
    t = tt(1.6)
    fall = osc("saw", sweep(520, 46, 1.5, 1.6), 30) * np.clip((1.6 - t) / .3, 0, 1)
    return y + .3 * mixat(3.3, (0, fall, 1))


def s_boss_warn():
    sec = 1.7
    n = n_of(sec)
    t = np.arange(n) / SR
    ph = (t * 1.25) % 1
    f = 210 * 2 ** (np.where(ph < .7, ph / .7, (1 - ph) / .3))
    x = osc("p50", f, 16) + .7 * np.sin(TAU * np.cumsum(f / 2) / SR)
    return x * np.clip(t / .02, 0, 1) * np.clip((sec - t) / .12, 0, 1)


def s_select():
    n = n_of(.06)
    return osc("p50", sweep(880, 1320, .02, .06)) * np.clip((n - np.arange(n)) / n, 0, 1) ** 2


def s_confirm():
    return jingle([("arp", "e6:1 b6:3", 1), ("bell", "e6:1 b6:3", .6)], 330, .1, .15)


def s_battle_win():
    return jingle([("hero", "c6:1 c6:1 g5:1 c6:1 e6:2 g6:2 e6:1 g6:1 c7:6", 1),
                   ("harm", "g5:4 c6:4 g6:8", .4), ("bass", "c3:2 c4:2 g3:2 g2:2 c3:8", .8)], 176, .5)


def s_battle_lose():
    return jingle([("brass", "g5:3 f#5:3 f5:3 e5:7", 1), ("bass", "c3:3 b2:3 a#2:3 a2:7", .35)], 150, .5)


def s_shrink():
    t = tt(.65)
    grind = svf(noise(.65, 190), 520, 1.6, "bp") * (.6 + .4 * np.sin(TAU * 23 * t)) * np.exp(-t / .24)
    return mixat(.65, (0, grind, 1.5), (0, thud(170, 46, .11, .42), 1.2), (.01, grains(.5, 12, 191, 600, 2200, .02), 2.6))


def s_countdown():
    n = n_of(.17)
    i = np.arange(n)
    return osc("p50", np.full(n, 880.0), 9) * np.clip(i / 60, 0, 1) * np.clip((n - i) / 1800, 0, 1)


# Target loudness (see measure_loud). Three deliberate tiers: soft / normal / heavy.
SOFT, NORMAL, HEAVY = -21.0, -15.0, -12.0
SFX = dict(place=(s_place, NORMAL), fuse=(s_fuse, SOFT), boom=(s_boom, HEAVY), boom2=(s_boom2, HEAVY),
           brick=(s_brick, NORMAL), kick=(s_kick, NORMAL), power=(s_power, NORMAL), power_bad=(s_power_bad, NORMAL),
           hurt=(s_hurt, NORMAL), die=(s_die, NORMAL), enemy_die=(s_enemy_die, NORMAL), exit_open=(s_exit_open, NORMAL),
           stage_clear=(s_stage_clear, NORMAL), gameover=(s_gameover, NORMAL), teleport=(s_teleport, NORMAL),
           wind=(s_wind, SOFT), car=(s_car, NORMAL), hurry=(s_hurry, NORMAL), switch=(s_switch, SOFT),
           jet=(s_jet, NORMAL), crumble=(s_crumble, NORMAL), secret=(s_secret, NORMAL), boss_hit=(s_boss_hit, NORMAL),
           boss_roar=(s_boss_roar, HEAVY), boss_die=(s_boss_die, HEAVY), boss_warn=(s_boss_warn, NORMAL),
           select=(s_select, SOFT), confirm=(s_confirm, SOFT), battle_win=(s_battle_win, NORMAL),
           battle_lose=(s_battle_lose, NORMAL), shrink=(s_shrink, NORMAL), countdown=(s_countdown, NORMAL))


def k_weight(x):
    """Approximate BS.1770 K-weighting: cut below ~40 Hz, +4 dB above ~1.5 kHz."""
    pad = max(len(x), 8192)
    sp = np.fft.rfft(x, pad)
    f = np.fft.rfftfreq(pad, 1 / SR)
    hp = 1 / np.sqrt(1 + (40 / np.maximum(f, .1)) ** 4)
    shelf = 1 + (db(4) - 1) / (1 + (1500 / np.maximum(f, .1)) ** 2)
    return np.fft.irfft(sp * hp * shelf, pad)[:len(x)]


def measure_loud(x, win=.1):
    """Loudest 100 ms of the K-weighted signal, in dB. A sound shorter than that counts its real length."""
    w = min(n_of(win), len(x))
    e = np.concatenate([[0], np.cumsum(k_weight(x) ** 2)])
    return 10 * math.log10(max(float(np.max(e[w:] - e[:-w])) / w, 1e-12))


def finish_sfx(x, target):
    """Trim, fade, then reach target loudness under the peak ceiling (soft-saturating if it must)."""
    x = np.asarray(x, float)
    x = x - x.mean()
    x = x / np.abs(x).max()
    idx = np.nonzero(np.abs(x) > db(-55))[0]
    x = x[idx[0]:idx[-1] + 1]
    fade = min(n_of(.012), len(x) // 4)
    x[-fade:] *= np.linspace(1, 0, fade)
    x[:24] *= np.linspace(0, 1, 24)
    ceil = db(PEAK_DB)
    g = 1.0
    y = x * ceil
    for _ in range(40):
        y = x * g
        pk = np.abs(y).max()
        if pk > ceil:  # saturate only the part above the knee
            y = np.tanh(y / ceil) * ceil
        err = target - measure_loud(y)
        if abs(err) < .1:
            break
        g *= db(err)
        g = min(g, db(9) * ceil)  # never drive past 9 dB over the ceiling
    y = y * min(1.0, ceil / np.abs(y).max())
    return y


# ---------------------------------------------------------------- verification
EXPECTED_SFX = list(SFX)
EXPECTED_MUSIC = list(TRACKS)


def longest_quiet(x, thr_db=-50, hop=.01):
    h = n_of(hop)
    m = len(x) // h
    r = np.sqrt((x[:m * h].reshape(m, h) ** 2).mean(1))
    q = r < db(thr_db)
    best = run = 0
    for v in q.tolist():
        run = run + 1 if v else 0
        best = max(best, run)
    return best * hop


def seam_stats(x, bars):
    """Compare the join end->start with every other bar line (the join is a bar line too)."""
    d = np.abs(np.diff(x))
    jump = abs(x[0] - x[-1])
    w = 64
    joined = np.concatenate([x[-w:], x[:w]])
    hf = np.abs(np.diff(joined, 2)).max()
    pts = [round(i * len(x) / bars) for i in range(1, bars)]
    others = [np.abs(np.diff(x[p - w:p + w], 2)).max() for p in pts]
    return jump, float(np.percentile(d, 99.9)), hf, float(max(others))


def bands(x):
    sp = np.abs(np.fft.rfft(x)) ** 2
    f = np.fft.rfftfreq(len(x), 1 / SR)
    tot = sp.sum()
    return [100 * sp[(f >= a) & (f < b)].sum() / tot for a, b in ((0, 150), (150, 1000), (1000, 5000), (5000, 22050))]


def verify():
    bad = []
    files = sorted(os.listdir(OUT))
    wavs = [f for f in files if f.endswith(".wav")]
    names = {f[:-4] for f in wavs}
    want = set(EXPECTED_SFX) | set(EXPECTED_MUSIC)
    if want - names:
        bad.append(f"missing: {sorted(want - names)}")
    if names - want:
        bad.append(f"unexpected: {sorted(names - want)}")
    total = sum(os.path.getsize(os.path.join(OUT, f)) for f in wavs)
    print(f"{len(wavs)} wav files, {total / 1e6:.1f} MB\n")
    print("MUSIC  name         sec   peak   rms  loud  crest clip quiet |  seam jump/p99.9  seam hf/max bar line | <150 <1k <5k >5k %  rms per quarter")
    for n in EXPECTED_MUSIC:
        x, info = read_wav(os.path.join(OUT, n + ".wav"))
        if info != (SR, 1, 2):
            bad.append(f"{n}: format {info}")
        pk = 20 * math.log10(np.abs(x).max())
        rms = 20 * math.log10(np.sqrt((x ** 2).mean()))
        q = np.array_split(x, 4)
        qr = " ".join(f"{20 * math.log10(np.sqrt((c ** 2).mean())):.1f}" for c in q)
        clip = int((np.abs(x) >= .999).sum())
        quiet = longest_quiet(x)
        jump, p999, hf, hfo = seam_stats(x, sum(len(s["chords"]) for s in TRACKS[n]()["sections"]))
        loop = n != "m_ending"
        dur = len(x) / SR
        print(f"  {n:11s} {dur:5.1f} {pk:6.2f} {rms:6.1f} {measure_loud(x, 3):5.1f} {pk - rms:5.1f} {clip:4d} {quiet:5.2f} | "
              f"{jump:.4f}/{p999:.4f} {hf:.4f}/{hfo:.4f} | " + " ".join(f"{b:3.0f}" for b in bands(x)) + "  " + qr)
        lo, hi = (45, 70) if loop else (20, 30)
        if not lo <= dur <= hi:
            bad.append(f"{n}: duration {dur:.1f}")
        if clip or abs(pk - PEAK_DB) > .2:
            bad.append(f"{n}: peak {pk:.2f} clip {clip}")
        if loop and (quiet > 0.05 or jump > p999 or hf > hfo * 1.05):
            bad.append(f"{n}: seam or silence (quiet {quiet:.2f}, jump {jump:.4f}/{p999:.4f}, hf {hf:.4f}/{hfo:.4f})")
        if not loop and quiet > 1.0:
            bad.append(f"{n}: silence {quiet:.2f}")
    print("\nEFFECT name         sec   peak   rms  loud target  start  end quiet | <150 <1k <5k >5k %")
    for n in EXPECTED_SFX:
        x, info = read_wav(os.path.join(OUT, n + ".wav"))
        if info != (SR, 1, 2):
            bad.append(f"{n}: format {info}")
        pk = 20 * math.log10(np.abs(x).max())
        rms = 20 * math.log10(np.sqrt((x ** 2).mean()))
        ld = measure_loud(x)
        tg = SFX[n][1]
        quiet = longest_quiet(x)
        print(f"  {n:12s} {len(x) / SR:5.2f} {pk:6.2f} {rms:6.1f} {ld:5.1f} {tg:6.1f} {abs(x[0]):.3f} {abs(x[-1]):.3f} {quiet:5.2f} | " + " ".join(f"{b:3.0f}" for b in bands(x)))
        if abs(ld - tg) > 1.0:
            bad.append(f"{n}: loudness {ld:.1f} vs {tg}")
        if pk > PEAK_DB + .1 or np.abs(x).max() >= .999:
            bad.append(f"{n}: peak {pk:.2f}")
        if abs(x[-1]) > .002:
            bad.append(f"{n}: ends with a step {abs(x[-1]):.4f}")
        if quiet > .3:
            bad.append(f"{n}: silence {quiet:.2f}")
    print("\nRESULT:", "all checks passed" if not bad else "PROBLEMS")
    for b in bad:
        print("  -", b)
    if total > 60e6:
        print("  - total over 60 MB")
    return not bad


def main(args):
    args = args or ["all"]
    music = list(TRACKS) if args[0] in ("all", "music") else [a for a in args if a in TRACKS]
    sfx = list(SFX) if args[0] in ("all", "sfx") else [a for a in args if a in SFX]
    for n in music:
        write_wav(n, render_track(TRACKS[n]()))
    for n in sfx:
        fn, target = SFX[n]
        write_wav(n, finish_sfx(fn(), target))
    if args[0] in ("all", "verify"):
        sys.exit(0 if verify() else 1)


if __name__ == "__main__":
    main(sys.argv[1:])
