using Bpmn.Model;
using Bpmn.Semantics;
using Bpmn.Runtime.InMemory;

namespace Bpmn.Semantics.Tests;

internal static class AdvancedScenarioFixtures
{
    public static InMemoryBpmnHost Host(IReadOnlyDictionary<string, BpmnProcessDefinition>? nestedProcesses = null) =>
        new(new InMemoryBpmnHostOptions { NestedProcesses = nestedProcesses });

    public static BpmnGraph Graph(BpmnProcessDefinition definition, Func<string, BpmnProcessDefinition?>? nestedProcess = null) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition, nestedProcess), BpmnHostCapabilities.Full);

    public static BpmnElement CompensationBoundary(string id, string hostId, string handlerId) =>
        new(id, BpmnElementTypes.BoundaryEvent,
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation)],
            attachedToRef: hostId,
            compensationHandlerElementId: handlerId);

    public static BpmnElement CompensationHandler(string id, string bindingRef) =>
        new(id, BpmnElementTypes.Task, bindingRef: bindingRef, isForCompensation: true);

    public static BpmnElement CompensateThrow(string id, string? activityRef = null) =>
        new(id, BpmnElementTypes.IntermediateThrowEvent,
            eventDefinitions: [CompensationDefinition(activityRef)]);

    public static BpmnElement CompensateEnd(string id, string? activityRef = null) =>
        new(id, BpmnElementTypes.EndEvent,
            eventDefinitions: [CompensationDefinition(activityRef)]);

    public static BpmnElement CancelEnd(string id) =>
        new(id, BpmnElementTypes.EndEvent,
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Cancel)]);

    public static BpmnElement CancelBoundary(string id, string hostId) =>
        new(id, BpmnElementTypes.BoundaryEvent,
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Cancel)],
            attachedToRef: hostId);

    private static BpmnEventDefinition CompensationDefinition(string? activityRef) =>
        new(BpmnEventDefinitionTypes.Compensation,
            activityRef is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [BpmnEventDefinitionProperties.ActivityRef] = activityRef
                });
}
