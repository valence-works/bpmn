using Bpmn.Interchange;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Conformance.Tests;

public sealed class HostWorkCompositionTests
{
    private static BpmnImportResult RoundTrip(string xml)
    {
        var reader = new BpmnXmlReader();
        var imported = reader.Read(xml);
        imported.Analysis.Issues.ShouldNotContain(issue => issue.Severity != BpmnImportIssueSeverity.Info);
        var reread = reader.Read(new BpmnXmlWriter().Write(imported.Definitions));
        reread.Analysis.Issues.ShouldNotContain(issue => issue.Severity != BpmnImportIssueSeverity.Info);
        return reread;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_imported_call_activity_routes_child_escalation_to_its_boundary(bool interrupting)
    {
        var imported = RoundTrip(CallActivityDocument(interrupting));
        var call = imported.Bindings.OfType<BpmnWorkBinding.CallProcess>().ShouldHaveSingleItem();
        call.CalledElement.ShouldBe("Child");
        var childDefinition = imported.Definitions.Processes.Single(process => process.ProcessId == call.CalledElement);
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition> { [call.BindingRef] = childDefinition }
        });
        var instance = host.Start(imported.Definitions.Processes.Single(process => process.ProcessId == "Parent"));
        var child = instance.Children.ShouldHaveSingleItem();

        child.CompleteWork("node-Trigger");

        instance.IsFaulted.ShouldBeFalse();
        instance.PendingWork.ShouldContain(work => work.BindingRef == "node-Recover");
        child.IsCancelled.ShouldBe(interrupting);
        if (interrupting)
        {
            child.PendingWork.ShouldBeEmpty();
            instance.CancelledWork.ShouldContain(work => work.ElementId == "Call"
                && work.Reason == BpmnInterpreter.EscalationHostInterruptedReason);
            instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["node-Recover"]);
        }
        else
        {
            child.PendingWork.Select(work => work.BindingRef).ShouldBe(["node-Hold"]);
            instance.PendingWork.ShouldContain(work => work.BindingRef == call.BindingRef);
        }

        instance.CompleteWork("node-Recover");
        if (!interrupting)
        {
            instance.IsCompleted.ShouldBeFalse();
            child.CompleteWork("node-Hold");
            instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["node-After"]);
            instance.CompleteWork("node-After");
        }

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.IsFaulted.ShouldBeFalse();
    }

    [Fact]
    public void An_imported_ordinary_task_runs_as_host_provided_compensation()
    {
        var imported = RoundTrip(CompensationDocument);
        var definition = imported.Definitions.Processes.ShouldHaveSingleItem();
        var handler = definition.Elements.Single(element => element.ElementId == "Undo");
        handler.IsForCompensation.ShouldBeTrue();
        var binding = imported.Bindings.OfType<BpmnWorkBinding.UnboundTask>()
            .Single(work => work.ElementId == handler.ElementId);
        handler.BindingRef.ShouldBe(binding.BindingRef);
        definition.Elements.Single(element => element.ElementId == "CompensateHost")
            .CompensationHandlerElementId.ShouldBe(handler.ElementId);

        var instance = new InMemoryBpmnHost().Start(definition);
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["node-Host"]);
        instance.CompleteWork("node-Host");

        instance.State.Compensables.ShouldHaveSingleItem().Status.ShouldBe(BpmnCompensableStatus.Claimed);
        instance.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe(binding.BindingRef);
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .ShouldContain(work => work.ElementId == handler.ElementId && work.BindingRef == binding.BindingRef);
        instance.CompleteWork(binding.BindingRef);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.IsFaulted.ShouldBeFalse();
        instance.State.Compensables.ShouldHaveSingleItem().Status.ShouldBe(BpmnCompensableStatus.Compensated);
        instance.State.CompensationRuns.ShouldBeEmpty();
    }

    private static string CallActivityDocument(bool interrupting) => $$"""
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="urn:host-work">
          <escalation id="Review" escalationCode="review" />
          <process id="Parent" isExecutable="true">
            <startEvent id="Start" />
            <callActivity id="Call" calledElement="Child" />
            <boundaryEvent id="Escalated" attachedToRef="Call" cancelActivity="{{interrupting.ToString().ToLowerInvariant()}}">
              <escalationEventDefinition escalationRef="Review" />
            </boundaryEvent>
            <serviceTask id="Recover" />
            <serviceTask id="After" />
            <endEvent id="End" />
            <sequenceFlow id="F1" sourceRef="Start" targetRef="Call" />
            <sequenceFlow id="F2" sourceRef="Call" targetRef="After" />
            <sequenceFlow id="F3" sourceRef="After" targetRef="End" />
            <sequenceFlow id="F4" sourceRef="Escalated" targetRef="Recover" />
            <sequenceFlow id="F5" sourceRef="Recover" targetRef="End" />
          </process>
          <process id="Child" isExecutable="true">
            <startEvent id="ChildStart" />
            <serviceTask id="Trigger" />
            <intermediateThrowEvent id="Raise"><escalationEventDefinition escalationRef="Review" /></intermediateThrowEvent>
            <serviceTask id="Hold" />
            <endEvent id="ChildEnd" />
            <sequenceFlow id="C1" sourceRef="ChildStart" targetRef="Trigger" />
            <sequenceFlow id="C2" sourceRef="Trigger" targetRef="Raise" />
            <sequenceFlow id="C3" sourceRef="Raise" targetRef="Hold" />
            <sequenceFlow id="C4" sourceRef="Hold" targetRef="ChildEnd" />
          </process>
        </definitions>
        """;

    private const string CompensationDocument = """
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" targetNamespace="urn:host-work">
          <process id="Compensation" isExecutable="true">
            <startEvent id="Start" />
            <serviceTask id="Host" />
            <boundaryEvent id="CompensateHost" attachedToRef="Host"><compensateEventDefinition /></boundaryEvent>
            <serviceTask id="Undo" isForCompensation="true" />
            <association id="Handler" sourceRef="CompensateHost" targetRef="Undo" />
            <intermediateThrowEvent id="Replay"><compensateEventDefinition /></intermediateThrowEvent>
            <endEvent id="End" />
            <sequenceFlow id="F1" sourceRef="Start" targetRef="Host" />
            <sequenceFlow id="F2" sourceRef="Host" targetRef="Replay" />
            <sequenceFlow id="F3" sourceRef="Replay" targetRef="End" />
          </process>
        </definitions>
        """;
}
