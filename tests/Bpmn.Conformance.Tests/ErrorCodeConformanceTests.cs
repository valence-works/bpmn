using Bpmn.Interchange;
using Bpmn.Model;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Conformance.Tests;

/// <summary>
/// An error catcher read from a document catches by the code it declares, and reports that code when it catches;
/// an error end event read from a document throws the code its <c>errorRef</c> resolves to. The interpreter reads
/// both codes from the event definitions, so these tests hold the reader and the interpreter to the same key, end
/// to end, rather than each against its own idea of it.
/// </summary>
public sealed class ErrorCodeConformanceTests
{
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();
    private readonly BpmnXmlReader _reader = new();

    private const string Declarations = """
        <bpmn:error id="Error_Credit" name="Credit declined" errorCode="CREDIT_DECLINED" />
        <bpmn:error id="Error_IdOnly" name="Declared without a code" />
        """;

    private static string Document(string errorRef, string catcher) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs" targetNamespace="http://example.test/errors">
          {Declarations}
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:serviceTask id="Charge" />
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Charge" />
            <bpmn:sequenceFlow id="F2" sourceRef="Charge" targetRef="Done" />
            {(catcher == "boundary" ? Boundary(errorRef) : EventSubprocess(errorRef))}
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static string ErrorDefinition(string errorRef) =>
        errorRef.Length == 0 ? "<bpmn:errorEventDefinition />" : $"""<bpmn:errorEventDefinition errorRef="{errorRef}" />""";

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

    [Theory]
    [InlineData("boundary", "Error_Credit", "Declined", "CREDIT_DECLINED")]
    [InlineData("boundary", "Error_IdOnly", "Declined", "Error_IdOnly")]
    [InlineData("boundary", "", "Declined", null)]
    [InlineData("event-subprocess", "Error_Credit", "OnDeclined", "CREDIT_DECLINED")]
    [InlineData("event-subprocess", "Error_IdOnly", "OnDeclined", "Error_IdOnly")]
    [InlineData("event-subprocess", "", "OnDeclined", null)]
    public void A_catcher_read_from_a_document_reports_the_code_it_declares(
        string catcher,
        string errorRef,
        string catchingElementId,
        string? expectedCode)
    {
        var faulted = FaultCharge(Document(errorRef, catcher), expectedCode);

        faulted.ErrorDisposition.ShouldBe(new BpmnErrorDisposition.Caught(catchingElementId, expectedCode));
    }

    [Theory]
    [InlineData("boundary", "Error_Credit")]
    [InlineData("boundary", "Error_IdOnly")]
    [InlineData("event-subprocess", "Error_Credit")]
    [InlineData("event-subprocess", "Error_IdOnly")]
    public void A_coded_catcher_read_from_a_document_does_not_catch_another_code(string catcher, string errorRef)
    {
        var faulted = FaultCharge(Document(errorRef, catcher), "SOMETHING_ELSE");

        faulted.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
        faulted.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().ErrorCode.ShouldBe("SOMETHING_ELSE");
    }

    private BpmnEvaluation FaultCharge(string document, string? errorCode)
    {
        var graph = Build(_reader.Read(document));

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, BpmnHostSnapshot.Root("scope-1")));
        var charge = started.Commands.OfType<BpmnHostCommand.StartWork>().Single(start => start.ElementId == "Charge");

        return Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
            graph, started.State, BpmnHostSnapshot.Root("scope-1"), charge.BindingRef, "h-charge", "Credit declined") { ErrorCode = errorCode });
    }

    private static BpmnGraph Build(BpmnImportResult imported)
    {
        var definition = imported.Definitions.Processes.Single();
        var nested = imported.Bindings.OfType<BpmnWorkBinding.NestedProcess>()
            .ToDictionary(binding => binding.BindingRef, binding => binding.Definition, StringComparer.Ordinal);
        return BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition, nested.GetValueOrDefault), BpmnHostCapabilities.Full);
    }

    [Theory]
    [InlineData("Error_Credit", "CREDIT_DECLINED")]
    [InlineData("Error_IdOnly", "Error_IdOnly")]
    [InlineData("", null)]
    public void An_error_end_event_read_from_a_document_throws_the_code_its_errorRef_resolves_to(string errorRef, string? expectedCode)
    {
        var imported = _reader.Read($"""
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs" targetNamespace="http://example.test/errors">
              {Declarations}
              <bpmn:process id="P" isExecutable="true">
                <bpmn:startEvent id="Start" />
                <bpmn:endEvent id="Failed">{ErrorDefinition(errorRef)}</bpmn:endEvent>
                <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Failed" />
              </bpmn:process>
            </bpmn:definitions>
            """);

        var started = Interpreter.Start(new BpmnStartRequest(Build(imported), null, BpmnHostSnapshot.Root("scope-1")));

        var fault = started.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe(BpmnInterpreter.ErrorThrownFaultCode);
        fault.ErrorCode.ShouldBe(expectedCode);
        started.ErrorDisposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
    }
}
