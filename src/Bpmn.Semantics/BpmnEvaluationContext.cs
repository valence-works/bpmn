using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>
/// One running unit of work to tear down: a losing event-based-gateway branch, an interrupted boundary
/// host, a retired listener, the work a cancelled transaction abandons. Carried — on the evaluation result,
/// or on the evaluation context when the result cannot reach the caller — and issued only on a non-fault
/// continuation.
/// </summary>
internal sealed record PendingTeardown(string Handle, string ElementId, string Reason);

/// <summary>
/// The mutable scratch space of one interpreter evaluation: the host snapshot it reads, and the command list
/// it builds. It exists so the interpreter can stay a pure function of its request — nothing here reaches the
/// host, and nothing here survives the evaluation.
/// </summary>
internal sealed class BpmnEvaluationContext(BpmnHostSnapshot host)
{
    private readonly List<BpmnHostCommand> _commands = [];
    private readonly List<PendingTeardown> _carriedTeardowns = [];

    /// <summary>What the host told the interpreter about its world.</summary>
    public BpmnHostSnapshot Host { get; } = host;

    /// <summary>The commands produced so far, in order.</summary>
    public IReadOnlyList<BpmnHostCommand> Commands => _commands;

    /// <summary>
    /// Whether this evaluation already asked the host to start work. A terminal continuation may not be
    /// returned from an evaluation that also started work, so the verdict is carried on the state instead
    /// (see <see cref="BpmnExecutionState.Terminated"/> and <see cref="BpmnExecutionState.PendingFault"/>).
    /// </summary>
    public bool StartedWork { get; private set; }

    /// <summary>Asks the host to start a unit of work.</summary>
    public void AddStartWork(BpmnHostCommand.StartWork command)
    {
        _commands.Add(command);
        StartedWork = true;
    }

    /// <summary>
    /// Asks the host to tear running work down, or to signal the enclosing scope. Only ever called from a
    /// non-fault continuation: teardown that has been carried but not staged is discarded when the process
    /// faults, because the whole scope is going away and a premature teardown would race the fault.
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
}
