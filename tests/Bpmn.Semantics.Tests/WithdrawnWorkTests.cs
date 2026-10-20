using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Work started and then stopped in the same evaluation, by a stop that leaves the scope running, watched from
/// the host's side of the port.
/// <para>
/// An interrupting own-scope escalation or a cancel end event can be reached in the same propagation that just
/// issued a sibling branch's <see cref="BpmnHostCommand.StartWork"/>. Stopping that sibling's token used to leave
/// the command on the list, so the host started work no token waited for. Nothing could tear it down, because it
/// was not in the snapshot yet, and its completion resolved to nothing and threw. A terminate or a fault already
/// withdrew such work; a stop short of ending the scope did not.
/// </para>
/// <para>
/// The tests read the deciding evaluation's commands rather than what the host ended up running: the reference
/// host tears down whatever a finishing scope still runs, which would hide a stray start in the completing case.
/// </para>
/// </summary>
public sealed class WithdrawnWorkTests
{
    private static readonly BpmnContinuation Defer = BpmnContinuation.Defer.Instance;

    private static InMemoryProcessInstance Start((BpmnProcessDefinition Parent, BpmnProcessDefinition Body) fixture, string bodyBindingRef) =>
        new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { [bodyBindingRef] = fixture.Body }
        }).Start(fixture.Parent);

    private static string[] Describe(IEnumerable<BpmnHostCommand> commands) =>
        commands.Select(command => command switch
        {
            BpmnHostCommand.StartWork start => $"start {start.BindingRef}",
            BpmnHostCommand.CancelWorkSubtree cancel => $"cancel {cancel.Handle} {cancel.ElementId} {cancel.Reason}",
            _ => command.ToString()
        }).ToArray();

    /// <summary>The property the fix restores: every unit the host runs is one a token in the scope is waiting on.</summary>
    private static void ShouldRunOnlyWorkATokenWaitsFor(InMemoryProcessInstance instance)
    {
        foreach (var work in instance.PendingWork)
        {
            var tokenId = work.Correlation[BpmnInterpreter.TokenIdCorrelationKey];
            instance.State.ActiveWork.ShouldContain(record => record.TokenId == tokenId && record.NodeId == work.BindingRef,
                $"the host runs '{work.BindingRef}' for token '{tokenId}', but no work record waits for it");
            instance.State.Tokens.Single(token => token.TokenId == tokenId).Status.ShouldBe(BpmnTokenStatus.AwaitingChild);
        }
    }

    // --- An interrupting own-scope escalation ------------------------------------------------------------

    [Fact]
    public void An_interrupting_own_scope_escalation_never_starts_the_sibling_work_it_stops_in_the_same_evaluation()
    {
        var instance = Start(ProcessFixtures.SplitIntoWorkAndOwnScopeEscalation(), "on-overdue");

        var evaluation = instance.Transcript.ShouldHaveSingleItem();
        evaluation.Continuation.ShouldBe(Defer, instance.Transcript.ToString());
        Describe(evaluation.Commands).ShouldBe(["start on-overdue"],
            "the long-running task was stopped before the host saw it, so only the event subprocess body is started");

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["on-overdue"]);
        instance.CancelledWork.ShouldBeEmpty("nothing was started for the stopped branch, so nothing is torn down for it");
        evaluation.TokenMoves.ShouldContain(move => move.ElementId == "LongRunning" && move.To == BpmnTokenStatus.Canceled);
        instance.State.Tokens.ShouldNotContain(token => token.AtElementId == "LongRunning");
        ShouldRunOnlyWorkATokenWaitsFor(instance);
    }

    [Fact]
    public void The_event_subprocess_that_interrupted_the_scope_still_runs_to_completion()
    {
        var instance = Start(ProcessFixtures.SplitIntoWorkAndOwnScopeEscalation(), "on-overdue");

        var handler = instance.Children.ShouldHaveSingleItem();
        handler.PendingWork.Select(work => work.BindingRef).ShouldBe(["handle"]);
        handler.CompleteWork("handle");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName);
        instance.CancelledWork.ShouldBeEmpty("the scope completed with nothing left running for the host to clean up");
    }

    [Fact]
    public void A_non_interrupting_own_scope_escalation_still_starts_the_sibling_work()
    {
        var instance = Start(ProcessFixtures.SplitIntoWorkAndOwnScopeEscalation(interrupting: false), "on-overdue");

        Describe(instance.Transcript.ShouldHaveSingleItem().Commands).ShouldBe(["start long-running", "start on-overdue"],
            "a non-interrupting catcher stops nothing, so the sibling branch keeps its work");
        ShouldRunOnlyWorkATokenWaitsFor(instance);

        instance.Children.ShouldHaveSingleItem().CompleteWork("handle");
        instance.IsCompleted.ShouldBeFalse("the long-running branch is still working");

        instance.CompleteWork("long-running");
        instance.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName, instance.Transcript.ToString());
    }

    // --- A cancel end event inside a transaction ---------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cancel_end_event_never_starts_the_sibling_work_it_stops_in_the_same_evaluation(bool compensateFirst)
    {
        var fixture = ProcessFixtures.TransactionCancelledBesideWorkItJustStarted(compensateFirst);
        var parent = Start(fixture, "booking");
        var transaction = parent.Children.ShouldHaveSingleItem();

        if (compensateFirst)
            transaction.CompleteWork("reserve");

        var evaluation = transaction.Transcript[^1];
        if (compensateFirst)
        {
            evaluation.Continuation.ShouldBe(Defer, transaction.Transcript.ToString());
            Describe(evaluation.Commands).ShouldBe(["start undo-reserve"], "the audit is withdrawn and only the replay starts");
        }
        else
        {
            evaluation.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.CancelledOutcomeName), transaction.Transcript.ToString());
            evaluation.Commands.ShouldBeEmpty("a completion carries no StartWork, so the audit is withdrawn");
        }

        transaction.CancelledWork.ShouldBeEmpty("the audit was never started, so nothing is torn down for it");
        ShouldRunOnlyWorkATokenWaitsFor(transaction);

        if (compensateFirst)
            transaction.CompleteWork("undo-reserve");

        transaction.Outcome.ShouldBe(BpmnInterpreter.CancelledOutcomeName, transaction.Transcript.ToString());
        parent.PendingWork.Select(work => work.ElementId).ShouldBe(["Recover"], parent.Transcript.ToString());
    }

    // --- The teardowns a withdrawal must not add to ------------------------------------------------------

    [Fact]
    public void Work_restarted_on_a_slot_that_is_being_torn_down_is_withdrawn_without_tearing_the_old_unit_down_twice()
    {
        var instance = Start(ProcessFixtures.InterruptedTaskReenteredAndEscalated(), "on-overdue");
        var interrupted = instance.ResolveWork("work").Handle;

        instance.CompleteWork("interrupt");

        var evaluation = instance.Transcript[^1];
        evaluation.Continuation.ShouldBe(Defer, instance.Transcript.ToString());
        Describe(evaluation.Commands).ShouldBe(
        [
            "start on-overdue",
            $"cancel {interrupted} Work {BpmnInterpreter.BoundaryHostInterruptedReason}"
        ], "the re-entered task and its listener are withdrawn, and the unit the boundary interrupted is torn down once, for the boundary");

        instance.CancelledWork.ShouldHaveSingleItem().Handle.ShouldBe(interrupted);
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["on-overdue"]);
        ShouldRunOnlyWorkATokenWaitsFor(instance);
    }
}
