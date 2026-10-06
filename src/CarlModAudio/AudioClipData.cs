using System;
using System.IO;
using CarlModAudio.Decoding;
using CarlModAudio.Internal;

namespace CarlModAudio;

/// <summary>
/// Decoded audio ready to play: 48 kHz mono 16-bit samples. Clips are immutable; one clip can play on any number of
/// <see cref="AudioPlayer"/>s at the same time.
/// </summary>
/// <remarks>
/// Files are decoded once and cached by full path (and file size and time stamp) within <c>clip_cache_mb</c>.
/// Stereo and multichannel input is mixed down to mono; every sample rate is converted to 48 kHz.
/// </remarks>
public sealed class AudioClipData
{
    /// <summary>Sample rate of every clip, and of the game's voice chat.</summary>
    public const int SampleRate = 48000;

    internal AudioClipData(string name, short[] samples)
    {
        Name = name;
        Samples = samples;
    }

    /// <summary>Display name: the file name for loaded clips.</summary>
    public string Name { get; }

    /// <summary>Length in samples at <see cref="SampleRate"/>.</summary>
    public int Length => Samples.Length;

    /// <summary>Length of the clip.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);

    internal short[] Samples { get; }

    internal long SizeBytes => Samples.LongLength * sizeof(short);

    /// <summary>
    /// Creates a clip from raw samples, for example generated tones. Interleaved input with several channels is mixed
    /// down; any sample rate is converted to 48 kHz. Values are clamped to -1..1.
    /// </summary>
    /// <param name="samples">Interleaved samples in the range -1..1.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/> in Hz.</param>
    /// <param name="channels">Number of interleaved channels.</param>
    /// <param name="name">Display name.</param>
    public static AudioClipData FromPcm(float[] samples, int sampleRate, int channels = 1, string name = "pcm")
    {
        if (samples is null)
            throw new ArgumentNullException(nameof(samples));
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels));

        int frames = samples.Length / channels;
        try
        {
            var builder = new ClipBuilder(sampleRate, channels, frames, AudioSystem.MaxClipSamples);
            builder.Write(samples, frames);
            return builder.Finish(name);
        }
        catch (AudioDecodeException e)
        {
            throw new ArgumentException(e.Message, nameof(samples));
        }
    }

    /// <summary>
    /// Decodes a file on the calling thread (blocking; a long Ogg file takes seconds), or returns the cached clip.
    /// Prefer <see cref="LoadAsync"/> on the main thread.
    /// </summary>
    /// <param name="path">Full path, or a path relative to the audio folder. ".ogg" or ".wav" may be left out.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file cannot be decoded; the message says why.</exception>
    public static AudioClipData Load(string path)
    {
        string fullPath = AudioFiles.Resolve(path, allowOutsideFolder: true, out string? error)
            ?? throw new FileNotFoundException(error, path);

        return ClipCache.LoadBlocking(fullPath);
    }

    /// <summary>
    /// Decodes a file on a worker thread and calls <paramref name="callback"/> on the main thread with the clip, or with
    /// null and an error message. A cached clip is passed at once, before this method returns. Call it on the main thread
    /// while the plugin is enabled.
    /// </summary>
    /// <param name="path">Full path, or a path relative to the audio folder. ".ogg" or ".wav" may be left out.</param>
    /// <param name="callback">Receives the clip or null, and the error message or null.</param>
    public static void LoadAsync(string path, Action<AudioClipData?, string?> callback)
    {
        if (callback is null)
            throw new ArgumentNullException(nameof(callback));

        string? fullPath = AudioFiles.Resolve(path, allowOutsideFolder: true, out string? error);
        if (fullPath is null)
        {
            callback(null, error);
            return;
        }

        ClipCache.LoadAsync(fullPath, callback);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({AudioFormat.Time(Duration)})";
}
