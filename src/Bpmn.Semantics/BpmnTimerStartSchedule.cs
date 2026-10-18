using Bpmn.Model;

namespace Bpmn.Semantics;

/// <summary>The recurring schedule declared by a timer start event.</summary>
public enum BpmnTimerStartScheduleKind
{
    /// <summary>An ISO-8601 duration between starts.</summary>
    Interval,

    /// <summary>A cron expression interpreted by the host's scheduler.</summary>
    Cron
}

/// <summary>
/// Validated timer-start schedule facts for a host registering a recurring trigger. Resolution checks the
/// interval-or-cron choice and normalizes whitespace; it does not parse scheduler syntax, read a clock or
/// register a trigger. This is not a persisted execution-state or payload contract.
/// </summary>
public sealed record BpmnTimerStartSchedule
{
    private BpmnTimerStartSchedule(BpmnTimerStartScheduleKind kind, string expression)
    {
        Kind = kind;
        Expression = expression;
    }

    /// <summary>Whether the start declares an interval or a cron expression.</summary>
    public BpmnTimerStartScheduleKind Kind { get; }

    /// <summary>The trimmed interval or cron expression with whitespace between fields collapsed.</summary>
    public string Expression { get; }

    /// <summary>
    /// Resolves exactly one nonblank <c>interval</c> or <c>cron</c> property from a timer start event.
    /// Throws <see cref="BpmnExecutionException"/> for a different element/definition or an ambiguous or
    /// missing schedule. A host must still validate the expression against its scheduler before registration.
    /// </summary>
    public static BpmnTimerStartSchedule Resolve(BpmnElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (BpmnElementFamilies.Resolve(element) != BpmnElementFamilies.StartEventTimer)
            throw new BpmnExecutionException($"BPMN element '{element.ElementId}' is not a timer start event.");

        var properties = element.EventDefinitions.Single().Properties;
        var hasInterval = properties.TryGetValue(BpmnEventDefinitionProperties.Interval, out var interval)
            && !string.IsNullOrWhiteSpace(interval);
        var hasCron = properties.TryGetValue(BpmnEventDefinitionProperties.Cron, out var cron)
            && !string.IsNullOrWhiteSpace(cron);

        if (hasInterval == hasCron)
            throw new BpmnExecutionException(
                $"BPMN timer start event '{element.ElementId}' must declare exactly one of '{BpmnEventDefinitionProperties.Interval}' or '{BpmnEventDefinitionProperties.Cron}'.");

        return hasInterval
            ? new BpmnTimerStartSchedule(BpmnTimerStartScheduleKind.Interval, interval!.Trim())
            : new BpmnTimerStartSchedule(BpmnTimerStartScheduleKind.Cron,
                string.Join(' ', cron!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }
}
