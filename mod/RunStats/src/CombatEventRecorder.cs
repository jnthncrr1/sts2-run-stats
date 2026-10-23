using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace RunStats;

public sealed class CombatEventRecorder
{
    private readonly EventJournal _journal;
    private readonly CombatEventData _data = new();
    private readonly ActionLinks _actions = new();
    private RunState? _run;
    private CombatState? _combat;
    private string _runKey = "";
    private string? _attemptId;
    private int _historyCursor;

    public CombatEventRecorder(string directory) => _journal = new EventJournal(directory);

    public void Attach()
    {
        RunManager.Instance.RunStarted += state => Guard(() =>
        {
            _run = state;
            _combat = null;
            _runKey = RunStatsTracker.BuildRunKey(state);
            _attemptId = null;
            _historyCursor = 0;
            _data.Clear();
            _actions.Clear();
            Write("RunStarted", "lifecycle", new
            {
                seed = state.Rng.StringSeed, gameMode = state.GameMode.ToString(), state.AscensionLevel,
                modVersion = typeof(ModEntry).Assembly.GetName().Version?.ToString(),
                gameAssemblyVersion = typeof(CombatManager).Assembly.GetName().Version?.ToString(),
                players = state.Players.Select(PlayerData).ToArray(),
            });
            GD.Print("[RunStats] events at " + _journal.PathFor(_runKey));
        });
        CombatManager manager = CombatManager.Instance;
        manager.CombatSetUp += state => Guard(() =>
        {
            // A missing CombatEnded marker leaves the previous attempt incomplete, never deleted.
            _combat = state;
            _attemptId = Guid.NewGuid().ToString("N");
            _historyCursor = 0;
            _data.Clear();
            _actions.Clear();
            Write("CombatSetUp", "lifecycle", StateData(state));
            DrainHistory();
        });
        manager.History.Changed += () => Guard(DrainHistory);
        manager.CombatBegan += state => Boundary("CombatBegan", state);
        manager.TurnStarted += state => Boundary("TurnStarted", state);
        manager.TurnEnded += state => Boundary("TurnEnded", state);
        manager.CreaturesChanged += state => Boundary("CreaturesChanged", state);
        manager.PlayerEndedTurn += (player, canUnend) => RecordEvent("PlayerEndedTurn", "lifecycle", () => new { playerNetId = player.NetId.ToString(), canUnend });
        manager.PlayerUnendedTurn += player => RecordEvent("PlayerUnendedTurn", "lifecycle", () => new { playerNetId = player.NetId.ToString() });
        manager.CombatWon += room => RecordEvent("CombatWon", "lifecycle", () => new { encounter = room.Encounter?.Id.Entry });
        manager.CombatEnded += room => Guard(() =>
        {
            DrainHistory();
            Write("CombatEnded", "lifecycle", room.CombatState == null ? new { } : StateData(room.CombatState));
            _combat = null;
            _attemptId = null;
            _actions.Clear();
        });
        DoomKillPatch.DoomKilled += (victim, hp) => RecordHook("DoomKill", () => new { victim = CombatEventData.Creature(victim), hpAtDeath = hp });
        PetHealPatch.PetHealed += (pet, healed) => RecordHook("PetHeal", () => new { pet = CombatEventData.Creature(pet), amount = healed, phase = "beforeHeal" });
        MissingDamagePatch.DamageUnrecorded += entry => Guard(() =>
        {
            if (_combat == null || !ReferenceEquals(entry.Receiver.CombatState, _combat)) return;
            DrainHistory();
            ActionLink? action = entry.CardSource == null ? _actions.Current : _actions.ForSource(entry.CardSource);
            Write(nameof(DamageReceivedEntry), "hook", new
            {
                captureMethod = "afterDamageGiven", payload = _data.History(entry),
            }, action);
        });
    }

    public void RecordHook(string type, Func<object> data) => RecordEvent(type, "hook", data);

    public object? EnterActionScope(object source)
    {
        try { return _combat == null ? null : _actions.EnterScope(source); }
        catch (Exception e) { GD.PushWarning("[RunStats] action scope failed: " + e); return null; }
    }

    public Task CompleteActionScope(Task task, object scope) => _actions.CompleteScope(task, scope);

    public void CardPlayed(CardPlay play) => Guard(() =>
    {
        if (_combat == null) return;
        DrainHistory();
        BeginCardPlay(play);
    });

