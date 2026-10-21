using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;
using static Bpmn.Semantics.Tests.ProcessFixtures;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Condition expressions evaluated through the host's <see cref="IBpmnConditionEvaluator"/> (ADR 0011): which
/// conditions the interpreter asks, in what order, and what the answers mean for routing.
/// <para>
/// These drive the interpreter directly, so the state comes back unpruned and every
/// <see cref="BpmnDiagnosticKind.ConditionEvaluated"/> diagnostic can be read. <see cref="InMemoryConditions"/>
/// stands in for a host's evaluator: it answers by flow id and records what it was asked.
/// </para>
/// </summary>
public sealed class ConditionEvaluationTests
{
    private const BpmnHostCapabilities Evaluating = BpmnHostCapabilities.Full | BpmnHostCapabilities.ConditionEvaluation;

    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    // --- Exclusive gateway ---------------------------------------------------------------------------------

    [Fact]
    public void An_exclusive_gateway_evaluates_in_declaration_order_and_stops_at_the_first_true()
    {
        var conditions = new InMemoryConditions().When("f1", false).When("f2", true).When("f3", true);

        var evaluation = Start(UnboundExclusive(), conditions);

        Arrived(evaluation).ShouldContain("End2");
        Arrived(evaluation).ShouldNotContain("End3");
        conditions.Requests.Select(request => request.FlowId).ShouldBe(["f1", "f2"], "the first true short-circuits: f3 is never asked");
        Evaluations(evaluation).ShouldBe([("Gate", "f1", "False"), ("Gate", "f2", "True")]);
    }

    [Fact]
    public void An_exclusive_gateway_takes_its_default_when_nothing_holds_and_never_evaluates_it()
    {
        var conditions = new InMemoryConditions().When("f1", false).When("f2", false).When("f3", false);

        var evaluation = Start(UnboundExclusive(defaultExpression: "never asked"), conditions);

        Arrived(evaluation).ShouldContain("EndDefault");
        conditions.Requests.Select(request => request.FlowId).ShouldBe(["f1", "f2", "f3"]);
    }

    [Fact]
    public void An_unconditional_flow_on_an_exclusive_gateway_counts_as_true_in_declaration_order()
    {
        var conditions = new InMemoryConditions().When("f1", false);
        var definition = WithExpressions(
            new BpmnProcessBuilder("xor-unconditional")
                .StartEvent("Start")
                .ExclusiveGateway("Gate")
                .EndEvent("End1")
                .EndEvent("End2")
                .EndEvent("End3")
                .EndEvent("EndDefault")
                .Connect("Start", "Gate")
                .Connect("Gate", "End1", flowId: "f1")
                .Connect("Gate", "End2", flowId: "f2")
                .Connect("Gate", "End3", flowId: "f3")
                .Connect("Gate", "EndDefault", isDefault: true, flowId: "fd")
                .Build(),
            ("f1", "amount > 10"), ("f3", "never asked"));

        var evaluation = Start(definition, conditions);

        Arrived(evaluation).ShouldBe(["Gate", "End2"]);
        conditions.Requests.Select(request => request.FlowId).ShouldBe(["f1"]);
    }

