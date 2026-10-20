# ADR 0011: Hosts evaluate condition expressions through a synchronous port

**Status**: Accepted
**Date**: 2026-10-20

## Context

A sequence flow can carry a condition in two ways. The library's own way is a `conditionOutcome`: a
name the source element's completing work reports. BPMN's way is a `<conditionExpression>`: a body in
some expression language, evaluated against process data when a token leaves the source element.

Until now the library has supported only the first. An expression condition reads as a `Degraded`
finding, the flow becomes unconditional, and the expression is kept as retained foreign XML.
[Supported constructs](../reference/supported-constructs.md) called that "a permanent design position",
and [Interpreter, not engine](../concepts/interpreter-not-engine.md#no-expressions) gave three reasons
for it: expression languages are host territory, embedding one would break the dependency promise,
and an arbitrary expression would break determinism.

Those three reasons are about **evaluating** expressions inside the library, and they still hold. What
they do not justify is the consequence for routing. Nearly every BPMN document written by a modeling
tool expresses its decisions as expressions. The outcome-name convention belongs to this library alone,
so a host that wants to run such a document has two options. It can rewrite every condition as an
outcome before building the graph, or it can run a process whose exclusive gateways take every branch.
The first moves BPMN routing semantics, including declaration order, defaults and the
no-match rule, into each host, which is the duplication this library exists to prevent. The second is
the silently incorrect execution [ADR 0004](0004-capabilities-refuse-at-graph-build-time.md) refuses to
allow anywhere else. A host also cannot reach the expression without digging it out of retained XML,
which is how this gap was first reported.

[ADR 0002](0002-the-host-port-is-synchronous-and-command-returning.md) anticipated the way out: if
sequence-flow condition expressions are supported later, "they get a second reader of the same shape"
as `IBpmnVariableReader`, rather than an async escape hatch. This record takes that path.

## Decision

The library decides **which** conditions are evaluated, **when** and in **what order**, and **what the
answers mean** for routing. The host decides **what an expression evaluates to**. The library still
contains no expression language, no parser and no dependency.

### The port

```csharp
public interface IBpmnConditionEvaluator
{
    BpmnConditionResult Evaluate(BpmnConditionRequest request);
}
```

It travels on the host snapshot beside `Variables`. Like the variable reader it is synchronous,
read-only and asked mid-evaluation, because which conditions are needed depends on which element the
propagation loop reaches. A host that would have to await something to answer pre-resolves it before
the evaluation. There is no async path.

A request carries:

| Field | Meaning |
| --- | --- |
| `ElementId` | The element whose outgoing flows are being chosen. |
| `FlowId` | The flow whose condition this is. |
| `Site` | What kind of condition this is. A string constant, as element types are, so a new site is additive. Sequence flows are the only site this decision implements. |
| `Body` | The expression text exactly as modeled. The library never parses it. |
| `Language` | The flow's own `language`, else the process default taken from `definitions/@expressionLanguage`. Null when the document declares neither. |
| `Iteration` | The multi-instance iteration the routed token belongs to, so the host can evaluate against that instance's frame. Null outside one. |

`Language` is deliberately null rather than BPMN's nominal XPath default when the document is silent.
Tools routinely omit the attribute while meaning their own language, so a library-supplied XPath URI
would be wrong more often than right. What a missing language means is the host's call.

A result is one of three: `True`, `False`, or `Failed(FaultCode?, Message)`.

### The capability

A new flag, `BpmnHostCapabilities.ConditionEvaluation`, is required by every sequence flow that
carries an expression and no `conditionOutcome`. It is **not** part of `Full`. `Full` means everything
a host can do without embedding a language. A host that declares it today has made no promise about
evaluating expressions, and adding the flag to `Full` would make that promise silently on its behalf.

`BpmnGraph.Build` refuses a definition that needs the capability when the host does not declare it,
as it refuses every other unmet capability. There is no degrade mode, and no import option that keeps
the old unconditional reading. An option like that would recreate, by configuration, exactly the
incorrect execution this decision removes.

### Routing

- **Outcome first.** A flow with both a `conditionOutcome` and an expression is outcome-matched. The
  expression is retained and written back, never evaluated, and does not require the capability.
- **Exclusive gateway.** The non-default flows are visited in declaration order and the first that
  is true is taken. Evaluation short-circuits, so later conditions are not evaluated. A flow without
  any condition counts as true, which is what widely deployed engines do. This is a change: today
  the exclusive selector passes over unconditional non-default flows.
- **Inclusive gateway and activities.** Every flow whose condition is true, plus every unconditional
  flow. Every condition is evaluated, in declaration order.
- **The default flow** is never evaluated. It is taken only when nothing else was.
- **No match and no default** faults the scope with `bpmn.gateway.no-flow-selected`, the code the
  gateways already use. This now applies to activities too. Today an activity whose outgoing flows all
  fail to match consumes its token silently. That is an intended behavior change for 0.4.0, and it
  applies to outcome-only processes as well. An activity with no outgoing flows at all is an implicit
  end and is unaffected.
- Where a gateway binds decision work, its conditions are evaluated when that work completes, together
  with the outcome match. Otherwise they are evaluated when the token arrives.

### Failure and trace

`Failed` faults the scope with the host's code, or with `bpmn.condition.failed` when the host gives
none. Like `bpmn.gateway.no-flow-selected`, it is a verdict on the scope
([ADR 0003](0003-faults-are-a-verdict-not-a-command.md)), not a BPMN error raised by bound work, so
no error catcher in the scope takes it. A host whose evaluator throws has broken the port, and the
exception propagates as it would from the variable reader.

Every evaluation is recorded as a `ConditionEvaluated` diagnostic naming the element, the flow and the
result. That keeps an evaluation reproducible from its inputs plus the answers given, which is what a
bug report needs now that part of the decision comes from the host.

### Extension points

`Site` exists so the same port can later serve a multi-instance `completionCondition`, a
`standardLoopCharacteristics` loop condition, and conditional events. None of those is implemented by
this decision. Each needs its own construct support and gets its own record when it does.

### Order of delivery

The decision ships in three steps. This record comes first. The second step models
`<conditionExpression>` and `definitions/@expressionLanguage` and round-trips them. Until the third
step, the interpreter, lands, a flow carrying only an expression still reads as unconditional with
its import finding. It never does so silently.

## Consequences

Documents written by modeling tools become executable without a rewrite. Routing semantics, including
declaration order, short-circuiting, defaults and the no-match fault, are implemented once, here,
instead of in each host.

Purity holds the way it already does for variables. Given the same definition, state, snapshot, event
and evaluator answers, an evaluation produces the same result. The determinism guarantee now depends on
the host's evaluator being a function of its inputs, exactly as it already depends on the variable
reader. A host that evaluates against a clock has made its own runs non-reproducible. The library
cannot prevent that and does not try.

The host port grows a second callback. "One callback" is no longer true of [the host
port](../concepts/host-port.md), and a host implementing expressions takes on an evaluator, its
language, and its sandboxing. That is the cost of putting language choice where the data and the
security model already are.

Two changes are visible to existing hosts. Definitions with expression-only flows that used to build
are refused until the host declares `ConditionEvaluation`. Activities and exclusive gateways route
differently in the cases above. Both are deliberate, and both are 0.4.0 release-note items.

A new diagnostic kind is a payload-format change for execution state, and is versioned as one.

Static analysis that predicts where a token goes, such as the build-time check that a cycle can
wait, can no longer know the answer for an expression flow. It has to treat that flow as possibly
taken.

## Alternatives considered

**Keep the permanent position and let hosts translate expressions into outcomes.** This is what the
library did. Rejected because every host then re-implements BPMN routing around the translation, and
any host that gets it wrong produces the silently incorrect execution the capability model exists to
prevent. The position protected the dependency promise and determinism. This decision keeps both.

**Embed an expression language.** Rejected for the reasons that have not changed: it would be an
external dependency in a shipped package, it would impose one language on hosts that already have
their own, and it would put an arbitrary evaluator inside the pure core.

**Make the port asynchronous.** Rejected for the reasons in ADR 0002. The propagation loop never
awaits, and a host that needs I/O to answer can pre-resolve.

**Pass the whole outgoing set and let the host choose the flows.** Rejected. Choosing is the BPMN
semantics, including declaration order, the default and the no-match rule, and handing it to the
host splits one rule across two codebases. The host answers one condition at a time. The library
decides what the answers mean.

**Degrade when the host cannot evaluate, with a finding or an import option.** Rejected under
ADR 0004. An exclusive gateway that takes every branch is not reduced BPMN. It is incorrect BPMN, and a
finding nobody reads does not make it correct.

**Add `ConditionEvaluation` to `Full`.** Rejected. Hosts that declare `Full` today would suddenly
promise an evaluator they do not have, and would discover it as a null reference at run time rather
than a refusal at build time.

## Amendment: as implemented

Recorded when the interpreter step landed, where it differs from or adds to the text above.

- **No match at an activity keeps its own code.** An activity, a subprocess, a catch or throw event
  and a boundary event already faulted with `bpmn.flow.none-taken` when their completing work matched
  no outgoing flow, so they keep that code. What is new is that the same fault now also applies when a
  token arrives at an element that binds no work, which used to consume the token silently. Exclusive
  and inclusive gateways keep `bpmn.gateway.no-flow-selected`.
- **A throwing evaluator surfaces wrapped.** The evaluator is asked from inside a behavior, so its
  exception reaches the host as a `BpmnExecutionException` naming the element and flow, with the
  evaluator's exception as `InnerException`, rather than unwrapped as a variable reader's would. A host
  that declares `ConditionEvaluation` and leaves `BpmnHostSnapshot.Conditions` null gets the same
  exception type the first time a condition is needed.
- **`Iteration` is null with the built-in behaviors.** The instances of a multi-instance activity
  never route: the activity routes once after its last instance, outside any iteration, and a
  multi-instance subprocess body is its own scope. The field is populated if a routing token is ever
  an instance token.
- **An unbound exclusive gateway with a single outgoing flow** still takes it without testing it unless
  that flow's only condition is an expression, so a single outcome-conditioned flow keeps routing as
  before.
- **Default flows.** A default flow is identified by the element's `default` reference as well as the
  flow's own flag, which is how a read document marks it. Before this, an activity treated a default
  marked only on the element as an ordinary unconditional flow and took it beside every other.
- **Event-based gateway flows** may not carry an expression, as they may not carry an outcome.
