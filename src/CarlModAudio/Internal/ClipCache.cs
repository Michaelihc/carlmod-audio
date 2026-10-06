using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CarlModAudio.Decoding;
using LabApi.Features.Console;

namespace CarlModAudio.Internal;

/// <summary>
/// Decoded clips by full path. Decoding runs on thread-pool threads; callbacks run on the main thread. Least recently
/// used clips that no player holds are dropped when the cache exceeds its budget.
/// </summary>
internal static class ClipCache
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Pending> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static long _bytes;
    private static long _useCounter;
    private static int _generation;

    public static long BudgetBytes { get; set; } = 128L * 1024 * 1024;

    public static AudioClipData LoadBlocking(string fullPath)
    {
        FileStamp stamp = FileStamp.Of(fullPath);
        lock (Lock)
        {
            if (TryGetLocked(fullPath, stamp, out AudioClipData? cached))
                return cached;
        }

        AudioClipData clip;
        try
        {
            clip = AudioDecoder.Decode(fullPath, Path.GetFileName(fullPath), AudioSystem.MaxClipSamples);
        }
        catch (AudioDecodeException e)
        {
            throw new InvalidDataException($"{Path.GetFileName(fullPath)}: {e.Message}", e);
        }

        lock (Lock)
            AddLocked(fullPath, stamp, clip);

        return clip;
    }

    public static void LoadAsync(string fullPath, Action<AudioClipData?, string?> callback)
    {
        FileStamp stamp = FileStamp.Of(fullPath);
        AudioClipData? cached;
        lock (Lock)
        {
            if (!TryGetLocked(fullPath, stamp, out cached))
            {
                if (InFlight.TryGetValue(fullPath, out Pending? pending) && pending.Stamp.Equals(stamp))
                {
                    pending.Callbacks.Add(callback);
                    return;
                }

                pending = new Pending(stamp, _generation);
                pending.Callbacks.Add(callback);
                InFlight[fullPath] = pending;
                ThreadPool.QueueUserWorkItem(_ => DecodeWorker(fullPath, pending));
                return;
            }
        }

        callback(cached, null);
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Entries.Clear();
            InFlight.Clear();
            _bytes = 0;
            _generation++;
        }
    }

    public static long CachedBytes
    {
        get
        {
            lock (Lock)
                return _bytes;
        }
    }

    private static void DecodeWorker(string fullPath, Pending pending)
    {
        AudioClipData? clip = null;
        string? error = null;
        try
        {
            clip = AudioDecoder.Decode(fullPath, Path.GetFileName(fullPath), AudioSystem.MaxClipSamples);
        }
        catch (AudioDecodeException e)
        {
            error = $"{Path.GetFileName(fullPath)}: {e.Message}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"{Path.GetFileName(fullPath)}: {e.Message}";
        }
        catch (Exception e)
        {
            error = $"{Path.GetFileName(fullPath)}: decoding failed ({e.GetType().Name}: {e.Message})";
            Logger.Error($"Decoding {fullPath} failed:\n{e}");
        }

        MainThread.Post(() => Complete(fullPath, pending, clip, error));
    }

    private static void Complete(string fullPath, Pending pending, AudioClipData? clip, string? error)
    {
        lock (Lock)
        {
            if (pending.Generation != _generation)
                return; // the plugin was disabled meanwhile

            if (InFlight.TryGetValue(fullPath, out Pending? current) && current == pending)
                InFlight.Remove(fullPath);

            if (clip != null)
                AddLocked(fullPath, pending.Stamp, clip);
        }

        foreach (Action<AudioClipData?, string?> callback in pending.Callbacks)
        {
            try
            {
                callback(clip, error);
            }
            catch (Exception e)
            {
                Logger.Error($"A clip load callback for {Path.GetFileName(fullPath)} threw:\n{e}");
            }
        }
    }

    private static bool TryGetLocked(string fullPath, FileStamp stamp, out AudioClipData clip)
    {
        if (Entries.TryGetValue(fullPath, out Entry? entry) && entry.Stamp.Equals(stamp))
        {
            entry.LastUse = ++_useCounter;
            clip = entry.Clip;
            return true;
        }

        clip = null!;
        return false;
    }

    private static void AddLocked(string fullPath, FileStamp stamp, AudioClipData clip)
    {
        if (Entries.TryGetValue(fullPath, out Entry? old))
            _bytes -= old.Clip.SizeBytes;

        Entries[fullPath] = new Entry(clip, stamp) { LastUse = ++_useCounter };
        _bytes += clip.SizeBytes;

        // Trimming reads the players, which only the main thread may touch; a worker-thread Load trims later.
        if (MainThread.IsCurrent)
            TrimLocked();
    }

    private static void TrimLocked()
    {
        while (_bytes > BudgetBytes)
        {
            string? oldestKey = null;
            Entry? oldest = null;
            foreach (KeyValuePair<string, Entry> pair in Entries)
            {
                if ((oldest == null || pair.Value.LastUse < oldest.LastUse) && !AudioSystem.IsClipInUse(pair.Value.Clip))
                {
                    oldest = pair.Value;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey == null)
                return; // everything left is playing

            Entries.Remove(oldestKey);
            _bytes -= oldest!.Clip.SizeBytes;
        }
    }

    private sealed class Entry(AudioClipData clip, FileStamp stamp)
    {
        public AudioClipData Clip { get; } = clip;

        public FileStamp Stamp { get; } = stamp;

        public long LastUse { get; set; }
    }

    private sealed class Pending(FileStamp stamp, int generation)
    {
        public FileStamp Stamp { get; } = stamp;

        public int Generation { get; } = generation;

        public List<Action<AudioClipData?, string?>> Callbacks { get; } = [];
    }

    private readonly struct FileStamp(long length, long writeTicks) : IEquatable<FileStamp>
    {
        private readonly long _length = length;
        private readonly long _writeTicks = writeTicks;

        public static FileStamp Of(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : default;
        }

        public bool Equals(FileStamp other) => _length == other._length && _writeTicks == other._writeTicks;
    }
}
