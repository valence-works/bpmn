# Building a process in code

You do not need XML to get a process. `Bpmn.Model` is a plain object model with public constructors,
so a definition can be built in code and handed straight to the writer or the interpreter.

This is the path for generated processes, for test fixtures, and for tools that produce BPMN rather
than consume it.

## The smallest useful process

Start, one task, end.

```csharp
using Bpmn.Model;

var process = new BpmnProcessDefinition(
    ProcessId: "order-approval",
    Name: "Order approval",
    IsExecutable: true,
    Elements:
    [
        new BpmnElement("start", BpmnElementTypes.StartEvent),
        new BpmnElement("approve", BpmnElementTypes.UserTask, name: "Approve order", bindingRef: "task:approve"),
        new BpmnElement("end", BpmnElementTypes.EndEvent)
    ],
    SequenceFlows:
    [
        new BpmnSequenceFlow("f1", "start", "approve"),
        new BpmnSequenceFlow("f2", "approve", "end")
    ]);

var definitions = new BpmnDefinitions(
    Id: "definitions-1",
    TargetNamespace: "https://example.com/processes",
    Processes: [process]);
```

Two things in there are load-bearing.

**`bindingRef` is the work.** It is an opaque host key naming the work the element runs. The
interpreter compares and echoes it; it never parses it. Gateways and plain start and end events have
no `bindingRef` because the interpreter handles them entirely on its own — they bind no work.

**Ids are yours.** `elementId` must be unique within its process, `flowId` within its flows, and
`sourceRef` / `targetRef` must name elements that exist. The constructors reject empty ids, but they
do not check that the graph hangs together; that is the graph builder's job. See
[Hosting the interpreter](hosting-the-interpreter.md).

## Branching

