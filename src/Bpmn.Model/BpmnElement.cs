using System.Text.Json.Serialization;

namespace Bpmn.Model;

/// <summary>
/// One BPMN flow element in the process graph.
/// <para>
/// Gateways and start/end events are interpreted entirely by the semantics core and bind no work.
/// Task-family, subprocess, and intermediate-catch-event elements bind host-provided work through
/// <see cref="BindingRef"/>, an opaque key the interpreter never inspects: it only ever asks the host to
/// "start the work bound to this element".
/// </para>
/// </summary>
public sealed class BpmnElement
{
    /// <summary>Creates a BPMN flow element.</summary>
    [JsonConstructor]
    public BpmnElement(
        string elementId,
        string elementType,
        string? name = null,
        string? bindingRef = null,
        string? laneId = null,
        string? defaultFlowId = null,
        IReadOnlyCollection<BpmnEventDefinition>? eventDefinitions = null,
        IReadOnlyDictionary<string, string>? properties = null,
        string? attachedToRef = null,
        bool cancelActivity = true,
        BpmnLoopCharacteristics? loopCharacteristics = null,
        bool isForCompensation = false,
        string? compensationHandlerElementId = null,
        bool isTransaction = false,
        bool triggeredByEvent = false,
        string? listenerBindingRef = null)
    {
        if (string.IsNullOrWhiteSpace(elementId)) throw new ArgumentException("An element id is required.", nameof(elementId));
        if (string.IsNullOrWhiteSpace(elementType)) throw new ArgumentException("An element type is required.", nameof(elementType));

        ElementId = elementId;
        ElementType = elementType;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        BindingRef = string.IsNullOrWhiteSpace(bindingRef) ? null : bindingRef.Trim();
        ListenerBindingRef = string.IsNullOrWhiteSpace(listenerBindingRef) ? null : listenerBindingRef.Trim();
        LaneId = string.IsNullOrWhiteSpace(laneId) ? null : laneId.Trim();
        DefaultFlowId = string.IsNullOrWhiteSpace(defaultFlowId) ? null : defaultFlowId.Trim();
        EventDefinitions = eventDefinitions ?? [];
        Properties = properties ?? new Dictionary<string, string>();
        AttachedToRef = string.IsNullOrWhiteSpace(attachedToRef) ? null : attachedToRef.Trim();
        CancelActivity = cancelActivity;
        LoopCharacteristics = loopCharacteristics;
        IsForCompensation = isForCompensation;
        CompensationHandlerElementId = string.IsNullOrWhiteSpace(compensationHandlerElementId) ? null : compensationHandlerElementId.Trim();
        IsTransaction = isTransaction;
        TriggeredByEvent = triggeredByEvent;
    }

    /// <summary>The BPMN <c>id</c> of this element, unique within its process.</summary>
    [JsonPropertyName("elementId")]
    public string ElementId { get; }

    /// <summary>The BPMN element type (see <see cref="BpmnElementTypes"/>).</summary>
    [JsonPropertyName("elementType")]
    public string ElementType { get; }

    /// <summary>The BPMN <c>name</c>, when authored.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; }

    /// <summary>
    /// An opaque host key identifying the work this element runs, or <c>null</c> for elements the
    /// interpreter handles entirely on its own (gateways, plain start/end events). The interpreter compares
    /// and echoes this value; it never parses or interprets it.
    /// </summary>
    [JsonPropertyName("bindingRef")]
    public string? BindingRef { get; }

    /// <summary>The BPMN lane this element belongs to, when the source declared lanes.</summary>
    [JsonPropertyName("laneId")]
    public string? LaneId { get; }

    /// <summary>The element's BPMN default sequence flow, taken when no conditional flow matches.</summary>
    [JsonPropertyName("defaultFlowId")]
    public string? DefaultFlowId { get; }

    /// <summary>The event definitions attached to this element (timer, message, signal, error, and so on).</summary>
    [JsonPropertyName("eventDefinitions")]
    public IReadOnlyCollection<BpmnEventDefinition> EventDefinitions { get; }

    /// <summary>Additional BPMN attributes carried through verbatim, keyed by attribute name.</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, string> Properties { get; }

    /// <summary>
    /// The element id a <c>boundaryEvent</c> is attached to; <c>null</c> on every non-boundary element.
    /// A boundary event reacts to its trigger while the activity it is attached to is running.
    /// </summary>
    [JsonPropertyName("attachedToRef")]
    public string? AttachedToRef { get; }

    /// <summary>
    /// Whether a <c>boundaryEvent</c> interrupts the activity it is attached to when it fires.
    /// <c>true</c> (the BPMN default) terminates that activity and routes the boundary path; <c>false</c>
    /// runs the boundary path alongside the still-running activity. Meaningful only on boundary events.
    /// <para>
    /// BPMN fixes this to <c>true</c> for error boundary events: catching an error is always interrupting.
    /// </para>
    /// </summary>
    [JsonPropertyName("cancelActivity")]
    public bool CancelActivity { get; }

    /// <summary>
    /// The multi-instance loop characteristics of this element; <c>null</c> when the element is not a
    /// multi-instance activity. Valid only on a task-family or <c>subProcess</c> element that binds work.
    /// </summary>
    [JsonPropertyName("loopCharacteristics")]
    public BpmnLoopCharacteristics? LoopCharacteristics { get; }

