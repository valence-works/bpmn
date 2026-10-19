using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class CompensationScenarioTests
{
    [Fact]
    public void RefLessThrow_ReplaysNewestCompletionFirst_OneHandlerAtATime()
    {
        var instance = AdvancedScenarioFixtures.Host().Start(TwoHostReplay(AdvancedScenarioFixtures.CompensateThrow("Throw")));

        instance.CompleteWork("host-a");
        instance.State.Compensables.Select(compensable => compensable.HostElementId).ShouldBe(["HostA"]);
        instance.CompleteWork("host-b");

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo-b"]);
        instance.State.Compensables.ShouldAllBe(compensable => compensable.Status == BpmnCompensableStatus.Claimed);
        instance.State.CompensationRuns.ShouldHaveSingleItem().PendingCompensableIds
            .ShouldBe(instance.State.Compensables.Reverse().Select(compensable => compensable.CompensableId));

        instance.CompleteWork("undo-b");

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo-a"]);
        instance.State.Compensables.Single(compensable => compensable.HostElementId == "HostB").Status
            .ShouldBe(BpmnCompensableStatus.Compensated);
        instance.State.Compensables.Single(compensable => compensable.HostElementId == "HostA").Status
            .ShouldBe(BpmnCompensableStatus.Claimed);

        instance.CompleteWork("undo-a");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.CompensationRuns.ShouldBeEmpty();
        instance.State.Compensables.ShouldAllBe(compensable => compensable.Status == BpmnCompensableStatus.Compensated);
    }

    [Fact]
    public void ReplayLifecycle_IsVisibleAfterTheThrowRoutesAndTheScopeRemainsActive()
    {
        var definition = new BpmnProcessBuilder("replay-lifecycle")
            .StartEvent("Start")
            .Task("Host", bindingRef: "host")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("CompBoundary", "Host", "Handler"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("Handler", "undo"))
            .Task("Gate", bindingRef: "gate")
            .IntermediateThrowEvent("Throw", new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation))
            .Task("Hold", bindingRef: "hold")
            .EndEvent("End")
            .ConnectSequence("Start", "Host", "Gate", "Throw", "Hold", "End")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("host");
        instance.State.Compensables.ShouldHaveSingleItem().Status.ShouldBe(BpmnCompensableStatus.Registered);
        instance.CompleteWork("gate");
        instance.State.Compensables.ShouldHaveSingleItem().Status.ShouldBe(BpmnCompensableStatus.Claimed);
        instance.State.CompensationRuns.ShouldHaveSingleItem().PendingCompensableIds
            .ShouldBe([instance.State.Compensables.Single().CompensableId]);
        instance.CompleteWork("undo");

        instance.IsCompleted.ShouldBeFalse();
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["hold"]);
        instance.State.Compensables.ShouldHaveSingleItem().Status.ShouldBe(BpmnCompensableStatus.Compensated);
        instance.State.CompensationRuns.ShouldBeEmpty();
    }

    [Fact]
    public void ActivityRefThrow_ClaimsOnlyTheReferencedHostsRegistration()
    {
        var instance = AdvancedScenarioFixtures.Host().Start(
            TwoHostReplay(AdvancedScenarioFixtures.CompensateThrow("Throw", activityRef: "HostA")));

        instance.CompleteWork("host-a");
        instance.CompleteWork("host-b");
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo-a"]);
        instance.CompleteWork("undo-a");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Compensables.Single(compensable => compensable.HostElementId == "HostA").Status
            .ShouldBe(BpmnCompensableStatus.Compensated);
        instance.State.Compensables.Single(compensable => compensable.HostElementId == "HostB").Status
            .ShouldBe(BpmnCompensableStatus.Registered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Host")]
    public void EmptyLogThrow_CompletesWithoutStartingAHandlerOrCreatingARun(string? activityRef)
    {
        var definition = new BpmnProcessBuilder("empty-log")
            .StartEvent("Start")
            .Element(AdvancedScenarioFixtures.CompensateThrow("Throw", activityRef))
            .EndEvent("End")
            .Task("Host", bindingRef: "host")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("CompBoundary", "Host", "Handler"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("Handler", "undo"))
            .ConnectSequence("Start", "Throw", "End")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.PendingWork.ShouldBeEmpty();
        instance.State.Compensables.ShouldBeEmpty();
        instance.State.CompensationRuns.ShouldBeEmpty();
    }

    [Fact]
    public void CompensateEnd_ReplaysHandlersAndConsumesItsToken()
    {
        var definition = TwoHostReplay(AdvancedScenarioFixtures.CompensateEnd("CompensateEnd"), addTerminalEnd: false);
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("host-a");
        instance.CompleteWork("host-b");
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo-b"]);
        instance.CompleteWork("undo-b");
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo-a"]);
        instance.CompleteWork("undo-a");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.CompensationRuns.ShouldBeEmpty();
        instance.State.Compensables.ShouldAllBe(compensable => compensable.Status == BpmnCompensableStatus.Compensated);
        instance.State.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Kind == BpmnDiagnosticKind.Consumed && diagnostic.ElementId == "CompensateEnd");
    }

    [Fact]
    public void Cycle_RegistersOncePerSuccessfulHostCompletionAndReplaysEveryRegistration()
    {
        var definition = new BpmnProcessBuilder("cycle-compensation")
            .StartEvent("Start")
            .Task("Host", bindingRef: "host")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("CompBoundary", "Host", "Handler"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("Handler", "undo"))
            .Element(new BpmnElement("Decision", BpmnElementTypes.ExclusiveGateway, bindingRef: "decision"))
            .IntermediateThrowEvent("Throw", new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation))
            .EndEvent("End")
            .Connect("Start", "Host")
            .Connect("Host", "Decision")
            .Connect("Decision", "Host", condition: "again")
            .Connect("Decision", "Throw", isDefault: true)
            .Connect("Throw", "End")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("host");
        instance.CompleteWork("decision", "again");
        instance.CompleteWork("host");
        instance.State.Compensables.Count.ShouldBe(2);
        instance.CompleteWork("decision", "exit");

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo"]);
        instance.CompleteWork("undo");
        instance.State.Compensables.Count(compensable => compensable.Status == BpmnCompensableStatus.Compensated)
            .ShouldBe(1);
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo"]);
        instance.CompleteWork("undo");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Compensables.Count.ShouldBe(2);
        instance.State.Compensables.ShouldAllBe(compensable => compensable.HostElementId == "Host"
            && compensable.Status == BpmnCompensableStatus.Compensated);
    }

    [Fact]
    public void HandlerFault_FaultsTheScopeInsteadOfCompletingTheThrow()
    {
        var instance = AdvancedScenarioFixtures.Host().Start(
            OneHostReplay(AdvancedScenarioFixtures.CompensateThrow("Throw"), includeEnd: true));

        instance.CompleteWork("host");
        instance.FaultWork("undo", "handler failed");

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.IsCompleted.ShouldBeFalse();
        instance.State.CompensationRuns.ShouldHaveSingleItem();
    }

    [Fact]
    public void ConcurrentThrows_ClaimDisjointTargetsAndRunEachHandlerOnce()
    {
        var definition = new BpmnProcessBuilder("parallel-compensation")
            .StartEvent("Start")
            .Task("HostA", bindingRef: "host-a")
            .Task("HostB", bindingRef: "host-b")
            .ParallelGateway("Fork")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("CompA", "HostA", "HandlerA"))
            .Element(AdvancedScenarioFixtures.CompensationBoundary("CompB", "HostB", "HandlerB"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("HandlerA", "undo-a"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("HandlerB", "undo-b"))
            .Element(AdvancedScenarioFixtures.CompensateThrow("ThrowA", "HostA"))
            .Element(AdvancedScenarioFixtures.CompensateThrow("ThrowB", "HostB"))
            .ParallelGateway("Join")
            .EndEvent("End")
            .ConnectSequence("Start", "HostA", "HostB", "Fork")
            .Connect("Fork", "ThrowA")
            .Connect("Fork", "ThrowB")
            .Connect("ThrowA", "Join")
            .Connect("ThrowB", "Join")
            .ConnectSequence("Join", "End")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("host-a");
        instance.CompleteWork("host-b");

        instance.PendingWork.Select(work => work.BindingRef).OrderBy(value => value).ShouldBe(["undo-a", "undo-b"]);
        instance.State.Compensables.Count(compensable => compensable.Status == BpmnCompensableStatus.Claimed)
            .ShouldBe(2);
        instance.CompleteWork(instance.ResolveWork("undo-a"));
        instance.CompleteWork(instance.ResolveWork("undo-b"));

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Compensables.ShouldAllBe(compensable => compensable.Status == BpmnCompensableStatus.Compensated);
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.Compensated).ShouldBe(2);
    }

    [Fact]
    public void TerminateDuringReplayRetainsTheRunAndItsClaimedRegistration()
    {
        var definition = new BpmnProcessBuilder("terminate-during-replay")
            .StartEvent("Start")
            .Task("Host", bindingRef: "host")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("CompBoundary", "Host", "Handler"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("Handler", "undo"))
            .ParallelGateway("Fork")
            .Element(AdvancedScenarioFixtures.CompensateThrow("Throw"))
            .Task("Waiter", bindingRef: "wait")
            .EndEvent("Terminate", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Terminate))
            .EndEvent("End")
            .ConnectSequence("Start", "Host", "Fork")
            .ConnectSequence("Fork", "Throw", "End")
            .ConnectSequence("Fork", "Waiter", "Terminate")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("host");
        instance.PendingWork.Select(work => work.BindingRef).OrderBy(value => value).ShouldBe(["undo", "wait"]);
        instance.CompleteWork("wait");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.CompensationRuns.ShouldHaveSingleItem();
        instance.State.Compensables.ShouldHaveSingleItem().Status.ShouldBe(BpmnCompensableStatus.Claimed);
    }

    [Fact]
    public void IdenticalRuns_ProduceIdenticalCompensationAndTokenIds()
    {
        var first = RunReplay();
        var second = RunReplay();

        first.State.Compensables.Select(compensable => $"{compensable.CompensableId}:{compensable.HostElementId}:{compensable.Status}")
            .OrderBy(value => value).ShouldBe(second.State.Compensables
                .Select(compensable => $"{compensable.CompensableId}:{compensable.HostElementId}:{compensable.Status}")
                .OrderBy(value => value));
        first.State.Diagnostics.Select(diagnostic => $"{diagnostic.DiagnosticId}:{diagnostic.Kind}:{diagnostic.ElementId}:{diagnostic.TokenId}")
            .ShouldBe(second.State.Diagnostics.Select(diagnostic => $"{diagnostic.DiagnosticId}:{diagnostic.Kind}:{diagnostic.ElementId}:{diagnostic.TokenId}"));
        first.TokenMoves.ShouldNotBeEmpty();
        first.TokenMoves.ShouldBe(second.TokenMoves);
        first.State.Compensables.ShouldNotBeEmpty();
        first.State.Diagnostics.ShouldNotBeEmpty();
    }

    private static (BpmnExecutionState State, string[] TokenMoves) RunReplay()
    {
        var instance = AdvancedScenarioFixtures.Host().Start(TwoHostReplay(AdvancedScenarioFixtures.CompensateThrow("Throw")));
        instance.CompleteWork("host-a");
        instance.CompleteWork("host-b");
        instance.CompleteWork("undo-b");
        instance.CompleteWork("undo-a");
        var tokenMoves = instance.Transcript.SelectMany(entry => entry.TokenMoves)
            .Select(move => $"{move.TokenId}:{move.ElementId}:{move.From}:{move.To}")
            .ToArray();
        return (instance.State, tokenMoves);
    }

    private static BpmnProcessDefinition OneHostReplay(BpmnElement thrower, bool includeEnd)
    {
        var builder = new BpmnProcessBuilder("one-host-replay")
            .StartEvent("Start")
            .Task("Host", bindingRef: "host")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("CompBoundary", "Host", "Handler"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("Handler", "undo"))
            .Element(thrower);
        if (includeEnd)
            builder.EndEvent("End").ConnectSequence("Start", "Host", thrower.ElementId, "End");
        else
            builder.ConnectSequence("Start", "Host", thrower.ElementId);
        return builder.Build();
    }

    private static BpmnProcessDefinition TwoHostReplay(BpmnElement thrower, bool addTerminalEnd = true)
    {
        var builder = new BpmnProcessBuilder("two-host-replay")
            .StartEvent("Start")
            .Task("HostA", bindingRef: "host-a")
            .Task("HostB", bindingRef: "host-b")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("CompA", "HostA", "HandlerA"))
            .Element(AdvancedScenarioFixtures.CompensationBoundary("CompB", "HostB", "HandlerB"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("HandlerA", "undo-a"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("HandlerB", "undo-b"))
            .Element(thrower);
        if (addTerminalEnd)
        {
            builder.EndEvent("End").ConnectSequence("Start", "HostA", "HostB", thrower.ElementId, "End");
        }
        else
        {
            builder.ConnectSequence("Start", "HostA", "HostB", thrower.ElementId);
        }
        return builder.Build();
    }
}
