using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// A timer start on a cycle resolves its repetition count and start, a cycle is checked against the parts written beside
/// it, and a timer catch, boundary or event subprocess on a cycle is accepted. A non-interrupting event subprocess on a
/// counted cycle re-arms until it has fired its count, carried on the listener token across persisted state.
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
    [InlineData("R3/PT1H", 3)]
    [InlineData("R/PT1H", null)]
    [InlineData("0 0 9 * * ?", null)]
    public void A_timer_event_subprocess_on_a_cycle_is_accepted_with_its_count(string cycle, int? repetitions)
    {
        var catcher = BpmnGraph.Build(EventSubprocessParent(), EventSubprocessBoundWork(EventSubprocessBody(Cycle(cycle))), BpmnHostCapabilities.Full)
            .ExternalTriggerEventSubprocesses.ShouldHaveSingleItem();

        catcher.TriggerKind.ShouldBe(BpmnEventSubprocessTriggerKind.Timer);
        catcher.FiresOnce.ShouldBeFalse();
        catcher.Repetitions.ShouldBe(repetitions);
    }

    // --- Event-subprocess repetitions ------------------------------------------------------------------

    [Fact]
    public void A_non_interrupting_counted_cycle_event_subprocess_fires_its_count_and_then_stops_re_arming()
    {
        var run = new EventSubprocessRun(EventSubprocessGraph(Cycle("R3/PT1H")));

        var fires = Enumerable.Range(0, 3).Select(_ => run.FireListener()).ToArray();

        fires.Select(StartedBindings).ShouldBe(
        [
            ["timer-handler", "timer-listener"],
            ["timer-handler", "timer-listener"],
            ["timer-handler"]
        ]);
        run.State.Tokens.ShouldNotContain(token => token.Kind == BpmnTokenKind.Listener && token.Status == BpmnTokenStatus.AwaitingChild);
        run.Live.ShouldNotContain(work => work.BindingRef == "timer-listener");

        foreach (var body in run.Live.Where(work => work.BindingRef == "timer-handler").ToArray())
        {
            run.Complete(body);
        }
        var completed = run.Complete(run.Live.ShouldHaveSingleItem());

        completed.Continuation.ShouldBe(new BpmnContinuation.Complete(BpmnInterpreter.DoneOutcomeName));
        completed.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldBeEmpty("no listener is left for completion to tear down");
    }

    [Fact]
    public void A_non_interrupting_uncounted_cycle_event_subprocess_re_arms_after_every_fire()
    {
        var run = new EventSubprocessRun(EventSubprocessGraph(Cycle("R/PT1H")));

        for (var fire = 1; fire <= 5; fire++)
        {
            StartedBindings(run.FireListener()).ShouldBe(["timer-handler", "timer-listener"]);
        }
        run.State.Tokens.ShouldAllBe(token => token.FireCount == null, "nothing bounds the cycle, so nothing counts its fires");
    }

    [Fact]
    public void The_fire_count_survives_persisting_and_restoring_the_state_between_fires()
    {
        var run = new EventSubprocessRun(EventSubprocessGraph(Cycle("R3/PT1H")));
        ArmedListener(run.State).FireCount.ShouldBeNull("the listener armed at scope start has not fired");

        StartedBindings(run.FireListener()).ShouldContain("timer-listener");
        run.Restore().ShouldContain("\"fireCount\":1");
        StartedBindings(run.FireListener()).ShouldContain("timer-listener");
        run.Restore().ShouldContain("\"fireCount\":2");
        ArmedListener(run.State).FireCount.ShouldBe(2);

        StartedBindings(run.FireListener()).ShouldBe(["timer-handler"]);
        run.State.Tokens.ShouldNotContain(token => token.Kind == BpmnTokenKind.Listener && token.Status == BpmnTokenStatus.AwaitingChild);
    }

    [Fact]
    public void An_interrupting_counted_cycle_event_subprocess_does_not_re_arm()
    {
        var run = new EventSubprocessRun(EventSubprocessGraph(Cycle("R3/PT1H"), interrupting: true));

        var fired = run.FireListener();

        StartedBindings(fired).ShouldBe(["timer-handler"]);
        fired.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldContain(cancel => cancel.ElementId == "LongRunning");
        run.State.Tokens.ShouldAllBe(token => token.FireCount == null);
    }

    [Fact]
    public void A_non_interrupting_date_event_subprocess_still_fires_once()
    {
        var run = new EventSubprocessRun(EventSubprocessGraph(new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer,
            Properties((BpmnEventDefinitionProperties.Date, "2026-12-24T18:00:00Z")))));

        StartedBindings(run.FireListener()).ShouldBe(["timer-handler"]);
        run.State.Tokens.ShouldAllBe(token => token.FireCount == null);
    }

    /// <summary>
    /// Drives one scope through the interpreter as a host would, keeping the ledger of the work it started and has not
    /// yet completed, so every evaluation sees an honest live-work snapshot.
    /// </summary>
    private sealed class EventSubprocessRun
    {
        private readonly BpmnGraph _graph;
        private readonly BpmnInterpreter _interpreter = BpmnInterpreter.CreateDefault();
        private readonly List<BpmnLiveWork> _live = [];

        public EventSubprocessRun(BpmnGraph graph)
        {
            _graph = graph;
            Apply(_interpreter.Start(new BpmnStartRequest(graph, null, Snapshot())));
        }

        public BpmnExecutionState State { get; private set; } = new();

        public IReadOnlyList<BpmnLiveWork> Live => _live;

        /// <summary>Fires the one armed timer listener.</summary>
        public BpmnEvaluation FireListener() => Complete(_live.Single(work => work.BindingRef == "timer-listener"));

        public BpmnEvaluation Complete(BpmnLiveWork work)
        {
            _live.Remove(work);
            return Apply(_interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(_graph, State, Snapshot(), work.BindingRef, work.Handle, [])
            {
                WorkTokenId = work.WorkTokenId
            }));
        }

        /// <summary>Writes the state as JSON and continues from what reads back, as a host persisting between callbacks does.</summary>
        public string Restore()
        {
            var json = JsonSerializer.Serialize(State);
            State = JsonSerializer.Deserialize<BpmnExecutionState>(json).ShouldNotBeNull();
            return json;
        }

        private BpmnEvaluation Apply(BpmnEvaluation evaluation)
        {
            State = evaluation.State;
            foreach (var start in evaluation.Commands.OfType<BpmnHostCommand.StartWork>())
            {
                _live.Add(new BpmnLiveWork(start.BindingRef, start.IterationScope?.IterationId, $"handle:{start.TokenId}") { WorkTokenId = start.TokenId });
            }

            foreach (var cancel in evaluation.Commands.OfType<BpmnHostCommand.CancelWorkSubtree>())
            {
                _live.RemoveAll(work => work.Handle == cancel.Handle);
            }
            return evaluation;
        }

        private BpmnHostSnapshot Snapshot() =>
            BpmnHostSnapshot.Root("scope-1") with { LiveWork = _live.ToArray() };
    }

    private static string[] StartedBindings(BpmnEvaluation evaluation) =>
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>()
            .Select(command => command.BindingRef)
            .OrderBy(binding => binding, StringComparer.Ordinal)
            .ToArray();

    private static BpmnToken ArmedListener(BpmnExecutionState state) =>
        state.Tokens.Where(token => token.Kind == BpmnTokenKind.Listener && token.Status == BpmnTokenStatus.AwaitingChild).ShouldHaveSingleItem();

    // --- Fixtures --------------------------------------------------------------------------------------

    private static BpmnGraph EventSubprocessGraph(BpmnEventDefinition timer, bool interrupting = false) =>
        BpmnGraph.Build(EventSubprocessParent(), EventSubprocessBoundWork(EventSubprocessBody(timer, interrupting)), BpmnHostCapabilities.Full);

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

    private static BpmnProcessDefinition EventSubprocessBody(BpmnEventDefinition timer, bool interrupting = false) =>
        new BpmnProcessBuilder("timer-event-subprocess-body")
            .Element(new BpmnElement("BodyStart", BpmnElementTypes.StartEvent, cancelActivity: interrupting, eventDefinitions: [timer]))
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
