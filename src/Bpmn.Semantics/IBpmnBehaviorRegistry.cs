namespace Bpmn.Semantics;

/// <summary>The element-family to <see cref="IBpmnElementBehavior"/> lookup the interpreter dispatches through.</summary>
public interface IBpmnBehaviorRegistry
{
    /// <summary>Every registered behavior.</summary>
    IReadOnlyCollection<IBpmnElementBehavior> Behaviors { get; }

    /// <summary>Finds the behavior registered for an element family.</summary>
    bool TryGet(string elementFamily, out IBpmnElementBehavior behavior);

    /// <summary>Finds the behavior registered for an element family, or throws.</summary>
    IBpmnElementBehavior GetRequired(string elementFamily);

    /// <summary>Registers a behavior, replacing any behavior already registered for its family.</summary>
    void Register(IBpmnElementBehavior behavior);
}
