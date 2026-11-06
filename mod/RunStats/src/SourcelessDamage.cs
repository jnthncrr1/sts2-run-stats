using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RunStats;

/// <summary>
/// Credits damage that the game records with no dealer. Four player-caused effects do that:
/// Poison and Demise (stack counters on the enemy, damage equal to the stacks), Strangle
/// (one instance per applier, damage when that applier plays a card) and Haunt (a buff on the
/// player, damage to a random enemy when they play a Soul).
///
/// Poison and Demise stacks from several players merge into one counter, so a ledger in the run
/// file remembers how many of each enemy's stacks each player owns. A tick pays each player
/// their owned stacks; the one-per-turn Poison decrement is taken from whoever owns the most.
/// </summary>
public static class SourcelessDamage
{
    public static void OnPowerChanged(RunStatsSnapshot snapshot, PowerReceivedEntry entry)
    {
        if (entry.Power is not (PoisonPower or DemisePower or DoomPower)) return;
        Dictionary<string, long> owned = snapshot.StackLedger.TryGetValue(LedgerKey(entry.Power), out var o) ? o : new();

        Player? applier = entry.Applier?.Player ?? entry.Applier?.PetOwner;
        if (applier != null && entry.Amount > 0)
        {
            // The entry is logged before the new total is set, so Power.Amount is the total the
            // ledger should already account for.
            StackAttribution.AddStacks(owned, applier.NetId, (long)entry.Amount, entry.Power.Amount);
        }
        else if (entry.Amount < 0)
        {
            // The game's own decrement (or a reduction by someone else).
            StackAttribution.RemoveFromTopOwner(owned, (long)-entry.Amount);
        }

        if (owned.Count == 0) snapshot.StackLedger.Remove(LedgerKey(entry.Power));
        else snapshot.StackLedger[LedgerKey(entry.Power)] = owned;
    }

    public static void Credit(RunStatsSnapshot snapshot, Creature receiver, int dealt, bool killed, Action<ulong, long> credit)
    {
        // Strangle: the instance belonging to the player who just played a card.
        var strangles = receiver.Powers.OfType<StranglePower>()
            .Select(p => (owner: p.Applier?.Player ?? p.Applier?.PetOwner, stacks: (long)p.Amount))
            .Where(s => s.owner != null)
            .Select(s => (s.owner!.NetId, s.stacks))
            .ToList();
        ulong? strangler = StackAttribution.PickStrangleApplier(strangles, dealt, killed, snapshot.LastCardPlayNetId);
        if (strangler != null)
        {
            credit(strangler.Value, dealt);
            return;
        }

        // Demise fires at the enemy's turn end for exactly its stack count; Poison at turn start.
        DemisePower? demise = receiver.GetPower<DemisePower>();
        if (demise != null && StackAttribution.MatchesStacks(demise.Amount, dealt, killed) && TryPayFromLedger(snapshot, demise, dealt, credit)) return;

        PoisonPower? poison = receiver.GetPower<PoisonPower>();
        if (poison != null && TryPayFromLedger(snapshot, poison, dealt, credit)) return;

        // Haunt: the buff is on the player, triggered by their own Soul play.
        IReadOnlyList<Player> players = receiver.CombatState?.Players ?? Array.Empty<Player>();
        var haunts = players
            .Select(p => (p.NetId, stacks: (long)(p.Creature.GetPower<HauntPower>()?.Amount ?? 0)))
            .Where(h => h.stacks > 0)
            .ToList();
        ulong? haunter = StackAttribution.PickHaunter(haunts, dealt, killed, snapshot.LastCardPlayNetId);
        if (haunter != null) credit(haunter.Value, dealt);
    }

    /// <summary>
    /// A Doom kill counts as damage equal to the victim's HP at death, split between the Doom
    /// owners in proportion to their stacks (largest remainder rounding).
    /// </summary>
    public static void CreditDoomKill(RunStatsSnapshot snapshot, Creature victim, int hpAtDeath, Action<ulong, long> credit)
    {
        DoomPower? doom = victim.GetPower<DoomPower>();
        if (doom == null || hpAtDeath <= 0) return;
        if (!snapshot.StackLedger.TryGetValue(LedgerKey(doom), out var owned) || owned.Count == 0) return;
        long totalStacks = owned.Values.Sum();
        if (totalStacks <= 0) return;

        StackAttribution.SplitProportionally(owned.Select(kv => (ulong.Parse(kv.Key), kv.Value)), hpAtDeath, credit);
        snapshot.StackLedger.Remove(LedgerKey(doom));
    }

    private static bool TryPayFromLedger(RunStatsSnapshot snapshot, PowerModel power, int dealt, Action<ulong, long> credit) =>
        snapshot.StackLedger.TryGetValue(LedgerKey(power), out var owned) && StackAttribution.PayFromLedger(owned, dealt, credit);

    private static string LedgerKey(PowerModel power)
    {
        Creature enemy = power.Owner;
        string enemyKey = enemy.CombatId?.ToString() ?? enemy.Name;
        return $"{enemyKey}|{power.Id.Entry}";
    }
}
