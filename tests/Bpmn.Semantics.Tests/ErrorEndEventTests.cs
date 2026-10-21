using Bpmn.Model;
using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// An error end event throws its error (ADR 0012). It consumes its token, then an error event subprocess of its own
/// scope that declares the error's code, else a catch-all one, catches it and interrupts the scope. Otherwise the
/// scope ends at once with a <c>bpmn.error.thrown</c> fault that carries the code, withdrawing work the evaluation
/// was about to start. Crossing into the enclosing scope is the host's job; the reference host's pass-through is
/// tested end to end in <c>ErrorCodePassThroughTests</c>.
/// </summary>
public sealed class ErrorEndEventTests
{
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    private const string CreditDeclined = "CREDIT_DECLINED";

    private static BpmnEventDefinition Error(string? code) =>
        code is null
            ? new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)
            : new BpmnEventDefinition(BpmnEventDefinitionTypes.Error,
                new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code });

    private static BpmnHostSnapshot Scope(params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root("scope-1") with { LiveWork = liveWork };

    private static BpmnLiveWork[] LiveWork(BpmnEvaluation evaluation) =>
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>()
            .Select(start => new BpmnLiveWork(start.BindingRef, start.IterationScope?.IterationId, $"h-{start.TokenId}") { WorkTokenId = start.TokenId })
            .ToArray();

    private static IReadOnlyList<string> Started(BpmnEvaluation evaluation) =>
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ToArray();

    /// <summary>An error event subprocess body whose start declares <paramref name="code"/> (or none).</summary>
    private static BpmnProcessDefinition Handler(string bindingRef, string? code) =>
        new BpmnProcessBuilder(bindingRef)
            .StartEvent("HandlerStart", null, Error(code))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("HandlerStart", "Handle", "HandlerEnd")
            .Build();

    /// <summary>
    /// <c>Start -> Split</c>, one branch to <c>Long</c> (a task left running) and one to <c>Charge -> Failed</c>, an
    /// error end event throwing <paramref name="thrown"/>. The scope carries an error event subprocess per entry of
    /// <paramref name="subprocesses"/>: <c>Sub{code}</c> (or <c>SubAny</c>) bound <c>sub-{code}</c>.
    /// </summary>
    private static BpmnGraph Build(string? thrown, params string?[] subprocesses)
    {
        var builder = new BpmnProcessBuilder("throws")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("Long", bindingRef: "long")
            .EndEvent("LongEnd")
            .Task("Charge", bindingRef: "charge")
            .EndEvent("Failed", null, Error(thrown))
            .Connect("Start", "Split")
            .Connect("Split", "Long")
            .Connect("Split", "Charge")
            .ConnectSequence("Long", "LongEnd")
            .ConnectSequence("Charge", "Failed");

        var bodies = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal);
        foreach (var code in subprocesses)
        {
            var bindingRef = $"sub-{(code ?? "any").ToLowerInvariant()}";
            builder = builder.SubProcess($"Sub{code ?? "Any"}", bindingRef: bindingRef, triggeredByEvent: true);
            bodies[bindingRef] = Handler(bindingRef, code);
        }

        var definition = builder.Build();
        return BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition, bodies.GetValueOrDefault), BpmnHostCapabilities.Full);
    }

    /// <summary>Starts <paramref name="graph"/>, then completes <c>Charge</c>, whose successor is the error end event.</summary>
    private static (BpmnEvaluation Thrown, BpmnLiveWork Long) StartAndThrow(BpmnGraph graph)
    {
        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));
        var live = LiveWork(started);
        var charge = live.Single(work => work.BindingRef == "charge");
        var running = live.Single(work => work.BindingRef == "long");

        var thrown = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, Scope(running), charge.BindingRef, charge.Handle, []) { WorkTokenId = charge.WorkTokenId });
        return (thrown, running);
    }

    // --- Uncaught -----------------------------------------------------------------------------------------

    [Fact]
    public void An_uncaught_error_end_event_faults_its_scope_with_the_thrown_code()
    {
        var (thrown, _) = StartAndThrow(Build(CreditDeclined));

        var fault = thrown.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe(BpmnInterpreter.ErrorThrownFaultCode);
        fault.ErrorCode.ShouldBe(CreditDeclined);
        fault.Message.ShouldContain("'Failed'");
        thrown.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance,
            "no catcher in the scope took the error; the parent decides again how it ends there");
    }

    [Fact]
    public void An_uncaught_error_end_event_leaves_the_host_to_stop_what_still_runs()
    {
        // A fault is a verdict, not a command (ADR 0003): the scope tears nothing down itself.
        var (thrown, _) = StartAndThrow(Build(CreditDeclined));

        thrown.Commands.ShouldBeEmpty();
        thrown.State.Tokens.Single(token => token.AtElementId == "Failed").Status.ShouldBe(BpmnTokenStatus.Consumed);
    }

    [Fact]
    public void An_uncoded_error_end_event_faults_with_no_code()
    {
        var (thrown, _) = StartAndThrow(Build(thrown: null));

        var fault = thrown.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe(BpmnInterpreter.ErrorThrownFaultCode);
        fault.ErrorCode.ShouldBeNull();
        thrown.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
    }

    [Fact]
    public void An_error_end_event_reached_at_start_withdraws_the_work_the_same_evaluation_was_about_to_start()
    {
        var definition = new BpmnProcessBuilder("throws-at-once")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("Long", bindingRef: "long")
            .EndEvent("LongEnd")
            .EndEvent("Failed", null, Error(CreditDeclined))
            .Connect("Start", "Split")
            .Connect("Split", "Long")
            .Connect("Split", "Failed")
            .ConnectSequence("Long", "LongEnd")
            .Build();
        var graph = BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full);

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));

        started.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().ErrorCode.ShouldBe(CreditDeclined);
        started.Commands.OfType<BpmnHostCommand.StartWork>().ShouldBeEmpty("a terminal continuation never starts work");
        started.State.ActiveWork.ShouldBeEmpty();
    }

    [Fact]
    public void A_coded_thrown_error_skips_an_event_subprocess_declaring_another_code()
    {
        var (thrown, _) = StartAndThrow(Build(CreditDeclined, "TIMEOUT"));

        thrown.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().ErrorCode.ShouldBe(CreditDeclined);
        thrown.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
    }

    [Fact]
    public void An_uncoded_thrown_error_skips_a_coded_event_subprocess()
    {
        var (thrown, _) = StartAndThrow(Build(thrown: null, CreditDeclined));

        thrown.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().ErrorCode.ShouldBeNull();
        thrown.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
    }

    // --- Caught by the scope's own event subprocess -----------------------------------------------------

    [Fact]
    public void An_error_end_event_is_caught_by_its_own_scopes_event_subprocess()
    {
        var (thrown, _) = StartAndThrow(Build(CreditDeclined, CreditDeclined));

        thrown.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught($"Sub{CreditDeclined}", CreditDeclined));
        thrown.Continuation.ShouldBe(BpmnContinuation.Defer.Instance);
        var body = thrown.Commands.OfType<BpmnHostCommand.StartWork>().ShouldHaveSingleItem();
        body.ElementId.ShouldBe($"Sub{CreditDeclined}");
        body.StartElementHint.ShouldBe("HandlerStart");
    }

    [Theory]
    [InlineData(CreditDeclined, "SubCREDIT_DECLINED", CreditDeclined)]
    [InlineData("TIMEOUT", "SubAny", null)]
    [InlineData(null, "SubAny", null)]
    public void A_thrown_error_reaches_the_exact_event_subprocess_before_the_catch_all(string? thrownCode, string catcher, string? reportedCode)
    {
        var (thrown, _) = StartAndThrow(Build(thrownCode, null, CreditDeclined));

        thrown.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught(catcher, reportedCode));
        thrown.Commands.OfType<BpmnHostCommand.StartWork>().ShouldHaveSingleItem().ElementId.ShouldBe(catcher);
    }

    [Fact]
    public void A_caught_error_interrupts_the_scope_and_tears_down_the_work_it_abandons()
    {
        var (thrown, running) = StartAndThrow(Build(CreditDeclined, CreditDeclined));

        var teardown = thrown.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldHaveSingleItem();
        teardown.Handle.ShouldBe(running.Handle);
        teardown.ElementId.ShouldBe("Long");
        teardown.Reason.ShouldBe(BpmnInterpreter.EventSubprocessScopeInterruptedReason);
        thrown.State.ActiveWork.Select(work => work.NodeId).ShouldBe([$"sub-{CreditDeclined.ToLowerInvariant()}"]);
    }

    [Fact]
    public void A_caught_error_reached_at_start_withdraws_the_sibling_work_the_same_evaluation_started()
    {
        var definition = new BpmnProcessBuilder("throws-at-once")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("Long", bindingRef: "long")
            .EndEvent("LongEnd")
            .EndEvent("Failed", null, Error(CreditDeclined))
            .Connect("Start", "Split")
            .Connect("Split", "Long")
            .Connect("Split", "Failed")
            .ConnectSequence("Long", "LongEnd")
            .SubProcess("OnDeclined", bindingRef: "on-declined", triggeredByEvent: true)
            .Build();
        var bodies = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["on-declined"] = Handler("on-declined", CreditDeclined) };
        var graph = BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition, bodies.GetValueOrDefault), BpmnHostCapabilities.Full);

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));

        started.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught("OnDeclined", CreditDeclined));
        Started(started).ShouldBe(["on-declined"], "the sibling task never reached the host, so it is withdrawn rather than torn down");
        started.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldBeEmpty();
        started.State.Tokens.Single(token => token.AtElementId == "Long").Status.ShouldBe(BpmnTokenStatus.Canceled);
    }

    [Fact]
    public void The_scope_completes_once_the_catching_subprocess_body_completes()
    {
        var graph = Build(CreditDeclined, CreditDeclined);
        var (thrown, _) = StartAndThrow(graph);
        var body = LiveWork(thrown).ShouldHaveSingleItem();

        var completed = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, thrown.State, Scope(), body.BindingRef, body.Handle, []) { WorkTokenId = body.WorkTokenId });

        completed.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        completed.ErrorDisposition.ShouldBeNull();
    }

    // --- Diagnostics ---------------------------------------------------------------------------------------

    [Fact]
    public void Throwing_records_the_thrower_and_its_code_and_a_catch_records_the_catcher()
    {
        var (thrown, _) = StartAndThrow(Build(CreditDeclined, [null]));

        var throwDiagnostic = thrown.State.Diagnostics.Where(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ErrorThrown).ShouldHaveSingleItem();
        throwDiagnostic.ElementId.ShouldBe("Failed");
        throwDiagnostic.Details["errorCode"].ShouldBe(CreditDeclined);

        var caught = thrown.State.Diagnostics.Where(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ErrorCaught).ShouldHaveSingleItem();
        caught.ElementId.ShouldBe("SubAny");
        caught.Details["errorCode"].ShouldBe(CreditDeclined);
        caught.Details["match"].ShouldBe("catch-all");

        thrown.State.Diagnostics.ToList().FindIndex(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ErrorThrown)
            .ShouldBeLessThan(thrown.State.Diagnostics.ToList().FindIndex(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ErrorCaught));
    }

    [Fact]
    public void An_uncoded_throw_records_no_code()
    {
        var (thrown, _) = StartAndThrow(Build(thrown: null));

        var throwDiagnostic = thrown.State.Diagnostics.Where(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ErrorThrown).ShouldHaveSingleItem();
        throwDiagnostic.Details.ShouldBeEmpty();
        throwDiagnostic.Message.ShouldContain("no code");
        thrown.State.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ErrorCaught);
    }

    // --- Graph build -------------------------------------------------------------------------------------

    [Fact]
    public void An_error_end_event_in_a_body_the_parent_carries_no_longer_refuses_the_build()
    {
        var body = new BpmnProcessBuilder("settle")
            .StartEvent("Start")
            .EndEvent("Failed", null, Error(CreditDeclined))
            .ConnectSequence("Start", "Failed")
            .Build();
        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .SubProcess("Settle", bindingRef: "settle")
            .EndEvent("End")
            .ConnectSequence("Start", "Settle", "End")
            .Build();

        Should.NotThrow(() => BpmnGraph.Build(parent,
            BpmnBoundWork.Derive(parent, bindingRef => bindingRef == "settle" ? body : null), BpmnHostCapabilities.Full));
        Should.NotThrow(() => BpmnGraph.Build(body, BpmnBoundWork.Derive(body), BpmnHostCapabilities.Full));
    }

    [Fact]
    public void An_error_end_event_that_binds_work_is_refused()
    {
        var definition = new BpmnProcessBuilder("bound-thrower")
            .StartEvent("Start")
            .Element(new BpmnElement("Failed", BpmnElementTypes.EndEvent, bindingRef: "failed", eventDefinitions: [Error(CreditDeclined)]))
            .ConnectSequence("Start", "Failed")
            .Build();

        Should.Throw<BpmnExecutionException>(() => BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full))
            .Message.ShouldContain("'Failed'");
    }
}
