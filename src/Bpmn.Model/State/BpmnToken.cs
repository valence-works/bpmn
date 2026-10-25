using System.Text.Json.Serialization;

namespace Bpmn.Model.State;

/// <summary>
/// One BPMN token. Tokens are minted when a start event fires or when a sequence flow is taken
/// (<see cref="FlowId"/> records the inbound flow), sit at <see cref="AtElementId"/>, and are consumed
/// when the element routes them onward, an end event absorbs them, or a terminate end event ends the
/// process.
/// </summary>
public sealed record BpmnToken
{
    [JsonConstructor]
    public BpmnToken(
    string tokenId,
    string atElementId,
    string? flowId = null,
    string? parentTokenId = null,
    BpmnTokenStatus status = BpmnTokenStatus.Active,
    string? producingWorkHandle = null,
    string? iterationKey = null,
    BpmnTokenKind? kind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(atElementId);

        TokenId = tokenId;
        AtElementId = atElementId;
        FlowId = flowId;
        ParentTokenId = parentTokenId;
        Status = status;
        ProducingWorkHandle = producingWorkHandle;
        IterationKey = iterationKey;
        Kind = kind;
    }

    [JsonPropertyName("tokenId")]
    public string TokenId { get; init; }

    [JsonPropertyName("atElementId")]
    public string AtElementId { get; init; }

    /// <summary>The sequence flow the token arrived on, or <c>null</c> for start-event tokens.</summary>
    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }

    [JsonPropertyName("parentTokenId")]
    public string? ParentTokenId { get; init; }

    [JsonPropertyName("status")]
    public BpmnTokenStatus Status { get; init; }

    /// <summary>The unit of host work whose completion produced this token, when known.</summary>
    [JsonPropertyName("producingWorkHandle")]
    public string? ProducingWorkHandle { get; init; }

    /// <summary>
    /// The loop-iteration key. It is
    /// <c>null</c> for the implicit first pass ("iteration 0") and is minted fresh only when a token traverses
    /// a backward (loop-back) sequence flow (<c>BpmnGraph.IsBackwardFlow</c>);
    /// every other minting site inherits its source/parent/group token's key. Join accounting groups arrivals
    /// by <c>(element, iteration key)</c> so a revisited join never conflates one iteration with the next.
    /// </summary>
    [JsonPropertyName("iterationKey")]
    public string? IterationKey { get; init; }

    /// <summary>
    /// The token-role discriminator: <c>null</c> for every ordinary token; <see cref="BpmnTokenKind.Activation"/>
    /// on an event-subprocess activation token, and <see cref="BpmnTokenKind.Listener"/> on a message, signal and timer triggered scope
    /// listener token (message/signal/timer-triggered event subprocess). A listener token and an activation token both
    /// sit at the same <c>TriggeredByEvent</c> element with a <c>null</c> <see cref="ParentTokenId"/>, so position no
    /// longer discriminates them — the completion-routing fork and the liveness gates read this field. It is an additive
    /// role field, <b>not</b> a token status (the status set is unchanged); a listener token is an ordinary
    /// <see cref="BpmnTokenStatus.AwaitingChild"/> token that is excluded from the completion/deadlock liveness view so
    /// it never blocks the scope from completing.
    /// </summary>
    [JsonPropertyName("kind")]
    public BpmnTokenKind? Kind { get; init; }

    /// <summary>
    /// How many times the scope listener this token replaces has already fired: <c>null</c> on every token except a
    /// <see cref="BpmnTokenKind.Listener"/> token re-armed after a non-interrupting fire of a timer on a counted cycle
    /// (<c>R&lt;n&gt;/</c>), which carries the fired listener's count plus one. A listener armed at scope start has not
    /// fired, and nothing counts the fires of an unbounded listener, so neither carries one. The count is what stops the
    /// cycle re-arming once it has fired n times; it lives on the token, so it survives persisting and restoring the state
    /// between fires. An init-only property, like <see cref="StoppedHandle"/>, so the constructor's signature is unchanged.
    /// Additive in payload format 1.9.0.
    /// </summary>
    [JsonPropertyName("fireCount")]
    public int? FireCount { get; init; }

    /// <summary>
    /// The host handle of the work the interpreter tore down when it cancelled this token, or <c>null</c>.
    /// Set only on a <see cref="BpmnTokenStatus.Canceled"/> token, and only once the teardown was actually issued:
    /// a <c>CancelWorkSubtree</c> for this handle went out on a non-fault continuation. A completion or fault that
    /// still arrives for the handle is the late callback of work the scope already stopped, and is absorbed; the
    /// field is then cleared, so a second report for the same handle is not. Work withdrawn before the host saw it
    /// had no handle, so it records none. Additive in payload format 1.1.0, and init-only rather than a constructor
    /// parameter so the constructor is unchanged.
    /// </summary>
    [JsonPropertyName("stoppedHandle")]
    public string? StoppedHandle { get; init; }
}

/// <summary>The token-role discriminator; see <see cref="BpmnToken.Kind"/>. Additive and nullable — it is never a token status.</summary>
public enum BpmnTokenKind
{
    /// <summary>A message, signal and timer triggered scope listener token: a suspending catcher armed at scope start (and re-armed per non-interrupting fire) that waits for a message/signal/timer stimulus without blocking the scope from completing.</summary>
    Listener,

    /// <summary>An event-subprocess activation token: the scope-level token minted when an event subprocess activates, kept alive until its body completes.</summary>
    Activation
}

public enum BpmnTokenStatus
{
    /// <summary>The token is at an element and must still be dispatched to the element's behavior.</summary>
    Active,

    /// <summary>The token is parked while the element's bound work runs.</summary>
    AwaitingChild,

    /// <summary>The token arrived at a joining gateway and waits for the join to fire.</summary>
    WaitingAtJoin,

    Consumed,
    Canceled
}
