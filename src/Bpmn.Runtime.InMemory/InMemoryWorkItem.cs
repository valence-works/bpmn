using Bpmn.Model;

namespace Bpmn.Runtime.InMemory;

/// <summary>What kind of thing the host is running for a unit of bound work.</summary>
public enum InMemoryWorkKind
{
    /// <summary>Ordinary work the outside world finishes: a task, a message wait, a decision.</summary>
    Activity,

    /// <summary>A timer the virtual clock will finish when it comes due.</summary>
    Timer,

    /// <summary>A nested BPMN process this host runs as a child scope.</summary>
    NestedProcess
}

/// <summary>
/// One unit of work this host started and has not yet finished — the host side of
/// <see cref="Bpmn.Semantics.BpmnLiveWork"/>, with everything the host itself needs to run it.
/// </summary>
/// <param name="Handle">The opaque handle this host assigned. The interpreter echoes it and never parses it.</param>
/// <param name="BindingRef">The binding the interpreter asked to start.</param>
/// <param name="ElementId">The element the work belongs to.</param>
/// <param name="IterationId">The multi-instance iteration this instance runs under, or <c>null</c>.</param>
/// <param name="SchedulingCause">Why the interpreter started it.</param>
/// <param name="Correlation">The opaque interpreter state carried with the work.</param>
/// <param name="IterationValues">
/// The per-iteration values the interpreter seeded: the zero-based <c>loopIndex</c>, plus the current item in
/// collection mode. This is what makes a multi-instance instance distinguishable to the thing performing it.
/// </param>
/// <param name="Kind">What the host is running.</param>
/// <param name="IsCatchListener">
/// Whether this work is an armed catcher rather than something on the token's critical path — a boundary
/// event's listener, or an event subprocess's scope listener. Completing one fires the catcher; the process
/// does not need it completed to finish.
/// </param>
/// <param name="DueAt">The virtual instant a timer is due at, or <c>null</c> when the work is not a timer with a known duration.</param>
/// <param name="ScopeInstanceId">The scope that started it.</param>
/// <param name="StartedAt">The virtual instant it was started at.</param>
public sealed record InMemoryWorkItem(
    string Handle,
    string BindingRef,
    string ElementId,
    string? IterationId,
    string SchedulingCause,
    IReadOnlyDictionary<string, string> Correlation,
    IReadOnlyDictionary<string, BpmnValue> IterationValues,
    InMemoryWorkKind Kind,
    bool IsCatchListener,
    TimeSpan? DueAt,
    string ScopeInstanceId,
    TimeSpan StartedAt)
{
    /// <summary>Whether the virtual clock owns finishing this work.</summary>
    public bool IsTimer => Kind == InMemoryWorkKind.Timer;

    /// <inheritdoc />
    public override string ToString()
    {
        var iteration = IterationId is null ? string.Empty : $" iteration={IterationId}";
        var due = DueAt is { } dueAt ? $" due={dueAt}" : string.Empty;
        return $"{Handle}: {ElementId} ({BindingRef}) {Kind}{iteration}{due}";
    }
}

/// <summary>A unit of work this host tore down, kept so a test can assert that a teardown actually happened.</summary>
/// <param name="Handle">The handle that was torn down.</param>
/// <param name="ElementId">The element whose work it was.</param>
/// <param name="Reason">The stable reason code the interpreter gave.</param>
/// <param name="At">The virtual instant it was torn down at.</param>
public sealed record InMemoryCancelledWork(string Handle, string ElementId, string Reason, TimeSpan At)
{
    /// <inheritdoc />
    public override string ToString() => $"{Handle}: {ElementId} cancelled at {At} ({Reason})";
}

/// <summary>
/// A signal a scope raised to its enclosing scope. At a root process there is no enclosing scope to deliver it
/// to, so the host records it here: BPMN says an unmatched escalation at the root is a no-op, and a no-op that
/// leaves no trace is indistinguishable from a bug.
/// </summary>
/// <param name="Code">The signal code; escalation uses <see cref="Bpmn.Semantics.BpmnInterpreter.EscalationSignalCode"/>.</param>
/// <param name="Payload">The signal payload.</param>
/// <param name="Delivered">Whether an enclosing scope received it.</param>
/// <param name="At">The virtual instant it was raised at.</param>
public sealed record InMemoryScopeSignal(string Code, System.Text.Json.JsonElement? Payload, bool Delivered, TimeSpan At)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"{Code}{(Payload is { } payload ? $" {payload}" : string.Empty)} at {At} ({(Delivered ? "delivered to the enclosing scope" : "recorded at the root")})";
}
