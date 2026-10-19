using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class TransactionScenarioTests
{
    [Fact]
    public void RootCancel_ReplaysNewestRegistrationFirst_AndCompletesCancelled()
    {
        var definition = new BpmnProcessBuilder("root-cancel-order")
            .Transaction()
            .StartEvent("Start")
            .Task("HostA", bindingRef: "host-a")
            .Task("HostB", bindingRef: "host-b")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("BoundaryA", "HostA", "HandlerA"))
            .Element(AdvancedScenarioFixtures.CompensationBoundary("BoundaryB", "HostB", "HandlerB"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("HandlerA", "undo-a"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("HandlerB", "undo-b"))
            .Element(AdvancedScenarioFixtures.CancelEnd("CancelEnd"))
            .ConnectSequence("Start", "HostA", "HostB", "CancelEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("host-a");
        instance.State.Compensables.ShouldHaveSingleItem().Status.ShouldBe(BpmnCompensableStatus.Registered);
        instance.CompleteWork("host-b");

        instance.State.Cancelling.ShouldBeTrue();
        instance.State.Compensables.Count.ShouldBe(2);
        instance.State.Compensables.ShouldAllBe(compensable => compensable.Status == BpmnCompensableStatus.Claimed);
        instance.State.CompensationRuns.ShouldHaveSingleItem().PendingCompensableIds
            .ShouldBe(instance.State.Compensables.Reverse().Select(compensable => compensable.CompensableId));
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo-b"]);

        instance.CompleteWork("undo-b");
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo-a"]);
        instance.CompleteWork("undo-a");

        instance.Outcome.ShouldBe(BpmnInterpreter.CancelledOutcomeName, instance.Transcript.ToString());
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Compensables.ShouldAllBe(compensable => compensable.Status == BpmnCompensableStatus.Compensated);
    }

    [Fact]
    public void RootCancel_WithEmptyLog_CompletesCancelledWithoutStartingHandlers()
    {
        var definition = new BpmnProcessBuilder("root-cancel-empty")
            .Transaction()
            .StartEvent("Start")
            .Task("Work", bindingRef: "work")
            .Element(AdvancedScenarioFixtures.CancelEnd("CancelEnd"))
            .ConnectSequence("Start", "Work", "CancelEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("work");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Outcome.ShouldBe(BpmnInterpreter.CancelledOutcomeName);
        instance.PendingWork.ShouldBeEmpty();
        instance.State.Compensables.ShouldBeEmpty();
        instance.State.CompensationRuns.ShouldBeEmpty();
    }

    [Fact]
    public void RootCancel_StopsAnotherLiveBranchWhileReplaying()
    {
        var definition = new BpmnProcessBuilder("root-cancel-parallel")
            .Transaction()
            .StartEvent("Start")
            .ParallelGateway("Fork")
            .Task("Host", bindingRef: "host")
            .Task("Waiter", bindingRef: "wait")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("Boundary", "Host", "Handler"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("Handler", "undo"))
            .Element(AdvancedScenarioFixtures.CancelEnd("CancelEnd"))
            .EndEvent("AbandonedEnd")
            .Connect("Start", "Fork")
            .Connect("Fork", "Host")
            .Connect("Fork", "Waiter")
            .Connect("Host", "CancelEnd")
            .Connect("Waiter", "AbandonedEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.PendingWork.Select(work => work.BindingRef).OrderBy(value => value).ShouldBe(["host", "wait"]);
        instance.CompleteWork("host");

        instance.CancelledWork.ShouldContain(work => work.ElementId == "Waiter"
            && work.Reason == BpmnInterpreter.TransactionCancelledStopReason);
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo"]);
        instance.CompleteWork("undo");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Outcome.ShouldBe(BpmnInterpreter.CancelledOutcomeName);
        var started = instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Select(command => command.ElementId).ToArray();
        started.Count(elementId => elementId == "Waiter").ShouldBe(1);
        instance.Transcript.SelectMany(entry => entry.TokenMoves)
            .ShouldNotContain(move => move.ElementId == "AbandonedEnd");
    }

    [Fact]
    public void CancelledTransaction_WithoutParentCancelBoundary_FaultsTheParent()
    {
        var body = new BpmnProcessBuilder("transaction-body")
            .Transaction()
            .StartEvent("InnerStart")
            .Element(AdvancedScenarioFixtures.CancelEnd("CancelEnd"))
            .Connect("InnerStart", "CancelEnd")
            .Build();
        var parent = new BpmnProcessBuilder("parent-without-catcher")
            .StartEvent("Start")
            .SubProcess("Transaction", bindingRef: "transaction", isTransaction: true)
            .EndEvent("End")
            .ConnectSequence("Start", "Transaction", "End")
            .Build();
        var host = AdvancedScenarioFixtures.Host(new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
        {
            ["transaction"] = body
        });

        var instance = host.Start(parent);

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Fault!.Code.ShouldBe(BpmnInterpreter.TransactionCancelledUnhandledFaultCode);
        instance.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void DoneTransaction_RoutesNormalFlowAndLeavesCancelBoundaryDormant()
    {
        var body = new BpmnProcessBuilder("done-body")
            .StartEvent("InnerStart")
            .EndEvent("InnerEnd")
            .Connect("InnerStart", "InnerEnd")
            .Build();
        var parent = new BpmnProcessBuilder("done-parent")
            .StartEvent("Start")
            .SubProcess("Transaction", bindingRef: "transaction", isTransaction: true)
            .BoundaryEvent("Cancelled", "Transaction", new BpmnEventDefinition(BpmnEventDefinitionTypes.Cancel))
            .Task("After", bindingRef: "after")
            .Task("Recovery", bindingRef: "recovery")
            .EndEvent("NormalEnd")
            .EndEvent("CancelledEnd")
            .ConnectSequence("Start", "Transaction", "After", "NormalEnd")
            .ConnectSequence("Cancelled", "Recovery", "CancelledEnd")
            .Build();
        var host = AdvancedScenarioFixtures.Host(new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
        {
            ["transaction"] = body
        });

        var instance = host.Start(parent);

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["After"]);
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        var started = instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Select(command => command.ElementId).ToArray();
        started.Count(elementId => elementId == "After").ShouldBe(1);
        started.ShouldNotContain("Recovery");
    }

    [Fact]
    public void DoneTransaction_WithCompensationBoundary_RegistersAndReplaysAsOneUnit()
    {
        var body = new BpmnProcessBuilder("compensable-body")
            .StartEvent("InnerStart")
            .EndEvent("InnerEnd")
            .Connect("InnerStart", "InnerEnd")
            .Build();
        var parent = new BpmnProcessBuilder("compensable-parent")
            .StartEvent("Start")
            .SubProcess("Transaction", bindingRef: "transaction", isTransaction: true)
            .Element(AdvancedScenarioFixtures.CompensationBoundary("Boundary", "Transaction", "Handler"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("Handler", "undo-transaction"))
            .Element(AdvancedScenarioFixtures.CompensateThrow("Replay"))
            .EndEvent("End")
            .ConnectSequence("Start", "Transaction", "Replay", "End")
            .Build();
        var host = AdvancedScenarioFixtures.Host(new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
        {
            ["transaction"] = body
        });

        var instance = host.Start(parent);

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["undo-transaction"]);
        instance.State.Compensables.ShouldHaveSingleItem().Status.ShouldBe(BpmnCompensableStatus.Claimed);
        instance.State.CompensationRuns.ShouldHaveSingleItem();
        instance.CompleteWork("undo-transaction");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Compensables.ShouldHaveSingleItem().Status.ShouldBe(BpmnCompensableStatus.Compensated);
        instance.State.CompensationRuns.ShouldBeEmpty();
    }

    [Fact]
    public void HandlerFault_DuringRootCancel_FaultsTheProcess()
    {
        var definition = new BpmnProcessBuilder("root-cancel-fault")
            .Transaction()
            .StartEvent("Start")
            .Task("Host", bindingRef: "host")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("Boundary", "Host", "Handler"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("Handler", "undo"))
            .Element(AdvancedScenarioFixtures.CancelEnd("CancelEnd"))
            .ConnectSequence("Start", "Host", "CancelEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.CompleteWork("host");
        instance.FaultWork("undo", "handler failed");

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.IsCompleted.ShouldBeFalse();
        instance.Fault.ShouldNotBeNull();
    }

    [Fact]
    public void IdenticalRootCancels_ProduceIdenticalCompensableAndTokenIds()
    {
        var first = RunRootCancel();
        var second = RunRootCancel();

        first.State.Compensables.Select(compensable => $"{compensable.CompensableId}:{compensable.HostElementId}:{compensable.Status}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ShouldBe(second.State.Compensables.Select(compensable => $"{compensable.CompensableId}:{compensable.HostElementId}:{compensable.Status}")
                .OrderBy(value => value, StringComparer.Ordinal));
        first.TokenMoves.ShouldNotBeEmpty();
        first.TokenMoves.ShouldBe(second.TokenMoves);
        first.State.Compensables.ShouldNotBeEmpty();
    }

    private static (BpmnExecutionState State, string[] TokenMoves) RunRootCancel()
    {
        var definition = new BpmnProcessBuilder("root-cancel-determinism")
            .Transaction()
            .StartEvent("Start")
            .Task("Host", bindingRef: "host")
            .Element(AdvancedScenarioFixtures.CompensationBoundary("Boundary", "Host", "Handler"))
            .Element(AdvancedScenarioFixtures.CompensationHandler("Handler", "undo"))
            .Element(AdvancedScenarioFixtures.CancelEnd("CancelEnd"))
            .ConnectSequence("Start", "Host", "CancelEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);
        instance.CompleteWork("host");
        instance.CompleteWork("undo");
        var tokenMoves = instance.Transcript.SelectMany(entry => entry.TokenMoves)
            .Select(move => $"{move.TokenId}:{move.ElementId}:{move.From}:{move.To}")
            .ToArray();
        return (instance.State, tokenMoves);
    }
}
