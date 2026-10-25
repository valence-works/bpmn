using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;
using System.Text.Json;

namespace Bpmn.Semantics.Tests;

/// <summary>Escalations travel to the enclosing scope, select the most specific catcher, and never become faults themselves.</summary>
public sealed class EscalationRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_matching_escalation_boundary_preserves_or_interrupts_its_nested_host(bool interrupting)
    {
        var body = ThrowingChild("raise");
        var host = HostWithNested(("sub", body));
        var instance = host.Start(BoundaryParent("raise", interrupting));
        var child = instance.Children.ShouldHaveSingleItem();

        child.CompleteWork("trigger");

        instance.PendingWork.Select(work => work.BindingRef).ShouldContain("recover");
        if (interrupting)
        {
            child.IsCancelled.ShouldBeTrue();
            child.PendingWork.ShouldBeEmpty();
            instance.CancelledWork.ShouldContain(work => work.ElementId == "Sub"
                && work.Reason == BpmnInterpreter.EscalationHostInterruptedReason);
            instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["recover"]);
            instance.CompleteWork("recover");
        }
        else
        {
            child.IsCancelled.ShouldBeFalse();
            child.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("hold");
            instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["sub", "recover"]);
            instance.CompleteWork("recover");
            instance.IsCompleted.ShouldBeFalse("the nested host's normal path is still live");
            child.CompleteWork("hold");
        }

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.IsFaulted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("specific", "recover")]
    [InlineData("other", "catch-all")]
    public void An_exact_escalation_boundary_wins_before_its_catch_all_fallback(string thrownCode, string expectedBinding)
    {
        var body = ThrowingChild(thrownCode);
        var parent = BoundaryParent("specific", interrupting: false, includeCatchAll: true);
        var instance = HostWithNested(("sub", body)).Start(parent);

        instance.Children.ShouldHaveSingleItem().CompleteWork("trigger");

        instance.PendingWork.ShouldContain(work => work.BindingRef == expectedBinding);
        instance.PendingWork.Count(work => work.BindingRef is "recover" or "catch-all").ShouldBe(1);
        instance.IsFaulted.ShouldBeFalse();
        var boundaryWork = instance.PendingWork.Single(work => work.BindingRef == expectedBinding);
        instance.CompleteWork(boundaryWork);
        instance.Children.ShouldHaveSingleItem().CompleteWork("hold");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void An_unmatched_child_escalation_is_unhandled_at_the_root_and_leaves_its_host_running()
    {
        var childBody = ThrowingChild("other");
        var instance = HostWithNested(("sub", childBody)).Start(BoundaryParent("specific", interrupting: true));

        instance.Children.ShouldHaveSingleItem().CompleteWork("trigger");

        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EscalationUnhandled);
        instance.Children.ShouldHaveSingleItem().IsCancelled.ShouldBeFalse();
        instance.PendingWork.ShouldContain(work => work.BindingRef == "sub");
        instance.PendingWork.ShouldNotContain(work => work.BindingRef == "recover");
        instance.IsFaulted.ShouldBeFalse();
    }

    [Fact]
    public void A_root_escalation_throw_is_a_diagnostic_no_op_then_normal_flow_continues()
    {
        var definition = new BpmnProcessBuilder("root-escalation")
            .StartEvent("Start")
            .IntermediateThrowEvent("Raise", Escalation("raise"))
            .Task("After", bindingRef: "after")
            .EndEvent("End")
            .ConnectSequence("Start", "Raise", "After", "End")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);

        instance.IsFaulted.ShouldBeFalse();
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EscalationUnhandled
            && diagnostic.ElementId == "Raise");
        instance.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("after");
        instance.CompleteWork("after");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void An_escalation_bubbles_through_an_intermediate_scope_to_the_first_matching_ancestor()
    {
        var innermost = ThrowingChild("raise");
        var middle = new BpmnProcessBuilder("middle")
            .StartEvent("Start")
            .SubProcess("Inner", bindingRef: "inner")
            .EndEvent("End")
            .ConnectSequence("Start", "Inner", "End")
            .Build();
        var root = BoundaryParent("raise", interrupting: false);
        var host = HostWithNested(("sub", middle), ("inner", innermost));
        var instance = host.Start(root);
        var middleInstance = instance.Children.ShouldHaveSingleItem();
        var innerInstance = middleInstance.Children.ShouldHaveSingleItem();

        innerInstance.CompleteWork("trigger");

        instance.PendingWork.ShouldContain(work => work.BindingRef == "recover");
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EscalationCaught
            && diagnostic.ElementId == "Specific");
        instance.CompleteWork("recover");
        innerInstance.CompleteWork("hold");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void An_escalation_end_event_notifies_the_attached_boundary()
    {
        var body = new BpmnProcessBuilder("end-escalation-body")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .EndEvent("Raise", null, Escalation("raise"))
            .Task("Hold", bindingRef: "hold")
            .EndEvent("End")
            .Connect("Start", "Split")
            .Connect("Split", "Raise")
            .Connect("Split", "Hold")
            .Connect("Hold", "End")
            .Build();
        var instance = HostWithNested(("sub", body)).Start(BoundaryParent("raise", interrupting: false));

        instance.PendingWork.ShouldContain(work => work.BindingRef == "recover");
        instance.Children.ShouldHaveSingleItem().PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("hold");
        instance.CompleteWork("recover");
        instance.Children[0].CompleteWork("hold");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void An_exact_event_subprocess_catcher_beats_a_boundary_catch_all()
    {
        var childBody = ThrowingChild("raise");
        var handler = EscalationHandler("raise", interrupting: true);
        var parent = new BpmnProcessBuilder("specific-event-subprocess")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("EndSub")
            .BoundaryEvent("CatchAll", "Sub", Escalation(null), interrupting: false)
            .Task("BoundaryWork", bindingRef: "boundary-work")
            .EndEvent("EndBoundary")
            .SubProcess("Handler", bindingRef: "handler-body", triggeredByEvent: true)
            .ConnectSequence("Start", "Sub", "EndSub")
            .ConnectSequence("CatchAll", "BoundaryWork", "EndBoundary")
            .Build();
        var host = HostWithNested(("sub", childBody), ("handler-body", handler));
        var instance = host.Start(parent);

        instance.Children.Single(child => child.Graph.Definition.ProcessId == "throwing-child").CompleteWork("trigger");

        instance.PendingWork.ShouldNotContain(work => work.BindingRef == "boundary-work");
        instance.Children.ShouldContain(child => child.Graph.Definition.ProcessId == "escalation-handler");
        instance.CancelledWork.ShouldContain(work => work.ElementId == "Sub"
            && work.Reason == BpmnInterpreter.EventSubprocessScopeInterruptedReason);
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EventSubprocessActivated
            && diagnostic.ElementId == "Handler");
        instance.Children.Single(child => child.Graph.Definition.ProcessId == "escalation-handler").CompleteWork("handle");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void An_exact_boundary_catcher_beats_an_event_subprocess_catch_all()
    {
        var childBody = ThrowingChild("raise");
        var handler = EscalationHandler(null, interrupting: false);
        var parent = new BpmnProcessBuilder("specific-boundary")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("EndSub")
            .BoundaryEvent("Specific", "Sub", Escalation("raise"), interrupting: false)
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndSpecific")
            .SubProcess("Handler", bindingRef: "handler-body", triggeredByEvent: true)
            .ConnectSequence("Start", "Sub", "EndSub")
            .ConnectSequence("Specific", "Recover", "EndSpecific")
            .Build();
        var instance = HostWithNested(("sub", childBody), ("handler-body", handler)).Start(parent);
        var child = instance.Children.ShouldHaveSingleItem();

        child.CompleteWork("trigger");

        instance.PendingWork.ShouldContain(work => work.BindingRef == "recover");
        instance.Children.Count.ShouldBe(1, "the event-subprocess catch-all did not activate");
        instance.Children.ShouldContain(scope => scope.Graph.Definition.ProcessId == "throwing-child" && !scope.IsCancelled);
        instance.State.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EventSubprocessActivated);
        instance.CompleteWork("recover");
        child.CompleteWork("hold");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void An_unmatched_escalation_event_subprocess_does_not_activate_and_the_child_can_finish()
    {
        var childBody = ThrowingChild("other");
        var handler = EscalationHandler("specific", interrupting: false);
        var parent = new BpmnProcessBuilder("unmatched-event-subprocess")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("EndSub")
            .SubProcess("Handler", bindingRef: "handler-body", triggeredByEvent: true)
            .ConnectSequence("Start", "Sub", "EndSub")
            .Build();
        var instance = HostWithNested(("sub", childBody), ("handler-body", handler)).Start(parent);
        var child = instance.Children.ShouldHaveSingleItem();

        child.CompleteWork("trigger");

        instance.Children.Count.ShouldBe(1);
        instance.Children.ShouldContain(scope => scope.Graph.Definition.ProcessId == "throwing-child" && !scope.IsCancelled);
        instance.PendingWork.ShouldContain(work => work.BindingRef == "sub");
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EscalationUnhandled);
        child.CompleteWork("hold");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void A_non_interrupting_event_subprocess_runs_its_body_for_each_nested_escalation()
    {
        var childBody = new BpmnProcessBuilder("repeated-child-escalation")
            .StartEvent("Start")
            .IntermediateThrowEvent("RaiseFirst", Escalation("raise"))
            .IntermediateThrowEvent("RaiseSecond", Escalation("raise"))
            .Task("Hold", bindingRef: "hold")
            .EndEvent("End")
            .ConnectSequence("Start", "RaiseFirst", "RaiseSecond", "Hold", "End")
            .Build();
        var handler = EscalationHandler("raise", interrupting: false);
        var parent = new BpmnProcessBuilder("repeated-nested-escalation")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("EndSub")
            .SubProcess("Handler", bindingRef: "handler-body", triggeredByEvent: true)
            .ConnectSequence("Start", "Sub", "EndSub")
            .Build();
        var instance = HostWithNested(("sub", childBody), ("handler-body", handler)).Start(parent);
        var child = instance.Children.Single(scope => scope.Graph.Definition.ProcessId == "repeated-child-escalation");
        var handlers = instance.Children.Where(scope => scope.Graph.Definition.ProcessId == "escalation-handler").ToArray();

        handlers.Length.ShouldBe(2);
        handlers.Select(scope => scope.PendingWork.ShouldHaveSingleItem().BindingRef)
            .ShouldAllBe(bindingRef => bindingRef == "handle");
        child.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("hold");
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EventSubprocessActivated
            && diagnostic.ElementId == "Handler").ShouldBe(2);

        handlers[0].CompleteWork("handle");
        handlers[1].CompleteWork("handle");
        child.CompleteWork("hold");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void A_non_interrupting_own_scope_event_subprocess_activates_for_each_escalation()
    {
        var handler = EscalationHandler("raise", interrupting: false);
        var parent = new BpmnProcessBuilder("repeated-own-scope-escalation")
            .StartEvent("Start")
            .Task("Begin", bindingRef: "begin")
            .IntermediateThrowEvent("RaiseFirst", Escalation("raise"))
            .IntermediateThrowEvent("RaiseSecond", Escalation("raise"))
            .EndEvent("End")
            .SubProcess("Handler", bindingRef: "handler-body", triggeredByEvent: true)
            .ConnectSequence("Start", "Begin", "RaiseFirst", "RaiseSecond", "End")
            .Build();
        var instance = HostWithNested(("handler-body", handler)).Start(parent);

        instance.CompleteWork("begin");

        instance.Children.Count.ShouldBe(2);
        instance.Children.Select(child => child.PendingWork.ShouldHaveSingleItem().BindingRef)
            .ShouldAllBe(bindingRef => bindingRef == "handle");
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EventSubprocessActivated
            && diagnostic.ElementId == "Handler").ShouldBe(2);
        instance.Children[0].CompleteWork("handle");
        instance.Children[1].CompleteWork("handle");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void A_non_interrupting_boundary_fires_once_per_escalation_notification()
    {
        var body = new BpmnProcessBuilder("repeated-boundary-child")
            .StartEvent("Start")
            .Task("Trigger", bindingRef: "trigger")
            .IntermediateThrowEvent("RaiseFirst", Escalation("raise"))
            .IntermediateThrowEvent("RaiseSecond", Escalation("raise"))
            .Task("Hold", bindingRef: "hold")
            .EndEvent("End")
            .ConnectSequence("Start", "Trigger", "RaiseFirst", "RaiseSecond", "Hold", "End")
            .Build();
        var instance = HostWithNested(("sub", body)).Start(BoundaryParent("raise", interrupting: false));
        var child = instance.Children.ShouldHaveSingleItem();

        child.CompleteWork("trigger");

        var boundaryWork = instance.PendingWork.Where(work => work.BindingRef == "recover").ToArray();
        boundaryWork.Length.ShouldBe(2);
        boundaryWork.Select(work => work.Handle).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        foreach (var work in boundaryWork)
            instance.CompleteWork(work);
        child.CompleteWork("hold");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void A_late_non_interrupting_signal_fires_after_its_host_routed_normally()
    {
        var definition = new BpmnProcessBuilder("late-escalation")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .Task("After", bindingRef: "after")
            .EndEvent("End")
            .BoundaryEvent("Escalation", "Sub", Escalation("raise"), interrupting: false)
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndRecovered")
            .ConnectSequence("Start", "Sub", "After", "End")
            .ConnectSequence("Escalation", "Recover", "EndRecovered")
            .Build();
        var host = new InMemoryBpmnHost();
        var instance = host.Start(definition);
        var completedHostWork = instance.ResolveWork("sub");

        instance.CompleteWork(completedHostWork);

        instance.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("after");
        var payload = JsonDocument.Parse("{\"code\":\"raise\"}");
        var signal = host.Interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            instance.Graph,
            instance.State,
            BpmnHostSnapshot.Root(instance.ScopeInstanceId) with
            {
                LiveWork = instance.PendingWork
                    .Select(work => new BpmnLiveWork(work.BindingRef, work.IterationId, work.Handle))
                    .ToArray()
            },
            completedHostWork.BindingRef,
            completedHostWork.Handle,
            BpmnInterpreter.EscalationSignalCode,
            payload.RootElement.Clone()));

        signal.Continuation.ShouldBeOfType<BpmnContinuation.Defer>();
        signal.Commands.OfType<BpmnHostCommand.StartWork>().ShouldContain(work => work.BindingRef == "recover");
        signal.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EscalationCaught
            && diagnostic.ElementId == "Escalation");
        signal.State.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EscalationLate);
        payload.Dispose();
    }

    [Fact]
    public void An_interrupting_escalation_from_one_multi_instance_child_cancels_the_coordinator_and_siblings()
    {
        var body = new BpmnProcessBuilder("multi-instance-escalation-child")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("Trigger", bindingRef: "trigger")
            .IntermediateThrowEvent("Raise", Escalation("raise"))
            .EndEvent("TriggerEnd")
            .Task("Hold", bindingRef: "hold")
            .EndEvent("HoldEnd")
            .Connect("Start", "Split")
            .ConnectSequence("Split", "Trigger", "Raise", "TriggerEnd")
            .ConnectSequence("Split", "Hold", "HoldEnd")
            .Build();
        var parent = new BpmnProcessBuilder("multi-instance-escalation-parent")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub", loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: 2))
            .EndEvent("EndSub")
            .BoundaryEvent("Escalation", "Sub", Escalation("raise"), interrupting: true)
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndRecovered")
            .ConnectSequence("Start", "Sub", "EndSub")
            .ConnectSequence("Escalation", "Recover", "EndRecovered")
            .Build();
        var instance = HostWithNested(("sub", body)).Start(parent);
        var instances = instance.Children.ToArray();
        instances.Length.ShouldBe(2);
        instances.Select(child => child.ScopeInstanceId).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        var multiInstanceWork = instance.PendingWork.Where(work => work.BindingRef == "sub").ToArray();
        multiInstanceWork.Length.ShouldBe(2);
        multiInstanceWork.Select(work => work.IterationId).ShouldAllBe(iterationId => iterationId != null);
        multiInstanceWork.Select(work => work.IterationId).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);

        instances[0].CompleteWork("trigger");

        instances.ShouldAllBe(child => child.IsCancelled);
        instances.ShouldAllBe(child => child.PendingWork.Count == 0);
        instance.CancelledWork.ShouldContain(work => work.ElementId == "Sub"
            && work.Reason == BpmnInterpreter.EscalationHostInterruptedReason);
        instance.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("recover");
        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void Repeating_the_same_escalation_scenario_preserves_token_and_diagnostic_ids()
    {
        static (string[] TokenMoves, string[] Diagnostics) RunOnce()
        {
            var body = ThrowingChild("raise");
            var instance = HostWithNested(("sub", body)).Start(BoundaryParent("raise", interrupting: false));
            instance.Children.ShouldHaveSingleItem().CompleteWork("trigger");
            instance.CompleteWork("recover");
            instance.Children[0].CompleteWork("hold");

            return AdvancedScenarioFixtures.DeterministicEvidence(instance);
        }

        var first = RunOnce();
        var second = RunOnce();
        first.TokenMoves.ShouldBe(second.TokenMoves);
        first.Diagnostics.ShouldBe(second.Diagnostics);
    }

    [Fact]
    public void Repeating_an_event_subprocess_scenario_preserves_token_and_diagnostic_ids()
    {
        static (string[] TokenMoves, string[] Diagnostics) RunOnce()
        {
            var handler = EscalationHandler("raise", interrupting: false);
            var parent = new BpmnProcessBuilder("deterministic-event-subprocess")
                .StartEvent("Start")
                .Task("Begin", bindingRef: "begin")
                .IntermediateThrowEvent("Raise", Escalation("raise"))
                .EndEvent("End")
                .SubProcess("Handler", bindingRef: "handler-body", triggeredByEvent: true)
                .ConnectSequence("Start", "Begin", "Raise", "End")
                .Build();
            var instance = HostWithNested(("handler-body", handler)).Start(parent);
            instance.CompleteWork("begin");
            instance.Children.ShouldHaveSingleItem().CompleteWork("handle");

            return AdvancedScenarioFixtures.DeterministicEvidence(instance);
        }

        var first = RunOnce();
        var second = RunOnce();
        first.TokenMoves.ShouldBe(second.TokenMoves);
        first.Diagnostics.ShouldBe(second.Diagnostics);
    }

    [Fact]
    public void An_escalation_event_subprocess_does_not_absorb_a_work_fault()
    {
        var handler = EscalationHandler("raise", interrupting: true);
        var parent = new BpmnProcessBuilder("escalation-does-not-catch-error")
            .StartEvent("Start")
            .Task("Work", bindingRef: "work")
            .EndEvent("End")
            .SubProcess("Handler", bindingRef: "handler-body", triggeredByEvent: true)
            .ConnectSequence("Start", "Work", "End")
            .Build();
        var instance = HostWithNested(("handler-body", handler)).Start(parent);

        instance.FaultWork("work", "operation failed");

        instance.IsFaulted.ShouldBeTrue();
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.Faulted);
        instance.Children.ShouldBeEmpty("an escalation catcher is not an error catcher");
    }

    [Fact]
    public void An_error_event_subprocess_absorbs_the_host_fault_and_runs_its_body()
    {
        var handler = ErrorHandler();
        var parent = new BpmnProcessBuilder("error-event-subprocess")
            .StartEvent("Start")
            .Task("Work", bindingRef: "work")
            .EndEvent("End")
            .SubProcess("Handler", bindingRef: "handler-body", triggeredByEvent: true)
            .ConnectSequence("Start", "Work", "End")
            .Build();
        var instance = HostWithNested(("handler-body", handler)).Start(parent);

        instance.FaultWork("work", "operation failed");

        instance.IsFaulted.ShouldBeFalse();
        instance.Children.ShouldHaveSingleItem().PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("handle");
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EventSubprocessActivated
            && diagnostic.ElementId == "Handler");
        instance.Children[0].CompleteWork("handle");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void An_event_subprocess_body_fault_uses_the_ordinary_scope_fault_path()
    {
        var handler = new BpmnProcessBuilder("faulting-handler")
            .StartEvent("HandlerStart", null, Escalation("raise"))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("HandlerStart", "Handle", "HandlerEnd")
            .Build();
        var parent = new BpmnProcessBuilder("faulting-handler-parent")
            .StartEvent("Start")
            .IntermediateThrowEvent("Raise", Escalation("raise"))
            .EndEvent("End")
            .SubProcess("Handler", bindingRef: "handler-body", triggeredByEvent: true)
            .ConnectSequence("Start", "Raise", "End")
            .Build();
        var instance = HostWithNested(("handler-body", handler)).Start(parent);

        instance.Children.ShouldHaveSingleItem().FaultWork("handle", "handler failed");

        instance.IsFaulted.ShouldBeTrue();
        instance.Fault!.Message.ShouldContain("handler failed");
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.Faulted);
    }

    private static BpmnProcessDefinition BoundaryParent(string code, bool interrupting, bool includeCatchAll = false)
    {
        var builder = new BpmnProcessBuilder("boundary-parent")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("EndSub")
            .BoundaryEvent("Specific", "Sub", Escalation(code), interrupting)
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndSpecific");
        builder.ConnectSequence("Start", "Sub", "EndSub")
            .ConnectSequence("Specific", "Recover", "EndSpecific");

        if (!includeCatchAll)
            return builder.Build();

        return builder
            .BoundaryEvent("CatchAll", "Sub", Escalation(null), interrupting: false)
            .Task("Fallback", bindingRef: "catch-all")
            .EndEvent("EndCatchAll")
            .ConnectSequence("CatchAll", "Fallback", "EndCatchAll")
            .Build();
    }

    private static BpmnProcessDefinition ThrowingChild(string code) =>
        new BpmnProcessBuilder("throwing-child")
            .StartEvent("Start")
            .Task("Trigger", bindingRef: "trigger")
            .IntermediateThrowEvent("Raise", Escalation(code))
            .Task("Hold", bindingRef: "hold")
            .EndEvent("End")
            .ConnectSequence("Start", "Trigger", "Raise", "Hold", "End")
            .Build();

    private static BpmnProcessDefinition EscalationHandler(string? code, bool interrupting) =>
        new BpmnProcessBuilder("escalation-handler")
            .Element(new BpmnElement("HandlerStart", BpmnElementTypes.StartEvent, cancelActivity: interrupting,
                eventDefinitions: [Escalation(code)]))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("End")
            .ConnectSequence("HandlerStart", "Handle", "End")
            .Build();

    private static BpmnProcessDefinition ErrorHandler() =>
        new BpmnProcessBuilder("error-handler")
            .StartEvent("HandlerStart", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("End")
            .ConnectSequence("HandlerStart", "Handle", "End")
            .Build();

    private static InMemoryBpmnHost HostWithNested(params (string BindingRef, BpmnProcessDefinition Definition)[] nested) =>
        new(new InMemoryBpmnHostOptions
        {
            NestedProcesses = nested.ToDictionary(entry => entry.BindingRef, entry => entry.Definition, StringComparer.Ordinal)
        });

    private static BpmnEventDefinition Escalation(string? code) =>
        new(BpmnEventDefinitionTypes.Escalation,
            code is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code });
}
