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
    private readonly BpmnXmlReader _reader = new();

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
}
