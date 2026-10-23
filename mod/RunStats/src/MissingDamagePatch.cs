using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace RunStats;

/// <summary>
/// The game skips damage-history entries when no primary enemy is alive, even before
/// combat ends. This hook observes the actual result before damage triggers and victory.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageGiven))]
public static class MissingDamagePatch
{
    public static event Action<DamageReceivedEntry>? DamageUnrecorded;

    [HarmonyPrefix]
    public static void Before(ICombatState combatState, Creature? dealer, DamageResult results, CardModel? cardSource)
    {
        try
        {
            CombatManager manager = CombatManager.Instance;
            if (!DamageHistoryFallback.ShouldCapture(manager.IsInProgress, results,
                    manager.History.Entries.OfType<DamageReceivedEntry>().Select(e => (object)e.Result))) return;
            var entry = new DamageReceivedEntry(results, results.Receiver, dealer, cardSource,
                combatState.RoundNumber, combatState.CurrentSide, manager.History, combatState.Players);
            DamageUnrecorded?.Invoke(entry);
        }
        catch (Exception e)
        {
            Godot.GD.PushWarning("[RunStats] missing damage hook failed: " + e);
        }
    }
}
