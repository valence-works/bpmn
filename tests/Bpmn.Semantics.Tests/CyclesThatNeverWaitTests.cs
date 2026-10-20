using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;
using static Bpmn.Semantics.Tests.ProcessFixtures;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// A cycle that never waits, refused at graph build when the definition shows it, and stopped by the
/// per-evaluation propagation limit when only a run shows it.
/// <para>
/// Before this, nothing bounded either. A token entering a cycle of gateways and unbound tasks was routed around
/// it inside one evaluation for good, so the host never got control back. Both halves are tested in both
/// directions, because each has a quiet way to be wrong: a refusal that is too eager rejects a definition that
/// would have run, and a limit that answers anything but a fault leaves a scope that looks as if it is waiting.
/// </para>
/// </summary>
public sealed class CyclesThatNeverWaitTests
{
    /// <summary>
    /// The limit the tests that reach it on purpose run with. Reaching the published one takes seconds, and what
    /// is tested here, where the evaluation stops and what it answers, does not depend on the number.
    /// </summary>
    private const int TestLimit = 100;

    private static readonly BpmnInterpreter Interpreter = new(BpmnBehaviorRegistry.CreateDefault(), TestLimit);

    /// <summary>A definition, and the event subprocess bodies graph build needs to read alongside it.</summary>
    private sealed record Shape(BpmnProcessDefinition Definition, IReadOnlyDictionary<string, BpmnProcessDefinition>? Nested = null);

    /// <summary>What the graph is built with: everything, condition evaluation included, so a shape is judged on its cycles alone.</summary>
    private const BpmnHostCapabilities Capabilities = BpmnHostCapabilities.Full | BpmnHostCapabilities.ConditionEvaluation;

    private static BpmnGraph Build(Shape shape) =>
        BpmnGraph.Build(
            shape.Definition,
            BpmnBoundWork.Derive(shape.Definition, bindingRef => shape.Nested?.GetValueOrDefault(bindingRef)),
            Capabilities);

    /// <summary>Runs the start evaluation directly, so the state comes back unpruned.</summary>
    private static BpmnEvaluation Start(BpmnProcessDefinition definition, IBpmnVariableReader? variables = null, bool hasEnclosingScope = false) =>
        Interpreter.Start(new BpmnStartRequest(
            Build(new Shape(definition)),
            PriorState: null,
            BpmnHostSnapshot.Root("scope") with { Variables = variables ?? BpmnNoVariables.Instance, HasEnclosingScope = hasEnclosingScope }));

    private static InMemoryProcessInstance Run(Shape shape) =>
        new InMemoryBpmnHost(new InMemoryBpmnHostOptions { NestedProcesses = shape.Nested }).Start(shape.Definition);

    private static int Dispatched(BpmnExecutionState state) => state.Tokens.Count(token => token.Status == BpmnTokenStatus.Consumed);

    // --- Refused at graph build ----------------------------------------------------------------------------

    public static TheoryData<string> CyclesThatCannotWait() => [.. Refused.Keys];

    [Theory]
    [MemberData(nameof(CyclesThatCannotWait))]
    public void A_cycle_on_which_nothing_can_hold_a_token_is_refused_at_graph_build_naming_the_cycle(string shape)
    {
        var (candidate, expectedCycle) = Refused[shape]();

        var refusal = Should.Throw<BpmnExecutionException>(() => Build(candidate));

        refusal.ShouldNotBeOfType<BpmnCapabilityException>("this is a structural refusal, not a missing capability");
        refusal.Message.ShouldContain($"has a cycle that can never wait: {expectedCycle}.");
    }

    [Fact]
    public void A_refusal_names_the_conditional_flow_an_unbound_gateway_on_the_cycle_never_takes()
    {
        var (gatewayLoop, _) = Refused["gateways and an unbound task"]();
        var (selfLoop, _) = Refused["an unbound task looping on itself"]();

        Should.Throw<BpmnExecutionException>(() => Build(gatewayLoop)).Message
            .ShouldContain("'Check' binds no decision, so its conditional flow 'flow-Check-End' is never taken.");
        Should.Throw<BpmnExecutionException>(() => Build(selfLoop)).Message
            .ShouldNotContain("never taken", customMessage: "a cycle with no conditional flow has nothing to explain");
    }

