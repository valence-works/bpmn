using Bpmn.Interchange;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

public sealed class WorkAvailabilityTests
{
    private const string Document = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          id="Definitions" targetNamespace="https://example.test/work-availability">
          <bpmn:process id="Orders" isExecutable="true">
            <bpmn:startEvent id="Start" />
            <bpmn:serviceTask id="Reserve" />
            <bpmn:boundaryEvent id="Timeout" attachedToRef="Reserve" cancelActivity="true">
              <bpmn:timerEventDefinition><bpmn:timeDuration>PT1M</bpmn:timeDuration></bpmn:timerEventDefinition>
            </bpmn:boundaryEvent>
            <bpmn:boundaryEvent id="UndoBoundary" attachedToRef="Reserve">
              <bpmn:compensateEventDefinition />
            </bpmn:boundaryEvent>
            <bpmn:serviceTask id="UndoReserve" isForCompensation="true" />
            <bpmn:endEvent id="TimedOut" />
            <bpmn:endEvent id="Done" />
            <bpmn:sequenceFlow id="StartReserve" sourceRef="Start" targetRef="Reserve" />
            <bpmn:sequenceFlow id="ReserveDone" sourceRef="Reserve" targetRef="Done" />
            <bpmn:sequenceFlow id="TimeoutDone" sourceRef="Timeout" targetRef="TimedOut" />
            <bpmn:association id="UndoAssociation" sourceRef="UndoReserve" targetRef="UndoBoundary" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static BpmnWorkBinding.UnboundTask ReviewBinding() =>
        new("Orders", "Review", "node-Review", BpmnBindingSlot.Primary, BpmnElementTypes.UserTask);

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    private static void ShouldHaveSameAnalysis(BpmnImportAnalysis actual, BpmnImportAnalysis expected)
    {
        actual.ProcessIds.ShouldBe(expected.ProcessIds);
        actual.ElementCounts.ShouldBe(expected.ElementCounts);
        actual.Issues.Select(issue => (issue.Severity, issue.Message, issue.ElementId, issue.ProcessId))
            .ShouldBe(expected.Issues.Select(issue => (issue.Severity, issue.Message, issue.ElementId, issue.ProcessId)));
    }

    [Fact]
    public void Assess_preserves_unknown_and_only_all_available_is_ready()
    {
        var binding = ReviewBinding();
        var calls = new List<BpmnWorkBinding>();

        var unknown = BpmnWorkAvailabilityValidator.Assess([binding], item =>
        {
            calls.Add(item);
            return BpmnWorkAvailability.Unknown;
        });

        unknown.Results.ShouldHaveSingleItem().Binding.ShouldBeSameAs(binding);
        unknown.Results[0].Availability.ShouldBe(BpmnWorkAvailability.Unknown);
        unknown.Results[0].Diagnostic.ShouldBeNull();
        unknown.AllWorkAvailable.ShouldBeFalse();
        calls.ShouldHaveSingleItem().ShouldBeSameAs(binding);

        var available = BpmnWorkAvailabilityValidator.Assess([binding], _ => BpmnWorkAvailability.Available);
        available.Results.ShouldHaveSingleItem().Availability.ShouldBe(BpmnWorkAvailability.Available);
        available.Results[0].Diagnostic.ShouldBeNull();
        available.AllWorkAvailable.ShouldBeTrue();

        var empty = BpmnWorkAvailabilityValidator.Assess([], _ => throw new InvalidOperationException("No binding should be classified."));
        empty.Results.ShouldBeEmpty();
        empty.AllWorkAvailable.ShouldBeTrue();
    }

