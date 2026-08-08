# The host port

The port is the entire interface between the interpreter and the world. It is small on purpose: four
inputs in, three outputs back, three commands, one callback.

This page is the contract. For a working implementation, see
[Hosting the interpreter](../guides/hosting-the-interpreter.md).

## The signature

```text
(graph, prior state, host snapshot, event) -> (next state, continuation, commands)
```

Every entry point is synchronous and returns a value. The interpreter never calls the host, with one
narrow exception described below.

| Entry point | The event it carries | Returns |
| --- | --- | --- |
| `Start` | The scope is beginning. | `BpmnEvaluation` |
| `OnWorkCompleted` | A unit of work finished, with an optional outcome name. | `BpmnEvaluation` |
| `OnWorkFaulted` | A unit of work failed, with an error code. | `BpmnFaultEvaluation` |
| `OnWorkSignalled` | A notification arrived for live work — including one delivered from a child scope. | `BpmnEvaluation` |

## Inputs

### The graph

`BpmnGraph.Build(definition, boundWork, capabilities)` produces a validated, executable form of a
definition. It is derived from three things that do not vary per instance, so it is built once and
reused.

Building is where structural problems surface: a flow pointing at an element that does not exist, a
multi-instance element with both a cardinality and a collection, a transaction that also carries loop
characteristics, a construct the declared capabilities cannot support. A graph that builds is a graph
the interpreter is willing to run.

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

Ids being a pure function of `Sequence` is what makes a run reproducible: replay the same events
against the same starting state and you get identical ids, not merely equivalent ones.

### The snapshot

`BpmnHostSnapshot` is what the interpreter is permitted to know about your world at the moment of the
call. It is a value, built fresh per evaluation, never held.

| Field | Meaning | Why the interpreter needs it |
| --- | --- | --- |
| `ScopeInstanceId` | Identity of the scope being evaluated. | Correlating commands and diagnostics. |
| `HasEnclosingScope` | Whether a parent exists. | Deciding whether an escalation or error can propagate upward at all. |
| `LiveWork` | The scope's outstanding work. | Deciding what an interruption must tear down, and whether a scope can complete. |
| `InvocationCorrelation` | How the parent knows this invocation. | Addressing `SignalEnclosingScope`. |
| `Variables` | An `IBpmnVariableReader`. | Reading a named value mid-evaluation. |
| `Capabilities` | What this host can do. | Consistency with the graph it was built against. |

You own the scope hierarchy. The interpreter for one scope has no view of the tree above or below it,
which is why parenthood arrives as two snapshot fields rather than as an object graph.

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
| `Complete(Outcome)` | The scope finished. `Outcome` is the name reported upward, and it is what a conditional flow in the parent selects on. |
| `Defer` | Nothing further to decide until another event arrives. |
| `Fault(Code, Message)` | The scope failed with an error nothing in it caught. |

`Defer` is not "waiting". The interpreter has no notion of waiting; it has a notion of having nothing
left to decide. What you do during that gap — suspend, poll, park a message subscription, hold a
thread, do nothing — is invisible to it.

### The commands

Exactly three, applied **in the order returned**. Ordering carries meaning: a teardown emitted before
a start is not interchangeable with the reverse.

#### `StartWork`

Start the work bound to an element, and remember the correlation you were given so you can report
completion against it.

The command carries the element id, the element's opaque `BindingRef`, and the work correlation. The
interpreter never parses a `BindingRef` — it compares and echoes it. Resolving one to an actual
timer, work item, HTTP call or nested process is entirely yours.

Every asynchronous thing BPMN can express is this command. A two-hour timer, a human approval, a
message subscription that may never fire, a scope listener armed for the life of a subprocess: all
`StartWork`. The interpreter does not distinguish them, because the difference is only in how they
run.

#### `CancelWorkSubtree`

Stop a unit of work **and everything started underneath it**. The name is exact. An interrupting
boundary event on a subprocess that has three tasks running and a nested subprocess below one of them
produces one `CancelWorkSubtree`, and BPMN expects all of it to stop.

When you apply it is your choice. Eager recursive teardown and staged teardown flushed on commit are
both legitimate, and that freedom is exactly why this is returned data rather than a callback.

#### `SignalEnclosingScope`

Deliver a notification to this scope's parent — the mechanism behind escalation crossing a scope
boundary. Route it to the parent scope instance identified by `InvocationCorrelation`, not to the
root, and deliver it there through `OnWorkSignalled`.

If `HasEnclosingScope` is false, the interpreter will not emit this command. It asks nothing of a
host it knows cannot answer.

### The fault disposition

`OnWorkFaulted` returns a `BpmnFaultEvaluation`: an evaluation plus a `BpmnErrorDisposition`.

- `Caught(CatchingElementId, ErrorCode)` — an error boundary event or an error event subprocess took
  the failure. The process continues down that path. From BPMN's point of view nothing went wrong.
- `Propagated` — nothing in this scope matched. The failure is now the enclosing scope's problem, or
  at the root, yours.

This is the only place the library says anything about failure policy, and it says the minimum: what
BPMN did. Whether a propagated fault is an incident, whether it is retried, whether anyone is paged
is not modeled here, because BPMN does not model it. See
[Errors and escalation](errors-and-escalation.md).

## The one callback

`IBpmnVariableReader` lets the interpreter pull a named variable mid-evaluation.

It cannot be hoisted into the snapshot, because which variable is needed depends on which element the
propagation loop reaches, and that is not known before the loop runs. It is synchronous, read-only
and side-effect free.

It returns a `BpmnValue`, and three answers are distinct:

| Answer | Meaning |
| --- | --- |
| `BpmnValue.Absent` | No such variable. |
| `BpmnValue.Null` | The variable exists and is null. |
| `BpmnValuePresence.StoredExternally` | The variable exists, but not inline, so it cannot be read. |

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

1. Pass back the state you were last given, for that scope instance.
2. Apply commands in order.
3. Report completion with the correlation you were given by `StartWork`.
4. Make `CancelWorkSubtree` recursive.
5. Do not mutate anything you were handed. The model and the state are immutable by design; treat
   them that way even where a compiler would let you cheat.
6. Declare capabilities honestly. See [Host capabilities](capabilities.md).

## Related

- [Hosting the interpreter](../guides/hosting-the-interpreter.md) — a complete host skeleton.
- [Interpreter, not engine](interpreter-not-engine.md) — why the port stops where it does.
- [ADR 0002: The host port is synchronous and command-returning](../adr/0002-the-host-port-is-synchronous-and-command-returning.md)