    /// <summary>
    /// Marks a <b>compensation handler</b>: a task-family or <c>subProcess</c> element that binds work,
    /// participates in no sequence flows, and is invoked only by compensation replay, never by normal token
    /// flow.
    /// </summary>
    [JsonPropertyName("isForCompensation")]
    public bool IsForCompensation { get; }

    /// <summary>
    /// Set only on a <b>compensation boundary event</b> (a <c>boundaryEvent</c> whose single event definition
    /// is <see cref="BpmnEventDefinitionTypes.Compensation"/>): the element id of its
    /// <see cref="IsForCompensation"/> handler. This models the BPMN boundary-to-handler association.
    /// </summary>
    [JsonPropertyName("compensationHandlerElementId")]
    public string? CompensationHandlerElementId { get; }

    /// <summary>
    /// Marks a <b>transaction subprocess</b>: a <c>subProcess</c> element whose nested process may be
    /// cancelled from within by a cancel end event. Valid only on a <c>subProcess</c>-family element that
    /// binds work; a transaction may not also carry loop characteristics. This element-side flag drives
    /// cancel-boundary attachment validation and the enclosing scope's <c>Cancelled</c> outcome.
    /// </summary>
    [JsonPropertyName("isTransaction")]
    public bool IsTransaction { get; }

    /// <summary>
    /// Marks an <b>event subprocess</b>: a flow-less <c>subProcess</c>-family element whose nested body is
    /// activated by its single event start event when that trigger occurs while the enclosing scope is
    /// active. The element participates in no sequence flows, hosts no boundary event, and is neither a
    /// compensation handler nor a transaction.
    /// </summary>
    [JsonPropertyName("triggeredByEvent")]
    public bool TriggeredByEvent { get; }

    /// <summary>
    /// A second binding channel used by event subprocesses whose start trigger is a message, signal, or
    /// timer: the work armed when the enclosing scope starts, so the trigger can be observed while the scope
    /// runs. <see cref="BindingRef"/> binds the body; this binds the listener.
    /// <para>
    /// Required on a <see cref="TriggeredByEvent"/> element with a message, signal, or timer start trigger,
    /// and <c>null</c> on error- and escalation-triggered event subprocesses, which are dormant catchers
    /// needing no armed listener. A given binding ref is bound either as some element's
    /// <see cref="BindingRef"/> or as some element's <see cref="ListenerBindingRef"/> - never both, never twice.
    /// </para>
    /// </summary>
    [JsonPropertyName("listenerBindingRef")]
    public string? ListenerBindingRef { get; }
}

/// <summary>
/// The BPMN element types the model understands. String constants rather than an enum so new element
/// types can be added without breaking the serialized format.
/// </summary>
public static class BpmnElementTypes
{
    /// <summary>A start event. Begins a token at process or subprocess start.</summary>
    public const string StartEvent = "startEvent";

    /// <summary>An end event. Consumes a token; the process completes when no tokens remain.</summary>
    public const string EndEvent = "endEvent";

    /// <summary>An intermediate catch event. Parks a token until its event definition fires.</summary>
    public const string IntermediateCatchEvent = "intermediateCatchEvent";

    /// <summary>An intermediate throw event. Raises its event definition and continues immediately.</summary>
    public const string IntermediateThrowEvent = "intermediateThrowEvent";

    /// <summary>An abstract task. Binds work and completes when that work completes.</summary>
    public const string Task = "task";

    /// <summary>A user task. Work performed by a person.</summary>
    public const string UserTask = "userTask";

    /// <summary>A service task. Work performed by an automated system.</summary>
    public const string ServiceTask = "serviceTask";

    /// <summary>A script task. Work performed by evaluating a script; the host owns the script engine.</summary>
    public const string ScriptTask = "scriptTask";

    /// <summary>A manual task. Work performed without system support.</summary>
    public const string ManualTask = "manualTask";

    /// <summary>A business rule task. Work performed by a decision service.</summary>
    public const string BusinessRuleTask = "businessRuleTask";

    /// <summary>A send task. Publishes a message and continues.</summary>
    public const string SendTask = "sendTask";

    /// <summary>A receive task. Waits for a named message.</summary>
    public const string ReceiveTask = "receiveTask";

    /// <summary>
    /// A call activity: invokes a separately defined process. Behaves as a member of the task family, so
    /// element behaviors need no special case; the distinction is that the host resolves and runs another
    /// process definition, and its failure is mapped to a BPMN error the enclosing scope can catch.
    /// </summary>
    public const string CallActivity = "callActivity";

    /// <summary>An embedded subprocess. Runs a nested process definition within this scope.</summary>
    public const string SubProcess = "subProcess";

    /// <summary>An exclusive gateway (XOR). Routes a token down exactly one outgoing flow.</summary>
    public const string ExclusiveGateway = "exclusiveGateway";

    /// <summary>A parallel gateway (AND). Splits into all outgoing flows; joins when all incoming arrive.</summary>
    public const string ParallelGateway = "parallelGateway";

    /// <summary>An inclusive gateway (OR). Splits down every matching flow; joins the branches that ran.</summary>
    public const string InclusiveGateway = "inclusiveGateway";

    /// <summary>An event-based gateway. Races its outgoing catch events; the first to fire wins.</summary>
    public const string EventBasedGateway = "eventBasedGateway";

    /// <summary>A boundary event attached to an activity, interrupting or non-interrupting.</summary>
    public const string BoundaryEvent = "boundaryEvent";
}
