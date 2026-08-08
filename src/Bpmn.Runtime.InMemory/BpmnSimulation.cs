using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Semantics;

namespace Bpmn.Runtime.InMemory;

/// <summary>Why a simulated run stopped.</summary>
public enum BpmnSimulationStop
{
    /// <summary>The process completed.</summary>
    Completed,

    /// <summary>The process faulted deterministically.</summary>
    Faulted,

    /// <summary>
    /// Nothing can move it: no work is waiting, no timer is due, and the process has not finished. Tokens are
    /// stranded — typically at a join that can never receive its missing arrival.
    /// </summary>
    Deadlocked,

    /// <summary>The strategy declined to decide a waiting unit of work, so the run stopped rather than guessing.</summary>
    Undecided,

    /// <summary>The run hit its step ceiling, which usually means the definition loops.</summary>
    StepLimit
}

/// <summary>How a simulated run should be set up and driven.</summary>
public sealed class BpmnSimulationOptions
{
    /// <summary>What the simulating host claims it can do.</summary>
    public BpmnHostCapabilities Capabilities { get; set; } = BpmnHostCapabilities.Full;

    /// <summary>Durations for timer work, keyed by binding ref.</summary>
    public IReadOnlyDictionary<string, TimeSpan>? TimerDurations { get; set; }

    /// <summary>A duration resolver for timer work.</summary>
    public Func<string, TimeSpan?>? TimerDuration { get; set; }

    /// <summary>Nested process definitions keyed by binding ref.</summary>
    public IReadOnlyDictionary<string, BpmnProcessDefinition>? NestedProcesses { get; set; }

    /// <summary>The variables the root scope starts with. Copied, not shared.</summary>
    public InMemoryVariables? Variables { get; set; }

    /// <summary>The start event an external trigger names, or <c>null</c> for direct invocation.</summary>
    public BpmnStartSignal? StartSignal { get; set; }

    /// <summary>
    /// Whether the run completes armed catchers — boundary event listeners and event-subprocess scope
    /// listeners. Off by default: firing every boundary event the moment it arms answers a question nobody
    /// asked. Timers are never driven this way; they are advanced by the clock.
    /// </summary>
    public bool DriveCatchListeners { get; set; }

    /// <summary>A ceiling on completions and clock advances in one run, so a looping definition stops.</summary>
    public int MaxSteps { get; set; } = 500;

    /// <summary>A ceiling on how many distinct paths <see cref="BpmnSimulation.FindDeadlocks"/> explores.</summary>
    public int MaxRuns { get; set; } = 128;
}

/// <summary>What one simulated run did.</summary>
/// <param name="Completed">Whether the process reached a <see cref="BpmnContinuation.Complete"/>.</param>
/// <param name="Outcome">The completion outcome, or <c>null</c>.</param>
/// <param name="Continuation">The root scope's final continuation.</param>
/// <param name="Path">
/// The elements tokens arrived at, in the order they arrived — the path taken. A parallel split contributes
/// each of its branches.
/// </param>
/// <param name="ChoicePath">The outcome each decision point reported, in decision order.</param>
/// <param name="Transcript">Every evaluation from every scope, interleaved in the order they happened.</param>
/// <param name="StuckElementIds">Where the still-live tokens sit, when the run did not complete.</param>
/// <param name="Stop">Why the run stopped.</param>
/// <param name="StopDetail">A human-readable elaboration, when there is one.</param>
/// <param name="Steps">How many completions and clock advances the run performed.</param>
/// <param name="ElapsedVirtualTime">How far the virtual clock moved.</param>
public sealed record BpmnSimulationResult(
    bool Completed,
    string? Outcome,
    BpmnContinuation Continuation,
    IReadOnlyList<string> Path,
    IReadOnlyList<string> ChoicePath,
    IReadOnlyList<BpmnTranscriptEntry> Transcript,
    IReadOnlyList<string> StuckElementIds,
    BpmnSimulationStop Stop,
    string? StopDetail,
    int Steps,
    TimeSpan ElapsedVirtualTime)
{
    /// <summary>Whether this run ended somewhere nothing could move it.</summary>
    public bool IsDeadlocked =>
        Stop == BpmnSimulationStop.Deadlocked
        || (Continuation is BpmnContinuation.Fault fault
            && StringComparer.Ordinal.Equals(fault.Code, BpmnSimulation.JoinDeadlockFaultCode));

    /// <inheritdoc />
    public override string ToString() =>
        $"{Stop}{(Outcome is null ? string.Empty : $" ({Outcome})")} after {Steps} step(s), {ElapsedVirtualTime} of virtual time"
        + $"{Environment.NewLine}path: {(Path.Count == 0 ? "(none)" : string.Join(" -> ", Path))}"
        + (StopDetail is null ? string.Empty : $"{Environment.NewLine}stopped because: {StopDetail}")
        + (StuckElementIds.Count == 0 ? string.Empty : $"{Environment.NewLine}stuck at: {string.Join(", ", StuckElementIds)}")
        + $"{Environment.NewLine}{string.Join(Environment.NewLine, Transcript)}";
}

