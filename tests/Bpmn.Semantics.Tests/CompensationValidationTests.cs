using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class CompensationValidationTests
{
    [Fact]
    public void ValidCompensationGraph_BuildsCleanly()
    {
        var graph = AdvancedScenarioFixtures.Graph(Definition(MinimalElements(), MinimalFlows));

        graph.AttachedCompensationBoundary("Host").ShouldNotBeNull();
        graph.AttachedCatchBoundaries("Host").ShouldBeEmpty();
    }

    [Fact]
    public void CompensationBoundary_ReferencingMissingHandler_IsRejected()
    {
        var elements = MinimalElements(boundary: AdvancedScenarioFixtures.CompensationBoundary("CompBoundary", "Host", "Ghost"));

        Rejects(elements, MinimalFlows, "references handler 'Ghost', which does not exist");
    }

    [Fact]
    public void CompensationBoundary_ReferencingNonHandler_IsRejected()
    {
        var elements = MinimalElements(boundary: AdvancedScenarioFixtures.CompensationBoundary("CompBoundary", "Host", "Plain"))
            .Append(new BpmnElement("Plain", BpmnElementTypes.Task, bindingRef: "plain"))
            .ToArray();

        Rejects(elements, MinimalFlows, "is not a compensation handler element");
    }

    [Fact]
    public void CompensationBoundary_WithOutboundFlow_IsRejected()
    {
        var flows = MinimalFlows.Append(new BpmnSequenceFlow("comp-flow", "CompBoundary", "End")).ToArray();

        Rejects(MinimalElements(), flows, "cannot have outbound sequence flows");
    }

    [Fact]
    public void CompensationBoundary_BindingChild_IsRejected()
    {
        var boundary = new BpmnElement("CompBoundary", BpmnElementTypes.BoundaryEvent,
            bindingRef: "listener",
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation)],
            attachedToRef: "Host",
            compensationHandlerElementId: "Handler");

        Rejects(MinimalElements(boundary: boundary), MinimalFlows, "cannot bind work");
    }

    [Fact]
    public void CompensationBoundary_OnMultiInstanceHost_IsRejected()
    {
        var host = new BpmnElement("Host", BpmnElementTypes.Task, bindingRef: "host",
            loopCharacteristics: new BpmnLoopCharacteristics(isSequential: true, cardinality: 2));

        Rejects(MinimalElements(host: host), MinimalFlows, "multi-instance host");
    }

    [Fact]
    public void Handler_NotTaskFamilyOrSubprocess_IsRejected()
    {
        var handler = new BpmnElement("Handler", BpmnElementTypes.ExclusiveGateway, isForCompensation: true);

        Rejects(MinimalElements(handler: handler), MinimalFlows, "must be a task-family or subprocess element");
    }

    [Fact]
    public void Handler_WithoutChild_IsRejected()
    {
        var handler = new BpmnElement("Handler", BpmnElementTypes.Task, isForCompensation: true);

        Rejects(MinimalElements(handler: handler), MinimalFlows, "must bind work");
    }

    [Fact]
    public void Handler_WithSequenceFlow_IsRejected()
    {
        var flows = MinimalFlows.Append(new BpmnSequenceFlow("handler-flow", "Handler", "End")).ToArray();

        Rejects(MinimalElements(), flows, "cannot participate in sequence flows");
    }

    [Fact]
    public void OrphanHandler_ReferencedByNoBoundary_IsRejected()
    {
        var elements = MinimalElements()
            .Append(AdvancedScenarioFixtures.CompensationHandler("Orphan", "orphan"))
            .ToArray();

        Rejects(elements, MinimalFlows, "referenced by no compensation boundary");
    }

    [Fact]
    public void Handler_HostingBoundary_IsRejected()
    {
        var elements = MinimalElements()
            .Append(new BpmnElement("ErrorOnHandler", BpmnElementTypes.BoundaryEvent,
                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)],
                attachedToRef: "Handler"))
            .ToArray();
        var flows = MinimalFlows.Append(new BpmnSequenceFlow("handler-error-flow", "ErrorOnHandler", "End")).ToArray();

        Rejects(elements, flows, "cannot host attached boundary events");
    }

    [Fact]
    public void ThrowEvent_WithNonCompensationDefinition_IsRejected()
    {
        var thrower = new BpmnElement("Throw", BpmnElementTypes.IntermediateThrowEvent,
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Signal)]);

        Rejects(MinimalElements(thrower: thrower), MinimalFlows, "only compensate and escalation throw events are supported");
    }

    [Fact]
    public void ThrowEvent_BindingChild_IsRejected()
    {
        var thrower = new BpmnElement("Throw", BpmnElementTypes.IntermediateThrowEvent,
            bindingRef: "thrower",
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation)]);

        Rejects(MinimalElements(thrower: thrower), MinimalFlows, "cannot bind work");
    }

    [Fact]
    public void ActivityRef_NamingUnknownElement_IsRejected()
    {
        var thrower = AdvancedScenarioFixtures.CompensateThrow("Throw", "Ghost");

        Rejects(MinimalElements(thrower: thrower), MinimalFlows, "does not exist in this process");
    }

    [Fact]
    public void ActivityRef_NamingHostWithoutCompensationBoundary_IsRejected()
    {
        var thrower = AdvancedScenarioFixtures.CompensateThrow("Throw", "Start");

        Rejects(MinimalElements(thrower: thrower), MinimalFlows, "has no attached compensation boundary");
    }

    [Fact]
    public void ActivityRef_NamingCompensationHost_ValidatesCleanly()
    {
        var definition = Definition(MinimalElements(thrower: AdvancedScenarioFixtures.CompensateThrow("Throw", "Host")), MinimalFlows);

        AdvancedScenarioFixtures.Graph(definition).GetRequiredElement("Host").ShouldNotBeNull();
    }

    private static readonly BpmnSequenceFlow[] MinimalFlows =
    [
        new("flow-start-host", "Start", "Host"),
        new("flow-host-throw", "Host", "Throw"),
        new("flow-throw-end", "Throw", "End")
    ];

    private static BpmnElement[] MinimalElements(
        BpmnElement? boundary = null,
        BpmnElement? handler = null,
        BpmnElement? thrower = null,
        BpmnElement? host = null) =>
    [
        new("Start", BpmnElementTypes.StartEvent),
        host ?? new BpmnElement("Host", BpmnElementTypes.Task, bindingRef: "host"),
        boundary ?? AdvancedScenarioFixtures.CompensationBoundary("CompBoundary", "Host", "Handler"),
        handler ?? AdvancedScenarioFixtures.CompensationHandler("Handler", "undo"),
        thrower ?? AdvancedScenarioFixtures.CompensateThrow("Throw"),
        new("End", BpmnElementTypes.EndEvent)
    ];

    private static BpmnProcessDefinition Definition(
        IReadOnlyCollection<BpmnElement> elements,
        IReadOnlyCollection<BpmnSequenceFlow> flows) =>
        new("compensation-validation", Elements: elements.ToArray(), SequenceFlows: flows.ToArray());

    private static void Rejects(
        IReadOnlyCollection<BpmnElement> elements,
        IReadOnlyCollection<BpmnSequenceFlow> flows,
        string expectedMessage)
    {
        var exception = Should.Throw<BpmnExecutionException>(() =>
            AdvancedScenarioFixtures.Graph(Definition(elements, flows)));
        exception.Message.ShouldContain(expectedMessage, Case.Sensitive);
    }
}
