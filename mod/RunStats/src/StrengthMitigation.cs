using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace RunStats;

/// <summary>
/// Credits enemy attack damage that never happened because players lowered the attacker's
/// Strength (Piercing Wail, Dark Shackles, permanent Strength-down effects, ...), and debits
/// damage players caused by giving enemies Strength. All of those end up as StrengthPower changes
/// on the enemy, so the run file keeps an ordered ledger of who changed each enemy's Strength by
/// how much. For every enemy attack the damage pipeline is re-run with
/// the reductions restored one at a time, oldest reduction first, and each player is credited
/// with the difference their reduction made. Once the attack is already at zero, later
/// reductions earn nothing, which is the "first to reduce gets the credit" rule.
/// </summary>
public static class StrengthMitigation
{
    /// <summary>
    /// Prevented damage computed when the pipeline ran, waiting for the matching damage entry.
    /// Keyed by dealer and target combat IDs; lives only between the two calls within one hit.
    /// </summary>
    private static readonly Dictionary<(uint, uint), List<(ulong netId, long prevented)>> _pending = new();

    /// <summary>
    /// Any player-caused Strength change on an enemy joins the ledger: reductions as positive
    /// amounts, buffs (cards that give enemies Strength as a cost) as negative ones, which end up
    /// as negative prevented damage even if a later reduction cancels them out.
    /// </summary>
    public static void OnPowerChanged(RunStatsSnapshot snapshot, PowerReceivedEntry entry)
    {
        if (entry.Power is not StrengthPower || entry.Amount == 0) return;
        var applier = entry.Applier?.Player ?? entry.Applier?.PetOwner;
        if (applier == null) return;
        string key = EnemyKey(entry.Power.Owner);
        if (!snapshot.StrengthLedger.TryGetValue(key, out var reductions))
        {
            reductions = new List<StrengthReduction>();
            snapshot.StrengthLedger[key] = reductions;
        }
        reductions.Add(new StrengthReduction { NetId = applier.NetId, Amount = (long)-entry.Amount });
    }

    /// <summary>
    /// A temporary Strength change expired. Removes that much from the owner's most recent
    /// entries of the same sign (positive for an expired reduction, negative for an expired buff).
    /// </summary>
    public static void OnTemporaryChangeReverted(RunStatsSnapshot snapshot, Creature enemy, ulong netId, long signedAmount)
    {
        if (signedAmount == 0 || !snapshot.StrengthLedger.TryGetValue(EnemyKey(enemy), out var reductions)) return;
        int sign = Math.Sign(signedAmount);
        long remaining = Math.Abs(signedAmount);
        for (int i = reductions.Count - 1; i >= 0 && remaining > 0; i--)
        {
            if (reductions[i].NetId != netId || Math.Sign(reductions[i].Amount) != sign) continue;
            long take = Math.Min(remaining, Math.Abs(reductions[i].Amount));
            reductions[i].Amount -= sign * take;
            remaining -= take;
            if (reductions[i].Amount == 0) reductions.RemoveAt(i);
        }
        if (reductions.Count == 0) snapshot.StrengthLedger.Remove(EnemyKey(enemy));
    }

    /// <summary>Called from the damage entry: pays out whatever the pipeline hook computed for this hit.</summary>
    public static void CreditPending(Creature dealer, Creature target, Action<ulong, long> credit)
    {
        if (dealer.CombatId == null || target.CombatId == null) return;
        if (!_pending.Remove((dealer.CombatId.Value, target.CombatId.Value), out var shares)) return;
        foreach (var share in shares) credit(share.netId, share.prevented);
    }

    public static void ClearPending() => _pending.Clear();

