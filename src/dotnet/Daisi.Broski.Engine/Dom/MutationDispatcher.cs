namespace Daisi.Broski.Engine.Dom;

/// <summary>
/// Per-document fan-out point for DOM mutations. The
/// <see cref="Document"/> lazily allocates one of these the
/// first time a JS-side <c>MutationObserver</c> calls
/// <c>observe()</c> against any node in the tree; from then
/// on every mutation method on <see cref="Node"/>,
/// <see cref="Element"/>, and <see cref="Text"/> notifies
/// the dispatcher, which fans out to each registered
/// observer scope.
///
/// <para>
/// Engine-level concerns (record queueing, microtask
/// scheduling, callback invocation) are intentionally not
/// modeled here — the dispatcher only cares about deciding
/// "does this mutation match this scope?" and pushing the
/// resulting <see cref="MutationRecord"/> into the
/// per-observer delivery callback. The JS-side
/// <c>MutationObserver</c> binding (in
/// <c>BuiltinMutationObserver</c>) implements the queue +
/// microtask + callback dance on top.
/// </para>
/// </summary>
public sealed class MutationDispatcher
{
    private readonly List<Registration> _registrations = new();

    /// <summary>One observe() call's worth of state — the
    /// target node, the options bag, and the delivery
    /// callback the dispatcher should invoke for matching
    /// mutations. Registrations are returned to the caller so
    /// they can be passed to <see cref="Unregister"/> later.</summary>
    public sealed class Registration
    {
        public Node Target { get; }
        public MutationObserveOptions Options { get; }
        public Action<MutationRecord> Deliver { get; }
        internal Registration(Node target, MutationObserveOptions opts, Action<MutationRecord> deliver)
        { Target = target; Options = opts; Deliver = deliver; }
    }

    /// <summary>Subscribe an observer to mutations on
    /// <paramref name="target"/> (and optionally its
    /// descendants when <paramref name="opts"/> sets
    /// <c>Subtree</c>). Returns a token the caller hangs onto
    /// so it can later unregister.</summary>
    public Registration Register(
        Node target, MutationObserveOptions opts, Action<MutationRecord> deliver)
    {
        var reg = new Registration(target, opts, deliver);
        _registrations.Add(reg);
        return reg;
    }

    public void Unregister(Registration reg) => _registrations.Remove(reg);

    /// <summary>Return how many observers are currently
    /// registered. Test-only hook.</summary>
    public int RegistrationCount => _registrations.Count;

    /// <summary>Notify all matching observers of a childList
    /// change. The dispatcher walks every registration and
    /// emits one record per observer whose target is the
    /// mutating parent (or an ancestor when subtree is
    /// set).</summary>
    public void NotifyChildList(
        Node parent,
        IReadOnlyList<Node> added,
        IReadOnlyList<Node> removed,
        Node? prevSibling,
        Node? nextSibling)
    {
        if (_registrations.Count == 0) return;
        foreach (var reg in _registrations)
        {
            if (!reg.Options.ChildList) continue;
            if (!Matches(reg, parent)) continue;
            reg.Deliver(new MutationRecord
            {
                Type = MutationRecordType.ChildList,
                Target = parent,
                AddedNodes = added,
                RemovedNodes = removed,
                PreviousSibling = prevSibling,
                NextSibling = nextSibling,
            });
        }
    }

    public void NotifyAttribute(Element target, string name, string? oldValue)
    {
        if (_registrations.Count == 0) return;
        foreach (var reg in _registrations)
        {
            if (!reg.Options.Attributes) continue;
            if (!Matches(reg, target)) continue;
            if (reg.Options.AttributeFilter is { Count: > 0 } filter
                && !filter.Contains(name)) continue;
            reg.Deliver(new MutationRecord
            {
                Type = MutationRecordType.Attributes,
                Target = target,
                AttributeName = name,
                OldValue = reg.Options.AttributeOldValue ? oldValue : null,
            });
        }
    }

    public void NotifyCharacterData(Node target, string oldValue)
    {
        if (_registrations.Count == 0) return;
        foreach (var reg in _registrations)
        {
            if (!reg.Options.CharacterData) continue;
            if (!Matches(reg, target)) continue;
            reg.Deliver(new MutationRecord
            {
                Type = MutationRecordType.CharacterData,
                Target = target,
                OldValue = reg.Options.CharacterDataOldValue ? oldValue : null,
            });
        }
    }

    /// <summary>True when <paramref name="mutating"/> falls
    /// inside the registration's observed scope: same node
    /// or, with subtree set, any descendant.</summary>
    private static bool Matches(Registration reg, Node mutating)
    {
        if (reg.Target == mutating) return true;
        if (!reg.Options.Subtree) return false;
        // Walk up the ancestor chain looking for the target.
        for (var n = mutating.ParentNode; n is not null; n = n.ParentNode)
        {
            if (n == reg.Target) return true;
        }
        return false;
    }
}

/// <summary>Options bag for one <c>observe()</c> call —
/// mirrors the spec's MutationObserverInit, normalized to
/// the booleans + filter set the dispatcher actually
/// consults.</summary>
public sealed class MutationObserveOptions
{
    public bool ChildList { get; init; }
    public bool Attributes { get; init; }
    public bool CharacterData { get; init; }
    public bool Subtree { get; init; }
    public bool AttributeOldValue { get; init; }
    public bool CharacterDataOldValue { get; init; }
    /// <summary>When non-null + non-empty, attribute mutations
    /// only match when their name is in this set.</summary>
    public IReadOnlyCollection<string>? AttributeFilter { get; init; }
}

/// <summary>One delivered mutation. Fields are populated
/// per-type — the spec defines which fields are meaningful
/// for each <see cref="MutationRecordType"/>.</summary>
public sealed class MutationRecord
{
    public required MutationRecordType Type { get; init; }
    public required Node Target { get; init; }
    public IReadOnlyList<Node> AddedNodes { get; init; } = Array.Empty<Node>();
    public IReadOnlyList<Node> RemovedNodes { get; init; } = Array.Empty<Node>();
    public Node? PreviousSibling { get; init; }
    public Node? NextSibling { get; init; }
    public string? AttributeName { get; init; }
    public string? AttributeNamespace { get; init; }
    public string? OldValue { get; init; }
}

public enum MutationRecordType
{
    ChildList,
    Attributes,
    CharacterData,
}
