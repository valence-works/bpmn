using System.Text.Json;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Semantics;

namespace Bpmn.Runtime.InMemory;

/// <summary>
/// One running BPMN process scope on an <see cref="InMemoryBpmnHost"/> — a root process, or a nested one the
/// host started because a subprocess element's bound work carried a definition.
/// <para>
/// Everything the interpreter's port asks of a host is here: the execution state, the live work, the variable
/// reader, the subtree-cancellation bookkeeping, and the routing of a scope signal to the scope that invoked
/// this one. None of it is durable and none of it is thread-safe.
/// </para>
/// </summary>
public sealed class InMemoryProcessInstance
{
    private readonly InMemoryBpmnHost _host;
    private readonly List<InMemoryWorkItem> _live = [];
    private readonly List<InMemoryCancelledWork> _cancelled = [];
    private readonly List<InMemoryScopeSignal> _signals = [];
    private readonly List<InMemoryProcessInstance> _children = [];
    private readonly Dictionary<string, InMemoryProcessInstance> _childScopesByHandle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BpmnTokenStatus> _tokenStatuses = new(StringComparer.Ordinal);
    private readonly InMemoryProcessInstance? _parent;
    private readonly string? _parentHandle;
    private readonly IReadOnlyDictionary<string, string> _invocationCorrelation;

    /// <summary>The teardown reason this host records for work a finishing scope walks away from.</summary>
    public const string ScopeFinishedReason = "bpmn.host.scope-finished";

    private BpmnExecutionState _state = new();

    internal InMemoryProcessInstance(
        InMemoryBpmnHost host,
        string scopeInstanceId,
        BpmnGraph graph,
        InMemoryProcessInstance? parent,
        string? parentHandle,
        IReadOnlyDictionary<string, string> invocationCorrelation,
        InMemoryVariables variables)
    {
        _host = host;
        _parent = parent;
        _parentHandle = parentHandle;
        _invocationCorrelation = invocationCorrelation;

        ScopeInstanceId = scopeInstanceId;
        Graph = graph;
        Variables = variables;

        parent?._children.Add(this);
    }

    /// <summary>The host-chosen identity of this scope. The interpreter echoes it onto tokens and never parses it.</summary>
    public string ScopeInstanceId { get; }

    /// <summary>The graph this scope executes.</summary>
    public BpmnGraph Graph { get; }

    /// <summary>The scope's variables. Mutable: set a collection here before a multi-instance activity reaches it.</summary>
    public InMemoryVariables Variables { get; }

    /// <summary>The clock this scope's timers run on. Shared with every other scope on the same host.</summary>
    public VirtualClock Clock => _host.Clock;

    /// <summary>The current execution state, as any host would hold it. Pruned after every evaluation.</summary>
    public BpmnExecutionState State => _state;

    /// <summary>What the interpreter last decided this scope should do. <see cref="BpmnContinuation.Defer"/> until something terminal happens.</summary>
    public BpmnContinuation Continuation { get; private set; } = BpmnContinuation.Defer.Instance;

    /// <summary>Whether the scope finished, either by completing or by faulting.</summary>
    public bool IsCompleted => Continuation is BpmnContinuation.Complete;

    /// <summary>Whether the scope failed deterministically. <see cref="Fault"/> carries the detail.</summary>
    public bool IsFaulted => Continuation is BpmnContinuation.Fault;

    /// <summary>Whether an enclosing scope tore this scope down before it finished.</summary>
    public bool IsCancelled { get; private set; }

    /// <summary>Whether nothing more will happen here: completed, faulted, or cancelled from above.</summary>
    public bool IsFinished => IsCompleted || IsFaulted || IsCancelled;

    /// <summary>
    /// The outcome the scope completed with — <see cref="BpmnInterpreter.DoneOutcomeName"/>, or
    /// <see cref="BpmnInterpreter.CancelledOutcomeName"/> when a cancel end event cancelled a transaction —
    /// or <c>null</c> while it is still running or if it faulted.
    /// </summary>
    public string? Outcome => (Continuation as BpmnContinuation.Complete)?.Outcome;

    /// <summary>The fault that ended this scope, or <c>null</c>.</summary>
    public BpmnContinuation.Fault? Fault => Continuation as BpmnContinuation.Fault;

