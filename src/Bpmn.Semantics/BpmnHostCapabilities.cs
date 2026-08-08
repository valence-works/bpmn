using Bpmn.Model;

namespace Bpmn.Semantics;

/// <summary>
/// What a host can do for the interpreter beyond starting and completing work. A capability is declared once,
/// at graph build time, and <see cref="BpmnGraph.Build"/> refuses a process definition that needs one the host
/// did not declare.
/// <para>
/// The library refuses rather than degrades. Silently skipping a teardown a host cannot perform turns correct
/// BPMN into subtly incorrect BPMN — an interrupting boundary event would leave two live branches where the
/// specification says one — and the damage surfaces long after its cause. An upfront refusal naming the
/// missing capability and the elements that need it costs one clear exception and buys correctness.
/// </para>
/// </summary>
[Flags]
public enum BpmnHostCapabilities
{
    /// <summary>The host can only start work and report its completion. Sufficient for plain token flow.</summary>
    None = 0,

    /// <summary>
    /// The host can act on <see cref="BpmnHostCommand.CancelWorkSubtree"/>: stop a started unit of work and
    /// everything it in turn started. Required wherever BPMN interrupts running work.
    /// </summary>
    SubtreeCancellation = 1,

    /// <summary>
    /// The host can act on <see cref="BpmnHostCommand.SignalEnclosingScope"/>: deliver a signal from this
    /// process to the scope that invoked it, and deliver signals from invoked processes back in through
    /// <see cref="BpmnInterpreter.OnWorkSignalled"/>. Required for escalation.
    /// </summary>
    ScopeSignalling = 2,

    /// <summary>
    /// The host can start the same bound work several times concurrently, each under its own
    /// <see cref="BpmnIterationScope"/>, and report each completion with its iteration id. Required for
    /// multi-instance activities.
    /// </summary>
    IterationScopes = 4,

    /// <summary>
    /// The host can answer <see cref="IBpmnVariableReader.TryRead"/> for the process's declared variables.
    /// Required for a collection-mode multi-instance activity, whose instance count comes from a variable.
    /// </summary>
    ScopeVariables = 8,

    /// <summary>Every capability. What a fully featured host declares.</summary>
    Full = SubtreeCancellation | ScopeSignalling | IterationScopes | ScopeVariables
}

