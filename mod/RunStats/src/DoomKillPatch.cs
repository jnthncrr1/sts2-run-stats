using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RunStats;

/// <summary>
/// Doom kills bypass the damage path entirely (a direct kill command), so nothing reaches the
/// combat history. This read-only prefix captures each victim's HP just before it dies and
/// hands it to the tracker. It changes nothing about the kill itself.
/// </summary>
[HarmonyPatch(typeof(DoomPower), nameof(DoomPower.DoomKill))]
public static class DoomKillPatch
{
    public static event Action<Creature, int>? DoomKilled;

    [HarmonyPrefix]
    public static void Prefix(IReadOnlyList<Creature> creatures)
    {
        foreach (Creature creature in creatures)
        {
            if (creature.IsAlive && creature.CurrentHp > 0)
            {
                DoomKilled?.Invoke(creature, creature.CurrentHp);
            }
        }
    }
}
