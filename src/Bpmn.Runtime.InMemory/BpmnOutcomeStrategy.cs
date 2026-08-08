namespace Bpmn.Runtime.InMemory;

/// <summary>
/// A decision point a simulation reached: a unit of work is waiting, and something has to say what it reports.
/// </summary>
/// <param name="ElementId">The element whose work is waiting.</param>
/// <param name="BindingRef">The binding that was started.</param>
/// <param name="IterationId">The multi-instance iteration, or <c>null</c>.</param>
/// <param name="Candidates">
/// The outcome names that would actually change the routing: the <c>ConditionOutcome</c> of the element's
/// non-default outbound flows, in declaration order. Empty when the element routes unconditionally, in which
/// case the outcome names do not matter.
/// </param>
/// <param name="DecisionIndex">The zero-based position of this decision within the run, which is what makes a run reproducible.</param>
/// <param name="ScopeInstanceId">The scope the work belongs to.</param>
public sealed record BpmnOutcomeChoice(
    string ElementId,
    string BindingRef,
    string? IterationId,
    IReadOnlyList<string> Candidates,
    int DecisionIndex,
    string ScopeInstanceId);

/// <summary>
/// Decides what a waiting unit of work reports, so a simulation can drive a process without a human at every
/// gateway.
/// <para>
/// Returning <c>null</c> means "I will not decide this one", which stops the run rather than guessing. There is
/// deliberately no randomized implementation anywhere in this package: a simulation whose result depends on a
/// seed is not evidence about the model.
/// </para>
/// </summary>
public interface IBpmnOutcomeStrategy
{
    /// <summary>The outcome names the work should report, or <c>null</c> to stop the run at this decision.</summary>
    IReadOnlyCollection<string>? SelectOutcomes(BpmnOutcomeChoice choice);
}

/// <summary>The built-in outcome strategies. All deterministic; none of them uses randomness.</summary>
public static class BpmnOutcomeStrategy
{
    /// <summary>Takes the first candidate outcome, in declaration order, and no outcome where there is no choice.</summary>
    public static IBpmnOutcomeStrategy AlwaysFirst { get; } =
        FromDelegate(choice => choice.Candidates.Count == 0 ? [] : [choice.Candidates[0]]);

    /// <summary>Takes the last candidate outcome, in declaration order.</summary>
    public static IBpmnOutcomeStrategy AlwaysLast { get; } =
        FromDelegate(choice => choice.Candidates.Count == 0 ? [] : [choice.Candidates[^1]]);

    /// <summary>
    /// Prefers the first of <paramref name="preferred"/> that the decision point actually offers, and falls
    /// back to the first candidate. Naming outcomes a process never offers is not an error: it lets one
    /// scenario ("approve everything") drive several definitions.
    /// </summary>
    public static IBpmnOutcomeStrategy ByName(params string[] preferred)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        var wanted = preferred.ToArray();

        return FromDelegate(choice =>
        {
            if (choice.Candidates.Count == 0)
                return [];

            var match = wanted.FirstOrDefault(name => choice.Candidates.Contains(name, StringComparer.Ordinal));
            return [match ?? choice.Candidates[0]];
        });
    }

    /// <summary>
    /// Takes the outcome named for each element id, falling back to the first candidate for an element the map
    /// does not mention.
    /// </summary>
    public static IBpmnOutcomeStrategy ByElement(IReadOnlyDictionary<string, string> outcomeByElementId)
    {
        ArgumentNullException.ThrowIfNull(outcomeByElementId);

        return FromDelegate(choice =>
            outcomeByElementId.TryGetValue(choice.ElementId, out var outcome)
                ? [outcome]
                : choice.Candidates.Count == 0
                    ? []
                    : [choice.Candidates[0]]);
    }

    /// <summary>
    /// Follows a fixed vector of candidate indices — decision 0 takes <c>choices[0]</c>, and so on — falling
    /// back to the first candidate once the vector runs out. This is how the deadlock search enumerates paths,
    /// and it is how a specific path is replayed exactly.
    /// </summary>
    public static IBpmnOutcomeStrategy ByIndex(IReadOnlyList<int> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var vector = choices.ToArray();

        return FromDelegate(choice =>
        {
            if (choice.Candidates.Count == 0)
                return [];

            var index = choice.DecisionIndex < vector.Length ? vector[choice.DecisionIndex] : 0;
            return [choice.Candidates[Math.Clamp(index, 0, choice.Candidates.Count - 1)]];
        });
    }

    /// <summary>Wraps a delegate as a strategy.</summary>
    public static IBpmnOutcomeStrategy FromDelegate(Func<BpmnOutcomeChoice, IReadOnlyCollection<string>?> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        return new DelegateStrategy(select);
    }

    private sealed class DelegateStrategy(Func<BpmnOutcomeChoice, IReadOnlyCollection<string>?> select) : IBpmnOutcomeStrategy
    {
        public IReadOnlyCollection<string>? SelectOutcomes(BpmnOutcomeChoice choice) => select(choice);
    }
}
