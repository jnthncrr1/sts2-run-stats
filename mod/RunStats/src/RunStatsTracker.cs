using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Combat;
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
/// Aggregates per-player totals from the game's combat history. Every client runs the full
/// deterministic combat simulation, so the local history already contains every player's
/// actions and no network traffic or Harmony patching is required.
/// </summary>
public sealed class RunStatsTracker
{
    private readonly Dictionary<ulong, PlayerStats> _players = new();
    private readonly Stopwatch _sinceLastWrite = Stopwatch.StartNew();
    private static readonly TimeSpan WriteThrottle = TimeSpan.FromMilliseconds(250);

    private string _runKey = "";
    private string _seed = "";
    private int _combatsFinished;
    private bool _inCombat;
    private int _processedEntries;
    private bool _dirty;

    public void Attach()
    {
        RunManager.Instance.RunStarted += OnRunStarted;
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        CombatManager.Instance.TurnEnded += _ => Flush(force: true);
        CombatManager.Instance.History.Changed += OnHistoryChanged;
        GD.Print("[RunStats] attached; writing to " + StatsFile.CurrentPath);
    }

    private void OnRunStarted(RunState state)
    {
        _seed = state.Rng.StringSeed;
        _runKey = BuildRunKey(state);
        _players.Clear();
        _combatsFinished = 0;
        _processedEntries = 0;
        _inCombat = false;

        // A saved run being continued: restore totals accumulated before the save.
        RunStatsSnapshot? saved = StatsFile.Read(_runKey);
        if (saved != null)
        {
            foreach (PlayerStats p in saved.Players) _players[p.NetId] = p;
            _combatsFinished = saved.CombatsFinished;
            GD.Print($"[RunStats] restored {saved.Players.Count} players for run {_runKey}");
        }

        foreach (Player player in state.Players) EnsurePlayer(player);
        Flush(force: true);
    }

    private void OnCombatSetUp(CombatState state)
    {
        _inCombat = true;
        _processedEntries = 0;
        foreach (Player player in state.Players) EnsurePlayer(player);
        Flush(force: true);
    }

    private void OnCombatEnded(CombatRoom room)
    {
        ProcessNewEntries();
        _inCombat = false;
        _combatsFinished++;
        Flush(force: true);
    }

    private void OnHistoryChanged()
    {
        ProcessNewEntries();
        Flush(force: false);
    }

    private void ProcessNewEntries()
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
                Record(entries[i]);
            }
            catch (Exception e)
            {
                GD.PushWarning($"[RunStats] failed to record {entries[i].GetType().Name}: {e}");
            }
        }
        _processedEntries = entries.Count;
    }

    private void Record(MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry entry)
    {
        switch (entry)
        {
            case DamageReceivedEntry damage:
            {
                if (IsFriendly(damage.Receiver)) return;
                Player? owner = OwnerOf(damage.Dealer);
                if (owner == null) return;
                EnsurePlayer(owner).Damage += damage.Result.UnblockedDamage;
                _dirty = true;
                return;
            }
            case BlockGainedEntry block:
            {
                // Credit whoever played the card; fall back to the creature that received the block.
                Player? owner = block.CardPlay?.Player ?? OwnerOf(block.Receiver);
                if (owner == null || block.Amount <= 0) return;
                EnsurePlayer(owner).Block += block.Amount;
                _dirty = true;
                return;
            }
            case PowerReceivedEntry power:
            {
                if (IsFriendly(power.Power.Owner)) return;
                Player? owner = OwnerOf(power.Applier);
                if (owner == null) return;
                PowerType type = power.Power.GetTypeForAmount(power.Amount);
                if (type != PowerType.Debuff) return;
                long stacks = (long)Math.Abs(power.Amount);
                if (stacks == 0) return;
                PlayerStats stats = EnsurePlayer(owner);
                switch (power.Power)
                {
                    case WeakPower: stats.Weak += stacks; break;
                    case VulnerablePower: stats.Vulnerable += stacks; break;
                    default: stats.OtherDebuffs += stacks; break;
                }
                _dirty = true;
                return;
            }
        }
    }

    private static bool IsFriendly(Creature creature) => creature.IsPlayer || creature.IsPet;

    private static Player? OwnerOf(Creature? creature) => creature?.Player ?? creature?.PetOwner;

    private PlayerStats EnsurePlayer(Player player)
    {
        if (!_players.TryGetValue(player.NetId, out PlayerStats? stats))
        {
            stats = new PlayerStats { NetId = player.NetId };
            _players[player.NetId] = stats;
        }
        stats.Name = ResolveName(player);
        stats.Character = player.Character.Id.Entry;
        return stats;
    }

    private static string ResolveName(Player player)
    {
        try
        {
            string name = PlatformUtil.GetPlayerNameRaw(RunManager.Instance.NetService.Platform, player.NetId);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch
        {
            // Platform lookups can fail outside Steam; fall through to the character name.
        }
        return player.Character.Id.Entry;
    }

    private static string BuildRunKey(RunState state)
    {
        var ids = state.Players.Select(p => p.NetId).OrderBy(id => id);
        return $"{state.Rng.StringSeed}-{state.GameMode}-{string.Join("_", ids)}";
    }

    private void Flush(bool force)
    {
        if (!force && (!_dirty || _sinceLastWrite.Elapsed < WriteThrottle)) return;
        if (_runKey.Length == 0 && _players.Count == 0) return;
        try
        {
            StatsFile.Write(new RunStatsSnapshot
            {
                RunKey = _runKey,
                Seed = _seed,
                InCombat = _inCombat,
                CombatsFinished = _combatsFinished,
                Players = _players.Values.OrderByDescending(p => p.Damage).ToList(),
            });
            _dirty = false;
            _sinceLastWrite.Restart();
        }
        catch (Exception e)
        {
            GD.PushWarning("[RunStats] write failed: " + e.Message);
        }
    }
}
