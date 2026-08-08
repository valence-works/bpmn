# Reading and writing BPMN XML

`Bpmn.Interchange` is the layer between BPMN 2.0 XML and the typed model. It is the part of this
library with the largest audience, because a linter, a converter, a documentation generator or a
migration tool never needs the interpreter at all.

This page covers what the reader reports, what the round-trip guarantees, what it deliberately does
not guarantee, and why analyzing a document and importing it are the same operation.

## What a read gives you

```csharp
using Bpmn.Interchange;

var reader = new BpmnXmlReader();
var result = reader.Read(File.ReadAllText("order.bpmn"));

BpmnDefinitions definitions = result.Definitions;
IReadOnlyList<BpmnWorkBinding> bindings = result.Bindings;
BpmnImportAnalysis analysis = result.Analysis;
IReadOnlyList<BpmnRetainedElement> retained = result.ElementExtensions;
```

`Definitions` is the document. `Bindings` is the work the document implies. `Analysis` is everything
the reader wants to tell you about the gap between the two. `ElementExtensions` is the foreign
content individual elements carried, which travels beside the model rather than inside it.

Alongside the issues, the analysis carries `ProcessIds` (every `<process>` id, in document order) and
`ElementCounts` (a histogram of BPMN local names encountered). A histogram is a cheap way to answer
"does this document use anything I do not handle?" without walking it yourself.

A document that cannot be read at all raises a `BpmnInterchangeException`. Anything that parsed is
reported as a finding instead.

## Diagnostics

Every finding is a `BpmnImportIssue`: a severity, a message, and the element and process it belongs
to. Findings are element-scoped, not document-scoped, because "this file has a problem" is not
actionable and "this timer on `Escalate` has no duration" is.

| Severity | Meaning | What you should do |
| --- | --- | --- |
| `Info` | Something worth knowing. Nothing was lost and nothing was weakened. | Usually nothing. |
| `Degraded` | The construct was imported, but with less capability than the source intended. | Decide whether the reduced form is acceptable. |
| `Dropped` | Something in the source is not represented in the model at all. | Decide whether the document is still the document you meant. |

```csharp
var dropped = analysis.Issues
    .Where(i => i.Severity == BpmnImportIssueSeverity.Dropped)
    .ToList();

if (dropped.Count > 0)
{
    foreach (var issue in dropped)
        Console.Error.WriteLine($"{issue.ProcessId}/{issue.ElementId}: {issue.Message}");

    return 1;
}
```

There is no fourth severity for "error". A document the reader cannot parse at all is not a finding;
it is a failure of the read. Findings describe documents that parsed.

Nothing is dropped silently. If a construct is missing from the model, there is an issue naming the
element it came from. That is the contract the analyzer exists to provide, and it is the reason a
`Dropped` count of zero is a meaningful thing to assert in a test.

## Analyze then commit

The reader exposes two entry points over one implementation:

```csharp
// Look, without building anything.
BpmnImportAnalysis preview = reader.Analyze(xml);

// Look and build.
BpmnImportResult imported = reader.Read(xml);
```

`Analyze` runs the same code path as `Read` and stops short of handing you the result. It does not
approximate the import, re-implement it, or check a subset of it. The analysis a user sees in a
preview dialog is, by construction, the analysis the import will produce.

This matters more than it sounds. The usual shape of an import feature is a validator written
separately from the importer, and the two drift: the preview says a file is clean, the import
degrades three elements, and the difference is discovered by a user. Sharing one code path removes
that class of bug rather than testing for it.

The practical pattern:

```csharp
var preview = reader.Analyze(xml);

if (preview.Issues.Any(i => i.Severity == BpmnImportIssueSeverity.Dropped) && !userConfirmed)
    return ImportOutcome.NeedsConfirmation(preview);

var result = reader.Read(xml);   // same findings, now with a model
```

Reading twice costs a second parse. If that matters, call `Read` once and use `result.Analysis`; the
findings are identical.

## Fidelity

`BpmnFidelity` controls how much of the source document is retained:

| Mode | Retains |
| --- | --- |
| `BpmnFidelity.Lossless` | Documentation, `extensionElements`, foreign attributes and unrecognized child elements, in addition to everything the semantics need. **The default.** |
| `BpmnFidelity.Semantic` | Only what the semantics need. Foreign content is reported as a finding and dropped. |

