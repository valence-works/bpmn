# Hosting the interpreter

The interpreter decides what a BPMN process means. A host makes things happen. This page is the
contract between them: what you must implement, what you get back, and why the port has the shape it
does.

If you only want to run a process and do not want to build any of this, use
`Bpmn.Runtime.InMemory` — see [Simulating a process](simulating-a-process.md).

## The shape of an evaluation

Every entry point is synchronous and returns a value:

```text
(graph, prior state, host snapshot, event) -> (next state, continuation, commands)
```

Nothing is called back. Nothing is awaited. The interpreter computes a decision and hands it to you;
you apply it in whatever idiom and on whatever thread your system prefers.

```csharp
var interpreter = BpmnInterpreter.CreateDefault();

var evaluation = interpreter.Start(graph, snapshot);
// evaluation.State        — the next token state, to persist
// evaluation.Continuation — what the scope itself should do now
// evaluation.Commands     — what you should do now
```

## What you must supply

Four things. Two of them are objects you implement.

### 1. A graph

A `BpmnGraph` is a validated, executable form of a process definition. Build it once and reuse it —
it is derived only from the definition, the work bindings and your capabilities, none of which change
per instance.

```csharp
var graph = BpmnGraph.Build(definition, bindings, BpmnHostCapabilities.Full);
```

`bindings` is the `IReadOnlyList<BpmnWorkBinding>` the reader produced, or an equivalent list you
assembled yourself. `capabilities` is what your host can actually do — and if the definition needs
something you did not declare, the build is refused here, at start-up, rather than degrading in
production. That is the subject of [Host capabilities](../concepts/capabilities.md).

### 2. A snapshot

`BpmnHostSnapshot` is what the interpreter is allowed to know about your world at the moment of the
call. It is a value, constructed fresh per evaluation.

| Field | What it is |
| --- | --- |
| `ScopeInstanceId` | The identity of the scope instance being evaluated. |
| `HasEnclosingScope` | Whether this scope has a parent that can be signalled. |
| `LiveWork` | The work this scope currently has outstanding. |
| `InvocationCorrelation` | How the enclosing scope knows this invocation, when there is one. |
| `Variables` | An `IBpmnVariableReader` the interpreter may pull named values through. |
| `Capabilities` | The same `BpmnHostCapabilities` the graph was built with. |

`HasEnclosingScope` and `InvocationCorrelation` exist because escalation and error propagation are
scope-hierarchy operations, and the interpreter for one scope has no view of the tree above it. You
own the tree; the snapshot is how you describe your position in it.

### 3. A variable reader

The one place the interpreter calls back into the host:

```csharp
public sealed class MyHost : IBpmnVariableReader
{
    public BpmnValue Read(string name) =>
        _variables.TryGetValue(name, out var value) ? value : BpmnValue.Absent;
}
```

It is synchronous, read-only and side-effect free. It exists because which variable is needed depends
on which element the propagation loop reaches, which is not knowable before the loop runs, so the
value cannot be hoisted into the snapshot.

Three answers are distinct, and the difference matters:

- `BpmnValue.Absent` — you have no such variable.
- `BpmnValue.Null` — you have it, and it is null.
- `BpmnValuePresence.StoredExternally` — you have it, but not inline, so the interpreter cannot read
  it. It is treated as unreadable rather than absent, so a multi-instance collection stored that way
  fails with a clear diagnostic instead of silently iterating zero times.

If your host needs to await something to answer, pre-resolve it before the evaluation. There is no
async escape hatch here, deliberately.

### 4. A place to keep state

`BpmnExecutionState` is the interpreter's memory: tokens, active children, diagnostics, races, loops,
compensation log, and a sequence counter that every generated id derives from. Treat it as opaque. It
serializes to JSON; persist it however you persist anything.

The only rule is that the state you pass in must be the state you last got out, for that scope
instance. The interpreter has no other memory.

## What you get back

### The continuation

What the scope itself should do, now that the event is processed.

