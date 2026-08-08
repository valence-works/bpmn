using Bpmn.Model;
using Bpmn.Semantics;

namespace Bpmn.Runtime.InMemory;

/// <summary>
/// A reference host for the BPMN interpreter: it runs a process entirely in memory, on a virtual clock, with
/// no I/O and no waiting.
/// <para>
/// <b>Be clear about what this is.</b> It is <b>not durable</b> — every instance, token and work item lives in
/// this object and dies with it. It is <b>single-process and single-threaded</b> — there is no locking, no
/// coordination, no leasing, and calling into one instance from two threads is undefined. It has <b>no
/// retries, no backoff, no poison handling and no dead letters</b>. It is <b>not a production workflow
/// engine</b>, and treating it as one will cost you data. It is a reference implementation, a test fixture,
/// and documentation that runs.
/// </para>
/// <para>
/// What it is for is falsifiability. The claim that the interpreter is host-agnostic is only worth something
/// if a second, unrelated host can implement the port completely — so this one implements every capability
/// rather than the convenient subset, and refuses a definition it cannot honour instead of quietly skipping
/// the part it cannot do.
/// </para>
/// <para>
/// <b>Nothing here reads a real clock.</b> There is no <c>DateTime.Now</c>, no <c>Task.Delay</c>, no
/// <c>Thread.Sleep</c>. Time is <see cref="Clock"/>, and it moves only when a caller moves it, so a seven-day
/// timer simulates in microseconds and the same inputs always produce the same transcript.
/// </para>
/// <example>
/// <code>
/// var host = new InMemoryBpmnHost();
/// var instance = host.Start(definition);
///
/// instance.CompleteWork("node-Approve", "Approved");
/// host.Clock.Advance(TimeSpan.FromDays(7));
///
/// Console.WriteLine(instance.Transcript);
/// </code>
/// </example>
/// </summary>
public sealed class InMemoryBpmnHost
{
    private readonly Queue<Action> _queue = new();
    private readonly List<InMemoryProcessInstance> _instances = [];
    private readonly Dictionary<string, InMemoryProcessInstance> _workOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BpmnProcessDefinition> _nestedProcesses = new(StringComparer.Ordinal);

    private bool _draining;
    private int _handleCounter;
    private int _scopeCounter;
    private int _transcriptCounter;

    /// <summary>Creates a host with the default options: full capabilities, an empty clock, no seeded variables.</summary>
    public InMemoryBpmnHost()
        : this(new InMemoryBpmnHostOptions())
    {
    }

    /// <summary>Creates a host with the given options.</summary>
    public InMemoryBpmnHost(InMemoryBpmnHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
        Interpreter = options.Interpreter ?? BpmnInterpreter.CreateDefault();
        Clock = new VirtualClock { Fired = FireTimer };

        if (options.NestedProcesses is { } nested)
        {
            foreach (var entry in nested)
                _nestedProcesses[entry.Key] = entry.Value;
        }
    }

    /// <summary>The options this host was built with.</summary>
    public InMemoryBpmnHostOptions Options { get; }

    /// <summary>What this host claims it can do. Passed to every graph build and every snapshot.</summary>
    public BpmnHostCapabilities Capabilities => Options.Capabilities;

    /// <summary>The simulated clock every scope on this host shares.</summary>
    public VirtualClock Clock { get; }

    /// <summary>The interpreter this host drives.</summary>
    public BpmnInterpreter Interpreter { get; }

    /// <summary>Every scope this host has started, root and nested, in creation order.</summary>
    public IReadOnlyList<InMemoryProcessInstance> Instances => _instances;

    /// <summary>Every unit of work still running anywhere on this host, in start order.</summary>
    public IReadOnlyList<InMemoryWorkItem> PendingWork =>
        _instances.SelectMany(instance => instance.PendingWork).ToArray();

