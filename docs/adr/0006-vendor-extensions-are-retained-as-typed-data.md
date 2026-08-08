# ADR 0006: Vendor extensions are retained as typed data

**Status**: Accepted
**Date**: 2026-08-09

## Context

Real BPMN files are full of content this library does not interpret. A file authored in a commercial
modeler carries `camunda:*`, `zeebe:*`, or `flowable:*` extension elements describing form fields,
job types, retry policies, and assignment rules. Files carry `<documentation>`. They carry attributes
from namespaces a general-purpose reader has never heard of.

The reader this library was extracted from discarded all of it. A single line skipped
`documentation`, `extensionElements`, `incoming`, and `outgoing`, and unrecognized child elements were
dropped with a diagnostic.

For an internal module that only ever read files it had itself written, that was defensible. For a
library whose main use case is interoperating with files other tools produced, it is close to
disqualifying: read a Camunda file, write it back, and the author's work is gone.

Layout had the same problem in one direction. Shapes and edge waypoints were read on import and only
shapes were written on export, so a single read-write cycle destroyed every hand-arranged connector
route.

## Decision

Retain foreign content as **typed, immutable, serializable data** attached to the definitions element,
each process, and each flow element:

```csharp
public sealed record BpmnExtensions(
    IReadOnlyList<BpmnDocumentation> Documentation,
    IReadOnlyList<BpmnExtensionElement> ExtensionElements,
    IReadOnlyList<BpmnForeignAttribute> ForeignAttributes,
    IReadOnlyList<BpmnForeignChild> ForeignChildren);
```

Retention is on by default (`BpmnFidelity.Lossless`). The reader emits one informational diagnostic per
retained namespace, so a user can see that their annotations survived rather than hoping.

BPMN DI is typed for the same reason: shapes and edges both, with waypoints and label bounds, written
back out in full.

### Typed data rather than retained XML nodes

Keeping live XML nodes would have been less code. It was rejected because such nodes do not survive
JSON serialization, and the model is persisted through exactly that path — the retained content would
have to be stringified, at which point it is an opaque blob with no queryability, no structural
equality, and no useful diff. Live XML nodes are also mutable and carry parent-document identity,
which sits badly in a model that is otherwise immutable records compared by value.

A conversion helper bridges to and from XML nodes for consumers who want one.

## Consequences

**Lossless for content, lossy for formatting.** Element structure, names, values, attributes, and
document order are preserved. Attribute order, whitespace, comments, CDATA-versus-text, and namespace
prefix choices are not.

That is a deliberate cut. A semantic round-trip a diff tool can compare is more useful than a
byte-exact one, and byte-exactness is unreachable without a fully source-preserving parser. Consumers
needing the original bytes can retain the source document alongside the model.

**Retained children must be written back in the right position.** BPMN's `tFlowNode` is an
`xsd:sequence`: `documentation`, then `extensionElements`, then `incoming`, then `outgoing`, then
type-specific content. Re-emitting retained children in arbitrary positions produces schema-invalid
XML that modeling tools reject. `BpmnForeignChild` records the index each child occupied, and the
writer restores it within the canonical child order for the element type. This is the single most
fiddly part of the writer and the easiest thing to regress.

**Extensions are carried, never interpreted.** The library does not know what a `camunda:formData`
means and does not try. It is not a migration tool between vendors; it preserves their annotations so
that something else can be.

## Alternatives considered

**Keep discarding foreign content.** Rejected. It makes the library unusable for the interoperability
case that is its main reason to exist.

**Retain XML nodes directly.** Rejected for the serialization, equality, and mutability reasons above.

**Model known vendor extensions with typed schemas.** Rejected as scope the library should not take
on. It would mean tracking three vendors' evolving formats, and the moment one changes, the library is
wrong. Verbatim retention is correct for all vendors, forever, at a fraction of the cost.