    [Fact]
    public void Assess_keeps_every_binding_in_order_with_process_element_and_slot_identity()
    {
        var first = new BpmnWorkBinding.UnboundTask(
            "Process-A", "Shared", "node-a", BpmnBindingSlot.Primary, BpmnElementTypes.ServiceTask);
        var listener = new BpmnWorkBinding.SignalWait(
            "Process-A", "Shared", "listener-a", BpmnBindingSlot.ScopeListener, "OrderReady");
        var secondProcess = new BpmnWorkBinding.UnboundTask(
            "Process-B", "Shared", "node-b", BpmnBindingSlot.Primary, BpmnElementTypes.ServiceTask);
        var bindings = new BpmnWorkBinding[] { first, listener, secondProcess };
        var classified = new List<BpmnWorkBinding>();

        var assessment = BpmnWorkAvailabilityValidator.Assess(bindings, binding =>
        {
            classified.Add(binding);
            return binding.BindingRef switch
            {
                "node-a" => BpmnWorkAvailability.Available,
                "listener-a" => BpmnWorkAvailability.Unavailable,
                _ => BpmnWorkAvailability.Unknown
            };
        });

        classified.ShouldBe(bindings);
        assessment.Results.Select(result => (result.Binding.ProcessId, result.Binding.ElementId, result.Binding.BindingRef, result.Binding.Slot, result.Availability))
            .ShouldBe([
                ("Process-A", "Shared", "node-a", BpmnBindingSlot.Primary, BpmnWorkAvailability.Available),
                ("Process-A", "Shared", "listener-a", BpmnBindingSlot.ScopeListener, BpmnWorkAvailability.Unavailable),
                ("Process-B", "Shared", "node-b", BpmnBindingSlot.Primary, BpmnWorkAvailability.Unknown)
            ]);
        assessment.Results.Select(result => result.Binding).Zip(bindings)
            .ShouldAllBe(pair => ReferenceEquals(pair.First, pair.Second));
        assessment.Results[0].Diagnostic.ShouldBeNull();
        var diagnostic = assessment.Results[1].Diagnostic;
        diagnostic.ShouldNotBeNull();
        diagnostic!.ShouldContain("Process-A");
        diagnostic.ShouldContain("Shared");
        diagnostic.ShouldContain("listener-a");
        diagnostic.ShouldContain(nameof(BpmnBindingSlot.ScopeListener));
        assessment.Results[2].Diagnostic.ShouldBeNull();
        assessment.AllWorkAvailable.ShouldBeFalse();
    }

    [Fact]
    public void Assess_rejects_invalid_inputs_and_classifier_values_but_propagates_classifier_failures()
    {
        var binding = ReviewBinding();

        Should.Throw<ArgumentNullException>(() => BpmnWorkAvailabilityValidator.Assess(null!, _ => BpmnWorkAvailability.Available));
        Should.Throw<ArgumentNullException>(() => BpmnWorkAvailabilityValidator.Assess([binding], null!));
        Should.Throw<ArgumentException>(() => BpmnWorkAvailabilityValidator.Assess([null!], _ => BpmnWorkAvailability.Available));
        Should.Throw<ArgumentOutOfRangeException>(() => BpmnWorkAvailabilityValidator.Assess(
            [binding], _ => (BpmnWorkAvailability)int.MaxValue));

        var expected = new InvalidOperationException("Inventory lookup failed.");
        Should.Throw<InvalidOperationException>(() => BpmnWorkAvailabilityValidator.Assess(
            [binding], _ => throw expected)).ShouldBeSameAs(expected);
    }

    [Fact]
    public void Assess_returns_a_defensive_read_only_result_snapshot()
    {
        var binding = ReviewBinding();
        var input = new List<BpmnWorkBinding> { binding };
        var assessment = BpmnWorkAvailabilityValidator.Assess(input, _ => BpmnWorkAvailability.Available);

        input.Clear();

        assessment.Results.ShouldHaveSingleItem().Binding.ShouldBeSameAs(binding);
        Should.Throw<NotSupportedException>(() =>
            ((IList<BpmnWorkAvailabilityResult>)assessment.Results).Add(new(binding, BpmnWorkAvailability.Unknown)));
        assessment.AllWorkAvailable.ShouldBeTrue();
    }

