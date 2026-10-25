using System.Text.Json;
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

    private static BpmnEvaluation Persist(BpmnEvaluation evaluation)
    {
        var pruned = evaluation.State.Prune();
        var persisted = JsonSerializer.Deserialize<BpmnExecutionState>(JsonSerializer.Serialize(pruned))
            ?? throw new InvalidOperationException("Pruning did not produce a serializable execution state.");
        return evaluation with { State = persisted };
    }

    private static BpmnLiveWork StartedWork(BpmnEvaluation evaluation, string bindingRef, string handle)
    {
        var start = evaluation.Commands.OfType<BpmnHostCommand.StartWork>().Single(command => command.BindingRef == bindingRef);
        return new BpmnLiveWork(start.BindingRef, start.IterationScope?.IterationId, handle) { WorkTokenId = start.TokenId };
    }

    private static BpmnEvaluation Complete(BpmnGraph graph, BpmnEvaluation prior, string bindingRef, string handle, BpmnLiveWork[] liveWork, string? iterationId = null, string? workTokenId = null) =>
        Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(graph, prior.State, Scope(liveWork), bindingRef, handle, [], iterationId) { WorkTokenId = workTokenId });

    private static BpmnEvaluation Fault(BpmnGraph graph, BpmnEvaluation prior, string bindingRef, string handle, BpmnLiveWork[] liveWork, string? iterationId = null, string? workTokenId = null) =>
        Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(graph, prior.State, Scope(liveWork), bindingRef, handle, "boom") { FaultedIterationId = iterationId, WorkTokenId = workTokenId });

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
        late.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Absorbed.Instance);
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

    private static (BpmnGraph Graph, BpmnEvaluation Retried, BpmnLiveWork FirstPass, BpmnLiveWork CurrentPass, BpmnLiveWork CurrentTimer, BpmnLiveWork Other) RetriedNTimes(int retryCount)
    {
        var graph = BuildGraph(ProcessFixtures.RetriedOnTimeout());
        var current = Persist(Start(graph));
        var firstPass = StartedWork(current, "long", "h-long-1");
        var pass = firstPass;
        var timer = StartedWork(current, "timeout", "h-timeout-1");
        var other = StartedWork(current, "other", "h-other");

        for (var retry = 1; retry <= retryCount; retry++)
        {
            var timedOut = Complete(graph, current, "timeout", timer.Handle, [pass, other], workTokenId: timer.WorkTokenId);

            timedOut.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldHaveSingleItem().Handle.ShouldBe(pass.Handle);
            current = Persist(timedOut);
            pass = StartedWork(timedOut, "long", $"h-long-{retry + 1}");
            timer = StartedWork(timedOut, "timeout", $"h-timeout-{retry + 1}");
        }

        return (graph, current, firstPass, pass, timer, other);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pruning_retry_boundaries_retains_each_late_callback_until_absorbed_then_removes_its_token(bool asFault)
    {
        const int retryCount = 5;
        var (graph, retried, firstPass, currentPass, currentTimer, other) = RetriedNTimes(retryCount);
        var firstCanceledToken = retried.State.Tokens.Single(token => token.StoppedHandle == firstPass.Handle);

        retried.State.Tokens.Count(token => token.StoppedHandle is not null).ShouldBe(retryCount);
        retried.State.Tokens.ShouldContain(token => token.StoppedHandle == firstPass.Handle);

        var late = asFault
            ? Fault(graph, retried, "long", firstPass.Handle, [currentPass, currentTimer, other], workTokenId: firstPass.WorkTokenId)
            : Complete(graph, retried, "long", firstPass.Handle, [currentPass, currentTimer, other], workTokenId: firstPass.WorkTokenId);
        ShouldHaveBeenAbsorbed(late, retried, firstPass.Handle);

        var afterAbsorption = Persist(late);
        afterAbsorption.State.Tokens.ShouldNotContain(token => token.TokenId == firstCanceledToken.TokenId);
        afterAbsorption.State.Tokens.Count(token => token.StoppedHandle is not null).ShouldBe(retryCount - 1);
        var currentPassTokenId = afterAbsorption.State.ActiveWork.Single(work => work.NodeId == currentPass.BindingRef).TokenId;
        afterAbsorption.State.Tokens.ShouldContain(token => token.TokenId == currentPassTokenId && token.Status == BpmnTokenStatus.AwaitingChild);

        var currentDone = Complete(graph, afterAbsorption, "long", currentPass.Handle, [currentTimer, other], workTokenId: currentPass.WorkTokenId);
        currentDone.Continuation.ShouldBe(Defer);
        currentDone.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldHaveSingleItem().Handle.ShouldBe(currentTimer.Handle);
        currentDone.State.ActiveWork.Select(work => work.NodeId).ShouldBe(["other"]);
        var progressed = Persist(currentDone);

        if (asFault)
        {
            Should.Throw<BpmnExecutionException>(() => Fault(graph, progressed, "long", firstPass.Handle, [other], workTokenId: firstPass.WorkTokenId));
        }
        else
        {
            Should.Throw<BpmnExecutionException>(() => Complete(graph, progressed, "long", firstPass.Handle, [other], workTokenId: firstPass.WorkTokenId));
        }
    }

    [Fact]
    public void Pruning_each_retry_and_delivering_each_late_callback_keeps_persisted_token_count_stable()
    {
        const int retryCount = 120;
        var graph = BuildGraph(ProcessFixtures.RetriedOnTimeout());
        var current = Persist(Start(graph));
        var retainedTokenCount = current.State.Tokens.Count;
        var pass = StartedWork(current, "long", "h-loop-long-1");
        var timer = StartedWork(current, "timeout", "h-loop-timeout-1");
        var other = StartedWork(current, "other", "h-other");

        for (var retry = 1; retry <= retryCount; retry++)
        {
            var timedOut = Complete(graph, current, "timeout", timer.Handle, [pass, other], workTokenId: timer.WorkTokenId);
            var nextPass = StartedWork(timedOut, "long", $"h-loop-long-{retry + 1}");
            var nextTimer = StartedWork(timedOut, "timeout", $"h-loop-timeout-{retry + 1}");
            var stoppedToken = timedOut.State.Tokens.Single(token => token.StoppedHandle == pass.Handle);
            var afterTimeout = Persist(timedOut);

            afterTimeout.State.Tokens.Single(token => token.TokenId == stoppedToken.TokenId).StoppedHandle.ShouldBe(pass.Handle);
            afterTimeout.State.Tokens.Count.ShouldBe(retainedTokenCount + 1);

            var late = retry % 2 == 0
                ? Fault(graph, afterTimeout, "long", pass.Handle, [nextPass, nextTimer, other], workTokenId: pass.WorkTokenId)
                : Complete(graph, afterTimeout, "long", pass.Handle, [nextPass, nextTimer, other], workTokenId: pass.WorkTokenId);
            late.Continuation.ShouldBe(Defer);
            late.State.Tokens.Single(token => token.TokenId == stoppedToken.TokenId).StoppedHandle.ShouldBeNull();

            current = Persist(late);
            current.State.Tokens.Count.ShouldBe(retainedTokenCount);
            current.State.Tokens.ShouldNotContain(token => token.TokenId == stoppedToken.TokenId);
            (current.State.Diagnostics.Count <= BpmnExecutionState.DiagnosticsCap).ShouldBeTrue();
            pass = nextPass;
            timer = nextTimer;
        }

        current.State.Diagnostics.Count.ShouldBe(BpmnExecutionState.DiagnosticsCap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pruning_an_event_gateway_loser_retains_it_until_absorbed_without_stopping_the_winner(bool asFault)
    {
        var definition = new BpmnProcessBuilder("event-gateway-pruned-loser")
            .StartEvent("start")
            .EventBasedGateway("race")
            .IntermediateCatchEvent("winner-catch", new BpmnEventDefinition(BpmnEventDefinitionTypes.Message), bindingRef: "winner-catch-work")
            .IntermediateCatchEvent("loser-catch", new BpmnEventDefinition(BpmnEventDefinitionTypes.Signal), bindingRef: "loser-catch-work")
            .Task("winner-work", bindingRef: "winner-work")
            .EndEvent("end")
            .Connect("start", "race")
            .Connect("race", "winner-catch")
            .Connect("race", "loser-catch")
            .Connect("winner-catch", "winner-work")
            .Connect("loser-catch", "end")
            .Connect("winner-work", "end")
            .Build();
        var graph = BuildGraph(definition);
        var started = Start(graph);
        var winnerCatch = StartedWork(started, "winner-catch-work", "h-winner-catch");
        var loserCatch = StartedWork(started, "loser-catch-work", "h-loser-catch");
        var stoppedTokenId = loserCatch.WorkTokenId!;

        var winnerCaught = Complete(graph, started, winnerCatch.BindingRef, winnerCatch.Handle, [loserCatch], workTokenId: winnerCatch.WorkTokenId);
        winnerCaught.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>()
            .ShouldHaveSingleItem().ShouldBe(new BpmnHostCommand.CancelWorkSubtree(
                loserCatch.Handle, "loser-catch", BpmnInterpreter.EventGatewayRaceCancellationReason));
        winnerCaught.State.Races.ShouldHaveSingleItem().Resolved.ShouldBeTrue();
        winnerCaught.State.Tokens.ShouldContain(token => token.TokenId == stoppedTokenId
            && token.Status == BpmnTokenStatus.Canceled
            && token.StoppedHandle == loserCatch.Handle);
        var winnerWork = StartedWork(winnerCaught, "winner-work", "h-winner-work");

        var persisted = Persist(winnerCaught);
        persisted.State.Tokens.ShouldContain(token => token.TokenId == stoppedTokenId
            && token.Status == BpmnTokenStatus.Canceled
            && token.StoppedHandle == loserCatch.Handle,
            "pruning must retain the canceled race member while its stopped handle can still report");
        persisted.State.ActiveWork.ShouldContain(work => work.TokenId == winnerWork.WorkTokenId);

        var late = asFault
            ? Fault(graph, persisted, loserCatch.BindingRef, loserCatch.Handle, [winnerWork], workTokenId: loserCatch.WorkTokenId)
            : Complete(graph, persisted, loserCatch.BindingRef, loserCatch.Handle, [winnerWork], workTokenId: loserCatch.WorkTokenId);
        ShouldHaveBeenAbsorbed(late, persisted, loserCatch.Handle);
        if (asFault)
            late.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Absorbed.Instance);

        var afterAbsorption = Persist(late);
        afterAbsorption.State.Tokens.ShouldNotContain(token => token.TokenId == stoppedTokenId);
        afterAbsorption.State.ActiveWork.ShouldContain(work => work.TokenId == winnerWork.WorkTokenId);

        var winnerDone = Complete(graph, afterAbsorption, winnerWork.BindingRef, winnerWork.Handle, [], workTokenId: winnerWork.WorkTokenId);
        winnerDone.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_late_report_into_a_terminated_scope_is_absorbed_whether_or_not_the_host_still_lists_what_it_is_tearing_down(
        bool asFault,
        bool secondPassStillListed)
    {
        // The other branch terminates while the second pass runs. The terminated scope is checked before anything
        // else, so the second pass still sitting on the first pass's slot in the snapshot cannot turn the report
        // into a refusal; the scope only repeats the teardowns the snapshot still lists.
        var graph = BuildGraph(ProcessFixtures.RetriedOnTimeout(otherTerminates: true));
        var retried = Complete(graph, Start(graph), "timeout", "h-timeout-1", [FirstPass, OtherUnit]);
        var terminated = Complete(graph, retried, "other", OtherUnit.Handle, [SecondPass, SecondTimer]);
        terminated.State.Terminated.ShouldBeTrue();
        BpmnLiveWork[] live = secondPassStillListed ? [SecondPass, SecondTimer] : [];

        var late = asFault
            ? Fault(graph, terminated, "long", FirstPass.Handle, live)
            : Complete(graph, terminated, "long", FirstPass.Handle, live);

        late.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        if (asFault)
            late.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Absorbed.Instance);
        else
            late.ErrorDisposition.ShouldBeNull("a late completion carries no failure disposition");
        late.Commands.ShouldBe(secondPassStillListed
            ?
            [
                new BpmnHostCommand.CancelWorkSubtree(SecondPass.Handle, "Long", BpmnInterpreter.TerminateStopReason),
                new BpmnHostCommand.CancelWorkSubtree(SecondTimer.Handle, "Timeout", BpmnInterpreter.TerminateStopReason)
            ]
            : []);
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

    // --- A multi-instance slot re-used by a retry loop ------------------------------------------------

    private static BpmnLiveWork[] InstancesOf(BpmnEvaluation evaluation, string pass) =>
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().Where(start => start.BindingRef == "review")
            .Select((start, index) => new BpmnLiveWork("review", start.IterationScope!.IterationId, $"h-review-{pass}-{index}"))
            .ToArray();

    /// <summary>
    /// <see cref="ProcessFixtures.MultiInstanceRetriedOnTimeout"/> after its first timeout: the first pass's
    /// instances torn down, the second pass's started under new iteration ids, and everything the host now runs.
    /// </summary>
    private static (BpmnGraph Graph, BpmnEvaluation Retried, BpmnLiveWork[] FirstPass, BpmnLiveWork[] Live) RetriedInstancesOnce(int cardinality)
    {
        var graph = BuildGraph(ProcessFixtures.MultiInstanceRetriedOnTimeout(cardinality));
        var started = Start(graph);
        var firstPass = InstancesOf(started, "1");

        var retried = Complete(graph, started, "timeout", "h-timeout-1", [.. firstPass, OtherUnit]);
        retried.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().Select(teardown => teardown.Handle)
            .ShouldBe(firstPass.Select(instance => instance.Handle));
        var secondPass = InstancesOf(retried, "2");
        secondPass.Length.ShouldBe(cardinality);

        return (graph, retried, firstPass, [.. secondPass, SecondTimer, OtherUnit]);
    }

    [Fact]
    public void A_late_completion_of_an_earlier_passes_instance_is_absorbed_while_several_later_instances_run()
    {
        var (graph, retried, firstPass, live) = RetriedInstancesOnce(cardinality: 2);

        var late = Complete(graph, retried, "review", firstPass[0].Handle, live, firstPass[0].IterationId);

        ShouldHaveBeenAbsorbed(late, retried, firstPass[0].Handle);
    }

    [Fact]
    public void A_late_fault_of_an_earlier_passes_instance_is_absorbed_while_several_later_instances_run()
    {
        var (graph, retried, firstPass, live) = RetriedInstancesOnce(cardinality: 2);

        var late = Fault(graph, retried, "review", firstPass[0].Handle, live, firstPass[0].IterationId);

        ShouldHaveBeenAbsorbed(late, retried, firstPass[0].Handle);
        late.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Absorbed.Instance, "the earlier pass had already been stopped");
    }

    [Fact]
    public void A_completion_with_an_earlier_passes_iteration_id_is_not_handed_to_the_later_passes_only_instance()
    {
        // No teardown named this handle, so it is not late. The iteration id names no live instance, and the
        // binding's only record belongs to the second pass: resolving to it would complete that pass's loop.
        var (graph, retried, firstPass, live) = RetriedInstancesOnce(cardinality: 1);

        Should.Throw<BpmnExecutionException>(() => Complete(graph, retried, "review", "h-never-started", live, firstPass[0].IterationId));
    }

    [Fact]
    public void A_fault_with_an_earlier_passes_iteration_id_is_not_caught_on_the_later_passes_only_instance()
    {
        var (graph, retried, firstPass, live) = RetriedInstancesOnce(cardinality: 1);

        var evaluation = Fault(graph, retried, "review", "h-never-started", live, firstPass[0].IterationId);

        evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance, "the second pass did not fail, so its error boundary must not fire");
        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.work.faulted");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_report_with_no_iteration_id_is_not_handed_to_the_only_instance_the_host_runs_under_another_handle(bool asFault)
    {
        // Without an iteration id the binding's only record answers, and that record is the second pass's
        // instance, which the snapshot lists under its own handle. Acting on it would catch a fault on it, or
        // finish its loop, for a report it never made.
        var (graph, retried, _, live) = RetriedInstancesOnce(cardinality: 1);

        Should.Throw<BpmnExecutionException>(() => asFault
                ? Fault(graph, retried, "review", "h-never-started", live)
                : Complete(graph, retried, "review", "h-never-started", live))
            .Message.ShouldContain(live[0].Handle);
    }

    [Fact]
    public void A_fault_under_an_unknown_handle_for_a_running_instances_iteration_id_throws_instead_of_failing_that_instance()
    {
        var (graph, retried, _, live) = RetriedInstancesOnce(cardinality: 2);
        var running = live[0];

        Should.Throw<BpmnExecutionException>(() => Fault(graph, retried, "review", "h-never-started", live, running.IterationId))
            .Message.ShouldContain(running.Handle);
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
