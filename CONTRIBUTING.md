# Contributing

Thank you for considering a contribution. This document covers what the project expects, what the
build enforces, and how to get a change through review without surprises.

## Ground rules

Three rules shape almost every review comment. They are not style preferences.

1. **No host dependencies.** The library must not name, depend on, or assume any particular runtime,
   framework, product, or execution environment. This applies to code, identifiers, comments, string
   literals, documentation, and test data.
2. **No external NuGet dependencies in shipped packages.** All four packages depend on the base class
   library and on each other. Nothing else.
3. **The semantics core stays pure.** No clock, no I/O, no threads, no dependency injection, no
   persistence, no randomness, no ambient state. If a change to `Bpmn.Semantics` needs any of those,
   the change is in the wrong place.

Rules 1 and 2 are checked by CI. Rule 3 is checked by review, and by the fact that violating it is
hard when the entry points are synchronous and return commands.

## Repository layout

```text
src/Bpmn.Model/             The neutral object model and execution-state records
src/Bpmn.Interchange/       BPMN 2.0 XML reading and writing
src/Bpmn.Semantics/         The token-semantics interpreter
src/Bpmn.Runtime.InMemory/  The reference host
tests/                      Model, interchange, semantics, conformance and architecture tests
samples/                    Runnable programs that double as documentation
docs/                       Documentation, published to the wiki
docs/adr/                   Architecture decision records
```

## Building and testing

```bash
dotnet restore Bpmn.slnx
dotnet build Bpmn.slnx --configuration Release
dotnet test  Bpmn.slnx --configuration Release
```

The solution targets `net8.0` and `net10.0`. You need both SDKs installed to build everything, and
a change that compiles on one target but not the other is a broken change.

## What CI checks

| Job | What it does | How to reproduce locally |
| --- | --- | --- |
| Build & Test | Restores, builds and tests the solution in Release. | The three commands above. |
| Samples run | Runs every program under `samples/` with `--non-interactive`. | `dotnet run --project samples/<name> -- --non-interactive` |
| Host-agnostic guard | Greps the tree for references to a specific host product, in any file type. | Read the workflow; the pattern is in `.github/workflows/ci.yml`. |

Two things follow from the samples job. Every sample must accept `--non-interactive` and must exit
zero without human input. And samples are not decoration: they are compiled and executed on every
pull request, which is what stops them rotting into snippets that no longer build.

The host-agnostic guard is deliberately paired with an architecture test. The architecture test
asserts the shipped assemblies reference only the base class library and each other; the grep catches
prose, identifiers and literals. An assembly test cannot catch a doc comment and a text search cannot
catch a transitive package edge, so both exist.

## Adding a dependency

Don't, in a shipped project. Package versions are centrally managed in `Directory.Packages.props`,
which is split into build-time-only and test-only sections for exactly this reason. A test-only
dependency is fine. A `PackageReference` from `src/` is a design problem, and the fix is the design.

## Code style

`.editorconfig` is authoritative and `EnforceCodeStyleInBuild` is on, so most of this is mechanical.
The parts worth stating anyway:

- Nullable reference types are enabled everywhere. Do not suppress; model the absence.
- Public model types are immutable — records, or classes with get-only properties and a
  `[JsonConstructor]`. Collection properties are never null; they default to empty.
- Collections are exposed as `IReadOnlyList<T>` / `IReadOnlyCollection<T>` / `IReadOnlyDictionary<K,V>`.
- Element and event types are string constants, not enums, so a new BPMN type can be added without
  breaking the serialized format.
- Public members get XML documentation. Missing docs warn rather than fail, but reviewers will ask.
- Explain *why*, not *what*. A comment restating the code is noise; a comment recording the trade-off
  that produced the code is the reason anyone can change it later.

## Tests

- `Bpmn.Model.Tests` — the object model and its serialized shape.
- `Bpmn.Interchange.Tests` — reading, writing, round-trip fidelity and import diagnostics.
- `Bpmn.Semantics.Tests` — the interpreter, driven directly with no host.
- `Bpmn.Conformance.Tests` — BPMN construct behavior end to end.
- `Bpmn.Architecture.Tests` — the dependency envelope and the purity constraints.

An interpreter change should come with a test written in the interpreter's own terms: feed a
definition, a state, a snapshot and an event; assert on the returned state, continuation and
commands. If a test needs a host, a scheduler or a clock to express what it is checking, the
behavior under test has probably escaped the semantics core.

A behavior change to round-trip fidelity should come with a document that round-trips.

## Documentation

`docs/` is synced to the GitHub wiki on every push to `main` by
`.github/workflows/publish-wiki.yml`. The wiki is a flat namespace, so
`.github/scripts/sync-wiki.py` flattens nested paths into `Folder-Page` names and rewrites links.
That imposes a few authoring rules:

- **Exactly one H1 per page.** The H1 becomes the page's title and its sidebar label, so it should
  read well as a navigation entry.
- **Use normal relative links** between files under `docs/`. They are rewritten to flattened wiki
  names automatically and work in both places.
- **Do not relatively link to files outside `docs/`.** Those links are not rewritten and will break
  on the wiki. Use an absolute GitHub URL instead.
- **Keep to the established folders**: `guides/`, `concepts/`, `reference/`, `adr/`. The sidebar is
  generated from them, in that order.
- `docs/index.md` is the wiki home page.

Do not hand-edit the wiki. It is overwritten on the next sync.

## Architecture decision records

Anything that changes the shape of the library — the package split, the host port, the capability
model, the fidelity contract — belongs in `docs/adr/` before it belongs in `src/`. Records are
numbered, dated and given a status; superseding a record is normal, editing away a decision that was
actually made is not.

Start from [ADR 0001](docs/adr/0001-bpmn-semantics-ship-as-a-host-agnostic-library.md) and
[ADR 0002](docs/adr/0002-the-host-port-is-synchronous-and-command-returning.md); they establish the
boundary everything else is argued against.

## Pull requests

- One concern per pull request. A refactor and a behavior change in the same diff cannot be reviewed.
- Describe the behavior before and after, not the files touched.
- Say which of the three ground rules the change comes near, if any.
- Public API additions should be justified in the description. The public surface is the thing that
  is expensive to get wrong.

## Reporting a bug in the interpreter

An interpreter evaluation is fully determined by four values: the definition, the prior state, the
host snapshot, and the event. All four serialize to JSON. A bug report containing those four values
is directly reproducible and can be turned into a regression test without a running host, which makes
it far more useful than a description of what your system did.

## License

Contributions are accepted under the [MIT License](LICENSE). By submitting a contribution you agree
that it may be distributed under those terms.
