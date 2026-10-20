using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// An error event definition's <c>errorRef</c> names a root <c>&lt;error&gt;</c>, and what BPMN matches on is
/// that error's code. The reader resolves the ref to the code once, onto the event definition, so nothing
/// downstream has to know where a code came from.
/// </summary>
public sealed class ErrorEventInterchangeTests
{
    private static readonly XNamespace Model = BpmnXmlNames.Model;

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    private const string Declarations = """
        <bpmn:error id="Error_Credit" name="Credit declined" errorCode="CREDIT_DECLINED" />
        <bpmn:error id="Error_IdOnly" name="Declared without a code" />
        """;

    private static string ErrorDefinition(string errorRef) =>
        errorRef.Length == 0 ? "<bpmn:errorEventDefinition />" : $"""<bpmn:errorEventDefinition errorRef="{errorRef}" />""";

    private static string Document(string body) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs" targetNamespace="http://example.test/errors">
          {Declarations}
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:serviceTask id="Charge" />
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Charge" />
            <bpmn:sequenceFlow id="F2" sourceRef="Charge" targetRef="Done" />
            {body}
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static string Boundary(string errorRef) => $"""
        <bpmn:boundaryEvent id="Declined" attachedToRef="Charge">{ErrorDefinition(errorRef)}</bpmn:boundaryEvent>
        <bpmn:endEvent id="Recovered" />
        <bpmn:sequenceFlow id="F3" sourceRef="Declined" targetRef="Recovered" />
        """;

    private static string EventSubprocess(string errorRef) => $"""
        <bpmn:subProcess id="OnDeclined" triggeredByEvent="true">
          <bpmn:startEvent id="DeclinedStart">{ErrorDefinition(errorRef)}</bpmn:startEvent>
          <bpmn:endEvent id="Handled" />
          <bpmn:sequenceFlow id="H1" sourceRef="DeclinedStart" targetRef="Handled" />
        </bpmn:subProcess>
        """;

    private static string ErrorEnd(string errorRef) => $"""
        <bpmn:exclusiveGateway id="Check" default="F4" />
        <bpmn:endEvent id="Failed">{ErrorDefinition(errorRef)}</bpmn:endEvent>
        <bpmn:sequenceFlow id="F4" sourceRef="Check" targetRef="Failed" />
        """;

    /// <summary>All three error constructs: a coded boundary, a coded event-subprocess start, and an end event per ref shape.</summary>
    private static string AllErrorConstructs() => Document(
        Boundary("Error_Credit")
        + EventSubprocess("Error_IdOnly")
        + """
          <bpmn:endEvent id="FailedCoded"><bpmn:errorEventDefinition errorRef="Error_Credit" /></bpmn:endEvent>
          <bpmn:endEvent id="FailedUncoded"><bpmn:errorEventDefinition /></bpmn:endEvent>
          <bpmn:endEvent id="FailedUndeclared"><bpmn:errorEventDefinition errorRef="Error_Undeclared" /></bpmn:endEvent>
          """);

    private static BpmnElement Element(BpmnImportResult read, string elementId) =>
        read.Definitions.Processes.Single().Elements.SingleOrDefault(element => element.ElementId == elementId)
        ?? read.Bindings.OfType<BpmnWorkBinding.NestedProcess>().SelectMany(binding => binding.Definition.Elements).Single(element => element.ElementId == elementId);

    private static string? ErrorRefOf(BpmnElement element) =>
        element.Properties.TryGetValue(BpmnXmlReader.ErrorRefPropertyKey, out var errorRef) ? errorRef : null;

    private static string? NameOf(BpmnElement element) =>
        element.EventDefinitions.Single().Properties.TryGetValue(BpmnEventDefinitionProperties.Name, out var name) ? name : null;

    private static string? CodeOf(BpmnElement element) =>
        element.EventDefinitions.Single().Properties.TryGetValue(BpmnEventDefinitionProperties.Code, out var code) ? code : null;

    [Theory]
    [InlineData("Error_Credit", "CREDIT_DECLINED")]
    [InlineData("Error_IdOnly", "Error_IdOnly")]
    [InlineData("Error_Undeclared", "Error_Undeclared")]
    [InlineData("", null)]
    public void An_error_boundary_carries_the_code_its_errorRef_resolves_to(string errorRef, string? expectedCode)
    {
        var read = _reader.Read(Document(Boundary(errorRef)));

        var boundary = read.Definitions.Processes.Single().Elements.Single(element => element.ElementId == "Declined");
        boundary.EventDefinitions.Single().Type.ShouldBe(BpmnEventDefinitionTypes.Error);
        CodeOf(boundary).ShouldBe(expectedCode);
    }

    [Theory]
    [InlineData("Error_Credit", "CREDIT_DECLINED")]
    [InlineData("Error_IdOnly", "Error_IdOnly")]
    [InlineData("Error_Undeclared", "Error_Undeclared")]
    [InlineData("", null)]
    public void An_error_event_subprocess_start_carries_the_code_its_errorRef_resolves_to(string errorRef, string? expectedCode)
    {
        var read = _reader.Read(Document(EventSubprocess(errorRef)));

        var body = read.Bindings.OfType<BpmnWorkBinding.NestedProcess>().Single(binding => binding.ElementId == "OnDeclined").Definition;
        var start = body.Elements.Single(element => element.ElementId == "DeclinedStart");
        start.EventDefinitions.Single().Type.ShouldBe(BpmnEventDefinitionTypes.Error);
        CodeOf(start).ShouldBe(expectedCode);
    }

