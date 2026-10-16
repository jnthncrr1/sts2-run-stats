using System.Text.Json;
using Godot;

namespace RunStats;

/// <summary>
/// Persists snapshots under the game's user data directory:
/// RunStats/current.json is always the live run (what the viewer tails), and
/// RunStats/runs/{runKey}.json survives save-and-continue so totals can be reloaded.
/// </summary>
public static class StatsFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Directory => Path.Combine(ProjectSettings.GlobalizePath("user://"), "RunStats");
    public static string CurrentPath => Path.Combine(Directory, "current.json");
    public static string RunPath(string runKey) => Path.Combine(Directory, "runs", SafeFileName(runKey) + ".json");

    public static void Write(RunStatsSnapshot snapshot)
    {
        snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        string json = JsonSerializer.Serialize(snapshot, Options);
        WriteAtomic(CurrentPath, json);
        if (snapshot.RunKey.Length > 0)
        {
            WriteAtomic(RunPath(snapshot.RunKey), json);
        }
    }

    public static RunStatsSnapshot? Read(string runKey)
    {
        string path = RunPath(runKey);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<RunStatsSnapshot>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            GD.PushWarning($"[RunStats] Could not read {path}: {e.Message}");
            return null;
        }
    }

    private static void WriteAtomic(string path, string contents)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    private static string SafeFileName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
