using System;
using System.Reflection;
using HarmonyLib;
using LabApi.Features.Console;

namespace CarlModAudio.Internal;

/// <summary>
/// Keeps a positional speaker (a Tutorial body) out of the SCP and facility checks that look at every living human:
/// it never counts as looking at SCP-173 or SCP-096 and never triggers tesla gates. Each patch is applied only if its
/// method exists; a missing one is logged and skipped.
/// </summary>
internal static class SpeakerPatches
{
    private const string HarmonyId = "carlmodaudio.speakers";

    private static Harmony? _harmony;

    public static void Apply()
    {
        _harmony ??= new Harmony(HarmonyId);
        Patch("PlayerRoles.PlayableScps.Scp173.Scp173ObserversTracker", "IsObservedBy", [typeof(ReferenceHub), typeof(float)]);
        Patch("PlayerRoles.PlayableScps.Scp096.Scp096TargetsTracker", "IsObservedBy", [typeof(ReferenceHub)]);
        Patch("TeslaGate", "PlayerInRange", [typeof(ReferenceHub)]);
        Patch("TeslaGate", "PlayerInIdleRange", [typeof(ReferenceHub)]);
    }

    public static void Remove()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
    }

    private static void Patch(string typeName, string methodName, Type[] parameters)
    {
        MethodInfo? target = AccessTools.TypeByName(typeName) is { } type ? AccessTools.Method(type, methodName, parameters) : null;
        if (target == null || target.ReturnType != typeof(bool))
        {
            Logger.Warn($"{typeName}.{methodName} not found; speakers are not excluded from it on this game build.");
            return;
        }

        try
        {
            _harmony!.Patch(target, prefix: new HarmonyMethod(typeof(SpeakerPatches), nameof(ReturnFalseForSpeakers)));
        }
        catch (Exception e)
        {
            Logger.Warn($"Patching {typeName}.{methodName} failed; speakers are not excluded from it: {e.Message}");
        }
    }

    // __0 is the player the check is about in every patched method.
    private static bool ReturnFalseForSpeakers(ReferenceHub __0, ref bool __result)
    {
        if (!SpeakerRegistry.Contains(__0))
            return true;

        __result = false;
        return false;
    }
}
