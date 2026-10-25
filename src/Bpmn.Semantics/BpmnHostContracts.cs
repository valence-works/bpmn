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
/// Work callbacks can echo the scope-local token id assigned when work started. Without that echo, the
/// interpreter retains the legacy rule that a callback's binding and reported iteration must identify one
/// active unit uniquely. Legacy signals use the exact binding/iteration slot, while terminal callbacks
/// can omit the iteration when the binding identifies a single record.
/// </para>
/// <para>
/// For ID-less legacy callbacks, the uniqueness rule applies <b>once a command batch has been applied in full,
/// in order</b> — not at every point within one. An interrupting path may emit replacement work before the
/// <see cref="BpmnHostCommand.CancelWorkSubtree"/> for the unit it supersedes. Hosts must apply commands in
/// order and keep the ledger keyed by handle, so teardown still names the older unit unambiguously. Hosts that
/// echo token ids can distinguish concurrent work even when binding and iteration are the same.
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
    /// <summary>
    /// Answers the condition expressions the interpreter needs while it routes, or <c>null</c> for a host that
    /// evaluates none. Required when the graph was built with
    /// <see cref="BpmnHostCapabilities.ConditionEvaluation"/>: a host that declared the capability and supplies
    /// no evaluator has broken the port, and the interpreter throws <see cref="BpmnExecutionException"/> naming
    /// the element and flow the first time it needs an answer.
    /// <para>
    /// An init-only property rather than a positional parameter, so adding it changes neither the constructor
    /// nor the deconstruction a host compiled against an earlier version uses. See ADR 0011.
    /// </para>
    /// </summary>
    public IBpmnConditionEvaluator? Conditions { get; init; }

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
/// work. Multi-instance invocations use distinct iterations; concurrent ordinary invocations use distinct
/// <see cref="WorkTokenId"/> values.
/// </param>
/// <param name="Handle">
/// An opaque host handle for this running unit. The interpreter echoes it back in
/// <see cref="BpmnHostCommand.CancelWorkSubtree"/> and never parses it. A handle must name one unit for the
/// scope's lifetime: never reuse one, because a late report for work the scope tore down is recognized by its
/// handle first; when the callback also supplies <see cref="WorkTokenId"/>, that id must agree with the stopped token.
/// </param>
public sealed record BpmnLiveWork(string BindingRef, string? IterationId, string Handle)
{
    /// <summary>
    /// The scope-local token id echoed from the <see cref="BpmnHostCommand.StartWork.TokenId"/> that created
    /// this live unit. When supplied, it must identify exactly one active work record whose binding and
    /// iteration agree with this snapshot entry. <c>null</c> keeps the legacy exact binding/iteration lookup.
    /// </summary>
    public string? WorkTokenId { get; init; }
}

/// <summary>Reads the process's declared variables. The interpreter only ever reads; it never writes.</summary>
public interface IBpmnVariableReader
{
    /// <summary>Reads a declared variable by name. Returns <c>false</c> when the host has no such variable at all.</summary>
    /// <remarks>
    /// Return <c>true</c> with <see cref="BpmnValue.Refused"/> when the host withholds the value;
    /// collection-mode multi-instance evaluation then faults with the host's supplied code and message.
    /// </remarks>
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
/// Evaluates the condition expressions of a process: the second host callback, of the same shape as
/// <see cref="IBpmnVariableReader"/>. The interpreter decides which conditions are asked, when, in what order
/// and what the answers mean for routing; the host decides only what one expression evaluates to. It is asked
/// synchronously, mid-evaluation, and must only read: a host that would have to await something to answer
/// resolves it before the evaluation. There is no asynchronous path.
/// <para>
/// The interpreter's determinism rests on the answers being a function of the request and the host's process
/// data, as it already rests on the variable reader. An evaluator that throws has broken the port; the exception
/// surfaces as a <see cref="BpmnExecutionException"/> whose inner exception is the evaluator's. A condition that
/// cannot be answered is reported as <see cref="BpmnConditionResult.Failed"/> instead, which faults the scope,
/// except where the routing it decided is moot: an escalation throw whose escalation an interrupting event
/// subprocess of its own scope caught.
/// See ADR 0011.
/// </para>
/// </summary>
public interface IBpmnConditionEvaluator
{
    /// <summary>Evaluates one condition expression.</summary>
    BpmnConditionResult Evaluate(BpmnConditionRequest request);
}

/// <summary>One condition expression the interpreter needs answered.</summary>
/// <param name="ElementId">The element whose outgoing flows are being chosen.</param>
/// <param name="FlowId">The flow whose condition this is.</param>
/// <param name="Site">What kind of condition this is; one of <see cref="BpmnConditionSites"/>.</param>
/// <param name="Body">The expression text exactly as modeled. The library never parses it.</param>
/// <param name="Language">
/// The expression's own language, else the process default
/// (<see cref="BpmnProcessDefinition.ExpressionLanguage"/>), or <c>null</c> when the document declares neither.
/// No XPath default is supplied: what a missing language means is the host's call.
/// </param>
/// <param name="Iteration">
/// The <see cref="BpmnIterationScope.IterationId"/> of the multi-instance instance the routed token belongs to,
/// so the host can evaluate against that instance's frame, or <c>null</c> outside one.
/// </param>
public sealed record BpmnConditionRequest(
    string ElementId,
    string FlowId,
    string Site,
    string Body,
    string? Language,
    string? Iteration);

/// <summary>The kinds of condition a <see cref="BpmnConditionRequest"/> can carry. A new site is additive.</summary>
public static class BpmnConditionSites
{
    /// <summary>A sequence flow's <c>&lt;conditionExpression&gt;</c>, asked when the source element routes.</summary>
    public const string SequenceFlow = "sequenceFlow";
}

/// <summary>What a <see cref="BpmnConditionResult"/> says.</summary>
public enum BpmnConditionResultKind
{
    /// <summary>The condition holds.</summary>
    True,

