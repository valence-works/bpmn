using Bpmn.Model.State;

namespace Bpmn.Semantics;

/// <summary>
/// The mutable scratch space of one interpreter evaluation: the host snapshot it reads, and the command list
/// it builds. It exists so the interpreter can stay a pure function of its request — nothing here reaches the
/// host, and nothing here survives the evaluation.
/// </summary>
internal sealed class BpmnEvaluationContext(BpmnHostSnapshot host)
{
    private readonly List<BpmnHostCommand> _commands = [];

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
}
