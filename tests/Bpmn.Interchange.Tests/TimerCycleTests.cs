using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// A timer on a <c>&lt;timeCycle&gt;</c> is kept at every position a timer can take: a start, an intermediate catch,
/// a boundary and an event-subprocess start. The cycle is kept as written beside its parts, a waiting timer binds a
/// <see cref="BpmnWorkBinding.TimerCycleWait"/>, and the cycle is written back unchanged.
/// </summary>
public sealed class TimerCycleTests
{
    private const string Cycle = "R3/2026-01-01T00:00:00Z/PT1H";
    private static readonly XNamespace Model = BpmnXmlNames.Model;

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    private static string Document(string cycle) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs" targetNamespace="http://example.test/orders">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Opening">
              <bpmn:timerEventDefinition><bpmn:timeCycle>{cycle}</bpmn:timeCycle></bpmn:timerEventDefinition>
            </bpmn:startEvent>
            <bpmn:intermediateCatchEvent id="Tick">
              <bpmn:timerEventDefinition><bpmn:timeCycle>{cycle}</bpmn:timeCycle></bpmn:timerEventDefinition>
            </bpmn:intermediateCatchEvent>
            <bpmn:userTask id="Review" />
            <bpmn:boundaryEvent id="Nudge" attachedToRef="Review" cancelActivity="false">
              <bpmn:timerEventDefinition><bpmn:timeCycle>{cycle}</bpmn:timeCycle></bpmn:timerEventDefinition>
            </bpmn:boundaryEvent>
            <bpmn:subProcess id="Reminder" triggeredByEvent="true">
              <bpmn:startEvent id="ReminderStart" isInterrupting="false">
                <bpmn:timerEventDefinition><bpmn:timeCycle>{cycle}</bpmn:timeCycle></bpmn:timerEventDefinition>
              </bpmn:startEvent>
              <bpmn:serviceTask id="Remind" />
              <bpmn:endEvent id="ReminderEnd" />
              <bpmn:sequenceFlow id="toRemind" sourceRef="ReminderStart" targetRef="Remind" />
              <bpmn:sequenceFlow id="toReminderEnd" sourceRef="Remind" targetRef="ReminderEnd" />
            </bpmn:subProcess>
            <bpmn:endEvent id="Done" />
            <bpmn:endEvent id="Nudged" />
            <bpmn:sequenceFlow id="toTick" sourceRef="Opening" targetRef="Tick" />
            <bpmn:sequenceFlow id="toReview" sourceRef="Tick" targetRef="Review" />
            <bpmn:sequenceFlow id="toDone" sourceRef="Review" targetRef="Done" />
            <bpmn:sequenceFlow id="toNudged" sourceRef="Nudge" targetRef="Nudged" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static readonly Dictionary<string, string> CycleProperties = new()
    {
        [BpmnEventDefinitionProperties.Cycle] = Cycle,
        [BpmnEventDefinitionProperties.Interval] = "PT1H",
        [BpmnEventDefinitionProperties.Repetitions] = "3",
        [BpmnEventDefinitionProperties.Start] = "2026-01-01T00:00:00Z"
    };

    [Theory]
    [InlineData("Opening")]
    [InlineData("Tick")]
    [InlineData("Nudge")]
    public void A_cycle_is_kept_as_written_beside_its_parts(string id)
    {
        var read = _reader.Read(Document($" {Cycle} "));

        var definition = Element(read, id).EventDefinitions.ShouldHaveSingleItem();
        definition.Type.ShouldBe(BpmnEventDefinitionTypes.Timer);
        definition.Properties.ShouldBe(CycleProperties, ignoreOrder: true);
        read.Analysis.Issues.ShouldNotContain(issue => issue.ElementId == id && issue.Severity != BpmnImportIssueSeverity.Info);
    }

