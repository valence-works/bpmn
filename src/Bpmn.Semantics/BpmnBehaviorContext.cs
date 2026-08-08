using Bpmn.Model;
using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>The default <see cref="IBpmnBehaviorContext"/>: an immutable snapshot of one behavior dispatch.</summary>
public sealed class BpmnBehaviorContext(
    BpmnBehaviorTrigger trigger,
    BpmnElement element,
    BpmnToken token,
    IReadOnlyCollection<BpmnSequenceFlow> outboundFlows,
    IReadOnlyCollection<BpmnSequenceFlow> inboundFlows,
    IReadOnlyCollection<string> outcomeNames,
    BpmnExecutionState state)
    : IBpmnBehaviorContext
{
    /// <inheritdoc />
    public BpmnBehaviorTrigger Trigger { get; } = trigger;

    /// <inheritdoc />
    public BpmnElement Element { get; } = element;

    /// <inheritdoc />
    public BpmnToken Token { get; } = token;

    /// <inheritdoc />
    public IReadOnlyCollection<BpmnSequenceFlow> OutboundFlows { get; } = outboundFlows.ToArray();

    /// <inheritdoc />
    public IReadOnlyCollection<BpmnSequenceFlow> InboundFlows { get; } = inboundFlows.ToArray();

    /// <inheritdoc />
    public IReadOnlyCollection<string> OutcomeNames { get; } = outcomeNames.ToArray();

    /// <inheritdoc />
    public BpmnExecutionState State { get; } = state;
}
