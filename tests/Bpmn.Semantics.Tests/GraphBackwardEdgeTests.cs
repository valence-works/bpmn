using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The backward-flow set is the graph's stable loop boundary: only an edge to an ancestor on the current
/// traversal is classified as backward. These tests pin the classification across loop shapes and start order.
/// </summary>
public sealed class GraphBackwardEdgeTests
{
    private static BpmnGraph Build(BpmnProcessDefinition definition) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full);

    [Fact]
    public void A_task_self_loop_classifies_only_its_loop_closing_flow()
    {
        var graph = Build(new BpmnProcessBuilder("self-loop")
            .StartEvent("start")
            .Task("task", bindingRef: "task")
            .EndEvent("end")
            .Connect("start", "task", flowId: "start-task")
            .Connect("task", "task", flowId: "back")
            .Connect("task", "end", flowId: "exit")
            .Build());

        graph.BackwardFlowIds.OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["back"]);
        graph.IsBackwardFlow("back").ShouldBeTrue();
        graph.IsBackwardFlow("start-task").ShouldBeFalse();
        graph.IsBackwardFlow("exit").ShouldBeFalse();
    }

    [Fact]
    public void A_do_while_shape_classifies_the_gateway_loop_back_flow()
    {
        var graph = Build(new BpmnProcessBuilder("do-while")
            .StartEvent("start")
            .Task("body", bindingRef: "body")
            .ExclusiveGateway("check")
            .EndEvent("end")
            .Connect("start", "body", flowId: "start-body")
            .Connect("body", "check", flowId: "body-check")
            .Connect("check", "body", condition: "Again", flowId: "again")
            .Connect("check", "end", isDefault: true, flowId: "exit")
            .Build());

        graph.BackwardFlowIds.OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["again"]);
        graph.IsBackwardFlow("body-check").ShouldBeFalse();
        graph.IsBackwardFlow("exit").ShouldBeFalse();
    }

    [Fact]
    public void Nested_loops_classify_both_loop_closers_and_no_forward_flow()
    {
        var graph = Build(new BpmnProcessBuilder("nested-loops")
            .StartEvent("start")
            .Task("outer", bindingRef: "outer")
            .Task("inner", bindingRef: "inner")
            .Task("leaf", bindingRef: "leaf")
            .EndEvent("end")
            .Connect("start", "outer", flowId: "start-outer")
            .Connect("outer", "inner", flowId: "outer-inner")
            .Connect("inner", "leaf", flowId: "inner-leaf")
            .Connect("leaf", "inner", condition: "RepeatInner", flowId: "inner-back")
            .Connect("leaf", "outer", condition: "RepeatOuter", flowId: "outer-back")
            .Connect("leaf", "end", isDefault: true, flowId: "exit")
            .Build());

        graph.BackwardFlowIds.OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["inner-back", "outer-back"]);
        graph.IsBackwardFlow("inner-leaf").ShouldBeFalse();
        graph.IsBackwardFlow("outer-inner").ShouldBeFalse();
    }

    [Fact]
    public void Multiple_start_event_order_does_not_change_backward_flow_classification()
    {
        var elements = new BpmnElement[]
        {
            new("start-a", BpmnElementTypes.StartEvent),
            new("start-b", BpmnElementTypes.StartEvent),
            new("task-a", BpmnElementTypes.Task, bindingRef: "work-a"),
            new("task-b", BpmnElementTypes.Task, bindingRef: "work-b"),
            new("end-a", BpmnElementTypes.EndEvent),
            new("end-b", BpmnElementTypes.EndEvent)
        };
        var flows = new BpmnSequenceFlow[]
        {
            new("start-a-flow", "start-a", "task-a"),
            new("back-a", "task-a", "task-a", conditionOutcome: "Again"),
            new("exit-a", "task-a", "end-a", isDefault: true),
            new("start-b-flow", "start-b", "task-b"),
            new("back-b", "task-b", "task-b", conditionOutcome: "Again"),
            new("exit-b", "task-b", "end-b", isDefault: true)
        };
        var forward = Build(new BpmnProcessDefinition("multiple-starts", Elements: elements, SequenceFlows: flows));
        var reordered = Build(new BpmnProcessDefinition(
            "multiple-starts",
            Elements: elements.Reverse().ToArray(),
            SequenceFlows: flows.Reverse().ToArray()));

        forward.BackwardFlowIds.OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["back-a", "back-b"]);
        reordered.BackwardFlowIds.OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["back-a", "back-b"]);
    }

    [Fact]
    public void A_parallel_fork_and_join_inside_a_loop_have_one_backward_flow()
    {
        var graph = Build(new BpmnProcessBuilder("parallel-loop")
            .StartEvent("start")
            .Task("top", bindingRef: "top")
            .ParallelGateway("fork")
            .Task("left", bindingRef: "left")
            .Task("right", bindingRef: "right")
            .ParallelGateway("join")
            .ExclusiveGateway("check")
            .EndEvent("end")
            .Connect("start", "top", flowId: "start-top")
            .Connect("top", "fork", flowId: "top-fork")
            .Connect("fork", "left", flowId: "fork-left")
            .Connect("fork", "right", flowId: "fork-right")
            .Connect("left", "join", flowId: "left-join")
            .Connect("right", "join", flowId: "right-join")
            .Connect("join", "check", flowId: "join-check")
            .Connect("check", "top", condition: "Again", flowId: "again")
            .Connect("check", "end", isDefault: true, flowId: "exit")
            .Build());

        graph.BackwardFlowIds.OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["again"]);
        graph.IsBackwardFlow("fork-left").ShouldBeFalse();
        graph.IsBackwardFlow("fork-right").ShouldBeFalse();
        graph.IsBackwardFlow("left-join").ShouldBeFalse();
        graph.IsBackwardFlow("right-join").ShouldBeFalse();
    }

    [Fact]
    public void A_loop_back_into_a_start_event_is_rejected_by_the_start_rule()
    {
        var definition = new BpmnProcessBuilder("loop-to-start")
            .StartEvent("start")
            .Task("work", bindingRef: "work")
            .EndEvent("end")
            .Connect("start", "work")
            .Connect("work", "start", condition: "Again", flowId: "back-to-start")
            .Connect("work", "end", isDefault: true)
            .Build();

        Should.Throw<BpmnExecutionException>(() => Build(definition))
            .Message.ShouldContain("start event 'start' cannot have inbound sequence flows");
    }

    [Fact]
    public void A_loop_back_into_a_boundary_event_is_rejected_by_the_boundary_rule()
    {
        var definition = new BpmnProcessBuilder("loop-to-boundary")
            .StartEvent("start")
            .Task("host", bindingRef: "host")
            .BoundaryEvent("boundary", "host", new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer), bindingRef: "listener")
            .EndEvent("end")
            .Connect("start", "host")
            .Connect("host", "boundary", condition: "Again", flowId: "back-to-boundary")
            .Connect("host", "end", isDefault: true)
            .Connect("boundary", "host", flowId: "boundary-host")
            .Build();

        Should.Throw<BpmnExecutionException>(() => Build(definition))
            .Message.ShouldContain("cannot have inbound sequence flows");
    }

    [Fact]
    public void A_loop_back_into_an_event_gateway_catch_is_rejected_by_its_single_inbound_rule()
    {
        var definition = new BpmnProcessBuilder("loop-to-race-catch")
            .StartEvent("start")
            .EventBasedGateway("race")
            .IntermediateCatchEvent("catch-a", new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer), bindingRef: "timer-a")
            .IntermediateCatchEvent("catch-b", new BpmnEventDefinition(BpmnEventDefinitionTypes.Message), bindingRef: "message-b")
            .Task("work", bindingRef: "work")
            .EndEvent("end")
            .Connect("start", "race")
            .Connect("race", "catch-a")
            .Connect("race", "catch-b")
            .Connect("catch-a", "work")
            .Connect("work", "catch-a", condition: "Again", flowId: "back-to-catch")
            .Connect("catch-b", "end")
            .Build();

        Should.Throw<BpmnExecutionException>(() => Build(definition))
            .Message.ShouldContain("must have exactly one inbound flow");
    }
}
