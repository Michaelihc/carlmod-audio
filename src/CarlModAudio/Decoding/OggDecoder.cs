using System.IO;
using System.Runtime.CompilerServices;
using NVorbis;

namespace CarlModAudio.Decoding;

/// <summary>Ogg Vorbis reader backed by NVorbis. Kept in its own class so NVorbis.dll is only loaded for Ogg files.</summary>
internal static class OggDecoder
{
    private const int FramesPerRead = 4096;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static AudioClipData Decode(Stream stream, string name, int maxSamples)
    {
        using var reader = new VorbisReader(stream, closeOnDispose: false);
        int channels = reader.Channels;
        var builder = new ClipBuilder(reader.SampleRate, channels, reader.TotalSamples, maxSamples);
        float[] buffer = new float[FramesPerRead * channels];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            builder.Write(buffer, read / channels);

        return builder.Finish(name);
    }
}
