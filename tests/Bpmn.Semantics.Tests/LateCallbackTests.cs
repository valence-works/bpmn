using Bpmn.Model;
using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Callbacks into a running scope for work it no longer waits on, driven against the interpreter directly.
/// <para>
/// A host can still report work after the scope tore it down: a completion that raced the teardown. That is
/// absorbed, but only on the evidence of the handle: the scope records each handle it issued a teardown for, and a
/// callback is late only when its handle is one of them. Matching on anything coarser is wrong in both
/// directions. An element revisited by a loop holds Canceled tokens from earlier passes, so a duplicate report
/// from a later pass would pass for late; and a late report from an earlier pass would resolve to the later pass's
/// record on the same slot. Every other unmatched callback is a host breaking the contract and stays loud. The
/// reference host cannot show any of this: it drops a callback for work it already stopped before the interpreter
/// sees it.
/// </para>
/// </summary>
public sealed class LateCallbackTests
{
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();
    private static readonly BpmnContinuation Defer = BpmnContinuation.Defer.Instance;

    private static BpmnGraph BuildGraph(BpmnProcessDefinition definition, BpmnProcessDefinition? body = null) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition, _ => body), BpmnHostCapabilities.Full);

    private static BpmnHostSnapshot Scope(params BpmnLiveWork[] liveWork) => BpmnHostSnapshot.Root("scope-1") with { LiveWork = liveWork };

    private static BpmnEvaluation Start(BpmnGraph graph) => Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));

    private static BpmnEvaluation Complete(BpmnGraph graph, BpmnEvaluation prior, string bindingRef, string handle, BpmnLiveWork[] liveWork, string? iterationId = null) =>
        Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(graph, prior.State, Scope(liveWork), bindingRef, handle, [], iterationId));

    private static BpmnFaultEvaluation Fault(BpmnGraph graph, BpmnEvaluation prior, string bindingRef, string handle, BpmnLiveWork[] liveWork) =>
        Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(graph, prior.State, Scope(liveWork), bindingRef, handle, "boom"));

    /// <summary>
    /// Asserts that <paramref name="late"/> asked the host for nothing and moved no token, and that it spent the
    /// record of <paramref name="handle"/>, so a second report for it cannot be absorbed again.
    /// </summary>
    private static void ShouldHaveBeenAbsorbed(BpmnEvaluation late, BpmnEvaluation prior, string handle)
    {
        late.Continuation.ShouldBe(Defer, "the scope is still running and the callback changes nothing about it");
        late.Commands.ShouldBeEmpty();
        late.State.Tokens.Select(token => token with { StoppedHandle = null })
            .ShouldBe(prior.State.Tokens.Select(token => token with { StoppedHandle = null }), "no token moves for work the scope already stopped");
        late.State.ActiveWork.ShouldBe(prior.State.ActiveWork);
        late.State.Diagnostics.Last().Kind.ShouldBe(BpmnDiagnosticKind.Canceled);

        prior.State.Tokens.ShouldContain(token => token.StoppedHandle == handle, "the precondition: the scope recorded the handle it tore down");
        late.State.Tokens.ShouldNotContain(token => token.StoppedHandle == handle);
    }

    // --- Absorbed: the handle is one the scope tore down ----------------------------------------------

    private static readonly BpmnLiveWork LongUnit = new("long", null, "h-long");
    private static readonly BpmnLiveWork EscalateUnit = new("escalate", null, "h-escalate");

    /// <summary><see cref="ProcessFixtures.TimerBoundary"/> after its timer fired and tore <c>Long</c> down.</summary>
    private static (BpmnGraph Graph, BpmnEvaluation Fired) TimedOut()
    {
        var graph = BuildGraph(ProcessFixtures.TimerBoundary());
        var fired = Complete(graph, Start(graph), "timeout", "h-timeout", [LongUnit]);
        fired.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldHaveSingleItem().Handle.ShouldBe(LongUnit.Handle);
        return (graph, fired);
    }

    [Fact]
    public void A_completion_that_raced_an_interrupting_boundary_teardown_is_absorbed()
    {
        var (graph, fired) = TimedOut();

        var late = Complete(graph, fired, "long", LongUnit.Handle, [EscalateUnit]);

        ShouldHaveBeenAbsorbed(late, fired, LongUnit.Handle);
    }

    [Fact]
    public void A_fault_that_raced_an_interrupting_boundary_teardown_is_absorbed()
    {
        var (graph, fired) = TimedOut();

        var late = Fault(graph, fired, "long", LongUnit.Handle, [EscalateUnit]);

        ShouldHaveBeenAbsorbed(late, fired, LongUnit.Handle);
    }

    [Fact]
    public void A_second_late_report_for_the_same_torn_down_unit_is_not_absorbed_again()
    {
        var (graph, fired) = TimedOut();
        var late = Complete(graph, fired, "long", LongUnit.Handle, [EscalateUnit]);

        Should.Throw<BpmnExecutionException>(() => Complete(graph, late, "long", LongUnit.Handle, [EscalateUnit]));
    }

    private static readonly BpmnLiveWork Cleanup = new("cleanup", null, "h-cleanup");

    /// <summary>
    /// Drives <see cref="ProcessFixtures.InterruptibleParallelMultiInstance"/> until its boundary fires and tears both
    /// instances down; the boundary path then waits on <see cref="Cleanup"/>, so the scope is still running.
    /// </summary>
    private static (BpmnGraph Graph, BpmnEvaluation Stopped, BpmnLiveWork[] Instances) StopTheInstances()
    {
        var graph = BuildGraph(ProcessFixtures.InterruptibleParallelMultiInstance());
        var started = Start(graph);
        var instances = started.Commands.OfType<BpmnHostCommand.StartWork>().Where(start => start.BindingRef == "review")
            .Select((start, index) => new BpmnLiveWork("review", start.IterationScope!.IterationId, $"h-review-{index}"))
            .ToArray();

        var stopped = Complete(graph, started, "stop", "h-stop", instances);
        stopped.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().Select(teardown => teardown.Handle)
            .ShouldBe(instances.Select(instance => instance.Handle));

        return (graph, stopped, instances);
    }

    [Fact]
    public void A_completion_of_a_torn_down_multi_instance_instance_is_absorbed()
    {
        var (graph, stopped, instances) = StopTheInstances();

        var late = Complete(graph, stopped, "review", instances[0].Handle, [Cleanup], instances[0].IterationId);

        ShouldHaveBeenAbsorbed(late, stopped, instances[0].Handle);
    }

    // --- A slot re-used by a retry loop ---------------------------------------------------------------

    private static readonly BpmnLiveWork OtherUnit = new("other", null, "h-other");
    private static readonly BpmnLiveWork FirstPass = new("long", null, "h-long-1");
    private static readonly BpmnLiveWork SecondPass = new("long", null, "h-long-2");
    private static readonly BpmnLiveWork SecondTimer = new("timeout", null, "h-timeout-2");

    /// <summary>
    /// <see cref="ProcessFixtures.RetriedOnTimeout"/> after its first timeout: <see cref="FirstPass"/> torn down, and
    /// <see cref="SecondPass"/> started on the same slot under its own timer.
    /// </summary>
    private static (BpmnGraph Graph, BpmnEvaluation Retried) RetriedOnce()
    {
        var graph = BuildGraph(ProcessFixtures.RetriedOnTimeout());
        var retried = Complete(graph, Start(graph), "timeout", "h-timeout-1", [FirstPass, OtherUnit]);
        retried.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldHaveSingleItem().Handle.ShouldBe(FirstPass.Handle);
        retried.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["long", "timeout"]);
        return (graph, retried);
    }

    [Fact]
    public void A_late_completion_from_an_earlier_pass_is_absorbed_and_the_current_pass_still_routes_on_its_own()
    {
        var (graph, retried) = RetriedOnce();

        var late = Complete(graph, retried, "long", FirstPass.Handle, [SecondPass, SecondTimer, OtherUnit]);
        ShouldHaveBeenAbsorbed(late, retried, FirstPass.Handle);

        var secondDone = Complete(graph, late, "long", SecondPass.Handle, [SecondTimer, OtherUnit]);
        secondDone.Commands.ShouldBe([new BpmnHostCommand.CancelWorkSubtree(SecondTimer.Handle, "Timeout", BpmnInterpreter.BoundarySupersededByHostCompletionReason)],
            "the current pass completes, so its own timer is retired");
        secondDone.State.ActiveWork.Select(work => work.NodeId).ShouldBe(["other"]);

        Complete(graph, secondDone, "other", OtherUnit.Handle, []).Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
    }

    /// <summary><see cref="RetriedOnce"/> with the second pass completed normally, so the element holds a Canceled token from pass one.</summary>
    private static (BpmnGraph Graph, BpmnEvaluation SecondDone) SecondPassCompleted()
    {
        var (graph, retried) = RetriedOnce();
        var secondDone = Complete(graph, retried, "long", SecondPass.Handle, [SecondTimer, OtherUnit]);
        secondDone.State.Tokens.ShouldContain(token => token.AtElementId == "Long" && token.Status == BpmnTokenStatus.Canceled);
        return (graph, secondDone);
    }

    [Fact]
    public void A_duplicate_completion_from_a_later_pass_stays_loud_though_an_earlier_pass_was_cancelled()
    {
        var (graph, secondDone) = SecondPassCompleted();

        Should.Throw<BpmnExecutionException>(() => Complete(graph, secondDone, "long", SecondPass.Handle, [OtherUnit]));
    }

    [Fact]
    public void A_duplicate_fault_from_a_later_pass_faults_the_scope_though_an_earlier_pass_was_cancelled()
    {
        var (graph, secondDone) = SecondPassCompleted();

        Fault(graph, secondDone, "long", SecondPass.Handle, [OtherUnit])
            .Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.work.faulted");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_report_on_a_slot_the_host_still_runs_under_another_handle_throws(bool asFault)
    {
        var (graph, retried) = RetriedOnce();
        BpmnLiveWork[] live = [SecondPass, SecondTimer, OtherUnit];

        Should.Throw<BpmnExecutionException>(() => asFault
                ? Fault(graph, retried, "long", "h-never-started", live)
                : Complete(graph, retried, "long", "h-never-started", live))
            .Message.ShouldContain(SecondPass.Handle, customMessage: "resolving it to the live record would finish the current pass with someone else's result");
    }

    // --- Still loud: no teardown names the handle -----------------------------------------------------

    [Fact]
    public void A_completion_for_work_withdrawn_before_the_host_saw_it_stays_loud()
    {
        // The long-running task was started and stopped in the same evaluation, so its StartWork was withdrawn
        // and no handle was ever recorded for it. A callback for it is one the host invented.
        var (parent, body) = ProcessFixtures.SplitIntoWorkAndOwnScopeEscalation();
        var graph = BuildGraph(parent, body);
        var started = Start(graph);
        started.State.Tokens.Single(token => token.AtElementId == "LongRunning").Status.ShouldBe(BpmnTokenStatus.Canceled);
        BpmnLiveWork[] live = [new("on-overdue", null, "h-body")];

        Should.Throw<BpmnExecutionException>(() => Complete(graph, started, "long-running", "h-long", live));
        Fault(graph, started, "long-running", "h-long", live).Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
    }

    /// <summary><see cref="ProcessFixtures.ParallelSplitAndJoin"/> with <c>left</c> completed normally and <c>right</c> still running.</summary>
    private static (BpmnGraph Graph, BpmnEvaluation LeftDone, BpmnLiveWork Right) LeftCompletedNormally()
    {
        var graph = BuildGraph(ProcessFixtures.ParallelSplitAndJoin());
        var right = new BpmnLiveWork("right", null, "h-right");
        var leftDone = Complete(graph, Start(graph), "left", "h-left", [right]);
        leftDone.Continuation.ShouldBe(Defer);

        return (graph, leftDone, right);
    }

    [Fact]
    public void A_second_completion_of_work_whose_token_completed_normally_still_throws()
    {
        var (graph, leftDone, right) = LeftCompletedNormally();

        Should.Throw<BpmnExecutionException>(() => Complete(graph, leftDone, "left", "h-left", [right]))
            .Message.ShouldContain("'left'");
    }

    [Fact]
    public void A_second_fault_of_work_whose_token_completed_normally_still_faults_the_scope()
    {
        var (graph, leftDone, right) = LeftCompletedNormally();

        var evaluation = Fault(graph, leftDone, "left", "h-left", [right]);

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.work.faulted");
    }

    [Fact]
    public void A_completion_for_a_binding_no_element_binds_still_throws()
    {
        var (graph, stopped, _) = StopTheInstances();

        Should.Throw<BpmnExecutionException>(() => Complete(graph, stopped, "nobody-binds-this", "h-x", [Cleanup]));
    }

    [Fact]
    public void A_completion_whose_handle_no_teardown_named_still_throws_beside_cancelled_instances()
    {
        var (graph, stopped, instances) = StopTheInstances();

        Should.Throw<BpmnExecutionException>(() =>
            Complete(graph, stopped, "review", "h-review-9", [Cleanup], instances[0].IterationId));
    }
}