```csharp
var stripped = reader.Read(xml, new BpmnImportOptions { Fidelity = BpmnFidelity.Semantic });
```

`BpmnImportOptions` carries two other knobs. `ProcessId` names the process you care about — every
process in the document is still read, but naming one the document does not declare fails fast
instead of silently reading something else. `BindingRefPrefix` sets the prefix for generated binding
refs, which default to `node-{elementId}`.

Use `Semantic` when you are producing a clean document from a messy one and want the removals
enumerated. Use the default for everything else — in particular, for anything that will write the
document back.

## What gets retained

Retained content is a `BpmnExtensions` record: documentation, extension elements, foreign
attributes, and unrecognized children.

```csharp
var extensions = definitions.Extensions;      // or process.Extensions

foreach (var doc in extensions.Documentation)
    Console.WriteLine(doc.Text);

foreach (var extension in extensions.ExtensionElements)
    Console.WriteLine($"{extension.Name} ({extension.Attributes.Count} attributes)");

foreach (var attribute in extensions.ForeignAttributes)
    Console.WriteLine($"{attribute.Name} = {attribute.Value}");

Console.WriteLine(extensions.IsEmpty ? "nothing retained" : "carrying foreign content");
```

`BpmnDefinitions` and `BpmnProcessDefinition` hold their retained content directly. The per-element
types do not, so element-scoped retention rides alongside the model as `BpmnRetainedElement` records,
matched back by process id plus element id:

```csharp
var approve = result.ElementExtensions
    .FirstOrDefault(r => r.ElementId == "approve")?.Extensions
    ?? BpmnExtensions.Empty;
```

Hand that list straight back to the writer and the content is written where it came from.

`BpmnQName` renders as `{namespace}localName`, matching the `XName` convention, so a `camunda:`
attribute prints as `{http://camunda.org/schema/1.0/bpmn}assignee`.

To find out whose annotations a document carries without walking the tree:

```csharp
foreach (var ns in definitions.Extensions.RetainedNamespaces())
    Console.WriteLine(ns);
```

The reader emits one informational finding per retained namespace, so a user can see that their
annotations survived rather than hoping.

Retained subtrees are held as data — `BpmnExtensionElement` records with a name, attributes, children
and text — rather than as live XML nodes. That is deliberate: data survives JSON serialization,
compares by value, and cannot be mutated behind the model's back. A conversion helper bridges to and
from XML nodes for consumers who want one.

Extensions are carried, never interpreted. The library does not know what a `camunda:formData` means
and does not try. It preserves vendor annotations so that something else can act on them; it is not a
migration tool between vendors.

### Position is retained too

An unrecognized child element is stored as a `BpmnForeignChild`, carrying the zero-based index it
occupied among its siblings. That index is not bookkeeping for its own sake. BPMN's `tFlowNode` is an
`xsd:sequence`, so re-emitting a retained child in an arbitrary position produces schema-invalid XML
that modeling tools reject. The writer restores each retained child at its recorded index within the
canonical child order for its element type.

## DI layout

Diagram layout is a first-class part of the model, not an opaque blob:

```csharp
var diagram = definitions.Diagrams[0];
var plane = diagram.Plane;

foreach (var shape in plane.Shapes)
    Console.WriteLine($"{shape.BpmnElementRef}: " +
                      $"{shape.Bounds.X},{shape.Bounds.Y} {shape.Bounds.Width}x{shape.Bounds.Height}");

foreach (var edge in plane.Edges)
    Console.WriteLine($"{edge.BpmnElementRef}: " +
                      string.Join(" -> ", edge.Waypoints.Select(w => $"({w.X},{w.Y})")));
```

Because layout is modeled, you can move a shape and write the document back without disturbing
anything else:

```csharp
var moved = plane with
{
    Shapes = plane.Shapes
        .Select(s => s.BpmnElementRef == "approve"
            ? s with { Bounds = s.Bounds with { X = s.Bounds.X + 120 } }
            : s)
        .ToList()
};
```

BPMN DI requires an edge to have at least two waypoints; an edge with fewer is never emitted. When a
source document carried no route for a flow, a straight two-point route is synthesized from the
endpoint shapes. Default sizes used when synthesizing layout are on `BpmnLayoutDefaults` — a 36pt
event circle, a 50pt gateway diamond, a 100×80 task rectangle, a 180pt horizontal pitch.

