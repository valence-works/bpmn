using System.Runtime.CompilerServices;
using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Exercises callback identity at the interpreter boundary. Work handles identify host-owned work; the token id
/// echoed from StartWork identifies which invocation the callback belongs to inside this scope.
/// </summary>
public sealed class WorkInvocationIdentityTests
{
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();
    private static readonly BpmnContinuation Defer = BpmnContinuation.Defer.Instance;

    private static BpmnGraph BuildGraph(BpmnProcessDefinition definition) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition, _ => null), BpmnHostCapabilities.Full);

    private static BpmnHostSnapshot Host(params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root("scope-1") with { LiveWork = liveWork };

    private static BpmnHostSnapshot Host(IBpmnVariableReader variables, params BpmnLiveWork[] liveWork) =>
        Host(liveWork) with { Variables = variables };

    private static BpmnEvaluation Start(BpmnGraph graph, IBpmnVariableReader? variables = null) =>
        Interpreter.Start(new BpmnStartRequest(graph, null, variables is null ? Host() : Host(variables)));

    private static BpmnLiveWork Live(BpmnHostCommand.StartWork work, string handle) =>
        new(work.BindingRef, work.IterationScope?.IterationId, handle) { WorkTokenId = work.TokenId };

    private static BpmnLiveWork Live(Invocation work) =>
        new(work.BindingRef, work.IterationId, work.Handle) { WorkTokenId = work.TokenId };

    private static BpmnEvaluation Complete(
        BpmnGraph graph,
        BpmnExecutionState state,
        string bindingRef,
        string handle,
        IEnumerable<BpmnLiveWork> liveWork,
        string? workTokenId = null,
        string? iterationId = null,
        IBpmnVariableReader? variables = null)
    {
        var host = Host(liveWork.ToArray());
        if (variables is not null)
            host = host with { Variables = variables };
        return Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, state, host, bindingRef, handle, [], iterationId) { WorkTokenId = workTokenId });
    }

    private static BpmnFaultEvaluation Fault(
        BpmnGraph graph,
        BpmnExecutionState state,
        string bindingRef,
        string handle,
        IEnumerable<BpmnLiveWork> liveWork,
        string? workTokenId = null,
        string? iterationId = null) =>
        Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            graph, state, Host(liveWork.ToArray()), bindingRef, handle, "boom")
        {
            FaultedIterationId = iterationId,
            WorkTokenId = workTokenId
        });

    private static BpmnEvaluation Signal(
        BpmnGraph graph,
        BpmnExecutionState state,
        string bindingRef,
        string handle,
        IEnumerable<BpmnLiveWork> liveWork,
        string code = "raise",
        string? workTokenId = null,
        string? iterationId = null) =>
        Interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            graph, state, Host(liveWork.ToArray()), bindingRef, handle, BpmnInterpreter.EscalationSignalCode,
            Payload(code), iterationId) { WorkTokenId = workTokenId });

    private static JsonElement Payload(string code) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = code });

    private static Invocation InvocationOf(BpmnHostCommand.StartWork start, string handle) =>
        new(start.BindingRef, start.TokenId, start.IterationScope?.IterationId, handle);

    private static (BpmnGraph Graph, BpmnEvaluation State, Invocation[] Work) StartTwoSameSlot(
        BpmnProcessDefinition? definition = null)
    {
        var graph = BuildGraph(definition ?? TwoArrivalsAtOneWorkHost());
        var started = Start(graph);
        var prep = started.Commands.OfType<BpmnHostCommand.StartWork>().ToDictionary(work => work.BindingRef, StringComparer.Ordinal);
        prep.Keys.OrderBy(key => key, StringComparer.Ordinal).ShouldBe(["prep-a", "prep-b"]);

        var prepA = Live(prep["prep-a"], "h-prep-a");
        var prepB = Live(prep["prep-b"], "h-prep-b");
        var firstArrival = Complete(graph, started.State, "prep-a", prepA.Handle, [prepB], prepA.WorkTokenId);
        var firstWork = firstArrival.Commands.OfType<BpmnHostCommand.StartWork>().Single(work => work.BindingRef == "work");
        var first = InvocationOf(firstWork, "h-work-a");
        var secondArrival = Complete(graph, firstArrival.State, "prep-b", prepB.Handle, [Live(first)], prepB.WorkTokenId);
        var secondWork = secondArrival.Commands.OfType<BpmnHostCommand.StartWork>().Single(work => work.BindingRef == "work");
        var second = InvocationOf(secondWork, "h-work-b");

        var invocations = new[] { first, second };
        invocations.Select(work => work.IterationId).ShouldAllBe(iterationId => iterationId == null);
        invocations.Select(work => work.TokenId).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        return (graph, secondArrival, invocations);
    }

    private static BpmnProcessDefinition TwoArrivalsAtOneWorkHost(bool withErrorBoundary = false, bool withEscalationBoundary = false)
    {
        var builder = new BpmnProcessBuilder("same-slot")
            .StartEvent("Start")
            .ParallelGateway("Fork")
            .Task("PrepA", bindingRef: "prep-a")
            .Task("PrepB", bindingRef: "prep-b");

        // Escalation boundaries may only attach to subprocess hosts. Direct interpreter tests can still model
        // their bound work without supplying an actual nested process definition.
        if (withEscalationBoundary)
            builder.SubProcess("Work", bindingRef: "work");
        else
            builder.Task("Work", bindingRef: "work");

        builder
            .Task("Next", bindingRef: "next")
            .EndEvent("End")
            .Connect("Start", "Fork")
            .Connect("Fork", "PrepA")
            .Connect("Fork", "PrepB")
            .Connect("PrepA", "Work")
            .Connect("PrepB", "Work")
            .Connect("Work", "Next")
            .Connect("Next", "End");

        if (withErrorBoundary)
        {
            builder
                .BoundaryEvent("Failure", "Work", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
                .Task("Recover", bindingRef: "recover")
                .EndEvent("EndRecovered")
                .Connect("Failure", "Recover")
                .Connect("Recover", "EndRecovered");
        }

        if (withEscalationBoundary)
        {
            builder
                .BoundaryEvent("Escalated", "Work", ProcessFixtures.Escalation("raise"), interrupting: true)
                .Task("Recover", bindingRef: "recover")
                .EndEvent("EndRecovered")
                .Connect("Escalated", "Recover")
                .Connect("Recover", "EndRecovered");
        }

        return builder.Build();
    }

    [Fact]
    public void Work_token_ids_are_additive_init_only_fields_on_live_work_and_callback_requests()
    {
        var graph = BuildGraph(ProcessFixtures.Linear());
        var host = Host();
        var liveWork = new BpmnLiveWork("work", null, "h-work") { WorkTokenId = "token:1" };
        var (bindingRef, iterationId, handle) = liveWork;
        bindingRef.ShouldBe("work");
        iterationId.ShouldBeNull();
        handle.ShouldBe("h-work");
        liveWork.WorkTokenId.ShouldBe("token:1");
        WorkTokenIdProperty(typeof(BpmnLiveWork)).SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
            .ShouldContain(typeof(IsExternalInit));

        var completed = new BpmnWorkCompletedRequest(graph, null, host, "work", "h-work", []) { WorkTokenId = "token:2" };
        var (_, _, _, completedBinding, completedHandle, _, completedIteration) = completed;
        completedBinding.ShouldBe("work");
        completedHandle.ShouldBe("h-work");
        completedIteration.ShouldBeNull();
        completed.WorkTokenId.ShouldBe("token:2");
        WorkTokenIdProperty(typeof(BpmnWorkCompletedRequest)).SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
            .ShouldContain(typeof(IsExternalInit));

        var faulted = new BpmnWorkFaultedRequest(graph, null, host, "work", "h-work", "boom") { WorkTokenId = "token:3" };
        var (_, _, _, faultedBinding, faultedHandle, faultMessage) = faulted;
        faultedBinding.ShouldBe("work");
        faultedHandle.ShouldBe("h-work");
        faultMessage.ShouldBe("boom");
        faulted.WorkTokenId.ShouldBe("token:3");
        WorkTokenIdProperty(typeof(BpmnWorkFaultedRequest)).SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
            .ShouldContain(typeof(IsExternalInit));

        var signalled = new BpmnWorkSignalledRequest(graph, null, host, "work", "h-work", "scope.signal")
            { WorkTokenId = "token:4" };
        var (_, _, _, signalledBinding, signalledHandle, code, _, signalledIteration) = signalled;
        signalledBinding.ShouldBe("work");
        signalledHandle.ShouldBe("h-work");
        code.ShouldBe("scope.signal");
        signalledIteration.ShouldBeNull();
        signalled.WorkTokenId.ShouldBe("token:4");
        WorkTokenIdProperty(typeof(BpmnWorkSignalledRequest)).SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
            .ShouldContain(typeof(IsExternalInit));
    }

    private static System.Reflection.PropertyInfo WorkTokenIdProperty(Type type) =>
        type.GetProperty("WorkTokenId") ?? throw new InvalidOperationException($"{type.Name} has no WorkTokenId property.");

    private static BpmnValue CollectionValue(params string[] items) =>
        BpmnValue.From(JsonSerializer.SerializeToElement(items), BpmnValueTypes.Any);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Same_binding_work_with_null_iteration_completes_the_reported_token_once(int firstCompletionIndex)
    {
        var (graph, state, work) = StartTwoSameSlot();
        var first = work[firstCompletionIndex];
        var sibling = work[1 - firstCompletionIndex];

        var routed = Complete(graph, state.State, first.BindingRef, first.Handle, [Live(sibling)], first.TokenId);

        routed.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["next"]);
        var next = InvocationOf(routed.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-next-first");
        routed.State.ActiveWork.Select(active => active.TokenId).ShouldContain(sibling.TokenId);
        routed.State.ActiveWork.Select(active => active.TokenId).ShouldContain(next.TokenId);
        routed.State.ActiveWork.Count.ShouldBe(2);
        routed.Continuation.ShouldBe(Defer);

        var beforeReplay = routed.State.ActiveWork.ToArray();
        Should.Throw<BpmnExecutionException>(() => Complete(graph, routed.State, first.BindingRef, first.Handle,
            [Live(sibling), Live(next)], first.TokenId));
        routed.State.ActiveWork.ShouldBe(beforeReplay, "a duplicate terminal callback must not mutate the prior state");

        var nextCompleted = Complete(graph, routed.State, next.BindingRef, next.Handle, [Live(sibling)], next.TokenId);
        nextCompleted.Commands.ShouldBeEmpty();
        nextCompleted.Continuation.ShouldBe(Defer);
        nextCompleted.State.ActiveWork.Select(active => active.TokenId).ShouldBe([sibling.TokenId]);

        var siblingRouted = Complete(graph, nextCompleted.State, sibling.BindingRef, sibling.Handle, [], sibling.TokenId);
        siblingRouted.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["next"]);
        var nextSibling = InvocationOf(siblingRouted.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-next-sibling");
        var done = Complete(graph, siblingRouted.State, nextSibling.BindingRef, nextSibling.Handle, [], nextSibling.TokenId);
        done.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
    }

    [Fact]
    public void A_fault_with_the_reported_token_catches_only_that_same_binding_invocation()
    {
        var (graph, state, work) = StartTwoSameSlot(TwoArrivalsAtOneWorkHost(withErrorBoundary: true));
        var failed = work[1];
        var sibling = work[0];

        var caught = Fault(graph, state.State, failed.BindingRef, failed.Handle, [Live(sibling)], failed.TokenId);

        caught.Disposition.ShouldBe(new BpmnErrorDisposition.Caught("Failure", ErrorCode: null));
        caught.Continuation.ShouldBe(Defer);
        caught.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["recover"]);
        caught.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldNotContain(command => command.Handle == sibling.Handle);
        caught.State.ActiveWork.Select(active => active.TokenId).ShouldContain(sibling.TokenId);
        var recovery = InvocationOf(caught.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-recover");
        caught.State.ActiveWork.Select(active => active.TokenId).ShouldContain(recovery.TokenId);
        caught.State.ActiveWork.Count.ShouldBe(2);

        var recovered = Complete(graph, caught.State, recovery.BindingRef, recovery.Handle, [Live(sibling)], recovery.TokenId);
        recovered.Commands.ShouldBeEmpty();
        recovered.Continuation.ShouldBe(Defer);
        var routed = Complete(graph, recovered.State, sibling.BindingRef, sibling.Handle, [], sibling.TokenId);
        var next = InvocationOf(routed.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-next-after-fault");
        var done = Complete(graph, routed.State, next.BindingRef, next.Handle, [], next.TokenId);
        done.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
    }

    [Fact]
    public void An_interrupting_escalation_identifies_the_live_invocation_and_cancels_its_exact_handle()
    {
        var (graph, state, work) = StartTwoSameSlot(TwoArrivalsAtOneWorkHost(withEscalationBoundary: true));
        var signalled = work[1];
        var sibling = work[0];

        var fired = Signal(graph, state.State, signalled.BindingRef, signalled.Handle, work.Select(Live), workTokenId: signalled.TokenId);

        fired.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().Select(command => command.Handle).ShouldBe([signalled.Handle]);
        fired.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["recover"]);
        fired.Continuation.ShouldBe(Defer);
        fired.State.ActiveWork.Select(active => active.TokenId).ShouldContain(sibling.TokenId);
        var recovery = InvocationOf(fired.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-recover");
        fired.State.ActiveWork.Select(active => active.TokenId).ShouldContain(recovery.TokenId);
        fired.State.ActiveWork.Select(active => active.TokenId).ShouldNotContain(signalled.TokenId);
        fired.State.Tokens.ShouldContain(token => token.TokenId == signalled.TokenId && token.Status == BpmnTokenStatus.Canceled);

        var recovered = Complete(graph, fired.State, recovery.BindingRef, recovery.Handle, [Live(sibling)], recovery.TokenId);
        recovered.Continuation.ShouldBe(Defer);
        var routed = Complete(graph, recovered.State, sibling.BindingRef, sibling.Handle, [], sibling.TokenId);
        var next = InvocationOf(routed.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-next-after-signal");
        var done = Complete(graph, routed.State, next.BindingRef, next.Handle, [], next.TokenId);
        done.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
    }

    [Theory]
    [InlineData("", "completion")]
    [InlineData("unknown-token", "completion")]
    [InlineData(" ", "completion")]
    [InlineData("", "fault")]
    [InlineData("unknown-token", "fault")]
    [InlineData(" ", "fault")]
    public void An_unknown_or_blank_terminal_identity_never_falls_back_to_a_same_slot_sibling(
        string reportedTokenId,
        string callback)
    {
        var (graph, state, work) = StartTwoSameSlot();
        var reported = work[0];
        var sibling = work[1];

        Should.Throw<BpmnExecutionException>(() =>
        {
            if (callback == "completion")
                Complete(graph, state.State, reported.BindingRef, reported.Handle, [Live(sibling)], reportedTokenId);
            else
                Fault(graph, state.State, reported.BindingRef, reported.Handle, [Live(sibling)], reportedTokenId);
        });
    }

    [Fact]
    public void An_explicit_identity_must_match_the_reported_binding()
    {
        var graph = BuildGraph(ProcessFixtures.ParallelSplitAndJoin());
        var started = Start(graph);
        var work = started.Commands.OfType<BpmnHostCommand.StartWork>().ToDictionary(item => item.BindingRef, StringComparer.Ordinal);
        var left = InvocationOf(work["left"], "h-left");
        var right = InvocationOf(work["right"], "h-right");

        Should.Throw<BpmnExecutionException>(() => Complete(graph, started.State, left.BindingRef, left.Handle,
            [Live(right)], right.TokenId));
    }

    [Fact]
    public void An_explicit_identity_must_match_the_reported_iteration()
    {
        var graph = BuildGraph(ProcessFixtures.MultiInstanceByCardinality(2));
        var started = Start(graph);
        var work = started.Commands.OfType<BpmnHostCommand.StartWork>().Where(item => item.BindingRef == "review").ToArray();
        work.Length.ShouldBe(2);
        var first = InvocationOf(work[0], "h-review-0");
        var second = InvocationOf(work[1], "h-review-1");

        Should.Throw<BpmnExecutionException>(() => Complete(graph, started.State, first.BindingRef, first.Handle,
            [Live(second)], first.TokenId, second.IterationId));
    }

    [Fact]
    public void An_explicit_multi_instance_identity_can_resolve_without_a_reported_iteration()
    {
        var graph = BuildGraph(ProcessFixtures.MultiInstanceByCardinality(2));
        var started = Start(graph);
        var work = started.Commands.OfType<BpmnHostCommand.StartWork>().Where(item => item.BindingRef == "review").ToArray();
        var first = InvocationOf(work[0], "h-review-0");
        var second = InvocationOf(work[1], "h-review-1");

        var firstDone = Complete(graph, started.State, first.BindingRef, first.Handle, [Live(second)], first.TokenId);

        firstDone.Continuation.ShouldBe(Defer);
        firstDone.State.ActiveWork.Select(active => active.TokenId).ShouldBe([second.TokenId]);
        var done = Complete(graph, firstDone.State, second.BindingRef, second.Handle, [], second.TokenId);
        done.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
    }

    [Fact]
    public void A_loop_reached_by_work_completion_reads_the_collection_from_that_callback_snapshot()
    {
        var graph = BuildGraph(new BpmnProcessBuilder("collection-at-completion")
            .Variable("reviewers")
            .StartEvent("Start")
            .Task("Gate", bindingRef: "gate")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(collectionVariable: "reviewers", itemVariable: "reviewer"))
            .EndEvent("End")
            .ConnectSequence("Start", "Gate", "Review", "End")
            .Build());
        var initial = new TestVariableReader(CollectionValue("initial"));
        var started = Start(graph, initial);
        var gate = InvocationOf(started.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-gate");
        started.Commands.OfType<BpmnHostCommand.StartWork>().Select(work => work.BindingRef).ShouldBe(["gate"]);

        var atCompletion = new TestVariableReader(CollectionValue("ada", "grace"));
        var loop = Complete(graph, started.State, gate.BindingRef, gate.Handle, [], gate.TokenId, variables: atCompletion);
        var iterations = loop.Commands.OfType<BpmnHostCommand.StartWork>().Where(work => work.BindingRef == "review").ToArray();

        iterations.Select(work => work.IterationScope!.Values["reviewer"].Json!.Value.GetString())
            .ShouldBe(["ada", "grace"]);
        loop.Continuation.ShouldBe(Defer);
    }

    [Fact]
    public void Mixed_explicit_and_legacy_live_evidence_for_one_slot_is_rejected()
    {
        var (graph, state, work) = StartTwoSameSlot();
        var reported = work[0];
        var sibling = work[1];
        var mixed = new[]
        {
            Live(sibling),
            new BpmnLiveWork(sibling.BindingRef, sibling.IterationId, "h-work-legacy")
        };

        Should.Throw<BpmnExecutionException>(() => Complete(graph, state.State, reported.BindingRef,
            reported.Handle, mixed, reported.TokenId));
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("iteration")]
    public void An_explicit_snapshot_token_must_match_its_binding_and_iteration(string mismatch)
    {
        var (graph, state, work) = StartTwoSameSlot();
        var reported = work[0];
        var sibling = work[1];
        var live = Live(sibling);
        live = mismatch switch
        {
            "binding" => live with { BindingRef = "different-binding" },
            "iteration" => live with { IterationId = "different-iteration" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, "Unknown mismatch.")
        };

        Should.Throw<BpmnExecutionException>(() => Complete(graph, state.State, reported.BindingRef,
            reported.Handle, [live], reported.TokenId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-iteration")]
    public void An_identityless_live_snapshot_cannot_drop_a_known_binding_with_a_wrong_iteration(string? reportedIteration)
    {
        var graph = BuildGraph(ProcessFixtures.MultiInstanceByCardinality(2));
        var started = Start(graph);
        var work = started.Commands.OfType<BpmnHostCommand.StartWork>().Where(item => item.BindingRef == "review").ToArray();
        var completed = InvocationOf(work[0], "h-completed");
        var sibling = InvocationOf(work[1], "h-sibling");
        var contradicting = new BpmnLiveWork(sibling.BindingRef, reportedIteration, sibling.Handle);

        Should.Throw<BpmnExecutionException>(() => Complete(graph, started.State, completed.BindingRef,
            completed.Handle, [contradicting], completed.TokenId));
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("fault")]
    [InlineData("signal")]
    public void An_identityless_legacy_callback_refuses_an_ambiguous_same_binding_slot(string callback)
    {
        var (graph, state, work) = StartTwoSameSlot(TwoArrivalsAtOneWorkHost(withEscalationBoundary: true));
        var reported = work[0];

        switch (callback)
        {
            case "completion":
                Should.Throw<BpmnExecutionException>(() => Complete(graph, state.State, reported.BindingRef,
                    reported.Handle, [Live(work[1])], workTokenId: null));
                break;
            case "fault":
                Should.Throw<BpmnExecutionException>(() => Fault(graph, state.State, reported.BindingRef,
                    reported.Handle, [Live(work[1])], workTokenId: null));
                break;
            case "signal":
                Should.Throw<BpmnExecutionException>(() => Signal(graph, state.State, reported.BindingRef,
                    reported.Handle, work.Select(Live), workTokenId: null));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(callback), callback, "Unknown callback kind.");
        }
    }

    [Fact]
    public void A_unique_legacy_completion_still_routes()
    {
        var graph = BuildGraph(ProcessFixtures.Linear());
        var started = Start(graph);
        var work = InvocationOf(started.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-work");

        var done = Complete(graph, started.State, work.BindingRef, work.Handle, [], workTokenId: null);

        done.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
    }

    [Fact]
    public void A_unique_legacy_fault_still_reaches_the_fault_path()
    {
        var graph = BuildGraph(ProcessFixtures.RiskyActivity(withErrorBoundary: false));
        var started = Start(graph);
        var work = InvocationOf(started.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-risky");

        var faulted = Fault(graph, started.State, work.BindingRef, work.Handle, [], workTokenId: null);

        faulted.Disposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
        faulted.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.work.faulted");
    }

    [Fact]
    public void A_unique_legacy_signal_still_fires_its_boundary()
    {
        var graph = BuildGraph(SingleEscalationBoundary(interrupting: false));
        var started = Start(graph);
        var work = InvocationOf(started.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-work");

        var fired = Signal(graph, started.State, work.BindingRef, work.Handle, [Live(work) with { WorkTokenId = null }], workTokenId: null);

        fired.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["notify"]);
        fired.State.ActiveWork.Select(active => active.TokenId).ShouldContain(work.TokenId);
    }

    [Fact]
    public void A_legacy_signal_with_no_iteration_does_not_fall_back_to_a_single_multi_instance_iteration()
    {
        var definition = new BpmnProcessBuilder("legacy-mi-signal")
            .StartEvent("Start")
            .SubProcess("Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: 1))
            .EndEvent("End")
            .BoundaryEvent("Escalated", "Review", ProcessFixtures.Escalation("raise"), interrupting: true)
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndRecovered")
            .ConnectSequence("Start", "Review", "End")
            .ConnectSequence("Escalated", "Recover", "EndRecovered")
            .Build();
        var graph = BuildGraph(definition);
        var started = Start(graph);
        var reviewStart = started.Commands.OfType<BpmnHostCommand.StartWork>().Single(work => work.BindingRef == "review");
        var review = InvocationOf(reviewStart, "h-review");
        review.IterationId.ShouldNotBeNull();
        var live = started.Commands.OfType<BpmnHostCommand.StartWork>()
            .Select(work => work.BindingRef == "review"
                ? new BpmnLiveWork(work.BindingRef, work.IterationScope?.IterationId, "h-review")
                : Live(work, $"h-{work.BindingRef}"))
            .ToArray();

        var signal = Signal(graph, started.State, review.BindingRef, review.Handle, live, workTokenId: null);

        signal.Continuation.ShouldBe(Defer);
        signal.Commands.OfType<BpmnHostCommand.StartWork>().ShouldBeEmpty();
        signal.State.ActiveWork.Select(active => active.TokenId).ShouldContain(review.TokenId);
        signal.State.Diagnostics.Last().Kind.ShouldBe(BpmnDiagnosticKind.EscalationLate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_legacy_signal_cannot_target_a_remaining_same_slot_sibling(bool omitSiblingFromSnapshot)
    {
        var (graph, initial, work) = StartTwoSameSlot(TwoArrivalsAtOneWorkHost(withEscalationBoundary: true));
        var retired = work[0];
        var sibling = work[1];
        var routed = Complete(graph, initial.State, retired.BindingRef, retired.Handle, [Live(sibling)], retired.TokenId);
        var next = InvocationOf(routed.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-next");
        var snapshot = omitSiblingFromSnapshot
            ? new[] { Live(next) }
            : new[] { Live(next), Live(sibling) with { WorkTokenId = null } };
        var before = JsonSerializer.Serialize(routed.State);

        Should.Throw<BpmnExecutionException>(() => Signal(graph, routed.State, retired.BindingRef,
            retired.Handle, snapshot));

        JsonSerializer.Serialize(routed.State).ShouldBe(before);
        routed.State.ActiveWork.ShouldContain(active => active.TokenId == sibling.TokenId);
    }

    [Fact]
    public void An_identityless_late_signal_still_fires_a_non_interrupting_boundary()
    {
        var graph = BuildGraph(LateSignalDefinition());
        var started = Start(graph);
        var work = started.Commands.OfType<BpmnHostCommand.StartWork>().ToDictionary(item => item.BindingRef, StringComparer.Ordinal);
        var completed = InvocationOf(work["work"], "h-work");
        var other = InvocationOf(work["other"], "h-other");
        var parked = Complete(graph, started.State, completed.BindingRef, completed.Handle, [Live(other)], completed.TokenId);
        parked.Continuation.ShouldBe(Defer, "the other branch keeps the scope alive after this work completes");

        var lateSignal = Signal(graph, parked.State, completed.BindingRef, completed.Handle,
            [new BpmnLiveWork(other.BindingRef, other.IterationId, other.Handle) { WorkTokenId = null }], workTokenId: null);

        lateSignal.Continuation.ShouldBe(Defer);
        lateSignal.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["notify"]);
    }

    private static BpmnProcessDefinition LateSignalDefinition() =>
        new BpmnProcessBuilder("late-signal")
            .StartEvent("Start")
            .ParallelGateway("Fork")
            .SubProcess("Work", bindingRef: "work")
            .Task("Other", bindingRef: "other")
            .ParallelGateway("Join")
            .EndEvent("End")
            .BoundaryEvent("Escalated", "Work", ProcessFixtures.Escalation("raise"), interrupting: false)
            .Task("Notify", bindingRef: "notify")
            .EndEvent("EndNotified")
            .Connect("Start", "Fork")
            .Connect("Fork", "Work")
            .Connect("Fork", "Other")
            .Connect("Work", "Join")
            .Connect("Other", "Join")
            .Connect("Join", "End")
            .Connect("Escalated", "Notify")
            .Connect("Notify", "EndNotified")
            .Build();

    private static BpmnProcessDefinition SingleEscalationBoundary(bool interrupting) =>
        new BpmnProcessBuilder("single-escalation")
            .StartEvent("Start")
            .SubProcess("Work", bindingRef: "work")
            .EndEvent("End")
            .BoundaryEvent("Escalated", "Work", ProcessFixtures.Escalation("raise"), interrupting)
            .Task("Notify", bindingRef: "notify")
            .EndEvent("EndNotified")
            .ConnectSequence("Start", "Work", "End")
            .ConnectSequence("Escalated", "Notify", "EndNotified")
            .Build();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_terminal_report_is_rejected_while_its_identified_work_is_still_live(bool asFault)
    {
        var graph = BuildGraph(ProcessFixtures.Linear());
        var started = Start(graph);
        var work = InvocationOf(started.Commands.OfType<BpmnHostCommand.StartWork>().Single(), "h-work");

        Should.Throw<BpmnExecutionException>(() => asFault
            ? Fault(graph, started.State, work.BindingRef, work.Handle, [Live(work)], work.TokenId)
            : Complete(graph, started.State, work.BindingRef, work.Handle, [Live(work)], work.TokenId));
    }

    private static BpmnProcessDefinition ThreeParallelTasks() =>
        new BpmnProcessBuilder("parallel-snapshot")
            .StartEvent("Start")
            .ParallelGateway("Fork")
            .Task("Primary", bindingRef: "primary")
            .Task("Left", bindingRef: "left")
            .Task("Right", bindingRef: "right")
            .EndEvent("EndPrimary")
            .EndEvent("EndLeft")
            .EndEvent("EndRight")
            .Connect("Start", "Fork")
            .Connect("Fork", "Primary")
            .Connect("Fork", "Left")
            .Connect("Fork", "Right")
            .Connect("Primary", "EndPrimary")
            .Connect("Left", "EndLeft")
            .Connect("Right", "EndRight")
            .Build();

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-handle")]
    public void Duplicate_live_work_identity_or_handles_are_rejected(string duplicate)
    {
        var graph = BuildGraph(ThreeParallelTasks());
        var started = Start(graph);
        var work = started.Commands.OfType<BpmnHostCommand.StartWork>().ToDictionary(item => item.BindingRef, StringComparer.Ordinal);
        var primary = InvocationOf(work["primary"], "h-primary");
        var left = InvocationOf(work["left"], "h-left");
        var right = InvocationOf(work["right"], "h-right");
        var leftLive = Live(left);
        var rightLive = duplicate switch
        {
            "duplicate-id" => Live(right) with { WorkTokenId = left.TokenId },
            "duplicate-handle" => Live(right) with { Handle = left.Handle },
            _ => throw new ArgumentOutOfRangeException(nameof(duplicate), duplicate, "Unknown duplicate kind.")
        };

        Should.Throw<BpmnExecutionException>(() => Complete(graph, started.State, primary.BindingRef, primary.Handle,
            [leftLive, rightLive], primary.TokenId));
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("whitespace")]
    [InlineData("unknown")]
    [InlineData("sibling")]
    [InlineData("not-live")]
    [InlineData("wrong-handle")]
    public void A_recognized_escalation_must_identify_the_same_active_live_work(string identityCase)
    {
        var (graph, state, work) = StartTwoSameSlot(TwoArrivalsAtOneWorkHost(withEscalationBoundary: true));
        var reported = work[0];
        var sibling = work[1];
        var reportedId = identityCase switch
        {
            "blank" => "",
            "whitespace" => " ",
            "unknown" => "unknown-token",
            "sibling" => sibling.TokenId,
            "not-live" or "wrong-handle" => reported.TokenId,
            _ => throw new ArgumentOutOfRangeException(nameof(identityCase), identityCase, "Unknown identity case.")
        };
        var live = identityCase == "not-live" ? [Live(sibling)] : work.Select(Live).ToArray();
        var handle = identityCase == "wrong-handle" ? $"{reported.Handle}-wrong" : reported.Handle;

        Should.Throw<BpmnExecutionException>(() => Signal(graph, state.State, reported.BindingRef,
            handle, live, workTokenId: reportedId));
    }

    [Fact]
    public void An_unknown_signal_code_remains_a_diagnostic_pass_through_without_identity_resolution()
    {
        var (graph, state, work) = StartTwoSameSlot();

        var ignored = Interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            graph, state.State, Host(), work[0].BindingRef, work[0].Handle, "future.signal", Payload("raise"))
        {
            WorkTokenId = "unknown-token"
        });

        ignored.Continuation.ShouldBe(Defer);
        ignored.Commands.ShouldBeEmpty();
        ignored.State.Diagnostics.Last().Kind.ShouldBe(BpmnDiagnosticKind.EscalationUnhandled);
    }

    private static (BpmnGraph Graph, BpmnEvaluation State, Invocation OldWork, Invocation NewWork, Invocation Other)
        RetryLongWork()
    {
        var graph = BuildGraph(ProcessFixtures.RetriedOnTimeout());
        var started = Start(graph);
        var initial = started.Commands.OfType<BpmnHostCommand.StartWork>().ToDictionary(item => item.BindingRef, StringComparer.Ordinal);
        var oldWork = InvocationOf(initial["long"], "h-long-old");
        var timeout = InvocationOf(initial["timeout"], "h-timeout-old");

        var retried = Complete(graph, started.State, timeout.BindingRef, timeout.Handle, [Live(oldWork)], timeout.TokenId);

        retried.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().Select(command => command.Handle).ShouldBe([oldWork.Handle]);
        var replacementStart = retried.Commands.OfType<BpmnHostCommand.StartWork>().Single(item => item.BindingRef == "long");
        var timerStart = retried.Commands.OfType<BpmnHostCommand.StartWork>().Single(item => item.BindingRef == "timeout");
        var replacement = InvocationOf(replacementStart, "h-long-new");
        var timer = InvocationOf(timerStart, "h-timeout-new");
        return (graph, retried, oldWork, replacement, timer);
    }

    private static (BpmnGraph Graph, BpmnEvaluation Terminated, Invocation Child, Invocation Other)
        TerminateBesideLiveWork()
    {
        var graph = BuildGraph(ProcessFixtures.TerminateBesideASignallingChild());
        var started = Start(graph);
        var work = started.Commands.OfType<BpmnHostCommand.StartWork>().ToDictionary(item => item.BindingRef, StringComparer.Ordinal);
        var child = InvocationOf(work["child"], "h-child");
        var other = InvocationOf(work["other"], "h-other");
        var trigger = InvocationOf(work["trigger"], "h-trigger");
        var terminated = Complete(graph, started.State, trigger.BindingRef, trigger.Handle, [Live(child), Live(other)], trigger.TokenId);
        terminated.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        terminated.State.Terminated.ShouldBeTrue();
        return (graph, terminated, child, other);
    }

    private static BpmnExecutionState DuplicateStoppedHandleOwner(BpmnExecutionState state, string tokenId)
    {
        var stopped = state.Tokens.Single(token => token.TokenId == tokenId);
        return state with { Tokens = [.. state.Tokens, stopped with { TokenId = $"{stopped.TokenId}:duplicate" }] };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stopped_handle_absorbs_only_its_matching_late_identity_without_mutating_replacement(bool asFault)
    {
        var (graph, retried, oldWork, replacement, timer) = RetryLongWork();
        var live = new[] { Live(replacement), Live(timer) };
        var mismatched = replacement.TokenId;

        Should.Throw<BpmnExecutionException>(() => asFault
            ? Fault(graph, retried.State, oldWork.BindingRef, oldWork.Handle, live, mismatched)
            : Complete(graph, retried.State, oldWork.BindingRef, oldWork.Handle, live, mismatched));
        retried.State.Tokens.ShouldContain(token => token.TokenId == oldWork.TokenId && token.StoppedHandle == oldWork.Handle,
            "a rejected identity must not consume the one-shot stopped-handle marker");

        var late = asFault
            ? Fault(graph, retried.State, oldWork.BindingRef, oldWork.Handle, live, oldWork.TokenId)
            : Complete(graph, retried.State, oldWork.BindingRef, oldWork.Handle, live, oldWork.TokenId);
        late.Continuation.ShouldBe(Defer);
        late.Commands.ShouldBeEmpty();
        late.State.ActiveWork.ShouldBe(retried.State.ActiveWork);
        late.State.Tokens.Select(token => token with { StoppedHandle = null })
            .ShouldBe(retried.State.Tokens.Select(token => token with { StoppedHandle = null }));
        late.State.Tokens.ShouldNotContain(token => token.StoppedHandle == oldWork.Handle);
        late.State.Diagnostics.Last().Kind.ShouldBe(BpmnDiagnosticKind.Canceled);
        late.State.ActiveWork.Select(active => active.TokenId).ShouldContain(replacement.TokenId);
        late.State.ActiveWork.Select(active => active.TokenId).ShouldContain(timer.TokenId);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, " ")]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    public void A_stopped_handle_rejects_blank_explicit_identity_without_spending_its_marker(bool asFault, string suppliedId)
    {
        var (graph, retried, oldWork, _, timer) = RetryLongWork();
        var live = new[] { Live(timer) };

        Should.Throw<BpmnExecutionException>(() => asFault
            ? Fault(graph, retried.State, oldWork.BindingRef, oldWork.Handle, live, suppliedId)
            : Complete(graph, retried.State, oldWork.BindingRef, oldWork.Handle, live, suppliedId));

        retried.State.Tokens.ShouldContain(token => token.TokenId == oldWork.TokenId && token.StoppedHandle == oldWork.Handle,
            "a rejected blank identity must not consume the one-shot stopped-handle marker");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_callback_rejects_a_handle_marked_stopped_on_more_than_one_token(bool asFault)
    {
        var (graph, retried, oldWork, _, timer) = RetryLongWork();
        var stateWithDuplicateMarker = DuplicateStoppedHandleOwner(retried.State, oldWork.TokenId);

        Should.Throw<BpmnExecutionException>(() => asFault
            ? Fault(graph, stateWithDuplicateMarker, oldWork.BindingRef, oldWork.Handle, [Live(timer)], oldWork.TokenId)
            : Complete(graph, stateWithDuplicateMarker, oldWork.BindingRef, oldWork.Handle, [Live(timer)], oldWork.TokenId));

        stateWithDuplicateMarker.Tokens.Count(token => token.StoppedHandle == oldWork.Handle).ShouldBe(2);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void A_running_callback_ignores_a_staged_stopped_row_before_mapping_live_work(
        bool stagedRowIsIdless, bool duplicateRow, bool replacementIsIdless)
    {
        var (graph, retried, oldWork, replacement, timer) = RetryLongWork();
        var stagedOldWork = Live(oldWork);
        if (stagedRowIsIdless)
            stagedOldWork = stagedOldWork with { WorkTokenId = null };
        var liveReplacement = Live(replacement);
        if (replacementIsIdless)
            liveReplacement = liveReplacement with { WorkTokenId = null };

        BpmnLiveWork[] live = duplicateRow
            ? [stagedOldWork, stagedOldWork, liveReplacement]
            : [stagedOldWork, liveReplacement];
        var nextTimeout = Complete(graph, retried.State, timer.BindingRef, timer.Handle, live, timer.TokenId);

        nextTimeout.Continuation.ShouldBe(Defer);
        nextTimeout.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>()
            .Select(command => command.Handle).ShouldBe([replacement.Handle]);
        nextTimeout.State.Tokens.ShouldContain(token => token.TokenId == oldWork.TokenId && token.StoppedHandle == oldWork.Handle,
            "resolving a staged snapshot must not spend the earlier stopped marker");
        nextTimeout.State.Tokens.ShouldContain(token => token.TokenId == replacement.TokenId && token.StoppedHandle == replacement.Handle,
            "the timeout routes against the replacement's own live handle");
        nextTimeout.State.Tokens.ShouldNotContain(token => token.TokenId == replacement.TokenId && token.StoppedHandle == oldWork.Handle);
    }

    [Fact]
    public void An_idless_staged_stopped_row_is_not_rebound_to_a_replacement_missing_from_the_snapshot()
    {
        var (graph, retried, oldWork, replacement, timer) = RetryLongWork();
        var stagedOldWork = Live(oldWork) with { WorkTokenId = null };

        var nextTimeout = Complete(graph, retried.State, timer.BindingRef, timer.Handle, [stagedOldWork], timer.TokenId);

        nextTimeout.Continuation.ShouldBe(Defer);
        nextTimeout.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldBeEmpty();
        nextTimeout.Commands.OfType<BpmnHostCommand.StartWork>().ShouldContain(start => start.BindingRef == "long");
        nextTimeout.State.Tokens.ShouldContain(token => token.TokenId == oldWork.TokenId && token.StoppedHandle == oldWork.Handle,
            "the old stopped marker remains available for its late callback");
        nextTimeout.State.Tokens.ShouldContain(token => token.TokenId == replacement.TokenId
            && token.Status == BpmnTokenStatus.Canceled && token.StoppedHandle == null,
            "a missing replacement host row cannot inherit the old handle");
        nextTimeout.State.Tokens.ShouldNotContain(token => token.TokenId == replacement.TokenId && token.StoppedHandle == oldWork.Handle);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("blank")]
    public void A_staged_stopped_row_with_contradictory_identity_is_rejected_before_live_work_mapping(string identity)
    {
        var (graph, retried, oldWork, replacement, timer) = RetryLongWork();
        var conflictingId = identity == "replacement" ? replacement.TokenId : "";
        var stagedOldWork = Live(oldWork) with { WorkTokenId = conflictingId };

        var exception = Should.Throw<BpmnExecutionException>(() => Complete(graph, retried.State,
            timer.BindingRef, timer.Handle, [Live(oldWork), stagedOldWork, Live(replacement)], timer.TokenId));

        exception.Message.ShouldContain("stopped token");
        retried.State.Tokens.ShouldContain(token => token.TokenId == oldWork.TokenId && token.StoppedHandle == oldWork.Handle,
            "a contradictory snapshot must not spend the old stopped marker");
        retried.State.ActiveWork.ShouldContain(work => work.TokenId == replacement.TokenId,
            "rejection must leave the replacement invocation available");
    }

    [Fact]
    public void Snapshot_resolution_rejects_a_stopped_handle_owned_by_multiple_tokens()
    {
        var (graph, retried, oldWork, replacement, timer) = RetryLongWork();
        var stateWithDuplicateMarker = DuplicateStoppedHandleOwner(retried.State, oldWork.TokenId);

        var exception = Should.Throw<BpmnExecutionException>(() => Complete(graph, stateWithDuplicateMarker,
            timer.BindingRef, timer.Handle, [Live(oldWork), Live(replacement)], timer.TokenId));

        exception.Message.ShouldContain("more than one stopped token");
        stateWithDuplicateMarker.Tokens.Count(token => token.StoppedHandle == oldWork.Handle).ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_ended_scope_absorbs_a_callback_before_resolving_its_unknown_identity(bool asFault)
    {
        var (graph, terminated, child, other) = TerminateBesideLiveWork();

        var late = asFault
            ? Fault(graph, terminated.State, child.BindingRef, child.Handle, [Live(child), Live(other)], "unknown-token")
            : Complete(graph, terminated.State, child.BindingRef, child.Handle, [Live(child), Live(other)], "unknown-token");

        late.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        late.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().Select(command => command.Handle)
            .ShouldBe([child.Handle, other.Handle]);
    }

    [Theory]
    [InlineData(false, "duplicate-idless")]
    [InlineData(true, "duplicate-idless")]
    [InlineData(false, "mixed-slot")]
    [InlineData(true, "mixed-slot")]
    [InlineData(false, "duplicate-token-id")]
    [InlineData(true, "duplicate-token-id")]
    public void An_ended_scope_tears_down_distinct_handles_without_resolving_staged_identity(
        bool asFault,
        string snapshotShape)
    {
        var (graph, terminated, child, other) = TerminateBesideLiveWork();
        var repeated = new BpmnLiveWork(other.BindingRef, other.IterationId, "h-repeated");
        BpmnLiveWork[] staged = snapshotShape switch
        {
            "duplicate-idless" =>
            [
                new BpmnLiveWork(child.BindingRef, child.IterationId, "h-staged-a"),
                new BpmnLiveWork(child.BindingRef, child.IterationId, "h-staged-b"),
                repeated,
                repeated
            ],
            "mixed-slot" =>
            [
                new BpmnLiveWork(child.BindingRef, child.IterationId, "h-staged-legacy"),
                new BpmnLiveWork(child.BindingRef, child.IterationId, "h-staged-explicit") { WorkTokenId = "unrelated-token" },
                repeated,
                repeated
            ],
            "duplicate-token-id" =>
            [
                new BpmnLiveWork(child.BindingRef, child.IterationId, "h-staged-a") { WorkTokenId = "reused-token" },
                new BpmnLiveWork(other.BindingRef, other.IterationId, "h-staged-b") { WorkTokenId = "reused-token" },
                repeated,
                repeated
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(snapshotShape), snapshotShape, "Unknown staged snapshot shape.")
        };

        var late = asFault
            ? Fault(graph, terminated.State, child.BindingRef, child.Handle, staged, "unknown-token")
            : Complete(graph, terminated.State, child.BindingRef, child.Handle, staged, "unknown-token");

        late.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        late.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().Select(command => command.Handle)
            .ShouldBe(staged.Select(live => live.Handle).Distinct(StringComparer.Ordinal));
    }

    private sealed record Invocation(string BindingRef, string TokenId, string? IterationId, string Handle);

    private sealed class TestVariableReader : IBpmnVariableReader
    {
        private readonly BpmnValue _reviewers;

        public TestVariableReader(BpmnValue reviewers) => _reviewers = reviewers;

        public bool TryRead(string name, out BpmnValue value)
        {
            if (StringComparer.Ordinal.Equals(name, "reviewers"))
            {
                value = _reviewers;
                return true;
            }

            value = BpmnValue.Absent;
            return false;
        }
    }
}
