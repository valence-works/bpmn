# The host port

The port is the entire interface between the interpreter and the world. It is small on purpose: four
inputs in, three outputs back, three commands, one callback.

This page is the contract. For a working implementation, see
[Hosting the interpreter](../guides/hosting-the-interpreter.md).

## The signature

```text
(graph, prior state, host snapshot, event) -> (next state, continuation, commands)
```

Every entry point is synchronous, takes a request record carrying those four values, and returns a
value. The interpreter never calls the host, with one narrow exception described below.

| Entry point | The event it carries | Returns |
| --- | --- | --- |
| `Start` | The scope is beginning, optionally at a start event an external trigger matched. | `BpmnEvaluation` |
| `OnWorkCompleted` | A unit of work finished, reporting zero or more outcome names. | `BpmnEvaluation` |
| `OnWorkFaulted` | A unit of work failed, with a human-readable message. | `BpmnFaultEvaluation` |
| `OnWorkSignalled` | A nested scope this one invoked signalled outward. | `BpmnEvaluation` |

Work is named by two values together. The **binding ref** is the interpreter's name for it, taken
from `BpmnElement.BindingRef` or `BpmnElement.ListenerBindingRef`. The **handle** is the host's own
opaque identifier, which the interpreter stores and echoes and never parses. Every callback carries
both, and multi-instance work additionally carries the iteration id it ran under.

## Inputs

### The graph

`BpmnGraph.Build(definition, boundWork, capabilities)` produces a validated, executable form of a
definition. It is derived from three things that do not vary per instance, so it is built once and
reused.

`boundWork` is a collection of `BpmnBoundWork(BindingRef, NestedProcess?)` — one entry per binding
the definition declares, each bound by exactly one element. The interpreter never inspects what the
work is; it only asks the host to start it.

Building is where structural problems surface: a flow pointing at an element that does not exist, a
multi-instance element with both a cardinality and a collection, a transaction that also carries loop
characteristics, a binding supplied twice. Those throw `BpmnExecutionException`; a definition needing
an undeclared capability throws `BpmnCapabilityException`. A graph that builds is a graph the
interpreter is willing to run.

### The prior state

`BpmnExecutionState` is the interpreter's entire memory of an instance:

| Field | Holds |
| --- | --- |
| `Tokens` | Every token, live and historical, with status and position. |
| `ActiveChildren` | The units of host work currently outstanding. |
| `Diagnostics` | An ordered record of what was decided. |
| `Sequence` | The counter every generated id is a pure function of. |
| `Races` | Open and resolved event-based-gateway races. |
| `Loops` | Live multi-instance loops. |
| `Compensables` | The durable, reverse-order compensation log. |
| `CompensationRuns` | In-flight compensation replays. |
| `Terminated` / `Cancelling` | Whether a terminate or cancel end event has ended the scope. |
| `PendingFault` | A fault decision deferred to the next callback. |

Treat it as opaque. It serializes to JSON; the only rule is that the state you pass in is the state
you last got out for that scope instance.

Call `Prune()` before persisting. It removes the records that can never influence a future decision —
consumed tokens no longer referenced by active work, and diagnostics beyond `DiagnosticsCap` (200) —
so a long-running process does not re-serialize an ever-growing blob. Canceled tokens are never
pruned: a terminate strips in-flight work while that work may still complete, and the late completion
is absorbed by the token lookup that the pruned record would have broken.

Ids being a pure function of `Sequence` is what makes a run reproducible: replay the same events
against the same starting state and you get byte-identical state, not merely equivalent state.
Pruning does not bump `Sequence`, so it cannot disturb that.

### The snapshot

`BpmnHostSnapshot` is what the interpreter is permitted to know about your world at the moment of the
call. It is a value, built fresh per evaluation, never held.

