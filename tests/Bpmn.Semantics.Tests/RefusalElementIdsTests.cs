using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The ids a graph-build refusal carries in <see cref="BpmnExecutionException.ElementIds"/>, so a host can point at
/// the elements and flows it names without parsing its message. Each refusal's own wording is covered beside the rule
/// it enforces; these cover the ids, including one that names an element the definition does not contain.
/// </summary>
public sealed class RefusalElementIdsTests
{
    [Fact]
    public void A_boundary_attached_to_a_missing_host_names_the_boundary_and_the_host()
    {
        var definition = new BpmnProcessDefinition(
            "missing-host",
            Elements:
            [
                new BpmnElement("Start", BpmnElementTypes.StartEvent),
                new BpmnElement("End", BpmnElementTypes.EndEvent),
                new BpmnElement("Timeout", BpmnElementTypes.BoundaryEvent, attachedToRef: "Ghost", eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer)])
            ],
            SequenceFlows: [new BpmnSequenceFlow("toEnd", "Start", "End")]);

        Build(definition).ElementIds.ShouldBe(["Timeout", "Ghost"]);
    }

    [Fact]
    public void A_flow_to_a_missing_target_names_the_flow_and_the_target()
    {
        var definition = new BpmnProcessDefinition(
            "missing-target",
            Elements: [new BpmnElement("Start", BpmnElementTypes.StartEvent)],
            SequenceFlows: [new BpmnSequenceFlow("toNowhere", "Start", "Nowhere")]);

        Build(definition).ElementIds.ShouldBe(["toNowhere", "Nowhere"]);
    }

    [Fact]
    public void A_refusal_about_the_process_as_a_whole_names_no_element()
    {
        var definition = new BpmnProcessDefinition(
            "no-start",
            Elements: [new BpmnElement("End", BpmnElementTypes.EndEvent)],
            SequenceFlows: []);

        Build(definition).ElementIds.ShouldBeEmpty();
    }

    private static BpmnExecutionException Build(BpmnProcessDefinition definition) =>
        Should.Throw<BpmnExecutionException>(() => BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full));
}
