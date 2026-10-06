using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using CarlModAudio.Internal;
using Logger = LabApi.Features.Console.Logger;
using LabApi.Features.Wrappers;
using Mirror;
using PlayerRoles.FirstPersonControl;
using UnityEngine;
using VoiceChat;
using VoiceChat.Networking;

namespace CarlModAudio;

/// <summary>
/// Plays clips to players through the game's voice chat. Each player that is playing uses one hidden dummy player as
/// its speaker. Use it from the main thread only.
/// </summary>
/// <remarks>
/// <para>A round restart destroys every player (<see cref="IsDestroyed"/> becomes true); create new ones afterwards,
/// for example in <c>ServerEvents.WaitingForPlayers</c>.</para>
/// <para>Audio is sent 10 ms frame by 10 ms frame in real time, a little ahead (<c>buffer_ms</c>), so the playback speed
/// does not depend on the server tick rate. Events are raised from the next server update, not from inside the call that
/// caused them.</para>
/// </remarks>
public sealed class AudioPlayer
{
    private const int FrameSamples = OpusMusicEncoder.FrameSamples;
    private const long FramesPerSecond = AudioClipData.SampleRate / FrameSamples;

    // After a server stall longer than this, the stream clock restarts instead of sending the backlog at once.
    private const int MaxLateFrames = 20;

    private static readonly long SpeakerReleaseDelay = Stopwatch.Frequency;
    private static readonly long SpeakerRetryDelay = Stopwatch.Frequency * 2;

    private readonly List<QueueItem> _queue = [];
    private readonly List<NetworkConnectionToClient> _receivers = [];
    private readonly List<PendingEvent> _events = [];
    private readonly float[] _pcm = new float[FrameSamples];
    private readonly byte[] _packet = new byte[OpusMusicEncoder.MaxPacketBytes];

    private QueueItem? _current;
    private int _sample;
    private Speaker? _speaker;
    private OpusMusicEncoder? _encoder;
    private long _speakerRetryAt;
    private int _spawnFailures;
    private long _stoppedAt;
    private bool _clockRunning;
    private long _clockStart;
    private long _framesSent;
    private float _gain;
    private float _volume;
    private string _speakerName;
    private Vector3 _targetPosition;
    private Func<Player, bool>? _receiverFilter;
    private bool _filterFailed;

    private AudioPlayer(string name)
    {
        Name = name;
        _speakerName = AudioSystem.Config.SpeakerName;
        _volume = Mathf.Clamp(AudioSystem.Config.DefaultVolume, 0, 200) / 100f;
        _gain = _volume;
    }

    /// <summary>Raised when a clip starts playing (from the next server update).</summary>
    public event Action<AudioPlayer, AudioClipData>? ClipStarted;

    /// <summary>Raised when a clip has played to its end. Not raised for <see cref="Skip"/>, <see cref="Stop"/> or loops.</summary>
    public event Action<AudioPlayer, AudioClipData>? ClipFinished;

    /// <summary>Raised when the player becomes <see cref="PlaybackState.Stopped"/>: the queue ran out, or <see cref="Stop"/> or <see cref="Destroy"/> was called.</summary>
    public event Action<AudioPlayer>? Stopped;

    /// <summary>All players that are not destroyed.</summary>
    public static IReadOnlyList<AudioPlayer> List => AudioSystem.Players;

    /// <summary>Unique name, used by the audio command.</summary>
    public string Name { get; }

    /// <summary>Current state.</summary>
    public PlaybackState State { get; private set; }

    /// <summary>True after <see cref="Destroy"/> or a round restart; the player then ignores every call.</summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>The clip being played, or the one that is loading; null when stopped.</summary>
    public AudioClipData? CurrentClip => _current?.Clip;

    /// <summary>File name or clip name of the current item, also while it is loading.</summary>
    public string? CurrentName => _current?.DisplayName;

    /// <summary>Playback position in <see cref="CurrentClip"/>.</summary>
    public TimeSpan Time => TimeSpan.FromSeconds(_sample / (double)AudioClipData.SampleRate);

    /// <summary>Clips (or files still loading) that play after the current one.</summary>
    public int QueueCount => _queue.Count;