    /// <summary>The 0.4.0 change without any expression: a bound decision whose outcome matches nothing used to take its default.</summary>
    [Fact]
    public void A_bound_exclusive_gateway_takes_an_unconditional_flow_before_its_default()
    {
        var definition = new BpmnProcessBuilder("xor-bound-unconditional")
            .StartEvent("Start")
            .Element(new BpmnElement("Decide", BpmnElementTypes.ExclusiveGateway, bindingRef: "decide"))
            .EndEvent("EndPlain")
            .EndEvent("EndApproved")
            .EndEvent("EndDefault")
            .Connect("Start", "Decide")
            .Connect("Decide", "EndPlain", flowId: "plain")
            .Connect("Decide", "EndApproved", condition: "Approved", flowId: "approved")
            .Connect("Decide", "EndDefault", isDefault: true, flowId: "fallback")
            .Build();

        var graph = Build(definition, BpmnHostCapabilities.Full);
        var started = Interpreter.Start(new BpmnStartRequest(graph, null, BpmnHostSnapshot.Root("scope")));
        var completed = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, BpmnHostSnapshot.Root("scope"), "decide", "h-decide", ["Rejected"]));

        completed.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        Arrived(completed).ShouldContain("EndPlain");
        Arrived(completed).ShouldNotContain("EndDefault");
    }

    [Fact]
    public void A_bound_exclusive_gateway_evaluates_nothing_on_arrival_and_its_expressions_with_the_outcome_on_completion()
    {
        var conditions = new InMemoryConditions().When("byExpression", true);
        var definition = WithExpressions(
            new BpmnProcessBuilder("xor-bound")
                .StartEvent("Start")
                .Element(new BpmnElement("Decide", BpmnElementTypes.ExclusiveGateway, bindingRef: "decide"))
                .EndEvent("EndOutcome")
                .EndEvent("EndExpression")
                .Connect("Start", "Decide")
                .Connect("Decide", "EndOutcome", condition: "Approved", flowId: "byOutcome")
                .Connect("Decide", "EndExpression", flowId: "byExpression")
                .Build(),
            ("byExpression", "amount > 10"));
        var graph = Build(definition);

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Snapshot(conditions)));
        conditions.Requests.ShouldBeEmpty("a bound gateway decides when its work completes, not when the token arrives");

        var completed = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, Snapshot(conditions), "decide", "h-decide", ["Rejected"]));

        Arrived(completed).ShouldContain("EndExpression");
        conditions.Requests.ShouldHaveSingleItem().FlowId.ShouldBe("byExpression");

        conditions = new InMemoryConditions();
        var approved = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, Snapshot(conditions), "decide", "h-decide", ["Approved"]));
        Arrived(approved).ShouldContain("EndOutcome");
        conditions.Requests.ShouldBeEmpty("the matched outcome comes first in declaration order, so nothing after it is evaluated");
    }

    [Fact]
    public void An_unbound_exclusive_gateway_still_takes_its_only_flow_when_that_flow_is_outcome_conditioned()
    {
        var definition = new BpmnProcessBuilder("xor-single")
            .StartEvent("Start")
            .ExclusiveGateway("Gate")
            .EndEvent("End")
            .Connect("Start", "Gate")
            .Connect("Gate", "End", condition: "Anything")
            .Build();

        var evaluation = Interpreter.Start(new BpmnStartRequest(Build(definition, BpmnHostCapabilities.Full), null, BpmnHostSnapshot.Root("scope")));

        evaluation.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
    }

    [Fact]
    public void An_unbound_exclusive_gateway_evaluates_its_only_flow_when_that_flow_has_an_expression()
    {
        var definition = WithExpressions(
            new BpmnProcessBuilder("xor-single-expression")
                .StartEvent("Start")
                .ExclusiveGateway("Gate")
                .EndEvent("End")
                .Connect("Start", "Gate")
                .Connect("Gate", "End", flowId: "only")
                .Build(),
            ("only", "ready"));

        var evaluation = Start(definition, new InMemoryConditions().When("only", false));

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.gateway.no-flow-selected");
    }

    // --- Inclusive gateway and activities ------------------------------------------------------------------

    [Fact]
    public void An_inclusive_gateway_takes_every_flow_that_holds_plus_every_unconditional_flow()
    {
        var conditions = new InMemoryConditions().When("f1", true).When("f2", false).When("f3", true);
        var definition = WithExpressions(
            new BpmnProcessBuilder("or")
                .StartEvent("Start")
                .InclusiveGateway("Gate")
                .EndEvent("End1")
                .EndEvent("End2")
                .EndEvent("End3")
                .EndEvent("End4")
                .EndEvent("EndDefault")
                .Connect("Start", "Gate")
                .Connect("Gate", "End1", flowId: "f1")
                .Connect("Gate", "End2", flowId: "f2")
                .Connect("Gate", "End3", flowId: "f3")
                .Connect("Gate", "End4", flowId: "f4")
                .Connect("Gate", "EndDefault", isDefault: true, flowId: "fd")
                .Build(),
            ("f1", "a"), ("f2", "b"), ("f3", "c"), ("fd", "never asked"));

        var evaluation = Start(definition, conditions);

        Arrived(evaluation).ShouldBe(["Gate", "End1", "End3", "End4"], ignoreOrder: true);
        conditions.Requests.Select(request => request.FlowId).ShouldBe(["f1", "f2", "f3"], "every expression, in declaration order, and never the default");
    }

    [Fact]
    public void A_task_takes_every_flow_that_holds_when_its_work_completes()
    {
        var conditions = new InMemoryConditions().When("f1", false).When("f2", true);
        var definition = WithExpressions(
            new BpmnProcessBuilder("task-split")
                .StartEvent("Start")
                .Task("Work", bindingRef: "work")
                .EndEvent("End1")
                .EndEvent("End2")
                .EndEvent("End3")
                .Connect("Start", "Work")
                .Connect("Work", "End1", flowId: "f1")
                .Connect("Work", "End2", flowId: "f2")
                .Connect("Work", "End3", flowId: "f3")
                .Build(),
            ("f1", "a"), ("f2", "b"));
        var graph = Build(definition);

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Snapshot(conditions)));
        var completed = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(graph, started.State, Snapshot(conditions), "work", "h-work", []));

        Arrived(completed, since: started).ShouldBe(["End2", "End3"], ignoreOrder: true);
        Evaluations(completed).ShouldBe([("Work", "f1", "False"), ("Work", "f2", "True")]);
    }

    // --- Outcome precedence --------------------------------------------------------------------------------

    [Fact]
    public void A_flow_with_an_outcome_and_an_expression_is_outcome_matched_and_needs_no_evaluator()
    {
        var definition = WithExpressions(ExclusiveChoice(), ("flow-Decide-Approve", "approved == true"));
        var graph = Build(definition, BpmnHostCapabilities.Full);

        BpmnCapabilityRequirements.Analyze(definition).Required.ShouldBe(BpmnHostCapabilities.None);

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, BpmnHostSnapshot.Root("scope")));
        var completed = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, BpmnHostSnapshot.Root("scope"), "decide", "h-decide", ["Approved"]));

        Arrived(completed).ShouldContain("Approve");
        completed.State.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ConditionEvaluated);
    }

    // --- No match ------------------------------------------------------------------------------------------

    [Fact]
    public void No_match_and_no_default_at_an_exclusive_gateway_faults_the_scope_after_the_evaluations()
    {
        var conditions = new InMemoryConditions().When("f1", false).When("f2", false).When("f3", false);

        var evaluation = Start(UnboundExclusive(withDefault: false), conditions);

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.gateway.no-flow-selected");
        var kinds = evaluation.State.Diagnostics.Select(diagnostic => diagnostic.Kind).ToList();
        kinds.LastIndexOf(BpmnDiagnosticKind.ConditionEvaluated).ShouldBeLessThan(kinds.IndexOf(BpmnDiagnosticKind.Faulted));
        Evaluations(evaluation).Count.ShouldBe(3);
    }

    [Fact]
    public void An_unbound_task_whose_outgoing_flows_all_fail_to_match_faults_instead_of_consuming_its_token()
    {
        var definition = new BpmnProcessBuilder("pass-through")
            .StartEvent("Start")
            .Element(new BpmnElement("Note", BpmnElementTypes.Task))
            .EndEvent("EndA")
            .EndEvent("EndB")
            .Connect("Start", "Note")
            .Connect("Note", "EndA", condition: "A")
            .Connect("Note", "EndB", condition: "B")
            .Build();

        var evaluation = Interpreter.Start(new BpmnStartRequest(Build(definition, BpmnHostCapabilities.Full), null, BpmnHostSnapshot.Root("scope")));

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.flow.none-taken");
    }

    [Fact]
    public void An_unbound_task_whose_expressions_are_all_false_faults()
    {
        var definition = WithExpressions(
            new BpmnProcessBuilder("pass-through-expressions")
                .StartEvent("Start")
                .Element(new BpmnElement("Note", BpmnElementTypes.Task))
                .EndEvent("End")
                .Connect("Start", "Note")
                .Connect("Note", "End", flowId: "f1")
                .Build(),
            ("f1", "ready"));

        var evaluation = Start(definition, new InMemoryConditions().When("f1", false));

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.flow.none-taken");
    }

    [Fact]
    public void A_bound_task_whose_expressions_are_all_false_faults_when_its_work_completes()
    {
        var definition = WithExpressions(
            new BpmnProcessBuilder("bound-expressions")
                .StartEvent("Start")
                .Task("Work", bindingRef: "work")
                .EndEvent("End")
                .Connect("Start", "Work")
                .Connect("Work", "End", flowId: "f1")
                .Build(),
            ("f1", "ready"));
        var conditions = new InMemoryConditions().When("f1", false);
        var graph = Build(definition);

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Snapshot(conditions)));
        var completed = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(graph, started.State, Snapshot(conditions), "work", "h-work", []));

        completed.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.flow.none-taken");
    }

    [Fact]
    public void An_unbound_task_with_no_outgoing_flow_is_still_an_implicit_end()
    {
        var definition = new BpmnProcessBuilder("implicit-end")
            .StartEvent("Start")
            .Element(new BpmnElement("Last", BpmnElementTypes.Task))
            .Connect("Start", "Last")
            .Build();

        var evaluation = Interpreter.Start(new BpmnStartRequest(Build(definition, BpmnHostCapabilities.Full), null, BpmnHostSnapshot.Root("scope")));

        evaluation.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
    }

    // --- Failure -------------------------------------------------------------------------------------------

    [Fact]
    public void A_failed_answer_faults_the_scope_with_the_hosts_code_and_stops_evaluating()
    {
        var conditions = new InMemoryConditions()
            .When("f1", BpmnConditionResult.Failed("orders.region-unknown", "region is not set"))
            .When("f2", true);

        var evaluation = Start(UnboundExclusive(), conditions);

        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe("orders.region-unknown");
        fault.Message.ShouldContain("region is not set");
        fault.Message.ShouldContain("'f1'");
        conditions.Requests.ShouldHaveSingleItem().FlowId.ShouldBe("f1");

        var diagnostic = evaluation.State.Diagnostics.Single(candidate => candidate.Kind == BpmnDiagnosticKind.ConditionEvaluated);
        diagnostic.Details["result"].ShouldBe("Failed");
        diagnostic.Details["faultCode"].ShouldBe("orders.region-unknown");
        diagnostic.Details["failure"].ShouldBe("region is not set");
    }

    [Fact]
    public void A_failed_answer_without_a_code_faults_with_the_library_code()
    {
        var conditions = new InMemoryConditions().When("f1", BpmnConditionResult.Failed(null, "cannot parse"));

        var evaluation = Start(UnboundExclusive(), conditions);

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.condition.failed");
    }

    [Fact]
    public void A_failed_answer_is_a_verdict_that_no_error_boundary_takes()
    {
        var definition = WithExpressions(
            new BpmnProcessBuilder("guarded")
                .StartEvent("Start")
                .Task("Work", bindingRef: "work")
                .EndEvent("End")
                .BoundaryEvent("Caught", "Work", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
                .EndEvent("EndCaught")
                .Connect("Start", "Work")
                .Connect("Work", "End", flowId: "f1")
                .Connect("Caught", "EndCaught")
                .Build(),
            ("f1", "ready"));
        var conditions = new InMemoryConditions().When("f1", BpmnConditionResult.Failed(null, "boom"));
        var graph = Build(definition);

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Snapshot(conditions)));
        var completed = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(graph, started.State, Snapshot(conditions), "work", "h-work", []));

        completed.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.condition.failed");
        completed.ErrorDisposition.ShouldBeNull("a condition failure is not a BPMN error raised by bound work");
        Arrived(completed).ShouldNotContain("EndCaught");
    }

    // --- The request ---------------------------------------------------------------------------------------

    [Fact]
    public void The_request_names_the_element_flow_site_and_body_and_inherits_the_process_language()
    {
        var conditions = new InMemoryConditions(_ => BpmnConditionResult.False);
        var definition = UnboundExclusive() with { ExpressionLanguage = "urn:test:process-language" };
        definition = WithExpressions(definition, ("f2", new BpmnConditionExpression("b", language: "urn:test:own-language")));

        Start(definition, conditions);

        conditions.Requests[0].ShouldBe(new BpmnConditionRequest("Gate", "f1", BpmnConditionSites.SequenceFlow, "a", "urn:test:process-language", null));
        conditions.Requests[1].Language.ShouldBe("urn:test:own-language");
    }

    [Fact]
    public void The_request_language_is_null_when_the_document_declares_none()
    {
        var conditions = new InMemoryConditions(_ => BpmnConditionResult.False);

        Start(UnboundExclusive(), conditions);

        conditions.Requests.ShouldAllBe(request => request.Language == null);
    }

    /// <summary>
    /// A multi-instance activity's instances never route: each completes and is consumed, and the coordinator
    /// routes once after the last. So the routing happens outside any iteration, and the request says so.
    /// </summary>
    [Fact]
    public void A_multi_instance_activity_routes_once_after_its_instances_outside_any_iteration()
    {
        var definition = WithExpressions(
            new BpmnProcessBuilder("mi-routing")
                .StartEvent("Start")
                .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                    loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: 2))
                .EndEvent("End")
                .Connect("Start", "Review")
                .Connect("Review", "End", flowId: "f1")
                .Build(),
            ("f1", "all approved"));
        var conditions = new InMemoryConditions().When("f1", true);

        var instance = new InMemoryBpmnHost(new InMemoryBpmnHostOptions { Conditions = conditions }).Start(definition);
        foreach (var work in instance.PendingWork.ToArray())
            instance.CompleteWork(work);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        conditions.Requests.ShouldHaveSingleItem().Iteration.ShouldBeNull();
    }

    // --- Diagnostics ---------------------------------------------------------------------------------------

    [Fact]
    public void Every_evaluation_is_recorded_before_the_routing_it_led_to()
    {
        var conditions = new InMemoryConditions().When("f1", false).When("f2", true);

        var evaluation = Start(UnboundExclusive(), conditions);

        var diagnostics = evaluation.State.Diagnostics;
        var evaluated = diagnostics.Where(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ConditionEvaluated).ToArray();
        evaluated.Select(diagnostic => diagnostic.FlowId).ShouldBe(["f1", "f2"]);
        evaluated.ShouldAllBe(diagnostic => diagnostic.ElementId == "Gate" && diagnostic.TokenId != null);
        evaluated[0].Message.ShouldContain("evaluated to false");
        evaluated[1].Message.ShouldContain("evaluated to true");

        var routed = diagnostics.ToList().FindIndex(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.TokenEmitted && diagnostic.ElementId == "End2");
        diagnostics.ToList().IndexOf(evaluated[1]).ShouldBeLessThan(routed);
    }

    [Fact]
    public void A_process_without_expressions_records_no_evaluation()
    {
        var instance = new InMemoryBpmnHost().Start(ExclusiveChoice());
        instance.CompleteWork("decide", "Approved");

        instance.State.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ConditionEvaluated);
    }

    // --- The port ------------------------------------------------------------------------------------------

    [Fact]
    public void A_host_that_declares_the_capability_but_supplies_no_evaluator_has_broken_the_port()
    {
        var exception = Should.Throw<BpmnExecutionException>(() => Start(UnboundExclusive(), conditions: null));

        exception.Message.ShouldContain("'Gate'");
        exception.Message.ShouldContain("'f1'");
        exception.Message.ShouldContain(nameof(BpmnHostSnapshot.Conditions));
    }

    [Fact]
    public void An_evaluator_that_throws_surfaces_as_an_execution_exception_carrying_it()
    {
        var thrown = new InvalidOperationException("evaluator down");
        var conditions = new InMemoryConditions(_ => throw thrown);

        var exception = Should.Throw<BpmnExecutionException>(() => Start(UnboundExclusive(), conditions));

        exception.InnerException.ShouldBeSameAs(thrown);
        exception.Message.ShouldContain("'f1'");
    }

    [Fact]
    public void A_behavior_context_built_without_an_evaluator_refuses_to_evaluate()
    {
        var flow = new BpmnSequenceFlow("f1", "Gate", "End") { ConditionExpression = new BpmnConditionExpression("a") };
        var context = new BpmnBehaviorContext(
            BpmnBehaviorTrigger.TokenArrived,
            new BpmnElement("Gate", BpmnElementTypes.ExclusiveGateway),
            new BpmnToken("t1", "Gate"),
            [flow],
            [],
            [],
            new BpmnExecutionState());

        Should.Throw<BpmnExecutionException>(() => context.EvaluateCondition(flow)).Message.ShouldContain("'f1'");
        Should.Throw<BpmnExecutionException>(() => ((IBpmnBehaviorContext)new ContextWithoutEvaluation(context)).EvaluateCondition(flow));
    }

    [Fact]
    public void The_snapshot_keeps_its_positional_constructor_and_deconstruction()
    {
        var snapshot = new BpmnHostSnapshot("scope", false, [], new Dictionary<string, string>(), BpmnNoVariables.Instance, BpmnHostCapabilities.Full);
        var (scope, hasEnclosing, live, correlation, variables, capabilities) = snapshot;

        (scope, hasEnclosing, live.Count, correlation.Count, variables, capabilities)
            .ShouldBe(("scope", false, 0, 0, (IBpmnVariableReader)BpmnNoVariables.Instance, BpmnHostCapabilities.Full));
        snapshot.Conditions.ShouldBeNull();
    }

    // --- Defaults as a read document marks them ------------------------------------------------------------

    /// <summary>
    /// The XML reader marks a default with the element's <c>default</c> reference and leaves the flow's own flag
    /// unset. Such a flow is still the default: it is neither counted as unconditional nor evaluated.
    /// </summary>
    [Fact]
    public void A_default_marked_only_by_the_element_is_never_taken_beside_a_matching_flow()
    {
        var definition = new BpmnProcessDefinition(
            "read-default",
            Elements:
            [
                new BpmnElement("Start", BpmnElementTypes.StartEvent),
                new BpmnElement("Work", BpmnElementTypes.Task, bindingRef: "work", defaultFlowId: "fallback"),
                new BpmnElement("Gate", BpmnElementTypes.ExclusiveGateway, defaultFlowId: "gateFallback"),
                new BpmnElement("EndMatched", BpmnElementTypes.EndEvent),
                new BpmnElement("EndFallback", BpmnElementTypes.EndEvent),
                new BpmnElement("EndGateFallback", BpmnElementTypes.EndEvent)
            ],
            SequenceFlows:
            [
                new BpmnSequenceFlow("toWork", "Start", "Work"),
                new BpmnSequenceFlow("matched", "Work", "Gate", conditionOutcome: "Done"),
                new BpmnSequenceFlow("fallback", "Work", "EndFallback"),
                new BpmnSequenceFlow("gateFallback", "Gate", "EndGateFallback"),
                new BpmnSequenceFlow("gateMatched", "Gate", "EndMatched") { ConditionExpression = new BpmnConditionExpression("ok") }
            ]);
        var conditions = new InMemoryConditions().When("gateMatched", true);
        var graph = Build(definition);

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Snapshot(conditions)));
        var completed = Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(graph, started.State, Snapshot(conditions), "work", "h-work", ["Done"]));

        Arrived(completed, since: started).ShouldBe(["Gate", "EndMatched"]);
        conditions.Requests.Select(request => request.FlowId).ShouldBe(["gateMatched"]);
    }

    // --- Escalation throw caught by its own scope ----------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_throw_an_interrupting_own_scope_catcher_takes_activates_it_whatever_its_onward_condition_answers(bool fails)
    {
        var conditions = new InMemoryConditions().When("onward", fails ? BpmnConditionResult.Failed("host.bad", "no such variable") : BpmnConditionResult.False);

        var evaluation = StartEscalating(WithExpressions(EscalatingToOwnScope(), ("onward", "ready")), conditions, interrupting: true);

        evaluation.Continuation.ShouldBe(BpmnContinuation.Defer.Instance, string.Join("\n", evaluation.State.Diagnostics.Select(d => d.Message)));
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().Select(command => command.BindingRef).ShouldBe(["on-overdue"],
            "the interrupting catcher stops the throw's successor anyway, so its routing cannot fault the scope");
        Evaluations(evaluation).ShouldBe([("Escalate", "onward", fails ? "Failed" : "False")], "the answer was asked and is still on the trace");
    }

    [Fact]
    public void A_throw_whose_outcome_only_flow_cannot_match_still_activates_an_interrupting_own_scope_catcher()
    {
        var evaluation = StartEscalating(EscalatingToOwnScope(onwardOutcome: "Never"), conditions: null, interrupting: true);

        evaluation.Continuation.ShouldBe(BpmnContinuation.Defer.Instance);
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().Select(command => command.BindingRef).ShouldBe(["on-overdue"]);
    }

    [Fact]
    public void A_throw_a_non_interrupting_own_scope_catcher_takes_still_faults_when_it_takes_no_flow()
    {
        var conditions = new InMemoryConditions().When("onward", false);

        var evaluation = StartEscalating(WithExpressions(EscalatingToOwnScope(), ("onward", "ready")), conditions, interrupting: false);

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.flow.none-taken",
            "a non-interrupting catcher leaves the throw's successor running, so its routing still decides");
    }

    // --- Fixtures and helpers ------------------------------------------------------------------------------

    /// <summary>
    /// An escalation throw whose only onward flow is <c>onward</c>, caught by an event subprocess of the same scope.
    /// </summary>
    private static BpmnProcessDefinition EscalatingToOwnScope(string? onwardOutcome = null) =>
        new BpmnProcessBuilder("escalating-to-own-scope")
            .StartEvent("Start")
            .IntermediateThrowEvent("Escalate", Escalation("overdue"))
            .EndEvent("End")
            .Element(new BpmnElement("OnOverdue", BpmnElementTypes.SubProcess, bindingRef: "on-overdue", triggeredByEvent: true))
            .Connect("Start", "Escalate")
            .Connect("Escalate", "End", condition: onwardOutcome, flowId: "onward")
            .Build();

    /// <summary>Starts <paramref name="definition"/> with its <c>on-overdue</c> event subprocess body supplied.</summary>
    private static BpmnEvaluation StartEscalating(BpmnProcessDefinition definition, IBpmnConditionEvaluator? conditions, bool interrupting)
    {
        var body = new BpmnProcessBuilder("on-overdue")
            .Element(new BpmnElement("HandlerStart", BpmnElementTypes.StartEvent, cancelActivity: interrupting, eventDefinitions: [Escalation("overdue")]))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("HandlerStart", "Handle", "HandlerEnd")
            .Build();
        var graph = BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition, bindingRef => bindingRef == "on-overdue" ? body : null), Evaluating);
        return Interpreter.Start(new BpmnStartRequest(graph, null, Snapshot(conditions)));
    }

    /// <summary>Start -> Gate, an unbound exclusive gateway with expression flows f1, f2, f3 to End1..End3, and a default fd.</summary>
    private static BpmnProcessDefinition UnboundExclusive(bool withDefault = true, string? defaultExpression = null)
    {
        var builder = new BpmnProcessBuilder("xor")
            .StartEvent("Start")
            .ExclusiveGateway("Gate")
            .EndEvent("End1")
            .EndEvent("End2")
            .EndEvent("End3")
            .Connect("Start", "Gate")
            .Connect("Gate", "End1", flowId: "f1")
            .Connect("Gate", "End2", flowId: "f2")
            .Connect("Gate", "End3", flowId: "f3");
        if (withDefault)
            builder.EndEvent("EndDefault").Connect("Gate", "EndDefault", isDefault: true, flowId: "fd");

        var definition = WithExpressions(builder.Build(), ("f1", "a"), ("f2", "b"), ("f3", "c"));
        return defaultExpression is null ? definition : WithExpressions(definition, ("fd", defaultExpression));
    }

    private static BpmnGraph Build(BpmnProcessDefinition definition, BpmnHostCapabilities capabilities = Evaluating) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), capabilities);

    private static BpmnHostSnapshot Snapshot(IBpmnConditionEvaluator? conditions) =>
        BpmnHostSnapshot.Root("scope", Evaluating) with { Conditions = conditions };

    private static BpmnEvaluation Start(BpmnProcessDefinition definition, IBpmnConditionEvaluator? conditions) =>
        Interpreter.Start(new BpmnStartRequest(Build(definition), null, Snapshot(conditions)));

    /// <summary>The elements tokens arrived at over sequence flows, in order; with <paramref name="since"/>, only after that evaluation.</summary>
    private static IReadOnlyList<string> Arrived(BpmnEvaluation evaluation, BpmnEvaluation? since = null) =>
        evaluation.State.Diagnostics
            .Skip(since?.State.Diagnostics.Count ?? 0)
            .Where(diagnostic => diagnostic is { Kind: BpmnDiagnosticKind.TokenEmitted, FlowId: not null })
            .Select(diagnostic => diagnostic.ElementId!)
            .ToArray();

    /// <summary>Each recorded evaluation as (element, flow, result).</summary>
    private static IReadOnlyList<(string ElementId, string FlowId, string Result)> Evaluations(BpmnEvaluation evaluation) =>
        evaluation.State.Diagnostics
            .Where(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ConditionEvaluated)
            .Select(diagnostic => (diagnostic.ElementId!, diagnostic.FlowId!, diagnostic.Details["result"]))
            .ToArray();

    /// <summary>A context implemented before condition evaluation existed: it relies on the interface's default.</summary>
    private sealed class ContextWithoutEvaluation(IBpmnBehaviorContext inner) : IBpmnBehaviorContext
    {
        public BpmnBehaviorTrigger Trigger => inner.Trigger;
        public BpmnElement Element => inner.Element;
        public BpmnToken Token => inner.Token;
        public IReadOnlyCollection<BpmnSequenceFlow> OutboundFlows => inner.OutboundFlows;
        public IReadOnlyCollection<BpmnSequenceFlow> InboundFlows => inner.InboundFlows;
        public IReadOnlyCollection<string> OutcomeNames => inner.OutcomeNames;
        public BpmnExecutionState State => inner.State;
    }
}
