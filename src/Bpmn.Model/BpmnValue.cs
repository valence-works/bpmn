using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// A value crossing the boundary between a host and the semantics core, carried as JSON with a
/// host-interpreted type hint. The interpreter reads values (to resolve a multi-instance collection, for
/// example) and seeds them into iteration scopes; it never assigns meaning to a host's type system.
/// </summary>
/// <param name="Presence">Whether the value exists, and whether it is available inline.</param>
/// <param name="TypeHint">A type name from <see cref="BpmnValueTypes"/>, or a host-specific one.</param>
/// <param name="Json">The value itself, when <see cref="Presence"/> is <see cref="BpmnValuePresence.Present"/>.</param>
public sealed record BpmnValue(
    [property: JsonPropertyName("presence")] BpmnValuePresence Presence,
    [property: JsonPropertyName("typeHint")] string? TypeHint = null,
    [property: JsonPropertyName("json")] JsonElement? Json = null)
{
    /// <summary>A value the host does not have.</summary>
    public static BpmnValue Absent { get; } = new(BpmnValuePresence.Absent);

    /// <summary>A value the host has, and which is explicitly null.</summary>
    public static BpmnValue Null { get; } = new(BpmnValuePresence.Null);

    /// <summary>Wraps an available value.</summary>
    public static BpmnValue From(JsonElement json, string? typeHint = null) =>
        new(BpmnValuePresence.Present, typeHint, json);

    /// <summary>Wraps an integer, the shape multi-instance cardinality and loop indices use.</summary>
    public static BpmnValue FromInteger(int value) =>
        new(BpmnValuePresence.Present, BpmnValueTypes.Integer, JsonSerializer.SerializeToElement(value));

    /// <summary>Whether the value is readable inline.</summary>
    public bool HasValue => Presence == BpmnValuePresence.Present && Json.HasValue;
}

/// <summary>Whether and how a value is available to the interpreter.</summary>
public enum BpmnValuePresence
{
    /// <summary>The host has no such value.</summary>
    Absent,

    /// <summary>The host has the value and it is null.</summary>
    Null,

    /// <summary>The host has the value and supplied it inline.</summary>
    Present,

    /// <summary>
    /// The host has the value but holds it outside the inline payload, so the interpreter cannot read it.
    /// Treated as unreadable rather than absent: a multi-instance collection stored this way fails with a
    /// clear diagnostic instead of silently iterating zero times.
    /// </summary>
    StoredExternally
}

/// <summary>Type hints the semantics core itself understands. Hosts may use any other string freely.</summary>
public static class BpmnValueTypes
{
    /// <summary>A whole number. Used for multi-instance cardinality and loop indices.</summary>
    public const string Integer = "integer";

    /// <summary>An unconstrained value. Used for multi-instance collection items.</summary>
    public const string Any = "any";
}
