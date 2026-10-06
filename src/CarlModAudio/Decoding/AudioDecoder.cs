using System;
using System.IO;

namespace CarlModAudio.Decoding;

/// <summary>Picks the decoder from the file's first bytes.</summary>
internal static class AudioDecoder
{
    public static AudioClipData Decode(string path, string name, int maxSamples)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        byte[] magic = new byte[4];
        if (stream.Read(magic, 0, 4) < 4)
            throw new AudioDecodeException("the file is too short");

        stream.Position = 0;
        if (magic[0] == 'R' && magic[1] == 'I' && magic[2] == 'F' && magic[3] == 'F')
            return WavDecoder.Decode(stream, name, maxSamples);

        if (magic[0] == 'O' && magic[1] == 'g' && magic[2] == 'g' && magic[3] == 'S')
            return DecodeOgg(stream, name, maxSamples);

        throw new AudioDecodeException("unknown format; use Ogg Vorbis (.ogg) or WAV (.wav)");
    }

    private static AudioClipData DecodeOgg(Stream stream, string name, int maxSamples)
    {
        try
        {
            return OggDecoder.Decode(stream, name, maxSamples);
        }
        catch (FileNotFoundException e) when (e.FileName?.StartsWith("NVorbis", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new AudioDecodeException("NVorbis.dll is missing; copy it from the release into LabAPI-Mobile\\dependencies\\global");
        }
        catch (TypeLoadException e) when (e.TypeName.StartsWith("NVorbis", StringComparison.Ordinal))
        {
            throw new AudioDecodeException("NVorbis.dll is missing; copy it from the release into LabAPI-Mobile\\dependencies\\global");
        }
        catch (InvalidDataException e)
        {
            throw new AudioDecodeException($"invalid Ogg Vorbis data ({e.Message})");
        }
        catch (ArgumentException e)
        {
            // NVorbis reports streams that are not Vorbis (Opus, FLAC in Ogg, ...) this way.
            throw new AudioDecodeException($"not an Ogg Vorbis stream ({e.Message})");
        }
    }
}
