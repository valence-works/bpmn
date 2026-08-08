using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// A parsed BPMN 2.0 document: the root <c>&lt;definitions&gt;</c> element and everything under it.
/// This is the neutral, host-independent representation the whole library is built around.
/// </summary>
/// <param name="Id">The document's BPMN <c>id</c>.</param>
/// <param name="TargetNamespace">The document's <c>targetNamespace</c>.</param>
/// <param name="Exporter">The tool that produced the document, when it identified itself.</param>
/// <param name="ExporterVersion">The version of that tool.</param>
/// <param name="Processes">Every <c>&lt;process&gt;</c> in the document, in document order.</param>
/// <param name="Collaboration">The <c>&lt;collaboration&gt;</c>, when the document declares pools.</param>
/// <param name="Diagrams">BPMN DI diagrams carrying layout for the processes above.</param>
/// <param name="Messages">Root-level <c>&lt;message&gt;</c> declarations, referenced by event definitions.</param>
/// <param name="Signals">Root-level <c>&lt;signal&gt;</c> declarations.</param>
/// <param name="Errors">Root-level <c>&lt;error&gt;</c> declarations, carrying the error codes BPMN matches on.</param>
/// <param name="Escalations">Root-level <c>&lt;escalation&gt;</c> declarations.</param>
/// <param name="Extensions">Foreign XML retained from the <c>&lt;definitions&gt;</c> element itself.</param>
public sealed record BpmnDefinitions(
 [property: JsonPropertyName("id")] string? Id = null,
 [property: JsonPropertyName("targetNamespace")] string? TargetNamespace = null,
 [property: JsonPropertyName("exporter")] string? Exporter = null,
 [property: JsonPropertyName("exporterVersion")] string? ExporterVersion = null,
 IReadOnlyList<BpmnProcessDefinition>? Processes = null,
 [property: JsonPropertyName("collaboration")] BpmnCollaboration? Collaboration = null,
 IReadOnlyList<BpmnDiagram>? Diagrams = null,
 IReadOnlyList<BpmnMessageDeclaration>? Messages = null,
 IReadOnlyList<BpmnSignalDeclaration>? Signals = null,
 IReadOnlyList<BpmnErrorDeclaration>? Errors = null,
 IReadOnlyList<BpmnEscalationDeclaration>? Escalations = null,
 BpmnExtensions? Extensions = null)
{
    /// <summary>Every process in the document. Never null.</summary>
    [JsonPropertyName("processes")]
    public IReadOnlyList<BpmnProcessDefinition> Processes { get; init; } = Processes ?? [];

    /// <summary>Diagrams carrying layout. Never null.</summary>
    [JsonPropertyName("diagrams")]
    public IReadOnlyList<BpmnDiagram> Diagrams { get; init; } = Diagrams ?? [];

    /// <summary>Root-level message declarations. Never null.</summary>
    [JsonPropertyName("messages")]
    public IReadOnlyList<BpmnMessageDeclaration> Messages { get; init; } = Messages ?? [];

    /// <summary>Root-level signal declarations. Never null.</summary>
    [JsonPropertyName("signals")]
    public IReadOnlyList<BpmnSignalDeclaration> Signals { get; init; } = Signals ?? [];

    /// <summary>Root-level error declarations. Never null.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<BpmnErrorDeclaration> Errors { get; init; } = Errors ?? [];

    /// <summary>Root-level escalation declarations. Never null.</summary>
    [JsonPropertyName("escalations")]
    public IReadOnlyList<BpmnEscalationDeclaration> Escalations { get; init; } = Escalations ?? [];

    /// <summary>Foreign XML retained from the definitions element. Never null.</summary>
    [JsonPropertyName("extensions")]
    public BpmnExtensions Extensions { get; init; } = Extensions ?? BpmnExtensions.Empty;

    /// <summary>Finds a process by its BPMN id, or returns <c>null</c>.</summary>
    public BpmnProcessDefinition? FindProcess(string processId) =>
    Processes.FirstOrDefault(p => string.Equals(p.ProcessId, processId, StringComparison.Ordinal));
}

