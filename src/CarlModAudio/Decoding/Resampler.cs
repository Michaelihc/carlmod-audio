using System;

namespace CarlModAudio.Decoding;

/// <summary>
/// Streaming sample-rate converter from any input rate to 48 kHz: a Kaiser-windowed sinc low-pass evaluated from a
/// table (512 phases per input sample, linearly interpolated). The cutoff sits at 95 % of the lower Nyquist frequency,
/// so downsampling does not alias. Runs on the decoding thread only.
/// </summary>
internal sealed class Resampler
{
    private const int OutputRate = AudioClipData.SampleRate;
    private const int ZeroCrossings = 16;
    private const int Phases = 512;
    private const double KaiserBeta = 9.0;

    private readonly Action<float> _output;
    private readonly bool _passThrough;
    private readonly double _step;
    private readonly int _halfTaps;
    private readonly float[] _table = Array.Empty<float>();

    // Input history: _buffer[0] holds the input sample with absolute index _bufferStart.
    private float[] _buffer = new float[8192];
    private long _bufferStart;
    private int _bufferCount;
    private long _inputTotal;
    private long _nextOutput;

    public Resampler(int inputRate, Action<float> output)
    {
        if (inputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate));

        _output = output;
        _passThrough = inputRate == OutputRate;
        if (_passThrough)
            return;

        _step = inputRate / (double)OutputRate;

        // Cutoff as a fraction of the input Nyquist frequency.
        double cutoff = Math.Min(1.0, OutputRate / (double)inputRate) * 0.95;
        double halfWidth = ZeroCrossings / cutoff;
        _halfTaps = (int)Math.Ceiling(halfWidth);

        _table = new float[(_halfTaps * Phases) + 2];
        double i0Beta = BesselI0(KaiserBeta);
        for (int i = 0; i < _table.Length; i++)
        {
            double t = i / (double)Phases;
            if (t >= halfWidth)
                break;

            double ratio = t / halfWidth;
            double window = BesselI0(KaiserBeta * Math.Sqrt(1.0 - (ratio * ratio))) / i0Beta;
            _table[i] = (float)(cutoff * Sinc(cutoff * t) * window);
        }
    }

    /// <summary>Adds mono input samples and emits every output sample they complete.</summary>
    public void Push(float[] samples, int count)
    {
        if (_passThrough)
        {
            for (int i = 0; i < count; i++)
                _output(samples[i]);

            return;
        }

        EnsureCapacity(_bufferCount + count);
        Array.Copy(samples, 0, _buffer, _bufferCount, count);
        _bufferCount += count;
        _inputTotal += count;
        Produce(final: false);
    }

    /// <summary>Emits the remaining output samples, treating the input after its end as silence.</summary>
    public void Flush()
    {
        if (!_passThrough)
            Produce(final: true);
    }

    private void Produce(bool final)
    {
        long available = _bufferStart + _bufferCount;
        while (true)
        {
            double x = _nextOutput * _step;
            if (final && x >= _inputTotal)
                break;

            long center = (long)Math.Floor(x);
            if (!final && center + _halfTaps >= available)
                break;

            _output(Convolve(x, center));
            _nextOutput++;
        }

        // Keep only the history the next output sample needs.
        long keepFrom = (long)Math.Floor(_nextOutput * _step) - _halfTaps + 1;
        int drop = (int)Math.Max(0, Math.Min(_bufferCount, keepFrom - _bufferStart));
        if (drop > 0)
        {
            _bufferCount -= drop;
            Array.Copy(_buffer, drop, _buffer, 0, _bufferCount);
            _bufferStart += drop;
        }
    }

    private float Convolve(double x, long center)
    {
        double frac = x - center;

        // Left taps sit at distances frac, frac + 1, ...; right taps at 1 - frac, 2 - frac, ...
        double a = frac * Phases;
        int leftIndex = (int)a;
        float leftWeight = (float)(a - leftIndex);
        double b = (1.0 - frac) * Phases;
        int rightIndex = (int)b;
        float rightWeight = (float)(b - rightIndex);

        float[] table = _table;
        float[] buffer = _buffer;
        long first = center - _halfTaps + 1;
        long last = center + _halfTaps;
        int c = (int)(center - _bufferStart);
        float acc = 0f;

        if (first >= _bufferStart && last < _bufferStart + _bufferCount)
        {
            for (int k = 0; k < _halfTaps; k++, leftIndex += Phases)
            {
                float h = table[leftIndex] + ((table[leftIndex + 1] - table[leftIndex]) * leftWeight);
                acc += buffer[c - k] * h;
            }

            for (int k = 0; k < _halfTaps; k++, rightIndex += Phases)
            {
                float h = table[rightIndex] + ((table[rightIndex + 1] - table[rightIndex]) * rightWeight);
                acc += buffer[c + 1 + k] * h;
            }

            return acc;
        }

        // Start or end of the stream: samples outside the buffer are silence.
        for (int k = 0; k < _halfTaps; k++, leftIndex += Phases)
        {
            int i = c - k;
            if (i >= 0 && i < _bufferCount)
                acc += buffer[i] * (table[leftIndex] + ((table[leftIndex + 1] - table[leftIndex]) * leftWeight));
        }

        for (int k = 0; k < _halfTaps; k++, rightIndex += Phases)
        {
            int i = c + 1 + k;
            if (i >= 0 && i < _bufferCount)
                acc += buffer[i] * (table[rightIndex] + ((table[rightIndex + 1] - table[rightIndex]) * rightWeight));
        }

        return acc;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length)
            return;

        int size = _buffer.Length;
        while (size < needed)
            size *= 2;

        Array.Resize(ref _buffer, size);
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-12)
            return 1.0;

        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    // Zeroth-order modified Bessel function of the first kind (power series).
    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        double half = x / 2.0;
        for (int k = 1; k < 64; k++)
        {
            term *= half / k;
            double add = term * term;
            sum += add;
            if (add < sum * 1e-16)
                break;
        }

        return sum;
    }
}
