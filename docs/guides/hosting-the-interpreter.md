# Hosting the interpreter

The interpreter decides what a BPMN process means. A host makes things happen. This page is the
contract between them: what you must implement, what you get back, and why the port has the shape it
does.

If you only want to run a process and do not want to build any of this, use
`Bpmn.Runtime.InMemory` — see [Simulating a process](simulating-a-process.md).

## The shape of an evaluation

Every entry point is synchronous, takes a request record, and returns a value:

```text
(graph, prior state, host snapshot, event) -> (next state, continuation, commands, error disposition?)
```

Nothing is awaited, and nothing is called back except to read: a variable, or the answer to a
condition expression. The interpreter computes a decision and hands it to you; you apply it in
whatever idiom and on whatever thread your system prefers.

```csharp
var interpreter = BpmnInterpreter.CreateDefault();

var evaluation = interpreter.Start(new BpmnStartRequest(graph, priorState: null, snapshot));
// evaluation.State        — the next execution state, to persist
// evaluation.Continuation — what the scope itself should do now
// evaluation.Commands     — what you should do now, in order
```

| Entry point | Request | Returns |
| --- | --- | --- |
| `Start` | `BpmnStartRequest(Graph, PriorState, Host, StartSignal?)` | `BpmnEvaluation` |
| `OnWorkCompleted` | `BpmnWorkCompletedRequest(..., CompletedIterationId?) { WorkTokenId, ErrorCode }` | `BpmnEvaluation` |
| `OnWorkFaulted` | `BpmnWorkFaultedRequest(..., FaultMessage?) { FaultedIterationId, WorkTokenId, ErrorCode }` | `BpmnEvaluation` |
| `OnWorkSignalled` | `BpmnWorkSignalledRequest(..., SignallingIterationId?) { WorkTokenId }` | `BpmnEvaluation` |

The **binding ref** names the kind of work, and the **handle** names the host's physical work item.
The scope-local **work token ID** names the particular invocation. It is the `TokenId` from the
`StartWork` command that began that work. Save it beside the handle and echo it as `WorkTokenId` on
the `BpmnLiveWork` snapshot entry and callback request. It does not replace the handle or the
multi-instance iteration ID.

`BpmnInterpreter.CreateDefault()` builds an interpreter carrying the built-in behavior for every
element family. No container, no registration, no configuration.

## What you must supply

### 1. A graph

A `BpmnGraph` is a validated, executable form of a process definition. Build it once and reuse it: it
is derived only from the definition, the bound work and your capabilities, none of which change per
instance.

```csharp
var graph = BpmnGraph.Build(definition, boundWork, BpmnHostCapabilities.Full);
```

`boundWork` is an `IReadOnlyCollection<BpmnBoundWork>` — one entry per `BindingRef` and
`ListenerBindingRef` the definition declares. Every declared binding must be present, and every entry
must be bound by exactly one element.

```csharp
public sealed record BpmnBoundWork(string BindingRef, BpmnProcessDefinition? NestedProcess = null);
```

`NestedProcess` is required for an event subprocess body, whose start-event trigger the graph
validator reads. It is null for every other kind of work, including subprocesses whose body you
resolve yourself.

If you got here from a `.bpmn` file, map the reader's bindings across:

```csharp
var boundWork = result.Bindings
    .Select(b => new BpmnBoundWork(
        b.BindingRef,
        b is BpmnWorkBinding.NestedProcess nested ? nested.Definition : null))
    .ToArray();
```

`Build` throws `BpmnExecutionException` for a structurally invalid definition, and
`BpmnCapabilityException` when the definition needs a capability you did not declare — see
[Host capabilities](../concepts/capabilities.md).

#### Nested bodies

Each subprocess body runs as its own scope with its own graph, which you build when the subprocess
starts. A body that cannot run is then refused mid-run, after earlier work has already happened. A
body carries no bound work of its own, so the bodies nested inside it, at any depth, are visible only
to you. To refuse a broken body up front, build the graph of every body the root can reach before you
run the root, and discard or cache the result. The reference host, `InMemoryBpmnHost`, does this
whenever it starts a root.

#### Recurring timer starts

Graph construction refuses a timer start unless it declares exactly one nonblank `interval`,
`cron` or `date` property. Hosts registering timer triggers use the same resolver:

