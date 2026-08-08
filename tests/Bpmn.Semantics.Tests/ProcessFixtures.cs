using Bpmn.Model;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The process definitions the host tests drive. They are built in code rather than read from XML so a test
/// reads as the shape it is asserting on, and so a failure points at the semantics rather than at the reader.
/// </summary>
public static class ProcessFixtures
{
    public const string TimerType = BpmnEventDefinitionTypes.Timer;

    /// <summary>Start -> Work -> End. The smallest thing that can complete.</summary>
    public static BpmnProcessDefinition Linear() =>
        new BpmnProcessBuilder("linear")
            .StartEvent("Start")
            .Task("Work", bindingRef: "work")
            .EndEvent("End")
            .ConnectSequence("Start", "Work", "End")
            .Build();

    /// <summary>A decision gateway that routes to exactly one of two branches on the outcome its work reports.</summary>
    public static BpmnProcessDefinition ExclusiveChoice() =>
        new BpmnProcessBuilder("exclusive")
            .StartEvent("Start")
            .Element(new BpmnElement("Decide", BpmnElementTypes.ExclusiveGateway, bindingRef: "decide"))
            .Task("Approve", bindingRef: "approve")
            .Task("Reject", bindingRef: "reject")
            .EndEvent("EndApproved")
            .EndEvent("EndRejected")
            .Connect("Start", "Decide")
            .Connect("Decide", "Approve", condition: "Approved")
            .Connect("Decide", "Reject", condition: "Rejected")
            .Connect("Approve", "EndApproved")
            .Connect("Reject", "EndRejected")
            .Build();

    /// <summary>A parallel split into two branches and a join that only fires once both arrive.</summary>
    public static BpmnProcessDefinition ParallelSplitAndJoin() =>
        new BpmnProcessBuilder("parallel")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("Left", bindingRef: "left")
            .Task("Right", bindingRef: "right")
            .ParallelGateway("Join")
            .EndEvent("End")
            .Connect("Start", "Split")
            .Connect("Split", "Left")
            .Connect("Split", "Right")
            .Connect("Left", "Join")
            .Connect("Right", "Join")
            .Connect("Join", "End")
            .Build();

    /// <summary>A long-running activity with a timer boundary event; <paramref name="interrupting"/> decides what firing does to it.</summary>
    public static BpmnProcessDefinition TimerBoundary(string isoDuration = "P7D", bool interrupting = true) =>
        new BpmnProcessBuilder("timer-boundary")
            .StartEvent("Start")
            .Task("Long", bindingRef: "long")
            .EndEvent("EndNormal")
            .BoundaryEvent("Timeout", "Long", Timer(isoDuration), interrupting, bindingRef: "timeout")
            .Task("Escalate", bindingRef: "escalate")
            .EndEvent("EndTimedOut")
            .ConnectSequence("Start", "Long", "EndNormal")
            .ConnectSequence("Timeout", "Escalate", "EndTimedOut")
            .Build();

    /// <summary>An activity with a non-interrupting message boundary event, so firing it leaves the activity running.</summary>
    public static BpmnProcessDefinition NonInterruptingMessageBoundary() =>
        new BpmnProcessBuilder("non-interrupting")
            .StartEvent("Start")
            .Task("Long", bindingRef: "long")
            .EndEvent("EndNormal")
            .BoundaryEvent("Nudged", "Long", Message("nudge"), interrupting: false, bindingRef: "nudge")
            .Task("Notify", bindingRef: "notify")
            .EndEvent("EndNotified")
            .ConnectSequence("Start", "Long", "EndNormal")
            .ConnectSequence("Nudged", "Notify", "EndNotified")
            .Build();

