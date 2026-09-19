#!/usr/bin/env python3
"""
Generates every sound in the mod's audio manifest (PLAN.md §12.4).

All of it is synthesised from first principles here -- oscillators, filtered
noise and envelopes -- so the mod ships no recording from any source and there
is no question about where any of it came from.

Re-run after changing anything:  python3 scripts/generate_sounds.py

The synthesis itself is seeded and reproducible, but the .ogg bytes are not:
every Ogg stream carries a randomly chosen serial number in its page headers,
so two encodes of identical audio differ. Compare by listening, not by hash —
which is why CI checks the textures for drift and not these.
"""
import os
import numpy as np
import soundfile as sf

SR = 44100
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "assets", "shinimodori", "sounds")
rng = np.random.default_rng(20251215)


# ----------------------------------------------------------------- primitives

def t(dur):
    return np.linspace(0, dur, int(SR * dur), endpoint=False)


def sine(freq, dur, phase=0.0):
    return np.sin(2 * np.pi * freq * t(dur) + phase)


def sweep(f0, f1, dur, log=True):
    x = t(dur)
    if log and f0 > 0 and f1 > 0:
        f = f0 * (f1 / f0) ** (x / dur)
        phase = 2 * np.pi * dur * (f - f0) / np.log(f1 / f0) if f1 != f0 else 2 * np.pi * f0 * x
    else:
        f = f0 + (f1 - f0) * x / dur
        phase = 2 * np.pi * (f0 * x + 0.5 * (f1 - f0) * x ** 2 / dur)
    return np.sin(phase)


def noise(dur):
    return rng.standard_normal(int(SR * dur))


def _exact(x, n):
    """Envelopes must match their signal sample-for-sample."""
    if len(x) >= n:
        return x[:n]
    return np.concatenate([x, np.full(n - len(x), x[-1] if len(x) else 0.0)])


def env_ad(dur, attack, decay, curve=2.0):
    n = max(1, int(SR * dur))
    a = min(max(1, int(SR * attack)), n)
    d = max(1, n - a)
    return _exact(np.concatenate([
        np.linspace(0, 1, a) ** (1 / curve),
        np.linspace(1, 0, d) ** curve,
    ]), n)


def env_asr(dur, attack, release):
    n = max(1, int(SR * dur))
    a = min(max(1, int(SR * attack)), n)
    r = min(max(1, int(SR * release)), max(1, n - a))
    s = max(0, n - a - r)
    parts = [np.linspace(0, 1, a)]
    if s > 0:
        parts.append(np.ones(s))
    parts.append(np.linspace(1, 0, r))
    return _exact(np.concatenate(parts), n)


def onepole_lp(x, cutoff):
    """Simple one-pole lowpass; cutoff in Hz."""
    a = np.exp(-2 * np.pi * cutoff / SR)
    y = np.empty_like(x)
    acc = 0.0
    for i in range(len(x)):
        acc = (1 - a) * x[i] + a * acc
        y[i] = acc
    return y


def onepole_hp(x, cutoff):
    return x - onepole_lp(x, cutoff)


def bandpass(x, low, high):
    return onepole_hp(onepole_lp(x, high), low)


def reverb(x, decay=0.45, taps=14, spread=0.035):
    """Cheap multi-tap reverb; enough to put a sound in a room that isn't there."""
    y = x.copy()
    for i in range(1, taps):
        d = int(SR * spread * i * (0.8 + 0.4 * rng.random()))
        if d >= len(x):
            break
        g = decay ** i
        y[d:] += x[:-d] * g
    return y


def fit(x, peak=0.85):
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 1e-9 else x


def loopable(x, fade=0.25):
    """Crossfades the tail over the head so a looping bed has no seam."""
    n = int(SR * fade)
    if n * 2 >= len(x):
        return x
    head, tail = x[:n].copy(), x[-n:].copy()
    ramp = np.linspace(0, 1, n)
    x = x[:-n]
    x[:n] = head * ramp + tail * (1 - ramp)
    return x


