using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// One BPMN sequence flow, connecting a source element to a target element.
/// <para>
/// A flow with a <see cref="ConditionOutcome"/> is conditional: it is taken when the work bound to the
/// source element reported that outcome name. Unconditional flows are always taken.
/// <see cref="IsDefault"/> marks the source element's BPMN default flow, taken only when no conditional
/// flow matched.
/// </para>
/// <para>
/// Conditions are matched by outcome <i>name</i>. This library evaluates no expressions - there is no
/// FEEL, JUEL, or script engine here - so a host that wants expression-based conditions resolves them and
/// reports the resulting outcome name.
/// </para>
/// </summary>
public sealed class BpmnSequenceFlow
{
    /// <summary>Creates a sequence flow.</summary>
    [JsonConstructor]
    public BpmnSequenceFlow(
        string flowId,
        string sourceRef,
        string targetRef,
        string? name = null,
        string? conditionOutcome = null,
        bool isDefault = false,
        BpmnExtensions? extensions = null)
    {
        if (string.IsNullOrWhiteSpace(flowId))
            throw new ArgumentException("A flow id is required.", nameof(flowId));
        if (string.IsNullOrWhiteSpace(sourceRef))
            throw new ArgumentException("A source element id is required.", nameof(sourceRef));
        if (string.IsNullOrWhiteSpace(targetRef))
            throw new ArgumentException("A target element id is required.", nameof(targetRef));

        FlowId = flowId;
        SourceRef = sourceRef;
        TargetRef = targetRef;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        ConditionOutcome = string.IsNullOrWhiteSpace(conditionOutcome) ? null : conditionOutcome.Trim();
        IsDefault = isDefault;
        Extensions = extensions ?? BpmnExtensions.Empty;
    }

    /// <summary>The BPMN <c>id</c> of this flow.</summary>
    [JsonPropertyName("flowId")]
    public string FlowId { get; }

    /// <summary>The element id this flow leaves.</summary>
    [JsonPropertyName("sourceRef")]
    public string SourceRef { get; }

    /// <summary>The element id this flow enters.</summary>
    [JsonPropertyName("targetRef")]
    public string TargetRef { get; }

    /// <summary>The BPMN <c>name</c>, often used as the visible branch label.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; }

    /// <summary>
    /// The outcome name that selects this flow, or <c>null</c> for an unconditional flow.
    /// </summary>
    [JsonPropertyName("conditionOutcome")]
    public string? ConditionOutcome { get; }

    /// <summary>Whether this is the source element's default flow.</summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; }

    /// <summary>
    /// Foreign XML retained from this flow: its <c>&lt;documentation&gt;</c>, its
    /// <c>&lt;extensionElements&gt;</c> children, and any attributes or child elements from namespaces the
    /// reader does not own.
    /// <para>
    /// Flows carry vendor annotations more often than one might expect - execution listeners on a flow are
    /// common in tool-authored files - so retention here is as load-bearing as it is on elements.
    /// </para>
    /// </summary>
    [JsonPropertyName("extensions")]
    public BpmnExtensions Extensions { get; }
}