    /// <summary>The condition does not hold.</summary>
    False,

    /// <summary>The condition could not be evaluated. The scope faults.</summary>
    Failed
}

/// <summary>
/// The host's answer to one <see cref="BpmnConditionRequest"/>: <see cref="True"/>, <see cref="False"/>, or
/// <see cref="Failed"/>. A failure faults the scope with <see cref="FaultCode"/>, or with
/// <c>bpmn.condition.failed</c> when the host gives none, except at an escalation throw an interrupting event
/// subprocess of its own scope caught, whose routing is moot. Like every interpreter fault it is a verdict on
/// the scope, not a BPMN error, so no error catcher takes it.
/// </summary>
public sealed record BpmnConditionResult
{
    private BpmnConditionResult(BpmnConditionResultKind kind, string? faultCode, string? message)
    {
        Kind = kind;
        FaultCode = faultCode;
        Message = message;
    }

    /// <summary>The condition holds.</summary>
    public static BpmnConditionResult True { get; } = new(BpmnConditionResultKind.True, null, null);

    /// <summary>The condition does not hold.</summary>
    public static BpmnConditionResult False { get; } = new(BpmnConditionResultKind.False, null, null);

    /// <summary>What the answer is.</summary>
    public BpmnConditionResultKind Kind { get; }

    /// <summary>The fault code a <see cref="BpmnConditionResultKind.Failed"/> answer faults the scope with, or <c>null</c> for the library's own.</summary>
    public string? FaultCode { get; }

    /// <summary>Why a <see cref="BpmnConditionResultKind.Failed"/> answer failed; <c>null</c> otherwise.</summary>
    public string? Message { get; }

    /// <summary>The condition could not be evaluated.</summary>
    /// <param name="faultCode">The code to fault the scope with, or <c>null</c> for <c>bpmn.condition.failed</c>.</param>
    /// <param name="message">Why it failed.</param>
    public static BpmnConditionResult Failed(string? faultCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new BpmnConditionResult(
            BpmnConditionResultKind.Failed,
            string.IsNullOrWhiteSpace(faultCode) ? null : faultCode,
            message);
    }

    /// <summary><see cref="True"/> or <see cref="False"/>.</summary>
    public static BpmnConditionResult Of(bool holds) => holds ? True : False;
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
    /// Opaque interpreter state with exactly one use: when the work is a nested BPMN process, the host supplies
    /// it as that process's <see cref="BpmnHostSnapshot.InvocationCorrelation"/>. It is never handed back on a
    /// callback. A callback can echo this work's token id through its <c>WorkTokenId</c> property; an ID-less
    /// legacy callback is resolved from its binding and reported iteration when those identify one unit.
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
        string? StartElementHint = null) : BpmnHostCommand
    {
        /// <summary>
        /// The opaque producer of ordinary element work: a completed or faulted work handle when routing its
        /// result, or the scope instance identity for scope-driven propagation. Null for scope-owned starts
        /// (multi-instance instances, compensation handlers, boundary/scope listeners and event-subprocess bodies).
        /// Reporting only; it does not participate in callback matching.
        /// </summary>
        public string? ProducedByHandle { get; init; }
    }