A sequence flow is conditional when it carries a `conditionOutcome`. The flow is taken when the
source element's completed work reported that outcome name. The library evaluates no expressions:
outcomes are strings the host supplies on completion. See
[Interpreter, not engine](../concepts/interpreter-not-engine.md#no-expressions).

```csharp
var elements = new List<BpmnElement>
{
    new("start", BpmnElementTypes.StartEvent),
    new("check", BpmnElementTypes.ServiceTask, name: "Check credit", bindingRef: "task:check-credit"),
    new("gate", BpmnElementTypes.ExclusiveGateway, name: "Approved?", defaultFlowId: "to-reject"),
    new("ship", BpmnElementTypes.ServiceTask, name: "Ship", bindingRef: "task:ship"),
    new("reject", BpmnElementTypes.EndEvent, name: "Rejected"),
    new("done", BpmnElementTypes.EndEvent, name: "Shipped")
};

var flows = new List<BpmnSequenceFlow>
{
    new("f1", "start", "check"),
    new("f2", "check", "gate"),
    new("to-ship", "gate", "ship", conditionOutcome: "Approved"),
    new("to-reject", "gate", "reject", isDefault: true),
    new("f3", "ship", "done")
};
```

`defaultFlowId` on the gateway and `isDefault` on the flow are two sides of the same statement, and
both are set. The default flow is taken only when no conditional flow matched.

## Events

Event semantics come from an element's `EventDefinitions`. The type strings are constants on
`BpmnEventDefinitionTypes`, and any extra data lives in the definition's `Properties`, keyed by the
constants on `BpmnEventDefinitionProperties`.

```csharp
// A message start event.
var messageStart = new BpmnElement(
    "start", BpmnElementTypes.StartEvent,
    bindingRef: "listener:order-placed",
    eventDefinitions:
    [
        new BpmnEventDefinition(
            BpmnEventDefinitionTypes.Message,
            new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = "OrderPlaced" })
    ]);

// A timer start event with a recurring interval.
var timerStart = new BpmnElement(
    "nightly", BpmnElementTypes.StartEvent,
    bindingRef: "listener:nightly",
    eventDefinitions:
    [
        new BpmnEventDefinition(
            BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string> { [BpmnEventDefinitionProperties.Interval] = "PT24H" })
    ]);

// A terminate end event: ends the process, discarding every remaining token.
var terminate = new BpmnElement(
    "abort", BpmnElementTypes.EndEvent,
    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Terminate)]);
```

`Interval` (an ISO-8601 duration) and `Cron` are mutually exclusive on a timer.

## Boundary events

A boundary event is an element with an `attachedToRef` and a `cancelActivity` flag.

```csharp
var task = new BpmnElement("call-customer", BpmnElementTypes.UserTask, bindingRef: "task:call");

// Interrupting: the timer fires, the task is torn down, the boundary path runs.
var timeout = new BpmnElement(
    "timeout", BpmnElementTypes.BoundaryEvent,
    attachedToRef: "call-customer",
    cancelActivity: true,
    bindingRef: "timer:2h",
    eventDefinitions:
    [
        new BpmnEventDefinition(
            BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string> { [BpmnEventDefinitionProperties.Interval] = "PT2H" })
    ]);

// Non-interrupting: the reminder path runs alongside the still-running task.
var reminder = new BpmnElement(
    "reminder", BpmnElementTypes.BoundaryEvent,
    attachedToRef: "call-customer",
    cancelActivity: false,
    bindingRef: "timer:30m",
    eventDefinitions:
    [
        new BpmnEventDefinition(
            BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string> { [BpmnEventDefinitionProperties.Interval] = "PT30M" })
    ]);
```

`cancelActivity: true` is the BPMN default and is what the constructor assumes. Error boundary events
are always interrupting — BPMN fixes that, and it is not a choice you get to make. See
[Errors and escalation](../concepts/errors-and-escalation.md).

## Multi-instance

Loop characteristics go on the host element, which must bind work:

```csharp
var reviewEach = new BpmnElement(
    "review", BpmnElementTypes.SubProcess,
    bindingRef: "subprocess:review",
    loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: 3));
```

Each instance gets a per-iteration frame seeding a zero-based `loopIndex`
(`BpmnLoopCharacteristics.LoopIndexVariable`). `isSequential: false` schedules all instances up front;
`true` runs one at a time.

Collection mode — `collectionVariable` instead of `cardinality` — is modeled for authoring but is not
executable: the graph builder rejects it and the importer degrades it. Exactly one of `cardinality`
and `collectionVariable` may be set.

## Compensation, transactions and event subprocesses

These are element flags rather than separate types.

```csharp
// A compensation handler: bound work, no sequence flows, invoked only by compensation replay.
var undo = new BpmnElement(
    "undo-charge", BpmnElementTypes.ServiceTask,
    bindingRef: "task:refund",
    isForCompensation: true);

// The boundary event that associates a host activity with that handler.
var compensateOn = new BpmnElement(
    "charge-compensation", BpmnElementTypes.BoundaryEvent,
    attachedToRef: "charge-card",
    compensationHandlerElementId: "undo-charge",
    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation)]);

// A transaction subprocess: may be cancelled from within by a cancel end event.
var booking = new BpmnElement(
    "booking", BpmnElementTypes.SubProcess,
    bindingRef: "subprocess:booking",
    isTransaction: true);

// An event subprocess triggered by an error: no flows, activated by its start event.
var onFailure = new BpmnElement(
    "on-failure", BpmnElementTypes.SubProcess,
    bindingRef: "subprocess:on-failure",
    triggeredByEvent: true);
```

A transaction may not also carry loop characteristics. An event subprocess participates in no
sequence flows, hosts no boundary event, and is neither a compensation handler nor a transaction.

An event subprocess whose start trigger is a message, signal or timer additionally needs a
`listenerBindingRef`: the work armed when the enclosing scope starts, so the trigger can be observed
while the scope runs. `bindingRef` binds the body; `listenerBindingRef` binds the listener. Error- and
escalation-triggered event subprocesses are dormant catchers and need no listener, so theirs stays
null.

A given binding ref is bound either as some element's `BindingRef` or as some element's
`ListenerBindingRef` — never both, never twice.

## Variables

Variable declarations are container-scoped and carry a name, an optional host-interpreted type hint,
and an optional default:

```csharp
var process = new BpmnProcessDefinition(
    ProcessId: "order-approval",
    Variables:
    [
        new BpmnVariableDeclaration("orderId", TypeHint: "string"),
        new BpmnVariableDeclaration("lineCount", TypeHint: "integer")
    ]);
```

The semantics core reads only the name. What a type hint means is entirely the host's business.

## Adding layout

Layout is optional — a definition with no diagram is valid and interprets fine. Add one when the
output is meant for a human's modeling tool:

```csharp
var plane = new BpmnPlane(
    id: "plane-1",
    bpmnElementRef: "order-approval",
    shapes:
    [
        new BpmnShape("s-start", "start", new BpmnBounds(160, 100, BpmnLayoutDefaults.EventSize, BpmnLayoutDefaults.EventSize)),
        new BpmnShape("s-approve", "approve", new BpmnBounds(260, 78, BpmnLayoutDefaults.TaskWidth, BpmnLayoutDefaults.TaskHeight)),
        new BpmnShape("s-end", "end", new BpmnBounds(440, 100, BpmnLayoutDefaults.EventSize, BpmnLayoutDefaults.EventSize))
    ],
    edges:
    [
        new BpmnEdge("e-f1", "f1", [new BpmnPoint(196, 118), new BpmnPoint(260, 118)]),
        new BpmnEdge("e-f2", "f2", [new BpmnPoint(360, 118), new BpmnPoint(440, 118)])
    ]);

var withLayout = definitions with { Diagrams = [new BpmnDiagram("diagram-1", null, plane)] };
```

`BpmnLayoutDefaults` carries the sizes the library itself uses when synthesizing layout, which makes
generated diagrams look consistent with imported ones.

## Editing an existing definition

`BpmnDefinitions`, `BpmnProcessDefinition`, and the DI types are records, so edits are `with`
expressions and the original is untouched:

```csharp
var updated = definitions with
{
    Processes = definitions.Processes
        .Select(p => p.ProcessId == "order-approval"
            ? p with { Elements = [.. p.Elements, newElement] }
            : p)
        .ToList()
};
```

`BpmnElement`, `BpmnSequenceFlow`, `BpmnEventDefinition`, `BpmnPool`, `BpmnLane` and
`BpmnMessageFlow` are immutable classes rather than records — replace them by construction.

## Writing it out

```csharp
using Bpmn.Interchange;

File.WriteAllText("generated.bpmn", BpmnXmlWriter.Write(definitions));
```

A definition built in code and a definition read from XML are the same type, so everything on
[Reading and writing BPMN XML](reading-and-writing-bpmn.md) applies to both.

## Related

- [Supported BPMN constructs](../reference/supported-constructs.md) — what you can express.
- [Simulating a process](simulating-a-process.md) — run what you just built.
- [Hosting the interpreter](hosting-the-interpreter.md) — turn a definition into an executable graph.
