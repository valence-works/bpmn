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
| `OnWorkCompleted` | `BpmnWorkCompletedRequest(Graph, PriorState, Host, CompletedBindingRef, CompletedHandle, OutcomeNames, CompletedIterationId?)` | `BpmnEvaluation` |
| `OnWorkFaulted` | `BpmnWorkFaultedRequest(Graph, PriorState, Host, FaultedBindingRef, FaultedHandle, FaultMessage?) { FaultedIterationId }` | `BpmnEvaluation` |
| `OnWorkSignalled` | `BpmnWorkSignalledRequest(Graph, PriorState, Host, SignallingBindingRef, SignallingHandle, Code, Payload?, SignallingIterationId?)` | `BpmnEvaluation` |

Work is identified by two values together: the **binding ref**, which the interpreter assigned and
knows, and the **handle**, which you assigned and it merely echoes. Every callback carries both.

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
starts. A body that cannot run would then be refused mid-run, after earlier work has already happened.
`Build` prevents that for the bodies it can see: it refuses an error end event, which nothing executes
yet, in any body your bound work carries in `NestedProcess`.

A body carries no bound work of its own, so the bodies nested inside it, at any depth, are visible only
to you. Build the graph of every body the root can reach before you run the root, and discard or cache
the result: the refusal then reaches the caller up front, wherever the body sits. The reference host,
`InMemoryBpmnHost`, does this whenever it starts a root.

#### Recurring timer starts

Graph construction refuses a timer start unless it declares exactly one nonblank `interval` or
`cron` property. Hosts registering recurring triggers use the same resolver:

```csharp
var schedule = BpmnTimerStartSchedule.Resolve(timerStartElement);
// schedule.Kind is BpmnTimerStartScheduleKind.Interval or Cron.
// schedule.Expression is normalized for the host's scheduler.
```

An interval is trimmed; cron whitespace between fields is collapsed to one space. Blank values
count as absent. Resolution validates the schedule choice, not ISO-8601 or cron syntax: validate
the expression against your scheduler before registering it. The helper performs no scheduling
and its result is not a new wire format. Event-subprocess timers retain their narrower rule:
one nonblank interval and no nonblank cron, representing a one-shot listener re-armed on each
non-interrupting fire. They are not recurring process-start registrations.

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
public sealed record BpmnLiveWork(string BindingRef, string? IterationId, string Handle);
```

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

The read outcomes are distinct, and the difference matters:

- return `false` — you have no such variable at all; collection-mode evaluation faults with
  `bpmn.loop.collection-unreadable`.
- return `true` with `BpmnValue.Absent` — the read was answered, but there is no value; the collection
  resolves as empty.
- return `true` with `BpmnValue.Null` — you have it, and it is null; the collection also resolves as empty.
- return `true` with `BpmnValuePresence.StoredExternally` — you have it, but not inline, so the
  interpreter cannot read it. Treated as unreadable rather than absent, so a multi-instance
  collection stored that way fails with a clear diagnostic instead of silently iterating zero times.
- return `true` with `BpmnValue.Refused(code, message)` — you withhold it. A collection-mode loop
  faults with your exact code and message, without starting an iteration. The reference reader exposes
  this through `new InMemoryVariables().SetRefused("reviewers", "host.variable.denied", "Collection is withheld.")`.

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

Call `Prune()` before writing it. That drops the records which can never influence a future decision
— consumed tokens no longer referenced by active work, and diagnostics beyond the 200-entry cap — so
a long-running process does not re-serialize an ever-growing blob. Pruning does not bump `Sequence`
and does not change the schema, so record ids stay a pure function of mutation order. Canceled tokens
are never pruned.

The only rule is that the state you pass in must be the state you last got out, for that scope
instance.

## What you get back

### The continuation

| Continuation | Meaning |
| --- | --- |
| `BpmnContinuation.Complete(Outcome)` | The scope finished: no token remains and no work is running. `Outcome` is `"Done"` normally, or `"Cancelled"` when a cancel end event cancelled a transaction. |
| `BpmnContinuation.Defer` | Still running: work was started, a token waits at a join, or a listener is armed. Persist the state and call back when something completes. |
| `BpmnContinuation.Fault(Code, Message)` | The scope failed deterministically and cannot continue. |

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

`Correlation` is opaque interpreter state with exactly one use: when the work you are starting is a
**nested BPMN process**, supply it as that process's `BpmnHostSnapshot.InvocationCorrelation`. That is
how the interpreter recognizes the nested scope on the way back in.

There is deliberately nowhere to hand it back on a completion, fault, or signal callback, so do not
try. Which is worth stating plainly, because it implies a rule that is otherwise invisible:

> **At most one live unit of work per `(BindingRef, IterationId)` within a scope.**

The interpreter re-finds the parked token from that pair alone. Two concurrent units of work sharing
one binding ref and iteration id are indistinguishable to it, and completing either will resume the
other's token. Multi-instance is exactly why `IterationId` exists: instances share a binding ref and
are told apart by it.

Read that as holding **once you have applied a command batch in full, in order** — not at every point
within one. An interrupting path may emit the replacement `StartWork` ahead of the
`CancelWorkSubtree` for the unit it supersedes, so a host that stopped halfway through the batch
would see the slot doubly occupied. That is why two other checklist items are load-bearing here:
apply commands in the order returned, and key your ledger by handle rather than by slot, so the
teardown still names the older unit unambiguously.

One corollary, because it is an easy and damaging mistake: **do not put a completing unit of work's
correlation into `InvocationCorrelation` to "help".** That dictionary is the scope's, fixed for the
scope's lifetime, and the event-subprocess start-element hint is read from it. Overwriting it
corrupts event subprocess activation in a way no test of yours will obviously catch.

`IterationScope` carries the per-instance values to seed: always the zero-based `loopIndex`, plus the
current item in collection mode. Report the completion, or the fault, back under the same `IterationId`.

Timers, human tasks, HTTP calls, message subscriptions, nested processes and long-lived scope
listeners are all `StartWork`. The interpreter does not distinguish them, because the difference is
entirely about how you run them.

### The error disposition

`BpmnEvaluation.ErrorDisposition` is nullable. `OnWorkFaulted` sets it when the reported failure
is routed or absorbed, and call-activity completions do the same for the failure outcomes `Faulted`,
`DispatchFailed`, or `Cancelled`:

```csharp
var evaluation = interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
    graph, state, snapshot, work.BindingRef, work.Handle, "Credit limit exceeded")
    { FaultedIterationId = work.IterationId });

