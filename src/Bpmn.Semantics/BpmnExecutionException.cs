namespace Bpmn.Semantics;

/// <summary>
/// Raised when a BPMN process graph is structurally invalid, or when the interpreter reaches a state its
/// own invariants forbid. It signals a defect in the process definition or in the host's use of the
/// library — never an ordinary process outcome. A BPMN error caught by a boundary event or an error event
/// subprocess is modelled as a token-flow outcome, not as an exception.
/// </summary>
public class BpmnExecutionException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public BpmnExecutionException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    public BpmnExecutionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised by <see cref="BpmnGraph.Build"/> when a process definition needs host capabilities the host did
/// not declare.
/// <para>
/// The library refuses at build time rather than degrading at run time. A host without subtree
/// cancellation running an interrupting boundary event produces incorrect BPMN — two live branches where
/// the specification says one — and that error surfaces far from its cause. An upfront refusal naming the
/// missing capability and the elements that need it is strictly more useful than a runtime diagnostic.
/// </para>
/// </summary>
public sealed class BpmnCapabilityException : BpmnExecutionException
{
    /// <summary>Creates the exception from an unmet requirement set.</summary>
    public BpmnCapabilityException(string message, BpmnHostCapabilities missing, IReadOnlyList<string> drivingElementIds)
        : base(message)
    {
        Missing = missing;
        DrivingElementIds = drivingElementIds;
    }

    /// <summary>The capabilities the definition needs and the host did not declare.</summary>
    public BpmnHostCapabilities Missing { get; }

    /// <summary>The element ids that need the missing capabilities, in ordinal order.</summary>
    public IReadOnlyList<string> DrivingElementIds { get; }
}
