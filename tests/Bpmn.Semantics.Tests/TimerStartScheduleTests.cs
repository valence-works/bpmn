using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class TimerStartScheduleTests
{
    [Theory]
    [InlineData("PT5M", "0 * * * *")]
    [InlineData(null, null)]
    [InlineData(" \t ", null)]
    [InlineData(null, "\r\n\t")]
    [InlineData("  ", "\t")]
    public void Graph_and_resolver_reject_the_same_invalid_timer_start(string? interval, string? cron)
    {
        var properties = Properties();
        if (interval is not null)
            properties[BpmnEventDefinitionProperties.Interval] = interval;
        if (cron is not null)
            properties[BpmnEventDefinitionProperties.Cron] = cron;
        var element = TimerStart(properties);

        var graphError = Should.Throw<BpmnExecutionException>(() => BuildGraph(element));
        var resolverError = Should.Throw<BpmnExecutionException>(() => BpmnTimerStartSchedule.Resolve(element));

        graphError.Message.ShouldContain("'TimerStart' must declare exactly one");
        resolverError.Message.ShouldBe(graphError.Message);
    }

    [Fact]
    public void Resolver_trims_an_interval_and_does_not_mutate_the_definition()
    {
        var properties = Properties((BpmnEventDefinitionProperties.Interval, " \tPT5M \r\n"));
        var element = TimerStart(properties);

        var schedule = BpmnTimerStartSchedule.Resolve(element);

        schedule.Kind.ShouldBe(BpmnTimerStartScheduleKind.Interval);
        schedule.Expression.ShouldBe("PT5M");
        properties[BpmnEventDefinitionProperties.Interval].ShouldBe(" \tPT5M \r\n");
        BuildGraph(element).StartEvents.ShouldHaveSingleItem();
    }

    [Fact]
    public void Resolver_collapses_cron_whitespace_and_treats_a_blank_interval_as_absent()
    {
        var element = TimerStart(Properties(
            (BpmnEventDefinitionProperties.Interval, " \t "),
            (BpmnEventDefinitionProperties.Cron, " \t0   *\r\n*  *\t* ")));

        var schedule = BpmnTimerStartSchedule.Resolve(element);

        schedule.Kind.ShouldBe(BpmnTimerStartScheduleKind.Cron);
        schedule.Expression.ShouldBe("0 * * * *");
        BuildGraph(element).StartEvents.ShouldHaveSingleItem();
    }

    [Fact]
    public void Resolver_treats_a_blank_cron_as_absent_when_interval_is_present()
    {
        var element = TimerStart(Properties(
            (BpmnEventDefinitionProperties.Interval, "PT1H"),
            (BpmnEventDefinitionProperties.Cron, "  \r\n")));

        var schedule = BpmnTimerStartSchedule.Resolve(element);

        schedule.Kind.ShouldBe(BpmnTimerStartScheduleKind.Interval);
        schedule.Expression.ShouldBe("PT1H");
        BuildGraph(element).StartEvents.ShouldHaveSingleItem();
    }

    [Fact]
    public void Resolver_requires_a_timer_start_with_one_supported_event_definition()
    {
        var plainStart = new BpmnElement("Plain", BpmnElementTypes.StartEvent);
        var multipleDefinitions = new BpmnElement(
            "Multiple",
            BpmnElementTypes.StartEvent,
            eventDefinitions:
            [
                new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, Properties((BpmnEventDefinitionProperties.Interval, "PT5M"))),
                new BpmnEventDefinition(BpmnEventDefinitionTypes.Message)
            ]);

        Should.Throw<BpmnExecutionException>(() => BpmnTimerStartSchedule.Resolve(plainStart));
        Should.Throw<BpmnExecutionException>(() => BpmnTimerStartSchedule.Resolve(multipleDefinitions));
        Should.Throw<ArgumentNullException>(() => BpmnTimerStartSchedule.Resolve(null!));
    }

    [Fact]
    public void Timer_event_subprocess_keeps_its_one_shot_interval_and_rejects_cron_without_a_cycle()
    {
        var body = EventSubprocessBody(Properties((BpmnEventDefinitionProperties.Interval, "PT1M")));
        var parent = EventSubprocessParent();
        var boundWork = EventSubprocessBoundWork(body);

        var graph = BpmnGraph.Build(parent, boundWork, BpmnHostCapabilities.Full);
        var bodyGraph = BpmnGraph.Build(body, [new BpmnBoundWork("body-work")], BpmnHostCapabilities.Full);

        graph.ExternalTriggerEventSubprocesses.ShouldHaveSingleItem().TriggerKind
            .ShouldBe(BpmnEventSubprocessTriggerKind.Timer);
        BpmnTimerStartSchedule.Resolve(bodyGraph.StartEvents.Single()).Kind
            .ShouldBe(BpmnTimerStartScheduleKind.Interval);

        var cronBody = EventSubprocessBody(Properties((BpmnEventDefinitionProperties.Cron, "0 * * * *")));
        var cronError = Should.Throw<BpmnExecutionException>(() =>
            BpmnGraph.Build(parent, EventSubprocessBoundWork(cronBody), BpmnHostCapabilities.Full));

        cronError.Message.ShouldContain("declares a cron schedule without a cycle");
    }

    private static BpmnGraph BuildGraph(BpmnElement start)
    {
        var definition = new BpmnProcessBuilder("timer-start-test")
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

    private static BpmnProcessDefinition EventSubprocessBody(IReadOnlyDictionary<string, string> timerProperties) =>
        new BpmnProcessBuilder("timer-event-subprocess-body")
            .StartEvent("BodyStart", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, timerProperties))
            .Task("BodyWork", bindingRef: "body-work")
            .EndEvent("BodyEnd")
            .ConnectSequence("BodyStart", "BodyWork", "BodyEnd")
            .Build();

    private static BpmnProcessDefinition EventSubprocessParent() =>
        new BpmnProcessBuilder("timer-event-subprocess-parent")
            .StartEvent("Start")
            .Task("LongRunning", bindingRef: "long-running")
            .EndEvent("End")
            .Element(new BpmnElement(
                "TimerHandler",
                BpmnElementTypes.SubProcess,
                bindingRef: "timer-handler",
                triggeredByEvent: true,
                listenerBindingRef: "timer-listener"))
            .ConnectSequence("Start", "LongRunning", "End")
            .Build();

    private static BpmnBoundWork[] EventSubprocessBoundWork(BpmnProcessDefinition body) =>
    [
        new("long-running"),
        new("timer-handler", body),
        new("timer-listener")
    ];
}
