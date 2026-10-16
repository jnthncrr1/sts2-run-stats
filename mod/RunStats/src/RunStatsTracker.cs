using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
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
        DoomKillPatch.DoomKilled += OnDoomKilled;
        PetHealPatch.PetHealed += OnPetHealed;
        GD.Print("[RunStats] attached; run files under " + StatsFile.Directory);
    }

    private void OnRunStarted(RunState state)
    {
        _seed = state.Rng.StringSeed;
        _runKey = BuildRunKey(state);
        _processedEntries = 0;
        Update(snapshot =>
        {
            snapshot.ClearInProgress();
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
            snapshot.ClearInProgress();
            StrengthMitigation.ClearPending();
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
            IRunState? run = room.CombatState?.RunState;
            snapshot.Floors.Add(new FloorSnapshot
            {
                Floor = run?.TotalFloor ?? snapshot.Floors.Count + 1,
                Act = (run?.CurrentActIndex ?? 0) + 1,
                ActFloor = run?.ActFloor ?? 0,
                Encounter = room.Encounter?.Id.Entry ?? "",
                Players = snapshot.Committed.Select(p => p.Clone()).ToList(),
            });
        });
    }

    private void OnHistoryChanged()
    {
        Update(ApplyNewEntries);
    }

    private void OnPetHealed(Creature pet, int healed)
    {
        Update(snapshot =>
        {
            ApplyNewEntries(snapshot);
            ulong netId = snapshot.ActiveSourceNetId != 0 ? snapshot.ActiveSourceNetId : pet.PetOwner?.NetId ?? 0;
            if (netId != 0) InProgressFor(snapshot, netId).Block += healed;
        });
    }

    private void OnDoomKilled(Creature victim, int hpAtDeath)
    {
        if (IsFriendly(victim)) return;
        Update(snapshot =>
        {
            ApplyNewEntries(snapshot);
            SourcelessDamage.CreditDoomKill(snapshot, victim, hpAtDeath, (netId, amount) => InProgressFor(snapshot, netId).Damage += amount);
        });
    }

    /// <summary>Reads the run file without writing it back.</summary>
    public void WithSnapshot(Action<RunStatsSnapshot> read)
    {
        if (_runKey.Length == 0) return;
        try
        {
            read(StatsFile.Load(_runKey));
        }
        catch (Exception e)
        {
            GD.PushWarning("[RunStats] read failed: " + e);
        }
    }

    public void Update(Action<RunStatsSnapshot> change)
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
            case CardPlayStartedEntry started:
                snapshot.LastCardPlayNetId = started.CardPlay.Player.NetId;
                snapshot.ActiveCardPlayNetId = started.CardPlay.Player.NetId;
                return;
            case CardPlayFinishedEntry:
                snapshot.ActiveCardPlayNetId = 0;
                return;
            case SummonedEntry summoned:
            {
                // Osty's HP works like block for the party, credited to whoever played the card
                // (summons for an ally) or to the summoner when a power or relic did it.
                if (summoned.Amount <= 0) return;
                ulong netId = snapshot.ActiveSourceNetId != 0 ? snapshot.ActiveSourceNetId : summoned.Actor.Player?.NetId ?? 0;
                if (netId != 0) InProgressFor(snapshot, netId).Block += summoned.Amount;
                return;
            }
            case DamageReceivedEntry damage:
            {
                if (IsFriendly(damage.Receiver))
                {
                    if (damage.Dealer != null && damage.Dealer.IsMonster)
                        StrengthMitigation.CreditPending(damage.Dealer, damage.Receiver, (netId, amount) => InProgressFor(snapshot, netId).DamagePrevented += amount);
                    return;
                }
                int dealt = damage.Result.TotalDamage;
                if (dealt <= 0) return;
                Player? owner = OwnerOf(damage.Dealer);
                if (owner != null)
                {
                    InProgress(snapshot, owner).Damage += dealt;
                }
                else if (damage.Dealer == null)
                {
                    SourcelessDamage.Credit(snapshot, damage.Receiver, dealt, (netId, amount) => InProgressFor(snapshot, netId).Damage += amount);
                }
                return;
            }
            case BlockGainedEntry block:
            {
                if (block.Amount <= 0) return;
                // Credit the card's player; without a card reference, the player whose card or
                // potion is resolving (a triggered power or relic); then Plating's applier when the
                // amount matches (a Plating potion thrown on an ally); otherwise the receiver.
                ulong netId = block.CardPlay?.Player.NetId
                    ?? (snapshot.ActiveSourceNetId != 0 ? snapshot.ActiveSourceNetId
                        : PlatingApplier(block.Receiver, block.Amount)?.NetId
                        ?? OwnerOf(block.Receiver)?.NetId ?? 0);
                if (netId != 0) InProgressFor(snapshot, netId).Block += block.Amount;
                return;
            }
            case PowerReceivedEntry power:
            {
                if (IsFriendly(power.Power.Owner)) return;
                SourcelessDamage.OnPowerChanged(snapshot, power);
                StrengthMitigation.OnPowerChanged(snapshot, power);
                Player? owner = OwnerOf(power.Applier);
                if (owner == null || power.Amount <= 0) return;
                long stacks = (long)power.Amount;
                PlayerStats stats = InProgress(snapshot, owner);
                switch (power.Power)
                {
                    case WeakPower: stats.Weak += stacks; break;
                    case VulnerablePower: stats.Vulnerable += stacks; break;
                }
                return;
            }
        }
    }

    private static bool IsFriendly(Creature creature) => creature.IsPlayer || creature.IsPet;

    private static Player? PlatingApplier(Creature receiver, int amount)
    {
        PlatingPower? plating = receiver.GetPower<PlatingPower>();
        if (plating == null || plating.Amount != amount) return null;
        return OwnerOf(plating.Applier);
    }

    private static Player? OwnerOf(Creature? creature) => creature?.Player ?? creature?.PetOwner;

    private static PlayerStats InProgress(RunStatsSnapshot snapshot, Player player)
    {
        RefreshIdentity(snapshot, player);
        return snapshot.InProgressFor(player.NetId);
    }

    private static PlayerStats InProgressFor(RunStatsSnapshot snapshot, ulong netId) => snapshot.InProgressFor(netId);

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
