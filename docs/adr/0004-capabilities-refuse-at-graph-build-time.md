# ADR 0004: Missing host capabilities refuse at graph-build time

**Status**: Accepted
**Date**: 2026-08-09

## Context

Not every host can do everything BPMN requires. Cancelling a subtree of running work, seeding
per-iteration variables for a multi-instance activity, reading a scope variable, and delivering a
signal to an enclosing scope are all things a host either supports or does not.

A host declares what it can do:

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

The question is what happens when a process needs something the host has not declared.

The obvious answer — carry on, emit a diagnostic, let the operator decide — is wrong, and it is worth
being precise about why.

Take a host without `SubtreeCancellation` running a process with an interrupting boundary event. BPMN
says interrupting means the activity the boundary is attached to is terminated. Without cancellation
the work keeps running while the token proceeds down the boundary path. The process now has two live
branches where the specification says one.

That is not degraded BPMN. It is **incorrect** BPMN, produced silently, behind a log line that nobody
reads until a support ticket arrives months later describing symptoms that make no sense.

For a library whose entire value is being right about BPMN semantics, shipping a mode that is quietly
wrong undermines the only thing it sells.

## Decision

Refuse early, and make the requirements inspectable.

A definition's requirements are computed statically:

```csharp
BpmnCapabilityRequirements BpmnCapabilityAnalyzer.Analyze(BpmnProcessDefinition definition);
```

It reports which capabilities the definition requires and which element ids drive each one:

| Construct | Requires |
| --- | --- |
| Interrupting boundary events, event subprocesses | `SubtreeCancellation` |
| Escalation throw and end events | `ScopeSignalling` |
| Multi-instance activities | `IterationScopes` |
| Collection-mode multi-instance | `ScopeVariables` |

`BpmnGraph.Build(definition, boundWork, capabilities)` throws when the host's declared set does not
cover the requirements, naming every unmet capability and the elements that need it.

The import analysis surfaces the same requirements per process, so `Analyze` on a `.bpmn` file answers
"can my host run this?" before anything is built.

## Consequences

A runtime landmine becomes a startup error with a precise message. That is the difference between "the
engine is flaky" and "the engine told me exactly what my host still needs to implement."

Adoption becomes incremental and directed. A prospective host can ask a real definition what it
requires, implement against that, and widen coverage as its own capabilities grow — instead of reading
the specification and guessing.

A host can no longer run a partially supported definition at all, even when the unsupported construct
sits on a branch that execution would never reach. This is a genuine loss of permissiveness and it is
accepted deliberately: a clear refusal up front beats a process that works until the day it does not.

Capability checking is static, so it depends only on the definition. It cannot be defeated by data.

## Alternatives considered

**Degrade with a diagnostic.** Rejected, for the reason the whole ADR exists: silent incorrectness is
worse than a refusal, and diagnostics that permit execution are read only after the damage.

**Refuse at runtime, when the interpreter first needs the missing capability.** Correct but late — it
fails deep inside execution, possibly in production, on a definition that appeared fine when it was
deployed. Static analysis is available; there is no reason to defer the check to the worst moment.

**Have the interpreter emulate missing capabilities.** Rejected. Emulating subtree cancellation
without host cooperation is not possible: the library does not know what work exists, let alone how to
stop it.
