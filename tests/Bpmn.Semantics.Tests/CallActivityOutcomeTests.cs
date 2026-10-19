using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class CallActivityOutcomeTests
{
    [Theory]
    [InlineData("Completed")]
    [InlineData("Dispatched")]
    public void NonFailureOutcome_RoutesNormalOutbound(string outcome)
    {
        var definition = new BpmnProcessBuilder("call-normal")
            .StartEvent("Start")
            .CallActivity("Call", "called-process", bindingRef: "call")
            .Task("After", bindingRef: "after")
            .EndEvent("End")
            .ConnectSequence("Start", "Call", "After", "End")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("call", outcome);

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["After"]);
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.CallActivityFailureRouted);
    }

    [Fact]
    public void CompletedOutcome_DiscriminatedByConditionOutcomeFlow()
    {
        var definition = new BpmnProcessBuilder("call-condition")
            .StartEvent("Start")
            .CallActivity("Call", "called-process", bindingRef: "call")
            .Task("Completed", bindingRef: "completed")
            .Task("Other", bindingRef: "other")
            .EndEvent("Done")
            .EndEvent("NotDone")
            .Connect("Start", "Call")
            .Connect("Call", "Completed", condition: "Completed")
            .Connect("Call", "Other", condition: "SomethingElse")
            .ConnectSequence("Completed", "Done")
            .ConnectSequence("Other", "NotDone")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("call", "Completed");

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Completed"]);
        instance.CompleteWork("completed");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        var started = instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Select(command => command.ElementId).ToArray();
        started.Count(elementId => elementId == "Completed").ShouldBe(1);
        started.ShouldNotContain("Other");
    }

    [Fact]
    public void TwoCallActivities_InOneProcess_BothRoute()
    {
        var definition = new BpmnProcessBuilder("two-calls")
            .StartEvent("Start")
            .CallActivity("First", "first-process", bindingRef: "first")
            .CallActivity("Second", "second-process", bindingRef: "second")
            .EndEvent("End")
            .ConnectSequence("Start", "First", "Second", "End")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("first", "Completed");
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Second"]);
        instance.CompleteWork("second", "Completed");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Theory]
    [InlineData(BpmnInterpreter.CallActivityFaultedOutcomeName)]
    [InlineData(BpmnInterpreter.CallActivityDispatchFailedOutcomeName)]
    [InlineData(BpmnInterpreter.CallActivityCancelledOutcomeName)]
    public void FailureOutcome_WithErrorBoundary_FiresBoundaryAndRecordsOutcome(string outcome)
    {
        var definition = new BpmnProcessBuilder("call-error-boundary")
            .StartEvent("Start")
            .CallActivity("Call", "called-process", bindingRef: "call")
            .BoundaryEvent("Failed", "Call", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .EndEvent("NormalEnd")
            .Task("Recover", bindingRef: "recover")
            .EndEvent("RecoveredEnd")
            .ConnectSequence("Start", "Call", "NormalEnd")
            .ConnectSequence("Failed", "Recover", "RecoveredEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("call", outcome);

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Recover"]);
        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Kind == BpmnDiagnosticKind.CallActivityFailureRouted
            && diagnostic.Message.Contains(outcome, StringComparison.Ordinal));
    }

    [Fact]
    public void Failure_WithErrorBoundary_TearsDownSiblingListener()
    {
        var timer = new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BpmnEventDefinitionProperties.Interval] = "PT5M"
            });
        var definition = new BpmnProcessBuilder("call-listener")
            .StartEvent("Start")
            .CallActivity("Call", "called-process", bindingRef: "call")
            .BoundaryEvent("Failed", "Call", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .BoundaryEvent("Timer", "Call", timer, bindingRef: "timer")
            .EndEvent("NormalEnd")
            .EndEvent("FailedEnd")
            .ConnectSequence("Start", "Call", "NormalEnd")
            .Connect("Failed", "FailedEnd")
            .Connect("Timer", "NormalEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("call", BpmnInterpreter.CallActivityFaultedOutcomeName);

        instance.CancelledWork.ShouldContain(work => work.ElementId == "Timer"
            && work.Reason == BpmnInterpreter.CallActivityFailureRoutedReason);
        instance.PendingWork.ShouldBeEmpty(instance.Transcript.ToString());
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void Failure_WithoutBoundary_ActivatesErrorEventSubprocess()
    {
        var handlerBody = new BpmnProcessBuilder("error-body")
            .StartEvent("ErrorStart", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("ErrorStart", "Handle", "HandlerEnd")
            .Build();
        var definition = new BpmnProcessBuilder("call-error-subprocess")
            .StartEvent("Start")
            .CallActivity("Call", "called-process", bindingRef: "call")
            .EndEvent("NormalEnd")
            .SubProcess("OnError", bindingRef: "error-body", triggeredByEvent: true)
            .ConnectSequence("Start", "Call", "NormalEnd")
            .Build();
        var host = AdvancedScenarioFixtures.Host(new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
        {
            ["error-body"] = handlerBody
        });
        var instance = host.Start(definition);

        instance.CompleteWork("call", BpmnInterpreter.CallActivityFaultedOutcomeName);

        var handler = instance.Children.ShouldHaveSingleItem();
        handler.PendingWork.Select(work => work.BindingRef).ShouldBe(["handle"]);
        handler.CompleteWork("handle");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Kind == BpmnDiagnosticKind.EventSubprocessActivated && diagnostic.ElementId == "OnError");
    }

    [Theory]
    [InlineData(BpmnInterpreter.CallActivityFaultedOutcomeName, BpmnInterpreter.CallActivityFaultedFaultCode)]
    [InlineData(BpmnInterpreter.CallActivityDispatchFailedOutcomeName, BpmnInterpreter.CallActivityDispatchFailedFaultCode)]
    [InlineData(BpmnInterpreter.CallActivityCancelledOutcomeName, BpmnInterpreter.CallActivityCancelledFaultCode)]
    public void FailureOutcome_NoCatcher_FaultsWithPinnedCode(string outcome, string faultCode)
    {
        var definition = new BpmnProcessBuilder("call-uncaught")
            .StartEvent("Start")
            .CallActivity("Call", "called-process", bindingRef: "call")
            .EndEvent("End")
            .ConnectSequence("Start", "Call", "End")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("call", outcome);

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Fault!.Code.ShouldBe(faultCode);
        instance.State.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Kind == BpmnDiagnosticKind.Faulted
            && diagnostic.Message.Contains("Call", StringComparison.Ordinal)
            && diagnostic.Message.Contains(outcome, StringComparison.Ordinal));
    }

    [Fact]
    public void MultiInstanceCallFailure_FiresBoundaryAndStopsSequentialLoop()
    {
        var definition = new BpmnProcessBuilder("call-loop-failure")
            .StartEvent("Start")
            .Element(Call("Call", "call", new BpmnLoopCharacteristics(isSequential: true, cardinality: 3)))
            .BoundaryEvent("Failed", "Call", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Recover", bindingRef: "recover")
            .EndEvent("NormalEnd")
            .EndEvent("RecoveredEnd")
            .ConnectSequence("Start", "Call", "NormalEnd")
            .ConnectSequence("Failed", "Recover", "RecoveredEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);
        var coordinatorId = instance.State.Loops.ShouldHaveSingleItem().TokenId;

        instance.CompleteWork("call", BpmnInterpreter.CallActivityFaultedOutcomeName);

        var started = instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Select(command => command.ElementId).ToArray();
        started.Count(elementId => elementId == "Call").ShouldBe(1);
        started.Count(elementId => elementId == "Recover").ShouldBe(1);
        instance.Transcript.SelectMany(entry => entry.TokenMoves).ShouldContain(move =>
            move.TokenId == coordinatorId && move.To == BpmnTokenStatus.Canceled);
        instance.State.Loops.ShouldNotContain(loop => loop.ElementId == "Call");
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Recover"]);
        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void MultiInstanceCall_RoutesOnlyAfterTheLastCompletion()
    {
        var definition = new BpmnProcessBuilder("call-loop-complete")
            .StartEvent("Start")
            .Element(Call("Call", "call", new BpmnLoopCharacteristics(isSequential: true, cardinality: 3)))
            .Task("After", bindingRef: "after")
            .EndEvent("End")
            .ConnectSequence("Start", "Call", "After", "End")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("call", "Completed");
        instance.IsCompleted.ShouldBeFalse();
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Call"]);
        instance.CompleteWork("call", "Completed");
        instance.IsCompleted.ShouldBeFalse();
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Call"]);
        instance.CompleteWork("call", "Completed");

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["After"]);
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void IdenticalRuns_ProduceIdenticalTokenAndDiagnosticIds()
    {
        var first = RunFailureBoundary();
        var second = RunFailureBoundary();

        first.TokenMoves.ShouldNotBeEmpty();
        first.TokenMoves.ShouldBe(second.TokenMoves);
        first.State.Diagnostics.Select(diagnostic => $"{diagnostic.DiagnosticId}:{diagnostic.Kind}:{diagnostic.ElementId}:{diagnostic.TokenId}")
            .ShouldBe(second.State.Diagnostics.Select(diagnostic => $"{diagnostic.DiagnosticId}:{diagnostic.Kind}:{diagnostic.ElementId}:{diagnostic.TokenId}"));
        first.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.CallActivityFailureRouted);
    }

    private static (BpmnExecutionState State, string[] TokenMoves) RunFailureBoundary()
    {
        var definition = new BpmnProcessBuilder("call-determinism")
            .StartEvent("Start")
            .CallActivity("Call", "called-process", bindingRef: "call")
            .BoundaryEvent("Failed", "Call", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Recover", bindingRef: "recover")
            .EndEvent("NormalEnd")
            .EndEvent("RecoveredEnd")
            .ConnectSequence("Start", "Call", "NormalEnd")
            .ConnectSequence("Failed", "Recover", "RecoveredEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);
        instance.CompleteWork("call", BpmnInterpreter.CallActivityFaultedOutcomeName);
        instance.CompleteWork("recover");
        var tokenMoves = instance.Transcript.SelectMany(entry => entry.TokenMoves)
            .Select(move => $"{move.TokenId}:{move.ElementId}:{move.From}:{move.To}")
            .ToArray();
        return (instance.State, tokenMoves);
    }

    private static BpmnElement Call(string elementId, string bindingRef, BpmnLoopCharacteristics? loop = null) =>
        new(elementId, BpmnElementTypes.CallActivity,
            bindingRef: bindingRef,
            loopCharacteristics: loop,
            properties: new Dictionary<string, string>(StringComparer.Ordinal) { ["calledElement"] = "called-process" });
}
