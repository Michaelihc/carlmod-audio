using System;

namespace CarlModAudio.Decoding;

/// <summary>
/// Turns decoded interleaved float samples of any rate and channel count into a 48 kHz mono 16-bit clip:
/// channels are averaged, then resampled. Runs on the decoding thread.
/// </summary>
internal sealed class ClipBuilder
{
    private readonly int _channels;
    private readonly int _maxSamples;
    private readonly Resampler _resampler;
    private float[] _mono = new float[4096];
    private short[] _samples;
    private int _length;

    /// <param name="sampleRate">Input sample rate.</param>
    /// <param name="channels">Input channel count.</param>
    /// <param name="expectedFrames">Input length in frames when known (sizes the output array), otherwise 0.</param>
    /// <param name="maxSamples">Longest accepted output, in 48 kHz samples.</param>
    public ClipBuilder(int sampleRate, int channels, long expectedFrames, int maxSamples)
    {
        if (channels < 1 || channels > 32)
            throw new AudioDecodeException($"unsupported channel count {channels}");
        if (sampleRate < 1000 || sampleRate > 768000)
            throw new AudioDecodeException($"unsupported sample rate {sampleRate} Hz");

        _channels = channels;
        _maxSamples = maxSamples;
        long expected = expectedFrames > 0 ? (long)Math.Ceiling(expectedFrames * (double)AudioClipData.SampleRate / sampleRate) : 0;
        if (expected > maxSamples)
            throw new AudioDecodeException(TooLongMessage(maxSamples));

        _samples = new short[Math.Max(expected + 64, AudioClipData.SampleRate)];
        _resampler = new Resampler(sampleRate, Append);
    }

    /// <summary>Adds <paramref name="frames"/> interleaved frames.</summary>
    public void Write(float[] interleaved, int frames)
    {
        if (_mono.Length < frames)
            _mono = new float[frames];

        if (_channels == 1)
        {
            Array.Copy(interleaved, _mono, frames);
        }
        else
        {
            float scale = 1f / _channels;
            for (int f = 0, i = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < _channels; c++, i++)
                    sum += interleaved[i];

                _mono[f] = sum * scale;
            }
        }

        _resampler.Push(_mono, frames);
    }

    public AudioClipData Finish(string name)
    {
        _resampler.Flush();
        if (_length == 0)
            throw new AudioDecodeException("the file contains no audio");

        if (_samples.Length != _length)
            Array.Resize(ref _samples, _length);

        return new AudioClipData(name, _samples);
    }

    private void Append(float sample)
    {
        if (_length == _samples.Length)
        {
            if (_length >= _maxSamples)
                throw new AudioDecodeException(TooLongMessage(_maxSamples));

            Array.Resize(ref _samples, (int)Math.Min((long)_samples.Length * 2, _maxSamples));
        }

        float scaled = sample * 32767f;
        if (scaled > 32767f)
            scaled = 32767f;
        else if (scaled < -32768f)
            scaled = -32768f;

        _samples[_length++] = (short)Math.Round(scaled);
    }

    private static string TooLongMessage(int maxSamples) =>
        $"longer than the {maxSamples / AudioClipData.SampleRate / 60} minute limit (max_clip_minutes)";
}

/// <summary>An audio file could not be decoded; the message says why.</summary>
internal sealed class AudioDecodeException(string message) : Exception(message);
