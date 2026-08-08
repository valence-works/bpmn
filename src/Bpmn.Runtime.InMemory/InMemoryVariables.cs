using System.Text.Json;
using Bpmn.Model;
using Bpmn.Semantics;

namespace Bpmn.Runtime.InMemory;

/// <summary>
/// The host's <see cref="IBpmnVariableReader"/>: a mutable, ordinal-keyed dictionary of
/// <see cref="BpmnValue"/>.
/// <para>
/// The three answers the port distinguishes are all reachable here, because collapsing them is exactly the
/// bug the port is shaped to prevent. A name that was never set reads as unknown (<c>TryRead</c> returns
/// <c>false</c>); a name set through <see cref="SetNull"/> reads as known-and-null; a name set through
/// <see cref="SetStoredExternally"/> reads as known-but-not-inline, which a collection-mode multi-instance
/// activity turns into a clear fault rather than a silent zero-instance loop.
/// </para>
/// </summary>
public sealed class InMemoryVariables : IBpmnVariableReader
{
    private readonly Dictionary<string, BpmnValue> _values;

    /// <summary>Creates an empty set of variables.</summary>
    public InMemoryVariables()
    {
        _values = new Dictionary<string, BpmnValue>(StringComparer.Ordinal);
    }

    /// <summary>Creates a set of variables seeded from <paramref name="values"/>.</summary>
    public InMemoryVariables(IEnumerable<KeyValuePair<string, BpmnValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new Dictionary<string, BpmnValue>(StringComparer.Ordinal);
        foreach (var entry in values)
            _values[entry.Key] = entry.Value;
    }

    /// <summary>The declared names, in ordinal order.</summary>
    public IReadOnlyCollection<string> Names => _values.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    /// <summary>The number of variables the host holds.</summary>
    public int Count => _values.Count;

    /// <summary>Reads or writes a variable. Reading a name the host does not hold returns <c>null</c>.</summary>
    public BpmnValue? this[string name]
    {
        get => _values.TryGetValue(name, out var value) ? value : null;
        set
        {
            if (value is null)
                Remove(name);
            else
                Set(name, value);
        }
    }

    /// <inheritdoc />
    public bool TryRead(string name, out BpmnValue value)
    {
        if (name is not null && _values.TryGetValue(name, out var found))
        {
            value = found;
            return true;
        }

        value = BpmnValue.Absent;
        return false;
    }

    /// <summary>Sets a variable to an already-built value.</summary>
    public InMemoryVariables Set(string name, BpmnValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _values[name] = value;
        return this;
    }

    /// <summary>Sets a variable to an inline JSON value.</summary>
    public InMemoryVariables SetJson(string name, JsonElement json, string? typeHint = null) =>
        Set(name, BpmnValue.From(json, typeHint));

    /// <summary>Sets a variable by serializing <paramref name="value"/> to JSON.</summary>
    public InMemoryVariables SetValue<T>(string name, T value, string? typeHint = null) =>
        Set(name, BpmnValue.From(JsonSerializer.SerializeToElement(value), typeHint));

    /// <summary>Sets a variable to a whole number — the shape multi-instance cardinality and loop indices use.</summary>
    public InMemoryVariables SetInteger(string name, int value) => Set(name, BpmnValue.FromInteger(value));

    /// <summary>
    /// Sets a variable to an inline array — the shape a collection-mode multi-instance activity iterates.
    /// </summary>
    public InMemoryVariables SetCollection<T>(string name, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Set(name, BpmnValue.From(JsonSerializer.SerializeToElement(items.ToArray()), BpmnValueTypes.Any));
    }

    /// <summary>Sets a variable the host holds and which is explicitly null. Distinct from never having set it.</summary>
    public InMemoryVariables SetNull(string name) => Set(name, BpmnValue.Null);

    /// <summary>
    /// Sets a variable the host holds outside the inline payload, so the interpreter cannot read its content.
    /// Distinct from both absent and null, and the reason a large collection does not silently iterate zero times.
    /// </summary>
    public InMemoryVariables SetStoredExternally(string name, string? typeHint = null) =>
        Set(name, new BpmnValue(BpmnValuePresence.StoredExternally, typeHint));

    /// <summary>Removes a variable, so it reads as unknown again.</summary>
    public bool Remove(string name) => _values.Remove(name);

    /// <summary>An independent copy. Used to seed a nested scope without letting its writes leak back out.</summary>
    public InMemoryVariables Clone() => new(_values);

    /// <inheritdoc />
    public override string ToString() =>
        _values.Count == 0
            ? "(no variables)"
            : string.Join(", ", Names.Select(name => $"{name}={Describe(_values[name])}"));

    private static string Describe(BpmnValue value) => value.Presence switch
    {
        BpmnValuePresence.Present => value.Json?.ToString() ?? "(present)",
        BpmnValuePresence.Null => "null",
        BpmnValuePresence.StoredExternally => "(stored externally)",
        _ => "(absent)"
    };
}
