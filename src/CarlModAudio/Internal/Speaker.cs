using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using RelativePositioning;
using RoundRestarting;
using UnityEngine;

namespace CarlModAudio.Internal;

/// <summary>
/// A hidden server-side dummy player whose voice carries one stream. The client plays a voice message through the
/// speaker's role: a spectator role (Overwatch) plays it unpositioned for everyone, a human role (Tutorial) plays it in
/// 3D at the speaker's body.
/// </summary>
/// <remarks>
/// Spawning follows the fork's <c>ServerDummy.Spawn</c>, with three differences: <c>NicknameSync</c>'s hub reference is
/// set before the nickname (the fork sets the nickname first and throws a NullReferenceException), the dummy's synced user
/// ID is the dedicated server's (clients and the server then treat it as the server's own player: no player list or RA
/// list entry, never counted for the lobby or for round-end checks), and roles change only through this class.
/// </remarks>
internal sealed class Speaker
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Role changes reach clients on the next frame; voice that arrives before the role is applied is lost.
    private static readonly long ReadyDelayTicks = Stopwatch.Frequency * 3 / 10;

    private const string DedicatedId = "ID_Dedicated";

    private static FieldInfo? _dummyHubField;
    private static FieldInfo? _dummyConnectionField;
    private static FieldInfo? _nicknameHubField;
    private static FieldInfo? _privateUserIdField;
    private static PropertyInfo? _instanceModeProperty;
    private static string? _unsupported;
    private static bool _resolved;

    private readonly NetworkConnectionToClient _connection;
    private bool _destroyed;

    private Speaker(ReferenceHub hub, NetworkConnectionToClient connection)
    {
        Hub = hub;
        _connection = connection;
    }

    public ReferenceHub Hub { get; }

    /// <summary>The dummy still exists (a round restart or an admin may have removed it).</summary>
    public bool IsValid => !_destroyed && Hub != null && Hub.gameObject != null;

    public bool IsPositional { get; private set; }

    /// <summary><see cref="Stopwatch"/> timestamp from which clients know the current role.</summary>
    public long ReadyAt { get; private set; }

    public Vector3 BodyPosition => Hub.roleManager.CurrentRole is IFpcRole fpc ? fpc.FpcModule.Position : Vector3.zero;

    /// <summary>Checks once that this game build has everything the speakers need.</summary>
    public static bool IsSupported(out string? reason)
    {
        if (!_resolved)
        {
            _resolved = true;
            try
            {
                _unsupported = Resolve();
            }
            catch (Exception e) when (e is TypeLoadException or MissingMemberException or FileNotFoundException)
            {
                _unsupported = $"{e.GetType().Name}: {e.Message}";
            }
        }

        reason = _unsupported;
        return reason == null;
    }

    /// <summary>Spawns a speaker, or returns null with the reason.</summary>
    public static Speaker? Spawn(string nickname, bool positional, Vector3 bodyPosition, out string? error)
    {
        if (!IsSupported(out error))
            return null;

        if (!NetworkServer.active || RoundRestart.IsRoundRestarting)
        {
            error = "the server is not running a round (restarting?)";
            return null;
        }

        if (NetworkManager.singleton == null || NetworkManager.singleton.playerPrefab == null || !ReferenceHub.TryGetHostHub(out _))
        {
            error = "the player prefab is not available yet";
            return null;
        }

        try
        {
            return SpawnDummy(nickname, positional, bodyPosition);
        }
        catch (Exception e)
        {
            error = $"spawning the speaker failed ({e.GetType().Name}: {e.Message})";
            LabApi.Features.Console.Logger.Error($"Spawning a speaker dummy failed:\n{e}");
            return null;
        }
    }

    /// <summary>
    /// Presents a speaker as the dedicated server's own player: synced user ID "ID_Dedicated" (clients derive the
    /// DedicatedServer mode from it and leave the player out of their player list) and server-side mode DedicatedServer.
    /// </summary>
    /// <remarks>
    /// The game sets the user ID again in <c>CharacterClassManager.Start</c>, which syncs a null ID; this runs from the
    /// resulting mode change in the same frame, so clients never receive the null.
    /// </remarks>
    public static void PresentAsServer(ReferenceHub hub)
    {
        CharacterClassManager manager = hub.characterClassManager;
        if (manager.SyncedUserId != DedicatedId)
            manager.NetworkSyncedUserId = DedicatedId;

        if (manager.InstanceMode != ClientInstanceMode.DedicatedServer)
            _instanceModeProperty?.SetValue(manager, ClientInstanceMode.DedicatedServer);
    }

    /// <summary>True for dummy connections (speakers and other server dummies), which have no client behind them.</summary>
    public static bool IsDummyConnection(NetworkConnectionToClient connection) => connection is ServerDummyConnection;

    /// <summary>
    /// Switches between global (spectator role) and positional (human role) playback. If the role cannot be set, the
    /// speaker removes itself (<see cref="IsValid"/> becomes false) and its player spawns a new one.
    /// </summary>
    public void SetMode(bool positional, Vector3 bodyPosition)
    {
        if (!IsValid)
            return;

        if (positional == IsPositional)
        {
            if (positional)
                MoveTo(bodyPosition);

            return;
        }

        IsPositional = positional;
        try
        {
            ApplyRole(bodyPosition);
        }
        catch (Exception e)
        {
            LabApi.Features.Console.Logger.Error($"Changing a speaker's role failed; removing it:\n{e}");
            Destroy();
        }
    }

    public void MoveTo(Vector3 bodyPosition)
    {
        if (!IsPositional || Hub.roleManager.CurrentRole is not IFpcRole fpc)
            return;

        FirstPersonMovementModule module = fpc.FpcModule;
        if ((module.Position - bodyPosition).sqrMagnitude < 0.0025f)
            return;

        module.ServerOverridePosition(bodyPosition, Vector3.zero);
        module.Motor.ReceivedPosition = new RelativePosition(bodyPosition);
    }

    public void SetNickname(string nickname)
    {
        if (IsValid && Hub.nicknameSync.MyNick != nickname)
            Hub.nicknameSync.UpdateNickname(nickname);
    }

    public void Destroy()
    {
        if (_destroyed)
            return;

        _destroyed = true;
        SpeakerRegistry.Remove(Hub);
        if (Hub != null)
            _connection.Disconnect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? Resolve()
    {
        _dummyHubField = typeof(ServerDummy).GetField("_hub", Instance);
        _dummyConnectionField = typeof(ServerDummy).GetField("_connection", Instance);
        _nicknameHubField = typeof(NicknameSync).GetField("_hub", Instance);
        _privateUserIdField = typeof(CharacterClassManager).GetField("_privUserId", Instance);
        _instanceModeProperty = typeof(CharacterClassManager).GetProperty(nameof(CharacterClassManager.InstanceMode), Instance);

        if (_dummyHubField == null || _dummyConnectionField == null)
            return "ServerDummy._hub/_connection not found";
        if (_nicknameHubField == null)
            return "NicknameSync._hub not found";
        if (_privateUserIdField == null)
            return "CharacterClassManager._privUserId not found";
        if (_instanceModeProperty?.GetSetMethod(true) == null)
            return "CharacterClassManager.InstanceMode has no setter";

        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Speaker SpawnDummy(string nickname, bool positional, Vector3 bodyPosition)
    {
        int id = -1;
        while (NetworkServer.connections.ContainsKey(id))
            id--;

        var connection = new ServerDummyConnection(id);
        GameObject? gameObject = null;
        ReferenceHub? hub = null;
        try
        {
            gameObject = UnityEngine.Object.Instantiate(NetworkManager.singleton.playerPrefab, bodyPosition, Quaternion.identity);
            hub = gameObject.GetComponent<ReferenceHub>();
            if (hub == null)
                throw new InvalidOperationException("The player prefab has no ReferenceHub.");

            // Registered first, so the guards already apply to the late-join role assignment that setting the user ID
            // triggers during a round.
            SpeakerRegistry.Add(hub);
            hub.characterClassManager.GodMode = true;

            ServerDummy dummy = gameObject.AddComponent<ServerDummy>();
            _dummyHubField!.SetValue(dummy, hub);
            _dummyConnectionField!.SetValue(dummy, connection);
            // The server keeps the dummy's own user ID, but clients get "ID_Dedicated" in the spawn message itself, as
            // for the server's own player: a client that sees any other ID first lists the player for good. The UserId
            // setter is bypassed because it would sync a null ID to clients.
            _privateUserIdField!.SetValue(hub.characterClassManager, connection.UserId);
            PresentAsServer(hub);

            NetworkServer.connections.Add(id, connection);
            if (!NetworkServer.AddPlayerForConnection(connection, gameObject))
                throw new InvalidOperationException("Mirror could not register the dummy player.");

            hub.serverRoles.RefreshPermissions();

            if (_nicknameHubField!.GetValue(hub.nicknameSync) == null)
                _nicknameHubField.SetValue(hub.nicknameSync, hub);

            hub.nicknameSync.UpdateNickname(nickname);

            var speaker = new Speaker(hub, connection) { IsPositional = positional };
            speaker.ApplyRole(bodyPosition);
            return speaker;
        }
        catch
        {
            if (hub != null)
                SpeakerRegistry.Remove(hub);

            connection.Disconnect();
            if (gameObject != null && !gameObject.GetComponent<NetworkIdentity>().isServer)
                UnityEngine.Object.Destroy(gameObject);

            throw;
        }
    }

    private void ApplyRole(Vector3 bodyPosition)
    {
        RoleTypeId role = IsPositional ? RoleTypeId.Tutorial : RoleTypeId.Overwatch;
        using (SpeakerRegistry.AllowRoleChange())
            Hub.roleManager.ServerSetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);

        if (Hub.roleManager.CurrentRole.RoleTypeId != role)
            throw new InvalidOperationException($"The speaker did not get the {role} role (a plugin may have blocked it).");

        if (Hub.roleManager.CurrentRole is IFpcRole fpc)
        {
            // Noclip keeps the server from simulating gravity, so a body below the floor stays where it is put.
            fpc.FpcModule.Noclip.IsActive = true;
            fpc.FpcModule.ServerOverridePosition(bodyPosition, Vector3.zero);
            fpc.FpcModule.Motor.ReceivedPosition = new RelativePosition(bodyPosition);
        }

        ReadyAt = Stopwatch.GetTimestamp() + ReadyDelayTicks;
    }
}
