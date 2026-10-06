using System;
using System.Diagnostics;
using System.IO;
using CarlModAudio.Decoding;

namespace CarlModAudio
{
    // Stand-in for the plugin's clip type: the decoders only need the constructor and the sample rate.
    public sealed class AudioClipData
    {
        public const int SampleRate = 48000;

        internal AudioClipData(string name, short[] samples)
        {
            Name = name;
            Samples = samples;
        }

        public string Name { get; }

        internal short[] Samples { get; }
    }

    internal static class Program
    {
        // DecoderCheck <output folder> <file>...: writes <output>/<name>.48k.wav per input and prints length and timing.
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: DecoderCheck <output folder> <file>...");
                return 2;
            }

            Directory.CreateDirectory(args[0]);
            int failures = 0;
            for (int i = 1; i < args.Length; i++)
            {
                string file = args[i];
                var watch = Stopwatch.StartNew();
                try
                {
                    AudioClipData clip = AudioDecoder.Decode(file, Path.GetFileName(file), 60 * 60 * AudioClipData.SampleRate);
                    watch.Stop();
                    string output = Path.Combine(args[0], Path.GetFileNameWithoutExtension(file) + ".48k.wav");
                    WriteWav(output, clip.Samples);
                    Console.WriteLine($"{Path.GetFileName(file)}: {clip.Samples.Length} samples ({clip.Samples.Length / 48000.0:0.000} s) in {watch.ElapsedMilliseconds} ms");
                }
                catch (Exception e)
                {
                    failures++;
                    Console.WriteLine($"{Path.GetFileName(file)}: FAILED {e.GetType().Name}: {e.Message}");
                }
            }

            return failures == 0 ? 0 : 1;
        }

        private static void WriteWav(string path, short[] samples)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write("RIFF".ToCharArray());
            w.Write(36 + (samples.Length * 2));
            w.Write("WAVEfmt ".ToCharArray());
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(48000);
            w.Write(96000);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data".ToCharArray());
            w.Write(samples.Length * 2);
            foreach (short s in samples)
                w.Write(s);
        }
    }
}
