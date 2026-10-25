using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>Gateway routing and join behavior asserted through observable host work and interpreter state.</summary>
public sealed class GatewayScenarioTests
{
    [Fact]
    public void UnboundAbstractTask_IsPassThrough()
    {
        var definition = new BpmnProcessBuilder("unbound-task")
            .StartEvent("start")
            .Element(new BpmnElement("abstract", BpmnElementTypes.Task))
            .Task("work", bindingRef: "work")
            .EndEvent("end")
            .ConnectSequence("start", "abstract", "work", "end")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["work"]);
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Select(command => command.ElementId).ShouldBe(["work"]);
        instance.CompleteWork("work");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void EmptyProcess_CompletesImmediately()
    {
        var instance = new InMemoryBpmnHost().Start(new BpmnProcessDefinition("empty"));

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Fault.ShouldBeNull();
        instance.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void ParallelForkAndJoin_RunsBothBranches_JoinsOnce_AndCompletes()
    {
        var instance = new InMemoryBpmnHost().Start(ParallelJoin());
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["left", "right"]);

        instance.CompleteWork("left");
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["right"]);
        instance.State.Tokens.ShouldContain(token => token.AtElementId == "join" && token.Status == BpmnTokenStatus.WaitingAtJoin);

        instance.CompleteWork("right");
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["after"]);
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.ElementId == "after").ShouldBe(1);
        instance.CompleteWork("after");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.Joined && diagnostic.ElementId == "join").ShouldBe(1);
    }

    [Fact]
    public void ExclusiveGateway_WithoutMatch_TakesDefaultFlow()
    {
        var definition = new BpmnProcessBuilder("exclusive-default")
            .StartEvent("start")
            .Element(new BpmnElement("decision", BpmnElementTypes.ExclusiveGateway, bindingRef: "decision"))
            .Task("approved", bindingRef: "approved")
            .Task("rejected", bindingRef: "rejected")
            .Task("fallback", bindingRef: "fallback")
            .EndEvent("end")
            .Connect("start", "decision")
            .Connect("decision", "approved", condition: "Approved")
            .Connect("decision", "rejected", condition: "Rejected")
            .Connect("decision", "fallback", isDefault: true)
            .ConnectSequence("approved", "end")
            .ConnectSequence("rejected", "end")
            .ConnectSequence("fallback", "end")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);

        instance.CompleteWork("decision", "SomethingElse");

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["fallback"]);
        instance.CompleteWork("fallback");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        var started = instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Select(command => command.ElementId).ToArray();
        started.ShouldContain("fallback");
        started.ShouldNotContain("approved");
        started.ShouldNotContain("rejected");
    }

    [Fact]
    public void InclusiveGateway_ActivationAwareJoin_FiresWithSingleActivatedBranch()
    {
        var definition = new BpmnProcessBuilder("inclusive-single")
            .StartEvent("start")
            .Element(new BpmnElement("split", BpmnElementTypes.InclusiveGateway, bindingRef: "choice"))
            .Task("approved", bindingRef: "approved")
            .Task("extra", bindingRef: "extra")
            .InclusiveGateway("join")
            .Task("after", bindingRef: "after")
            .EndEvent("end")
            .Connect("start", "split")
            .Connect("split", "approved", condition: "Approved")
            .Connect("split", "extra", condition: "Extra")
            .Connect("approved", "join")
            .Connect("extra", "join")
            .Connect("join", "after")
            .Connect("after", "end")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["split"]);

        instance.CompleteWork("choice", "Approved");
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["approved"]);
        instance.CompleteWork("approved");

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["after"], instance.Transcript.ToString());
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Select(command => command.ElementId).ShouldNotContain("extra");
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void InclusiveGateway_BothBranchesActivated_JoinWaitsForBoth()
    {
        var definition = new BpmnProcessBuilder("inclusive-both")
            .StartEvent("start")
            .InclusiveGateway("split")
            .Task("left", bindingRef: "left")
            .Task("right", bindingRef: "right")
            .InclusiveGateway("join")
            .Task("after", bindingRef: "after")
            .EndEvent("end")
            .Connect("start", "split")
            .Connect("split", "left")
            .Connect("split", "right")
            .Connect("left", "join")
            .Connect("right", "join")
            .Connect("join", "after")
            .Connect("after", "end")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["left", "right"]);

        instance.CompleteWork("left");
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["right"]);
        instance.State.Tokens.ShouldContain(token => token.AtElementId == "join" && token.Status == BpmnTokenStatus.WaitingAtJoin);
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .ShouldNotContain(command => command.ElementId == "after");

        instance.CompleteWork("right");
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["after"]);
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Count(command => command.ElementId == "after").ShouldBe(1);
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void FaultingChild_FaultsProcess_InsteadOfHangingAtTheJoin()
    {
        var instance = new InMemoryBpmnHost().Start(ParallelJoin());
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["left", "right"]);

        instance.FaultWork("left", "branch failed");

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Fault.ShouldNotBeNull();
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .ShouldNotContain(command => command.ElementId == "after");
        instance.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void ParallelJoin_UnreachableInboundFlow_FaultsAsDeadlock()
    {
        var definition = new BpmnProcessBuilder("unreachable-parallel-join")
            .StartEvent("start")
            .Element(new BpmnElement("decision", BpmnElementTypes.ExclusiveGateway, bindingRef: "decision"))
            .Task("a", bindingRef: "a")
            .Task("b", bindingRef: "b")
            .ParallelGateway("join")
            .EndEvent("end")
            .Connect("start", "decision")
            .Connect("decision", "a", condition: "A")
            .Connect("decision", "b", condition: "B")
            .Connect("a", "join")
            .Connect("b", "join")
            .Connect("join", "end")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);
        instance.CompleteWork("decision", "A");
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["a"]);

        instance.CompleteWork("a");

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Fault!.Code.ShouldBe("bpmn.join.deadlock");
        instance.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void TaskWithoutMatchingFlow_AndNoDefault_Faults()
    {
        var definition = new BpmnProcessBuilder("task-no-matching-flow")
            .StartEvent("start")
            .Task("task", bindingRef: "task")
            .EndEvent("end")
            .Connect("start", "task")
            .Connect("task", "end", condition: "Expected")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);

        instance.CompleteWork("task");

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Fault!.Code.ShouldBe("bpmn.flow.none-taken");
        instance.PendingWork.ShouldBeEmpty();
    }

    private static BpmnProcessDefinition ParallelJoin() => new BpmnProcessBuilder("parallel-join")
        .StartEvent("start")
        .ParallelGateway("fork")
        .Task("left", bindingRef: "left")
        .Task("right", bindingRef: "right")
        .ParallelGateway("join")
        .Task("after", bindingRef: "after")
        .EndEvent("end")
        .Connect("start", "fork")
        .Connect("fork", "left")
        .Connect("fork", "right")
        .Connect("left", "join")
        .Connect("right", "join")
        .Connect("join", "after")
        .Connect("after", "end")
        .Build();
}
