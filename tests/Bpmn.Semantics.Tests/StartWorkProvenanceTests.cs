using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The producer reported on each work-start command. Ordinary propagation carries the scope identity or the
/// callback handle that caused it; starts owned by BPMN scopes carry no producer handle.
/// </summary>
public sealed class StartWorkProvenanceTests
{
    private const string ScopeInstanceId = "scope-1";
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    private static BpmnGraph BuildGraph(BpmnProcessDefinition definition, Func<string, BpmnProcessDefinition?>? nestedProcess = null) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition, nestedProcess), BpmnHostCapabilities.Full);

    private static BpmnHostSnapshot Scope(params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root(ScopeInstanceId) with { LiveWork = liveWork };

    private static BpmnEvaluation Start(BpmnGraph graph, BpmnHostSnapshot? host = null) =>
        Interpreter.Start(new BpmnStartRequest(graph, null, host ?? Scope()));

    private static BpmnEvaluation Complete(
        BpmnGraph graph,
        BpmnEvaluation prior,
        string bindingRef,
        string handle,
        BpmnHostSnapshot host,
        string? iterationId = null) =>
        Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(graph, prior.State, host, bindingRef, handle, [], iterationId));

    private static BpmnHostCommand.StartWork[] StartedWork(BpmnEvaluation evaluation) =>
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().ToArray();

    [Fact]
    public void Ordinary_completion_carries_its_handle_through_a_chain_and_fan_out()
    {
        var definition = new BpmnProcessBuilder("start-work-provenance")
            .StartEvent("Start")
            .Task("First", bindingRef: "first")
            .Task("Second", bindingRef: "second")
            .ParallelGateway("Split")
            .Task("Left", bindingRef: "left")
            .EndEvent("LeftEnd")
            .Task("Right", bindingRef: "right")
            .EndEvent("RightEnd")
            .ConnectSequence("Start", "First", "Second", "Split")
            .ConnectSequence("Split", "Left", "LeftEnd")
            .ConnectSequence("Split", "Right", "RightEnd")
            .Build();
        var graph = BuildGraph(definition);

        var startedEvaluation = Start(graph);
        var started = StartedWork(startedEvaluation).ShouldHaveSingleItem();
        started.BindingRef.ShouldBe("first");
        started.ProducedByHandle.ShouldBe(ScopeInstanceId);

        var afterFirst = Complete(graph, startedEvaluation, "first", "h-first", Scope(new BpmnLiveWork("first", null, "h-first")));
        var second = StartedWork(afterFirst).ShouldHaveSingleItem();
        second.BindingRef.ShouldBe("second");
        second.ProducedByHandle.ShouldBe("h-first");

        var afterSecond = Complete(graph, afterFirst, "second", "h-second", Scope(new BpmnLiveWork("second", null, "h-second")));
        var branches = StartedWork(afterSecond);
        branches.Select(work => work.BindingRef).ShouldBe(["left", "right"]);
        branches.ShouldAllBe(work => work.ProducedByHandle == "h-second");
    }

    [Fact]
    public void Escalation_boundary_routing_uses_scope_identity_not_the_signalling_handle()
    {
        const string escalationCode = "overdue";
        var definition = new BpmnProcessBuilder("start-work-provenance-signal")
            .StartEvent("Start")
            .Task("Work", bindingRef: "work")
            .EndEvent("End")
            .BoundaryEvent("Escalated", "Work", ProcessFixtures.Escalation(escalationCode), interrupting: true)
            .Task("Handle", bindingRef: "handle")
            .EndEvent("EndEscalated")
            .ConnectSequence("Start", "Work", "End")
            .ConnectSequence("Escalated", "Handle", "EndEscalated")
            .Build();
        var graph = BuildGraph(definition);
        var started = Start(graph);
        const string signallingHandle = "h-signaller";

        var signalled = Interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            graph,
            started.State,
            Scope(new BpmnLiveWork("work", null, signallingHandle)),
            "work",
            signallingHandle,
            BpmnInterpreter.EscalationSignalCode,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = escalationCode
            })));

        var downstream = StartedWork(signalled).ShouldHaveSingleItem();
        downstream.BindingRef.ShouldBe("handle");
        downstream.ProducedByHandle.ShouldBe(ScopeInstanceId);
        downstream.ProducedByHandle.ShouldNotBe(signallingHandle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Multi_instance_work_has_no_producer_handle(bool sequential)
    {
        var graph = BuildGraph(ProcessFixtures.MultiInstanceByCardinality(2, sequential));

        var started = Start(graph);
        var instances = StartedWork(started).Where(work => work.BindingRef == "review").ToArray();
        instances.ShouldNotBeEmpty();
        instances.ShouldAllBe(work => work.ProducedByHandle == null);

        if (!sequential)
            return;

        var first = instances.ShouldHaveSingleItem();
        var iterationId = first.IterationScope!.IterationId;
        var handle = "h-review-0";
        var next = Complete(
            graph,
            started,
            "review",
            handle,
            Scope(new BpmnLiveWork("review", iterationId, handle)),
            iterationId);

        var nextInstance = StartedWork(next).ShouldHaveSingleItem();
        nextInstance.BindingRef.ShouldBe("review");
        nextInstance.ProducedByHandle.ShouldBeNull();
    }

    [Fact]
    public void Boundary_and_scope_listener_starts_have_no_producer_handle()
    {
        var fixture = ProcessFixtures.TerminateBesideLiveWork();
        var graph = BuildGraph(fixture.Parent, bindingRef => fixture.Nested.GetValueOrDefault(bindingRef));

        var evaluation = Start(graph);
        var starts = StartedWork(evaluation);

        starts.Single(work => work.BindingRef == "long").ProducedByHandle.ShouldBe(ScopeInstanceId);
        starts.Single(work => work.BindingRef == "timeout").ProducedByHandle.ShouldBeNull();
        starts.Where(work => work.BindingRef == "review").ShouldAllBe(work => work.ProducedByHandle == null);
        starts.Single(work => work.BindingRef == "on-nudge-listener").ProducedByHandle.ShouldBeNull();
    }

    [Fact]
    public void Error_boundary_recovery_carries_the_faulted_work_handle()
    {
        var graph = BuildGraph(ProcessFixtures.RiskyActivity(withErrorBoundary: true));
        var started = Start(graph);
        var faultedHandle = "h-risky";

        var evaluation = Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            graph,
            started.State,
            Scope(new BpmnLiveWork("risky", null, faultedHandle)),
            "risky",
            faultedHandle,
            "boom"));

        evaluation.Disposition.ShouldBe(new BpmnErrorDisposition.Caught("Failed", ErrorCode: null));
        var recovery = StartedWork(evaluation).ShouldHaveSingleItem();
        recovery.BindingRef.ShouldBe("recover");
        recovery.ProducedByHandle.ShouldBe(faultedHandle);
    }

    [Fact]
    public void A_compensation_handler_start_has_no_producer_handle()
    {
        var (_, body) = ProcessFixtures.CancellableTransactionWithNothingElseLive();
        var graph = BuildGraph(body);
        var nestedScope = Scope() with { HasEnclosingScope = true };
        var started = Start(graph, nestedScope);

        var afterReserve = Complete(
            graph,
            started,
            "reserve",
            "h-reserve",
            Scope(new BpmnLiveWork("reserve", null, "h-reserve")) with { HasEnclosingScope = true });
        var trigger = StartedWork(afterReserve).ShouldHaveSingleItem();
        trigger.BindingRef.ShouldBe("trigger");
        trigger.ProducedByHandle.ShouldBe("h-reserve");

        var afterTrigger = Complete(
            graph,
            afterReserve,
            "trigger",
            "h-trigger",
            Scope(new BpmnLiveWork("trigger", null, "h-trigger")) with { HasEnclosingScope = true });

        var compensation = StartedWork(afterTrigger).ShouldHaveSingleItem();
        compensation.BindingRef.ShouldBe("undo-reserve");
        compensation.ProducedByHandle.ShouldBeNull();
    }

    [Fact]
    public void An_event_subprocess_body_start_has_no_producer_handle()
    {
        var (parent, body) = ProcessFixtures.OwnScopeEscalationInterruptsASecondBranch();
        var graph = BuildGraph(parent, _ => body);
        var started = Start(graph);
        var beforeEscalation = StartedWork(started);
        var trigger = beforeEscalation.Single(work => work.BindingRef == "escalate-trigger");
        var longRunning = beforeEscalation.Single(work => work.BindingRef == "long-running");

        var activated = Complete(
            graph,
            started,
            trigger.BindingRef,
            "h-escalate-trigger",
            Scope(new BpmnLiveWork(longRunning.BindingRef, null, "h-long-running")));

        var bodyStart = StartedWork(activated).ShouldHaveSingleItem();
        bodyStart.BindingRef.ShouldBe("on-overdue");
        bodyStart.StartElementHint.ShouldBe("HandlerStart");
        bodyStart.ProducedByHandle.ShouldBeNull();
    }

    [Fact]
    public void The_seven_argument_constructor_and_deconstruction_remain_compatible()
    {
        var correlation = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "token-1" };
        var legacy = new BpmnHostCommand.StartWork("work", "Work", "token-1", "ordinary", correlation, null, null);
        legacy.ProducedByHandle.ShouldBeNull();
        var command = legacy with { ProducedByHandle = "h-producer" };

        var (bindingRef, elementId, tokenId, schedulingCause, deconstructedCorrelation, iterationScope, startElementHint) = command;

        bindingRef.ShouldBe("work");
        elementId.ShouldBe("Work");
        tokenId.ShouldBe("token-1");
        schedulingCause.ShouldBe("ordinary");
        deconstructedCorrelation.ShouldBe(correlation);
        iterationScope.ShouldBeNull();
        startElementHint.ShouldBeNull();
        command.ProducedByHandle.ShouldBe("h-producer");
    }

    [Fact]
    public void Produced_by_handle_round_trips_in_json()
    {
        var command = new BpmnHostCommand.StartWork(
            "work", "Work", "token-1", "ordinary", new Dictionary<string, string>())
        {
            ProducedByHandle = "h-producer"
        };

        var json = JsonSerializer.Serialize(command);
        var restored = JsonSerializer.Deserialize<BpmnHostCommand.StartWork>(json);

        restored.ShouldNotBeNull();
        restored!.ProducedByHandle.ShouldBe("h-producer");
    }

    [Fact]
    public void Legacy_start_work_json_without_produced_by_handle_deserializes_with_null_producer()
    {
        const string json = """
            {
              "BindingRef": "work",
              "ElementId": "Work",
              "TokenId": "token-1",
              "SchedulingCause": "ordinary",
              "Correlation": { "token": "token-1" },
              "IterationScope": null,
              "StartElementHint": null
            }
            """;

        var restored = JsonSerializer.Deserialize<BpmnHostCommand.StartWork>(json);

        restored.ShouldNotBeNull();
        var command = restored!;
        command.BindingRef.ShouldBe("work");
        command.ElementId.ShouldBe("Work");
        command.TokenId.ShouldBe("token-1");
        command.SchedulingCause.ShouldBe("ordinary");
        command.Correlation.Count.ShouldBe(1);
        command.Correlation["token"].ShouldBe("token-1");
        command.IterationScope.ShouldBeNull();
        command.StartElementHint.ShouldBeNull();
        command.ProducedByHandle.ShouldBeNull();
    }
}