    /// <summary>
    /// Starts a process. <paramref name="boundWork"/> may be omitted, in which case it is derived from the
    /// definition's own declared bindings (see <see cref="InMemoryBoundWork"/>).
    /// <para>
    /// Throws <see cref="BpmnCapabilityException"/> when the definition needs a capability
    /// <see cref="Capabilities"/> does not declare, and <see cref="BpmnExecutionException"/> when the
    /// definition is structurally invalid. Both are raised here, before anything runs.
    /// </para>
    /// </summary>
    public InMemoryProcessInstance Start(
        BpmnProcessDefinition definition,
        IReadOnlyCollection<BpmnBoundWork>? boundWork = null,
        InMemoryVariables? variables = null,
        BpmnStartSignal? startSignal = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var bindings = boundWork ?? DeriveBoundWork(definition);
        RegisterNestedProcesses(bindings);

        return Start(BpmnGraph.Build(definition, bindings, Capabilities), variables, startSignal);
    }

    /// <summary>
    /// Starts a process from a graph that was already built. Use this when the same definition runs many
    /// times: a graph depends only on the definition, the bound work and the capabilities, none of which
    /// change per instance.
    /// </summary>
    public InMemoryProcessInstance Start(
        BpmnGraph graph,
        InMemoryVariables? variables = null,
        BpmnStartSignal? startSignal = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var instance = CreateScope(
            graph,
            parent: null,
            parentHandle: null,
            invocationCorrelation: new Dictionary<string, string>(StringComparer.Ordinal),
            variables: variables ?? Options.Variables?.Clone() ?? new InMemoryVariables());

        Enqueue(() => instance.RunStart(startSignal));
        Drain();
        return instance;
    }

    /// <summary>The unit of work with this handle, wherever on this host it is running, or <c>null</c>.</summary>
    public InMemoryWorkItem? FindWork(string handle) => OwnerOf(handle)?.FindWork(handle);

    /// <summary>The scope running the work with this handle, or <c>null</c>.</summary>
    public InMemoryProcessInstance? OwnerOf(string handle) =>
        handle is not null && _workOwners.TryGetValue(handle, out var owner) ? owner : null;

    /// <summary>The bound work <paramref name="definition"/> declares, with nested definitions this host knows about attached.</summary>
    public IReadOnlyCollection<BpmnBoundWork> DeriveBoundWork(BpmnProcessDefinition definition) =>
        InMemoryBoundWork.Derive(definition, ResolveNestedProcess);

    // --- Internals the instances use -----------------------------------------------------------------

    internal InMemoryProcessInstance CreateScope(
        BpmnGraph graph,
        InMemoryProcessInstance? parent,
        string? parentHandle,
        IReadOnlyDictionary<string, string> invocationCorrelation,
        InMemoryVariables variables)
    {
        var instance = new InMemoryProcessInstance(
            this,
            $"scope-{++_scopeCounter}",
            graph,
            parent,
            parentHandle,
            invocationCorrelation,
            variables);

        _instances.Add(instance);
        return instance;
    }

    internal string NextHandle() => $"work-{++_handleCounter}";

    internal int NextTranscriptSequence() => ++_transcriptCounter;

    internal void RegisterWork(string handle, InMemoryProcessInstance owner) => _workOwners[handle] = owner;

    internal void UnregisterWork(string handle) => _workOwners.Remove(handle);

    internal void RegisterNestedProcesses(IEnumerable<BpmnBoundWork> boundWork)
    {
        foreach (var work in boundWork)
        {
            if (work.NestedProcess is { } nested)
                _nestedProcesses[work.BindingRef] = nested;
        }
    }

    internal BpmnProcessDefinition? ResolveNestedProcess(string bindingRef) =>
        _nestedProcesses.TryGetValue(bindingRef, out var definition)
            ? definition
            : Options.NestedProcesses is { } configured && configured.TryGetValue(bindingRef, out var fallback)
                ? fallback
                : null;

    /// <summary>
    /// Queues an action to run once the current evaluation has finished applying its commands. Everything the
    /// host does goes through this queue, which is what keeps a synchronously completing nested process from
    /// re-entering its parent halfway through a command list.
    /// </summary>
    internal void Enqueue(Action action) => _queue.Enqueue(action);

