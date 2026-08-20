# Host capabilities

Not every host can do everything BPMN requires. A host declares what it can do, and a definition
needing more than that is **refused when the graph is built** — not degraded, not partially executed,
not warned about at run time.

This page explains why refusing is the correct behavior, and why the alternative is worse than it
looks.

## The declaration

```csharp
[Flags]
public enum BpmnHostCapabilities
{
    None = 0,
    SubtreeCancellation = 1,
    ScopeSignalling = 2,
    IterationScopes = 4,
    ScopeVariables = 8,
    Full = SubtreeCancellation | ScopeSignalling | IterationScopes | ScopeVariables
}
```

| Capability | What the host is promising |
| --- | --- |
| `SubtreeCancellation` | It can stop a unit of work *and everything started underneath it*, recursively. |
| `ScopeSignalling` | It can deliver a notification from a child scope to its enclosing scope. |
| `IterationScopes` | It can run the same bound work several times, keeping each instance's frame distinct. |
| `ScopeVariables` | It can hold and read container-scoped variables through `IBpmnVariableReader`. |

`Full` is every capability. It is a claim, not a default. `None` is a legitimate declaration and runs
a large fraction of real BPMN correctly.

You declare capabilities twice, and they must agree: once when building the graph, and once in each
`BpmnHostSnapshot`.

```csharp
var graph = BpmnGraph.Build(definition, boundWork, BpmnHostCapabilities.SubtreeCancellation
                                                 | BpmnHostCapabilities.ScopeVariables);
```

`BpmnGraph.Build` throws `BpmnCapabilityException` when the declared set does not cover what the
definition requires, naming every unmet capability and the elements that need it.

## Ask before you build

You do not have to discover a mismatch by catching an exception. A definition's requirements are
computed statically:

```csharp
var requirements = BpmnCapabilityRequirements.Analyze(definition);

var missing = requirements.MissingFrom(MyCapabilities);
if (missing != BpmnHostCapabilities.None)
{
    foreach (var (capability, elementIds) in requirements.DrivingElementIds)
        Console.WriteLine($"{capability} is needed by {string.Join(", ", elementIds)}");

    return Rejected(missing);
}
```

`Required` is the union of everything the definition needs; `DrivingElementIds` says which elements
drive each capability. That is enough to produce a good error at import time, in your own vocabulary,
pointing at the elements in the document the user just uploaded — rather than an exception at
deployment.

`requirements.ThrowIfUnmet(available, processId)` is the same check with the library's own message,
which is what `Build` calls internally.

Only the definition you pass is analyzed. A nested process carried as bound work is a separate scope
with its own graph, so analyze and build it separately.

The import analysis surfaces the same requirements per process, so analyzing a `.bpmn` file answers
"can my host run this?" before anything is built.

Because the check is static, it depends only on the definition. It cannot be defeated by data, and it
does not vary between instances.

## Why refuse instead of degrade

Here is the concrete case. It is not hypothetical, and it is why the rule exists.

A process has a subprocess with an **interrupting boundary timer** attached. The timer fires.

BPMN says exactly what happens: the subprocess and everything running inside it is terminated, and
one token continues along the boundary path. **One** live branch.

Now suppose the host cannot cancel a work subtree. Everything else still works. The timer fires, the
interpreter routes the boundary path, a token continues. But the subprocess is still running, because
nothing stopped it. When its own work eventually completes, its normal outgoing path continues too.

**Two live branches where BPMN says one.**

Downstream of that: a parallel join that will never fire because it is receiving arrivals it was
never designed for, or fires twice. A task executed twice. A payment taken twice. A compensation log
with registrations from work that was supposed to have been destroyed.

That is not a *degraded* execution of the model. It is a **different and incorrect** execution of it,
and nothing in the resulting state says so. The process looks like it is running fine. It is running
wrong.

## The three options, and why two of them lose

**Degrade at run time.** Fire the boundary event, skip the cancellation, keep going. The failure is
silent, arbitrarily delayed, and manifests as a data-integrity problem far from its cause. Nobody
debugging a doubled payment three weeks later is going to trace it back to a missing capability.