    [Theory]
    [InlineData("R/2026-01-01T00:00:00Z/PT1H", "PT1H", null, "2026-01-01T00:00:00Z")]
    [InlineData("R5/P1D", "P1D", "5", null)]
    public void A_timer_start_keeps_the_count_and_reads_the_duration_after_a_start_instant(string cycle, string interval, string? repetitions, string? start)
    {
        var properties = Element(_reader.Read(Document(cycle)), "Opening").EventDefinitions.ShouldHaveSingleItem().Properties;

        properties[BpmnEventDefinitionProperties.Interval].ShouldBe(interval);
        properties.GetValueOrDefault(BpmnEventDefinitionProperties.Repetitions).ShouldBe(repetitions);
        properties.GetValueOrDefault(BpmnEventDefinitionProperties.Start).ShouldBe(start);
    }

    [Theory]
    [InlineData("Tick")]
    [InlineData("Nudge")]
    public void A_cycle_catch_or_boundary_binds_a_wait_on_the_cycle(string id)
    {
        var read = _reader.Read(Document(Cycle));

        var element = Element(read, id);
        var wait = read.Bindings.OfType<BpmnWorkBinding.TimerCycleWait>().Single(binding => binding.ElementId == id);
        wait.ShouldBe(new BpmnWorkBinding.TimerCycleWait("P", id, element.BindingRef!, BpmnBindingSlot.Primary, Cycle, "PT1H", null, 3, "2026-01-01T00:00:00Z"));
        read.Bindings.OfType<BpmnWorkBinding.TimerWait>().ShouldBeEmpty();
    }

    [Fact]
    public void A_cycle_event_subprocess_arms_a_listener_on_the_cycle()
    {
        var read = _reader.Read(Document(Cycle));

        var reminder = Element(read, "Reminder");
        var listener = read.Bindings.OfType<BpmnWorkBinding.TimerCycleWait>().Single(binding => binding.ElementId == "Reminder");
        listener.Slot.ShouldBe(BpmnBindingSlot.ScopeListener);
        listener.BindingRef.ShouldBe(reminder.ListenerBindingRef);
        listener.Repetitions.ShouldBe(3);

        var body = read.Bindings.OfType<BpmnWorkBinding.NestedProcess>().Single(binding => binding.ElementId == "Reminder").Definition;
        body.Elements.Single(element => element.ElementId == "ReminderStart").EventDefinitions.ShouldHaveSingleItem()
            .Properties.ShouldBe(CycleProperties, ignoreOrder: true);
    }

    [Fact]
    public void A_cron_cycle_binds_a_wait_on_the_cron_expression()
    {
        const string cron = "0 0 9 * * ?";

        var read = _reader.Read(Document(cron));

        var wait = read.Bindings.OfType<BpmnWorkBinding.TimerCycleWait>().Single(binding => binding.ElementId == "Tick");
        (wait.Cycle, wait.IsoDuration, wait.Cron, wait.Repetitions, wait.Start).ShouldBe((cron, null, cron, null, null));
        Element(read, "Opening").EventDefinitions.ShouldHaveSingleItem().Properties[BpmnEventDefinitionProperties.Cron].ShouldBe(cron);
    }

