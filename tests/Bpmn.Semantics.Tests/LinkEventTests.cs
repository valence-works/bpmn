using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Link events: a link throw jumps its token to the link catch of the same name in its scope, within the same
/// evaluation, and graph build refuses a link that cannot be followed.
/// </summary>
public sealed class LinkEventTests
{
    private static BpmnEventDefinition Link(string name) =>
        new(BpmnEventDefinitionTypes.Link, new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = name });

    private static BpmnGraph Build(BpmnProcessDefinition definition) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full);

    private static IReadOnlyList<string> PendingElements(InMemoryProcessInstance instance) =>
        instance.PendingWork.Select(work => work.ElementId).ToArray();

    /// <summary>Start, a task, then a link throw to a catch that leads to a second task and the end.</summary>
    private static BpmnProcessDefinition JumpOverTheGap() =>
        new BpmnProcessBuilder("jump")
            .StartEvent("Start")
            .Task("First", bindingRef: "first")
            .IntermediateThrowEvent("GoOn", Link("continue"))
            .IntermediateCatchEvent("ContinueHere", Link("continue"))
            .Task("Second", bindingRef: "second")
            .EndEvent("End")
            .ConnectSequence("Start", "First", "GoOn")
            .ConnectSequence("ContinueHere", "Second", "End")
            .Build();

    [Fact]
    public void A_link_throw_jumps_to_the_catch_of_its_name_within_the_same_evaluation()
    {
        var instance = new InMemoryBpmnHost().Start(JumpOverTheGap());

        instance.CompleteWork("first");

        PendingElements(instance).ShouldBe(["Second"], "the jump and the catch's routing happen in the evaluation that completed First");
        instance.Transcript.Last().TokenMoves.Select(move => move.ElementId).ShouldContain("ContinueHere");
        instance.CompleteWork("second");
        instance.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void The_graph_joins_a_throw_to_its_catch_with_a_link_flow_the_definition_does_not_declare()
    {
        var graph = Build(JumpOverTheGap());

        var linkFlow = graph.OutboundFlows("GoOn").ShouldHaveSingleItem();
        linkFlow.FlowId.ShouldBe("link:GoOn");
        linkFlow.TargetRef.ShouldBe("ContinueHere");
        graph.IsLinkFlow(linkFlow.FlowId).ShouldBeTrue();
        graph.SequenceFlows.ShouldNotContain(flow => flow.FlowId == "link:GoOn");
    }

    [Fact]
    public void A_link_back_to_an_earlier_catch_is_a_loop_each_pass_of_which_is_its_own_iteration()
    {
        // Start jumps in through one throw; the task loops back through another throw of the same name.
        var definition = new BpmnProcessBuilder("link-loop")
            .StartEvent("Start")
            .IntermediateThrowEvent("Enter", Link("again"))
            .IntermediateCatchEvent("Again", Link("again"))
            .Task("Work", bindingRef: "work")
            .IntermediateThrowEvent("Repeat", Link("again"))
            .EndEvent("End")
            .Connect("Start", "Enter")
            .Connect("Again", "Work")
            .Connect("Work", "Repeat", condition: "again")
            .Connect("Work", "End", condition: "done")
            .Build();

        var graph = Build(definition);
        graph.BackwardFlowIds.ShouldContain("link:Repeat", "the jump back closes the loop, so it mints each pass's iteration key");

        var instance = new InMemoryBpmnHost().Start(definition);
        var firstPass = instance.PendingWork.ShouldHaveSingleItem();
        instance.CompleteWork("work", "again");

        var secondPass = instance.PendingWork.ShouldHaveSingleItem();
        secondPass.ElementId.ShouldBe("Work");
        secondPass.Handle.ShouldNotBe(firstPass.Handle);
        instance.CompleteWork("work", "done");
        instance.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void A_link_inside_a_subprocess_jumps_within_the_subprocess()
    {
        var body = new BpmnProcessBuilder("body")
            .StartEvent("BodyStart")
            .IntermediateThrowEvent("BodyJump", Link("inner"))
            .IntermediateCatchEvent("BodyLanding", Link("inner"))
            .Task("Inner", bindingRef: "inner")
            .EndEvent("BodyEnd")
            .Connect("BodyStart", "BodyJump")
            .ConnectSequence("BodyLanding", "Inner", "BodyEnd")
            .Build();
        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("End")
            .ConnectSequence("Start", "Sub", "End")
            .Build();
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions { NestedProcesses = new Dictionary<string, BpmnProcessDefinition> { ["sub"] = body } });

        var instance = host.Start(parent);
        var child = instance.Children.ShouldHaveSingleItem();

        PendingElements(child).ShouldBe(["Inner"]);
        child.CompleteWork("inner");
        instance.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void A_throw_whose_catch_is_in_another_scope_is_refused_because_a_link_does_not_leave_its_scope()
    {
        // The parent declares the catch; the body that throws to it does not.
        var body = new BpmnProcessBuilder("body")
            .StartEvent("BodyStart")
            .IntermediateThrowEvent("Escape", Link("outside"))
            .Connect("BodyStart", "Escape")
            .Build();

        var refusal = Should.Throw<BpmnExecutionException>(() => Build(body));

        refusal.Message.ShouldContain("no link catch event in its scope has that name");
        refusal.ElementIds.ShouldBe(["Escape"]);
    }

    [Fact]
    public void A_cycle_made_only_of_links_is_refused_as_a_cycle_that_can_never_wait()
    {
        var definition = new BpmnProcessBuilder("spin")
            .StartEvent("Start")
            .EndEvent("End")
            .IntermediateCatchEvent("Land", Link("spin"))
            .IntermediateThrowEvent("Spin", Link("spin"))
            .Connect("Start", "End")
            .Connect("Land", "Spin")
            .Build();

        var refusal = Should.Throw<BpmnExecutionException>(() => Build(definition));

        refusal.Message.ShouldContain("has a cycle that can never wait");
        refusal.ElementIds.ShouldContain("Land");
        refusal.ElementIds.ShouldContain("Spin");
    }

    public static TheoryData<string> Refusals() => [.. RefusedShapes.Keys];

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_link_that_cannot_be_followed_is_refused_at_graph_build_naming_its_elements(string shape)
    {
        var (definition, message, elementIds) = RefusedShapes[shape]();

        var refusal = Should.Throw<BpmnExecutionException>(() => Build(definition));

        refusal.Message.ShouldContain(message);
        refusal.ElementIds.ShouldBe(elementIds);
    }

    private static readonly Dictionary<string, Func<(BpmnProcessDefinition Definition, string Message, string[] ElementIds)>> RefusedShapes = new()
    {
        ["two catches with one name"] = () => (
            new BpmnProcessBuilder("p")
                .StartEvent("Start")
                .IntermediateThrowEvent("Go", Link("x"))
                .IntermediateCatchEvent("CatchA", Link("x"))
                .IntermediateCatchEvent("CatchB", Link("x"))
                .EndEvent("EndA")
                .EndEvent("EndB")
                .Connect("Start", "Go")
                .Connect("CatchA", "EndA")
                .Connect("CatchB", "EndB")
                .Build(),
            "share the link name 'x'",
            ["CatchA", "CatchB"]),
        ["a throw with no catch"] = () => (
            new BpmnProcessBuilder("p")
                .StartEvent("Start")
                .IntermediateThrowEvent("Go", Link("nowhere"))
                .Connect("Start", "Go")
                .Build(),
            "no link catch event in its scope has that name",
            ["Go"]),
        ["a flow into a catch"] = () => (
            new BpmnProcessBuilder("p")
                .StartEvent("Start")
                .IntermediateThrowEvent("Go", Link("x"))
                .IntermediateCatchEvent("Land", Link("x"))
                .EndEvent("End")
                .Connect("Start", "Go")
                .Connect("Start", "Land", flowId: "intoLand")
                .Connect("Land", "End")
                .Build(),
            "a link catch is reached only by a link throw",
            ["Land", "intoLand"]),
        ["a flow out of a throw"] = () => (
            new BpmnProcessBuilder("p")
                .StartEvent("Start")
                .IntermediateThrowEvent("Go", Link("x"))
                .IntermediateCatchEvent("Land", Link("x"))
                .EndEvent("End")
                .Connect("Start", "Go")
                .Connect("Go", "End", flowId: "outOfGo")
                .Connect("Land", "End")
                .Build(),
            "a link throw continues only at its catch",
            ["Go", "outOfGo"]),
        ["an unnamed link"] = () => (
            new BpmnProcessBuilder("p")
                .StartEvent("Start")
                .IntermediateThrowEvent("Go", Link("  "))
                .Connect("Start", "Go")
                .Build(),
            "must declare a non-empty link name",
            ["Go"]),
        ["a link that binds work"] = () => (
            new BpmnProcessBuilder("p")
                .StartEvent("Start")
                .IntermediateThrowEvent("Go", Link("x"), bindingRef: "go")
                .IntermediateCatchEvent("Land", Link("x"))
                .EndEvent("End")
                .Connect("Start", "Go")
                .Connect("Land", "End")
                .Build(),
            "cannot bind work",
            ["Go"]),
        ["a declared flow with a link flow's id"] = () => (
            new BpmnProcessBuilder("p")
                .StartEvent("Start")
                .IntermediateThrowEvent("Go", Link("x"))
                .IntermediateCatchEvent("Land", Link("x"))
                .EndEvent("End")
                .Connect("Start", "Go", flowId: "link:Go")
                .Connect("Land", "End")
                .Build(),
            "is the id of link throw event 'Go''s link flow",
            ["link:Go", "Go"])
    };
}
