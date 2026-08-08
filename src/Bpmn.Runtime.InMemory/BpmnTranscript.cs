using System.Collections;
using System.Text;
using Bpmn.Model.State;
using Bpmn.Semantics;

namespace Bpmn.Runtime.InMemory;

/// <summary>What provoked one interpreter evaluation.</summary>
public abstract record BpmnTranscriptTrigger
{
    private BpmnTranscriptTrigger()
    {
    }

    /// <summary>A short, readable form used by the transcript's own <c>ToString</c>.</summary>
    public abstract string Describe();

    /// <inheritdoc />
    public override string ToString() => Describe();

    /// <summary>The scope was started.</summary>
    /// <param name="StartElementId">The start event an external trigger named, or <c>null</c> for direct invocation.</param>
    public sealed record Started(string? StartElementId) : BpmnTranscriptTrigger
    {
        /// <inheritdoc />
        public override string Describe() =>
            StartElementId is null ? "started" : $"started at {StartElementId}";
    }

    /// <summary>A unit of work reported completion.</summary>
    /// <param name="Work">The work that completed.</param>
    /// <param name="OutcomeNames">The outcome names it reported.</param>
    public sealed record WorkCompleted(InMemoryWorkItem Work, IReadOnlyCollection<string> OutcomeNames) : BpmnTranscriptTrigger
    {
        /// <inheritdoc />
        public override string Describe() =>
            $"completed {Work.ElementId} ({Work.BindingRef}){Iteration(Work)}{Outcomes(OutcomeNames)}";
    }

    /// <summary>A timer came due, which completes the work it was scheduled for.</summary>
    /// <param name="Work">The timer work that fired.</param>
    public sealed record TimerFired(InMemoryWorkItem Work) : BpmnTranscriptTrigger
    {
        /// <inheritdoc />
        public override string Describe() => $"timer fired {Work.ElementId} ({Work.BindingRef}){Iteration(Work)}";
    }

    /// <summary>A unit of work failed terminally.</summary>
    /// <param name="Work">The work that failed.</param>
    /// <param name="Message">The human-readable failure description the host reported.</param>
    public sealed record WorkFaulted(InMemoryWorkItem Work, string? Message) : BpmnTranscriptTrigger
    {
        /// <inheritdoc />
        public override string Describe() =>
            $"faulted {Work.ElementId} ({Work.BindingRef}){Iteration(Work)}: {Message ?? "(no message)"}";
    }

    /// <summary>A nested scope signalled outward, and this scope is where it landed.</summary>
    /// <param name="Work">The work that raised the signal; it keeps running.</param>
    /// <param name="Code">The signal code.</param>
    public sealed record WorkSignalled(InMemoryWorkItem Work, string Code) : BpmnTranscriptTrigger
    {
        /// <inheritdoc />
        public override string Describe() => $"signalled {Work.ElementId} ({Work.BindingRef}) code={Code}";
    }

    private static string Iteration(InMemoryWorkItem work) =>
        work.IterationId is null ? string.Empty : $" [{work.IterationId}]";

    private static string Outcomes(IReadOnlyCollection<string> outcomeNames) =>
        outcomeNames.Count == 0 ? string.Empty : $" outcomes=[{string.Join(", ", outcomeNames)}]";
}

/// <summary>One token's change of position or status during a single evaluation.</summary>
/// <param name="TokenId">The token.</param>
/// <param name="ElementId">Where it sits after the evaluation.</param>
/// <param name="From">Its previous status, or <c>null</c> when the evaluation minted it.</param>
/// <param name="To">Its status after the evaluation.</param>
public sealed record BpmnTokenMove(string TokenId, string ElementId, BpmnTokenStatus? From, BpmnTokenStatus To)
{
    /// <inheritdoc />
    public override string ToString() =>
        From is null
            ? $"{TokenId} minted {To} @{ElementId}"
            : $"{TokenId} {From}->{To} @{ElementId}";
}