    /// <summary>
    /// Stop a running unit of work and everything it in turn started. Issued when BPMN interrupts: a losing
    /// event-based-gateway branch, an interrupting boundary event tearing its host down, a host completion
    /// retiring its still-armed boundary listeners, an interrupting event subprocess draining its scope, a
    /// completing scope retiring its listeners, and a terminate end event ending its scope.
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
    /// <param name="Code">
    /// A stable fault code: the library's reason the scope stopped, such as <c>bpmn.work.faulted</c> or
    /// <see cref="BpmnInterpreter.ErrorThrownFaultCode"/>, or the host's exact code for a refused
    /// variable read. Never a BPMN error code taken from a document.
    /// </param>
    /// <param name="Message">A human-readable explanation.</param>
    public sealed record Fault(string Code, string Message) : BpmnContinuation
    {
        /// <summary>
        /// The BPMN error that caused the fault, or <c>null</c> when the fault is not a BPMN error or the error
        /// has no code. Set when no catcher in the scope took a work fault the host reported with
        /// <see cref="BpmnWorkFaultedRequest.ErrorCode"/>, a call activity's <c>Faulted</c> outcome reported with
        /// <see cref="BpmnWorkCompletedRequest.ErrorCode"/>, or an error an error end event threw. A fault such as
        /// the propagation limit, a gateway that selects no flow, or a cancelled transaction with no cancel
        /// boundary carries none.
        /// <para>
        /// A scope's interpreter cannot see the scope above it, so the host carries the error across: when a
        /// nested scope ends with this fault, copy this code into the parent's fault report for an embedded
        /// subprocess, or into its completion report with the <c>Faulted</c> outcome for a call activity. A host
        /// that drops it degrades the error to an uncoded one, which only a catch-all catches. See ADR 0012.
        /// </para>
        /// <para>
        /// An init-only property rather than a positional parameter, so adding it changes neither the constructor
        /// nor the deconstruction a host compiled against an earlier version uses.
        /// </para>
        /// </summary>
        public string? ErrorCode { get; init; }
    }
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
    IReadOnlyList<BpmnHostCommand> Commands)
{
    /// <summary>
    /// What BPMN did with a host-reported failure, including a call activity's failure outcome, or with an
    /// error an error end event threw, or <c>null</c> when this evaluation did not route or absorb a failure.
    /// Transaction cancellation and event subprocesses activated without a failure do not produce an error
    /// disposition.
    /// </summary>
    public BpmnErrorDisposition? ErrorDisposition { get; init; }
}

/// <summary>
/// What became of a host-reported failure, including a call activity's failure outcome, or of an error an
/// error end event threw. This is a verdict, not an instruction: the interpreter reports how BPMN routed or
/// absorbed it, and the host decides what that means for its own error bookkeeping.
/// </summary>
public abstract record BpmnErrorDisposition
{
    private BpmnErrorDisposition()
    {
    }

    /// <summary>
    /// A BPMN catcher took the error: an error boundary event attached to the failing activity's host, or an
    /// error event subprocess of the scope. A catcher that declares a code catches only an error with that
    /// code, and wins over a catch-all; a catch-all catches any error, including one with no code. The process
    /// continues along the catcher's path, so the host should treat the failure as handled.
    /// </summary>
    /// <param name="CatchingElementId">The boundary event or event subprocess that caught it.</param>
    /// <param name="ErrorCode">
    /// The error code the catcher declares, which is the code of the error it caught, or <c>null</c> when a
    /// catch-all caught it.
    /// </param>
    public sealed record Caught(string CatchingElementId, string? ErrorCode) : BpmnErrorDisposition;

    /// <summary>
    /// No catcher handled the error, or the catcher's path itself faulted. The final continuation is always a
    /// <see cref="BpmnContinuation.Fault"/> and the failure is the host's to surface. An error end event that no
    /// event subprocess of its scope catches reports this too: the error leaves the scope on the fault's
    /// <see cref="BpmnContinuation.Fault.ErrorCode"/>, and how it ends is decided again by the parent's own
    /// evaluation of the fault report the host makes there.
    /// </summary>
    public sealed record Propagated : BpmnErrorDisposition
    {
        /// <summary>The shared instance.</summary>
        public static Propagated Instance { get; } = new();
    }

