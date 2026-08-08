using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>
/// Appends audit-only <see cref="BpmnDiagnosticEvent"/> records. Diagnostics are never read back by the
/// interpreter and are capped by <see cref="BpmnExecutionState.Prune"/>, but each append still bumps the
/// sequence and derives its id via <see cref="BpmnStateMutator.NewId"/>, so their placement is part of the
/// pinned id stream.
/// </summary>
internal static class BpmnDiagnosticAccumulator
{
    public static BpmnExecutionState Add(
        BpmnExecutionState state,
        BpmnDiagnosticKind kind,
        string? elementId,
        string? flowId,
        string? tokenId,
        string message)
    {
        var diagnostic = new BpmnDiagnosticEvent(
            BpmnStateMutator.NewId(state, "diag"),
            kind,
            message,
            elementId,
            flowId,
            tokenId);

        return state with { Diagnostics = state.Diagnostics.Append(diagnostic).ToArray(), Sequence = state.Sequence + 1 };
    }
}
