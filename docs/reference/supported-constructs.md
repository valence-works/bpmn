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
| `scriptTask` | Supported | The element is supported. Its `scriptFormat` and `<script>` body are modeled as `BpmnElement.Script` and round-trip, but the **script is not executed** — the host owns the script engine. |
| `businessRuleTask` | Supported | The **decision is not evaluated**; there is no DMN here. |
| `sendTask` | Supported | Publishes a named message and continues. |
| `receiveTask` | Supported | Waits for a named message. |
| `callActivity` | Supported | Behaves as a member of the task family; the host resolves and runs the called process. A `calledElement`-less call activity reads with an `Info` finding. |
| `callActivity` failure mapping | Supported | The outcomes `Faulted`, `DispatchFailed` and `Cancelled` are translated into BPMN error handling instead of ordinary flow, so an enclosing error catcher can take them. A `Faulted` outcome carries the called process's error in `BpmnWorkCompletedRequest.ErrorCode`, so a coded catcher can match it; `DispatchFailed` and `Cancelled` carry no error and reach only catch-alls. The returned `BpmnEvaluation.ErrorDisposition` reports whether routing caught, propagated or absorbed the failure. |
| `subProcess`, embedded | Supported | The nested body travels as a `NestedProcess` work binding. |
| `subProcess`, event (`triggeredByEvent`) | Supported | Error and escalation triggers are dormant catchers. Message, signal and timer triggers additionally get a `ListenerBindingRef`: work armed at scope start so the trigger can be observed while the scope runs. A timer trigger is a `<timeDuration>`, a `<timeDate>` or a `<timeCycle>` (a `timerCycleWait`). A non-interrupting duration or cycle trigger re-arms after each fire, without end: a cycle's repetition count is carried to the host but not yet counted by the interpreter. A date trigger fires once. |
| `transaction` | Supported | Modeled as `subProcess` with `isTransaction: true`. May not also carry loop characteristics. |
| Event subprocess with more than one start event | Dropped | The body must declare exactly one. |
| Second code-less catch-all error event subprocess in one scope, or two claiming the same error code | Dropped | A scope may carry several error event subprocesses with distinct codes and at most one catch-all. The same rule applies to escalation event subprocesses. |
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
| Timer | Supported | A **recurring schedule** (`<timeCycle>`: an ISO-8601 repeating interval `R[n]/[start/]duration`, a bare duration, or a `cron` expression) or a **single start at a date** (`<timeDate>`: an ISO-8601 `date`). The cycle is kept as written in `cycle`, beside its `interval` or `cron`, its `repetitions` and its `start`. Exactly one nonblank `interval`, `cron` or `date`; graph build rejects a missing or ambiguous choice, and `BpmnTimerStartSchedule.Resolve` exposes the same rule to hosts (`Kind` is `Interval`, `Cron` or `Date`, with `Repetitions` and `Start` from the cycle; a host stops the trigger after `Repetitions` starts). A timer start with no usable cycle or date (a `<timeDuration>` start, or a cycle such as `R/<duration>/<end>`) degrades on import. |
| Error (event subprocess body only) | Supported | An error-triggered event subprocess. Its `errorRef` resolves to the code it catches: only that code, preferred over a catch-all in the same scope. One with no ref is the catch-all. |
| Escalation (event subprocess body only) | Supported | |
| More than one event definition | Degraded | Reads with one trigger; the finding says which. |
| Conditional, link, and every other definition | Degraded | Reported as an unsupported definition and read as a plain start event. |

### End events

| Construct | Status | Notes |
| --- | --- | --- |
| None (plain) | Supported | Consumes a token; the process completes when none remain. |
| Terminate | Supported | Ends the process, discarding remaining tokens and tearing down the work still running. Late child completions are ignored. |
| Message | Supported | Publishes by name and ends. An unresolvable name degrades to a plain end event. |
| Signal | Supported | Broadcasts by name and ends: it binds `SignalPublish` work, and delivering the signal to its catchers is the host's. An unresolvable `signalRef` degrades to a plain end event. |
| Escalation | Supported | **Requires** a code; a ref-less escalation end is dropped. |
| Cancel | Supported | Only inside a transaction. Outside one it degrades to a plain end event. |
| Compensation | Supported | Triggers compensation replay; `activityRef` narrows the target, and an `activityRef` naming an element with no compensation boundary is dropped. |
| Error | Supported | Throws the code its `errorRef` resolves to, or an error with no code when it has no ref. An error event subprocess of its own scope that declares the code, else the scope's catch-all, catches it and interrupts the scope; otherwise the scope faults with `bpmn.error.thrown` and `Fault.ErrorCode` set to the code, for the enclosing scope to catch. |
| Conditional, link | Degraded | Reported as unsupported definitions; reads as a plain (or terminate) end event. |

### Intermediate events

