using RunStats;

const ulong P1 = 1, P2 = 2;
int checks = 0;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    checks++;
}

Dictionary<ulong, long> Credits(Action<Action<ulong, long>> run)
{
    var credited = new Dictionary<ulong, long>();
    run((netId, amount) => credited[netId] = credited.GetValueOrDefault(netId) + amount);
    return credited;
}

bool Paid(Dictionary<ulong, long> credited, params (ulong netId, long amount)[] expected) =>
    credited.Count == expected.Length && expected.All(e => credited.GetValueOrDefault(e.netId) == e.amount);

// Killing blow. The game skips the history entry for the hit that ends a fight, so the
// AfterDamageGiven hook supplies it unless that same result is already in history.
{
    object timesUpLethal = new { totalDamage = 20, wasTargetKilled = true };
    object timesUpSurvived = new { totalDamage = 20, wasTargetKilled = false };
    Check(DamageHistoryFallback.ShouldCapture(true, timesUpLethal, Array.Empty<object>()),
        "A fight-ending Times Up missing from history is captured");
    Check(!DamageHistoryFallback.ShouldCapture(true, timesUpSurvived, new[] { timesUpSurvived }),
        "A Times Up the enemy survives is already in history and is not counted twice");
    Check(DamageHistoryFallback.ShouldCapture(true, timesUpLethal, new[] { new { totalDamage = 20, wasTargetKilled = true } }),
        "An earlier hit with the same damage is not mistaken for the killing blow");
    Check(!DamageHistoryFallback.ShouldCapture(false, timesUpLethal, Array.Empty<object>()),
        "Damage outside an active combat is ignored");
}

// Poison from two players, ticking down until it kills the enemy.
{
    var poison = new Dictionary<string, long>();
    StackAttribution.AddStacks(poison, P1, 5, powerTotal: 0);
    StackAttribution.AddStacks(poison, P2, 3, powerTotal: 5);
    Check(Paid(Credits(c => StackAttribution.PayFromLedger(poison, 8, c)), (P1, 5), (P2, 3)),
        "A full 8-stack tick pays each player their own stacks");

    StackAttribution.RemoveFromTopOwner(poison, 1);
    Check(poison[P1.ToString()] == 4 && poison[P2.ToString()] == 3,
        "The per-turn decrement comes off the player with the most stacks");

    // 7 stacks left, enemy at 4 HP: 4/7 of 4 is 2.29 and 3/7 of 4 is 1.71, and the leftover point
    // goes to the larger remainder.
    Check(Paid(Credits(c => StackAttribution.PayFromLedger(poison, 4, c)), (P1, 2), (P2, 2)),
        "A lethal short tick splits the remaining HP by stack ownership and sums to the damage dealt");

    StackAttribution.AddStacks(poison, P2, 2, powerTotal: 0);
    Check(poison.Count == 1 && poison[P2.ToString()] == 2,
        "Re-applying after the power expired drops the stale ownership");
}

// Demise deals its whole stack count, except the tick that kills, which deals the HP left.
{
    Check(StackAttribution.MatchesStacks(10, 10, killed: false), "A full Demise tick matches its stacks");
    Check(StackAttribution.MatchesStacks(10, 7, killed: true), "A lethal Demise tick on a 7 HP enemy matches its 10 stacks");
    Check(!StackAttribution.MatchesStacks(10, 7, killed: false), "A short hit that did not kill is some other source");
    Check(!StackAttribution.MatchesStacks(10, 0, killed: true), "A zero-damage hit never matches");

    var demise = new Dictionary<string, long> { [P1.ToString()] = 6, [P2.ToString()] = 4 };
    Check(Paid(Credits(c => StackAttribution.PayFromLedger(demise, 7, c)), (P1, 4), (P2, 3)),
        "The lethal 7 is split 6:4 between the Demise owners");
}

// Strangle: one instance per applier, firing when that applier plays a card.
{
    var strangles = new List<(ulong, long)> { (P1, 4), (P2, 6) };
    Check(StackAttribution.PickStrangleApplier(strangles, 4, killed: false, lastCardPlayer: P1) == P1,
        "P1's card fires P1's 4 Strangle");
    Check(StackAttribution.PickStrangleApplier(strangles, 3, killed: true, lastCardPlayer: P2) == P2,
        "P2's Strangle finishing a 3 HP enemy goes to P2, not the first applier");
    Check(StackAttribution.PickStrangleApplier(strangles, 3, killed: false, lastCardPlayer: P2) == null,
        "A non-lethal 3 matches neither instance and stays uncredited");
    Check(StackAttribution.PickStrangleApplier(new List<(ulong, long)> { (P1, 5) }, 2, killed: false, lastCardPlayer: P2) == P1,
        "With a single applier any Strangle damage is theirs");
}

// Haunt: a buff on the player, firing on their own Soul play.
{
    var haunts = new List<(ulong, long)> { (P1, 5), (P2, 5) };
    Check(StackAttribution.PickHaunter(haunts, 5, killed: false, lastCardPlayer: P2) == P2,
        "Equal Haunts are told apart by who played the Soul");
    Check(StackAttribution.PickHaunter(haunts, 2, killed: true, lastCardPlayer: P1) == P1,
        "P1's Haunt finishing a 2 HP enemy goes to P1");
    Check(StackAttribution.PickHaunter(haunts, 2, killed: false, lastCardPlayer: P1) == null,
        "A non-lethal 2 is not a Haunt hit");
    Check(StackAttribution.PickHaunter(new List<(ulong, long)> { (P1, 5) }, 2, killed: true, lastCardPlayer: 0) == P1,
        "A lone lethal Haunt is credited without a card play");
}

// Doom kills split the victim's HP by stack ownership.
{
    Check(Paid(Credits(c => StackAttribution.SplitProportionally(new[] { (P1, 1L), (P2, 1L) }, 3, c)), (P1, 2), (P2, 1)),
        "An exact tie gives the leftover point to the lowest net ID");
    Check(Credits(c => StackAttribution.SplitProportionally(new[] { (P1, 3L), (P2, 0L) }, 9, c)) is { Count: 1 } only && only[P1] == 9,
        "Players without stacks get nothing");
}

Console.WriteLine($"PASS: {checks} checks");
