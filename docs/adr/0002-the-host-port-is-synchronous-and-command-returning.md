# ADR 0002: The host port is synchronous and command-returning

**Status**: Accepted
**Date**: 2026-08-09

## Context

The interpreter has to tell a host to do things: start the work bound to an element, cancel a subtree
when an interrupting boundary event fires, signal an enclosing scope when an escalation bubbles.

There are two shapes for that. The interpreter can hold a host interface and call it — probably
asynchronously, since hosts do I/O. Or it can return a description of what should happen and let the
host apply it.

The choice looks stylistic. It is not.

## Decision

Every entry point is synchronous and returns a value:

```
(graph, prior state, host snapshot, event) -> (next state, continuation, commands)
```

The host applies the commands after the interpreter returns, in whatever idiom and on whatever thread
it likes. The interpreter never calls the host back, with one narrow exception noted below.

There are exactly three commands: `StartWork`, `CancelWorkSubtree`, and `SignalEnclosingScope`.

## Rationale

**The semantics contain no asynchrony.** In the codebase this was extracted from, the engine had
`Async` method signatures and zero `await` expressions — every one wrapped a synchronous body in a
completed task. Making the port a callback interface would have propagated `async` through a tight
token-propagation loop and a command-application switch for no awaits at all, costing readability,
allocations, and stack-trace quality to model something that never happens.

**Hosts disagree about timing, and commands let them.** One host cancels a subtree eagerly and
recursively, walking its context tree in place. Another stages the cancellation and flushes it only
when the surrounding evaluation commits without faulting. Both are legitimate. A callback interface
would have baked one host's timing into the library and made the other's adoption an uphill fight.
Returned commands are inert data: each host applies them the way its own execution model requires.

**A correctness invariant becomes structural.** Teardown may only be staged on a non-fault
continuation — firing it earlier tears down work that a fault path still needs. When behaviors hold a
live host reference, nothing prevents them firing immediately, and the invariant survives only by
discipline. When they return commands, nothing *can* fire before the interpreter returns, and the
invariant holds by construction.

**It removes a question the interpreter should never have to ask.** The original code asked the host
"did I schedule anything during this evaluation?" in order to decide how to conclude. With commands,
that is an inspection of the list just produced. The interpreter regains authority over its own
decision.

**Failures become reproducible.** An evaluation is fully determined by four values — definition,
state, snapshot, event — all of which serialize to JSON. A bug report can be a paste of those four
values, and a regression test can be written from it without a running host.

## The one callback

`IBpmnVariableReader` lets the interpreter pull a named variable mid-evaluation. It cannot be hoisted
into the snapshot, because which variable is needed depends on which element the propagation loop
reaches, which is not known before the loop runs. It is synchronous, read-only, and side-effect free.

If sequence-flow condition expressions are supported later, they get a second reader of the same
shape rather than an async escape hatch. A host that needs to await something pre-resolves it.

## Consequences

Asynchronous work is expressed, not eliminated. "Wait for a timer" is a `StartWork` command plus a
`Defer` continuation; the host suspends however it likes, and resumption arrives as
`OnWorkCompleted`. Long-lived scope listeners are the same thing — work that may never complete, torn
down by `CancelWorkSubtree`. The library's entire relationship with time is that the host has some.

Hosts must apply commands in the order returned. Ordering carries meaning: a teardown emitted before
a start is not interchangeable with the reverse.

The synchronous core makes the semantics testable without a host, a scheduler, or a clock — feed a
definition and an event, assert on state, continuation, and commands.

## Alternatives considered

**An async host interface the interpreter awaits.** Rejected for the reasons above. The one argument
in its favor — that a future host might need to await while scheduling — does not survive inspection:
the host applies commands *after* the interpreter returns, so it can await as freely as it likes. The
command shape constrains only when decisions are made, not when work happens.

**An event/observer model.** Rejected. Observers make ordering and failure handling implicit, and
give no natural place for the non-fault-continuation invariant to live.
