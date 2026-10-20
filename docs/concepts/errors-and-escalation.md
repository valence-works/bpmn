# Errors and escalation

BPMN has five distinct ways for something to go other than the happy path, and they are not
interchangeable. Choosing the wrong one produces a model that is technically valid and semantically
wrong, and the mistake is easy to make because most of them look similar on a canvas.

This page covers what each one means, how errors propagate, and where the library's responsibility
ends.

## Error

An **error** means an activity failed. It is BPMN's exception.

An error is declared at the document root and identified by a code:

```xml
<error id="CreditDeclined" name="Credit declined" errorCode="CREDIT_DECLINED" />
```

In the model, that is a `BpmnErrorDeclaration` with `Id`, `Name` and `ErrorCode`. The code is what
matching happens on, never the name. From 0.4.0 an `<error>` without an `errorCode` uses its `id` as
the code ([ADR 0012](../adr/0012-hosts-and-error-end-events-name-bpmn-errors.md)).

A host reports that work failed; it does not name the error:

```csharp
interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
    graph, state, snapshot, work.BindingRef, work.Handle, "Credit limit exceeded")
    { FaultedIterationId = work.IterationId });   // null unless a multi-instance instance failed
```

Today an error catcher catches any reported failure, whatever code it declares. The host supplies a
human-readable message, and no code. [ADR 0012](../adr/0012-hosts-and-error-end-events-name-bpmn-errors.md)
changes this in 0.4.0. The host names the error its work raised, error end events throw, and a
catcher that declares a code catches only that code. For a call activity, the child's error code is
reported only with the `Faulted` outcome; `DispatchFailed` and `Cancelled` are uncoded failures of the
call itself and reach only catch-alls.

### Error boundary events are always interrupting

An error boundary event attached to an activity catches an error thrown by that activity, terminates
it, and routes a token down the boundary path.

**This is not configurable.** BPMN fixes `cancelActivity` to `true` for error boundary events, and
the model documents it as fixed. There is no such thing as a non-interrupting error boundary event,
and there is no coherent meaning for one: the activity has failed, so there is nothing left to
continue alongside.

An error boundary event without an `errorRef` is a catch-all and matches **any** error. From 0.4.0, a
boundary event whose `errorRef` resolves to a code matches only that code (ADR 0012); until then it
matches any error too. Leaving the `errorRef` off is a catch-all, and like every catch-all it is
occasionally what you want and usually not.

### Error start events in event subprocesses

The other catcher is an event subprocess whose start event carries an error event definition — an
element with `triggeredByEvent: true` whose start trigger is an error.

The difference from a boundary event is scope. A boundary event catches errors from the one activity
it is attached to. An error event subprocess catches errors raised anywhere in the scope that
contains it.

Error-triggered event subprocesses are dormant catchers: unlike message, signal and timer triggers,
they need no armed listener, so their `ListenerBindingRef` is null. Nothing has to be running for
them to catch.

### Propagation

An error that is not caught where it is thrown moves **up the scope hierarchy** until something
catches it.

1. Work faults.
2. Does an error boundary event on the failing activity match? If so, the activity is terminated and
   the boundary path runs. Done.
3. Does an error event subprocess in the enclosing scope match? If so, it activates. Done.
4. Otherwise the scope itself fails, and step 2 repeats one level up, with the enclosing activity as
   the failing one.
5. If it reaches the root uncaught, the process fails.

The returned `BpmnEvaluation` reports how BPMN handled a host-reported failure in its nullable
`ErrorDisposition`:

- `Caught(CatchingElementId, ErrorCode?)` — a catcher in this scope matched and this evaluation
  returned without fault. Recovery work may still be running. A null code means the catcher is a
  catch-all.
- `Propagated` — the final continuation is always a `Fault`. No catcher may have matched, or a
  catcher's route faulted during this evaluation, so the failure has gone up.
- `Absorbed` — the failure belonged to work or a scope that had already stopped.

This applies both to `OnWorkFaulted` and to `OnWorkCompleted` when a call activity reports the
failure outcomes `Faulted`, `DispatchFailed`, or `Cancelled`. Those outcomes enter the same catcher
routing ladder. An ordinary start, completion, signal, transaction cancellation, or event subprocess
activation on another path leaves `ErrorDisposition` null. A transaction's `Cancelled` completion
does not mean the same thing as a call activity's `Cancelled` failure outcome.

