# Simulating a process

`Bpmn.Runtime.InMemory` is a reference host. It runs a BPMN process end to end without you writing
any of the host machinery described in [Hosting the interpreter](hosting-the-interpreter.md), and
without waiting for real time to pass.

It exists for three reasons: so nobody has to implement a host before they can see the library work;
so the interpreter has a host to be tested against; and so a process can be simulated, traced and
reasoned about without an execution environment.

## Install

```bash
dotnet add package Bpmn.Runtime.InMemory
```

It brings `Bpmn.Semantics` and `Bpmn.Model`, and no external dependency.

## What it is not

Read this before you build on it.

- **Not durable.** State lives in memory. The process dies with the process.
- **Not distributed.** Single process, no coordination, no leases, no locks.
- **No retries, no backoff, no poison handling, no dead letters.**
- **Not for production.** It is a reference implementation and a test fixture. Treat it as
  documentation that runs.

If you need any of the above, you need a host. This package is the worked example of what one looks
like, not a shortcut around building one.

## Running something

```csharp
using Bpmn.Interchange;
using Bpmn.Runtime.InMemory;

var imported = new BpmnXmlReader().Read(File.ReadAllText("order.bpmn"));
var definition = imported.Definitions.FindProcess("order-approval")!;

var host = new InMemoryBpmnHost();
var instance = host.Start(definition, imported.Bindings);
```

Work that the definition binds — tasks, timers, message waits — is started by the host as the
interpreter asks for it. Work that only the outside world can finish waits for you to finish it.

## The virtual clock

The host has no relationship with wall-clock time. Timers are entries in a schedule the host owns,
and the schedule is advanced by asking, not by waiting.

```csharp
instance.Clock.Advance(TimeSpan.FromDays(7));
```

A seven-day escalation timer fires immediately, in the same call, deterministically. So does a
30-day one. A test for "what happens when the customer never responds for a month" costs the same as
any other test.

This is the property that makes simulation useful. Three consequences worth internalizing:

**Time only moves when you move it.** Nothing fires on its own. If a process appears stuck, it is
either waiting for work you have not completed, or waiting for a clock you have not advanced.

**Ordering is deterministic.** Two timers due at the same virtual instant fire in a defined order,
the same order every run. There is no scheduler jitter to flake a test.

**Elapsed virtual time is not elapsed real time.** Anything you measure with a real clock inside the
simulation — an HTTP timeout, a stopwatch in your own task code — is measuring something the
simulation does not model.

## Driving from the outside

A simulation is a conversation: the host runs what it can, then stops and waits for you.

```csharp
// Finish a unit of work, naming the outcomes a conditional sequence flow selects on.
instance.CompleteWork(handle, "Approved");

// Fail one. An error boundary event on the activity may catch it.
instance.FaultWork(handle, "Credit limit exceeded");

// Move time.
instance.Clock.Advance(TimeSpan.FromHours(2));
```

`handle` is the opaque identifier the host assigned when it started the work, exactly as in a host
you write yourself — see [The host port](../concepts/host-port.md).

Outcome names are the whole conditional-routing story: the library evaluates no expressions, so a
sequence flow's `ConditionOutcome` is matched against an outcome name the completing work reported.
Note that faulting does not name an error code — which error a catcher matches is a property of the
model, not of the report. See
[Interpreter, not engine](../concepts/interpreter-not-engine.md#no-expressions).

## Inspecting what happened

The instance exposes the same `BpmnExecutionState` any host would hold, so everything you can inspect
in a simulation you can inspect in production.

```csharp
using Bpmn.Model.State;

foreach (var token in instance.State.Tokens.Where(t => t.Status != BpmnTokenStatus.Consumed))
    Console.WriteLine($"{token.TokenId} at {token.AtElementId} ({token.Status})");

foreach (var work in instance.State.ActiveWork)
    Console.WriteLine($"waiting on {work.ElementId} (token {work.TokenId})");
```

`BpmnTokenStatus` distinguishes `Active` (at an element, not yet dispatched), `AwaitingChild` (parked
while bound work runs), `WaitingAtJoin` (arrived at a join that has not fired), `Consumed` and
`Canceled`. Consumed and canceled tokens are kept rather than pruned, so the state is a history and
not just a position.

### The diagnostic trace

`State.Diagnostics` is an ordered record of what the interpreter decided and why:

```csharp
foreach (var diagnostic in instance.State.Diagnostics)
    Console.WriteLine($"{diagnostic.Kind,-28} {diagnostic.ElementId} {diagnostic.Message}");
```

`BpmnDiagnosticKind` names the events precisely, which is what makes a trace readable:
`TokenEmitted`, `Scheduled`, `Waiting`, `Joined`, `Consumed`, `Canceled`, `Terminated`, `Completed`,
`Faulted`, plus the construct-specific ones — `CompensationRegistered`, `CompensationTriggered`,
`Compensated`, `TransactionCancelled`, `EscalationRaised`, `EscalationCaught`, `EscalationUnhandled`,
`EscalationLate`, `EventSubprocessActivated`, `EventSubprocessCompleted`,
`CallActivityFailureRouted`, `ScopeListenerArmed`, `ScopeListenerFired`, `ScopeListenerRetired`.

Two of those are worth knowing about before you go hunting for a bug. `EscalationUnhandled` means an
escalation reached a scope that could not catch it — a no-op, never a fault. `EscalationLate` means
an interrupting escalation boundary matched a notification whose host had already finished — also a
no-op. Both are BPMN behaving correctly, and both look like something went wrong if you do not know
they exist.

## What to use it for

**Testing a definition, not your code.** Feed a process the sequence of completions and clock
advances that represent a scenario, and assert on the resulting tokens and diagnostics. No database,
no scheduler, no test containers.

**Answering questions about a model.** "If the approval times out twice, does the compensation
handler run?" is a simulation, not an argument.

**Learning the semantics.** Reading a trace of `TokenEmitted` / `Scheduled` / `Joined` for an
inclusive gateway teaches more in a minute than the specification prose does in an hour.

**Regression-testing an interchange change.** Round-trip a document through the writer and the
reader, run the same scenario against both definitions, and compare the diagnostics.

## Related

- [Hosting the interpreter](hosting-the-interpreter.md) — what this package is a worked example of.
- [Interpreter, not engine](../concepts/interpreter-not-engine.md) — why the durable half is
  deliberately missing.
- [Supported BPMN constructs](../reference/supported-constructs.md) — what you can simulate.
