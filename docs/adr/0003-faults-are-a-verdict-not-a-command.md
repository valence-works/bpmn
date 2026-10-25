# ADR 0003: Faults are a verdict, not a command

**Status**: Accepted; amended 2026-10-15 for 0.4.0; error codes amended by
[ADR 0012](0012-hosts-and-error-end-events-name-bpmn-errors.md)
**Date**: 2026-08-09

ADR 0012 supersedes this record's position that a fault request carries no error code. In 0.4.0
the host names the BPMN error its work raised, error end events throw, and a coded catcher catches
only its own code. The verdict described here is unchanged.

## Current decision: 0.4.0 amendment (2026-10-15)

The owner amended this decision for the breaking 0.4.0 release. The verdict remains the
interpreter's report of what BPMN did, rather than a command to update host-owned failure records.
The report now belongs to the common `BpmnEvaluation` shape so a host can handle every evaluation
uniformly:

```csharp
public BpmnErrorDisposition? ErrorDisposition { get; init; }
```

`BpmnFaultEvaluation` is removed. `ErrorDisposition` is populated whenever a host-reported failure
is routed or absorbed, and, since ADR 0012, whenever an error end event throws. Routing includes call-activity `Faulted`, `DispatchFailed`, and `Cancelled`
outcomes handled by `RouteCallActivityFailureOutcome`; absorption covers late failures of stopped
work or scopes. It remains `null` when no failure was routed or absorbed, including transaction
cancellation and an event subprocess activated on another path.

The disposition is a closed set:

- `Caught(CatchingElementId, ErrorCode?)` names the catcher that took the error in an evaluation
  that returned without fault; recovery work may still be running. A null code means the catcher
  was a catch-all.
- `Propagated` means the final evaluation continuation is `Fault`: either no catcher took the error,
  or a catcher's route faulted during this evaluation.
- `Absorbed` means the failure belonged to work or a scope that had already stopped.

The transcript's `BpmnTranscriptEntry.Disposition` remains unchanged. This amendment changes the
evaluation property to `ErrorDisposition`; it does not rename or alter the transcript field.

The original decision below records why the command was removed and why incident policy belongs to
the host. Its choice to put the verdict on a fault-only derived evaluation is superseded by this
amendment.

## Original decision (2026-08-09; superseded in part for 0.4.0)

### Context

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

### Decision

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

### Consequences

The concept is gone from the library, not renamed. No type, parameter, or comment refers to incidents.

`OnWorkFaulted` returns strictly more than the other three entry points. Rather than adding a nullable
field that is meaningless on three of four paths, it returns a derived type, so the common shape stays
honest and the extra information is only present where it exists.

Adoption gets easier. "Can your host resolve a failure record by id?" is a question many hosts cannot
answer cleanly. "What does your host do when BPMN catches an error?" is answerable by every host,
including the answer "nothing".

The disposition carries `ErrorCode` so the shape did not change when code-matched catching arrived.
ADR 0012 closes that gap: a catcher now matches by code, exact before the catch-all.

### Alternatives considered

**Keep the command behind a capability flag.** Rejected. The flag existed only to paper over a concept
that did not belong in the library. Removing the concept removes the need for the flag.

**Drop it entirely and let hosts infer from the continuation.** Rejected. "The process continued" and
"the error was caught" are not the same proposition — a process can continue for reasons unrelated to
error routing — and leaving hosts to infer it would produce divergent behavior across hosts for no
saving.