| Continuation | Meaning |
| --- | --- |
| `BpmnContinuation.Complete(Outcome)` | The scope is finished. `Outcome` is the name to report upward. |
| `BpmnContinuation.Defer` | Nothing more to decide. More events will arrive. |
| `BpmnContinuation.Fault(Code, Message)` | The scope failed with a BPMN error code nothing caught. |

`Defer` is the common case, and it is what "the process is waiting" looks like: a timer, a human
task, an unfired message. The interpreter has no notion of waiting — only of having nothing further
to decide until something else happens.

### The commands

There are exactly three. Apply them **in the order returned**; the ordering carries meaning, and a
teardown emitted before a start is not interchangeable with the reverse.

| Command | What it asks you to do |
| --- | --- |
| `StartWork` | Start the work bound to an element and remember its correlation. |
| `CancelWorkSubtree` | Stop a unit of work and everything running underneath it. |
| `SignalEnclosingScope` | Deliver a notification to this scope's parent. |

That is the entire vocabulary. Timers, human tasks, HTTP calls, message subscriptions, nested
processes and long-lived scope listeners are all `StartWork` — the interpreter does not distinguish
them, because the difference is entirely about how you run them.

### The fault disposition

`OnWorkFaulted` returns a `BpmnFaultEvaluation`, which is an evaluation plus one extra field:

```csharp
var evaluation = interpreter.OnWorkFaulted(graph, state, snapshot, workId, errorCode, message);

var handled = evaluation.Disposition switch
{
    BpmnErrorDisposition.Caught caught => $"caught at {caught.CatchingElementId} as {caught.ErrorCode}",
    BpmnErrorDisposition.Propagated    => "nothing here caught it; it went up",
    _                                  => "unknown"
};
```

This is the interpreter telling you whether BPMN absorbed the failure. `Caught` means an error
boundary event or an error event subprocess took it and the process continues along that path.
`Propagated` means no catcher in this scope matched, and the failure is now the enclosing scope's
problem — or, at the root, yours.

You need this because **BPMN has no concept of an incident**. Fault records, alerting, retry policy
and operator dashboards are engine-operations concerns, and the specification says nothing about any
of them. The disposition is the hook: record a fault when it is `Propagated`, stay quiet when the
model already handles it. See [Errors and escalation](../concepts/errors-and-escalation.md).

## A host skeleton

Complete enough to run, small enough to read. It is single-scope, in-process, and keeps everything in
memory — a real host replaces the storage and the work runner, not the shape.

