# Packages

Four packages. Every one targets `net8.0` and `net10.0`, is MIT licensed, and has **zero external
NuGet dependencies**.

## The set

| Package | Contains | Depends on |
| --- | --- | --- |
| `Bpmn.Model` | The neutral BPMN 2.0 object model: processes, flow elements, sequence flows, event definitions, DI layout, retained vendor extensions, and the execution-state records. | nothing |
| `Bpmn.Interchange` | BPMN 2.0 XML reading and writing over that model, preserving DI layout and vendor `extensionElements`, with element-scoped import diagnostics. | `Bpmn.Model` |
| `Bpmn.Semantics` | The token-semantics interpreter: a pure, synchronous state transition function over a process graph. | `Bpmn.Model` |
| `Bpmn.Runtime.InMemory` | A reference host: runs processes in memory with a virtual clock, for simulation, analysis and testing. | `Bpmn.Semantics` |

Dependencies are transitive, so installing `Bpmn.Runtime.InMemory` gives you all four.

```text
Bpmn.Runtime.InMemory
  └── Bpmn.Semantics
        └── Bpmn.Model

Bpmn.Interchange
  └── Bpmn.Model
```

## Which one do I install?

| I want to | Install |
| --- | --- |
| Read, lint, convert or generate `.bpmn` files | `Bpmn.Interchange` |
| Work with the object model, having got it some other way | `Bpmn.Model` |
| Know what happens next, inside my own runtime | `Bpmn.Semantics` (plus `Bpmn.Interchange` if the input is XML) |
| Run or simulate a process without writing a host | `Bpmn.Runtime.InMemory` |
| Test a BPMN definition in CI | `Bpmn.Runtime.InMemory` |

```bash
dotnet add package Bpmn.Interchange
dotnet add package Bpmn.Semantics
dotnet add package Bpmn.Runtime.InMemory
```

The four-way split exists mainly for the first row. A linter, a converter, a documentation generator
or a migration tool never takes the interpreter into its dependency closure, and that is the largest
audience.

## Upgrade notes on unreleased main

### Cancellation history

After the published v0.4.0 baseline, `BpmnExecutionState.Prune()` also removes canceled tokens that no
active work references and that carry no unresolved stopped-work handle. This changes retained execution
history without changing the payload schema. Custom hosts must capture cancellation transitions from each evaluation's unpruned `State` before pruning;
the reference host records them in `BpmnTranscriptEntry.TokenMoves`. UIs should use that captured history
rather than rely on canceled tokens remaining in persisted `State.Tokens`. Carry this behavior change
into the next release notes; it is not part of v0.4.0.

