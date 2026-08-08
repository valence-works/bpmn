using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The analysis helpers: driving a definition to completion, and searching its decision tree for a path that
/// strands a token. Both are questions about a model rather than about code, which is why they ship rather than
/// living here.
/// </summary>
public sealed class BpmnSimulationTests
{
    [Fact]
    public void A_linear_process_completes_and_reports_the_path_it_took()
    {
        var result = BpmnSimulation.CanComplete(ProcessFixtures.Linear());

        result.Completed.ShouldBeTrue(result.ToString());
        result.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName);
        result.Path.ShouldBe(["Start", "Work", "End"]);
        result.Stop.ShouldBe(BpmnSimulationStop.Completed);
    }

    [Fact]
    public void A_process_with_timers_completes_by_advancing_the_clock_rather_than_by_waiting()
    {
        var result = BpmnSimulation.CanComplete(ProcessFixtures.TimerBoundary("P30D"));

        result.Completed.ShouldBeTrue(result.ToString());
        result.Path.ShouldContain("EndNormal", customMessage: "the activity is driven before the boundary timer is reached");
        result.ElapsedVirtualTime.ShouldBe(TimeSpan.Zero, "nothing needed the clock");
    }

    [Fact]
    public void An_outcome_strategy_chooses_which_branch_the_run_takes()
    {
        var approved = BpmnSimulation.CanComplete(ProcessFixtures.ExclusiveChoice(), strategy: BpmnOutcomeStrategy.ByName("Approved"));
        var rejected = BpmnSimulation.CanComplete(ProcessFixtures.ExclusiveChoice(), strategy: BpmnOutcomeStrategy.ByName("Rejected"));

        approved.Path.ShouldContain("EndApproved");
        approved.Path.ShouldNotContain("EndRejected");
        rejected.Path.ShouldContain("EndRejected");
        rejected.Path.ShouldNotContain("EndApproved");
    }

    [Fact]
    public void The_first_and_last_strategies_take_opposite_branches()
    {
        BpmnSimulation.CanComplete(ProcessFixtures.ExclusiveChoice(), strategy: BpmnOutcomeStrategy.AlwaysFirst)
            .Path.ShouldContain("EndApproved");

        BpmnSimulation.CanComplete(ProcessFixtures.ExclusiveChoice(), strategy: BpmnOutcomeStrategy.AlwaysLast)
            .Path.ShouldContain("EndRejected");
    }

    [Fact]
    public void A_strategy_that_declines_stops_the_run_rather_than_guessing()
    {
        var result = BpmnSimulation.CanComplete(
            ProcessFixtures.ExclusiveChoice(),
            strategy: BpmnOutcomeStrategy.FromDelegate(_ => null));

        result.Completed.ShouldBeFalse();
        result.Stop.ShouldBe(BpmnSimulationStop.Undecided);
        result.StopDetail.ShouldContain("Decide");
    }

    [Fact]
    public void A_parallel_join_downstream_of_an_exclusive_decision_deadlocks_on_every_branch()
    {
        var result = BpmnSimulation.CanComplete(ProcessFixtures.JoinThatCanStarve());

        result.Completed.ShouldBeFalse(result.ToString());
        result.IsDeadlocked.ShouldBeTrue(result.ToString());

        var deadlocks = BpmnSimulation.FindDeadlocks(ProcessFixtures.JoinThatCanStarve());

        deadlocks.Count.ShouldBe(2, "one for each branch the decision can take");
        deadlocks.Select(deadlock => deadlock.ChoicePath.Single()).ShouldBe(["Decide=Left", "Decide=Right"], ignoreOrder: true);
        deadlocks.ShouldAllBe(deadlock => deadlock.StuckElementIds.Contains("Join"));
    }

    [Fact]
    public void A_sound_process_has_no_deadlocks_on_any_branch()
    {
        BpmnSimulation.FindDeadlocks(ProcessFixtures.ExclusiveChoice()).ShouldBeEmpty();
        BpmnSimulation.FindDeadlocks(ProcessFixtures.ParallelSplitAndJoin()).ShouldBeEmpty();
        BpmnSimulation.FindDeadlocks(ProcessFixtures.MultiInstanceByCardinality(3)).ShouldBeEmpty();
    }

    [Fact]
    public void A_deadlocking_path_can_be_replayed_exactly_from_what_the_search_reported()
    {
        var found = BpmnSimulation.FindDeadlocks(ProcessFixtures.JoinThatCanStarve())
            .First(deadlock => deadlock.ChoicePath.Single() == "Decide=Right");

        var replay = BpmnSimulation.CanComplete(
            ProcessFixtures.JoinThatCanStarve(),
            strategy: BpmnOutcomeStrategy.ByIndex(found.ChoiceIndices));

        replay.IsDeadlocked.ShouldBeTrue();
        replay.ChoicePath.ShouldBe(found.ChoicePath);
    }

    [Fact]
    public void The_search_is_deterministic()
    {
        var first = BpmnSimulation.FindDeadlocks(ProcessFixtures.JoinThatCanStarve());
        var second = BpmnSimulation.FindDeadlocks(ProcessFixtures.JoinThatCanStarve());

        first.Select(deadlock => deadlock.ToString()).ShouldBe(second.Select(deadlock => deadlock.ToString()));
    }

    [Fact]
    public void A_run_that_never_settles_stops_at_the_step_ceiling_instead_of_hanging()
    {
        var result = BpmnSimulation.CanComplete(NeverEnding(), options: new BpmnSimulationOptions { MaxSteps = 20 });

        result.Completed.ShouldBeFalse();
        result.Stop.ShouldBe(BpmnSimulationStop.StepLimit);
        result.Steps.ShouldBe(20);
    }

    [Fact]
    public void A_faulting_process_is_reported_as_faulted_rather_than_as_a_deadlock()
    {
        var variables = new InMemoryVariables().SetStoredExternally("reviewers");

        var result = BpmnSimulation.CanComplete(
            ProcessFixtures.MultiInstanceByCollection(),
            options: new BpmnSimulationOptions { Variables = variables });

        result.Stop.ShouldBe(BpmnSimulationStop.Faulted);
        result.IsDeadlocked.ShouldBeFalse();
        result.StopDetail.ShouldContain(BpmnInterpreter.CollectionNotInlineFaultCode);
    }

    /// <summary>A task that loops back to itself forever, so a run can only stop at its own ceiling.</summary>
    private static BpmnProcessDefinition NeverEnding() =>
        new BpmnProcessBuilder("never-ending")
            .StartEvent("Start")
            .Task("Spin", bindingRef: "spin")
            .ConnectSequence("Start", "Spin")
            .Connect("Spin", "Spin")
            .Build();
}
