# ADR 0005: The library owns a versioned payload format

**Status**: Accepted
**Date**: 2026-08-09

## Context

A host that stores BPMN processes has to serialize them. So does anything that caches a parsed
document, ships one over a wire, or diffs two versions of one.

The tempting answer is that serialization is the host's business: the library provides an in-memory
model, and each consumer persists it however suits them.

That answer does not survive a second consumer. Two hosts then evolve two serializations of the same
BPMN content. Every new construct gets serialized twice, in two codebases, and kept in sync by hand.
The shared library — the thing that exists so BPMN is implemented once — helps with neither.

It does not survive a second *language* either. A design surface that renders BPMN needs the same
shape in TypeScript, and hand-maintained mirrors of a format owned elsewhere drift. When they drift,
a diagram silently renders wrong.

## Decision

`Bpmn.Model` owns and versions the serialization of the model, and publishes a JSON Schema for it as a
build artifact.

Hosts **wrap** rather than replace it. A host that needs to attach its own data does so in an envelope
around the library-owned document:

```json
{
  "hostSpecificThing": [ ... ],
  "definition": { /* library-owned, versioned, schema-described */ }
}
```

The nested document is identical across hosts. The envelope is each host's own business.

Non-.NET consumers generate their types from the published schema rather than mirroring the model by
hand.

## Consequences

There is one BPMN serialization, described by a machine-checkable schema, with its own round-trip
tests that need no host at all. That is a real asset: a documented on-disk BPMN format is something no
other .NET BPMN library ships.

Format changes become a versioned, deliberate act with a schema diff to review, rather than an
incidental consequence of editing a model class.

Consumers that generate from the schema stay in step automatically. Consumers that do not are at least
diffing against something authoritative.

The format is a public contract from the first release. It is versioned for that reason, and it
deserves the same care as the API — a careless field rename is a breaking change for every consumer,
including ones in other languages.

## Alternatives considered

**Leave serialization to hosts.** Simplest, and correct for a library with exactly one consumer.
Rejected because it guarantees divergence the moment there is a second, which is the situation this
library was built for.

**Own the entire persisted document, hosts store it verbatim.** Cleaner in principle, rejected in
practice. Hosts legitimately need to attach their own data — bindings to their work implementations,
their own variable declarations — and none of that belongs in a neutral BPMN format. The envelope
keeps the shared part shared and the host-specific part with the host, which is the same cut the rest
of the design makes.

**Publish a TypeScript types package alongside the NuGet packages.** Better developer experience for
JavaScript consumers, and worth revisiting. Not now: it commits the project to a second registry and a
second release process for a single known consumer. The JSON Schema serves that consumer today and
serves every other language too.
