using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// A signal intermediate throw and a signal end event are read with their resolved signal name and bound to a
/// <see cref="BpmnWorkBinding.SignalPublish"/>, the broadcast the host performs, and written back unchanged.
/// </summary>
public sealed class SignalThrowTests
{
    private static readonly XNamespace Model = BpmnXmlNames.Model;

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    private static string Document(string throwRef, string endRef) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs" targetNamespace="http://example.test/orders">
          <bpmn:signal id="Sig_Closed" name="Order closed" />
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:intermediateThrowEvent id="Announce" name="Announce closure">
              <bpmn:signalEventDefinition{throwRef} />
            </bpmn:intermediateThrowEvent>
            <bpmn:endEvent id="Closed">
              <bpmn:signalEventDefinition{endRef} />
            </bpmn:endEvent>
            <bpmn:sequenceFlow id="toAnnounce" sourceRef="Start" targetRef="Announce" />
            <bpmn:sequenceFlow id="toClosed" sourceRef="Announce" targetRef="Closed" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static string Resolvable => Document(" signalRef=\"Sig_Closed\"", " signalRef=\"Sig_Closed\"");

    private static BpmnElement Element(BpmnImportResult result, string id) =>
        result.Definitions.Processes.Single().Elements.Single(element => element.ElementId == id);

    [Theory]
    [InlineData("Announce", BpmnElementTypes.IntermediateThrowEvent)]
    [InlineData("Closed", BpmnElementTypes.EndEvent)]
    public void A_signal_throw_and_end_are_read_with_the_signal_name_and_bound_to_a_broadcast(string id, string elementType)
    {
        var read = _reader.Read(Resolvable);

        var element = Element(read, id);
        element.ElementType.ShouldBe(elementType);
        var definition = element.EventDefinitions.ShouldHaveSingleItem();
        definition.Type.ShouldBe(BpmnEventDefinitionTypes.Signal);
        definition.Properties[BpmnEventDefinitionProperties.Name].ShouldBe("Order closed");

        var binding = read.Bindings.OfType<BpmnWorkBinding.SignalPublish>().Single(candidate => candidate.ElementId == id);
        binding.SignalName.ShouldBe("Order closed");
        binding.BindingRef.ShouldBe(element.BindingRef);
        binding.Slot.ShouldBe(BpmnBindingSlot.Primary);
        read.Analysis.Issues.ShouldNotContain(issue => issue.ElementId == id && issue.Severity != BpmnImportIssueSeverity.Info);
    }

    [Fact]
    public void A_signal_throw_with_no_resolvable_signal_is_dropped()
    {
        var read = _reader.Read(Document(" signalRef=\"Sig_Missing\"", " signalRef=\"Sig_Closed\""));

        read.Definitions.Processes.Single().Elements.ShouldNotContain(element => element.ElementId == "Announce");
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Announce" && issue.Severity == BpmnImportIssueSeverity.Dropped && issue.Message.Contains("Signal throw event"));
    }

    [Fact]
    public void A_signal_end_with_no_resolvable_signal_degrades_to_a_plain_end()
    {
        var read = _reader.Read(Document(" signalRef=\"Sig_Closed\"", ""));

        var closed = Element(read, "Closed");
        closed.EventDefinitions.ShouldBeEmpty();
        closed.BindingRef.ShouldBeNull();
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Closed" && issue.Severity == BpmnImportIssueSeverity.Degraded && issue.Message.Contains("Signal end event"));
    }

    [Fact]
    public void A_signal_throw_beside_another_definition_is_dropped_with_a_finding_not_read_as_a_broadcast()
    {
        var read = _reader.Read(Document(" signalRef=\"Sig_Closed\" /><bpmn:compensateEventDefinition", " signalRef=\"Sig_Closed\""));

        read.Definitions.Processes.Single().Elements.ShouldNotContain(element => element.ElementId == "Announce");
        read.Bindings.ShouldNotContain(binding => binding.ElementId == "Announce");
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Announce" && issue.Severity == BpmnImportIssueSeverity.Dropped);
    }

