using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// Multi-instance loop characteristics on a task-family or <c>subProcess</c> element that binds work: the
/// bound work runs <see cref="Cardinality"/> times (cardinality mode) or once per item of
/// <see cref="CollectionVariable"/> (collection mode), either <see cref="IsSequential"/> (one instance at a
/// time) or in parallel (all instances up front).
/// <para>
/// Exactly one of <see cref="Cardinality"/> or <see cref="CollectionVariable"/> is set; any other shape is
/// rejected during graph construction. Each instance receives a per-iteration scope seeding a zero-based
/// <c>loopIndex</c> and, in collection mode, the current item under <see cref="ItemVariable"/>.
/// </para>
/// </summary>
/// <remarks>
/// Both modes are executable. Collection mode additionally requires the host to declare
/// <c>BpmnHostCapabilities.ScopeVariables</c>, since the interpreter has to read the collection to know how
/// many instances to start; graph construction refuses the definition when that capability is absent. The
/// collection variable itself must be declared on the process.
/// </remarks>
public sealed record BpmnLoopCharacteristics
{
    /// <summary>The default per-iteration frame key for the current item in collection mode.</summary>
    public const string DefaultItemVariable = "item";

    /// <summary>The per-iteration frame key always seeded with the zero-based iteration index.</summary>
    public const string LoopIndexVariable = "loopIndex";

    [JsonConstructor]
    public BpmnLoopCharacteristics(
    bool isSequential = false,
    int? cardinality = null,
    string? collectionVariable = null,
    string? itemVariable = null)
    {
        IsSequential = isSequential;
        Cardinality = cardinality;
        CollectionVariable = string.IsNullOrWhiteSpace(collectionVariable) ? null : collectionVariable.Trim();
        ItemVariable = string.IsNullOrWhiteSpace(itemVariable) ? DefaultItemVariable : itemVariable.Trim();
    }

    /// <summary><c>true</c> = one instance at a time (each starts when the previous completes); <c>false</c> = all instances scheduled up front (parallel).</summary>
    [JsonPropertyName("isSequential")]
    public bool IsSequential { get; }

    /// <summary>The literal instance count (cardinality mode); <c>null</c> in collection mode.</summary>
    [JsonPropertyName("cardinality")]
    public int? Cardinality { get; }

    /// <summary>The name of a declared container-scoped variable holding the collection (collection mode); <c>null</c> in cardinality mode.</summary>
    [JsonPropertyName("collectionVariable")]
    public string? CollectionVariable { get; }

    /// <summary>The per-iteration frame key for the current item (collection mode only); defaults to <see cref="DefaultItemVariable"/>.</summary>
    [JsonPropertyName("itemVariable")]
    public string ItemVariable { get; }

    /// <summary>True when this is collection mode (a collection variable rather than a literal cardinality).</summary>
    [JsonIgnore]
    public bool IsCollectionMode => CollectionVariable is not null;
}
