using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class MultiInstanceScenarioTests
{
    [Fact]
    public void ParallelCardinalityLoop_RoutesOnlyAfterTheLastInstanceCompletes()
    {
        var definition = new BpmnProcessBuilder("cardinality-after")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: 3))
            .Task("After", bindingRef: "after")
            .EndEvent("End")
            .ConnectSequence("Start", "Review", "After", "End")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);
        var work = instance.PendingWork.ToArray();

        work.Length.ShouldBe(3);
        work.Select(item => item.ElementId).ShouldAllBe(elementId => elementId == "Review");
        work.Select(item => item.IterationId).Distinct().Count().ShouldBe(3);
        instance.CompleteWork(work[0]);
        instance.CompleteWork(work[1]);

        instance.IsCompleted.ShouldBeFalse(instance.Transcript.ToString());
        instance.PendingWork.Select(item => item.ElementId).ShouldBe(["Review"]);
        StartedWorkCount(instance, "After").ShouldBe(0);
        instance.CompleteWork(instance.PendingWork.Single());

        instance.PendingWork.Select(item => item.ElementId).ShouldBe(["After"]);
        StartedWorkCount(instance, "After").ShouldBe(1);
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void InterruptingBoundary_MidLoop_CancelsInstancesAndRoutesBoundaryFlow()
    {
        var timer = new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BpmnEventDefinitionProperties.Interval] = "PT5M"
            });
        var definition = new BpmnProcessBuilder("cardinality-boundary")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: 2))
            .BoundaryEvent("Timeout", "Review", timer, bindingRef: "timeout")
            .Task("Recover", bindingRef: "recover")
            .EndEvent("NormalEnd")
            .EndEvent("BoundaryEnd")
            .ConnectSequence("Start", "Review", "NormalEnd")
            .ConnectSequence("Timeout", "Recover", "BoundaryEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);

        instance.PendingWork.Count(work => work.ElementId == "Review").ShouldBe(2);
        instance.PendingWork.ShouldContain(work => work.ElementId == "Timeout" && work.IsTimer);
        instance.Clock.Advance(TimeSpan.FromMinutes(5));

        instance.CancelledWork.Count(work => work.ElementId == "Review"
            && work.Reason == BpmnInterpreter.BoundaryHostInterruptedReason).ShouldBe(2);
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Recover"]);
        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void InstanceFault_WithoutErrorBoundary_FaultsTheLoopScope()
    {
        var instance = AdvancedScenarioFixtures.Host().Start(ProcessFixtures.MultiInstanceByCardinality(2, sequential: true));

        instance.FaultWork("review", "instance failed");

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.IsCompleted.ShouldBeFalse();
        instance.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void InstanceFault_WithErrorBoundary_InterruptsTheCoordinatorAndRoutesRecovery()
    {
        var definition = new BpmnProcessBuilder("sequential-fault-boundary")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: true, cardinality: 2))
            .BoundaryEvent("Failed", "Review", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Recover", bindingRef: "recover")
            .EndEvent("NormalEnd")
            .EndEvent("RecoveredEnd")
            .ConnectSequence("Start", "Review", "NormalEnd")
            .ConnectSequence("Failed", "Recover", "RecoveredEnd")
            .Build();
        var instance = AdvancedScenarioFixtures.Host().Start(definition);
        var coordinatorId = instance.State.Loops.ShouldHaveSingleItem().TokenId;

        instance.FaultWork("review", "instance failed");

        instance.IsFaulted.ShouldBeFalse();
        instance.Transcript.SelectMany(entry => entry.TokenMoves)
            .ShouldContain(move => move.TokenId == coordinatorId && move.To == BpmnTokenStatus.Canceled);
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.Canceled
            && diagnostic.TokenId == coordinatorId && diagnostic.Message.Contains("caught a work fault", StringComparison.Ordinal));
        instance.State.Loops.ShouldBeEmpty();
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Recover"]);
        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void IdenticalCardinalityLoops_ProduceIdenticalLoopAndTokenMoveIds()
    {
        var first = StartParallelCardinalityLoop();
        var second = StartParallelCardinalityLoop();

        first.State.Loops.Select(loop => loop.LoopId).ShouldNotBeEmpty();
        first.State.Loops.Select(loop => loop.LoopId).ShouldBe(second.State.Loops.Select(loop => loop.LoopId));
        first.TokenMoves.ShouldNotBeEmpty();
        first.TokenMoves.ShouldBe(second.TokenMoves);
    }

    [Fact]
    public void CollectionSequentialLoop_UsesEachItemInOrderAndAdvancesOneAtATime()
    {
        var variables = new InMemoryVariables().SetCollection("items", new[] { "a", "b", "c" });
        var instance = AdvancedScenarioFixtures.Host().Start(CollectionProcess(isSequential: true), variables: variables);

        foreach (var expectedItem in new string?[] { "a", "b", "c" })
        {
            var work = instance.PendingWork.ShouldHaveSingleItem();
            Item(work).ShouldBe(expectedItem);
            instance.CompleteWork(work);
        }

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void CollectionParallelLoop_SeedsDistinctItemsAndRoutesOnlyAfterTheLastCompletion()
    {
        var variables = new InMemoryVariables().SetCollection("items", new[] { "a", "b", "c" });
        var instance = AdvancedScenarioFixtures.Host().Start(CollectionProcess(isSequential: false, withAfter: true), variables: variables);
        var work = instance.PendingWork.ToArray();

        work.Length.ShouldBe(3);
        work.Select(item => item.IterationId).Distinct().Count().ShouldBe(3);
        work.Select(Item).OrderBy(item => item, StringComparer.Ordinal).ShouldBe(["a", "b", "c"]);
        StartedWorkCount(instance, "After").ShouldBe(0);
        instance.CompleteWork(work[0]);
        instance.CompleteWork(work[1]);

        instance.IsCompleted.ShouldBeFalse(instance.Transcript.ToString());
        instance.PendingWork.Select(item => item.ElementId).ShouldBe(["Review"]);
        StartedWorkCount(instance, "After").ShouldBe(0);
        instance.CompleteWork(instance.PendingWork.Single());

        instance.PendingWork.Select(item => item.ElementId).ShouldBe(["After"]);
        StartedWorkCount(instance, "After").ShouldBe(1);
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void EmptyCollection_RoutesImmediatelyWithoutStartingInstances()
    {
        var variables = new InMemoryVariables().SetCollection("items", Array.Empty<string>());
        var instance = AdvancedScenarioFixtures.Host().Start(CollectionProcess(isSequential: true), variables: variables);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.PendingWork.ShouldBeEmpty();
        instance.State.Loops.ShouldBeEmpty();
    }

    [Fact]
    public void NullCollection_RoutesImmediatelyWithoutStartingInstances()
    {
        var variables = new InMemoryVariables().SetNull("items");
        var instance = AdvancedScenarioFixtures.Host().Start(CollectionProcess(isSequential: false), variables: variables);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.PendingWork.ShouldBeEmpty();
        instance.State.Loops.ShouldBeEmpty();
    }

    [Fact]
    public void NonArrayCollection_FaultsWithTheCollectionShapeCode()
    {
        var variables = new InMemoryVariables().SetValue("items", "not-an-array");
        var instance = AdvancedScenarioFixtures.Host().Start(CollectionProcess(isSequential: true), variables: variables);

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Fault!.Code.ShouldBe(BpmnInterpreter.CollectionNotACollectionFaultCode);
        instance.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void CollectionSnapshot_IsStoredOnLoopAndDrivesLaterSequentialInstances()
    {
        var variables = new InMemoryVariables().SetCollection("items", new[] { "a", "b", "c" });
        var instance = AdvancedScenarioFixtures.Host().Start(CollectionProcess(isSequential: true), variables: variables);

        var loop = instance.State.Loops.ShouldHaveSingleItem();
        loop.Items!.Select(item => item.GetString()).ShouldBe(["a", "b", "c"]);
        loop.TotalCount.ShouldBe(3);
        instance.Variables.SetCollection("items", new[] { "x", "y" });
        instance.CompleteWork(instance.PendingWork.Single());

        Item(instance.PendingWork.ShouldHaveSingleItem()).ShouldBe("b");
        instance.CompleteWork(instance.PendingWork.Single());
        Item(instance.PendingWork.ShouldHaveSingleItem()).ShouldBe("c");
        instance.CompleteWork(instance.PendingWork.Single());

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void CollectionLoop_ReachedOnWorkCompletion_ReadsTheCurrentCollection()
    {
        var definition = new BpmnProcessBuilder("collection-on-completion")
            .Variable("items")
            .StartEvent("Start")
            .Task("Prepare", bindingRef: "prepare")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: true, collectionVariable: "items"))
            .EndEvent("End")
            .ConnectSequence("Start", "Prepare", "Review", "End")
            .Build();
        var variables = new InMemoryVariables().SetCollection("items", new[] { "old" });
        var instance = AdvancedScenarioFixtures.Host().Start(definition, variables: variables);

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Prepare"]);
        instance.Variables.SetCollection("items", new[] { "new-a", "new-b" });
        instance.CompleteWork("prepare");

        Item(instance.PendingWork.ShouldHaveSingleItem()).ShouldBe("new-a");
        instance.CompleteWork(instance.PendingWork.Single());
        Item(instance.PendingWork.ShouldHaveSingleItem()).ShouldBe("new-b");
        instance.CompleteWork(instance.PendingWork.Single());
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void IdenticalCollectionLoops_ProduceIdenticalLoopAndTokenMoveIds()
    {
        var first = StartCollectionLoop();
        var second = StartCollectionLoop();

        first.State.Loops.Select(loop => loop.LoopId).ShouldNotBeEmpty();
        first.State.Loops.Select(loop => loop.LoopId).ShouldBe(second.State.Loops.Select(loop => loop.LoopId));
        first.TokenMoves.ShouldNotBeEmpty();
        first.TokenMoves.ShouldBe(second.TokenMoves);
    }

    [Fact]
    public void CollectionLoop_InterruptingBoundaryCancelsInstancesAndRoutesRecovery()
    {
        var timer = new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BpmnEventDefinitionProperties.Interval] = "PT5M"
            });
        var definition = new BpmnProcessBuilder("collection-boundary")
            .Variable("items")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, collectionVariable: "items"))
            .BoundaryEvent("Timeout", "Review", timer, bindingRef: "timeout")
            .Task("Recover", bindingRef: "recover")
            .EndEvent("NormalEnd")
            .EndEvent("RecoveredEnd")
            .ConnectSequence("Start", "Review", "NormalEnd")
            .ConnectSequence("Timeout", "Recover", "RecoveredEnd")
            .Build();
        var variables = new InMemoryVariables().SetCollection("items", new[] { "a", "b" });
        var instance = AdvancedScenarioFixtures.Host().Start(definition, variables: variables);

        instance.PendingWork.Count(work => work.ElementId == "Review").ShouldBe(2);
        instance.Clock.Advance(TimeSpan.FromMinutes(5));

        instance.CancelledWork.Count(work => work.ElementId == "Review"
            && work.Reason == BpmnInterpreter.BoundaryHostInterruptedReason).ShouldBe(2);
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["Recover"]);
        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    private static BpmnProcessDefinition CollectionProcess(bool isSequential, bool withAfter = false)
    {
        var builder = new BpmnProcessBuilder("collection-loop")
            .Variable("items")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: isSequential, collectionVariable: "items"));

        if (withAfter)
            return builder.Task("After", bindingRef: "after")
                .EndEvent("End")
                .ConnectSequence("Start", "Review", "After", "End")
                .Build();

        return builder
            .EndEvent("End")
            .ConnectSequence("Start", "Review", "End")
            .Build();
    }

    private static (BpmnExecutionState State, string[] TokenMoves) StartParallelCardinalityLoop()
    {
        var instance = AdvancedScenarioFixtures.Host().Start(ProcessFixtures.MultiInstanceByCardinality(2));
        return Capture(instance);
    }

    private static (BpmnExecutionState State, string[] TokenMoves) StartCollectionLoop()
    {
        var variables = new InMemoryVariables().SetCollection("items", new[] { "a", "b" });
        var instance = AdvancedScenarioFixtures.Host().Start(CollectionProcess(isSequential: false), variables: variables);
        return Capture(instance);
    }

    private static (BpmnExecutionState State, string[] TokenMoves) Capture(InMemoryProcessInstance instance) =>
        (instance.State,
            instance.Transcript.SelectMany(entry => entry.TokenMoves)
                .Select(move => $"{move.TokenId}:{move.ElementId}:{move.From}:{move.To}")
                .ToArray());

    private static int StartedWorkCount(InMemoryProcessInstance instance, string elementId) =>
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.ElementId == elementId);

    private static string? Item(InMemoryWorkItem work) =>
        work.IterationValues["item"].Json!.Value.GetString();
}
