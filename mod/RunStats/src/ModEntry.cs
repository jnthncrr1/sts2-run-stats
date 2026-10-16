using System.Reflection;
using Godot;
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
        Server = new StatsHttpServer(ViewerPort, LoadPage(), ReadStats, GD.Print);
        Server.Start();
    }

    private static byte[] LoadPage()
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RunStats.index.html")
                         ?? throw new InvalidOperationException("index.html resource missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[]? ReadStats() => File.Exists(StatsFile.CurrentPath) ? File.ReadAllBytes(StatsFile.CurrentPath) : null;
}
