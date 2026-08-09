using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The virtual clock, which is the reason a simulation is cheap. Nothing here waits, and nothing here reads a
/// real clock — so a seven-day timer costs the same as a one-second one, and the ordering is the same on every
/// run.
/// </summary>
public sealed class VirtualClockTests
{
    private readonly InMemoryBpmnHost _host = new();

    [Fact]
    public void A_clock_starts_at_zero_and_only_moves_when_asked()
    {
        _host.Clock.Now.ShouldBe(TimeSpan.Zero);

        _host.Start(ProcessFixtures.TimerBoundary("P7D"));

        _host.Clock.Now.ShouldBe(TimeSpan.Zero, "starting a process does not make time pass");
        _host.Clock.Pending.ShouldHaveSingleItem().DueAt.ShouldBe(TimeSpan.FromDays(7));
    }

    [Fact]
    public void A_seven_day_timer_fires_in_a_single_call()
    {
        var instance = _host.Start(ProcessFixtures.TimerBoundary("P7D"));

        _host.Clock.Advance(TimeSpan.FromDays(7));

        _host.Clock.Now.ShouldBe(TimeSpan.FromDays(7));
        instance.Transcript.Any(entry => entry.Trigger is BpmnTranscriptTrigger.TimerFired).ShouldBeTrue();
    }

    [Fact]
    public void Advancing_to_the_next_timer_jumps_exactly_to_it()
    {
        _host.Start(ProcessFixtures.TimerBoundary("P30D"));

        _host.Clock.AdvanceToNextTimer().ShouldBeTrue();

        _host.Clock.Now.ShouldBe(TimeSpan.FromDays(30));
        _host.Clock.AdvanceToNextTimer().ShouldBeFalse("nothing is scheduled any more");
        _host.Clock.Now.ShouldBe(TimeSpan.FromDays(30), "a refused advance leaves the clock alone");
    }

    [Fact]
    public void Timers_fire_in_due_order_regardless_of_the_order_they_were_scheduled_in()
    {
        var instance = _host.Start(TwoTimers());

        _host.Clock.Pending.Select(timer => timer.ElementId).ShouldBe(["Fast", "Slow"]);

        _host.Clock.Advance(TimeSpan.FromDays(365));

        var fired = instance.Transcript
            .Select(entry => entry.Trigger)
            .OfType<BpmnTranscriptTrigger.TimerFired>()
            .Select(trigger => trigger.Work.ElementId)
            .ToArray();

        fired.ShouldBe(["Fast", "Slow"]);
    }

    [Fact]
    public void The_clock_refuses_to_run_backwards()
    {
        _host.Clock.Advance(TimeSpan.FromHours(2));

        Should.Throw<ArgumentOutOfRangeException>(() => _host.Clock.Advance(TimeSpan.FromHours(-1)));
        Should.Throw<ArgumentOutOfRangeException>(() => _host.Clock.AdvanceTo(TimeSpan.FromHours(1)));
        _host.Clock.Now.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public void A_duration_supplied_by_the_host_beats_the_one_in_the_model()
    {
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            TimerDurations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal) { ["timeout"] = TimeSpan.FromMinutes(5) }
        });

        host.Start(ProcessFixtures.TimerBoundary("P7D"));

        host.Clock.Pending.ShouldHaveSingleItem().DueAt.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void The_same_scenario_produces_the_same_transcript_every_time()
    {
        static string RunOnce()
        {
            var host = new InMemoryBpmnHost();
            var instance = host.Start(ProcessFixtures.TimerBoundary("P7D"));
            host.Clock.Advance(TimeSpan.FromDays(7));
            instance.CompleteWork("escalate");
            return instance.Transcript.ToString();
        }

        RunOnce().ShouldBe(RunOnce());
    }

    /// <summary>Two intermediate timer catch events on parallel branches, scheduled slow-first, due fast-first.</summary>
    private static BpmnProcessDefinition TwoTimers() =>
        new BpmnProcessBuilder("two-timers")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .IntermediateCatchEvent("Slow", ProcessFixtures.Timer("P7D"), bindingRef: "slow")
            .IntermediateCatchEvent("Fast", ProcessFixtures.Timer("PT1H"), bindingRef: "fast")
            .ParallelGateway("Join")
            .EndEvent("End")
            .Connect("Start", "Split")
            .Connect("Split", "Slow")
            .Connect("Split", "Fast")
            .Connect("Slow", "Join")
            .Connect("Fast", "Join")
            .Connect("Join", "End")
            .Build();
}
