namespace Bpmn.Runtime.InMemory;

/// <summary>
/// A timer the host has scheduled and the clock has not yet fired.
/// </summary>
/// <param name="Handle">The work handle the timer will complete when it fires.</param>
/// <param name="BindingRef">The binding whose work the timer runs.</param>
/// <param name="ElementId">The element the work belongs to, for reporting.</param>
/// <param name="DueAt">The virtual instant the timer is due at, measured from the clock's zero.</param>
/// <param name="Sequence">
/// A monotonically increasing registration number. It is the tie-break for timers due at the same instant, so
/// two timers scheduled for the same moment always fire in registration order — the same order every run.
/// </param>
public sealed record PendingTimer(string Handle, string BindingRef, string ElementId, TimeSpan DueAt, long Sequence)
{
    /// <inheritdoc />
    public override string ToString() => $"{ElementId} ({BindingRef}) due at {DueAt}";
}

/// <summary>
/// The simulated clock a <see cref="InMemoryBpmnHost"/> runs on. It has no relationship with wall-clock time:
/// <see cref="Now"/> starts at <see cref="TimeSpan.Zero"/> and moves only when a caller moves it.
/// <para>
/// Nothing here waits. A seven-day timer costs the same as a one-second timer, because "seven days pass" is a
/// single call that walks the schedule rather than a delay that elapses. That is what makes a test for "the
/// customer never replied for a month" as cheap as any other test, and it is why the same inputs always produce
/// the same transcript: there is no scheduler, no thread, and no jitter to reorder anything.
/// </para>
/// </summary>
public sealed class VirtualClock
{
    private readonly List<PendingTimer> _pending = [];
    private long _sequence;

    /// <summary>
    /// The host's dispatcher for a fired timer. Set once by the owning host; the clock itself knows nothing
    /// about processes, work or interpreters.
    /// </summary>
    internal Action<PendingTimer>? Fired { get; set; }

    /// <summary>The current virtual instant, measured from the clock's zero. Starts at <see cref="TimeSpan.Zero"/>.</summary>
    public TimeSpan Now { get; private set; }

    /// <summary>The timers that have been scheduled and not yet fired or cancelled, in due order.</summary>
    public IReadOnlyCollection<PendingTimer> Pending =>
        _pending.OrderBy(timer => timer.DueAt).ThenBy(timer => timer.Sequence).ToArray();

    /// <summary>The next timer due, or <c>null</c> when nothing is scheduled.</summary>
    public PendingTimer? Next => _pending.Count == 0
        ? null
        : _pending.Aggregate((best, candidate) =>
            candidate.DueAt < best.DueAt || (candidate.DueAt == best.DueAt && candidate.Sequence < best.Sequence)
                ? candidate
                : best);

    /// <summary>
    /// Moves the clock forward by <paramref name="by"/>, firing every timer that comes due on the way in due
    /// order. A timer scheduled by one of those firings is itself fired if it lands at or before the new
    /// <see cref="Now"/>.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(by), by, "A virtual clock only moves forward.");

        AdvanceTo(Now + by);
    }

    /// <summary>
    /// Moves the clock to <paramref name="instant"/>, firing every timer due at or before it in due order.
    /// </summary>
    public void AdvanceTo(TimeSpan instant)
    {
        if (instant < Now)
            throw new ArgumentOutOfRangeException(nameof(instant), instant, $"A virtual clock only moves forward; it is already at {Now}.");

        while (Next is { } next && next.DueAt <= instant)
        {
            // Fire at the timer's own instant, so work the firing schedules is relative to when it actually
            // happened rather than to where the caller was heading.
            Now = next.DueAt > Now ? next.DueAt : Now;
            _pending.Remove(next);
            Fired?.Invoke(next);
        }

        Now = instant;
    }

    /// <summary>
    /// Jumps exactly to the next due timer and fires it (together with anything else due at that same instant).
    /// Returns <c>false</c>, leaving <see cref="Now"/> untouched, when no timer is pending.
    /// </summary>
    public bool AdvanceToNextTimer()
    {
        if (Next is not { } next)
            return false;

        AdvanceTo(next.DueAt > Now ? next.DueAt : Now);
        return true;
    }

    /// <summary>Schedules a timer for <paramref name="dueAt"/>. Called by the host when the interpreter starts timer work.</summary>
    internal PendingTimer Schedule(string handle, string bindingRef, string elementId, TimeSpan dueAt)
    {
        var timer = new PendingTimer(handle, bindingRef, elementId, dueAt, _sequence++);
        _pending.Add(timer);
        return timer;
    }

    /// <summary>Drops the timer registered for <paramref name="handle"/>, if any. Idempotent.</summary>
    internal void Cancel(string handle)
    {
        for (var index = _pending.Count - 1; index >= 0; index--)
        {
            if (StringComparer.Ordinal.Equals(_pending[index].Handle, handle))
                _pending.RemoveAt(index);
        }
    }
}
