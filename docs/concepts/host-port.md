# The host port

The port is the entire interface between the interpreter and the world. It is small on purpose: four
inputs in, three commands, two read-only callbacks, and an optional error-routing verdict on the
evaluation.

This page is the contract. For a working implementation, see
[Hosting the interpreter](../guides/hosting-the-interpreter.md).

## The signature

```text
(graph, prior state, host snapshot, event) -> (next state, continuation, commands, error disposition?)
```

Every entry point is synchronous, takes a request record carrying those four inputs, and returns a
`BpmnEvaluation`. Its nullable `ErrorDisposition` is populated when a host-reported failure is
routed or absorbed; otherwise it is `null`. The interpreter never calls the host, with two narrow
exceptions described [below](#the-callbacks): reading a variable, and evaluating a condition
expression.

| Entry point | The event it carries | Returns |
| --- | --- | --- |
| `Start` | The scope is beginning, optionally at a start event an external trigger matched. | `BpmnEvaluation` |
| `OnWorkCompleted` | A unit of work finished, reporting zero or more outcome names. | `BpmnEvaluation` |
| `OnWorkFaulted` | A unit of work failed, with a human-readable message. | `BpmnEvaluation` |
| `OnWorkSignalled` | A nested scope this one invoked signalled outward. | `BpmnEvaluation` |

Work is named by two values together. The **binding ref** is the interpreter's name for it, taken
from `BpmnElement.BindingRef` or `BpmnElement.ListenerBindingRef`. The **handle** is the host's own
opaque identifier, which the interpreter stores and echoes and never parses. Every callback carries
both, and multi-instance work additionally carries the iteration id it ran under. A completion and a
signal take it as a trailing parameter; a fault takes it as the init-only
`BpmnWorkFaultedRequest.FaultedIterationId`. The interpreter finds the unit a completion or a fault
reports from the binding ref and iteration id alone. A reported iteration id must match exactly; only
a report without one falls back to the binding's single outstanding unit. So a report for one of
several concurrent instances that leaves the iteration id out throws `BpmnExecutionException`
rather than guess which. That is deliberately not what a fault for work nothing matches does: such a
fault faults the scope, because a unit failed that the scope cannot account for, whereas an
ambiguous report is the host leaving out what it must report.

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
An arriving token reports no outcome, so a flow conditioned on an outcome out of an unbound element is
never taken, and a cycle closed only by such a flow is not a cycle the token can follow. When a refused
cycle has such a flow, the refusal names it, because it is usually the way out the modeler meant.

A flow whose only condition is an expression is different: the host answers it at run time, and `Build`
cannot ask. The check treats such a flow as possibly taken and never as certainly taken. An exclusive
gateway whose way off a cycle is an expression therefore builds, because the answer may take the token
off the cycle; so does one whose way back is an expression. A task or inclusive split with an
unconditional flow back is still refused even when an expression offers a way out, because a split
takes its unconditional flows whatever the expression answers. An exclusive gateway takes the first
flow that holds in declaration order, and a flow without a condition always holds, so an unconditional
flow back declared before the expression exit is refused as well.

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

Call `Prune()` before persisting. It removes the records that can never influence a future decision —
consumed tokens no longer referenced by active work, and diagnostics beyond `DiagnosticsCap` (200) —
so a long-running process does not re-serialize an ever-growing blob. Canceled tokens are never
pruned: tearing work down drops its record while the work may still report back, and the cancelled
token carries the torn-down handle that recognizes that late callback (see
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
| `LiveWork` | `BpmnLiveWork(BindingRef, IterationId, Handle)` per outstanding unit. | Deciding what an interruption must tear down, and whether a scope can complete. |
| `InvocationCorrelation` | The correlation dictionary the host carried into this invocation, echoed back verbatim. | Empty for a process started directly. Carries the start-element hint for an event subprocess body. Never used to find the work a callback reports: in a nested scope the token id it carries belongs to the enclosing scope. |
| `Variables` | An `IBpmnVariableReader`. | Reading a named value mid-evaluation. |
| `Capabilities` | What this host can do. | Consistency with the graph it was built against. |
| `Conditions` | An `IBpmnConditionEvaluator`, or `null`. Init-only, not a positional parameter. | Answering a sequence flow's condition expression mid-evaluation. Required when the graph needs `ConditionEvaluation`. |

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
supply it as that process's `InvocationCorrelation`. Nothing hands it back. A completion, fault or
signal is matched by its binding ref, iteration id and handle alone.

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
Build that payload with `BpmnInterpreter.EscalationPayload` and read it with
`BpmnInterpreter.TryReadEscalationCode` rather than reproducing its shape; see
[Errors and escalation](errors-and-escalation.md#escalation-crosses-scopes-by-signalling).

If `HasEnclosingScope` is false, the interpreter will not emit this command. It asks nothing of a
host it knows cannot answer.

### The error disposition

`BpmnEvaluation.ErrorDisposition` is a nullable `BpmnErrorDisposition`. It reports how BPMN handled a
host-reported failure without making the host adopt an incident or retry policy:

- `Caught(CatchingElementId, ErrorCode?)` — an error boundary event or an error event subprocess took
  the failure and this evaluation returned without fault. Recovery work may still be running. A
  null `ErrorCode` means the catcher is a catch-all.
- `Propagated` — the final evaluation has a `Fault` continuation. This means no catcher in the scope
  matched, or a catcher's route faulted during this evaluation; the failure is now the enclosing
  scope's problem, or at the root, yours.
- `Absorbed` — the failure belonged to work or a scope that had already stopped.

The disposition is present for failed work reported by `OnWorkFaulted`, and for a call-activity
completion reported by `OnWorkCompleted` with
the outcome `Faulted`, `DispatchFailed`, or `Cancelled`. Those call-activity outcomes use the same
error-catcher routing as a fault report. A late failure for work or a scope that was already stopped
is reported as `Absorbed`.

The property is `null` when no host-reported failure was routed or absorbed. That includes normal
starts, completions and signals, transaction cancellation, and an event subprocess activated on an
unrelated path. In particular, a transaction's `Cancelled` completion is distinct from a call
activity's `Cancelled` failure outcome. `BpmnTranscriptEntry.Disposition` is a separate transcript
field and is unchanged.

Note what `BpmnWorkFaultedRequest` does not carry: an error code. The host reports that work failed
and describes why in human terms. Today every error catcher takes any failure. This changes in 0.4.0:
[ADR 0012](../adr/0012-hosts-and-error-end-events-name-bpmn-errors.md) adds an error code to the
fault request and to the `Fault` continuation, and makes a coded catcher catch only its own code.

This is the only place the library says anything about failure policy, and it says the minimum: what
BPMN did. Whether a propagated fault is an incident, whether it is retried, whether anyone is paged
is not modeled here, because BPMN does not model it. See
[Errors and escalation](errors-and-escalation.md).

### Callbacks for work already stopped

A callback can still reach a running scope for work it already tore down: a completion that raced
the teardown an interruption issued. The scope recognizes it by its handle. Each `CancelWorkSubtree`
that goes out on a non-fault continuation records its handle on the token it cancelled
(`BpmnToken.StoppedHandle`, added in payload format 1.1.0). A completion or fault reporting one of
those handles is absorbed once: it changes nothing in the scope, nothing is issued, and a `Canceled`
diagnostic records it. The record is then spent, so a second report for the same handle is not
absorbed. Matching is on the handle alone, never on the binding or the element, because a loop that
re-enters an element starts a new unit on the same `(binding ref, iteration id)` slot, and an earlier
pass's late report must neither pass for the later pass's nor stop a later pass's duplicate from
being caught. That requires a handle to name one unit for the scope's lifetime: never reuse one.

Any other callback is a broken contract rather than a race, and stays loud: a completion throws
`BpmnExecutionException`, and a fault faults the scope. That covers a second report for work that
completed normally, a binding no element binds, and work the interpreter withdrew before you ever
started it, which has no handle to match. A report the snapshot contradicts throws even when a
record would match it: the reported unit has already left `LiveWork`, so if the snapshot still lists
the slot of the record the report resolves to under another handle, that record belongs to other
work. That includes a report with no iteration id that would fall back to the binding's only record. A state an earlier version persisted
records no handles, so a late callback against it is never absorbed. A late completion throws. A
late fault faults the scope, or throws when the snapshot still lists its slot under another handle.

## The callbacks

There are two, of the same shape. Neither can be hoisted into the snapshot as a value, because which
variable or which condition is needed depends on which element the propagation loop reaches, and that
is not known before the loop runs. Both are synchronous, read-only and side-effect free, and neither
has an async path.

### Variables

`IBpmnVariableReader` lets the interpreter pull a named variable mid-evaluation.

```csharp
public interface IBpmnVariableReader
{
    bool TryRead(string name, out BpmnValue value);
}
```

The read outcomes are distinct:

| Answer | Meaning |
| --- | --- |
| `false` | No such variable; a collection-mode loop faults with `bpmn.loop.collection-unreadable`. |
| `true`, with `BpmnValue.Absent` | The read was answered, but there is no value; a collection-mode loop treats it as an empty collection. |
| `true`, with `BpmnValue.Null` | The variable exists and is null; a collection-mode loop treats it as an empty collection. |
| `true`, with `BpmnValuePresence.StoredExternally` | The variable exists, but not inline, so it cannot be read. |
| `true`, with `BpmnValue.Refused(code, message)` | The host withholds the value; a collection-mode loop faults with exactly that code and message. |

`BpmnNoVariables.Instance` is the shared implementation for a host with no variables at all.

External storage is treated as unreadable rather than absent, so a multi-instance
collection held outside the inline payload fails with a clear diagnostic instead of silently
iterating zero times. Distinguishing "no" from "cannot tell you" prevents a whole class of quiet
wrong answers. A refusal is also distinct: return `true` with `BpmnValue.Refused("host.variable.denied", "The collection is withheld.")`.
Both sequential and parallel collection loops return the host's exact fault code and message without
starting iterations. A malformed refused value without a usable code faults as
`bpmn.loop.collection-unreadable`; a missing message gets a deterministic diagnostic. Empty host
messages are preserved. See [the decision](../adr/0008-variable-read-refusals-preserve-host-faults.md).

Values cross the boundary as JSON with a host-interpreted type hint. `BpmnValueTypes` names the two
hints the interpreter itself understands — `integer` and `any` — and every other string is yours to
define. The library never assigns meaning to your type system.

If your host would need to await something to answer a read, pre-resolve it before the evaluation.
There is deliberately no async path here.

### Conditions

`IBpmnConditionEvaluator` answers a sequence flow's `<conditionExpression>`. It travels on the snapshot
as `Conditions`, beside `Variables`, and is needed only by a host that declares
[`ConditionEvaluation`](capabilities.md).

```csharp
public interface IBpmnConditionEvaluator
{
    BpmnConditionResult Evaluate(BpmnConditionRequest request);
}

public sealed record BpmnConditionRequest(
    string ElementId,   // the element whose outgoing flows are being chosen
    string FlowId,      // the flow whose condition this is
    string Site,        // BpmnConditionSites.SequenceFlow
    string Body,        // the expression text exactly as modeled
    string? Language,   // the flow's language, else the process default, else null
    string? Iteration); // the multi-instance iteration id, or null outside one
```

The library still contains no expression language. It decides **which** conditions are asked, **when**
and in **what order**, and **what the answers mean** for routing. Your evaluator decides only what one
expression evaluates to, against your own data, in your own language. `Language` is null when the
document declares none, not BPMN's nominal XPath: what a missing language means is your call.
`Iteration` names the multi-instance instance the routed token belongs to. The instances of a
multi-instance activity never route on their own: the activity routes once, after its last instance,
outside any iteration, and a multi-instance subprocess body runs as its own scope with its own snapshot.
So with the built-in behaviors it is null today; it exists so the request does not change shape when a
routing site inside an iteration does.

The answer is one of three:

| Answer | Meaning |
| --- | --- |
| `BpmnConditionResult.True` | The condition holds. |
| `BpmnConditionResult.False` | It does not. |
| `BpmnConditionResult.Failed(faultCode, message)` | It could not be evaluated. The scope faults with `faultCode`, or with `bpmn.condition.failed` when that is null. The one exception is an escalation throw whose escalation an interrupting event subprocess of its own scope caught: its successor is stopped anyway, so the answer stays on the trace and the token ends. |

A failure is a verdict on the scope, like `bpmn.gateway.no-flow-selected`, not a BPMN error raised by
bound work, so no error boundary or error event subprocess takes it. An evaluator that throws has broken
the port: the exception surfaces from the entry point as a `BpmnExecutionException` naming the element
and flow, with yours as its `InnerException`. So does a host that declared `ConditionEvaluation` and
left `Conditions` null.

When the interpreter asks:

- A flow with a `conditionOutcome` is matched on the outcome and its expression is never evaluated,
  even when it has one.
- The default flow is never evaluated. It is taken only when nothing else was.
- An exclusive gateway visits its non-default flows in declaration order and takes the first that holds.
  Nothing after it is evaluated. A flow without any condition holds.
- An inclusive gateway, a task, and every other element that routes like one evaluates every expression
  in declaration order, stopping only at a failure, and takes every flow that holds plus every flow
  without a condition.
- A gateway that binds decision work evaluates when that work completes, together with the outcome
  match. Every other element evaluates when it routes: on token arrival when it binds nothing, on
  completion when it does.
- Nothing holds and there is no default: an exclusive or inclusive gateway faults the scope with
  `bpmn.gateway.no-flow-selected`, and any other element that has outgoing flows faults it with
  `bpmn.flow.none-taken`. An element with no outgoing flow at all is an implicit end. An escalation throw
  that an interrupting event subprocess of its own scope caught does not fault: the activation stops its
  successor anyway, so a failed answer or no flow taken there ends the token.

Every evaluation adds a `ConditionEvaluated` diagnostic naming the element, the flow and the answer,
before the routing or fault it led to. Given the same inputs and the same answers, an evaluation is
still byte-for-byte reproducible; that now rests on your evaluator being a function of its inputs, as
it already rested on your variable reader.

## Invariants a host must uphold

1. Pass back the state you were last given, for that scope instance — pruned, if you like.
2. Apply commands in order.
3. Report a callback with the binding ref and handle the work was started under, and with its
   iteration id when it is a multi-instance instance.
4. Pass `StartWork.Correlation` to a nested process as its `InvocationCorrelation`, and write nothing
   else into that dictionary.
5. Make `CancelWorkSubtree` recursive.
6. Do not mutate anything you were handed. The model and the state are immutable by design; treat
   them that way even where a compiler would let you cheat.
7. Declare capabilities honestly, and declare the same set to `Build` and to every snapshot. See
   [Host capabilities](capabilities.md).
8. Never reuse a handle within a scope. A late report for work the scope tore down is recognized by
   its handle alone.

## Related

- [Hosting the interpreter](../guides/hosting-the-interpreter.md) — a complete host skeleton.
- [Interpreter, not engine](interpreter-not-engine.md) — why the port stops where it does.
- [ADR 0002: The host port is synchronous and command-returning](../adr/0002-the-host-port-is-synchronous-and-command-returning.md)
