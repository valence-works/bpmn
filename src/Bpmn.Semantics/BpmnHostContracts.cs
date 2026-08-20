using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>
/// Everything the interpreter needs to know about the host's world for one evaluation. The interpreter reads
/// this snapshot and never calls back into the host: it returns commands instead.
/// </summary>
/// <param name="ScopeInstanceId">
/// An opaque host-chosen identity for this running process scope. The interpreter only echoes it (onto tokens,
/// for provenance) and never parses it.
/// </param>
/// <param name="HasEnclosingScope">
/// Whether this process was invoked by another scope that can receive a
/// <see cref="BpmnHostCommand.SignalEnclosingScope"/>. A root process has none, so an unhandled escalation is a
/// documented no-op rather than a fault.
/// </param>
/// <param name="LiveWork">The units of work this scope started that are still running.</param>
/// <param name="InvocationCorrelation">
/// The <see cref="BpmnHostCommand.StartWork.Correlation"/> of the work that started THIS scope, when this
/// scope is a nested process. Empty for a process the host started directly.
/// <para>
/// It belongs to the scope and is fixed for the scope's lifetime. It is not a per-callback channel:
/// there is deliberately nowhere to hand a completing unit of work's correlation back, and writing one
/// here corrupts the event-subprocess start-element hint, which is read from this same dictionary.
/// </para>
/// <para>
/// Because correlation does not round-trip, the interpreter re-finds a parked token from
/// (binding ref, iteration id) alone. That imposes a rule on every host: <b>at most one live unit of
/// work per (binding ref, iteration id) within a scope</b>. Multi-instance instances share a binding ref
/// and are told apart by their iteration id, which is what it is for.
/// </para>
/// <para>
/// The rule holds <b>once a command batch has been applied in full, in order</b> — not at every point
/// within one. An interrupting path may emit the replacement <see cref="BpmnHostCommand.StartWork"/>
/// ahead of the <see cref="BpmnHostCommand.CancelWorkSubtree"/> for the unit it supersedes, so a host
/// that applied the batch halfway would see the slot doubly occupied. Two obligations follow: apply
/// commands in the order returned, and key the ledger by handle rather than by slot, so the teardown
/// still names the older unit unambiguously.
/// </para>
/// </param>
/// <param name="Variables">The process's declared variables, as the host sees them.</param>
/// <param name="Capabilities">What this host can do. Must match what the graph was built with.</param>
public sealed record BpmnHostSnapshot(
    string ScopeInstanceId,
    bool HasEnclosingScope,
    IReadOnlyCollection<BpmnLiveWork> LiveWork,
    IReadOnlyDictionary<string, string> InvocationCorrelation,
    IBpmnVariableReader Variables,
    BpmnHostCapabilities Capabilities)
{
    /// <summary>A snapshot for a root scope with nothing running and no variables.</summary>
    public static BpmnHostSnapshot Root(string scopeInstanceId, BpmnHostCapabilities capabilities = BpmnHostCapabilities.Full) =>
        new(scopeInstanceId, HasEnclosingScope: false, [], new Dictionary<string, string>(), BpmnNoVariables.Instance, capabilities);
}

/// <summary>
/// One unit of work this scope started that is still running.
/// </summary>
/// <param name="BindingRef">The <see cref="BpmnElement.BindingRef"/> (or <see cref="BpmnElement.ListenerBindingRef"/>) it runs.</param>
/// <param name="IterationId">
/// The <see cref="BpmnIterationScope.IterationId"/> it was started under, or <c>null</c> for ordinary
/// single-run work. Several concurrent instances of the same binding are distinguished by this.
/// </param>
/// <param name="Handle">
/// An opaque host handle for this running unit. The interpreter echoes it back in
/// <see cref="BpmnHostCommand.CancelWorkSubtree"/> and never parses it.
/// </param>
public sealed record BpmnLiveWork(string BindingRef, string? IterationId, string Handle);

/// <summary>Reads the process's declared variables. The interpreter only ever reads; it never writes.</summary>
public interface IBpmnVariableReader
{
    /// <summary>Reads a declared variable by name. Returns <c>false</c> when the host has no such variable at all.</summary>
    bool TryRead(string name, out BpmnValue value);
}

