using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>
/// One running unit of work to tear down: a losing event-based-gateway branch, an interrupted boundary
/// host, a retired listener, the work a cancelled transaction abandons. Carried — on the evaluation result,
/// or on the evaluation context when the result cannot reach the caller — and issued only on a non-fault
/// continuation, which is also when <paramref name="TokenId"/>'s token records the handle as stopped.
/// </summary>
internal sealed record PendingTeardown(string Handle, string ElementId, string Reason, string TokenId);

/// <summary>
/// The mutable scratch space of one interpreter evaluation: the graph and host snapshot it reads, and the
/// command list it builds. It exists so the interpreter can stay a pure function of its request — nothing here
/// reaches the host, and nothing here survives the evaluation.
/// </summary>
internal sealed class BpmnEvaluationContext(BpmnGraph graph, BpmnHostSnapshot host, BpmnExecutionState entryState)
{
    private readonly List<BpmnHostCommand> _commands = [];
    private readonly List<PendingTeardown> _carriedTeardowns = [];

    /// <summary>The immutable state used to correlate entries in <see cref="BpmnHostSnapshot.LiveWork"/>.</summary>
    public BpmnExecutionState EntryState { get; } = entryState;

    /// <summary>The lazily indexed host ledger for this evaluation's entry state.</summary>
    public BpmnLiveWorkSnapshotResolver? LiveWorkSnapshotResolver { get; set; }

    /// <summary>The graph this evaluation runs.</summary>
    public BpmnGraph Graph { get; } = graph;

    /// <summary>What the host told the interpreter about its world.</summary>
    public BpmnHostSnapshot Host { get; } = host;

    /// <summary>The commands produced so far, in order.</summary>
    public IReadOnlyList<BpmnHostCommand> Commands => _commands;

    /// <summary>
    /// Whether the command list currently asks the host to start work. A terminal continuation may not also
    /// start work, so an evaluation that ends its scope withdraws those starts (see
    /// <see cref="WithdrawStartedWork"/>) rather than deferring its verdict.
    /// </summary>
    public bool StartedWork { get; private set; }

    /// <summary>Asks the host to start a unit of work.</summary>
    public void AddStartWork(BpmnHostCommand.StartWork command)
    {
        _commands.Add(command);
        StartedWork = true;
    }

    /// <summary>
    /// Withdraws every <see cref="BpmnHostCommand.StartWork"/> on the command list, keeps every other command
    /// where it was, and returns what was withdrawn. Commands are returned rather than executed, so nothing
    /// withdrawn here has reached the host: work activated and ended in the same instant is never started.
    /// </summary>
    public IReadOnlyList<BpmnHostCommand.StartWork> WithdrawStartedWork()
    {
        var withdrawn = _commands.OfType<BpmnHostCommand.StartWork>().ToArray();
        _commands.RemoveAll(command => command is BpmnHostCommand.StartWork);
        StartedWork = false;
        return withdrawn;
    }

    /// <summary>
    /// Withdraws the <see cref="BpmnHostCommand.StartWork"/> this evaluation issued for <paramref name="tokenId"/>,
    /// if it issued one, keeps every other command where it was, and returns what was withdrawn. A token is started
    /// at most once, so there is at most one. Called when the same evaluation cancels that token: the host has not
    /// seen the command yet, so the work is never started rather than started with no token waiting for it.
    /// </summary>
    public BpmnHostCommand.StartWork? WithdrawStartedWorkFor(string tokenId)
    {
        var index = _commands.FindIndex(command =>
            command is BpmnHostCommand.StartWork start && StringComparer.Ordinal.Equals(start.TokenId, tokenId));
        if (index < 0)
            return null;

        var withdrawn = (BpmnHostCommand.StartWork)_commands[index];
        _commands.RemoveAt(index);
        StartedWork = _commands.Exists(command => command is BpmnHostCommand.StartWork);
        return withdrawn;
    }

    /// <summary>
    /// Asks the host to tear running work down, or to signal the enclosing scope. A teardown is only ever added
    /// on a non-fault continuation: one that has been carried but not staged is discarded when the process
    /// faults, because the whole scope is going away and a premature teardown would race the fault. The one
    /// command added mid-propagation is the signal an escalation throw raises, which happened before any
    /// verdict and so rides whichever continuation follows, except a fault at the propagation limit, which
    /// withdraws it.
    /// </summary>
    public void AddCommand(BpmnHostCommand command) => _commands.Add(command);

    /// <summary>
    /// Teardowns discovered inside the token propagation loop, which the evaluation result cannot carry out:
    /// the loop keeps only each step's state and returns a fresh result, so anything else a step carried is
    /// dropped. They are held here instead, and issued under exactly the same rule as the result-carried ones
    /// — at the clean exit only, never on a fault.
    /// </summary>
    public IReadOnlyList<PendingTeardown> CarriedTeardowns => _carriedTeardowns;

    /// <summary>Carries a teardown to the clean exit, for a step whose result never reaches the caller.</summary>
    public void CarryTeardown(PendingTeardown teardown) => _carriedTeardowns.Add(teardown);

    /// <summary>
    /// How many tokens this evaluation has dispatched to an element behavior on arrival. Counted here rather
    /// than in the propagation loop so the limit holds for the evaluation as a whole, however many times it
    /// enters that loop.
    /// </summary>
    public int PropagationSteps { get; private set; }

    /// <summary>The element of the token most recently dispatched, or <c>null</c> before the first.</summary>
    public string? LastPropagatedElementId { get; private set; }

    /// <summary>
    /// Withdraws every <see cref="BpmnHostCommand.SignalEnclosingScope"/> on the command list and returns how many
    /// there were. Only the propagation limit does this; every other continuation keeps the signals an
    /// escalation throw raised.
    /// </summary>
    public int WithdrawScopeSignals() => _commands.RemoveAll(command => command is BpmnHostCommand.SignalEnclosingScope);

    /// <summary>Records one token dispatched to the behavior of <paramref name="elementId"/>.</summary>
    public void CountPropagationStep(string elementId)
    {
        PropagationSteps++;
        LastPropagatedElementId = elementId;
    }
}
