using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class FamilyRefusalElementIdsTests
{
    private const string ElementId = "unsupported-element";

    private sealed record RefusalCase(Func<BpmnElement> CreateElement, string Message);

    private static readonly IReadOnlyDictionary<string, RefusalCase> RefusalsByShape = new Dictionary<string, RefusalCase>(StringComparer.Ordinal)
    {
        ["unsupported element type"] = new(
            () => new BpmnElement(ElementId, "customTask"),
            $"BPMN element '{ElementId}' has element type 'customTask', which this library does not support."),
        ["boundary event with no definition"] = new(
            () => new BpmnElement(ElementId, BpmnElementTypes.BoundaryEvent),
            $"BPMN boundary event '{ElementId}' must declare exactly one event definition; it declares 0."),
        ["boundary event with an unsupported definition"] = new(
            () => new BpmnElement(ElementId, BpmnElementTypes.BoundaryEvent,
                eventDefinitions: [new BpmnEventDefinition("unsupported")]),
            $"BPMN boundary event '{ElementId}' declares event definition type 'unsupported'; only timer, message, signal, error, escalation, compensation, and cancel boundary events are supported."),
        ["intermediate catch with multiple definitions"] = new(
            () => new BpmnElement(ElementId, BpmnElementTypes.IntermediateCatchEvent,
                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Message), new BpmnEventDefinition(BpmnEventDefinitionTypes.Signal)]),
            $"BPMN intermediate catch event '{ElementId}' must declare exactly one event definition; it declares 2."),
        ["intermediate catch with an unsupported definition"] = new(
            () => new BpmnElement(ElementId, BpmnElementTypes.IntermediateCatchEvent,
                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)]),
            $"BPMN intermediate catch event '{ElementId}' declares event definition type 'error'; only timer, message, signal, and link catch events are supported."),
        ["start event with unsupported definitions"] = new(
            () => new BpmnElement(ElementId, BpmnElementTypes.StartEvent,
                eventDefinitions: [new BpmnEventDefinition("unsupported")]),
            $"BPMN start event '{ElementId}' declares unsupported event definitions; only none, timer, message, signal, and event-subprocess (escalation/error) start events are supported (exactly one such definition)."),
        ["end event with unsupported definitions"] = new(
            () => new BpmnElement(ElementId, BpmnElementTypes.EndEvent,
                eventDefinitions: [new BpmnEventDefinition("unsupported")]),
            $"BPMN end event '{ElementId}' declares unsupported event definitions; only none, terminate, compensate, cancel, escalation, error, message, and signal end events are supported."),
        ["intermediate throw with no definition"] = new(
            () => new BpmnElement(ElementId, BpmnElementTypes.IntermediateThrowEvent),
            $"BPMN intermediate throw event '{ElementId}' must declare exactly one event definition; it declares 0."),
        ["intermediate throw with an unsupported definition"] = new(
            () => new BpmnElement(ElementId, BpmnElementTypes.IntermediateThrowEvent,
                eventDefinitions: [new BpmnEventDefinition("unsupported")]),
            $"BPMN intermediate throw event '{ElementId}' declares event definition type 'unsupported'; only compensate, escalation, message, signal, and link throw events are supported.")
    };

    public static TheoryData<string> RefusalShapes() => [.. RefusalsByShape.Keys];

    [Theory]
    [MemberData(nameof(RefusalShapes))]
    public void Resolve_names_the_element_for_each_family_refusal(string shape)
    {
        var refusal = RefusalsByShape[shape];
        var element = refusal.CreateElement();

        var exception = Should.Throw<BpmnExecutionException>(() => BpmnElementFamilies.Resolve(element));

        exception.Message.ShouldBe(refusal.Message);
        exception.ElementIds.ShouldBe([element.ElementId]);
    }

    [Theory]
    [MemberData(nameof(RefusalShapes))]
    public void Derive_then_graph_build_preserves_each_family_refusal(string shape)
    {
        var refusal = RefusalsByShape[shape];
        var element = refusal.CreateElement();
        var definition = DefinitionContaining(element);

        var exception = Should.Throw<BpmnExecutionException>(() =>
            BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full));

        exception.Message.ShouldBe(refusal.Message);
        exception.ElementIds.ShouldBe([element.ElementId]);
    }

    private static BpmnProcessDefinition DefinitionContaining(BpmnElement element)
    {
        var start = element.ElementType == BpmnElementTypes.StartEvent
            ? element
            : new BpmnElement("start", BpmnElementTypes.StartEvent);
        var end = element.ElementType == BpmnElementTypes.EndEvent
            ? element
            : new BpmnElement("end", BpmnElementTypes.EndEvent);

        var elements = new List<BpmnElement> { start };
        if (!ReferenceEquals(element, start))
            elements.Add(element);
        if (!ReferenceEquals(element, end))
            elements.Add(end);

        var flows = new List<BpmnSequenceFlow>();
        if (!ReferenceEquals(element, start))
            flows.Add(new BpmnSequenceFlow("to-element", start.ElementId, element.ElementId));
        if (!ReferenceEquals(element, end))
            flows.Add(new BpmnSequenceFlow("to-end", element.ElementId, end.ElementId));

        return new BpmnProcessDefinition("family-refusal", Elements: elements, SequenceFlows: flows);
    }
}
