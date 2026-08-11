using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bpmn.Model.State;

/// <summary>
/// The live state of one multi-instance loop. A coordinator token
/// (<see cref="TokenId"/>) stays <c>AwaitingChild</c> at the host <see cref="ElementId"/> while its instance
/// sub-tokens run the bound child; this record tracks how many instances remain. It is additive engine state
/// (schema stays version 1) whose only mutation home is <c>BpmnStateMutator</c>; <see cref="LoopId"/> and the
/// instance token ids derive from <c>BpmnExecutionState.Sequence</c>. The record is dropped when the loop
/// completes or its coordinator is cancelled.
/// </summary>
public sealed record BpmnLoopState
{
    [JsonConstructor]
    public BpmnLoopState(
    string loopId,
    string tokenId,
    string elementId,
    bool isSequential,
    int totalCount,
    int nextIndex,
    int completedCount,
    IReadOnlyList<JsonElement>? items = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);

        LoopId = loopId;
        TokenId = tokenId;
        ElementId = elementId;
        IsSequential = isSequential;
        TotalCount = totalCount;
        NextIndex = nextIndex;
        CompletedCount = completedCount;
        Items = items;
    }

    /// <summary>The loop record id (a pure function of <c>Sequence</c>).</summary>
    [JsonPropertyName("loopId")]
    public string LoopId { get; init; }

    /// <summary>The coordinator token id: it stays <c>AwaitingChild</c> at <see cref="ElementId"/> while instances run.</summary>
    [JsonPropertyName("tokenId")]
    public string TokenId { get; init; }

    /// <summary>The multi-instance host element id.</summary>
    [JsonPropertyName("elementId")]
    public string ElementId { get; init; }

    /// <summary><c>true</c> = one instance at a time; <c>false</c> = all instances scheduled up front.</summary>
    [JsonPropertyName("isSequential")]
    public bool IsSequential { get; init; }

    /// <summary>The total number of instances the loop runs.</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    /// <summary>The next zero-based instance index to schedule (sequential mode advances this per completion).</summary>
    [JsonPropertyName("nextIndex")]
    public int NextIndex { get; init; }

    /// <summary>How many instances have completed; the loop finishes when this reaches <see cref="TotalCount"/>.</summary>
    [JsonPropertyName("completedCount")]
    public int CompletedCount { get; init; }

    /// <summary>
    /// The collection-mode per-instance items, snapshotted at loop start: <c>Items[k]</c> is the
    /// value seeded under the host's <c>ItemVariable</c> for instance <c>k</c>. <c>null</c> in cardinality mode.
    /// Persisted on the record because sequential mode seeds instance <c>k+1</c> in a later evaluation (instance
    /// <c>k</c>'s completion) and the snapshot semantics forbid re-reading the variable; parallel mode reads the
    /// same record for uniformity. Additive state growth (schema stays version 1).
    /// </summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<JsonElement>? Items { get; init; }
}
