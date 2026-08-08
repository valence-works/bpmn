using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// Wiring documentation for a BPMN <c>&lt;messageFlow&gt;</c>: the resolved send/receive endpoint
/// elements and their pools, plus the flow's message name. This is authored-side metadata only — execution
/// rides the name-keyed stimulus fabric (a send publishes by name; receivers subscribe by name), so the engine
/// never reads a message flow, the graph validator ignores it, and it is stripped from the compiled executable
/// structure. Interchange records it so a collaboration's cross-pool wiring survives the round-trip and surfaces
/// as an analysis finding. An endpoint that resolves to a black-box pool carries a <c>PoolId</c> with a null
/// element id.
/// </summary>
public sealed class BpmnMessageFlow
{
    [JsonConstructor]
    public BpmnMessageFlow(
    string flowId,
    string? name = null,
    string? sourceElementId = null,
    string? sourcePoolId = null,
    string? targetElementId = null,
    string? targetPoolId = null,
    string? messageName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);

        FlowId = flowId;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        SourceElementId = string.IsNullOrWhiteSpace(sourceElementId) ? null : sourceElementId.Trim();
        SourcePoolId = string.IsNullOrWhiteSpace(sourcePoolId) ? null : sourcePoolId.Trim();
        TargetElementId = string.IsNullOrWhiteSpace(targetElementId) ? null : targetElementId.Trim();
        TargetPoolId = string.IsNullOrWhiteSpace(targetPoolId) ? null : targetPoolId.Trim();
        MessageName = string.IsNullOrWhiteSpace(messageName) ? null : messageName.Trim();
    }

    [JsonPropertyName("flowId")]
    public string FlowId { get; }

    [JsonPropertyName("name")]
    public string? Name { get; }

    /// <summary>The send-side element id; <c>null</c> when the source endpoint is a black-box pool.</summary>
    [JsonPropertyName("sourceElementId")]
    public string? SourceElementId { get; }

    /// <summary>The pool id the send-side endpoint lives in; <c>null</c> when unknown (ambiguous multi-participant process).</summary>
    [JsonPropertyName("sourcePoolId")]
    public string? SourcePoolId { get; }

    /// <summary>The receive-side element id; <c>null</c> when the target endpoint is a black-box pool.</summary>
    [JsonPropertyName("targetElementId")]
    public string? TargetElementId { get; }

    /// <summary>The pool id the receive-side endpoint lives in; <c>null</c> when unknown (ambiguous multi-participant process).</summary>
    [JsonPropertyName("targetPoolId")]
    public string? TargetPoolId { get; }

    /// <summary>The message name carried on the flow (its <c>messageRef</c>, else the matched endpoints' shared name); <c>null</c> when none is resolvable.</summary>
    [JsonPropertyName("messageName")]
    public string? MessageName { get; }
}
