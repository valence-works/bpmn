using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The in-memory host driven through the BPMN constructs that make the host port worth having: routing,
/// interruption, iteration, failure and nesting.
/// <para>
/// These are the second implementation of the port. If a construct works here and in a real host, the claim
/// that the interpreter is host-agnostic has been tested rather than asserted.
/// </para>
/// </summary>
public sealed class InMemoryHostTests
{
    private readonly InMemoryBpmnHost _host = new();

    private InMemoryProcessInstance Start(BpmnProcessDefinition definition, InMemoryVariables? variables = null) =>
        _host.Start(definition, variables: variables);

    private static IReadOnlyList<string> ElementIds(IEnumerable<InMemoryWorkItem> work) =>
        work.Select(item => item.ElementId).ToArray();

    // --- Plain token flow ------------------------------------------------------------------------------

    [Fact]
    public void A_linear_sequence_waits_for_its_work_and_then_completes()
    {
        var instance = Start(ProcessFixtures.Linear());

        instance.IsCompleted.ShouldBeFalse();
        instance.Continuation.ShouldBeOfType<BpmnContinuation.Defer>();
        ElementIds(instance.PendingWork).ShouldBe(["Work"]);

        instance.CompleteWork("work");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName);
        instance.PendingWork.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Approved", "Approve", "EndApproved")]
    [InlineData("Rejected", "Reject", "EndRejected")]
    public void An_exclusive_gateway_takes_the_branch_its_decision_named(string outcome, string expectedTask, string expectedEnd)
    {
        var instance = Start(ProcessFixtures.ExclusiveChoice());

        ElementIds(instance.PendingWork).ShouldBe(["Decide"]);
        instance.CompleteWork("decide", outcome);

        ElementIds(instance.PendingWork).ShouldBe([expectedTask]);
        instance.CompleteWork(instance.PendingWork[0]);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        VisitedElements(instance).ShouldContain(expectedEnd);
        VisitedElements(instance).ShouldNotContain(expectedTask == "Approve" ? "Reject" : "Approve");
    }

