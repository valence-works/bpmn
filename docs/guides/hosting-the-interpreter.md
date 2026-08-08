# Hosting the interpreter

The interpreter decides what a BPMN process means. A host makes things happen. This page is the
contract between them: what you must implement, what you get back, and why the port has the shape it
does.

If you only want to run a process and do not want to build any of this, use
`Bpmn.Runtime.InMemory` — see [Simulating a process](simulating-a-process.md).

## The shape of an evaluation

Every entry point is synchronous, takes a request record, and returns a value:

```text
(graph, prior state, host snapshot, event) -> (next state, continuation, commands)
```

Nothing is called back. Nothing is awaited. The interpreter computes a decision and hands it to you;
you apply it in whatever idiom and on whatever thread your system prefers.

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
| `OnWorkFaulted` | `BpmnWorkFaultedRequest(Graph, PriorState, Host, FaultedBindingRef, FaultedHandle, FaultMessage?)` | `BpmnFaultEvaluation` |
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

Three answers are distinct, and the difference matters:

- return `false` — you have no such variable at all.
- return `true` with `BpmnValue.Null` — you have it, and it is null.
- return `true` with `BpmnValuePresence.StoredExternally` — you have it, but not inline, so the
  interpreter cannot read it. Treated as unreadable rather than absent, so a multi-instance
  collection stored that way fails with a clear diagnostic instead of silently iterating zero times.

If your host would need to await something to answer, pre-resolve it before the evaluation. There is
no async escape hatch here, deliberately.

### 4. A place to keep state

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
    IReadOnlyDictionary<string, string> Correlation,  // carry this with the work; hand it back
    BpmnIterationScope? IterationScope = null,        // multi-instance instance frame
    string? StartElementHint = null);                 // event subprocess body start event

BpmnHostCommand.CancelWorkSubtree(string Handle, string ElementId, string Reason);

BpmnHostCommand.SignalEnclosingScope(string Code, JsonElement? Payload);
```

`Correlation` is opaque interpreter state. Carry it with the work, hand it back on the resulting
callback, and — when the work is a nested BPMN process — supply it as that process's
`InvocationCorrelation`.

`IterationScope` carries the per-instance values to seed: always the zero-based `loopIndex`, plus the
current item in collection mode. Report the completion back under the same `IterationId`.

Timers, human tasks, HTTP calls, message subscriptions, nested processes and long-lived scope
listeners are all `StartWork`. The interpreter does not distinguish them, because the difference is
entirely about how you run them.

### The fault disposition

`OnWorkFaulted` returns a `BpmnFaultEvaluation`, which is an evaluation plus a disposition:

```csharp
var evaluation = interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
    graph, state, snapshot, work.BindingRef, work.Handle, "Credit limit exceeded"));

var summary = evaluation.Disposition switch
{
    BpmnErrorDisposition.Caught caught => $"caught at {caught.CatchingElementId} ({caught.ErrorCode ?? "any"})",
    BpmnErrorDisposition.Propagated    => "nothing here caught it; the continuation is a Fault",
    _                                  => "unknown"
};
```

Note what the request does **not** carry: an error code. The host reports that work failed and why in
human terms. Which BPMN error a catcher matches is a property of the model — an error boundary event
declares its code, and a code-less one catches anything — not something the host names.

`Caught` means an error boundary event or an error event subprocess took the failure and the process
continues along that path. `Propagated` means no catcher matched, the continuation is a
`BpmnContinuation.Fault`, and the failure is yours to surface.

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

    // --- The interpreter's one callback -------------------------------------------------

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
            _graph, _state, Snapshot(), work.BindingRef, handle, outcomeNames, work.IterationId)));
    }

    public void FaultWork(string handle, string message)
    {
        var work = Take(handle);

        var evaluation = _interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            _graph, _state, Snapshot(), work.BindingRef, handle, message));

        // BPMN has no incidents. Recording one is this host's decision, and the
        // disposition is what makes it an informed decision.
        if (evaluation.Disposition is BpmnErrorDisposition.Propagated)
            _faults.Add(new FaultRecord(work.BindingRef, message));

        Apply(evaluation);
    }

    /// A nested scope signalled outward; this scope is where it lands.
    public void SignalFromChild(string handle, string code, JsonElement? payload)
    {
        var work = _live[handle];   // still running: a signal is not a completion

        Apply(_interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            _graph, _state, Snapshot(), work.BindingRef, handle, code, payload, work.IterationId)));
    }

    // --- Applying what came back --------------------------------------------------------

    private void Apply(BpmnEvaluation evaluation)
    {
        // Persist this before acting on the commands, if you persist at all.
        _state = evaluation.State.Prune();

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
- [ ] Persist `evaluation.State.Prune()` before acting on commands, if you persist.
- [ ] Apply commands in the order returned.
- [ ] Report a callback with the same binding ref and handle the work was started under.
- [ ] Carry `StartWork.Correlation` with the work, and pass it into a nested process as its
      `InvocationCorrelation`.
- [ ] Pass outcome names on completion when a conditional sequence flow depends on them.
- [ ] Make `CancelWorkSubtree` recursive. It is not "cancel this one thing".
- [ ] Route `SignalEnclosingScope` to the immediate parent scope, not to the root.
- [ ] Decide your failure policy from `BpmnErrorDisposition`, not from every fault.

## Related

- [The host port](../concepts/host-port.md) — the contract in detail.
- [Host capabilities](../concepts/capabilities.md) — why an under-capable host is refused up front.
- [Errors and escalation](../concepts/errors-and-escalation.md) — what BPMN does with a failure
  before it reaches you.
- [ADR 0002: The host port is synchronous and command-returning](../adr/0002-the-host-port-is-synchronous-and-command-returning.md)
- [ADR 0003: Faults are a verdict, not a command](../adr/0003-faults-are-a-verdict-not-a-command.md)
- [ADR 0004: Missing host capabilities refuse at graph-build time](../adr/0004-capabilities-refuse-at-graph-build-time.md)
