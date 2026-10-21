# ADR 0010: Work callbacks echo the started token

**Status**: Proposed (issue #59)
**Date**: 2026-10-19

## Context

A non-interrupting event subprocess can activate twice while both bodies remain live. Multiple
arrivals can also start the same ordinary task more than once. Each start already carries a distinct,
scope-local `StartWork.TokenId`, but callbacks and live snapshots identify only a binding and an
optional multi-instance iteration. Two ordinary invocations therefore share the same null-iteration
slot. Completion and fault resolution become ambiguous; signal and teardown lookup can select the
wrong sibling. Restricting one live unit per slot contradicts the supported repeated-token semantics.

## Decision

Hosts retain the token ID from each start and echo it in a nullable, init-only `WorkTokenId` property
on `BpmnLiveWork`, `BpmnWorkCompletedRequest`, `BpmnWorkFaultedRequest`, and
`BpmnWorkSignalledRequest`. Existing positional constructors and deconstruction remain unchanged.
The reference host retains and echoes it on its work ledger and every corresponding report.

The ID is opaque to hosts and local to the scope that issued the start. It is not the token of a
nested scope, a host handle, an iteration ID, or the handle of work that produced this start.
`InvocationCorrelation` continues to describe how the receiving scope itself was started and must
never be overwritten with callback data.

Resolution with an explicit ID selects that active-work record and verifies the binding and any
reported iteration. Unknown, blank, or contradictory identity fails deterministically; it never
falls back to a sibling. Reports without an ID retain the legacy lookup only when the binding and
reported iteration identify at most one record. Ambiguity is refused for signals as well as terminal
reports. A legacy signal resolved to live work must match its live handle, so an old handle cannot
act on a sole replacement invocation. ID-less late-signal behavior remains compatible when no active
matching slot remains. For
recognized escalations, an explicit ID opts into strict active-work and live-handle validation: a
report after that invocation ended is refused, rather than guessed from another invocation. Unknown
signal codes retain their existing diagnostic pass-through and do not resolve identity. The reference host absorbs signals
for already-retired handles before calling the interpreter, as it does for late terminal callbacks.

Live-work lookup uses the explicit token ID to identify a teardown handle. An identity-less legacy
snapshot is usable only when its slot maps unambiguously to the intended invocation. Duplicate
handles/IDs or conflicting identity/slot evidence must not silently overwrite a map entry. A live
sibling on the same binding/iteration is legitimate when it has a different explicit token ID.
Legacy slot ambiguity is checked when a callback or selective teardown needs that slot, not merely
because an unrelated callback carries the snapshot. Unrelated work can continue while the host
migrates. If an interruption needs an ambiguous legacy handle, evaluation refuses rather than
returning a cancellation that silently omits physical teardown. Whole-scope terminate cleanup
continues to cancel every distinct reported handle without resolving token identity.
The live ledger is correlated with the active-work records at evaluation entry. This correlation
remains valid after the evaluation cancels or replaces work internally; commands have not yet reached
the host. Terminal reports must already have left the live ledger; explicitly identified escalation
signals must still name live work.

The host handle continues to identify physical work and is never reused within a scope. Known
stopped-handle completion/fault absorption runs before active-work resolution, remains one-shot,
and cannot mutate a replacement or live sibling. If a stopped-handle report supplies an ID that does
not match the stopped token, it is rejected before clearing the one-shot marker. An ended scope retains
its existing absorption rules. No new persistence state or state format is required: active work already
stores its token ID.

## Consequences

Hosts gain a small additive obligation to support concurrent ordinary work: persist one ID they
already receive and echo it. Older callers remain valid for unambiguous scenarios, but cannot safely
run concurrent same-slot work without the new field. The reference host demonstrates the complete
contract; direct interpreter tests cover contradictions, missing identity, late callbacks, signals,
and cancellation independently of that host.

The implementation must preserve ordered command application, synchronous pure evaluation, and
handle-based teardown. It must be checked together with the separate producing-handle and canceled
token retention changes before release. This decision does not change scheduling, multi-instance
values, callback delivery guarantees, or promise exactly-once external effects.

## Alternatives considered

- Reuse iteration IDs for every invocation: rejected because ordinary token work is not a
  multi-instance loop and must not inherit fabricated iteration values or scopes.
- Reuse scope invocation correlation: rejected because it belongs to the enclosing invocation and
  can name a coincident token in the wrong scope.
- Use producer provenance: rejected because one producer can start many independent units, and
  starts that have no producer still need identity.
- Select the first matching slot, or serialize all handlers: rejected because each changes valid
  process semantics or silently targets a sibling.
- Add a host-start acknowledgement and persist opaque handles in interpreter state: viable but adds
  a new protocol round trip and recovery surface when the existing deterministic token already
  supplies the needed identity.