    /// <summary>Each shape, and the cycle its refusal must name: in flow order, opening where the loop-back flow lands.</summary>
    private static readonly IReadOnlyDictionary<string, Func<(Shape, string)>> Refused =
        new Dictionary<string, Func<(Shape, string)>>(StringComparer.Ordinal)
        {
            ["gateways and an unbound task"] = () => (
                new Shape(LoopThrough("Note", "Note", builder => builder.Element(new BpmnElement("Note", BpmnElementTypes.Task)))),
                "'Merge' -> 'Note' -> 'Check' -> 'Merge'"),
            ["an unbound task looping on itself"] = () => (
                new Shape(new BpmnProcessBuilder("spin")
                    .StartEvent("Start")
                    .Element(new BpmnElement("Spin", BpmnElementTypes.Task))
                    .Connect("Start", "Spin")
                    .Connect("Spin", "Spin")
                    .Build()),
                "'Spin' -> 'Spin'"),
            ["a parallel split that also leaves the cycle"] = () => (
                new Shape(LoopThrough("Fork", "Fork", builder => builder
                    .ParallelGateway("Fork")
                    .EndEvent("Elsewhere")
                    .Connect("Fork", "Elsewhere"))),
                "'Merge' -> 'Fork' -> 'Check' -> 'Merge'"),
            ["an unbound inclusive split"] = () => (
                new Shape(LoopThrough("Or", "Or", builder => builder.InclusiveGateway("Or"))),
                "'Merge' -> 'Or' -> 'Check' -> 'Merge'"),
            ["an escalation throw nothing in the scope catches"] = () => (
                new Shape(LoopThrough("Raise", "Raise", builder => builder.IntermediateThrowEvent("Raise", Escalation("again")))),
                "'Merge' -> 'Raise' -> 'Check' -> 'Merge'"),
            // Starts the handler's body every pass, which looks like waiting and is not: the throw's own token goes on.
            ["an escalation throw a non-interrupting event subprocess catches"] = () => (
                EscalationLoop(interrupting: false),
                "'Merge' -> 'Raise' -> 'Check' -> 'Merge'"),
            // A task splits: the unconditional flow back is taken whatever the expression beside it answers.
            ["an unbound task with an unconditional flow back beside an expression exit"] = () => (
                new Shape(WithExpressions(
                    new BpmnProcessBuilder("spin-with-exit")
                        .StartEvent("Start")
                        .Element(new BpmnElement("Spin", BpmnElementTypes.Task))
                        .EndEvent("End")
                        .Connect("Start", "Spin")
                        .Connect("Spin", "Spin")
                        .Connect("Spin", "End", flowId: "exit")
                        .Build(),
                    ("exit", "done"))),
                "'Spin' -> 'Spin'"),
            // An unconditional flow counts as true, so an exclusive gateway takes it before any flow declared after it.
            ["an exclusive gateway with an unconditional flow back declared before its exit"] = () => (
                new Shape(ExpressionCheckLoop(loopBackFirst: true)),
                "'Merge' -> 'Note' -> 'Check' -> 'Merge'")
        };

    // --- Accepted at graph build ---------------------------------------------------------------------------

    public static TheoryData<string> CyclesThatCanWait() => [.. Accepted.Keys];

    [Theory]
    [MemberData(nameof(CyclesThatCanWait))]
    public void A_cycle_with_something_on_it_that_can_hold_a_token_builds(string shape) =>
        Should.NotThrow(() => Build(Accepted[shape]()));

