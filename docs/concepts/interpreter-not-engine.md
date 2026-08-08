# Interpreter, not engine

This library answers what a BPMN process means. It does not make anything happen. Every other design
choice in it follows from that one sentence, and most surprises come from expecting the other kind of
library.

## The distinction

An **interpreter** takes a definition, a state and an event, and computes the next state. It is a
function. Ask it the same question twice and you get the same answer.

An **engine** runs processes. It owns durability, scheduling, retries, correlation, concurrency,
multi-tenancy, version migration and operational visibility. It has a database, a clock, and an
on-call rotation.

The BPMN 2.0 specification describes the first and says essentially nothing about the second. That is
not an oversight. Token movement, join semantics, boundary-event interruption and compensation
ordering are properties of a notation. Durability and retries are properties of an execution
environment, and no notation can specify them.

This library draws its boundary exactly where the specification draws it.

## The whole contract

```text
(process definition, current token state, one event) -> (next state, commands for the host)
```

That is not a simplification for the documentation. It is the actual shape of every entry point.

- No clock. The library never asks what time it is.
- No I/O. It never reads or writes anything.
- No threads. It never schedules, queues or parallelizes.
- No dependency injection. There is no container, no service locator, no registration.
- No persistence. It hands you a state object and forgets it.
- No randomness, no ambient state, no statics that change.

Given the same four inputs, an evaluation produces the same output on any machine, in any process, at
any time. That is a stronger property than it sounds, and several useful things are downstream of it.

## What you get from the boundary

**Bug reports are reproducible.** An evaluation is fully determined by definition, state, snapshot and
event. All four serialize to JSON. A report is a paste of those values, and a regression test is
written directly from it — no host, no scheduler, no clock.

**Tests are cheap.** Feed a definition and an event, assert on state, continuation and commands.
BPMN's hardest constructs — inclusive-gateway joins, non-interrupting boundary events on
multi-instance subprocesses, compensation replay ordering — become ordinary unit tests.

**The library cannot corrupt your data.** It has no access to it.

**Your execution model stays yours.** Timing, transaction boundaries, concurrency and persistence are
decisions the library never makes on your behalf. It does not fight your architecture, because it has
no opinion about it.

**Upgrades are boring.** A library with no I/O and no dependencies has a small surface on which to
break you.

## What the boundary costs

Honesty about this is the point of the page.

**A host is real work.** You must run and correlate work, persist an opaque state document, cancel
work subtrees recursively, and deliver signals to enclosing scopes. That is more than an embedded
engine asks of you. `Bpmn.Runtime.InMemory` ships as a supported package precisely because nobody
should have to write a host before they can see the library do anything.

**Some hosts cannot do all of it.** Rather than degrade quietly, the library refuses definitions
whose constructs a host cannot support, at graph-build time. See
[Host capabilities](capabilities.md).

**You own operations entirely.** Retries, incidents, alerting, dashboards and dead letters do not
exist here. BPMN does not define them, so the library does not have them.

## No expressions

Sequence-flow conditions are **not** evaluated by this library. There is no FEEL, no JUEL, no
JavaScript, and no plan to embed one.

A conditional sequence flow carries a `ConditionOutcome`: a name. The flow is taken when the source
element's completing work reported that outcome. The host decides what an outcome is and how it was
arrived at.

```csharp
new BpmnSequenceFlow("to-ship", "gate", "ship", conditionOutcome: "Approved");
```

Three reasons.

**Expression languages are host territory.** A host already has a way to evaluate expressions against
its own data, with its own security model, its own sandboxing, and its own idea of what a variable
is. A second one embedded in a library is a conflict, not a feature.

**It would break the dependency promise.** Every credible expression engine is a NuGet package. Every
shipped package here has zero external dependencies, and that is a constraint the build enforces.

**It would break determinism.** An arbitrary expression can read a clock, call out, or observe
mutable state, and the pure-function property would be gone the first time someone used it.

If sequence-flow condition expressions are supported later, they arrive as a second host-supplied
reader of the same shape as `IBpmnVariableReader` — not as an embedded engine and not as an async
escape hatch.

## Where the line is drawn, precisely

| Concern | Owner |
| --- | --- |
| What a gateway does with a token | Library |
| Which sequence flow a named outcome selects | Library |
| What produced that outcome | Host |
| When a boundary event interrupts | Library |
| How the interrupted work is actually stopped | Host |
| Compensation ordering | Library |
| Running the compensation handler | Host |
| That a timer must be waited for | Library |
| Waiting | Host |
| That a message is awaited by name | Library |
| Subscriptions, brokers, correlation, delivery | Host |
| Which errors a scope catches | Library |
| What a caught error means to your business | Host |
| Whether a failure escaped BPMN's model | Library |
| Whether that failure is an incident | Host |

The last row is the one people most often expect to find on the wrong side. **"Incident" is an
engine-operations concept and does not appear in BPMN at all.** The library tells you whether a fault
was caught or propagated; what to record, alert on or retry is entirely yours. See
[Errors and escalation](errors-and-escalation.md).

## Why not just ship an engine

It was considered and rejected. An engine would force opinions about persistence, serialization,
scheduling and concurrency onto hosts that already have their own and would fight them. It would
destroy the zero-dependency property. And it would turn infrastructure into a competitor: nobody
building a workflow engine takes a dependency on a rival engine, whereas a correct, neutral semantics
library is something they might.

The full argument, including the alternatives, is in
[ADR 0001](../adr/0001-bpmn-semantics-ship-as-a-host-agnostic-library.md).

## Related

- [The host port](host-port.md) — the contract in detail.
- [Host capabilities](capabilities.md) — what happens when a host cannot hold up its end.
- [Hosting the interpreter](../guides/hosting-the-interpreter.md) — how to implement one.
- [ADR 0002: The host port is synchronous and command-returning](../adr/0002-the-host-port-is-synchronous-and-command-returning.md)
