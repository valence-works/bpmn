using Bpmn.Model;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>Escalation graph rules and the public attached-boundary index.</summary>
public sealed class EscalationValidationTests
{
    [Fact]
    public void A_valid_escalation_boundary_is_indexed_on_its_subprocess_host()
    {
        var graph = Build(
            [Start(), Subprocess(), Boundary("Coded", "Sub", "E1"), Throw("Throw", "E1"), End("End"), End("BoundaryEnd")],
            [("Start", "Sub"), ("Sub", "Throw"), ("Throw", "End"), ("Coded", "BoundaryEnd")]);

        var boundary = graph.AttachedEscalationBoundaries("Sub").ShouldHaveSingleItem();
        boundary.ElementId.ShouldBe("Coded");
        boundary.AttachedToRef.ShouldBe("Sub");
        boundary.EventDefinitions.ShouldHaveSingleItem().Properties[BpmnEventDefinitionProperties.Code].ShouldBe("E1");
        graph.AttachedCatchBoundaries("Sub").ShouldBeEmpty();
    }

    [Fact]
    public void An_escalation_throw_without_a_code_is_rejected() =>
        Rejects(
            [Start(), Subprocess(), Throw("Throw", code: null), End("End")],
            [("Start", "Sub"), ("Sub", "Throw"), ("Throw", "End")],
            "must declare a non-empty escalation code");

    [Fact]
    public void An_escalation_end_without_a_code_is_rejected() =>
        Rejects(
            [Start(), Subprocess(), new BpmnElement("EscalationEnd", BpmnElementTypes.EndEvent,
                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Escalation)] )],
            [("Start", "Sub"), ("Sub", "EscalationEnd")],
            "must declare a non-empty escalation code");

    [Fact]
    public void An_escalation_throw_cannot_bind_host_work() =>
        Rejects(
            [Start(), Subprocess(), Throw("Throw", "E1", bindingRef: "throw-work"), End("End")],
            [("Start", "Sub"), ("Sub", "Throw"), ("Throw", "End")],
            "cannot bind work");

    [Fact]
    public void An_escalation_boundary_can_only_attach_to_a_subprocess_host() =>
        Rejects(
            [Start(), Task("Task", "task"), Boundary("Boundary", "Task", "E1"), End("End"), End("BoundaryEnd")],
            [("Start", "Task"), ("Task", "End"), ("Boundary", "BoundaryEnd")],
            "may only attach to a subprocess host");

    [Fact]
    public void An_escalation_boundary_cannot_bind_listener_work() =>
        Rejects(
            [Start(), Subprocess(), Boundary("Boundary", "Sub", "E1", bindingRef: "listener"), End("End"), End("BoundaryEnd")],
            [("Start", "Sub"), ("Sub", "End"), ("Boundary", "BoundaryEnd")],
            "cannot bind work");

    [Fact]
    public void An_escalation_boundary_requires_an_outbound_flow() =>
        Rejects(
            [Start(), Subprocess(), Boundary("Boundary", "Sub", "E1"), End("End")],
            [("Start", "Sub"), ("Sub", "End")],
            "must have at least one outbound");

    [Fact]
    public void A_subprocess_cannot_have_duplicate_coded_escalation_boundaries() =>
        Rejects(
            [Start(), Subprocess(), Boundary("First", "Sub", "E1"), Boundary("Second", "Sub", "E1"), End("End"), End("FirstEnd"), End("SecondEnd")],
            [("Start", "Sub"), ("Sub", "End"), ("First", "FirstEnd"), ("Second", "SecondEnd")],
            "more than one escalation boundary for code 'E1'");

    [Fact]
    public void A_subprocess_cannot_have_two_escalation_catch_all_boundaries() =>
        Rejects(
            [Start(), Subprocess(), Boundary("First", "Sub"), Boundary("Second", "Sub"), End("End"), End("FirstEnd"), End("SecondEnd")],
            [("Start", "Sub"), ("Sub", "End"), ("First", "FirstEnd"), ("Second", "SecondEnd")],
            "declares more than one code-less catch-all escalation boundary");

    [Fact]
    public void A_coded_escalation_boundary_can_coexist_with_one_catch_all()
    {
        var graph = Build(
            [Start(), Subprocess(), Boundary("Coded", "Sub", "E1"), Boundary("CatchAll", "Sub"), End("End"), End("CodedEnd"), End("CatchAllEnd")],
            [("Start", "Sub"), ("Sub", "End"), ("Coded", "CodedEnd"), ("CatchAll", "CatchAllEnd")]);

        var boundaries = graph.AttachedEscalationBoundaries("Sub");
        boundaries.Select(boundary => boundary.ElementId).ShouldBe(["Coded", "CatchAll"]);
        boundaries.Single(boundary => boundary.ElementId == "Coded")
            .EventDefinitions.Single().Properties[BpmnEventDefinitionProperties.Code].ShouldBe("E1");
        boundaries.Single(boundary => boundary.ElementId == "CatchAll").EventDefinitions.Single().Properties
            .ContainsKey(BpmnEventDefinitionProperties.Code).ShouldBeFalse();
    }

    private static void Rejects(
        BpmnElement[] elements,
        (string Source, string Target)[] flows,
        string expectedMessage)
    {
        Should.Throw<BpmnExecutionException>(() => Build(elements, flows))
            .Message.ShouldContain(expectedMessage);
    }

    private static BpmnGraph Build(BpmnElement[] elements, (string Source, string Target)[] flows)
    {
        var builder = new BpmnProcessBuilder("escalation-validation");
        foreach (var element in elements)
            builder.Element(element);
        foreach (var (source, target) in flows)
            builder.Connect(source, target);

        var definition = builder.Build();
        var boundWork = elements
            .Where(element => element.BindingRef is not null)
            .Select(element => new BpmnBoundWork(element.BindingRef!))
            .ToArray();
        return BpmnGraph.Build(definition, boundWork, BpmnHostCapabilities.Full);
    }

    private static BpmnElement Start() => new("Start", BpmnElementTypes.StartEvent);

    private static BpmnElement Subprocess() => new("Sub", BpmnElementTypes.SubProcess, bindingRef: "sub");

    private static BpmnElement Task(string elementId, string bindingRef) =>
        new(elementId, BpmnElementTypes.Task, bindingRef: bindingRef);

    private static BpmnElement End(string elementId) => new(elementId, BpmnElementTypes.EndEvent);

    private static BpmnElement Throw(string elementId, string? code, string? bindingRef = null) =>
        new(elementId, BpmnElementTypes.IntermediateThrowEvent, bindingRef: bindingRef,
            eventDefinitions: [code is null
                ? new BpmnEventDefinition(BpmnEventDefinitionTypes.Escalation)
                : Escalation(code)]);

    private static BpmnElement Boundary(string elementId, string hostId, string? code = null, string? bindingRef = null) =>
        new(elementId, BpmnElementTypes.BoundaryEvent, attachedToRef: hostId, bindingRef: bindingRef,
            eventDefinitions: [code is null ? EscalationWithoutCode() : Escalation(code)]);

    private static BpmnEventDefinition Escalation(string code) =>
        new(BpmnEventDefinitionTypes.Escalation,
            new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code });

    private static BpmnEventDefinition EscalationWithoutCode() => new(BpmnEventDefinitionTypes.Escalation);
}
