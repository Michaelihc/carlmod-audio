# CarlModAudio

[简体中文](README.zh-CN.md)

A [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile) plugin that plays audio files to players on Carl Mod
servers (the mobile fork of SCP: Secret Laboratory). It runs on the server only: players keep the stock Carl Mod
Android client, which plays the audio through its voice chat.

- **Global** playback: every player hears it at the same volume, like the intercom.
- **Positional** playback: the sound comes from a point in the facility, or follows a player, and fades with distance.
- WAV (8, 16, 24, 32-bit PCM and 32/64-bit float) and Ogg Vorbis files, any sample rate, mono or stereo.
- Play, queue, skip, stop, pause, resume, volume, loop; several players at once; optionally only for chosen players.
- An `audio` command for admins and an `AudioPlayer` API for other plugins.

## How it works

The client plays voice chat that a player sends. For every audio player that is playing, CarlModAudio spawns a hidden
dummy player (a *speaker*), encodes the file with the game's own Opus encoder and sends it as that speaker's voice,
10 ms at a time in real time.

- For global playback the speaker is a spectator (Overwatch role). Clients play a spectator's voice without direction
  and show the speaker's nickname (`speaker_name`, default "Music") in the voice chat indicator, as for a talking
  player.
- For positional playback the speaker is a Tutorial whose body sits `speaker_depth` (2.2 m) below the sound source, so
  the floor hides it. Clients play its voice in 3D at its body.

Speakers are kept out of the game:

| Hidden or excluded | How |
| --- | --- |
| Client player list, RA player list, `players` console list | The speaker presents itself as the dedicated server's own player |
| Role assignment at round start, late join, respawn waves, deathmatch respawns, `forceclass` | Every role change not made by CarlModAudio is cancelled |
| Lobby player count, round-end team counts | Not counted (dedicated-server player; Overwatch and Tutorial are not counted by the game either) |
| AFK kick, other kicks | Cancelled |
| Damage (decontamination, warhead, SCPs, falling) | God mode |
| SCP-173 and SCP-096 "looking at" checks, tesla gates | Never triggered by a speaker |

