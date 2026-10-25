using Bpmn.Model;
using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>Read-only element/graph/state view handed to <see cref="IBpmnElementBehavior"/> implementations.</summary>
public interface IBpmnBehaviorContext
{
    /// <summary>What caused this behavior invocation.</summary>
    BpmnBehaviorTrigger Trigger { get; }

    /// <summary>The element the current token sits at.</summary>
    BpmnElement Element { get; }

    /// <summary>The current token.</summary>
    BpmnToken Token { get; }

    /// <summary>The sequence flows leaving <see cref="Element"/>.</summary>
    IReadOnlyCollection<BpmnSequenceFlow> OutboundFlows { get; }

    /// <summary>The sequence flows arriving at <see cref="Element"/>.</summary>
    IReadOnlyCollection<BpmnSequenceFlow> InboundFlows { get; }

    /// <summary>The completing work's outcome names (empty on token arrival).</summary>
    IReadOnlyCollection<string> OutcomeNames { get; }

    /// <summary>The execution state as of this dispatch.</summary>
    BpmnExecutionState State { get; }

    /// <summary>
    /// Asks the host for the value of <paramref name="flow"/>'s condition expression. The interpreter builds the
    /// request, asks <see cref="BpmnHostSnapshot.Conditions"/>, and records the answer as a
    /// <see cref="BpmnDiagnosticKind.ConditionEvaluated"/> diagnostic; a behavior only decides which flows to ask
    /// about and what the answers mean. Ask only about a flow of <see cref="OutboundFlows"/> that carries a
    /// <see cref="BpmnSequenceFlow.ConditionExpression"/>.
    /// <para>
    /// The default implementation throws <see cref="BpmnExecutionException"/>, so a context implemented before
    /// condition expressions were evaluated still compiles and fails loudly if one is ever needed.
    /// </para>
    /// </summary>
    BpmnConditionResult EvaluateCondition(BpmnSequenceFlow flow) =>
        throw new BpmnExecutionException(
            $"BPMN behavior context for element '{Element.ElementId}' cannot evaluate condition expressions, so the condition on flow '{flow.FlowId}' has no answer.");
}

/// <summary>What caused an <see cref="IBpmnElementBehavior"/> invocation.</summary>
public enum BpmnBehaviorTrigger
{
    /// <summary>A token arrived at the element.</summary>
    TokenArrived,

    /// <summary>The work bound to the element completed.</summary>
    WorkCompleted
}
