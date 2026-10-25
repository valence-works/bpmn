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

Retention is on by default (`BpmnFidelity.Lossless`). Import diagnostics summarize retained foreign
content by namespace. `RetainedNamespaces()` also reports resolved QName-value namespace context;
those bindings do not each generate another diagnostic.

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

## 2026-10-25 amendment: namespace provenance in attribute values

A prefix inside an attribute value is not merely formatting. For example, retaining `xsd:string`
without its original `xmlns:xsd` changes how a QName consumer resolves the value. Keeping expanded
element and attribute names alone does not preserve this information, including after a JSON transfer.

`BpmnForeignAttribute.ValueNamespaces` therefore carries optional per-prefix provenance, as a list of
`BpmnNamespaceBinding` records with `prefix` and `ns`. A null `ns` means the prefix was observed
unbound at that attribute's source element. An absent `valueNamespaces` property means no provenance
was recorded, as in legacy JSON or a manually built attribute. The new init property does not change
the existing two-argument constructor or deconstruction; retained attributes compare and hash their
provenance structurally.

Capture recognizes a single prefixed QName or an XML-whitespace-separated list of prefixed QNames.
Each token must consist of two valid XML NCNames separated by one colon. The whole value must match
that grammar. Values outside it, such as `https://example.test/type` and arbitrary expressions, are
opaque text. This is lexical recognition, not vendor schema typing: `urn:value` also matches, even if
a vendor treats it as a plain string, and can trigger collision refusal. Resolve each prefix in its attribute's
own scope, preserving local shadowing. Keep the lexical value, except for the existing normalization
of BPMN `xsi:type` values; provenance then describes the normalized prefix.

The writer restores bound mappings locally, reserves observed unbound prefixes from generated root
prefixes, and checks their actual resolution before returning XML. A collision with a required fixed
prefix, or incompatible bindings on one output element, raises `BpmnInterchangeException` rather than
silently changing the value's meaning. This can reject an export that previously wrote misleading
namespace context. In particular, an unbound `dc` token conflicts with the writer's required diagram
namespace; changing the source or choosing a nonconflicting host vendor prefix where applicable is an
explicit caller decision.

This is a scoped fidelity guarantee. It does not interpret arbitrary vendor expression languages,
unprefixed QName values that depend on a default namespace, or QName content in element text.
Legacy JSON cannot reconstruct namespace context already lost; re-import the original XML to acquire
provenance. A caller changing a retained attribute's value must update or clear its provenance too.
The semantic-only import mode continues to discard foreign content.

Unlike host-port `BpmnValue` JSON, retained attributes are reachable from the published payload roots.
The optional metadata adds payload format **1.10.0** under [ADR 0005](0005-the-library-owns-a-versioned-payload-format.md),
with the generated schema reviewed alongside it. Older payloads omit the metadata and still read;
a consumer using the older closed schema must upgrade before accepting the new property. Package
versioning and the stable v0.5.0 release are separate from the payload format.
