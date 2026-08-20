using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// An escalation throw activating an interrupting event subprocess in its own scope, watched from the host's
/// side of the port.
/// <para>
/// This activation drains the scope the same way an interrupting boundary event does, so the work it abandons
/// has to be torn down: nothing else will ever complete a human task or a message subscription whose scope has
/// just been interrupted, and a host that is never told holds it for the rest of the scope's life.
/// </para>
/// </summary>
public sealed class OwnScopeEscalationTests
{
    private readonly InMemoryBpmnHost _host;
    private readonly InMemoryProcessInstance _instance;

    public OwnScopeEscalationTests()
    {
        var (parent, body) = ProcessFixtures.OwnScopeEscalationInterruptsASecondBranch();
        _host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["on-overdue"] = body }
        });
        _instance = _host.Start(parent);
    }

    private static IReadOnlyList<string> BindingRefs(InMemoryProcessInstance instance) =>
        instance.PendingWork.Select(work => work.BindingRef).ToArray();

    [Fact]
    public void An_interrupting_own_scope_activation_tears_down_the_branch_it_abandons()
    {
        var abandoned = _instance.ResolveWork("long-running").Handle;

        _instance.CompleteWork("escalate-trigger");

        var teardown = _instance.CancelledWork.ShouldHaveSingleItem(_instance.Transcript.ToString());
        teardown.Handle.ShouldBe(abandoned);
        teardown.ElementId.ShouldBe("LongRunning");
        BindingRefs(_instance).ShouldBe(["on-overdue"], "the interrupted scope runs the event subprocess body and nothing else");
    }

    [Fact]
    public void The_event_subprocess_body_still_runs_to_completion_after_the_teardown()
    {
        _instance.CompleteWork("escalate-trigger");

        var handler = _instance.Children.ShouldHaveSingleItem();
        BindingRefs(handler).ShouldBe(["handle"]);

        handler.CompleteWork("handle");

        _instance.IsCompleted.ShouldBeTrue(_instance.Transcript.ToString());
        _instance.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName);
    }

    [Fact]
    public void The_scope_declares_the_subtree_cancellation_its_activation_uses()
    {
        var (parent, _) = ProcessFixtures.OwnScopeEscalationInterruptsASecondBranch();

        var requirements = BpmnCapabilityRequirements.Analyze(parent);

        requirements.Required.HasFlag(BpmnHostCapabilities.SubtreeCancellation)
            .ShouldBeTrue("the event subprocess element already drives the capability its activation needs");
        requirements.DrivingElementIds[BpmnHostCapabilities.SubtreeCancellation].ShouldContain("OnOverdue");
    }
}
