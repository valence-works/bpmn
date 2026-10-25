using Bpmn.Model;

namespace Bpmn.Semantics;

/// <summary>
/// Maps a <see cref="BpmnElement"/> to the behavior family that executes it. Families are the
/// registration keys of <see cref="IBpmnBehaviorRegistry"/>; the whole task family shares one behavior
/// because the task subtype only changes which host work is bound, never the token semantics.
/// </summary>
public static class BpmnElementFamilies
{
    public const string StartEventNone = "startEvent.none";
    public const string StartEventTimer = "startEvent.timer";
    public const string StartEventMessage = "startEvent.message";
    public const string StartEventSignal = "startEvent.signal";

    /// <summary>The escalation event-subprocess body start family: seeds one token via the start-element hint, then routes outbound like a none start. Never externally triggered.</summary>
    public const string StartEventEscalation = "startEvent.escalation";

    /// <summary>The error event-subprocess body start family: seeds one token via the start-element hint, then routes outbound like a none start. Never externally triggered.</summary>
    public const string StartEventError = "startEvent.error";
    public const string EndEventNone = "endEvent.none";
    public const string EndEventTerminate = "endEvent.terminate";

    /// <summary>The compensate end event family: triggers a compensation replay, then consumes its token (none-end semantics).</summary>
    public const string EndEventCompensation = "endEvent.compensation";

    /// <summary>The cancel end event family: cancels the enclosing transaction — stop other live work, replay the scope's compensables, then complete with the <c>Cancelled</c> outcome.</summary>
    public const string EndEventCancel = "endEvent.cancel";
    public const string IntermediateCatchEvent = "intermediateCatchEvent.catch";

    /// <summary>The link intermediate catch event family: the landing point of a link throw of the same name, which binds no work and routes its outbound flows at once.</summary>
    public const string IntermediateCatchEventLink = "intermediateCatchEvent.link";

    /// <summary>The link intermediate throw event family: jumps its token to the link catch of the same name in its scope, within the same evaluation.</summary>
    public const string IntermediateThrowEventLink = "intermediateThrowEvent.link";

    /// <summary>The compensate intermediate throw event family: triggers a compensation replay, then routes its outbound flows.</summary>
    public const string IntermediateThrowEventCompensation = "intermediateThrowEvent.compensation";

    /// <summary>The escalation intermediate throw event family: raises an escalation to the enclosing scope, then routes its outbound flows (fire-and-continue).</summary>
    public const string IntermediateThrowEventEscalation = "intermediateThrowEvent.escalation";

    /// <summary>The message intermediate throw event family: a bound-work send — starts the bound publish work, then routes its outbound flows on that work's fire-and-continue completion.</summary>
    public const string IntermediateThrowEventMessage = "intermediateThrowEvent.message";

    /// <summary>The signal intermediate throw event family: a bound-work broadcast — starts the bound publish work, then routes its outbound flows on that work's fire-and-continue completion.</summary>
    public const string IntermediateThrowEventSignal = "intermediateThrowEvent.signal";

    /// <summary>The escalation end event family: raises an escalation to the enclosing scope, then consumes its token (none-end semantics).</summary>
    public const string EndEventEscalation = "endEvent.escalation";

    /// <summary>The error end event family: throws the error it declares, which an error event subprocess of its scope catches, or which ends the scope with a fault carrying the error's code.</summary>
    public const string EndEventError = "endEvent.error";

    /// <summary>The message end event family: a bound-work send — starts the bound publish work, then consumes its token on that work's completion (none-end semantics).</summary>
    public const string EndEventMessage = "endEvent.message";

    /// <summary>The signal end event family: a bound-work broadcast — starts the bound publish work, then consumes its token on that work's completion (none-end semantics).</summary>
    public const string EndEventSignal = "endEvent.signal";
    public const string Task = "task";
    public const string SubProcess = "subProcess";
    public const string ExclusiveGateway = "exclusiveGateway";
    public const string ParallelGateway = "parallelGateway";
    public const string InclusiveGateway = "inclusiveGateway";
    public const string EventBasedGateway = "eventBasedGateway";

