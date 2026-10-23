namespace RunStats;

public sealed record ActionLink(string ActionId, string? ParentActionId);

/// <summary>Execution-scoped links, not guesses based on the last card a player used.</summary>
public sealed class ActionLinks
{
    private readonly List<(object scope, object source)> _scopes = new();
    private readonly List<(object scope, object source, ActionLink link)> _active = new();
    private readonly Dictionary<object, ActionLink> _known = new(ReferenceEqualityComparer.Instance);

    public ActionLink? Current => _active.Count == 0 ? null : _active[^1].link;

    public object EnterScope(object source)
    {
        object scope = new();
        _scopes.Add((scope, source));
        return scope;
    }

    public (ActionLink link, bool isNew) Begin(object action, object source)
    {
        if (_known.TryGetValue(action, out var known)) return (known, false);
        object scope = _scopes.LastOrDefault(s => ReferenceEquals(s.source, source)).scope ?? action;
        // Repetitions in a wrapper are siblings, not children of its preceding repetition.
        _active.RemoveAll(a => ReferenceEquals(a.scope, scope));
        var link = new ActionLink(Guid.NewGuid().ToString("N"), Current?.ActionId);
        _known.Add(action, link);
        _active.Add((scope, source, link));
        return (link, true);
    }

    public ActionLink? ForAction(object action) => _known.GetValueOrDefault(action);

    public ActionLink? ForSource(object source) =>
        _active.LastOrDefault(a => ReferenceEquals(a.source, source)).link;

    public void ExitScope(object scope)
    {
        _active.RemoveAll(a => ReferenceEquals(a.scope, scope));
        _scopes.RemoveAll(s => ReferenceEquals(s.scope, scope));
    }

    public async Task CompleteScope(Task task, object scope)
    {
        try { await task; }
        finally { ExitScope(scope); }
    }

    public void Clear()
    {
        _scopes.Clear();
        _active.Clear();
        _known.Clear();
    }
}
