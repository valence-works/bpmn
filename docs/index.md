# BPMN for .NET

A BPMN 2.0 interpreter and interchange library. It reads and writes BPMN XML over a typed, immutable
object model, and it answers one question about a running process: given this definition, this token
state, and this event, what is the next state, and what should the host do about it?

It never does the doing.

```text
(process definition, current token state, one event) -> (next state, commands for the host)
```

The semantics core is a pure function. No clock, no I/O, no threads, no dependency injection, no
persistence. A host supplies all of that, and applies the commands the interpreter hands back.

## What it is

- A BPMN 2.0 XML reader and writer over a typed, immutable object model.
- A content-lossless round-trip: foreign `extensionElements` (`camunda:*`, `zeebe:*`, `flowable:*`)
  and BPMN DI layout survive a read-modify-write cycle.
- An import analyzer with element-scoped `Info` / `Degraded` / `Dropped` diagnostics, where analyzing
  and committing share one code path.
- A programmatic way to build processes without XML.
- A token-semantics interpreter covering exclusive, parallel, inclusive and event-based gateways;
  start, intermediate and end events; interrupting and non-interrupting boundary events; embedded and
  event subprocesses; multi-instance; compensation; transactions; escalation; and cyclic flows.
- Deterministic and synchronous throughout.

## What it is not

- Not a production workflow engine. No durable persistence, scheduling, retries, broker, queue, or
  distributed coordination.
- Not an executor of anything.
- Not an expression or script evaluator. No FEEL, no JUEL, no JavaScript; sequence-flow conditions
  are resolved by the host.
- Not DMN, and not CMMN.
- Not a modeler or a renderer.
- Not a client for any BPMN vendor's server.
- Not an XSD validator.
- Not byte-exact on round-trip. Content is preserved; formatting is not.

The honesty about that second list is the point. A library that quietly does half of what a workflow
engine does is harder to build on than one that does none of it and says so.

## The four packages

| Package | Use it when | Depends on |
| --- | --- | --- |
| `Bpmn.Model` | You need the object model and nothing else. | nothing |
| `Bpmn.Interchange` | You read or write `.bpmn` files: linters, converters, doc generators, migration tools. | `Bpmn.Model` |
| `Bpmn.Semantics` | You need to know what happens next. | `Bpmn.Model` |
| `Bpmn.Runtime.InMemory` | You want to run a process without writing a host: simulation, analysis, tests. | `Bpmn.Semantics` |

Every shipped package has zero external NuGet dependencies and targets `net8.0` and `net10.0`.
See [Packages](reference/packages.md).

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

## Where to go next

**If you are here to handle `.bpmn` files**, read [Getting started](guides/getting-started.md), then
[Reading and writing BPMN XML](guides/reading-and-writing-bpmn.md). You will never need the
interpreter.

**If you are here to run processes without building anything**, read
[Simulating a process](guides/simulating-a-process.md).

**If you are here to embed BPMN in your own system**, read
[Interpreter, not engine](concepts/interpreter-not-engine.md) to understand where the boundary is,
then [Hosting the interpreter](guides/hosting-the-interpreter.md) for how to implement one.

**If you want to know whether your diagrams are covered**, read
[Supported BPMN constructs](reference/supported-constructs.md).

### Guides

- [Getting started](guides/getting-started.md)
- [Reading and writing BPMN XML](guides/reading-and-writing-bpmn.md)
- [Building a process in code](guides/building-a-process-in-code.md)
- [Simulating a process](guides/simulating-a-process.md)
- [Hosting the interpreter](guides/hosting-the-interpreter.md)

### Concepts

- [Interpreter, not engine](concepts/interpreter-not-engine.md)
- [The host port](concepts/host-port.md)
- [Host capabilities](concepts/capabilities.md)
- [Errors and escalation](concepts/errors-and-escalation.md)

### Reference

- [Supported BPMN constructs](reference/supported-constructs.md)
- [Packages](reference/packages.md)

### Decisions

- [ADR 0001: BPMN semantics ship as a host-agnostic library](adr/0001-bpmn-semantics-ship-as-a-host-agnostic-library.md)
- [ADR 0002: The host port is synchronous and command-returning](adr/0002-the-host-port-is-synchronous-and-command-returning.md)

## License

MIT, stewarded by Valence Works. The library names no host, depends on no host, and assumes no host.
