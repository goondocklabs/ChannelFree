"""Original smooth synth track for the guide channel (loops seamlessly).
Electric piano comping, soft pad, bass, lead melody, light drums. 92 BPM, 32 bars."""
import numpy as np
from scipy.signal import butter, lfilter
import wave, os

SR = 44100
BPM = 92
BEAT = 60 / BPM
BAR = 4 * BEAT
rng = np.random.default_rng(7)

NOTE = {n: i for i, n in enumerate(["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"])}
def hz(midi): return 440.0 * 2 ** ((midi - 69) / 12)

# chord: root name, intervals (semitones above root)
MAJ9 = [0, 4, 7, 11, 14]; MIN9 = [0, 3, 7, 10, 14]; MIN7 = [0, 3, 7, 10]; MAJ7 = [0, 4, 7, 11]
DOM9 = [0, 4, 7, 10, 14]; SUS9 = [0, 5, 7, 10, 14]
A = [("F", MAJ9), ("E", MIN7), ("D", MIN9), ("C", MAJ7), ("Bb", MAJ7), ("A", MIN7), ("G", MIN9), ("C", SUS9)]
B = [("Db", MAJ7), ("C", MIN7), ("Bb", MIN9), ("Eb", DOM9), ("Ab", MAJ7), ("G", MIN7), ("G", MIN9), ("C", DOM9)]
SONG = A + A + B + A
N_BARS = len(SONG)
TOTAL = N_BARS * BAR
L = int(TOTAL * SR)
mix = np.zeros((L, 2))

def add(sig, start, pan=0.0, gain=1.0):
    i = int(start * SR) % L
    n = len(sig)
    left = np.sqrt(0.5 * (1 - pan)) * gain
    right = np.sqrt(0.5 * (1 + pan)) * gain
    end = i + n
    if end <= L:
        mix[i:end, 0] += sig * left; mix[i:end, 1] += sig * right
    else:  # wrap around so the loop is seamless
        k = L - i
        mix[i:, 0] += sig[:k] * left; mix[i:, 1] += sig[:k] * right
        mix[: n - k, 0] += sig[k:] * left; mix[: n - k, 1] += sig[k:] * right

def t_(dur): return np.arange(int(dur * SR)) / SR

def env(n, a=0.005, d=1.2, rel=0.08, dur=None):
    t = np.arange(n) / SR
    e = np.minimum(t / a, 1) * np.exp(-t / d)
    if dur:
        tail = t > dur
        e[tail] *= np.exp(-(t[tail] - dur) / rel)
    return e

def lowpass(x, f, order=2):
    b, a = butter(order, f / (SR / 2), "low"); return lfilter(b, a, x)

def highpass(x, f, order=2):
    b, a = butter(order, f / (SR / 2), "high"); return lfilter(b, a, x)

def bandpass(x, lo, hi):
    b, a = butter(2, [lo / (SR / 2), hi / (SR / 2)], "band"); return lfilter(b, a, x)

def epiano(f, dur):
    t = t_(dur + 1.5)
    mod_env = np.exp(-t / 0.25)
    mod = 1.8 * mod_env * np.sin(2 * np.pi * f * 1.0 * t)
    x = np.sin(2 * np.pi * f * t + mod) + 0.25 * np.sin(2 * np.pi * f * 2 * t) * np.exp(-t / 0.4)
    trem = 1 + 0.12 * np.sin(2 * np.pi * 4.5 * t)
    return x * env(len(t), 0.004, 1.6, 0.25, dur) * trem

def pad(f, dur):
    t = t_(dur + 0.6)
    x = sum(np.sin(2 * np.pi * f * (1 + c) * t + p) for c, p in ((0, 0), (0.0035, 1.1), (-0.0032, 2.3)))
    x += 0.3 * sum(np.sin(2 * np.pi * 2 * f * (1 + c) * t) for c in (0.002, -0.002))
    e = np.minimum(t / 0.6, 1) * np.where(t > dur, np.exp(-(t - dur) / 0.4), 1)
    return lowpass(x, 2200) * e

def bass(f, dur):
    t = t_(dur + 0.1)
    x = np.sin(2 * np.pi * f * t) + 0.35 * np.sin(2 * np.pi * 2 * f * t) + 0.1 * np.sin(2 * np.pi * 3 * f * t)
    return x * env(len(t), 0.006, 0.9, 0.05, dur)

def lead(f, dur):
    t = t_(dur + 0.4)
    vib = 1 + 0.006 * np.sin(2 * np.pi * 5.2 * t) * np.minimum(t / 0.35, 1)
    ph = 2 * np.pi * f * np.cumsum(vib) / SR
    x = sum(np.sin(k * ph) / k ** 1.2 for k in range(1, 9))  # soft saw (synth sax)
    e = np.minimum(t / 0.03, 1) * np.where(t > dur, np.exp(-(t - dur) / 0.12), 1) * (0.85 + 0.15 * np.exp(-t / 0.3))
    return lowpass(x, 2600) * e

