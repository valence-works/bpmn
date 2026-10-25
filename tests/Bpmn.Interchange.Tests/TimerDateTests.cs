using System.Xml.Linq;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>
/// A timer at a <c>&lt;timeDate&gt;</c> is kept at every position a timer can take: a start, an intermediate catch,
/// a boundary and an event-subprocess start. The instant is kept as written, a waiting timer binds a
/// <see cref="BpmnWorkBinding.TimerDateWait"/>, and the date is written back unchanged.
/// </summary>
public sealed class TimerDateTests
{
    private const string Instant = "2026-12-24T18:00:00Z";
    private static readonly XNamespace Model = BpmnXmlNames.Model;

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    private static string Document(string date) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="Defs" targetNamespace="http://example.test/orders">
          <bpmn:process id="P" isExecutable="true">
            <bpmn:startEvent id="Opening">
              <bpmn:timerEventDefinition><bpmn:timeDate>{date}</bpmn:timeDate></bpmn:timerEventDefinition>
            </bpmn:startEvent>
            <bpmn:intermediateCatchEvent id="Deadline">
              <bpmn:timerEventDefinition><bpmn:timeDate>{date}</bpmn:timeDate></bpmn:timerEventDefinition>
            </bpmn:intermediateCatchEvent>
            <bpmn:userTask id="Review" />
            <bpmn:boundaryEvent id="Cutoff" attachedToRef="Review" cancelActivity="false">
              <bpmn:timerEventDefinition><bpmn:timeDate>{date}</bpmn:timeDate></bpmn:timerEventDefinition>
            </bpmn:boundaryEvent>
            <bpmn:subProcess id="Reminder" triggeredByEvent="true">
              <bpmn:startEvent id="ReminderStart" isInterrupting="false">
                <bpmn:timerEventDefinition><bpmn:timeDate>{date}</bpmn:timeDate></bpmn:timerEventDefinition>
              </bpmn:startEvent>
              <bpmn:serviceTask id="Remind" />
              <bpmn:endEvent id="ReminderEnd" />
              <bpmn:sequenceFlow id="toRemind" sourceRef="ReminderStart" targetRef="Remind" />
              <bpmn:sequenceFlow id="toReminderEnd" sourceRef="Remind" targetRef="ReminderEnd" />
            </bpmn:subProcess>
            <bpmn:endEvent id="Done" />
            <bpmn:endEvent id="Late" />
            <bpmn:sequenceFlow id="toDeadline" sourceRef="Opening" targetRef="Deadline" />
            <bpmn:sequenceFlow id="toReview" sourceRef="Deadline" targetRef="Review" />
            <bpmn:sequenceFlow id="toDone" sourceRef="Review" targetRef="Done" />
            <bpmn:sequenceFlow id="toLate" sourceRef="Cutoff" targetRef="Late" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    [Fact]
    public void A_timer_start_at_a_date_keeps_the_instant_as_written()
    {
        var read = _reader.Read(Document($" {Instant} "));

        var definition = Element(read, "Opening").EventDefinitions.ShouldHaveSingleItem();
        definition.Type.ShouldBe(BpmnEventDefinitionTypes.Timer);
        definition.Properties.ShouldBe(new Dictionary<string, string> { [BpmnEventDefinitionProperties.Date] = Instant });
        read.Bindings.ShouldNotContain(binding => binding.ElementId == "Opening");
        read.Analysis.Issues.ShouldNotContain(issue => issue.Severity != BpmnImportIssueSeverity.Info);
    }

    [Theory]
    [InlineData("Deadline")]
    [InlineData("Cutoff")]
    public void A_date_catch_or_boundary_binds_a_wait_until_the_instant(string id)
    {
        var read = _reader.Read(Document(Instant));

        var element = Element(read, id);
        element.EventDefinitions.ShouldHaveSingleItem().Properties[BpmnEventDefinitionProperties.Date].ShouldBe(Instant);
        var wait = read.Bindings.OfType<BpmnWorkBinding.TimerDateWait>().Single(binding => binding.ElementId == id);
        wait.IsoDate.ShouldBe(Instant);
        wait.BindingRef.ShouldBe(element.BindingRef);
        wait.Slot.ShouldBe(BpmnBindingSlot.Primary);
        read.Bindings.OfType<BpmnWorkBinding.TimerWait>().ShouldBeEmpty();
    }