/// <summary>A reader for a host with no variables; every read reports the variable as unknown.</summary>
public sealed class BpmnNoVariables : IBpmnVariableReader
{
    /// <summary>The shared instance.</summary>
    public static BpmnNoVariables Instance { get; } = new();

    private BpmnNoVariables()
    {
    }

    /// <inheritdoc />
    public bool TryRead(string name, out BpmnValue value)
    {
        value = BpmnValue.Absent;
        return false;
    }
}

/// <summary>
/// The per-instance scope a multi-instance activity's work runs under. The host seeds these values so the work
/// can read them, and reports the completion back under the same <see cref="IterationId"/>.
/// </summary>
/// <param name="IterationId">An interpreter-minted, deterministic id, unique within the process.</param>
/// <param name="Values">
/// The values to seed: always the zero-based <see cref="BpmnLoopCharacteristics.LoopIndexVariable"/>, plus the
/// current item under the activity's <see cref="BpmnLoopCharacteristics.ItemVariable"/> in collection mode.
/// </param>
public sealed record BpmnIterationScope(string IterationId, IReadOnlyDictionary<string, BpmnValue> Values);

/// <summary>
/// Names the start event a process instance begins at, when the host resolved an external trigger (a message,
/// a signal, or a timer) rather than invoking the process directly.
/// </summary>
/// <param name="StartElementId">The event-defined start event the trigger matched.</param>
public sealed record BpmnStartSignal(string StartElementId);

/// <summary>
/// Something the interpreter asks the host to do. There are exactly three: start work, tear work down, and
/// signal the enclosing scope. Notably there is no fault-resolution command — how an error was handled is a
/// verdict the interpreter returns (<see cref="BpmnErrorDisposition"/>), not an instruction it issues.
/// </summary>
public abstract record BpmnHostCommand
{
    private BpmnHostCommand()
    {
    }

    /// <summary>Start the work bound to a BPMN element.</summary>
    /// <param name="BindingRef">The opaque binding key naming the work to start.</param>
    /// <param name="ElementId">The element the work belongs to, for the host's own reporting.</param>
    /// <param name="TokenId">The token parked while the work runs.</param>
    /// <param name="SchedulingCause">Why the work is being started, for the host's own reporting.</param>
    /// <param name="Correlation">
    /// Opaque interpreter state the host must carry with the work and hand back on the resulting callback,
    /// and — when the work is a nested BPMN process — supply as that process's
    /// <see cref="BpmnHostSnapshot.InvocationCorrelation"/>.
    /// </param>
    /// <param name="IterationScope">The per-instance scope for a multi-instance instance; <c>null</c> otherwise.</param>
    /// <param name="StartElementHint">
    /// For an event subprocess body, the start event the nested process must begin at; <c>null</c> otherwise.
    /// </param>
    public sealed record StartWork(
        string BindingRef,
        string ElementId,
        string TokenId,
        string SchedulingCause,
        IReadOnlyDictionary<string, string> Correlation,
        BpmnIterationScope? IterationScope = null,
        string? StartElementHint = null) : BpmnHostCommand;

    /// <summary>
    /// Stop a running unit of work and everything it in turn started. Issued when BPMN interrupts: a losing
    /// event-based-gateway branch, an interrupting boundary event tearing its host down, a host completion
    /// retiring its still-armed boundary listeners, an interrupting event subprocess draining its scope, and a
    /// completing scope retiring its listeners.
    /// </summary>
    /// <param name="Handle">The <see cref="BpmnLiveWork.Handle"/> of the work to stop.</param>
    /// <param name="ElementId">The element whose work is being stopped, for the host's own reporting.</param>
    /// <param name="Reason">A stable reason code, for the host's own reporting.</param>
    public sealed record CancelWorkSubtree(string Handle, string ElementId, string Reason) : BpmnHostCommand;