```csharp
var schedule = BpmnTimerStartSchedule.Resolve(timerStartElement);
// schedule.Kind is BpmnTimerStartScheduleKind.Interval, Cron or Date.
// schedule.Expression is normalized for the host's scheduler.
// schedule.Repetitions and schedule.Start come from a <timeCycle>, when the start declares one.
```

An interval or date is trimmed; cron whitespace between fields is collapsed to one space. Blank
values count as absent. A `Date` start runs once, at that ISO-8601 instant; an `Interval` or `Cron`
start recurs. A start read from a `<timeCycle>` keeps the cycle as written in its `cycle` property;
`Repetitions` is the count of an `R<n>/` cycle, after which the host stops the trigger, and `Start` the
instant of its first occurrence, both `null` when the cycle does not say. The parts beside a `cycle`
must agree with it, and a definition built in code may leave them out. Resolution validates the
schedule choice, not ISO-8601 or cron syntax: validate the expression against your scheduler before
registering it. The helper performs no scheduling and its result is not a new wire format.

Event-subprocess timers take a cycle, or else one nonblank interval or date and no nonblank cron.
A duration or uncounted cycle listener is re-armed on each non-interrupting fire, without end. A
counted cycle (`R<n>/`, `BpmnEventSubprocessCatcher.Repetitions`) is re-armed until it has fired n
times: the re-armed listener token carries the fires so far as `BpmnToken.FireCount`, so a host that
persists the state between fires keeps the count with it, and after the nth fire no listener work is
started. Each arm is still one wait for one occurrence. A date listener (`BpmnEventSubprocessCatcher.FiresOnce`) is
not re-armed, because its instant has passed. They are not recurring process-start registrations. A
timer catch or boundary on a cycle binds a `timerCycleWait`, which ends at the cycle's first
occurrence.

### 2. A snapshot

`BpmnHostSnapshot` is what the interpreter is allowed to know about your world at the moment of the
call. It is a value, constructed fresh per evaluation, never held.

| Field | What it is |
| --- | --- |
| `ScopeInstanceId` | An opaque, host-chosen identity for this running scope. Echoed onto tokens for provenance; never parsed. |
| `HasEnclosingScope` | Whether a parent scope exists to receive a signal. A root process has none, so an unhandled escalation is a documented no-op rather than a fault. |
| `LiveWork` | The `BpmnLiveWork` entries this scope started that are still running. |
| `InvocationCorrelation` | The `Correlation` dictionary the host carried into this invocation, echoed back verbatim. Empty for a process started directly. |
| `Variables` | An `IBpmnVariableReader`. |
| `Capabilities` | What this host can do. Must match what the graph was built with. |
| `Conditions` | An `IBpmnConditionEvaluator`, or `null`. An init-only property, set with `with { Conditions = ... }`. Required when you declare `ConditionEvaluation`. |

```csharp
public sealed record BpmnLiveWork(string BindingRef, string? IterationId, string Handle)
{
    public string? WorkTokenId { get; init; }
}
```

Keep each `StartWork.TokenId` with the handle that `StartWork` returns. An explicit ID selects that
one invocation and must agree with the reported binding and any iteration ID. Unknown, blank, or
contradictory IDs fail deterministically; the interpreter never falls back to a sibling. Existing
positional constructors and deconstruction remain available, so callers can migrate by setting the
new init-only property.

Legacy completion and fault reports without `WorkTokenId` use binding and iteration lookup. A
reported iteration must match exactly; when it is omitted, the binding must have exactly one
outstanding invocation. Recognized legacy escalation signals use a stricter exact-slot lookup:
`SignallingIterationId` must match exactly, and null matches only a null-iteration slot. It does not
fall back to a sole multi-instance invocation when the iteration is missing. An ambiguous exact
slot is refused. When the exact slot resolves an active invocation, its `LiveWork` entry must be
present and `SignallingHandle` must match that entry, including for an ID-less invocation record.
The established late-signal behavior applies only when no active invocation matches that exact slot.
Hosts that can start concurrent ordinary work on one binding should store and echo the token ID.

