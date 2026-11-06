namespace RunStats;

/// <summary>
/// The decisions behind crediting damage with no dealer, kept free of game types so they can be
/// tested outside the game. Ledgers map a player's net ID (as a string, for JSON) to the stacks
/// they own on one enemy's Poison, Demise or Doom.
/// </summary>
public static class StackAttribution
{
    /// <summary>
    /// A Demise, Strangle or Haunt hit deals exactly its stack count, except the one that kills its
    /// target, which deals only the HP that was left.
    /// </summary>
    public static bool MatchesStacks(long stacks, int dealt, bool killed) =>
        dealt == stacks || (killed && dealt > 0 && dealt < stacks);

    /// <summary>
    /// The Strangle applier behind a hit: the matching instance of whoever just played a card, then
    /// any matching instance, then the only instance on the enemy.
    /// </summary>
    public static ulong? PickStrangleApplier(IReadOnlyList<(ulong netId, long stacks)> strangles, int dealt, bool killed, ulong lastCardPlayer)
    {
        if (strangles.Count == 0) return null;
        var matching = strangles.Where(s => MatchesStacks(s.stacks, dealt, killed)).ToList();
        if (matching.Any(s => s.netId == lastCardPlayer)) return lastCardPlayer;
        if (matching.Count > 0) return matching[0].netId;
        return strangles.Count == 1 ? strangles[0].netId : null;
    }

    /// <summary>
    /// The player whose Haunt dealt a hit: the matching haunter who just played a card, or the only
    /// matching haunter.
    /// </summary>
    public static ulong? PickHaunter(IReadOnlyList<(ulong netId, long stacks)> haunts, int dealt, bool killed, ulong lastCardPlayer)
    {
        var matching = haunts.Where(h => MatchesStacks(h.stacks, dealt, killed)).ToList();
        if (matching.Any(h => h.netId == lastCardPlayer)) return lastCardPlayer;
        return matching.Count == 1 ? matching[0].netId : null;
    }

    /// <summary>
    /// Records stacks a player applied. <paramref name="powerTotal"/> is the power's amount before
    /// this application; if the ledger disagrees, the power expired or was cleansed since (removals
    /// are not logged) and earlier ownership is stale.
    /// </summary>
    public static void AddStacks(Dictionary<string, long> owned, ulong netId, long amount, long powerTotal)
    {
        if (owned.Values.Sum() != powerTotal) owned.Clear();
        string id = netId.ToString();
        owned[id] = owned.GetValueOrDefault(id) + amount;
    }

    /// <summary>Takes a reduction from the largest owner first, ties broken by lowest net ID.</summary>
    public static void RemoveFromTopOwner(Dictionary<string, long> owned, long amount)
    {
        while (amount > 0 && owned.Count > 0)
        {
            string top = owned.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            long take = Math.Min(amount, owned[top]);
            owned[top] -= take;
            amount -= take;
            if (owned[top] <= 0) owned.Remove(top);
        }
    }

    /// <summary>
    /// Pays each owner their stacks. When less was dealt than the stacks owned (the enemy had
    /// less HP left), the shortfall is split proportionally with largest-remainder rounding.
    /// </summary>
    public static bool PayFromLedger(Dictionary<string, long> owned, int dealt, Action<ulong, long> credit)
    {
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
}
