using System.Globalization;
using Bpmn.Model;

namespace Bpmn.Semantics;

/// <summary>
/// The rules a timer definition's <see cref="BpmnEventDefinitionProperties.Cycle"/> follows at every position: the
/// cycle is the source of truth, so it must parse, it cannot sit beside a <see cref="BpmnEventDefinitionProperties.Date"/>,
/// and the parts written beside it must agree with it. A definition built in code may leave the parts out; they are
/// then taken from the cycle.
/// </summary>
internal static class BpmnTimerCycleRules
{
    /// <summary>
    /// The parsed cycle of a timer definition, or <c>null</c> when it declares none. Throws
    /// <see cref="BpmnExecutionException"/>, naming <paramref name="elementIds"/>, for a cycle that does not parse, one
    /// beside a date, a part that disagrees with it, or <c>repetitions</c> or <c>start</c> without a cycle.
    /// </summary>
    public static BpmnTimerCycle? Resolve(IReadOnlyDictionary<string, string> properties, string description, IReadOnlyList<string> elementIds)
    {
        if (!HasNonBlank(properties, BpmnEventDefinitionProperties.Cycle))
        {
            if (HasNonBlank(properties, BpmnEventDefinitionProperties.Repetitions) || HasNonBlank(properties, BpmnEventDefinitionProperties.Start))
                throw new BpmnExecutionException($"{description} declares '{BpmnEventDefinitionProperties.Repetitions}' or '{BpmnEventDefinitionProperties.Start}' without a '{BpmnEventDefinitionProperties.Cycle}'; they are parts of a cycle.") { ElementIds = elementIds };
            return null;
        }

        var text = properties[BpmnEventDefinitionProperties.Cycle];
        if (!BpmnTimerCycle.TryParse(text, out var cycle))
            throw new BpmnExecutionException($"{description} declares the cycle '{text.Trim()}', which is not an ISO-8601 repeating interval R[n]/[start/]duration, a duration, or a cron expression.") { ElementIds = elementIds };
        if (HasNonBlank(properties, BpmnEventDefinitionProperties.Date))
            throw new BpmnExecutionException($"{description} declares both a cycle and a date; a timer declares exactly one.") { ElementIds = elementIds };

        Agree(properties, BpmnEventDefinitionProperties.Interval, cycle.IsoDuration, description, elementIds);
        Agree(properties, BpmnEventDefinitionProperties.Cron, cycle.Cron, description, elementIds);
        Agree(properties, BpmnEventDefinitionProperties.Repetitions, cycle.Repetitions?.ToString(CultureInfo.InvariantCulture), description, elementIds);
        Agree(properties, BpmnEventDefinitionProperties.Start, cycle.Start, description, elementIds);
        return cycle;
    }

    private static void Agree(IReadOnlyDictionary<string, string> properties, string key, string? fromCycle, string description, IReadOnlyList<string> elementIds)
    {
        if (!properties.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return;
        // A cron expression's fields are compared, not the spaces between them.
        var agrees = key == BpmnEventDefinitionProperties.Cron
            ? fromCycle is not null && StringComparer.Ordinal.Equals(CollapseWhitespace(value), CollapseWhitespace(fromCycle))
            : StringComparer.Ordinal.Equals(value.Trim(), fromCycle);
        if (!agrees)
            throw new BpmnExecutionException($"{description} declares '{key}' as '{value.Trim()}', which disagrees with its cycle '{properties[BpmnEventDefinitionProperties.Cycle].Trim()}'.") { ElementIds = elementIds };
    }

    internal static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool HasNonBlank(IReadOnlyDictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
}
