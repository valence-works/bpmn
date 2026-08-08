using System.Text.Json.Serialization;

namespace Bpmn.Model.State;

/// <summary>
/// One first-catch-wins race opened by an <c>eventBasedGateway</c>: the gateway minted one
/// member token per outbound flow (each arriving at an intermediate catch event that arms). The first
/// member whose child completes wins and routes; every other member token is cancelled and its armed child
/// subtree torn down. <see cref="RaceId"/> derives from <c>BpmnExecutionState.Sequence</c> (the only id
/// source); the record is additive payload growth — the state schema stays version 1.
/// </summary>
public sealed record BpmnEventRace
{
    [JsonConstructor]
    public BpmnEventRace(
    string raceId,
    string gatewayElementId,
    IReadOnlyCollection<string> memberTokenIds,
    bool resolved = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayElementId);

        RaceId = raceId;
        GatewayElementId = gatewayElementId;
        MemberTokenIds = memberTokenIds ?? [];
        Resolved = resolved;
    }

    public string RaceId { get; init; }
    public string GatewayElementId { get; init; }

    /// <summary>The tokens the gateway minted onto its outbound flows; exactly one wins, the rest are cancelled.</summary>
    public IReadOnlyCollection<string> MemberTokenIds { get; init; }

    /// <summary>Set once the first member's child completed and the losing siblings were cancelled.</summary>
    public bool Resolved { get; init; }
}
