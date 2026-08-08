using Bpmn.Model;

namespace Bpmn.Semantics.Behaviors;

/// <summary>
/// Exclusive (XOR) gateway. Join side: each token passes through independently (handled by the interpreter's
/// join accounting, which never holds tokens at an exclusive gateway). Split side: when a decision
/// is bound as work it is started, and the first outcome-matching conditional flow is taken on completion;
/// without bound work the gateway needs a single outbound flow or a default flow.
/// </summary>
public sealed class ExclusiveGatewayBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.ExclusiveGateway;
    public string DisplayName => "Exclusive Gateway";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context)
    {
        if (context.Element.BindingRef is not null)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.StartWork());

        if (context.OutboundFlows.Count == 1)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(context.OutboundFlows)));

        var defaultFlow = BpmnFlowSelector.ResolveDefaultFlow(context);
        if (defaultFlow is not null)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens([defaultFlow.FlowId]));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
            "bpmn.gateway.no-flow-selected",
            $"BPMN exclusive gateway '{context.Element.ElementId}' has multiple outbound flows but no bound decision work and no default flow."));
    }

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context)
    {
        var flows = BpmnFlowSelector.SelectExclusiveFlow(context);
        if (flows.Count == 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.gateway.no-flow-selected",
                $"BPMN exclusive gateway '{context.Element.ElementId}' decision completed with outcomes [{string.Join(", ", context.OutcomeNames)}] but no conditional flow matched and no default flow is declared."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(flows)));
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
/// decision is bound as work, every outcome-matching conditional flow plus every unconditional flow is
/// taken; without bound work, all unconditional flows.
/// </summary>
public sealed class InclusiveGatewayBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.InclusiveGateway;
    public string DisplayName => "Inclusive Gateway";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context)
    {
        if (context.Element.BindingRef is not null)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.StartWork());

        var flows = context.OutboundFlows.Where(flow => !flow.IsDefault && flow.ConditionOutcome is null).ToArray();
        if (flows.Length == 0)
        {
            var defaultFlow = BpmnFlowSelector.ResolveDefaultFlow(context);
            if (defaultFlow is not null)
                return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens([defaultFlow.FlowId]));

            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.gateway.no-flow-selected",
                $"BPMN inclusive gateway '{context.Element.ElementId}' has no unconditional outbound flow, no bound decision work, and no default flow."));
        }

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(flows)));
    }

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context)
    {
        var flows = BpmnFlowSelector.SelectInclusiveFlows(context);
        if (flows.Count == 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.gateway.no-flow-selected",
                $"BPMN inclusive gateway '{context.Element.ElementId}' decision completed with outcomes [{string.Join(", ", context.OutcomeNames)}] but no flow matched and no default flow is declared."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(flows)));
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
