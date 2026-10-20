using Bpmn.Model;
using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>The default <see cref="IBpmnBehaviorContext"/>: an immutable snapshot of one behavior dispatch.</summary>
public sealed class BpmnBehaviorContext : IBpmnBehaviorContext
{
    private readonly Func<BpmnSequenceFlow, BpmnConditionResult>? _conditionEvaluator;

    /// <summary>Creates a context that cannot evaluate condition expressions.</summary>
    public BpmnBehaviorContext(
        BpmnBehaviorTrigger trigger,
        BpmnElement element,
        BpmnToken token,
        IReadOnlyCollection<BpmnSequenceFlow> outboundFlows,
        IReadOnlyCollection<BpmnSequenceFlow> inboundFlows,
        IReadOnlyCollection<string> outcomeNames,
        BpmnExecutionState state)
        : this(trigger, element, token, outboundFlows, inboundFlows, outcomeNames, state, conditionEvaluator: null)
    {
    }

    /// <summary>Creates a context whose <see cref="EvaluateCondition"/> answers through <paramref name="conditionEvaluator"/>.</summary>
    /// <param name="trigger">What caused this behavior invocation.</param>
    /// <param name="element">The element the current token sits at.</param>
    /// <param name="token">The current token.</param>
    /// <param name="outboundFlows">The sequence flows leaving <paramref name="element"/>.</param>
    /// <param name="inboundFlows">The sequence flows arriving at <paramref name="element"/>.</param>
    /// <param name="outcomeNames">The completing work's outcome names (empty on token arrival).</param>
    /// <param name="state">The execution state as of this dispatch.</param>
    /// <param name="conditionEvaluator">
    /// Answers one flow's condition expression, or <c>null</c> for a context that cannot, whose
    /// <see cref="EvaluateCondition"/> then throws <see cref="BpmnExecutionException"/>.
    /// </param>
    public BpmnBehaviorContext(
        BpmnBehaviorTrigger trigger,
        BpmnElement element,
        BpmnToken token,
        IReadOnlyCollection<BpmnSequenceFlow> outboundFlows,
        IReadOnlyCollection<BpmnSequenceFlow> inboundFlows,
        IReadOnlyCollection<string> outcomeNames,
        BpmnExecutionState state,
        Func<BpmnSequenceFlow, BpmnConditionResult>? conditionEvaluator)
    {
        Trigger = trigger;
        Element = element;
        Token = token;
        OutboundFlows = outboundFlows.ToArray();
        InboundFlows = inboundFlows.ToArray();
        OutcomeNames = outcomeNames.ToArray();
        State = state;
        _conditionEvaluator = conditionEvaluator;
    }

    /// <inheritdoc />
    public BpmnBehaviorTrigger Trigger { get; }

    /// <inheritdoc />
    public BpmnElement Element { get; }

    /// <inheritdoc />
    public BpmnToken Token { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<BpmnSequenceFlow> OutboundFlows { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<BpmnSequenceFlow> InboundFlows { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> OutcomeNames { get; }

    /// <inheritdoc />
    public BpmnExecutionState State { get; }

    /// <inheritdoc />
    public BpmnConditionResult EvaluateCondition(BpmnSequenceFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        return _conditionEvaluator is { } evaluate
            ? evaluate(flow)
            : throw new BpmnExecutionException(
                $"BPMN behavior context for element '{Element.ElementId}' cannot evaluate condition expressions, so the condition on flow '{flow.FlowId}' has no answer.");
    }
}