An ambiguous ID-less slot does not block an unrelated completion, fault or signal. It is refused
when that slot must be resolved, including to select a handle for an interruption. This does not
make ambiguous work safely completable: hosts must echo the token ID to address those invocations.
Duplicate handles, contradictory explicit identities and invalid iteration evidence still refuse.
A whole-scope terminate is different: it cancels each distinct reported live handle without choosing
between tokens. Finished work must leave `LiveWork` before a terminal callback; retaining the exact
completing handle is now rejected explicitly, even when the other invocation fields would resolve.

For `OnWorkSignalled`, an explicit ID is strict for the recognized escalation code: the invocation
must still be active and its matching handle must remain in `LiveWork`. An ID-less signal uses the
exact binding and `SignallingIterationId` slot, with null matching only null. If the slot resolves an
active invocation, its `LiveWork` entry must be present and `SignallingHandle` must match that
entry's handle, even when the invocation record is ID-less. The established late-signal behavior
applies only when no active invocation matches the exact slot. If the host has already retired the
handle, absorb that signal before calling the interpreter; the reference host does this.
Unrecognized signal codes remain diagnostic pass-throughs and do not use the ID to route. Do not
create completed-token tombstones to make a late explicit signal resolve.

For a root scope with nothing running and no variables there is a shorthand:

```csharp
var snapshot = BpmnHostSnapshot.Root(instanceId);
```

### 3. A variable reader

The one place the interpreter reaches back into the host:

```csharp
public interface IBpmnVariableReader
{
    bool TryRead(string name, out BpmnValue value);
}
```

It is synchronous, read-only and side-effect free. It exists because which variable is needed depends
on which element the propagation loop reaches, which is not knowable before the loop runs, so the
value cannot be hoisted into the snapshot.

`BpmnNoVariables.Instance` is the do-nothing implementation for a host with no variables.

Three answers are distinct, and the difference matters:

- return `false` — you have no such variable at all.
- return `true` with `BpmnValue.Null` — you have it, and it is null.
- return `true` with `BpmnValuePresence.StoredExternally` — you have it, but not inline, so the
  interpreter cannot read it. Treated as unreadable rather than absent, so a multi-instance
  collection stored that way fails with a clear diagnostic instead of silently iterating zero times.

If your host would need to await something to answer, pre-resolve it before the evaluation. There is
no async escape hatch here, deliberately.

### 4. A condition evaluator, if you evaluate expressions

Documents written by modeling tools route on `<conditionExpression>` bodies in some expression
language. The library contains no language. If you want to run such documents, you supply the second
callback, of the same shape as the variable reader:

```csharp
public sealed class MyConditions : IBpmnConditionEvaluator
{
    public BpmnConditionResult Evaluate(BpmnConditionRequest request) =>
        request.Language switch
        {
            null or "https://www.omg.org/spec/DMN/20191111/FEEL/" => EvaluateFeel(request.Body), // your own engine
            _ => BpmnConditionResult.Failed("my.condition.unsupported-language", $"Cannot evaluate '{request.Language}'.")
        };
}

var graph = BpmnGraph.Build(definition, boundWork, BpmnHostCapabilities.Full | BpmnHostCapabilities.ConditionEvaluation);
var snapshot = BpmnHostSnapshot.Root(instanceId, BpmnHostCapabilities.Full | BpmnHostCapabilities.ConditionEvaluation)
    with { Conditions = new MyConditions() };
```

Declare `ConditionEvaluation` to `Build` and on every snapshot, and put the evaluator on every
snapshot. Without the capability, `Build` refuses any definition with a flow whose only condition is an
expression, naming the flows; the import reports nothing about them, because the capability is the
check. `Full` does not include `ConditionEvaluation`: it is the one capability that means embedding a
language.

The request carries the element whose flows are being chosen, the flow, the expression body exactly as
modeled, and its language: the flow's own, else the process default from
`definitions/@expressionLanguage`, else `null`. Answer `True`, `False`, or `Failed(faultCode, message)`;
a failure faults the scope with your code, or `bpmn.condition.failed`. Evaluate against the same
process data your variable reader serves, and keep the answer a function of the request and that data:
an evaluation is reproducible only while your answers are.

