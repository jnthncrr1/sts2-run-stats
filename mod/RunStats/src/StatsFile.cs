using System.Text.Json;
using Godot;

namespace RunStats;

/// <summary>
/// The run file under RunStats/runs/ is the only store of totals; the tracker re-reads it
/// before every update, so deleting it resets the run and edits made while the game runs
/// are picked up. RunStats/current.json mirrors the active run for the viewer.
/// </summary>
public static class StatsFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Directory => Path.Combine(ProjectSettings.GlobalizePath("user://"), "RunStats");
    public static string CurrentPath => Path.Combine(Directory, "current.json");
    public static string RunPath(string runKey) => Path.Combine(Directory, "runs", SafeFileName(runKey) + ".json");

    public static RunStatsSnapshot Load(string runKey)
    {
        string path = RunPath(runKey);
        if (File.Exists(path))
        {
            try
            {
                RunStatsSnapshot? saved = JsonSerializer.Deserialize<RunStatsSnapshot>(File.ReadAllText(path));
                if (saved != null) return saved;
            }
            catch (Exception e)
            {
                GD.PushWarning($"[RunStats] could not read {path}, starting this run from zero: {e.Message}");
            }
        }
        return new RunStatsSnapshot { RunKey = runKey };
    }

    public static void Save(RunStatsSnapshot snapshot)
    {
        snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        string json = JsonSerializer.Serialize(snapshot, Options);
        WriteAtomic(RunPath(snapshot.RunKey), json);
        WriteAtomic(CurrentPath, json);
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