A non-null stopped-work handle still retains its token until its late callback is absorbed. Hosts that
never deliver that callback can still accumulate retained records; no count or time cap is introduced.
See [the pruning contract](../concepts/host-port.md#the-prior-state).

### Host value JSON

The next v0.5.0 release will also stop writing the computed `BpmnValue.HasValue` property to JSON.
Its C# getter and three-value deconstruction remain available, and legacy JSON containing the field
still reads with the getter recomputed from `presence` and `json`. Hosts that require the old field in
a host-value schema must update that schema or compute the derived value. This change does not affect
the three published payload roots or their format version. See
[the host-port amendment](../adr/0002-the-host-port-is-synchronous-and-command-returning.md#2026-10-25-amendment-computed-host-values).
It is not a change to the already published v0.4.0 packages.

### Retained QName values

The next v0.5.0 release carries optional namespace provenance for retained QName attribute values in
payload format 1.10.0. Older JSON still reads without that metadata, but cannot recover lost namespace
context. Export now refuses a recorded namespace collision rather than changing the value's meaning.
See [the XML guide](../guides/reading-and-writing-bpmn.md#namespace-context-inside-retained-attribute-values)
for the supported lexical grammar and limitations. This is separate from the host-value change above.

## The zero-dependency rule

No shipped package references any external NuGet package. Not a JSON library, not a logging
abstraction, not an XML helper, not a collections package. Only the base class library and each
other.

This is enforced, not aspirational. Package versions are managed centrally in
`Directory.Packages.props`, which contains only build-time-only assets and test-only dependencies; an
architecture test asserts the shipped assemblies reference nothing else. A `PackageReference` from a
project under `src/` is treated as a design defect rather than a version bump.

Two consequences you get for free: no diamond conflicts with whatever your application already uses,
and no transitive CVE arriving through this library.

## `Bpmn.Model`

The types everything else is expressed in.

- **Document**: `BpmnDefinitions`, `BpmnProcessDefinition`, `BpmnCollaboration`,
  `BpmnVariableDeclaration`.
- **Root declarations**: `BpmnMessageDeclaration`, `BpmnSignalDeclaration`, `BpmnErrorDeclaration`,
  `BpmnEscalationDeclaration`.
- **Graph**: `BpmnElement` (with `BpmnElementTypes`), `BpmnSequenceFlow`, `BpmnEventDefinition` (with
  `BpmnEventDefinitionTypes` and `BpmnEventDefinitionProperties`), `BpmnLoopCharacteristics`.
- **Collaboration**: `BpmnPool`, `BpmnLane`, `BpmnMessageFlow`.
- **Layout**: `BpmnDiagram`, `BpmnPlane`, `BpmnShape`, `BpmnEdge`, `BpmnPoint`, `BpmnBounds`,
  `BpmnLabel`, `BpmnLayoutDefaults`.
- **Retention**: `BpmnExtensions`, `BpmnQName`, `BpmnExtensionElement`, `BpmnForeignAttribute`,
  `BpmnForeignChild`, `BpmnDocumentation`, `BpmnNamespaceBinding`, `BpmnFidelity`.
- **Values**: `BpmnValue`, `BpmnValuePresence`, `BpmnValueTypes`.
- **State** (`Bpmn.Model.State`): `BpmnExecutionState`, `BpmnToken`, `BpmnActiveWork`,
  `BpmnDiagnosticEvent`, `BpmnEventRace`, `BpmnLoopState`, `BpmnCompensable`, `BpmnCompensationRun`.

Everything is immutable. Records where the type is a value; classes with get-only properties and a
`[JsonConstructor]` where identity or validation matters. Collection properties are never null — an
absent list is an empty list.

The whole model round-trips through `System.Text.Json` with the property names it declares, which is
what makes an execution state something you can persist as a document and an evaluation something you
can paste into a bug report.

### The serialization is owned and versioned

`Bpmn.Model` owns the JSON serialization of the model rather than leaving it to each host, and
publishes a JSON Schema for the three payload roots listed below. Host-port values such as
`BpmnValue` are outside those roots. Hosts **wrap** rather than replace it: a host that
needs to attach its own data puts the library-owned document inside its own envelope.

```json
{
  "hostSpecificThing": [ ],
  "definition": { }
}
```

The nested document is identical across hosts, and non-.NET consumers generate their types from the
published schema instead of hand-mirroring the model. Treat the format as a public contract — see
[ADR 0005](../adr/0005-the-library-owns-a-versioned-payload-format.md).

The schema ships inside the `Bpmn.Model` package at `schema/bpmn-payload.schema.json`, and lives in the
repository at `src/Bpmn.Model/schema/`. `BpmnPayloadFormat.Version` is the format version, which is
**not** the package version: pin to the former.

Three things a consumer generating types should know, all of them described in the schema itself:

- **Enums are integers.** The model declares no `JsonStringEnumConverter`, so every enum crosses the
  wire as a number. The schema publishes the names alongside as `x-enumNames`.
- **Properties are camelCase**, uniformly. Every serialized property declares its wire name explicitly
  rather than relying on a default, and a test fails on any that does not — generating the schema found
  53 properties that had drifted to PascalCase, and they were normalized before the format was frozen.
  See ADR 0005.
- **There are three roots.** The schema root uses a `oneOf` over `bpmnDefinitions`,
  `bpmnExecutionState`, and `bpmnWorkBinding`. Each is also listed under `x-roots` for code generators.
  Format 1.2.0 adds the binding root as a union of seven cases selected by `kind`; existing definition
  and execution-state shapes are unchanged. See [ADR 0007](../adr/0007-work-bindings-have-a-json-contract.md).
  Format 1.3.0 adds optional properties only: `conditionExpression` on a sequence
  flow (the `bpmnConditionExpression` shape) and `expressionLanguage` on the definitions and on each
  process. A 1.2.0 payload reads as before, with them absent. Format 1.4.0 appends one diagnostic
  kind, `ConditionEvaluated` (24), which execution state records for every condition expression the
  host evaluated; no existing value is renumbered. See
  [ADR 0011](../adr/0011-hosts-evaluate-condition-expressions-through-a-synchronous-port.md). Format
  1.5.0 appends two more, `ErrorThrown` (25) and `ErrorCaught` (26), recorded when
  an error end event throws and when an error catcher takes an error. See
  [ADR 0012](../adr/0012-hosts-and-error-end-events-name-bpmn-errors.md). Format 1.6.0 adds one optional
  property, `script` on an element (the `bpmnScript` shape), which carries a script task's `scriptFormat`
  and `<script>` body. A 1.5.0 payload reads as before, with it absent. Format 1.7.0 adds the
  `signalPublish` work-binding case, the broadcast a signal throw or signal end event binds. Format 1.8.0
  adds the `timerDateWait` case, the wait a timer at a `<timeDate>` binds. Format 1.9.0
  adds the `timerCycleWait` case, the wait a timer on a `<timeCycle>` binds. See
  [ADR 0007](../adr/0007-work-bindings-have-a-json-contract.md). Format 1.10.0, the current version,
  adds optional `valueNamespaces` to retained attributes. Bound and observed unbound prefixes in
  recognized QName values survive JSON transfer; older payloads omit that metadata. Consumers with
  an older closed schema must upgrade. See
  [ADR 0006](../adr/0006-vendor-extensions-are-retained-as-typed-data.md#2026-10-25-amendment-namespace-provenance-in-attribute-values).

Regenerate the schema after any model change:

```bash
dotnet run --project tools/Bpmn.Schema.Generator -- .
```

## `Bpmn.Interchange`

`BpmnXmlReader` (`Read`, `Analyze`) and `BpmnXmlWriter` (`Write`), plus:

- **Results**: `BpmnImportResult`, `BpmnImportAnalysis`, `BpmnImportIssue`,
  `BpmnImportIssueSeverity`, `BpmnRetainedElement`.
- **Options**: `BpmnImportOptions`, `BpmnExportOptions`.
- **Bindings**: the `BpmnWorkBinding` family and `BpmnBindingSlot`.
- **Failure**: `BpmnInterchangeException`, raised only for a document that cannot be read at all.

Both the reader and the writer are ordinary instantiable classes holding no state between calls.

See [Reading and writing BPMN XML](../guides/reading-and-writing-bpmn.md).

## `Bpmn.Semantics`

- **Entry points**: `BpmnInterpreter` (`CreateDefault`, `Start`, `OnWorkCompleted`,
  `OnWorkFaulted`, `OnWorkSignalled`).
- **Escalation signals**: `BpmnInterpreter.EscalationSignalCode`, `EscalationPayload` and
  `TryReadEscalationCode`; see [Errors and escalation](../concepts/errors-and-escalation.md#escalation-crosses-scopes-by-signalling).
- **Requests**: `BpmnStartRequest`, `BpmnWorkCompletedRequest`, `BpmnWorkFaultedRequest`,
  `BpmnWorkSignalledRequest`, `BpmnStartSignal`.
- **Graph**: `BpmnGraph`, `BpmnBoundWork`.
- **Results**: `BpmnEvaluation` (including its nullable `ErrorDisposition`), `BpmnContinuation`,
  `BpmnErrorDisposition`.
- **Commands**: `BpmnHostCommand.StartWork`, `.CancelWorkSubtree`, `.SignalEnclosingScope`.
- **Host contract**: `BpmnHostSnapshot`, `BpmnLiveWork`, `BpmnIterationScope`,
  `IBpmnVariableReader`, `BpmnNoVariables`.
- **Capabilities**: `BpmnHostCapabilities`, `BpmnCapabilityRequirements`, `BpmnCapabilityException`.
- **Failure**: `BpmnExecutionException`.

No clock, no I/O, no threads, no dependency injection, no persistence. See
[Hosting the interpreter](../guides/hosting-the-interpreter.md).

## `Bpmn.Runtime.InMemory`

`InMemoryBpmnHost` and its virtual clock. Non-durable, single-process, and explicitly not for
production — it exists so nobody has to write a host before they can see the library work, and so the
interpreter has something to be tested against. See
[Simulating a process](../guides/simulating-a-process.md).

## Versioning and feeds

All four packages are versioned and released together from a single base version, so matching
versions across the set is always correct.

- **Releases** go to NuGet.org, versioned from the release tag.
- **Previews** are built from `main` as `<base>-preview.<build>` and pushed to a Feedz.io feed:
  `https://f.feedz.io/valence-works/bpmn/nuget/index.json`.

Packages are deterministic builds, carry SourceLink metadata, and ship `.snupkg` symbol packages —
you can step into the library from an application that does not have the source checked out.

## Related

- [Getting started](../guides/getting-started.md)
- [Supported BPMN constructs](supported-constructs.md)
- [ADR 0001: BPMN semantics ship as a host-agnostic library](../adr/0001-bpmn-semantics-ship-as-a-host-agnostic-library.md)
- [ADR 0005: The library owns a versioned payload format](../adr/0005-the-library-owns-a-versioned-payload-format.md)
