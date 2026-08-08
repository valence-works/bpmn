using System.Text.Json.Serialization;

namespace Bpmn.Semantics;

/// <summary>
/// The immutable decision returned by an <see cref="IBpmnElementBehavior"/>. The interpreter validates and
/// applies the commands; behaviors never mutate state or dispatch work themselves.
/// </summary>
public sealed record BpmnBehaviorDecision
{
    /// <summary>Creates a decision from a command list.</summary>
    [JsonConstructor]
    public BpmnBehaviorDecision(IReadOnlyCollection<BpmnBehaviorCommand>? commands = null)
    {
        Commands = commands ?? [];
    }

    /// <summary>The commands, applied in order.</summary>
    public IReadOnlyCollection<BpmnBehaviorCommand> Commands { get; init; }

    /// <summary>Creates a decision from a command sequence.</summary>
    public static BpmnBehaviorDecision Of(params BpmnBehaviorCommand[] commands) => new(commands);
}

/// <summary>One command in a <see cref="BpmnBehaviorDecision"/>.</summary>
public sealed record BpmnBehaviorCommand
{
    /// <summary>Creates a command.</summary>
    [JsonConstructor]
    public BpmnBehaviorCommand(
        BpmnBehaviorCommandKind kind,
        IReadOnlyCollection<string>? flowIds = null,
        string? faultCode = null,
        string? message = null)
    {
        Kind = kind;
        FlowIds = flowIds ?? [];
        FaultCode = faultCode;
        Message = message;
    }

    /// <summary>What the command asks the interpreter to do.</summary>
    public BpmnBehaviorCommandKind Kind { get; init; }

    /// <summary>The sequence flows to take (for <see cref="BpmnBehaviorCommandKind.EmitTokens"/>).</summary>
    public IReadOnlyCollection<string> FlowIds { get; init; }

    /// <summary>The fault code (for <see cref="BpmnBehaviorCommandKind.Fault"/>).</summary>
    public string? FaultCode { get; init; }

    /// <summary>A human-readable message.</summary>
    public string? Message { get; init; }

    /// <summary>Consume the current token and emit one token per listed outbound sequence flow.</summary>
    public static BpmnBehaviorCommand EmitTokens(IReadOnlyCollection<string> flowIds) =>
        new(BpmnBehaviorCommandKind.EmitTokens, flowIds);

    /// <summary>Park the current token and start the element's bound work.</summary>
    public static BpmnBehaviorCommand StartWork() => new(BpmnBehaviorCommandKind.StartWork);

    /// <summary>Consume the current token without emitting successors.</summary>
    public static BpmnBehaviorCommand ConsumeToken() => new(BpmnBehaviorCommandKind.ConsumeToken);

    /// <summary>Consume every live token and end the process.</summary>
    public static BpmnBehaviorCommand TerminateProcess(string? message = null) =>
        new(BpmnBehaviorCommandKind.TerminateProcess, message: message);

    /// <summary>Fault the process deterministically.</summary>
    public static BpmnBehaviorCommand Fault(string faultCode, string message) =>
        new(BpmnBehaviorCommandKind.Fault, faultCode: faultCode, message: message);

    /// <summary>Replay the compensation log.</summary>
    public static BpmnBehaviorCommand TriggerCompensation() => new(BpmnBehaviorCommandKind.TriggerCompensation);

    /// <summary>Cancel the enclosing transaction.</summary>
    public static BpmnBehaviorCommand CancelTransaction() => new(BpmnBehaviorCommandKind.CancelTransaction);

    /// <summary>Raise an escalation.</summary>
    public static BpmnBehaviorCommand RaiseEscalation() => new(BpmnBehaviorCommandKind.RaiseEscalation);
}

/// <summary>What a <see cref="BpmnBehaviorCommand"/> asks the interpreter to do.</summary>
public enum BpmnBehaviorCommandKind
{
    /// <summary>Consume the current token and emit one token per listed outbound sequence flow.</summary>
    EmitTokens,

    /// <summary>Park the current token and start the work bound to the element.</summary>
    StartWork,

    /// <summary>Consume the current token without emitting successors (a none end event).</summary>
    ConsumeToken,

    /// <summary>Consume every live token and end the process (a terminate end event).</summary>
    TerminateProcess,

    /// <summary>Fault the process deterministically.</summary>
    Fault,

    /// <summary>
    /// Replay the compensation log: a compensate throw/end token asks the interpreter to claim and run the
    /// registered compensation handlers in reverse registration order, then route or consume when done. The
    /// behavior stays decision-only — registration, ordering, claiming, and replay are interpreter-owned.
    /// </summary>
    TriggerCompensation,

    /// <summary>
    /// Cancel the enclosing transaction: a cancel end event asks the interpreter to stop all other live work in
    /// the scope, replay every registered compensable in reverse registration order, and then complete the
    /// process with the <c>Cancelled</c> outcome. The behavior stays decision-only.
    /// </summary>
    CancelTransaction,

    /// <summary>
    /// Raise an escalation: an escalation throw/end event asks the interpreter to signal the enclosing scope —
    /// or, at a root process, to record a no-op diagnostic. Companion to the throw's routing command
    /// (<see cref="EmitTokens"/> on an intermediate throw, <see cref="ConsumeToken"/> on an end event). The
    /// behavior stays decision-only — the escalation code is read from the element, and matching, firing, and
    /// bubbling are interpreter-owned.
    /// </summary>
    RaiseEscalation
}
