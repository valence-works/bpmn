namespace Bpmn.Semantics;

/// <summary>
/// The behavior of one BPMN element family. Behaviors receive a read-only
/// <see cref="IBpmnBehaviorContext"/> and return a <see cref="BpmnBehaviorDecision"/>; the
/// <see cref="BpmnInterpreter"/> validates and applies those commands, keeping mutation and work-dispatch
/// authority inside the interpreter. A behavior is a pure function of its context.
/// </summary>
public interface IBpmnElementBehavior
{
    /// <summary>The element family this behavior handles (see <see cref="BpmnElementFamilies"/>).</summary>
    string ElementFamily { get; }

    /// <summary>A human-readable name, used in diagnostics.</summary>
    string DisplayName { get; }

    /// <summary>Invoked when a token arrives at an element of this family (after join accounting).</summary>
    BpmnBehaviorDecision OnTokenArrived(IBpmnBehaviorContext context);

    /// <summary>Invoked when the host work bound to the element completed.</summary>
    BpmnBehaviorDecision OnWorkCompleted(IBpmnBehaviorContext context);
}