Steps 2 and 3 work the same in a nested scope as at the root. The interpreter finds the failing
activity from the binding ref and iteration id you report, never from the scope's invocation
correlation, whose token id names a token of the enclosing scope.

Because a scope's interpreter has no view of the tree above it, propagation across a scope boundary
is your host's routing job. `HasEnclosingScope` and `InvocationCorrelation` on the snapshot are how
the interpreter learns whether that is even possible.

## Escalation

An **escalation** means something needs attention. It does not mean the activity failed.

That single difference produces every other one:

| | Error | Escalation |
| --- | --- | --- |
| Means | The activity failed | Something needs attention |
| Boundary event may be non-interrupting | No — always interrupting | Yes |
| The activity keeps running | Never | Yes, if the boundary is non-interrupting |
| Uncaught at the root | The process fails | Nothing happens |
| Matched on | `errorCode` | `escalationCode` |

The canonical use is a threshold. An order above a value needs a manager to look at it, but the order
should keep being processed while they do. That is a non-interrupting escalation boundary event: the
escalation path runs alongside the still-running activity. Modeling it as an error would kill the
order.

Escalations are declared as `BpmnEscalationDeclaration` at the root with an `EscalationCode`, and the
code lives in an escalation event definition's `Code` property. A throwing escalation event
**requires** a code — a throw must say what it is escalating. An escalation boundary event's code is
**optional**, and a code-less boundary is the catch-all that matches any escalation whose specific
code no other boundary on the same host claims.

### Uncaught escalation is not a failure

An escalation that reaches a scope with nothing to catch it is a no-op. The interpreter records a
diagnostic — `EscalationUnhandled` — and continues. A root-process escalation throw with no catcher
behaves the same way.

There is a second no-op worth knowing: `EscalationLate`, recorded when an interrupting escalation
boundary matches a notification whose host activity had already finished. Nothing to interrupt,
nothing to do.

Both look alarming in a trace and neither is a fault. This is the single most common
misreading of an escalation trace.

### Escalation crosses scopes by signalling

When an escalation must leave the scope it was raised in, the interpreter emits a
`SignalEnclosingScope` command. Your host routes it to the immediate parent scope and delivers it
there through `OnWorkSignalled`.

There is exactly one signal code for this — `BpmnInterpreter.EscalationSignalCode`, the string
`bpmn.escalation` — with the escalation's identity travelling in the payload. A host multiplexing
several kinds of scope signal reserves one code rather than tracking a growing namespace.

The payload shape is part of the contract. `BpmnInterpreter.EscalationPayload(code, name?)` builds it,
and `BpmnInterpreter.TryReadEscalationCode(payload, out code)` reads it the way the interpreter matches
it. A host that originates an escalation from outside the process, such as an operator action or an
external system, delivers `EscalationSignalCode` with a payload from `EscalationPayload` through
`OnWorkSignalled`, as if the work had escalated. A payload with no readable code matches no catcher.

This is what the `ScopeSignalling` capability promises, and a host that does not declare it will be
refused a definition that needs it — see [Host capabilities](capabilities.md).

## Cancel

**Cancel is transaction-only.** A cancel end event is valid only inside a transaction subprocess —
an element with `isTransaction: true` — and a cancel boundary event is valid only attached to one.

The sequence, when a cancel end event fires:

1. All other live work in the transaction stops, and is torn down on the host — a cancelled
   transaction leaves nothing running behind it. Work the same evaluation was about to start, on a
   branch that reached a task before the cancel end event, is withdrawn instead and never starts.
2. The transaction's registered compensations replay, in reverse order.
3. The scope completes with the `Cancelled` outcome, which is distinct from ordinary completion.
4. A cancel boundary event on the transaction, if present, routes the cancellation path in the parent.

Cancel is not a general-purpose abort. Outside a transaction it has no meaning, and the graph builder
will say so. Reach for a terminate end event or an interrupting boundary event instead.

## Compensation

