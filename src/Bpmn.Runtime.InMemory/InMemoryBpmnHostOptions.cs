using Bpmn.Model;
using Bpmn.Semantics;

namespace Bpmn.Runtime.InMemory;

/// <summary>
/// How an <see cref="InMemoryBpmnHost"/> should behave. Every setting has a working default, so
/// <c>new InMemoryBpmnHost()</c> is a complete host.
/// </summary>
public sealed class InMemoryBpmnHostOptions
{
    /// <summary>
    /// What this host claims it can do. <see cref="BpmnHostCapabilities.Full"/> by default, because this host
    /// implements all of it; lower it to see the refusal a definition gets when it needs something the host
    /// does not have. It is passed to <see cref="BpmnGraph.Build"/>, so an under-capable host is refused at
    /// build time rather than degrading at run time.
    /// </summary>
    public BpmnHostCapabilities Capabilities { get; set; } = BpmnHostCapabilities.Full;

    /// <summary>
    /// Durations for timer work, keyed by binding ref. Consulted before the model's own ISO-8601 duration, so
    /// a test can shorten or supply a timer the definition does not carry one for.
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan>? TimerDurations { get; set; }

    /// <summary>
    /// A duration resolver for timer work, consulted when <see cref="TimerDurations"/> has no entry. Returning
    /// <c>null</c> falls through to the duration the model carries.
    /// </summary>
    public Func<string, TimeSpan?>? TimerDuration { get; set; }

    /// <summary>
    /// Nested process definitions keyed by binding ref, for a subprocess or event subprocess whose body this
    /// host should run as a child scope. Bound work supplied directly to
    /// <see cref="InMemoryBpmnHost.Start(BpmnProcessDefinition, IReadOnlyCollection{BpmnBoundWork}, InMemoryVariables, BpmnStartSignal)"/>
    /// is registered here automatically, so this is only needed for nesting deeper than the bound work names.
    /// </summary>
    public IReadOnlyDictionary<string, BpmnProcessDefinition>? NestedProcesses { get; set; }

    /// <summary>
    /// The variables a root scope starts with. Each root instance gets its own copy, so one host can run
    /// several instances without them sharing state.
    /// </summary>
    public InMemoryVariables? Variables { get; set; }

    /// <summary>
    /// A ceiling on how many evaluations one externally triggered call may cascade into, so a definition that
    /// loops forever fails loudly instead of hanging a test run. Exceeding it throws.
    /// </summary>
    public int MaxEvaluationsPerCall { get; set; } = 10_000;

    /// <summary>The interpreter to drive. Defaults to <see cref="BpmnInterpreter.CreateDefault"/>.</summary>
    public BpmnInterpreter? Interpreter { get; set; }
}