**Fault at run time.** Fire the boundary event, discover the cancellation cannot be performed, fault
the instance. Correct in the sense that nothing wrong is committed — but it faults in production,
inside a live instance, on a path that may only be reached by a rare timeout. The capability was
missing all along; the process merely had not happened to need it yet. Discovering a structural
mismatch through a production incident is the worst available time.

**Refuse at build time.** The definition contains an interrupting boundary event. Interrupting
boundary events require subtree cancellation. The host did not declare it. Refuse, name the element
and the capability, stop.

The third is the only one where the mismatch is discovered at the moment it can be fixed, by the
person who can fix it, with the information needed to fix it. The determination is purely structural
— it depends on the definition and the declaration, not on which paths a particular instance takes —
so there is no reason to wait.

## The principle

**A host that cannot honor a construct must not be allowed to pretend it did.**

There is no partial credit in a process model. A boundary event either interrupts or it does not; an
escalation either reaches its parent or it does not. A construct executed approximately is not a
weaker version of that construct — it is a different behavior wearing its name, and it is worse than
an absent one because it is invisible.

The build-time refusal converts an invisible correctness problem into a visible deployment problem.

## What this means in practice

**If you can implement everything, declare `Full` and stop thinking about this.** Most hosts can.

**If you cannot, you are not locked out.** A large fraction of real BPMN uses none of these
capabilities: linear processes, exclusive, parallel and inclusive gateways, tasks, plain start and
end events, and intermediate message and timer catches. A host declaring `None` runs those correctly
and is refused only where it would be wrong.

**Add capabilities as you need them.** Each one is a discrete piece of host machinery, and the
refusal message tells you which one a document is asking for. That is a much better roadmap than a
list of features nobody has asked for yet.

**Check requirements at the boundary of your system**, not at deployment.
`BpmnCapabilityRequirements.Analyze` exists so a user uploading a diagram is told immediately, in
your product's language, that your system does not support the construct they drew.

**A branch that never executes still counts.** A definition is refused even when the unsupported
construct sits on a path that a particular instance would never reach. That is a real loss of
permissiveness, accepted deliberately: a clear refusal up front beats a process that works until the
day it does not.

## Which constructs need what

| Construct | Requires | Why |
| --- | --- | --- |
| Interrupting boundary event | `SubtreeCancellation` | It tears its host activity down. |
| Catch boundary event (timer, message, signal), even non-interrupting | `SubtreeCancellation` | Its armed listener is torn down when its host completes. |
| Event subprocess | `SubtreeCancellation` | An interrupting one drains the scope; every scope listener is retired when the scope completes. |
| Event-based gateway | `SubtreeCancellation` | The losing branches are torn down when the first catch wins. |
| Cancel end event | `SubtreeCancellation` | It tears down the transaction's other live work when it abandons it. |
| Escalation throw or end event, escalation boundary event | `ScopeSignalling` | A throw signals the enclosing scope, and a scope re-signals what it cannot match itself. |
| Multi-instance activity | `IterationScopes` | Each instance needs its own frame. |
| Collection-mode multi-instance | `ScopeVariables` | Its instance count and items come from a declared variable. |
| Compensation boundary event | nothing | It arms no listener. |
| Exclusive, parallel and inclusive gateways | nothing | |
| Tasks, plain start and end events, sequence flows | nothing | |

Note the second row. A *non-interrupting* catch boundary event still needs subtree cancellation,
because the listener it arms has to be retired when its host finishes. It is easy to assume only
interrupting events tear anything down; the analyzer knows better.

`BpmnCapabilityRequirements.Analyze(definition)` is the authority for a specific document, and the
graph builder is the authority overall.

## Related

- [The host port](host-port.md) — what a host implements.
- [Hosting the interpreter](../guides/hosting-the-interpreter.md) — how to declare and honor
  capabilities.
- [Supported BPMN constructs](../reference/supported-constructs.md) — what the library supports
  before capabilities enter the picture.
- [ADR 0004: Missing host capabilities refuse at graph-build time](../adr/0004-capabilities-refuse-at-graph-build-time.md)
