using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class RefusedVariableReadTests
{
    private const string CollectionVariable = "reviewers";
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_refused_collection_read_faults_with_the_hosts_exact_code_and_message(bool sequential)
    {
        const string faultCode = "storage.read-refused";
        const string faultMessage = "The collection is outside this scope's readable partition.";
        var variables = new InMemoryVariables().SetRefused(CollectionVariable, faultCode, faultMessage);

        var evaluation = Start(CollectionLoop(sequential), variables);

        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe(faultCode);
        fault.Message.ShouldBe(faultMessage);
        evaluation.State.Diagnostics.Last().Message.ShouldBe(faultMessage);
        AssertNoIterationStarted(evaluation);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, " \t")]
    [InlineData(true, " \t")]
    public void A_blank_refusal_message_is_preserved_on_the_fault_with_a_nonblank_diagnostic(bool sequential, string message)
    {
        const string faultCode = "storage.read-refused";
        var variables = new InMemoryVariables().SetRefused(CollectionVariable, faultCode, message);

        var evaluation = Start(CollectionLoop(sequential), variables);

        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe(faultCode);
        fault.Message.ShouldBe(message);
        evaluation.State.Diagnostics.Last().Message.ShouldBe(
            "BPMN multi-instance element 'Review' collection variable 'reviewers' was refused by the host.");
        AssertNoIterationStarted(evaluation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void A_refused_collection_read_without_a_stable_code_uses_the_existing_unreadable_code(string? faultCode)
    {
        const string faultMessage = "The host refused this collection read.";
        var value = new BpmnValue(BpmnValuePresence.Refused)
        {
            FaultCode = faultCode,
            FaultMessage = faultMessage
        };
        var variables = new InMemoryVariables().Set(CollectionVariable, value);

        var evaluation = Start(CollectionLoop(sequential: false), variables);

        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe(BpmnInterpreter.CollectionUnreadableFaultCode);
        fault.Message.ShouldBe(faultMessage);
        evaluation.State.Diagnostics.Last().Message.ShouldBe(faultMessage);
        AssertNoIterationStarted(evaluation);
    }

    [Fact]
    public void A_refused_collection_read_without_a_message_uses_a_nonempty_safe_diagnostic()
    {
        var value = new BpmnValue(BpmnValuePresence.Refused)
        {
            FaultCode = "storage.read-refused"
        };
        var variables = new InMemoryVariables().Set(CollectionVariable, value);

        var evaluation = Start(CollectionLoop(sequential: false), variables);

        var fault = evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>();
        fault.Code.ShouldBe("storage.read-refused");
        fault.Message.Trim().ShouldNotBeEmpty();
        evaluation.State.Diagnostics.Last().Message.ShouldBe(fault.Message);
        AssertNoIterationStarted(evaluation);
    }

    private static BpmnEvaluation Start(BpmnProcessDefinition definition, IBpmnVariableReader variables)
    {
        var graph = BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full);
        var host = BpmnHostSnapshot.Root("scope") with { Variables = variables };
        return Interpreter.Start(new BpmnStartRequest(graph, PriorState: null, Host: host));
    }

    private static BpmnProcessDefinition CollectionLoop(bool sequential) =>
        new BpmnProcessBuilder("refused-collection-read")
            .Variable(CollectionVariable)
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(
                    isSequential: sequential,
                    collectionVariable: CollectionVariable,
                    itemVariable: "reviewer"))
            .EndEvent("End")
            .ConnectSequence("Start", "Review", "End")
            .Build();

    private static void AssertNoIterationStarted(BpmnEvaluation evaluation)
    {
        evaluation.Continuation.ShouldBeOfType<BpmnContinuation.Fault>().ErrorCode.ShouldBeNull(
            "a host variable-read refusal is not a BPMN error");
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>().ShouldBeEmpty(
            "a refused collection read must fault before issuing an iteration command");
        evaluation.State.Loops.ShouldBeEmpty("a refused collection read must not create a loop");
    }
}
