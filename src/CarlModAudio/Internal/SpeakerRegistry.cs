using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;

namespace CarlModAudio.Internal;

/// <summary>
/// The speaker dummies, and the guards that keep them out of round logic. The event handlers are subscribed only while
/// at least one speaker exists, so an idle plugin adds no event work.
/// </summary>
internal static class SpeakerRegistry
{
    private static readonly HashSet<ReferenceHub> Hubs = [];
    private static int _roleChangeDepth;

    public static int Count => Hubs.Count;

    /// <summary>Fast check for patches and other hot paths.</summary>
    public static bool Contains(ReferenceHub? hub) => Hubs.Count != 0 && hub != null && Hubs.Contains(hub);

    public static void Add(ReferenceHub hub)
    {
        if (Hubs.Add(hub) && Hubs.Count == 1)
            Subscribe();
    }

    public static void Remove(ReferenceHub hub)
    {
        if (Hubs.Remove(hub) && Hubs.Count == 0)
            Unsubscribe();
    }

    public static void Clear()
    {
        if (Hubs.Count == 0)
            return;

        Hubs.Clear();
        Unsubscribe();
    }

    /// <summary>Lets the speaker code change a speaker's role while the returned scope is open.</summary>
    public static RoleChangeScope AllowRoleChange()
    {
        _roleChangeDepth++;
        return default;
    }

    private static void Subscribe()
    {
        CharacterClassManager.OnInstanceModeChanged += OnInstanceModeChanged;
        PlayerEvents.ChangingRole += OnChangingRole;
        PlayerEvents.Kicking += OnKicking;
        PlayerEvents.RaPlayerListAddingPlayer += OnRaPlayerListAddingPlayer;
    }

    private static void Unsubscribe()
    {
        CharacterClassManager.OnInstanceModeChanged -= OnInstanceModeChanged;
        PlayerEvents.ChangingRole -= OnChangingRole;
        PlayerEvents.Kicking -= OnKicking;
        PlayerEvents.RaPlayerListAddingPlayer -= OnRaPlayerListAddingPlayer;
    }

    // CharacterClassManager.Start (the frame after spawning) sets the user ID again, which syncs a null ID and changes the
    // mode; restore both in the same frame.
    private static void OnInstanceModeChanged(ReferenceHub hub, ClientInstanceMode mode)
    {
        if (Contains(hub))
            Speaker.PresentAsServer(hub);
    }

    // Round start, late join, respawn waves, deathmatch respawns, forceclass, death: a speaker keeps its role.
    private static void OnChangingRole(PlayerChangingRoleEventArgs ev)
    {
        if (_roleChangeDepth == 0 && Contains(ev.Player.ReferenceHub))
            ev.IsAllowed = false;
    }

    // The AFK check kicks a human-role speaker that never moves.
    private static void OnKicking(PlayerKickingEventArgs ev)
    {
        if (Contains(ev.Player.ReferenceHub))
            ev.IsAllowed = false;
    }

    // The fork already leaves dedicated-server players out of the RA list; this covers builds where it does not.
    private static void OnRaPlayerListAddingPlayer(PlayerRaPlayerListAddingPlayerEventArgs ev)
    {
        if (Contains(ev.Target.ReferenceHub))
            ev.IsAllowed = false;
    }

    public readonly struct RoleChangeScope : IDisposable
    {
        public void Dispose() => _roleChangeDepth--;
    }
}
