using Bpmn.Model;

namespace Bpmn.Semantics.Behaviors;

/// <summary>
/// Shared outbound-flow selection rules. Each non-default flow is tested once, in declaration order:
/// <list type="bullet">
/// <item>a flow with a <c>conditionOutcome</c> is taken when the completing work reported that outcome name
/// (on token arrival nothing is reported, so it is not taken). An expression the flow also carries is retained
/// only and never evaluated;</item>
/// <item>a flow whose only condition is an expression is taken when the host's evaluator answers true. A
/// failed answer stops the selection at once;</item>
/// <item>a flow without any condition is always taken.</item>
/// </list>
/// The default flow is never tested: it is taken only when nothing else was. Exclusive selection takes the first
/// flow that is taken and tests nothing after it; task and inclusive selection take every one (BPMN's implicit
/// AND-split at activities).
/// </summary>
internal static class BpmnFlowSelector
{
    /// <summary>The fault code a failed condition faults the scope with when the host gives none.</summary>
    public const string ConditionFailedFaultCode = "bpmn.condition.failed";

    /// <summary>The fault code an activity or event faults with when it has outbound flows and takes none.</summary>
    public const string NoneTakenFaultCode = "bpmn.flow.none-taken";

    /// <summary>
    /// Routes an activity or event by task selection: a failed condition faults the scope; nothing selected
    /// while the element has outbound flows faults with <see cref="NoneTakenFaultCode"/> and
    /// <paramref name="noneTakenMessage"/>; otherwise one token per selected flow. An element with no outbound
    /// flow at all is an implicit end, and its token is consumed.
    /// </summary>
    public static BpmnBehaviorCommand RouteAsTask(IBpmnBehaviorContext context, string noneTakenMessage)
    {
        var selection = SelectTaskFlows(context);
        if (selection.HasFailed)
            return ConditionFailed(context, selection);

        if (selection.Flows.Count == 0 && context.OutboundFlows.Count > 0)
            return BpmnBehaviorCommand.Fault(NoneTakenFaultCode, noneTakenMessage);

        return BpmnBehaviorCommand.EmitTokens(FlowIds(selection.Flows));
    }

    /// <summary>Task and inclusive selection: every taken flow, else the default, else nothing.</summary>
    public static BpmnFlowSelection SelectTaskFlows(IBpmnBehaviorContext context)
    {
        var outcomes = context.OutcomeNames.ToHashSet(StringComparer.Ordinal);
        var defaultFlow = ResolveDefaultFlow(context);
        var taken = new List<BpmnSequenceFlow>();

        foreach (var flow in NonDefault(context.OutboundFlows, defaultFlow))
        {
            var result = Test(context, flow, outcomes);
            if (result.Kind == BpmnConditionResultKind.Failed)
                return BpmnFlowSelection.FailedAt(flow, result);
            if (result.Kind == BpmnConditionResultKind.True)
                taken.Add(flow);
        }

        if (taken.Count > 0)
            return new BpmnFlowSelection(taken);

        return new BpmnFlowSelection(defaultFlow is null ? [] : [defaultFlow]);
    }

    /// <summary>Exclusive selection: the first taken flow in declaration order, else the default, else nothing.</summary>
    public static BpmnFlowSelection SelectExclusiveFlow(IBpmnBehaviorContext context)
    {
        var outcomes = context.OutcomeNames.ToHashSet(StringComparer.Ordinal);
        var defaultFlow = ResolveDefaultFlow(context);

        foreach (var flow in NonDefault(context.OutboundFlows, defaultFlow))
        {
            var result = Test(context, flow, outcomes);
            if (result.Kind == BpmnConditionResultKind.Failed)
                return BpmnFlowSelection.FailedAt(flow, result);
            if (result.Kind == BpmnConditionResultKind.True)
                return new BpmnFlowSelection([flow]);
        }

        return new BpmnFlowSelection(defaultFlow is null ? [] : [defaultFlow]);
    }

    /// <summary>Inclusive selection: the task rule.</summary>
    public static BpmnFlowSelection SelectInclusiveFlows(IBpmnBehaviorContext context) =>
        SelectTaskFlows(context);

    /// <summary>
    /// The flows task selection is certain to take for a token arriving at <paramref name="element"/>, which
    /// reports no outcome, whatever the host's evaluator would answer. Graph build reads it to predict where a
    /// token goes without asking anyone. A flow whose only condition is an expression is possibly taken, never
    /// certainly, and while one could be taken the default is not certain either.
    /// </summary>
    public static IReadOnlyCollection<BpmnSequenceFlow> CertainTaskFlowsOnArrival(
        BpmnElement element,
        IReadOnlyCollection<BpmnSequenceFlow> outboundFlows)
    {
        var defaultFlow = ResolveDefaultFlow(element, outboundFlows);
        var nonDefault = NonDefault(outboundFlows, defaultFlow).ToArray();

        var unconditional = nonDefault.Where(IsUnconditional).ToArray();
        if (unconditional.Length > 0)
            return unconditional;

        if (nonDefault.Any(IsExpressionOnly))
            return [];

        return defaultFlow is null ? [] : [defaultFlow];
    }