The interpreter decides the rest: which flows are asked and in what order, that a flow with a
`conditionOutcome` is matched on the outcome instead, that the default flow is never asked, and what
happens when nothing holds. [The host port](../concepts/host-port.md#conditions) has the rules. Every
answer is recorded as a `ConditionEvaluated` diagnostic.

`Bpmn.Runtime.InMemory` ships `InMemoryConditions`, a table of answers by flow id with no language in
it, for tests and samples.

### 5. A place to keep state

`BpmnExecutionState` is the interpreter's memory: tokens, active children, diagnostics, races, loops,
compensation log, and a sequence counter that every generated id derives from. Treat it as opaque. It
serializes to JSON; persist it however you persist anything.

Call `Prune()` before writing it. It removes consumed tokens no longer referenced by active work and
diagnostics beyond the 200-entry cap. Canceled tokens are never pruned; their stopped-work handles
must remain available to recognize a late callback. See
[the host-port pruning rules](../concepts/host-port.md#the-prior-state). Pruning does not bump
`Sequence` or change the schema, so record IDs stay a pure function of mutation order.

The only rule is that the state you pass in must be the state you last got out, for that scope
instance.

## What you get back

### The continuation

| Continuation | Meaning |
| --- | --- |
| `BpmnContinuation.Complete(Outcome)` | The scope finished: no token remains and no work is running. `Outcome` is `"Done"` normally, or `"Cancelled"` when a cancel end event cancelled a transaction. |
| `BpmnContinuation.Defer` | Still running: work was started, a token waits at a join, or a listener is armed. Persist the state and call back when something completes. |
| `BpmnContinuation.Fault(Code, Message) { ErrorCode }` | The scope failed deterministically and cannot continue. `ErrorCode` names the BPMN error behind the fault, when there is one; pass it to the parent scope. |

The outcome names are constants: `BpmnInterpreter.DoneOutcomeName` and
`BpmnInterpreter.CancelledOutcomeName`. `Defer` is the common case, and it is what "the process is
waiting" looks like. The interpreter has no notion of waiting — only of having nothing further to
decide until something else happens.

### The commands

Exactly three, applied **in the order returned**; the ordering carries meaning, and a teardown
emitted before a start is not interchangeable with the reverse.

```csharp
BpmnHostCommand.StartWork(
    string BindingRef,                                // the work to start
    string ElementId,                                 // for your reporting
    string TokenId,                                   // the token parked while it runs
    string SchedulingCause,                           // why, for your reporting
    IReadOnlyDictionary<string, string> Correlation,  // pass to a nested process as its InvocationCorrelation
    BpmnIterationScope? IterationScope = null,        // multi-instance instance frame
    string? StartElementHint = null);                 // event subprocess body start event

BpmnHostCommand.CancelWorkSubtree(string Handle, string ElementId, string Reason);

BpmnHostCommand.SignalEnclosingScope(string Code, JsonElement? Payload);
```

`StartWork.TokenId` and `StartWork.Correlation` have separate jobs. Save `TokenId` beside the host
handle and echo it as `WorkTokenId` on reports. When the work starts a **nested BPMN process**, pass
its `Correlation` as that process's `BpmnHostSnapshot.InvocationCorrelation`. Never put callback
identity into `InvocationCorrelation`; that dictionary belongs to the nested scope's invocation and
is fixed for its lifetime.

Multiple live units may share a `(BindingRef, IterationId)` slot, including ordinary work whose
iteration ID is null. Their distinct work token IDs identify the invocations. Key the host ledger by
its unique handle, retain each start's token ID in that ledger entry, and apply command batches in
the order returned. An interrupting path can emit replacement work before the teardown of the older
unit it supersedes, so the slot can be occupied twice partway through one batch.

If teardown is staged while the scope remains running, a snapshot row may still name work already
marked by `BpmnToken.StoppedHandle`. The interpreter excludes that row from active correlation only when the handle
matches exactly one stopped marker and `WorkTokenId` is absent or matches that marker's token ID;
it rejects an explicit mismatch or an ambiguous marker. After termination, it issues cleanup for each distinct
handle still in the snapshot without correlating teardown targets to active token identities.

`IterationScope` carries the per-instance values to seed: always the zero-based `loopIndex`, plus the
current item in collection mode. Report the completion, or the fault, back under the same `IterationId`.

Timers, human tasks, HTTP calls, message subscriptions, nested processes and long-lived scope
listeners are all `StartWork`. The interpreter does not distinguish them, because the difference is
entirely about how you run them.

### The error disposition

`BpmnEvaluation.ErrorDisposition` is nullable. `OnWorkFaulted` sets it when the reported failure
is routed or absorbed, and call-activity completions do the same for the failure outcomes `Faulted`,
`DispatchFailed`, or `Cancelled`. Any evaluation in which an error end event throws sets it too:

```csharp
var evaluation = interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
    graph, state, snapshot, work.BindingRef, work.Handle, "Credit limit exceeded")
    { FaultedIterationId = work.IterationId, WorkTokenId = work.WorkTokenId, ErrorCode = "CREDIT_DECLINED" });

var summary = evaluation.ErrorDisposition switch
{
    BpmnErrorDisposition.Caught caught => $"caught at {caught.CatchingElementId} ({caught.ErrorCode ?? "any"})",
    BpmnErrorDisposition.Propagated    => "the error was not handled; the continuation is a Fault",
    BpmnErrorDisposition.Absorbed      => "the work or scope had already stopped",
    null                               => "no failure was routed or absorbed",
    _                                  => "unknown"
};
```

`ErrorCode` names the BPMN error the work raised, or is `null` when you cannot name one. The message
stays a human-readable description. Matching is strict
([ADR 0012](../adr/0012-hosts-and-error-end-events-name-bpmn-errors.md)): a catcher that declares a code
catches only that code, and wins over a catch-all, while an uncoded fault reaches only catch-alls. A
fault no catcher takes faults the scope, and the `Fault` continuation keeps the code in its own
`ErrorCode`. For a call activity, report the called process's error in
`BpmnWorkCompletedRequest.ErrorCode` alongside the `Faulted` outcome; the interpreter ignores the code
on any other completion, so `DispatchFailed` and `Cancelled` reach only catch-alls.

An error end event in the model throws too. An error event subprocess of its own scope catches it, and
the evaluation reports `Caught`; otherwise the scope faults with `bpmn.error.thrown`
(`BpmnInterpreter.ErrorThrownFaultCode`), `Fault.ErrorCode` carries the thrown code, and the evaluation
reports `Propagated`.

Crossing scopes is your job, as it is for every fault. When a nested scope ends with a `Fault`, copy its
`ErrorCode` into the parent's report:

```csharp
// The child scope ended with childEvaluation.Continuation is BpmnContinuation.Fault fault.
var parentEvaluation = parentWorkIsCallActivity
    ? interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
        parentGraph, parentState, parentSnapshot, parentWork.BindingRef, parentWork.Handle,
        [BpmnInterpreter.CallActivityFaultedOutcomeName], parentWork.IterationId)
        { WorkTokenId = parentWork.WorkTokenId, ErrorCode = fault.ErrorCode })
    : interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
        parentGraph, parentState, parentSnapshot, parentWork.BindingRef, parentWork.Handle, fault.Message)
        { FaultedIterationId = parentWork.IterationId, WorkTokenId = parentWork.WorkTokenId, ErrorCode = fault.ErrorCode });
```

The reference host, `InMemoryBpmnHost`, forwards the code this way. A host that drops the code degrades
every crossing error to an uncoded one, which only a catch-all catches.

`Caught` means an error boundary event or an error event subprocess took the failure and this
evaluation returned without fault; its `ErrorCode` is the code the catcher declares, null only for a
catch-all. Recovery work may still be running. `Propagated` means the final
continuation is always a `BpmnContinuation.Fault`: no catcher matched, or a catcher's route faulted
during this evaluation. `Absorbed` means
the failure belonged to work or a scope that had already stopped. Ordinary starts, completions and
signals, transaction cancellation, and an event subprocess activated on another path leave
`ErrorDisposition` null. The transaction's `Cancelled` completion is different from a call activity's
`Cancelled` failure outcome. `BpmnTranscriptEntry.Disposition` remains an independent transcript
field.

You need this because **BPMN has no concept of an incident**. Fault records, alerting, retry policy
and operator dashboards are engine-operations concerns, and the specification says nothing about any
of them. The disposition is the hook: record a fault when it is `Propagated`, stay quiet when the
model already handles it. See [Errors and escalation](../concepts/errors-and-escalation.md).

## A host skeleton

Complete enough to run, small enough to read. It keeps everything in memory; a real host replaces the
storage and the work runner, not the shape.

```csharp
using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Semantics;

public sealed class MinimalHost : IBpmnVariableReader
{
    private const BpmnHostCapabilities Capabilities = BpmnHostCapabilities.Full;

    private readonly BpmnInterpreter _interpreter = BpmnInterpreter.CreateDefault();
    private readonly BpmnGraph _graph;
    private readonly IWorkRunner _runner;                 // your own type
    private readonly string _instanceId = Guid.NewGuid().ToString("n");
    private readonly Dictionary<string, BpmnValue> _variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BpmnLiveWork> _live = new(StringComparer.Ordinal);
    private readonly List<FaultRecord> _faults = [];

    private BpmnExecutionState _state = new();

    public MinimalHost(
        BpmnProcessDefinition definition,
        IReadOnlyCollection<BpmnBoundWork> boundWork,
        IWorkRunner runner)
    {
        _runner = runner;

        // Throws now if the definition needs something this host did not declare.
        _graph = BpmnGraph.Build(definition, boundWork, Capabilities);
    }

    /// The parent scope, and the handle it started this scope under. Both null at the root.
    public MinimalHost? Parent { get; init; }
    public string? ParentHandle { get; init; }
    public IReadOnlyDictionary<string, string> InvocationCorrelation { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<FaultRecord> Faults => _faults;

    // --- The interpreter's variable callback --------------------------------------------
    // A host that evaluates condition expressions also puts an IBpmnConditionEvaluator on the
    // snapshot (see section 4) and declares ConditionEvaluation.

    public bool TryRead(string name, out BpmnValue value)
    {
        if (_variables.TryGetValue(name, out var found))
        {
            value = found;
            return true;
        }

        value = BpmnValue.Absent;
        return false;
    }

    // --- Driving the interpreter --------------------------------------------------------

    public void Start(BpmnStartSignal? startSignal = null) =>
        Apply(_interpreter.Start(new BpmnStartRequest(_graph, _state, Snapshot(), startSignal)));

    public void CompleteWork(string handle, params string[] outcomeNames)
    {
        var work = Take(handle);

        Apply(_interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            _graph, _state, Snapshot(), work.BindingRef, handle, outcomeNames, work.IterationId)
            { WorkTokenId = work.WorkTokenId }), work);
    }

    /// errorCode: the BPMN error the work raised, or null when you cannot name one.
    public void FaultWork(string handle, string message, string? errorCode = null)
    {
        var work = Take(handle);

        var evaluation = _interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            _graph, _state, Snapshot(), work.BindingRef, handle, message)
            { FaultedIterationId = work.IterationId, WorkTokenId = work.WorkTokenId, ErrorCode = errorCode });

        Apply(evaluation, work, message);
    }

    /// A nested scope signalled outward; this scope is where it lands.
    public void SignalFromChild(string handle, string code, JsonElement? payload)
    {
        if (!_live.TryGetValue(handle, out var work))
            return;                 // the handle was retired before this signal arrived

        Apply(_interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            _graph, _state, Snapshot(), work.BindingRef, handle, code, payload, work.IterationId)
            { WorkTokenId = work.WorkTokenId }));
    }

    // --- Applying what came back --------------------------------------------------------

    private void Apply(BpmnEvaluation evaluation, BpmnLiveWork? reportedWork = null, string? reportedMessage = null)
    {
        // Persist this before acting on the commands, if you persist at all.
        _state = evaluation.State.Prune();

        // This host records only unhandled reported failures, from fault callbacks or call completions.
        if (reportedWork is not null
            && evaluation.ErrorDisposition is BpmnErrorDisposition.Propagated
            && evaluation.Continuation is BpmnContinuation.Fault failure)
            _faults.Add(new FaultRecord(reportedWork.BindingRef, reportedMessage ?? failure.Message));

        foreach (var command in evaluation.Commands)   // order matters
        {
            switch (command)
            {
                case BpmnHostCommand.StartWork start:
                {
                    // A timer schedules; a user task creates a work item; a service task calls out;
                    // a nested process starts another instance. The interpreter does not care which.
                    var handle = _runner.Start(start);
                    _live[handle] = new BpmnLiveWork(start.BindingRef, start.IterationScope?.IterationId, handle)
                    {
                        WorkTokenId = start.TokenId
                    };
                    break;
                }

                case BpmnHostCommand.CancelWorkSubtree cancel:
                    // Stop this work and everything started underneath it. Eagerly or on commit —
                    // both are legitimate, which is why this is a command and not a callback.
                    _live.Remove(cancel.Handle);
                    _runner.CancelSubtree(cancel.Handle, cancel.Reason);
                    break;

                case BpmnHostCommand.SignalEnclosingScope signal:
                    // Route to the parent scope, under the handle it started us with.
                    Parent?.SignalFromChild(ParentHandle!, signal.Code, signal.Payload);
                    break;
            }
        }

        switch (evaluation.Continuation)
        {
            case BpmnContinuation.Complete complete:
                OnScopeCompleted(complete.Outcome);      // "Done", or "Cancelled"
                break;

            case BpmnContinuation.Defer:
                break;                                   // waiting; another event will arrive

            case BpmnContinuation.Fault fault:
                // A nested scope reports this to its parent with fault.ErrorCode: as FaultWork's
                // errorCode for a subprocess, or with the Faulted outcome for a call activity.
                OnScopeFaulted(fault.Code, fault.Message, fault.ErrorCode);
                break;
        }
    }

    private BpmnHostSnapshot Snapshot() => new(
        ScopeInstanceId: _instanceId,
        HasEnclosingScope: Parent is not null,
        LiveWork: [.. _live.Values],
        InvocationCorrelation: InvocationCorrelation,
        Variables: this,
        Capabilities: Capabilities);

    private BpmnLiveWork Take(string handle)
    {
        if (!_live.Remove(handle, out var work))
            throw new InvalidOperationException($"No live work with handle '{handle}'.");

        return work;
    }

    private void OnScopeCompleted(string outcome) { /* report upward, archive, whatever */ }
    private void OnScopeFaulted(string code, string message, string? errorCode) { /* your failure model, if any */ }
}

public sealed record FaultRecord(string BindingRef, string Message);

// Host-owned, not part of the library.
public interface IWorkRunner
{
    /// Starts the work and returns an opaque handle the interpreter will echo back.
    string Start(BpmnHostCommand.StartWork start);

    void CancelSubtree(string handle, string reason);
}
```

## Why it is shaped like this

### Why commands instead of a host interface

The interpreter could hold a reference to your host and call it. It does not, for four reasons.

**Hosts disagree about timing, and commands let them.** One host cancels a subtree eagerly, walking
its context tree in place. Another stages the cancellation and flushes it only when the surrounding
evaluation commits without faulting. Both are correct. A callback interface bakes one host's timing
into the library and makes the other's adoption an uphill fight. Returned commands are inert data.

**A correctness invariant becomes structural.** Teardown may only be staged on a non-fault
continuation; firing it earlier destroys work the fault path still needs. If behaviors held a live
host reference, nothing would stop them firing immediately, and the invariant would survive on
discipline alone. With commands, nothing *can* fire before the interpreter returns.

**The interpreter regains a decision it had given away.** "Did I schedule anything during this
evaluation?" becomes an inspection of the list just produced, rather than a question asked of the
host.

**Failures become reproducible.** An evaluation is fully determined by four values — definition,
state, snapshot, event — all of which serialize to JSON. A bug report is a paste of those four
values, and a regression test can be written from it with no running host.

The same argument is why there is no fault-resolution command. How an error was handled is a verdict
the interpreter returns, not an instruction it issues — see
[ADR 0003](../adr/0003-faults-are-a-verdict-not-a-command.md).

### Why it is synchronous

Because the semantics contain no asynchrony. There is nothing in "which flow does this token take"
to await. Making the port asynchronous would propagate `async` through a tight token-propagation loop
and a command-application switch to model something that never happens, costing readability,
allocations and stack-trace quality.

Asynchronous work is expressed, not eliminated. "Wait for a timer" is a `StartWork` command plus a
`Defer` continuation. Your host suspends however it likes; resumption arrives as `OnWorkCompleted`.
The library's entire relationship with time is that you have some.

You may await as freely as you want — you apply the commands *after* the interpreter returns. The
command shape constrains when decisions are made, not when work happens.

## Checklist

- [ ] Build the graph once per definition, not per instance.
- [ ] Declare capabilities honestly. `Full` is a claim, not a default.
- [ ] Declare `ConditionEvaluation` only if you put an `IBpmnConditionEvaluator` on every snapshot, and
      keep its answers a function of the request and your process data.
- [ ] Persist `evaluation.State.Prune()` before acting on commands, if you persist.
- [ ] Apply commands in the order returned.
- [ ] Report a callback with the same binding ref and handle the work was started under, echo its
      `StartWork.TokenId` as `WorkTokenId`, and include its iteration ID for multi-instance work.
- [ ] Never reuse a handle within a scope. A late terminal report for torn-down work is recognized
      by that handle; if it includes `WorkTokenId`, the ID must match the retained stopped token.
- [ ] Keep each invocation in a handle-keyed ledger with its `WorkTokenId`. Multiple live invocations
      may share a `(BindingRef, IterationId)` slot.
- [ ] Pass `StartWork.Correlation` into a nested process as its `InvocationCorrelation`, and never
      write anything else into that dictionary.
- [ ] Remove finished work from `LiveWork` **before** calling `OnWorkCompleted` or `OnWorkFaulted`.
      Keep an explicitly identified escalation signal's work **in** `LiveWork`; absorb signals for
      handles already retired before calling the interpreter. See below.
- [ ] Queue a parent callback rather than recursing when a nested scope finishes synchronously.
- [ ] Pass outcome names on completion when a conditional sequence flow depends on them.
- [ ] Make `CancelWorkSubtree` recursive. It is not "cancel this one thing".
- [ ] Route `SignalEnclosingScope` to the immediate parent scope, not to the root.
- [ ] Name the BPMN error a failed unit of work raised in `ErrorCode`, when you can; a coded catcher
      catches nothing else.
- [ ] When a nested scope faults, copy its `Fault.ErrorCode` into the parent's fault report, or into
      the parent's `Faulted` completion for a call activity.
- [ ] Decide your failure policy from `BpmnEvaluation.ErrorDisposition`, not from every fault.

## Two traps that are easy to hit and hard to diagnose

Both of these were found by writing a second host against this port. Neither is obvious from the API,
so they are spelled out here rather than left to be rediscovered.

### `LiveWork` is pruned differently depending on which callback you are making

`BpmnHostSnapshot.LiveWork` is the interpreter's view of what is still running, and what belongs in it
depends on the callback:

| Callback | The work being reported |
| --- | --- |
| `OnWorkCompleted` | must **already be removed** |
| `OnWorkFaulted` | must **already be removed** |
| `OnWorkSignalled` | A recognized escalation with explicit `WorkTokenId` must still be present. An ID-less signal resolving a live exact slot must match its `LiveWork` handle; the late-signal path applies only when no active invocation matches. Unknown codes are pass-throughs. |

Completion and fault are terminal, so leaving the work in place lets a re-armed non-interrupting
listener's invocation share a slot with the unit that just finished, and a teardown could target the
wrong unit. Remove the reported unit but retain any replacement unit in `LiveWork`; `WorkTokenId`
distinguishes them. A recognized escalation signal is not terminal: an escalating activity keeps
running, and explicit identity must name that live invocation. An ID-less signal for an active exact
slot must match the handle in its `LiveWork` entry; the compatibility path can still describe a late
signal only when no active invocation matches that slot. The reference host absorbs a signal for a
handle it already retired before calling the interpreter, and unknown signal codes remain diagnostic
pass-throughs.

### A nested scope can finish while you are still applying commands

A nested process whose body runs start-to-end with no waiting will terminalize **during** the parent's
command loop. Call the parent's `OnWorkCompleted` directly from inside command application and you
re-enter the interpreter with a half-applied `LiveWork` set and an evaluation already in flight.

Queue the parent callback and drain the queue after the current command list is fully applied. The
reference host uses a single FIFO for exactly this.

## Related

- [The host port](../concepts/host-port.md) — the contract in detail.
- [Host capabilities](../concepts/capabilities.md) — why an under-capable host is refused up front.
- [Errors and escalation](../concepts/errors-and-escalation.md) — what BPMN does with a failure
  before it reaches you.
- [ADR 0002: The host port is synchronous and command-returning](../adr/0002-the-host-port-is-synchronous-and-command-returning.md)
- [ADR 0003: Faults are a verdict, not a command](../adr/0003-faults-are-a-verdict-not-a-command.md)
- [ADR 0004: Missing host capabilities refuse at graph-build time](../adr/0004-capabilities-refuse-at-graph-build-time.md)
