using Bpmn.Model;
using Bpmn.Model.State;

namespace Bpmn.Semantics.Behaviors;

/// <summary>
/// Start event (none / timer / message / signal): pass the arriving token straight onto every outbound sequence
/// flow. All start families share this token behavior — an event-defined start differs only in how its instance
/// is *started* (the host resolves the trigger and names the start element), never in how the seeded token
/// routes. Registered once per start family so diagnostics keep the family's display name.
/// </summary>
public sealed class StartEventBehavior(string elementFamily, string displayName) : IBpmnElementBehavior
{
    public string ElementFamily { get; } = elementFamily;
    public string DisplayName { get; } = displayName;

    public static StartEventBehavior None() => new(BpmnElementFamilies.StartEventNone, "Start Event (None)");
    public static StartEventBehavior Timer() => new(BpmnElementFamilies.StartEventTimer, "Start Event (Timer)");
    public static StartEventBehavior Message() => new(BpmnElementFamilies.StartEventMessage, "Start Event (Message)");
    public static StartEventBehavior Signal() => new(BpmnElementFamilies.StartEventSignal, "Start Event (Signal)");

    /// <summary>The escalation event-subprocess body start: once seeded via the start-element hint, routes outbound like a none start.</summary>
    public static StartEventBehavior Escalation() => new(BpmnElementFamilies.StartEventEscalation, "Start Event (Escalation)");

    /// <summary>The error event-subprocess body start: once seeded via the start-element hint, routes outbound like a none start.</summary>
    public static StartEventBehavior Error() => new(BpmnElementFamilies.StartEventError, "Start Event (Error)");

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(context.OutboundFlows)));

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN start event '{context.Element.ElementId}' cannot bind work.");
}

/// <summary>None end event: absorb the token.</summary>
public sealed class NoneEndEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.EndEventNone;
    public string DisplayName => "End Event (None)";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.ConsumeToken());

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN end event '{context.Element.ElementId}' cannot bind work.");
}

/// <summary>
/// Intermediate catch event (timer/message/signal): parks the arriving token and starts the element's bound
/// suspending work (a timer for a timer event, a waiting subscription for message/signal). The host holds the
/// durable timer or subscription; its completion is an ordinary work completion that routes outbound flows by
/// the shared task selection rules. Graph validation guarantees the binding exists.
/// </summary>
public sealed class CatchEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.IntermediateCatchEvent;
    public string DisplayName => "Intermediate Catch Event";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.StartWork());

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context)
    {
        var flows = BpmnFlowSelector.SelectTaskFlows(context);
        if (flows.Count == 0 && context.OutboundFlows.Count > 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.flow.none-taken",
                $"BPMN intermediate catch event '{context.Element.ElementId}' completed with outcomes [{string.Join(", ", context.OutcomeNames)}] but no outbound sequence flow matched and no default flow is declared."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(flows)));
    }
}

/// <summary>
/// Compensate intermediate throw event: on token arrival, emit the single
/// <see cref="BpmnBehaviorCommandKind.TriggerCompensation"/> command and nothing else. The interpreter owns
/// target selection, claiming, and the reverse-order handler replay; when the replay finishes it routes the
/// throw token's outbound flows through normal task-flow selection. The behavior stays semantics-unaware.
/// </summary>
public sealed class CompensationThrowEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.IntermediateThrowEventCompensation;
    public string DisplayName => "Compensate Throw Event";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.TriggerCompensation());

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN compensate throw event '{context.Element.ElementId}' cannot bind work.");
}

/// <summary>
/// Compensate end event: on token arrival, emit the single
/// <see cref="BpmnBehaviorCommandKind.TriggerCompensation"/> command; when the reverse-order replay finishes
/// the interpreter consumes the token (none-end semantics). The behavior stays semantics-unaware.
/// </summary>
public sealed class CompensationEndEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.EndEventCompensation;
    public string DisplayName => "Compensate End Event";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.TriggerCompensation());

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN compensate end event '{context.Element.ElementId}' cannot bind work.");
}