    /// <summary>
    /// The flow an unbound exclusive gateway is certain to take for an arriving token, whatever the host's
    /// evaluator would answer; empty when that depends on an answer or nothing can be taken. Mirrors
    /// <see cref="ExclusiveGatewayBehavior.OnTokenArrived"/>, including its single-flow rule.
    /// </summary>
    public static IReadOnlyCollection<BpmnSequenceFlow> CertainExclusiveFlowOnArrival(
        BpmnElement element,
        IReadOnlyCollection<BpmnSequenceFlow> outboundFlows)
    {
        if (TakesOnlyFlowOnArrival(outboundFlows))
            return outboundFlows;

        var defaultFlow = ResolveDefaultFlow(element, outboundFlows);
        foreach (var flow in NonDefault(outboundFlows, defaultFlow))
        {
            if (flow.ConditionOutcome is not null)
                continue;
            if (flow.ConditionExpression is not null)
                return [];
            return [flow];
        }

        return defaultFlow is null ? [] : [defaultFlow];
    }

    /// <summary>
    /// Whether an unbound exclusive gateway takes its single outbound flow without testing it. It does, as it
    /// always has, unless that flow's only condition is an expression: an outcome cannot match on arrival, but a
    /// single outcome-conditioned flow has always been taken, and keeping that is the smaller change.
    /// </summary>
    public static bool TakesOnlyFlowOnArrival(IReadOnlyCollection<BpmnSequenceFlow> outboundFlows) =>
        outboundFlows.Count == 1 && !IsExpressionOnly(outboundFlows.Single());

    /// <summary>The fault a failed condition answer turns into.</summary>
    public static BpmnBehaviorCommand ConditionFailed(IBpmnBehaviorContext context, BpmnFlowSelection selection)
    {
        var failure = selection.Failure ?? throw new InvalidOperationException("The selection did not fail.");
        return BpmnBehaviorCommand.Fault(
            failure.FaultCode ?? ConditionFailedFaultCode,
            $"BPMN condition on flow '{selection.FailedFlow!.FlowId}' leaving '{context.Element.ElementId}' could not be evaluated: {failure.Message}");
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

    /// <summary>True for a flow whose only condition is an expression, which the host evaluates.</summary>
    public static bool IsExpressionOnly(BpmnSequenceFlow flow) =>
        flow is { ConditionOutcome: null, ConditionExpression: not null };

    private static bool IsUnconditional(BpmnSequenceFlow flow) =>
        flow is { ConditionOutcome: null, ConditionExpression: null };

    /// <summary>
    /// Every outbound flow except the resolved default, in declaration order. The default is the element's
    /// <c>default</c> reference when it has one, which is how a read document marks it, so a flow is excluded
    /// here by being the default rather than by its own <see cref="BpmnSequenceFlow.IsDefault"/> flag alone.
    /// </summary>
    private static IEnumerable<BpmnSequenceFlow> NonDefault(IReadOnlyCollection<BpmnSequenceFlow> outboundFlows, BpmnSequenceFlow? defaultFlow) =>
        outboundFlows.Where(flow => !flow.IsDefault
                                    && (defaultFlow is null || !StringComparer.Ordinal.Equals(flow.FlowId, defaultFlow.FlowId)));

    private static BpmnConditionResult Test(IBpmnBehaviorContext context, BpmnSequenceFlow flow, IReadOnlySet<string> outcomes)
    {
        if (flow.ConditionOutcome is { } outcome)
            return BpmnConditionResult.Of(outcomes.Contains(outcome));

        if (flow.ConditionExpression is not null)
            return context.EvaluateCondition(flow);

        return BpmnConditionResult.True;
    }
}

/// <summary>
/// What a selection chose: the flows to take, or the flow whose condition failed and the host's answer for it.
/// </summary>
internal sealed record BpmnFlowSelection(IReadOnlyCollection<BpmnSequenceFlow> Flows)
{
    /// <summary>The flow whose condition the host could not evaluate, when the selection failed.</summary>
    public BpmnSequenceFlow? FailedFlow { get; private init; }

    /// <summary>The host's failed answer, when the selection failed.</summary>
    public BpmnConditionResult? Failure { get; private init; }

    /// <summary>Whether a condition failed, which faults the scope instead of routing.</summary>
    public bool HasFailed => Failure is not null;

    public static BpmnFlowSelection FailedAt(BpmnSequenceFlow flow, BpmnConditionResult failure) =>
        new(Array.Empty<BpmnSequenceFlow>()) { FailedFlow = flow, Failure = failure };
}
