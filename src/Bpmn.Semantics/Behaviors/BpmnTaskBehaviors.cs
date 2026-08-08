using Bpmn.Model;

namespace Bpmn.Semantics.Behaviors;

/// <summary>
/// Task family (task/userTask/serviceTask/…): starts the element's bound work when a token arrives, then
/// routes outbound flows by the completing work's outcome names. An unbound abstract task is a
/// pass-through.
/// </summary>
public sealed class TaskBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.Task;
    public string DisplayName => "Task";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context)
    {
        if (context.Element.BindingRef is not null)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.StartWork());

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(
            BpmnFlowSelector.FlowIds(BpmnFlowSelector.SelectTaskFlows(context))));
    }

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context)
    {
        var flows = BpmnFlowSelector.SelectTaskFlows(context);
        if (flows.Count == 0 && context.OutboundFlows.Count > 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.flow.none-taken",
                $"BPMN task '{context.Element.ElementId}' completed with outcomes [{string.Join(", ", context.OutcomeNames)}] but no outbound sequence flow matched and no default flow is declared."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(flows)));
    }
}

/// <summary>
/// Embedded subprocess: starts the bound work (typically a nested BPMN process) and routes like a task on
/// completion.
/// </summary>
public sealed class SubProcessBehavior : IBpmnElementBehavior
{
    public string ElementFamily => BpmnElementFamilies.SubProcess;
    public string DisplayName => "Subprocess";

    public BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context) =>
        BpmnBehaviorDecision.Of(BpmnBehaviorCommand.StartWork());

    public BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context)
    {
        var flows = BpmnFlowSelector.SelectTaskFlows(context);
        if (flows.Count == 0 && context.OutboundFlows.Count > 0)
            return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.Fault(
                "bpmn.flow.none-taken",
                $"BPMN subprocess '{context.Element.ElementId}' completed but no outbound sequence flow matched and no default flow is declared."));

        return BpmnBehaviorDecision.Of(BpmnBehaviorCommand.EmitTokens(BpmnFlowSelector.FlowIds(flows)));
    }
}