    /// <summary>The single behavior family for every boundary event; catch versus error is a per-element definition detail, not a separate behavior.</summary>
    public const string BoundaryEvent = "boundaryEvent";

    private static readonly HashSet<string> TaskElementTypes = new(StringComparer.Ordinal)
    {
        BpmnElementTypes.Task,
        BpmnElementTypes.UserTask,
        BpmnElementTypes.ServiceTask,
        BpmnElementTypes.ScriptTask,
        BpmnElementTypes.ManualTask,
        BpmnElementTypes.BusinessRuleTask,
        BpmnElementTypes.SendTask,
        BpmnElementTypes.ReceiveTask,
        // A call activity resolves to the task family (TaskBehavior unchanged) and is a boundary-host /
        // multi-instance-legal member; its call-activity distinction is the interpreter-side failure-outcome translation.
        BpmnElementTypes.CallActivity
    };

    public static string Resolve(BpmnElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (TaskElementTypes.Contains(element.ElementType))
            return Task;

        return element.ElementType switch
        {
            BpmnElementTypes.StartEvent => ResolveStartEvent(element),
            BpmnElementTypes.EndEvent => ResolveEndEvent(element),
            BpmnElementTypes.IntermediateCatchEvent => ResolveIntermediateCatchEvent(element),
            BpmnElementTypes.IntermediateThrowEvent => ResolveIntermediateThrowEvent(element),
            BpmnElementTypes.SubProcess => SubProcess,
            BpmnElementTypes.ExclusiveGateway => ExclusiveGateway,
            BpmnElementTypes.ParallelGateway => ParallelGateway,
            BpmnElementTypes.InclusiveGateway => InclusiveGateway,
            BpmnElementTypes.EventBasedGateway => EventBasedGateway,
            BpmnElementTypes.BoundaryEvent => ResolveBoundaryEvent(element),
            _ => throw new BpmnExecutionException(
                $"BPMN element '{element.ElementId}' has element type '{element.ElementType}', which this library does not support.")
            { ElementIds = [element.ElementId] }
        };
    }

    /// <summary>
    /// The event-definition types an intermediate catch event may declare. The definition type is authoring
    /// semantics: every catch event waits through its bound suspending work, so the runtime family is the
    /// same for all three.
    /// </summary>
    private static readonly HashSet<string> SupportedCatchEventDefinitionTypes = new(StringComparer.Ordinal)
    {
        BpmnEventDefinitionTypes.Timer,
        BpmnEventDefinitionTypes.Message,
        BpmnEventDefinitionTypes.Signal
    };

    /// <summary>
    /// The event-definition types a boundary event may declare: the three listener kinds
    /// (timer/message/signal, which arm suspending work) plus error, escalation, compensation, and cancel.
    /// </summary>
    private static readonly HashSet<string> SupportedBoundaryDefinitionTypes = new(StringComparer.Ordinal)
    {
        BpmnEventDefinitionTypes.Timer,
        BpmnEventDefinitionTypes.Message,
        BpmnEventDefinitionTypes.Signal,
        BpmnEventDefinitionTypes.Error,
        BpmnEventDefinitionTypes.Escalation,
        BpmnEventDefinitionTypes.Compensation,
        BpmnEventDefinitionTypes.Cancel
    };

    private static string ResolveBoundaryEvent(BpmnElement element)
    {
        if (element.EventDefinitions.Count != 1)
            throw new BpmnExecutionException(
                $"BPMN boundary event '{element.ElementId}' must declare exactly one event definition; it declares {element.EventDefinitions.Count}.")
            { ElementIds = [element.ElementId] };

        var definitionType = element.EventDefinitions.Single().Type;
        if (!SupportedBoundaryDefinitionTypes.Contains(definitionType))
            throw new BpmnExecutionException(
                $"BPMN boundary event '{element.ElementId}' declares event definition type '{definitionType}'; only timer, message, signal, error, escalation, compensation, and cancel boundary events are supported.")
            { ElementIds = [element.ElementId] };

        return BoundaryEvent;
    }

