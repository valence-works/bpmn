using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// A timer start on a cycle resolves its repetition count and start, a cycle is checked against the parts written beside
/// it, and a timer catch, boundary or event subprocess on a cycle is accepted.
/// </summary>
public sealed class TimerCycleTests
{
    private readonly InMemoryBpmnHost _host = new();

    // --- Timer start schedule --------------------------------------------------------------------------

    [Theory]
    [InlineData("R3/2026-01-01T00:00:00Z/PT1H", BpmnTimerStartScheduleKind.Interval, "PT1H", 3, "2026-01-01T00:00:00Z")]
    [InlineData("R/PT15M", BpmnTimerStartScheduleKind.Interval, "PT15M", null, null)]
    [InlineData("0  0 9 * * ?", BpmnTimerStartScheduleKind.Cron, "0 0 9 * * ?", null, null)]
    public void A_timer_start_on_a_cycle_resolves_its_repetitions_and_start(string cycle, BpmnTimerStartScheduleKind kind, string expression, int? repetitions, string? start)
    {
        var schedule = BpmnTimerStartSchedule.Resolve(TimerStart(Cycle(cycle)));

        (schedule.Kind, schedule.Expression, schedule.Repetitions, schedule.Start).ShouldBe((kind, expression, repetitions, start));
    }

    [Fact]
    public void A_timer_start_built_in_code_with_only_a_cycle_takes_its_interval_from_it()
    {
        var schedule = BpmnTimerStartSchedule.Resolve(TimerStart(Properties((BpmnEventDefinitionProperties.Cycle, "R2/P1D"))));

        (schedule.Kind, schedule.Expression, schedule.Repetitions).ShouldBe((BpmnTimerStartScheduleKind.Interval, "P1D", 2));
    }

    [Fact]
    public void A_timer_start_on_a_duration_has_no_repetitions_or_start()
    {
        var schedule = BpmnTimerStartSchedule.Resolve(TimerStart(Properties((BpmnEventDefinitionProperties.Interval, "PT1H"))));

        (schedule.Repetitions, schedule.Start).ShouldBe((null, null));
    }

    [Theory]
    [InlineData("a cycle that does not parse", "R/PT1H/2026-01-01T00:00:00Z", null, null, "which is not an ISO-8601 repeating interval")]
    [InlineData("a cycle beside a date", "R/PT1H", BpmnEventDefinitionProperties.Date, "2026-01-01T00:00:00Z", "declares both a cycle and a date")]
    [InlineData("an interval that disagrees with the cycle", "R/PT1H", BpmnEventDefinitionProperties.Interval, "PT2H", "disagrees with its cycle")]
    [InlineData("a count that disagrees with the cycle", "R3/PT1H", BpmnEventDefinitionProperties.Repetitions, "4", "disagrees with its cycle")]
    [InlineData("a start that disagrees with the cycle", "R/2026-01-01T00:00:00Z/PT1H", BpmnEventDefinitionProperties.Start, "2027-01-01T00:00:00Z", "disagrees with its cycle")]
    [InlineData("a cron that disagrees with the cycle", "0 0 9 * * ?", BpmnEventDefinitionProperties.Cron, "0 0 10 * * ?", "disagrees with its cycle")]
    public void A_timer_start_whose_cycle_is_unusable_or_contradicted_is_refused(string because, string cycle, string? key, string? value, string message)
    {
        var properties = Properties((BpmnEventDefinitionProperties.Cycle, cycle));
        if (key is not null)
            properties[key] = value!;

        var error = Should.Throw<BpmnExecutionException>(() => BpmnTimerStartSchedule.Resolve(TimerStart(properties)), because);

        error.Message.ShouldContain(message);
        error.ElementIds.ShouldBe(["TimerStart"]);
    }

    [Fact]
    public void A_count_without_a_cycle_is_refused()
    {
        var error = Should.Throw<BpmnExecutionException>(() => BpmnTimerStartSchedule.Resolve(TimerStart(
            Properties((BpmnEventDefinitionProperties.Interval, "PT1H"), (BpmnEventDefinitionProperties.Repetitions, "3")))));

        error.Message.ShouldContain("without a 'cycle'");
    }

    // --- Graph validation ------------------------------------------------------------------------------

