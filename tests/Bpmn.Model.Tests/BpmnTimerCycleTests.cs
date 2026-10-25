using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Model.Tests;

/// <summary>
/// <see cref="BpmnTimerCycle"/> splits a <c>&lt;timeCycle&gt;</c> into its parts, keeping each as written, and refuses
/// the shapes it cannot represent rather than guessing.
/// </summary>
public sealed class BpmnTimerCycleTests
{
    [Theory]
    [InlineData("R3/PT1H", "PT1H", null, 3, null)]
    [InlineData("R/PT1H", "PT1H", null, null, null)]
    [InlineData("R/2026-01-01T00:00:00Z/PT1H", "PT1H", null, null, "2026-01-01T00:00:00Z")]
    [InlineData("R2/2026-01-01T00:00:00Z/P1D", "P1D", null, 2, "2026-01-01T00:00:00Z")]
    [InlineData("PT1H", "PT1H", null, null, null)]
    [InlineData("0 0 9 * * ?", null, "0 0 9 * * ?", null, null)]
    [InlineData("  R3/PT1H  ", "PT1H", null, 3, null)]
    public void A_cycle_is_split_into_its_parts(string text, string? isoDuration, string? cron, int? repetitions, string? start)
    {
        BpmnTimerCycle.TryParse(text, out var cycle).ShouldBeTrue();

        cycle!.Text.ShouldBe(text.Trim());
        cycle.IsoDuration.ShouldBe(isoDuration);
        cycle.Cron.ShouldBe(cron);
        cycle.Repetitions.ShouldBe(repetitions);
        cycle.Start.ShouldBe(start);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("R0/PT1H")]
    [InlineData("R-1/PT1H")]
    [InlineData("Rx/PT1H")]
    [InlineData("R3")]
    [InlineData("R3/")]
    [InlineData("R/PT1H/2026-01-01T00:00:00Z")]
    [InlineData("R/2026-01-01T00:00:00Z/2026-02-01T00:00:00Z")]
    [InlineData("R/a/b/c")]
    [InlineData("PT1H/PT2H")]
    public void A_cycle_of_another_shape_is_refused(string? text)
    {
        BpmnTimerCycle.TryParse(text, out var cycle).ShouldBeFalse();
        cycle.ShouldBeNull();
    }

    [Fact]
    public void A_cycle_is_read_into_its_event_definition_properties()
    {
        BpmnTimerCycle.TryParse("R2/2026-01-01T00:00:00Z/P1D", out var cycle).ShouldBeTrue();

        cycle!.ToProperties().ShouldBe(new Dictionary<string, string>
        {
            [BpmnEventDefinitionProperties.Cycle] = "R2/2026-01-01T00:00:00Z/P1D",
            [BpmnEventDefinitionProperties.Interval] = "P1D",
            [BpmnEventDefinitionProperties.Repetitions] = "2",
            [BpmnEventDefinitionProperties.Start] = "2026-01-01T00:00:00Z"
        }, ignoreOrder: true);
    }

    [Fact]
    public void A_cron_cycle_is_read_into_its_text_and_cron()
    {
        BpmnTimerCycle.TryParse("0 0 9 * * ?", out var cycle).ShouldBeTrue();

        cycle!.ToProperties().ShouldBe(new Dictionary<string, string>
        {
            [BpmnEventDefinitionProperties.Cycle] = "0 0 9 * * ?",
            [BpmnEventDefinitionProperties.Cron] = "0 0 9 * * ?"
        }, ignoreOrder: true);
    }
}
