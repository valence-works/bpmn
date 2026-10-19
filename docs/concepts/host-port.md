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

Work carries three complementary identifiers. The **binding ref** is the interpreter's name for it,
taken from `BpmnElement.BindingRef` or `BpmnElement.ListenerBindingRef`. The **handle** is the host's
own opaque identifier for the physical work; the interpreter stores and echoes it but never parses
it. The **work token ID** is the scope-local `BpmnHostCommand.StartWork.TokenId` that started this
invocation. Save that ID with the handle and echo it as `WorkTokenId` on the live-work snapshot and
callback request. It is not an iteration ID, a nested scope's token, or `InvocationCorrelation`.

For completion and fault callbacks, an explicit `WorkTokenId` selects one active-work record. Its
binding ref and any reported iteration ID must agree with that record; an unknown, blank, or
contradictory ID is rejected rather than used to select a sibling. Without an ID, a supplied
iteration ID must match exactly. If the callback omits its iteration ID, it resolves only when that
binding has exactly one outstanding invocation. Ambiguous legacy completions and faults throw
`BpmnExecutionException`; an unmatched legacy fault faults the scope.

Recognized legacy escalation signals use a different lookup: the binding ref and
`SignallingIterationId` must match the same slot exactly. A null signalling iteration matches only a
null-iteration slot; it never falls back to a sole multi-instance invocation. An ambiguous exact
slot throws. If the exact slot resolves an active invocation, the snapshot must contain its matching
`LiveWork` entry and `SignallingHandle` must equal that entry's handle; this applies to ID-less
invocation records too. Only when no active invocation matches that exact slot does the established
legacy late-signal behavior apply. A recognized escalation with explicit `WorkTokenId` must identify
live work and its matching handle must remain in `LiveWork`. Unrecognized signal codes remain
diagnostic pass-throughs and do not use `WorkTokenId` to route. Hosts that already retired a work
handle absorb its late signal before calling the interpreter, as the reference host does.

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

#### A cycle must be able to wait

Cycles are executable, but one structural problem is specific to them. If every element on a cycle
passes a token straight on, a token that enters it is routed around the cycle forever within one
evaluation, and the evaluation never returns to the host. `Build` refuses such a definition with a
`BpmnExecutionException` that names the cycle's element ids in flow order, starting where the loop-back
flow lands.

A cycle can wait when at least one element on it can hold a token:

- any element that binds work, because the token waits for the host: every kind of task, including a
  call activity, plus a subprocess, a catch event, a message throw event, and an exclusive or inclusive
  gateway bound to a decision;
- a join, meaning a parallel or inclusive gateway with more than one incoming flow;
- a compensate throw event, which holds the token while compensation handlers run;
- an escalation throw event that an interrupting event subprocess of the same scope catches, because
  the activation stops every other token in the scope.

Everything else passes the token on: an unbound task, an unbound exclusive or inclusive split, a
parallel split, and an escalation throw that nothing interrupting catches. That includes one a
non-interrupting event subprocess catches, which starts the handler on every pass while the throw's own
token carries on. The check follows only the flows these elements actually take when a token arrives.
An arriving token reports no outcome, so a conditional flow out of an unbound element is never taken,
and a cycle closed only by such a flow is not a cycle the token can follow. When a refused cycle has
such a flow, the refusal names it, because it is usually the way out the modeler meant.

The check reads the built-in behaviors. An interpreter given a registry that replaces one of them
behaves as that registry says, but `Build` still answers for the built-in behavior, and a host whose
registry routes differently has no way to override the check yet. The check also judges the cycle on
its own: it does not credit another branch that might end the scope first, such as a parallel path
into a terminate end event, and it does not ask whether any token can reach the cycle, so a cycle no
token can get to is refused too.