| Field | Meaning | Why the interpreter needs it |
| --- | --- | --- |
| `ScopeInstanceId` | An opaque host-chosen identity for this scope. | Echoed onto tokens for provenance. Never parsed. |
| `HasEnclosingScope` | Whether a parent exists to receive a signal. | A root process has none, so an unhandled escalation is a documented no-op rather than a fault. |
| `LiveWork` | `BpmnLiveWork(BindingRef, IterationId, Handle)` per outstanding unit. | Deciding what an interruption must tear down, and whether a scope can complete. |
| `InvocationCorrelation` | The correlation dictionary the host carried into this invocation, echoed back verbatim. | Empty for a process started directly. Carries the start-element hint for an event subprocess body. |
| `Variables` | An `IBpmnVariableReader`. | Reading a named value mid-evaluation. |
| `Capabilities` | What this host can do. | Consistency with the graph it was built against. |

`BpmnHostSnapshot.Root(scopeInstanceId, capabilities)` builds the root-scope case: no parent, nothing
running, no variables.

You own the scope hierarchy. The interpreter for one scope has no view of the tree above or below it,
which is why parenthood arrives as snapshot fields rather than as an object graph.

### The event

One event per call. Not a batch, not a stream. The interpreter processes a single stimulus to
quiescence and returns.

## Outputs

### The next state

Persist it before acting on the commands, if you persist at all. Applying a command whose state was
never durably recorded is how a crash produces work with no token behind it.

### The continuation

What the scope itself should do now.

| Continuation | Meaning |
| --- | --- |
| `Complete(Outcome)` | The scope finished: no token remains and no work is running. `Outcome` is `"Done"` normally, or `"Cancelled"` when a cancel end event cancelled a transaction. It is what a conditional flow in the parent selects on. |
| `Defer` | Still running: work was started, a token waits at a join, or a listener is armed. |
| `Fault(Code, Message)` | The scope failed deterministically and cannot continue. |

`Defer` is not "waiting". The interpreter has no notion of waiting; it has a notion of having nothing
left to decide. What you do during that gap — suspend, poll, park a message subscription, hold a
thread, do nothing — is invisible to it.

### The commands

Exactly three, applied **in the order returned**. Ordering carries meaning: a teardown emitted before
a start is not interchangeable with the reverse.

#### `StartWork`

Start the work bound to an element, and remember the handle you assign so you can report back against
it.

It carries the `BindingRef` naming the work, the `ElementId` and `SchedulingCause` for your own
reporting, the `TokenId` parked while the work runs, and a `Correlation` dictionary of opaque
interpreter state you must carry with the work and hand back on the resulting callback — and, when
the work is a nested BPMN process, supply as that process's `InvocationCorrelation`.

Two optional fields appear on specific constructs. `IterationScope` carries the per-instance values a
multi-instance instance must be seeded with: always the zero-based `loopIndex`, plus the current item
in collection mode. `StartElementHint` names the start event an event subprocess body must begin at.

The interpreter never parses a `BindingRef` — it compares and echoes it. Resolving one to an actual
timer, work item, HTTP call or nested process is entirely yours.

Every asynchronous thing BPMN can express is this command. A two-hour timer, a human approval, a
message subscription that may never fire, a scope listener armed for the life of a subprocess: all
`StartWork`. The interpreter does not distinguish them, because the difference is only in how they
run.

#### `CancelWorkSubtree`

Stop a unit of work **and everything started underneath it**. The name is exact. An interrupting
boundary event on a subprocess that has three tasks running and a nested subprocess below one of them
produces one `CancelWorkSubtree`, and BPMN expects all of it to stop.

It carries the `Handle` of the work to stop, the `ElementId` for reporting, and a stable `Reason`
code — `bpmn.boundary.host-interrupted`, `bpmn.event-based-gateway.superseded-by-first-catch`, and so
on. The reasons are constants on `BpmnInterpreter`, and they are worth surfacing: they turn "work was
cancelled" into "work was cancelled because the first catch won the race".

When you apply it is your choice. Eager recursive teardown and staged teardown flushed on commit are
both legitimate, and that freedom is exactly why this is returned data rather than a callback.

