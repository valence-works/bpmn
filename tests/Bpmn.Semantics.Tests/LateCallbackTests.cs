using Bpmn.Model;
using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Callbacks into a running scope that no work record matches, driven against the interpreter directly.
/// <para>
/// A host can still report work after the scope stopped it: a completion that raced the teardown an interrupt
/// issued, or work an earlier version started beside a same-evaluation stop because it left that StartWork on
/// the command list. Both leave the same trace, the Canceled token the work belonged to, and both are absorbed.
/// A callback with no such trace is a host breaking the contract, and it still fails loudly. The reference host
/// cannot show either: it drops a callback for work it already stopped before the interpreter sees it.
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

    /// <summary>Asserts that <paramref name="late"/> changed nothing but its diagnostic, and asked the host for nothing.</summary>
    private static void ShouldHaveBeenAbsorbed(BpmnEvaluation late, BpmnEvaluation prior)
    {
        late.Continuation.ShouldBe(Defer, "the scope is still running and the callback changes nothing about it");
        late.Commands.ShouldBeEmpty();
        late.State.Tokens.ShouldBe(prior.State.Tokens, "no token moves for work the scope already stopped");
        late.State.ActiveWork.ShouldBe(prior.State.ActiveWork);
        late.State.Diagnostics.Last().Kind.ShouldBe(BpmnDiagnosticKind.Canceled);
    }

    // --- Absorbed: the Canceled token is there ----------------------------------------------------------

    [Fact]
    public void A_completion_that_raced_an_interrupting_boundary_teardown_is_absorbed()
    {
        var graph = BuildGraph(ProcessFixtures.TimerBoundary());
        var fired = Complete(graph, Start(graph), "timeout", "h-timeout", [new BpmnLiveWork("long", null, "h-long")]);
        fired.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldHaveSingleItem().Handle.ShouldBe("h-long");

        var late = Complete(graph, fired, "long", "h-long", [new BpmnLiveWork("escalate", null, "h-escalate")]);

        ShouldHaveBeenAbsorbed(late, fired);
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
    public void A_completion_of_a_torn_down_multi_instance_instance_is_absorbed_by_its_iteration_id()
    {
        var (graph, stopped, instances) = StopTheInstances();

        var late = Complete(graph, stopped, "review", instances[0].Handle, [Cleanup], instances[0].IterationId);

        ShouldHaveBeenAbsorbed(late, stopped);
    }

    /// <summary>
    /// The scope <see cref="ProcessFixtures.SplitIntoWorkAndOwnScopeEscalation"/> holds after it started: the
    /// long-running task's token Canceled with no work record, the event subprocess body running. An earlier version
    /// reached the same tokens and records but also returned the task's StartWork, so a host may be running it.
    /// </summary>
    private static (BpmnGraph Graph, BpmnEvaluation Started, BpmnLiveWork Body) StartedSplitIntoWorkAndOwnScopeEscalation()
    {
        var (parent, body) = ProcessFixtures.SplitIntoWorkAndOwnScopeEscalation();
        var graph = BuildGraph(parent, body);
        var started = Start(graph);
        started.State.Tokens.Single(token => token.AtElementId == "LongRunning").Status.ShouldBe(BpmnTokenStatus.Canceled);
        started.State.ActiveWork.ShouldNotContain(work => work.NodeId == "long-running");

        return (graph, started, new BpmnLiveWork("on-overdue", null, "h-body"));
    }

    [Fact]
    public void A_completion_of_work_an_earlier_version_started_beside_a_same_evaluation_stop_is_absorbed()
    {
        var (graph, started, body) = StartedSplitIntoWorkAndOwnScopeEscalation();

        var late = Complete(graph, started, "long-running", "h-long", [body]);

        ShouldHaveBeenAbsorbed(late, started);
    }

    [Fact]
    public void A_fault_of_work_an_earlier_version_started_beside_a_same_evaluation_stop_is_absorbed()
    {
        var (graph, started, body) = StartedSplitIntoWorkAndOwnScopeEscalation();

        var late = Fault(graph, started, "long-running", "h-long", [body]);

        ShouldHaveBeenAbsorbed(late, started);
    }

    // --- Still loud: nothing accounts for the callback --------------------------------------------------

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

    // In the two below, both instances are Canceled, so only the binding and iteration-id checks keep them loud.

    [Fact]
    public void A_completion_for_a_binding_no_element_binds_still_throws()
    {
        var (graph, stopped, _) = StopTheInstances();

        Should.Throw<BpmnExecutionException>(() => Complete(graph, stopped, "nobody-binds-this", "h-x", [Cleanup]));
    }

    [Fact]
    public void A_completion_whose_iteration_id_names_no_cancelled_instance_still_throws()
    {
        var (graph, stopped, _) = StopTheInstances();

        Should.Throw<BpmnExecutionException>(() =>
            Complete(graph, stopped, "review", "h-review-9", [Cleanup], BpmnInterpreter.MultiInstanceIterationIdPrefix + "token:999"));
    }
}
