# CarlModAudio

[English](README.md)

一个 [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile) 插件，在 Carl Mod 服务器（SCP: Secret Laboratory
的手机版分支）上向玩家播放音频文件。它只在服务端运行：玩家使用原版 Carl Mod 安卓客户端，客户端通过语音聊天播放音频。

- **全局**播放：所有玩家听到的音量相同，类似广播（Intercom）。
- **位置**播放：声音来自设施中的某个位置，或跟随某个玩家，随距离衰减。
- 支持 WAV（8、16、24、32 位 PCM 以及 32/64 位浮点）和 Ogg Vorbis 文件，任意采样率，单声道或立体声。
- 播放、排队、跳过、停止、暂停、继续、音量、循环；可同时运行多个播放器；可只让指定玩家听到。
- 管理员使用 `audio` 命令，其他插件使用 `AudioPlayer` API。

## 工作原理

客户端会播放玩家发出的语音。每个正在播放的音频播放器，CarlModAudio 都会生成一个隐藏的假人玩家（*扬声器*），用游戏自带的
Opus 编码器编码文件，并以这个扬声器的语音实时发送，每次 10 毫秒。

- 全局播放时，扬声器是观察者（Overwatch 角色）。客户端播放观察者语音时没有方向，并像显示正在说话的玩家一样，在语音指示器里
  显示扬声器的昵称（`speaker_name`，默认 “Music”）。
- 位置播放时，扬声器是教程角色（Tutorial），身体位于声源下方 `speaker_depth`（2.2 米）处，由地板挡住。客户端在其身体处以
  3D 方式播放语音。

扬声器不会影响游戏：

| 隐藏或排除 | 方式 |
| --- | --- |
| 客户端玩家列表、RA 玩家列表、控制台 `players` 列表 | 扬声器以专用服务器自身玩家的身份出现 |
| 回合开始分配角色、中途加入、刷新波次、死斗模式重生、`forceclass` | 取消一切非 CarlModAudio 发起的角色变更 |
| 大厅人数、回合结束时的阵营统计 | 不计入（专用服务器玩家；游戏本身也不统计观察者和教程角色） |
| 挂机踢出、其他踢出、封禁 | 取消 |
| 伤害（去污、核弹、SCP、摔落） | 无敌模式 |
| SCP-173 和 SCP-096 的“被注视”判定、电网门 | 扬声器永远不会触发 |