    /// <summary>Runs the queue to exhaustion. A no-op when already draining, so nesting is safe.</summary>
    internal void Drain()
    {
        if (_draining)
            return;

        _draining = true;
        try
        {
            var evaluations = 0;
            while (_queue.Count > 0)
            {
                if (++evaluations > Options.MaxEvaluationsPerCall)
                {
                    _queue.Clear();
                    throw new InvalidOperationException(
                        $"The BPMN in-memory host performed more than {Options.MaxEvaluationsPerCall} evaluations in a single call, "
                        + "which almost always means the definition loops without ever waiting for anything. "
                        + $"Raise {nameof(InMemoryBpmnHostOptions)}.{nameof(InMemoryBpmnHostOptions.MaxEvaluationsPerCall)} if the process really is that large.");
                }

                _queue.Dequeue()();
            }
        }
        finally
        {
            _draining = false;
        }
    }

    /// <summary>
    /// Resolves the timer behind a binding. A duration supplied through the options wins; otherwise the
    /// model's own ISO-8601 duration is used. A timer element with no resolvable duration is still reported as
    /// a timer, so a caller can see it and complete it by hand rather than wondering why the clock ignores it.
    /// </summary>
    internal bool TryResolveTimer(BpmnGraph graph, string bindingRef, out TimeSpan duration, out bool isTimer)
    {
        duration = default;
        isTimer = false;

        if (Options.TimerDurations is { } durations && durations.TryGetValue(bindingRef, out var configured))
        {
            duration = configured;
            isTimer = true;
            return true;
        }

        if (Options.TimerDuration?.Invoke(bindingRef) is { } resolved)
        {
            duration = resolved;
            isTimer = true;
            return true;
        }

        var iso = ResolveModelDuration(graph, bindingRef, out isTimer);
        if (iso is null)
            return false;

        duration = IsoDuration.Parse(iso);
        return true;
    }

    private static string? ResolveModelDuration(BpmnGraph graph, string bindingRef, out bool isTimer)
    {
        isTimer = false;

        // The binding is an element's own work: a timer catch event, or a timer boundary event's listener.
        if (graph.FindElementByBindingRef(bindingRef) is { } element)
        {
            if (TimerDefinition(element) is not { } definition)
                return null;

            isTimer = true;
            return ReadInterval(definition);
        }

        // The binding is an event subprocess's scope listener. The trigger lives on the body's start event,
        // which travels with the body's own bound work.
        var catcher = graph.EventSubprocesses.FirstOrDefault(candidate =>
            candidate.ListenerBindingRef is { } listener && StringComparer.Ordinal.Equals(listener, bindingRef));

        if (catcher is null || catcher.TriggerKind != BpmnEventSubprocessTriggerKind.Timer)
            return null;

        isTimer = true;

        var body = graph.GetRequiredBoundWork(catcher.BindingRef).NestedProcess;
        var bodyStart = body?.Elements.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.ElementId, catcher.BodyStartElementId));

        return bodyStart is not null && TimerDefinition(bodyStart) is { } bodyTimer ? ReadInterval(bodyTimer) : null;
    }

    private static BpmnEventDefinition? TimerDefinition(BpmnElement element) =>
        element.EventDefinitions.FirstOrDefault(definition =>
            StringComparer.Ordinal.Equals(definition.Type, BpmnEventDefinitionTypes.Timer));

    private static string? ReadInterval(BpmnEventDefinition definition) =>
        definition.Properties.TryGetValue(BpmnEventDefinitionProperties.Interval, out var interval)
        && !string.IsNullOrWhiteSpace(interval)
            ? interval
            : null;

    private void FireTimer(PendingTimer timer)
    {
        if (OwnerOf(timer.Handle) is not { } owner)
            return;

        Enqueue(() => owner.RunTimerFired(timer.Handle));
        Drain();
    }
}
