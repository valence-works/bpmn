using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Model.Tests;

public sealed class BpmnExecutionStateTests
{
    private static BpmnToken Token(string tokenId, BpmnTokenStatus status = BpmnTokenStatus.Active, string? stoppedHandle = null) =>
        new BpmnToken(tokenId, $"element-{tokenId}", status: status) { StoppedHandle = stoppedHandle };

    private static BpmnActiveWork Work(string tokenId) =>
        new("node", $"element-{tokenId}", tokenId, "scheduled");

    [Fact]
    public void Prune_keeps_live_tokens_and_unresolved_stopped_handles_but_drops_obsolete_terminal_tokens()
    {
        var tokens = new[]
        {
            Token("active"),
            Token("awaiting", BpmnTokenStatus.AwaitingChild),
            Token("waiting", BpmnTokenStatus.WaitingAtJoin),
            Token("consumed", BpmnTokenStatus.Consumed),
            Token("consumed-live", BpmnTokenStatus.Consumed),
            Token("canceled", BpmnTokenStatus.Canceled),
            Token("canceled-live", BpmnTokenStatus.Canceled),
            Token("canceled-empty-handle", BpmnTokenStatus.Canceled, ""),
            Token("canceled-with-handle", BpmnTokenStatus.Canceled, "host-work-1")
        };
        var activeWork = new[] { Work("consumed-live"), Work("canceled-live") };
        var diagnostics = new[] { new BpmnDiagnosticEvent("diagnostic-1", BpmnDiagnosticKind.Scheduled, "scheduled") };
        var races = new[] { new BpmnEventRace("race-1", "gateway", ["winner", "loser"], resolved: true) };
        var loops = new[] { new BpmnLoopState("loop-1", "coordinator", "Multi", false, 2, 1, 0) };
        var compensables = new[] { new BpmnCompensable("comp-1", "host", "handler", BpmnCompensableStatus.Registered) };
        var compensationRuns = new[] { new BpmnCompensationRun("run-1", "throw-token", ["comp-1"]) };
        var state = new BpmnExecutionState(
            tokens,
            activeWork,
            diagnostics,
            sequence: 17,
            terminated: true,
            pendingFault: new BpmnPendingFault("fault-code", "fault message"),
            races: races,
            loops: loops,
            compensables: compensables,
            compensationRuns: compensationRuns,
            cancelling: true);

        var pruned = state.Prune();

        pruned.Tokens.Select(token => token.TokenId).ShouldBe(
            ["active", "awaiting", "waiting", "consumed-live", "canceled-live", "canceled-empty-handle", "canceled-with-handle"]);
        pruned.Sequence.ShouldBe(state.Sequence);
        pruned.Prune().Tokens.Select(token => token.TokenId).ShouldBe(pruned.Tokens.Select(token => token.TokenId));

        // Prune only changes Tokens and, when over its cap, Diagnostics. Replacing those two with the
        // originals must restore the exact immutable state, including every other field.
        (pruned with { Tokens = state.Tokens, Diagnostics = state.Diagnostics }).ShouldBe(state);
        state.Tokens.Select(token => token.TokenId).ShouldBe(tokens.Select(token => token.TokenId));
    }

    [Fact]
    public void Prune_keeps_the_newest_diagnostics_in_their_original_order()
    {
        var diagnostics = Enumerable.Range(0, BpmnExecutionState.DiagnosticsCap + 2)
            .Select(index => new BpmnDiagnosticEvent($"diagnostic-{index}", BpmnDiagnosticKind.Scheduled, $"message {index}"))
            .ToArray();
        var state = new BpmnExecutionState(diagnostics: diagnostics, sequence: 23);

        var pruned = state.Prune();

        pruned.Diagnostics.Select(diagnostic => diagnostic.DiagnosticId).ShouldBe(
            Enumerable.Range(2, BpmnExecutionState.DiagnosticsCap).Select(index => $"diagnostic-{index}"));
        pruned.Sequence.ShouldBe(state.Sequence);
        state.Diagnostics.Count.ShouldBe(BpmnExecutionState.DiagnosticsCap + 2);
    }
}
