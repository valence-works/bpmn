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
/// FEEL, JUEL, or script engine here. A BPMN <c>&lt;conditionExpression&gt;</c> is modeled as
/// <see cref="ConditionExpression"/>; ADR 0011 records how a host will evaluate it. Until then a host that
/// wants expression-based routing resolves the expression itself and reports the resulting outcome name.
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
    /// The author's <c>&lt;conditionExpression&gt;</c>, or <c>null</c> when the flow declares none.
    /// <para>
    /// When <see cref="ConditionOutcome"/> is also set, the outcome decides and the expression is retained
    /// only. The <c>outcome == '…'</c> predicate this library writes for an outcome is not an author's
    /// expression: a read consumes an expression whose body is exactly <c>outcome == '&lt;outcome&gt;'</c>
    /// for this flow's own outcome and carries no attributes or children, and does not model it. Any other
    /// expression is modeled, including one of the same shape that names a different outcome; while the
    /// flow keeps an outcome, a write replaces that lookalike with the predicate for the current outcome. The interpreter does not evaluate an expression yet: a flow
    /// whose only condition is an expression is routed as unconditional, which the XML reader reports as a
    /// <c>Degraded</c> finding. See ADR 0011.
    /// </para>
    /// <para>
    /// Additive in payload format 1.3.0, and init-only rather than a constructor parameter so the
    /// constructor is unchanged.
    /// </para>
    /// </summary>
    [JsonPropertyName("conditionExpression")]
    public BpmnConditionExpression? ConditionExpression { get; init; }

    /// <summary>
    /// Foreign XML retained from this flow: its <c>&lt;documentation&gt;</c>, its
    /// <c>&lt;extensionElements&gt;</c> children, unrecognized children, and any attributes from
    /// namespaces the reader does not own. An author's <c>&lt;conditionExpression&gt;</c> is read into
    /// <see cref="ConditionExpression"/> instead, and a second one on the same flow, which BPMN does not
    /// allow, is dropped with a finding; a model serialized before payload format 1.3.0 may still carry one
    /// here, and the writer honors it. The predicate this library writes for a
    /// <see cref="ConditionOutcome"/> is not retained, so a later edit of that outcome cannot leave
    /// a stale expression.
    /// <para>
    /// Flows carry vendor annotations more often than one might expect - execution listeners on a flow are
    /// common in tool-authored files - so retention here is as load-bearing as it is on elements.
    /// </para>
    /// </summary>
    [JsonPropertyName("extensions")]
    public BpmnExtensions Extensions { get; }
}