    [Fact]
    public void A_date_event_subprocess_arms_a_listener_that_waits_until_the_instant()
    {
        var read = _reader.Read(Document(Instant));

        var reminder = Element(read, "Reminder");
        var listener = read.Bindings.OfType<BpmnWorkBinding.TimerDateWait>().Single(binding => binding.ElementId == "Reminder");
        listener.IsoDate.ShouldBe(Instant);
        listener.Slot.ShouldBe(BpmnBindingSlot.ScopeListener);
        listener.BindingRef.ShouldBe(reminder.ListenerBindingRef);

        var body = read.Bindings.OfType<BpmnWorkBinding.NestedProcess>().Single(binding => binding.ElementId == "Reminder").Definition;
        body.Elements.Single(element => element.ElementId == "ReminderStart").EventDefinitions.ShouldHaveSingleItem()
            .Properties[BpmnEventDefinitionProperties.Date].ShouldBe(Instant);
    }

    [Fact]
    public void A_blank_date_is_treated_as_absent_at_every_position()
    {
        var read = _reader.Read(Document("  "));

        Element(read, "Opening").EventDefinitions.ShouldBeEmpty();
        var elementIds = read.Definitions.Processes.Single().Elements.Select(element => element.ElementId).ToArray();
        elementIds.ShouldNotContain("Deadline");
        elementIds.ShouldNotContain("Cutoff");
        elementIds.ShouldNotContain("Reminder");
        read.Bindings.OfType<BpmnWorkBinding.TimerDateWait>().ShouldBeEmpty();
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Opening" && issue.Severity == BpmnImportIssueSeverity.Degraded);
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Deadline" && issue.Severity == BpmnImportIssueSeverity.Dropped);
        read.Analysis.Issues.ShouldContain(issue => issue.ElementId == "Cutoff" && issue.Severity == BpmnImportIssueSeverity.Dropped);
    }

    [Fact]
    public void Every_date_timer_is_written_back_as_its_time_date_and_read_again_unchanged()
    {
        var first = _reader.Read(Document(Instant));

        var xml = _writer.Write(first);
        var document = XDocument.Parse(xml);
        foreach (var id in new[] { "Opening", "Deadline", "Cutoff", "ReminderStart" })
        {
            var timer = document.Descendants().Single(element => (string?)element.Attribute("id") == id)
                .Element(Model + "timerEventDefinition").ShouldNotBeNull();
            timer.Elements().Select(child => (child.Name.LocalName, child.Value)).ShouldBe([("timeDate", Instant)]);
        }

        var second = _reader.Read(xml);
        second.Bindings.OfType<BpmnWorkBinding.TimerDateWait>().Select(binding => (binding.ElementId, binding.Slot, binding.IsoDate))
            .ShouldBe(first.Bindings.OfType<BpmnWorkBinding.TimerDateWait>().Select(binding => (binding.ElementId, binding.Slot, binding.IsoDate)));
        Element(second, "Opening").EventDefinitions.ShouldHaveSingleItem().Properties[BpmnEventDefinitionProperties.Date].ShouldBe(Instant);
    }

    [Fact]
    public void A_date_catch_built_in_code_takes_its_instant_from_the_binding()
    {
        var definitions = new BpmnDefinitions(
            Id: "built",
            Processes: [new BpmnProcessDefinition("orders", Elements:
            [
                new BpmnElement("Start", BpmnElementTypes.StartEvent),
                new BpmnElement("Deadline", BpmnElementTypes.IntermediateCatchEvent, bindingRef: "node-deadline",
                    eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Timer)]),
                new BpmnElement("End", BpmnElementTypes.EndEvent)
            ], SequenceFlows:
            [
                new BpmnSequenceFlow("toDeadline", "Start", "Deadline"),
                new BpmnSequenceFlow("toEnd", "Deadline", "End")
            ])]);
        var bindings = new BpmnWorkBinding[]
        {
            new BpmnWorkBinding.TimerDateWait("orders", "Deadline", "node-deadline", BpmnBindingSlot.Primary, Instant)
        };

        var xml = _writer.Write(definitions, bindings);

        xml.ShouldContain($"<timeDate>{Instant}</timeDate>");
        _reader.Read(xml).Bindings.OfType<BpmnWorkBinding.TimerDateWait>().ShouldHaveSingleItem().IsoDate.ShouldBe(Instant);
    }

    private static BpmnElement Element(BpmnImportResult result, string id) =>
        result.Definitions.Processes.Single().Elements.Single(element => element.ElementId == id);
}