def kick():
    t = t_(0.35); f = 50 + 70 * np.exp(-t / 0.03)
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t / 0.12)

def snare():
    t = t_(0.25)
    return bandpass(rng.standard_normal(len(t)), 900, 5000) * np.exp(-t / 0.06) + 0.3 * np.sin(2 * np.pi * 190 * t) * np.exp(-t / 0.05)

def hat(open_=False):
    t = t_(0.2 if open_ else 0.06)
    return highpass(rng.standard_normal(len(t)), 7000) * np.exp(-t / (0.08 if open_ else 0.018))

SWING = 0.58  # swung eighths
def pos(bar, beat_float):
    whole = int(beat_float); frac = beat_float - whole
    frac = SWING if abs(frac - 0.5) < 1e-6 else frac
    return bar * BAR + (whole + frac) * BEAT

melody_rhythms = [
    [(0, 1.5, 3), (1.5, 1, 2), (2.5, 1.5, 1)],
    [(0.5, 1, 2), (1.5, 0.5, 3), (2, 2, 4)],
    [(0, 2, 2), (2.5, 0.5, 3), (3, 1, 1)],
    [(0, 3, 4)],
]
pent = None
for bar, (root, ivs) in enumerate(SONG):
    r = NOTE[root]
    root_mid = 48 + r if r < 6 else 36 + r  # bass register
    chord_mid = 60 + r - (12 if r > 5 else 0)
    voicing = [chord_mid + iv for iv in ivs[1:]]  # rootless voicing, Rhodes style
    # pad: whole bar
    for m in voicing[:4]:
        add(pad(hz(m), BAR), bar * BAR, pan=0.0, gain=0.035)
    # electric piano comp: beat 1 and the and of 2 (and a little push on 4 every other bar)
    hits = [(0, 1.2), (1.5, 1.8)] + ([(3.5, 0.4)] if bar % 2 else [])
    for b_, d in hits:
        for j, m in enumerate(voicing):
            add(epiano(hz(m), d * BEAT), pos(bar, b_) + j * 0.006, pan=-0.35 + 0.17 * j, gain=0.07)
    # bass: root on 1, fifth on 3, approach note on the and of 4
    nxt = NOTE[SONG[(bar + 1) % N_BARS][0]]
    nxt_mid = 48 + nxt if nxt < 6 else 36 + nxt
    add(bass(hz(root_mid), 1.6 * BEAT), pos(bar, 0), gain=0.32)
    add(bass(hz(root_mid + 7), 1.3 * BEAT), pos(bar, 2), gain=0.28)
    add(bass(hz(nxt_mid - 1 if nxt_mid - 1 != root_mid else nxt_mid + 1), 0.4 * BEAT), pos(bar, 3.5), gain=0.22)
    # drums
    add(kick(), pos(bar, 0), gain=0.55); add(kick(), pos(bar, 2.5), gain=0.35)
    add(snare(), pos(bar, 1), gain=0.16, pan=0.1); add(snare(), pos(bar, 3), gain=0.16, pan=0.1)
    for e8 in range(8):
        add(hat(open_=(e8 == 7 and bar % 4 == 3)), pos(bar, e8 * 0.5), gain=0.05 if e8 % 2 else 0.07, pan=0.4)
    # lead melody: chord tones, a phrase every bar except breaths at the end of each 8 bars
    if bar % 8 != 7:
        tones = [chord_mid + 12 + iv for iv in ivs]
        for start, dur, idx in melody_rhythms[bar % 4]:
            m = tones[min(idx, len(tones) - 1)]
            while m > 86: m -= 12
            add(lead(hz(m), dur * BEAT * 0.92), pos(bar, start), pan=0.2, gain=0.085)

# room: a few circular echoes (np.roll wraps, so the loop point stays seamless)
wet = np.zeros_like(mix)
for ms, g, side in ((73, 0.22, 0), (131, 0.15, 1), (197, 0.10, 0), (263, 0.07, 1)):
    sh = int(ms / 1000 * SR)
    wet[:, side] += g * np.roll(mix[:, 0], sh)
    wet[:, 1 - side] += g * 0.8 * np.roll(mix[:, 1], sh)
mix = mix + lowpass(wet.T, 3500).T if False else mix + wet
# gentle bus: soft clip + normalize to a calm level
mix = np.tanh(mix * 1.4)
mix /= np.max(np.abs(mix)) * 1.12
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "guide-synth.wav")
with wave.open(out, "wb") as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes((mix * 32767).astype("<i2").tobytes())
print(out, round(TOTAL, 1), "seconds")
