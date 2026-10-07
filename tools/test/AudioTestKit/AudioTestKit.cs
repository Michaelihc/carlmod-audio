using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CarlModAudio;
using CommandSystem;
using CommandSystem.Commands.Console;
using HarmonyLib;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using RelativePositioning;
using RemoteAdmin;
using RemoteAdmin.Communication;
using Respawning;
using RoundRestarting;
using UnityEngine;
using VoiceChat;
using VoiceChat.Networking;

namespace AudioTestKit;

public sealed class AudioTestKitPlugin : Plugin
{
    private Harmony? _harmony;

    public override string Name => "AudioTestKit";

    public override string Description => "Local test helper for CarlModAudio.";

    public override string Author => "carlmod-audio";

    public override Version Version => new(1, 1, 0);

    public override Version RequiredApiVersion => new(1, 0, 0);

    public override void Enable()
    {
        _harmony = new Harmony("carlmodaudio.audiotestkit");
        MethodInfo? spawn = AccessTools.Method(typeof(NetworkServer), "SendSpawnMessage");
        if (spawn != null)
            _harmony.Patch(spawn, prefix: new HarmonyMethod(typeof(SpawnWatch), nameof(SpawnWatch.Prefix)));

        // With the file console, console output (LabAPI's log included) reaches no log file; mirror it.
        MethodInfo? addLog = AccessTools.Method(typeof(ServerConsole), nameof(ServerConsole.AddLog), [typeof(string), typeof(ConsoleColor)]);
        if (addLog != null)
            _harmony.Patch(addLog, prefix: new HarmonyMethod(typeof(TestLog), nameof(TestLog.Console)));

        TestLog.Write($"AudioTestKit enabled; SendSpawnMessage hook {(spawn != null ? "on" : "MISSING")}, console mirror {(addLog != null ? "on" : "MISSING")}");
    }

    public override void Disable()
    {
        _harmony?.UnpatchAll("carlmodaudio.audiotestkit");
        Listeners.RemoveAll();
    }
}

/// <summary>Command output goes to atest-&lt;port&gt;.log in the server's working directory (the file console does not return it).</summary>
internal static class TestLog
{
    private static string? _path;

    public static void Write(string text)
    {
        _path ??= Path.Combine(Environment.CurrentDirectory, $"atest-{ServerStatic.ServerPort}.log");
        File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {text}\n");
    }

    public static void Console(string q) => Write("[console] " + q);
}

/// <summary>Records the synced user ID that each spawn message carries to a listener.</summary>
internal static class SpawnWatch
{
    public static readonly List<string> Records = [];

    public static void Prefix(NetworkIdentity identity, NetworkConnectionToClient conn)
    {
        if (conn is not ListenerConnection || identity == null || !identity.TryGetComponent(out ReferenceHub hub))
            return;

        string kind = AudioPlayer.IsSpeaker(hub) ? "speaker" : hub.connectionToClient is ServerDummyConnection ? "dummy" : hub.connectionToClient is ListenerConnection ? "listener" : "other";
        Records.Add($"spawn msg to conn {((ListenerConnection)conn).connectionId}: player {hub.PlayerId} ({kind}) syncedUserId={hub.characterClassManager.SyncedUserId ?? "(null)"}");
    }
}

/// <summary>Per speaker, what one listener connection received.</summary>
internal sealed class StreamStats
{
    private static readonly double Freq = Stopwatch.Frequency;

    public string Name = "?";
    public long Frames;
    public long PayloadBytes;
    public long WireBytes;
    public long First;
    public long Last;
    public int FirstFlushFrames;
    public double MaxGapMs;
    public int Gaps100;
    public double MinLead = double.MaxValue;
    public double MaxLead = double.MinValue;
    public readonly Dictionary<VoiceChatChannel, long> Channels = [];
    private long _flush;