## Work bindings

`result.Bindings` is the reader's answer to "what would a host have to be able to do to run this?",
computed without running anything. Each binding carries the process and element it came from, the
element's opaque `BindingRef`, and a slot distinguishing an element's body binding from its listener
binding (see `BpmnElement.ListenerBindingRef`).

```csharp
foreach (var binding in result.Bindings)
{
    var description = binding switch
    {
        BpmnWorkBinding.TimerWait timer        => $"wait {timer.IsoDuration}",
        BpmnWorkBinding.MessageWait message    => $"await message '{message.MessageName}'",
        BpmnWorkBinding.SignalWait signal      => $"await signal '{signal.SignalName}'",
        BpmnWorkBinding.MessagePublish publish => $"publish message '{publish.MessageName}'",
        BpmnWorkBinding.CallProcess call       => $"call '{call.CalledElement}'" +
                                                  (call.WaitForCompletion ? " and wait" : ""),
        BpmnWorkBinding.NestedProcess nested   => $"run nested process '{nested.Definition.ProcessId}'",
        BpmnWorkBinding.UnboundTask task       => $"unbound {task.TaskType}",
        _                                      => binding.GetType().Name
    };

    Console.WriteLine($"{binding.ProcessId}/{binding.ElementId}: {description}");
}
```

`UnboundTask` is the interesting one. A `serviceTask` with no implementation the reader can resolve is
not an error and not a dropped element — it is a task whose work the host must supply. Enumerating
the unbound tasks in a document is how you find out what an integration still owes.

## Writing

```csharp
var writer = new BpmnXmlWriter();

// Round-trip: writes back exactly what a read produced.
var xml = writer.Write(result);

// Or write a model you assembled or edited yourself.
var edited = writer.Write(definitions, bindings, elementExtensions, new BpmnExportOptions
{
    Exporter = "my-tool",
    ExporterVersion = "1.4.0"
});
```

Prefer the first overload whenever you have a `BpmnImportResult`. The second takes the pieces
separately: `bindings` supplies the nested process behind each subprocess element — without it,
subprocesses are written empty — and `elementExtensions` supplies the per-element retained content.
`BpmnExportOptions` can also override the target namespace and omit the XML declaration.

Layout in the output is always complete. Every element, pool and lane gets a `BPMNShape`, and every
sequence flow and message flow gets a `BPMNEdge` with at least the two waypoints BPMN DI demands,
synthesized from the endpoint boxes when the source carried none. A flow that runs backwards is
routed as an elbow below the row, so a cyclic graph does not render as a pile of overlapping lines.

What the writer does not do is reproduce the source byte for byte.

**Lossless for content, lossy for formatting.** Preserved: element structure, names, values, document
order, layout, foreign content. Not preserved: attribute order, whitespace, comments, CDATA-versus-text,
namespace prefix choices.

That is a deliberate trade rather than an unfinished feature. Byte-exactness is unreachable without a
source-preserving parser, and a semantic round-trip that a diff tool can compare is more useful than
one that cannot. The test to write is "read, write, read again, compare the two models" — not
"compare the two strings".

## Reading a document you did not author

A short checklist for BPMN arriving from a modeling tool you do not control:

1. `Analyze` first. Treat any `Dropped` finding as a question for a human.
2. Check `Definitions.Exporter` — knowing which tool produced a file explains most of its oddities.
3. Enumerate `Extensions.RetainedNamespaces()` at the document level. Vendor namespaces tell you what
   the file assumes about its runtime.
4. Enumerate `Bindings` and look for `UnboundTask`. Those are the elements that will not run until
   someone wires them.
5. Look up anything exotic in [Supported BPMN constructs](../reference/supported-constructs.md)
   before assuming it is executable.

## Related

- [Supported BPMN constructs](../reference/supported-constructs.md) — what is modeled, what is
  degraded, what is dropped.
- [Building a process in code](building-a-process-in-code.md) — the same model, without XML.
- [Hosting the interpreter](hosting-the-interpreter.md) — what to do with those bindings.
- [ADR 0006: Vendor extensions are retained as typed data](../adr/0006-vendor-extensions-are-retained-as-typed-data.md)
