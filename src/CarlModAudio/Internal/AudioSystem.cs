using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LabApi.Events.Handlers;
using Logger = LabApi.Features.Console.Logger;
using UnityEngine;

namespace CarlModAudio.Internal;

/// <summary>Plugin-wide state: settings, the players, the per-frame update and the lifecycle.</summary>
internal static class AudioSystem
{
    private static readonly long StatsWindow = Stopwatch.Frequency * 5;

    private static long _statsStart;
    private static long _busyTicks;
    private static int _frames;

    public static List<AudioPlayer> Players { get; } = [];

    public static bool IsEnabled { get; private set; }

    public static Config Config { get; private set; } = new();

    /// <summary>Why audio cannot play on this game build, or null.</summary>
    public static string? UnavailableReason { get; private set; }

    public static int MaxPlayers { get; private set; } = 4;

    public static int Bitrate { get; private set; } = 64000;

    public static int LeadFrames { get; private set; } = 15;

    public static float SpeakerDepth { get; private set; } = 2.2f;

    /// <summary>Factor from 16-bit samples to encoder input at volume 1: 1/32768 times the headroom attenuation.</summary>
    public static float SampleScale { get; private set; } = 1f / 32768f;

    public static int MaxClipSamples { get; private set; } = 20 * 60 * AudioClipData.SampleRate;

    /// <summary>Main-thread time spent on audio, in milliseconds per second, over the last complete 5 s window.</summary>
    public static double CpuMsPerSecond { get; private set; }

    /// <summary>Server frames per second over the last complete 5 s window.</summary>
    public static double ServerFps { get; private set; }

    public static void Enable(Config config, string audioFolder)
    {
        MainThread.Capture();
        ApplyConfig(config, audioFolder);

        if (!OpusMusicEncoder.Probe(out string? opusError))
            UnavailableReason = $"the game's Opus library cannot be used ({opusError})";
        else if (!Speaker.IsSupported(out string? speakerError))
            UnavailableReason = $"this game build cannot spawn speaker dummies ({speakerError})";
        else
            UnavailableReason = null;

        if (UnavailableReason != null)
            Logger.Error($"Audio playback is unavailable: {UnavailableReason}.");

        SpeakerPatches.Apply();
        StaticUnityMethods.OnUpdate += Update;
        ServerEvents.RoundRestarted += OnRoundRestarted;
        IsEnabled = true;
    }

    public static void Disable()
    {
        IsEnabled = false;
        ServerEvents.RoundRestarted -= OnRoundRestarted;
        StaticUnityMethods.OnUpdate -= Update;
        DestroyAll();
        SpeakerRegistry.Clear();
        SpeakerPatches.Remove();
        ClipCache.Clear();
        MainThread.Clear();
    }

    public static bool IsClipInUse(AudioClipData clip)
    {
        foreach (AudioPlayer player in Players)
        {
            if (player.Uses(clip))
                return true;
        }

        return false;
    }

    private static void ApplyConfig(Config config, string audioFolder)
    {
        Config = config;
        MaxPlayers = Mathf.Clamp(config.MaxPlayers, 1, 16);
        Bitrate = Mathf.Clamp(config.Bitrate, 8000, 128000);
        LeadFrames = Mathf.Clamp(config.BufferMs, 20, 400) / 10;
        SpeakerDepth = Mathf.Clamp(config.SpeakerDepth, 0f, 10f);
        SampleScale = Mathf.Pow(10f, -Mathf.Clamp(config.HeadroomDb, 0f, 40f) / 20f) / 32768f;
        MaxClipSamples = Mathf.Clamp(config.MaxClipMinutes, 1, 60) * 60 * AudioClipData.SampleRate;
        ClipCache.BudgetBytes = Math.Max(1, config.ClipCacheMb) * 1024L * 1024L;
        AudioFiles.Folder = audioFolder;
        try
        {
            Directory.CreateDirectory(audioFolder);
        }
        catch (Exception e)
        {
            Logger.Warn($"Cannot create the audio folder {audioFolder}: {e.Message}");
        }
    }

    private static void Update()
    {
        long now = Stopwatch.GetTimestamp();
        _frames++;
        if (now - _statsStart >= StatsWindow)
        {
            double seconds = (now - _statsStart) / (double)Stopwatch.Frequency;
            ServerFps = _frames / seconds;
            CpuMsPerSecond = _busyTicks * 1000.0 / Stopwatch.Frequency / seconds;
            _statsStart = now;
            _frames = 0;
            _busyTicks = 0;
        }

        if (Players.Count == 0 && !MainThread.HasWork)
            return;

        MainThread.Drain();
        for (int i = 0; i < Players.Count; i++)
        {
            AudioPlayer player = Players[i];
            try
            {
                player.Tick(now);
            }
            catch (Exception e)
            {
                Logger.Error($"Audio player {player.Name} failed and is destroyed:\n{e}");
                player.Destroy();
            }

            // A player that destroyed itself left the list; do not skip the next one.
            if (i < Players.Count && Players[i] != player)
                i--;
        }

        _busyTicks += Stopwatch.GetTimestamp() - now;
    }

    private static void OnRoundRestarted() => DestroyAll();

    private static void DestroyAll()
    {
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            if (i < Players.Count)
                Players[i].Destroy();
        }

        Players.Clear();
    }
}
