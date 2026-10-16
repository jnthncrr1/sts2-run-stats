using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace RunStats;

/// <summary>
/// Healing is not recorded in the combat history. Healing Osty is the party's block in the same
/// way summoning is, so this read-only prefix reports the HP a pet is about to regain, capped at
/// the HP it is actually missing.
/// </summary>
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Heal))]
public static class PetHealPatch
{
    public static event Action<Creature, int>? PetHealed;

    [HarmonyPrefix]
    public static void Prefix(Creature creature, decimal amount)
    {
        try
        {
            if (!creature.IsPet || !creature.IsAlive) return;
            int actual = (int)Math.Min(amount, creature.MaxHp - creature.CurrentHp);
            if (actual > 0) PetHealed?.Invoke(creature, actual);
        }
        catch (Exception e)
        {
            Godot.GD.PushWarning("[RunStats] pet heal hook failed: " + e);
        }
    }
}