| Construct | Status | Notes |
| --- | --- | --- |
| Catch: timer | Supported | A one-shot `<timeDuration>` (a `timerWait`), a `<timeDate>` (a `timerDateWait`, firing once at that instant) or a `<timeCycle>` (a `timerCycleWait`, firing once, at the cycle's first occurrence). A catch with none of them usable is dropped. |
| Catch: message, signal | Supported | Dropped when the name cannot be resolved. |
| Catch: anything else, or more than one definition | Dropped | |
| Throw: message | Supported | Publishes by name; dropped when the name cannot be resolved, because a throw must say what it publishes. |
| Throw: escalation | Supported | Requires a code; a ref-less escalation throw is dropped. |
| Throw: signal | Supported | Broadcasts by name and continues: it binds `SignalPublish` work, and delivering the signal to its catchers is the host's. Dropped when the name cannot be resolved, because a throw must say what it broadcasts. |
| Throw: compensation | Supported | A ref-less throw compensates everything registered; an `activityRef` is kept only when it names an element carrying a compensation boundary. |
| Throw: anything else | Dropped | Only message, signal, escalation and compensate throws are supported. |

## Boundary events

| Construct | Status | Notes |
| --- | --- | --- |
| Interrupting (`cancelActivity: true`) | Supported | The BPMN default. Terminates the host activity and routes the boundary path. |
| Non-interrupting (`cancelActivity: false`) | Supported | The boundary path runs alongside the still-running activity. |
| Error | Supported | **Always interrupting** — BPMN fixes this, and a non-interrupting error boundary is dropped. Its `errorRef` resolves to the code it catches: a coded boundary catches **only** that code, and wins over the host's catch-all. One with no code is the catch-all. A second code-less one on the same host, or a duplicate code, is dropped. |
| Escalation | Supported | May be non-interrupting. A code-less boundary is the catch-all; a second code-less one on the same host, or a duplicate code, is dropped. |
| Timer | Supported | A `<timeDuration>`, a `<timeDate>` or a `<timeCycle>` (a `timerCycleWait`); a boundary with none of them usable is dropped. Interrupting or not, a timer boundary fires once, a cycle boundary at its first occurrence: re-arming a non-interrupting cycle boundary for later occurrences is not supported yet. |
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
| Default flow | Supported | Taken only when no other flow was, and never evaluated. Marked by `defaultFlowId` on the element, `isDefault` on the flow, or both; a read document marks it on the element. |
| Unconditional flow | Supported | Always taken from an activity or an inclusive gateway. On an exclusive gateway it counts as true in declaration order, so it is taken unless a flow declared before it holds first. Before 0.4.0 an exclusive gateway passed over it. |
| Conditional flow, **outcome-based** | Supported | A flow with a `ConditionOutcome` is taken when the source element's completing work reported that outcome name. A token arriving at an element that binds no work reports none. |
| Conditional flow, **expression-based** | Supported, requires `ConditionEvaluation` | A `<conditionExpression>` is modeled as `BpmnSequenceFlow.ConditionExpression` (body, `language`, and the rest retained: `xsi:type`, `id`, vendor attributes) and written back. The host evaluates it through `IBpmnConditionEvaluator`; the library never does. An exclusive gateway evaluates in declaration order and stops at the first true; an inclusive gateway and an activity evaluate every one. A failed answer faults the scope, except at an escalation throw an interrupting event subprocess of its own scope caught, whose onward routing is moot. A host that does not declare the [capability](../concepts/capabilities.md) is refused at graph build, naming the flows. A flow with a `conditionOutcome` as well is outcome-matched, and its expression is retained only and needs no capability. See [The host port](../concepts/host-port.md#conditions) and [ADR 0011](../adr/0011-hosts-evaluate-condition-expressions-through-a-synchronous-port.md). |
| Expression on a flow out of a start event, parallel gateway or event-based gateway | Not supported | These take every outgoing flow and evaluate nothing, so graph build refuses an expression-only flow out of one, naming the element and flow, whatever capabilities the host declares. |
| No flow taken | Faults | An exclusive or inclusive gateway faults the scope with `bpmn.gateway.no-flow-selected`, and any other element with outgoing flows with `bpmn.flow.none-taken`, including an unbound task when a token arrives (before 0.4.0 that consumed the token silently). An escalation throw caught by an interrupting event subprocess of its own scope does not fault: the activation stops its successor anyway, so the token ends. An element with no outgoing flow is an implicit end. |
| Cyclic flow (loop-backs) | Supported | A token traversing a backward flow mints a fresh iteration key; join accounting groups arrivals by element *and* iteration, so a revisited join never conflates one pass with the next. A cycle through a join, a compensate throw or a multi-instance activity can go around without waiting on some runs, with an empty collection for example; one evaluation stops after 10,000 dispatches and faults with `bpmn.propagation.limit`. |
| Cyclic flow on which nothing can wait | Not supported | A cycle made only of elements that pass a token straight on (unbound tasks and splits, parallel splits, escalation throws nothing interrupting catches) would circulate forever within one evaluation, so the graph build throws and names the cycle. See [A cycle must be able to wait](../concepts/host-port.md#a-cycle-must-be-able-to-wait). |
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
| Transaction subprocess | Supported | Cancel from within stops other live work, tearing down what is running and withdrawing what the same evaluation was about to start, replays compensations, and completes with the `Cancelled` outcome. A transaction that completes `Cancelled` with no cancel boundary attached raises a fault. |

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
| `<definitions>` metadata | Supported | Id, target namespace, exporter, exporter version, and `expressionLanguage`, which is also copied onto every process as its default expression language. No XPath default is assumed when the document declares none. |
| Root `<message>`, `<signal>`, `<error>`, `<escalation>` | Supported | Matching is on the **name** for messages and signals, and on the **code** for errors and escalations. An `<error>` without an `errorCode` is identified by its `id`. |
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
- **Expression and script languages.** FEEL, JUEL, JavaScript, Groovy — none, permanently. A host
  evaluates condition expressions through `IBpmnConditionEvaluator` in its own language, and runs a
  script task's `BpmnElement.Script` in the work bound to it.
- **Rendering.** Layout is preserved and synthesized, not drawn.
- **Vendor server protocols.** This is not a client for anyone's engine.

## Related

- [Reading and writing BPMN XML](../guides/reading-and-writing-bpmn.md) — what a finding looks like.
- [Errors and escalation](../concepts/errors-and-escalation.md) — the five failure constructs, in
  detail.
- [Host capabilities](../concepts/capabilities.md) — why a supported construct can still be refused.