    [Fact]
    public void A_timer_catch_or_boundary_whose_cycle_does_not_parse_is_refused()
    {
        var unusable = new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, Properties((BpmnEventDefinitionProperties.Cycle, "R0/PT1H")));
        var catchProcess = new BpmnProcessBuilder("cycle-catch")
            .StartEvent("Start")
            .IntermediateCatchEvent("Tick", unusable, bindingRef: "tick")
            .EndEvent("End")
            .ConnectSequence("Start", "Tick", "End")
            .Build();

        var catchError = Should.Throw<BpmnExecutionException>(() => BpmnGraph.Build(catchProcess, [new("tick")], BpmnHostCapabilities.Full));
        var boundaryError = Should.Throw<BpmnExecutionException>(() => _host.Start(BoundaryProcess(unusable, interrupting: false)));

        catchError.ElementIds.ShouldBe(["Tick"]);
        boundaryError.ElementIds.ShouldBe(["Nudge"]);
    }

    [Theory]
    [InlineData("R3/PT1H")]
    [InlineData("R/PT1H")]
    [InlineData("0 0 9 * * ?")]
    public void A_timer_event_subprocess_on_a_cycle_is_accepted(string cycle)
    {
        var catcher = BpmnGraph.Build(EventSubprocessParent(), EventSubprocessBoundWork(EventSubprocessBody(Cycle(cycle))), BpmnHostCapabilities.Full)
            .ExternalTriggerEventSubprocesses.ShouldHaveSingleItem();

        catcher.TriggerKind.ShouldBe(BpmnEventSubprocessTriggerKind.Timer);
        catcher.FiresOnce.ShouldBeFalse();
    }

    // --- Fixtures --------------------------------------------------------------------------------------

    private static BpmnProcessDefinition BoundaryProcess(BpmnEventDefinition timer, bool interrupting) =>
        new BpmnProcessBuilder("cycle-boundary")
            .StartEvent("Start")
            .Task("Long", bindingRef: "long")
            .EndEvent("EndNormal")
            .BoundaryEvent("Nudge", "Long", timer, interrupting, bindingRef: "nudge")
            .Task("Notify", bindingRef: "notify")
            .EndEvent("EndNotified")
            .ConnectSequence("Start", "Long", "EndNormal")
            .ConnectSequence("Nudge", "Notify", "EndNotified")
            .Build();

    private static BpmnProcessDefinition EventSubprocessBody(BpmnEventDefinition timer) =>
        new BpmnProcessBuilder("timer-event-subprocess-body")
            .Element(new BpmnElement("BodyStart", BpmnElementTypes.StartEvent, cancelActivity: false, eventDefinitions: [timer]))
            .Task("BodyWork", bindingRef: "body-work")
            .EndEvent("BodyEnd")
            .ConnectSequence("BodyStart", "BodyWork", "BodyEnd")
            .Build();

    private static BpmnProcessDefinition EventSubprocessParent() =>
        new BpmnProcessBuilder("timer-event-subprocess-parent")
            .StartEvent("Start")
            .Task("LongRunning", bindingRef: "long-running")
            .EndEvent("End")
            .Element(new BpmnElement("TimerHandler", BpmnElementTypes.SubProcess,
                bindingRef: "timer-handler", triggeredByEvent: true, listenerBindingRef: "timer-listener"))
            .ConnectSequence("Start", "LongRunning", "End")
            .Build();

    private static BpmnBoundWork[] EventSubprocessBoundWork(BpmnProcessDefinition body) =>
        [new("long-running"), new("timer-handler", body), new("timer-listener")];

    private static BpmnEventDefinition Cycle(string text) =>
        BpmnTimerCycle.TryParse(text, out var cycle)
            ? new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, cycle.ToProperties())
            : throw new ArgumentException($"'{text}' is not a cycle.", nameof(text));

    private static BpmnElement TimerStart(BpmnEventDefinition timer) =>
        new("TimerStart", BpmnElementTypes.StartEvent, eventDefinitions: [timer]);

    private static BpmnElement TimerStart(IReadOnlyDictionary<string, string> properties) =>
        TimerStart(new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer, properties));

    private static Dictionary<string, string> Properties(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}