```csharp
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Semantics;

public sealed class MinimalHost : IBpmnVariableReader
{
    private readonly BpmnInterpreter _interpreter = BpmnInterpreter.CreateDefault();
    private readonly BpmnGraph _graph;
    private readonly string _instanceId = Guid.NewGuid().ToString("n");
    private readonly Dictionary<string, BpmnValue> _variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IWorkHandle> _live = new(StringComparer.Ordinal);
    private readonly List<FaultRecord> _faults = [];
    private readonly IWorkRunner _runner;   // your own type: timers, queues, work items, HTTP

    private BpmnExecutionState _state = new();

    public MinimalHost(
        BpmnProcessDefinition definition,
        IReadOnlyList<BpmnWorkBinding> bindings,
        IWorkRunner runner)
    {
        _runner = runner;

        // Refuses now if the definition needs something this host cannot do.
        _graph = BpmnGraph.Build(definition, bindings, Capabilities);
    }

    private static BpmnHostCapabilities Capabilities => BpmnHostCapabilities.Full;

    public IReadOnlyList<FaultRecord> Faults => _faults;

    // --- The interpreter's one callback -------------------------------------------------

    public BpmnValue Read(string name) =>
        _variables.TryGetValue(name, out var value) ? value : BpmnValue.Absent;

    // --- Driving the interpreter --------------------------------------------------------

    public void Start() => Apply(_interpreter.Start(_graph, Snapshot()));

    public void CompleteWork(string workId, string? outcome = null)
    {
        _live.Remove(workId);
        Apply(_interpreter.OnWorkCompleted(_graph, _state, Snapshot(), workId, outcome));
    }

    public void FaultWork(string workId, string errorCode, string message)
    {
        _live.Remove(workId);

        var evaluation = _interpreter.OnWorkFaulted(_graph, _state, Snapshot(), workId, errorCode, message);

        // BPMN has no incidents. Recording one is this host's decision, and the
        // disposition is what makes it an informed decision.
        if (evaluation.Disposition is BpmnErrorDisposition.Propagated)
            _faults.Add(new FaultRecord(workId, errorCode, message));

        Apply(evaluation);
    }

    public void SignalWork(string workId, object signal) =>
        Apply(_interpreter.OnWorkSignalled(_graph, _state, Snapshot(), workId, signal));

    // --- Applying what came back --------------------------------------------------------

    private void Apply(BpmnEvaluation evaluation)
    {
        _state = evaluation.State;          // persist this before acting, if you persist at all

        foreach (var command in evaluation.Commands)   // order matters
        {
            switch (command)
            {
                case BpmnCommand.StartWork start:
                    _live[start.WorkId] = StartWork(start);
                    break;

                case BpmnCommand.CancelWorkSubtree cancel:
                    CancelSubtree(cancel.WorkId);
                    break;

                case BpmnCommand.SignalEnclosingScope signal:
                    Parent?.SignalWork(_instanceId, signal);
                    break;
            }
        }

        switch (evaluation.Continuation)
        {
            case BpmnContinuation.Complete complete:
                OnScopeCompleted(complete.Outcome);
                break;

            case BpmnContinuation.Defer:
                break;                       // waiting; another event will arrive

            case BpmnContinuation.Fault fault:
                OnScopeFaulted(fault.Code, fault.Message);
                break;
        }
    }

    private BpmnHostSnapshot Snapshot() => new(
        ScopeInstanceId: _instanceId,
        HasEnclosingScope: Parent is not null,
        LiveWork: [.. _live.Keys],
        InvocationCorrelation: InvocationCorrelation,
        Variables: this,
        Capabilities: Capabilities);

    // --- Everything below is the host's own business ------------------------------------

    public MinimalHost? Parent { get; init; }
    public string? InvocationCorrelation { get; init; }

    private IWorkHandle StartWork(BpmnCommand.StartWork start)
    {
        // start.ElementId  — which BPMN element asked
        // start.BindingRef — the opaque key naming the work; yours to resolve
        // start.WorkId     — the correlation to give back on completion
        //
        // A timer schedules; a user task creates a work item; a service task calls out;
        // a nested process starts another instance. The interpreter does not care which.
        return _runner.Start(start);
    }

    private void CancelSubtree(string workId)
    {
        // Stop this work and everything started underneath it. Eagerly or on commit —
        // both are legitimate, which is exactly why this is a command and not a callback.
        if (_live.Remove(workId, out var handle))
            handle.CancelSubtree();
    }

    private void OnScopeCompleted(string? outcome) { /* report upward, archive, whatever */ }
    private void OnScopeFaulted(string code, string message) { /* your incident model, if any */ }
}

public sealed record FaultRecord(string WorkId, string ErrorCode, string Message);

// Host-owned, not part of the library.
public interface IWorkRunner
{
    IWorkHandle Start(BpmnCommand.StartWork start);
}

public interface IWorkHandle
{
    void CancelSubtree();
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
- [ ] Persist `evaluation.State` before acting on commands, if you persist.
- [ ] Apply commands in the order returned.
- [ ] Give `OnWorkCompleted` the same `WorkId` you were given by `StartWork`.
- [ ] Pass an outcome name on completion when a conditional sequence flow depends on it.
- [ ] Make `CancelWorkSubtree` recursive. It is not "cancel this one thing".
- [ ] Route `SignalEnclosingScope` to the parent scope instance, not to the root.
- [ ] Decide your incident policy from `BpmnErrorDisposition`, not from every fault.

## Related

- [The host port](../concepts/host-port.md) — the contract in detail.
- [Host capabilities](../concepts/capabilities.md) — why an under-capable host is refused up front.
- [Errors and escalation](../concepts/errors-and-escalation.md) — what BPMN does with a failure
  before it reaches you.
- [ADR 0002: The host port is synchronous and command-returning](../adr/0002-the-host-port-is-synchronous-and-command-returning.md)