/// <summary>
/// One <c>&lt;process&gt;</c>: the semantic graph the interpreter executes, plus its authoring metadata.
/// </summary>
/// <param name="ProcessId">The BPMN <c>id</c> of the process.</param>
/// <param name="Name">The BPMN <c>name</c>.</param>
/// <param name="IsExecutable">The BPMN <c>isExecutable</c> flag.</param>
/// <param name="IsTransaction">Whether this process is a transaction, so a cancel end event may occur in it.</param>
/// <param name="Elements">Flow elements, keyed by <see cref="BpmnElement.ElementId"/>.</param>
/// <param name="SequenceFlows">Sequence flows connecting the elements.</param>
/// <param name="Lanes">Lanes declared by this process.</param>
/// <param name="Variables">Container-scoped variable declarations visible to this process.</param>
/// <param name="Extensions">Foreign XML retained from the process element.</param>
public sealed record BpmnProcessDefinition(
 [property: JsonPropertyName("processId")] string ProcessId,
 [property: JsonPropertyName("name")] string? Name = null,
 [property: JsonPropertyName("isExecutable")] bool IsExecutable = true,
 [property: JsonPropertyName("isTransaction")] bool IsTransaction = false,
 IReadOnlyList<BpmnElement>? Elements = null,
 IReadOnlyList<BpmnSequenceFlow>? SequenceFlows = null,
 IReadOnlyList<BpmnLane>? Lanes = null,
 IReadOnlyList<BpmnVariableDeclaration>? Variables = null,
 BpmnExtensions? Extensions = null)
{
    /// <summary>Flow elements. Never null.</summary>
    [JsonPropertyName("elements")]
    public IReadOnlyList<BpmnElement> Elements { get; init; } = Elements ?? [];

    /// <summary>Sequence flows. Never null.</summary>
    [JsonPropertyName("sequenceFlows")]
    public IReadOnlyList<BpmnSequenceFlow> SequenceFlows { get; init; } = SequenceFlows ?? [];

    /// <summary>Lanes. Never null.</summary>
    [JsonPropertyName("lanes")]
    public IReadOnlyList<BpmnLane> Lanes { get; init; } = Lanes ?? [];

    /// <summary>Variable declarations. Never null.</summary>
    [JsonPropertyName("variables")]
    public IReadOnlyList<BpmnVariableDeclaration> Variables { get; init; } = Variables ?? [];

    /// <summary>Foreign XML retained from the process element. Never null.</summary>
    public BpmnExtensions Extensions { get; init; } = Extensions ?? BpmnExtensions.Empty;
}

/// <summary>
/// A <c>&lt;collaboration&gt;</c>: the pools in a multi-participant document and the message flows between them.
/// </summary>
/// <param name="Id">The collaboration's BPMN id.</param>
/// <param name="Pools">Participants, one per pool.</param>
/// <param name="MessageFlows">Message flows crossing pool boundaries.</param>
public sealed record BpmnCollaboration(
 [property: JsonPropertyName("id")] string? Id = null,
 IReadOnlyList<BpmnPool>? Pools = null,
 IReadOnlyList<BpmnMessageFlow>? MessageFlows = null)
{
    /// <summary>Participants. Never null.</summary>
    [JsonPropertyName("pools")]
    public IReadOnlyList<BpmnPool> Pools { get; init; } = Pools ?? [];

    /// <summary>Message flows. Never null.</summary>
    [JsonPropertyName("messageFlows")]
    public IReadOnlyList<BpmnMessageFlow> MessageFlows { get; init; } = MessageFlows ?? [];
}

/// <summary>
/// A container-scoped variable declared by a process. The semantics core reads only <see cref="Name"/>;
/// the type hint and default are carried for hosts that want them.
/// </summary>
/// <param name="Name">The variable name, as referenced by expressions and multi-instance collections.</param>
/// <param name="TypeHint">An optional, host-interpreted type name.</param>
/// <param name="DefaultValue">An optional default, serialized as JSON.</param>
public sealed record BpmnVariableDeclaration(
 [property: JsonPropertyName("name")] string Name,
 [property: JsonPropertyName("typeHint")] string? TypeHint = null,
 [property: JsonPropertyName("defaultValue")] System.Text.Json.JsonElement? DefaultValue = null);

/// <summary>A root-level <c>&lt;message&gt;</c> declaration.</summary>
/// <param name="Id">The BPMN id, referenced by <c>messageRef</c>.</param>
/// <param name="Name">The message name, which is what the interpreter matches on.</param>
public sealed record BpmnMessageDeclaration(
 [property: JsonPropertyName("id")] string Id,
 [property: JsonPropertyName("name")] string? Name = null);

/// <summary>A root-level <c>&lt;signal&gt;</c> declaration.</summary>
/// <param name="Id">The BPMN id, referenced by <c>signalRef</c>.</param>
/// <param name="Name">The signal name, which is what the interpreter matches on.</param>
public sealed record BpmnSignalDeclaration(
 [property: JsonPropertyName("id")] string Id,
 [property: JsonPropertyName("name")] string? Name = null);

/// <summary>
/// A root-level <c>&lt;error&gt;</c> declaration. BPMN matches a thrown error against catching events by
/// <see cref="ErrorCode"/>; an error boundary event with no code catches any error.
/// </summary>
/// <param name="Id">The BPMN id, referenced by <c>errorRef</c>.</param>
/// <param name="Name">A human-readable name.</param>
/// <param name="ErrorCode">The code BPMN matches on.</param>
public sealed record BpmnErrorDeclaration(
 [property: JsonPropertyName("id")] string Id,
 [property: JsonPropertyName("name")] string? Name = null,
 [property: JsonPropertyName("errorCode")] string? ErrorCode = null);

/// <summary>
/// A root-level <c>&lt;escalation&gt;</c> declaration. Unlike an error, an escalation does not imply the
/// activity failed, and a catching escalation event may be non-interrupting.
/// </summary>
/// <param name="Id">The BPMN id, referenced by <c>escalationRef</c>.</param>
/// <param name="Name">A human-readable name.</param>
/// <param name="EscalationCode">The code escalation catchers match on.</param>
public sealed record BpmnEscalationDeclaration(
 [property: JsonPropertyName("id")] string Id,
 [property: JsonPropertyName("name")] string? Name = null,
 [property: JsonPropertyName("escalationCode")] string? EscalationCode = null);
