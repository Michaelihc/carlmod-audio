using System;
using System.Runtime.InteropServices;

namespace CarlModAudio.Internal;

/// <summary>
/// 48 kHz mono Opus encoder tuned for music, calling the libopus build that ships with the game
/// ("Carl Mod_Data\Plugins\x86_64\libopus-0.dll", also used by the game's own voice chat). The client decodes the
/// frames with its stock voice decoder.
/// </summary>
internal sealed class OpusMusicEncoder : IDisposable
{
    /// <summary>Samples per frame: 10 ms, the frame size the game's voice chat uses.</summary>
    public const int FrameSamples = 480;

    /// <summary>Largest encoded frame the client's voice message reader accepts.</summary>
    public const int MaxPacketBytes = 512;

    private const string Library = "libopus-0";
    private const int ApplicationAudio = 2049;
    private const int SetBitrateRequest = 4002;
    private const int SetSignalRequest = 4024;
    private const int SignalMusic = 3002;

    private IntPtr _handle;

    public OpusMusicEncoder(int bitrate)
    {
        _handle = opus_encoder_create(AudioClipData.SampleRate, 1, ApplicationAudio, out int error);
        if (_handle == IntPtr.Zero || error != 0)
            throw new InvalidOperationException($"opus_encoder_create failed with error {error}.");

        opus_encoder_ctl(_handle, SetBitrateRequest, bitrate);
        opus_encoder_ctl(_handle, SetSignalRequest, SignalMusic);
    }

    /// <summary>Checks that the native library and its exports can be called.</summary>
    public static bool Probe(out string? error)
    {
        try
        {
            using var encoder = new OpusMusicEncoder(64000);
            float[] silence = new float[FrameSamples];
            byte[] packet = new byte[MaxPacketBytes];
            int length = encoder.Encode(silence, packet);
            error = length > 0 ? null : $"opus_encode_float returned {length}";
            return error == null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    /// <summary>Encodes one frame of <see cref="FrameSamples"/> samples; returns the packet length.</summary>
    public int Encode(float[] pcm, byte[] packet)
    {
        int length = opus_encode_float(_handle, pcm, FrameSamples, packet, packet.Length);
        if (length < 0)
            throw new InvalidOperationException($"opus_encode_float failed with error {length}.");

        return length;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;

        opus_encoder_destroy(_handle);
        _handle = IntPtr.Zero;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr opus_encoder_create(int fs, int channels, int application, out int error);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int opus_encode_float(IntPtr encoder, float[] pcm, int frameSize, byte[] data, int maxDataBytes);

    // Variadic in C; on x64 an int argument is passed exactly like a fixed parameter.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int opus_encoder_ctl(IntPtr encoder, int request, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void opus_encoder_destroy(IntPtr encoder);
}
