using MegaCrit.Sts2.Core.Modding;

namespace RunStats;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const int ViewerPort = 8765;

    public static RunStatsTracker? Tracker { get; private set; }
    public static StatsHttpServer? Server { get; private set; }

    public static void Initialize()
    {
        Tracker = new RunStatsTracker();
        Tracker.Attach();
        Server = new StatsHttpServer(ViewerPort);
        Server.Start();
    }
}
