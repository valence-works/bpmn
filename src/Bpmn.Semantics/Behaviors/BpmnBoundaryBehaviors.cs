using Bpmn.Model;
using Bpmn.Model.State;

namespace Bpmn.Semantics.Behaviors;

/// <summary>
/// Boundary event. A single behavior serves both boundary kinds; the interrupting-teardown and
/// error-catching semantics are owned entirely by <see cref="BpmnInterpreter"/>, so this behavior only
/// routes the boundary's outbound flows when it fires:
/// <list type="bullet">
/// <item>A <b>catch</b> boundary (timer/message/signal) arms a suspending listener; when that listener
/// completes, <see cref="OnWorkCompleted"/> routes the outbound flows (the interpreter has already torn
/// down the host and/or the sibling listeners for an interrupting fire, before dispatch).</item>
/// <item>An <b>error</b> boundary has no listener; the interpreter mints an <see cref="BpmnTokenStatus.Active"/>
/// token at the boundary when the host's work faults, so <see cref="OnTokenArrived"/> routes it.</item>
/// </list>
/// Boundary elements bind no work on the arriving-token path (only the catch listener is bound work,
/// started by the interpreter's arming), so <see cref="OnTokenArrived"/> emits rather than starts work.
/// </summary>
public sealed class BoundaryEventBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.BoundaryEvent;
    public string DisplayName => "Boundary Event";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        Route(context);

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context) =>
        Route(context);

    private static BpmnBehaviorDecision Route(IBpmnBehaviorContext context)
    {
        var flows = BpmnFlowSelector.SelectTaskFlows(context);
        if (flows.Count == 0 && context.OutboundFlows.Count > 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.flow.none-taken",
                $"BPMN boundary event '{context.Element.ElementId}' fired but no outbound sequence flow matched and no default flow is declared."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(flows)));
    }
}
