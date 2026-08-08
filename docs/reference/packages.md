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
  `BpmnForeignChild`, `BpmnDocumentation`, `BpmnFidelity`.
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
publishes a JSON Schema for it as a build artifact. Hosts **wrap** rather than replace it: a host that
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
- **Requests**: `BpmnStartRequest`, `BpmnWorkCompletedRequest`, `BpmnWorkFaultedRequest`,
  `BpmnWorkSignalledRequest`, `BpmnStartSignal`.
- **Graph**: `BpmnGraph`, `BpmnBoundWork`.
- **Results**: `BpmnEvaluation`, `BpmnFaultEvaluation`, `BpmnContinuation`, `BpmnErrorDisposition`.
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
