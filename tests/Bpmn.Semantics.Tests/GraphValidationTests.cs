using Bpmn.Model;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>Executable graph rules exercised with shapes that otherwise satisfy the same rule set.</summary>
public sealed class GraphValidationTests
{
    private static BpmnGraph Build(BpmnProcessDefinition definition, params BpmnBoundWork[] extraWork) =>
        BpmnGraph.Build(definition, [.. BpmnBoundWork.Derive(definition), .. extraWork], BpmnHostCapabilities.Full);

    private static BpmnProcessDefinition Definition(
        string id,
        BpmnElement[] elements,
        BpmnSequenceFlow[] flows,
        BpmnVariableDeclaration[]? variables = null) =>
        new(id, Elements: elements, SequenceFlows: flows, Variables: variables);

    private static BpmnSequenceFlow Flow(string id, string source, string target, string? condition = null, bool isDefault = false) =>
        new(id, source, target, conditionOutcome: condition, isDefault: isDefault);

    private static BpmnElement Start(string id = "start") => new(id, BpmnElementTypes.StartEvent);
    private static BpmnElement End(string id = "end") => new(id, BpmnElementTypes.EndEvent);
    private static BpmnElement Work(string id, string? binding = null, BpmnLoopCharacteristics? loop = null) =>
        new(id, BpmnElementTypes.Task, bindingRef: binding, loopCharacteristics: loop);
    private static BpmnElement Catch(string id, string? binding = "listener", string? definition = BpmnEventDefinitionTypes.Message) =>
        new(id, BpmnElementTypes.IntermediateCatchEvent, bindingRef: binding,
            eventDefinitions: definition is null ? [] : [new BpmnEventDefinition(definition)]);

