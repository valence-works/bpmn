using System.Text.Json.Serialization;

namespace Bpmn.Model.State;

/// <summary>
/// One in-flight compensation replay. A compensate throw/end token becomes the run coordinator
/// (stays <c>AwaitingChild</c>) while its claimed handlers run one at a time in reverse registration order.
/// <see cref="RunId"/> is <c>comprun:N</c> from <c>BpmnExecutionState.Sequence</c>; the only mutation home is
/// <c>BpmnStateMutator</c>. The record is dropped when the last handler completes (the throw then routes/consumes)
/// or when the coordinator token is cancelled (its unrun <c>Claimed</c> compensables release back to
/// <c>Registered</c>).
/// </summary>
public sealed record BpmnCompensationRun
{
    [JsonConstructor]
    public BpmnCompensationRun(
    string runId,
    string throwTokenId,
    IReadOnlyList<string> pendingCompensableIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(throwTokenId);

        RunId = runId;
        ThrowTokenId = throwTokenId;
        PendingCompensableIds = pendingCompensableIds ?? [];
    }

    /// <summary>The run record id (a pure function of <c>Sequence</c>).</summary>
    [JsonPropertyName("runId")]
    public string RunId { get; init; }

    /// <summary>The compensate throw/end token that coordinates this run (stays <c>AwaitingChild</c> until the run finishes).</summary>
    [JsonPropertyName("throwTokenId")]
    public string ThrowTokenId { get; init; }

    /// <summary>The still-to-run claimed compensables, ordered <b>descending by registration</b> (reverse completion order); the head is the currently running handler.</summary>
    [JsonPropertyName("pendingCompensableIds")]
    public IReadOnlyList<string> PendingCompensableIds { get; init; }
}
