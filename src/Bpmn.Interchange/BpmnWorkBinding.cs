using Bpmn.Model;

namespace Bpmn.Interchange;

/// <summary>
/// Which channel of an element a binding fills.
/// </summary>
public enum BpmnBindingSlot
{
    /// <summary>The element's own work: the task body, the catch the token waits on, the nested process.</summary>
    Primary,

    /// <summary>
    /// The listener an event subprocess arms when its enclosing scope starts, so its start trigger can be
    /// observed while the scope runs. Distinct from <see cref="Primary"/>, which is the subprocess body.
    /// </summary>
    ScopeListener
}

/// <summary>
/// A declaration of the work one BPMN element needs, stated in BPMN vocabulary. The reader does not
/// synthesize host activities; it says what each element requires and hands the host an opaque
/// <see cref="BindingRef"/> that also appears on the element (as <see cref="BpmnElement.BindingRef"/> or
/// <see cref="BpmnElement.ListenerBindingRef"/>), so the host can wire its own implementation to it.
/// </summary>
/// <param name="ProcessId">The process the element belongs to. A nested process uses the subprocess element's id.</param>
/// <param name="ElementId">The BPMN id of the element that needs the work.</param>
/// <param name="BindingRef">The opaque key shared with the element, assigned deterministically from the element id.</param>
/// <param name="Slot">Which channel of the element this binding fills.</param>
public abstract record BpmnWorkBinding(string ProcessId, string ElementId, string BindingRef, BpmnBindingSlot Slot)
{
    /// <summary>A wait that ends after an ISO-8601 duration: a timer catch event, timer boundary event, or timer event-subprocess trigger.</summary>
    public sealed record TimerWait(
        string ProcessId,
        string ElementId,
        string BindingRef,
        BpmnBindingSlot Slot,
        string IsoDuration) : BpmnWorkBinding(ProcessId, ElementId, BindingRef, Slot);

    /// <summary>A wait for a named message: a message catch event, message boundary event, receive task, or message event-subprocess trigger.</summary>
    public sealed record MessageWait(
        string ProcessId,
        string ElementId,
        string BindingRef,
        BpmnBindingSlot Slot,
        string MessageName) : BpmnWorkBinding(ProcessId, ElementId, BindingRef, Slot);

    /// <summary>A wait for a named signal: a signal catch event, signal boundary event, or signal event-subprocess trigger.</summary>
    public sealed record SignalWait(
        string ProcessId,
        string ElementId,
        string BindingRef,
        BpmnBindingSlot Slot,
        string SignalName) : BpmnWorkBinding(ProcessId, ElementId, BindingRef, Slot);

    /// <summary>A publish of a named message: a message throw event, message end event, or send task.</summary>
    public sealed record MessagePublish(
        string ProcessId,
        string ElementId,
        string BindingRef,
        BpmnBindingSlot Slot,
        string MessageName) : BpmnWorkBinding(ProcessId, ElementId, BindingRef, Slot);

    /// <summary>A call activity: run a separately defined process the host resolves.</summary>
    public sealed record CallProcess(
        string ProcessId,
        string ElementId,
        string BindingRef,
        BpmnBindingSlot Slot,
        string? CalledElement,
        bool WaitForCompletion) : BpmnWorkBinding(ProcessId, ElementId, BindingRef, Slot);

    /// <summary>An embedded subprocess, transaction, or event subprocess: run the nested process carried here.</summary>
    public sealed record NestedProcess(
        string ProcessId,
        string ElementId,
        string BindingRef,
        BpmnBindingSlot Slot,
        BpmnProcessDefinition Definition) : BpmnWorkBinding(ProcessId, ElementId, BindingRef, Slot);

    /// <summary>
    /// A task the document describes but does not say how to perform: the host decides what a
    /// <c>userTask</c>, <c>serviceTask</c>, <c>scriptTask</c>, and so on actually does.
    /// </summary>
    public sealed record UnboundTask(
        string ProcessId,
        string ElementId,
        string BindingRef,
        BpmnBindingSlot Slot,
        string TaskType) : BpmnWorkBinding(ProcessId, ElementId, BindingRef, Slot);
}
