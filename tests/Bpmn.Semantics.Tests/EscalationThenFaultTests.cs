using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// An escalation raised in a nested scope is delivered before that scope's same-evaluation fault reaches its
/// parent. A fault is the verdict for the work after the throw; it does not undo an escalation that already ran.
/// </summary>
public sealed class EscalationThenFaultTests
{
    private const string EscalationCode = "up";
    private const string FaultCode = "bpmn.gateway.no-flow-selected";

    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    private static BpmnProcessDefinition EscalatesThenFaults() =>
        new BpmnProcessBuilder("escalate-then-fault")
            .StartEvent("Start")
            .IntermediateThrowEvent("Raise", ProcessFixtures.Escalation(EscalationCode))
            .ExclusiveGateway("Undecidable")
            .EndEvent("EndLeft")
            .EndEvent("EndRight")
            .ConnectSequence("Start", "Raise", "Undecidable")
            .Connect("Undecidable", "EndLeft", condition: "Left")
            .Connect("Undecidable", "EndRight", condition: "Right")
            .Build();

    private static BpmnGraph BuildGraph(BpmnProcessDefinition definition) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full);

    private static (BpmnProcessDefinition Parent, IReadOnlyDictionary<string, BpmnProcessDefinition> Nested) ParentCatchesEscalation(bool interrupting)
    {
        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("EndSub")
            .BoundaryEvent("Escalated", "Sub", ProcessFixtures.Escalation(EscalationCode), interrupting)
            .Task("Handle", bindingRef: "handle")
            .EndEvent("EndHandled")
            .ConnectSequence("Start", "Sub", "EndSub")
            .ConnectSequence("Escalated", "Handle", "EndHandled")
            .Build();

        return (parent, new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
        {
            ["sub"] = EscalatesThenFaults()
        });
    }

    private static InMemoryProcessInstance StartWithParentCatcher(bool interrupting)
    {
        var (parent, nested) = ParentCatchesEscalation(interrupting);
        return new InMemoryBpmnHost(new InMemoryBpmnHostOptions { NestedProcesses = nested }).Start(parent);
    }

    [Fact]
    public void A_nested_scope_keeps_its_new_escalation_command_when_the_same_evaluation_faults()
    {
        var graph = BuildGraph(EscalatesThenFaults());
        var host = BpmnHostSnapshot.Root("child-scope") with { HasEnclosingScope = true };

        var evaluation = Interpreter.Start(new BpmnStartRequest(graph, null, host));

        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().Code.ShouldBe(FaultCode);
        evaluation.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EscalationRaised);
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().ShouldBeEmpty();
        evaluation.Commands.OfType<BpmnHostCommand.SignalEnclosingScope>().ShouldHaveSingleItem()
            .Code.ShouldBe(BpmnInterpreter.EscalationSignalCode);
    }

    [Fact]
    public void An_interrupting_parent_boundary_catches_the_signal_before_the_child_fault_callback()
    {
        var parent = StartWithParentCatcher(interrupting: true);
        var child = parent.Children.ShouldHaveSingleItem();

        child.IsFaulted.ShouldBeTrue(child.Transcript.ToString());
        child.Fault!.Code.ShouldBe(FaultCode);
        child.ScopeSignals.ShouldHaveSingleItem().Delivered.ShouldBeTrue("the host delivers the signal to the live parent work before reporting the child's fault");

        var caught = parent.Transcript.Single(entry => entry.Trigger is BpmnTranscriptTrigger.WorkSignalled);
        caught.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldContain(command => command.ElementId == "Sub");
        parent.IsFaulted.ShouldBeFalse(parent.Transcript.ToString());
        parent.PendingWork.Select(work => work.BindingRef).ShouldBe(["handle"]);

        parent.CompleteWork("handle");
        parent.IsCompleted.ShouldBeTrue(parent.Transcript.ToString());
    }

    [Fact]
    public void A_non_interrupting_parent_boundary_starts_its_path_before_the_child_fault_reaches_the_parent()
    {
        var parent = StartWithParentCatcher(interrupting: false);
        var child = parent.Children.ShouldHaveSingleItem();

        child.IsFaulted.ShouldBeTrue(child.Transcript.ToString());
        child.ScopeSignals.ShouldHaveSingleItem().Delivered.ShouldBeTrue("the host delivers the signal to the live parent work before reporting the child's fault");

        var signalIndex = parent.Transcript.ToList().FindIndex(entry => entry.Trigger is BpmnTranscriptTrigger.WorkSignalled);
        var faultIndex = parent.Transcript.ToList().FindIndex(entry => entry.Trigger is BpmnTranscriptTrigger.WorkFaulted);
        signalIndex.ShouldBeGreaterThanOrEqualTo(0, parent.Transcript.ToString());
        faultIndex.ShouldBeGreaterThan(signalIndex, parent.Transcript.ToString());

        var caught = parent.Transcript[signalIndex];
        caught.Continuation.ShouldBe(BpmnContinuation.Defer.Instance, parent.Transcript.ToString());
        caught.Commands.OfType<BpmnHostCommand.StartWork>().Select(command => command.BindingRef).ShouldContain("handle");
        parent.IsFaulted.ShouldBeTrue(parent.Transcript.ToString());
        parent.Fault!.Code.ShouldBe("bpmn.work.faulted");
        parent.CancelledWork.ShouldContain(work => work.ElementId == "Handle" && work.Reason == InMemoryProcessInstance.ScopeFinishedReason);
    }

    [Fact]
    public void A_legacy_pending_fault_absorbs_an_unmatched_signal_without_forwarding_it()
    {
        var definition = new BpmnProcessBuilder("legacy-pending-fault")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("End")
            .ConnectSequence("Start", "Sub", "End")
            .Build();
        var graph = BuildGraph(definition);
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = EscalationCode
        });
        var prior = new BpmnExecutionState(pendingFault: new BpmnPendingFault("legacy.fault", "fault already parked"));

        var evaluation = Interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
            graph,
            prior,
            BpmnHostSnapshot.Root("scope") with { HasEnclosingScope = true },
            "sub",
            "late-subprocess-handle",
            BpmnInterpreter.EscalationSignalCode,
            payload));

        evaluation.Continuation.ShouldBe(new BpmnContinuation.Fault("legacy.fault", "fault already parked"));
        evaluation.Commands.OfType<BpmnHostCommand.SignalEnclosingScope>().ShouldBeEmpty();
        evaluation.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.Canceled);
    }
}