    [Fact]
    public void CyclicGraph_IsAccepted_AndClassifiesTheLoopBackFlow()
    {
        var graph = Build(Definition("cycle",
            [Start(), Work("a", "a"), Work("b", "b"), End()],
            [Flow("start-a", "start", "a"), Flow("a-b", "a", "b"), Flow("b-a", "b", "a"), Flow("b-end", "b", "end")]));

        graph.IsBackwardFlow("b-a").ShouldBeTrue();
        graph.BackwardFlowIds.OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["b-a"]);
        graph.IsBackwardFlow("start-a").ShouldBeFalse();
        graph.IsBackwardFlow("a-b").ShouldBeFalse();
        graph.IsBackwardFlow("b-end").ShouldBeFalse();
    }

    [Fact]
    public void MissingStartEvent_IsRejected()
    {
        var definition = Definition("missing-start", [Work("task", "task"), End()], [Flow("task-end", "task", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("requires at least one start event");
    }

    [Fact]
    public void UnknownFlowTarget_IsRejected()
    {
        var definition = Definition("unknown-target", [Start(), End()], [Flow("dangling", "start", "missing")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("target 'missing' does not exist");
    }

    [Fact]
    public void SubprocessWithoutChild_IsRejected()
    {
        var definition = Definition("subprocess-without-binding",
            [Start(), new BpmnElement("sub", BpmnElementTypes.SubProcess), End()],
            [Flow("start-sub", "start", "sub"), Flow("sub-end", "sub", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("requires bound work");
    }

    [Fact]
    public void CallActivity_ResolvesToTaskFamily_BoundChildAndBoundaryHostAndMultiInstance_AreLegal()
    {
        var definition = Definition("call-mi-boundary",
            [Start(), new BpmnElement("call", BpmnElementTypes.CallActivity, bindingRef: "called",
                loopCharacteristics: new BpmnLoopCharacteristics(cardinality: 2)),
             new BpmnElement("error", BpmnElementTypes.BoundaryEvent, attachedToRef: "call",
                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)]), End("ok"), End("failed")],
            [Flow("start-call", "start", "call"), Flow("call-ok", "call", "ok"), Flow("error-failed", "error", "failed")]);

        var graph = Build(definition);
        BpmnElementFamilies.Resolve(graph.GetRequiredElement("call")).ShouldBe(BpmnElementFamilies.Task);
        BpmnElementFamilies.IsCallActivity(graph.GetRequiredElement("call")).ShouldBeTrue();
        graph.AttachedErrorBoundary("call")!.ElementId.ShouldBe("error");
    }

    [Fact]
    public void ChildlessCallActivity_IsLegalAtValidation()
    {
        var definition = Definition("childless-call",
            [Start(), new BpmnElement("call", BpmnElementTypes.CallActivity), End()],
            [Flow("start-call", "start", "call"), Flow("call-end", "call", "end")]);
        Build(definition).GetRequiredElement("call").BindingRef.ShouldBeNull();
    }

    [Fact]
    public void GatewayBindingRules_ParallelGatewayCannotBindChild()
    {
        var definition = Definition("bound-parallel",
            [Start(), new BpmnElement("fork", BpmnElementTypes.ParallelGateway, bindingRef: "fork-work"), End()],
            [Flow("start-fork", "start", "fork"), Flow("fork-end", "fork", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("cannot bind work");
    }

    [Fact]
    public void UnboundChildActivity_IsRejected()
    {
        var definition = Definition("orphan-binding", [Start(), End()], [Flow("start-end", "start", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition, new BpmnBoundWork("orphan")))
            .Message.ShouldContain("not bound to any element");
    }

    [Fact]
    public void UnsupportedElementType_IsRejected()
    {
        var definition = Definition("unsupported",
            [Start(), new BpmnElement("complex", "complexGateway"), End()],
            [Flow("start-complex", "start", "complex"), Flow("complex-end", "complex", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("does not support");
    }

    [Fact]
    public void CatchEventWithoutChild_IsRejected()
    {
        var definition = Definition("catch-unbound", [Start(), Catch("catch", binding: null), End()],
            [Flow("start-catch", "start", "catch"), Flow("catch-end", "catch", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("requires bound suspending work");
    }

    [Fact]
    public void CatchEventWithoutEventDefinitions_IsRejected()
    {
        var definition = Definition("catch-no-definition",
            [Start(), Catch("catch", definition: null), End()],
            [Flow("start-catch", "start", "catch"), Flow("catch-end", "catch", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("exactly one event definition");
    }

    [Fact]
    public void CatchEventWithMultipleEventDefinitions_IsRejected()
    {
        var definition = Definition("catch-many-definitions",
            [Start(), new BpmnElement("catch", BpmnElementTypes.IntermediateCatchEvent, bindingRef: "listener",
                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Message), new BpmnEventDefinition(BpmnEventDefinitionTypes.Signal)]), End()],
            [Flow("start-catch", "start", "catch"), Flow("catch-end", "catch", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("exactly one event definition");
    }

    [Fact]
    public void CatchEventWithUnsupportedEventDefinition_IsRejected()
    {
        var definition = Definition("catch-error-definition",
            [Start(), Catch("catch", definition: BpmnEventDefinitionTypes.Error), End()],
            [Flow("start-catch", "start", "catch"), Flow("catch-end", "catch", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("only timer, message, and signal catch events");
    }

    [Fact]
    public void EventBasedGateway_WithSingleOutboundFlow_IsRejected()
    {
        var definition = Definition("one-member-race",
            [Start(), new BpmnElement("race", BpmnElementTypes.EventBasedGateway), Catch("catch"), End()],
            [Flow("start-race", "start", "race"), Flow("race-catch", "race", "catch"), Flow("catch-end", "catch", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("at least two outbound sequence flows");
    }

    [Fact]
    public void EventBasedGateway_TargetingNonCatchEvent_IsRejected()
    {
        var definition = Definition("race-task-target",
            [Start(), new BpmnElement("race", BpmnElementTypes.EventBasedGateway), Catch("catch-a"), Work("task", "task"), End()],
            [Flow("start-race", "start", "race"), Flow("race-catch", "race", "catch-a"), Flow("race-task", "race", "task"),
             Flow("catch-end", "catch-a", "end"), Flow("task-end", "task", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("must target an intermediate catch event");
    }

    [Fact]
    public void EventBasedGateway_TargetCatchWithExtraInboundFlow_IsRejected()
    {
        var definition = Definition("race-extra-inbound",
            [Start(), new BpmnElement("race", BpmnElementTypes.EventBasedGateway), Catch("a", "a-listener"), Catch("b", "b-listener"), End()],
            [Flow("start-race", "start", "race"), Flow("race-a", "race", "a"), Flow("race-b", "race", "b"),
             Flow("b-a", "b", "a"), Flow("a-end", "a", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("must have exactly one inbound flow");
    }

    [Fact]
    public void EventBasedGateway_WithConditionalOutboundFlow_IsRejected()
    {
        var definition = ValidRace() with
        {
            SequenceFlows = [Flow("start-race", "start", "race"), Flow("race-a", "race", "a", "Done"),
                Flow("race-b", "race", "b"), Flow("a-end", "a", "end-a"), Flow("b-end", "b", "end-b")]
        };
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("cannot carry a condition");
    }

    [Fact]
    public void EventBasedGateway_BindingChild_IsRejected()
    {
        var definition = Definition("bound-race",
            [Start(), new BpmnElement("race", BpmnElementTypes.EventBasedGateway, bindingRef: "race-work"),
             Catch("a", "a-listener"), Catch("b", "b-listener"), End("end-a"), End("end-b")],
            [Flow("start-race", "start", "race"), Flow("race-a", "race", "a"), Flow("race-b", "race", "b"),
             Flow("a-end", "a", "end-a"), Flow("b-end", "b", "end-b")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("cannot bind work");
    }

    [Fact]
    public void BoundaryEvent_AttachedToMissingHost_IsRejected()
    {
        var definition = BoundaryGraph(boundary: Boundary("timer", "ghost", "timer-listener"));
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("does not exist");
    }

    [Fact]
    public void BoundaryEvent_AttachedToNonHostFamily_IsRejected()
    {
        var definition = Definition("boundary-on-start",
            [Start(), Boundary("timer", "start", "timer-listener"), End()],
            [Flow("start-end", "start", "end"), Flow("timer-end", "timer", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("not a task-family or subprocess host");
    }

    [Fact]
    public void BoundaryEvent_AttachedToChildlessHost_IsRejected()
    {
        var definition = BoundaryGraph(boundary: Boundary("timer", "host", "timer-listener"), hostBinding: null);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("binds no work");
    }

    [Fact]
    public void BoundaryEvent_WithInboundFlow_IsRejected()
    {
        var definition = BoundaryGraph(boundary: Boundary("timer", "host", "timer-listener"), inboundBoundary: true);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("cannot have inbound sequence flows");
    }

    [Fact]
    public void BoundaryEvent_WithoutOutboundFlow_IsRejected()
    {
        var definition = BoundaryGraph(boundary: Boundary("timer", "host", "timer-listener"), boundaryOutbound: false);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("at least one outbound sequence flow");
    }

    [Fact]
    public void CatchBoundaryEvent_WithoutChild_IsRejected()
    {
        var definition = BoundaryGraph(boundary: Boundary("listener", "host", null));
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("requires bound suspending listener work");
    }

    [Fact]
    public void ErrorBoundaryEvent_WithChild_IsRejected()
    {
        var definition = BoundaryGraph(boundary: Boundary("error", "host", "bad-listener", BpmnEventDefinitionTypes.Error));
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("cannot bind work");
    }

    [Fact]
    public void NonInterruptingErrorBoundaryEvent_IsRejected()
    {
        var definition = BoundaryGraph(boundary: Boundary("error", "host", null, BpmnEventDefinitionTypes.Error, interrupting: false));
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("must be interrupting");
    }

    [Fact]
    public void MultipleErrorBoundaryEvents_OnOneHost_AreRejected()
    {
        var definition = Definition("two-error-boundaries",
            [Start(), Work("host", "host-work"),
             Boundary("error-a", "host", null, BpmnEventDefinitionTypes.Error), Boundary("error-b", "host", null, BpmnEventDefinitionTypes.Error), End("normal"), End("a"), End("b")],
            [Flow("start-host", "start", "host"), Flow("host-normal", "host", "normal"), Flow("a-end", "error-a", "a"), Flow("b-end", "error-b", "b")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("more than one error boundary");
    }

    [Fact]
    public void BoundaryEvent_WithDefaultFlow_IsRejected()
    {
        var boundary = new BpmnElement("timer", BpmnElementTypes.BoundaryEvent, defaultFlowId: "boundary-end",
            bindingRef: "timer-listener", eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer)], attachedToRef: "host");
        var definition = BoundaryGraph(boundary, boundaryDefaultFlow: true);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("cannot declare a default sequence flow");
    }

    [Fact]
    public void ValidTimerBoundaryEvent_IsAccepted()
    {
        var definition = BoundaryGraph(Boundary("timer", "host", "timer-listener"));
        var graph = Build(definition);
        graph.AttachedCatchBoundaries("host").Select(element => element.ElementId).ShouldBe(["timer"]);
        graph.AttachedErrorBoundary("host").ShouldBeNull();
    }

    [Fact]
    public void MultiInstance_OnChildlessHost_IsRejected()
    {
        var definition = Definition("mi-childless",
            [Start(), Work("mi", loop: new BpmnLoopCharacteristics(cardinality: 3)), End()],
            [Flow("start-mi", "start", "mi"), Flow("mi-end", "mi", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("not a task-family or subprocess host with bound work");
    }

    [Fact]
    public void MultiInstance_OnGateway_IsRejected()
    {
        var definition = Definition("mi-gateway",
            [Start(), new BpmnElement("fork", BpmnElementTypes.ParallelGateway, loopCharacteristics: new BpmnLoopCharacteristics(cardinality: 2)), Work("work", "work"), End()],
            [Flow("start-fork", "start", "fork"), Flow("fork-work", "fork", "work"), Flow("work-end", "work", "end")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("not a task-family or subprocess host");
    }

    [Fact]
    public void MultiInstance_BothCardinalityAndCollection_IsRejected()
    {
        var definition = MultiInstance(new BpmnLoopCharacteristics(cardinality: 3, collectionVariable: "items"));
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("exactly one of a positive cardinality or a collection variable");
    }

    [Fact]
    public void MultiInstance_NeitherCardinalityNorCollection_IsRejected()
    {
        var definition = MultiInstance(new BpmnLoopCharacteristics(isSequential: true));
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("exactly one of a positive cardinality or a collection variable");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MultiInstance_NonPositiveCardinality_IsRejected(int cardinality)
    {
        var definition = MultiInstance(new BpmnLoopCharacteristics(cardinality: cardinality));
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("cardinality must be a positive integer");
    }

    [Fact]
    public void MultiInstance_CollectionMode_UndeclaredVariable_IsRejected()
    {
        var definition = MultiInstance(new BpmnLoopCharacteristics(collectionVariable: "missing"));
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("not a declared container-scoped variable");
    }

    [Fact]
    public void MultiInstance_CollectionMode_ReservedItemVariable_IsRejected()
    {
        var definition = MultiInstance(new BpmnLoopCharacteristics(collectionVariable: "items", itemVariable: BpmnLoopCharacteristics.LoopIndexVariable),
            [new BpmnVariableDeclaration("items")]);
        Should.Throw<BpmnExecutionException>(() => Build(definition)).Message.ShouldContain("reserved for the zero-based iteration index");
    }

    [Fact]
    public void MultiInstance_CollectionMode_DeclaredVariable_IsAccepted()
    {
        var definition = MultiInstance(new BpmnLoopCharacteristics(collectionVariable: "items"), [new BpmnVariableDeclaration("items")]);
        var loop = Build(definition).GetRequiredElement("mi").LoopCharacteristics!;
        loop.CollectionVariable.ShouldBe("items");
        loop.ItemVariable.ShouldBe(BpmnLoopCharacteristics.DefaultItemVariable);
    }

    private static BpmnProcessDefinition ValidRace() => Definition("valid-race",
        [Start(), new BpmnElement("race", BpmnElementTypes.EventBasedGateway), Catch("a", "a-listener"), Catch("b", "b-listener"), End("end-a"), End("end-b")],
        [Flow("start-race", "start", "race"), Flow("race-a", "race", "a"), Flow("race-b", "race", "b"),
         Flow("a-end", "a", "end-a"), Flow("b-end", "b", "end-b")]);

    private static BpmnElement Boundary(string id, string host, string? binding, string definition = BpmnEventDefinitionTypes.Timer, bool interrupting = true) =>
        new(id, BpmnElementTypes.BoundaryEvent, bindingRef: binding, attachedToRef: host, cancelActivity: interrupting,
            eventDefinitions: [new BpmnEventDefinition(definition)]);

    private static BpmnProcessDefinition BoundaryGraph(
        BpmnElement boundary,
        string? hostBinding = "host-work",
        bool boundaryOutbound = true,
        bool inboundBoundary = false,
        bool boundaryDefaultFlow = false)
    {
        var flows = new List<BpmnSequenceFlow> { Flow("start-host", "start", "host"), Flow("host-end", "host", "end") };
        if (boundaryOutbound) flows.Add(Flow("boundary-end", boundary.ElementId, "end", isDefault: boundaryDefaultFlow));
        if (inboundBoundary) flows.Add(Flow("host-boundary", "host", boundary.ElementId));
        return Definition("boundary-" + boundary.ElementId,
            [Start(), Work("host", hostBinding), boundary, End()], flows.ToArray());
    }

    private static BpmnProcessDefinition MultiInstance(BpmnLoopCharacteristics loop, BpmnVariableDeclaration[]? variables = null) =>
        Definition("mi", [Start(), Work("mi", "mi-work", loop), End()],
            [Flow("start-mi", "start", "mi"), Flow("mi-end", "mi", "end")], variables);
}
