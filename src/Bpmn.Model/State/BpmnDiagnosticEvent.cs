using System.Text.Json.Serialization;

namespace Bpmn.Model.State;

public sealed record BpmnDiagnosticEvent
{
    [JsonConstructor]
    public BpmnDiagnosticEvent(
    string diagnosticId,
    BpmnDiagnosticKind kind,
    string message,
    string? elementId = null,
    string? flowId = null,
    string? tokenId = null,
    IReadOnlyDictionary<string, string>? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        DiagnosticId = diagnosticId;
        Kind = kind;
        Message = message;
        ElementId = elementId;
        FlowId = flowId;
        TokenId = tokenId;
        Details = details ?? new Dictionary<string, string>();
    }

    [JsonPropertyName("diagnosticId")]
    public string DiagnosticId { get; init; }

    [JsonPropertyName("kind")]
    public BpmnDiagnosticKind Kind { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; }

    [JsonPropertyName("elementId")]
    public string? ElementId { get; init; }

    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }

    [JsonPropertyName("tokenId")]
    public string? TokenId { get; init; }

    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string> Details { get; init; }
}

public enum BpmnDiagnosticKind
{
    TokenEmitted,
    Scheduled,
    Waiting,
    Joined,
    Consumed,
    Canceled,
    Terminated,
    BehaviorFailure,
    Completed,
    Faulted,

    /// <summary>A host completion carrying an attached compensation boundary registered a compensable.</summary>
    CompensationRegistered,

    /// <summary>A compensate throw/end event triggered a compensation replay.</summary>
    CompensationTriggered,

    /// <summary>A compensation handler ran to completion for one registered compensable.</summary>
    Compensated,

    /// <summary>A cancel end event began (or completed) cancelling a transaction scope.</summary>
    TransactionCancelled,

    /// <summary>An escalation throw/end event staged a the enclosing-scope signal notification to its parent.</summary>
    EscalationRaised,

    /// <summary>An escalation notification matched an attached boundary and fired it.</summary>
    EscalationCaught,

    /// <summary>An escalation reached a scope that could not catch it: a root-process throw, or a root-unmatched notification; a no-op, never a fault.</summary>
    EscalationUnhandled,

    /// <summary>An interrupting escalation boundary matched a notification whose host had already terminalized; a no-op, never a fault.</summary>
    EscalationLate,

    /// <summary>An event subprocess was activated by its start-event trigger (escalation or error): an activation token was minted and its body scheduled.</summary>
    EventSubprocessActivated,

    /// <summary>An event subprocess body ran to completion: the activation token is consumed and nothing is routed (the element has no flows).</summary>
    EventSubprocessCompleted,

    /// <summary>A call activity's bound child completed with a failure outcome (Faulted/DispatchFailed/Cancelled) and the engine routed the call-activity failure ladder, instead of normal outbound flows.</summary>
    CallActivityFailureRouted,

    /// <summary>A message, signal and timer triggered scope listener was armed: a message/signal/timer event subprocess minted a listener token and scheduled its suspending listener child at scope start (or re-armed after a non-interrupting fire).</summary>
    ScopeListenerArmed,

    /// <summary>A message, signal and timer triggered scope listener fired: a message/signal/timer stimulus resumed the listener child, so the event subprocess activates (non-interrupting also re-arms; interrupting drains sibling listeners).</summary>
    ScopeListenerFired,

    /// <summary>A message, signal and timer triggered scope listener was retired: its scope completed (teardown-then-complete) or was interrupted, so the still-armed listener token and its durable child are cancelled.</summary>
    ScopeListenerRetired
}
