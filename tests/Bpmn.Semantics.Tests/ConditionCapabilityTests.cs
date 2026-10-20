using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;
using static Bpmn.Semantics.Tests.ProcessFixtures;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// What a definition with condition expressions asks of its host before anything runs, and what the reference
/// host does with them: the <see cref="BpmnHostCapabilities.ConditionEvaluation"/> capability, the refusal of a
/// host without it, and every routing site the interpreter evaluates at besides the gateways and tasks
/// <see cref="ConditionEvaluationTests"/> covers.
/// </summary>
public sealed class ConditionCapabilityTests
{
    private const BpmnHostCapabilities Evaluating = BpmnHostCapabilities.Full | BpmnHostCapabilities.ConditionEvaluation;

    // --- The capability ------------------------------------------------------------------------------------

    [Fact]
    public void Full_does_not_promise_condition_evaluation()
    {
        (BpmnHostCapabilities.Full & BpmnHostCapabilities.ConditionEvaluation).ShouldBe(BpmnHostCapabilities.None);
        BpmnHostSnapshot.Root("scope").Capabilities.HasFlag(BpmnHostCapabilities.ConditionEvaluation).ShouldBeFalse();
    }

    [Fact]
    public void Analysis_reports_every_expression_only_flow_and_no_outcome_matched_or_default_flow()
    {
        var requirements = BpmnCapabilityRequirements.Analyze(Gate());

        requirements.Required.ShouldBe(BpmnHostCapabilities.ConditionEvaluation);
        requirements.DrivingElementIds[BpmnHostCapabilities.ConditionEvaluation].ShouldBe(["byExpression", "second"]);

        // A read document marks its default on the element only; that flow is still never evaluated.
        var readDefault = new BpmnProcessDefinition(
            "read-default",
            Elements:
            [
                new BpmnElement("Start", BpmnElementTypes.StartEvent),
                new BpmnElement("Gate", BpmnElementTypes.ExclusiveGateway, defaultFlowId: "fallback"),
                new BpmnElement("End", BpmnElementTypes.EndEvent)
            ],
            SequenceFlows:
            [
                new BpmnSequenceFlow("toGate", "Start", "Gate"),
                new BpmnSequenceFlow("fallback", "Gate", "End") { ConditionExpression = new BpmnConditionExpression("never") }
            ]);
        BpmnCapabilityRequirements.Analyze(readDefault).ShouldBe(BpmnCapabilityRequirements.None);
    }

    [Fact]
    public void A_host_without_the_capability_is_refused_at_graph_build_naming_the_flows()
    {
        var refusal = Should.Throw<BpmnCapabilityException>(() =>
            BpmnGraph.Build(Gate(), BpmnBoundWork.Derive(Gate()), BpmnHostCapabilities.Full));

        refusal.Missing.ShouldBe(BpmnHostCapabilities.ConditionEvaluation);
        refusal.DrivingElementIds.ShouldBe(["byExpression", "second"]);
        refusal.Message.ShouldContain("ConditionEvaluation (needed by 'byExpression', 'second')");

        Should.NotThrow(() => BpmnGraph.Build(Gate(), BpmnBoundWork.Derive(Gate()), Evaluating));
    }

