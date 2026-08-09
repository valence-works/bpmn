using Bpmn.Model;
using Bpmn.Semantics;

namespace Bpmn.Runtime.InMemory;

/// <summary>
/// Forwards to <see cref="BpmnBoundWork.Derive"/>, which is where this logic now lives.
/// <para>
/// It started here, in the reference host, and that was the wrong home: every host that runs
/// subprocesses needs the same walk, so leaving it here meant each one rewriting it. Kept as a
/// forwarder so existing callers keep compiling.
/// </para>
/// </summary>
public static class InMemoryBoundWork
{
    /// <inheritdoc cref="BpmnBoundWork.Derive"/>
    public static IReadOnlyCollection<BpmnBoundWork> Derive(
        BpmnProcessDefinition definition,
        Func<string, BpmnProcessDefinition?>? nestedProcess = null) =>
        BpmnBoundWork.Derive(definition, nestedProcess);
}