    public void Add(long now, VoiceChatChannel channel, int payload, int wire)
    {
        if (First == 0)
        {
            First = now;
            _flush = now;
        }

        if (now != _flush)
        {
            CloseFlush();
            double gap = (now - _flush) * 1000.0 / Freq;
            MaxGapMs = Math.Max(MaxGapMs, gap);
            if (gap > 100)
                Gaps100++;
            _flush = now;
        }

        if (now == First)
            FirstFlushFrames++;

        Frames++;
        PayloadBytes += payload;
        WireBytes += wire;
        Last = now;
        Channels[channel] = Channels.TryGetValue(channel, out long c) ? c + 1 : 1;
    }

    // Frames received minus frames due in real time since the first one (100 per second): the buffer-ahead.
    private void CloseFlush()
    {
        double elapsed = (_flush - First) / Freq;
        if (elapsed < 1.0)
            return;

        double lead = Frames - (elapsed * 100.0);
        MinLead = Math.Min(MinLead, lead);
        MaxLead = Math.Max(MaxLead, lead);
    }

    public string Report()
    {
        double seconds = (Last - First) / Freq;
        double rate = seconds > 0 ? (Frames - FirstFlushFrames) / seconds : 0;
        double kbps = seconds > 0 ? PayloadBytes * 8 / seconds / 1000 : 0;
        string channels = string.Join(",", Channels.Select(kv => $"{kv.Key}:{kv.Value}"));
        string lead = MinLead == double.MaxValue ? "n/a" : $"{MinLead:F1}..{MaxLead:F1}";
        return $"{Name}: frames={Frames} payload={PayloadBytes}B ({(Frames > 0 ? PayloadBytes / (double)Frames : 0):F1}B/frame, {kbps:F1} kbit/s) wire={WireBytes}B "
            + $"span={seconds:F2}s rate={rate:F1}/s firstFlush={FirstFlushFrames} lead={lead} maxGap={MaxGapMs:F0}ms gaps>100ms={Gaps100} ch={channels}";
    }
}

/// <summary>
/// A connection without a client that counts what the server sends it. Its player is a ready client, so CarlModAudio
/// sends it audio like a real player (ServerDummyConnection players are skipped as listeners).
/// </summary>
internal sealed class ListenerConnection : NetworkConnectionToClient
{
    private static readonly ushort VoiceId = NetworkMessages.GetId<VoiceMessage>();
    private static int _checkedFrame = -1;

    private bool _disconnected;

    public ListenerConnection(int id)
        : base(id, "atest-listener")
    {
        isAuthenticated = true;
    }

    public static long FlushChecks { get; set; }

    public static List<string> FlushViolations { get; } = [];

    public long TransportBytes { get; set; }

    public long Batches { get; set; }

    public long ParseErrors { get; set; }

    public Dictionary<int, StreamStats> Streams { get; } = [];

    public void Reset()
    {
        TransportBytes = 0;
        Batches = 0;
        ParseErrors = 0;
        Streams.Clear();
    }

    public override void Disconnect()
    {
        if (_disconnected)
            return;

        _disconnected = true;
        isReady = false;
        Cleanup();
        NetworkServer.RemoveConnection(connectionId);
        Listeners.ForgetUserId(connectionId);
        if (NetworkServer.active && NetworkManager.singleton != null)
            NetworkManager.singleton.OnServerDisconnect(this);
        else
            NetworkServer.DestroyPlayerForConnection(this);
    }

    protected override void SendToTransport(ArraySegment<byte> segment, int channelId = 0)
    {
        CheckSpeakersAtFlush();
        TransportBytes += segment.Count;
        Batches++;
        try
        {
            Parse(segment);
        }
        catch (Exception)
        {
            ParseErrors++;
        }
    }

    protected override void UpdatePing()
    {
    }

