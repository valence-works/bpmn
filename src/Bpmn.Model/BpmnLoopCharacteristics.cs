using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// Multi-instance loop characteristics (spec 121) authored on a task-family or <c>subProcess</c> host
/// element that binds a child: the bound child runs <see cref="Cardinality"/> times (cardinality mode) or
/// once per item of <see cref="CollectionVariable"/> (collection mode), either <see cref="IsSequential"/>
/// (one instance at a time) or in parallel (all instances up front). Exactly one of
/// <see cref="Cardinality"/> XOR <see cref="CollectionVariable"/> is set — <see cref="BpmnGraph"/> rejects
/// any other shape. Each instance receives a per-iteration frame seeding a zero-based <c>loopIndex</c> (and,
/// in collection mode, the item under <see cref="ItemVariable"/>).
/// </summary>
/// <remarks>
/// Collection mode is authoring-modeled but <b>not executable in this slice</b> (its per-instance item read
/// needs a container-variable read seam that structural evaluations do not yet expose); the graph validator
/// rejects it and the importer degrades it. Cardinality mode is fully executable.
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
