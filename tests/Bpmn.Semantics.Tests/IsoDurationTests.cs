using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class IsoDurationTests
{
    [Theory]
    [InlineData("PT1H", 0, 1, 0, 0)]
    [InlineData("PT30M", 0, 0, 30, 0)]
    [InlineData("P7D", 7, 0, 0, 0)]
    [InlineData("P1DT2H30M", 1, 2, 30, 0)]
    [InlineData("PT0S", 0, 0, 0, 0)]
    [InlineData("PT1.5S", 0, 0, 0, 1)]
    public void The_common_shapes_parse(string text, int days, int hours, int minutes, int seconds)
    {
        var parsed = IsoDuration.Parse(text);

        parsed.Days.ShouldBe(days);
        parsed.Hours.ShouldBe(hours);
        parsed.Minutes.ShouldBe(minutes);
        parsed.Seconds.ShouldBe(seconds);
    }

    [Theory]
    [InlineData("7 days")]
    [InlineData("P")]
    [InlineData("1H")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? text)
    {
        IsoDuration.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void The_failure_names_the_offending_text_and_shows_what_was_expected()
    {
        var failure = Should.Throw<FormatException>(() => IsoDuration.Parse("7 days"));

        failure.Message.ShouldContain("7 days");
        failure.Message.ShouldContain("P1DT2H30M");
    }

    [Fact]
    public void Whitespace_around_a_duration_is_tolerated()
    {
        IsoDuration.Parse("  PT15M  ").ShouldBe(TimeSpan.FromMinutes(15));
    }
}
