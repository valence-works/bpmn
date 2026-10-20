# ADR 0012: Hosts and error end events name BPMN errors

**Status**: Accepted
**Date**: 2026-10-20

Amends [ADR 0003](0003-faults-are-a-verdict-not-a-command.md) and
[the host port](../concepts/host-port.md), which both say the fault request deliberately carries no
error code. The decision ships in 0.4.0, and the matching rule in it is a breaking change.

## Context

BPMN names errors. An `<error>` declares an `errorCode`, and an event refers to it by `errorRef`. An
error boundary event or an error event subprocess that names an error catches only that error. One
that names none is a catch-all. An error end event throws the error it names, and the error travels up
the scope hierarchy until a matching catcher stops it.

The library does none of this. Every error catcher behaves as a catch-all. `BpmnWorkFaultedRequest`
has no error code, on purpose: ADR 0003 and the host-port page say "which BPMN error a catcher matches
is a property of the model, not something the host names". A process cannot raise an error either. An
error end event reads as a plain end event with a `Degraded` finding. Graph validation allows one
error boundary per host and one error event subprocess per scope, because without matching a second
catcher could never fire.

That position confused two separate things. Which **catcher** an error reaches is a property of the
model. Which **error** was raised is a fact about the run, and only whatever raised it knows that.
When bound work fails, that is the host. When a scope ends at an error end event, it is the model.
With nothing able to name the error, the codes in a document are decorative. A process that handles
"credit declined" and "service unavailable" differently cannot be expressed, and a document that
throws errors has to be rewritten before it runs.

ADR 0003 reserved `Caught.ErrorCode` "so the shape does not change when that gap closes". This record
closes it.

## Decision

### Matching

- **Strict matching.** A catcher with a code catches only an error with that code. An error with no
  code reaches only catch-alls, meaning catchers with no `errorRef`.
- **Exact match first, then the catch-all.** On one host, a boundary declaring the error's code wins
  over the host's catch-all boundary. In one scope, the same holds for event subprocesses.
- **The code of an `<error>`** is its `errorCode`, or its `id` when it declares none. The reader
  resolves an `errorRef` to that code. An `errorRef` naming no declaration is its own code.
- **Cardinality.** A host may carry several error boundaries, and a scope several error event
  subprocesses, provided their codes are distinct and there is at most one catch-all each. This is
  the escalation rule, applied to errors.

### The host names the error its work raised

```csharp
public sealed record BpmnWorkFaultedRequest(/* unchanged */)
{
    public string? ErrorCode { get; init; }
}

public sealed record BpmnWorkCompletedRequest(/* unchanged */)
{
    public string? ErrorCode { get; init; }
}
```

`BpmnWorkFaultedRequest.ErrorCode` is the BPMN error the failed work raised, or `null` when the host
cannot name one. The message stays what it was: a human-readable description of the failure.

`BpmnWorkCompletedRequest.ErrorCode` matters only alongside a call activity's `Faulted` outcome. It
lets the error a called process raised reach the caller's catchers. The interpreter ignores it on any
other completion. `DispatchFailed` and `Cancelled` are failures of the call itself, so they carry no
error of the callee and reach only catch-alls.

### The fault ladder

A work fault, and a call activity's failure outcome, try three tiers in order:

1. An error boundary on the failing activity: the exact code, else the catch-all.
2. An error event subprocess in the scope: the exact code, else the catch-all.
3. Otherwise the scope faults with `bpmn.work.faulted`, or with the call activity's per-outcome code,
   and the error code is preserved on the fault.

```csharp
public sealed record Fault(string Code, string Message) : BpmnContinuation
{
    public string? ErrorCode { get; init; }
}
```

`Fault.Code` stays the library's reason the scope stopped. `Fault.ErrorCode` is the BPMN error that
caused it, when there is one. Faults that are not BPMN errors carry no `ErrorCode`. That covers
`bpmn.propagation.limit`, `bpmn.gateway.no-flow-selected`, and a transaction cancelled with no
cancel boundary.

`Caught(CatchingElementId, ErrorCode)` keeps its meaning: the code the catcher declares, or `null`
when the catcher is a catch-all.

### Error end events throw

An error end event becomes executable. Its behavior returns a new `ThrowError` behavior command, which
consumes the token and then:

1. Checks the scope's own error event subprocesses: the exact code, else the catch-all. A match
   activates the subprocess, which is always interrupting, and the evaluation reports `Caught`.
2. Otherwise ends the scope at once with `Fault(bpmn.error.thrown) { ErrorCode }`. Like every fault,
   it withdraws work the evaluation was about to start. The evaluation reports `Propagated`, because
   its continuation is a `Fault` and no catcher in the scope took the error.

The fault code is a public constant, `BpmnInterpreter.ErrorThrownFaultCode`. An error end event
without an `errorRef` throws an error with no code, which only a catch-all catches.

