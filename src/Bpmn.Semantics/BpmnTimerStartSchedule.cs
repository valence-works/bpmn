using Bpmn.Model;

namespace Bpmn.Semantics;

/// <summary>The schedule declared by a timer start event: recurring, or a single start at a date.</summary>
public enum BpmnTimerStartScheduleKind
{
    /// <summary>An ISO-8601 duration between starts.</summary>
    Interval,

    /// <summary>A cron expression interpreted by the host's scheduler.</summary>
    Cron,

    /// <summary>An ISO-8601 instant: the process starts once, at that moment.</summary>
    Date
}

/// <summary>
/// Validated timer-start schedule facts for a host registering a timer trigger. Resolution checks the
/// interval, cron or date choice and normalizes whitespace; it does not parse scheduler syntax, read a clock or
/// register a trigger. This is not a persisted execution-state or payload contract.
/// </summary>
public sealed record BpmnTimerStartSchedule
{
    private BpmnTimerStartSchedule(BpmnTimerStartScheduleKind kind, string expression, BpmnTimerCycle? cycle = null)
    {
        Kind = kind;
        Expression = expression;
        Repetitions = cycle?.Repetitions;
        Start = cycle?.Start;
    }

    /// <summary>Whether the start declares an interval, a cron expression or a date.</summary>
    public BpmnTimerStartScheduleKind Kind { get; }

    /// <summary>The trimmed interval, date, or cron expression with whitespace between fields collapsed.</summary>
    public string Expression { get; }

    /// <summary>
    /// How many times the process starts, from a cycle's <c>R&lt;n&gt;/</c>; <c>null</c> when it starts without end, or for
    /// a date, which starts it once. A host stops the trigger after this many starts.
    /// </summary>
    public int? Repetitions { get; }

    /// <summary>The instant of the first start, from a cycle's <c>R[n]/&lt;start&gt;/&lt;duration&gt;</c>; <c>null</c> when the cycle does not say.</summary>
    public string? Start { get; }

    /// <summary>
    /// Resolves exactly one nonblank <c>interval</c>, <c>cron</c> or <c>date</c> property from a timer start event,
    /// with the repetition count and start instant of its <c>cycle</c>, when it declares one. A <c>cycle</c> with no
    /// <c>interval</c> or <c>cron</c> beside it supplies them. Throws <see cref="BpmnExecutionException"/> for a different
    /// element/definition, an ambiguous or missing schedule, or a cycle that does not parse or that its parts disagree
    /// with. A host must still validate the expression against its scheduler before registration.
    /// </summary>
    public static BpmnTimerStartSchedule Resolve(BpmnElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (BpmnElementFamilies.Resolve(element) != BpmnElementFamilies.StartEventTimer)
            throw new BpmnExecutionException($"BPMN element '{element.ElementId}' is not a timer start event.");

        var properties = element.EventDefinitions.Single().Properties;
        var cycle = BpmnTimerCycleRules.Resolve(properties, $"BPMN timer start event '{element.ElementId}'", [element.ElementId]);
        var hasInterval = properties.TryGetValue(BpmnEventDefinitionProperties.Interval, out var interval)
            && !string.IsNullOrWhiteSpace(interval);
        var hasCron = properties.TryGetValue(BpmnEventDefinitionProperties.Cron, out var cron)
            && !string.IsNullOrWhiteSpace(cron);
        if (cycle is not null && !hasInterval && !hasCron)
        {
            (hasInterval, interval, hasCron, cron) = (cycle.IsoDuration is not null, cycle.IsoDuration, cycle.Cron is not null, cycle.Cron);
        }
        var hasDate = properties.TryGetValue(BpmnEventDefinitionProperties.Date, out var date)
            && !string.IsNullOrWhiteSpace(date);

        if ((hasInterval ? 1 : 0) + (hasCron ? 1 : 0) + (hasDate ? 1 : 0) != 1)
            throw new BpmnExecutionException(
                $"BPMN timer start event '{element.ElementId}' must declare exactly one of '{BpmnEventDefinitionProperties.Interval}', '{BpmnEventDefinitionProperties.Cron}' or '{BpmnEventDefinitionProperties.Date}'.");

        if (hasInterval)
            return new BpmnTimerStartSchedule(BpmnTimerStartScheduleKind.Interval, interval!.Trim(), cycle);
        if (hasDate)
            return new BpmnTimerStartSchedule(BpmnTimerStartScheduleKind.Date, date!.Trim());
        return new BpmnTimerStartSchedule(BpmnTimerStartScheduleKind.Cron, BpmnTimerCycleRules.CollapseWhitespace(cron!), cycle);
    }
}