    /// <summary>
    /// Deliver a signal to the scope that invoked this process, which the host surfaces there through
    /// <see cref="BpmnInterpreter.OnWorkSignalled"/>. Carries BPMN escalation: a throw signals outward, and a
    /// scope that cannot match an incoming escalation re-signals it one hop further out.
    /// </summary>
    /// <param name="Code">The signal code; see <see cref="BpmnInterpreter.EscalationSignalCode"/>.</param>
    /// <param name="Payload">The signal payload.</param>
    public sealed record SignalEnclosingScope(string Code, JsonElement? Payload) : BpmnHostCommand;
}

/// <summary>What the interpreter decided this process scope should do next.</summary>
public abstract record BpmnContinuation
{
    private BpmnContinuation()
    {
    }

    /// <summary>The process finished. No token remains and no work is running.</summary>
    /// <param name="Outcome">
    /// <see cref="BpmnInterpreter.DoneOutcomeName"/> normally, or
    /// <see cref="BpmnInterpreter.CancelledOutcomeName"/> when a cancel end event cancelled a transaction.
    /// </param>
    public sealed record Complete(string Outcome) : BpmnContinuation;

    /// <summary>
    /// The process is still running: work was started, a token waits at a join, or a listener is armed. The
    /// host persists the returned state and calls back when something completes.
    /// </summary>
    public sealed record Defer : BpmnContinuation
    {
        /// <summary>The shared instance.</summary>
        public static Defer Instance { get; } = new();
    }

    /// <summary>The process failed deterministically and cannot continue.</summary>
    /// <param name="Code">A stable fault code.</param>
    /// <param name="Message">A human-readable explanation.</param>
    public sealed record Fault(string Code, string Message) : BpmnContinuation;
}

/// <summary>
/// One evaluation of the interpreter: the state to persist, what to do next, and what to ask the host to do.
/// </summary>
/// <param name="State">
/// The new execution state. Persist it verbatim and hand it back as the next request's prior state; call
/// <see cref="BpmnExecutionState.Prune"/> first to keep the persisted payload bounded.
/// </param>
/// <param name="Continuation">What this process scope should do next.</param>
/// <param name="Commands">What to ask the host to do, in order.</param>
public record BpmnEvaluation(
    BpmnExecutionState State,
    BpmnContinuation Continuation,
    IReadOnlyList<BpmnHostCommand> Commands);

/// <summary>An evaluation of a work fault, which additionally reports how the error was handled.</summary>
/// <param name="State">The new execution state.</param>
/// <param name="Continuation">What this process scope should do next.</param>
/// <param name="Commands">What to ask the host to do, in order.</param>
/// <param name="Disposition">Whether a BPMN catcher took the error, or it propagated.</param>
public sealed record BpmnFaultEvaluation(
    BpmnExecutionState State,
    BpmnContinuation Continuation,
    IReadOnlyList<BpmnHostCommand> Commands,
    BpmnErrorDisposition Disposition) : BpmnEvaluation(State, Continuation, Commands);

/// <summary>
/// What became of a faulted unit of work. This is a verdict, not an instruction: the interpreter reports what
/// BPMN did with the error and the host decides what that means for its own error bookkeeping.
/// </summary>
public abstract record BpmnErrorDisposition
{
    private BpmnErrorDisposition()
    {
    }

    /// <summary>
    /// A BPMN catcher took the error: an error boundary event attached to the failing activity's host, or the
    /// scope's error event subprocess. The process continues along the catcher's path, so the host should treat
    /// the failure as handled.
    /// </summary>
    /// <param name="CatchingElementId">The boundary event or event subprocess that caught it.</param>
    /// <param name="ErrorCode">The error code the catcher declares, or <c>null</c> for a catch-all.</param>
    public sealed record Caught(string CatchingElementId, string? ErrorCode) : BpmnErrorDisposition;

    /// <summary>
    /// No catcher took the error. The continuation is a <see cref="BpmnContinuation.Fault"/> and the failure is
    /// the host's to surface.
    /// </summary>
    public sealed record Propagated : BpmnErrorDisposition
    {
        /// <summary>The shared instance.</summary>
        public static Propagated Instance { get; } = new();
    }
}