Own-scope escalation already works this way, and is the model for this path. Two differences follow
from BPMN. An error is always interrupting. An uncaught error fails its scope, where an uncaught
escalation is a no-op.

### Crossing scopes is the host's job

A scope's interpreter cannot see the scope above it, so it cannot route an error there. That is
already true of work faults and of escalation. When a child scope ends with a `Fault` carrying an
`ErrorCode`, the host reports that code with the parent's fault: as `BpmnWorkFaultedRequest.ErrorCode`
for an embedded subprocess, or as `BpmnWorkCompletedRequest.ErrorCode` with the `Faulted` outcome for
a call activity. The parent then runs the same ladder.

The reference host, `Bpmn.Runtime.InMemory`, does exactly this, so the behavior is tested end to end.
No new command or capability is needed. A fault is a verdict, not a command (ADR 0003), and the host
already acts on that verdict.

### Trace

Two diagnostic kinds are added. `ErrorThrown` records an error end event firing, with its element and
code. `ErrorCaught` records a catcher taking an error, with the catcher and the code it matched. The
new kinds are an additive revision of the execution-state payload format
([ADR 0005](0005-the-library-owns-a-versioned-payload-format.md)).

### Order of delivery

The decision ships in two steps after this record. The interchange step comes first. It resolves an
`errorRef` to its code on the event definition, keeps error end events in the model and writes them
back, and fixes `Caught.ErrorCode`, which was always `null` for a document read from XML. Until the
interpreter step lands, `BpmnGraph.Build` refuses a definition containing an error end event, so it
never runs as a plain end event. The interpreter step then adds the request and fault fields, strict
matching, the ladder, `ThrowError`, the diagnostics, the cardinality change, and the reference host's
pass-through.

## Consequences

Error codes mean something. A process can handle different errors differently, the codes in a
document written by a modeling tool are honored, and a document that throws errors runs without a
rewrite.

**Strict matching breaks existing hosts.** Today a coded catcher catches any fault. After this change
it catches only its own code. A host that reports no codes will see a fault that a coded catcher used
to take propagate past it. The same is true of a call activity's `DispatchFailed` and `Cancelled`
outcomes. This is the correct BPMN reading, and it is a 0.4.0 release-note item. A host that wants the
old behavior has two options: report the code its catchers expect, or model a catch-all.

The host port grows: an optional field on two requests and one on the fault continuation. All three
are init-only and default to `null`, so a host that never names an error compiles and runs unchanged,
apart from the matching change above.

A host that crosses scopes takes on one more duty: copy `Fault.ErrorCode` into the parent's request.
A host that forgets loses the code and degrades every crossing error to an uncoded one, which only a
catch-all catches. The reference host and the hosting guide show the pass-through. The library cannot
enforce it, for the same reason it cannot route the fault itself.

The disposition is still about one scope. An error that propagates out of a child is `Propagated`
there, and is `Caught` or `Propagated` again in the parent's own evaluation. A host that records an
incident for every `Propagated` evaluation records the child's even when the parent catches it. That
is true of nested work faults already. Deciding what counts as an incident stays the host's job.

## Alternatives considered

**Keep the code-less port and match only codes the model can know.** Error end events alone can name
an error from the model. Rejected, because then a failed task, the most common source of an error,
could never reach a coded catcher, and every coded boundary on a task would be dead. This is the
position ADR 0003 took, and the reason this record exists.

**Match leniently: a coded catcher also takes uncoded faults.** This would keep today's behavior for
hosts that report no codes. Rejected. It would make a catch-all and a coded catcher indistinguishable
for uncoded faults, so a document with both would be ambiguous. It would also diverge from the
specification and from what modeling tools show their users. The break is cheaper now, in 0.4.0, than
after hosts depend on the lenient reading.

**Let the interpreter route errors across scopes.** This would emit a command to the parent, as
escalation does with `SignalEnclosingScope`. Rejected. An error ends the child scope, so the child's
verdict already is the message. A second, command-shaped copy of the same fact is what ADR 0003
removed once already. Escalation needs a signal because the child keeps running. An error does not.

**Put the code in `Fault.Code`.** This would use `bpmn.error.thrown:CREDIT_DECLINED`, or the BPMN code
itself, as the fault code. Rejected. `Fault.Code` is the library's namespace of reasons a scope
stopped, and hosts switch on it. Mixing user-defined codes into it would make a document able to
forge a library reason, and would force hosts to parse a string to separate the two.

**Keep reading error end events as plain end events until the interpreter supports them.** Rejected,
because a scope that ends at an error end event has failed. Completing it as `Done` is incorrect BPMN,
and [ADR 0004](0004-capabilities-refuse-at-graph-build-time.md) refuses incorrect execution rather
than degrading to it. The interchange step therefore refuses at build instead.