玩家仍可能注意到的内容见[限制](#限制)。

## 环境要求

- Carl Mod 专用服务器，游戏版本 0.0.4 或 0.0.5，并安装 [LabAPI-Mobile](https://github.com/Michaelihc/labapimobile)：
  0.0.4 需要 1.1.7-mobile.3 或更新版本，0.0.5 需要 1.1.7-mobile.5 或更新版本。
- 玩家无需安装任何东西：原版 Carl Mod 客户端即可播放。

## 安装

1. 安装 LabAPI-Mobile，并启动一次服务器。
2. 停止服务器。从[发布页](https://github.com/Michaelihc/carlmod-audio/releases)的压缩包中复制
   - `plugins\CarlModAudio.dll` 到 `<AppData>\SCP Secret Laboratory\LabAPI-Mobile\plugins\global\`
   - `dependencies\NVorbis.dll` 到 `<AppData>\SCP Secret Laboratory\LabAPI-Mobile\dependencies\global\`
3. 启动服务器。它会创建音频文件夹
   `<AppData>\SCP Secret Laboratory\LabAPI-Mobile\configs\global\CarlModAudio\audio\` 和配置文件
   `LabAPI-Mobile\configs\<端口>\CarlModAudio\config.yml`。
4. 把 `.ogg` 或 `.wav` 文件放进音频文件夹（压缩包里的 `audio\chime.wav` 是一段简短的测试音）。服务器运行时也可以添加新文件。

`<AppData>` 是运行服务器的用户的 `%APPDATA%`；如果服务器的 `hoster_policy.txt` 包含 `gamedir_for_configs: true`，
则是服务器目录下的 `AppData` 文件夹。

## 命令

`audio`（别名 `au`）可在服务器控制台和远程管理（RA）中使用；在游戏内控制台中输入时前面加 `/`。需要 `command_permission`
中设定的 RA 权限（默认 `Broadcasting`）。

| 命令 | 作用 |
| --- | --- |
| `audio play <文件> [位置] [选项]` | 播放文件。不带 `id=` 时创建一个新播放器，播放结束后自动移除。 |
| `audio queue <文件> [id]` | 把文件加入播放器的队列。 |
| `audio stop [id\|all]` | 停止并移除一个播放器，或全部。 |
| `audio pause [id]`、`audio resume [id]`、`audio skip [id]` | 暂停、继续、播放队列中的下一个文件。 |
| `audio volume <0-200> [id]` | 音量（百分比）。 |
| `audio loop <on\|off> [id]` | 循环当前文件。 |
| `audio move <位置> [id]` | 移动声源（播放中可在全局和位置播放之间切换）。 |
| `audio list` | 音频文件夹中的文件。 |
| `audio status` | 播放器、正在播放的内容、扬声器、服务器开销。 |

`<文件>` 是音频文件夹中的文件名，可带或不带 `.ogg`/`.wav`，可以包含子文件夹。只有一个播放器时可以省略 `[id]`。

`位置`：`global`（默认）、`here`（你所在的位置）、`at <x> <y> <z>`，或 `on <玩家 ID>`（跟随该玩家）。

`play` 的选项：`loop`、`vol=<0-200>`、`id=<名称>`（为播放器命名，或替换已有播放器正在播放的内容）、
`to=<玩家 ID>,<玩家 ID>,...`（只有这些玩家能听到）。

示例：

```
audio play lobby.ogg loop vol=60 id=music
audio play alarm.wav at 12.5 1 -40
audio play radio on 7
audio play announcement.wav to=3,5
audio volume 30 music
audio stop all
```

尚未解码的文件会在后台解码；命令会立即回复，文件就绪后再回复一次。解码后的文件保存在内存中（`clip_cache_mb`）。

## 配置

`LabAPI-Mobile\configs\<端口>\CarlModAudio\config.yml`。修改后重启服务器。

| 键 | 默认值 | 含义 |
| --- | --- | --- |
| `audio_folder` | `audio` | 命令播放的文件夹。相对路径从 `LabAPI-Mobile\configs\global\CarlModAudio` 开始。 |
| `speaker_name` | `Music` | 扬声器的昵称，全局播放时显示在语音指示器中。 |
| `max_players` | `4` | 同时存在的音频播放器上限（1-16）。每个正在播放的播放器占用一个扬声器。 |
| `default_volume` | `100` | 新播放器的音量（百分比）。 |
| `headroom_db` | `18` | 对所有文件的衰减。见[音量](#音量)。 |
| `bitrate` | `64000` | Opus 码率（8000-128000）。每个正在播放的播放器，每位听众大约接收这么多数据。 |
| `buffer_ms` | `150` | 提前于实时发送的音频（20-400）。可吸收网络波动；暂停和停止要过这么久才会被听到。 |
| `speaker_depth` | `2.2` | 位置播放时扬声器身体位于声源下方的米数。`0` 表示让它站在声源处。 |
| `clip_cache_mb` | `128` | 解码后文件占用的内存。一分钟约 5.5 MB。 |
| `max_clip_minutes` | `20` | 解码的最长文件（1-60 分钟）。 |
| `command_permission` | `Broadcasting` | `audio` 命令需要的 RA 权限。 |

### 音量

客户端会把语音放大约 17 dB 再进行限幅，以便听清音量小的麦克风。正常母带处理的音乐如果原样发送，会被深深推进限幅器，听起来很扁平。
因此 CarlModAudio 会把所有文件降低 `headroom_db`（18 dB）：音量 100 时，峰值达到满幅的文件刚好低于客户端的限幅。
更低的音量按比例变小；高于 100 的音量会被客户端限幅，听起来更“密”而不是明显更响。玩家设备本身的媒体音量也会叠加生效。

## 插件开发者

引用 `CarlModAudio.dll`（`Private="false"`），并要求服务器安装本插件。请在主线程中使用 API。

```csharp
using CarlModAudio;

// 大厅音乐：一个对所有人循环播放某个文件的播放器。
AudioPlayer music = AudioPlayer.Create("lobby");
music.Loop = true;
music.Volume = 0.6f;
music.Play("lobby.ogg");             // 音频文件夹中的文件名或完整路径；在后台解码

// 某个位置的声音，只有一个阵营能听到。
AudioPlayer alarm = AudioPlayer.Create();
alarm.SetPosition(new Vector3(12.5f, 1f, -40f));
alarm.ReceiverFilter = player => player.Team == Team.FoundationForces;
alarm.DestroyWhenStopped = true;
alarm.ClipFinished += (p, clip) => Logger.Info($"{clip.Name} finished");
alarm.Play("alarm.wav");

// 程序生成的音频。
float[] tone = new float[48000];
for (int i = 0; i < tone.Length; i++)
    tone[i] = 0.5f * Mathf.Sin(2 * Mathf.PI * 440 * i / 48000f);
AudioPlayer.Create().Play(AudioClipData.FromPcm(tone, 48000));
```

| 成员 | |
| --- | --- |
| `AudioPlayer.Create(name)`、`TryGet(name, out player)`、`List` | 创建（已有 `max_players` 个时抛出异常）、查找、列出。 |
| `Play(clip / path)`、`Enqueue(clip / path)`、`Skip()`、`Pause()`、`Resume()`、`Stop()`、`Destroy()` | 播放控制。`Play` 替换当前文件并清空队列。 |
| `SetGlobal()`、`SetPosition(Vector3)`、`AttachTo(Player)`、`Mode`、`Position`、`AttachedTo` | 在哪里听到。 |
| `Volume`（0-2）、`Loop`、`ReceiverFilter`、`SpeakerName`、`DestroyWhenStopped` | 设置。 |
| `State`、`CurrentClip`、`CurrentName`、`Time`、`QueueCount`、`IsDestroyed`、`SpeakerPlayer` | 状态。 |
| `ClipStarted`、`ClipFinished`、`Stopped` | 事件，在下一次服务器更新时触发。 |
| `AudioPlayer.IsSpeaker(player)` | 对扬声器返回 true；统计或列出玩家时请跳过它们。 |
| `AudioClipData.LoadAsync(path, callback)`、`Load(path)`、`FromPcm(samples, rate, channels)` | 音频片段（48 kHz 单声道，可在播放器之间共享）。 |

回合重启会销毁所有音频播放器；之后需要重新创建（例如在 `ServerEvents.WaitingForPlayers` 中）。扬声器会出现在 `Player.List`
中，其 `IsDummy` 为 true。

## 开销

在本地服务器上测得，客户端为安卓模拟器中的 Carl Mod 0.0.4（0.0.5 服务端的数据相同）：

- 服务端：每个正在向听众推流的播放器约占每秒 4 毫秒主线程时间（含编码），即 60 fps 时每帧约 0.07 毫秒。同时四个播放器：
  每秒 16 毫秒；服务器保持 60 fps。解码在工作线程中进行（1 分钟 44.1 kHz 立体声文件在台式机 CPU 上约 0.1 秒）。
  推流过程每帧不分配内存。没有音频播放器时，插件只统计服务器帧数。
- 网络：默认码率下，每个正在播放的播放器对每位听众约 70 kbit/s。位置播放的音频只发送给在听觉范围内的玩家。
- 客户端：帧率无可测变化（空闲时 56.6 和 57.6 fps，播放时 56.6 和 54.9 fps；每次测量之间的波动约为 ±4 fps）。

## 限制

- 全局播放时，**扬声器的昵称**会显示在每位玩家的语音指示器中（客户端对任何正在说话的观察者都这样显示）。请据此设置
  `speaker_name`。
- **位置播放的扬声器有身体。** 它在地板下方，但玩家可能从下方、地板缝隙或竖井中看到它。观察者可以在观察列表中选中它
  （显示为名为 `speaker_name` 的教程角色），然后从地板下方观看。全局扬声器没有身体，也不在观察列表中。
- 扬声器会占用玩家 ID 和网络连接位置，其他插件会在 `Player.List` 中看到它们（请使用 `AudioPlayer.IsSpeaker`）。
  控制台 `players` 命令的标题行仍会把它们计入人数。拥有 `GameplayData` 权限的管理员在自己的客户端中会看到全局扬声器是观察者
  （Overwatch）。
- 位置播放的声音像语音聊天一样衰减：测试中 5 米处比 1 米处低约 8 dB，10 米处低 17 dB，15 米处低 30 dB，约 20 米以外听不到。
  客户端只在游戏的可见范围内（约 33 米，地表 70 米）放置玩家，更远的地方听不到任何声音。
- 扬声器生成约 0.3 秒后才开始播放；声音在发送后经过 `buffer_ms` 加上网络延迟到达玩家。
- 每个正在播放的播放器使用各自的扬声器；`max_players`（最多 16）限制其数量。
- 不支持 MP3、Opus 和 FLAC 文件；请转换为 Ogg Vorbis。
- 已用 Carl Mod 0.0.4（服务端和安卓客户端）和 0.0.5 测试。0.0.5 上已验证服务端（扬声器保持隐藏、不参与回合逻辑，每位玩家都以正确的
  速率收到音频帧），但尚未在真实的 0.0.5 客户端上确认播放效果；其语音聊天代码和消息与 0.0.4 相同。插件启动时会检查所需的游戏成员；
  如果游戏版本不同，它会在日志中说明无法播放的原因，而不会在游戏中出错。

## 构建

```powershell
.\tools\Get-LabApiMobile.ps1        # 下载 LabAPI-Mobile 发布包到 .runtime\refs
dotnet build src\CarlModAudio\CarlModAudio.csproj -c Release -p:CarlManaged="<服务端>\Carl Mod_Data\Managed"
.\tools\Package.ps1 -CarlManaged "<服务端>\Carl Mod_Data\Managed"   # 发布压缩包输出到 dist\
```

`CarlManaged` 是 Carl Mod 0.0.4 服务端的 `Carl Mod_Data\Managed` 文件夹（发布版本基于 0.0.4 构建，也可在 0.0.5 上运行）；
默认使用同级目录
[labapimobile](https://github.com/Michaelihc/labapimobile) 检出中的 `.runtime\server-original`。`docs/testing.md`
（英文）说明了如何在安卓客户端上以及仅在服务端测试播放。

## 许可

MIT，见 [LICENSE](LICENSE)。发布包中附带 NVorbis（MIT）；LabAPI-Mobile（LGPL-3.0）是单独安装的运行时依赖。见
[NOTICE.md](NOTICE.md)。本项目与 Northwood Studios 及 Carl Mod 无关。