    /// <summary>A multi-instance activity that runs a fixed number of times, in parallel or one at a time.</summary>
    public static BpmnProcessDefinition MultiInstanceByCardinality(int cardinality, bool sequential = false) =>
        new BpmnProcessBuilder("multi-instance")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: sequential, cardinality: cardinality))
            .EndEvent("End")
            .ConnectSequence("Start", "Review", "End")
            .Build();

    /// <summary>A multi-instance activity that runs once per item of a declared collection variable.</summary>
    public static BpmnProcessDefinition MultiInstanceByCollection(string variableName = "reviewers") =>
        new BpmnProcessBuilder("multi-instance-collection")
            .Variable(variableName)
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(collectionVariable: variableName, itemVariable: "reviewer"))
            .EndEvent("End")
            .ConnectSequence("Start", "Review", "End")
            .Build();

    /// <summary>A risky activity, optionally with an error boundary event to catch its failure.</summary>
    public static BpmnProcessDefinition RiskyActivity(bool withErrorBoundary)
    {
        var builder = new BpmnProcessBuilder("risky")
            .StartEvent("Start")
            .Task("Risky", bindingRef: "risky")
            .EndEvent("EndOk")
            .ConnectSequence("Start", "Risky", "EndOk");

        if (!withErrorBoundary)
            return builder.Build();

        return builder
            .BoundaryEvent("Failed", "Risky", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndRecovered")
            .ConnectSequence("Failed", "Recover", "EndRecovered")
            .Build();
    }

    /// <summary>A subprocess whose body this host runs as a nested scope, with the body's own activity inside it.</summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) NestedSubProcess()
    {
        var body = new BpmnProcessBuilder("body")
            .StartEvent("BodyStart")
            .Task("Inner", bindingRef: "inner")
            .EndEvent("BodyEnd")
            .ConnectSequence("BodyStart", "Inner", "BodyEnd")
            .Build();

        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("End")
            .ConnectSequence("Start", "Sub", "End")
            .Build();

        return (parent, body);
    }

    /// <summary>A subprocess with an interrupting timer boundary, so firing it must tear the whole nested scope down.</summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) NestedSubProcessWithTimerBoundary(string isoDuration = "PT1H")
    {
        var body = new BpmnProcessBuilder("body")
            .StartEvent("BodyStart")
            .Task("Inner", bindingRef: "inner")
            .EndEvent("BodyEnd")
            .ConnectSequence("BodyStart", "Inner", "BodyEnd")
            .Build();

        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("End")
            .BoundaryEvent("SubTimeout", "Sub", Timer(isoDuration), interrupting: true, bindingRef: "sub-timeout")
            .EndEvent("EndTimedOut")
            .ConnectSequence("Start", "Sub", "End")
            .ConnectSequence("SubTimeout", "EndTimedOut")
            .Build();

        return (parent, body);
    }

    /// <summary>
    /// A subprocess whose body escalates part-way through and then keeps running, with an interrupting
    /// escalation boundary on the parent. Exercises scope signalling and subtree cancellation together.
    /// </summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) EscalatingSubProcess(string code = "overdue")
    {
        var body = new BpmnProcessBuilder("body")
            .StartEvent("BodyStart")
            .IntermediateThrowEvent("Escalate", Escalation(code))
            .Task("Inner", bindingRef: "inner")
            .EndEvent("BodyEnd")
            .ConnectSequence("BodyStart", "Escalate", "Inner", "BodyEnd")
            .Build();

        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("End")
            .BoundaryEvent("Escalated", "Sub", Escalation(code), interrupting: true)
            .EndEvent("EndEscalated")
            .ConnectSequence("Start", "Sub", "End")
            .ConnectSequence("Escalated", "EndEscalated")
            .Build();

        return (parent, body);
    }

    /// <summary>
    /// A parallel split where one branch is decided away, so the join can never receive its second arrival.
    /// The classic modelling mistake, and what a deadlock search should find.
    /// </summary>
    public static BpmnProcessDefinition JoinThatCanStarve() =>
        new BpmnProcessBuilder("starving-join")
            .StartEvent("Start")
            .Element(new BpmnElement("Decide", BpmnElementTypes.ExclusiveGateway, bindingRef: "decide"))
            .Task("Left", bindingRef: "left")
            .Task("Right", bindingRef: "right")
            .ParallelGateway("Join")
            .EndEvent("End")
            .Connect("Start", "Decide")
            .Connect("Decide", "Left", condition: "Left")
            .Connect("Decide", "Right", condition: "Right")
            .Connect("Left", "Join")
            .Connect("Right", "Join")
            .Connect("Join", "End")
            .Build();

    public static BpmnEventDefinition Timer(string isoDuration) =>
        new(BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Interval] = isoDuration });

    public static BpmnEventDefinition Escalation(string code) =>
        new(BpmnEventDefinitionTypes.Escalation,
            new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code });

    public static BpmnEventDefinition Message(string name) =>
        new(BpmnEventDefinitionTypes.Message,
            new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Name] = name });
}