    [Fact]
    public void An_event_based_gateway_flow_cannot_carry_an_expression()
    {
        var definition = WithExpressions(
            new BpmnProcessBuilder("race")
                .StartEvent("Start")
                .EventBasedGateway("Race")
                .IntermediateCatchEvent("OnTimer", Timer("PT1H"), bindingRef: "on-timer")
                .IntermediateCatchEvent("OnMessage", Message("stop"), bindingRef: "on-message")
                .EndEvent("End")
                .Connect("Start", "Race")
                .Connect("Race", "OnTimer", flowId: "toTimer")
                .Connect("Race", "OnMessage")
                .Connect("OnTimer", "End")
                .Connect("OnMessage", "End")
                .Build(),
            ("toTimer", "soon"));

        var refusal = Should.Throw<BpmnExecutionException>(() => BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), Evaluating));

        refusal.ShouldNotBeOfType<BpmnCapabilityException>();
        refusal.Message.ShouldContain("'toTimer' cannot carry a condition");
    }

    // --- The reference host --------------------------------------------------------------------------------

    [Fact]
    public void The_reference_host_declares_the_capability_only_when_it_has_an_evaluator()
    {
        new InMemoryBpmnHost().Capabilities.HasFlag(BpmnHostCapabilities.ConditionEvaluation).ShouldBeFalse();
        new InMemoryBpmnHost(new InMemoryBpmnHostOptions { Conditions = new InMemoryConditions() })
            .Capabilities.ShouldBe(Evaluating);

        Should.Throw<BpmnCapabilityException>(() => new InMemoryBpmnHost().Start(Gate()))
            .Missing.ShouldBe(BpmnHostCapabilities.ConditionEvaluation);
    }

    [Fact]
    public void The_reference_host_routes_on_its_evaluators_answers()
    {
        var conditions = new InMemoryConditions().When("byExpression", true);
        var instance = new InMemoryBpmnHost(new InMemoryBpmnHostOptions { Conditions = conditions }).Start(Gate());

        instance.CompleteWork("decide", "Rejected");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        conditions.Requests.ShouldHaveSingleItem().FlowId.ShouldBe("byExpression");
    }

    [Fact]
    public void A_flow_the_reference_evaluator_has_no_answer_for_faults_the_scope()
    {
        var instance = new InMemoryBpmnHost(new InMemoryBpmnHostOptions { Conditions = new InMemoryConditions() }).Start(Gate());

        instance.CompleteWork("decide", "Rejected");

        instance.Fault.ShouldNotBeNull().Code.ShouldBe("bpmn.condition.failed");
        instance.Fault.Message.ShouldContain("No answer is configured for the condition on flow 'byExpression'");
    }

    [Fact]
    public void A_nested_body_asks_the_same_evaluator()
    {
        var body = WithExpressions(
            new BpmnProcessBuilder("body")
                .StartEvent("BodyStart")
                .ExclusiveGateway("BodyGate")
                .EndEvent("BodyEnd")
                .EndEvent("BodyOther")
                .Connect("BodyStart", "BodyGate")
                .Connect("BodyGate", "BodyEnd", flowId: "inBody")
                .Connect("BodyGate", "BodyOther", isDefault: true)
                .Build(),
            ("inBody", "ready"));
        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("End")
            .ConnectSequence("Start", "Sub", "End")
            .Build();
        var conditions = new InMemoryConditions().When("inBody", true);

        var instance = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            Conditions = conditions,
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["sub"] = body }
        }).Start(parent);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        conditions.Requests.ShouldHaveSingleItem().ElementId.ShouldBe("BodyGate");
    }

    [Fact]
    public void A_simulation_routes_expressions_through_its_evaluator()
    {
        var conditions = new InMemoryConditions().When("byExpression", true);

        var result = BpmnSimulation.CanComplete(Gate(), strategy: BpmnOutcomeStrategy.FromDelegate(_ => ["Rejected"]),
            options: new BpmnSimulationOptions { Conditions = conditions });

        result.Completed.ShouldBeTrue(result.ToString());
        result.Path.ShouldContain("EndExpression");
    }

    // --- Other routing sites -------------------------------------------------------------------------------

    [Fact]
    public void A_compensate_throw_evaluates_its_outgoing_conditions_after_the_replay()
    {
        var definition = WithExpressions(
            new BpmnProcessBuilder("compensate")
                .StartEvent("Start")
                .Task("Reserve", bindingRef: "reserve")
                .Element(new BpmnElement(
                    "ReserveCompensation", BpmnElementTypes.BoundaryEvent,
                    attachedToRef: "Reserve",
                    compensationHandlerElementId: "UndoReserve",
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation)]))
                .Element(new BpmnElement("UndoReserve", BpmnElementTypes.Task, bindingRef: "undo-reserve", isForCompensation: true))
                .IntermediateThrowEvent("Replay", new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation))
                .EndEvent("EndChecked")
                .EndEvent("EndOther")
                .ConnectSequence("Start", "Reserve", "Replay")
                .Connect("Replay", "EndChecked", flowId: "checked")
                .Connect("Replay", "EndOther", isDefault: true)
                .Build(),
            ("checked", "undone"));
        var conditions = new InMemoryConditions().When("checked", true);
        var instance = new InMemoryBpmnHost(new InMemoryBpmnHostOptions { Conditions = conditions }).Start(definition);

        instance.CompleteWork("reserve");
        conditions.Requests.ShouldBeEmpty("the throw routes only once its replay has finished");
        instance.CompleteWork("undo-reserve");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        conditions.Requests.ShouldHaveSingleItem().ElementId.ShouldBe("Replay");
        instance.State.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Kind == BpmnDiagnosticKind.ConditionEvaluated && diagnostic.FlowId == "checked");
    }

    [Fact]
    public void An_escalation_throw_that_takes_no_flow_still_raises_its_escalation_and_then_faults()
    {
        var definition = WithExpressions(
            new BpmnProcessBuilder("escalate")
                .StartEvent("Start")
                .IntermediateThrowEvent("Raise", Escalation("up"))
                .EndEvent("End")
                .ConnectSequence("Start", "Raise")
                .Connect("Raise", "End", flowId: "onward")
                .Build(),
            ("onward", "proceed"));

        var evaluation = BpmnInterpreter.CreateDefault().Start(new BpmnStartRequest(
            BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), Evaluating),
            null,
            BpmnHostSnapshot.Root("scope", Evaluating) with { Conditions = new InMemoryConditions().When("onward", false) }));

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.flow.none-taken");
        var kinds = evaluation.State.Diagnostics.Select(diagnostic => diagnostic.Kind).ToList();
        kinds.IndexOf(BpmnDiagnosticKind.ConditionEvaluated).ShouldBeLessThan(kinds.IndexOf(BpmnDiagnosticKind.EscalationUnhandled));
        kinds.IndexOf(BpmnDiagnosticKind.EscalationUnhandled).ShouldBeLessThan(kinds.IndexOf(BpmnDiagnosticKind.Faulted));
    }

    // --- Fixtures ------------------------------------------------------------------------------------------

    /// <summary>
    /// A bound decision gateway with an outcome flow that also carries an expression, two expression-only
    /// flows, and a default that carries an expression too. Only the expression-only flows need evaluating.
    /// </summary>
    private static BpmnProcessDefinition Gate() =>
        WithExpressions(
            new BpmnProcessBuilder("gate")
                .StartEvent("Start")
                .Element(new BpmnElement("Decide", BpmnElementTypes.ExclusiveGateway, bindingRef: "decide"))
                .EndEvent("EndOutcome")
                .EndEvent("EndExpression")
                .EndEvent("EndSecond")
                .EndEvent("EndDefault")
                .Connect("Start", "Decide")
                .Connect("Decide", "EndOutcome", condition: "Approved", flowId: "byOutcome")
                .Connect("Decide", "EndExpression", flowId: "byExpression")
                .Connect("Decide", "EndSecond", flowId: "second")
                .Connect("Decide", "EndDefault", isDefault: true, flowId: "fallback")
                .Build(),
            ("byOutcome", "approved == true"), ("byExpression", "amount > 10"), ("second", "amount > 99"), ("fallback", "never"));
}
