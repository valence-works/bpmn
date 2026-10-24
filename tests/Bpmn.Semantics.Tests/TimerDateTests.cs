using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// A timer at a date fires once. A timer start at a date resolves to a <see cref="BpmnTimerStartScheduleKind.Date"/>
/// schedule, and a timer event subprocess at a date is accepted but, unlike a duration one, is not re-armed after a
/// non-interrupting fire: its instant has passed, so a fresh listener would fire again at once, without end.
/// </summary>
public sealed class TimerDateTests
{
    private const string Instant = "2026-12-24T18:00:00Z";

    [Fact]
    public void A_timer_start_at_a_date_resolves_to_a_date_schedule()
    {
        var start = TimerStart(Properties((BpmnEventDefinitionProperties.Date, $" {Instant}\t")));

        var schedule = BpmnTimerStartSchedule.Resolve(start);

        schedule.Kind.ShouldBe(BpmnTimerStartScheduleKind.Date);
        schedule.Expression.ShouldBe(Instant);
        BuildGraph(start).StartEvents.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(BpmnEventDefinitionProperties.Interval, "PT5M")]
    [InlineData(BpmnEventDefinitionProperties.Cron, "0 * * * *")]
    public void A_timer_start_with_a_date_and_a_recurring_schedule_is_ambiguous(string key, string value)
    {
        var start = TimerStart(Properties((BpmnEventDefinitionProperties.Date, Instant), (key, value)));

        var graphError = Should.Throw<BpmnExecutionException>(() => BuildGraph(start));
        var resolverError = Should.Throw<BpmnExecutionException>(() => BpmnTimerStartSchedule.Resolve(start));

        graphError.Message.ShouldContain("'TimerStart' must declare exactly one of 'interval', 'cron' or 'date'");
        resolverError.Message.ShouldBe(graphError.Message);
    }

    [Theory]
    [InlineData(BpmnEventDefinitionProperties.Interval, "PT1M", false)]
    [InlineData(BpmnEventDefinitionProperties.Date, Instant, true)]
    public void A_timer_event_subprocess_at_a_date_is_accepted_and_fires_once(string key, string value, bool firesOnce)
    {
        var graph = EventSubprocessGraph(Properties((key, value)));

        var catcher = graph.ExternalTriggerEventSubprocesses.ShouldHaveSingleItem();
        catcher.TriggerKind.ShouldBe(BpmnEventSubprocessTriggerKind.Timer);
        catcher.FiresOnce.ShouldBe(firesOnce);
    }

    [Fact]
    public void A_timer_event_subprocess_with_neither_a_duration_nor_a_date_is_refused()
    {
        var error = Should.Throw<BpmnExecutionException>(() => EventSubprocessGraph(Properties((BpmnEventDefinitionProperties.Date, "  "))));

        error.Message.ShouldContain("declares no interval or date");
        error.ElementIds.ShouldBe(["TimerHandler", "BodyStart"]);
    }

    [Theory]
    [InlineData(BpmnEventDefinitionProperties.Interval, "PT1M", new[] { "timer-handler", "timer-listener" })]
    [InlineData(BpmnEventDefinitionProperties.Date, Instant, new[] { "timer-handler" })]
    public void A_non_interrupting_timer_event_subprocess_re_arms_after_a_duration_but_not_after_a_date(string key, string value, string[] startedAfterFire)
    {
        var graph = EventSubprocessGraph(Properties((key, value)));
        var interpreter = BpmnInterpreter.CreateDefault();
        var started = interpreter.Start(new BpmnStartRequest(graph, null, Host()));
        var starts = started.Commands.OfType<BpmnHostCommand.StartWork>().ToDictionary(command => command.BindingRef, StringComparer.Ordinal);
        var listener = starts["timer-listener"];

        var fired = interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            graph, started.State, Host(Live(starts["long-running"], "handle:long")), listener.BindingRef, "handle:listener", [])
        {
            WorkTokenId = listener.TokenId
        });

        fired.Commands.OfType<BpmnHostCommand.StartWork>().Select(command => command.BindingRef)
            .OrderBy(binding => binding, StringComparer.Ordinal)
            .ShouldBe(startedAfterFire);
        fired.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldBeEmpty("a non-interrupting fire leaves the scope's work running");
    }

    private static BpmnGraph EventSubprocessGraph(IReadOnlyDictionary<string, string> timerProperties)
    {
        var body = new BpmnProcessBuilder("timer-event-subprocess-body")
            .Element(new BpmnElement("BodyStart", BpmnElementTypes.StartEvent, cancelActivity: false,
                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, timerProperties)]))
            .Task("BodyWork", bindingRef: "body-work")
            .EndEvent("BodyEnd")
            .ConnectSequence("BodyStart", "BodyWork", "BodyEnd")
            .Build();
        var parent = new BpmnProcessBuilder("timer-event-subprocess-parent")
            .StartEvent("Start")
            .Task("LongRunning", bindingRef: "long-running")
            .EndEvent("End")
            .Element(new BpmnElement("TimerHandler", BpmnElementTypes.SubProcess,
                bindingRef: "timer-handler", triggeredByEvent: true, listenerBindingRef: "timer-listener"))
            .ConnectSequence("Start", "LongRunning", "End")
            .Build();

        return BpmnGraph.Build(parent, [new("long-running"), new("timer-handler", body), new("timer-listener")], BpmnHostCapabilities.Full);
    }

    private static BpmnGraph BuildGraph(BpmnElement start)
    {
        var definition = new BpmnProcessBuilder("timer-date-start")
            .Element(start)
            .EndEvent("End")
            .Connect(start.ElementId, "End")
            .Build();

        return BpmnGraph.Build(definition, [], BpmnHostCapabilities.Full);
    }

    private static BpmnElement TimerStart(IReadOnlyDictionary<string, string> properties) =>
        new("TimerStart", BpmnElementTypes.StartEvent, eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, properties)]);

    private static Dictionary<string, string> Properties(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static BpmnHostSnapshot Host(params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root("scope-1") with { LiveWork = liveWork };

    private static BpmnLiveWork Live(BpmnHostCommand.StartWork start, string handle) =>
        new(start.BindingRef, start.IterationScope?.IterationId, handle) { WorkTokenId = start.TokenId };
}
