using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace RunStats;

/// <summary>
/// The potion-used history entry is written after the potion's effects resolve, so block a
/// potion grants (to its user or to an ally) has already arrived by then. These read-only
/// prefixes on the game's own before/after hooks mark which player's potion is resolving so
/// that block can be credited to them.
/// </summary>
[HarmonyPatch(typeof(Hook))]
public static class PotionUsePatch
{
    [HarmonyPrefix, HarmonyPatch(nameof(Hook.BeforePotionUsed))]
    public static void Before(PotionModel potion, Creature? target)
    {
        try
        {
            ulong netId = potion.Owner.NetId;
            ModEntry.Recorder?.PotionUsed(potion, target);
            ModEntry.Tracker?.Update(snapshot => snapshot.ActivePotionNetId = netId);
        }
        catch (Exception e)
        {
            Godot.GD.PushWarning("[RunStats] potion hook failed: " + e);
        }
    }

    [HarmonyPrefix, HarmonyPatch(nameof(Hook.AfterPotionUsed))]
    public static void After()
    {
        try
        {
            ModEntry.Tracker?.Update(snapshot =>
            {
                snapshot.ActivePotionNetId = 0;
            });
        }
        catch (Exception e)
        {
            Godot.GD.PushWarning("[RunStats] potion hook failed: " + e);
        }
    }
}

[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.OnUseWrapper))]
public static class PotionActionScopePatch
{
    [HarmonyPrefix]
    public static void Before(PotionModel __instance, out object? __state) =>
        __state = ModEntry.Recorder?.EnterActionScope(__instance);

    [HarmonyPostfix]
    public static void After(object? __state, ref Task __result)
    {
        if (__state != null && ModEntry.Recorder != null)
            __result = ModEntry.Recorder.CompleteActionScope(__result, __state);
    }
}
