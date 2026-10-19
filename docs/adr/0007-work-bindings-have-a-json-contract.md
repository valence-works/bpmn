# ADR 0007: Work bindings have a JSON contract

**Status**: Accepted
**Date**: 2026-10-17

## Context

A process document's nested subprocess bodies and call options live in `BpmnWorkBinding`, outside
`BpmnDefinitions`. Transferring definitions alone therefore loses content a client needs to edit and
a writer needs to reproduce. The binding records had no published polymorphic JSON contract, so a
host could not transfer definitions plus bindings using the library's own format.

## Decision

Extend the library-owned payload format from ADR 0005 to cover `BpmnWorkBinding` and its seven concrete
cases. The additive format revision is **1.2.0**; the existing definitions and execution-state shapes
stay unchanged.

Bindings use explicit camelCase property names and a `kind` discriminator with these stable values:
`timerWait`, `messageWait`, `signalWait`, `messagePublish`, `callProcess`, `nestedProcess`, and
`unboundTask`. The shared fields are `processId`, `elementId`, `bindingRef`, and `slot`. The slot keeps
the format's numeric enum convention: `0` is Primary and `1` is ScopeListener.

Use the base binding type, or a collection of that type, with `System.Text.Json`'s default options.
Unknown discriminator values are rejected rather than silently losing binding-specific data. A host
continues to own the envelope containing `definitions` and `bindings`; no new host document type is
introduced. Nested definitions reuse the existing model contract, including retained extensions.

The default serializer writes `kind` first and expects discriminator metadata before ordinary
properties when reading. Clients rebuilding JSON should preserve that position; JSON Schema cannot
enforce property order. This follows the
[System.Text.Json polymorphism contract](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/polymorphism).

The generated schema adds an addressable work-binding root and a discriminated union covering every
concrete case. The build-time generator may reference `Bpmn.Interchange` to reflect that contract;
`Bpmn.Model` acquires no runtime reference to interchange. The schema remains packaged at its existing
location, so clients resolve definitions and bindings from one format artifact.

## Consequences

A document can pass through a JSON client and back to the XML writer without dropping nested process
bodies or call options. Serializer round trips and schema checks cover each binding case, both slots,
and a nested-document XML round trip.

This freezes a new wire surface. Future binding kinds and property changes require deliberate format
versioning and a reviewed schema diff. The previous uncontracted CLR/PascalCase binding JSON is not an
alternate accepted format; callers should use the published 1.2 contract. Existing 1.0/1.1 definitions
and execution-state documents remain valid under the expanded schema.

## Alternatives considered

**Let each host invent a discriminator and names.** Rejected for the same reason as ADR 0005: each
consumer would duplicate the library's format and lose interoperability.

**Move the binding types into Bpmn.Model.** Unnecessary for JSON support and disruptive to the public
API. A build-time reference lets the schema cover them without moving types or reversing the runtime
dependency direction.

**Introduce a new document-envelope class.** Hosts already need different envelopes. Publishing the
binding component closes the content gap while retaining that boundary.
