using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunStats;

/// <summary>Append-only observations, independent of the resettable aggregate stats files.</summary>
public sealed class EventJournal
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _directory;
    private readonly object _gate = new();
    private long _sequence;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public EventJournal(string directory) => _directory = directory;

    public string PathFor(string runKey)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) runKey = runKey.Replace(c, '_');
        return Path.Combine(_directory, "events", runKey, SessionId + ".jsonl");
    }

    public void Append(string runKey, string? combatAttemptId, EventContext context, string type, string source, object data, ActionLink? action = null)
    {
        lock (_gate)
        {
            string line = JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                sessionId = SessionId,
                sequence = ++_sequence,
                recordedAt = DateTimeOffset.UtcNow,
                runKey,
                combatAttemptId,
                context,
                type,
                source,
                actionId = action?.ActionId,
                parentActionId = source == "action" ? action?.ParentActionId : null,
                data,
            }, Options);
            string path = PathFor(runKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Each event is flushed and closed; a quit does not strand a buffered combat.
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes);
            stream.Flush();
        }
    }
}

public sealed record EventContext(int Floor, int Act, int ActFloor, string? Encounter, int? Round, string? Side);
