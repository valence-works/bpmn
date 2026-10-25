using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class ConcurrentWorkHostTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Repeated_event_subprocess_bodies_complete_independently(bool nestedEscalation, bool reverseCompletion)
    {
        var handler = new BpmnProcessBuilder("handler")
            .Element(new BpmnElement("HandlerStart", BpmnElementTypes.StartEvent,
                eventDefinitions: [Escalation()], cancelActivity: false))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("HandlerStart", "Handle", "HandlerEnd")
            .Build();
        var raiser = RaisingProcess();
        var builder = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .EndEvent("End")
            .SubProcess("Handler", bindingRef: "handler", triggeredByEvent: true);
        if (nestedEscalation)
            builder.SubProcess("Raiser", bindingRef: "raiser").ConnectSequence("Start", "Raiser", "End");
        else
            builder.IntermediateThrowEvent("First", Escalation())
                .IntermediateThrowEvent("Second", Escalation())
                .ConnectSequence("Start", "First", "Second", "End");
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
            {
                ["handler"] = handler,
                ["raiser"] = raiser
            }
        });
        var instance = host.Start(builder.Build());
        var bodies = instance.Children.Where(child => child.Graph.Definition.ProcessId == "handler").ToArray();
        var work = instance.PendingWork.Where(item => item.BindingRef == "handler").ToArray();

        bodies.Length.ShouldBe(2);
        AssertDistinctInvocations(instance, work);
        if (reverseCompletion)
            Array.Reverse(bodies);
        bodies[0].CompleteWork("handle");

        instance.IsCompleted.ShouldBeFalse();
        instance.PendingWork.Count(item => item.BindingRef == "handler").ShouldBe(1);
        bodies[1].CompleteWork("handle");
        instance.PendingWork.ShouldNotContain(item => item.BindingRef == "handler");
        if (nestedEscalation)
        {
            instance.IsCompleted.ShouldBeFalse();
            instance.Children.Single(child => child.Graph.Definition.ProcessId == "raiser").CompleteWork("hold");
        }

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.EventSubprocessCompleted).ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_boundary_recovery_keeps_each_invocation_until_its_own_completion(bool reverseCompletion)
    {
        var parent = new BpmnProcessBuilder("boundary-parent")
            .StartEvent("Start")
            .SubProcess("Raiser", bindingRef: "raiser")
            .BoundaryEvent("Notification", "Raiser", Escalation(), interrupting: false)
            .Task("Recover", bindingRef: "recover")
            .EndEvent("NormalEnd")
            .EndEvent("RecoveryEnd")
            .ConnectSequence("Start", "Raiser", "NormalEnd")
            .ConnectSequence("Notification", "Recover", "RecoveryEnd")
            .Build();
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["raiser"] = RaisingProcess() }
        });
        var instance = host.Start(parent);
        var recovery = instance.PendingWork.Where(work => work.BindingRef == "recover").ToArray();

        AssertDistinctInvocations(instance, recovery);
        if (reverseCompletion)
            Array.Reverse(recovery);
        instance.CompleteWork(recovery[0]);
        instance.PendingWork.Where(work => work.BindingRef == "recover").ShouldHaveSingleItem().Handle.ShouldBe(recovery[1].Handle);
        instance.CompleteWork(recovery[1]);
        instance.IsCompleted.ShouldBeFalse();
        instance.Children.ShouldHaveSingleItem().CompleteWork("hold");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.CancelledWork.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parallel_arrivals_at_one_task_complete_in_either_order(bool reverseCompletion)
    {
        var instance = new InMemoryBpmnHost().Start(RepeatedTask());
        var work = instance.PendingWork.ToArray();
        AssertDistinctInvocations(instance, work);

        if (reverseCompletion)
            Array.Reverse(work);
        instance.CompleteWork(work[0]);
        instance.PendingWork.ShouldHaveSingleItem().Handle.ShouldBe(work[1].Handle);
        instance.IsCompleted.ShouldBeFalse();
        instance.CompleteWork(work[1]);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Transcript.SelectMany(entry => entry.TokenMoves)
            .Count(move => move.ElementId == "End" && move.To == Bpmn.Model.State.BpmnTokenStatus.Consumed).ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_caught_fault_cancels_only_the_reported_invocation(bool faultSecond)
    {
        var instance = new InMemoryBpmnHost().Start(RepeatedTask(withErrorBoundary: true));
        var work = instance.PendingWork.ToArray();
        AssertDistinctInvocations(instance, work);
        var failed = work[faultSecond ? 1 : 0];
        var sibling = work[faultSecond ? 0 : 1];

        instance.FaultWork(failed, "one invocation failed");

        instance.IsFaulted.ShouldBeFalse();
        instance.PendingWork.Where(item => item.BindingRef == "work").ShouldHaveSingleItem().Handle.ShouldBe(sibling.Handle);
        instance.PendingWork.Count(item => item.BindingRef == "recover").ShouldBe(1);
        instance.CancelledWork.ShouldNotContain(item => item.Handle == sibling.Handle);
        instance.CompleteWork(sibling);
        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_signal_interrupts_the_reported_invocation_and_preserves_its_sibling(bool signalSecond)
    {
        var definition = RepeatedTask(withEscalationBoundary: true);
        var instance = new InMemoryBpmnHost().Start(definition);
        var work = instance.PendingWork.ToArray();
        var signalled = work[signalSecond ? 1 : 0];
        var sibling = work[signalSecond ? 0 : 1];

        instance.EscalateWork(signalled.Handle, "notice");

        instance.CancelledWork.ShouldHaveSingleItem().Handle.ShouldBe(signalled.Handle);
        instance.PendingWork.Where(item => item.BindingRef == "work").ShouldHaveSingleItem().Handle.ShouldBe(sibling.Handle);
        instance.CompleteWork(sibling);
        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void A_nested_escalation_cancels_only_its_same_slot_parent_invocation()
    {
        var parent = RepeatedTask(withEscalationBoundary: true);
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
            {
                ["work"] = SignalThenHoldProcess()
            }
        });
        var instance = host.Start(parent);
        var work = instance.PendingWork.Where(item => item.BindingRef == "work").ToArray();
        AssertDistinctInvocations(instance, work);
        var signalled = work[0];
        var sibling = work[1];
        var signalledChild = instance.ChildScope(signalled.Handle)!;
        var siblingChild = instance.ChildScope(sibling.Handle)!;
        signalledChild.ParentWorkHandle.ShouldBe(signalled.Handle);
        siblingChild.ParentWorkHandle.ShouldBe(sibling.Handle);
        signalledChild.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("trigger");
        siblingChild.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("trigger");

        signalledChild.CompleteWork("trigger");

        instance.IsCompleted.ShouldBeFalse();
        instance.CancelledWork.Select(item => item.Handle).ShouldBe([signalled.Handle]);
        instance.PendingWork.Where(item => item.BindingRef == "work").ShouldHaveSingleItem().Handle.ShouldBe(sibling.Handle);
        instance.PendingWork.Count(item => item.BindingRef == "recover").ShouldBe(1);
        signalledChild.IsCancelled.ShouldBeTrue();
        siblingChild.IsCancelled.ShouldBeFalse();
        siblingChild.PendingWork.ShouldHaveSingleItem().BindingRef.ShouldBe("trigger");

        siblingChild.CompleteWork("trigger");

        instance.PendingWork.ShouldAllBe(item => item.BindingRef == "recover");
        instance.PendingWork.Count.ShouldBe(2);
        instance.CancelledWork.Select(item => item.Handle).ShouldBe([signalled.Handle, sibling.Handle]);
        siblingChild.IsCancelled.ShouldBeTrue();
        var recovery = instance.PendingWork.ToArray();
        instance.CompleteWork(recovery[1]);
        instance.IsCompleted.ShouldBeFalse();
        instance.CompleteWork(recovery[0]);
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void A_signal_for_retired_work_does_not_act_on_a_live_sibling()
    {
        var instance = new InMemoryBpmnHost().Start(RepeatedTask(withEscalationBoundary: true));
        var work = instance.PendingWork.ToArray();
        instance.CompleteWork(work[0]);
        var transcriptCount = instance.Transcript.Count;

        instance.SignalWork(work[0], BpmnInterpreter.EscalationSignalCode,
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["code"] = "notice" }));

        instance.Transcript.Count.ShouldBe(transcriptCount);
        instance.CancelledWork.ShouldBeEmpty();
        instance.PendingWork.ShouldHaveSingleItem().Handle.ShouldBe(work[1].Handle);
        instance.CompleteWork(work[1]);
        instance.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void A_live_work_ledger_round_trip_preserves_invocation_identity()
    {
        var instance = new InMemoryBpmnHost().Start(RepeatedTask());
        var ledger = instance.PendingWork.Select(work => new BpmnLiveWork(work.BindingRef, work.IterationId, work.Handle)
            { WorkTokenId = work.WorkTokenId }).ToArray();

        var restored = JsonSerializer.Deserialize<BpmnLiveWork[]>(JsonSerializer.Serialize(ledger))!;

        restored.ShouldBe(ledger);
        restored.Select(work => work.WorkTokenId).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        restored.ShouldAllBe(work => work.WorkTokenId != null);
    }

    [Fact]
    public void A_legacy_live_work_ledger_without_identity_still_deserializes()
    {
        const string json = """[{"BindingRef":"work","IterationId":null,"Handle":"legacy-handle"}]""";

        var restored = JsonSerializer.Deserialize<BpmnLiveWork[]>(json)!.ShouldHaveSingleItem();

        restored.ShouldBe(new BpmnLiveWork("work", null, "legacy-handle"));
        restored.WorkTokenId.ShouldBeNull();
    }

    private static void AssertDistinctInvocations(InMemoryProcessInstance instance, IReadOnlyList<InMemoryWorkItem> work)
    {
        work.Count.ShouldBe(2);
        work.ShouldAllBe(item => item.IterationId == null && item.WorkTokenId != null);
        work.Select(item => item.Handle).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        work.Select(item => item.WorkTokenId).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        var startedTokens = instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Where(start => start.BindingRef == work[0].BindingRef).Select(start => start.TokenId).ToArray();
        work.Select(item => item.WorkTokenId).ShouldBe(startedTokens);
    }

    private static BpmnProcessDefinition RepeatedTask(bool withErrorBoundary = false, bool withEscalationBoundary = false)
    {
        var builder = new BpmnProcessBuilder("repeated-task")
            .StartEvent("Start")
            .ParallelGateway("Fork")
            .Element(new BpmnElement("Work", withEscalationBoundary ? BpmnElementTypes.SubProcess : BpmnElementTypes.Task, bindingRef: "work"))
            .EndEvent("End")
            .Connect("Start", "Fork")
            .Connect("Fork", "Work", flowId: "first")
            .Connect("Fork", "Work", flowId: "second")
            .Connect("Work", "End");
        if (withErrorBoundary || withEscalationBoundary)
            builder.BoundaryEvent("Caught", "Work", withErrorBoundary ? new BpmnEventDefinition(BpmnEventDefinitionTypes.Error) : Escalation())
                .Task("Recover", bindingRef: "recover")
                .EndEvent("RecoveredEnd")
                .ConnectSequence("Caught", "Recover", "RecoveredEnd");
        return builder.Build();
    }

    private static BpmnProcessDefinition RaisingProcess() => new BpmnProcessBuilder("raiser")
        .StartEvent("Start")
        .IntermediateThrowEvent("First", Escalation())
        .IntermediateThrowEvent("Second", Escalation())
        .Task("Hold", bindingRef: "hold")
        .EndEvent("End")
        .ConnectSequence("Start", "First", "Second", "Hold", "End")
        .Build();

    private static BpmnProcessDefinition SignalThenHoldProcess() => new BpmnProcessBuilder("signal-then-hold")
        .StartEvent("Start")
        .Task("Trigger", bindingRef: "trigger")
        .IntermediateThrowEvent("Escalate", Escalation())
        .Task("Hold", bindingRef: "hold")
        .EndEvent("End")
        .ConnectSequence("Start", "Trigger", "Escalate", "Hold", "End")
        .Build();

    private static BpmnEventDefinition Escalation() => new(BpmnEventDefinitionTypes.Escalation,
        new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = "notice" });
}
