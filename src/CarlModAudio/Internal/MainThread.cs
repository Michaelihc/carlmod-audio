using System;
using System.Collections.Concurrent;
using System.Threading;
using LabApi.Features.Console;

namespace CarlModAudio.Internal;

/// <summary>Runs work posted from other threads on the Unity main thread, during the audio update.</summary>
internal static class MainThread
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static int _threadId = -1;

    public static bool IsCurrent => Thread.CurrentThread.ManagedThreadId == _threadId;

    public static bool HasWork => !Queue.IsEmpty;

    /// <summary>Remembers the calling thread as the main thread (call from plugin enable).</summary>
    public static void Capture() => _threadId = Thread.CurrentThread.ManagedThreadId;

    public static void Post(Action action) => Queue.Enqueue(action);

    public static void Drain()
    {
        while (Queue.TryDequeue(out Action? action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Logger.Error($"Main-thread callback failed:\n{e}");
            }
        }
    }

    public static void Clear()
    {
        while (Queue.TryDequeue(out _))
        {
        }
    }
}