    /// <summary>
    /// The failure belonged to work or a process scope that had already stopped. It is a late report,
    /// not a new unhandled error; the scope retains its existing continuation.
    /// </summary>
    public sealed record Absorbed : BpmnErrorDisposition
    {
        /// <summary>The shared instance.</summary>
        public static Absorbed Instance { get; } = new();
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
/// is terminal, so reporting the same invocation as live would contradict the callback.
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
    string? CompletedIterationId = null)
{
    /// <summary>
    /// The scope-local token id echoed from the <see cref="BpmnHostCommand.StartWork.TokenId"/> that started
    /// this work. When present, it selects the invocation and must agree with the binding and any reported
    /// iteration. It is init-only to preserve the positional constructor and deconstruction shape.
    /// </summary>
    public string? WorkTokenId { get; init; }

    /// <summary>
    /// The BPMN error a called process raised, meaningful only alongside a call activity's
    /// <see cref="BpmnInterpreter.CallActivityFaultedOutcomeName"/> outcome: it lets that error reach the
    /// caller's error catchers, which a coded catcher otherwise never matches. Typically the
    /// <see cref="BpmnContinuation.Fault.ErrorCode"/> the called process ended with. The interpreter ignores it
    /// on any other completion, including the <c>DispatchFailed</c> and <c>Cancelled</c> outcomes, which are
    /// failures of the call itself and reach only catch-alls. Surrounding whitespace is trimmed, and a blank
    /// code counts as none. See ADR 0012.
    /// <para>
    /// An init-only property rather than a positional parameter, so adding it changes neither the constructor
    /// nor the deconstruction a host compiled against an earlier version uses.
    /// </para>
    /// </summary>
    public string? ErrorCode { get; init; }
}

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
/// <param name="FaultMessage">
/// A human-readable description of the failure, used in the propagated fault message. The BPMN error the work
/// raised, if any, goes in <see cref="BpmnWorkFaultedRequest.ErrorCode"/>, not here.
/// </param>
public sealed record BpmnWorkFaultedRequest(
    BpmnGraph Graph,
    BpmnExecutionState? PriorState,
    BpmnHostSnapshot Host,
    string FaultedBindingRef,
    string FaultedHandle,
    string? FaultMessage = null)
{
    /// <summary>
    /// The iteration id the failed work ran under, for a multi-instance instance; <c>null</c> for ordinary
    /// single-run work. When <see cref="WorkTokenId"/> is absent, the faulted unit is found from this and
    /// <see cref="FaultedBindingRef"/> and that pair must identify one active unit uniquely. An explicit token id
    /// can identify the unit without an iteration id; if both are supplied, they must agree.
    /// <para>
    /// An init-only property rather than a positional parameter, so adding it changes neither the constructor
    /// nor the deconstruction a host compiled against an earlier version uses.
    /// </para>
    /// </summary>
    public string? FaultedIterationId { get; init; }

    /// <summary>
    /// The scope-local token id echoed from the <see cref="BpmnHostCommand.StartWork.TokenId"/> that started
    /// this work. When present, it selects the invocation and must agree with the binding and any reported
    /// iteration. It is init-only to preserve the positional constructor and deconstruction shape.
    /// </summary>
    public string? WorkTokenId { get; init; }

    /// <summary>
    /// The BPMN error the failed work raised, or <c>null</c> when the host cannot name one. Which error was
    /// raised is a fact about the run that only the host knows; which catcher it reaches is a property of the
    /// model. An error boundary on the failing activity, then an error event subprocess of the scope, catches it
    /// when it declares this code, else when it is a catch-all; an uncoded fault reaches only catch-alls. When
    /// nothing catches it, the scope faults and <see cref="BpmnContinuation.Fault.ErrorCode"/> keeps the code.
    /// For an embedded subprocess, this is the <see cref="BpmnContinuation.Fault.ErrorCode"/> the nested scope
    /// ended with. Surrounding whitespace is trimmed, and a blank code counts as none. See ADR 0012.
    /// <para>
    /// An init-only property rather than a positional parameter, so adding it changes neither the constructor
    /// nor the deconstruction a host compiled against an earlier version uses.
    /// </para>
    /// </summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Reports a signal raised by a nested process this scope invoked — the receiving half of
/// <see cref="BpmnHostCommand.SignalEnclosingScope"/>.
/// <para>
/// For a recognized escalation signal with <see cref="BpmnWorkSignalledRequest.WorkTokenId"/>, the signalling
/// work must still be present in <see cref="BpmnHostSnapshot.LiveWork"/> with the same token id and handle. An
/// ID-less legacy signal resolved to an active invocation must also match its live handle. Only when no
/// active exact-slot match remains can it use the legacy late-signal path. An unrecognized signal code
/// remains a forward-compatible diagnostic pass-through and does not resolve invocation identity.
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
    string? SignallingIterationId = null)
{
    /// <summary>
    /// The scope-local token id echoed from the <see cref="BpmnHostCommand.StartWork.TokenId"/> that raised
    /// this signal. When present on a recognized escalation signal, it must identify an active invocation and
    /// match the live-work snapshot entry, including its handle. It is init-only to preserve the positional
    /// constructor and deconstruction shape. A null value retains legacy signal handling.
    /// </summary>
    public string? WorkTokenId { get; init; }
}