What players can still notice is listed under [Limitations](#limitations).

## Requirements

- A Carl Mod dedicated server (tested with game version 0.0.4) with
  [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile) 1.1.7-mobile.3 or later.
- Players need nothing: the stock Carl Mod client plays the audio.

## Install

1. Install LabAPI-Mobile and start the server once.
2. Stop the server. From the [release](https://github.com/Michaelihc/carlmod-audio/releases) archive copy
   - `plugins\CarlModAudio.dll` into `<AppData>\SCP Secret Laboratory\LabAPI-Mobile\plugins\global\`
   - `dependencies\NVorbis.dll` into `<AppData>\SCP Secret Laboratory\LabAPI-Mobile\dependencies\global\`
3. Start the server. It creates the audio folder
   `<AppData>\SCP Secret Laboratory\LabAPI-Mobile\configs\global\CarlModAudio\audio\` and the config
   `LabAPI-Mobile\configs\<port>\CarlModAudio\config.yml`.
4. Put `.ogg` or `.wav` files into the audio folder (the archive's `audio\chime.wav` is a short test sound). New files
   can be added while the server runs.

`<AppData>` is `%APPDATA%` of the user running the server, or the server's own `AppData` folder when its
`hoster_policy.txt` contains `gamedir_for_configs: true`.

## Commands

`audio` (alias `au`) works in the server console and in Remote Admin; in the in-game console type it with a leading
`/`. It needs the RA permission in `command_permission` (default `Broadcasting`).

| Command | What it does |
| --- | --- |
| `audio play <file> [where] [options]` | Plays a file. Without `id=` it creates a new player that removes itself when it finishes. |
| `audio queue <file> [id]` | Adds a file to a player's queue. |
| `audio stop [id\|all]` | Stops and removes a player, or all. |
| `audio pause [id]`, `audio resume [id]`, `audio skip [id]` | Pause, continue, next file in the queue. |
| `audio volume <0-200> [id]` | Volume in percent. |
| `audio loop <on\|off> [id]` | Repeat the current file. |
| `audio move <where> [id]` | Moves the sound (switches between global and positional while playing). |
| `audio list` | Files in the audio folder. |
| `audio status` | Players, what they play, speakers, server cost. |

`<file>` is a name in the audio folder, with or without `.ogg`/`.wav`; subfolders are allowed. `[id]` can be left out
while only one player exists.

`where`: `global` (default), `here` (your position), `at <x> <y> <z>`, or `on <player ID>` (follows the player).

Options for `play`: `loop`, `vol=<0-200>`, `id=<name>` (name the player, or replace what an existing player plays),
`to=<player ID>,<player ID>,...` (only these players hear it).

Examples:

```
audio play lobby.ogg loop vol=60 id=music
audio play alarm.wav at 12.5 1 -40
audio play radio on 7
audio play announcement.wav to=3,5
audio volume 30 music
audio stop all
```

A file that is not decoded yet is decoded in the background; the command answers at once and again when the file is
ready. Decoded files are kept in memory (`clip_cache_mb`).

## Configuration

`LabAPI-Mobile\configs\<port>\CarlModAudio\config.yml`. Restart the server after editing it.

| Key | Default | Meaning |
| --- | --- | --- |
| `audio_folder` | `audio` | Folder the command plays from. Relative to `LabAPI-Mobile\configs\global\CarlModAudio`. |
| `speaker_name` | `Music` | Nickname of the speakers, shown in the voice chat indicator during global playback. |
| `max_players` | `4` | Most audio players at once (1-16). Each playing one uses one speaker. |
| `default_volume` | `100` | Volume of new players in percent. |
| `headroom_db` | `18` | Attenuation of every file. See [Volume](#volume). |
| `bitrate` | `64000` | Opus bitrate (8000-128000). Each listener receives about this much per playing player. |
| `buffer_ms` | `150` | Audio sent ahead of real time (20-400). Absorbs network hiccups; pause and stop take this long to be heard. |
| `speaker_depth` | `2.2` | Metres below the sound source where a positional speaker's body is put. `0` shows it standing there. |
| `clip_cache_mb` | `128` | Memory for decoded files. One minute takes 5.5 MB. |
| `max_clip_minutes` | `20` | Longest file that is decoded (1-60). |
| `command_permission` | `Broadcasting` | RA permission the `audio` command needs. |

### Volume

The client amplifies voice chat by about 17 dB and then limits it, so that quiet microphones are audible. A normally
mastered music file sent unchanged would be pushed far into that limiter and sound flat. CarlModAudio therefore lowers
every file by `headroom_db` (18 dB): at volume 100 a file that peaks at full scale stays just below the client's limit.
Lower volumes are proportionally quieter; volumes above 100 are limited by the client and sound denser rather than much
louder. The player's own device volume (media volume) applies on top.

## For plugin developers

Reference `CarlModAudio.dll` (with `Private="false"`) and require the plugin to be installed. Use the API from the main
thread.

```csharp
using CarlModAudio;

// Lobby music: a player that loops one file for everyone.
AudioPlayer music = AudioPlayer.Create("lobby");
music.Loop = true;
music.Volume = 0.6f;
music.Play("lobby.ogg");             // name in the audio folder, or a full path; decoded in the background

// A sound at a position, heard only by one team.
AudioPlayer alarm = AudioPlayer.Create();
alarm.SetPosition(new Vector3(12.5f, 1f, -40f));
alarm.ReceiverFilter = player => player.Team == Team.FoundationForces;
alarm.DestroyWhenStopped = true;
alarm.ClipFinished += (p, clip) => Logger.Info($"{clip.Name} finished");
alarm.Play("alarm.wav");

// Generated audio.
float[] tone = new float[48000];
for (int i = 0; i < tone.Length; i++)
    tone[i] = 0.5f * Mathf.Sin(2 * Mathf.PI * 440 * i / 48000f);
AudioPlayer.Create().Play(AudioClipData.FromPcm(tone, 48000));
```

| Member | |
| --- | --- |
| `AudioPlayer.Create(name)`, `TryGet(name, out player)`, `List` | Create (throws when `max_players` exist), find, list. |
| `Play(clip / path)`, `Enqueue(clip / path)`, `Skip()`, `Pause()`, `Resume()`, `Stop()`, `Destroy()` | Playback. `Play` replaces the current file and clears the queue. |
| `SetGlobal()`, `SetPosition(Vector3)`, `AttachTo(Player)`, `Mode`, `Position`, `AttachedTo` | Where it is heard. |
| `Volume` (0-2), `Loop`, `ReceiverFilter`, `SpeakerName`, `DestroyWhenStopped` | Settings. |
| `State`, `CurrentClip`, `CurrentName`, `Time`, `QueueCount`, `IsDestroyed`, `SpeakerPlayer` | State. |
| `ClipStarted`, `ClipFinished`, `Stopped` | Events, raised from the next server update. |
| `AudioPlayer.IsSpeaker(player)` | True for speakers; skip them when you count or list players. |
| `AudioClipData.LoadAsync(path, callback)`, `Load(path)`, `FromPcm(samples, rate, channels)` | Clips (48 kHz mono, shared between players). |

A round restart destroys every audio player; create new ones afterwards (for example in
`ServerEvents.WaitingForPlayers`). Speakers appear in `Player.List` with `IsDummy` true.

## Cost

Measured on a local server with the Carl Mod 0.0.4 client in an Android emulator:

- Server: about 4 ms of main-thread time per second for each playing player while it streams to a listener (encoding
  included), about 0.07 ms per server frame at 60 fps. Four players at once: 16 ms per second; the server stayed at
  60 fps. Decoding runs on worker threads (a 1-minute stereo 44.1 kHz file takes about 0.1 s on a desktop CPU).
  Streaming allocates nothing per frame. Without audio players the plugin only counts server frames.
- Network: about 70 kbit/s per playing player for each listener at the default bitrate. Positional audio is sent only
  to players within hearing range.
- Client: no measurable frame-rate change (56.6 and 57.6 fps idle, 56.6 and 54.9 fps while playing; run-to-run spread
  is about ±4 fps).

## Limitations

- **The speaker's nickname** shows in the voice chat indicator of every player during global playback (that is how the
  client shows any talking spectator). Choose `speaker_name` accordingly.
- **Positional speakers have a body.** It is under the floor, but players can see it from below, through floor gaps or
  in shafts. Spectators can select it in the spectator list (it shows as a Tutorial named `speaker_name`) and then
  watch from under the floor. Global speakers have no body and are not in the spectator list.
- Speakers occupy player IDs and network slots, and other plugins see them in `Player.List` (use
  `AudioPlayer.IsSpeaker`). The `players` console command still counts them in its header line. Admins with the
  `GameplayData` permission see a global speaker as Overwatch in their own client.
- Positional sound fades like voice chat: in tests about 8 dB quieter at 5 m than at 1 m, 17 dB at 10 m, 30 dB at
  15 m, and inaudible beyond about 20 m. The client places a player only within the game's visibility range (about 33 m, 70 m on the surface), so
  nothing further away can be heard.
- Playback starts about 0.3 s after the speaker is created, and sound reaches players `buffer_ms` plus network delay
  after it is sent.
- Each playing player uses its own speaker; `max_players` (at most 16) caps them.
- MP3, Opus and FLAC files are not supported; convert them to Ogg Vorbis.
- Tested with Carl Mod 0.0.4 (server and Android client). On startup the plugin checks the game members it needs; on a
  build where they differ it logs why playback is unavailable instead of failing in the game.

## Building

```powershell
.\tools\Get-LabApiMobile.ps1        # downloads the LabAPI-Mobile release into .runtime\refs
dotnet build src\CarlModAudio\CarlModAudio.csproj -c Release -p:CarlManaged="<server>\Carl Mod_Data\Managed"
.\tools\Package.ps1 -CarlManaged "<server>\Carl Mod_Data\Managed"   # release archive in dist\
```

`CarlManaged` is the `Carl Mod_Data\Managed` folder of a Carl Mod 0.0.4 server; by default the build looks in a sibling
[labapimobile](https://github.com/Michaelihc/labapimobile) checkout's `.runtime\server-original`. `docs/testing.md`
describes how playback was tested on the Android client.

## Licence

MIT, see [LICENSE](LICENSE). NVorbis (MIT) is bundled in the release; LabAPI-Mobile (LGPL-3.0) is a separate runtime
dependency. See [NOTICE.md](NOTICE.md). Not affiliated with Northwood Studios or Carl Mod.