    private static readonly IReadOnlyDictionary<string, Func<Shape>> Accepted =
        new Dictionary<string, Func<Shape>>(StringComparer.Ordinal)
        {
            ["a task that binds work"] = () => new Shape(LoopThrough("Work", "Work", builder => builder.Task("Work", bindingRef: "work"))),
            ["a receive task"] = () => new Shape(LoopThrough("Receive", "Receive", builder => builder.ReceiveTask("Receive"))),
            ["a call activity"] = () => new Shape(LoopThrough("Call", "Call", builder => builder.CallActivity("Call", "other-process"))),
            ["a subprocess"] = () => new Shape(LoopThrough("Sub", "Sub", builder => builder.SubProcess("Sub"))),
            ["a catch event"] = () => new Shape(LoopThrough("Wait", "Wait", builder => builder.IntermediateCatchEvent("Wait", Timer("PT1H"), bindingRef: "wait"))),
            ["a message throw event"] = () => new Shape(LoopThrough("Send", "Send", builder => builder.IntermediateThrowEvent("Send", Message("ping"), bindingRef: "send"))),
            ["a decision gateway that binds work"] = () => new Shape(LoopThrough("Decide", "Decide", builder =>
                builder.Element(new BpmnElement("Decide", BpmnElementTypes.ExclusiveGateway, bindingRef: "decide")))),
            ["an event-based gateway"] = () => new Shape(LoopThrough("Race", "OnTimer", builder => builder
                .EventBasedGateway("Race")
                .IntermediateCatchEvent("OnTimer", Timer("PT1H"), bindingRef: "on-timer")
                .IntermediateCatchEvent("OnMessage", Message("stop"), bindingRef: "on-message")
                .EndEvent("Stopped")
                .Connect("Race", "OnTimer")
                .Connect("Race", "OnMessage")
                .Connect("OnMessage", "Stopped"))),
            // Conservative: the cycle satisfies this join by itself, so it never actually waits. The limit stops it.
            ["a parallel join"] = () => new Shape(JoinLoop()),
            // Conservative: it waits while handlers run, and only when something is registered to compensate.
            ["a compensate throw event"] = () => new Shape(CompensateLoop()),
            // The activation stops every other token in the scope, the throw's own successor included.
            ["an escalation throw an interrupting event subprocess catches"] = () => EscalationLoop(interrupting: true),
            // The flow back is conditional, and a token arriving at an unbound gateway reports no outcome.
            ["a loop-back flow the routing never takes"] = () => new Shape(LoopBackNeverTaken()),
            // The way out is an expression the host answers, so the default back is possibly not taken.
            ["an exclusive gateway whose way off the cycle is an expression"] = () => new Shape(ExpressionExitLoop()),
            // The expression is asked first, so the unconditional flow back is taken only when it answers false.
            ["an exclusive gateway with an expression exit declared before its unconditional flow back"] = () =>
                new Shape(ExpressionCheckLoop(loopBackFirst: false)),
            ["an unbound task whose only flow back is an expression"] = () => new Shape(WithExpressions(
                new BpmnProcessBuilder("spin-on-expression")
                    .StartEvent("Start")
                    .Element(new BpmnElement("Spin", BpmnElementTypes.Task))
                    .EndEvent("End")
                    .Connect("Start", "Spin")
                    .Connect("Spin", "Spin", flowId: "again")
                    .Connect("Spin", "End", isDefault: true)
                    .Build(),
                ("again", "more to do")))
        };

    // --- Accepted cycles behave as accepted ----------------------------------------------------------------

    [Fact]
    public void A_cycle_through_bound_work_goes_around_once_per_evaluation_until_it_routes_out()
    {
        var instance = Run(new Shape(RetryUntilDone()));

        for (var pass = 0; pass < 3; pass++)
            instance.CompleteWork("work");
        instance.CompleteWork("work", "Done");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Transcript.Take(4).ShouldAllBe(evaluation =>
            evaluation.Continuation == BpmnContinuation.Defer.Instance
            && evaluation.Commands.OfType<BpmnHostCommand.StartWork>().Count() == 1);
    }

