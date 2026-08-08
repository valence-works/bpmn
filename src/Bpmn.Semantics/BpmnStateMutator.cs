using System.Text.Json;
using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>
/// The single home for immutable <see cref="BpmnExecutionState"/> mutations. Every builder bumps
/// <see cref="BpmnExecutionState.Sequence"/> and <see cref="NewId"/> derives every record id
/// (<c>token:N</c>, <c>diag:N</c>) from that sequence, so the persisted id stream is a pure, stable
/// function of mutation order and count.
/// </summary>
internal static class BpmnStateMutator
{
    public static string NewId(BpmnExecutionState state, string prefix) =>
        $"{prefix}:{state.Sequence + 1}";

    public static BpmnToken NewToken(
        BpmnExecutionState state,
        string atElementId,
        string? flowId,
        string? parentTokenId,
        BpmnTokenStatus status,
        string? producingWorkHandle,
        string? iterationKey = null,
        BpmnTokenKind? kind = null) =>
        new(NewId(state, "token"), atElementId, flowId, parentTokenId, status, producingWorkHandle, iterationKey, kind);

    /// <summary>
    /// The loop-iteration key minted when a token traverses a backward (loop-back) sequence flow:
    /// <c>"{loopEntryElementId}#{Sequence+1}"</c>. It is a pure function of mutation order — <see cref="NewId"/>
    /// derives the loop-entry token's id from the same <c>Sequence+1</c>, so the key number and its token id
    /// number coincide — globally unique across the process (<see cref="BpmnExecutionState.Sequence"/> is
    /// monotonic and never reused), and needs no per-owner counter record.
    /// </summary>
    public static string NewIterationKey(BpmnExecutionState state, string loopEntryElementId) =>
        $"{loopEntryElementId}#{state.Sequence + 1}";

    public static BpmnExecutionState AddToken(BpmnExecutionState state, BpmnToken token) =>
        state with { Tokens = state.Tokens.Append(token).ToArray(), Sequence = state.Sequence + 1 };

    public static BpmnExecutionState UpdateToken(BpmnExecutionState state, BpmnToken token) =>
        state with
        {
            Tokens = state.Tokens
                .Select(existing => StringComparer.Ordinal.Equals(existing.TokenId, token.TokenId) ? token : existing)
                .ToArray(),
            Sequence = state.Sequence + 1
        };

    public static BpmnExecutionState AddActiveWork(BpmnExecutionState state, BpmnActiveWork work) =>
        state with { ActiveWork = state.ActiveWork.Append(work).ToArray(), Sequence = state.Sequence + 1 };

    public static BpmnExecutionState RemoveActiveWork(BpmnExecutionState state, string tokenId) =>
        state with
        {
            ActiveWork = state.ActiveWork
                .Where(work => !StringComparer.Ordinal.Equals(work.TokenId, tokenId))
                .ToArray(),
            Sequence = state.Sequence + 1
        };

    public static BpmnExecutionState AddRace(BpmnExecutionState state, string gatewayElementId, IReadOnlyCollection<string> memberTokenIds)
    {
        var race = new BpmnEventRace(NewId(state, "race"), gatewayElementId, memberTokenIds);
        return state with { Races = state.Races.Append(race).ToArray(), Sequence = state.Sequence + 1 };
    }

    public static BpmnExecutionState MarkRaceResolved(BpmnExecutionState state, string raceId) =>
        state with
        {
            Races = state.Races
                .Select(race => StringComparer.Ordinal.Equals(race.RaceId, raceId) ? race with { Resolved = true } : race)
                .ToArray(),
            Sequence = state.Sequence + 1
        };

    public static (BpmnExecutionState State, BpmnLoopState Loop) AddLoop(
        BpmnExecutionState state,
        string coordinatorTokenId,
        string elementId,
        bool isSequential,
        int totalCount,
        IReadOnlyList<JsonElement>? items = null)
    {
        var loop = new BpmnLoopState(NewId(state, "loop"), coordinatorTokenId, elementId, isSequential, totalCount, nextIndex: 0, completedCount: 0, items);
        return (state with { Loops = state.Loops.Append(loop).ToArray(), Sequence = state.Sequence + 1 }, loop);
    }

