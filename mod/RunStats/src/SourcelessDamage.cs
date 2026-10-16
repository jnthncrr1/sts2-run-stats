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
            // ledger should already account for. A mismatch means the power expired or was
            // cleansed since (removals are not logged) and earlier ownership is stale.
            if (owned.Values.Sum() != entry.Power.Amount) owned.Clear();
            string id = applier.NetId.ToString();
            owned[id] = owned.GetValueOrDefault(id) + (long)entry.Amount;
        }
        else if (entry.Amount < 0)
        {
            // The game's own decrement (or a reduction by someone else): take it from the top owner.
            long toRemove = (long)-entry.Amount;
            while (toRemove > 0 && owned.Count > 0)
            {
                string top = owned.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
                long take = Math.Min(toRemove, owned[top]);
                owned[top] -= take;
                toRemove -= take;
                if (owned[top] <= 0) owned.Remove(top);
            }
        }

        if (owned.Count == 0) snapshot.StackLedger.Remove(LedgerKey(entry.Power));
        else snapshot.StackLedger[LedgerKey(entry.Power)] = owned;
    }

    public static void Credit(RunStatsSnapshot snapshot, Creature receiver, int dealt, Action<ulong, long> credit)
    {
        // Strangle: the instance belonging to the player who just played a card.
        StranglePower? strangle = PickStrangle(receiver, dealt, snapshot.LastCardPlayNetId);
        if (strangle != null)
        {
            Player owner = strangle.Applier!.Player ?? strangle.Applier.PetOwner!;
            credit(owner.NetId, dealt);
            return;
        }

        // Demise fires at the enemy's turn end for exactly its stack count; Poison at turn start.
        DemisePower? demise = receiver.GetPower<DemisePower>();
        if (demise != null && demise.Amount == dealt && TryPayFromLedger(snapshot, demise, dealt, credit)) return;

        PoisonPower? poison = receiver.GetPower<PoisonPower>();
        if (poison != null && TryPayFromLedger(snapshot, poison, dealt, credit)) return;

        // Haunt: the buff is on the player, triggered by their own Soul play.
        Player? haunter = PickHaunter(receiver, dealt, snapshot.LastCardPlayNetId);
        if (haunter != null) credit(haunter.NetId, dealt);
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

        SplitProportionally(owned.Select(kv => (ulong.Parse(kv.Key), kv.Value)), hpAtDeath, credit);
        snapshot.StackLedger.Remove(LedgerKey(doom));
    }

    /// <summary>
    /// Pays each owner their stacks. When less was dealt than the stacks owned (the enemy had
    /// less HP left), the shortfall is split proportionally with largest-remainder rounding.
    /// </summary>
    private static bool TryPayFromLedger(RunStatsSnapshot snapshot, PowerModel power, int dealt, Action<ulong, long> credit)
    {
        if (!snapshot.StackLedger.TryGetValue(LedgerKey(power), out var owned) || owned.Count == 0) return false;
        long totalOwned = owned.Values.Sum();
        if (totalOwned <= 0) return false;
        if (dealt >= totalOwned)
        {
            foreach (var kv in owned) credit(ulong.Parse(kv.Key), kv.Value);
            return true;
        }
        SplitProportionally(owned.Select(kv => (ulong.Parse(kv.Key), kv.Value)), dealt, credit);
        return true;
    }

    /// <summary>Splits an integer total across weights so the shares sum exactly to the total.</summary>
    public static void SplitProportionally(IEnumerable<(ulong netId, long weight)> weights, long total, Action<ulong, long> credit)
    {
        var list = weights.Where(w => w.weight > 0).ToList();
        long weightSum = list.Sum(w => w.weight);
        if (weightSum <= 0 || total <= 0) return;
        var shares = list
            .Select(w => { double exact = (double)total * w.weight / weightSum; long whole = (long)Math.Floor(exact); return (w.netId, whole, frac: exact - whole); })
            .ToList();
        long leftover = total - shares.Sum(x => x.whole);
        foreach (var share in shares.OrderByDescending(x => x.frac).ThenBy(x => x.netId))
        {
            long amount = share.whole + (leftover > 0 ? 1 : 0);
            if (leftover > 0) leftover--;
            if (amount > 0) credit(share.netId, amount);
        }
    }

    private static StranglePower? PickStrangle(Creature receiver, int dealt, ulong lastCardPlayer)
    {
        var candidates = receiver.Powers.OfType<StranglePower>()
            .Where(p => (p.Applier?.Player ?? p.Applier?.PetOwner) != null)
            .ToList();
        if (candidates.Count == 0) return null;
        return candidates.FirstOrDefault(p => (p.Applier!.Player ?? p.Applier.PetOwner)!.NetId == lastCardPlayer && p.Amount == dealt)
            ?? candidates.FirstOrDefault(p => p.Amount == dealt)
            ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    private static Player? PickHaunter(Creature receiver, int dealt, ulong lastCardPlayer)
    {
        IReadOnlyList<Player> players = receiver.CombatState?.Players ?? Array.Empty<Player>();
        List<Player> haunters = players.Where(p => p.Creature.GetPower<HauntPower>()?.Amount == dealt).ToList();
        if (haunters.Count == 0) return null;
        return haunters.FirstOrDefault(p => p.NetId == lastCardPlayer) ?? (haunters.Count == 1 ? haunters[0] : null);
    }

    private static string LedgerKey(PowerModel power)
    {
        Creature enemy = power.Owner;
        string enemyKey = enemy.CombatId?.ToString() ?? enemy.Name;
        return $"{enemyKey}|{power.Id.Entry}";
    }
}