    /// <summary>The work this scope started and has not finished, in start order.</summary>
    public IReadOnlyList<InMemoryWorkItem> PendingWork => _live.ToArray();

    /// <summary>The work this scope tore down, in teardown order.</summary>
    public IReadOnlyList<InMemoryCancelledWork> CancelledWork => _cancelled;

    /// <summary>
    /// The scope signals raised from this scope. A nested scope's are also delivered to its parent; a root
    /// process has nowhere to deliver them, so they are recorded here as the observable outcome.
    /// </summary>
    public IReadOnlyList<InMemoryScopeSignal> ScopeSignals => _signals;

    /// <summary>The nested scopes this one started, in creation order.</summary>
    public IReadOnlyList<InMemoryProcessInstance> Children => _children;

    /// <summary>The scope that invoked this one, or <c>null</c> at a root process.</summary>
    public InMemoryProcessInstance? Parent => _parent;

    /// <summary>The handle the parent scope started this one under, or <c>null</c> at a root process.</summary>
    public string? ParentWorkHandle => _parentHandle;

    /// <summary>Every evaluation of this scope, in order. Print it to find out what happened.</summary>
    public BpmnTranscript Transcript { get; } = new();

    /// <summary>The nested scope started under <paramref name="handle"/>, or <c>null</c>.</summary>
    public InMemoryProcessInstance? ChildScope(string handle) =>
        _childScopesByHandle.TryGetValue(handle, out var child) ? child : null;

    // --- Driving from the outside --------------------------------------------------------------------

    /// <summary>
    /// Reports that a unit of work completed, naming the outcomes a conditional sequence flow selects on.
    /// <paramref name="handleOrBindingRef"/> is matched against live handles first and against live binding
    /// refs second, so both <c>CompleteWork("work-3")</c> and <c>CompleteWork("node-Approve")</c> work; a
    /// binding ref with several live instances (a parallel multi-instance activity) is ambiguous and throws,
    /// which is what the <see cref="InMemoryWorkItem"/> overload is for.
    /// </summary>
    public void CompleteWork(string handleOrBindingRef, params string[] outcomeNames) =>
        CompleteWork(ResolveWork(handleOrBindingRef), outcomeNames);

    /// <summary>Reports that a specific unit of work completed.</summary>
    public void CompleteWork(InMemoryWorkItem work, params string[] outcomeNames)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(outcomeNames);

