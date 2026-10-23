using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace RunStats;

/// <summary>Copies event values immediately instead of serializing mutable game object graphs.</summary>
public sealed class CombatEventData
{
    private readonly Dictionary<CardModel, int> _cards = new(ReferenceEqualityComparer.Instance);

    public void Clear() => _cards.Clear();

    public object History(CombatHistoryEntry entry) => entry switch
    {
        BlockGainedEntry e => new { receiver = Creature(e.Receiver), e.Amount, props = e.Props.ToString() },
        CardAfflictedEntry e => new { card = Card(e.Card), affliction = e.Affliction.Id.Entry },
        CardDiscardedEntry e => new { card = Card(e.Card) },
        CardExhaustedEntry e => new { card = Card(e.Card) },
        CardGeneratedEntry e => new { card = Card(e.Card), creatorNetId = e.Creator?.NetId.ToString() },
        DamageReceivedEntry e => new { dealer = Creature(e.Dealer), card = Card(e.CardSource), result = Damage(e.Result) },
        MonsterPerformedMoveEntry e => new { monster = Creature(e.Actor), move = e.Move.Id, targets = e.Targets?.Select(Creature).ToArray() },
        OrbChanneledEntry e => new { player = Creature(e.Actor), orb = e.Orb.Id.Entry },
        PowerReceivedEntry e => new
        {
            receiver = Creature(e.Actor), applier = Creature(e.Applier), power = e.Power.Id.Entry,
            e.Amount, observedPowerAmount = e.Power.Amount, powerApplier = Creature(e.Power.Applier),
        },
        StarsModifiedEntry e => new { player = Creature(e.Actor), e.Amount },
        SummonedEntry e => new { player = Creature(e.Actor), e.Amount },
        _ => new { actor = Creature(entry.Actor), unsupportedType = entry.GetType().FullName },
    };

    public object? Card(CardModel? card)
    {
        if (card == null) return null;
        if (!_cards.TryGetValue(card, out int instanceId))
        {
            instanceId = _cards.Count + 1;
            _cards.Add(card, instanceId);
        }
        return new
        {
            instanceId, id = card.Id.Entry, ownerNetId = card.Owner.NetId.ToString(),
            upgradeLevel = card.CurrentUpgradeLevel,
        };
    }

    public object? Play(CardPlay? play) => play == null ? null : new
    {
        card = Card(play.Card), playerNetId = play.Player.NetId.ToString(), target = Creature(play.Target),
        resultPile = play.ResultPile.ToString(), play.IsAutoPlay, play.PlayIndex, play.PlayCount,
        resourceCostPaid = play.PlayIndex == 0,
        resources = new { play.Resources.EnergySpent, play.Resources.EnergyValue, play.Resources.StarsSpent, play.Resources.StarValue },
    };

    public static object? Creature(Creature? creature) => creature == null ? null : new
    {
        combatId = creature.CombatId,
        modelId = creature.ModelId.Entry,
        playerNetId = creature.Player?.NetId.ToString(),
        petOwnerNetId = creature.PetOwner?.NetId.ToString(),
        side = creature.Side.ToString(),
        hp = creature.CurrentHp, maxHp = creature.MaxHp, block = creature.Block,
    };

    private static object Damage(DamageResult result) => new
    {
        receiver = Creature(result.Receiver), props = result.Props.ToString(),
        result.BlockedDamage, result.UnblockedDamage, result.OverkillDamage, result.TotalDamage,
        result.WasBlockBroken, result.WasFullyBlocked, result.WasTargetKilled,
    };
}