    // Batch: 8-byte timestamp, then per message a varuint size and the message (ushort id + payload).
    private void Parse(ArraySegment<byte> segment)
    {
        long now = Stopwatch.GetTimestamp();
        var reader = new NetworkReader(segment);
        reader.ReadDouble();
        while (reader.Remaining > 0)
        {
            int size = (int)Compression.DecompressVarUInt(reader);
            ArraySegment<byte> message = reader.ReadBytesSegment(size);
            var r = new NetworkReader(message);
            if (r.ReadUShort() != VoiceId)
                continue;

            int speaker = r.ReadRecyclablePlayerId().Value;
            var channel = (VoiceChatChannel)r.ReadByte();
            int length = r.ReadUShort();
            if (!Streams.TryGetValue(speaker, out StreamStats? stats))
            {
                Streams[speaker] = stats = new StreamStats();
                AudioPlayer? player = AudioPlayer.List.FirstOrDefault(p => p.SpeakerPlayer?.PlayerId == speaker);
                stats.Name = $"{player?.Name ?? "?"}#{speaker}";
            }

            stats.Add(now, channel, length, size + Compression.VarUIntSize((ulong)size));
        }
    }

    // Once per frame, while the batches go out: every speaker must still present "ID_Dedicated".
    private static void CheckSpeakersAtFlush()
    {
        if (Time.frameCount == _checkedFrame)
            return;

        _checkedFrame = Time.frameCount;
        foreach (AudioPlayer player in AudioPlayer.List)
        {
            if (player.SpeakerPlayer is not { } speaker)
                continue;

            FlushChecks++;
            CharacterClassManager ccm = speaker.ReferenceHub.characterClassManager;
            if (ccm.SyncedUserId != "ID_Dedicated" && FlushViolations.Count < 50)
                FlushViolations.Add($"frame {Time.frameCount}: speaker {speaker.PlayerId} syncedUserId={ccm.SyncedUserId ?? "(null)"} mode={ccm.InstanceMode}");
        }
    }
}

/// <summary>Keeps a listener alive (as ServerDummy does for dummies) and removes it on a round restart.</summary>
internal sealed class ListenerDriver : MonoBehaviour
{
    public ReferenceHub? Hub;
    public ListenerConnection? Connection;

    private void LateUpdate()
    {
        if (!NetworkServer.active || Connection == null || Hub == null)
            return;

        Connection.lastMessageTime = Time.time;
        if (Hub.roleManager.CurrentRole is IFpcRole fpc && fpc.FpcModule.ModuleReady)
            fpc.FpcModule.Motor.ReceivedPosition = new RelativePosition(fpc.FpcModule.Position);
    }

    private void OnEnable() => RoundRestart.OnRestartTriggered += OnRestart;

    private void OnDisable() => RoundRestart.OnRestartTriggered -= OnRestart;

    private void OnRestart() => Connection?.Disconnect();
}

internal static class Listeners
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? UserIds = typeof(CustomLiteNetLib4MirrorTransport).GetField("ConnectedUserIds", Any);
    private static readonly FieldInfo? UserIdLock = typeof(CustomLiteNetLib4MirrorTransport).GetField("UserIdLock", Any);
    private static readonly FieldInfo? NicknameHub = typeof(NicknameSync).GetField("_hub", Any);
    private static int _serial;

    public static List<ListenerConnection> All { get; } = [];

    public static string Spawn()
    {
        int id = -100;
        while (NetworkServer.connections.ContainsKey(id))
            id--;

        int serial = ++_serial;
        var connection = new ListenerConnection(id);

        // CharacterClassManager.Init takes the user ID of a non-dummy connection from the transport.
        if (UserIds?.GetValue(null) is Dictionary<int, string> ids)
        {
            lock (UserIdLock?.GetValue(null) ?? ids)
                ids[id] = $"device_atestlistener{serial}@device";
        }

        GameObject go = UnityEngine.Object.Instantiate(NetworkManager.singleton.playerPrefab);
        ReferenceHub hub = go.GetComponent<ReferenceHub>();
        ListenerDriver driver = go.AddComponent<ListenerDriver>();
        driver.Hub = hub;
        driver.Connection = connection;
        NetworkServer.connections.Add(id, connection);
        if (!NetworkServer.AddPlayerForConnection(connection, go))
            throw new InvalidOperationException("AddPlayerForConnection failed");

        if (NicknameHub != null && NicknameHub.GetValue(hub.nicknameSync) == null)
            NicknameHub.SetValue(hub.nicknameSync, hub);

        hub.nicknameSync.UpdateNickname($"Listener {serial}");
        All.Add(connection);
        return $"listener {serial}: player {hub.PlayerId}, connection {id}";
    }

    public static void ForgetUserId(int id)
    {
        All.RemoveAll(c => c.connectionId == id);
        if (UserIds?.GetValue(null) is Dictionary<int, string> ids)
        {
            lock (UserIdLock?.GetValue(null) ?? ids)
                ids.Remove(id);
        }
    }

    public static void RemoveAll()
    {
        foreach (ListenerConnection c in All.ToArray())
            c.Disconnect();

        All.Clear();
    }
}

