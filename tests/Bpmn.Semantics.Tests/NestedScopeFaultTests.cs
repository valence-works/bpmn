using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Faults, and completions, of work inside a nested scope.
/// <para>
/// A nested scope's invocation correlation is the correlation of the work that started it, so its token id names
/// a token of the <i>enclosing</i> scope. Token ids are scope-local (<c>token:N</c>), and the fault path looked
/// the failed unit up by that id. In a nested scope it therefore found nothing, and no catcher there ever ran,
/// unless the two scopes' ids happened to coincide. How many elements precede the subprocess in the parent
/// decides the parent's id, which is what the padded theories vary. A coincidence with a token the nested scope
/// had already consumed was worse: the fault was absorbed as late, and simply vanished.
/// </para>
/// </summary>
public sealed class NestedScopeFaultTests
{
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    private static readonly BpmnErrorDisposition CaughtByBoundary = new BpmnErrorDisposition.Caught("Failed", ErrorCode: null);

    /// <summary>
    /// Runs <paramref name="body"/> as the subprocess of <see cref="ProcessFixtures.SubProcessAfterPads"/>, past its
    /// pad tasks, and returns the root and the nested scope. With no <paramref name="padTasks"/> the body runs as the
    /// root itself, and both are the same scope.
    /// </summary>
    private static (InMemoryProcessInstance Root, InMemoryProcessInstance Scope) Run(
        BpmnProcessDefinition body,
        int? padTasks,
        BpmnProcessDefinition? handler = null)
    {
        var nested = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["body"] = body };
        if (handler is not null)
            nested["on-error"] = handler;

        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions { NestedProcesses = nested });
        if (padTasks is not { } pads)
        {
            var alone = host.Start(body);
            return (alone, alone);
        }

        var root = host.Start(ProcessFixtures.SubProcessAfterPads(pads));
        for (var index = 0; index < pads; index++)
            root.CompleteWork($"pad{index}");

        return (root, root.Children.ShouldHaveSingleItem());
    }

    /// <summary>Whether the parent's token parked on the subprocess and the nested scope's only waiting token share an id.</summary>
    private static bool IdsCoincide(InMemoryProcessInstance root, InMemoryProcessInstance nested) =>
        root.State.ActiveWork.Single().TokenId == nested.State.ActiveWork.Single().TokenId;

    // --- Catchers inside a nested scope --------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void An_error_boundary_inside_a_subprocess_catches_however_many_elements_precede_the_subprocess(int padTasks)
    {
        var (root, nested) = Run(ProcessFixtures.RiskyActivity(withErrorBoundary: true), padTasks);
        IdsCoincide(root, nested).ShouldBe(padTasks == 0, "the theory must cover both the coincidence and its absence");

        nested.FaultWork("risky", "boom");

        nested.Transcript[^1].Disposition.ShouldBe(CaughtByBoundary, nested.Transcript.ToString());
        nested.PendingWork.Select(work => work.BindingRef).ShouldBe(["recover"]);
        root.IsFaulted.ShouldBeFalse(root.Transcript.ToString());

        nested.CompleteWork("recover");
        root.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName, root.Transcript.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void An_error_event_subprocess_inside_a_subprocess_catches_however_many_elements_precede_the_subprocess(int padTasks)
    {
        var (body, handler) = ProcessFixtures.RiskyActivityWithErrorEventSubprocess();
        var (root, nested) = Run(body, padTasks, handler);
        IdsCoincide(root, nested).ShouldBe(padTasks == 0, "the theory must cover both the coincidence and its absence");

        nested.FaultWork("risky", "boom");

        nested.Transcript[^1].Disposition.ShouldBe(new BpmnErrorDisposition.Caught("OnError", ErrorCode: null), nested.Transcript.ToString());
        var handlerScope = nested.Children.ShouldHaveSingleItem();
        handlerScope.PendingWork.Select(work => work.BindingRef).ShouldBe(["handle"]);

        handlerScope.CompleteWork("handle");
        nested.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName, nested.Transcript.ToString());
        root.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName, root.Transcript.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_parallel_multi_instance_fault_tears_down_exactly_the_other_instances(int? padTasks)
    {
        var (_, scope) = Run(ProcessFixtures.ParallelMultiInstanceWithErrorBoundary(), padTasks);
        var instances = scope.PendingWork.Where(work => work.BindingRef == "review").ToArray();
        instances.Length.ShouldBe(3);

        scope.FaultWork(instances[2], "boom");

        var evaluation = scope.Transcript[^1];
        evaluation.Disposition.ShouldBe(CaughtByBoundary, scope.Transcript.ToString());
        evaluation.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().Select(teardown => teardown.Handle)
            .ShouldBe([instances[0].Handle, instances[1].Handle], ignoreOrder: true,
                customMessage: "the instances still running are torn down; the one that failed has already gone");
        scope.PendingWork.Select(work => work.BindingRef).ShouldBe(["recover"]);
        scope.State.ActiveWork.Select(work => work.NodeId).ShouldBe(["recover"], "no record of an instance survives the catch");
    }

    // --- The enclosing scope's token id, driven directly ---------------------------------------------------

    private static BpmnGraph BuildGraph(BpmnProcessDefinition definition) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full);

    /// <summary>
    /// A nested scope invoked by work whose parked token in the enclosing scope is <paramref name="enclosingTokenId"/>,
    /// with the correlation an enclosing scope issues for a subprocess. No state is pruned, as a host need not prune.
    /// </summary>
    private static BpmnHostSnapshot NestedScope(string enclosingTokenId, params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root("scope-2") with
        {
            HasEnclosingScope = true,
            LiveWork = liveWork,
            InvocationCorrelation = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BpmnInterpreter.TokenIdCorrelationKey] = enclosingTokenId,
                [BpmnInterpreter.ElementIdCorrelationKey] = "Sub",
                [BpmnInterpreter.SchedulingCauseCorrelationKey] = $"element:{BpmnElementTypes.SubProcess}",
                [BpmnInterpreter.BindingRefCorrelationKey] = "body"
            }
        };

    /// <summary>
    /// Starts <paramref name="graph"/> as a nested scope whose enclosing token id coincides with the token
    /// <paramref name="collideWith"/> picks from the start's own work. Ids are a pure function of the state, and the
    /// correlation does not reach seeding here, so a probe start predicts them.
    /// </summary>
    private static (BpmnEvaluation Started, string EnclosingTokenId) StartWithCoincidingId(
        BpmnGraph graph,
        Func<IReadOnlyList<BpmnHostCommand.StartWork>, string> collideWith)
    {
        var probe = Interpreter.Start(new BpmnStartRequest(graph, null, NestedScope("token:0")));
        var enclosingTokenId = collideWith(probe.Commands.OfType<BpmnHostCommand.StartWork>().ToArray());

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, NestedScope(enclosingTokenId)));
        started.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.TokenId)
            .ShouldBe(probe.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.TokenId));

        return (started, enclosingTokenId);
    }

    [Fact]
    public void A_fault_is_not_absorbed_as_late_when_the_enclosing_token_id_names_a_consumed_token_here()
    {
        var graph = BuildGraph(ProcessFixtures.RiskyActivityAfterPreparation());
        var (started, enclosingTokenId) = StartWithCoincidingId(graph, starts => starts.Single().TokenId);

        var prepared = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, NestedScope(enclosingTokenId), "prep", "h-prep", []));
        var prepToken = prepared.State.Tokens.Single(token => token.TokenId == enclosingTokenId);
        (prepToken.AtElementId, prepToken.Status).ShouldBe(("Prep", BpmnTokenStatus.Consumed), "the precondition: the enclosing id names a consumed token here");

        var evaluation = Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            graph, prepared.State, NestedScope(enclosingTokenId), "risky", "h-risky", "boom"));

        evaluation.ErrorDisposition.ShouldBe(CaughtByBoundary, "the fault belongs to the live risky task, whatever the enclosing scope's ids are");
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["recover"]);
    }

    [Fact]
    public void A_completion_resolves_its_own_instance_when_the_enclosing_token_id_names_another_live_instance()
    {
        var graph = BuildGraph(ProcessFixtures.MultiInstanceByCardinality(3));
        var (started, enclosingTokenId) = StartWithCoincidingId(graph, starts => starts[1].TokenId);
        var instances = started.Commands.OfType<BpmnHostCommand.StartWork>().ToArray();

        var evaluation = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, NestedScope(enclosingTokenId), "review", "h-review-2", [], instances[2].IterationScope!.IterationId));

        evaluation.State.Tokens.Single(token => token.TokenId == instances[2].TokenId).Status.ShouldBe(BpmnTokenStatus.Consumed);
        evaluation.State.Tokens.Single(token => token.TokenId == enclosingTokenId).Status.ShouldBe(BpmnTokenStatus.AwaitingChild,
            "the instance whose id the enclosing scope happens to share is still running");
        evaluation.State.ActiveWork.Select(work => work.TokenId).ShouldBe([instances[0].TokenId, instances[1].TokenId]);
    }

    [Fact]
    public void A_completion_no_record_matches_is_never_resolved_to_the_token_the_enclosing_id_names()
    {
        var graph = BuildGraph(ProcessFixtures.ParallelSplitAndJoin());
        var (started, enclosingTokenId) = StartWithCoincidingId(graph, starts => starts.Single(start => start.BindingRef == "right").TokenId);
        var right = new BpmnLiveWork("right", null, "h-right");
        var leftDone = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, NestedScope(enclosingTokenId, right), "left", "h-left", []));

        // A second report for work that already completed normally names no record. Resolving it to the id the
        // enclosing scope's correlation carries would land it on the live right-hand task, and complete that.
        Should.Throw<BpmnExecutionException>(() => Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
                graph, leftDone.State, NestedScope(enclosingTokenId, right), "left", "h-left", [])))
            .Message.ShouldContain("'left'");
    }

    [Fact]
    public void A_fault_of_one_of_several_live_instances_without_its_iteration_id_throws_rather_than_guess()
    {
        var graph = BuildGraph(ProcessFixtures.ParallelMultiInstanceWithErrorBoundary());
        var started = Interpreter.Start(new BpmnStartRequest(graph, null, BpmnHostSnapshot.Root("scope-1")));
        var live = started.Commands.OfType<BpmnHostCommand.StartWork>()
            .Select((start, index) => new BpmnLiveWork(start.BindingRef, start.IterationScope?.IterationId, $"h-{index}"))
            .ToArray();

        Should.Throw<BpmnExecutionException>(() => Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
                graph, started.State, BpmnHostSnapshot.Root("scope-1") with { LiveWork = live[..2] }, "review", live[2].Handle, "boom")))
            .Message.ShouldContain("'review'");
    }
}