/// <summary>
/// One evaluation, recorded. An entry says what provoked the interpreter, what it decided, what it asked the
/// host to do, which tokens moved, and — for a fault — whether BPMN caught the error or let it out.
/// <para>
/// This is the artefact that makes the interpreter's behavior visible. A failing test prints the transcript
/// and the reason is usually in it; the alternative is inferring token flow from a final state, which is
/// guesswork.
/// </para>
/// </summary>
/// <param name="Index">The one-based position of this evaluation within its scope.</param>
/// <param name="ScopeInstanceId">The scope that was evaluated.</param>
/// <param name="At">The virtual instant of the evaluation.</param>
/// <param name="Trigger">What provoked it.</param>
/// <param name="Continuation">What the interpreter decided the scope should do next.</param>
/// <param name="Commands">What the interpreter asked the host to do, in the order it must be done.</param>
/// <param name="TokenMoves">The tokens minted or moved by this evaluation.</param>
/// <param name="Disposition">For a fault, whether a BPMN catcher took the error or it propagated; otherwise <c>null</c>.</param>
public sealed record BpmnTranscriptEntry(
    int Index,
    string ScopeInstanceId,
    TimeSpan At,
    BpmnTranscriptTrigger Trigger,
    BpmnContinuation Continuation,
    IReadOnlyList<BpmnHostCommand> Commands,
    IReadOnlyList<BpmnTokenMove> TokenMoves,
    BpmnErrorDisposition? Disposition)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append($"#{Index} t={At} [{ScopeInstanceId}] {Trigger.Describe()} -> {Describe(Continuation)}");

        if (Disposition is not null)
            text.Append($" ({Describe(Disposition)})");

        if (Commands.Count > 0)
            text.Append($"{Environment.NewLine}    commands: {string.Join("; ", Commands.Select(Describe))}");

        if (TokenMoves.Count > 0)
            text.Append($"{Environment.NewLine}    tokens:   {string.Join("; ", TokenMoves)}");

        return text.ToString();
    }

    /// <summary>A one-line description of a continuation.</summary>
    public static string Describe(BpmnContinuation continuation) => continuation switch
    {
        BpmnContinuation.Complete complete => $"Complete({complete.Outcome})",
        BpmnContinuation.Defer => "Defer",
        BpmnContinuation.Fault fault => $"Fault({fault.Code}: {fault.Message})",
        _ => continuation.ToString()
    };

    /// <summary>A one-line description of an error disposition.</summary>
    public static string Describe(BpmnErrorDisposition disposition) => disposition switch
    {
        BpmnErrorDisposition.Caught caught => $"Caught by {caught.CatchingElementId} [{caught.ErrorCode ?? "any error"}]",
        BpmnErrorDisposition.Propagated => "Propagated",
        _ => disposition.ToString()
    };

    /// <summary>A one-line description of a host command.</summary>
    public static string Describe(BpmnHostCommand command) => command switch
    {
        BpmnHostCommand.StartWork start =>
            $"StartWork {start.BindingRef} @{start.ElementId} token={start.TokenId} cause={start.SchedulingCause}"
            + (start.IterationScope is { } scope ? $" iteration={scope.IterationId}" : string.Empty)
            + (start.StartElementHint is { } hint ? $" startAt={hint}" : string.Empty),
        BpmnHostCommand.CancelWorkSubtree cancel =>
            $"CancelWorkSubtree {cancel.Handle} @{cancel.ElementId} ({cancel.Reason})",
        BpmnHostCommand.SignalEnclosingScope signal =>
            $"SignalEnclosingScope {signal.Code}",
        _ => command.ToString()
    };
}

/// <summary>
/// A scope's evaluations, in order. Printable as a whole: <c>Console.WriteLine(instance.Transcript)</c> is the
/// intended way to find out what a process did.
/// </summary>
public sealed class BpmnTranscript : IReadOnlyList<BpmnTranscriptEntry>
{
    private readonly List<BpmnTranscriptEntry> _entries = [];

    /// <inheritdoc />
    public int Count => _entries.Count;

    /// <inheritdoc />
    public BpmnTranscriptEntry this[int index] => _entries[index];

    /// <inheritdoc />
    public IEnumerator<BpmnTranscriptEntry> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Add(BpmnTranscriptEntry entry) => _entries.Add(entry);

    /// <inheritdoc />
    public override string ToString() =>
        _entries.Count == 0
            ? "(nothing evaluated)"
            : string.Join(Environment.NewLine, _entries.Select(entry => entry.ToString()));
}
