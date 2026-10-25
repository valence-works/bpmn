# Raised escalations survive a later fault

**Status**: Accepted

**Date**: 2026-10-17

## Context

An evaluation can throw an escalation and then reach a deterministic fault. The parent can react to
that escalation with either an interrupting or a non-interrupting catcher. Discarding the child's
command list when its verdict is a fault erases an event that happened before that verdict.

[OMG BPMN 2.0.2](https://www.omg.org/spec/BPMN/2.0.2/PDF/) describes escalation throws as raising an
escalation (Tables 10.88 and 10.89) and defines the parent boundary catch behavior (Table 10.90).
The ordering below is this library's host-port contract for preserving that occurrence; the standard
does not specify the host's persistence or commit mechanism.

## Decision

A `SignalEnclosingScope` raised by an escalation throw stays on an ordinary `Fault` continuation's
command list. Hosts deliver the outward signal to the parent **before reporting the scope's fault,
or atomically together with that report**. They apply the command list in its returned order and
must not discard it because the continuation is a fault. In particular, an interrupting parent
catcher must have the opportunity to handle the escalation while its child work is still live.

There is no `SignalsSurviveFault` capability or degrade-with-diagnostic option. `ScopeSignalling`
already states whether the host can deliver scope signals; [ADR 0004](0004-capabilities-refuse-at-graph-build-time.md)
requires unsupported definitions to be refused. The signal and the fault are separate facts:
[ADR 0003](0003-faults-are-a-verdict-not-a-command.md) makes the latter a verdict rather than an instruction
to erase previously raised notifications.

This records existing behavior. `RaiseEscalation` issues a throw's signal immediately and
`FinishEvaluation` retains it on an ordinary fault. The reference host applies commands before
`Finish` queues the parent's fault callback, so its queue delivers the signal first.

Two existing boundaries remain:

- The propagation-step limit deliberately withdraws signals raised by the runaway evaluation,
  preventing a cycle from flooding the parent. It records the number suppressed.
- An unmatched incoming escalation re-signalled by `BubbleOrUnhandled` is carried as
  `PendingScopeSignal`, then issued only on a non-fault continuation. Legacy state containing
  `PendingFault` already holds a verdict: callbacks are absorbed and no re-signal is forwarded.
  This deferred forwarding is distinct from an escalation throw raised during the current evaluation.

## Consequences

Hosts that commit signals and fault verdicts together must preserve both facts in that operation.
Hosts that report them separately must preserve their order. An interrupting parent catcher can
handle the escalation before a later child fault arrives; a non-interrupting catcher is notified even
though it does not prevent the child fault from propagating.

The interpreter stays pure and synchronous. No public API, capability, payload format or runtime
policy changes. Existing propagation-limit and legacy-state behavior is retained.

## Alternatives considered

- Drop outward signals on every fault: loses a thrown escalation and makes parent behavior depend
  on the host's commit policy.
- Add `SignalsSurviveFault` with a fallback: creates two semantic modes for the same definition and
  contradicts the capability boundary in ADR 0004.
- Change the host's transaction design in the library: the library returns data and has no host
  dependency; each adapter implements the ordering contract in its own commit mechanism.
