using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>
/// Resolves host work rows against the immutable state at evaluation entry. Explicit identities and malformed
/// legacy iterations are checked globally; valid ID-less rows are resolved only when their slot is needed.
/// </summary>
internal sealed class BpmnLiveWorkSnapshotResolver(BpmnHostSnapshot host, BpmnExecutionState entryState)
{
    private readonly Dictionary<string, BpmnActiveWork> _activeByTokenId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeBindings = new(StringComparer.Ordinal);
    private readonly Dictionary<(string BindingRef, string? IterationId), List<BpmnActiveWork>> _activeBySlot = [];
    private readonly Dictionary<(string BindingRef, string? IterationId), List<BpmnLiveWork>> _idlessBySlot = [];
    private readonly HashSet<(string BindingRef, string? IterationId)> _explicitSlots = [];
    private readonly Dictionary<string, BpmnLiveWork> _explicitByTokenId = new(StringComparer.Ordinal);
    private bool _globalEvidenceValidated;

    /// <summary>Checks evidence that remains global even when a valid legacy slot is unrelated to this query.</summary>
    public void ValidateGlobalEvidence(string callback)
    {
        if (_globalEvidenceValidated)
            return;

        var stoppedByHandle = entryState.Tokens
            .Where(token => token.StoppedHandle is not null)
            .ToLookup(token => token.StoppedHandle!, StringComparer.Ordinal);
        var liveWork = new List<BpmnLiveWork>(host.LiveWork.Count);
        foreach (var live in host.LiveWork)
        {
            var stoppedTokens = stoppedByHandle[live.Handle].ToArray();
            if (stoppedTokens.Length == 0)
            {
                liveWork.Add(live);
                continue;
            }

            if (stoppedTokens.Length > 1)
                throw new BpmnExecutionException($"BPMN {callback} found more than one stopped token for handle '{live.Handle}'.");
            if (live.WorkTokenId is not null
                && (string.IsNullOrWhiteSpace(live.WorkTokenId)
                    || !StringComparer.Ordinal.Equals(live.WorkTokenId, stoppedTokens[0].TokenId)))
                throw new BpmnExecutionException($"BPMN {callback} found live handle '{live.Handle}' with WorkTokenId '{live.WorkTokenId}' conflicting with stopped token '{stoppedTokens[0].TokenId}'.");
        }

        var handles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var live in liveWork)
        {
            if (!handles.Add(live.Handle))
                throw new BpmnExecutionException($"BPMN {callback} found duplicate live-work handle '{live.Handle}'.");

            var slot = (live.BindingRef, live.IterationId);
            if (live.WorkTokenId is null)
            {
                AddToIndex(_idlessBySlot, slot, live);
                continue;
            }

            if (string.IsNullOrWhiteSpace(live.WorkTokenId))
                throw new BpmnExecutionException($"BPMN {callback} found a blank WorkTokenId on live work '{live.BindingRef}'.");
            if (!_explicitByTokenId.TryAdd(live.WorkTokenId, live))
                throw new BpmnExecutionException($"BPMN {callback} found duplicate live-work token id '{live.WorkTokenId}'.");

            _explicitSlots.Add(slot);
        }

        foreach (var work in entryState.ActiveWork)
        {
            if (!_activeByTokenId.TryAdd(work.TokenId, work))
                throw new BpmnExecutionException($"BPMN {callback} cannot resolve duplicate active work token id '{work.TokenId}'.");

            _activeBindings.Add(work.NodeId);
            AddToIndex(_activeBySlot, (work.NodeId, work.IterationId), work);
        }

        foreach (var live in liveWork.Where(candidate => candidate.WorkTokenId is not null))
        {
            var tokenId = live.WorkTokenId!;
            if (!_activeByTokenId.TryGetValue(tokenId, out var active))
                throw new BpmnExecutionException($"BPMN {callback} found live work '{live.BindingRef}' with unknown WorkTokenId '{tokenId}'.");
            if (!StringComparer.Ordinal.Equals(active.NodeId, live.BindingRef)
                || !StringComparer.Ordinal.Equals(active.IterationId, live.IterationId))
                throw new BpmnExecutionException($"BPMN {callback} found live work token '{tokenId}' whose binding or iteration contradicts its active work record.");
        }

        // A legacy row for a binding known to this scope cannot silently disappear into an unknown iteration.
        // Valid but ambiguous slots and mixed explicit/legacy slots are intentionally left for a targeted lookup.
        foreach (var slot in _idlessBySlot.Keys)
        {
            var candidates = GetValues(_activeBySlot, slot);
            if (candidates.Count == 0 && _activeBindings.Contains(slot.BindingRef))
                throw new BpmnExecutionException($"BPMN {callback} found ID-less live work for binding '{slot.BindingRef}' whose iteration '{slot.IterationId ?? "none"}' contradicts its active work records.");
        }

        _globalEvidenceValidated = true;
    }

    /// <summary>Returns the live host row for one entry-state token, rejecting ambiguous evidence in its slot.</summary>
    public BpmnLiveWork? ResolveForToken(string tokenId, string callback)
    {
        ValidateGlobalEvidence(callback);
        if (!_activeByTokenId.TryGetValue(tokenId, out var active))
            return null;

        (string BindingRef, string? IterationId) slot = (active.NodeId, active.IterationId);
        var legacyRows = GetValues(_idlessBySlot, slot);
        if (legacyRows.Count > 1)
            throw new BpmnExecutionException($"BPMN {callback} found duplicate ID-less live work for binding '{slot.BindingRef}' on iteration '{slot.IterationId ?? "none"}'.");
        if (legacyRows.Count > 0 && _explicitSlots.Contains(slot))
            throw new BpmnExecutionException($"BPMN {callback} found mixed explicit and ID-less live-work evidence for binding '{slot.BindingRef}' on iteration '{slot.IterationId ?? "none"}'.");

        if (_explicitByTokenId.TryGetValue(tokenId, out var explicitLive))
            return explicitLive;
        if (legacyRows.Count == 0)
            return null;

        var candidates = GetValues(_activeBySlot, slot);
        if (candidates.Count > 1)
            throw new BpmnExecutionException($"BPMN {callback} found ambiguous ID-less live work for binding '{slot.BindingRef}' on iteration '{slot.IterationId ?? "none"}'.");

        return candidates.Count == 1 && StringComparer.Ordinal.Equals(candidates[0].TokenId, tokenId)
            ? legacyRows[0]
            : null;
    }

    private static IReadOnlyList<TValue> GetValues<TKey, TValue>(Dictionary<TKey, List<TValue>> index, TKey key)
        where TKey : notnull =>
        index.TryGetValue(key, out var values) ? values : [];

    private static void AddToIndex<TKey, TValue>(Dictionary<TKey, List<TValue>> index, TKey key, TValue value)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var values))
            index.Add(key, values = []);
        values.Add(value);
    }
}
