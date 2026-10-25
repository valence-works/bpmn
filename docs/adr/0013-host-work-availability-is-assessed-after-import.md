# ADR 0013: Host work availability is assessed after import

**Status**: Accepted
**Date**: 2026-10-25

## Context

A work binding declares what an element requires. Neither a non-null `BindingRef` nor an
`UnboundTask` descriptor establishes whether a particular host can perform it. Rejecting every
`UnboundTask` would discard valid task boundaries and compensation handlers supplied by a host.
Import fidelity and host readiness answer different questions.

## Decision

Keep import behavior and the existing import records unchanged. After constructing its work
inventory, a host may call `BpmnWorkAvailabilityValidator.Assess(imported.Bindings, classifier)`.
The classifier returns `Unknown`, `Available`, or `Unavailable` for each descriptor. The assessment
retains every descriptor and status in order, including process, element, binding ref and slot.
Known unavailable work receives a library-generated diagnostic. `AllWorkAvailable` is true only
when every supplied requirement is available; no requirements is a valid empty assessment.

Assessment never removes elements, bindings, associations, boundaries or flows. It neither adds
implementations nor enforces an execution policy. The host decides whether to proceed and must
reassess when its inventory changes. Availability does not establish graph validity, capability
support, durability or successful execution.

Also reconcile escalation validation with the reader: an escalation boundary may attach to a
Sub-Process or Call Activity, with the existing distinct-code and single-catch-all rules. Ordinary
task hosts remain rejected. An imported ordinary Task can be a compensation handler when its host
supplies the work. Existing Call Activity compensation behavior is unchanged; this issue qualifies
only ordinary-task handlers.

These two composition rules follow [BPMN 2.0.2](https://www.omg.org/spec/BPMN/2.0.2/PDF): section
10.3.6, printed page184, propagates called-process escalations to the Call Activity boundary;
section10.7.1, printed page302, permits a Task or Sub-Process outside normal flow as a compensation
activity. Import-to-execution tests cover both escalation interruption modes and ordinary-task replay.

## Consequences

Hosts can report missing implementations without losing authored behavior. Unknown work remains
visible and cannot masquerade as an all-available assessment. The helper uses an explicit host
classifier rather than inferring implementation from a binding subtype. Classifier exceptions
propagate; undefined classifier values are rejected. Diagnostics carry a generic explanation,
while structured binding identity lets a host present its own inventory details.

The additive API and Call Activity validation change belong to unreleased main after v0.4.0.
Existing import constructors, record equality, binding JSON and execution-state schemas are unchanged.
The reference-host execution witnesses do not qualify a production host's transport or persistence.

## Alternatives considered

- Reject `UnboundTask` during import: breaks supported host-supplied work and round trips.
- Add an import-time resolver: mixes environment readiness with source-fidelity findings and may
  run before the host can construct its inventory from the returned bindings.
- Return only unavailable findings: loses the distinction between all-unknown and all-available.
- Build a provider framework: unnecessary for a synchronous classification over existing descriptors.