var summary = evaluation.ErrorDisposition switch
{
    BpmnErrorDisposition.Caught caught => $"caught at {caught.CatchingElementId} ({caught.ErrorCode ?? "any"})",
    BpmnErrorDisposition.Propagated    => "the error was not handled; the continuation is a Fault",
    BpmnErrorDisposition.Absorbed      => "the work or scope had already stopped",
    null                               => "no failure was routed or absorbed",
    _                                  => "unknown"
};
```

Note what the request does **not** carry: an error code. The host reports that work failed and why in
human terms, and today every error catcher takes any failure. In 0.4.0,
[ADR 0012](../adr/0012-hosts-and-error-end-events-name-bpmn-errors.md) lets the host name the error, and
makes a coded catcher catch only that code. A host that crosses scopes will then pass a child's
`Fault.ErrorCode` up with the parent's fault report.

`Caught` means an error boundary event or an error event subprocess took the failure and this
evaluation returned without fault. Recovery work may still be running. `Propagated` means the final
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
            _graph, _state, Snapshot(), work.BindingRef, handle, outcomeNames, work.IterationId)), work);
    }

    public void FaultWork(string handle, string message)
    {
        var work = Take(handle);

        var evaluation = _interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            _graph, _state, Snapshot(), work.BindingRef, handle, message) { FaultedIterationId = work.IterationId });

        Apply(evaluation, work, message);
    }

    /// A nested scope signalled outward; this scope is where it lands.
    public void SignalFromChild(string handle, string code, JsonElement? payload)
    {
        var work = _live[handle];   // still running: a signal is not a completion

        Apply(_interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            _graph, _state, Snapshot(), work.BindingRef, handle, code, payload, work.IterationId)));
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
                    _live[handle] = new BpmnLiveWork(start.BindingRef, start.IterationScope?.IterationId, handle);
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
                OnScopeFaulted(fault.Code, fault.Message);
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
    private void OnScopeFaulted(string code, string message) { /* your failure model, if any */ }
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
- [ ] Report a callback with the same binding ref and handle the work was started under, and with
      its iteration id for a multi-instance instance, faults included.
- [ ] Never reuse a handle within a scope. A late report for torn-down work is recognized by its
      handle alone.
- [ ] Keep at most one live unit of work per `(BindingRef, IterationId)` in a scope, once each
      command batch has been applied in full. Key your ledger by handle, not by slot.
- [ ] Pass `StartWork.Correlation` into a nested process as its `InvocationCorrelation`, and never
      write anything else into that dictionary.
- [ ] Remove finished work from `LiveWork` **before** calling `OnWorkCompleted` or `OnWorkFaulted`,
      and leave signalling work **in** it when calling `OnWorkSignalled`. See below — this one is
      load-bearing.
- [ ] Queue a parent callback rather than recursing when a nested scope finishes synchronously.
- [ ] Pass outcome names on completion when a conditional sequence flow depends on them.
- [ ] Make `CancelWorkSubtree` recursive. It is not "cancel this one thing".
- [ ] Route `SignalEnclosingScope` to the immediate parent scope, not to the root.
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
| `OnWorkSignalled` | must **still be present** |

Completion and fault are terminal, so leaving the work in place lets a re-armed non-interrupting
listener key onto the same `(binding ref, iteration id)` slot — and a teardown can then target the work
that just finished. A signal is not terminal: an escalating activity keeps running, and removing it
makes the interpreter believe it has already gone.

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
