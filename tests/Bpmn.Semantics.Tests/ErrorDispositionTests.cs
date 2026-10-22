using Bpmn.Model;
using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class ErrorDispositionTests
{
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    private static BpmnHostSnapshot Scope(params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root("scope-1") with { LiveWork = liveWork };

    private static BpmnGraph BuildGraph(
        BpmnProcessDefinition definition,
        IReadOnlyDictionary<string, BpmnProcessDefinition>? nestedProcesses = null) =>
        BpmnGraph.Build(
            definition,
            BpmnBoundWork.Derive(definition, bindingRef => nestedProcesses?.GetValueOrDefault(bindingRef)),
            BpmnHostCapabilities.Full);

    private static BpmnEvaluation Start(BpmnGraph graph) =>
        Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));

    private static BpmnLiveWork[] LiveWork(BpmnEvaluation evaluation) =>
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>()
            .Select((start, index) => new BpmnLiveWork(start.BindingRef, start.IterationScope?.IterationId, $"h-{index}"))
            .ToArray();

    private static BpmnProcessDefinition CallActivity(Catcher catcher)
    {
        var builder = new BpmnProcessBuilder("call-failure")
            .StartEvent("Start")
            .CallActivity("Call", "child", bindingRef: "call")
            .EndEvent("EndOk")
            .ConnectSequence("Start", "Call", "EndOk");

        return catcher switch
        {
            Catcher.Boundary => builder
                .BoundaryEvent("Failed", "Call", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
                .Task("Recover", bindingRef: "recover")
                .EndEvent("EndRecovered")
                .ConnectSequence("Failed", "Recover", "EndRecovered")
                .Build(),
            Catcher.BrokenBoundary => builder
                .BoundaryEvent("Failed", "Call", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
                .ExclusiveGateway("Undecidable")
                .EndEvent("EndLeft")
                .EndEvent("EndRight")
                .Connect("Failed", "Undecidable")
                .Connect("Undecidable", "EndLeft", condition: "Left")
                .Connect("Undecidable", "EndRight", condition: "Right")
                .Build(),
            Catcher.ErrorEventSubprocess => builder
                .SubProcess("OnError", bindingRef: "on-error", triggeredByEvent: true)
                .Build(),
            _ => builder.Build()
        };
    }

    private static BpmnProcessDefinition ErrorHandler() =>
        new BpmnProcessBuilder("on-error")
            .StartEvent("ErrorStart", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("ErrorStart", "Handle", "HandlerEnd")
            .Build();

    private static (BpmnGraph Graph, BpmnEvaluation Started, BpmnLiveWork[] Live, BpmnLiveWork Call) StartCall(Catcher catcher)
    {
        var definition = CallActivity(catcher);
        var nestedProcesses = catcher == Catcher.ErrorEventSubprocess
            ? new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["on-error"] = ErrorHandler() }
            : null;
        var graph = BuildGraph(definition, nestedProcesses);
        var started = Start(graph);
        var live = LiveWork(started);
        return (graph, started, live, live.Single(work => work.BindingRef == "call"));
    }

    private static BpmnEvaluation CompleteCall(
        BpmnGraph graph,
        BpmnEvaluation prior,
        BpmnLiveWork[] live,
        BpmnLiveWork call,
        string outcome) =>
        Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, prior.State, Scope(live.Where(work => work.Handle != call.Handle).ToArray()), call.BindingRef, call.Handle, [outcome], call.IterationId));

    [Theory]
    [InlineData("Faulted", "boundary", "Failed", null)]
    [InlineData("DispatchFailed", "boundary", "Failed", null)]
    [InlineData("Cancelled", "boundary", "Failed", null)]
    [InlineData("Faulted", "event-subprocess", "OnError", null)]
    [InlineData("DispatchFailed", "event-subprocess", "OnError", null)]
    [InlineData("Cancelled", "event-subprocess", "OnError", null)]
    [InlineData("Faulted", "none", null, BpmnInterpreter.CallActivityFaultedFaultCode)]
    [InlineData("DispatchFailed", "none", null, BpmnInterpreter.CallActivityDispatchFailedFaultCode)]
    [InlineData("Cancelled", "none", null, BpmnInterpreter.CallActivityCancelledFaultCode)]
    public void A_call_activity_failure_outcome_reports_how_its_error_path_ended(
        string failureOutcome,
        string catcher,
        string? catchingElementId,
        string? expectedFaultCode)
    {
        var catcherKind = catcher switch
        {
            "boundary" => Catcher.Boundary,
            "event-subprocess" => Catcher.ErrorEventSubprocess,
            _ => Catcher.None
        };
        var (graph, started, live, call) = StartCall(catcherKind);

        var evaluation = CompleteCall(graph, started, live, call, failureOutcome);

        if (catchingElementId is { } expectedCatcher)
        {
            evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught(expectedCatcher, ErrorCode: null));
            evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Defer>();
            evaluation.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef)
                .ShouldContain(catcherKind == Catcher.Boundary ? "recover" : "on-error");
        }
        else
        {
            evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
            evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe(expectedFaultCode);
        }
    }

    [Theory]
    [InlineData(BpmnInterpreter.CallActivityFaultedOutcomeName)]
    [InlineData(BpmnInterpreter.CallActivityDispatchFailedOutcomeName)]
    [InlineData(BpmnInterpreter.CallActivityCancelledOutcomeName)]
    public void A_call_activity_failure_whose_catcher_path_faults_reports_propagated(string failureOutcome)
    {
        var (graph, started, live, call) = StartCall(Catcher.BrokenBoundary);

        var evaluation = CompleteCall(graph, started, live, call, failureOutcome);

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.gateway.no-flow-selected");
        evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
    }

    [Fact]
    public void Starting_and_completing_ordinary_work_leave_the_disposition_null()
    {
        var graph = BuildGraph(ProcessFixtures.Linear());

        var started = Start(graph);
        started.ErrorDisposition.ShouldBeNull();
        var work = LiveWork(started).ShouldHaveSingleItem();

        var completed = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, Scope(), work.BindingRef, work.Handle, []));

        completed.Continuation.ShouldBeOfType<BpmnContinuation.Complete>();
        completed.ErrorDisposition.ShouldBeNull();
    }

    [Fact]
    public void A_call_activity_that_completes_normally_leaves_the_disposition_null()
    {
        var (graph, started, live, call) = StartCall(Catcher.None);

        var evaluation = CompleteCall(graph, started, live, call, BpmnInterpreter.DoneOutcomeName);

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Complete>();
        evaluation.ErrorDisposition.ShouldBeNull();
    }

    [Theory]
    [InlineData(BpmnInterpreter.CallActivityFaultedOutcomeName)]
    [InlineData(BpmnInterpreter.CallActivityDispatchFailedOutcomeName)]
    [InlineData(BpmnInterpreter.CallActivityCancelledOutcomeName)]
    public void A_late_call_activity_failure_outcome_in_a_terminated_scope_is_absorbed(string failureOutcome)
    {
        var definition = new BpmnProcessBuilder("call-stopped-by-terminate")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .CallActivity("Call", "child", bindingRef: "call")
            .Task("Stop", bindingRef: "stop")
            .EndEvent("EndCall")
            .EndEvent("Terminate", null, ProcessFixtures.Terminate())
            .Connect("Start", "Split")
            .Connect("Split", "Call")
            .Connect("Split", "Stop")
            .ConnectSequence("Call", "EndCall")
            .ConnectSequence("Stop", "Terminate")
            .Build();
        var graph = BuildGraph(definition);
        var started = Start(graph);
        var live = LiveWork(started);
        var call = live.Single(work => work.BindingRef == "call");
        var stop = live.Single(work => work.BindingRef == "stop");
        var terminated = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, Scope([call]), stop.BindingRef, stop.Handle, [], stop.IterationId));
        terminated.State.Terminated.ShouldBeTrue();

        var late = CompleteCall(graph, terminated, [], call, failureOutcome);

        late.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        late.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Absorbed.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_late_failure_in_a_legacy_faulted_scope_is_absorbed_and_preserves_the_fault(bool asFault)
    {
        var (graph, started, _, call) = StartCall(Catcher.None);
        var ended = started with
        {
            State = started.State with { PendingFault = new BpmnPendingFault("original-fault", "Already ended") }
        };

        var late = asFault
            ? Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(graph, ended.State, Scope(), call.BindingRef, call.Handle, "late failure"))
            : CompleteCall(graph, ended, [], call, BpmnInterpreter.CallActivityFaultedOutcomeName);

        late.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("original-fault");
        late.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Absorbed.Instance);
        late.Commands.ShouldBeEmpty();
    }

    [Fact]
    public void An_event_subprocess_fired_by_escalation_does_not_report_an_error_disposition()
    {
        var (definition, handler) = ProcessFixtures.OwnScopeEscalationInterruptsASecondBranch();
        var graph = BuildGraph(definition,
            new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["on-overdue"] = handler });
        var started = Start(graph);
        var live = LiveWork(started);
        var trigger = live.Single(work => work.BindingRef == "escalate-trigger");

        var evaluation = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, Scope(live.Where(work => work.Handle != trigger.Handle).ToArray()),
            trigger.BindingRef, trigger.Handle, [], trigger.IterationId));

        evaluation.ErrorDisposition.ShouldBeNull();
    }

    private enum Catcher
    {
        None,
        Boundary,
        BrokenBoundary,
        ErrorEventSubprocess
    }
}
