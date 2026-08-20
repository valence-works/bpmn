# Supported BPMN constructs

An honest inventory of what the library does with each BPMN 2.0 construct. The point of this page is
the "no" column.

## How to read it

| Status | Meaning |
| --- | --- |
| **Supported** | Modeled, round-tripped, and interpreted with BPMN's meaning. |
| **Degraded** | Read, but in a reduced form, with an element-scoped `Degraded` finding saying what it became. |
| **Retained** | Modeled and round-tripped, but carries no executable meaning. It survives; it does nothing. |
| **Dropped** | Not represented. The reader emits a `Dropped` finding naming the element, and the sequence flows that referenced it cascade-drop with it. |

Nothing is dropped silently. Every row below that is not "Supported" produces a finding you can
inspect before committing an import — see
[Reading and writing BPMN XML](../guides/reading-and-writing-bpmn.md#diagnostics).

Support here is **before capabilities**: a supported construct is still refused at graph-build time
if your host has not declared what it needs. See [Host capabilities](../concepts/capabilities.md).

## Activities

| Construct | Status | Notes |
| --- | --- | --- |
| `task` | Supported | Abstract task; binds work and completes when the work completes. |
| `userTask`, `serviceTask`, `manualTask`, `businessRuleTask` | Supported | The element is supported. What it *does* is the host's, bound through an `UnboundTask` work binding. |
| `scriptTask` | Supported | The element is supported. The **script is not executed** — the host owns the script engine. |
| `businessRuleTask` | Supported | The **decision is not evaluated**; there is no DMN here. |
| `sendTask` | Supported | Publishes a named message and continues. |
| `receiveTask` | Supported | Waits for a named message. |
| `callActivity` | Supported | Behaves as a member of the task family; the host resolves and runs the called process. A `calledElement`-less call activity reads with an `Info` finding. |
| `callActivity` failure mapping | Supported | The outcomes `Faulted`, `DispatchFailed` and `Cancelled` are translated into BPMN error handling instead of ordinary flow, so an enclosing error catcher can take them. |
| `subProcess`, embedded | Supported | The nested body travels as a `NestedProcess` work binding. |
| `subProcess`, event (`triggeredByEvent`) | Supported | Error and escalation triggers are dormant catchers. Message, signal and timer triggers additionally get a `ListenerBindingRef`: work armed at scope start so the trigger can be observed while the scope runs. |
| `transaction` | Supported | Modeled as `subProcess` with `isTransaction: true`. May not also carry loop characteristics. |
| Event subprocess with more than one start event | Dropped | The body must declare exactly one. |
| Second error-triggered event subprocess in one scope | Dropped | At most one. The same rule applies to a second code-less catch-all escalation event subprocess, and to two event subprocesses claiming the same escalation code. |
| Non-interrupting error event subprocess | Dropped | BPMN does not allow it. |
| `adHocSubProcess` | Dropped | Not part of the model the reader builds. |

## Gateways

| Construct | Status | Notes |
| --- | --- | --- |
| `exclusiveGateway` | Supported | Routes exactly one outgoing flow. Default flow honored. |
| `parallelGateway` | Supported | Splits into all outgoing flows; joins when all incoming arrive. |
| `inclusiveGateway` | Supported | Splits down every matching flow; joins the branches that actually ran. |
| `eventBasedGateway` | Supported | Opens a first-catch-wins race across its outgoing catch events. The winner routes; every losing member token is cancelled and its armed child subtree torn down. |
| `complexGateway` | Dropped | Not part of the model the reader builds. |

## Events

### Start events

| Construct | Status | Notes |
| --- | --- | --- |
| None (plain) | Supported | |
| Message, signal | Supported | Matched on the resolved message or signal **name**. An unresolvable `messageRef` / `signalRef` degrades to a plain start. |
| Timer | Supported | Must be a **recurring schedule**: an ISO-8601 `interval` or a `cron` expression. A timer start that is not recurring degrades. |
| Error (event subprocess body only) | Supported | An error-triggered event subprocess. |
| Escalation (event subprocess body only) | Supported | |
| More than one event definition | Degraded | Reads with one trigger; the finding says which. |
| Conditional, link, and every other definition | Degraded | Reported as an unsupported definition and read as a plain start event. |

### End events

| Construct | Status | Notes |
| --- | --- | --- |
| None (plain) | Supported | Consumes a token; the process completes when none remain. |
| Terminate | Supported | Ends the process, discarding remaining tokens. Late child completions are ignored. |
| Message | Supported | Publishes by name and ends. An unresolvable name degrades to a plain end event. |
| Escalation | Supported | **Requires** a code; a ref-less escalation end is dropped. |
| Cancel | Supported | Only inside a transaction. Outside one it degrades to a plain end event. |
| Compensation | Supported | Triggers compensation replay; `activityRef` narrows the target, and an `activityRef` naming an element with no compensation boundary is dropped. |
| **Error** | Degraded | There is no error end event. It reads as a plain end event with a finding. Raise an error by faulting the bound work instead. |
| Signal, conditional, link | Degraded | Reported as unsupported definitions; reads as a plain (or terminate) end event. |

### Intermediate events

| Construct | Status | Notes |
| --- | --- | --- |
| Catch: timer | Supported | Requires a one-shot `<timeDuration>`. A timer catch without one is dropped. |
| Catch: message, signal | Supported | Dropped when the name cannot be resolved. |
| Catch: anything else, or more than one definition | Dropped | |
| Throw: message | Supported | Publishes by name; dropped when the name cannot be resolved, because a throw must say what it publishes. |
| Throw: escalation | Supported | Requires a code; a ref-less escalation throw is dropped. |
| Throw: compensation | Supported | A ref-less throw compensates everything registered; an `activityRef` is kept only when it names an element carrying a compensation boundary. |
| Throw: signal, and everything else | Dropped | Only message, escalation and compensate throws are supported. |

## Boundary events

| Construct | Status | Notes |
| --- | --- | --- |
| Interrupting (`cancelActivity: true`) | Supported | The BPMN default. Terminates the host activity and routes the boundary path. |
| Non-interrupting (`cancelActivity: false`) | Supported | The boundary path runs alongside the still-running activity. |
| Error | Supported | **Always interrupting** — BPMN fixes this, and a non-interrupting error boundary is dropped. A boundary with no code catches any error. |
| Escalation | Supported | May be non-interrupting. A code-less boundary is the catch-all; a second code-less one on the same host, or a duplicate code, is dropped. |
| Timer | Supported | Requires a one-shot `<timeDuration>`; without one it is dropped. |
| Message, signal | Supported | Dropped when the name cannot be resolved. |
| Cancel | Supported | Only on a transaction host, and at most one per transaction. |
| Compensation | Supported | Must associate with a flow-less compensation handler activity, or it is dropped. |
| Conditional, and any other definition | Dropped | |
| No `attachedToRef`, or attached to a non-activity | Dropped | A boundary event needs a host that binds work to interrupt. |
| More than one event definition | Dropped | |

## Flow and connectivity

| Construct | Status | Notes |
| --- | --- | --- |
| `sequenceFlow` | Supported | Dropped when it is missing `sourceRef` / `targetRef`, or when either endpoint was dropped. |
| Default flow | Supported | Taken only when no conditional flow matched. Declared on both sides: `defaultFlowId` on the element, `isDefault` on the flow. |
| Conditional flow, **outcome-based** | Supported | A flow with a `ConditionOutcome` is taken when the source element's completing work reported that outcome name. |
| Conditional flow, **expression-based** | Degraded | A `<conditionExpression>` is not evaluated; the flow reads as unconditional and the finding quotes the expression. This is a permanent design position, not a gap — see [Interpreter, not engine](../concepts/interpreter-not-engine.md#no-expressions). |
| Cyclic flow (loop-backs) | Supported | A token traversing a backward flow mints a fresh iteration key; join accounting groups arrivals by element *and* iteration, so a revisited join never conflates one pass with the next. |
| `association` | Supported | Read to associate a compensation boundary event with its handler. |
| `textAnnotation`, `group` | Dropped | Reported and dropped; they carry no executable meaning. |

## Multi-instance and loops

| Construct | Status | Notes |
| --- | --- | --- |
| Multi-instance, **cardinality**, parallel | Supported | All instances scheduled up front. Requires a positive integer `<loopCardinality>`. |
| Multi-instance, **cardinality**, sequential | Supported | One instance at a time; the next starts when the previous completes. |
| Multi-instance, **collection** | Supported | The collection is read once at loop start and snapshotted; each instance is seeded with its item. The named collection must be a declared container-scoped variable of the process, or the graph build throws. Needs `ScopeVariables`. |
| Per-iteration `loopIndex` | Supported | Zero-based, seeded into every instance's frame. |
| Loop characteristics on a non-activity | Degraded | BPMN allows them only on activities. |
| Item variable named `loopIndex` | Degraded | The key is reserved. |
| `standardLoopCharacteristics` (while / until) | Degraded | Not represented. Express a loop with a cyclic sequence flow instead. |
| Multi-instance completion condition | Not modeled | |

Exactly one of a cardinality and a collection variable may be set; any other shape is rejected at
graph-build time.

## Compensation and transactions

| Construct | Status | Notes |
| --- | --- | --- |
| Compensation handler (`isForCompensation`) | Supported | Binds work, participates in no sequence flows, invoked only by compensation replay. A handler referenced by no compensation boundary is dropped. |
| Compensation registration | Supported | A successful completion of an activity carrying a compensation boundary is logged. |
| Reverse-order replay | Supported | Last registered, first compensated. Entries are never pruned; a claimed entry whose run is torn down before it ran is released back to registered. |
| Targeted compensation (`activityRef`) | Supported | Compensates only that element's registrations; absent means everything registered in the process. |
| Transaction subprocess | Supported | Cancel from within stops and tears down other live work, replays compensations, and completes with the `Cancelled` outcome. A transaction that completes `Cancelled` with no cancel boundary attached raises a fault. |

## Collaboration

| Construct | Status | Notes |
| --- | --- | --- |
| `participant` (pool), white-box | Retained | Modeled with its `processRef`. Visual and organizational; carries no executable semantics. A `processRef` naming no process in the document degrades. |
| `participant` (pool), black-box | Retained | No `processRef`; recorded as an `Info` finding. |
| `lane`, `laneSet` | Retained | Elements carry a `LaneId`. Visual and organizational. |
| `messageFlow` | Retained | Endpoints, pools and message name are recorded so cross-pool wiring survives the round-trip and surfaces as a finding. The graph validator ignores it and it is stripped from the compiled structure — execution rides name-keyed messaging instead. |
| Choreography, conversation | Dropped | Not part of the model the reader builds. |

## Data

| Construct | Status | Notes |
| --- | --- | --- |
| Process variable declarations | Supported | Name, optional host-interpreted type hint, optional JSON default. The semantics core reads only the name. |
| `dataObject`, `dataObjectReference` | Dropped | Reported by element id. |
| `dataStore`, `dataStoreReference` | Dropped | Reported by element id. |
| `dataInputAssociation`, `dataOutputAssociation`, `ioSpecification` | Dropped | Reported by element id. |
| Item definitions and typed data | Not modeled | Values cross the boundary as JSON with a host-interpreted type hint. The library assigns no meaning to a type system. |

## Document-level

| Construct | Status | Notes |
| --- | --- | --- |
| `<definitions>` metadata | Supported | Id, target namespace, exporter, exporter version. |
| Root `<message>`, `<signal>`, `<error>`, `<escalation>` | Supported | Matching is on the **name** for messages and signals, and on the **code** for errors and escalations. |
| `<documentation>` | Supported | Retained, with `textFormat`. |
| `extensionElements` (any vendor) | Supported | `camunda:*`, `zeebe:*`, `flowable:*` and anything else are retained verbatim as data and written back at their recorded position. |
| Foreign attributes | Supported | Retained with their qualified names. |
| Unrecognized child elements | Supported | Retained with the index they occupied, because BPMN's `tFlowNode` is an `xsd:sequence` and re-emitting a child out of position produces schema-invalid XML. |
| BPMN DI (`BPMNDiagram`, `BPMNPlane`, `BPMNShape`, `BPMNEdge`, labels) | Supported | First-class in the model. The writer always emits complete layout: a shape per element, pool and lane, and an edge with at least two waypoints per flow, synthesized from the endpoint boxes when the source carried none. |
| `<import>` of another BPMN document | Dropped | Not modeled. |
| XSD schema validation | Not supported | Deliberately. The reader reports what it could not use, which is a different and more useful job. A document that is not readable at all raises `BpmnInterchangeException`. |
| Byte-exact round-trip | Not supported | Content is preserved; attribute order, whitespace, comments, CDATA-versus-text and namespace prefixes are not. |

## Not in scope at all

- **DMN.** Business rule tasks are modeled; decisions are not evaluated.
- **CMMN.**
- **Expression and script languages.** FEEL, JUEL, JavaScript, Groovy — none, permanently.
- **Rendering.** Layout is preserved and synthesized, not drawn.
- **Vendor server protocols.** This is not a client for anyone's engine.

## Related

- [Reading and writing BPMN XML](../guides/reading-and-writing-bpmn.md) — what a finding looks like.
- [Errors and escalation](../concepts/errors-and-escalation.md) — the five failure constructs, in
  detail.
- [Host capabilities](../concepts/capabilities.md) — why a supported construct can still be refused.
