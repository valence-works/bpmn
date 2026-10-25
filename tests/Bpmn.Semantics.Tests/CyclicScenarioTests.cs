using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>Loop iterations retain their identity through work, catch events, multi-instance scopes, and joins.</summary>
public sealed class CyclicScenarioTests
{
    [Fact]
    public void DoWhile_ExclusiveGatewayCountLoop_RunsBodyPerPass_AndCompletes()
    {
        var instance = new InMemoryBpmnHost().Start(SimpleLoop());
        for (var pass = 0; pass < 3; pass++)
        {
            instance.PendingWork.Select(work => work.ElementId).ShouldBe(["body"]);
            instance.CompleteWork("body");
            instance.PendingWork.Select(work => work.ElementId).ShouldBe(["check"]);
            instance.CompleteWork("check", "Again");
        }

        instance.CompleteWork("body");
        instance.CompleteWork("check", "Exit");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        var starts = instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>().ToArray();
        starts.Count(command => command.ElementId == "body").ShouldBe(4);
        starts.Count(command => command.ElementId == "check").ShouldBe(4);
    }

    [Fact]
    public void ParallelForkJoinInsideLoop_JoinsOncePerIteration_WithCorrectPairing()
    {
        var instance = new InMemoryBpmnHost().Start(ParallelLoop());
        var joinKeys = new List<string?>();
        for (var pass = 0; pass < 2; pass++)
        {
            instance.CompleteWork("top");
            instance.PendingWork.Select(work => work.ElementId).ShouldBe(["left", "right"]);
            instance.CompleteWork("left");
            instance.CompleteWork("right");
            instance.PendingWork.Select(work => work.ElementId).ShouldBe(["check"]);
            joinKeys.Add(instance.State.Tokens.Single(token => token.AtElementId == "check" && token.Status == BpmnTokenStatus.AwaitingChild).IterationKey);
            instance.CompleteWork("check", pass == 0 ? "Again" : "Exit");
        }

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.Joined && diagnostic.ElementId == "join").ShouldBe(2);
        joinKeys.Distinct(StringComparer.Ordinal).Count().ShouldBe(2, "the two join firings must retain separate loop keys");
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.ElementId == "left").ShouldBe(2);
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.ElementId == "right").ShouldBe(2);
    }

    [Fact]
    public void CatchEventInsideLoop_ReArmsPerIteration_WithDistinctIterationKeys()
    {
        var instance = new InMemoryBpmnHost().Start(CatchLoop());
        var observedKeys = new List<string?>();
        for (var pass = 0; pass < 3; pass++)
        {
            instance.PendingWork.Select(work => work.ElementId).ShouldBe(["catch"]);
            observedKeys.Add(instance.State.Tokens.Single(token => token.AtElementId == "catch" && token.Status == BpmnTokenStatus.AwaitingChild).IterationKey);
            instance.CompleteWork("catch-work");
            instance.PendingWork.Select(work => work.ElementId).ShouldBe(["check"]);
            instance.CompleteWork("check", pass < 2 ? "Again" : "Exit");
        }

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        observedKeys[0].ShouldBeNull();
        observedKeys.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.ElementId == "catch").ShouldBe(3);
        instance.PendingWork.ShouldBeEmpty("every catch wait has completed before its loop continuation");
    }

    [Fact]
    public void MultiInstanceHostInsideLoop_EachPassRunsItsOwnInstances()
    {
        var definition = new BpmnProcessBuilder("mi-loop")
            .StartEvent("start")
            .Element(new BpmnElement("mi", BpmnElementTypes.Task, bindingRef: "mi-work",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: true, cardinality: 2)))
            .Element(new BpmnElement("check", BpmnElementTypes.ExclusiveGateway, bindingRef: "check-work"))
            .EndEvent("end")
            .Connect("start", "mi")
            .Connect("mi", "check")
            .Connect("check", "mi", condition: "Again", flowId: "again")
            .Connect("check", "end", isDefault: true, flowId: "exit")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);
        var seenIterationIds = new List<string>();
        for (var pass = 0; pass < 2; pass++)
        {
            for (var index = 0; index < 2; index++)
            {
                var iteration = instance.PendingWork.Single(work => work.ElementId == "mi");
                seenIterationIds.Add(iteration.IterationId!);
                iteration.IterationId.ShouldNotBeNull();
                instance.CompleteWork(iteration);
            }

            instance.PendingWork.Select(work => work.ElementId).ShouldBe(["check"]);
            instance.CompleteWork("check-work", pass == 0 ? "Again" : "Exit");
        }

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        seenIterationIds.Count.ShouldBe(4);
        seenIterationIds.Distinct(StringComparer.Ordinal).Count().ShouldBe(4);
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.ElementId == "mi").ShouldBe(4);
    }

    [Fact]
    public void BoundaryHostInsideLoop_ListenerArmsAndTearsDownPerPass()
    {
        var timer = new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Interval] = "PT1H" });
        var definition = new BpmnProcessBuilder("boundary-loop")
            .StartEvent("start")
            .Task("host", bindingRef: "host-work")
            .BoundaryEvent("timer", "host", timer, bindingRef: "timer-work")
            .Element(new BpmnElement("check", BpmnElementTypes.ExclusiveGateway, bindingRef: "check-work"))
            .EndEvent("end")
            .EndEvent("timed-out")
            .Connect("start", "host")
            .Connect("host", "check")
            .Connect("check", "host", condition: "Again", flowId: "again")
            .Connect("check", "end", isDefault: true, flowId: "exit")
            .Connect("timer", "timed-out")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);

        for (var pass = 0; pass < 2; pass++)
        {
            instance.PendingWork.Select(work => work.ElementId).ShouldBe(["host", "timer"]);
            instance.PendingWork.Single(work => work.ElementId == "timer").IsTimer.ShouldBeTrue();
            instance.CompleteWork("host-work");
            instance.CancelledWork.Count(work => work.ElementId == "timer").ShouldBe(pass + 1);
            instance.PendingWork.Select(work => work.ElementId).ShouldBe(["check"]);
            instance.CompleteWork("check-work", pass == 0 ? "Again" : "Exit");
        }

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.CancelledWork.Count(work => work.ElementId == "timer").ShouldBe(2);
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.ElementId == "timer").ShouldBe(2);
    }

    [Fact]
    public void IdenticalRuns_ProduceIdenticalIterationKeys()
    {
        RunCatchLoopAndCollectKeys().ShouldBe(RunCatchLoopAndCollectKeys());
    }

    private static IReadOnlyList<string?> RunCatchLoopAndCollectKeys()
    {
        var instance = new InMemoryBpmnHost().Start(CatchLoop());
        var keys = new List<string?>();
        for (var pass = 0; pass < 3; pass++)
        {
            keys.Add(instance.State.Tokens.Single(token => token.AtElementId == "catch" && token.Status == BpmnTokenStatus.AwaitingChild).IterationKey);
            instance.CompleteWork("catch-work");
            instance.CompleteWork("check", pass < 2 ? "Again" : "Exit");
        }
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        return keys;
    }

    private static BpmnProcessDefinition SimpleLoop() => new BpmnProcessBuilder("do-while")
        .StartEvent("start")
        .Task("body", bindingRef: "body")
        .Element(new BpmnElement("check", BpmnElementTypes.ExclusiveGateway, bindingRef: "check"))
        .EndEvent("end")
        .Connect("start", "body")
        .Connect("body", "check")
        .Connect("check", "body", condition: "Again", flowId: "again")
        .Connect("check", "end", isDefault: true, flowId: "exit")
        .Build();

    private static BpmnProcessDefinition ParallelLoop() => new BpmnProcessBuilder("parallel-loop")
        .StartEvent("start")
        .Task("top", bindingRef: "top")
        .ParallelGateway("fork")
        .Task("left", bindingRef: "left")
        .Task("right", bindingRef: "right")
        .ParallelGateway("join")
        .Element(new BpmnElement("check", BpmnElementTypes.ExclusiveGateway, bindingRef: "check"))
        .EndEvent("end")
        .ConnectSequence("start", "top", "fork")
        .Connect("fork", "left")
        .Connect("fork", "right")
        .Connect("left", "join")
        .Connect("right", "join")
        .Connect("join", "check")
        .Connect("check", "top", condition: "Again", flowId: "again")
        .Connect("check", "end", isDefault: true, flowId: "exit")
        .Build();

    private static BpmnProcessDefinition CatchLoop() => new BpmnProcessBuilder("catch-loop")
        .StartEvent("start")
        .IntermediateCatchEvent("catch", new BpmnEventDefinition(BpmnEventDefinitionTypes.Message), bindingRef: "catch-work")
        .Element(new BpmnElement("check", BpmnElementTypes.ExclusiveGateway, bindingRef: "check"))
        .EndEvent("end")
        .Connect("start", "catch")
        .Connect("catch", "check")
        .Connect("check", "catch", condition: "Again", flowId: "again")
        .Connect("check", "end", isDefault: true, flowId: "exit")
        .Build();
}
