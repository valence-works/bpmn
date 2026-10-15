using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// A terminate end event and a behavior fault ending their scope, watched from the host's side of the port.
/// <para>
/// Two things went wrong here. A verdict reached in the same evaluation that had already activated work was
/// deferred: parked on the state and surfaced on the next completion, so a scope whose freshly started work
/// never completed — a message that never arrives — never ended. And a terminate tore nothing down: it
/// cancelled live work only logically, so a host that does not stop a finished scope's children by itself
/// kept that work running for good.
/// </para>
/// <para>
/// The reference host <i>does</i> stop a finishing scope's children by itself, which is exactly what makes
/// this easy to test wrongly: its cancelled-work list fills up whether or not the interpreter asked. So these
/// tests read the commands and continuation of the deciding evaluation, and the teardown reasons, rather than
/// what the host ended up doing.
/// </para>
/// </summary>
public sealed class TerminateAndFaultTests
{
    private static readonly BpmnContinuation Done = new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName);

    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    private static BpmnGraph BuildGraph(BpmnProcessDefinition definition) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full);

    private static InMemoryProcessInstance Start(BpmnProcessDefinition definition) => new InMemoryBpmnHost().Start(definition);

    private static InMemoryProcessInstance Start(
        (BpmnProcessDefinition Parent, IReadOnlyDictionary<string, BpmnProcessDefinition> Nested) fixture) =>
        new InMemoryBpmnHost(new InMemoryBpmnHostOptions { NestedProcesses = fixture.Nested }).Start(fixture.Parent);

    /// <summary>Every unit of work the scope has running except <paramref name="trigger"/>, which is about to complete.</summary>
    private static IReadOnlyList<InMemoryWorkItem> LiveBesides(InMemoryProcessInstance instance, InMemoryWorkItem trigger) =>
        instance.PendingWork.Where(work => work.Handle != trigger.Handle).ToArray();

    // --- No deferral -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_terminate_completes_at_once_and_never_starts_the_work_its_own_evaluation_activated(bool workFirst)
    {
        var instance = Start(ProcessFixtures.SplitIntoWorkAndTerminate(workFirst));

        var evaluation = instance.Transcript.ShouldHaveSingleItem();
        evaluation.Continuation.ShouldBe(Done, instance.Transcript.ToString());
        evaluation.Commands.ShouldBeEmpty("work activated and terminated in the same instant is never started");

        instance.IsCompleted.ShouldBeTrue();
        instance.PendingWork.ShouldBeEmpty();
        instance.State.ActiveWork.ShouldBeEmpty("the state must not claim work the host was never asked to start");
        instance.State.Tokens.Where(token => token.AtElementId is "Wait" or "Hold")
            .ShouldAllBe(token => token.Status == BpmnTokenStatus.Canceled);
    }

    [Fact]
    public void A_fault_is_returned_at_once_and_never_starts_the_work_its_own_evaluation_activated()
    {
        var instance = Start(ProcessFixtures.SplitIntoWorkAndFault());

        var evaluation = instance.Transcript.ShouldHaveSingleItem();
        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>(instance.Transcript.ToString())
            .Code.ShouldBe("bpmn.gateway.no-flow-selected");
        evaluation.Commands.ShouldBeEmpty("a fault continuation carries no StartWork, and no teardown either");

        instance.PendingWork.ShouldBeEmpty();
        instance.State.PendingFault.ShouldBeNull("the verdict was returned, not parked for a later callback");
        instance.State.ActiveWork.ShouldBeEmpty("the state must not claim work the host was never asked to start");
        instance.State.Tokens.Single(token => token.AtElementId == "Wait").Status.ShouldBe(BpmnTokenStatus.Canceled);
    }

    [Fact]
    public void A_caught_error_whose_path_then_faults_beside_new_work_propagates_at_once()
    {
        var instance = Start(ProcessFixtures.ErrorPathThatForksIntoWorkAndFault());

        instance.FaultWork("risky", "boom");

        var evaluation = instance.Transcript[^1];
        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>(instance.Transcript.ToString())
            .Code.ShouldBe("bpmn.gateway.no-flow-selected");
        evaluation.Disposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance,
            "the catcher's own path faulted, so the error was not caught after all");
        evaluation.Commands.ShouldBeEmpty("neither the recovery's StartWork nor any teardown rides the fault");
    }

    // --- Teardown --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_terminate_tears_down_every_unit_an_earlier_evaluation_left_running(bool activatesWorkInTheSameInstant)
    {
        var instance = Start(ProcessFixtures.TerminateBesideLiveWork(activatesWorkInTheSameInstant));
        var trigger = instance.ResolveWork("trigger");
        var earlier = LiveBesides(instance, trigger);
        earlier.Select(work => work.ElementId)
            .ShouldBe(["Long", "Timeout", "Review", "Review", "Sub", "OnNudge"], ignoreOrder: true,
                customMessage: "the fixture must really have this work running when the terminate is reached");

        instance.CompleteWork(trigger);

        var evaluation = instance.Transcript[^1];
        evaluation.Continuation.ShouldBe(Done, instance.Transcript.ToString());
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().ShouldBeEmpty("a terminal continuation never carries a StartWork");

        var teardowns = evaluation.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ToArray();
        teardowns.Select(teardown => teardown.Handle).ShouldBe(earlier.Select(work => work.Handle), ignoreOrder: true);
        teardowns.Select(teardown => teardown.ElementId).ShouldBe(earlier.Select(work => work.ElementId), ignoreOrder: true);
        teardowns.ShouldAllBe(teardown => teardown.Reason == BpmnInterpreter.TerminateStopReason);

        instance.CancelledWork.ShouldAllBe(cancelled => cancelled.Reason == BpmnInterpreter.TerminateStopReason,
            "the host must not have had to stop anything the interpreter did not name");
        instance.Children.Single(child => child.ParentWorkHandle == earlier.Single(work => work.ElementId == "Sub").Handle)
            .IsCancelled.ShouldBeTrue("tearing the subprocess down takes its own work with it");
    }

    [Fact]
    public void A_unit_a_carried_teardown_already_names_keeps_its_own_reason_when_a_terminate_follows()
    {
        var instance = Start(ProcessFixtures.RaceWonIntoTerminate());
        var loser = instance.ResolveWork("catch-b").Handle;
        var other = instance.ResolveWork("other").Handle;

        instance.CompleteWork("catch-a");

        var evaluation = instance.Transcript[^1];
        evaluation.Continuation.ShouldBe(Done, instance.Transcript.ToString());
        evaluation.Commands.ShouldBe(
        [
            new BpmnHostCommand.CancelWorkSubtree(loser, "CatchB", BpmnInterpreter.EventGatewayRaceCancellationReason),
            new BpmnHostCommand.CancelWorkSubtree(other, "Other", BpmnInterpreter.TerminateStopReason)
        ], "the losing catch is torn down once, for losing the race; only the rest is torn down for the terminate");
    }

    [Fact]
    public void A_fault_carries_no_teardown_for_the_work_an_earlier_evaluation_left_running()
    {
        var instance = Start(ProcessFixtures.FaultBesideLiveWork());
        var trigger = instance.ResolveWork("trigger");
        var earlier = LiveBesides(instance, trigger);

        instance.CompleteWork(trigger);

        var evaluation = instance.Transcript[^1];
        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>(instance.Transcript.ToString());
        evaluation.Commands.ShouldBeEmpty("a fault is a verdict: no StartWork for the follow-up, and no teardown either");

        // Stopping what was already running is the host's half of a fault, and this host does it.
        instance.CancelledWork.Select(cancelled => cancelled.Handle).ShouldBe(earlier.Select(work => work.Handle), ignoreOrder: true);
        instance.CancelledWork.ShouldAllBe(cancelled => cancelled.Reason == InMemoryProcessInstance.ScopeFinishedReason);
    }

    // --- Nesting, and the commands a withdrawal keeps -------------------------------------------------

    private static InMemoryProcessInstance StartSubProcessThatEscalatesThenTerminates()
    {
        var (parent, body) = ProcessFixtures.SubProcessThatEscalatesThenTerminates();
        return new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["sub"] = body }
        }).Start(parent);
    }

    [Fact]
    public void A_terminate_inside_a_subprocess_ends_that_scope_and_leaves_its_parent_running()
    {
        var instance = StartSubProcessThatEscalatesThenTerminates();

        var child = instance.Children.ShouldHaveSingleItem();
        child.Transcript.ShouldHaveSingleItem().Continuation.ShouldBe(Done, child.Transcript.ToString());

        instance.IsCompleted.ShouldBeFalse("the terminate ended the subprocess, not the process that called it");
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["ParentWork"], instance.Transcript.ToString());
        instance.CancelledWork.ShouldBeEmpty();

        instance.CompleteWork("parent-work");
        instance.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName);
    }

    [Fact]
    public void Withdrawing_the_work_keeps_the_signal_the_same_evaluation_already_raised()
    {
        var child = StartSubProcessThatEscalatesThenTerminates().Children.ShouldHaveSingleItem();

        var evaluation = child.Transcript.ShouldHaveSingleItem();
        evaluation.Continuation.ShouldBe(Done, child.Transcript.ToString());
        evaluation.Commands.ShouldHaveSingleItem().ShouldBeOfType<BpmnHostCommand.SignalEnclosingScope>()
            .Code.ShouldBe(BpmnInterpreter.EscalationSignalCode, "the escalation happened before the terminate, so the parent still hears it");
        child.ScopeSignals.ShouldHaveSingleItem().Delivered.ShouldBeTrue();
    }

    // --- Callbacks that reach a scope after it terminated -----------------------------------------------

    private static readonly BpmnGraph SignallingChildGraph = BuildGraph(ProcessFixtures.TerminateBesideASignallingChild());
    private static readonly BpmnLiveWork LiveChild = new("child", null, "h-child");
    private static readonly BpmnLiveWork LiveOther = new("other", null, "h-other");

    private static BpmnHostSnapshot NestedScope(params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root("scope-1") with { HasEnclosingScope = true, LiveWork = liveWork };

    private static BpmnHostCommand[] TerminateTeardowns(params (BpmnLiveWork Work, string ElementId)[] units) =>
        units.Select(unit => (BpmnHostCommand)new BpmnHostCommand.CancelWorkSubtree(unit.Work.Handle, unit.ElementId, BpmnInterpreter.TerminateStopReason)).ToArray();

    /// <summary>
    /// Drives <see cref="ProcessFixtures.TerminateBesideASignallingChild"/>, as a nested scope, through its terminate
    /// and returns the state a host would persist. The test is the host's ledger here, and it deliberately has not
    /// applied the terminate's teardowns yet: that is the window in which a real host can still receive a callback
    /// from the child or the other task.
    /// </summary>
    private static BpmnExecutionState TerminateBesideASignallingChild()
    {
        var started = Interpreter.Start(new BpmnStartRequest(SignallingChildGraph, null, NestedScope()));
        started.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["child", "other", "trigger"]);

        var terminated = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            SignallingChildGraph, started.State, NestedScope(LiveChild, LiveOther), "trigger", "h-trigger", []));
        terminated.Continuation.ShouldBe(Done);
        terminated.Commands.ShouldBe(TerminateTeardowns((LiveChild, "Child"), (LiveOther, "Other")));

        return terminated.State;
    }

    /// <summary>Verifies teardown signals cannot activate a catcher or bubble an escalation out of a terminated scope.</summary>
    /// <param name="code">An escalation code that a live scope would bubble or catch.</param>
    [Theory]
    [InlineData("late")]
    [InlineData("forward")]
    public void A_terminated_scope_neither_forwards_nor_catches_a_signal_from_work_it_is_tearing_down(string code)
    {
        // 'late' matches nothing here, so a live scope would bubble it to the parent. 'forward' fires the
        // child's non-interrupting boundary, whose path throws an escalation outward.
        var payload = BpmnInterpreter.EscalationPayload(code);

        var evaluation = Interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            SignallingChildGraph, TerminateBesideASignallingChild(), NestedScope(LiveChild, LiveOther),
            "child", LiveChild.Handle, BpmnInterpreter.EscalationSignalCode, payload));

        evaluation.Continuation.ShouldBe(Done);
        evaluation.Commands.ShouldBe(TerminateTeardowns((LiveChild, "Child"), (LiveOther, "Other")),
            "a dead scope forwards nothing and starts nothing; it only repeats the teardowns still outstanding");
    }

    [Fact]
    public void A_terminated_scope_absorbs_a_fault_from_work_it_is_tearing_down()
    {
        var evaluation = Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            SignallingChildGraph, TerminateBesideASignallingChild(), NestedScope(LiveOther), "child", LiveChild.Handle, "boom"));

        evaluation.Continuation.ShouldBe(Done, "the scope already ended; a fault cannot reopen it");
        evaluation.Commands.ShouldBe(TerminateTeardowns((LiveOther, "Other")));
    }

    [Fact]
    public void A_late_completion_after_a_terminate_completes_again_repeats_the_outstanding_teardowns_and_starts_nothing()
    {
        // The child finished on its own before the host got to its teardown; the other task is still listed.
        var evaluation = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            SignallingChildGraph, TerminateBesideASignallingChild(), NestedScope(LiveOther), "child", LiveChild.Handle, []));

        evaluation.Continuation.ShouldBe(Done);
        evaluation.Commands.ShouldBe(TerminateTeardowns((LiveOther, "Other")),
            "the child's outbound flow is not routed, and the teardown for the unit still listed is sent again");
    }

    // --- States an earlier version persisted -----------------------------------------------------------

    /// <summary>
    /// The state the previous version persisted for <see cref="ProcessFixtures.SplitIntoWorkAndTerminate"/>
    /// when it deferred a verdict: every live token cancelled, every work record dropped, and the verdict
    /// parked. It had answered <c>Defer</c> with both tasks started, so the host is running both.
    /// </summary>
    private static BpmnExecutionState ParkedState(bool terminated = false, BpmnPendingFault? pendingFault = null) =>
        new(
            tokens:
            [
                new BpmnToken("token:1", "Start", status: BpmnTokenStatus.Consumed),
                new BpmnToken("token:3", "Split", "flow-1", "token:1", BpmnTokenStatus.Consumed),
                new BpmnToken("token:5", "Wait", "flow-2", "token:3", BpmnTokenStatus.Canceled),
                new BpmnToken("token:6", "Hold", "flow-3", "token:3", BpmnTokenStatus.Canceled),
                new BpmnToken("token:7", "Terminate", "flow-4", "token:3", BpmnTokenStatus.Consumed)
            ],
            sequence: 20,
            terminated: terminated,
            pendingFault: pendingFault);

    /// <summary>Reports that <c>wait</c> completed against <paramref name="state"/>, with <c>hold</c> still running on the host.</summary>
    private static BpmnEvaluation CompleteWaitAgainst(BpmnExecutionState state)
    {
        var host = BpmnHostSnapshot.Root("scope-1") with { LiveWork = [new BpmnLiveWork("hold", null, "work-2")] };

        return Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            BuildGraph(ProcessFixtures.SplitIntoWorkAndTerminate()), state, host, CompletedBindingRef: "wait", CompletedHandle: "work-1", OutcomeNames: []));
    }

    [Fact]
    public void A_terminate_an_earlier_version_deferred_completes_on_the_next_completion_and_tears_down_what_it_left()
    {
        var evaluation = CompleteWaitAgainst(ParkedState(terminated: true));

        evaluation.Continuation.ShouldBe(Done);
        evaluation.Commands.ShouldBe([new BpmnHostCommand.CancelWorkSubtree("work-2", "Hold", BpmnInterpreter.TerminateStopReason)]);
    }

    [Fact]
    public void A_fault_an_earlier_version_deferred_surfaces_on_the_next_completion()
    {
        var evaluation = CompleteWaitAgainst(ParkedState(pendingFault: new BpmnPendingFault("bpmn.gateway.no-flow-selected", "parked")));

        evaluation.Continuation.ShouldBe(new BpmnContinuation.Fault("bpmn.gateway.no-flow-selected", "parked"));
        evaluation.Commands.ShouldBeEmpty("a fault still carries no teardown");
    }

    // --- Capabilities ----------------------------------------------------------------------------------

    [Fact]
    public void A_terminate_end_event_declares_the_subtree_cancellation_it_now_uses()
    {
        var requirements = BpmnCapabilityRequirements.Analyze(ProcessFixtures.SplitIntoWorkAndTerminate());

        requirements.MissingFrom(BpmnHostCapabilities.None).ShouldBe(BpmnHostCapabilities.SubtreeCancellation);
        requirements.DrivingElementIds[BpmnHostCapabilities.SubtreeCancellation].ShouldBe(["Terminate"]);
    }
}