    [Fact]
    public void An_interrupting_own_scope_catch_really_stops_the_cycle_its_throw_is_on()
    {
        var instance = Run(EscalationLoop(interrupting: true));

        instance.IsFaulted.ShouldBeFalse(instance.Transcript.ToString());
        instance.Children.ShouldHaveSingleItem().CompleteWork("handle");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void A_cycle_whose_exit_is_an_expression_goes_around_until_the_host_answers_true()
    {
        var asked = 0;
        var conditions = new InMemoryConditions().When("exit", _ => BpmnConditionResult.Of(++asked == 3));

        var instance = new InMemoryBpmnHost(new InMemoryBpmnHostOptions { Conditions = conditions }).Start(ExpressionExitLoop());

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        conditions.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public void A_loop_back_flow_the_routing_never_takes_completes_without_going_around()
    {
        var instance = Run(new Shape(LoopBackNeverTaken()));

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    // --- The runtime limit ---------------------------------------------------------------------------------

    [Fact]
    public void An_interpreter_a_host_creates_stops_at_the_published_limit()
    {
        BpmnInterpreter.PropagationStepLimit.ShouldBe(10_000, "the documentation states the number");
        BpmnInterpreter.CreateDefault().EffectivePropagationStepLimit.ShouldBe(BpmnInterpreter.PropagationStepLimit);
        new BpmnInterpreter(BpmnBehaviorRegistry.CreateDefault()).EffectivePropagationStepLimit.ShouldBe(BpmnInterpreter.PropagationStepLimit);
    }

    [Fact]
    public void A_fault_at_the_limit_carries_no_commands_and_drops_the_signals_its_cycle_raised()
    {
        var evaluation = Start(EscalatingJoinLoop(), hasEnclosingScope: true);

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe(BpmnInterpreter.PropagationLimitFaultCode);
        evaluation.Commands.ShouldBeEmpty("one escalation per pass would otherwise reach the parent");

        var raised = evaluation.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EscalationRaised);
        raised.ShouldBeGreaterThan(1, "the fixture must really raise an escalation on more than one pass");
        evaluation.State.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Kind == BpmnDiagnosticKind.Canceled && diagnostic.Message.StartsWith($"BPMN dropped {raised} escalation signal(s)"));
    }

    [Fact]
    public void Any_other_fault_still_carries_the_signal_its_evaluation_raised()
    {
        var evaluation = Start(EscalateThenFault(), hasEnclosingScope: true);

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe("bpmn.gateway.no-flow-selected");
        evaluation.Commands.ShouldHaveSingleItem().ShouldBeOfType<BpmnHostCommand.SignalEnclosingScope>()
            .Code.ShouldBe(BpmnInterpreter.EscalationSignalCode, "the escalation happened before the fault, so the parent still hears it");
    }

    [Fact]
    public void A_cycle_through_a_join_it_always_satisfies_itself_builds_and_then_faults_at_the_limit()
    {
        var evaluation = Start(JoinLoop());

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>(
                "an evaluation that has not come to rest must not read as Defer, a scope apparently waiting")
            .Code.ShouldBe(BpmnInterpreter.PropagationLimitFaultCode);
    }

    [Fact]
    public void A_cycle_that_waits_on_paper_but_not_on_this_run_faults_after_exactly_the_limit()
    {
        var reviewers = new InMemoryVariables().SetCollection("reviewers", Array.Empty<string>());

        var evaluation = Start(ReviewUntilDone(), reviewers);

        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>(
            "an evaluation that has not come to rest must not read as Defer, a scope apparently waiting");
        fault.Code.ShouldBe(BpmnInterpreter.PropagationLimitFaultCode);
        evaluation.Commands.ShouldBeEmpty("a fault continuation carries no StartWork");

        // The start event, then Review and Check on every pass. Each dispatch consumes the token it was handed,
        // so the count of consumed tokens is the count of dispatches.
        Dispatched(evaluation.State).ShouldBe(TestLimit);
        var last = TestLimit % 2 == 0 ? "Review" : "Check";
        fault.Message.ShouldContain($"the last to element '{last}'");
        var diagnostic = evaluation.State.Diagnostics.Last();
        diagnostic.Kind.ShouldBe(BpmnDiagnosticKind.Faulted);
        diagnostic.ElementId.ShouldBe(last);
    }

    [Fact]
    public void An_evaluation_that_comes_to_rest_after_exactly_the_limit_completes()
    {
        var evaluation = Start(FanOutDispatching(TestLimit));

        evaluation.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        Dispatched(evaluation.State).ShouldBe(TestLimit, "the fixture must really dispatch exactly the limit");
    }

    // --- Fixtures ------------------------------------------------------------------------------------------

    /// <summary>
    /// Start -> Merge -> [entry .. exit] -> Check, where the unbound Check gateway always takes its default flow
    /// back to Merge. Whether the cycle can wait is decided by what <paramref name="addBody"/> puts between
    /// <paramref name="entry"/> and <paramref name="exit"/>.
    /// </summary>
    private static BpmnProcessDefinition LoopThrough(string entry, string exit, Func<BpmnProcessBuilder, BpmnProcessBuilder> addBody) =>
        addBody(new BpmnProcessBuilder("loop")
                .StartEvent("Start")
                .ExclusiveGateway("Merge")
                .ExclusiveGateway("Check")
                .EndEvent("End"))
            .Connect("Start", "Merge")
            .Connect("Merge", entry)
            .Connect(exit, "Check")
            .Connect("Check", "End", condition: "Done")
            .Connect("Check", "Merge", isDefault: true)
            .Build();

    /// <summary>A parallel split into two unbound tasks and the join that merges them, on a cycle.</summary>
    private static BpmnProcessDefinition JoinLoop() =>
        LoopThrough("Split", "Join", builder => builder
            .ParallelGateway("Split")
            .Element(new BpmnElement("Left", BpmnElementTypes.Task))
            .Element(new BpmnElement("Right", BpmnElementTypes.Task))
            .ParallelGateway("Join")
            .Connect("Split", "Left")
            .Connect("Split", "Right")
            .Connect("Left", "Join")
            .Connect("Right", "Join"));

    /// <summary>The join cycle with an escalation throw on it that nothing in the scope catches, so every pass signals the parent.</summary>
    private static BpmnProcessDefinition EscalatingJoinLoop() =>
        LoopThrough("Split", "Raise", builder => builder
            .ParallelGateway("Split")
            .Element(new BpmnElement("Left", BpmnElementTypes.Task))
            .Element(new BpmnElement("Right", BpmnElementTypes.Task))
            .ParallelGateway("Join")
            .IntermediateThrowEvent("Raise", Escalation("up"))
            .Connect("Split", "Left")
            .Connect("Split", "Right")
            .Connect("Left", "Join")
            .Connect("Right", "Join")
            .Connect("Join", "Raise"));

    /// <summary>An escalation nothing in the scope catches, then a gateway that faults because it can decide nothing.</summary>
    private static BpmnProcessDefinition EscalateThenFault() =>
        new BpmnProcessBuilder("escalate-then-fault")
            .StartEvent("Start")
            .IntermediateThrowEvent("Raise", Escalation("up"))
            .ExclusiveGateway("Undecidable")
            .EndEvent("EndLeft")
            .EndEvent("EndRight")
            .ConnectSequence("Start", "Raise", "Undecidable")
            .Connect("Undecidable", "EndLeft", condition: "Left")
            .Connect("Undecidable", "EndRight", condition: "Right")
            .Build();

    /// <summary>A compensate throw on a cycle, compensating the work that ran before the cycle.</summary>
    private static BpmnProcessDefinition CompensateLoop() =>
        new BpmnProcessBuilder("compensate-loop")
            .StartEvent("Start")
            .Task("Reserve", bindingRef: "reserve")
            .Element(new BpmnElement(
                "ReserveCompensation", BpmnElementTypes.BoundaryEvent,
                attachedToRef: "Reserve",
                compensationHandlerElementId: "UndoReserve",
                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation)]))
            .Element(new BpmnElement("UndoReserve", BpmnElementTypes.Task, bindingRef: "undo-reserve", isForCompensation: true))
            .ExclusiveGateway("Merge")
            .IntermediateThrowEvent("Replay", new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation))
            .ConnectSequence("Start", "Reserve", "Merge", "Replay", "Merge")
            .Build();

    /// <summary>An escalation throw on a cycle, caught in its own scope by an event subprocess.</summary>
    private static Shape EscalationLoop(bool interrupting)
    {
        var handler = new BpmnProcessBuilder("on-again")
            .Element(new BpmnElement("AgainStart", BpmnElementTypes.StartEvent, eventDefinitions: [Escalation("again")], cancelActivity: interrupting))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("AgainStart", "Handle", "HandlerEnd")
            .Build();

        var definition = LoopThrough("Raise", "Raise", builder => builder
            .IntermediateThrowEvent("Raise", Escalation("again"))
            .SubProcess("OnAgain", bindingRef: "on-again", triggeredByEvent: true));

        return new Shape(definition, new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["on-again"] = handler });
    }

    /// <summary>A cycle closed only by a conditional flow out of an unbound gateway, which always takes its default.</summary>
    private static BpmnProcessDefinition LoopBackNeverTaken() =>
        new BpmnProcessBuilder("never-taken")
            .StartEvent("Start")
            .ExclusiveGateway("Merge")
            .ExclusiveGateway("Check")
            .EndEvent("End")
            .ConnectSequence("Start", "Merge", "Check")
            .Connect("Check", "Merge", condition: "Again")
            .Connect("Check", "End", isDefault: true)
            .Build();

    /// <summary>Start -> Merge -> Note -> Check, where the unbound Check leaves on an expression and otherwise takes its default back to Merge.</summary>
    private static BpmnProcessDefinition ExpressionExitLoop() =>
        WithExpressions(
            new BpmnProcessBuilder("expression-exit")
                .StartEvent("Start")
                .ExclusiveGateway("Merge")
                .Element(new BpmnElement("Note", BpmnElementTypes.Task))
                .ExclusiveGateway("Check")
                .EndEvent("End")
                .ConnectSequence("Start", "Merge", "Note", "Check")
                .Connect("Check", "End", flowId: "exit")
                .Connect("Check", "Merge", isDefault: true)
                .Build(),
            ("exit", "done"));

    /// <summary>
    /// Start -> Merge -> Note -> Check, where the unbound Check has no default: an unconditional flow back to Merge
    /// and an expression exit, in the order <paramref name="loopBackFirst"/> says.
    /// </summary>
    private static BpmnProcessDefinition ExpressionCheckLoop(bool loopBackFirst)
    {
        var builder = new BpmnProcessBuilder("expression-check")
            .StartEvent("Start")
            .ExclusiveGateway("Merge")
            .Element(new BpmnElement("Note", BpmnElementTypes.Task))
            .ExclusiveGateway("Check")
            .EndEvent("End")
            .ConnectSequence("Start", "Merge", "Note", "Check");

        builder = loopBackFirst
            ? builder.Connect("Check", "Merge").Connect("Check", "End", flowId: "exit")
            : builder.Connect("Check", "End", flowId: "exit").Connect("Check", "Merge");

        return WithExpressions(builder.Build(), ("exit", "done"));
    }

    /// <summary>Do the work again until it reports <c>Done</c>.</summary>
    private static BpmnProcessDefinition RetryUntilDone() =>
        new BpmnProcessBuilder("retry-until-done")
            .StartEvent("Start")
            .ExclusiveGateway("Merge")
            .Task("Work", bindingRef: "work")
            .EndEvent("End")
            .ConnectSequence("Start", "Merge", "Work")
            .Connect("Work", "End", condition: "Done")
            .Connect("Work", "Merge", isDefault: true)
            .Build();

    /// <summary>
    /// Review every pending item, then loop until a pass reports nothing left. The multi-instance activity binds
    /// work, so graph build accepts the cycle; an empty collection routes it on at once, so this run never waits.
    /// </summary>
    private static BpmnProcessDefinition ReviewUntilDone() =>
        new BpmnProcessBuilder("review-until-done")
            .Variable("reviewers")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(collectionVariable: "reviewers", itemVariable: "reviewer"))
            .ExclusiveGateway("Check")
            .EndEvent("End")
            .ConnectSequence("Start", "Review", "Check")
            .Connect("Check", "End", condition: "Done")
            .Connect("Check", "Review", isDefault: true)
            .Build();

    /// <summary>
    /// An acyclic process whose start evaluation dispatches exactly <paramref name="dispatches"/> tokens and then
    /// completes: the start event, an unbound task when the count needs it to come out even, a parallel split,
    /// and that many unbound tasks each arriving at the one end event.
    /// </summary>
    private static BpmnProcessDefinition FanOutDispatching(int dispatches)
    {
        var branches = (dispatches - 2) / 2;
        var builder = new BpmnProcessBuilder("fan-out").StartEvent("Start").ParallelGateway("Split").EndEvent("End");

        if ((dispatches - 2) % 2 == 1)
            builder.Element(new BpmnElement("Pad", BpmnElementTypes.Task)).ConnectSequence("Start", "Pad", "Split");
        else
            builder.Connect("Start", "Split");

        for (var branch = 0; branch < branches; branch++)
            builder.Element(new BpmnElement($"Branch{branch}", BpmnElementTypes.Task)).ConnectSequence("Split", $"Branch{branch}", "End");

        return builder.Build();
    }
}
