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
        string? producingActivityExecutionId = null,
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
        ProducingActivityExecutionId = producingActivityExecutionId;
        IterationKey = iterationKey;
        Kind = kind;
    }

    public string TokenId { get; init; }
    public string AtElementId { get; init; }

    /// <summary>The sequence flow the token arrived on, or <c>null</c> for start-event tokens.</summary>
    public string? FlowId { get; init; }

    public string? ParentTokenId { get; init; }
    public BpmnTokenStatus Status { get; init; }

    /// <summary>The activity execution whose completion produced this token, when known.</summary>
    public string? ProducingActivityExecutionId { get; init; }

    /// <summary>
    /// The loop-iteration key (spec 122; the token analog of the Flowchart #382 loop-iteration scope). It is
    /// <c>null</c> for the implicit first pass ("iteration 0") and is minted fresh only when a token traverses
    /// a backward (loop-back) sequence flow (<c>BpmnGraph.IsBackwardFlow</c>);
    /// every other minting site inherits its source/parent/group token's key. Join accounting groups arrivals
    /// by <c>(element, iteration key)</c> so a revisited join never conflates one iteration with the next.
    /// </summary>
    public string? IterationKey { get; init; }

    /// <summary>
    /// The token-role discriminator (spec 134): <c>null</c> for every ordinary token; <see cref="BpmnTokenKind.Activation"/>
    /// on an event-subprocess activation token (spec 128), and <see cref="BpmnTokenKind.Listener"/> on a tier-2 scope
    /// listener token (message/signal/timer-triggered event subprocess). A listener token and an activation token both
    /// sit at the same <c>TriggeredByEvent</c> element with a <c>null</c> <see cref="ParentTokenId"/>, so position no
    /// longer discriminates them — the completion-routing fork and the liveness gates read this field. It is an additive
    /// role field, <b>not</b> a token status (the status set is unchanged); a listener token is an ordinary
    /// <see cref="BpmnTokenStatus.AwaitingChild"/> token that is excluded from the completion/deadlock liveness view so
    /// it never blocks the scope from completing.
    /// </summary>
    public BpmnTokenKind? Kind { get; init; }
}

/// <summary>The token-role discriminator (spec 134); see <see cref="BpmnToken.Kind"/>. Additive and nullable — it is never a token status.</summary>
public enum BpmnTokenKind
{
    /// <summary>A tier-2 scope listener token (spec 134): a suspending catcher armed at scope start (and re-armed per non-interrupting fire) that waits for a message/signal/timer stimulus without blocking the scope from completing.</summary>
    Listener,

    /// <summary>An event-subprocess activation token (spec 128): the scope-level token minted when an event subprocess activates, kept alive until its body completes.</summary>
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