    /// <summary>
    /// Runs after the game computed an enemy attack's damage. Re-runs the same pipeline with the
    /// player reductions progressively restored and stores each player's share for the entry.
    /// </summary>
    public static void OnDamageComputed(RunStatsSnapshot snapshot, IRunState runState, ICombatState? combatState, Creature target, Creature dealer,
        decimal baseDamage, ValueProp props, CardModel? cardSource, CardPlay? cardPlay, decimal actual)
    {
        if (!snapshot.StrengthLedger.TryGetValue(EnemyKey(dealer), out var reductions) || reductions.Count == 0) return;
        if (dealer.CombatId == null || target.CombatId == null) return;

        // Sanity check: our replica of the pipeline must reproduce the game's own result.
        decimal replica = Pipeline(runState, combatState, target, dealer, baseDamage, props, cardSource, cardPlay, 0m);
        if ((int)replica != (int)actual) return;

        var shares = new List<(ulong, long)>();
        decimal restored = reductions.Sum(r => r.Amount);
        decimal previous = Pipeline(runState, combatState, target, dealer, baseDamage, props, cardSource, cardPlay, restored);
        foreach (StrengthReduction reduction in reductions)
        {
            restored -= reduction.Amount;
            decimal next = Pipeline(runState, combatState, target, dealer, baseDamage, props, cardSource, cardPlay, restored);
            long prevented = (long)previous - (long)next;
            if (prevented != 0) shares.Add((reduction.NetId, prevented));
            previous = next;
        }
        if (shares.Count > 0) _pending[(dealer.CombatId.Value, target.CombatId.Value)] = shares;
    }

    /// <summary>
    /// Mirror of the game's additive → multiplicative → cap damage pipeline, with an extra
    /// additive offset standing in for Strength the player reductions took away.
    /// </summary>
    private static decimal Pipeline(IRunState runState, ICombatState? combatState, Creature target, Creature dealer,
        decimal baseDamage, ValueProp props, CardModel? cardSource, CardPlay? cardPlay, decimal strengthOffset)
    {
        decimal num = baseDamage;
        foreach (AbstractModel model in runState.IterateHookListeners(combatState))
            num += model.ModifyDamageAdditive(target, num, props, dealer, cardSource, cardPlay);
        num += strengthOffset;
        foreach (AbstractModel model in runState.IterateHookListeners(combatState))
            num *= model.ModifyDamageMultiplicative(target, num, props, dealer, cardSource, cardPlay);
        decimal cap = decimal.MaxValue;
        foreach (AbstractModel model in runState.IterateHookListeners(combatState))
            cap = Math.Min(cap, model.ModifyDamageCap(target, props, dealer, cardSource, cardPlay));
        num = Math.Min(num, cap);
        return Math.Max(0m, num);
    }

    public static string EnemyKey(Creature enemy) => enemy.CombatId?.ToString() ?? enemy.Name;
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
public static class ModifyDamagePatch
{
    [HarmonyPostfix]
    public static void Postfix(IRunState runState, ICombatState? combatState, Creature? target, Creature? dealer, decimal damage, ValueProp props,
        CardModel? cardSource, CardPlay? cardPlay, ModifyDamageHookType modifyDamageHookType, CardPreviewMode previewMode, decimal __result)
    {
        try
        {
            if (previewMode != CardPreviewMode.None || modifyDamageHookType != ModifyDamageHookType.All) return;
            if (target == null || dealer == null || !dealer.IsMonster || !(target.IsPlayer || target.IsPet)) return;
            if (!props.IsPoweredAttack()) return;
            ModEntry.Tracker?.WithSnapshot(snapshot =>
                StrengthMitigation.OnDamageComputed(snapshot, runState, combatState, target, dealer, damage, props, cardSource, cardPlay, __result));
        }
        catch (Exception e)
        {
            Godot.GD.PushWarning("[RunStats] ModifyDamage hook failed: " + e);
        }
    }
}

[HarmonyPatch(typeof(TemporaryStrengthPower), nameof(TemporaryStrengthPower.AfterSideTurnEnd))]
public static class TemporaryStrengthRevertPatch
{
    [HarmonyPrefix]
    public static void Prefix(TemporaryStrengthPower __instance, IEnumerable<Creature> participants)
    {
        try
        {
            if (!participants.Contains(__instance.Owner) || !__instance.Owner.IsMonster) return;
            var applier = __instance.Applier?.Player ?? __instance.Applier?.PetOwner;
            if (applier == null) return;
            bool isReduction = __instance.Type == MegaCrit.Sts2.Core.Entities.Powers.PowerType.Debuff;
            long signedAmount = isReduction ? __instance.Amount : -__instance.Amount;
            ModEntry.Tracker?.Update(snapshot =>
                StrengthMitigation.OnTemporaryChangeReverted(snapshot, __instance.Owner, applier.NetId, signedAmount));
        }
        catch (Exception e)
        {
            Godot.GD.PushWarning("[RunStats] temporary strength hook failed: " + e);
        }
    }
}
