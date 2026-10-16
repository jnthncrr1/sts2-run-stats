using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace RunStats;

/// <summary>
/// Aggregates per-player totals from the game's combat history into the run file. Every
/// client runs the full deterministic combat simulation, so the local history already
/// contains every player's actions and no network traffic or Harmony patching is required.
/// Totals are never held here: each update loads the run file, applies the new history
/// entries, and writes it back.
/// </summary>
public sealed class RunStatsTracker
{
    private string _runKey = "";
    private string _seed = "";
    private int _processedEntries;

    public void Attach()
    {
        RunManager.Instance.RunStarted += OnRunStarted;
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        CombatManager.Instance.History.Changed += OnHistoryChanged;
        GD.Print("[RunStats] attached; run files under " + StatsFile.Directory);
    }

    private void OnRunStarted(RunState state)
    {
        _seed = state.Rng.StringSeed;
        _runKey = BuildRunKey(state);
        _processedEntries = 0;
        Update(snapshot =>
        {
            snapshot.InProgress.Clear();
            snapshot.InCombat = false;
            foreach (Player player in state.Players) RefreshIdentity(snapshot, player);
        });
    }

    private void OnCombatSetUp(CombatState state)
    {
        _processedEntries = 0;
        Update(snapshot =>
        {
            // A setup without a preceding CombatEnded is a restarted fight: drop the aborted attempt.
            snapshot.InProgress.Clear();
            snapshot.InCombat = true;
            foreach (Player player in state.Players) RefreshIdentity(snapshot, player);
        });
    }

    private void OnCombatEnded(CombatRoom room)
    {
        Update(snapshot =>
        {
            ApplyNewEntries(snapshot);
            snapshot.FoldInProgress();
            snapshot.InCombat = false;
            snapshot.CombatsFinished++;
        });
    }

    private void OnHistoryChanged()
    {
        Update(ApplyNewEntries);
    }

    private void Update(Action<RunStatsSnapshot> change)
    {
        if (_runKey.Length == 0) return;
        try
        {
            RunStatsSnapshot snapshot = StatsFile.Load(_runKey);
            snapshot.Seed = _seed;
            change(snapshot);
            snapshot.RefreshDisplayTotals();
            StatsFile.Save(snapshot);
        }
        catch (Exception e)
        {
            GD.PushWarning("[RunStats] update failed: " + e);
        }
    }

    private void ApplyNewEntries(RunStatsSnapshot snapshot)
    {
        var entries = CombatManager.Instance.History.Entries.ToList();
        if (entries.Count < _processedEntries)
        {
            // History was cleared for a new combat.
            _processedEntries = 0;
        }
        for (int i = _processedEntries; i < entries.Count; i++)
        {
            try
            {
                Record(snapshot, entries[i]);
            }
            catch (Exception e)
            {
                GD.PushWarning($"[RunStats] failed to record {entries[i].GetType().Name}: {e}");
            }
        }
        _processedEntries = entries.Count;
    }

    private static void Record(RunStatsSnapshot snapshot, CombatHistoryEntry entry)
    {
        switch (entry)
        {
            case DamageReceivedEntry damage:
            {
                if (IsFriendly(damage.Receiver)) return;
                Player? owner = OwnerOf(damage.Dealer);
                if (owner == null) return;
                InProgress(snapshot, owner).Damage += damage.Result.UnblockedDamage;
                return;
            }
            case BlockGainedEntry block:
            {
                // Credit whoever played the card; fall back to the creature that received the block.
                Player? owner = block.CardPlay?.Player ?? OwnerOf(block.Receiver);
                if (owner == null || block.Amount <= 0) return;
                InProgress(snapshot, owner).Block += block.Amount;
                return;
            }
            case PowerReceivedEntry power:
            {
                if (IsFriendly(power.Power.Owner)) return;
                Player? owner = OwnerOf(power.Applier);
                if (owner == null) return;
                if (power.Power.GetTypeForAmount(power.Amount) != PowerType.Debuff) return;
                long stacks = (long)Math.Abs(power.Amount);
                if (stacks == 0) return;
                PlayerStats stats = InProgress(snapshot, owner);
                switch (power.Power)
                {
                    case WeakPower: stats.Weak += stacks; break;
                    case VulnerablePower: stats.Vulnerable += stacks; break;
                    default: stats.OtherDebuffs += stacks; break;
                }
                return;
            }
        }
    }

    private static bool IsFriendly(Creature creature) => creature.IsPlayer || creature.IsPet;

    private static Player? OwnerOf(Creature? creature) => creature?.Player ?? creature?.PetOwner;

    private static PlayerStats InProgress(RunStatsSnapshot snapshot, Player player)
    {
        RefreshIdentity(snapshot, player);
        return snapshot.InProgressFor(player.NetId);
    }

    private static void RefreshIdentity(RunStatsSnapshot snapshot, Player player)
    {
        PlayerStats stats = snapshot.CommittedFor(player.NetId);
        stats.Name = ResolveName(player);
        stats.Character = player.Character.Id.Entry;
    }

    /// <summary>
    /// Multiplayer net IDs are Steam IDs, so the platform lookup gives the persona name. In
    /// singleplayer the net ID is a small local number and the lookup echoes it back, so the
    /// local account's own name is used instead.
    /// </summary>
    private static string ResolveName(Player player)
    {
        try
        {
            PlatformType platform = RunManager.Instance.NetService.Platform;
            string name = PlatformUtil.GetPlayerNameRaw(platform, player.NetId);
            if (IsRealName(name, player.NetId)) return name;
            ulong localId = PlatformUtil.GetLocalPlayerId(platform);
            name = PlatformUtil.GetPlayerNameRaw(platform, localId);
            if (IsRealName(name, localId)) return name;
        }
        catch
        {
            // Platform lookups can fail outside Steam; fall through to the character name.
        }
        return player.Character.Id.Entry;
    }

    private static bool IsRealName(string? name, ulong id) =>
        !string.IsNullOrWhiteSpace(name) && name != id.ToString();

    private static string BuildRunKey(RunState state)
    {
        var ids = state.Players.Select(p => p.NetId).OrderBy(id => id);
        return $"{state.Rng.StringSeed}-{state.GameMode}-{string.Join("_", ids)}";
    }
}
