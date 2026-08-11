using System.Text.Json.Serialization;

namespace Bpmn.Model.State;

/// <summary>One unit of host work that a BPMN element started and that has not yet completed.</summary>
public sealed record BpmnActiveWork
{
    [JsonConstructor]
    public BpmnActiveWork(
    string nodeId,
    string elementId,
    string tokenId,
    string schedulingCause,
    string? iterationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedulingCause);

        NodeId = nodeId;
        ElementId = elementId;
        TokenId = tokenId;
        SchedulingCause = schedulingCause;
        IterationId = string.IsNullOrWhiteSpace(iterationId) ? null : iterationId;
    }

    /// <summary>The executable node id of the scheduled child activity.</summary>
    [JsonPropertyName("nodeId")]
    public string NodeId { get; init; }

    [JsonPropertyName("elementId")]
    public string ElementId { get; init; }

    [JsonPropertyName("tokenId")]
    public string TokenId { get; init; }

    [JsonPropertyName("schedulingCause")]
    public string SchedulingCause { get; init; }

    /// <summary>
    /// The iteration id the child was scheduled with; <c>null</c>
    /// for ordinary single-run children. A teardown resolves this child's live activity-execution id from the
    /// <c>(NodeId, IterationId)</c> live-child map so N concurrent same-node instances resolve distinctly.
    /// </summary>
    [JsonPropertyName("iterationId")]
    public string? IterationId { get; init; }
}
