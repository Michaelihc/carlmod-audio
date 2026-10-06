namespace CarlModAudio;

/// <summary>What an <see cref="AudioPlayer"/> is doing.</summary>
public enum PlaybackState
{
    /// <summary>Nothing to play.</summary>
    Stopped,

    /// <summary>Waiting for the current clip to finish decoding.</summary>
    Loading,

    /// <summary>Sending audio.</summary>
    Playing,

    /// <summary>Paused; <see cref="AudioPlayer.Resume"/> continues where it stopped.</summary>
    Paused,
}

/// <summary>Where listeners hear an <see cref="AudioPlayer"/>.</summary>
public enum SpeakerMode
{
    /// <summary>Everyone hears it at the same volume, without direction, like the intercom.</summary>
    Global,

    /// <summary>It comes from a fixed world position and gets quieter with distance, like a player's voice.</summary>
    Position,

    /// <summary>It comes from a player and moves with them.</summary>
    Player,
}
