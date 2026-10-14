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
    public static IReadOnlyCollection<BpmnSequenceFlow> SelectTaskFlows(IBpmnBehaviorContext context) =>
        SelectTaskFlows(context.Element, context.OutboundFlows, context.OutcomeNames);

    /// <summary>
    /// Task selection without a behavior context. Graph build uses it to predict where a token arriving at an
    /// unbound element goes (arrival reports no outcomes), so it reads the same rule the behaviors run.
    /// </summary>
    public static IReadOnlyCollection<BpmnSequenceFlow> SelectTaskFlows(
        BpmnElement element,
        IReadOnlyCollection<BpmnSequenceFlow> outboundFlows,
        IReadOnlyCollection<string> outcomeNames)
    {
        var outcomes = outcomeNames.ToHashSet(StringComparer.Ordinal);
        var taken = outboundFlows
            .Where(flow => !flow.IsDefault)
            .Where(flow => flow.ConditionOutcome is null || outcomes.Contains(flow.ConditionOutcome))
            .ToArray();

        if (taken.Length > 0)
            return taken;

        return ResolveDefaultFlow(element, outboundFlows) is { } defaultFlow ? [defaultFlow] : [];
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

    public static BpmnSequenceFlow? ResolveDefaultFlow(IBpmnBehaviorContext context) =>
        ResolveDefaultFlow(context.Element, context.OutboundFlows);

    /// <summary>The element's default flow among <paramref name="outboundFlows"/>, without a behavior context.</summary>
    public static BpmnSequenceFlow? ResolveDefaultFlow(BpmnElement element, IReadOnlyCollection<BpmnSequenceFlow> outboundFlows)
    {
        if (element.DefaultFlowId is { } defaultFlowId)
            return outboundFlows.FirstOrDefault(flow => StringComparer.Ordinal.Equals(flow.FlowId, defaultFlowId));

        return outboundFlows.FirstOrDefault(flow => flow.IsDefault);
    }

    public static IReadOnlyCollection<string> FlowIds(IReadOnlyCollection<BpmnSequenceFlow> flows) =>
        flows.Select(flow => flow.FlowId).ToArray();
}
