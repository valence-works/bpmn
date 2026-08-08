# ADR 0003: Faults are a verdict, not a command

**Status**: Accepted
**Date**: 2026-08-09

## Context

When work bound to a BPMN element fails, the host reports the failure and the interpreter decides
whether BPMN routes it: an error boundary event on the activity, an error start event in an event
subprocess, or nothing — in which case the error propagates out of the process.

The codebase this was extracted from had a fourth command for this, alongside start, cancel, and
signal. It asked the host to resolve an *incident* by id, marking the recorded failure as handled so
the host would stop treating it as outstanding.

Two things were wrong with it.

**It was not a command.** The identifier it carried came from the fault callback — the host had
handed that id to the interpreter moments earlier, on the way in. The interpreter did not derive it,
could not have derived it, and did nothing with it except send it straight back with a reason string
attached. The entire information content was *"the failure you just told me about was caught by BPMN
error routing."* That is an answer to a question, not an instruction to perform an action.

**"Incident" is not a BPMN concept.** The specification defines `<error>` with an `errorCode`, error
boundary events (always interrupting — BPMN fixes `cancelActivity` to true for errors), error start
events in event subprocesses, and propagation up the scope hierarchy until a matching catcher stops
it. It defines Escalation as deliberately distinct: non-fatal, possibly non-interrupting. It defines
Cancel for transactions, Compensation for undo, and Terminate for immediate shutdown.

It does not define an incident. An incident is an operational record meaning "a human needs to look at
this" — invented by engine vendors for their operations tooling. Universal among engines, absent from
the standard. A concept that every engine has and the specification lacks is, almost by definition,
on the host's side of this boundary.

## Decision

Delete the command. `OnWorkFaulted` returns a `BpmnFaultEvaluation`, which extends the ordinary
evaluation with a disposition:

```csharp
public abstract record BpmnErrorDisposition
{
    public sealed record Caught(string CatchingElementId, string? ErrorCode) : BpmnErrorDisposition;
    public sealed record Propagated : BpmnErrorDisposition;
}
```

`Caught` names the boundary event or event start event that stopped propagation, and the error code
that matched. `Propagated` means the error left this process unhandled.

The host maps that onto whatever its own failure bookkeeping requires — resolving a record, clearing a
flag, emitting a metric, or nothing at all.

The port drops from four commands to three, and the capability flag that existed to cover hosts
without a failure ledger disappears with it.

## Consequences

The concept is gone from the library, not renamed. No type, parameter, or comment refers to incidents.

`OnWorkFaulted` returns strictly more than the other three entry points. Rather than adding a nullable
field that is meaningless on three of four paths, it returns a derived type, so the common shape stays
honest and the extra information is only present where it exists.

Adoption gets easier. "Can your host resolve a failure record by id?" is a question many hosts cannot
answer cleanly. "What does your host do when BPMN catches an error?" is answerable by every host,
including the answer "nothing".

The disposition carries `ErrorCode` even though code-matched catching is not yet fully implemented —
today a matching error catcher is selected without narrowing on the code. Reserving the field now
means the shape does not change when that gap closes.

## Alternatives considered

**Keep the command behind a capability flag.** Rejected. The flag existed only to paper over a concept
that did not belong in the library. Removing the concept removes the need for the flag.

**Drop it entirely and let hosts infer from the continuation.** Rejected. "The process continued" and
"the error was caught" are not the same proposition — a process can continue for reasons unrelated to
error routing — and leaving hosts to infer it would produce divergent behavior across hosts for no
saving.
