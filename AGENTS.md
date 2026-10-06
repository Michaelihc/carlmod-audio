# CarlModAudio

Follow the parent workspace instructions. Reply in English unless requested otherwise.

LabAPI-Mobile plugin for the Carl Mod server (mobile SCP:SL fork, ~SL 13.1-13.2 game code) that plays audio files to
players through the game's voice chat. Server-side only; the Android client is IL2CPP and is never patched.

## Layout

- `src/CarlModAudio/` — the plugin. `AudioPlayer.cs` (public API, pacing, encoding, sending), `AudioClipData.cs`
  (clips, loading), `Decoding/` (WAV, Ogg via NVorbis, resampler), `Internal/Speaker.cs` (speaker dummies),
  `Internal/SpeakerRegistry.cs` and `SpeakerPatches.cs` (keeping speakers out of round logic), `Commands/`.
- `tools/Package.ps1`, `tools/package/` — release archive in `dist/`. `tools/Get-LabApiMobile.ps1` — build references.
- `tools/test/` — test signals, capture analysis, `DecoderCheck`, `AudioTestKit` (developer plugin, never packaged).
- `docs/testing.md` — how playback is tested on the Android client.
- `README.md` / `README.zh-CN.md` — keep both in sync; commands, file names and config keys in English.
- `.runtime/` (ignored) — references, test server, captures, logs.

## Build and test

Builds against a LabAPI-Mobile release (`LabApiDir`) and the Carl Mod server's Managed folder (`CarlManaged`); see the
README. Read `../labapimobile/AGENTS.md` for the performance rules and test tooling; do not edit that repository from
here. Use your own server copy under `.runtime/` and port 7791 unless told otherwise.

## Client facts this plugin relies on (Carl Mod 0.0.4)

- The client processes a `VoiceMessage` through the *speaker's* role on the client, not the listener's: a spectator
  role writes to its unpositioned global playback, a human role writes Proximity voice to a 3D source at its body.
- The opus decoder is per speaker, so each stream needs its own speaker.
- The client's voice buffer holds 0.5 s and is cleared on overflow; the plugin stays `buffer_ms` ahead.
- The client amplifies voice by about 17 dB and limits it; `headroom_db` compensates.
- Clients leave a player out of the player list only if its synced user ID is "ID_Dedicated" from the spawn message on.