    [Fact]
    public void A_signal_end_beside_another_definition_degrades_with_a_finding_not_read_as_a_broadcast()
    {
        var read = _reader.Read(Document(" signalRef=\"Sig_Closed\"", " signalRef=\"Sig_Closed\" /><bpmn:terminateEventDefinition"));

        var closed = Element(read, "Closed");
        closed.EventDefinitions.ShouldHaveSingleItem().Type.ShouldBe(BpmnEventDefinitionTypes.Terminate);
        closed.BindingRef.ShouldBeNull();
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Closed" && issue.Severity == BpmnImportIssueSeverity.Degraded);
    }

    [Fact]
    public void A_throw_or_end_with_two_signal_definitions_is_reported_not_read_as_one_broadcast()
    {
        var second = " signalRef=\"Sig_Closed\" /><bpmn:signalEventDefinition signalRef=\"Sig_Closed\"";
        var read = _reader.Read(Document(second, second));

        read.Bindings.ShouldNotContain(binding => binding is BpmnWorkBinding.SignalPublish);
        read.Definitions.Processes.Single().Elements.ShouldNotContain(element => element.ElementId == "Announce");
        Element(read, "Closed").EventDefinitions.ShouldBeEmpty();
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Announce" && issue.Severity == BpmnImportIssueSeverity.Dropped);
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Closed" && issue.Severity == BpmnImportIssueSeverity.Degraded);
    }

    [Fact]
    public void Both_are_written_back_with_their_signal_and_read_again_unchanged()
    {
        var first = _reader.Read(Resolvable);

        var xml = _writer.Write(first);
        var document = XDocument.Parse(xml);
        foreach (var (name, id) in new[] { ("intermediateThrowEvent", "Announce"), ("endEvent", "Closed") })
        {
            var written = document.Descendants(Model + name).Single(element => (string?)element.Attribute("id") == id);
            var signalRef = (string?)written.Element(Model + "signalEventDefinition").ShouldNotBeNull().Attribute("signalRef");
            document.Descendants(Model + "signal").Single(signal => (string?)signal.Attribute("id") == signalRef)
                .Attribute("name")!.Value.ShouldBe("Order closed");
        }

        var second = _reader.Read(xml);
        second.Bindings.OfType<BpmnWorkBinding.SignalPublish>().Select(binding => (binding.ElementId, binding.SignalName))
            .ShouldBe(first.Bindings.OfType<BpmnWorkBinding.SignalPublish>().Select(binding => (binding.ElementId, binding.SignalName)));
        foreach (var id in new[] { "Announce", "Closed" })
        {
            var definition = Element(second, id).EventDefinitions.ShouldHaveSingleItem();
            definition.Type.ShouldBe(BpmnEventDefinitionTypes.Signal);
            definition.Properties[BpmnEventDefinitionProperties.Name].ShouldBe("Order closed");
        }
    }

    [Fact]
    public void A_code_built_signal_end_takes_its_signal_name_from_the_binding()
    {
        var end = new BpmnElement("Closed", BpmnElementTypes.EndEvent, bindingRef: "closed",
            eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Signal)]);
        var process = new BpmnProcessDefinition("p", Elements: [new BpmnElement("Start", BpmnElementTypes.StartEvent), end],
            SequenceFlows: [new BpmnSequenceFlow("f", "Start", "Closed")]);

        var xml = _writer.Write(process, [new BpmnWorkBinding.SignalPublish("p", "Closed", "closed", BpmnBindingSlot.Primary, "Order closed")]);

        var read = _reader.Read(xml);
        Element(read, "Closed").EventDefinitions.ShouldHaveSingleItem().Properties[BpmnEventDefinitionProperties.Name].ShouldBe("Order closed");
    }
}
