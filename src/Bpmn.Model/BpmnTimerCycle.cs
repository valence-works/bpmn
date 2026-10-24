using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Bpmn.Model;

/// <summary>
/// The parts of a timer's <c>&lt;timeCycle&gt;</c>: an ISO-8601 repeating interval <c>R[n]/[start/]duration</c>, a
/// bare duration repeated without end, or a cron expression.
/// <para>
/// Parsing only splits the text. The duration, the start instant and the cron expression are kept as strings,
/// never interpreted; the host that schedules the timer reads them, as it reads every other timer value.
/// </para>
/// </summary>
public sealed record BpmnTimerCycle
{
    private BpmnTimerCycle(string text, string? isoDuration, string? cron, int? repetitions, string? start)
    {
        Text = text;
        IsoDuration = isoDuration;
        Cron = cron;
        Repetitions = repetitions;
        Start = start;
    }

    /// <summary>The cycle as written, trimmed.</summary>
    public string Text { get; }

    /// <summary>The ISO-8601 duration between occurrences; <c>null</c> for a cron cycle.</summary>
    public string? IsoDuration { get; }

    /// <summary>The cron expression; <c>null</c> for an ISO-8601 cycle.</summary>
    public string? Cron { get; }

    /// <summary>How many times the timer fires, from <c>R&lt;n&gt;/</c>; <c>null</c> when it repeats without end.</summary>
    public int? Repetitions { get; }

    /// <summary>The instant of the first occurrence, from <c>R[n]/&lt;start&gt;/&lt;duration&gt;</c>; <c>null</c> when the cycle does not say.</summary>
    public string? Start { get; }

    /// <summary>
    /// Parses a <c>&lt;timeCycle&gt;</c> text. Text beginning with <c>R</c> is a repeating interval: <c>R</c> or
    /// <c>R&lt;n&gt;</c> (n at least 1), then a duration, or a start instant and a duration. Text beginning with
    /// <c>P</c> is a duration repeated without end. Anything else is a cron expression. Returns <c>false</c> for blank
    /// text, a repetition count that is not a positive whole number, or a repeating interval of another shape (such as
    /// <c>R/&lt;duration&gt;/&lt;end&gt;</c>).
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out BpmnTimerCycle? cycle)
    {
        cycle = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        if (trimmed[0] == 'P')
        {
            if (trimmed.Contains('/'))
                return false;
            cycle = new BpmnTimerCycle(trimmed, trimmed, cron: null, repetitions: null, start: null);
            return true;
        }

        if (trimmed[0] != 'R')
        {
            cycle = new BpmnTimerCycle(trimmed, isoDuration: null, cron: trimmed, repetitions: null, start: null);
            return true;
        }

        var parts = trimmed.Split('/');
        int? repetitions = null;
        if (parts[0].Length > 1)
        {
            if (!int.TryParse(parts[0].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
                return false;
            repetitions = count;
        }

        switch (parts.Length)
        {
            case 2 when IsDuration(parts[1]):
                cycle = new BpmnTimerCycle(trimmed, parts[1], cron: null, repetitions, start: null);
                return true;
            case 3 when !IsDuration(parts[1]) && parts[1].Length > 0 && IsDuration(parts[2]):
                cycle = new BpmnTimerCycle(trimmed, parts[2], cron: null, repetitions, parts[1]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The event-definition properties a cycle is read into: <see cref="BpmnEventDefinitionProperties.Cycle"/> with its
    /// text, <see cref="BpmnEventDefinitionProperties.Interval"/> or <see cref="BpmnEventDefinitionProperties.Cron"/>, and
    /// <see cref="BpmnEventDefinitionProperties.Repetitions"/> and <see cref="BpmnEventDefinitionProperties.Start"/> when it has them.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToProperties()
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Cycle] = Text };
        if (IsoDuration is not null)
            properties[BpmnEventDefinitionProperties.Interval] = IsoDuration;
        if (Cron is not null)
            properties[BpmnEventDefinitionProperties.Cron] = Cron;
        if (Repetitions is { } repetitions)
            properties[BpmnEventDefinitionProperties.Repetitions] = repetitions.ToString(CultureInfo.InvariantCulture);
        if (Start is not null)
            properties[BpmnEventDefinitionProperties.Start] = Start;
        return properties;
    }

    private static bool IsDuration(string part) => part.Length > 1 && part[0] == 'P';
}
