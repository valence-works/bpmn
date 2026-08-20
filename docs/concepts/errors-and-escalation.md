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
matching happens on — not the id, not the name.

A host reports that work failed; it does not name the error:

```csharp
interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
    graph, state, snapshot, work.BindingRef, work.Handle, "Credit limit exceeded"));
```

Which error a catcher matches is a property of the model, not of the report. An error boundary event
declares the code it catches, and a code-less one catches anything. The host supplies a
human-readable message; BPMN supplies the matching.

### Error boundary events are always interrupting

An error boundary event attached to an activity catches an error thrown by that activity, terminates
it, and routes a token down the boundary path.

**This is not configurable.** BPMN fixes `cancelActivity` to `true` for error boundary events, and
the model documents it as fixed. There is no such thing as a non-interrupting error boundary event,
and there is no coherent meaning for one: the activity has failed, so there is nothing left to
continue alongside.

An error boundary event with no code matches **any** error. A boundary event with a code matches only
that code. Leaving the code off is a catch-all, and like every catch-all it is occasionally what you
want and usually not.

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

The interpreter reports which of these happened, as a `BpmnErrorDisposition` on the
`BpmnFaultEvaluation` returned by `OnWorkFaulted`:

- `Caught(CatchingElementId, ErrorCode?)` — a catcher in this scope matched. From BPMN's point of
  view nothing is wrong; the model anticipated this. A null code means the catcher is a catch-all.
- `Propagated` — nothing here matched. The continuation is a `Fault` and the failure has gone up.

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

This is what the `ScopeSignalling` capability promises, and a host that does not declare it will be
refused a definition that needs it — see [Host capabilities](capabilities.md).

## Cancel

**Cancel is transaction-only.** A cancel end event is valid only inside a transaction subprocess —
an element with `isTransaction: true` — and a cancel boundary event is valid only attached to one.

The sequence, when a cancel end event fires:

1. All other live work in the transaction stops, and is torn down on the host — a cancelled
   transaction leaves nothing running behind it.
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
    graph, state, snapshot, work.BindingRef, work.Handle, message));

if (evaluation.Disposition is BpmnErrorDisposition.Propagated)
    _incidents.Record(work, message);   // your model, your policy
// Caught: the process modeled this failure. Stay quiet.
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