**Compensation undoes work that already succeeded.** It is not error handling; it is the "actually,
put that back" path, and it typically runs *because of* error handling elsewhere.

The mechanism has three parts:

1. A **compensation handler**: a task or subprocess with `isForCompensation: true`. It participates
   in no sequence flows and is never reached by normal token flow.
2. A **compensation boundary event** on the activity to be undone, carrying
   `CompensationHandlerElementId` pointing at that handler.
3. A **compensate throw or end event** that triggers the replay. Its `ActivityRef` property, if set,
   compensates only that element's registrations; if absent, everything registered in the process.

Registration is automatic and ordered. When an activity carrying a compensation boundary completes
successfully, a `BpmnCompensable` entry is written to the state's compensation log. Replay runs in
**reverse registration order** — the last thing done is the first thing undone.

Two properties of that log are worth knowing. Entries are **never pruned**, including after they have
been compensated, so the record stays available for determinism, audit, and double-replay protection.
And a claimed entry whose run is torn down before it executed is **released** back to registered, so
a cancelled compensation run does not silently consume the registrations it never ran.

Compensation only applies to work that **completed successfully**. Work that failed was never
registered; there is nothing to undo.

## Terminate

A **terminate end event** ends the process immediately, discarding every remaining token. No
compensation, no boundary paths, no outcome negotiation. The state records `Terminated`, and late
child completions are ignored.

Every unit of work still running in the scope is torn down on the host, so a terminated process
leaves nothing running behind it. A unit that the same evaluation already tears down for a more
specific reason keeps that reason, such as the losing branch of an event-based gateway or a boundary
listener retired by its host's completion. Every other unit is torn down with the reason
`bpmn.terminate.stopped-live-work`. Work the same evaluation was about to start is never started:
the interpreter withdraws its `StartWork` and completes at once, rather than starting work only to
stop it.

A fault ends the scope at once in the same way, withdrawing work it was about to start, but it
carries no teardown. A fault is a verdict, not a command, so stopping what a faulted scope still has
running is the host's half of the fault.

It is the bluntest instrument BPMN has. Use it when "stop, right now, everywhere" is genuinely the
requirement, and something more specific otherwise.

## Choosing between them

| You want to say | Use |
| --- | --- |
| This activity failed | Error |
| Something needs attention, keep going | Escalation, non-interrupting |
| Something needs attention, stop this activity | Escalation, interrupting |
| Abandon this transaction and undo its work | Cancel end event, inside a transaction |
| Undo work that already succeeded | Compensation |
| Stop the entire process now | Terminate |

## Incidents are not a BPMN concept

BPMN has no notion of an incident. There is no incident element, no retry policy, no dead letter, no
operator queue, no alerting, no "failed job". The specification is silent on all of it, because those
are properties of an execution environment and not of a notation.

This is deliberate on the library's side too. Fault records are the host's business, and there is no
default policy waiting to surprise you.

What the library gives you instead is the one piece of information you cannot compute yourself:
**whether BPMN absorbed the failure**.

```csharp
var evaluation = interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
    graph, state, snapshot, work.BindingRef, work.Handle, message) { FaultedIterationId = work.IterationId });

if (evaluation.ErrorDisposition is BpmnErrorDisposition.Propagated)
    _incidents.Record(work, message);   // your model, your policy
// Caught: the process modeled this failure. Absorbed: this work or scope had already stopped.
// Null: no host-reported failure was routed or absorbed in this evaluation.
```

Recording an incident for every fault buries the real ones under the modeled ones — an error boundary
event that fires ten thousand times a day is a designed path, not ten thousand incidents. Recording
none loses the failures that escaped the model entirely. The disposition is the line between the two,
and drawing it is a decision only your host can make.

## Related

- [The host port](host-port.md) — where the disposition comes from.
- [Host capabilities](capabilities.md) — why escalation needs `ScopeSignalling`.
- [Supported BPMN constructs](../reference/supported-constructs.md) — which of these are executable
  today.
- [Hosting the interpreter](../guides/hosting-the-interpreter.md) — handling faults in a host.
- [ADR 0003: Faults are a verdict, not a command](../adr/0003-faults-are-a-verdict-not-a-command.md)
