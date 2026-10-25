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

`BpmnImportOptions` carries three other knobs. `ProcessId` names the process you care about — every
process in the document is still read, but naming one the document does not declare fails fast
instead of silently reading something else. `BindingRefPrefix` sets the prefix for generated binding
refs, which default to `node-{elementId}`. `VendorNamespace` and `VendorPrefix` are covered in
[the vendor namespace](#the-vendor-namespace) below.

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

Import findings summarize retained foreign content by namespace. `RetainedNamespaces()` also includes
resolved namespace context from recognized QName attribute values; those bindings do not each add an
import finding and are not necessarily vendor or runtime dependencies.

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

## The vendor namespace

BPMN 2.0 has no standard representation for a handful of authoring facts, so this library writes them
in a vendor namespace of its own — `https://bpmn.valenceworks.io/schema/bpmn`, conventionally
prefixed `vw`. Five names live there:

| Name | Where it hangs | What it says |
| --- | --- | --- |
| `<variable>` | a container's `extensionElements` | a container-scoped variable declaration |
| `conditionOutcome` | `sequenceFlow` | the outcome name this branch is taken on |
| `collection` | `multiInstanceLoopCharacteristics` | the declared variable to loop over |
| `itemVariable` | `multiInstanceLoopCharacteristics` | the per-iteration item key |
| `waitForCompletion` | `callActivity` | `"false"` for a fire-and-forget call |

The namespace and the prefix are both per-call options, on both sides:

```csharp
var vendor = "https://example.test/schema/process";

var result = reader.Read(xml, new BpmnImportOptions
{
    VendorNamespace = vendor,
    VendorPrefix = "acme"
});

var written = writer.Write(result, new BpmnExportOptions
{
    VendorNamespace = vendor,
    VendorPrefix = "acme"
});
```

They default to the values above, so leaving them alone changes nothing. Set them when you already
publish a vendor namespace of your own and would rather your documents carry it than carry two. The
constants remain on `BpmnXmlNames` (`VendorNamespaceName`, `VendorPrefix`) as the documented default.

An empty namespace or an unusable prefix is rejected with a `BpmnInterchangeException` naming the
option, rather than producing a document nothing can parse.

### Reading and writing do not have to agree

They usually should. When they do not, the writer's namespace wins, which makes a read under one
namespace and a write under another a one-pass migration:

```csharp
var model = reader.Read(xml);                                        // the default namespace
var moved = writer.Write(model, new BpmnExportOptions { VendorNamespace = vendor, VendorPrefix = "acme" });
```

Every vendor name in `moved` is in the new namespace, and none is left in the old one.

The other direction — reading a document written against a *different* vendor namespace — is where
the interpreted-versus-foreign line matters. A vendor name is interpreted only in the namespace the
read was configured for. In any other namespace it is somebody else's extension content, so it is
retained rather than read into the model:

```csharp
// A document authored with the default namespace, read by a consumer configured for its own.
var read = reader.Read(defaultNamespacedXml, new BpmnImportOptions { VendorNamespace = vendor });

read.Definitions.Processes[0].Variables;                    // empty — not this read's to interpret
read.Definitions.Processes[0].Extensions.ExtensionElements; // the <variable> declarations, verbatim
```

Nothing is lost and nothing is claimed twice: write that model back and the annotations return in the
namespace they were authored in, exactly once. That is deliberately the same rule the library applies
to any other vendor's annotations — it carries them, it does not reinterpret them.

There is one boundary. `collection` and `itemVariable` hang on a `multiInstanceLoopCharacteristics`
element the reader always consumes, so there is nowhere to retain them from. A collection
multi-instance authored in another vendor namespace therefore reads without loop characteristics, and
says so as a `Degraded` finding naming both namespaces. Read such a document with the namespace it was
written in, and write it with yours.

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
        BpmnWorkBinding.TimerDateWait date     => $"wait until {date.IsoDate}",
        BpmnWorkBinding.MessageWait message    => $"await message '{message.MessageName}'",
        BpmnWorkBinding.SignalWait signal      => $"await signal '{signal.SignalName}'",
        BpmnWorkBinding.MessagePublish publish => $"publish message '{publish.MessageName}'",
        BpmnWorkBinding.SignalPublish cast     => $"broadcast signal '{cast.SignalName}'",
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

### Assessing host work availability

A binding is a requirement, not proof that the host has an implementation. `UnboundTask` can be
implemented by the host; its subtype is not an unavailable-work verdict. After constructing a work
inventory from the returned descriptors, assess it explicitly:

```csharp
// Example inventory supplied by this host, independent of the binding subtype.
var inventory = new Dictionary<(string ProcessId, string ElementId, BpmnBindingSlot Slot), bool>
{
    [("Orders", "Reserve", BpmnBindingSlot.Primary)] = true,
    [("Orders", "UndoReserve", BpmnBindingSlot.Primary)] = false
};

var assessment = BpmnWorkAvailabilityValidator.Assess(result.Bindings, binding =>
    inventory.TryGetValue((binding.ProcessId, binding.ElementId, binding.Slot), out var available)
        ? (available ? BpmnWorkAvailability.Available : BpmnWorkAvailability.Unavailable)
        : BpmnWorkAvailability.Unknown);

foreach (var workResult in assessment.Results)
{
    // Binding retains ProcessId, ElementId, BindingRef and Slot; Diagnostic is library-generated.
    if (workResult.Diagnostic is { } diagnostic)
        Console.WriteLine(diagnostic);
}

if (!assessment.AllWorkAvailable)
{
    // Resolve unknown or unavailable requirements before this host chooses to execute.
}
```

Here `inventory` is a host-owned dictionary whose key is `(string ProcessId, string ElementId,
BpmnBindingSlot Slot)` and whose value states whether an implementation is available. No inventory
entry means unknown, which never counts as ready. An empty supplied set needs no work and is ready.
Assess the full set your host intends to execute, including reachable nested work, and reassess if
its inventory changes. This helper does not establish capability support, graph validity or durability.

The assessment preserves every supplied binding and status in order. It changes no imported elements,
associations, flows or analysis findings, including when a boundary host or compensation handler is
known unavailable. The host owns execution policy; the library does not silently remove behavior.
This is additive API on unreleased main after v0.4.0. See
[ADR0013](../adr/0013-host-work-availability-is-assessed-after-import.md).

## Writing

```csharp
var writer = new BpmnXmlWriter();

// Round-trip: writes back exactly what a read produced.
var xml = writer.Write(result);

// Write a full document model you assembled or edited yourself.
var edited = writer.Write(definitions, bindings, new BpmnExportOptions
{
    Exporter = "my-tool",
    ExporterVersion = "1.4.0"
});

// A host holding just one process can write it directly.
var singleProcessXml = writer.Write(process, bindings);
```

Prefer `Write(result)` whenever you have a `BpmnImportResult`. For models assembled or edited in code,
both `Write(definitions, bindings, options)` and `Write(process, bindings, options)` accept optional
bindings and export options. `bindings` supplies nested subprocess bodies and event binding data;
without it, subprocesses are written empty. Retained foreign content lives on the model itself.
`BpmnExportOptions` can override the target namespace, set the
[vendor namespace](#the-vendor-namespace), and omit the XML declaration.

The single-process overload wraps the process in minimal definitions and uses the same writer. It
synthesizes message and signal declarations from known names (including event bindings), and escalation
declarations from known event-definition codes. A recorded `errorRef` gets a matching error
declaration carrying the code and name the reader resolved onto the event definition, and an error
event definition that carries only a code gets a declaration for that code.
Use the definitions overload when you need explicit root declaration metadata, document metadata,
collaboration, or retained diagrams. The single-process overload generates layout instead of retaining
document-level diagrams.

Layout in the output is always complete. Every element, pool and lane gets a `BPMNShape`, and every
sequence flow and message flow gets a `BPMNEdge` with at least the two waypoints BPMN DI demands,
synthesized from the endpoint boxes when the source carried none. A flow that runs backwards is
routed as an elbow below the row, so a cyclic graph does not render as a pile of overlapping lines.

A sequence flow's `<conditionExpression>` is modeled as `BpmnSequenceFlow.ConditionExpression`:
the `Body` (the text and CDATA content as authored; whitespace-only text around a CDATA section or
between child elements is not kept), its own `Language`, and its remaining content (`xsi:type`, `id`,
vendor attributes, documentation) retained on the expression. The document's `expressionLanguage` is
`BpmnDefinitions.ExpressionLanguage`, copied onto every process and nested body as
`BpmnProcessDefinition.ExpressionLanguage`. A read then write reproduces both. The library never
evaluates an expression itself: a host does, through `IBpmnConditionEvaluator`, and a read reports no
finding for an expression-only flow, because whether a host can evaluate it is the
`ConditionEvaluation` capability that graph build checks (see
[ADR 0011](../adr/0011-hosts-evaluate-condition-expressions-through-a-synchronous-port.md)). Before
0.4.0 such a flow read as unconditional with a `Degraded` finding.

A few rules keep "read, write, read again" giving the same model:

- An expression that has a `language` but no `xsi:type` is written as `bpmn:tFormalExpression`,
  because BPMN allows a language only on a formal expression. Since the writer adds that type to every
  expression with a language, a read does not retain it there; any other `xsi:type` is retained.
- An expression that inherits a language is written with that language when its process's default
  differs from the document's, typically a process built in code and written into a document declaring
  another default. BPMN has no per-process expression language, so the next read gives the process the
  document's default and models the language as the expression's own. The meaning is unchanged.
- BPMN allows at most one `<conditionExpression>` per flow. If a document has more, counting a
  generated `outcome == '…'` predicate among them, the first author's expression is modeled and the
  rest are dropped with a `Dropped` finding naming the flow and the number it declares.

> **Upgrading from payload format 1.2.0.** An author's `<conditionExpression>` used to be retained as a
> foreign child of the flow, in `flow.Extensions.ForeignChildren`. It is now read into
> `flow.ConditionExpression` and is no longer in `ForeignChildren`, so code that looked for it there
> finds nothing. Read `ConditionExpression` instead. A 1.2.0 payload that still carries it as a foreign
> child is written once and reads back as a modeled expression.

A generated `outcome == '…'` expression is written only when the model has a `conditionOutcome` and
no author's expression, typically for a process built in code. That generated predicate is consumed
on the next read rather than modeled, so changing or clearing the outcome does not leave a stale
expression. Only the exact predicate for the flow's own outcome counts as generated. An expression of
the same shape that names another outcome, such as `outcome == 'Rejected'` on a flow whose
`conditionOutcome` is `Approved`, is the author's: it is modeled, written back as the flow's one
`<conditionExpression>` with no generated predicate beside it, and never evaluated, while
`vw:conditionOutcome` still decides the route.

A script task's `scriptFormat` and `<script>` are modeled the same way, as `BpmnElement.Script`: the
`Body` is read like an expression's, the `Format` is the task's `scriptFormat`, and any attributes or
child elements on `<script>` are retained on the script. `Script` is `null` on a task that declares
neither. A task with a format and no `<script>` reads with an empty body and writes no `<script>`. BPMN
allows one `<script>`; a second one is reported as `Dropped` and not written back. The
library never runs a script: the host that runs the task's bound work reads it from the element.

> **Upgrading from payload format 1.5.0.** A script task's `<script>` used to be retained in
> `element.Extensions.ForeignChildren`, and its `scriptFormat` was dropped. Both are now read into
> `element.Script`. A 1.5.0 payload that still carries the script as a foreign child is written once
> and reads back as a modeled script.

What the writer does not do is reproduce the source byte for byte.

**Lossless for content, lossy for formatting.** Preserved: element structure, names, values, document
order, layout, foreign content. Not preserved: attribute order, whitespace, comments, CDATA-versus-text,
namespace prefix choices.

That is a deliberate trade rather than an unfinished feature. Byte-exactness is unreachable without a
source-preserving parser, and a semantic round-trip that a diff tool can compare is more useful than
one that cannot. The test to write is "read, write, read again, compare the two models" — not
"compare the two strings".

### Namespace context inside retained attribute values

Payload format 1.10.0 records namespace provenance for retained attribute values that are a single
prefixed QName, such as `xsd:string`, or a whitespace-separated list of them. It preserves both bound
and observed unbound prefixes through XML, model JSON, and XML again, including local shadowing.
Namespace prefix choices in XML names may still change; prefixes inside these values keep their
recorded meaning. Recognition is lexical: a value such as `urn:value` matches too, regardless of the
vendor's type system, and can therefore be subject to collision refusal.

If an observed unbound prefix collides with a required writer prefix, export throws
`BpmnInterchangeException` rather than binding it silently. Arbitrary expressions, unprefixed QName
values and QName text content are outside this recognition rule. Older JSON without provenance still
reads, but cannot recover context discarded by an earlier import; re-import the original XML for that.
See [the retention decision](../adr/0006-vendor-extensions-are-retained-as-typed-data.md#2026-10-25-amendment-namespace-provenance-in-attribute-values)
for the metadata, collision and editing contract.

### Passing a document through JSON

Transfer the definitions **and** bindings when a client needs to edit nested subprocesses or call
options. Payload format 1.2.0 gives `BpmnWorkBinding` a JSON contract with explicit camelCase names and
a `kind` discriminator. Serialize bindings as `BpmnWorkBinding` or a collection of that base type,
using `System.Text.Json`'s default options:

```csharp
using System.Text.Json;

var payload = JsonSerializer.Serialize(new
{
    definitions = result.Definitions,
    bindings = result.Bindings
});

using var envelope = JsonDocument.Parse(payload);
var definitions = envelope.RootElement.GetProperty("definitions").Deserialize<BpmnDefinitions>()!;
var bindings = envelope.RootElement.GetProperty("bindings").Deserialize<IReadOnlyList<BpmnWorkBinding>>()!;
var rewritten = writer.Write(definitions, bindings);
```

The envelope belongs to the host. Its two components use the library's
[published schema](../reference/packages.md#the-serialization-is-owned-and-versioned); validate each
binding against the `BpmnWorkBinding` root. Its cases are `timerWait`, `timerDateWait`, `timerCycleWait`, `messageWait`, `signalWait`,
`messagePublish`, `signalPublish`, `callProcess`, `nestedProcess`, and `unboundTask`. Unknown `kind` values are rejected.
Keep `kind` first when a client rebuilds a binding object: the default serializer writes it there,
and [System.Text.Json requires discriminator metadata first by default](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/polymorphism).
`slot` is numeric like other enums in the format: `0` for Primary and `1` for ScopeListener. Nested
process definitions and their retained content travel in `nestedProcess.definition`; nested work
bindings remain entries in the bindings collection, identified by their `processId`.

## Reading a document you did not author

A short checklist for BPMN arriving from a modeling tool you do not control:

1. `Analyze` first. Treat any `Dropped` finding as a question for a human.
2. Check `Definitions.Exporter` — knowing which tool produced a file explains most of its oddities.
3. Enumerate `Extensions.RetainedNamespaces()` at the document level to inspect foreign content and
   QName-value context. A datatype namespace alone does not imply a vendor runtime dependency.
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
