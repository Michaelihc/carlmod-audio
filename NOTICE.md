# Notice

CarlModAudio is licensed under the MIT licence in [LICENSE](LICENSE).

## Third-party components

- **NVorbis** 0.10.5, <https://github.com/NVorbis/NVorbis>, Copyright (c) 2020 Andrew Ward, MIT licence
  ([licenses/NVorbis-MIT.txt](licenses/NVorbis-MIT.txt)). Decodes Ogg Vorbis files. The release archive ships its
  unmodified `NVorbis.dll`.

## Runtime dependencies (not included)

- **LabAPI-Mobile**, <https://github.com/Michaelihc/labapimobile>, the plugin framework CarlModAudio runs on. It is a
  modified version of Northwood Studios' LabAPI and is licensed under the LGPL-3.0; it is installed separately and is
  not part of this repository or its release archives. CarlModAudio also uses Harmony (`0Harmony.dll`, MIT), which
  LabAPI-Mobile installs.
- The game's own Opus library (`libopus-0.dll` in the server's `Carl Mod_Data\Plugins\x86_64`) encodes the audio.

## Game

SCP: Secret Laboratory is a game by Northwood Studios. Carl Mod is a third-party mobile version of it. This project is
not affiliated with either. The repository and its release archives contain no game files.

## Sample audio

`samples/chime.wav` is synthesized by `tools/make_chime.py` (part of this repository, MIT).
