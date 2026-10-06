using System;
using CommandSystem;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using PlayerRoles;
using RemoteAdmin;
using Respawning;

namespace AudioTestKit;

public sealed class AudioTestKitPlugin : Plugin
{
    public override string Name => "AudioTestKit";

    public override string Description => "Local test helper for CarlModAudio.";

    public override string Author => "carlmod-audio";

    public override Version Version => new(1, 0, 0);

    public override Version RequiredApiVersion => new(1, 0, 0);

    public override void Enable()
    {
    }

    public override void Disable()
    {
    }
}

/// <summary>
/// atest role &lt;id&gt; &lt;RoleTypeId&gt;   ServerSetRole through RemoteAdmin, like forceclass; prints the role after it
/// atest kick &lt;id&gt;                kick through BanPlayer.KickUser, like the AFK check
/// atest respawn &lt;ntf|ci&gt;          force a respawn wave
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
        string[] a = new string[arguments.Count];
        for (int i = 0; i < a.Length; i++)
            a[i] = arguments.Array![arguments.Offset + i];

        if (a.Length >= 3 && a[0] == "role" && Player.TryGet(int.Parse(a[1]), out Player? p) && Enum.TryParse(a[2], true, out RoleTypeId role))
        {
            RoleTypeId before = p.Role;
            p.ReferenceHub.roleManager.ServerSetRole(role, RoleChangeReason.RemoteAdmin);
            response = $"atest: role of {p.PlayerId} {before} -> requested {role} -> now {p.Role}";
            return true;
        }

        if (a.Length >= 2 && a[0] == "kick" && Player.TryGet(int.Parse(a[1]), out Player? k))
        {
            bool kicked = BanPlayer.KickUser(k.ReferenceHub, "atest kick");
            response = $"atest: KickUser({k.PlayerId}) returned {kicked}";
            return true;
        }

        if (a.Length >= 2 && a[0] == "respawn" && RespawnManager.Singleton != null)
        {
            RespawnManager.Singleton.ForceSpawnTeam(a[1] == "ci" ? SpawnableTeamType.ChaosInsurgency : SpawnableTeamType.NineTailedFox);
            response = $"atest: forced a {a[1]} respawn";
            return true;
        }

        response = "atest role <id> <role> | kick <id> | respawn <ntf|ci>";
        return false;
    }
}
