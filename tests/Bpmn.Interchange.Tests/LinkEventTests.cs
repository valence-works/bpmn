using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// A link intermediate throw and catch are read with their link name and no bound work, and written back unchanged;
/// a link that cannot be read is reported rather than kept half-formed.
/// </summary>
public sealed class LinkEventTests
{
    private static readonly XNamespace Model = BpmnXmlNames.Model;

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    private static string Document(string throwDefinition, string catchDefinition) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs" targetNamespace="http://example.test/orders">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:intermediateThrowEvent id="GoToShipping" name="To shipping">
              {throwDefinition}
            </bpmn:intermediateThrowEvent>
            <bpmn:intermediateCatchEvent id="Shipping" name="Shipping">
              {catchDefinition}
            </bpmn:intermediateCatchEvent>
            <bpmn:endEvent id="End" />
            <bpmn:sequenceFlow id="toThrow" sourceRef="Start" targetRef="GoToShipping" />
            <bpmn:sequenceFlow id="toEnd" sourceRef="Shipping" targetRef="End" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private const string Named = """<bpmn:linkEventDefinition name="Ship" />""";

    private static BpmnElement Element(BpmnImportResult result, string id) =>
        result.Definitions.Processes.Single().Elements.Single(element => element.ElementId == id);

    [Theory]
    [InlineData("GoToShipping", BpmnElementTypes.IntermediateThrowEvent)]
    [InlineData("Shipping", BpmnElementTypes.IntermediateCatchEvent)]
    public void A_link_throw_and_catch_are_read_with_their_link_name_and_no_work(string id, string elementType)
    {
        var read = _reader.Read(Document(Named, Named));

        var element = Element(read, id);
        element.ElementType.ShouldBe(elementType);
        element.BindingRef.ShouldBeNull();
        var definition = element.EventDefinitions.ShouldHaveSingleItem();
        definition.Type.ShouldBe(BpmnEventDefinitionTypes.Link);
        definition.Properties[BpmnEventDefinitionProperties.Name].ShouldBe("Ship");
        read.Bindings.ShouldNotContain(binding => binding.ElementId == id);
        read.Analysis.Issues.ShouldNotContain(issue => issue.ElementId == id && issue.Severity != BpmnImportIssueSeverity.Info);
    }

    [Theory]
    [InlineData("GoToShipping", """<bpmn:linkEventDefinition />""", Named, "Link throw event")]
    [InlineData("Shipping", Named, """<bpmn:linkEventDefinition name="  " />""", "Link catch event")]
    public void A_link_with_no_name_is_dropped_with_a_finding(string id, string throwDefinition, string catchDefinition, string message)
    {
        var read = _reader.Read(Document(throwDefinition, catchDefinition));

        read.Definitions.Processes.Single().Elements.ShouldNotContain(element => element.ElementId == id);
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == id && issue.Severity == BpmnImportIssueSeverity.Dropped && issue.Message.Contains(message));
    }

    [Theory]
    [InlineData("GoToShipping")]
    [InlineData("Shipping")]
    public void A_link_beside_another_event_definition_is_dropped_not_read_as_a_link(string id)
    {
        const string twoDefinitions = """<bpmn:linkEventDefinition name="Ship" /><bpmn:linkEventDefinition name="Ship" />""";
        var read = _reader.Read(id == "GoToShipping" ? Document(twoDefinitions, Named) : Document(Named, twoDefinitions));

        read.Definitions.Processes.Single().Elements.ShouldNotContain(element => element.ElementId == id);
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == id && issue.Severity == BpmnImportIssueSeverity.Dropped);
    }

    [Fact]
    public void A_link_throw_beside_a_message_definition_is_dropped_not_read_as_a_message_throw()
    {
        var withMessage = Document("""<bpmn:linkEventDefinition name="Ship" /><bpmn:messageEventDefinition messageRef="Msg" />""", Named)
            .Replace("""<bpmn:process id="P" isExecutable="true">""", """<bpmn:message id="Msg" name="Shipped" /><bpmn:process id="P" isExecutable="true">""");

        var read = _reader.Read(withMessage);

        read.Definitions.Processes.Single().Elements.ShouldNotContain(element => element.ElementId == "GoToShipping");
        read.Bindings.ShouldNotContain(binding => binding.ElementId == "GoToShipping");
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "GoToShipping" && issue.Severity == BpmnImportIssueSeverity.Dropped);
    }

    [Fact]
    public void Both_are_written_back_with_their_link_name_and_read_again_unchanged()
    {
        var first = _reader.Read(Document(Named, Named));

        var xml = _writer.Write(first);
        var document = XDocument.Parse(xml);
        foreach (var (name, id) in new[] { ("intermediateThrowEvent", "GoToShipping"), ("intermediateCatchEvent", "Shipping") })
        {
            var written = document.Descendants(Model + name).Single(element => (string?)element.Attribute("id") == id);
            ((string?)written.Element(Model + "linkEventDefinition").ShouldNotBeNull().Attribute("name")).ShouldBe("Ship");
        }

        var second = _reader.Read(xml);
        foreach (var id in new[] { "GoToShipping", "Shipping" })
        {
            var definition = Element(second, id).EventDefinitions.ShouldHaveSingleItem();
            definition.Type.ShouldBe(BpmnEventDefinitionTypes.Link);
            definition.Properties[BpmnEventDefinitionProperties.Name].ShouldBe("Ship");
        }
    }

    [Fact]
    public void A_link_start_or_end_event_is_not_a_link_position_and_still_degrades()
    {
        const string xml = """
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs" targetNamespace="http://example.test/orders">
              <bpmn:process id="P" isExecutable="true">
                <bpmn:startEvent id="Start"><bpmn:linkEventDefinition name="In" /></bpmn:startEvent>
                <bpmn:endEvent id="End"><bpmn:linkEventDefinition name="Out" /></bpmn:endEvent>
                <bpmn:sequenceFlow id="f" sourceRef="Start" targetRef="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        var read = _reader.Read(xml);

        Element(read, "Start").EventDefinitions.ShouldBeEmpty();
        Element(read, "End").EventDefinitions.ShouldBeEmpty();
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Start" && issue.Severity == BpmnImportIssueSeverity.Degraded);
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "End" && issue.Severity == BpmnImportIssueSeverity.Degraded);
    }
}
