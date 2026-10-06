"""Measures which test tones an emulator audio capture contains, step by step.

The capture is the WAV the Android emulator writes with QEMU_AUDIO_DRV=wav (the guest's mixed audio output). The step
log has one line per test step: "HH:MM:SS.fff STEP <label> :: <commands>". The capture is aligned to wall-clock time by
its end, which is when the emulator was stopped (--end, the time of the last line by default).

For every step the script prints the level of each test tone (dBFS of a narrow band around it, median over the step,
skipping the first second so the stream and buffer have settled) and the broadband level.

    python tools/test/analyze_capture.py capture.wav steps.txt [--end HH:MM:SS.fff] [--offset seconds] [--beeps label]
"""
import argparse
import datetime as dt
import re
import sys
import wave

import numpy as np

TONES = [700, 800, 1000, 1200, 1500, 2000, 2500, 3000]
WINDOW = 0.25  # seconds per analysis frame


def parse_time(text):
    t = dt.datetime.strptime(text, '%H:%M:%S.%f')
    return t.hour * 3600 + t.minute * 60 + t.second + t.microsecond / 1e6


def load(path):
    with wave.open(path, 'rb') as w:
        rate, channels, width = w.getframerate(), w.getnchannels(), w.getsampwidth()
        raw = w.readframes(w.getnframes())
    if width != 2:
        sys.exit('expected 16-bit PCM')
    x = np.frombuffer(raw, dtype='<i2').astype(np.float32) / 32768
    return x.reshape(-1, channels).mean(axis=1), rate


def frame_levels(x, rate):
    """Per WINDOW frame: band level per tone and broadband level, in dBFS."""
    n = int(WINDOW * rate)
    frames = len(x) // n
    seg = x[: frames * n].reshape(frames, n) * np.hanning(n)
    spec = np.abs(np.fft.rfft(seg, axis=1)) * 2 / np.sum(np.hanning(n))
    freqs = np.fft.rfftfreq(n, 1 / rate)
    bands = {}
    for f in TONES:
        sel = (freqs > f - 15) & (freqs < f + 15)
        bands[f] = 20 * np.log10(np.sqrt(np.sum(spec[:, sel] ** 2, axis=1) / 2) + 1e-9)
    broad = 20 * np.log10(np.sqrt(np.mean(seg ** 2, axis=1)) * np.sqrt(8 / 3) + 1e-9)
    return bands, broad


def beep_period(x, rate, start, stop, freq=800):
    """Mean interval between beep onsets (s) in [start, stop): measures the playback speed."""
    a, b = int(start * rate), int(stop * rate)
    seg = x[a:b]
    t = np.arange(len(seg)) / rate
    env = np.abs(seg * np.exp(-2j * np.pi * freq * t))
    k = int(0.005 * rate)
    env = np.convolve(env, np.ones(k) / k, mode='same')
    on = env > 0.5 * np.percentile(env, 99)
    edges = np.flatnonzero(on[1:] & ~on[:-1]) / rate
    edges = edges[np.insert(np.diff(edges) > 0.5, 0, True)]
    return (np.diff(edges).mean() if len(edges) > 2 else float('nan')), len(edges)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('capture')
    ap.add_argument('steps')
    ap.add_argument('--end', help='wall-clock time the capture ended (default: last line of the step log)')
    ap.add_argument('--offset', type=float, default=0.0, help='seconds added to the alignment')
    ap.add_argument('--beeps', action='append', default=[], help='step label containing the 2 s beep track')
    args = ap.parse_args()

    x, rate = load(args.capture)
    duration = len(x) / rate
    steps = []
    for line in open(args.steps, encoding='utf-8'):
        m = re.match(r'(\d\d:\d\d:\d\d\.\d+) STEP (\S+)', line)
        if m:
            steps.append((parse_time(m.group(1)), m.group(2), line.split('::', 1)[-1].strip()))
    end = parse_time(args.end) if args.end else steps[-1][0]
    start_wall = end - duration + args.offset
    print(f'capture {duration:.1f} s at {rate} Hz, starts {dt.timedelta(seconds=round(start_wall, 1))} wall clock')

    bands, broad = frame_levels(x, rate)
    print(f"{'step':24s} {'dur':>5s} " + ' '.join(f'{f:>6d}' for f in TONES) + '   broad')
    for i, (t0, label, _) in enumerate(steps):
        t1 = steps[i + 1][0] if i + 1 < len(steps) else end
        a, b = t0 - start_wall + 1.0, t1 - start_wall - 0.2
        if b - a < WINDOW or a < 0 or b > duration:
            continue
        fa, fb = int(a / WINDOW), int(b / WINDOW)
        row = ' '.join(f'{np.median(bands[f][fa:fb]):6.1f}' for f in TONES)
        print(f'{label:24s} {t1 - t0:5.1f} {row}  {np.median(broad[fa:fb]):6.1f}')
        if label in args.beeps:
            period, count = beep_period(x, rate, a, b)
            print(f'{"":24s} beep onsets {count}, mean period {period:.4f} s (file: 2.0000 s)')


if __name__ == '__main__':
    main()
