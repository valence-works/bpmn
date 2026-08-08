using Bpmn.Model.State;
using static Bpmn.Semantics.BpmnStateMutator;

namespace Bpmn.Semantics;

/// <summary>
/// Join accounting for the BPMN interpreter. Tokens arriving
/// at a multi-inbound parallel or inclusive gateway park as <see cref="BpmnTokenStatus.WaitingAtJoin"/>;
/// this coordinator decides when a join is ready and fires it by consuming one arrival per arrived
/// inbound flow and minting one merged token at the gateway. Every other element is an implicit XOR
/// merge: tokens pass through independently and never park here.
/// <para>
/// Arrivals are grouped by <c>(element, iteration key)</c>, so a join that is revisited across
/// loop iterations never conflates iteration <c>N</c> with iteration <c>N+1</c>: each group is a separate
/// join firing whose merged token inherits the group's iteration key. In an acyclic graph every iteration
/// key is <c>null</c>, so the grouping collapses to the pre-cycle by-element behavior.
/// </para>
/// </summary>
public sealed class BpmnTokenCoordinator
{
    /// <summary>Whether a token arriving at <paramref name="targetElementId"/> must park for a join.</summary>
    public static bool ShouldWaitAtJoin(BpmnGraph graph, string targetElementId)
    {
        var element = graph.GetRequiredElement(targetElementId);
        var family = BpmnElementFamilies.Resolve(element);
        if (family is not BpmnElementFamilies.ParallelGateway and not BpmnElementFamilies.InclusiveGateway)
            return false;

        return graph.InboundFlows(targetElementId).Count > 1;
    }

    /// <summary>
    /// Fires every ready join, repeating until none fires. Returns the updated state and the merged
    /// tokens minted by fired joins (already appended to the state as Active).
    /// </summary>
    public BpmnExecutionState ReleaseReadyJoins(BpmnExecutionState state, BpmnGraph graph)
    {
        while (true)
        {
            var group = state.Tokens
                .Where(token => token.Status == BpmnTokenStatus.WaitingAtJoin)
                .GroupBy(token => (token.AtElementId, token.IterationKey))
                .FirstOrDefault(group => IsJoinReady(state, graph, group.Key.AtElementId, group.Key.IterationKey));

            if (group is null)
                return state;

            state = FireJoin(state, graph, group.Key.AtElementId, group.Key.IterationKey);
        }
    }

    private static bool IsJoinReady(BpmnExecutionState state, BpmnGraph graph, string elementId, string? iterationKey)
    {
        var element = graph.GetRequiredElement(elementId);
        var family = BpmnElementFamilies.Resolve(element);
        var inboundFlows = graph.InboundFlows(elementId);
        var arrivedFlowIds = WaitingAtJoin(state, elementId, iterationKey)
            .Select(token => token.FlowId)
            .Where(flowId => flowId is not null)
            .Select(flowId => flowId!)
            .ToHashSet(StringComparer.Ordinal);

        if (family == BpmnElementFamilies.ParallelGateway)
            return inboundFlows.All(flow => arrivedFlowIds.Contains(flow.FlowId));

        // Inclusive join: activation-aware. Wait only while an un-arrived inbound flow can still be
        // reached by a live token of THIS iteration (a different-iteration token that reaches the
        // source arrives under its own key, forming a different group, so it must not hold this group open).
        foreach (var inboundFlow in inboundFlows)
        {
            if (arrivedFlowIds.Contains(inboundFlow.FlowId))
                continue;

            if (AnyLivePositionCanReach(state, graph, elementId, iterationKey, inboundFlow.SourceRef))
                return false;
        }

        return true;
    }

    private static bool AnyLivePositionCanReach(BpmnExecutionState state, BpmnGraph graph, string joinElementId, string? iterationKey, string flowSourceElementId)
    {
        var livePositions = state.Tokens
            .Where(token => StringComparer.Ordinal.Equals(token.IterationKey, iterationKey)
                            && (token.Status is BpmnTokenStatus.Active or BpmnTokenStatus.AwaitingChild
                                || (token.Status == BpmnTokenStatus.WaitingAtJoin && !StringComparer.Ordinal.Equals(token.AtElementId, joinElementId))))
            .Select(token => token.AtElementId);

        return livePositions.Any(position => graph.CanReach(position, flowSourceElementId));
    }

    private static IEnumerable<BpmnToken> WaitingAtJoin(BpmnExecutionState state, string elementId, string? iterationKey) =>
        state.Tokens.Where(token =>
            token.Status == BpmnTokenStatus.WaitingAtJoin &&
            StringComparer.Ordinal.Equals(token.AtElementId, elementId) &&
            StringComparer.Ordinal.Equals(token.IterationKey, iterationKey));

    private static BpmnExecutionState FireJoin(BpmnExecutionState state, BpmnGraph graph, string elementId, string? iterationKey)
    {
        var waiting = WaitingAtJoin(state, elementId, iterationKey).ToArray();

        // BPMN join semantics: consume exactly one arrival per arrived inbound flow; surplus arrivals on
        // the same flow stay parked for a later firing round.
        var consumed = new List<BpmnToken>();
        foreach (var flowGroup in waiting.GroupBy(token => token.FlowId, StringComparer.Ordinal))
            consumed.Add(flowGroup.First());

        foreach (var token in consumed)
            state = UpdateToken(state, token with { Status = BpmnTokenStatus.Consumed });

        var mergedToken = NewToken(
            state,
            elementId,
            flowId: null,
            parentTokenId: consumed[0].TokenId,
            status: BpmnTokenStatus.Active,
            producingWorkHandle: consumed[0].ProducingWorkHandle,
            iterationKey: iterationKey);
        state = AddToken(state, mergedToken);

        return BpmnDiagnosticAccumulator.Add(
            state,
            BpmnDiagnosticKind.Joined,
            elementId,
            null,
            mergedToken.TokenId,
            $"BPMN join '{elementId}' fired after {consumed.Count} arrival(s).");
    }
}