The rule refuses only what is certain to circulate. A join, a compensate throw and a multi-instance
activity hold a token on some runs and not on others: a join the cycle satisfies by itself fires at
once, a compensate throw with nothing registered completes at once, and a multi-instance activity
whose collection is empty routes on at once. Cycles through those build, and the propagation limit
described under [the continuation](#the-continuation) stops a run on which they did not wait.

### The prior state

`BpmnExecutionState` is the interpreter's entire memory of an instance:

| Field | Holds |
| --- | --- |
| `Tokens` | Every token, live and historical, with status and position. |
| `ActiveWork` | The units of host work currently outstanding. |
| `Diagnostics` | An ordered record of what was decided. |
| `Sequence` | The counter every generated id is a pure function of. |
| `Races` | Open and resolved event-based-gateway races. |
| `Loops` | Live multi-instance loops. |
| `Compensables` | The durable, reverse-order compensation log. |
| `CompensationRuns` | In-flight compensation replays. |
| `Terminated` / `Cancelling` | Whether a terminate or cancel end event has ended the scope. |
| `PendingFault` | A fault decision an earlier version deferred to the next callback. No longer written, and surfaced on the next callback when present. |

Treat it as opaque. It serializes to JSON; the only rule is that the state you pass in is the state
you last got out for that scope instance.

Call `Prune()` before persisting. It removes consumed tokens no longer referenced by active work and
diagnostics beyond `DiagnosticsCap` (200). Canceled tokens are never pruned: a stopped-work handle
must remain available to recognize a late callback (see
[Callbacks for work already stopped](#callbacks-for-work-already-stopped)).

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
| `LiveWork` | One `BpmnLiveWork` per outstanding unit, including its `WorkTokenId`. | Resolving reports and deciding what an interruption must tear down, and whether a scope can complete. |
| `InvocationCorrelation` | The correlation dictionary the host carried into this invocation, echoed back verbatim. | Empty for a process started directly. Carries the start-element hint for an event subprocess body. Never used to find the work a callback reports: in a nested scope the token id it carries belongs to the enclosing scope. |
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

One `Fault` comes from the interpreter rather than from the process. A single evaluation dispatches at
most `BpmnInterpreter.PropagationStepLimit` tokens to element behaviors, which is 10,000; a dispatch is
one token arriving at one element. An evaluation that reaches the limit without coming to rest answers
`Fault` with the code `bpmn.propagation.limit` (`BpmnInterpreter.PropagationLimitFaultCode`), and the
message names the element dispatched last. In practice it means a cycle that could have waited did not
on this run; see [A cycle must be able to wait](#a-cycle-must-be-able-to-wait). An evaluation that
comes to rest crosses each sequence flow roughly once per token, so no ordinary process gets near the
limit.

The limit is a count, not a clock, so where a run stops is deterministic. It is not configurable.
Reaching it is expensive: every dispatch grows the state the next one copies, so hitting the limit takes
seconds and returns a state of several megabytes. Read it as a defect report about the definition, not
as a way to bound a loop.

This `Fault` carries no commands at all. Like every fault it carries no `StartWork`, and unlike every
other fault it also drops the `SignalEnclosingScope` commands the evaluation raised, recording how many
in a diagnostic. An escalation throw on the cycle raised one on every pass, so the parent would
otherwise hear thousands of escalations, and a non-interrupting escalation boundary there would fire
once per pass.

### The commands

Exactly three, applied **in the order returned**. Ordering carries meaning: a teardown emitted before
a start is not interchangeable with the reverse.

#### `StartWork`

Start the work bound to an element, and remember the handle you assign so you can report back against
it.

It carries the `BindingRef` naming the work, the `ElementId` and `SchedulingCause` for your own
reporting, the `TokenId` parked while the work runs, and a `Correlation` dictionary of opaque
interpreter state. The dictionary has exactly one use: when the work is a nested BPMN process,
supply it as that process's `InvocationCorrelation`. Nothing hands it back. Keep `TokenId` with the
handle and echo it as `WorkTokenId` in live-work snapshots and callback requests. Completion and
fault callbacks may use legacy binding/iteration lookup: a supplied iteration must match exactly,
and an omitted iteration is accepted only when that binding has one outstanding invocation. A
recognized legacy escalation signal instead requires an exact `(BindingRef,
SignallingIterationId)` slot match. A null signalling iteration matches only a null slot and cannot
select a sole multi-instance instance. When that exact slot has an active invocation, the snapshot
must contain its matching `LiveWork` entry and `SignallingHandle` must match the entry's handle,
including when the invocation record is ID-less. The legacy late-signal path applies only when no
active invocation matches the exact slot. For a recognized escalation with explicit `WorkTokenId`,
the invocation and its matching handle must still be live. Unknown signal codes pass through as
diagnostic evidence without identity-based routing.

Two optional fields appear on specific constructs. `IterationScope` carries the per-instance values a
multi-instance instance must be seeded with: always the zero-based `loopIndex`, plus the current item
in collection mode. `StartElementHint` names the start event an event subprocess body must begin at.

The interpreter never parses a `BindingRef` — it compares and echoes it. Resolving one to an actual
timer, work item, HTTP call or nested process is entirely yours.

Every asynchronous thing BPMN can express is this command. A two-hour timer, a human approval, a
message subscription that may never fire, a scope listener armed for the life of a subprocess: all
`StartWork`. The interpreter does not distinguish them, because the difference is only in how they
run.

Every `StartWork` you receive is for a token that is still waiting on it when the evaluation returns.
When the same evaluation stops that token again, the command is withdrawn rather than returned: an
interrupting event subprocess or a cancel end event reached in the same propagation as a sibling
branch's task, say. No teardown is issued for withdrawn work either, because you never had it.

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

Make it idempotent: a `CancelWorkSubtree` for a handle you no longer hold, because that work already
finished or was already torn down, must be a no-op. You will see repeats. Once a terminate has ended a
scope, any callback that still reaches it is answered with `Complete` again, plus a teardown for every
handle the snapshot still lists, including ones you were already asked to stop but have not yet
stopped. Nothing is ever started again in that scope.

#### `SignalEnclosingScope`

Deliver a notification to this scope's parent — the mechanism behind escalation crossing a scope
boundary. Route it to the immediate parent scope, not the root, and deliver it there through
`OnWorkSignalled` under the handle the parent started this scope with.

It is the one command a `Fault` continuation may carry. An escalation throw issues its signal as it
is raised, before the evaluation reaches any verdict, so when the same evaluation then faults, the
parent still hears the escalation. A scope that has already ended forwards nothing, and a fault at the
propagation limit drops what it raised; see [the continuation](#the-continuation).

It carries a `Code` and an optional JSON `Payload`. There is exactly one code for escalation —
`BpmnInterpreter.EscalationSignalCode`, which is `bpmn.escalation` — with the escalation's identity
travelling in the payload. A host multiplexing several kinds of scope signal therefore reserves one
code rather than tracking a growing namespace.

For that recognized code, include the `WorkTokenId` of the parent-scope invocation that started the
signalling child and leave its `BpmnLiveWork` entry in the snapshot. An explicit ID must match the
live invocation and handle; it does not fall back to another unit on the same slot. Legacy callers
may omit the ID, subject to unique lookup for the exact binding/iteration slot. If that slot resolves
an active invocation, its `BpmnLiveWork` entry must be present and the signalled handle must match
that entry; the existing late-signal behavior applies only when no active invocation matches the
slot. A signal with another code is diagnostic pass-through, and its identity is not used to route it.

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

### Callbacks for work already stopped

A callback can still reach a running scope for work it already tore down: a completion or fault that
raced the teardown an interruption issued. The scope recognizes it by its handle. Each
`CancelWorkSubtree` that goes out on a non-fault continuation records its handle on the token it
cancelled (`BpmnToken.StoppedHandle`, added in payload format 1.1.0). A completion or fault reporting
one of those handles is absorbed once: it changes nothing in the scope, nothing is issued, and a
`Canceled` diagnostic records it. If the request supplies `WorkTokenId`, it must match the token
carrying that stopped handle before the one-shot marker is cleared; a mismatch is rejected without
spending the marker. An ID-less legacy report retains handle-only absorption. The record is then
spent, so a second report for the same handle is not absorbed. A handle must name one unit for the
scope's lifetime; never reuse one.

Any other callback is a broken contract rather than a race, and stays loud: a completion throws
`BpmnExecutionException`, and a fault faults the scope. That covers a second report for work that
completed normally, a binding no element binds, and work the interpreter withdrew before you ever
started it, which has no handle to match. Terminal work's own handle and token ID must have left
`LiveWork`; a snapshot entry for that exact invocation contradicts the report. A different
invocation on the same binding and iteration is valid when its own explicit `WorkTokenId` resolves.
Without an ID, the legacy binding/iteration lookup must be unambiguous; it cannot guess between
siblings that share a slot.

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
3. Report a callback with the binding ref and handle the work was started under, and with its
   iteration id when it is a multi-instance instance. Echo `StartWork.TokenId` as `WorkTokenId` when
   using explicit invocation identity.
4. Pass `StartWork.Correlation` to a nested process as its `InvocationCorrelation`, and write nothing
   else into that dictionary.
5. Make `CancelWorkSubtree` recursive.
6. Do not mutate anything you were handed. The model and the state are immutable by design; treat
   them that way even where a compiler would let you cheat.
7. Declare capabilities honestly, and declare the same set to `Build` and to every snapshot. See
   [Host capabilities](capabilities.md).
8. Never reuse a handle within a scope. A late ID-less terminal report for work the scope tore down
   is absorbed by its handle; if it supplies `WorkTokenId`, that ID must also match the stopped
   token. Absorb a signal for a handle the host already retired before calling the interpreter.

## Related

- [Hosting the interpreter](../guides/hosting-the-interpreter.md) — a complete host skeleton.
- [Interpreter, not engine](interpreter-not-engine.md) — why the port stops where it does.
- [ADR 0002: The host port is synchronous and command-returning](../adr/0002-the-host-port-is-synchronous-and-command-returning.md)
