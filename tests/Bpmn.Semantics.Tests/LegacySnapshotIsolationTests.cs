using Bpmn.Model;
using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class LegacySnapshotIsolationTests
{
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();
    private static readonly BpmnContinuation Defer = BpmnContinuation.Defer.Instance;

    private enum OtherPath
    {
        End,
        ErrorBoundary,
        WorkEscalationBoundary,
        NonInterruptingEscalation,
        InterruptingEscalationSubprocess,
        Terminate
    }

    private sealed record Invocation(string BindingRef, string TokenId, string? IterationId, string Handle);

    private sealed record RunningFork(BpmnGraph Graph, BpmnEvaluation Evaluation, Invocation[] Work, Invocation Other);

    private sealed record StateSnapshot(BpmnActiveWork[] ActiveWork, BpmnToken[] Tokens);

    private static BpmnGraph BuildGraph(OtherPath otherPath)
    {
        IReadOnlyDictionary<string, BpmnProcessDefinition>? nestedProcesses = null;
        var builder = new BpmnProcessBuilder("legacy-snapshot-isolation")
            .StartEvent("Start")
            .ParallelGateway("Fork")
            .Task("PrepA", bindingRef: "prep-a")
            .Task("PrepB", bindingRef: "prep-b");

        if (otherPath == OtherPath.WorkEscalationBoundary)
            builder.SubProcess("Work", bindingRef: "work");
        else
            builder.Task("Work", bindingRef: "work");

        if (otherPath == OtherPath.NonInterruptingEscalation)
            builder.SubProcess("Other", bindingRef: "other");
        else
            builder.Task("Other", bindingRef: "other");

        builder
            .EndEvent("EndWork")
            .Connect("Start", "Fork")
            .Connect("Fork", "PrepA")
            .Connect("Fork", "PrepB")
            .Connect("Fork", "Other")
            .Connect("PrepA", "Work")
            .Connect("PrepB", "Work")
            .Connect("Work", "EndWork");

        switch (otherPath)
        {
            case OtherPath.End:
                builder.EndEvent("EndOther").Connect("Other", "EndOther");
                break;
            case OtherPath.ErrorBoundary:
                builder
                    .EndEvent("EndOther")
                    .BoundaryEvent("Failure", "Other", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
                    .Task("Recover", bindingRef: "recover")
                    .EndEvent("EndRecovered")
                    .Connect("Other", "EndOther")
                    .ConnectSequence("Failure", "Recover", "EndRecovered");
                break;
            case OtherPath.WorkEscalationBoundary:
                builder
                    .EndEvent("EndOther")
                    .BoundaryEvent("WorkEscalated", "Work", ProcessFixtures.Escalation("raise"), interrupting: false)
                    .Task("Notify", bindingRef: "notify")
                    .EndEvent("EndNotified")
                    .Connect("Other", "EndOther")
                    .ConnectSequence("WorkEscalated", "Notify", "EndNotified");
                break;
            case OtherPath.NonInterruptingEscalation:
                builder
                    .EndEvent("EndOther")
                    .BoundaryEvent("Escalated", "Other", ProcessFixtures.Escalation("raise"), interrupting: false)
                    .Task("Notify", bindingRef: "notify")
                    .EndEvent("EndNotified")
                    .Connect("Other", "EndOther")
                    .ConnectSequence("Escalated", "Notify", "EndNotified");
                break;
            case OtherPath.InterruptingEscalationSubprocess:
                builder
                    .EndEvent("EndOther")
                    .SubProcess("OnEscalation", bindingRef: "on-escalation", triggeredByEvent: true)
                    .Connect("Other", "EndOther");
                nestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
                {
                    ["on-escalation"] = new BpmnProcessBuilder("on-escalation")
                        .StartEvent("EscalationStart", null, ProcessFixtures.Escalation("raise"))
                        .Task("Handle", bindingRef: "handle")
                        .EndEvent("HandlerEnd")
                        .ConnectSequence("EscalationStart", "Handle", "HandlerEnd")
                        .Build()
                };
                break;
            case OtherPath.Terminate:
                builder
                    .EndEvent("Terminate", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Terminate))
                    .Connect("Other", "Terminate");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(otherPath), otherPath, "Unknown other path.");
        }

        var definition = builder.Build();
        return BpmnGraph.Build(
            definition,
            BpmnBoundWork.Derive(definition, bindingRef => nestedProcesses?.GetValueOrDefault(bindingRef)),
            BpmnHostCapabilities.Full);
    }

    private static RunningFork StartWithTwoWorkInvocations(OtherPath otherPath)
    {
        var graph = BuildGraph(otherPath);
        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Host([])));
        var initial = started.Commands.OfType<BpmnHostCommand.StartWork>()
            .ToDictionary(work => work.BindingRef, StringComparer.Ordinal);
        var prepA = InvocationOf(initial["prep-a"], "h-prep-a");
        var prepB = InvocationOf(initial["prep-b"], "h-prep-b");
        var other = InvocationOf(initial["other"], "h-other");

        var afterPrepA = Complete(graph, started.State, prepA, [Live(prepB), Live(other)], prepA.TokenId);
        var workA = InvocationOf(afterPrepA.Commands.OfType<BpmnHostCommand.StartWork>()
            .Single(work => work.BindingRef == "work"), "h-work-a");
        var afterPrepB = Complete(graph, afterPrepA.State, prepB, [Live(workA), Live(other)], prepB.TokenId);
        var workB = InvocationOf(afterPrepB.Commands.OfType<BpmnHostCommand.StartWork>()
            .Single(work => work.BindingRef == "work"), "h-work-b");

        return new RunningFork(graph, afterPrepB, [workA, workB], other);
    }

    private static BpmnLiveWork Live(Invocation work) =>
        new(work.BindingRef, work.IterationId, work.Handle) { WorkTokenId = work.TokenId };

    private static BpmnLiveWork Legacy(Invocation work) =>
        new(work.BindingRef, work.IterationId, work.Handle);

    private static BpmnEvaluation Complete(
        BpmnGraph graph,
        BpmnExecutionState priorState,
        Invocation reported,
        IEnumerable<BpmnLiveWork> liveWork,
        string? workTokenId) =>
        Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, priorState, Host(liveWork), reported.BindingRef, reported.Handle, [], reported.IterationId)
        {
            WorkTokenId = workTokenId
        });

    private static BpmnEvaluation Fault(
        BpmnGraph graph,
        BpmnExecutionState priorState,
        Invocation reported,
        IEnumerable<BpmnLiveWork> liveWork,
        string? workTokenId) =>
        Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            graph, priorState, Host(liveWork), reported.BindingRef, reported.Handle, "boom")
        {
            FaultedIterationId = reported.IterationId,
            WorkTokenId = workTokenId
        });

    private static BpmnEvaluation Signal(
        BpmnGraph graph,
        BpmnExecutionState priorState,
        Invocation reported,
        IEnumerable<BpmnLiveWork> liveWork,
        string? workTokenId) =>
        Interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            graph, priorState, Host(liveWork), reported.BindingRef, reported.Handle,
            BpmnInterpreter.EscalationSignalCode, BpmnInterpreter.EscalationPayload("raise"), reported.IterationId)
        {
            WorkTokenId = workTokenId
        });

    private static BpmnHostSnapshot Host(IEnumerable<BpmnLiveWork> liveWork) =>
        BpmnHostSnapshot.Root("scope-1") with { LiveWork = liveWork.ToArray() };

    private static Invocation InvocationOf(BpmnHostCommand.StartWork work, string handle) =>
        new(work.BindingRef, work.TokenId, work.IterationScope?.IterationId, handle);

    private static StateSnapshot Snapshot(BpmnExecutionState state) =>
        new(state.ActiveWork.ToArray(), state.Tokens.ToArray());

    private static void ShouldMatchSnapshot(BpmnExecutionState state, StateSnapshot snapshot)
    {
        state.ActiveWork.ShouldBe(snapshot.ActiveWork);
        state.Tokens.ShouldBe(snapshot.Tokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unrelated_completion_preserves_work_when_legacy_snapshot_slots_are_ambiguous(bool reportBothWorkHandles)
    {
        var scenario = StartWithTwoWorkInvocations(OtherPath.End);
        var before = Snapshot(scenario.Evaluation.State);
        var ambiguousSnapshot = scenario.Work.Take(reportBothWorkHandles ? 2 : 1).Select(Legacy).ToArray();

        var completed = Complete(scenario.Graph, scenario.Evaluation.State, scenario.Other,
            ambiguousSnapshot, scenario.Other.TokenId);

        ShouldMatchSnapshot(scenario.Evaluation.State, before);
        completed.Continuation.ShouldBe(Defer);
        completed.Commands.ShouldBeEmpty();
        completed.State.ActiveWork.Select(work => work.TokenId).ShouldBe(scenario.Work.Select(work => work.TokenId));
        completed.State.ActiveWork.Select(work => work.NodeId).ShouldAllBe(binding => binding == "work");
        completed.State.Tokens.Where(token => scenario.Work.Any(work => work.TokenId == token.TokenId))
            .ShouldBe(before.Tokens.Where(token => scenario.Work.Any(work => work.TokenId == token.TokenId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unrelated_fault_reaches_its_error_boundary_while_legacy_work_is_ambiguous(bool reportBothWorkHandles)
    {
        var scenario = StartWithTwoWorkInvocations(OtherPath.ErrorBoundary);
        var before = Snapshot(scenario.Evaluation.State);

        var faulted = Fault(scenario.Graph, scenario.Evaluation.State, scenario.Other,
            scenario.Work.Take(reportBothWorkHandles ? 2 : 1).Select(Legacy), scenario.Other.TokenId);

        ShouldMatchSnapshot(scenario.Evaluation.State, before);
        faulted.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught("Failure", ErrorCode: null));
        faulted.Continuation.ShouldBe(Defer);
        faulted.Commands.OfType<BpmnHostCommand.StartWork>().Select(work => work.BindingRef).ShouldBe(["recover"]);
        faulted.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldBeEmpty();
        faulted.State.ActiveWork.Select(work => work.TokenId).ShouldBe(
            [.. scenario.Work.Select(work => work.TokenId), faulted.Commands.OfType<BpmnHostCommand.StartWork>().Single().TokenId]);
        faulted.State.Tokens.Where(token => scenario.Work.Any(work => work.TokenId == token.TokenId))
            .ShouldBe(before.Tokens.Where(token => scenario.Work.Any(work => work.TokenId == token.TokenId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_noninterrupting_signal_on_other_work_fires_alongside_ambiguous_legacy_work(bool reportBothWorkHandles)
    {
        var scenario = StartWithTwoWorkInvocations(OtherPath.NonInterruptingEscalation);
        var before = Snapshot(scenario.Evaluation.State);
        BpmnLiveWork[] liveSnapshot = [.. scenario.Work.Take(reportBothWorkHandles ? 2 : 1).Select(Legacy), Live(scenario.Other)];

        var signalled = Signal(scenario.Graph, scenario.Evaluation.State, scenario.Other,
            liveSnapshot, scenario.Other.TokenId);

        ShouldMatchSnapshot(scenario.Evaluation.State, before);
        signalled.Continuation.ShouldBe(Defer);
        signalled.Commands.OfType<BpmnHostCommand.StartWork>().Select(work => work.BindingRef).ShouldBe(["notify"]);
        signalled.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldBeEmpty();
        signalled.State.ActiveWork.Select(work => work.TokenId).ShouldContain(scenario.Work[0].TokenId);
        signalled.State.ActiveWork.Select(work => work.TokenId).ShouldContain(scenario.Work[1].TokenId);
        signalled.State.ActiveWork.ShouldContain(work => work.TokenId == scenario.Other.TokenId);
        signalled.State.Tokens.Where(token => scenario.Work.Any(work => work.TokenId == token.TokenId))
            .ShouldBe(before.Tokens.Where(token => scenario.Work.Any(work => work.TokenId == token.TokenId)));
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("fault")]
    [InlineData("signal")]
    public void An_identityless_callback_still_refuses_ambiguous_work(string callback)
    {
        var scenario = StartWithTwoWorkInvocations(callback == "signal"
            ? OtherPath.WorkEscalationBoundary
            : OtherPath.End);
        var before = Snapshot(scenario.Evaluation.State);
        var reported = scenario.Work[0];
        BpmnLiveWork[] snapshot = callback == "signal"
            ? [Legacy(scenario.Work[0]), Legacy(scenario.Work[1]), Live(scenario.Other)]
            : [Legacy(scenario.Work[1]), Live(scenario.Other)];

        var exception = Should.Throw<BpmnExecutionException>(() =>
        {
            switch (callback)
            {
                case "completion":
                    Complete(scenario.Graph, scenario.Evaluation.State, reported, snapshot, workTokenId: null);
                    break;
                case "fault":
                    Fault(scenario.Graph, scenario.Evaluation.State, reported, snapshot, workTokenId: null);
                    break;
                case "signal":
                    Signal(scenario.Graph, scenario.Evaluation.State, reported, snapshot, workTokenId: null);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(callback), callback, "Unknown callback.");
            }
        });

        exception.Message.ShouldContain("ambiguous");
        ShouldMatchSnapshot(scenario.Evaluation.State, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_interrupting_event_subprocess_refuses_to_cancel_legacy_work_without_token_mapping(bool reportBothWorkHandles)
    {
        var scenario = StartWithTwoWorkInvocations(OtherPath.InterruptingEscalationSubprocess);
        var before = Snapshot(scenario.Evaluation.State);
        BpmnLiveWork[] liveSnapshot = [.. scenario.Work.Take(reportBothWorkHandles ? 2 : 1).Select(Legacy), Live(scenario.Other)];
        liveSnapshot.Where(work => work.BindingRef == "work").Select(work => work.Handle)
            .ShouldBe(scenario.Work.Take(reportBothWorkHandles ? 2 : 1).Select(work => work.Handle));

        Should.Throw<BpmnExecutionException>(() => Signal(scenario.Graph, scenario.Evaluation.State,
            scenario.Other, liveSnapshot, scenario.Other.TokenId));

        ShouldMatchSnapshot(scenario.Evaluation.State, before);
    }

    [Fact]
    public void Terminate_cancels_each_reported_handle_when_legacy_token_identity_is_ambiguous()
    {
        var scenario = StartWithTwoWorkInvocations(OtherPath.Terminate);
        var before = Snapshot(scenario.Evaluation.State);

        var terminated = Complete(scenario.Graph, scenario.Evaluation.State, scenario.Other,
            scenario.Work.Select(Legacy), scenario.Other.TokenId);

        ShouldMatchSnapshot(scenario.Evaluation.State, before);
        terminated.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        terminated.State.Terminated.ShouldBeTrue();
        terminated.State.ActiveWork.ShouldBeEmpty();
        var teardowns = terminated.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ToArray();
        teardowns.Select(command => command.Handle).ShouldBe(scenario.Work.Select(work => work.Handle));
        teardowns.Select(command => command.Handle).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        teardowns.ShouldAllBe(command => command.Reason == BpmnInterpreter.TerminateStopReason);
    }
}
