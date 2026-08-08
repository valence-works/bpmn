using Bpmn.Semantics.Behaviors;

namespace Bpmn.Semantics;

/// <summary>The default <see cref="IBpmnBehaviorRegistry"/>: a family-keyed dictionary, last registration wins.</summary>
public sealed class BpmnBehaviorRegistry : IBpmnBehaviorRegistry
{
    private readonly Dictionary<string, IBpmnElementBehavior> _behaviors = new(StringComparer.Ordinal);

    /// <summary>Creates an empty registry.</summary>
    public BpmnBehaviorRegistry()
    {
    }

    /// <summary>Creates a registry populated with <paramref name="behaviors"/>.</summary>
    public BpmnBehaviorRegistry(IEnumerable<IBpmnElementBehavior> behaviors)
    {
        foreach (var behavior in behaviors)
            Register(behavior);
    }

    /// <summary>
    /// A registry carrying the built-in behavior for every element family the interpreter executes. This is
    /// the whole composition root of the library: no container, no discovery, no configuration.
    /// </summary>
    public static BpmnBehaviorRegistry CreateDefault() =>
        new(
        [
            StartEventBehavior.None(),
            StartEventBehavior.Timer(),
            StartEventBehavior.Message(),
            StartEventBehavior.Signal(),
            StartEventBehavior.Escalation(),
            StartEventBehavior.Error(),
            new NoneEndEventBehavior(),
            new TerminateEndEventBehavior(),
            new CompensationEndEventBehavior(),
            new CancelEndEventBehavior(),
            new EscalationEndEventBehavior(),
            new MessageEndEventBehavior(),
            new CatchEventBehavior(),
            new CompensationThrowEventBehavior(),
            new EscalationThrowEventBehavior(),
            new MessageThrowEventBehavior(),
            new TaskBehavior(),
            new SubProcessBehavior(),
            new ExclusiveGatewayBehavior(),
            new ParallelGatewayBehavior(),
            new InclusiveGatewayBehavior(),
            new EventBasedGatewayBehavior(),
            new BoundaryEventBehavior()
        ]);

    /// <inheritdoc />
    public IReadOnlyCollection<IBpmnElementBehavior> Behaviors => _behaviors.Values;

    /// <inheritdoc />
    public bool TryGet(string elementFamily, out IBpmnElementBehavior behavior)
    {
        if (string.IsNullOrWhiteSpace(elementFamily)) throw new ArgumentException("An element family is required.", nameof(elementFamily));
        return _behaviors.TryGetValue(elementFamily, out behavior!);
    }

    /// <inheritdoc />
    public IBpmnElementBehavior GetRequired(string elementFamily)
    {
        if (TryGet(elementFamily, out var behavior))
            return behavior;

        throw new BpmnExecutionException($"No BPMN element behavior is registered for element family '{elementFamily}'.");
    }

    /// <inheritdoc />
    public void Register(IBpmnElementBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        _behaviors[behavior.ElementFamily] = behavior;
    }
}