/// <summary>A path through a definition that ends somewhere nothing can move it.</summary>
/// <param name="ChoicePath">The outcomes that led here, in decision order. Replay it with <see cref="BpmnOutcomeStrategy.ByName"/> or a fixed index vector.</param>
/// <param name="ChoiceIndices">The candidate indices that produced <paramref name="ChoicePath"/>, for exact replay through <see cref="BpmnOutcomeStrategy.ByIndex"/>.</param>
/// <param name="StuckElementIds">Where the stranded tokens sit.</param>
/// <param name="Run">The whole run, transcript included.</param>
public sealed record BpmnDeadlock(
    IReadOnlyList<string> ChoicePath,
    IReadOnlyList<int> ChoiceIndices,
    IReadOnlyList<string> StuckElementIds,
    BpmnSimulationResult Run)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"deadlock at [{string.Join(", ", StuckElementIds)}] via [{string.Join(" -> ", ChoicePath)}]";
}

/// <summary>
/// Answers questions about a definition by running it, rather than by reading it.
/// <para>
/// These live in the shipped package rather than in a test project on purpose. "Can this process ever
/// complete?" and "is there a path that strands a token?" are questions about a model, asked by the people who
/// author models — and answering them needs a host, a clock and a way to resolve decision points, all of which
/// are here already. Everything is deterministic: the same definition and the same strategy always produce the
/// same answer.
/// </para>
/// </summary>
public static class BpmnSimulation
{
    /// <summary>The interpreter's fault code for a join that can never receive its missing arrival.</summary>
    public const string JoinDeadlockFaultCode = "bpmn.join.deadlock";

    /// <summary>
    /// Drives <paramref name="definition"/> to completion, resolving each decision point with
    /// <paramref name="strategy"/> and advancing the clock whenever only timers remain, and reports whether it
    /// completed and the path it took.
    /// </summary>
    public static BpmnSimulationResult CanComplete(
        BpmnProcessDefinition definition,
        IReadOnlyCollection<BpmnBoundWork>? boundWork = null,
        IBpmnOutcomeStrategy? strategy = null,
        BpmnSimulationOptions? options = null) =>
        Drive(definition, boundWork, strategy ?? BpmnOutcomeStrategy.AlwaysFirst, options ?? new BpmnSimulationOptions()).Result;

    /// <summary>
    /// Explores the definition's decision points and reports every path that ends in a state where work is not
    /// pending, no timer is due, and nothing can make progress.
    /// <para>
    /// The search is a breadth-first walk of the decision tree: run with every decision taking its first
    /// candidate, then re-run branching at each decision that had more than one, and so on, bounded by
    /// <see cref="BpmnSimulationOptions.MaxRuns"/>. It is exhaustive for definitions whose decision tree fits
    /// in that budget and a sampling of it otherwise, so an empty result is evidence, not proof.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BpmnDeadlock> FindDeadlocks(
        BpmnProcessDefinition definition,
        IReadOnlyCollection<BpmnBoundWork>? boundWork = null,
        BpmnSimulationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        options ??= new BpmnSimulationOptions();

        var deadlocks = new List<BpmnDeadlock>();
        var explored = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<int[]>();
        frontier.Enqueue([]);

        var runs = 0;
        while (frontier.Count > 0 && runs < options.MaxRuns)
        {
            var prefix = frontier.Dequeue();
            if (!explored.Add(Key(prefix)))
                continue;

            runs++;
            var (result, candidateCounts, chosenIndices) = Drive(
                definition, boundWork, BpmnOutcomeStrategy.ByIndex(prefix), options);

            if (result.IsDeadlocked)
                deadlocks.Add(new BpmnDeadlock(result.ChoicePath, chosenIndices, result.StuckElementIds, result));

            // Branch only at decisions the prefix did not already fix, so each path is enumerated once.
            for (var decision = prefix.Length; decision < candidateCounts.Count; decision++)
            {
                for (var alternative = 1; alternative < candidateCounts[decision]; alternative++)
                {
                    var branch = new int[decision + 1];
                    Array.Copy(chosenIndices.ToArray(), branch, decision);
                    branch[decision] = alternative;
                    frontier.Enqueue(branch);
                }
            }
        }

        return deadlocks;

