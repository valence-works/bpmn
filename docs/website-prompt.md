# Lovable prompt: BPMN for .NET marketing site

**How to use this.** Copy everything inside the fenced block below (the whole thing, including the
C# samples) and paste it as a single message into Lovable's agent. It is written to be executed in
one pass. Afterwards, iterate with short follow-ups ("tighten the hero", "reduce the feature grid to
six cards") rather than re-pasting. The C# in the prompt is the API surface the site should show —
tell Lovable nothing else about the API, and it will not invent methods.

````text
Build a single-page marketing and documentation-entry website for an open-source .NET library
called **BPMN for .NET**. Repository: https://github.com/valence-works/bpmn. License: MIT.
Publisher: **Valence Works**. NuGet packages: `Bpmn.Model`, `Bpmn.Interchange`, `Bpmn.Semantics`,
`Bpmn.Runtime.InMemory`.

## Positioning — get this exactly right

It is a **BPMN interpreter, not a workflow engine.** It answers "what does this BPMN mean, and what
happens next." It never makes anything happen. The semantics core is a pure function: given a
process definition, the current token state, and one event, it returns the next state plus a list of
commands describing what the host should do. No clock, no I/O, no threads, no DI container, no
persistence. The site must lead with this distinction, not bury it.

## Site structure

One landing page, anchor-linked, in this order. Sticky top nav with the section links, a GitHub
link, and a theme toggle.

1. **Hero**
2. **The gap** — why this library exists
3. **What it is / What it is not** — two honest columns, side by side, equal visual weight
4. **Feature grid**
5. **Code** — three tabbed or stacked samples
6. **Who this is for** — four audience cards
7. **Packages** — four package cards
8. **Getting started**
9. **FAQ**
10. **Footer**

### 1. Hero

Pick one headline; keep the other two as commented-out alternatives in the code.

- "BPMN 2.0 for .NET. Read it, write it, understand it."
- "A BPMN interpreter for .NET. Not a workflow engine."
- "What does this BPMN mean? Ask the library."

Subhead: "A typed, immutable BPMN 2.0 object model, a content-lossless XML reader and writer, and a
deterministic token-semantics interpreter. MIT licensed. Zero dependencies. net8.0 and net10.0."

Primary CTA: "Get started" (anchors to Getting started). Secondary CTA: "View on GitHub" (links to
the repo, with a small GitHub mark). Under the CTAs, a single monospace line:
`dotnet add package Bpmn.Interchange` with a copy button.

### 2. The gap

State this plainly, as findings, not as a pitch. A survey of 88 C# repositories and 50 NuGet
packages found no maintained, license-clean .NET library that does BPMN 2.0 properly:

- The only full engine is GPL-3.0-or-later.
- The closest interchange library ships as a closed-source binary that hard-depends on
  `System.Drawing.Common`, so it throws on non-Windows.
- The Camunda and Zeebe .NET clients never parse BPMN at all. They ship `.bpmn` files as opaque
  blobs.
- No generated-from-XSD BPMN object model is published anywhere on NuGet.

Close with one sentence: "This is a verifiable hole in the ecosystem. That is the whole reason this
library exists."

### 3. What it is / What it is not

Two columns. The right column is a feature of the product, not a disclaimer — style it with the same
confidence as the left, no warning icons, no muted gray, no apology.

**What it is:** BPMN 2.0 XML reader and writer over a typed, immutable object model ·
content-lossless round-tripping, including foreign `extensionElements` (Camunda, Zeebe, Flowable)
and BPMN DI layout (shapes, edges, waypoints, label bounds) · an import analyzer with element-scoped
diagnostics on an Info / Degraded / Dropped ladder, where analyze and commit share one code path so
a dry run can never drift from the real one · a programmatic model builder · a token-semantics
interpreter · deterministic and synchronous · zero external dependencies on every shipped package.

**What it is not:** not a production workflow engine — no durable persistence, scheduling, retries,
message broker, job queue, distributed coordination, or transactions · it does not execute anything ·
not an expression or script evaluator — no FEEL, no JUEL, no JavaScript · not DMN or CMMN · not a
modeler or a renderer · not a Camunda, Zeebe, or Flowable client · not an XSD schema validator ·
not byte-exact on round-trip — content is preserved, formatting is not.

### 4. Feature grid

Six to eight cards, one short paragraph each: the immutable object model; lossless round-tripping
and foreign extension preservation; the diagnostics ladder with one shared analyze/commit path; BPMN
DI layout preservation; the programmatic builder; interpreter coverage (exclusive, parallel,
inclusive and event-based gateways; start, intermediate and end events; interrupting and
non-interrupting boundary events; embedded and event subprocesses; multi-instance; compensation;
transactions; escalation; cyclic flows); determinism ("any evaluation is reproducible from four JSON
values"); zero dependencies on net8.0 and net10.0.

### 5. Code

Three samples. Use this C# **verbatim** — do not modify, extend, or invent additional API. Label
them "Read a .bpmn file", "Build a process in code", "Simulate with a virtual clock".

```csharp
using Bpmn.Interchange;

// Analyze and Read share one code path, so a dry run cannot drift from the real one.
var result = new BpmnXmlReader().Read(File.ReadAllText("order-intake.bpmn"));

foreach (var issue in result.Analysis.Issues)
    Console.WriteLine($"{issue.Severity,-8} {issue.ElementId ?? "-",-24} {issue.Message}");

var definitions = result.Definitions;
Console.WriteLine($"{definitions.Processes.Count} process(es), {result.Analysis.Issues.Count} finding(s)");

// Vendor annotations other readers discard are still here.
foreach (var element in definitions.Processes.SelectMany(p => p.Elements))
    if (!element.Extensions.IsEmpty)
        Console.WriteLine($"{element.ElementId}: retained {string.Join(", ", element.Extensions.RetainedNamespaces())}");
```

```csharp
using Bpmn.Interchange;
using Bpmn.Model;

var definitions = new BpmnDefinitionsBuilder()
    .TargetNamespace("http://valence.works/orders")
    .Process("order-intake", process => process
        .StartEvent("start")
        .ExclusiveGateway("large-order")
        .UserTask("manual-review", "Manual review")
        .EndEvent("accepted")
        .Connect("start", "large-order")
        .Connect("large-order", "manual-review", condition: "large")
        .Connect("large-order", "accepted", isDefault: true)
        .Connect("manual-review", "accepted"))
    .Build();

// Layout is synthesized where the model carries none, and every edge gets at least
// two waypoints, so the output opens in a BPMN modeler without complaint.
File.WriteAllText("order-intake.bpmn", new BpmnXmlWriter().Write(definitions));
```

```csharp
using Bpmn.Interchange;
using Bpmn.Runtime.InMemory;

// A reference host: virtual clock, single process, nothing durable.
var definitions = new BpmnXmlReader().Read(File.ReadAllText("order-intake.bpmn")).Definitions;

var host = new InMemoryBpmnHost();
var instance = host.Start(definitions.Processes[0]);

instance.CompleteWork("node-manual-review");
instance.Clock.Advance(TimeSpan.FromDays(7)); // a seven-day timer resolves in microseconds

foreach (var work in instance.PendingWork)
    Console.WriteLine($"waiting on {work.ElementId}");

Console.WriteLine(instance.IsCompleted ? $"completed: {instance.Outcome}" : "still running");

// Every evaluation is recorded, so you can see exactly what the interpreter decided.
Console.WriteLine(instance.Transcript);
```

### 6. Who this is for

Four cards:

- **Engine builders** — you are writing the durable engine. Take the semantics; keep your own
  persistence, scheduling, and retries.
- **Tooling authors** — linters, converters, documentation generators, migration tools. Lossless
  round-tripping means your tool rewrites one attribute and leaves everything else untouched.
- **Analysis and simulation** — walk a process, check for deadlocks in CI, generate test paths,
  answer "can this ever reach that task", without standing up a runtime.
- **.NET teams needing BPMN interop** — you receive `.bpmn` files authored in Camunda, Zeebe, or
  Flowable Modeler and must read them in C# without losing vendor extensions.

### 7. Packages

Four cards, each with the package id in monospace, one line of description, and a copy-to-clipboard
`dotnet add package …` command.

- `Bpmn.Model` — the typed, immutable object model and execution state.
- `Bpmn.Interchange` — BPMN 2.0 XML reader, writer, import analyzer, and model builder.
- `Bpmn.Semantics` — the pure token-semantics interpreter.
- `Bpmn.Runtime.InMemory` — reference host with a virtual clock, for simulation and tests.

State once, near the cards: every shipped package has zero external dependencies and targets net8.0
and net10.0.

### 8. Getting started

Three numbered steps: install, read a file, inspect diagnostics. Reuse the first code sample rather
than writing a new one. End with a link to the repository README and to the samples folder.

### 9. FAQ

Six questions, short answers. Can I run production workflows on this? (No.) Does it evaluate
conditions? (No expression evaluator ships; conditions reach you as opaque strings to hand to your
own evaluator.) Will round-tripping change my file? (Content preserved, formatting not.) Does it
keep Camunda and Zeebe extensions? (Yes, including DI layout.) Does it support DMN? (No.) License?
(MIT.)

### 10. Footer

Repository link, NuGet links, license, "Published by Valence Works", copyright. No newsletter form,
no social icons beyond GitHub.

## Design direction

Technical, credible, developer-facing. Dark-mode-first with a working light mode and a toggle;
respect `prefers-color-scheme` on first load. Restrained palette: near-black or deep slate
background, off-white text, one accent color (cool cyan or amber) used only for links, active nav,
and token dots. Monospace for package ids, commands, code, and section eyebrows; a clean sans for
body. Generous whitespace, hairline borders instead of drop shadows.

Diagrams over decoration. One BPMN-flavored motif, used sparingly: a thin horizontal sequence flow
between a circle (event), a diamond (gateway), and a rounded rectangle (task), with a small
accent-colored token dot travelling along it. Inline SVG, animated slowly and only once per viewport
entry, static under `prefers-reduced-motion`. Hero and optionally one divider. Not every section.

Explicitly forbidden: stock photos of people in meetings, generic SaaS gradient meshes, blob shapes,
fake dashboard or product screenshots, 3D illustrations, emoji as icons, animated counters.

## Tone

Precise and unhyped. This audience is allergic to marketing language. Short declarative sentences.
Never use "revolutionary", "seamless", "empower", "unlock", "supercharge", "game-changing",
"effortless", "blazing fast". No exclamation marks. Do not describe the library as easy. The honesty
about what it is not is the marketing — write that section as confidently as the feature list.

## Technical constraints

Responsive from 320px up. WCAG AA contrast in both themes, keyboard navigable with visible focus
rings, semantic heading order with exactly one `h1`, real landmarks, `alt` text or `aria-hidden` on
every SVG. Fast: prefer a system font stack, no analytics, no third-party scripts, and therefore no
cookie banner. Syntax-highlighted C# code blocks, each with a copy button. Title, description,
canonical, and Open Graph / Twitter card meta tags. Favicon: an inline SVG of a monochrome BPMN
start-event glyph, a thin circle with a smaller filled dot.

## Do not invent

This is a young library. Fabricated social proof would be actively harmful. Do not put on the site:
adoption or download numbers, GitHub star counts, testimonials, quotes, customer or company logos,
benchmark figures, performance percentages, version numbers, release dates, roadmap promises, or any
claim that it is used in production anywhere. If a section feels empty without such content, leave
it out rather than filling it.
````