    [Fact]
    public void A_parallel_gateway_splits_into_both_branches_and_joins_only_when_both_arrive()
    {
        var instance = Start(ProcessFixtures.ParallelSplitAndJoin());

        ElementIds(instance.PendingWork).ShouldBe(["Left", "Right"]);

        instance.CompleteWork("left");
        instance.IsCompleted.ShouldBeFalse("the join must wait for the second branch");
        instance.State.Tokens.ShouldContain(token => token.AtElementId == "Join" && token.Status == BpmnTokenStatus.WaitingAtJoin);

        instance.CompleteWork("right");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    // --- Boundary events -------------------------------------------------------------------------------

    [Fact]
    public void An_interrupting_timer_boundary_fires_on_clock_advance_and_terminates_its_activity()
    {
        var instance = Start(ProcessFixtures.TimerBoundary("P7D"));

        ElementIds(instance.PendingWork).ShouldBe(["Long", "Timeout"]);
        _host.Clock.Pending.Single().DueAt.ShouldBe(TimeSpan.FromDays(7));

        _host.Clock.Advance(TimeSpan.FromDays(6));
        instance.IsCompleted.ShouldBeFalse("the timer is not due yet");
        ElementIds(instance.PendingWork).ShouldBe(["Long", "Timeout"]);

        _host.Clock.Advance(TimeSpan.FromDays(1));

        instance.CancelledWork.Select(cancelled => cancelled.ElementId).ShouldBe(["Long"]);
        ElementIds(instance.PendingWork).ShouldBe(["Escalate"], instance.Transcript.ToString());

        instance.CompleteWork("escalate");

        instance.IsCompleted.ShouldBeTrue();
        VisitedElements(instance).ShouldContain("EndTimedOut");
        VisitedElements(instance).ShouldNotContain("EndNormal");
    }

    [Fact]
    public void An_interrupting_boundary_that_never_fires_is_retired_when_its_host_completes()
    {
        var instance = Start(ProcessFixtures.TimerBoundary("P7D"));

        instance.CompleteWork("long");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.CancelledWork.Select(cancelled => cancelled.ElementId).ShouldBe(["Timeout"]);
        _host.Clock.Pending.ShouldBeEmpty("a retired timer must leave the schedule");
    }

    [Fact]
    public void A_non_interrupting_boundary_fires_alongside_its_activity_which_keeps_running()
    {
        var instance = Start(ProcessFixtures.NonInterruptingMessageBoundary());

        var nudge = instance.ResolveWork("nudge");
        instance.CompleteWork(nudge);

        instance.CancelledWork.ShouldBeEmpty("a non-interrupting boundary tears nothing down");
        ElementIds(instance.PendingWork).ShouldContain("Long", customMessage: instance.Transcript.ToString());
        ElementIds(instance.PendingWork).ShouldContain("Notify");
        instance.IsCompleted.ShouldBeFalse();

        instance.CompleteWork("notify");
        instance.CompleteWork("long");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    // --- Multi-instance --------------------------------------------------------------------------------

    [Fact]
    public void A_parallel_multi_instance_activity_starts_one_unit_of_work_per_iteration()
    {
        var instance = Start(ProcessFixtures.MultiInstanceByCardinality(3));

        instance.PendingWork.Count.ShouldBe(3);
        instance.PendingWork.Select(work => work.ElementId).ShouldAllBe(elementId => elementId == "Review");
        instance.PendingWork.Select(work => work.IterationId).Distinct().Count().ShouldBe(3);
        instance.PendingWork.Select(LoopIndex).ShouldBe([0, 1, 2]);

        foreach (var work in instance.PendingWork)
            instance.CompleteWork(work);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void A_sequential_multi_instance_activity_runs_one_iteration_at_a_time()
    {
        var instance = Start(ProcessFixtures.MultiInstanceByCardinality(3, sequential: true));

        var seen = new List<int>();
        while (!instance.IsCompleted)
        {
            instance.PendingWork.Count.ShouldBe(1, "a sequential loop never runs two instances at once");
            seen.Add(LoopIndex(instance.PendingWork[0]));
            instance.CompleteWork(instance.PendingWork[0]);
        }

        seen.ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void A_collection_multi_instance_activity_seeds_each_iteration_with_its_item()
    {
        var variables = new InMemoryVariables().SetCollection("reviewers", new[] { "ada", "grace", "edsger" });

        var instance = Start(ProcessFixtures.MultiInstanceByCollection(), variables);

        instance.PendingWork.Count.ShouldBe(3);
        instance.PendingWork
            .Select(work => work.IterationValues["reviewer"].Json!.Value.GetString())
            .ShouldBe(["ada", "grace", "edsger"]);
    }

    [Fact]
    public void A_collection_the_host_holds_outside_the_payload_faults_rather_than_iterating_zero_times()
    {
        var variables = new InMemoryVariables().SetStoredExternally("reviewers");

        var instance = Start(ProcessFixtures.MultiInstanceByCollection(), variables);

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Fault!.Code.ShouldBe(BpmnInterpreter.CollectionNotInlineFaultCode);
    }

    // --- Failure ---------------------------------------------------------------------------------------

    [Fact]
    public void An_error_boundary_catches_a_failure_and_the_disposition_says_so()
    {
        var instance = Start(ProcessFixtures.RiskyActivity(withErrorBoundary: true));

        instance.FaultWork("risky", "the credit limit was exceeded");

        var caught = instance.Transcript.Last().Disposition.ShouldBeOfType<BpmnErrorDisposition.Caught>();
        caught.CatchingElementId.ShouldBe("Failed");

        instance.IsFaulted.ShouldBeFalse("a caught error is not a process failure");
        ElementIds(instance.PendingWork).ShouldBe(["Recover"]);

        instance.CompleteWork("recover");
        instance.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void An_uncaught_failure_propagates_and_faults_the_process()
    {
        var instance = Start(ProcessFixtures.RiskyActivity(withErrorBoundary: false));

        instance.FaultWork("risky", "the credit limit was exceeded");

        instance.Transcript.Last().Disposition.ShouldBeOfType<BpmnErrorDisposition.Propagated>();
        instance.IsFaulted.ShouldBeTrue();
        instance.Fault!.Message.ShouldContain("the credit limit was exceeded");
        instance.PendingWork.ShouldBeEmpty();
    }

    // --- Nesting, subtree cancellation and scope signalling ---------------------------------------------

    [Fact]
    public void A_subprocess_runs_as_a_nested_scope_whose_completion_finishes_the_parent_activity()
    {
        var (parent, body) = ProcessFixtures.NestedSubProcess();
        var host = HostWithNested(("sub", body));

        var instance = host.Start(parent);
        var child = instance.Children.ShouldHaveSingleItem();

        child.Parent.ShouldBeSameAs(instance);
        ElementIds(child.PendingWork).ShouldBe(["Inner"]);
        instance.PendingWork.ShouldHaveSingleItem().Kind.ShouldBe(InMemoryWorkKind.NestedProcess);

        child.CompleteWork("inner");

        child.IsCompleted.ShouldBeTrue();
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void Cancelling_a_subprocess_cancels_everything_that_subprocess_started()
    {
        var (parent, body) = ProcessFixtures.NestedSubProcessWithTimerBoundary("PT1H");
        var host = HostWithNested(("sub", body));

        var instance = host.Start(parent);
        var child = instance.Children.ShouldHaveSingleItem();
        ElementIds(child.PendingWork).ShouldBe(["Inner"]);

        host.Clock.Advance(TimeSpan.FromHours(1));

        instance.CancelledWork.Select(cancelled => cancelled.ElementId).ShouldContain("Sub");
        child.IsCancelled.ShouldBeTrue("the nested scope goes down with the work that started it");
        child.PendingWork.ShouldBeEmpty("and so does everything that scope had started");
        host.PendingWork.ShouldBeEmpty();
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void An_escalation_from_a_nested_scope_is_delivered_to_the_scope_that_invoked_it()
    {
        var (parent, body) = ProcessFixtures.EscalatingSubProcess("overdue");
        var host = HostWithNested(("sub", body));

        var instance = host.Start(parent);
        var child = instance.Children.ShouldHaveSingleItem();

        child.ScopeSignals.ShouldHaveSingleItem().Delivered.ShouldBeTrue();
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        VisitedElements(instance).ShouldContain("EndEscalated");
        child.IsCancelled.ShouldBeTrue("an interrupting escalation boundary tears its host down");
    }

    // --- Capabilities ----------------------------------------------------------------------------------

    [Fact]
    public void A_definition_that_needs_a_withheld_capability_is_refused_before_anything_runs()
    {
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            Capabilities = BpmnHostCapabilities.Full & ~BpmnHostCapabilities.IterationScopes
        });

        var refusal = Should.Throw<BpmnCapabilityException>(() => host.Start(ProcessFixtures.MultiInstanceByCardinality(3)));

        refusal.Missing.ShouldBe(BpmnHostCapabilities.IterationScopes);
        refusal.DrivingElementIds.ShouldBe(["Review"]);
        host.Instances.ShouldBeEmpty("nothing may start when the graph is refused");
    }

    [Fact]
    public void A_host_without_subtree_cancellation_is_refused_an_interrupting_boundary_event()
    {
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            Capabilities = BpmnHostCapabilities.None
        });

        var refusal = Should.Throw<BpmnCapabilityException>(() => host.Start(ProcessFixtures.TimerBoundary()));

        refusal.Missing.ShouldBe(BpmnHostCapabilities.SubtreeCancellation);
    }

    [Fact]
    public void A_definition_needing_nothing_special_runs_on_a_host_that_declares_nothing()
    {
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions { Capabilities = BpmnHostCapabilities.None });

        var instance = host.Start(ProcessFixtures.Linear());
        instance.CompleteWork("work");

        instance.IsCompleted.ShouldBeTrue();
    }

    // --- Resolving work --------------------------------------------------------------------------------

    [Fact]
    public void Work_can_be_named_by_handle_or_by_binding_ref()
    {
        var instance = Start(ProcessFixtures.Linear());
        var work = instance.PendingWork.ShouldHaveSingleItem();

        instance.ResolveWork(work.Handle).ShouldBe(work);
        instance.ResolveWork("work").ShouldBe(work);
        _host.FindWork(work.Handle).ShouldBe(work);
    }

    [Fact]
    public void A_binding_ref_with_several_concurrent_instances_refuses_to_guess()
    {
        var instance = Start(ProcessFixtures.MultiInstanceByCardinality(3));

        Should.Throw<InvalidOperationException>(() => instance.CompleteWork("review"))
            .Message.ShouldContain("3 concurrent live instances");
    }

    [Fact]
    public void Naming_work_that_is_not_running_reports_what_is()
    {
        var instance = Start(ProcessFixtures.Linear());

        Should.Throw<InvalidOperationException>(() => instance.CompleteWork("nope"))
            .Message.ShouldContain("work (work)");
    }

    // --- Helpers ---------------------------------------------------------------------------------------

    private static InMemoryBpmnHost HostWithNested(params (string BindingRef, BpmnProcessDefinition Definition)[] nested) =>
        new(new InMemoryBpmnHostOptions
        {
            NestedProcesses = nested.ToDictionary(entry => entry.BindingRef, entry => entry.Definition, StringComparer.Ordinal)
        });

    private static int LoopIndex(InMemoryWorkItem work) =>
        work.IterationValues[BpmnLoopCharacteristics.LoopIndexVariable].Json!.Value.GetInt32();

    private static IReadOnlyList<string> VisitedElements(InMemoryProcessInstance instance) =>
        instance.Transcript
            .SelectMany(entry => entry.TokenMoves.Where(move => move.From is null).Select(move => move.ElementId))
            .ToArray();
}
