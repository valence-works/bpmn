using Bpmn.Semantics;

namespace Bpmn.Runtime.InMemory;

/// <summary>
/// The host's <see cref="IBpmnConditionEvaluator"/>: a table of answers keyed by sequence flow id, with an
/// optional function for every flow the table does not name. There is deliberately no expression language
/// here, just as there is none in the library: a test or a sample says what each condition evaluates to,
/// either as a fixed answer or as a function of the request.
/// <para>
/// A flow nobody answers fails, so a missing entry faults the scope with <c>bpmn.condition.failed</c> rather
/// than quietly routing one way. Every request is recorded in <see cref="Requests"/>, in the order asked, so a
/// test can assert which conditions were evaluated and which were not.
/// </para>
/// <example>
/// <code>
/// var conditions = new InMemoryConditions()
///     .When("toApprove", holds: true)
///     .When("toReject", request => BpmnConditionResult.Failed("orders.unknown-region", "no region"));
///
/// var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions { Conditions = conditions });
/// </code>
/// </example>
/// </summary>
public sealed class InMemoryConditions : IBpmnConditionEvaluator
{
    private readonly Dictionary<string, Func<BpmnConditionRequest, BpmnConditionResult>> _answers = new(StringComparer.Ordinal);
    private readonly Func<BpmnConditionRequest, BpmnConditionResult>? _otherwise;
    private readonly List<BpmnConditionRequest> _requests = [];

    /// <summary>Creates an empty table. Every flow fails until it is answered with <see cref="When(string, bool)"/>.</summary>
    public InMemoryConditions()
    {
    }

    /// <summary>Creates a table that answers every flow it does not name with <paramref name="otherwise"/>.</summary>
    public InMemoryConditions(Func<BpmnConditionRequest, BpmnConditionResult> otherwise)
    {
        ArgumentNullException.ThrowIfNull(otherwise);
        _otherwise = otherwise;
    }

    /// <summary>Every request this evaluator answered, in the order asked.</summary>
    public IReadOnlyList<BpmnConditionRequest> Requests => _requests;

    /// <summary>Answers <paramref name="flowId"/>'s condition with a fixed true or false.</summary>
    public InMemoryConditions When(string flowId, bool holds) => When(flowId, BpmnConditionResult.Of(holds));

    /// <summary>Answers <paramref name="flowId"/>'s condition with a fixed result, which may be a failure.</summary>
    public InMemoryConditions When(string flowId, BpmnConditionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return When(flowId, _ => result);
    }

    /// <summary>Answers <paramref name="flowId"/>'s condition with a function of the request.</summary>
    public InMemoryConditions When(string flowId, Func<BpmnConditionRequest, BpmnConditionResult> answer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        ArgumentNullException.ThrowIfNull(answer);
        _answers[flowId] = answer;
        return this;
    }

    /// <inheritdoc />
    public BpmnConditionResult Evaluate(BpmnConditionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Add(request);

        if (_answers.TryGetValue(request.FlowId, out var answer))
            return answer(request);

        return _otherwise is { } otherwise
            ? otherwise(request)
            : BpmnConditionResult.Failed(null, $"No answer is configured for the condition on flow '{request.FlowId}' ('{request.Body}').");
    }
}