/// <summary>Starts a process instance.</summary>
/// <param name="Graph">The built process graph.</param>
/// <param name="PriorState">A previously returned state, or <c>null</c> to start fresh.</param>
/// <param name="Host">The host snapshot for this evaluation.</param>
/// <param name="StartSignal">
/// The start event an external trigger matched, or <c>null</c> for direct invocation (every none start event
/// seeds a token) or for an event-subprocess body (whose start element arrives on the invocation correlation).
/// </param>
public sealed record BpmnStartRequest(
    BpmnGraph Graph,
    BpmnExecutionState? PriorState,
    BpmnHostSnapshot Host,
    BpmnStartSignal? StartSignal = null);

/// <summary>
/// Reports that a started unit of work completed.
/// <para>
/// The completing work must ALREADY be removed from <see cref="BpmnHostSnapshot.LiveWork"/>. Completion
/// is terminal; leaving it present lets a re-armed non-interrupting listener key onto the same
/// (binding ref, iteration id) slot, and a teardown can then target the work that just finished.
/// </para>
/// </summary>
/// <param name="Graph">The built process graph.</param>
/// <param name="PriorState">The state returned by the previous evaluation.</param>
/// <param name="Host">The host snapshot for this evaluation.</param>
/// <param name="CompletedBindingRef">The binding whose work completed.</param>
/// <param name="CompletedHandle">The completed work's opaque host handle.</param>
/// <param name="OutcomeNames">The outcome names the work reported; conditional sequence flows match on these.</param>
/// <param name="CompletedIterationId">The iteration id the work ran under, for a multi-instance instance.</param>
public sealed record BpmnWorkCompletedRequest(
    BpmnGraph Graph,
    BpmnExecutionState? PriorState,
    BpmnHostSnapshot Host,
    string CompletedBindingRef,
    string CompletedHandle,
    IReadOnlyCollection<string> OutcomeNames,
    string? CompletedIterationId = null);

/// <summary>
/// Reports that a started unit of work failed terminally.
/// <para>
/// As with a completion, the failed work must ALREADY be removed from
/// <see cref="BpmnHostSnapshot.LiveWork"/>.
/// </para>
/// </summary>
/// <param name="Graph">The built process graph.</param>
/// <param name="PriorState">The state returned by the previous evaluation.</param>
/// <param name="Host">The host snapshot for this evaluation.</param>
/// <param name="FaultedBindingRef">The binding whose work failed.</param>
/// <param name="FaultedHandle">The failed work's opaque host handle.</param>
/// <param name="FaultMessage">A human-readable description of the failure, used in the propagated fault message.</param>
public sealed record BpmnWorkFaultedRequest(
    BpmnGraph Graph,
    BpmnExecutionState? PriorState,
    BpmnHostSnapshot Host,
    string FaultedBindingRef,
    string FaultedHandle,
    string? FaultMessage = null);

/// <summary>
/// Reports a signal raised by a nested process this scope invoked — the receiving half of
/// <see cref="BpmnHostCommand.SignalEnclosingScope"/>.
/// <para>
/// Unlike a completion or a fault, the signalling work must STILL BE PRESENT in
/// <see cref="BpmnHostSnapshot.LiveWork"/>. A signal is not terminal: an escalating activity keeps
/// running, and removing it makes the interpreter believe it has already gone.
/// </para>
/// <para>
/// A nested scope can terminalize synchronously while the parent is still applying commands. Queue the
/// parent callback and drain it after the current command list is fully applied; do not recurse.
/// </para>
/// </summary>
/// <param name="Graph">The built process graph.</param>
/// <param name="PriorState">The state returned by the previous evaluation.</param>
/// <param name="Host">The host snapshot for this evaluation.</param>
/// <param name="SignallingBindingRef">The binding whose work raised the signal.</param>
/// <param name="SignallingHandle">The signalling work's opaque host handle.</param>
/// <param name="Code">The signal code.</param>
/// <param name="Payload">The signal payload.</param>
/// <param name="SignallingIterationId">The iteration id the signalling work runs under, if any.</param>
public sealed record BpmnWorkSignalledRequest(
    BpmnGraph Graph,
    BpmnExecutionState? PriorState,
    BpmnHostSnapshot Host,
    string SignallingBindingRef,
    string SignallingHandle,
    string Code,
    JsonElement? Payload = null,
    string? SignallingIterationId = null);
