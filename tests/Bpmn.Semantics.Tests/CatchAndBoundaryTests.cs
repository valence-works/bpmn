using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>Catch-event waits route their parked tokens; boundary listeners are scoped to their host work.</summary>
public sealed class CatchAndBoundaryTests
{
    [Fact]
    public void A_timer_catch_waits_until_due_then_routes_to_the_next_task()
    {
        var host = new InMemoryBpmnHost();
        var instance = host.Start(new BpmnProcessBuilder("timer-catch")
            .StartEvent("Start")
            .IntermediateCatchEvent("Wait", Timer("PT5M"), bindingRef: "wait")
            .Task("After", bindingRef: "after")
            .EndEvent("End")
            .ConnectSequence("Start", "Wait", "After", "End")
            .Build());

        instance.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("wait");
        instance.ResolveWork("wait").Kind.ShouldBe(InMemoryWorkKind.Timer);
        instance.PendingWork.ShouldNotContain(work => work.BindingRef == "after");
        host.Clock.Pending.ShouldHaveSingleItem().DueAt.ShouldBe(TimeSpan.FromMinutes(5));

        host.Clock.Advance(TimeSpan.FromMinutes(5));

        instance.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("after");
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Theory]
    [InlineData(BpmnEventDefinitionTypes.Message, "shipment-ready")]
    [InlineData(BpmnEventDefinitionTypes.Signal, "shipment-ready")]
    public void A_message_or_signal_catch_routes_after_its_bound_wait_completes(string triggerType, string name)
    {
        var instance = new InMemoryBpmnHost().Start(new BpmnProcessBuilder("external-catch")
            .StartEvent("Start")
            .IntermediateCatchEvent("Wait", NamedTrigger(triggerType, name), bindingRef: "wait")
            .Task("After", bindingRef: "after")
            .EndEvent("End")
            .ConnectSequence("Start", "Wait", "After", "End")
            .Build());

        instance.PendingWork.ShouldHaveSingleItem().ElementId.ShouldBe("Wait");
        instance.PendingWork.ShouldNotContain(work => work.BindingRef == "after");

        instance.CompleteWork("wait");

        instance.PendingWork.ShouldHaveSingleItem().ElementId.ShouldBe("After");
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void Parallel_catch_events_resume_independently_before_the_join_completes()
    {
        var definition = new BpmnProcessBuilder("parallel-catches")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .IntermediateCatchEvent("CatchA", NamedTrigger(BpmnEventDefinitionTypes.Message, "a"), bindingRef: "catch-a")
            .IntermediateCatchEvent("CatchB", NamedTrigger(BpmnEventDefinitionTypes.Signal, "b"), bindingRef: "catch-b")
            .ParallelGateway("Join")
            .EndEvent("End")
            .Connect("Start", "Split")
            .Connect("Split", "CatchA")
            .Connect("Split", "CatchB")
            .Connect("CatchA", "Join")
            .Connect("CatchB", "Join")
            .Connect("Join", "End")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["catch-a", "catch-b"]);
        instance.CompleteWork("catch-a");

        instance.IsCompleted.ShouldBeFalse("one catch arrival cannot satisfy both inbound join flows");
        instance.State.Tokens.ShouldContain(token => token.AtElementId == "Join" && token.Status == BpmnTokenStatus.WaitingAtJoin);
        instance.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("catch-b");

        instance.CompleteWork("catch-b");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void An_error_boundary_cancels_its_sibling_timer_listener_when_it_absorbs_a_host_fault()
    {
        var definition = new BpmnProcessBuilder("error-with-timer-boundary")
            .StartEvent("Start")
            .Task("Host", bindingRef: "host")
            .BoundaryEvent("Error", "Host", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .BoundaryEvent("Timeout", "Host", Timer("PT5M"), bindingRef: "timeout")
            .EndEvent("EndHost")
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndError")
            .ConnectSequence("Start", "Host", "EndHost")
            .ConnectSequence("Error", "Recover", "EndError")
            .Connect("Timeout", "EndHost")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["host", "timeout"]);

        instance.FaultWork("host", "host operation failed");

        instance.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("recover");
        instance.CancelledWork.ShouldContain(work => work.ElementId == "Timeout"
            && work.Reason == BpmnInterpreter.BoundaryHostInterruptedReason);
        instance.IsFaulted.ShouldBeFalse("the error boundary absorbs the host failure");
        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.Canceled
            && diagnostic.ElementId == "Timeout");
    }

    [Fact]
    public void Non_interrupting_message_boundary_runs_beside_the_host_and_host_completion_retires_timer()
    {
        var host = new InMemoryBpmnHost();
        var definition = new BpmnProcessBuilder("multiple-boundaries")
            .StartEvent("Start")
            .Task("Host", bindingRef: "host")
            .BoundaryEvent("Timeout", "Host", Timer("PT5M"), bindingRef: "timeout")
            .BoundaryEvent("Ping", "Host", NamedTrigger(BpmnEventDefinitionTypes.Message, "ping"), interrupting: false, bindingRef: "ping")
            .BoundaryEvent("Error", "Host", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .EndEvent("EndHost")
            .Task("Notify", bindingRef: "notify")
            .EndEvent("EndPing")
            .ConnectSequence("Start", "Host", "EndHost")
            .Connect("Timeout", "EndHost")
            .ConnectSequence("Ping", "Notify", "EndPing")
            .Connect("Error", "EndPing")
            .Build();
        var instance = host.Start(definition);

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["host", "timeout", "ping"]);
        instance.CompleteWork("ping");

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["host", "timeout", "notify"]);
        instance.CancelledWork.ShouldBeEmpty();
        instance.CompleteWork("notify");
        instance.IsCompleted.ShouldBeFalse("the host still owns the normal path");

        instance.CompleteWork("host");

        instance.CancelledWork.ShouldContain(work => work.ElementId == "Timeout"
            && work.Reason == BpmnInterpreter.BoundarySupersededByHostCompletionReason);
        host.Clock.Pending.ShouldBeEmpty();
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    private static BpmnEventDefinition Timer(string interval) =>
        new(BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Interval] = interval });

    private static BpmnEventDefinition NamedTrigger(string type, string name) =>
        new(type, new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Name] = name });
}
