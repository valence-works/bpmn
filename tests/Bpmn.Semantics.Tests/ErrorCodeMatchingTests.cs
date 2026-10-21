using System.Runtime.CompilerServices;
using Bpmn.Model;
using Bpmn.Model.State;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Error catchers match on the error's code (ADR 0012). A catcher that declares a code catches only an error with
/// that code; a catcher that declares none is a catch-all and catches any error, including one with no code. The
/// fault ladder tries an error boundary on the failing activity (exact, else catch-all), then an error event
/// subprocess of the scope (exact, else catch-all), and otherwise faults the scope with the code preserved.
/// <para>
/// Driven directly, with no host: each test feeds a definition, a state, a snapshot and a report, and asserts on
/// the disposition, the continuation and the commands.
/// </para>
/// </summary>
public sealed class ErrorCodeMatchingTests
{
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    private const string CreditDeclined = "CREDIT_DECLINED";
    private const string Timeout = "TIMEOUT";

    private static BpmnEventDefinition Error(string? code) =>
        code is null
            ? new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)
            : new BpmnEventDefinition(BpmnEventDefinitionTypes.Error,
                new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code });

    private static BpmnHostSnapshot Scope(params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root("scope-1") with { LiveWork = liveWork };

    private static BpmnGraph BuildGraph(BpmnProcessDefinition definition, IReadOnlyDictionary<string, BpmnProcessDefinition>? bodies = null) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition, bindingRef => bodies?.GetValueOrDefault(bindingRef)), BpmnHostCapabilities.Full);

    private static BpmnLiveWork[] LiveWork(BpmnEvaluation evaluation) =>
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>()
            .Select(start => new BpmnLiveWork(start.BindingRef, start.IterationScope?.IterationId, $"h-{start.TokenId}") { WorkTokenId = start.TokenId })
            .ToArray();

    private static IReadOnlyList<string> Started(BpmnEvaluation evaluation) =>
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ToArray();

    /// <summary>Starts <paramref name="graph"/> and faults its single <paramref name="bindingRef"/> work with <paramref name="errorCode"/>.</summary>
    private static BpmnEvaluation StartAndFault(BpmnGraph graph, string bindingRef, string? errorCode)
    {
        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));
        var live = LiveWork(started);
        var failed = live.Single(work => work.BindingRef == bindingRef);

        return Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            graph, started.State, Scope(live.Where(work => work.Handle != failed.Handle).ToArray()), failed.BindingRef, failed.Handle, "boom")
        {
            WorkTokenId = failed.WorkTokenId,
            ErrorCode = errorCode
        });
    }

    /// <summary>
    /// <c>Charge</c> carrying an error boundary per entry of <paramref name="boundaries"/>: a code, or <c>null</c> for
    /// the catch-all. Boundary <c>On{code}</c> (or <c>OnAny</c>) routes to work bound <c>handle-{code}</c> (or <c>handle-any</c>).
    /// </summary>
    private static BpmnProcessDefinition ChargeWithBoundaries(params string?[] boundaries) => ChargeBuilder(boundaries).Build();

    private static BpmnProcessBuilder ChargeBuilder(string?[] boundaries)
    {
        var builder = new BpmnProcessBuilder("charge")
            .StartEvent("Start")
            .ServiceTask("Charge", bindingRef: "charge")
            .EndEvent("Charged")
            .ConnectSequence("Start", "Charge", "Charged");

        foreach (var code in boundaries)
        {
            var name = code ?? "Any";
            builder = builder
                .BoundaryEvent($"On{name}", "Charge", Error(code))
                .Task($"Handle{name}", bindingRef: $"handle-{name.ToLowerInvariant()}")
                .EndEvent($"Handled{name}")
                .ConnectSequence($"On{name}", $"Handle{name}", $"Handled{name}");
        }

        return builder;
    }

    /// <summary>An error event subprocess body whose start declares <paramref name="code"/> (or none), running one task.</summary>
    private static BpmnProcessDefinition Handler(string bindingRef, string? code) =>
        new BpmnProcessBuilder(bindingRef)
            .StartEvent("HandlerStart", null, Error(code))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("HandlerStart", "Handle", "HandlerEnd")
            .Build();

    /// <summary>
    /// <c>Charge</c> in a scope carrying an error event subprocess per entry of <paramref name="subprocesses"/>,
    /// <c>Sub{code}</c> (or <c>SubAny</c>) bound <c>sub-{code}</c>, plus the error boundaries in <paramref name="boundaries"/>.
    /// </summary>
    private static (BpmnProcessDefinition Definition, IReadOnlyDictionary<string, BpmnProcessDefinition> Bodies) ChargeWithEventSubprocesses(
        string?[] subprocesses,
        params string?[] boundaries)
    {
        var builder = ChargeBuilder(boundaries);
        var bodies = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal);
        foreach (var code in subprocesses)
        {
            var bindingRef = $"sub-{(code ?? "any").ToLowerInvariant()}";
            builder = builder.SubProcess($"Sub{code ?? "Any"}", bindingRef: bindingRef, triggeredByEvent: true);
            bodies[bindingRef] = Handler(bindingRef, code);
        }

        return (builder.Build(), bodies);
    }

    // --- Error boundaries ----------------------------------------------------------------------------------

    [Fact]
    public void A_boundary_declaring_the_code_wins_over_the_catch_all()
    {
        var graph = BuildGraph(ChargeWithBoundaries(null, CreditDeclined));

        var evaluation = StartAndFault(graph, "charge", CreditDeclined);

        evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught($"On{CreditDeclined}", CreditDeclined));
        Started(evaluation).ShouldBe([$"handle-{CreditDeclined.ToLowerInvariant()}"]);
        evaluation.Continuation.ShouldBe(BpmnContinuation.Defer.Instance);
    }

    [Fact]
    public void A_coded_fault_no_boundary_declares_reaches_the_catch_all_which_reports_no_code()
    {
        var graph = BuildGraph(ChargeWithBoundaries(CreditDeclined, null));

        var evaluation = StartAndFault(graph, "charge", Timeout);

        evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught("OnAny", ErrorCode: null));
        Started(evaluation).ShouldBe(["handle-any"]);
    }

    [Fact]
    public void An_uncoded_fault_skips_a_coded_boundary()
    {
        var graph = BuildGraph(ChargeWithBoundaries(CreditDeclined));

        var evaluation = StartAndFault(graph, "charge", errorCode: null);

        evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe("bpmn.work.faulted");
        fault.ErrorCode.ShouldBeNull();
        evaluation.Commands.ShouldBeEmpty();
    }

    [Fact]
    public void An_uncoded_fault_reaches_the_catch_all_beside_a_coded_boundary()
    {
        var graph = BuildGraph(ChargeWithBoundaries(CreditDeclined, null));

        var evaluation = StartAndFault(graph, "charge", errorCode: null);

        evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught("OnAny", ErrorCode: null));
        Started(evaluation).ShouldBe(["handle-any"]);
    }

    [Fact]
    public void A_coded_fault_no_catcher_matches_propagates_with_its_code_on_the_fault()
    {
        var graph = BuildGraph(ChargeWithBoundaries(CreditDeclined));

        var evaluation = StartAndFault(graph, "charge", Timeout);

        evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe("bpmn.work.faulted");
        fault.ErrorCode.ShouldBe(Timeout);
        fault.Message.ShouldContain($"'{Timeout}'");
    }

    [Theory]
    [InlineData("A", "OnA", "handle-a")]
    [InlineData("B", "OnB", "handle-b")]
    [InlineData("C", "OnC", "handle-c")]
    [InlineData("D", "OnAny", "handle-any")]
    public void Several_coded_boundaries_on_one_host_each_catch_their_own_code(string errorCode, string catcher, string handler)
    {
        var graph = BuildGraph(ChargeWithBoundaries("A", "B", null, "C"));

        var evaluation = StartAndFault(graph, "charge", errorCode);

        evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught(catcher, catcher == "OnAny" ? null : errorCode));
        Started(evaluation).ShouldBe([handler]);
    }

    [Theory]
    [InlineData(" CREDIT_DECLINED ", true)]
    [InlineData("   ", false)]
    [InlineData("", false)]
    public void A_reported_code_is_trimmed_and_a_blank_one_counts_as_none(string reported, bool matchesTheCodedBoundary)
    {
        var graph = BuildGraph(ChargeWithBoundaries(CreditDeclined, null));

        var evaluation = StartAndFault(graph, "charge", reported);

        evaluation.ErrorDisposition.ShouldBe(matchesTheCodedBoundary
            ? new BpmnErrorDisposition.Caught($"On{CreditDeclined}", CreditDeclined)
            : new BpmnErrorDisposition.Caught("OnAny", ErrorCode: null));
    }

    // --- Error event subprocesses ------------------------------------------------------------------------

    [Theory]
    [InlineData(CreditDeclined, "SubCREDIT_DECLINED", CreditDeclined)]
    [InlineData(Timeout, "SubAny", null)]
    [InlineData(null, "SubAny", null)]
    public void An_event_subprocess_declaring_the_code_wins_over_the_catch_all(string? errorCode, string catcher, string? reportedCode)
    {
        var (definition, bodies) = ChargeWithEventSubprocesses([null, CreditDeclined]);
        var graph = BuildGraph(definition, bodies);

        var evaluation = StartAndFault(graph, "charge", errorCode);

        evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught(catcher, reportedCode));
        var body = evaluation.Commands.OfType<BpmnHostCommand.StartWork>().ShouldHaveSingleItem();
        body.ElementId.ShouldBe(catcher);
        body.StartElementHint.ShouldBe("HandlerStart");
    }

    [Fact]
    public void An_uncoded_fault_skips_a_coded_event_subprocess()
    {
        var (definition, bodies) = ChargeWithEventSubprocesses([CreditDeclined]);
        var graph = BuildGraph(definition, bodies);

        var evaluation = StartAndFault(graph, "charge", errorCode: null);

        evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().ErrorCode.ShouldBeNull();
    }

    [Fact]
    public void The_boundary_tier_runs_before_the_event_subprocess_tier_even_when_only_the_subprocess_matches_exactly()
    {
        // The ladder is tiered by catcher kind, not by specificity across kinds: a catch-all boundary on the
        // failing activity is closer than an exact event subprocess of the scope (ADR 0012).
        var (definition, bodies) = ChargeWithEventSubprocesses([CreditDeclined], boundaries: [null]);
        var graph = BuildGraph(definition, bodies);

        var evaluation = StartAndFault(graph, "charge", CreditDeclined);

        evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught("OnAny", ErrorCode: null));
    }

    [Fact]
    public void A_coded_fault_the_boundary_misses_falls_through_to_a_matching_event_subprocess()
    {
        var (definition, bodies) = ChargeWithEventSubprocesses([CreditDeclined], boundaries: [Timeout]);
        var graph = BuildGraph(definition, bodies);

        var evaluation = StartAndFault(graph, "charge", CreditDeclined);

        evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught($"Sub{CreditDeclined}", CreditDeclined));
    }

    // --- Multi-instance --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(CreditDeclined, true)]
    [InlineData(Timeout, false)]
    public void A_multi_instance_fault_matches_by_code_and_a_catch_interrupts_every_instance(string errorCode, bool caught)
    {
        var definition = new BpmnProcessBuilder("multi-instance")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: 3))
            .EndEvent("EndOk")
            .BoundaryEvent("Declined", "Review", Error(CreditDeclined))
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndRecovered")
            .ConnectSequence("Start", "Review", "EndOk")
            .ConnectSequence("Declined", "Recover", "EndRecovered")
            .Build();
        var graph = BuildGraph(definition);
        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));
        var live = LiveWork(started);

        var evaluation = Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            graph, started.State, Scope(live[..2]), "review", live[2].Handle, "boom")
        {
            WorkTokenId = live[2].WorkTokenId,
            ErrorCode = errorCode
        });

        if (caught)
        {
            evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught("Declined", CreditDeclined));
            evaluation.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().Select(teardown => teardown.Handle)
                .ShouldBe([live[0].Handle, live[1].Handle], ignoreOrder: true);
            Started(evaluation).ShouldBe(["recover"]);
        }
        else
        {
            evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
            evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().ErrorCode.ShouldBe(Timeout);
        }
    }

    // --- Call activities -----------------------------------------------------------------------------------

    private static BpmnProcessDefinition CallWithBoundaries(params string?[] boundaries)
    {
        var builder = new BpmnProcessBuilder("call")
            .StartEvent("Start")
            .CallActivity("Call", "child", bindingRef: "call")
            .EndEvent("EndOk")
            .ConnectSequence("Start", "Call", "EndOk");

        foreach (var code in boundaries)
        {
            var name = code ?? "Any";
            builder = builder
                .BoundaryEvent($"On{name}", "Call", Error(code))
                .Task($"Handle{name}", bindingRef: $"handle-{name.ToLowerInvariant()}")
                .EndEvent($"Handled{name}")
                .ConnectSequence($"On{name}", $"Handle{name}", $"Handled{name}");
        }

        return builder.Build();
    }

    private static BpmnEvaluation CompleteCall(BpmnGraph graph, string outcome, string? errorCode)
    {
        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));
        var call = LiveWork(started).Single(work => work.BindingRef == "call");

        return Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(graph, started.State, Scope(), call.BindingRef, call.Handle, [outcome])
        {
            WorkTokenId = call.WorkTokenId,
            ErrorCode = errorCode
        });
    }

    [Fact]
    public void A_call_activity_faulted_outcome_carrying_a_code_reaches_the_boundary_declaring_it()
    {
        var graph = BuildGraph(CallWithBoundaries(null, CreditDeclined));

        var evaluation = CompleteCall(graph, BpmnInterpreter.CallActivityFaultedOutcomeName, CreditDeclined);

        evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught($"On{CreditDeclined}", CreditDeclined));
        Started(evaluation).ShouldBe([$"handle-{CreditDeclined.ToLowerInvariant()}"]);
    }

    [Fact]
    public void A_call_activity_faulted_outcome_no_catcher_matches_faults_with_its_code()
    {
        var graph = BuildGraph(CallWithBoundaries(Timeout));

        var evaluation = CompleteCall(graph, BpmnInterpreter.CallActivityFaultedOutcomeName, CreditDeclined);

        evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe(BpmnInterpreter.CallActivityFaultedFaultCode);
        fault.ErrorCode.ShouldBe(CreditDeclined);
    }

    [Theory]
    [InlineData(BpmnInterpreter.CallActivityDispatchFailedOutcomeName)]
    [InlineData(BpmnInterpreter.CallActivityCancelledOutcomeName)]
    public void A_dispatch_failed_or_cancelled_call_ignores_a_reported_code_and_reaches_only_the_catch_all(string outcome)
    {
        var graph = BuildGraph(CallWithBoundaries(CreditDeclined, null));

        var evaluation = CompleteCall(graph, outcome, CreditDeclined);

        evaluation.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught("OnAny", ErrorCode: null));
        Started(evaluation).ShouldBe(["handle-any"]);
    }

    [Theory]
    [InlineData(BpmnInterpreter.CallActivityDispatchFailedOutcomeName, BpmnInterpreter.CallActivityDispatchFailedFaultCode)]
    [InlineData(BpmnInterpreter.CallActivityCancelledOutcomeName, BpmnInterpreter.CallActivityCancelledFaultCode)]
    public void A_dispatch_failed_or_cancelled_call_skips_a_coded_boundary_and_faults_with_no_code(string outcome, string faultCode)
    {
        var graph = BuildGraph(CallWithBoundaries(CreditDeclined));

        var evaluation = CompleteCall(graph, outcome, CreditDeclined);

        evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe(faultCode);
        fault.ErrorCode.ShouldBeNull("the failure is the call's own, not an error of the callee");
    }

    [Fact]
    public void A_code_on_an_ordinary_completion_is_ignored()
    {
        var graph = BuildGraph(CallWithBoundaries(CreditDeclined, null));

        var evaluation = CompleteCall(graph, BpmnInterpreter.DoneOutcomeName, CreditDeclined);

        evaluation.ErrorDisposition.ShouldBeNull();
        evaluation.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
    }

    [Fact]
    public void A_fault_on_the_catchers_own_path_is_not_the_bpmn_error_and_carries_no_code()
    {
        // The boundary caught the coded error, then its own path faulted for a library reason. The continuation is
        // that fault, which is not a BPMN error, so it carries no code; the disposition still reports the failure
        // as not handled.
        var definition = new BpmnProcessBuilder("broken-catcher")
            .StartEvent("Start")
            .CallActivity("Call", "child", bindingRef: "call")
            .EndEvent("EndOk")
            .ConnectSequence("Start", "Call", "EndOk")
            .BoundaryEvent("Declined", "Call", Error(CreditDeclined))
            .ExclusiveGateway("Undecidable")
            .EndEvent("EndLeft")
            .EndEvent("EndRight")
            .Connect("Declined", "Undecidable")
            .Connect("Undecidable", "EndLeft", condition: "Left")
            .Connect("Undecidable", "EndRight", condition: "Right")
            .Build();
        var graph = BuildGraph(definition);

        var evaluation = CompleteCall(graph, BpmnInterpreter.CallActivityFaultedOutcomeName, CreditDeclined);

        evaluation.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe("bpmn.gateway.no-flow-selected");
        fault.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public void A_cancelled_transaction_with_no_cancel_boundary_faults_with_no_error_code()
    {
        var definition = new BpmnProcessBuilder("transaction-host")
            .StartEvent("Start")
            .SubProcess("Booking", bindingRef: "booking", isTransaction: true)
            .EndEvent("End")
            .ConnectSequence("Start", "Booking", "End")
            .Build();
        var graph = BuildGraph(definition);
        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));
        var booking = LiveWork(started).Single();

        var evaluation = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, Scope(), booking.BindingRef, booking.Handle, [BpmnInterpreter.CancelledOutcomeName])
        {
            ErrorCode = CreditDeclined
        });

        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe(BpmnInterpreter.TransactionCancelledUnhandledFaultCode);
        fault.ErrorCode.ShouldBeNull();
    }

    // --- Diagnostics -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(CreditDeclined, "OnCREDIT_DECLINED", "exact")]
    [InlineData(Timeout, "OnAny", "catch-all")]
    [InlineData(null, "OnAny", "catch-all")]
    public void A_catch_records_the_catcher_the_code_and_how_it_matched(string? errorCode, string catcher, string match)
    {
        var graph = BuildGraph(ChargeWithBoundaries(CreditDeclined, null));

        var evaluation = StartAndFault(graph, "charge", errorCode);

        var caught = evaluation.State.Diagnostics.Where(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ErrorCaught).ShouldHaveSingleItem();
        caught.ElementId.ShouldBe(catcher);
        caught.Details["match"].ShouldBe(match);
        if (errorCode is null)
        {
            caught.Details.ContainsKey("errorCode").ShouldBeFalse();
            caught.Message.ShouldContain("no code");
        }
        else
        {
            caught.Details["errorCode"].ShouldBe(errorCode);
        }
    }

    [Fact]
    public void An_event_subprocess_catch_is_recorded_too()
    {
        var (definition, bodies) = ChargeWithEventSubprocesses([CreditDeclined]);
        var graph = BuildGraph(definition, bodies);

        var evaluation = StartAndFault(graph, "charge", CreditDeclined);

        var caught = evaluation.State.Diagnostics.Where(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ErrorCaught).ShouldHaveSingleItem();
        caught.ElementId.ShouldBe($"Sub{CreditDeclined}");
        caught.Details["errorCode"].ShouldBe(CreditDeclined);
        caught.Details["match"].ShouldBe("exact");
    }

    [Fact]
    public void A_fault_nothing_catches_records_no_catch()
    {
        var graph = BuildGraph(ChargeWithBoundaries(CreditDeclined));

        var evaluation = StartAndFault(graph, "charge", Timeout);

        evaluation.State.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ErrorCaught);
    }

    // --- Graph lookups and cardinality ----------------------------------------------------------------------

    [Fact]
    public void The_graph_resolves_an_error_catcher_by_code_exact_before_catch_all()
    {
        var (definition, bodies) = ChargeWithEventSubprocesses([null, CreditDeclined], boundaries: [null, CreditDeclined]);
        var graph = BuildGraph(definition, bodies);

        graph.AttachedErrorBoundaries("Charge").Select(boundary => boundary.ElementId).ShouldBe(["OnAny", $"On{CreditDeclined}"]);
        graph.AttachedErrorBoundary("Charge", CreditDeclined)!.ElementId.ShouldBe($"On{CreditDeclined}");
        graph.AttachedErrorBoundary("Charge", Timeout)!.ElementId.ShouldBe("OnAny");
        graph.AttachedErrorBoundary("Charge", errorCode: null)!.ElementId.ShouldBe("OnAny");
        graph.ErrorEventSubprocess(CreditDeclined)!.ElementId.ShouldBe($"Sub{CreditDeclined}");
        graph.ErrorEventSubprocess(Timeout)!.ElementId.ShouldBe("SubAny");
        graph.ErrorEventSubprocess(errorCode: null)!.ElementId.ShouldBe("SubAny");
        graph.ErrorEventSubprocessExact(Timeout).ShouldBeNull();
        graph.ErrorCatchAllEventSubprocess()!.ElementId.ShouldBe("SubAny");
    }

    [Theory]
    [InlineData(null, null, "more than one code-less catch-all error boundary")]
    [InlineData(CreditDeclined, CreditDeclined, "more than one error boundary for code 'CREDIT_DECLINED'")]
    public void A_host_with_two_catch_all_or_two_same_code_error_boundaries_is_refused(string? first, string? second, string refusal)
    {
        var definition = new BpmnProcessBuilder("duplicated")
            .StartEvent("Start")
            .Task("Charge", bindingRef: "charge")
            .EndEvent("End")
            .ConnectSequence("Start", "Charge", "End")
            .BoundaryEvent("First", "Charge", Error(first))
            .BoundaryEvent("Second", "Charge", Error(second))
            .EndEvent("FirstEnd")
            .EndEvent("SecondEnd")
            .Connect("First", "FirstEnd")
            .Connect("Second", "SecondEnd")
            .Build();

        Should.Throw<BpmnExecutionException>(() => BuildGraph(definition)).Message.ShouldContain(refusal);
    }

    [Theory]
    [InlineData(null, null, "more than one code-less catch-all error event subprocess")]
    [InlineData(CreditDeclined, CreditDeclined, "more than one error event subprocess for code 'CREDIT_DECLINED'")]
    public void A_scope_with_two_catch_all_or_two_same_code_error_event_subprocesses_is_refused(string? first, string? second, string refusal)
    {
        var definition = new BpmnProcessBuilder("duplicated")
            .StartEvent("Start")
            .Task("Charge", bindingRef: "charge")
            .EndEvent("End")
            .ConnectSequence("Start", "Charge", "End")
            .SubProcess("First", bindingRef: "first", triggeredByEvent: true)
            .SubProcess("Second", bindingRef: "second", triggeredByEvent: true)
            .Build();
        var bodies = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
        {
            ["first"] = Handler("first", first),
            ["second"] = Handler("second", second)
        };

        Should.Throw<BpmnExecutionException>(() => BuildGraph(definition, bodies)).Message.ShouldContain(refusal);
    }

    [Fact]
    public void Distinct_codes_and_one_catch_all_per_host_and_per_scope_build()
    {
        var (definition, bodies) = ChargeWithEventSubprocesses([CreditDeclined, Timeout, null], boundaries: [CreditDeclined, Timeout, null]);

        var graph = BuildGraph(definition, bodies);

        graph.AttachedErrorBoundaries("Charge").Count.ShouldBe(3);
        graph.EventSubprocesses.Count(catcher => catcher.TriggerKind == BpmnEventSubprocessTriggerKind.Error).ShouldBe(3);
    }

    // --- The host port's shape -------------------------------------------------------------------------------

    [Fact]
    public void Error_codes_are_init_only_so_constructors_and_deconstruction_are_unchanged()
    {
        var graph = BuildGraph(ProcessFixtures.Linear());
        var host = Scope();

        var faulted = new BpmnWorkFaultedRequest(graph, null, host, "work", "h-work", "boom") { ErrorCode = CreditDeclined };
        var (_, _, _, faultedBinding, faultedHandle, faultMessage) = faulted;
        (faultedBinding, faultedHandle, faultMessage).ShouldBe(("work", "h-work", "boom"));
        faulted.ErrorCode.ShouldBe(CreditDeclined);
        InitOnly(typeof(BpmnWorkFaultedRequest));

        var completed = new BpmnWorkCompletedRequest(graph, null, host, "work", "h-work", ["Faulted"]) { ErrorCode = CreditDeclined };
        var (_, _, _, completedBinding, completedHandle, outcomes, iteration) = completed;
        (completedBinding, completedHandle, iteration).ShouldBe(("work", "h-work", (string?)null));
        outcomes.ShouldBe(["Faulted"]);
        completed.ErrorCode.ShouldBe(CreditDeclined);
        InitOnly(typeof(BpmnWorkCompletedRequest));

        var fault = new BpmnContinuation.Fault("bpmn.error.thrown", "thrown") { ErrorCode = CreditDeclined };
        var (code, message) = fault;
        (code, message).ShouldBe(("bpmn.error.thrown", "thrown"));
        fault.ErrorCode.ShouldBe(CreditDeclined);
        fault.ShouldNotBe(new BpmnContinuation.Fault("bpmn.error.thrown", "thrown"), "the code is part of the fault's value");
        InitOnly(typeof(BpmnContinuation.Fault));

        new BpmnWorkFaultedRequest(graph, null, host, "work", "h-work").ErrorCode.ShouldBeNull();
        new BpmnWorkCompletedRequest(graph, null, host, "work", "h-work", []).ErrorCode.ShouldBeNull();
        new BpmnContinuation.Fault("code", "message").ErrorCode.ShouldBeNull();
    }

    private static void InitOnly(Type type) =>
        (type.GetProperty("ErrorCode") ?? throw new InvalidOperationException($"{type.Name} has no ErrorCode property."))
            .SetMethod!.ReturnParameter.GetRequiredCustomModifiers().ShouldContain(typeof(IsExternalInit));
}