        var outcomes = outcomeNames.ToArray();
        Post(() => RunWorkCompleted(work, outcomes, timerFired: false));
    }

    /// <summary>
    /// Reports that a unit of work failed terminally. Note what this does not carry: an error code. Which BPMN
    /// error a catcher matches is a property of the model, not of the report.
    /// </summary>
    public void FaultWork(string handleOrBindingRef, string? message = null) =>
        FaultWork(ResolveWork(handleOrBindingRef), message);

    /// <summary>Reports that a specific unit of work failed terminally.</summary>
    public void FaultWork(InMemoryWorkItem work, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        Post(() => RunWorkFaulted(work, message));
    }

    /// <summary>
    /// Delivers a signal from a running unit of work to this scope — the receiving half of
    /// <see cref="BpmnHostCommand.SignalEnclosingScope"/>. The work keeps running; a signal is not a completion.
    /// </summary>
    public void SignalWork(string handleOrBindingRef, string code, JsonElement? payload = null) =>
        SignalWork(ResolveWork(handleOrBindingRef), code, payload);

    /// <summary>Delivers a signal from a specific unit of running work to this scope.</summary>
    public void SignalWork(InMemoryWorkItem work, string code, JsonElement? payload = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Post(() => RunWorkSignalled(work, code, payload));
    }

    /// <summary>
    /// Raises a BPMN escalation from a running unit of work, which is the common case of
    /// <see cref="SignalWork(string, string, JsonElement?)"/>: it uses the interpreter's escalation code and
    /// wraps <paramref name="escalationCode"/> in the payload shape the interpreter reads.
    /// </summary>
    public void EscalateWork(string handleOrBindingRef, string escalationCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(escalationCode);

        using var payload = JsonDocument.Parse(
            JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = escalationCode }));

        SignalWork(handleOrBindingRef, BpmnInterpreter.EscalationSignalCode, payload.RootElement.Clone());
    }

    /// <summary>The live work with this handle, or <c>null</c>.</summary>
    public InMemoryWorkItem? FindWork(string handle) =>
        _live.FirstOrDefault(work => StringComparer.Ordinal.Equals(work.Handle, handle));

    /// <summary>
    /// The live work for <paramref name="handleOrBindingRef"/>. Throws when nothing matches, or when a binding
    /// ref names several concurrent instances.
    /// </summary>
    public InMemoryWorkItem ResolveWork(string handleOrBindingRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handleOrBindingRef);

        if (FindWork(handleOrBindingRef) is { } byHandle)
            return byHandle;

        var byBinding = _live
            .Where(work => StringComparer.Ordinal.Equals(work.BindingRef, handleOrBindingRef))
            .ToArray();

        return byBinding.Length switch
        {
            1 => byBinding[0],
            0 => throw new InvalidOperationException(
                $"BPMN scope '{ScopeInstanceId}' has no live work for handle or binding ref '{handleOrBindingRef}'. "
                + $"Live work: {(_live.Count == 0 ? "(none)" : string.Join(", ", _live.Select(work => $"{work.Handle} ({work.BindingRef})")))}."),
            _ => throw new InvalidOperationException(
                $"BPMN binding ref '{handleOrBindingRef}' has {byBinding.Length} concurrent live instances in scope '{ScopeInstanceId}'; "
                + "name one by its handle, or pass the work item itself.")
        };
    }

    // --- The interpreter's four entry points ---------------------------------------------------------

    internal void RunStart(BpmnStartSignal? startSignal) =>
        Evaluate(
            new BpmnTranscriptTrigger.Started(startSignal?.StartElementId),
            snapshot => _host.Interpreter.Start(new BpmnStartRequest(Graph, _state, snapshot, startSignal)));

    internal void RunTimerFired(string handle)
    {
        if (FindWork(handle) is { } work)
            RunWorkCompleted(work, [], timerFired: true);
    }

    internal void RunWorkCompleted(InMemoryWorkItem work, IReadOnlyCollection<string> outcomeNames, bool timerFired)
    {
        // A late completion for work that was torn down, or for a scope that already finished, is absorbed
        // rather than faulted. Both are ordinary races in BPMN: an interrupting boundary event tears its host
        // down while the host's work is still in flight.
        if (IsFinished || !Retire(work.Handle))
            return;

        BpmnTranscriptTrigger trigger = timerFired
            ? new BpmnTranscriptTrigger.TimerFired(work)
            : new BpmnTranscriptTrigger.WorkCompleted(work, outcomeNames);

        Evaluate(trigger, snapshot => _host.Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(
            Graph, _state, snapshot, work.BindingRef, work.Handle, outcomeNames, work.IterationId)));
    }

    internal void RunWorkFaulted(InMemoryWorkItem work, string? message)
    {
        if (IsFinished || !Retire(work.Handle))
            return;

        Evaluate(new BpmnTranscriptTrigger.WorkFaulted(work, message), snapshot =>
            _host.Interpreter.OnWorkFaulted(new BpmnWorkFaultedRequest(
                Graph, _state, snapshot, work.BindingRef, work.Handle, message)));
    }

    internal void RunWorkSignalled(InMemoryWorkItem work, string code, JsonElement? payload)
    {
        // Unlike a completion, a signal leaves the work running — the signalling scope carries on after
        // escalating — so the live-work record stays exactly where it is.
        if (IsFinished)
            return;

        Evaluate(new BpmnTranscriptTrigger.WorkSignalled(work, code), snapshot =>
            _host.Interpreter.OnWorkSignalled(new BpmnWorkSignalledRequest(
                Graph, _state, snapshot, work.BindingRef, work.Handle, code, payload, work.IterationId)));
    }

    // --- Applying an evaluation ----------------------------------------------------------------------

    private void Evaluate(BpmnTranscriptTrigger trigger, Func<BpmnHostSnapshot, BpmnEvaluation> run)
    {
        if (IsFinished)
            return;

        var evaluation = run(Snapshot());

        // Diff against the raw state before pruning: pruning drops consumed tokens, and a diff against a
        // pruned baseline would keep re-reporting them as newly minted.
        var moves = DiffTokens(evaluation.State);

        _state = evaluation.State.Prune();
        Continuation = evaluation.Continuation;

        Transcript.Add(new BpmnTranscriptEntry(
            Transcript.Count + 1,
            _host.NextTranscriptSequence(),
            ScopeInstanceId,
            Clock.Now,
            trigger,
            evaluation.Continuation,
            evaluation.Commands,
            moves,
            (evaluation as BpmnFaultEvaluation)?.Disposition));

        // In the order returned. A teardown emitted before a start is not interchangeable with the reverse.
        foreach (var command in evaluation.Commands)
            Apply(command);

        if (Continuation is BpmnContinuation.Complete or BpmnContinuation.Fault)
            Finish();
    }

    private void Apply(BpmnHostCommand command)
    {
        switch (command)
        {
            case BpmnHostCommand.StartWork start:
                StartWork(start);
                break;

            case BpmnHostCommand.CancelWorkSubtree cancel:
                CancelWork(cancel.Handle, cancel.Reason);
                break;

            case BpmnHostCommand.SignalEnclosingScope signal:
                RaiseScopeSignal(signal);
                break;
        }
    }

    private void StartWork(BpmnHostCommand.StartWork start)
    {
        var handle = _host.NextHandle();
        var nested = Graph.GetRequiredBoundWork(start.BindingRef).NestedProcess;
        var hasDuration = _host.TryResolveTimer(Graph, start.BindingRef, out var duration, out var isTimer);

        var kind = nested is not null
            ? InMemoryWorkKind.NestedProcess
            : isTimer
                ? InMemoryWorkKind.Timer
                : InMemoryWorkKind.Activity;

        var work = new InMemoryWorkItem(
            handle,
            start.BindingRef,
            start.ElementId,
            start.IterationScope?.IterationId,
            start.SchedulingCause,
            start.Correlation,
            start.IterationScope?.Values ?? new Dictionary<string, BpmnValue>(StringComparer.Ordinal),
            kind,
            IsCatchListener(start),
            hasDuration && nested is null ? Clock.Now + duration : null,
            ScopeInstanceId,
            Clock.Now);

        _live.Add(work);
        _host.RegisterWork(handle, this);

        if (nested is not null)
            StartNestedScope(work, nested, start);
        else if (work.DueAt is { } dueAt)
            Clock.Schedule(handle, work.BindingRef, work.ElementId, dueAt);
    }

    /// <summary>
    /// Runs a subprocess or event-subprocess body as a child scope on the same host. Its work is parented to
    /// the handle that started it, which is what makes <see cref="BpmnHostCommand.CancelWorkSubtree"/> mean
    /// "and everything it in turn started" rather than "cancel this one thing".
    /// </summary>
    private void StartNestedScope(InMemoryWorkItem work, BpmnProcessDefinition nested, BpmnHostCommand.StartWork start)
    {
        var boundWork = _host.DeriveBoundWork(nested);
        _host.RegisterNestedProcesses(boundWork);

        var graph = BpmnGraph.Build(nested, boundWork, _host.Capabilities);

        // The child sees this scope's variables plus its own iteration frame — which is how a multi-instance
        // subprocess instance can tell which item it is running for.
        var variables = Variables.Clone();
        foreach (var value in work.IterationValues)
            variables.Set(value.Key, value.Value);

        var child = _host.CreateScope(graph, this, work.Handle, start.Correlation, variables);
        _childScopesByHandle[work.Handle] = child;

        _host.Enqueue(() => child.RunStart(null));
    }

    private void RaiseScopeSignal(BpmnHostCommand.SignalEnclosingScope signal)
    {
        var target = _parent is not null && _parentHandle is not null ? _parent.FindWork(_parentHandle) : null;

        _signals.Add(new InMemoryScopeSignal(signal.Code, signal.Payload, target is not null, Clock.Now));

        if (_parent is { } parent && target is { } parentWork)
            _host.Enqueue(() => parent.RunWorkSignalled(parentWork, signal.Code, signal.Payload));
    }

    /// <summary>
    /// Tears down one unit of work and everything underneath it. Recursive by construction: a nested scope's
    /// own live work goes with it, and so does anything that scope had started in turn.
    /// </summary>
    internal void CancelWork(string handle, string reason)
    {
        if (FindWork(handle) is not { } work)
            return;

        Retire(handle);
        _cancelled.Add(new InMemoryCancelledWork(handle, work.ElementId, reason, Clock.Now));

        if (_childScopesByHandle.TryGetValue(handle, out var child))
        {
            _childScopesByHandle.Remove(handle);
            child.CancelScope(reason);
        }
    }

    /// <summary>Tears this whole scope down: every unit of work it is running, and every scope underneath those.</summary>
    internal void CancelScope(string reason)
    {
        if (IsCancelled)
            return;

        IsCancelled = true;

        foreach (var work in _live.ToArray())
            CancelWork(work.Handle, reason);
    }

    private void Finish()
    {
        // A clean completion has already retired its listeners through CancelWorkSubtree. Anything still live
        // here is work the scope is walking away from — a fault discards its carried teardowns, because the
        // whole scope is going away — so tear it down, subtrees and all, rather than leaving orphans behind.
        foreach (var work in _live.ToArray())
            CancelWork(work.Handle, ScopeFinishedReason);

        if (_parent is not { } parent || _parentHandle is not { } parentHandle)
            return;

        if (parent.FindWork(parentHandle) is not { } parentWork)
            return;

        switch (Continuation)
        {
            case BpmnContinuation.Complete complete:
                _host.Enqueue(() => parent.RunWorkCompleted(parentWork, [complete.Outcome], timerFired: false));
                break;

            case BpmnContinuation.Fault fault:
                _host.Enqueue(() => parent.RunWorkFaulted(parentWork, $"{fault.Code}: {fault.Message}"));
                break;
        }
    }

    // --- Plumbing --------------------------------------------------------------------------------------

    private BpmnHostSnapshot Snapshot() => new(
        ScopeInstanceId,
        HasEnclosingScope: _parent is not null,
        LiveWork: _live.Select(work => new BpmnLiveWork(work.BindingRef, work.IterationId, work.Handle)).ToArray(),
        InvocationCorrelation: _invocationCorrelation,
        Variables: Variables,
        Capabilities: _host.Capabilities);

    private bool Retire(string handle)
    {
        var index = _live.FindIndex(work => StringComparer.Ordinal.Equals(work.Handle, handle));
        if (index < 0)
            return false;

        _live.RemoveAt(index);
        Clock.Cancel(handle);
        _host.UnregisterWork(handle);
        return true;
    }

    private void Post(Action action)
    {
        _host.Enqueue(action);
        _host.Drain();
    }

    /// <summary>
    /// Whether a start command arms a catcher rather than advancing the token: a boundary event's listener, or
    /// an event subprocess's scope listener. Neither has to be completed for the process to finish, which
    /// matters to anything driving the process automatically.
    /// </summary>
    private bool IsCatchListener(BpmnHostCommand.StartWork start)
    {
        if (StringComparer.Ordinal.Equals(start.SchedulingCause, BpmnInterpreter.ScopeListenerSchedulingCause))
            return true;

        return Graph.FindElementByBindingRef(start.BindingRef) is { } element
               && StringComparer.Ordinal.Equals(element.ElementType, BpmnElementTypes.BoundaryEvent);
    }

    private IReadOnlyList<BpmnTokenMove> DiffTokens(BpmnExecutionState state)
    {
        var moves = new List<BpmnTokenMove>();

        foreach (var token in state.Tokens)
        {
            if (!_tokenStatuses.TryGetValue(token.TokenId, out var previous))
                moves.Add(new BpmnTokenMove(token.TokenId, token.AtElementId, null, token.Status));
            else if (previous != token.Status)
                moves.Add(new BpmnTokenMove(token.TokenId, token.AtElementId, previous, token.Status));

            _tokenStatuses[token.TokenId] = token.Status;
        }

        return moves;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{ScopeInstanceId}: {BpmnTranscriptEntry.Describe(Continuation)}"
        + (IsCancelled ? " (cancelled by an enclosing scope)" : string.Empty)
        + (_live.Count == 0 ? string.Empty : $", waiting on {string.Join(", ", _live.Select(work => work.ElementId))}");
}
