using System;
using System.IO;
using CarlModAudio.Internal;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;

namespace CarlModAudio;

/// <summary>
/// Plays audio files to players on Carl Mod servers through the game's voice chat: globally, at a position, or from a
/// player. Other plugins use <see cref="AudioPlayer"/>; admins use the <c>audio</c> command.
/// </summary>
public sealed class CarlModAudioPlugin : Plugin<Config>
{
    /// <inheritdoc/>
    public override string Name => "CarlModAudio";

    /// <inheritdoc/>
    public override string Description => "Plays audio files to players through the game's voice chat.";

    /// <inheritdoc/>
    public override string Author => "Michaelihc";

    /// <inheritdoc/>
    public override Version Version => new(1, 0, 1);

    /// <inheritdoc/>
    public override Version RequiredApiVersion => new(1, 0, 0);

    /// <summary>Full path of the audio folder the command reads from.</summary>
    public static string AudioFolder { get; private set; } = string.Empty;

    /// <inheritdoc/>
    public override void Enable()
    {
        string folder = string.IsNullOrWhiteSpace(Config.AudioFolder) ? "audio" : Config.AudioFolder;
        AudioFolder = Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(this.GetConfigDirectory(isGlobal: true).FullName, folder));
        AudioSystem.Enable(Config, AudioFolder);
    }

    /// <inheritdoc/>
    public override void Disable() => AudioSystem.Disable();
}
