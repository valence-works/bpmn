using Bpmn.Model;
using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Keeps host-snapshot identity tied to the state at evaluation entry while one callback cancels work
/// before later routing in that same evaluation needs its handle.
/// </summary>
public sealed class EvaluationSnapshotIdentityTests
{
    [Fact]
    public void Race_loser_and_scope_listener_keep_their_entry_snapshot_handles_through_own_scope_escalation()
    {
        var (definition, nestedProcesses) = RaceWinnerEscalatesWithinScope();
        var graph = BpmnGraph.Build(
            definition,
            BpmnBoundWork.Derive(definition, bindingRef => nestedProcesses.GetValueOrDefault(bindingRef)),
            BpmnHostCapabilities.Full);
        var interpreter = BpmnInterpreter.CreateDefault();
        var started = interpreter.Start(new BpmnStartRequest(graph, null, Host()));
        var startsByBinding = started.Commands
            .OfType<BpmnHostCommand.StartWork>()
            .ToDictionary(command => command.BindingRef, StringComparer.Ordinal);

        startsByBinding.Keys.OrderBy(binding => binding, StringComparer.Ordinal)
            .ShouldBe(["listener", "loser", "winner"]);

        var winner = startsByBinding["winner"];
        var loser = Live(startsByBinding["loser"], "handle:loser");
        var listener = Live(startsByBinding["listener"], "handle:listener");
        var completed = interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph,
            started.State,
            Host(loser, listener),
            winner.BindingRef,
            "handle:winner",
            [])
        {
            WorkTokenId = winner.TokenId
        });

        completed.Continuation.ShouldBe(BpmnContinuation.Defer.Instance);
        completed.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>()
            .Select(command => (command.Handle, command.ElementId, command.Reason))
            .OrderBy(command => command.Handle, StringComparer.Ordinal)
            .ShouldBe(
            [
                ("handle:listener", "OnMessage", BpmnInterpreter.EventSubprocessScopeInterruptedReason),
                ("handle:loser", "CatchLoser", BpmnInterpreter.EventGatewayRaceCancellationReason)
            ]);
        completed.Commands.OfType<BpmnHostCommand.StartWork>().Select(command => command.BindingRef)
            .ShouldBe(["escalation-handler"]);
        completed.State.ActiveWork.Select(work => work.NodeId).ShouldBe(["escalation-handler"]);

        var handler = completed.Commands.OfType<BpmnHostCommand.StartWork>().Single();
        var finished = interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, completed.State, Host(), handler.BindingRef, "handle:handler", [])
        {
            WorkTokenId = handler.TokenId
        });
        finished.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        finished.State.ActiveWork.ShouldBeEmpty();
        finished.Commands.ShouldBeEmpty();
    }

    private static BpmnHostSnapshot Host(params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root("scope-1") with { LiveWork = liveWork };

    private static BpmnLiveWork Live(BpmnHostCommand.StartWork start, string handle) =>
        new(start.BindingRef, start.IterationScope?.IterationId, handle) { WorkTokenId = start.TokenId };

    private static (BpmnProcessDefinition Definition, IReadOnlyDictionary<string, BpmnProcessDefinition> NestedProcesses)
        RaceWinnerEscalatesWithinScope()
    {
        var escalationBody = new BpmnProcessBuilder("escalation-handler-body")
            .StartEvent("EscalationStart", null, ProcessFixtures.Escalation("raise"))
            .Task("HandleEscalation", bindingRef: "handle-escalation")
            .EndEvent("EscalationEnd")
            .ConnectSequence("EscalationStart", "HandleEscalation", "EscalationEnd")
            .Build();
        var messageBody = new BpmnProcessBuilder("message-handler-body")
            .StartEvent("MessageStart", null, ProcessFixtures.Message("notification"))
            .Task("HandleMessage", bindingRef: "handle-message")
            .EndEvent("MessageEnd")
            .ConnectSequence("MessageStart", "HandleMessage", "MessageEnd")
            .Build();

        var definition = new BpmnProcessBuilder("entry-snapshot-race-escalation")
            .StartEvent("Start")
            .EventBasedGateway("Race")
            .IntermediateCatchEvent("CatchWinner", ProcessFixtures.Message("winner"), bindingRef: "winner")
            .IntermediateCatchEvent("CatchLoser", ProcessFixtures.Message("loser"), bindingRef: "loser")
            .IntermediateThrowEvent("Raise", ProcessFixtures.Escalation("raise"))
            .EndEvent("WinnerEnd")
            .EndEvent("LoserEnd")
            .SubProcess("OnEscalation", bindingRef: "escalation-handler", triggeredByEvent: true)
            .Element(new BpmnElement(
                "OnMessage",
                BpmnElementTypes.SubProcess,
                bindingRef: "message-handler",
                triggeredByEvent: true,
                listenerBindingRef: "listener"))
            .Connect("Start", "Race")
            .Connect("Race", "CatchWinner")
            .Connect("Race", "CatchLoser")
            .ConnectSequence("CatchWinner", "Raise", "WinnerEnd")
            .Connect("CatchLoser", "LoserEnd")
            .Build();

        return (definition, new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
        {
            ["escalation-handler"] = escalationBody,
            ["message-handler"] = messageBody
        });
    }
}
