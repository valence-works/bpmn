using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// One BPMN sequence flow. A flow with a <see cref="ConditionOutcome"/> is conditional: it is taken when
/// the source element's completing child reported that outcome name. Unconditional flows are always
/// taken. <see cref="IsDefault"/> marks the source element's BPMN default flow, taken only when no
/// conditional flow matched (expression-based conditions arrive in a later phase).
/// </summary>
public sealed class BpmnSequenceFlow
{
    [JsonConstructor]
    public BpmnSequenceFlow(
    string flowId,
    string sourceRef,
    string targetRef,
    string? name = null,
    string? conditionOutcome = null,
    bool isDefault = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRef);

        FlowId = flowId;
        SourceRef = sourceRef;
        TargetRef = targetRef;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        ConditionOutcome = string.IsNullOrWhiteSpace(conditionOutcome) ? null : conditionOutcome.Trim();
        IsDefault = isDefault;
    }

    [JsonPropertyName("flowId")]
    public string FlowId { get; }

    [JsonPropertyName("sourceRef")]
    public string SourceRef { get; }

    [JsonPropertyName("targetRef")]
    public string TargetRef { get; }

    [JsonPropertyName("name")]
    public string? Name { get; }

    [JsonPropertyName("conditionOutcome")]
    public string? ConditionOutcome { get; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; }
}
