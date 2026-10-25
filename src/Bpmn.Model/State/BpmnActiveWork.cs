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
    /// The multi-instance iteration the work was scheduled under; <c>null</c> for ordinary work.
    /// The scope-local <see cref="TokenId"/> identifies each invocation, including concurrent ordinary work
    /// sharing the same binding and null iteration.
    /// </summary>
    [JsonPropertyName("iterationId")]
    public string? IterationId { get; init; }
}
