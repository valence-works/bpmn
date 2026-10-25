using Bpmn.Model;

namespace Bpmn.Semantics.Behaviors;

/// <summary>
/// Exclusive (XOR) gateway. Join side: each token passes through independently (handled by the interpreter's
/// join accounting, which never holds tokens at an exclusive gateway). Split side: when a decision is bound as
/// work it is started, and on completion the non-default flows are tested in declaration order against its
/// outcome names and the host's condition answers; the first that holds is taken and nothing after it is
/// evaluated. A flow without any condition holds. Without bound work the same rule runs on arrival, when no
/// outcome is reported, except that a single outbound flow is taken untested unless its only condition is an
/// expression. Otherwise the default flow, and with none the scope faults.
/// </summary>
public sealed class ExclusiveGatewayBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.ExclusiveGateway;
    public string DisplayName => "Exclusive Gateway";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context)
    {
        if (context.Element.BindingRef is not null)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.StartWork());

        if (BpmnFlowSelector.TakesOnlyFlowOnArrival(context.OutboundFlows))
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(context.OutboundFlows)));

        var selection = BpmnFlowSelector.SelectExclusiveFlow(context);
        if (selection.HasFailed)
            return BpmnBehaviorDecision.Of(BpmnFlowSelector.ConditionFailed(context, selection));

        if (selection.Flows.Count == 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.gateway.no-flow-selected",
                $"BPMN exclusive gateway '{context.Element.ElementId}' has no outbound flow whose condition holds, no bound decision work, and no default flow."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(selection.Flows)));
    }

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context)
    {
        var selection = BpmnFlowSelector.SelectExclusiveFlow(context);
        if (selection.HasFailed)
            return BpmnBehaviorDecision.Of(BpmnFlowSelector.ConditionFailed(context, selection));

        if (selection.Flows.Count == 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.gateway.no-flow-selected",
                $"BPMN exclusive gateway '{context.Element.ElementId}' decision completed with outcomes [{string.Join(", ", context.OutcomeNames)}] but no outbound flow's condition held and no default flow is declared."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(selection.Flows)));
    }
}

/// <summary>
/// Parallel (AND) gateway. Join side: the interpreter's join accounting holds tokens until every inbound
/// flow has arrived. Split side: emit one token per outbound flow.
/// </summary>
public sealed class ParallelGatewayBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.ParallelGateway;
    public string DisplayName => "Parallel Gateway";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(context.OutboundFlows)));

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN parallel gateway '{context.Element.ElementId}' cannot bind work.");
}

/// <summary>
/// Inclusive (OR) gateway. Join side: the interpreter's activation-aware join accounting waits while any
/// live token or running work upstream can still reach an un-arrived inbound flow. Split side: when a
/// decision is bound as work it is started, and on completion every flow whose condition holds is taken: an
/// outcome the decision reported, an expression the host answers true, or no condition at all. Every expression
/// is evaluated, in declaration order. Without bound work the same rule runs on arrival, when no outcome is
/// reported. Otherwise the default flow, and with none the scope faults.
/// </summary>
public sealed class InclusiveGatewayBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.InclusiveGateway;
    public string DisplayName => "Inclusive Gateway";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context)
    {
        if (context.Element.BindingRef is not null)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.StartWork());

        var selection = BpmnFlowSelector.SelectInclusiveFlows(context);
        if (selection.HasFailed)
            return BpmnBehaviorDecision.Of(BpmnFlowSelector.ConditionFailed(context, selection));

        if (selection.Flows.Count == 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.gateway.no-flow-selected",
                $"BPMN inclusive gateway '{context.Element.ElementId}' has no outbound flow whose condition holds, no bound decision work, and no default flow."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(selection.Flows)));
    }

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context)
    {
        var selection = BpmnFlowSelector.SelectInclusiveFlows(context);
        if (selection.HasFailed)
            return BpmnBehaviorDecision.Of(BpmnFlowSelector.ConditionFailed(context, selection));

        if (selection.Flows.Count == 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.gateway.no-flow-selected",
                $"BPMN inclusive gateway '{context.Element.ElementId}' decision completed with outcomes [{string.Join(", ", context.OutcomeNames)}] but no flow matched and no default flow is declared."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(selection.Flows)));
    }
}

/// <summary>
/// Event-based gateway. Split side only (it never joins): emit one token per outbound flow, exactly like a
/// parallel split — each outbound targets an intermediate catch event that arms simultaneously. The
/// first-catch-wins race (mark the winner, cancel every losing sibling token and tear down its armed work) is
/// owned entirely by the interpreter; this behavior stays race-unaware. The gateway binds no work.
/// </summary>
public sealed class EventBasedGatewayBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.EventBasedGateway;
    public string DisplayName => "Event-Based Gateway";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(context.OutboundFlows)));

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        throw new BpmnExecutionException($"BPMN event-based gateway '{context.Element.ElementId}' cannot bind work.");
}