        static string Key(IReadOnlyList<int> prefix) => string.Join(",", prefix);
    }

    private static (BpmnSimulationResult Result, IReadOnlyList<int> CandidateCounts, IReadOnlyList<int> ChosenIndices) Drive(
        BpmnProcessDefinition definition,
        IReadOnlyCollection<BpmnBoundWork>? boundWork,
        IBpmnOutcomeStrategy strategy,
        BpmnSimulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(strategy);

        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            Capabilities = options.Capabilities,
            TimerDurations = options.TimerDurations,
            TimerDuration = options.TimerDuration,
            NestedProcesses = options.NestedProcesses
        });

        var root = host.Start(definition, boundWork, options.Variables?.Clone(), options.StartSignal);

        var candidateCounts = new List<int>();
        var chosenIndices = new List<int>();
        var choicePath = new List<string>();
        var steps = 0;
        var decisionIndex = 0;
        string? stopDetail = null;
        var stop = BpmnSimulationStop.Completed;

        while (!root.IsFinished)
        {
            if (steps >= options.MaxSteps)
            {
                stop = BpmnSimulationStop.StepLimit;
                stopDetail = $"stopped after {steps} steps without finishing.";
                break;
            }

            if (NextDrivable(host, options) is { } next)
            {
                steps++;
                var candidates = Candidates(next.Owner.Graph, next.Work.ElementId);
                candidateCounts.Add(candidates.Count);

                var outcomes = strategy.SelectOutcomes(new BpmnOutcomeChoice(
                    next.Work.ElementId, next.Work.BindingRef, next.Work.IterationId, candidates, decisionIndex++, next.Work.ScopeInstanceId));

                if (outcomes is null)
                {
                    stop = BpmnSimulationStop.Undecided;
                    stopDetail = $"the strategy declined to decide '{next.Work.ElementId}' ({next.Work.BindingRef}).";
                    break;
                }

                var chosen = outcomes.ToArray();
                chosenIndices.Add(chosen.Length == 0 ? 0 : Math.Max(0, IndexOf(candidates, chosen[0])));
                choicePath.Add(chosen.Length == 0 ? $"{next.Work.ElementId}" : $"{next.Work.ElementId}={string.Join("+", chosen)}");

                next.Owner.CompleteWork(next.Work, chosen);
                continue;
            }

            if (host.Clock.AdvanceToNextTimer())
            {
                steps++;
                continue;
            }

            stop = BpmnSimulationStop.Deadlocked;
            stopDetail = "no work is waiting, no timer is due, and the process has not finished.";
            break;
        }

        if (root.IsCompleted)
            stop = BpmnSimulationStop.Completed;
        else if (root.IsFaulted)
        {
            stop = BpmnSimulationStop.Faulted;
            stopDetail = $"{root.Fault!.Code}: {root.Fault.Message}";
        }

        var transcript = host.Instances
            .SelectMany(instance => instance.Transcript)
            .OrderBy(entry => entry.Sequence)
            .ToArray();

        var result = new BpmnSimulationResult(
            root.IsCompleted,
            root.Outcome,
            root.Continuation,
            transcript.SelectMany(entry => entry.TokenMoves.Where(move => move.From is null).Select(move => move.ElementId)).ToArray(),
            choicePath,
            transcript,
            StuckElements(host),
            stop,
            stopDetail,
            steps,
            host.Clock.Now);

        return (result, candidateCounts, chosenIndices);
    }

    /// <summary>
    /// The next unit of work a run should finish: the first live one that is neither a timer (the clock owns
    /// those), nor a nested process (its own scope owns it), nor — by default — an armed catcher.
    /// </summary>
    private static (InMemoryProcessInstance Owner, InMemoryWorkItem Work)? NextDrivable(
        InMemoryBpmnHost host,
        BpmnSimulationOptions options)
    {
        foreach (var instance in host.Instances)
        {
            if (instance.IsFinished)
                continue;

            foreach (var work in instance.PendingWork)
            {
                if (work.Kind != InMemoryWorkKind.Activity)
                    continue;

                if (work.IsCatchListener && !options.DriveCatchListeners)
                    continue;

                return (instance, work);
            }
        }

        return null;
    }

    /// <summary>The outcome names that change where a token goes when this element's work completes.</summary>
    private static IReadOnlyList<string> Candidates(BpmnGraph graph, string elementId) =>
        graph.OutboundFlows(elementId)
            .Where(flow => !flow.IsDefault && flow.ConditionOutcome is not null)
            .Select(flow => flow.ConditionOutcome!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static int IndexOf(IReadOnlyList<string> candidates, string outcome)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(candidates[index], outcome))
                return index;
        }

        return 0;
    }

    private static IReadOnlyList<string> StuckElements(InMemoryBpmnHost host) =>
        host.Instances
            .Where(instance => !instance.IsCompleted)
            .SelectMany(instance => instance.State.Tokens)
            .Where(token => token.Status is BpmnTokenStatus.Active or BpmnTokenStatus.AwaitingChild or BpmnTokenStatus.WaitingAtJoin)
            .Select(token => token.AtElementId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(elementId => elementId, StringComparer.Ordinal)
            .ToArray();
}