    public void PotionUsed(PotionModel potion, Creature? target) => Guard(() =>
    {
        if (_combat == null) return;
        DrainHistory();
        var (link, isNew) = _actions.Begin(potion, potion);
        if (isNew) Write("PotionUsed", "action", new
        {
            playerNetId = potion.Owner.NetId.ToString(), potion = potion.Id.Entry,
            target = CombatEventData.Creature(target),
        }, link);
    });

    private void BeginCardPlay(CardPlay play)
    {
        var (link, isNew) = _actions.Begin(play, play.Card);
        if (isNew) Write("CardPlayed", "action", _data.Play(play)!, link);
    }

    private void RecordEvent(string type, string source, Func<object> data) => Guard(() =>
    {
        if (_run == null) return;
        DrainHistory();
        Write(type, source, data(), source == "hook" ? _actions.Current : null);
    });

    private void Boundary(string type, CombatState state) => Guard(() =>
    {
        // CreaturesChanged can fire while the next combat is still being set up.
        if (!ReferenceEquals(state, _combat)) return;
        DrainHistory();
        Write(type, "lifecycle", StateData(state));
    });

    private void DrainHistory()
    {
        if (_run == null || _combat == null) return;
        var entries = CombatManager.Instance.History.Entries.ToList();
        if (entries.Count < _historyCursor)
        {
            Write("HistoryCleared", "lifecycle", new { previousEntryCount = _historyCursor });
            _historyCursor = 0;
            _actions.Clear();
        }
        while (_historyCursor < entries.Count)
        {
            var entry = entries[_historyCursor];
            switch (entry)
            {
                case CardPlayStartedEntry started:
                    BeginCardPlay(started.CardPlay);
                    _historyCursor++;
                    continue;
                case CardPlayFinishedEntry:
                case CardDrawnEntry:
                case CreatureAttackedEntry:
                case EnergySpentEntry:
                case PotionUsedEntry:
                    _historyCursor++;
                    continue;
            }
            object payload;
            try
            {
                payload = _data.History(entry);
            }
            catch (Exception e)
            {
                // Preserve the event's place in the stream even if an unfamiliar payload fails.
                payload = new { captureError = e.ToString() };
                GD.PushWarning("[RunStats] event payload failed: " + e);
            }
            ActionLink? action = entry switch
            {
                BlockGainedEntry { CardPlay: not null } block => _actions.ForAction(block.CardPlay),
                DamageReceivedEntry { CardSource: not null } damage => _actions.ForSource(damage.CardSource),
                _ => _actions.Current,
            };
            Write(entry.GetType().Name, "history", new { historyIndex = _historyCursor, payload }, action);
            _historyCursor++;
        }
    }

    private void Write(string type, string source, object data, ActionLink? action = null)
    {
        if (_run == null) return;
        _journal.Append(_runKey, _attemptId, new EventContext(
            _run.TotalFloor, _run.CurrentActIndex + 1, _run.ActFloor,
            _combat?.Encounter?.Id.Entry, _combat?.RoundNumber, _combat?.CurrentSide.ToString()), type, source, data, action);
    }

    private object PlayerData(Player player) => new
    {
        netId = player.NetId.ToString(), name = RunStatsTracker.ResolveName(player), character = player.Character.Id.Entry,
        creature = CombatEventData.Creature(player.Creature), player.Gold,
        deck = player.Deck.Cards.Select(_data.Card).ToArray(),
        relics = player.Relics.Select(r => r.Id.Entry).ToArray(),
        potions = player.Potions.Select(p => p.Id.Entry).ToArray(),
        combat = player.PlayerCombatState == null ? null : new
        {
            player.PlayerCombatState.TurnNumber, player.PlayerCombatState.Energy, player.PlayerCombatState.Stars,
            hand = player.PlayerCombatState.Hand.Cards.Select(_data.Card).ToArray(),
        },
    };

    private object StateData(CombatState state) => new
    {
        creatures = state.Creatures.Select(c => new
        {
            creature = CombatEventData.Creature(c),
            powers = c.Powers.Select(p => new { id = p.Id.Entry, p.Amount, applier = CombatEventData.Creature(p.Applier) }).ToArray(),
        }).ToArray(),
        players = state.Players.Select(PlayerData).ToArray(),
    };

    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { GD.PushWarning("[RunStats] event recording failed: " + e); }
    }
}
