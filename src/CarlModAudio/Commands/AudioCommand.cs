using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CarlModAudio.Internal;
using CommandSystem;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using RemoteAdmin;
using UnityEngine;

namespace CarlModAudio.Commands;

/// <summary>The <c>audio</c> command for the Remote Admin console and the server console.</summary>
[CommandHandler(typeof(RemoteAdminCommandHandler))]
[CommandHandler(typeof(GameConsoleCommandHandler))]
public sealed class AudioCommand : ICommand, IUsageProvider
{
    private const string Help =
        "audio play <file> [global|here|at <x> <y> <z>|on <player>] [loop] [vol=<0-200>] [id=<name>] [to=<player,...>]\n" +
        "audio queue <file> [id]       add a file to a player's queue\n" +
        "audio stop [id|all]           stop and remove a player\n" +
        "audio pause [id] | resume [id] | skip [id]\n" +
        "audio volume <0-200> [id]     volume in percent\n" +
        "audio loop <on|off> [id]      repeat the current file\n" +
        "audio move <global|here|at <x> <y> <z>|on <player>> [id]\n" +
        "audio list                    files in the audio folder\n" +
        "audio status                  players, speakers, cost\n" +
        "[id] may be left out while only one player exists. <player> is a player ID.";

    /// <inheritdoc/>
    public string Command => "audio";

    /// <inheritdoc/>
    public string[] Aliases => ["au"];

    /// <inheritdoc/>
    public string Description => "Plays audio files to players (CarlModAudio).";

    /// <inheritdoc/>
    public string[] Usage => ["play/queue/stop/pause/resume/skip/volume/loop/move/list/status", "..."];

