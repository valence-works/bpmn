using Bpmn.Model;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class CallActivityEscalationValidationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_call_activity_can_have_one_coded_and_one_catch_all_boundary(bool codedInterrupting)
    {
        var graph = Build(
            [Start(), CallActivity(), Boundary("Coded", "Call", "E1", codedInterrupting), Boundary("CatchAll", "Call", null, !codedInterrupting), End("End"), End("CodedEnd"), End("CatchAllEnd")],
            [("Start", "Call"), ("Call", "End"), ("Coded", "CodedEnd"), ("CatchAll", "CatchAllEnd")]);

        graph.AttachedEscalationBoundaries("Call").Select(boundary => boundary.ElementId)
            .ShouldBe(["Coded", "CatchAll"]);
        graph.AttachedEscalationBoundaries("Call").Select(boundary => boundary.CancelActivity)
            .ShouldBe([codedInterrupting, !codedInterrupting]);
    }

    [Fact]
    public void A_call_activity_cannot_have_two_boundaries_for_the_same_code() =>
        Should.Throw<BpmnExecutionException>(() => Build(
            [Start(), CallActivity(), Boundary("First", "Call", "E1"), Boundary("Second", "Call", "E1"), End("End"), End("FirstEnd"), End("SecondEnd")],
            [("Start", "Call"), ("Call", "End"), ("First", "FirstEnd"), ("Second", "SecondEnd")]))
            .Message.ShouldContain("more than one escalation boundary for code 'E1'");

    [Fact]
    public void A_call_activity_cannot_have_two_catch_all_boundaries() =>
        Should.Throw<BpmnExecutionException>(() => Build(
            [Start(), CallActivity(), Boundary("First", "Call"), Boundary("Second", "Call"), End("End"), End("FirstEnd"), End("SecondEnd")],
            [("Start", "Call"), ("Call", "End"), ("First", "FirstEnd"), ("Second", "SecondEnd")]))
            .Message.ShouldContain("declares more than one code-less catch-all escalation boundary");

    [Fact]
    public void An_escalation_boundary_still_rejects_an_ordinary_task_host()
    {
        var exception = Should.Throw<BpmnExecutionException>(() => Build(
            [Start(), Task(), Boundary("Boundary", "Task", "E1"), End("End"), End("BoundaryEnd")],
            [("Start", "Task"), ("Task", "End"), ("Boundary", "BoundaryEnd")]));

        exception.Message.ShouldContain("call activity");
        exception.ElementIds.ShouldBe(["Boundary", "Task"]);
    }

    private static BpmnGraph Build(
        BpmnElement[] elements,
        (string Source, string Target)[] flows)
    {
        var builder = new BpmnProcessBuilder("call-activity-escalation-validation");
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

    private static BpmnElement CallActivity() => new("Call", BpmnElementTypes.CallActivity, bindingRef: "call");

    private static BpmnElement Task() => new("Task", BpmnElementTypes.Task, bindingRef: "task");

    private static BpmnElement End(string elementId) => new(elementId, BpmnElementTypes.EndEvent);

    private static BpmnElement Boundary(string elementId, string hostId, string? code = null, bool interrupting = true) =>
        new(elementId, BpmnElementTypes.BoundaryEvent, attachedToRef: hostId, cancelActivity: interrupting,
            eventDefinitions: [code is null ? new BpmnEventDefinition(BpmnEventDefinitionTypes.Escalation) : Escalation(code)]);

    private static BpmnEventDefinition Escalation(string code) =>
        new(BpmnEventDefinitionTypes.Escalation,
            new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code });
}