def write(name, x, loop=False):
    x = np.asarray(x, dtype=np.float64)
    if loop:
        x = loopable(x)
    x = fit(x)
    # A short fade at both ends stops any click on trigger.
    n = min(len(x) // 8, int(SR * 0.006))
    if n > 1:
        x[:n] *= np.linspace(0, 1, n)
        x[-n:] *= np.linspace(1, 0, n)
    path = os.path.join(OUT, name + ".ogg")
    sf.write(path, x.astype(np.float32), SR, format="OGG", subtype="VORBIS")
    return path, len(x) / SR


# -------------------------------------------------------------------- voices

def whisper(dur, seed_words=7, pitch=1.0):
    """
    Almost-language: noise shaped by moving formants, gated into word-lengths.
    Reads as a voice without ever being one.
    """
    x = noise(dur)
    n = len(x)
    out = np.zeros(n)
    # Three formants wandering slowly, the way a mouth does.
    for base, amp in ((520, 1.0), (1180, 0.6), (2600, 0.3)):
        lfo = 1 + 0.22 * np.sin(2 * np.pi * (0.6 + 0.4 * rng.random()) * t(dur) + rng.random() * 6)
        f = base * pitch * lfo
        lo, hi = np.mean(f) * 0.7, np.mean(f) * 1.45
        out += bandpass(x, lo, hi) * amp
    # Gate it into syllables with gaps, so it breathes.
    gate = np.zeros(n)
    pos = 0
    while pos < n:
        wl = int(SR * (0.09 + 0.16 * rng.random()))
        gap = int(SR * (0.05 + 0.18 * rng.random()))
        seg = min(wl, n - pos)
        if seg > 8:
            gate[pos:pos + seg] = _exact(env_asr(seg / SR, 0.02, 0.05), seg) * (0.5 + 0.5 * rng.random())
        pos += wl + gap
    return out * gate


def heartbeat(dur, bpm, thump=1.0):
    """Two thumps per beat: a low pitched-down sine kick plus a body transient."""
    n = int(SR * dur)
    out = np.zeros(n)
    period = 60.0 / bpm
    beat = 0.0
    while beat < dur:
        for offset, gain, f0, f1 in ((0.0, 1.0, 92, 38), (0.30 * period, 0.62, 78, 34)):
            start = int(SR * (beat + offset))
            d = 0.30
            seg = int(SR * d)
            if start + seg > n:
                seg = n - start
            if seg <= 16:
                continue
            body = _exact(sweep(f0, f1, seg / SR), seg) * _exact(env_ad(seg / SR, 0.004, d, 3.2), seg)
            knock = _exact(onepole_lp(noise(seg / SR), 260), seg) * _exact(env_ad(seg / SR, 0.001, 0.05, 4), seg) * 0.35
            out[start:start + seg] += (body + knock) * gain * thump
        beat += period
    return out


# -------------------------------------------------------------------- sounds

def build():
    made = []

    # --- The Void ---------------------------------------------------------
    # A reversed whisper bed: synthesised forward, then reversed, so the
    # consonants land backwards and it never quite becomes words.
    bed = whisper(8.0, pitch=0.78)
    bed = reverb(bed[::-1], decay=0.5, taps=18, spread=0.05) * 0.5
    bed += onepole_lp(noise(len(bed) / SR), 90) * 0.22          # sub-bass floor
    made.append(write("sm_void_whisper_bed", bed, loop=True))

    # "...I love you." -- three descending syllables, close and very quiet.
    love = np.zeros(int(SR * 2.4))
    for i, (start, dur, p) in enumerate(((0.15, 0.22, 1.05), (0.52, 0.30, 0.96), (0.98, 0.55, 0.88))):
        s = int(SR * start)
        w = whisper(dur, pitch=p)
        seg = w * _exact(env_asr(dur, 0.03, 0.12), len(w))
        love[s:s + len(seg)] += seg
    made.append(write("sm_whisper_love", reverb(love, decay=0.38) * 0.9))

    # --- Hearts -----------------------------------------------------------
    made.append(write("sm_heartbeat_slow", heartbeat(6.0, 46, 1.0), loop=True))
    made.append(write("sm_heartbeat_fast", heartbeat(6.0, 132, 0.95), loop=True))
    # The crush: slower and deeper, with the room closing around it.
    crush = heartbeat(6.0, 34, 1.2)
    made.append(write("sm_heartbeat_crush", reverb(crush, decay=0.3, taps=8) * 0.9, loop=True))

    # --- Dying / arrival ---------------------------------------------------
    # One 60Hz sub-drop as everything else cuts out.
    drop = sweep(150, 34, 1.4, log=True) * env_ad(1.4, 0.005, 1.4, 2.4)
    made.append(write("sm_dying_drop", drop))

    # A sharp intake of breath: rising bandpassed noise, no voicing.
    g = 0.85
    gasp = noise(g)
    f_lo = np.linspace(280, 900, len(gasp))
    gasp = bandpass(gasp, 250, 2400) * (f_lo / 900)
    gasp *= _exact(np.concatenate([np.linspace(0, 1, int(SR * 0.18)) ** 0.5,
                                   np.linspace(1, 0, len(gasp) - int(SR * 0.18)) ** 2.2]), len(gasp))
    made.append(write("sm_gasp", gasp * 0.9))

    # --- Rewind ------------------------------------------------------------
    sw = 1.6
    rew = sweep(80, 1800, sw, log=True) * 0.5
    nz = bandpass(noise(sw), 200, 6000)
    rew = _exact(rew, len(nz)) + nz * np.linspace(0.1, 0.9, len(nz)) * 0.5
    # Frame-tear stutter: gate it in accelerating slices.
    gate = np.ones(int(SR * sw))
    pos, step = 0, int(SR * 0.09)
    while pos < len(gate):
        gate[pos:pos + max(4, step // 6)] = 0.15
        pos += step
        step = max(int(SR * 0.012), int(step * 0.86))
    made.append(write("sm_rewind_sweep", (rew * gate)[::-1]))

    imp = sweep(220, 44, 0.9) * env_ad(0.9, 0.002, 0.9, 3.0)
    imp += onepole_lp(noise(0.9), 400) * env_ad(0.9, 0.001, 0.25, 4) * 0.4
    made.append(write("sm_rewind_impact", imp))

    # --- Anchors -----------------------------------------------------------
    # A single low bell: inharmonic partials, long decay, easy to miss.
    bell = np.zeros(int(SR * 2.8))
    for mult, amp, dec in ((1.0, 1.0, 2.8), (2.76, 0.45, 1.9), (5.40, 0.22, 1.2), (8.93, 0.11, 0.8)):
        seg = sine(196 * mult, 2.8) * env_ad(2.8, 0.002, dec, 2.6)
        bell += seg * amp
    made.append(write("sm_anchor_bell", reverb(bell, decay=0.25, taps=6) * 0.8))

    # --- The Taboo ---------------------------------------------------------
    # A sub-bass swell that arrives before you know it has.
    sw2 = 2.2
    swell = sweep(28, 64, sw2, log=True) * 0.9
    swell += sine(41, sw2) * 0.4
    swell *= _exact(np.linspace(0, 1, int(SR * sw2)) ** 1.6, len(swell))
    made.append(write("sm_taboo_swell", swell))

    # Many hands: layered dry rustles, each at its own speed.
    hands = np.zeros(int(SR * 3.0))
    for i in range(9):
        start = int(SR * (0.1 + 0.26 * i * rng.random()))
        d = 0.9 + 0.8 * rng.random()
        seg = bandpass(noise(d), 900, 7000)
        seg *= _exact(env_ad(d, 0.25, d, 1.6), len(seg)) * (0.25 + 0.5 * rng.random())
        seg *= _exact(1 + 0.5 * np.sin(2 * np.pi * (7 + 9 * rng.random()) * t(d)), len(seg))
        end = min(len(hands), start + len(seg))
        hands[start:end] += seg[:end - start]
    made.append(write("sm_taboo_hands", reverb(hands, decay=0.3, taps=8) * 0.75))

    # The crunch. One wet transient and a scatter of splintering after it.
    cr = 0.55
    base = onepole_lp(noise(cr), 700)
    crunch = base * _exact(env_ad(cr, 0.0015, 0.09, 5), len(base)) * 1.2
    hi = bandpass(noise(cr), 1200, 9000)
    crunch += _exact(hi, len(crunch)) * _exact(env_ad(cr, 0.001, 0.045, 6), len(crunch)) * 0.7
    for _ in range(11):
        s = int(SR * (0.01 + 0.30 * rng.random()))
        d = 0.02 + 0.05 * rng.random()
        sg = bandpass(noise(d), 1800, 11000)
        seg = sg * _exact(env_ad(d, 0.0008, d, 5), len(sg)) * (0.12 + 0.3 * rng.random())
        e = min(len(crunch), s + len(seg))
        crunch[s:e] += seg[:e - s]
    crunch += _exact(sweep(70, 30, cr), len(crunch)) * _exact(env_ad(cr, 0.002, 0.2, 3), len(crunch)) * 0.55
    made.append(write("sm_taboo_crunch", crunch))

    # --- Miasma / mabeasts --------------------------------------------------
    amb = onepole_lp(noise(9.0), 140) * 0.5
    amb += _exact(whisper(9.0, pitch=0.62), len(amb)) * 0.18
    amb += _exact(sine(47, 9.0), len(amb)) * 0.12
    made.append(write("sm_miasma_ambient_loop", reverb(amb, decay=0.4, taps=10), loop=True))

    # A wolf howl pitched a third down, with a reversed tail on the end.
    hd = 2.6
    x = t(hd)
    vib = 1 + 0.018 * np.sin(2 * np.pi * 5.2 * x)
    contour = _exact(np.concatenate([
        np.linspace(150, 330, int(SR * 0.45)),
        np.full(int(SR * 1.3), 330.0),
        np.linspace(330, 210, max(1, len(x) - int(SR * 0.45) - int(SR * 1.3))),
    ]), len(x)) * 0.70 * vib                                    # 30% down
    phase = 2 * np.pi * np.cumsum(contour) / SR
    howl = np.zeros(len(x))
    for h, a in ((1, 1.0), (2, 0.5), (3, 0.28), (4, 0.16), (5, 0.08)):
        howl += np.sin(phase * h) * a
    howl *= _exact(env_asr(hd, 0.25, 0.7), len(howl))
    howl += _exact(bandpass(noise(hd), 400, 3000), len(howl)) * 0.06 * _exact(env_asr(hd, 0.3, 0.8), len(howl))
    howl = reverb(howl, decay=0.42, taps=12, spread=0.06)
    howl = np.concatenate([howl, howl[::-1][:int(SR * 0.7)] * 0.45])   # reversed tail
    made.append(write("sm_mabeast_howl", howl))

    gd = 1.5
    growl = sine(58, gd) + 0.6 * sine(87, gd) + 0.35 * sine(131, gd)
    growl *= _exact(1 + 0.75 * np.sin(2 * np.pi * 27 * t(gd)), len(growl))   # the rasp
    growl += _exact(bandpass(noise(gd), 150, 1400), len(growl)) * 0.35
    growl *= _exact(env_asr(gd, 0.12, 0.35), len(growl))
    made.append(write("sm_mabeast_growl", growl * 0.8))

    # --- The tea party -----------------------------------------------------
    sd = 10.0
    string = np.zeros(int(SR * sd))
    for f, a in ((659.25, 1.0), (1318.5, 0.32), (1977.75, 0.12), (329.63, 0.22)):
        det = 1 + 0.0016 * np.sin(2 * np.pi * 0.23 * t(sd))
        string += np.sin(2 * np.pi * f * det * t(sd)) * a
    string *= _exact(env_asr(sd, 2.2, 2.2), len(string))
    string += _exact(bandpass(noise(sd), 2000, 9000), len(string)) * 0.02       # bow noise
    made.append(write("sm_teaparty_string", reverb(string, decay=0.35, taps=10) * 0.45, loop=True))

    pd = 1.8
    pn = bandpass(noise(pd), 900, 6500)
    pour = pn * _exact(env_asr(pd, 0.12, 0.5), len(pn)) * 0.35
    for _ in range(26):                                          # bubbles
        s = int(SR * (0.1 + 1.5 * rng.random()))
        d = 0.02 + 0.03 * rng.random()
        f = 700 + 1800 * rng.random()
        sg = sine(f, d)
        seg = sg * _exact(env_ad(d, 0.001, d, 3), len(sg)) * 0.22
        e = min(len(pour), s + len(seg))
        pour[s:e] += seg[:e - s]
    made.append(write("sm_teaparty_pour", pour))

    # Her laugh: three short voiced falls, amused rather than cruel.
    ld = 1.3
    laugh = np.zeros(int(SR * ld))
    for i, start in enumerate((0.05, 0.28, 0.52, 0.74)):
        d = 0.16
        f0 = 430 - i * 38
        seg = (sine(f0, d) + 0.5 * sine(f0 * 2, d) + 0.2 * sine(f0 * 3, d))
        seg *= _exact(env_ad(d, 0.012, d, 2.2), len(seg))
        seg *= _exact(1 + 0.3 * np.sin(2 * np.pi * 6 * t(d)), len(seg))
        s = int(SR * start)
        e = min(len(laugh), s + len(seg))
        laugh[s:e] += seg[:e - s] * (0.9 - i * 0.14)
    laugh = bandpass(laugh, 200, 4200)
    made.append(write("sm_echidna_laugh", reverb(laugh, decay=0.28, taps=6) * 0.7))

    # --- Despair ------------------------------------------------------------
    bd = 5.0
    breath = np.zeros(int(SR * bd))
    pos = 0.0
    while pos < bd - 1.2:
        for d, lo, hi, g in ((0.55, 300, 2200, 0.9), (0.65, 200, 1500, 0.6)):
            s = int(SR * pos)
            sg = bandpass(noise(d), lo, hi)
            seg = sg * _exact(env_asr(d, 0.2, 0.3), len(sg)) * g
            e = min(len(breath), s + len(seg))
            breath[s:e] += seg[:e - s]
            pos += d + 0.08
        pos += 0.5
    made.append(write("sm_despair_breath", breath * 0.55, loop=True))

    wd = 8.0
    whis = np.zeros(int(SR * wd))
    for i in range(5):
        layer = whisper(wd, pitch=0.7 + 0.25 * rng.random())
        whis += _exact(layer, len(whis)) * (0.35 - 0.05 * i)
    made.append(write("sm_despair_whispers", reverb(whis, decay=0.45, taps=14) * 0.5, loop=True))

    # --- The Authority -------------------------------------------------------
    ed = 1.6
    ext = sweep(40, 160, ed, log=True) * 0.6
    ext += _exact(bandpass(noise(ed), 300, 2600), len(ext)) * _exact(np.linspace(0, 1, int(SR * ed)) ** 2, len(ext)) * 0.4
    ext += _exact(whisper(ed, pitch=0.55), len(ext)) * 0.35      # muffled voices
    ext *= _exact(env_asr(ed, 0.3, 0.5), len(ext))
    made.append(write("sm_hands_extend", reverb(ext, decay=0.4, taps=12) * 0.85))

    return made


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    total = 0
    for path, dur in build():
        size = os.path.getsize(path)
        total += size
        print(f"  {os.path.basename(path):<34} {dur:5.2f}s  {size / 1024:7.1f} KB")
    print(f"\n{total / 1024:.1f} KB total in {OUT}")
