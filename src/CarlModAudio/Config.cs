using System.ComponentModel;

namespace CarlModAudio;

/// <summary>Settings in <c>LabAPI-Mobile\configs\&lt;port&gt;\CarlModAudio\config.yml</c>.</summary>
public sealed class Config
{
    [Description("Folder with the files the audio command plays (.ogg, .wav). A relative path starts in LabAPI-Mobile\\configs\\global\\CarlModAudio.")]
    public string AudioFolder { get; set; } = "audio";

    [Description("Nickname of the speakers. Players see it in the voice chat indicator during global playback.")]
    public string SpeakerName { get; set; } = "Music";

    [Description("Most audio players that can exist at once (1-16). Each playing player uses one hidden dummy player as its speaker.")]
    public int MaxPlayers { get; set; } = 4;

    [Description("Volume of new players in percent (0-200).")]
    public int DefaultVolume { get; set; } = 100;

    [Description("Attenuation applied to every file, in dB (0-40). The client amplifies voice chat by about 17 dB and then limits it, which flattens loud music; with 18, a file that peaks at full scale stays just below that limit at volume 100%.")]
    public float HeadroomDb { get; set; } = 18f;

    [Description("Opus bitrate in bits per second (8000-128000). Each listener receives about this much per playing player.")]
    public int Bitrate { get; set; } = 64000;

    [Description("Audio sent ahead of real time, in milliseconds (20-400). More absorbs network hiccups; pause and stop take this long to be heard.")]
    public int BufferMs { get; set; } = 150;

    [Description("Positional playback: metres below the sound source where the speaker's body is put, so the floor hides it. 0 shows the speaker as a Tutorial standing at the source.")]
    public float SpeakerDepth { get; set; } = 2.2f;

    [Description("Memory for decoded clips in MB. Beyond it, the least recently used clips that are not playing are dropped. One minute of audio takes 5.5 MB.")]
    public int ClipCacheMb { get; set; } = 128;

    [Description("Longest file that is decoded, in minutes (1-60).")]
    public int MaxClipMinutes { get; set; } = 20;

    [Description("Remote Admin permission the audio command needs. The server console may always use it.")]
    public PlayerPermissions CommandPermission { get; set; } = PlayerPermissions.Broadcasting;
}
