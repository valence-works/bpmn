# BPMN for .NET

A BPMN 2.0 interpreter and interchange library for .NET.

It reads and writes BPMN 2.0 XML over a typed, immutable object model, and it answers one question
about a running process: given this definition, this token state, and this event, what is the next
state and what should the host do about it?

It never does the doing. There is no clock, no I/O, no threads, no dependency injection container,
no persistence. The semantics core is a pure function:

```text
(process definition, current token state, one event) -> (next state, commands for the host)
```

That is the whole design. Everything else follows from it.

- **License**: MIT
- **Target frameworks**: `net8.0`, `net10.0`
- **External NuGet dependencies**: none, in any shipped package
- **Host dependencies**: none — the library names no host, depends on no host, and assumes no host

## The gap this fills

The .NET ecosystem has no maintained, license-clean BPMN library. A survey of 88 C# repositories
matching "bpmn" and 50 NuGet packages found:

- One full workflow engine, licensed GPL-3.0-or-later — unusable in most commercial products.
- One interchange library that is nominally permissive but ships as a closed-source binary and hard-depends
  on `System.Drawing.Common`, which throws on non-Windows platforms.
- Several vendor clients that move `.bpmn` files around as opaque blobs and never parse them.
- No BPMN object model generated from the XSD published on NuGet at all.

So a .NET team that needs to read a `.bpmn` file, understand it, and reason about what it means has,
until now, had to write that themselves or take a license they cannot ship.

## What it is

- A BPMN 2.0 XML reader and writer over a typed, immutable object model.
- **Content-lossless round-trip.** Foreign `extensionElements` (`camunda:*`, `zeebe:*`, `flowable:*`,
  anything else) and BPMN DI layout survive a read-modify-write cycle.
- An **import analyzer** producing element-scoped `Info` / `Degraded` / `Dropped` diagnostics, where
  analyzing and committing share one code path — so the preview cannot disagree with the import.
- A **model builder**: processes can be constructed in code, without XML.
- A **token-semantics interpreter** covering exclusive, parallel, inclusive and event-based gateways;
  start, intermediate and end events; interrupting and non-interrupting boundary events; embedded and
  event subprocesses; multi-instance; compensation; transactions; escalation; cyclic flows.
- **Deterministic and synchronous.** The same four inputs always produce the same output, so a bug
  report can be four JSON documents.

## What it is not

- **Not a production workflow engine.** No durable persistence, no scheduling, no retries, no broker,
  no queue, no distributed coordination.
- **Not an executor.** It emits commands. Something else runs them.
- **Not an expression or script evaluator.** No FEEL, no JUEL, no JavaScript. Sequence-flow conditions
  are resolved by the host.
- **Not DMN or CMMN.**
- **Not a modeler or a renderer.** It preserves diagram layout; it does not draw it.
- **Not a client** for any BPMN vendor's server.
- **Not an XSD validator.** It reports what it could not use, which is a different job.
- **Not byte-exact on round-trip.** Content is preserved; formatting is not.

## Install

```bash
dotnet add package Bpmn.Interchange   # read and write BPMN XML
dotnet add package Bpmn.Semantics     # interpret a process
```

`Bpmn.Model` comes with either of them. Install it alone if you only need the object model.

## A taste

```csharp
using Bpmn.Interchange;

// Read a .bpmn file. Vendor extensions and DI layout survive the trip.
var result = BpmnXmlReader.Read(File.ReadAllText("order.bpmn"));

// Every element the reader could not fully use says so, and says where.
foreach (var issue in result.Analysis.Issues)
    Console.WriteLine($"{issue.Severity} {issue.ElementId}: {issue.Message}");

var process = result.Definitions.FindProcess("order-approval")!;
Console.WriteLine($"{process.Name}: {process.Elements.Count} elements, {process.SequenceFlows.Count} flows");

// Every unit of host work the definition needs, listed up front.
foreach (var binding in result.Bindings)
    Console.WriteLine($"{binding.ElementId} -> {binding.GetType().Name}");

// Nothing ran, nothing was scheduled, nothing was persisted. Write it back out.
File.WriteAllText("order.out.bpmn", BpmnXmlWriter.Write(result.Definitions));
```

## Packages

| Package | What it gives you | Depends on |
| --- | --- | --- |
| `Bpmn.Model` | The neutral object model: processes, flow elements, sequence flows, event definitions, DI layout, retained vendor extensions, and the execution-state records. | nothing |
| `Bpmn.Interchange` | BPMN 2.0 XML reading and writing over that model, with element-scoped import diagnostics. | `Bpmn.Model` |
| `Bpmn.Semantics` | The token-semantics interpreter: a pure, synchronous state transition function. | `Bpmn.Model` |
| `Bpmn.Runtime.InMemory` | A reference host with a virtual clock, for simulation, analysis and testing. Non-durable, single-process, not for production. | `Bpmn.Semantics` |

Every one of them has zero external NuGet dependencies. That is a constraint the build enforces, not
an aspiration.

## Documentation

The full documentation lives in [`docs/`](docs/index.md) and is published to the
[project wiki](https://github.com/valence-works/bpmn/wiki).

- [Getting started](https://github.com/valence-works/bpmn/wiki/guides-getting-started)
- [Reading and writing BPMN XML](https://github.com/valence-works/bpmn/wiki/guides-reading-and-writing-bpmn)
- [Building a process in code](https://github.com/valence-works/bpmn/wiki/guides-building-a-process-in-code)
- [Simulating a process](https://github.com/valence-works/bpmn/wiki/guides-simulating-a-process)
- [Hosting the interpreter](https://github.com/valence-works/bpmn/wiki/guides-hosting-the-interpreter)
- [Interpreter, not engine](https://github.com/valence-works/bpmn/wiki/concepts-interpreter-not-engine)
- [Supported BPMN constructs](https://github.com/valence-works/bpmn/wiki/reference-supported-constructs)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Architectural decisions are recorded in
[`docs/adr/`](docs/adr).

## License and stewardship

MIT. See [LICENSE](LICENSE).

The library is stewarded by **Valence Works**. It was extracted from a working system, but it carries
nothing of that system with it: no host is named, referenced, or assumed anywhere in the shipped
code, and a build check enforces that rather than trusting anyone to remember.
