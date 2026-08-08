using Bpmn.Model;
using Bpmn.Semantics;

namespace Bpmn.Runtime.InMemory;

/// <summary>
/// Derives the <see cref="BpmnBoundWork"/> collection a definition needs from the definition itself.
/// <para>
/// <see cref="BpmnGraph.Build"/> demands one entry per declared binding, no more and no less. That is the
/// right contract for a host that is wiring real implementations to bindings, and pure ceremony for a host
/// whose implementations are "wait until someone tells me". This walks the elements and produces exactly the
/// set the graph will accept.
/// </para>
/// </summary>
public static class InMemoryBoundWork
{
    /// <summary>
    /// The bound work <paramref name="definition"/> declares. <paramref name="nestedProcess"/> supplies the
    /// nested definition for a binding that runs one — required for an event subprocess body, whose start
    /// trigger the graph validator reads out of it.
    /// </summary>
    public static IReadOnlyCollection<BpmnBoundWork> Derive(
        BpmnProcessDefinition definition,
        Func<string, BpmnProcessDefinition?>? nestedProcess = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var bindings = new List<BpmnBoundWork>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in definition.Elements)
        {
            Add(element.BindingRef, nested: true);
            Add(element.ListenerBindingRef, nested: false);
        }

        return bindings;

        void Add(string? bindingRef, bool nested)
        {
            if (bindingRef is null || !seen.Add(bindingRef))
                return;

            bindings.Add(new BpmnBoundWork(bindingRef, nested ? nestedProcess?.Invoke(bindingRef) : null));
        }
    }
}