/// <summary>Captures an RA reply.</summary>
internal sealed class CaptureSender : CommandSender
{
    public string Reply = string.Empty;

    public override string SenderId => "atest";

    public override string Nickname => "atest";

    public override ulong Permissions => ulong.MaxValue;

    public override byte KickPower => byte.MaxValue;

    public override bool FullPermissions => true;

    public override void RaReply(string text, bool success, bool logToConsole, string overrideDisplay) => Reply += text;

    public override void Print(string text) => Reply += text;
}

/// <summary>
/// atest role &lt;id&gt; &lt;RoleTypeId&gt;   ServerSetRole through RemoteAdmin, like forceclass; prints the role after it
/// atest kick &lt;id&gt;                kick through BanPlayer.KickUser, like the AFK check
/// atest ban &lt;id&gt;                 ban for 60 s through BanPlayer.BanUser
/// atest respawn &lt;ntf|ci&gt;          force a respawn wave
/// atest listen [n]                 spawn n listener players whose connections count what they receive
/// atest unlisten                   remove the listeners
/// atest stats [reset]              per listener: bytes, and per speaker the voice frames, bytes, rate, lead, gaps
/// atest check                      players, connections, speakers, the players command, the RA list, spawn IDs
/// atest tp &lt;id&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt;        move a player
/// atest lock &lt;on|off&gt;             RoundSummary.RoundLock
/// atest nspawn &lt;role&gt;             native ServerDummy.Spawn
/// atest drop &lt;id&gt;                 disconnect a player's connection
/// atest ra &lt;command&gt;              run an RA command (forceclass, ban, ...) as a full-permission admin
/// An argument "speaker" stands for the player ID of the first speaker, L1..L9 for the listeners'. Output goes to atest-&lt;port&gt;.log in the server
/// folder.
/// </summary>
[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class AudioTestCommand : ICommand
{
    public string Command => "atest";

    public string[] Aliases => [];

    public string Description => "CarlModAudio test helper (local servers only).";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        // "speaker" stands for the player ID of the first audio player's speaker.
        string? speaker = AudioPlayer.List.Select(p => p.SpeakerPlayer?.PlayerId.ToString()).FirstOrDefault(id => id != null);
        string[] a = new string[arguments.Count];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = arguments.Array![arguments.Offset + i];
            if (a[i] == "speaker" && speaker != null)
                a[i] = speaker;
            else if (a[i].Length == 2 && a[i][0] == 'L' && char.IsDigit(a[i][1]) && a[i][1] - '1' < Listeners.All.Count
                && Listeners.All[a[i][1] - '1'].identity is { } listener)
                a[i] = listener.GetComponent<ReferenceHub>().PlayerId.ToString();
        }

        try
        {
            response = Run(a);
        }
        catch (Exception e)
        {
            response = $"atest {string.Join(" ", a)} threw: {e}";
        }

        TestLog.Write($"> atest {string.Join(" ", a)}\n{response}");
        return true;
    }

    private static string Run(string[] a)
    {
        string verb = a.Length > 0 ? a[0] : string.Empty;
        switch (verb)
        {
            case "role" when a.Length >= 3 && Player.TryGet(int.Parse(a[1]), out Player? p) && Enum.TryParse(a[2], true, out RoleTypeId role):
            {
                RoleTypeId before = p.Role;
                p.ReferenceHub.roleManager.ServerSetRole(role, RoleChangeReason.RemoteAdmin);
                return $"role of {p.PlayerId} {before} -> requested {role} -> now {p.Role}";
            }

            case "kick" when a.Length >= 2 && Player.TryGet(int.Parse(a[1]), out Player? k):
                return $"KickUser({k.PlayerId}) returned {BanPlayer.KickUser(k.ReferenceHub, "atest kick")}";

            case "ban" when a.Length >= 2 && Player.TryGet(int.Parse(a[1]), out Player? b):
            {
                int idBans = BanHandler.GetBans((BanHandler.BanType)0).Count;
                int ipBans = BanHandler.GetBans((BanHandler.BanType)1).Count;
                bool result = BanPlayer.BanUser(b.ReferenceHub, "atest ban", 60);
                return $"BanUser({b.PlayerId}) returned {result}; ID bans {idBans} -> {BanHandler.GetBans((BanHandler.BanType)0).Count}, IP bans {ipBans} -> {BanHandler.GetBans((BanHandler.BanType)1).Count}";
            }

            case "respawn" when a.Length >= 2 && RespawnManager.Singleton != null:
                RespawnManager.Singleton.ForceSpawnTeam(a[1] == "ci" ? SpawnableTeamType.ChaosInsurgency : SpawnableTeamType.NineTailedFox);
                return $"forced a {a[1]} respawn";

            case "listen":
            {
                int n = a.Length >= 2 ? int.Parse(a[1]) : 1;
                var sb = new StringBuilder();
                for (int i = 0; i < n; i++)
                    sb.AppendLine(Listeners.Spawn());
                return sb.ToString();
            }

            case "unlisten":
                Listeners.RemoveAll();
                return "listeners removed";

            case "stats":
                return Stats(a.Length >= 2 && a[1] == "reset");

            case "check":
                return Check();

            case "tp" when a.Length >= 5 && Player.TryGet(int.Parse(a[1]), out Player? t):
                t.Position = new Vector3(float.Parse(a[2]), float.Parse(a[3]), float.Parse(a[4]));
                return $"{t.PlayerId} at {t.Position}";

            case "lock" when a.Length >= 2:
                RoundSummary.RoundLock = a[1] == "on";
                return $"RoundLock={RoundSummary.RoundLock}";

            case "nspawn" when a.Length >= 2 && Enum.TryParse(a[1], true, out RoleTypeId nrole):
            {
                ReferenceHub hub = ServerDummy.Spawn(nrole, new Vector3(0, 1000, 0), Quaternion.identity);
                return $"native dummy {hub.PlayerId} role={hub.roleManager.CurrentRole.RoleTypeId} mode={hub.Mode} syncedUserId={hub.characterClassManager.SyncedUserId ?? "(null)"}";
            }

            case "drop" when a.Length >= 2 && Player.TryGet(int.Parse(a[1]), out Player? d):
                d.ReferenceHub.connectionToClient.Disconnect();
                return $"disconnected {d.PlayerId}";

            case "ra" when a.Length >= 2:
            {
                // An RA command as an admin with full permissions types it (CommandProcessor.ProcessQuery is internal).
                var ra = new CaptureSender();
                string query = string.Join(" ", a.Skip(1));
                AccessTools.Method(typeof(CommandProcessor), "ProcessQuery").Invoke(null, [query, ra]);
                return $"RA '{query}': {Regex.Replace(ra.Reply, "<[^>]+>", string.Empty)}";
            }

            default:
                return "atest role|kick|ban|respawn|listen|unlisten|stats|check|tp|lock|nspawn|drop|ra";
        }
    }

    private static string Stats(bool reset)
    {
        var sb = new StringBuilder();
        foreach (ListenerConnection c in Listeners.All)
        {
            ReferenceHub? hub = c.identity != null ? c.identity.GetComponent<ReferenceHub>() : null;
            string where = hub != null && hub.roleManager.CurrentRole is IFpcRole fpc ? fpc.FpcModule.Position.ToString() : "-";
            sb.AppendLine($"listener conn {c.connectionId} player {hub?.PlayerId} role {hub?.roleManager.CurrentRole.RoleTypeId} at {where}: transport {c.TransportBytes}B in {c.Batches} batches, parse errors {c.ParseErrors}");
            foreach (StreamStats s in c.Streams.Values)
                sb.AppendLine("  " + s.Report());

            if (reset)
                c.Reset();
        }

        return sb.Length == 0 ? "no listeners" : sb.ToString();
    }

    private static string Check()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"round: started={RoundSummary.RoundInProgress()} lock={RoundSummary.RoundLock} restarting={RoundRestart.IsRoundRestarting}");
        foreach (ReferenceHub hub in ReferenceHub.AllHubs.OrderBy(h => h.PlayerId))
        {
            CharacterClassManager ccm = hub.characterClassManager;
            string conn = hub.connectionToClient == null ? "none" : $"{hub.connectionToClient.GetType().Name}({hub.connectionToClient.connectionId})";
            sb.AppendLine($"  hub {hub.PlayerId} '{hub.nicknameSync.MyNick}' mode={hub.Mode} synced={ccm.SyncedUserId ?? "(null)"} userId={ccm.UserId ?? "(null)"} role={hub.roleManager.CurrentRole.RoleTypeId} team={hub.roleManager.CurrentRole.Team} god={ccm.GodMode} speaker={AudioPlayer.IsSpeaker(hub)} conn={conn}");
        }

        sb.AppendLine($"connections: {NetworkServer.connections.Count} = {string.Join(", ", NetworkServer.connections.Values.Select(c => $"{c.connectionId}:{c.GetType().Name}"))}");
        sb.AppendLine($"ServerDummy components: {UnityEngine.Object.FindObjectsByType<ServerDummy>(FindObjectsSortMode.None).Length}");
        sb.AppendLine($"audio players: {string.Join(", ", AudioPlayer.List.Select(p => $"{p.Name}:{p.State}:{p.Mode}:speaker={p.SpeakerPlayer?.PlayerId.ToString() ?? "-"}"))}");
        sb.AppendLine($"counts: lobby ReadyClient={ReferenceHub.AllHubs.Count(h => h.Mode == ClientInstanceMode.ReadyClient)} round-end non-dedicated={ReferenceHub.AllHubs.Count(h => h.characterClassManager.InstanceMode != ClientInstanceMode.DedicatedServer)} Player.List={Player.List.Count} dummies={Player.List.Count(p => p.IsDummy)} speakers(IsSpeaker)={Player.List.Count(AudioPlayer.IsSpeaker)}");

        new PlayersCommand().Execute(default, ServerConsole.Scs, out string players);
        sb.AppendLine("players command: " + Regex.Replace(players, "<[^>]+>", string.Empty).Replace("\n", " | "));

        var ra = new CaptureSender();
        new RaPlayerList().ReceiveData(ra, "0 0 0");
        sb.AppendLine("RA list: " + Regex.Replace(ra.Reply, "<[^>]+>", string.Empty).Replace("\n", " | "));

        sb.AppendLine($"flush checks: {ListenerConnection.FlushChecks}, violations: {ListenerConnection.FlushViolations.Count}");
        foreach (string v in ListenerConnection.FlushViolations)
            sb.AppendLine("  " + v);

        sb.AppendLine($"spawn messages to listeners: {SpawnWatch.Records.Count}");
        foreach (string r in SpawnWatch.Records)
            sb.AppendLine("  " + r);

        SpawnWatch.Records.Clear();
        return sb.ToString();
    }
}
