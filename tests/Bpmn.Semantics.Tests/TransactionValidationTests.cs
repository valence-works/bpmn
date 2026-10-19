using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class TransactionValidationTests
{
    [Fact]
    public void IsTransaction_OnNonSubProcess_IsRejected()
    {
        var process = Definition(
            [
                new("Start", BpmnElementTypes.StartEvent),
                new("Work", BpmnElementTypes.Task, bindingRef: "work", isTransaction: true),
                new("End", BpmnElementTypes.EndEvent)
            ],
            [new("start-work", "Start", "Work"), new("work-end", "Work", "End")]);

        Rejects(process, "only a subprocess element may be a transaction");
    }

    [Fact]
    public void IsTransaction_WithLoopCharacteristics_IsRejected()
    {
        var process = Definition(
            [
                new("Start", BpmnElementTypes.StartEvent),
                new("Transaction", BpmnElementTypes.SubProcess, bindingRef: "transaction",
                    loopCharacteristics: new BpmnLoopCharacteristics(isSequential: true, cardinality: 2), isTransaction: true),
                new("End", BpmnElementTypes.EndEvent)
            ],
            [new("start-transaction", "Start", "Transaction"), new("transaction-end", "Transaction", "End")]);

        Rejects(process, "cannot carry multi-instance loop characteristics");
    }

    [Fact]
    public void CancelEnd_OutsideTransactionStructure_IsRejected()
    {
        var process = Definition(
            [new("Start", BpmnElementTypes.StartEvent), AdvancedScenarioFixtures.CancelEnd("CancelEnd")],
            [new("start-cancel", "Start", "CancelEnd")]);

        Rejects(process, "only valid inside a transaction");
    }

    [Fact]
    public void CancelBoundary_OnNonTransactionHost_IsRejected()
    {
        var process = Definition(
            [
                new("Start", BpmnElementTypes.StartEvent),
                new("Work", BpmnElementTypes.Task, bindingRef: "work"),
                AdvancedScenarioFixtures.CancelBoundary("Cancelled", "Work"),
                new("NormalEnd", BpmnElementTypes.EndEvent),
                new("CancelledEnd", BpmnElementTypes.EndEvent)
            ],
            [
                new("start-work", "Start", "Work"),
                new("work-end", "Work", "NormalEnd"),
                new("cancelled-end", "Cancelled", "CancelledEnd")
            ]);

        Rejects(process, "which is not a transaction");
    }

    [Fact]
    public void TwoCancelBoundaries_OnOneTransactionHost_IsRejected()
    {
        var process = Definition(
            [
                new("Start", BpmnElementTypes.StartEvent),
                new("Transaction", BpmnElementTypes.SubProcess, bindingRef: "transaction", isTransaction: true),
                AdvancedScenarioFixtures.CancelBoundary("CancelledA", "Transaction"),
                AdvancedScenarioFixtures.CancelBoundary("CancelledB", "Transaction"),
                new("NormalEnd", BpmnElementTypes.EndEvent),
                new("CancelledEndA", BpmnElementTypes.EndEvent),
                new("CancelledEndB", BpmnElementTypes.EndEvent)
            ],
            [
                new("start-transaction", "Start", "Transaction"),
                new("transaction-end", "Transaction", "NormalEnd"),
                new("cancel-a-end", "CancelledA", "CancelledEndA"),
                new("cancel-b-end", "CancelledB", "CancelledEndB")
            ]);

        Rejects(process, "more than one cancel boundary event");
    }

    [Fact]
    public void CancelBoundary_WithoutOutboundFlow_IsRejected()
    {
        var process = Definition(
            [
                new("Start", BpmnElementTypes.StartEvent),
                new("Transaction", BpmnElementTypes.SubProcess, bindingRef: "transaction", isTransaction: true),
                AdvancedScenarioFixtures.CancelBoundary("Cancelled", "Transaction"),
                new("End", BpmnElementTypes.EndEvent)
            ],
            [new("start-transaction", "Start", "Transaction"), new("transaction-end", "Transaction", "End")]);

        Rejects(process, "must have at least one outbound sequence flow");
    }

    [Fact]
    public void ValidNestedTransaction_WithCancelEnd_Validates()
    {
        var process = Definition(
            [new("Start", BpmnElementTypes.StartEvent), AdvancedScenarioFixtures.CancelEnd("CancelEnd")],
            [new("start-cancel", "Start", "CancelEnd")],
            isTransaction: true);

        var graph = AdvancedScenarioFixtures.Graph(process);

        graph.IsTransaction.ShouldBeTrue();
        graph.GetRequiredElement("CancelEnd").ElementType.ShouldBe(BpmnElementTypes.EndEvent);
    }

    [Fact]
    public void ValidParentTransaction_WithCancelBoundary_Validates()
    {
        var transactionBody = Definition(
            [new("InnerStart", BpmnElementTypes.StartEvent), AdvancedScenarioFixtures.CancelEnd("InnerCancel")],
            [new("inner-cancel", "InnerStart", "InnerCancel")],
            isTransaction: true);
        var parent = Definition(
            [
                new("Start", BpmnElementTypes.StartEvent),
                new("Transaction", BpmnElementTypes.SubProcess, bindingRef: "transaction", isTransaction: true),
                AdvancedScenarioFixtures.CancelBoundary("Cancelled", "Transaction"),
                new("NormalEnd", BpmnElementTypes.EndEvent),
                new("CancelledEnd", BpmnElementTypes.EndEvent)
            ],
            [
                new("start-transaction", "Start", "Transaction"),
                new("transaction-end", "Transaction", "NormalEnd"),
                new("cancelled-end", "Cancelled", "CancelledEnd")
            ]);

        var graph = AdvancedScenarioFixtures.Graph(parent,
            bindingRef => StringComparer.Ordinal.Equals(bindingRef, "transaction") ? transactionBody : null);

        graph.AttachedCancelBoundary("Transaction").ShouldNotBeNull();
        graph.AttachedCatchBoundaries("Transaction").ShouldBeEmpty();
    }

    private static void Rejects(BpmnProcessDefinition process, string expectedMessage)
    {
        var exception = Should.Throw<BpmnExecutionException>(() => AdvancedScenarioFixtures.Graph(process));
        exception.Message.ShouldContain(expectedMessage, Case.Sensitive);
    }

    private static BpmnProcessDefinition Definition(
        IReadOnlyCollection<BpmnElement> elements,
        IReadOnlyCollection<BpmnSequenceFlow> flows,
        bool isTransaction = false) =>
        new("transaction-validation", Elements: elements.ToArray(), SequenceFlows: flows.ToArray(), IsTransaction: isTransaction);
}