    [Fact]
    public void A_cycle_of_a_shape_it_cannot_read_is_reported_at_every_position()
    {
        var read = _reader.Read(Document("R/PT1H/2026-01-01T00:00:00Z"));

        Element(read, "Opening").EventDefinitions.ShouldBeEmpty();
        var elementIds = read.Definitions.Processes.Single().Elements.Select(element => element.ElementId).ToArray();
        elementIds.ShouldNotContain("Tick");
        elementIds.ShouldNotContain("Nudge");
        elementIds.ShouldNotContain("Reminder");
        read.Bindings.OfType<BpmnWorkBinding.TimerCycleWait>().ShouldBeEmpty();
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Opening" && issue.Severity == BpmnImportIssueSeverity.Degraded);
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Tick" && issue.Severity == BpmnImportIssueSeverity.Dropped);
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Nudge" && issue.Severity == BpmnImportIssueSeverity.Dropped);
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "ReminderStart" && issue.Severity == BpmnImportIssueSeverity.Degraded);
    }

    [Fact]
    public void Every_cycle_timer_is_written_back_as_its_time_cycle_and_read_again_unchanged()
    {
        var first = _reader.Read(Document(Cycle));

        var xml = _writer.Write(first);
        var document = XDocument.Parse(xml);
        foreach (var id in new[] { "Opening", "Tick", "Nudge", "ReminderStart" })
        {
            var timer = document.Descendants().Single(element => (string?)element.Attribute("id") == id)
                .Element(Model + "timerEventDefinition").ShouldNotBeNull();
            timer.Elements().Select(child => (child.Name.LocalName, child.Value)).ShouldBe([("timeCycle", Cycle)]);
        }

        var second = _reader.Read(xml);
        second.Bindings.OfType<BpmnWorkBinding.TimerCycleWait>().ShouldBe(first.Bindings.OfType<BpmnWorkBinding.TimerCycleWait>());
        Element(second, "Opening").EventDefinitions.ShouldHaveSingleItem().Properties.ShouldBe(CycleProperties, ignoreOrder: true);
    }

    [Fact]
    public void A_duration_beside_a_cycle_still_wins_at_a_catch_and_a_cycle_beside_a_date_still_wins_at_a_start()
    {
        const string xml = """
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs" targetNamespace="http://example.test/orders">
              <bpmn:process id="P" isExecutable="true">
                <bpmn:startEvent id="Opening">
                  <bpmn:timerEventDefinition><bpmn:timeDate>2026-12-24T18:00:00Z</bpmn:timeDate><bpmn:timeCycle>R/PT1H</bpmn:timeCycle></bpmn:timerEventDefinition>
                </bpmn:startEvent>
                <bpmn:intermediateCatchEvent id="Tick">
                  <bpmn:timerEventDefinition><bpmn:timeCycle>R/PT1H</bpmn:timeCycle><bpmn:timeDuration>PT5M</bpmn:timeDuration></bpmn:timerEventDefinition>
                </bpmn:intermediateCatchEvent>
                <bpmn:endEvent id="Done" />
                <bpmn:sequenceFlow id="toTick" sourceRef="Opening" targetRef="Tick" />
                <bpmn:sequenceFlow id="toDone" sourceRef="Tick" targetRef="Done" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        var read = _reader.Read(xml);

        Element(read, "Opening").EventDefinitions.ShouldHaveSingleItem().Properties[BpmnEventDefinitionProperties.Cycle].ShouldBe("R/PT1H");
        read.Bindings.ShouldHaveSingleItem().ShouldBeOfType<BpmnWorkBinding.TimerWait>().IsoDuration.ShouldBe("PT5M");
    }

    [Fact]
    public void A_cycle_catch_built_in_code_takes_its_cycle_from_the_binding()
    {
        var definitions = new BpmnDefinitions(
            Id: "built",
            Processes: [new BpmnProcessDefinition("orders", Elements:
            [
                new BpmnElement("Start", BpmnElementTypes.StartEvent),
                new BpmnElement("Tick", BpmnElementTypes.IntermediateCatchEvent, bindingRef: "node-tick",
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer)]),
                new BpmnElement("End", BpmnElementTypes.EndEvent)
            ], SequenceFlows:
            [
                new BpmnSequenceFlow("toTick", "Start", "Tick"),
                new BpmnSequenceFlow("toEnd", "Tick", "End")
            ])]);
        var bindings = new BpmnWorkBinding[]
        {
            new BpmnWorkBinding.TimerCycleWait("orders", "Tick", "node-tick", BpmnBindingSlot.Primary, "R2/PT10M", "PT10M", null, 2, null)
        };

        var xml = _writer.Write(definitions, bindings);

        xml.ShouldContain("<timeCycle>R2/PT10M</timeCycle>");
        _reader.Read(xml).Bindings.OfType<BpmnWorkBinding.TimerCycleWait>().ShouldHaveSingleItem().Repetitions.ShouldBe(2);
    }

    private static BpmnElement Element(BpmnImportResult result, string id) =>
        result.Definitions.Processes.Single().Elements.Single(element => element.ElementId == id);
}
