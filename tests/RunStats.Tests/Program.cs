using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using RunStats;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

// Inspect metadata without running the game's Godot-dependent module initializer.
using (var pe = new PEReader(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "sts2.dll"))))
using (var mapperStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CombatEventData.cs")!)
using (var mapperReader = new StreamReader(mapperStream))
using (var recorderStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CombatEventRecorder.cs")!)
using (var recorderReader = new StreamReader(recorderStream))
{
    string mapper = mapperReader.ReadToEnd();
    string recorder = recorderReader.ReadToEnd();
    Check(mapper.Contains("observedPowerAmount = e.Power.Amount", StringComparison.Ordinal)
        && !mapper.Contains("amountBefore =", StringComparison.Ordinal), "Power totals are labeled as observations, not assumed pre-change values");
    var metadata = pe.GetMetadataReader();
    int historyTypes = 0;
    foreach (var handle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(handle);
        if (type.BaseType.IsNil || type.BaseType.Kind != HandleKind.TypeDefinition) continue;
        var parent = metadata.GetTypeDefinition((TypeDefinitionHandle)type.BaseType);
        if (metadata.GetString(parent.Name) != "CombatHistoryEntry") continue;
        string name = metadata.GetString(type.Name);
        Check(mapper.Contains(name + " e =>", StringComparison.Ordinal)
            || recorder.Contains("case " + name, StringComparison.Ordinal), name + " needs explicit mapping or filtering");
        historyTypes++;
    }
    Check(historyTypes >= 17, "The referenced game contains the expected history types");
}

var actions = new ActionLinks();
object card = new(), otherCard = new(), potion = new(), firstPlay = new();
object outerScope = actions.EnterScope(card);
var first = actions.Begin(firstPlay, card);
Check(first.isNew && first.link.ParentActionId == null, "A manual play starts one root action");
Check(actions.Begin(firstPlay, card) == (first.link, false), "History start cannot duplicate the hook's action");
Check(actions.ForSource(card) == first.link && actions.Current == first.link, "Direct effects resolve to the current card action");
Check(actions.ForAction(firstPlay) == first.link, "Explicit card-play references retain the exact action");
Check(actions.ForSource(otherCard) == null, "An unrelated card source is not guessed");
object nestedScope = actions.EnterScope(otherCard);
var nested = actions.Begin(new object(), otherCard);
Check(nested.link.ParentActionId == first.link.ActionId, "Nested auto-play links to its triggering action");
Check(actions.Current == nested.link, "Nested effects use the nested action");
await actions.CompleteScope(Task.CompletedTask, nestedScope);
Check(actions.Current == first.link, "Finishing nested play restores the outer action");
Check(actions.ForSource(otherCard) == null, "Later ticks cannot inherit a completed card's source link");
object recursiveScope = actions.EnterScope(card);
var recursive = actions.Begin(new object(), card);
Check(recursive.link.ParentActionId == first.link.ActionId, "Nested play of the same card instance has a separate scope");
await actions.CompleteScope(Task.CompletedTask, recursiveScope);
Check(actions.Current == first.link, "Closing same-card recursion preserves the outer scope");
var repeat = actions.Begin(new object(), card);
Check(repeat.link.ActionId != first.link.ActionId, "A repeated resolution has a distinct action ID");
Check(repeat.link.ParentActionId == null, "A repetition is not a child of its preceding resolution");
Check(actions.ForAction(firstPlay) == first.link, "Explicit references to the preceding repetition remain stable");
object potionScope = actions.EnterScope(potion);
var potionAction = actions.Begin(potion, potion);
Check(potionAction.link.ParentActionId == repeat.link.ActionId, "Potion effects can nest in an active action");
await actions.CompleteScope(Task.CompletedTask, potionScope);
Check(actions.Current == repeat.link, "Potion completion restores its enclosing card context");
var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Task completing = actions.CompleteScope(deferred.Task, outerScope);
Check(!completing.IsCompleted && actions.Current == repeat.link, "Context survives asynchronous effects until the wrapper completes");
deferred.SetResult();
await completing;
Check(actions.Current == null && actions.ForSource(card) == null, "Post-play effects cannot acquire stale execution context");
Check(actions.ForAction(firstPlay) == first.link, "An explicit historical card-play reference still works after completion");
object failedScope = actions.EnterScope(otherCard);
actions.Begin(new object(), otherCard);
var originalFailure = new InvalidOperationException("original game failure");
Exception? observedFailure = null;
try { await actions.CompleteScope(Task.FromException(originalFailure), failedScope); }
catch (Exception e) { observedFailure = e; }
Check(ReferenceEquals(originalFailure, observedFailure), "Observing completion preserves the game's original exception");
Check(actions.Current == null, "Faulted plays cannot leave stale action context");
object cancelledScope = actions.EnterScope(card);
actions.Begin(new object(), card);
Task cancelled = actions.CompleteScope(Task.FromCanceled(new CancellationToken(true)), cancelledScope);
try { await cancelled; } catch (OperationCanceledException) { }
Check(cancelled.IsCanceled && actions.Current == null, "Cancelled plays stay cancelled and release their context");
actions.Clear();
Check(actions.ForAction(firstPlay) == null && actions.Current == null, "A combat restart clears action associations");
object newScope = actions.EnterScope(card);
var newAttempt = actions.Begin(firstPlay, card);
Check(newAttempt.isNew && newAttempt.link.ActionId != first.link.ActionId, "A new attempt cannot reuse old action IDs");
actions.ExitScope(newScope);

object killingBlow = new { totalDamage = 9, wasTargetKilled = true };
Check(DamageHistoryFallback.ShouldCapture(true, killingBlow, Array.Empty<object>()),
    "The fight-ending 9 damage is captured even when the game omits its history entry");
Check(!DamageHistoryFallback.ShouldCapture(true, killingBlow, new[] { killingBlow }),
    "An actual result already in history is not counted twice");
Check(DamageHistoryFallback.ShouldCapture(true, killingBlow, new object[] { new { totalDamage = 9, wasTargetKilled = true } }),
    "Distinct hits with equal damage are not mistaken for the same result");
Check(!DamageHistoryFallback.ShouldCapture(false, killingBlow, Array.Empty<object>()),
    "Damage outside an active combat is not added to a completed fight");

string directory = Path.Combine(AppContext.BaseDirectory, "journal-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var journal = new EventJournal(directory);
    var context = new EventContext(4, 1, 4, "BIG_DUMMY", 2, "Player");
    string attempt1 = Guid.NewGuid().ToString("N"), attempt2 = Guid.NewGuid().ToString("N");
    journal.Append("seed-mode-player", attempt1, context, "DamageReceivedEntry", "history", new { text = "line one\nline two", netId = "76561198025004891" });
    string path = journal.PathFor("seed-mode-player");
    Check(File.ReadAllLines(path).Length == 1, "An event is readable immediately, without closing a session");
    journal.Append("seed-mode-player", attempt2, context, "CombatSetUp", "lifecycle", new { });
    journal.Append("seed-mode-player", attempt2, context, "CombatEnded", "lifecycle", new { });
    var resumed = new EventJournal(directory);
    resumed.Append("seed-mode-player", Guid.NewGuid().ToString("N"), context, "CombatSetUp", "lifecycle", new { });
    Check(resumed.PathFor("seed-mode-player") != path, "Resumed sessions cannot overwrite earlier recordings");
    var rows = File.ReadAllLines(path).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
    Check(rows.Length == 3, "Restart retains the incomplete attempt");
    Check(rows[0].GetProperty("data").GetProperty("text").GetString()!.Contains('\n'), "Embedded newlines don't break JSONL");
    Check(rows.Select(r => r.GetProperty("sequence").GetInt64()).SequenceEqual(new long[] { 1, 2, 3 }), "Events retain write order");
    Check(rows[0].GetProperty("combatAttemptId").GetString() == attempt1, "First attempt kept separate");
    Check(rows[1].GetProperty("combatAttemptId").GetString() == attempt2, "Retry has distinct attempt ID");
    Check(rows[0].GetProperty("schemaVersion").GetInt32() == 3, "Schema version recorded");
    Check(rows[0].GetProperty("data").GetProperty("netId").GetString() == "76561198025004891", "Steam ID strings preserve every digit");
    Check(rows[0].GetProperty("context").GetProperty("round").GetInt32() == 2, "Round and floor context recorded");
    Check(journal.PathFor("bad/key").StartsWith(Path.Combine(directory, "events", "bad_key")), "Run keys stay inside the log directory");
    journal.Append("another-run", null, context, "RunStarted", "lifecycle", new { });
    Check(File.Exists(journal.PathFor("another-run")) && File.ReadAllLines(path).Length == 3, "Runs are isolated");
    Parallel.For(0, 30, i => journal.Append("concurrent", attempt1, context, "test", "test", new { i }));
    var parallel = File.ReadAllLines(journal.PathFor("concurrent")).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
    Check(parallel.Length == 30 && parallel.Select(r => r.GetProperty("sequence").GetInt64()).Distinct().Count() == 30, "Concurrent appends produce complete ordered records");
    Check(parallel.Select(r => r.GetProperty("sequence").GetInt64()).SequenceEqual(parallel.Select(r => r.GetProperty("sequence").GetInt64()).Order()), "Concurrent writes remain in sequence order");
    var mutable = new Dictionary<string, int> { ["hp"] = 10 };
    journal.Append("frozen", attempt1, context, "test", "test", mutable);
    mutable["hp"] = 0;
    using var frozen = JsonDocument.Parse(File.ReadAllLines(journal.PathFor("frozen"))[0]);
    Check(frozen.RootElement.GetProperty("data").GetProperty("hp").GetInt32() == 10, "Data is serialized immediately, not retained as a live object");
    File.WriteAllText(Path.Combine(directory, "blocked"), "not a directory");
    bool failed = false;
    try { new EventJournal(Path.Combine(directory, "blocked")).Append("run", null, context, "test", "test", new { }); }
    catch (IOException) { failed = true; }
    Check(failed, "Storage failures are surfaced to the recorder's warning handler");
    journal.Append("linked", attempt1, context, "CardPlayed", "action", new { card = "BASH" }, first.link);
    journal.Append("linked", attempt1, context, "DamageReceivedEntry", "history", new { amount = 8 }, first.link);
    journal.Append("linked", attempt1, context, "PowerReceivedEntry", "history", new { power = "VULNERABLE", amount = 2 }, first.link);
    journal.Append("linked", attempt1, context, "CardPlayed", "action", new { card = "STRIKE" }, nested.link);
    var linked = File.ReadAllLines(journal.PathFor("linked")).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
    Check(linked.Take(3).All(r => r.GetProperty("actionId").GetString() == first.link.ActionId), "Bash's play, damage and Vulnerable share one action ID");
    Check(linked.Count(r => r.GetProperty("source").GetString() == "action" && r.GetProperty("actionId").GetString() == first.link.ActionId) == 1, "Several outcomes do not duplicate the play action");
    Check(linked[3].GetProperty("parentActionId").GetString() == first.link.ActionId, "Nested actions persist their parent ID");
    Check(!linked[1].TryGetProperty("parentActionId", out _), "Effects link to their own action rather than duplicating its parent metadata");
    Check(!rows[0].TryGetProperty("actionId", out _), "Unrelated or delayed effects need not invent a card action");
    journal.Append("linked", attempt1, context, "DamageReceivedEntry", "hook",
        new { captureMethod = "afterDamageGiven", payload = new { result = new { totalDamage = 9, wasTargetKilled = true } } }, first.link);
    var lethal = JsonDocument.Parse(File.ReadAllLines(journal.PathFor("linked"))[^1]).RootElement.Clone();
    Check(lethal.GetProperty("actionId").GetString() == first.link.ActionId
        && lethal.GetProperty("data").GetProperty("payload").GetProperty("result").GetProperty("totalDamage").GetInt32() == 9,
        "Fallback killing-blow damage retains the same payload shape and card action ID");
}
finally { Directory.Delete(directory, recursive: true); }
Console.WriteLine($"PASS: {checks} checks");