    [Theory]
    [InlineData("Error_Credit", "CREDIT_DECLINED")]
    [InlineData("Error_IdOnly", "Error_IdOnly")]
    [InlineData("Error_Undeclared", "Error_Undeclared")]
    [InlineData("", null)]
    public void An_error_end_event_reads_with_its_error(string errorRef, string? expectedCode)
    {
        var read = _reader.Read(Document(ErrorEnd(errorRef)));

        var end = Element(read, "Failed");
        end.ElementType.ShouldBe(BpmnElementTypes.EndEvent);
        end.EventDefinitions.Single().Type.ShouldBe(BpmnEventDefinitionTypes.Error);
        CodeOf(end).ShouldBe(expectedCode);
        ErrorRefOf(end).ShouldBe(errorRef.Length == 0 ? null : errorRef);
        // A ref-less error end event is an uncoded throw, not a degraded one.
        read.Analysis.Issues.ShouldNotContain(issue => issue.ElementId == "Failed");
    }

    [Fact]
    public void An_error_definition_carries_the_declared_error_name()
    {
        var read = _reader.Read(AllErrorConstructs());

        NameOf(Element(read, "Declined")).ShouldBe("Credit declined");
        NameOf(Element(read, "DeclinedStart")).ShouldBe("Declared without a code");
        NameOf(Element(read, "FailedCoded")).ShouldBe("Credit declined");
        NameOf(Element(read, "FailedUndeclared")).ShouldBeNull();
        ErrorRefOf(Element(read, "DeclinedStart")).ShouldBe("Error_IdOnly");
    }

    [Fact]
    public void Every_error_construct_survives_a_round_trip()
    {
        var first = _reader.Read(AllErrorConstructs());

        var written = _writer.Write(first);
        var second = _reader.Read(written);

        foreach (var elementId in new[] { "Declined", "DeclinedStart", "FailedCoded", "FailedUncoded", "FailedUndeclared" })
        {
            var before = Element(first, elementId);
            var after = Element(second, elementId);
            after.ElementType.ShouldBe(before.ElementType, elementId);
            after.EventDefinitions.Single().Type.ShouldBe(BpmnEventDefinitionTypes.Error, elementId);
            CodeOf(after).ShouldBe(CodeOf(before), elementId);
            NameOf(after).ShouldBe(NameOf(before), elementId);
            ErrorRefOf(after).ShouldBe(ErrorRefOf(before), elementId);
        }

        var document = XDocument.Parse(written);
        var refs = document.Descendants(Model + "errorEventDefinition")
            .Select(definition => (string?)definition.Attribute("errorRef"))
            .ToArray();
        refs.ShouldBe(["Error_Credit", "Error_IdOnly", "Error_Credit", null, "Error_Undeclared"], ignoreOrder: true);
        // The undeclared ref gets a declaration, so the written document is self-contained.
        document.Root!.Elements(Model + "error").Select(error => (string?)error.Attribute("id"))
            .ShouldBe(["Error_Credit", "Error_IdOnly", "Error_Undeclared"], ignoreOrder: true);
        _writer.Write(second).ShouldBe(written);
    }

    [Fact]
    public void A_model_built_in_code_writes_an_errorRef_for_its_code()
    {
        var process = new BpmnProcessBuilder("P")
            .StartEvent("Start")
            .Task("Charge", bindingRef: "charge")
            .BoundaryEvent("Declined", "Charge", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error,
                new Dictionary<string, string> { [BpmnEventDefinitionProperties.Code] = "CREDIT_DECLINED" }))
            .EndEvent("Done")
            .EndEvent("Failed", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Error,
                new Dictionary<string, string> { [BpmnEventDefinitionProperties.Code] = "PAYMENT_LOST", [BpmnEventDefinitionProperties.Name] = "Payment lost" }))
            .ConnectSequence("Start", "Charge", "Done")
            .Connect("Declined", "Failed")
            .Build();
        var definitions = new BpmnDefinitionsBuilder()
            .Process(process)
            .Error("Error_Credit", "Credit declined", "CREDIT_DECLINED")
            .Build();

        var written = _writer.Write(definitions);
        var reread = _reader.Read(written);

        // An existing declaration for the code is reused; a code with none gets a synthesized declaration.
        ErrorRefOf(Element(reread, "Declined")).ShouldBe("Error_Credit");
        CodeOf(Element(reread, "Declined")).ShouldBe("CREDIT_DECLINED");
        var synthesized = XDocument.Parse(written).Root!.Elements(Model + "error")
            .Single(error => (string?)error.Attribute("errorCode") == "PAYMENT_LOST");
        ((string?)synthesized.Attribute("name")).ShouldBe("Payment lost");
        ErrorRefOf(Element(reread, "Failed")).ShouldBe((string?)synthesized.Attribute("id"));
        CodeOf(Element(reread, "Failed")).ShouldBe("PAYMENT_LOST");
        NameOf(Element(reread, "Failed")).ShouldBe("Payment lost");
    }

    [Fact]
    public void A_single_process_write_declares_the_code_and_name_each_errorRef_resolved_to()
    {
        // The single-process overload writes no root declarations of its own, so each error is rebuilt from
        // what the read resolved onto the event definition.
        var read = _reader.Read(AllErrorConstructs());

        var reread = _reader.Read(_writer.Write(read.Definitions.Processes.Single(), read.Bindings));

        foreach (var elementId in new[] { "Declined", "DeclinedStart", "FailedCoded", "FailedUncoded", "FailedUndeclared" })
        {
            CodeOf(Element(reread, elementId)).ShouldBe(CodeOf(Element(read, elementId)), elementId);
            NameOf(Element(reread, elementId)).ShouldBe(NameOf(Element(read, elementId)), elementId);
            ErrorRefOf(Element(reread, elementId)).ShouldBe(ErrorRefOf(Element(read, elementId)), elementId);
        }
    }
}