    /// <summary>
    /// Volume from 0 (silent) to 2. At 1, a file that peaks at full scale reaches the client's voice limiter
    /// (<c>headroom_db</c>); above 1 the client limits loud parts, so it sounds denser rather than much louder.
    /// </summary>
    public float Volume
    {
        get => _volume;
        set => _volume = float.IsNaN(value) ? 0f : Mathf.Clamp(value, 0f, 2f);
    }

    /// <summary>Repeat the current clip until this is turned off or the clip is skipped. The queue waits meanwhile.</summary>
    public bool Loop { get; set; }

    /// <summary>Destroy the player once it is stopped. The audio command sets this on the players it creates.</summary>
    public bool DestroyWhenStopped { get; set; }

    /// <summary>
    /// Only players for which this returns true hear the audio; null means everyone. It runs for every listener on
    /// every server update while playing, so keep it cheap (for example a <c>HashSet.Contains</c>).
    /// </summary>
    public Func<Player, bool>? ReceiverFilter
    {
        get => _receiverFilter;
        set
        {
            _receiverFilter = value;
            _filterFailed = false;
        }
    }

    /// <summary>Nickname of the speaker. Global playback shows it in every listener's voice chat indicator.</summary>
    public string SpeakerName
    {
        get => _speakerName;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("The speaker name must contain a visible character.", nameof(value));

            _speakerName = value;
            _speaker?.SetNickname(value);
        }
    }

    /// <summary>Where listeners hear the audio. Change it with <see cref="SetGlobal"/>, <see cref="SetPosition"/> or <see cref="AttachTo"/>.</summary>
    public SpeakerMode Mode { get; private set; }

    /// <summary>Source position for <see cref="SpeakerMode.Position"/>, last known position of the player for <see cref="SpeakerMode.Player"/>.</summary>
    public Vector3 Position => _targetPosition;

    /// <summary>The player the audio follows in <see cref="SpeakerMode.Player"/> mode.</summary>
    public Player? AttachedTo { get; private set; }

    /// <summary>The speaker dummy while one exists, otherwise null.</summary>
    public Player? SpeakerPlayer => _speaker is { IsValid: true } speaker ? Player.Get(speaker.Hub) : null;

    /// <summary>
    /// Creates a player. It starts in <see cref="SpeakerMode.Global"/> mode, stopped.
    /// </summary>
    /// <param name="name">Unique name without spaces; null picks the lowest free number.</param>
    /// <exception cref="ArgumentException">The name is in use or contains whitespace.</exception>
    /// <exception cref="InvalidOperationException">The plugin is disabled, playback is unavailable on this game build, or <c>max_players</c> players exist.</exception>
    public static AudioPlayer Create(string? name = null)
    {
        if (!AudioSystem.IsEnabled)
            throw new InvalidOperationException("CarlModAudio is not enabled.");
        if (AudioSystem.UnavailableReason != null)
            throw new InvalidOperationException($"Audio playback is unavailable: {AudioSystem.UnavailableReason}.");
        if (AudioSystem.Players.Count >= AudioSystem.MaxPlayers)
            throw new InvalidOperationException($"{AudioSystem.MaxPlayers} audio players exist already (max_players).");

        if (name == null)
        {
            for (int i = 1; name == null; i++)
            {
                string candidate = i.ToString();
                if (!TryGet(candidate, out _))
                    name = candidate;
            }
        }
        else
        {
            if (name.Length == 0 || name.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
                throw new ArgumentException("A player name must not be empty or contain whitespace.", nameof(name));
            if (TryGet(name, out _))
                throw new ArgumentException($"An audio player named '{name}' exists already.", nameof(name));
        }

        var player = new AudioPlayer(name);
        AudioSystem.Players.Add(player);
        return player;
    }

    /// <summary>Finds a player by name (case-insensitive).</summary>
    public static bool TryGet(string name, [NotNullWhen(true)] out AudioPlayer? player)
    {
        foreach (AudioPlayer candidate in AudioSystem.Players)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                player = candidate;
                return true;
            }
        }

        player = null;
        return false;
    }

    /// <summary>True for the dummy players that carry the audio. Plugins that count or list players can skip them.</summary>
    public static bool IsSpeaker(Player? player) => player != null && SpeakerRegistry.Contains(player.ReferenceHub);

    /// <inheritdoc cref="IsSpeaker(Player)"/>
    public static bool IsSpeaker(ReferenceHub? hub) => SpeakerRegistry.Contains(hub);

    /// <summary>Everyone hears the audio, without direction.</summary>
    public void SetGlobal()
    {
        if (IsDestroyed)
            return;

        Mode = SpeakerMode.Global;
        AttachedTo = null;
        _speaker?.SetMode(false, default);
    }

    /// <summary>The audio comes from <paramref name="position"/> and gets quieter with distance.</summary>
    public void SetPosition(Vector3 position)
    {
        if (IsDestroyed)
            return;

        Mode = SpeakerMode.Position;
        AttachedTo = null;
        _targetPosition = position;
        _speaker?.SetMode(true, BodyPosition(position));
    }

    /// <summary>The audio comes from <paramref name="player"/> and follows them. If they leave, it stays where they were.</summary>
    /// <exception cref="ArgumentException">The player has no position (spectator, SCP-079, ...) or is a speaker.</exception>
    public void AttachTo(Player player)
    {
        if (player is null)
            throw new ArgumentNullException(nameof(player));
        if (IsDestroyed)
            return;
        if (player.IsDestroyed || player.ReferenceHub.roleManager.CurrentRole is not IFpcRole)
            throw new ArgumentException($"{player.Nickname} has no position in the world.", nameof(player));
        if (IsSpeaker(player))
            throw new ArgumentException("Audio cannot follow a speaker.", nameof(player));

        Mode = SpeakerMode.Player;
        AttachedTo = player;
        _targetPosition = player.Position;
        _speaker?.SetMode(true, BodyPosition(_targetPosition));
    }

    /// <summary>Stops what is playing, clears the queue and plays <paramref name="clip"/>.</summary>
    public void Play(AudioClipData clip)
    {
        if (clip is null)
            throw new ArgumentNullException(nameof(clip));

        PlayItem(new QueueItem(clip));
    }

    /// <summary>
    /// Stops what is playing, clears the queue and plays a file, decoding it first if it is not cached
    /// (<see cref="State"/> is <see cref="PlaybackState.Loading"/> meanwhile). Errors are logged and stop the player.
    /// </summary>
    /// <param name="path">Full path, or a path relative to the audio folder; ".ogg" or ".wav" may be left out.</param>
    public void Play(string path) => PlayItem(LoadItem(path));

    /// <summary>Adds a clip to the queue; plays it at once if the player is stopped.</summary>
    public void Enqueue(AudioClipData clip)
    {
        if (clip is null)
            throw new ArgumentNullException(nameof(clip));

        EnqueueItem(new QueueItem(clip));
    }

    /// <summary>Adds a file to the queue (decoded in the background); plays it at once if the player is stopped.</summary>
    public void Enqueue(string path) => EnqueueItem(LoadItem(path));

    /// <summary>Removes every queued item; the current clip keeps playing.</summary>
    public void ClearQueue() => _queue.Clear();

    /// <summary>Ends the current clip and continues with the queue.</summary>
    public void Skip()
    {
        if (IsDestroyed || _current == null)
            return;

        bool paused = State == PlaybackState.Paused;
        Advance();
        if (paused && State != PlaybackState.Stopped)
            State = PlaybackState.Paused;
    }

    /// <summary>Pauses; listeners hear the buffered <c>buffer_ms</c> first.</summary>
    public void Pause()
    {
        if (IsDestroyed || State is PlaybackState.Stopped or PlaybackState.Paused)
            return;

        State = PlaybackState.Paused;
        _clockRunning = false;
    }

    /// <summary>Continues after <see cref="Pause"/>.</summary>
    public void Resume()
    {
        if (IsDestroyed || State != PlaybackState.Paused)
            return;

        State = PlaybackState.Loading;
        ResolveCurrent();
    }

    /// <summary>Stops playback and clears the queue. The speaker is removed shortly after.</summary>
    public void Stop()
    {
        if (IsDestroyed)
            return;

        _queue.Clear();
        _current = null;
        if (State != PlaybackState.Stopped)
            EnterStopped();
    }

    /// <summary>Stops, removes the speaker and the player. Further calls are ignored.</summary>
    public void Destroy()
    {
        if (IsDestroyed)
            return;

        bool wasActive = State != PlaybackState.Stopped;
        IsDestroyed = true;
        State = PlaybackState.Stopped;
        _queue.Clear();
        _current = null;
        _events.Clear();
        ReleaseSpeaker();
        _encoder?.Dispose();
        _encoder = null;
        AudioSystem.Players.Remove(this);
        if (wasActive)
            Raise(Stopped);
    }

    /// <inheritdoc/>
    public override string ToString() => $"AudioPlayer {Name} ({State})";

    internal bool Uses(AudioClipData clip)
    {
        if (_current?.Clip == clip)
            return true;

        foreach (QueueItem item in _queue)
        {
            if (item.Clip == clip)
                return true;
        }

        return false;
    }

    /// <summary>Server update: advances loading, sends due frames, follows the attached player.</summary>
    internal void Tick(long now)
    {
        if (Mode == SpeakerMode.Player)
            FollowAttachedPlayer();

        if (State == PlaybackState.Loading)
            ResolveCurrent();

        if (State == PlaybackState.Playing)
            Pump(now);
        else if (State == PlaybackState.Paused && _speaker is { IsValid: false })
            ReleaseSpeaker();

        RaiseEvents();
        if (IsDestroyed || State != PlaybackState.Stopped)
            return;

        // Keep the speaker a moment so listeners hear the buffered end, then remove it.
        if (_speaker != null && now - _stoppedAt > SpeakerReleaseDelay)
            ReleaseSpeaker();

        if (DestroyWhenStopped && _speaker == null)
            Destroy();
    }

    private QueueItem LoadItem(string path)
    {
        if (path is null)
            throw new ArgumentNullException(nameof(path));

        var item = new QueueItem(path);
        AudioClipData.LoadAsync(path, (clip, error) =>
        {
            item.Clip = clip;
            item.Error = error;
        });
        return item;
    }

    private void PlayItem(QueueItem item)
    {
        if (IsDestroyed)
            return;

        _queue.Clear();
        _current = item;
        _sample = 0;
        _clockRunning = false;
        State = PlaybackState.Loading;
        ResolveCurrent();
    }

    private void EnqueueItem(QueueItem item)
    {
        if (IsDestroyed)
            return;

        if (State == PlaybackState.Stopped && _current == null)
        {
            PlayItem(item);
            return;
        }

        _queue.Add(item);
    }

    // Loading → Playing once the current item has a clip; failed items are logged and skipped.
    private void ResolveCurrent()
    {
        while (State == PlaybackState.Loading)
        {
            if (_current == null)
            {
                EnterStopped();
                return;
            }

            if (_current.Clip != null)
            {
                State = PlaybackState.Playing;
                MarkStarted(_current);
                return;
            }

            if (_current.Error == null)
                return; // still decoding

            Logger.Warn($"Audio player {Name}: cannot play {_current.Error}");
            NextItem();
        }
    }

    // Raises ClipStarted once per queue item, when its clip first plays (not again on resume).
    private void MarkStarted(QueueItem item)
    {
        if (item.Started || item.Clip == null)
            return;

        item.Started = true;
        _sample = 0;
        _events.Add(new PendingEvent(EventKind.Started, item.Clip));
    }

    private void Advance()
    {
        NextItem();
        if (_current == null)
        {
            EnterStopped();
            return;
        }

        State = PlaybackState.Loading;
        _clockRunning = false;
        ResolveCurrent();
    }

    private void NextItem()
    {
        _sample = 0;
        if (_queue.Count == 0)
        {
            _current = null;
            return;
        }

        _current = _queue[0];
        _queue.RemoveAt(0);
    }

    private void EnterStopped()
    {
        State = PlaybackState.Stopped;
        _clockRunning = false;
        _stoppedAt = Stopwatch.GetTimestamp();
        _events.Add(new PendingEvent(EventKind.Stopped, null));
    }

    private void Pump(long now)
    {
        if (!EnsureSpeaker(now) || now < _speaker!.ReadyAt)
        {
            _clockRunning = false;
            return;
        }

        long lead = AudioSystem.LeadFrames;
        if (!_clockRunning)
        {
            _clockRunning = true;
            _clockStart = now;
            _framesSent = 0;
        }

        long due = ((now - _clockStart) * FramesPerSecond / Stopwatch.Frequency) + lead;
        long pending = due - _framesSent;
        if (pending <= 0)
            return;

        if (pending > lead + MaxLateFrames)
        {
            // The server stalled; the listeners' buffers have run dry. Restart the clock with a fresh lead.
            _clockStart = now - (_framesSent * Stopwatch.Frequency / FramesPerSecond);
            pending = lead;
        }

        int receivers = CollectReceivers();
        VoiceChatChannel channel = _speaker.IsPositional ? VoiceChatChannel.Proximity : VoiceChatChannel.Spectator;
        while (pending-- > 0)
        {
            FillFrame();
            if (receivers > 0)
            {
                SendFrame(channel);
                if (State != PlaybackState.Playing)
                    return; // encoding failed and stopped the player
            }

            _framesSent++;
            if (_current == null)
            {
                EnterStopped();
                return;
            }

            if (_current.Clip == null)
            {
                // The next queued file is still decoding (or failed); continue once it resolves.
                State = PlaybackState.Loading;
                _clockRunning = false;
                return;
            }
        }
    }

    // Fills one frame from the current clip, crossing into the next queued clip; pads with silence at the end.
    private void FillFrame()
    {
        float scale = AudioSystem.SampleScale;
        float start = _gain;
        float step = (_volume - start) / FrameSamples;
        int filled = 0;
        while (filled < FrameSamples)
        {
            AudioClipData? clip = _current?.Clip;
            if (clip == null)
                break;

            short[] samples = clip.Samples;
            int count = Math.Min(FrameSamples - filled, samples.Length - _sample);
            for (int i = 0; i < count; i++)
            {
                float value = samples[_sample + i] * ((start + (step * (filled + i))) * scale);
                _pcm[filled + i] = value > 1f ? 1f : value < -1f ? -1f : value;
            }

            filled += count;
            _sample += count;
            if (_sample < samples.Length)
                continue;

            if (Loop)
            {
                _sample = 0;
                continue;
            }

            _events.Add(new PendingEvent(EventKind.Finished, clip));
            NextItem();
            while (_current is { Clip: null, Error: not null })
            {
                Logger.Warn($"Audio player {Name}: cannot play {_current.Error}");
                NextItem();
            }

            if (_current != null)
                MarkStarted(_current);
        }

        if (filled < FrameSamples)
            Array.Clear(_pcm, filled, FrameSamples - filled);

        _gain = _volume;
    }

    private void SendFrame(VoiceChatChannel channel)
    {
        int length;
        try
        {
            _encoder ??= new OpusMusicEncoder(AudioSystem.Bitrate);
            length = _encoder.Encode(_pcm, _packet);
        }
        catch (Exception e)
        {
            Logger.Error($"Audio player {Name}: encoding failed, stopping:\n{e}");
            Stop();
            return;
        }

        var message = new VoiceMessage(_speaker!.Hub, channel, _packet, length, false);
        for (int i = 0; i < _receivers.Count; i++)
            _receivers[i].Send(message);
    }

    private int CollectReceivers()
    {
        _receivers.Clear();
        bool positional = _speaker!.IsPositional;
        Vector3 source = positional ? _speaker.BodyPosition : default;
        Func<Player, bool>? filter = _receiverFilter;
        foreach (ReferenceHub hub in ReferenceHub.AllHubs)
        {
            if (hub.isLocalPlayer || hub.Mode != ClientInstanceMode.ReadyClient)
                continue;

            NetworkConnectionToClient connection = hub.connectionToClient;
            if (connection == null || !connection.isReady || Speaker.IsDummyConnection(connection))
                continue;

            if (positional && !InHearingRange(hub, source))
                continue;

            if (filter != null && (_filterFailed || !PassesFilter(filter, hub)))
                continue;

            _receivers.Add(connection);
        }

        return _receivers.Count;
    }

    private bool PassesFilter(Func<Player, bool> filter, ReferenceHub hub)
    {
        try
        {
            return filter(Player.Get(hub)!);
        }
        catch (Exception e)
        {
            // Fail closed: a broken filter must not send audio to players it was meant to exclude.
            if (!_filterFailed)
                Logger.Error($"Audio player {Name}: the receiver filter threw; nobody hears this player until the filter is replaced:\n{e}");

            _filterFailed = true;
            return false;
        }
    }

    // The client only places a player within its visibility range (FpcVisibilityController: about 33 m, 70 m on the
    // surface); further away the speaker sits at its hidden position and cannot be heard, so nothing is sent.
    private static bool InHearingRange(ReferenceHub hub, Vector3 source)
    {
        if (hub.roleManager.CurrentRole is not IFpcRole fpc)
            return true; // spectators listen from the player they watch

        Vector3 position = fpc.FpcModule.Position;
        float maxSqr = Mathf.Min(position.y, source.y) > 800f ? 4900f : 1100f;
        return (position - source).sqrMagnitude <= maxSqr;
    }

    private bool EnsureSpeaker(long now)
    {
        if (_speaker is { IsValid: true })
            return true;

        if (_speaker != null)
        {
            // Removed from outside (an admin, a plugin) or its role could not be set; make a new one.
            ReleaseSpeaker();
            Logger.Warn($"Audio player {Name}: the speaker was removed; spawning a new one.");
        }

        if (now < _speakerRetryAt)
            return false;

        bool positional = Mode != SpeakerMode.Global;
        _speaker = Speaker.Spawn(_speakerName, positional, positional ? BodyPosition(_targetPosition) : Vector3.zero, out string? error);
        if (_speaker != null)
        {
            _spawnFailures = 0;
            return true;
        }

        _speakerRetryAt = now + SpeakerRetryDelay;
        if (!Speaker.IsSupported(out _) || ++_spawnFailures >= 3)
        {
            Logger.Error($"Audio player {Name}: no speaker, stopping ({error}).");
            Stop();
        }
        else
        {
            Logger.Warn($"Audio player {Name}: no speaker yet ({error}); retrying.");
        }

        return false;
    }

    private void ReleaseSpeaker()
    {
        _speaker?.Destroy();
        _speaker = null;
        _clockRunning = false;
    }

    private void FollowAttachedPlayer()
    {
        Player? player = AttachedTo;
        if (player == null || player.IsDestroyed)
        {
            // The player left: stay where they were.
            Mode = SpeakerMode.Position;
            AttachedTo = null;
            return;
        }

        if (player.ReferenceHub.roleManager.CurrentRole is not IFpcRole fpc)
            return; // dead or spectating: keep the last position

        _targetPosition = fpc.FpcModule.Position;
        if (_speaker is { IsValid: true, IsPositional: true })
            _speaker.MoveTo(BodyPosition(_targetPosition));
    }

    private static Vector3 BodyPosition(Vector3 position) => position + (Vector3.down * AudioSystem.SpeakerDepth);

    private void RaiseEvents()
    {
        if (_events.Count == 0)
            return;

        // Handlers may call back into this player; work on a fixed count and keep later additions for next time.
        int count = _events.Count;
        for (int i = 0; i < count; i++)
        {
            PendingEvent pending = _events[i];
            switch (pending.Kind)
            {
                case EventKind.Started:
                    Raise(ClipStarted, pending.Clip!);
                    break;
                case EventKind.Finished:
                    Raise(ClipFinished, pending.Clip!);
                    break;
                default:
                    Raise(Stopped);
                    break;
            }

            if (IsDestroyed)
                return;
        }

        _events.RemoveRange(0, Math.Min(count, _events.Count));
    }

    private void Raise(Action<AudioPlayer, AudioClipData>? handler, AudioClipData clip)
    {
        try
        {
            handler?.Invoke(this, clip);
        }
        catch (Exception e)
        {
            Logger.Error($"An audio player {Name} event handler threw:\n{e}");
        }
    }

    private void Raise(Action<AudioPlayer>? handler)
    {
        try
        {
            handler?.Invoke(this);
        }
        catch (Exception e)
        {
            Logger.Error($"An audio player {Name} event handler threw:\n{e}");
        }
    }

    private enum EventKind : byte
    {
        Started,
        Finished,
        Stopped,
    }

    private readonly struct PendingEvent(EventKind kind, AudioClipData? clip)
    {
        public EventKind Kind { get; } = kind;

        public AudioClipData? Clip { get; } = clip;
    }

    private sealed class QueueItem
    {
        public QueueItem(AudioClipData clip)
        {
            Clip = clip;
            DisplayName = clip.Name;
        }

        public QueueItem(string path)
        {
            DisplayName = System.IO.Path.GetFileName(path);
        }

        public string DisplayName { get; }

        public AudioClipData? Clip { get; set; }

        public string? Error { get; set; }

        public bool Started { get; set; }
    }
}
