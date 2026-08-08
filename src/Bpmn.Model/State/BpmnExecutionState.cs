using System.Text.Json.Serialization;

namespace Bpmn.Model.State;

/// <summary>
/// The BPMN engine's typed, versioned private-state envelope payload (<c>bpmn.execution.state</c>,
/// schema version 1). All record ids are a pure function of <see cref="Sequence"/>; the only mutation
/// home is <c>BpmnStateMutator</c>.
/// </summary>
public sealed record BpmnExecutionState
{
    [JsonConstructor]
    public BpmnExecutionState(
        IReadOnlyCollection<BpmnToken>? tokens = null,
        IReadOnlyCollection<BpmnActiveWork>? activeChildren = null,
        IReadOnlyCollection<BpmnDiagnosticEvent>? diagnostics = null,
        int sequence = 0,
        bool terminated = false,
        BpmnPendingFault? pendingFault = null,
        IReadOnlyCollection<BpmnEventRace>? races = null,
        IReadOnlyCollection<BpmnLoopState>? loops = null,
        IReadOnlyCollection<BpmnCompensable>? compensables = null,
        IReadOnlyCollection<BpmnCompensationRun>? compensationRuns = null,
        bool cancelling = false)
    {
        Tokens = tokens ?? [];
        ActiveChildren = activeChildren ?? [];
        Diagnostics = diagnostics ?? [];
        Sequence = sequence;
        Terminated = terminated;
        PendingFault = pendingFault;
        Races = races ?? [];
        Loops = loops ?? [];
        Compensables = compensables ?? [];
        CompensationRuns = compensationRuns ?? [];
        Cancelling = cancelling;
    }

    public IReadOnlyCollection<BpmnToken> Tokens { get; init; }
    public IReadOnlyCollection<BpmnActiveWork> ActiveChildren { get; init; }
    public IReadOnlyCollection<BpmnDiagnosticEvent> Diagnostics { get; init; }
    public int Sequence { get; init; }

    /// <summary>The open/resolved first-catch-wins races opened by event-based gateways (spec 119); additive, schema stays v1.</summary>
    public IReadOnlyCollection<BpmnEventRace> Races { get; init; }

    /// <summary>The live multi-instance loops (spec 121); each is a coordinator token with private per-instance sub-tokens. Additive, schema stays v1.</summary>
    public IReadOnlyCollection<BpmnLoopState> Loops { get; init; }

    /// <summary>The durable reverse-order compensation log (spec 124); each host completion carrying an attached compensation boundary is registered here. Never pruned. Additive, schema stays v1.</summary>
    public IReadOnlyCollection<BpmnCompensable> Compensables { get; init; }

    /// <summary>The in-flight compensation replay runs (spec 124); each is a compensate throw/end coordinator token replaying its claimed handlers sequentially. Additive, schema stays v1.</summary>
    public IReadOnlyCollection<BpmnCompensationRun> CompensationRuns { get; init; }

    /// <summary>Set when a terminate end event ended the process; late child completions are ignored.</summary>
    public bool Terminated { get; init; }

    /// <summary>
    /// Set when a cancel end event began cancelling a transaction scope (spec 125): all other live work is
    /// stopped, the registered compensables are replayed, and the process then completes with the
    /// <c>Cancelled</c> outcome (parallel to <see cref="Terminated"/>, but completing with a distinct outcome
    /// rather than <c>Done</c>). Additive, schema stays v1.
    /// </summary>
    public bool Cancelling { get; init; }

    /// <summary>
    /// A fault decision that could not be returned as a terminal continuation because the same
    /// evaluation had already staged child schedules (the runtime forbids terminal decisions that also
    /// schedule children). The next callback surfaces it.
    /// </summary>
    public BpmnPendingFault? PendingFault { get; init; }

    /// <summary>The maximum number of diagnostics <see cref="Prune"/> retains; the oldest are dropped first.</summary>
    public const int DiagnosticsCap = 200;

    /// <summary>
    /// Returns a copy with the records that can never influence a future interpreter decision removed:
    /// consumed tokens no longer referenced by active work, and diagnostics beyond
    /// <see cref="DiagnosticsCap"/>. Intended for a host about to persist the state, so a long-running
    /// process never re-serializes an ever-growing blob.
    /// <para>
    /// <see cref="BpmnTokenStatus.Canceled"/> tokens are <b>never</b> pruned. A terminate strips in-flight
    /// work from <see cref="ActiveChildren"/> while that work may still complete, and the late completion is
    /// absorbed by the by-id token lookup; prune the record and that lookup faults the process instead.
    /// Cancelled counts are bounded by graph structure.
    /// </para>
    /// <para>
    /// Pruning shapes only what is written: the schema is unchanged and <see cref="Sequence"/> is not bumped,
    /// so record ids stay a pure function of mutation order.
    /// </para>
    /// </summary>
    public BpmnExecutionState Prune()
    {
        var retainedTokenIds = ActiveChildren.Select(work => work.TokenId).ToHashSet(StringComparer.Ordinal);
        var tokens = Tokens
            .Where(token => token.Status != BpmnTokenStatus.Consumed || retainedTokenIds.Contains(token.TokenId))
            .ToArray();

        var diagnostics = Diagnostics.Count <= DiagnosticsCap
            ? Diagnostics
            : Diagnostics.Skip(Diagnostics.Count - DiagnosticsCap).ToArray();

        return this with { Tokens = tokens, Diagnostics = diagnostics };
    }
}

/// <summary>A deferred fault decision carried on the execution state (see <see cref="BpmnExecutionState.PendingFault"/>).</summary>
public sealed record BpmnPendingFault(string FaultCode, string Message);