/// <summary>
/// The host capabilities a process definition needs, and the elements that need them. Computed statically
/// from the definition alone — no host, no state, no execution.
/// </summary>
/// <param name="Required">The union of every capability the definition needs.</param>
/// <param name="DrivingElementIds">Per capability, the element ids that need it, in ordinal order.</param>
public sealed record BpmnCapabilityRequirements(
    BpmnHostCapabilities Required,
    IReadOnlyDictionary<BpmnHostCapabilities, IReadOnlyList<string>> DrivingElementIds)
{
    /// <summary>A definition that needs nothing beyond starting and completing work.</summary>
    public static BpmnCapabilityRequirements None { get; } =
        new(BpmnHostCapabilities.None, new Dictionary<BpmnHostCapabilities, IReadOnlyList<string>>());

    /// <summary>The single capabilities <see cref="Analyze"/> reports on, in declaration order.</summary>
    private static readonly BpmnHostCapabilities[] Individually =
    [
        BpmnHostCapabilities.SubtreeCancellation,
        BpmnHostCapabilities.ScopeSignalling,
        BpmnHostCapabilities.IterationScopes,
        BpmnHostCapabilities.ScopeVariables
    ];

    /// <summary>
    /// Determines which capabilities <paramref name="definition"/> needs.
    /// <list type="bullet">
    /// <item><b>Subtree cancellation</b> — every construct that tears running work down: an interrupting
    /// boundary event, a catch boundary event (whose armed listener is torn down when its host completes),
    /// an event subprocess (an interrupting one drains the whole scope; every scope listener is retired when
    /// the scope completes), and an event-based gateway (whose losing branches are torn down when the first
    /// catch wins).</item>
    /// <item><b>Scope signalling</b> — escalation: a throw or end event signals the enclosing scope, and a
    /// scope carrying escalation catchers re-signals what it cannot match itself.</item>
    /// <item><b>Iteration scopes</b> — a multi-instance activity.</item>
    /// <item><b>Scope variables</b> — a collection-mode multi-instance activity, whose instance count and
    /// per-instance items come from a declared variable.</item>
    /// </list>
    /// Only <paramref name="definition"/> itself is analyzed. A nested process carried as bound work is a
    /// separate scope with its own graph, so the host analyzes and builds it separately.
    /// </summary>
    public static BpmnCapabilityRequirements Analyze(BpmnProcessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var drivers = new Dictionary<BpmnHostCapabilities, SortedSet<string>>();
        var required = BpmnHostCapabilities.None;

        void Require(BpmnHostCapabilities capability, string elementId)
        {
            required |= capability;
            if (!drivers.TryGetValue(capability, out var ids))
                drivers[capability] = ids = new SortedSet<string>(StringComparer.Ordinal);
            ids.Add(elementId);
        }

        foreach (var element in definition.Elements)
        {
            if (StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent)
                && !BpmnElementFamilies.IsCompensationBoundary(element)
                && (element.CancelActivity || IsCatchBoundary(element)))
                Require(BpmnHostCapabilities.SubtreeCancellation, element.ElementId);

            if (element.TriggeredByEvent)
                Require(BpmnHostCapabilities.SubtreeCancellation, element.ElementId);

            if (StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.EventBasedGateway))
                Require(BpmnHostCapabilities.SubtreeCancellation, element.ElementId);

            if (BpmnElementFamilies.IsEscalationThrowOrEnd(element) || BpmnElementFamilies.IsEscalationBoundary(element))
                Require(BpmnHostCapabilities.ScopeSignalling, element.ElementId);

            if (element.LoopCharacteristics is { } loop)
            {
                Require(BpmnHostCapabilities.IterationScopes, element.ElementId);
                if (loop.IsCollectionMode)
                    Require(BpmnHostCapabilities.ScopeVariables, element.ElementId);
            }
        }

        return required == BpmnHostCapabilities.None
            ? None
            : new BpmnCapabilityRequirements(
                required,
                drivers.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)entry.Value.ToArray()));
    }

    /// <summary>The capabilities <paramref name="available"/> does not cover.</summary>
    public BpmnHostCapabilities MissingFrom(BpmnHostCapabilities available) => Required & ~available;

    /// <summary>
    /// Throws a <see cref="BpmnCapabilityException"/> naming every unmet capability and the elements that
    /// need it, or returns quietly when <paramref name="available"/> covers everything.
    /// </summary>
    public void ThrowIfUnmet(BpmnHostCapabilities available, string processId)
    {
        var missing = MissingFrom(available);
        if (missing == BpmnHostCapabilities.None)
            return;

        var unmet = Individually.Where(capability => (missing & capability) != 0).ToArray();
        var drivingElementIds = unmet
            .SelectMany(capability => DrivingElementIds.TryGetValue(capability, out var ids) ? ids : [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var detail = string.Join("; ", unmet.Select(capability =>
        {
            var ids = DrivingElementIds.TryGetValue(capability, out var driving) ? driving : [];
            return $"{capability} (needed by {string.Join(", ", ids.Select(id => $"'{id}'"))})";
        }));

        throw new BpmnCapabilityException(
            $"BPMN process '{processId}' needs host capabilities the host did not declare: {detail}. "
            + "Declare them, or remove the constructs that need them — running without them would produce incorrect BPMN, "
            + "so the graph is refused rather than degraded.",
            missing,
            drivingElementIds);
    }

    /// <summary>True for a boundary event that arms suspending listener work (timer, message, or signal).</summary>
    private static bool IsCatchBoundary(BpmnElement element) =>
        !BpmnElementFamilies.IsErrorBoundary(element)
        && !BpmnElementFamilies.IsEscalationBoundary(element)
        && !BpmnElementFamilies.IsCompensationBoundary(element)
        && !BpmnElementFamilies.IsCancelBoundary(element);
}
