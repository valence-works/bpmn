using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// A cancel end event abandoning a transaction, watched from the host's side of the port.
/// <para>
/// The interesting case is cancelling while a compensation replay is in flight. Stopping the in-flight run
/// releases its unrun compensable, and the cancel's own run re-claims it and restarts the same handler — the
/// same element, so the same binding ref and the same inherited iteration key. Unless the abandoned unit is
/// torn down, the host is left holding two live units for one slot, which is the one thing
/// <see cref="BpmnHostSnapshot"/> asks it never to do and the one thing it cannot prevent by itself: its
/// ledger has exactly two inputs, and both of them are interpreter commands.
/// </para>
/// </summary>
public sealed class TransactionCancellationTests
{
    private static (InMemoryProcessInstance Parent, InMemoryProcessInstance Transaction) StartTransaction(
        (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) fixture)
    {
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["booking"] = fixture.Body }
        });

        var parent = host.Start(fixture.Parent);
        return (parent, parent.Children.ShouldHaveSingleItem());
    }

    /// <summary>Drives the transaction to the moment of cancellation and reports the handle of the compensation handler left mid-replay.</summary>
    private static string CancelMidReplay(InMemoryProcessInstance transaction)
    {
        transaction.CompleteWork("reserve");
        var replaying = transaction.ResolveWork("undo-reserve");

        transaction.CompleteWork("trigger");
        return replaying.Handle;
    }

    private static IReadOnlyList<string> BindingRefs(InMemoryProcessInstance instance) =>
        instance.PendingWork.Select(work => work.BindingRef).ToArray();

    // --- The slot the cancel re-uses -------------------------------------------------------------------

    [Fact]
    public void Cancelling_mid_replay_tears_the_abandoned_handler_down_before_restarting_its_slot()
    {
        var (_, transaction) = StartTransaction(ProcessFixtures.CancellableTransaction());

        var abandoned = CancelMidReplay(transaction);

        BindingRefs(transaction).ShouldBe(["undo-reserve"], transaction.Transcript.ToString());
        transaction.PendingWork.Single().Handle.ShouldNotBe(abandoned, "the cancel's run restarts the handler under a fresh handle");

        var teardown = transaction.CancelledWork.ShouldHaveSingleItem(transaction.Transcript.ToString());
        teardown.Handle.ShouldBe(abandoned, "the teardown must name the unit the host is still holding, not the replacement");
        teardown.ElementId.ShouldBe("UndoReserve");
        teardown.Reason.ShouldBe(BpmnInterpreter.TransactionCancelledStopReason);
    }

    [Fact]
    public void Cancelling_tears_down_a_third_branch_that_is_still_genuinely_in_flight()
    {
        var (_, transaction) = StartTransaction(ProcessFixtures.CancellableTransaction(withThirdBranch: true));

        var audit = transaction.ResolveWork("audit").Handle;
        var abandoned = CancelMidReplay(transaction);

        transaction.CancelledWork.Select(cancelled => cancelled.Handle)
            .ShouldBe([abandoned, audit], ignoreOrder: true, customMessage: transaction.Transcript.ToString());
        BindingRefs(transaction).ShouldBe(["undo-reserve"], "the abandoned transaction leaves nothing running but its own replay");
    }

    [Fact]
    public void Cancelling_with_nothing_else_live_tears_nothing_down()
    {
        var (_, transaction) = StartTransaction(ProcessFixtures.CancellableTransactionWithNothingElseLive());

        transaction.CompleteWork("reserve");
        transaction.CompleteWork("trigger");

        transaction.CancelledWork.ShouldBeEmpty(transaction.Transcript.ToString());
        BindingRefs(transaction).ShouldBe(["undo-reserve"]);
    }

    // --- The path the teardowns run on -----------------------------------------------------------------

    [Fact]
    public void A_cancelled_transaction_finishes_its_replay_and_routes_the_parent_cancel_boundary()
    {
        var (parent, transaction) = StartTransaction(ProcessFixtures.CancellableTransaction());
        CancelMidReplay(transaction);

        transaction.CompleteWork("undo-reserve");

        transaction.Outcome.ShouldBe(BpmnInterpreter.CancelledOutcomeName, transaction.Transcript.ToString());
        parent.PendingWork.Select(work => work.ElementId).ShouldBe(["Recover"], parent.Transcript.ToString());

        parent.CompleteWork("recover");
        parent.IsCompleted.ShouldBeTrue(parent.Transcript.ToString());
    }

    // --- Capabilities ----------------------------------------------------------------------------------

    [Fact]
    public void A_cancel_end_event_declares_the_subtree_cancellation_it_now_uses()
    {
        var (_, body) = ProcessFixtures.CancellableTransaction();

        var requirements = BpmnCapabilityRequirements.Analyze(body);

        requirements.MissingFrom(BpmnHostCapabilities.None).ShouldBe(BpmnHostCapabilities.SubtreeCancellation);
        requirements.DrivingElementIds[BpmnHostCapabilities.SubtreeCancellation].ShouldBe(["CancelEnd"]);
    }
}
