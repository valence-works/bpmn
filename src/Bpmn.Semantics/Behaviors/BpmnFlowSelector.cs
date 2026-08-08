using Bpmn.Model;

namespace Bpmn.Semantics.Behaviors;

/// <summary>
/// Shared outbound-flow selection rules. A conditional flow (one with a <c>conditionOutcome</c>)
/// matches when the completing work reported that outcome name; unconditional non-default flows are
/// always taken (BPMN's implicit AND-split at tasks); the default flow is taken only when nothing else
/// was.
/// </summary>
internal static class BpmnFlowSelector
{
    public static IReadOnlyCollection<BpmnSequenceFlow> SelectTaskFlows(IBpmnBehaviorContext context)
    {
        var outcomes = context.OutcomeNames.ToHashSet(StringComparer.Ordinal);
        var taken = context.OutboundFlows
            .Where(flow => !flow.IsDefault)
            .Where(flow => flow.ConditionOutcome is null || outcomes.Contains(flow.ConditionOutcome))
            .ToArray();

        if (taken.Length > 0)
            return taken;

        return DefaultOrEmpty(context);
    }

    /// <summary>Exclusive selection: the first matching conditional flow in declaration order, else the default.</summary>
    public static IReadOnlyCollection<BpmnSequenceFlow> SelectExclusiveFlow(IBpmnBehaviorContext context)
    {
        var outcomes = context.OutcomeNames.ToHashSet(StringComparer.Ordinal);
        var match = context.OutboundFlows
            .Where(flow => !flow.IsDefault)
            .FirstOrDefault(flow => flow.ConditionOutcome is not null && outcomes.Contains(flow.ConditionOutcome));

        if (match is not null)
            return [match];

        return DefaultOrEmpty(context);
    }

    /// <summary>Inclusive selection: every matching conditional flow plus every unconditional flow, else the default.</summary>
    public static IReadOnlyCollection<BpmnSequenceFlow> SelectInclusiveFlows(IBpmnBehaviorContext context) =>
        SelectTaskFlows(context);

    public static IReadOnlyCollection<BpmnSequenceFlow> DefaultOrEmpty(IBpmnBehaviorContext context)
    {
        var defaultFlow = ResolveDefaultFlow(context);
        return defaultFlow is null ? [] : [defaultFlow];
    }

    public static BpmnSequenceFlow? ResolveDefaultFlow(IBpmnBehaviorContext context)
    {
        if (context.Element.DefaultFlowId is { } defaultFlowId)
            return context.OutboundFlows.FirstOrDefault(flow => StringComparer.Ordinal.Equals(flow.FlowId, defaultFlowId));

        return context.OutboundFlows.FirstOrDefault(flow => flow.IsDefault);
    }

    public static IReadOnlyCollection<string> FlowIds(IReadOnlyCollection<BpmnSequenceFlow> flows) =>
        flows.Select(flow => flow.FlowId).ToArray();
}
