using System.Text.Json.Serialization;

namespace RunStats;

/// <summary>Run-wide totals for one player. Serialized straight into the stats file.</summary>
public sealed class PlayerStats
{
    [JsonPropertyName("netId")] public ulong NetId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    [JsonPropertyName("damage")] public long Damage { get; set; }
    [JsonPropertyName("block")] public long Block { get; set; }
    [JsonPropertyName("weak")] public long Weak { get; set; }
    [JsonPropertyName("vulnerable")] public long Vulnerable { get; set; }
    [JsonPropertyName("otherDebuffs")] public long OtherDebuffs { get; set; }

    /// <summary>Enemy attack damage that did not happen because this player lowered the attacker's Strength.</summary>
    [JsonPropertyName("damagePrevented")] public long DamagePrevented { get; set; }

    public PlayerStats Clone() => (PlayerStats)MemberwiseClone();

    public void Add(PlayerStats other)
    {
        Damage += other.Damage;
        Block += other.Block;
        Weak += other.Weak;
        Vulnerable += other.Vulnerable;
        OtherDebuffs += other.OtherDebuffs;
        DamagePrevented += other.DamagePrevented;
    }
}

public sealed class RunStatsSnapshot
{
    [JsonPropertyName("runKey")] public string RunKey { get; set; } = "";
    [JsonPropertyName("seed")] public string Seed { get; set; } = "";
    [JsonPropertyName("inCombat")] public bool InCombat { get; set; }
    [JsonPropertyName("combatsFinished")] public int CombatsFinished { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Totals shown to the viewer: finished combats plus the fight in progress.</summary>
    [JsonPropertyName("players")] public List<PlayerStats> Players { get; set; } = new();

    /// <summary>
    /// Totals through the last finished combat only. A combat restart (retry, or quitting
    /// and continuing mid-fight) replays the fight from scratch, so the in-progress part
    /// is discarded and a continued run starts from these.
    /// </summary>
    [JsonPropertyName("committed")] public List<PlayerStats> Committed { get; set; } = new();

    /// <summary>Contribution of the fight currently in progress.</summary>
    [JsonPropertyName("inProgress")] public List<PlayerStats> InProgress { get; set; } = new();

    /// <summary>
    /// Who owns the stacks of Poison and Demise on each enemy, keyed "enemy|POWER" then
    /// player net ID. Those powers deal their damage with no dealer, so the ticks are credited
    /// from here. Lives and dies with the fight in progress.
    /// </summary>
    [JsonPropertyName("stackLedger")] public Dictionary<string, Dictionary<string, long>> StackLedger { get; set; } = new();

    /// <summary>
    /// Player-caused Strength reductions still active on each enemy, oldest first, keyed by
    /// enemy. Credit for prevented damage goes to the earliest reduction first.
    /// </summary>
    [JsonPropertyName("strengthLedger")] public Dictionary<string, List<StrengthReduction>> StrengthLedger { get; set; } = new();

    /// <summary>Net ID of the player whose card play most recently started; 0 if none.</summary>
    [JsonPropertyName("lastCardPlayNetId")] public ulong LastCardPlayNetId { get; set; }

    /// <summary>
    /// Net ID of the player whose card play is currently resolving; 0 between plays. Block and
    /// summons that arrive without a card reference during a play are credited to this player,
    /// which covers cards that shield or summon for an ally.
    /// </summary>
    [JsonPropertyName("activeCardPlayNetId")] public ulong ActiveCardPlayNetId { get; set; }

    public PlayerStats CommittedFor(ulong netId) => GetOrAdd(Committed, netId);
    public PlayerStats InProgressFor(ulong netId) => GetOrAdd(InProgress, netId);

    public void FoldInProgress()
    {
        foreach (PlayerStats delta in InProgress) CommittedFor(delta.NetId).Add(delta);
        ClearInProgress();
    }

    public void ClearInProgress()
    {
        InProgress.Clear();
        StackLedger.Clear();
        StrengthLedger.Clear();
        LastCardPlayNetId = 0;
        ActiveCardPlayNetId = 0;
    }

    public void RefreshDisplayTotals()
    {
        Players = Committed
            .Select(c =>
            {
                PlayerStats total = c.Clone();
                PlayerStats? delta = InProgress.FirstOrDefault(d => d.NetId == c.NetId);
                if (delta != null) total.Add(delta);
                return total;
            })
            .OrderByDescending(p => p.Damage)
            .ToList();
    }

    private static PlayerStats GetOrAdd(List<PlayerStats> list, ulong netId)
    {
        PlayerStats? stats = list.FirstOrDefault(p => p.NetId == netId);
        if (stats == null)
        {
            stats = new PlayerStats { NetId = netId };
            list.Add(stats);
        }
        return stats;
    }
}

public sealed class StrengthReduction
{
    [JsonPropertyName("netId")] public ulong NetId { get; set; }
    [JsonPropertyName("amount")] public long Amount { get; set; }
}
