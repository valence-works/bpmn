# Getting started

Install the library, read a BPMN file, inspect what came back, and write it out again. Ten minutes,
no host, no engine, no configuration.

## Install

```bash
dotnet add package Bpmn.Interchange
```

`Bpmn.Interchange` brings `Bpmn.Model` with it, and nothing else — no shipped package in this library
has an external NuGet dependency. Both target `net8.0` and `net10.0`.

Add `Bpmn.Semantics` only when you need to know what a process *does*; see
[Hosting the interpreter](hosting-the-interpreter.md).

## Read a file

```csharp
using Bpmn.Interchange;
using Bpmn.Model;

var xml = File.ReadAllText("order.bpmn");
var result = new BpmnXmlReader().Read(xml);
```

`BpmnXmlReader` and `BpmnXmlWriter` are ordinary instantiable classes. They hold no state between
calls, so keep one around or new one up per call; both are fine.

`BpmnImportResult` has four parts, and each answers a different question:

| Part | Question it answers |
| --- | --- |
| `Definitions` | What is in the document? |
| `Bindings` | What work would a host have to be able to do to run it? |
| `Analysis` | What could the reader not fully use, and where? |
| `ElementExtensions` | What foreign content did individual elements carry? |

Read the analysis first. It is the only one that can tell you the others are incomplete.

A document that is not readable at all raises a `BpmnInterchangeException`. Everything that parsed
but could not be fully used is a finding, not an exception.

```csharp
foreach (var issue in result.Analysis.Issues)
    Console.WriteLine($"[{issue.Severity}] {issue.ProcessId}/{issue.ElementId}: {issue.Message}");
```

`BpmnImportIssueSeverity` has three values: `Info` (something was noted), `Degraded` (the element read
in a reduced form), and `Dropped` (the element could not be represented and was dropped, along with
the flows that referenced it). Nothing is ever discarded silently.

## Inspect the model

`BpmnDefinitions` is the root of the document: the `<definitions>` element and everything under it.

```csharp
var definitions = result.Definitions;

Console.WriteLine($"target namespace: {definitions.TargetNamespace}");
Console.WriteLine($"exported by:      {definitions.Exporter} {definitions.ExporterVersion}");
Console.WriteLine($"processes:        {definitions.Processes.Count}");
Console.WriteLine($"diagrams:         {definitions.Diagrams.Count}");
```

Every collection on the model is non-null. An absent `<collaboration>` is a null `Collaboration`, but
an empty list of processes is an empty list, never null. You do not need null guards around
enumeration.

Reach into a process by id, or take them in document order:

```csharp
var process = definitions.FindProcess("order-approval")
              ?? throw new InvalidOperationException("no such process");

foreach (var element in process.Elements)
{
    var eventTypes = string.Join(", ", element.EventDefinitions.Select(d => d.Type));
    Console.WriteLine($"{element.ElementId,-24} {element.ElementType,-24} {element.Name} {eventTypes}");
}

foreach (var flow in process.SequenceFlows)
    Console.WriteLine($"{flow.SourceRef} -> {flow.TargetRef}{(flow.IsDefault ? " (default)" : "")}");
```

`BpmnElement.ElementType` is a string, and the values are constants on `BpmnElementTypes`:

```csharp
var userTasks = process.Elements
    .Where(e => e.ElementType == BpmnElementTypes.UserTask)
    .ToList();

var boundaryEvents = process.Elements
    .Where(e => e.ElementType == BpmnElementTypes.BoundaryEvent)
    .ToList();

foreach (var boundary in boundaryEvents)
    Console.WriteLine($"{boundary.ElementId} on {boundary.AttachedToRef}, " +
                      $"{(boundary.CancelActivity ? "interrupting" : "non-interrupting")}");
```

They are string constants rather than an enum on purpose: a BPMN element type the model does not yet
name can be added without breaking anything already serialized.

### What survived that you did not ask for

Layout and vendor annotations are in the result, not thrown away:

```csharp
var plane = definitions.Diagrams[0].Plane;
Console.WriteLine($"{plane.Shapes.Count} shapes, {plane.Edges.Count} edges");

// The definitions and process elements carry their retained content on the model itself.
foreach (var ns in process.Extensions.RetainedNamespaces())
    Console.WriteLine($"retained foreign namespace: {ns}");

// Per-element retained content travels alongside, matched back by process and element id.
foreach (var retained in result.ElementExtensions)
    Console.WriteLine($"{retained.ProcessId}/{retained.ElementId}: " +
                      $"{retained.Extensions.ExtensionElements.Count} extension elements");
```

`RetainedNamespaces()` is the quick way to see whose annotations a document is carrying.

## Write it back

```csharp
var writer = new BpmnXmlWriter();

// Hand the whole result back and everything the reader retained is written where it came from.
File.WriteAllText("order.out.bpmn", writer.Write(result));
```

The round-trip is **lossless for content, lossy for formatting**. Element structure, names, values,
document order, DI layout and foreign extensions come back. Attribute order, whitespace, comments,
CDATA-versus-text and namespace prefix choices do not. Diffing the two files will show differences;
diffing the two parsed models should not.

If byte-exactness matters to you, this is not the tool — and no tool without a source-preserving
parser can offer it. See [Reading and writing BPMN XML](reading-and-writing-bpmn.md) for the full
fidelity contract.

## Modify before writing

The model is immutable, so edits are `with` expressions rather than mutation:

```csharp
var renamed = process with { Name = "Order approval (revised)" };

var updated = definitions with
{
    Processes = definitions.Processes
        .Select(p => p.ProcessId == renamed.ProcessId ? renamed : p)
        .ToList()
};

File.WriteAllText(
    "order.revised.bpmn",
    writer.Write(updated, result.Bindings, result.ElementExtensions));
```

Pass the bindings and the retained element content along with an edited model: the bindings supply
the nested process behind each subprocess element, and without them subprocesses are written empty.

Nothing you held before the edit changed. That property is what makes analyze-then-commit workflows
safe; see [the analyze-then-commit contract](reading-and-writing-bpmn.md#analyze-then-commit).

## Where to go next

- [Reading and writing BPMN XML](reading-and-writing-bpmn.md) — diagnostics, fidelity modes and the
  round-trip contract in depth.
- [Building a process in code](building-a-process-in-code.md) — construct a definition with no XML at
  all.
- [Simulating a process](simulating-a-process.md) — run one, with a virtual clock, in memory.
- [Interpreter, not engine](../concepts/interpreter-not-engine.md) — what this library will and will
  not do for you, and why the line is there.