    /// <inheritdoc/>
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!AudioSystem.IsEnabled)
        {
            response = "CarlModAudio is disabled.";
            return false;
        }

        // The server console has every permission.
        if (!sender.CheckPermission(AudioSystem.Config.CommandPermission, out response))
            return false;

        var args = new List<string>(arguments.Count);
        for (int i = 0; i < arguments.Count; i++)
            args.Add(arguments.Array![arguments.Offset + i]);

        if (args.Count == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            response = Help;
            return true;
        }

        string sub = args[0].ToLowerInvariant();
        args.RemoveAt(0);
        try
        {
            return sub switch
            {
                "play" => PlayCommand(args, sender, queue: false, out response),
                "queue" => PlayCommand(args, sender, queue: true, out response),
                "stop" => Stop(args, out response),
                "pause" => Simple(args, p => p.Pause(), "paused", out response),
                "resume" => Simple(args, p => p.Resume(), "resumed", out response),
                "skip" => Simple(args, p => p.Skip(), "skipped to the next file", out response),
                "volume" or "vol" => Volume(args, out response),
                "loop" => LoopCommand(args, out response),
                "move" => Move(args, sender, out response),
                "list" => ListFiles(out response),
                "status" => Status(out response),
                _ => Fail($"Unknown subcommand '{sub}'.\n{Help}", out response),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            response = e.Message;
            return false;
        }
    }

    private static bool PlayCommand(List<string> args, ICommandSender sender, bool queue, out string response)
    {
        if (args.Count == 0)
            return Fail(queue ? "Usage: audio queue <file> [id]" : "Usage: audio play <file> [where] [options]", out response);

        string file = args[0];
        string? fullPath = AudioFiles.Resolve(file, allowOutsideFolder: false, out string? error);
        if (fullPath == null)
            return Fail($"{error}. 'audio list' shows the files.", out response);

        if (queue)
        {
            AudioPlayer? target = ResolvePlayer(args.Count > 1 ? args[1] : null, out response);
            if (target == null)
                return false;

            target.Enqueue(fullPath);
            ReportLoad(fullPath, sender, target);
            response = $"Queued {file} on player {target.Name} ({target.QueueCount} waiting).";
            return true;
        }

        // Options: loop, vol=, id=, to=; the rest is the location.
        bool loop = false;
        float? volume = null;
        string? id = null;
        HashSet<ReferenceHub>? receivers = null;
        var location = new List<string>();
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.Equals("loop", StringComparison.OrdinalIgnoreCase))
                loop = true;
            else if (TryOption(arg, "vol", out string value) || TryOption(arg, "volume", out value))
                volume = ParsePercent(value);
            else if (TryOption(arg, "id", out value))
                id = value;
            else if (TryOption(arg, "to", out value))
                receivers = ParseReceivers(value);
            else
                location.Add(arg);
        }

        AudioPlayer? player = null;
        bool created = false;
        if (id != null && AudioPlayer.TryGet(id, out player))
        {
            // Replace what an existing player plays.
        }
        else
        {
            player = AudioPlayer.Create(id);
            player.DestroyWhenStopped = true;
            created = true;
        }

        try
        {
            string where = ApplyLocation(player, location, sender);
            if (volume.HasValue)
                player.Volume = volume.Value;

            if (created || loop)
                player.Loop = loop;
            if (receivers != null)
                player.ReceiverFilter = p => receivers.Contains(p.ReferenceHub);

            player.Play(fullPath);
            ReportLoad(fullPath, sender, player);
            response = player.State == PlaybackState.Playing
                ? $"Player {player.Name}: playing {player.CurrentName} ({AudioFormat.Time(player.CurrentClip!.Duration)}) {where}{Extras(player, receivers)}."
                : $"Player {player.Name}: loading {player.CurrentName}, then playing it {where}{Extras(player, receivers)}.";
            return true;
        }
        catch
        {
            if (created)
                player.Destroy();

            throw;
        }
    }

    private static bool Stop(List<string> args, out string response)
    {
        if (args.Count > 0 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            int count = AudioPlayer.List.Count;
            for (int i = count - 1; i >= 0; i--)
                AudioPlayer.List[i].Destroy();

            response = $"Stopped and removed {count} player(s).";
            return true;
        }

        AudioPlayer? player = ResolvePlayer(args.Count > 0 ? args[0] : null, out response);
        if (player == null)
            return false;

        player.Destroy();
        response = $"Player {player.Name} stopped and removed.";
        return true;
    }

    private static bool Simple(List<string> args, Action<AudioPlayer> action, string done, out string response)
    {
        AudioPlayer? player = ResolvePlayer(args.Count > 0 ? args[0] : null, out response);
        if (player == null)
            return false;

        action(player);
        response = $"Player {player.Name}: {done} ({player.State}).";
        return true;
    }

    private static bool Volume(List<string> args, out string response)
    {
        if (args.Count == 0)
            return Fail("Usage: audio volume <0-200> [id]", out response);

        float volume = ParsePercent(args[0]);
        AudioPlayer? player = ResolvePlayer(args.Count > 1 ? args[1] : null, out response);
        if (player == null)
            return false;

        player.Volume = volume;
        response = $"Player {player.Name}: volume {AudioFormat.Percent(player.Volume)}.";
        return true;
    }

    private static bool LoopCommand(List<string> args, out string response)
    {
        if (args.Count == 0 || !TryParseOnOff(args[0], out bool loop))
            return Fail("Usage: audio loop <on|off> [id]", out response);

        AudioPlayer? player = ResolvePlayer(args.Count > 1 ? args[1] : null, out response);
        if (player == null)
            return false;

        player.Loop = loop;
        response = $"Player {player.Name}: loop {(loop ? "on" : "off")}.";
        return true;
    }

    private static bool Move(List<string> args, ICommandSender sender, out string response)
    {
        if (args.Count == 0)
            return Fail("Usage: audio move <global|here|at <x> <y> <z>|on <player>> [id]", out response);

        // The location takes 1 (global, here), 2 (on <player>) or 4 (at x y z) words; a word after it is the id.
        int locationWords = args[0].ToLowerInvariant() switch
        {
            "at" => 4,
            "on" => 2,
            _ => 1,
        };
        string? id = args.Count > locationWords ? args[locationWords] : null;
        AudioPlayer? player = ResolvePlayer(id, out response);
        if (player == null)
            return false;

        string where = ApplyLocation(player, args.GetRange(0, Math.Min(locationWords, args.Count)), sender);
        response = $"Player {player.Name}: now {where}.";
        return true;
    }

    private static bool ListFiles(out string response)
    {
        List<string> files = AudioFiles.List();
        response = files.Count == 0
            ? $"No .ogg or .wav files in {AudioFiles.Folder}."
            : $"{files.Count} file(s) in {AudioFiles.Folder}:\n{string.Join("\n", files)}";
        return true;
    }

    private static bool Status(out string response)
    {
        var text = new StringBuilder();
        if (AudioSystem.UnavailableReason != null)
            text.Append("Playback unavailable: ").Append(AudioSystem.UnavailableReason).Append('\n');

        text.Append(AudioPlayer.List.Count).Append('/').Append(AudioSystem.MaxPlayers).Append(" player(s), ")
            .Append(SpeakerRegistry.Count).Append(" speaker(s). Audio CPU ")
            .Append(AudioSystem.CpuMsPerSecond.ToString("0.00", CultureInfo.InvariantCulture)).Append(" ms/s, server ")
            .Append(AudioSystem.ServerFps.ToString("0.0", CultureInfo.InvariantCulture)).Append(" fps, cache ")
            .Append((ClipCache.CachedBytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)).Append(" MB.");

        foreach (AudioPlayer player in AudioPlayer.List)
        {
            text.Append('\n').Append(player.Name).Append(": ").Append(player.State);
            if (player.CurrentName != null)
            {
                text.Append(' ').Append(player.CurrentName);
                if (player.CurrentClip != null)
                    text.Append(' ').Append(AudioFormat.Time(player.Time)).Append('/').Append(AudioFormat.Time(player.CurrentClip.Duration));
            }

            text.Append(", ").Append(DescribeMode(player)).Append(", volume ").Append(AudioFormat.Percent(player.Volume));
            if (player.Loop)
                text.Append(", loop");
            if (player.QueueCount > 0)
                text.Append(", ").Append(player.QueueCount).Append(" queued");
            if (player.ReceiverFilter != null)
                text.Append(", limited listeners");
        }

        response = text.ToString();
        return true;
    }

    // Applies global / here / at x y z / on <player>; returns a description. Nothing given keeps the current mode.
    private static string ApplyLocation(AudioPlayer player, List<string> words, ICommandSender sender)
    {
        if (words.Count == 0)
            return DescribeMode(player);

        switch (words[0].ToLowerInvariant())
        {
            case "global":
                if (words.Count != 1)
                    break;

                player.SetGlobal();
                return DescribeMode(player);

            case "here":
                if (words.Count != 1)
                    break;

                if (Player.Get(sender) is not { } self || self.ReferenceHub.roleManager.CurrentRole is not IFpcRole)
                    throw new ArgumentException("'here' needs a player with a position; use 'at <x> <y> <z>' from the server console.");

                player.SetPosition(self.Position);
                return DescribeMode(player);

            case "at":
                if (words.Count != 4)
                    break;

                player.SetPosition(new Vector3(ParseFloat(words[1]), ParseFloat(words[2]), ParseFloat(words[3])));
                return DescribeMode(player);

            case "on":
                if (words.Count != 2)
                    break;

                player.AttachTo(ParsePlayer(words[1]));
                return DescribeMode(player);
        }

        throw new ArgumentException($"Unknown location '{string.Join(" ", words)}'. Use global, here, at <x> <y> <z> or on <player>.");
    }

    private static string DescribeMode(AudioPlayer player) => player.Mode switch
    {
        SpeakerMode.Position => $"at {AudioFormat.Vector(player.Position)}",
        SpeakerMode.Player => $"on {player.AttachedTo?.Nickname ?? "?"} ({player.AttachedTo?.PlayerId})",
        _ => "globally",
    };

    private static string Extras(AudioPlayer player, HashSet<ReferenceHub>? receivers)
    {
        var text = new StringBuilder();
        text.Append(", volume ").Append(AudioFormat.Percent(player.Volume));
        if (player.Loop)
            text.Append(", loop");
        if (receivers != null)
            text.Append(", for ").Append(receivers.Count).Append(" player(s)");

        return text.ToString();
    }

    // Answers the sender later if the file still had to be decoded.
    private static void ReportLoad(string fullPath, ICommandSender sender, AudioPlayer player)
    {
        bool immediate = true;
        AudioClipData.LoadAsync(fullPath, (clip, error) =>
        {
            if (immediate)
                return;

            sender.Respond(clip != null
                ? $"audio: {clip.Name} decoded ({AudioFormat.Time(clip.Duration)}), player {player.Name}."
                : $"audio: {error}", clip != null);
        });
        immediate = false;
    }

    private static AudioPlayer? ResolvePlayer(string? id, out string response)
    {
        if (id != null)
        {
            if (AudioPlayer.TryGet(id, out AudioPlayer? named))
            {
                response = string.Empty;
                return named;
            }

            response = $"No audio player '{id}'. {ListIds()}";
            return null;
        }

        if (AudioPlayer.List.Count == 1)
        {
            response = string.Empty;
            return AudioPlayer.List[0];
        }

        response = AudioPlayer.List.Count == 0 ? "No audio player exists." : $"Several players exist; name one. {ListIds()}";
        return null;
    }

    private static string ListIds()
    {
        if (AudioPlayer.List.Count == 0)
            return "None exist.";

        var names = new string[AudioPlayer.List.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = AudioPlayer.List[i].Name;

        return "Players: " + string.Join(", ", names) + ".";
    }

    private static Player ParsePlayer(string text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && Player.TryGet(id, out Player? player) && !AudioPlayer.IsSpeaker(player))
            return player;

        throw new ArgumentException($"No player with ID '{text}'.");
    }

    private static HashSet<ReferenceHub> ParseReceivers(string text)
    {
        var set = new HashSet<ReferenceHub>();
        foreach (string part in text.Split([','], StringSplitOptions.RemoveEmptyEntries))
            set.Add(ParsePlayer(part.Trim()).ReferenceHub);

        if (set.Count == 0)
            throw new ArgumentException("to= needs at least one player ID.");

        return set;
    }

    private static float ParsePercent(string text)
    {
        if (!float.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float percent) || percent < 0f || percent > 200f)
            throw new ArgumentException($"'{text}' is not a volume from 0 to 200.");

        return percent / 100f;
    }

    private static float ParseFloat(string text)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
            throw new ArgumentException($"'{text}' is not a number.");

        return value;
    }

    private static bool TryOption(string arg, string name, out string value)
    {
        if (arg.Length > name.Length + 1 && arg[name.Length] == '=' && arg.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            value = arg.Substring(name.Length + 1);
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseOnOff(string text, out bool value)
    {
        switch (text.ToLowerInvariant())
        {
            case "on" or "true" or "1" or "yes":
                value = true;
                return true;
            case "off" or "false" or "0" or "no":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool Fail(string message, out string response)
    {
        response = message;
        return false;
    }
}