/// <summary>
/// Cancel end event: on token arrival, emit the single
/// <see cref="BpmnBehaviorCommandKind.CancelTransaction"/> command and nothing else. The interpreter owns the
/// stop-then-claim-then-replay sequencing and the <c>Cancelled</c> completion; the behavior stays
/// semantics-unaware. Valid only inside a transaction (enforced by graph validation).
/// </summary>
public sealed class CancelEndEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.EndEventCancel;
    public string DisplayName => "Cancel End Event";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.CancelTransaction());

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN cancel end event '{context.Element.ElementId}' cannot bind work.");
}

/// <summary>
/// Escalation intermediate throw event: on token arrival, emit a <see cref="BpmnBehaviorCommandKind.RaiseEscalation"/>
/// command followed by <see cref="BpmnBehaviorCommandKind.EmitTokens"/> over its selected outbound flows — the
/// throw signals its enclosing scope and continues immediately (fire-and-continue; escalation is non-blocking).
/// The interpreter reads the escalation code from the element and owns the signalling (or the root no-op); the
/// behavior stays semantics-unaware.
/// </summary>
public sealed class EscalationThrowEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.IntermediateThrowEventEscalation;
    public string DisplayName => "Escalation Throw Event";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(
            BpmnBehaviorCommand.RaiseEscalation(),
            BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(BpmnFlowSelector.SelectTaskFlows(context))));

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN escalation throw event '{context.Element.ElementId}' cannot bind work.");
}

/// <summary>
/// Escalation end event: on token arrival, emit a <see cref="BpmnBehaviorCommandKind.RaiseEscalation"/>
/// command followed by <see cref="BpmnBehaviorCommandKind.ConsumeToken"/> (none-end semantics) — the throw
/// signals its enclosing scope and consumes its token. The interpreter owns the signalling (or the root no-op);
/// the behavior stays semantics-unaware.
/// </summary>
public sealed class EscalationEndEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.EndEventEscalation;
    public string DisplayName => "Escalation End Event";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.RaiseEscalation(), BpmnBehaviorCommand.ConsumeToken());

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN escalation end event '{context.Element.ElementId}' cannot bind work.");
}

/// <summary>
/// Message intermediate throw event: a bound-work send. On token arrival it starts the element's bound publish
/// work; that work completes immediately (fire-and-continue), which is an ordinary work completion that routes
/// the throw token's outbound flows by the shared task selection rules. The behavior stays semantics-unaware.
/// Graph validation guarantees the binding exists (a message throw is never unbound).
/// </summary>
public sealed class MessageThrowEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.IntermediateThrowEventMessage;
    public string DisplayName => "Message Throw Event";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.StartWork());

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context)
    {
        var flows = BpmnFlowSelector.SelectTaskFlows(context);
        if (flows.Count == 0 && context.OutboundFlows.Count > 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.flow.none-taken",
                $"BPMN message throw event '{context.Element.ElementId}' completed with outcomes [{string.Join(", ", context.OutcomeNames)}] but no outbound sequence flow matched and no default flow is declared."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(flows)));
    }
}

/// <summary>
/// Message end event: a bound-work send with none-end semantics. On token arrival it starts the element's bound
/// publish work; when that work completes (fire-and-continue) the token is consumed. The behavior stays
/// semantics-unaware. Graph validation guarantees the binding exists.
/// </summary>
public sealed class MessageEndEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.EndEventMessage;
    public string DisplayName => "Message End Event";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.StartWork());

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.ConsumeToken());
}

/// <summary>Terminate end event: consume every live token and complete the process immediately.</summary>
public sealed class TerminateEndEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.EndEventTerminate;
    public string DisplayName => "End Event (Terminate)";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.TerminateProcess(
            $"BPMN terminate end event '{context.Element.ElementId}' ended the process."));

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN end event '{context.Element.ElementId}' cannot bind work.");
}