    /// <summary>True when a <c>boundaryEvent</c> is an escalation boundary: dormant (no listener child), notification-driven, routes its outbound flows.</summary>
    public static bool IsEscalationBoundary(BpmnElement element) =>
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent) &&
        element.EventDefinitions.Count == 1 &&
        StringComparer.Ordinal.Equals(element.EventDefinitions.Single().Type, BpmnEventDefinitionTypes.Escalation);

    /// <summary>True when an element is a message throw (intermediate) or message end event: it carries exactly one message event definition. A message throw/end is a bound-work send.</summary>
    public static bool IsMessageThrowOrEnd(BpmnElement element) =>
        (StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.IntermediateThrowEvent) ||
         StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EndEvent)) &&
        element.EventDefinitions.Count == 1 &&
        StringComparer.Ordinal.Equals(element.EventDefinitions.Single().Type, BpmnEventDefinitionTypes.Message);

    /// <summary>True when an element is a signal throw (intermediate) or signal end event: it carries exactly one signal event definition. A signal throw/end is a bound-work broadcast.</summary>
    public static bool IsSignalThrowOrEnd(BpmnElement element) =>
        (StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.IntermediateThrowEvent) ||
         StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EndEvent)) &&
        element.EventDefinitions.Count == 1 &&
        StringComparer.Ordinal.Equals(element.EventDefinitions.Single().Type, BpmnEventDefinitionTypes.Signal);

    /// <summary>True when an element is an escalation throw (intermediate) or escalation end event: it carries exactly one escalation event definition.</summary>
    public static bool IsEscalationThrowOrEnd(BpmnElement element) =>
        (StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.IntermediateThrowEvent) ||
         StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EndEvent)) &&
        element.EventDefinitions.Count == 1 &&
        StringComparer.Ordinal.Equals(element.EventDefinitions.Single().Type, BpmnEventDefinitionTypes.Escalation);

    /// <summary>True when a <c>boundaryEvent</c> is an error boundary (absorbs the host's work fault, no listener); false when it is a timer/message/signal catch boundary.</summary>
    public static bool IsErrorBoundary(BpmnElement element) =>
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent) &&
        element.EventDefinitions.Count == 1 &&
        StringComparer.Ordinal.Equals(element.EventDefinitions.Single().Type, BpmnEventDefinitionTypes.Error);

    /// <summary>True when a <c>boundaryEvent</c> is a compensation boundary: it is dormant (no listener, no outbound flows) and its handler is reached by association, not by token flow.</summary>
    public static bool IsCompensationBoundary(BpmnElement element) =>
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent) &&
        element.EventDefinitions.Count == 1 &&
        StringComparer.Ordinal.Equals(element.EventDefinitions.Single().Type, BpmnEventDefinitionTypes.Compensation);

    /// <summary>True when an element carries a compensate event definition: a compensate throw or a compensate end event.</summary>
    public static bool HasCompensateDefinition(BpmnElement element) =>
        element.EventDefinitions.Count == 1 &&
        StringComparer.Ordinal.Equals(element.EventDefinitions.Single().Type, BpmnEventDefinitionTypes.Compensation);

    /// <summary>True when an element is a cancel end event: an <c>endEvent</c> whose single event definition is <see cref="BpmnEventDefinitionTypes.Cancel"/>.</summary>
    public static bool IsCancelEndEvent(BpmnElement element) =>
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EndEvent) &&
        element.EventDefinitions.Count == 1 &&
        StringComparer.Ordinal.Equals(element.EventDefinitions.Single().Type, BpmnEventDefinitionTypes.Cancel);

    /// <summary>True when a <c>boundaryEvent</c> is a cancel boundary: dormant (no listener), fires on the transaction's <c>Cancelled</c> outcome and routes its outbound flows.</summary>
    public static bool IsCancelBoundary(BpmnElement element) =>
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent) &&
        element.EventDefinitions.Count == 1 &&
        StringComparer.Ordinal.Equals(element.EventDefinitions.Single().Type, BpmnEventDefinitionTypes.Cancel);

    /// <summary>True when an element is a call activity: a task-family element whose bound work maps its failure outcomes onto BPMN error handling.</summary>
    public static bool IsCallActivity(BpmnElement element) =>
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.CallActivity);

    /// <summary>
    /// True for an element whose built-in behavior takes every outgoing flow without testing any condition:
    /// every start event, a parallel gateway, and an event-based gateway. A condition expression on such a flow
    /// would never be evaluated, so graph build refuses one rather than ignore it, and capability analysis does
    /// not count it.
    /// </summary>
    internal static bool TakesEveryOutgoingFlow(BpmnElement element) =>
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.StartEvent) ||
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.ParallelGateway) ||
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EventBasedGateway);

    /// <summary>The host families a boundary event may attach to: the task family and embedded subprocesses.</summary>
    public static bool IsBoundaryHostFamily(BpmnElement element) =>
        TaskElementTypes.Contains(element.ElementType) ||
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.SubProcess);

    private static string ResolveIntermediateCatchEvent(BpmnElement element)
    {
        if (element.EventDefinitions.Count != 1)
            throw new BpmnExecutionException(
                $"BPMN intermediate catch event '{element.ElementId}' must declare exactly one event definition; it declares {element.EventDefinitions.Count}.")
            { ElementIds = [element.ElementId] };

        var definitionType = element.EventDefinitions.Single().Type;
        if (StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Link))
            return IntermediateCatchEventLink;
        if (!SupportedCatchEventDefinitionTypes.Contains(definitionType))
            throw new BpmnExecutionException(
                $"BPMN intermediate catch event '{element.ElementId}' declares event definition type '{definitionType}'; only timer, message, signal, and link catch events are supported.")
            { ElementIds = [element.ElementId] };

        return IntermediateCatchEvent;
    }

    /// <summary>
    /// The event-defined start families, keyed by event-definition type. A start event declaring
    /// exactly one timer/message/signal definition registers a durable start trigger at publish time and seeds a
    /// single token at runtime — the trigger machinery is entirely publish/dispatch-time; the runtime token
    /// behavior equals a none start.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> EventStartFamiliesByDefinitionType =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BpmnEventDefinitionTypes.Timer] = StartEventTimer,
            [BpmnEventDefinitionTypes.Message] = StartEventMessage,
            [BpmnEventDefinitionTypes.Signal] = StartEventSignal
        };

    /// <summary>The externally triggered start families (all of them route outbound like a none start).</summary>
    public static readonly IReadOnlySet<string> StartEventFamilies =
        new HashSet<string>(StringComparer.Ordinal) { StartEventNone, StartEventTimer, StartEventMessage, StartEventSignal };

    private static string ResolveStartEvent(BpmnElement element)
    {
        if (element.EventDefinitions.Count == 0)
            return StartEventNone;

        if (element.EventDefinitions.Count == 1)
        {
            var type = element.EventDefinitions.Single().Type;
            if (EventStartFamiliesByDefinitionType.TryGetValue(type, out var family))
                return family;
            // An escalation/error start event is an event-subprocess body start (seeded via the start-element hint). Its runtime token behavior equals a none start; it is never a publish-time start trigger.
            if (StringComparer.Ordinal.Equals(type, BpmnEventDefinitionTypes.Escalation))
                return StartEventEscalation;
            if (StringComparer.Ordinal.Equals(type, BpmnEventDefinitionTypes.Error))
                return StartEventError;
        }

        throw new BpmnExecutionException(
            $"BPMN start event '{element.ElementId}' declares unsupported event definitions; only none, timer, message, signal, and event-subprocess (escalation/error) start events are supported (exactly one such definition).")
        { ElementIds = [element.ElementId] };
    }

    /// <summary>
    /// True when a start event is externally triggered: a timer/message/signal start. A none start is
    /// direct-invocation, and an escalation/error start is an event-subprocess body start seeded via the
    /// start-element hint — neither is externally triggered.
    /// </summary>
    public static bool IsExternalStartTrigger(BpmnElement element) =>
        StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.StartEvent) &&
        element.EventDefinitions.Count == 1 &&
        EventStartFamiliesByDefinitionType.ContainsKey(element.EventDefinitions.Single().Type);

    private static string ResolveEndEvent(BpmnElement element)
    {
        if (element.EventDefinitions.Count == 0)
            return EndEventNone;

        if (element.EventDefinitions.Count == 1)
        {
            var type = element.EventDefinitions.Single().Type;
            if (StringComparer.Ordinal.Equals(type, BpmnEventDefinitionTypes.Terminate))
                return EndEventTerminate;
            if (StringComparer.Ordinal.Equals(type, BpmnEventDefinitionTypes.Compensation))
                return EndEventCompensation;
            if (StringComparer.Ordinal.Equals(type, BpmnEventDefinitionTypes.Cancel))
                return EndEventCancel;
            if (StringComparer.Ordinal.Equals(type, BpmnEventDefinitionTypes.Escalation))
                return EndEventEscalation;
            if (StringComparer.Ordinal.Equals(type, BpmnEventDefinitionTypes.Message))
                return EndEventMessage;
            if (StringComparer.Ordinal.Equals(type, BpmnEventDefinitionTypes.Signal))
                return EndEventSignal;
            if (StringComparer.Ordinal.Equals(type, BpmnEventDefinitionTypes.Error))
                return EndEventError;
        }

        throw new BpmnExecutionException(
            $"BPMN end event '{element.ElementId}' declares unsupported event definitions; only none, terminate, compensate, cancel, escalation, error, message, and signal end events are supported.")
        { ElementIds = [element.ElementId] };
    }

    /// <summary>
    /// Resolves an intermediate throw event. Exactly one
    /// <see cref="BpmnEventDefinitionTypes.Compensation"/>, <see cref="BpmnEventDefinitionTypes.Escalation"/>,
    /// <see cref="BpmnEventDefinitionTypes.Message"/>, <see cref="BpmnEventDefinitionTypes.Signal"/> or
    /// <see cref="BpmnEventDefinitionTypes.Link"/> definition → that throw family; any other (or no) definition is
    /// rejected.
    /// </summary>
    private static string ResolveIntermediateThrowEvent(BpmnElement element)
    {
        if (element.EventDefinitions.Count != 1)
            throw new BpmnExecutionException(
                $"BPMN intermediate throw event '{element.ElementId}' must declare exactly one event definition; it declares {element.EventDefinitions.Count}.")
            { ElementIds = [element.ElementId] };

        var definitionType = element.EventDefinitions.Single().Type;
        if (StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Compensation))
            return IntermediateThrowEventCompensation;
        if (StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Escalation))
            return IntermediateThrowEventEscalation;
        if (StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Message))
            return IntermediateThrowEventMessage;
        if (StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Signal))
            return IntermediateThrowEventSignal;
        if (StringComparer.Ordinal.Equals(definitionType, BpmnEventDefinitionTypes.Link))
            return IntermediateThrowEventLink;

        throw new BpmnExecutionException(
            $"BPMN intermediate throw event '{element.ElementId}' declares event definition type '{definitionType}'; only compensate, escalation, message, signal, and link throw events are supported.")
        { ElementIds = [element.ElementId] };
    }
}