#### `SignalEnclosingScope`

Deliver a notification to this scope's parent — the mechanism behind escalation crossing a scope
boundary. Route it to the immediate parent scope, not the root, and deliver it there through
`OnWorkSignalled` under the handle the parent started this scope with.

It carries a `Code` and an optional JSON `Payload`. There is exactly one code for escalation —
`BpmnInterpreter.EscalationSignalCode`, which is `bpmn.escalation` — with the escalation's identity
travelling in the payload. A host multiplexing several kinds of scope signal therefore reserves one
code rather than tracking a growing namespace.

If `HasEnclosingScope` is false, the interpreter will not emit this command. It asks nothing of a
host it knows cannot answer.

### The fault disposition

`OnWorkFaulted` returns a `BpmnFaultEvaluation`: an evaluation plus a `BpmnErrorDisposition`.

- `Caught(CatchingElementId, ErrorCode?)` — an error boundary event or an error event subprocess took
  the failure. The process continues down that path. From BPMN's point of view nothing went wrong. A
  null `ErrorCode` means the catcher is a catch-all.
- `Propagated` — nothing in this scope matched. The continuation is a `Fault`, and the failure is now
  the enclosing scope's problem, or at the root, yours.

Note what the fault request does not carry: an error code. The host reports that work failed and
describes why in human terms; which BPMN error a catcher matches is a property of the model, not
something the host names.

This is the only place the library says anything about failure policy, and it says the minimum: what
BPMN did. Whether a propagated fault is an incident, whether it is retried, whether anyone is paged
is not modeled here, because BPMN does not model it. See
[Errors and escalation](errors-and-escalation.md).

## The one callback

`IBpmnVariableReader` lets the interpreter pull a named variable mid-evaluation.

It cannot be hoisted into the snapshot, because which variable is needed depends on which element the
propagation loop reaches, and that is not known before the loop runs. It is synchronous, read-only
and side-effect free.

```csharp
public interface IBpmnVariableReader
{
    bool TryRead(string name, out BpmnValue value);
}
```

Three answers are distinct:

| Answer | Meaning |
| --- | --- |
| `false` | No such variable. |
| `true`, with `BpmnValue.Null` | The variable exists and is null. |
| `true`, with `BpmnValuePresence.StoredExternally` | The variable exists, but not inline, so it cannot be read. |

`BpmnNoVariables.Instance` is the shared implementation for a host with no variables at all.

The third is the interesting one. It is treated as unreadable rather than absent, so a multi-instance
collection held outside the inline payload fails with a clear diagnostic instead of silently
iterating zero times. Distinguishing "no" from "cannot tell you" prevents a whole class of quiet
wrong answers.

Values cross the boundary as JSON with a host-interpreted type hint. `BpmnValueTypes` names the two
hints the interpreter itself understands — `integer` and `any` — and every other string is yours to
define. The library never assigns meaning to your type system.

If your host would need to await something to answer a read, pre-resolve it before the evaluation.
There is deliberately no async path here.

## Invariants a host must uphold

1. Pass back the state you were last given, for that scope instance — pruned, if you like.
2. Apply commands in order.
3. Report a callback with the binding ref and handle the work was started under.
4. Carry `StartWork.Correlation` with the work and hand it back.
5. Make `CancelWorkSubtree` recursive.
6. Do not mutate anything you were handed. The model and the state are immutable by design; treat
   them that way even where a compiler would let you cheat.
7. Declare capabilities honestly, and declare the same set to `Build` and to every snapshot. See
   [Host capabilities](capabilities.md).

## Related

- [Hosting the interpreter](../guides/hosting-the-interpreter.md) — a complete host skeleton.
- [Interpreter, not engine](interpreter-not-engine.md) — why the port stops where it does.
- [ADR 0002: The host port is synchronous and command-returning](../adr/0002-the-host-port-is-synchronous-and-command-returning.md)