    public static BpmnExecutionState UpdateLoop(BpmnExecutionState state, BpmnLoopState loop) =>
        state with
        {
            Loops = state.Loops
                .Select(existing => StringComparer.Ordinal.Equals(existing.LoopId, loop.LoopId) ? loop : existing)
                .ToArray(),
            Sequence = state.Sequence + 1
        };

    public static BpmnExecutionState RemoveLoop(BpmnExecutionState state, string loopId) =>
        state with
        {
            Loops = state.Loops
                .Where(loop => !StringComparer.Ordinal.Equals(loop.LoopId, loopId))
                .ToArray(),
            Sequence = state.Sequence + 1
        };

    /// <summary>The live multi-instance loop coordinated by <paramref name="coordinatorTokenId"/>, or <c>null</c> when that token is not a coordinator.</summary>
    public static BpmnLoopState? FindLoopByCoordinator(BpmnExecutionState state, string coordinatorTokenId) =>
        state.Loops.FirstOrDefault(loop => StringComparer.Ordinal.Equals(loop.TokenId, coordinatorTokenId));

    /// <summary>Appends a <c>Registered</c> compensation-log entry; <c>comp:N</c> derives from <see cref="BpmnExecutionState.Sequence"/> so registration order is total and deterministic.</summary>
    public static (BpmnExecutionState State, BpmnCompensable Compensable) AddCompensable(
        BpmnExecutionState state, string hostElementId, string handlerElementId)
    {
        var compensable = new BpmnCompensable(NewId(state, "comp"), hostElementId, handlerElementId, BpmnCompensableStatus.Registered);
        return (state with { Compensables = state.Compensables.Append(compensable).ToArray(), Sequence = state.Sequence + 1 }, compensable);
    }

    public static BpmnExecutionState UpdateCompensable(BpmnExecutionState state, BpmnCompensable compensable) =>
        state with
        {
            Compensables = state.Compensables
                .Select(existing => StringComparer.Ordinal.Equals(existing.CompensableId, compensable.CompensableId) ? compensable : existing)
                .ToArray(),
            Sequence = state.Sequence + 1
        };

    /// <summary>Opens a compensation replay run; <c>comprun:N</c> derives from <see cref="BpmnExecutionState.Sequence"/>.</summary>
    public static (BpmnExecutionState State, BpmnCompensationRun Run) AddCompensationRun(
        BpmnExecutionState state, string throwTokenId, IReadOnlyList<string> pendingCompensableIds)
    {
        var run = new BpmnCompensationRun(NewId(state, "comprun"), throwTokenId, pendingCompensableIds);
        return (state with { CompensationRuns = state.CompensationRuns.Append(run).ToArray(), Sequence = state.Sequence + 1 }, run);
    }

    public static BpmnExecutionState UpdateCompensationRun(BpmnExecutionState state, BpmnCompensationRun run) =>
        state with
        {
            CompensationRuns = state.CompensationRuns
                .Select(existing => StringComparer.Ordinal.Equals(existing.RunId, run.RunId) ? run : existing)
                .ToArray(),
            Sequence = state.Sequence + 1
        };

    public static BpmnExecutionState RemoveCompensationRun(BpmnExecutionState state, string runId) =>
        state with
        {
            CompensationRuns = state.CompensationRuns
                .Where(run => !StringComparer.Ordinal.Equals(run.RunId, runId))
                .ToArray(),
            Sequence = state.Sequence + 1
        };

    /// <summary>The in-flight compensation run coordinated by <paramref name="throwTokenId"/>, or <c>null</c> when that token is not a run coordinator.</summary>
    public static BpmnCompensationRun? FindCompensationRun(BpmnExecutionState state, string throwTokenId) =>
        state.CompensationRuns.FirstOrDefault(run => StringComparer.Ordinal.Equals(run.ThrowTokenId, throwTokenId));

    public static BpmnToken GetRequiredToken(BpmnExecutionState state, string tokenId) =>
        state.Tokens.FirstOrDefault(token => StringComparer.Ordinal.Equals(token.TokenId, tokenId))
        ?? throw new BpmnExecutionException($"BPMN token '{tokenId}' was not found on the execution state.");
}