    [Fact]
    public void Known_unavailable_boundary_host_and_compensation_handler_remain_in_import_and_round_trip()
    {
        var read = _reader.Read(Document);
        var originalDefinitions = read.Definitions;
        var originalBindings = read.Bindings.ToArray();
        var originalXml = _writer.Write(read);
        var analysisBefore = _reader.Analyze(Document);

        var assessment = BpmnWorkAvailabilityValidator.Assess(read.Bindings, binding =>
            binding.ElementId is "Reserve" or "UndoReserve"
                ? BpmnWorkAvailability.Unavailable
                : BpmnWorkAvailability.Available);

        assessment.Results.Where(result => result.Availability == BpmnWorkAvailability.Unavailable)
            .Select(result => (result.Binding.ProcessId, result.Binding.ElementId, result.Binding.BindingRef, result.Binding.Slot))
            .ShouldBe([
                ("Orders", "Reserve", "node-Reserve", BpmnBindingSlot.Primary),
                ("Orders", "UndoReserve", "node-UndoReserve", BpmnBindingSlot.Primary)
            ]);
        assessment.AllWorkAvailable.ShouldBeFalse();

        read.Definitions.ShouldBeSameAs(originalDefinitions);
        read.Bindings.Count.ShouldBe(originalBindings.Length);
        for (var index = 0; index < originalBindings.Length; index++)
            read.Bindings[index].ShouldBeSameAs(originalBindings[index]);
        _writer.Write(read).ShouldBe(originalXml);
        ShouldHaveSameAnalysis(_reader.Analyze(Document), analysisBefore);
        ShouldHaveSameAnalysis(read.Analysis, analysisBefore);

        var importedProcess = read.Definitions.Processes.Single();
        importedProcess.Elements.ShouldContain(element => element.ElementId == "Timeout" && element.AttachedToRef == "Reserve");
        importedProcess.Elements.ShouldContain(element => element.ElementId == "UndoBoundary"
            && element.AttachedToRef == "Reserve"
            && element.CompensationHandlerElementId == "UndoReserve");
        importedProcess.Elements.ShouldContain(element => element.ElementId == "UndoReserve" && element.IsForCompensation);
        importedProcess.SequenceFlows.Select(flow => (flow.FlowId, flow.SourceRef, flow.TargetRef)).ShouldBe([
            ("StartReserve", "Start", "Reserve"),
            ("ReserveDone", "Reserve", "Done"),
            ("TimeoutDone", "Timeout", "TimedOut")
        ]);
        read.Bindings.OfType<BpmnWorkBinding.UnboundTask>().Select(binding => binding.ElementId)
            .ShouldBe(["Reserve", "UndoReserve"]);
        read.Bindings.OfType<BpmnWorkBinding.TimerWait>().ShouldHaveSingleItem().ElementId.ShouldBe("Timeout");

        var roundTripped = _reader.Read(originalXml);
        var roundTrippedProcess = roundTripped.Definitions.Processes.Single();
        roundTrippedProcess.Elements.ShouldContain(element => element.ElementId == "Timeout" && element.AttachedToRef == "Reserve");
        roundTrippedProcess.Elements.ShouldContain(element => element.ElementId == "UndoBoundary"
            && element.CompensationHandlerElementId == "UndoReserve");
        roundTrippedProcess.Elements.ShouldContain(element => element.ElementId == "UndoReserve" && element.IsForCompensation);
        roundTripped.Bindings.OfType<BpmnWorkBinding.TimerWait>().ShouldHaveSingleItem().ElementId.ShouldBe("Timeout");
        roundTrippedProcess.SequenceFlows.Select(flow => (flow.FlowId, flow.SourceRef, flow.TargetRef))
            .ShouldBe(importedProcess.SequenceFlows.Select(flow => (flow.FlowId, flow.SourceRef, flow.TargetRef)));
        roundTripped.Bindings.Select(binding => (binding.GetType(), binding.ProcessId, binding.ElementId, binding.BindingRef, binding.Slot))
            .ShouldBe(read.Bindings.Select(binding => (binding.GetType(), binding.ProcessId, binding.ElementId, binding.BindingRef, binding.Slot)));
    }
}
