using System;
using System.IO;
using System.Text;

namespace CarlModAudio.Decoding;

/// <summary>
/// RIFF/WAVE reader: integer PCM (8, 16, 24, 32 bit), IEEE float (32, 64 bit) and WAVE_FORMAT_EXTENSIBLE with either
/// subformat. Other encodings (ADPCM, A-law, mu-law, ...) are rejected.
/// </summary>
internal static class WavDecoder
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;
    private const int FramesPerRead = 4096;

    public static AudioClipData Decode(Stream stream, string name, int maxSamples)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (ReadId(reader) != "RIFF")
            throw new AudioDecodeException("not a RIFF file");

        reader.ReadUInt32();
        if (ReadId(reader) != "WAVE")
            throw new AudioDecodeException("not a WAVE file");

        ushort format = 0, channels = 0, blockAlign = 0, bits = 0;
        int sampleRate = 0;
        bool haveFormat = false;

        while (stream.Position + 8 <= stream.Length)
        {
            string id = ReadId(reader);
            long size = reader.ReadUInt32();
            long next = stream.Position + size + (size & 1);

            if (id == "fmt ")
            {
                if (size < 16)
                    throw new AudioDecodeException("invalid fmt chunk");

                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                blockAlign = reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == FormatExtensible && size >= 40)
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    format = reader.ReadUInt16(); // first two bytes of the subformat GUID
                }

                haveFormat = true;
            }
            else if (id == "data")
            {
                if (!haveFormat)
                    throw new AudioDecodeException("data chunk before fmt chunk");

                // Some writers leave the size at 0 or 0xFFFFFFFF when streaming; read to the end then.
                long remaining = stream.Length - stream.Position;
                long dataSize = size == 0 || size == uint.MaxValue || size > remaining ? remaining : size;
                return ReadData(stream, name, maxSamples, format, channels, sampleRate, blockAlign, bits, dataSize);
            }

            if (next > stream.Length)
                break;

            stream.Position = next;
        }

        throw new AudioDecodeException("no data chunk");
    }

    private static AudioClipData ReadData(Stream stream, string name, int maxSamples, ushort format, int channels,
        int sampleRate, int blockAlign, int bits, long dataSize)
    {
        int bytesPerSample = bits / 8;
        bool supported = format switch
        {
            FormatPcm => bits is 8 or 16 or 24 or 32,
            FormatFloat => bits is 32 or 64,
            _ => false,
        };
        if (!supported)
            throw new AudioDecodeException($"unsupported WAV encoding (format {format}, {bits} bit); use PCM or float, or Ogg Vorbis");
        if (channels < 1 || blockAlign < bytesPerSample * channels)
            throw new AudioDecodeException("invalid WAV block alignment");

        long frames = dataSize / blockAlign;
        var builder = new ClipBuilder(sampleRate, channels, frames, maxSamples);
        byte[] raw = new byte[FramesPerRead * blockAlign];
        float[] samples = new float[FramesPerRead * channels];

        while (frames > 0)
        {
            int want = (int)Math.Min(FramesPerRead, frames);
            int got = ReadFully(stream, raw, want * blockAlign) / blockAlign;
            if (got == 0)
                break;

            for (int f = 0; f < got; f++)
            {
                int offset = f * blockAlign;
                for (int c = 0; c < channels; c++, offset += bytesPerSample)
                    samples[(f * channels) + c] = ReadSample(raw, offset, format, bits);
            }

            builder.Write(samples, got);
            frames -= got;
            if (got < want)
                break;
        }

        return builder.Finish(name);
    }

    private static float ReadSample(byte[] b, int i, ushort format, int bits)
    {
        if (format == FormatFloat)
            return bits == 32 ? BitConverter.ToSingle(b, i) : (float)BitConverter.ToDouble(b, i);

        switch (bits)
        {
            case 8:
                return (b[i] - 128) / 128f;
            case 16:
                return (short)(b[i] | (b[i + 1] << 8)) / 32768f;
            case 24:
                return ((b[i] << 8) | (b[i + 1] << 16) | (b[i + 2] << 24)) / 2147483648f;
            default:
                return BitConverter.ToInt32(b, i) / 2147483648f;
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n <= 0)
                break;

            total += n;
        }

        return total;
    }

    private static string ReadId(BinaryReader reader)
    {
        byte[] id = reader.ReadBytes(4);
        if (id.Length < 4)
            throw new AudioDecodeException("truncated file");

        return Encoding.ASCII.GetString(id);
    }
}
