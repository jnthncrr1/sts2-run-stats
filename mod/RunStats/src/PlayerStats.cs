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

    public PlayerStats Clone() => (PlayerStats)MemberwiseClone();

    public void Add(PlayerStats other)
    {
        Damage += other.Damage;
        Block += other.Block;
        Weak += other.Weak;
        Vulnerable += other.Vulnerable;
        OtherDebuffs += other.OtherDebuffs;
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
    /// is discarded and restored runs start from these.
    /// </summary>
    [JsonPropertyName("committed")] public List<PlayerStats> Committed { get; set; } = new();
}
