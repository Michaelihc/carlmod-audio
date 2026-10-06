# Testing on the Android client

CarlModAudio is tested against a local Carl Mod 0.0.4 server and the stock Carl Mod 0.0.4 client running in an Android
emulator. The server, emulator and client tooling come from
[labapimobile](https://github.com/Michaelihc/labapimobile) (`tools\`, `tools\android\`, see its `docs/testing.md`) or
[carlmod-testkit](https://github.com/Michaelihc/carlmod-testkit). Test files live in this repository's ignored
`.runtime\` folder.

## Setup

- A copy of the server with LabAPI-Mobile installed by its installer, `CarlModAudio.dll` in `plugins\global`,
  `NVorbis.dll` in `dependencies\global`, and two developer plugins: TestKitHelper from carlmod-testkit (mirrors the
  console into the Unity log, so file-console command output is visible; `testkit list` prints player positions) and
  `tools/test/AudioTestKit` (`atest role|kick|respawn`, which drive role changes, kicks and respawn waves through the
  game's own code).
- `python tools/test/make_test_audio.py` writes the test files into the test server's audio folder: one tone frequency
  per file, covering 16-bit 48 kHz, stereo 44.1 kHz, 24-bit 32 kHz, float 22.05 kHz, 96 kHz and Ogg Vorbis, plus a
  2-second beep track for the playback speed.
- `tools/test/DecoderCheck` compiles the plugin's decoders into a console program that writes the decoded 48 kHz files;
  it checks lengths, pitch, level and aliasing without a server.

Send commands with the file console (`Send-ServerCommand.ps1`) and keep `-WaitSec` at 1.2 s or more: the script deletes
command files the server has not picked up yet.

## Recording what the client plays

The emulator's scripts start it with `-no-audio`. For audio tests start the emulator yourself with the WAV backend,
which writes the guest's mixed output (44.1 kHz stereo) to a file:

```powershell
$env:QEMU_AUDIO_DRV = 'wav'; $env:QEMU_WAV_PATH = "$PWD\.runtime\capture.wav"
& "$env:LOCALAPPDATA\Android\Sdk\emulator\emulator.exe" -avd carlmod_api36 -port 5554 -accel on -gpu host `
    -no-boot-anim -netdelay none -netspeed full -no-snapshot
```

Set the media volume to the maximum (`adb shell input keyevent KEYCODE_VOLUME_UP` repeatedly while the game runs;
`adb shell cmd media_session volume --stream 3 --get` shows it). The file is locked until the emulator exits, so a test session logs each step as
`HH:MM:SS.fff STEP <label> :: <commands>`, then stops the emulator, and

```powershell
python tools/test/analyze_capture.py .runtime/capture.wav .runtime/steps.txt --end <time the emulator was stopped> --beeps speed-beeps
```

prints, per step, the level of every test tone and the broadband level; `--beeps` measures the interval between beep
onsets.

## What is checked

- Global playback of every format arrives at the right pitch; silence before, after `stop`, during `pause` and at
  volume 0.
- Volume steps change the level proportionally (6 dB per halving) up to the client's limiter.
- Pacing: 2.000 s between beeps; no dropouts in two minutes of continuous playback (which bounds any clock mismatch
  with the 150 ms buffer).
- Positional playback: level against distance, following a player, switching to global while playing.
- Two and four simultaneous streams, `max_players`, `to=` receiver filtering, queue and skip, loop.
- Speakers on the client: the voice chat indicator shows the speaker name; the player list does not list speakers
  (screenshots with `Capture.ps1`).
- Guards: `atest role` and `atest kick` on speakers are refused; a forced respawn wave takes only real spectators;
  `roundrestart` leaves no players or speakers; playback works in the lobby and with no client connected.
- Server log: no exceptions; `audio status` reports the audio time per second and the server frame rate.
- Client frame rate with `Measure-FrameTime.ps1`, idle and playing, interleaved.
