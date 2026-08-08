using System.Text.Json.Serialization;

namespace Bpmn.Model.State;

/// <summary>
/// One durable compensation-log entry (spec 124): a host element's successful child completion that carries an
/// attached compensation boundary is registered here so a later compensate throw/end can replay its handler.
/// <see cref="CompensableId"/> is <c>comp:N</c> from <c>BpmnExecutionState.Sequence</c> (registration order —
/// total and deterministic; reverse replay = descending id). Written via <c>BpmnStateMutator</c> only. Records
/// are <b>never pruned</b> (parallel to <c>Canceled</c> tokens): <c>Compensated</c>/<c>Claimed</c> entries stay
/// for determinism, audit, and double-replay protection.
/// </summary>
public sealed record BpmnCompensable
{
    [JsonConstructor]
    public BpmnCompensable(
        string compensableId,
        string hostElementId,
        string handlerElementId,
        BpmnCompensableStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compensableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostElementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerElementId);

        CompensableId = compensableId;
        HostElementId = hostElementId;
        HandlerElementId = handlerElementId;
        Status = status;
    }

    /// <summary>The registration id (a pure function of <c>Sequence</c>); reverse-registration replay orders by descending id.</summary>
    public string CompensableId { get; init; }

    /// <summary>The host element whose completion was registered.</summary>
    public string HostElementId { get; init; }

    /// <summary>The compensation handler element replayed for this registration.</summary>
    public string HandlerElementId { get; init; }

    public BpmnCompensableStatus Status { get; init; }
}

public enum BpmnCompensableStatus
{
    /// <summary>Registered by a successful host completion; visible to compensation target selection.</summary>
    Registered,

    /// <summary>Claimed by a compensation run; invisible to later selections (at-most-once). Released back to <see cref="Registered"/> if the run is torn down before it ran.</summary>
    Claimed,

    /// <summary>The handler ran to completion; terminal. Never re-selected, never pruned.</summary>
    Compensated
}
