# ADR 0001: BPMN semantics ship as a host-agnostic library

**Status**: Accepted
**Date**: 2026-08-09

## Context

BPMN 2.0 defines what a process means: how tokens move, when a parallel gateway joins, what an
interrupting boundary event does to the activity it is attached to, in what order compensation
replays. None of that depends on how a particular system runs work.

Everything that makes a workflow engine hard is the opposite. Durability, distribution, idempotency,
retries, timers, correlation, multi-tenancy, migrating running instances across versions, operational
visibility — the BPMN specification says nothing about any of it, and not by oversight. Those are
properties of an execution environment, not of a process notation.

This library was extracted from a working workflow engine, where the BPMN semantics had been built as
an internal module. Measured against that codebase, roughly 2,550 lines of semantics — token
coordination, join accounting, element family classification, gateway and event behaviors, graph
validation — referenced nothing from the host runtime at all. The coupling was concentrated in a
scheduling adapter of 64 lines and a persistence adapter of 89 lines.

That ratio is the whole argument. The semantics were already separable; they had simply never been
separated.

Meanwhile the .NET ecosystem has no usable BPMN library. A survey of 88 C# repositories matching
"bpmn" and 50 NuGet packages found: one full engine, licensed GPL-3.0-or-later; one interchange
library that is MIT but ships as a closed-source binary and hard-depends on `System.Drawing.Common`,
which throws on non-Windows platforms; several vendor clients that transport `.bpmn` files as opaque
blobs without ever parsing them; and no published object model generated from the BPMN XSD. The gap
is real and verifiable.

## Decision

Ship BPMN as a standalone, host-agnostic library. Draw the boundary exactly where the specification
draws it: the library owns what BPMN means, the host owns how anything gets done.

Four packages, every one with zero external NuGet dependencies:

- `Bpmn.Model` — the neutral object model and the versioned on-disk format
- `Bpmn.Interchange` — BPMN 2.0 XML reading and writing over that model
- `Bpmn.Semantics` — the token-semantics interpreter
- `Bpmn.Runtime.InMemory` — a reference host for simulation, analysis, and testing

The interpreter is a pure function. Given a process definition, the current token state, and one
event, it returns the next token state plus a list of commands describing what the host should do. It
holds no clock, performs no I/O, starts no threads, and persists nothing.

The library names no host, depends on no host, and assumes no host. That constraint is enforced
mechanically rather than by convention: an architecture test asserts the shipped assemblies reference
only the base class library and each other, and a CI job rejects host-specific references in code,
identifiers, comments, and literals. The assembly test cannot catch a doc comment; the text check
cannot catch a transitive package edge. Both are needed.

## Consequences

A host must supply more than it would to an embedded engine: run and correlate work, persist an
opaque state document, cancel work subtrees, and deliver a signal to an enclosing scope. That is a
real integration cost, and it is why `Bpmn.Runtime.InMemory` ships as a supported package rather than
a test fixture — nobody should have to implement a host before they can see the library work.

Some hosts cannot do all of it. Rather than degrade quietly, the library refuses definitions whose
constructs a host cannot support; see [ADR 0004](0004-capabilities-refuse-at-graph-build-time.md).

The four-way package split means a consumer who only wants to read and write BPMN XML — a linter, a
converter, a documentation generator, a migration tool — never takes the interpreter into their
dependency closure. That is the largest audience, and the split exists mainly to serve it.

Two independently released artifacts mean version coordination that a single codebase would not need.
That cost is accepted deliberately: a shared implementation that lives inside one host's repository
would couple every consumer to that host's release train, which is the outcome this decision exists
to prevent.

## Alternatives considered

**Keep the semantics inside the host and publish nothing.** Cheapest, and it satisfies internal
architectural tidiness completely — a negative-dependency architecture test enforces a clean boundary
just as well in a monorepo. It fails only the thing that actually motivates this: a second,
independent consumer cannot adopt code that belongs to another product's release cycle.

**Ship a full engine, with persistence and scheduling included.** Rejected. It would force opinions
about persistence, serialization, scheduling, and concurrency onto hosts that already have their own
and would fight them. It would destroy the zero-dependency property. And it would turn infrastructure
into a competitor: nobody building a workflow engine takes a dependency on a rival engine, whereas a
correct, neutral semantics library is something they might.

**Ship only the interchange layer and keep the interpreter private.** Tempting, because interchange
carries no design risk and holds the larger audience. Rejected because the interpreter is the part
that cannot be reproduced from the specification in an afternoon, and withholding it would leave the
ecosystem gap exactly where it was.
