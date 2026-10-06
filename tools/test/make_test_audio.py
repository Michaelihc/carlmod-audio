"""Writes the test signals used for client checks into a folder (default: the test server's audio folder).

Each test uses its own tone frequency, so an audio capture shows which stream was audible when. Formats cover the
decoder paths: 16-bit PCM at 48 kHz, stereo 44.1 kHz (downmix and resampling), 24-bit, 32-bit float, 96 kHz
(downsampling) and Ogg Vorbis. Needs numpy and soundfile.

    python tools/test/make_test_audio.py [output folder]
"""
import os
import sys

import numpy as np
import soundfile as sf


def tone(freq, seconds, rate, channels=1, level=0.5):
    t = np.arange(int(seconds * rate)) / rate
    x = level * np.sin(2 * np.pi * freq * t)
    ramp = int(0.01 * rate)  # 10 ms fades, no clicks
    x[:ramp] *= np.linspace(0, 1, ramp)
    x[-ramp:] *= np.linspace(1, 0, ramp)
    return np.repeat(x[:, None], channels, axis=1) if channels > 1 else x


def beeps(freq, count, rate, on=0.5, period=2.0, level=0.5):
    """count beeps of `on` seconds every `period` seconds; the onsets measure the playback speed."""
    out = np.zeros(int(count * period * rate))
    b = tone(freq, on, rate, level=level)
    for i in range(count):
        s = int(i * period * rate)
        out[s:s + len(b)] = b
    return out


def main():
    default = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), '.runtime',
                           'server', 'AppData', 'SCP Secret Laboratory', 'LabAPI-Mobile', 'configs', 'global',
                           'CarlModAudio', 'audio')
    out = sys.argv[1] if len(sys.argv) > 1 else default
    os.makedirs(out, exist_ok=True)
    files = {
        't1000.wav': (tone(1000, 60, 48000), 48000, 'PCM_16'),
        't1500_44k_stereo.wav': (tone(1500, 60, 44100, channels=2), 44100, 'PCM_16'),
        't2000_24bit_32k.wav': (tone(2000, 30, 32000), 32000, 'PCM_24'),
        't2500_float_22k.wav': (tone(2500, 30, 22050), 22050, 'FLOAT'),
        't700_96k.wav': (tone(700, 30, 96000), 96000, 'PCM_16'),
        't3000.ogg': (tone(3000, 60, 44100, channels=2), 44100, 'VORBIS'),
        't1200_long.ogg': (tone(1200, 120, 48000), 48000, 'VORBIS'),
        'beeps800.ogg': (beeps(800, 30, 44100), 44100, 'VORBIS'),
    }
    for name, (data, rate, subtype) in files.items():
        path = os.path.join(out, name)
        channels = 1 if data.ndim == 1 else data.shape[1]
        # Written in blocks: libsndfile's Vorbis encoder overflows the stack on one large write.
        with sf.SoundFile(path, 'w', rate, channels, subtype=subtype) as f:
            for i in range(0, len(data), 8192):
                f.write(data[i:i + 8192])
        print(f'{path} ({subtype}, {rate} Hz)')


if __name__ == '__main__':
    main()
