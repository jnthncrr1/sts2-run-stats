namespace RunStats;

public static class DamageHistoryFallback
{
    public static bool ShouldCapture(bool combatInProgress, object result, IEnumerable<object> recordedResults) =>
        combatInProgress && !recordedResults.Any(recorded => ReferenceEquals(recorded, result));
}
