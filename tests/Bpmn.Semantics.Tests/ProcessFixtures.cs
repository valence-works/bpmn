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

    /// <summary>
    /// <see cref="RiskyActivity"/> with a preparation task ahead of the risky one, so by the time <c>Risky</c> runs
    /// the scope holds a consumed token of its own.
    /// </summary>
    public static BpmnProcessDefinition RiskyActivityAfterPreparation() =>
        new BpmnProcessBuilder("prepared-risky")
            .StartEvent("Start")
            .Task("Prep", bindingRef: "prep")
            .Task("Risky", bindingRef: "risky")
            .EndEvent("EndOk")
            .BoundaryEvent("Failed", "Risky", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndRecovered")
            .ConnectSequence("Start", "Prep", "Risky", "EndOk")
            .ConnectSequence("Failed", "Recover", "EndRecovered")
            .Build();

    /// <summary>A risky activity with no boundary, whose failure the scope's error event subprocess catches.</summary>
    public static (BpmnProcessDefinition Body, BpmnProcessDefinition Handler) RiskyActivityWithErrorEventSubprocess()
    {
        var handler = new BpmnProcessBuilder("on-error")
            .StartEvent("ErrorStart", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("ErrorStart", "Handle", "HandlerEnd")
            .Build();

        var body = new BpmnProcessBuilder("risky-with-handler")
            .StartEvent("Start")
            .Task("Risky", bindingRef: "risky")
            .EndEvent("EndOk")
            .SubProcess("OnError", bindingRef: "on-error", triggeredByEvent: true)
            .ConnectSequence("Start", "Risky", "EndOk")
            .Build();

        return (body, handler);
    }

    /// <summary>A parallel multi-instance task under an error boundary, so one instance's failure interrupts the others.</summary>
    public static BpmnProcessDefinition ParallelMultiInstanceWithErrorBoundary(int cardinality = 3) =>
        new BpmnProcessBuilder("multi-instance-risky")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: cardinality))
            .EndEvent("EndOk")
            .BoundaryEvent("Failed", "Review", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndRecovered")
            .ConnectSequence("Start", "Review", "EndOk")
            .ConnectSequence("Failed", "Recover", "EndRecovered")
            .Build();

    /// <summary>
    /// A subprocess bound to <c>body</c> behind <paramref name="padTasks"/> tasks. Every element ahead of the
    /// subprocess moves the id of the parent's token parked on it, and that id travels into the nested scope on
    /// its invocation correlation.
    /// </summary>
    public static BpmnProcessDefinition SubProcessAfterPads(int padTasks)
    {
        var builder = new BpmnProcessBuilder("padded-parent").StartEvent("Start");
        var chain = new List<string> { "Start" };
        for (var index = 0; index < padTasks; index++)
        {
            builder.Task($"Pad{index}", bindingRef: $"pad{index}");
            chain.Add($"Pad{index}");
        }

        return builder
            .SubProcess("Sub", bindingRef: "body")
            .EndEvent("End")
            .ConnectSequence([.. chain, "Sub", "End"])
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

    /// <summary>A subprocess with an escalation boundary on the parent, and a body that escalates nothing by itself.</summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) SubProcessWithEscalationBoundary(
        string code = "overdue",
        bool interrupting = true)
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
            .BoundaryEvent("Escalated", "Sub", Escalation(code), interrupting)
            .EndEvent("EndEscalated")
            .ConnectSequence("Start", "Sub", "End")
            .ConnectSequence("Escalated", "EndEscalated")
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

    /// <summary>
    /// A transaction subprocess that cancels itself while a compensation replay is already in flight, which is
    /// the shape that puts two units of work on one slot: stopping the in-flight run releases its unrun
    /// compensable, and the cancel's own run then re-claims it and restarts the same handler.
    /// <para>
    /// The body splits in two (three with <paramref name="withThirdBranch"/>): one branch reserves and then
    /// throws a compensate event, parking on the replay; another completes a task and hits the cancel end event.
    /// The third branch, when present, is work that is still genuinely in flight and has nothing to do with the
    /// slot collision — it is what separates tearing down everything from tearing down only the re-used slot.
    /// </para>
    /// </summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) CancellableTransaction(bool withThirdBranch = false)
    {
        var body = new BpmnProcessBuilder("booking")
            .Transaction()
            .StartEvent("BodyStart")
            .ParallelGateway("Split")
            .Task("Reserve", bindingRef: "reserve")
            .IntermediateThrowEvent("Replay", new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation))
            .EndEvent("EndReserved")
            .Task("Trigger", bindingRef: "trigger")
            .EndEvent("CancelEnd", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Cancel))
            .Connect("BodyStart", "Split")
            .Connect("Split", "Reserve")
            .ConnectSequence("Reserve", "Replay", "EndReserved")
            .Connect("Split", "Trigger")
            .ConnectSequence("Trigger", "CancelEnd");

        CompensateReserve(body);

        if (withThirdBranch)
            body.Task("Audit", bindingRef: "audit")
                .EndEvent("EndAudited")
                .Connect("Split", "Audit")
                .ConnectSequence("Audit", "EndAudited");

        return (CancellableTransactionParent(), body.Build());
    }

    /// <summary>
    /// The same transaction cancelling with nothing else live: one sequential branch, so when the cancel end
    /// event fires the only work the scope starts is the compensation the cancel itself replays.
    /// </summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) CancellableTransactionWithNothingElseLive()
    {
        var body = new BpmnProcessBuilder("booking")
            .Transaction()
            .StartEvent("BodyStart")
            .Task("Reserve", bindingRef: "reserve")
            .Task("Trigger", bindingRef: "trigger")
            .EndEvent("CancelEnd", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Cancel))
            .ConnectSequence("BodyStart", "Reserve", "Trigger", "CancelEnd");

        CompensateReserve(body);

        return (CancellableTransactionParent(), body.Build());
    }

    /// <summary>Registers <c>Reserve</c> for compensation by <c>UndoReserve</c>: the boundary event and the handler it names.</summary>
    private static void CompensateReserve(BpmnProcessBuilder body) =>
        body.Element(new BpmnElement(
                "ReserveCompensation", BpmnElementTypes.BoundaryEvent,
                attachedToRef: "Reserve",
                compensationHandlerElementId: "UndoReserve",
                eventDefinitions: [new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation)]))
            .Element(new BpmnElement(
                "UndoReserve", BpmnElementTypes.Task,
                bindingRef: "undo-reserve",
                isForCompensation: true));

    /// <summary>The enclosing scope for a cancellable transaction: a cancel boundary routes the cancellation to recovery.</summary>
    private static BpmnProcessDefinition CancellableTransactionParent() =>
        new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .SubProcess("Booking", bindingRef: "booking", isTransaction: true)
            .EndEvent("EndDone")
            .BoundaryEvent("BookingCancelled", "Booking", new BpmnEventDefinition(BpmnEventDefinitionTypes.Cancel))
            .Task("Recover", bindingRef: "recover")
            .EndEvent("EndCancelled")
            .ConnectSequence("Start", "Booking", "EndDone")
            .ConnectSequence("BookingCancelled", "Recover", "EndCancelled")
            .Build();

    /// <summary>
    /// A scope that escalates to its own interrupting event subprocess while a second branch is still working.
    /// The throw sits behind a task so the escalation fires only once the other branch's work is genuinely live.
    /// The activation drains the scope, so the branch's task is abandoned mid-flight and must be torn down on
    /// the host: nothing will ever complete it, and the scope that started it is being interrupted.
    /// </summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) OwnScopeEscalationInterruptsASecondBranch(string code = "overdue")
    {
        var body = new BpmnProcessBuilder("on-overdue")
            .StartEvent("HandlerStart", null, Escalation(code))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("HandlerStart", "Handle", "HandlerEnd")
            .Build();

        var parent = new BpmnProcessBuilder("escalating-scope")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("Trigger", bindingRef: "escalate-trigger")
            .IntermediateThrowEvent("Escalate", Escalation(code))
            .EndEvent("EndEscalated")
            .Task("LongRunning", bindingRef: "long-running")
            .EndEvent("EndWorked")
            .SubProcess("OnOverdue", bindingRef: "on-overdue", triggeredByEvent: true)
            .Connect("Start", "Split")
            .Connect("Split", "Trigger")
            .ConnectSequence("Trigger", "Escalate", "EndEscalated")
            .Connect("Split", "LongRunning")
            .ConnectSequence("LongRunning", "EndWorked")
            .Build();

        return (parent, body);
    }

    /// <summary>
    /// A parallel split into a long-running task and an escalation throw that the scope's own event subprocess
    /// catches. The split emits the task first, so its work is activated earlier in the same evaluation in which
    /// the escalation is caught. An <paramref name="interrupting"/> catcher stops that task again before the host
    /// has seen it; a non-interrupting one leaves it running alongside the handler.
    /// </summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) SplitIntoWorkAndOwnScopeEscalation(
        bool interrupting = true,
        string code = "overdue")
    {
        var body = new BpmnProcessBuilder("on-overdue")
            .Element(new BpmnElement("HandlerStart", BpmnElementTypes.StartEvent, cancelActivity: interrupting, eventDefinitions: [Escalation(code)]))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("HandlerStart", "Handle", "HandlerEnd")
            .Build();

        var parent = new BpmnProcessBuilder("split-escalate")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("LongRunning", bindingRef: "long-running")
            .EndEvent("EndWorked")
            .IntermediateThrowEvent("Escalate", Escalation(code))
            .EndEvent("EndEscalated")
            .SubProcess("OnOverdue", bindingRef: "on-overdue", triggeredByEvent: true)
            .Connect("Start", "Split")
            .Connect("Split", "LongRunning")
            .Connect("Split", "Escalate")
            .ConnectSequence("LongRunning", "EndWorked")
            .ConnectSequence("Escalate", "EndEscalated")
            .Build();

        return (parent, body);
    }

    /// <summary>
    /// A transaction whose body splits into an audit task and a cancel end event, emitted in that order, so the
    /// cancel end event is reached in the same evaluation that activated the audit's work. With
    /// <paramref name="compensateFirst"/> the body first runs <c>Reserve</c>, which registers for compensation, so
    /// the cancel also starts a replay in that evaluation; without it there is nothing to replay and the
    /// transaction ends at once.
    /// </summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) TransactionCancelledBesideWorkItJustStarted(bool compensateFirst)
    {
        var body = new BpmnProcessBuilder("booking")
            .Transaction()
            .StartEvent("BodyStart")
            .ParallelGateway("Split")
            .Task("Audit", bindingRef: "audit")
            .EndEvent("EndAudited")
            .EndEvent("CancelEnd", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Cancel))
            .Connect("Split", "Audit")
            .Connect("Split", "CancelEnd")
            .Connect("Audit", "EndAudited");

        if (compensateFirst)
        {
            body.Task("Reserve", bindingRef: "reserve").ConnectSequence("BodyStart", "Reserve", "Split");
            CompensateReserve(body);
        }
        else
        {
            body.Connect("BodyStart", "Split");
        }

        return (CancellableTransactionParent(), body.Build());
    }

    /// <summary>
    /// A task whose interrupting message boundary forks back into the same task and into an escalation throw that
    /// the scope's own interrupting event subprocess catches. Firing the boundary tears the running task down and
    /// re-enters it, starting new work on the very (binding ref, iteration id) slot the torn-down unit still holds
    /// in the host's snapshot; the escalation, caught in the same propagation, then stops that new work again.
    /// </summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) InterruptedTaskReenteredAndEscalated(string code = "overdue")
    {
        var body = new BpmnProcessBuilder("on-overdue")
            .StartEvent("HandlerStart", null, Escalation(code))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("HandlerEnd")
            .ConnectSequence("HandlerStart", "Handle", "HandlerEnd")
            .Build();

        var parent = new BpmnProcessBuilder("reenter-escalate")
            .StartEvent("Start")
            .Task("Work", bindingRef: "work")
            .EndEvent("End")
            .BoundaryEvent("Interrupt", "Work", Message("interrupt"), interrupting: true, bindingRef: "interrupt")
            .ParallelGateway("Fork")
            .IntermediateThrowEvent("Escalate", Escalation(code))
            .EndEvent("EndEscalated")
            .SubProcess("OnOverdue", bindingRef: "on-overdue", triggeredByEvent: true)
            .ConnectSequence("Start", "Work", "End")
            .Connect("Interrupt", "Fork")
            .Connect("Fork", "Work")
            .Connect("Fork", "Escalate")
            .Connect("Escalate", "EndEscalated")
            .Build();

        return (parent, body);
    }

    /// <summary>
    /// A task retried by its own interrupting timer boundary, beside a second branch that keeps the scope running.
    /// The boundary routes back to a merge ahead of the task, so every timeout tears one pass's unit down and starts
    /// the next pass on the very same (binding ref, iteration id) slot under a new handle. With
    /// <paramref name="otherTerminates"/> the second branch ends in a terminate end event.
    /// </summary>
    public static BpmnProcessDefinition RetriedOnTimeout(bool otherTerminates = false) =>
        new BpmnProcessBuilder("retried-on-timeout")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .ExclusiveGateway("Retry")
            .Task("Long", bindingRef: "long")
            .EndEvent("EndLong")
            .BoundaryEvent("Timeout", "Long", Timer("PT1M"), interrupting: true, bindingRef: "timeout")
            .Task("Other", bindingRef: "other")
            .EndEvent("EndOther", null, otherTerminates ? [Terminate()] : [])
            .Connect("Start", "Split")
            .Connect("Split", "Retry")
            .Connect("Split", "Other")
            .ConnectSequence("Retry", "Long", "EndLong")
            .Connect("Timeout", "Retry")
            .Connect("Other", "EndOther")
            .Build();

    /// <summary>
    /// <see cref="RetriedOnTimeout"/> with a parallel multi-instance task in place of the single one, and an error
    /// boundary on it as well. Every timeout tears one pass's instances down and starts the next pass's; the error
    /// boundary makes a fault attributed to the wrong pass visible, because it would be caught there.
    /// </summary>
    public static BpmnProcessDefinition MultiInstanceRetriedOnTimeout(int cardinality) =>
        new BpmnProcessBuilder("multi-instance-retried-on-timeout")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .ExclusiveGateway("Retry")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: cardinality))
            .EndEvent("EndReview")
            .BoundaryEvent("Timeout", "Review", Timer("PT1M"), interrupting: true, bindingRef: "timeout")
            .BoundaryEvent("Failed", "Review", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .EndEvent("EndFailed")
            .Task("Other", bindingRef: "other")
            .EndEvent("EndOther")
            .Connect("Start", "Split")
            .Connect("Split", "Retry")
            .Connect("Split", "Other")
            .ConnectSequence("Retry", "Review", "EndReview")
            .Connect("Timeout", "Retry")
            .Connect("Failed", "EndFailed")
            .Connect("Other", "EndOther")
            .Build();

    /// <summary>
    /// A parallel multi-instance task under an interrupting message boundary, which tears every live instance down
    /// when it fires. The boundary path waits on a cleanup task, so the scope is still running afterwards.
    /// </summary>
    public static BpmnProcessDefinition InterruptibleParallelMultiInstance(int cardinality = 2) =>
        new BpmnProcessBuilder("interruptible-multi-instance")
            .StartEvent("Start")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: cardinality))
            .EndEvent("End")
            .BoundaryEvent("Stop", "Review", Message("stop"), interrupting: true, bindingRef: "stop")
            .Task("Cleanup", bindingRef: "cleanup")
            .EndEvent("EndStopped")
            .ConnectSequence("Start", "Review", "End")
            .ConnectSequence("Stop", "Cleanup", "EndStopped")
            .Build();

    /// <summary>
    /// A parallel split into two waiting tasks and a terminate end event. The order the split emits its branches
    /// decides whether the tasks' work is activated earlier in the same evaluation that then terminates
    /// (<paramref name="workFirst"/>), or whether the terminate cancels their tokens before they reach a task.
    /// </summary>
    public static BpmnProcessDefinition SplitIntoWorkAndTerminate(bool workFirst = true)
    {
        var builder = new BpmnProcessBuilder("split-terminate")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("Wait", bindingRef: "wait")
            .Task("Hold", bindingRef: "hold")
            .EndEvent("EndWaited")
            .EndEvent("EndHeld")
            .EndEvent("Terminate", null, Terminate())
            .Connect("Start", "Split");

        if (workFirst)
            builder.Connect("Split", "Wait").Connect("Split", "Hold").Connect("Split", "Terminate");
        else
            builder.Connect("Split", "Terminate").Connect("Split", "Wait").Connect("Split", "Hold");

        return builder
            .Connect("Wait", "EndWaited")
            .Connect("Hold", "EndHeld")
            .Build();
    }

    /// <summary>
    /// A parallel split into a waiting task and an exclusive gateway that can never choose: it binds no decision,
    /// declares no default, and both of its flows are conditional, so a token arriving there faults the process.
    /// The task is emitted first, so its work is activated earlier in the same evaluation that then faults.
    /// </summary>
    public static BpmnProcessDefinition SplitIntoWorkAndFault() =>
        WithUndecidableGateway(new BpmnProcessBuilder("split-fault")
                .StartEvent("Start")
                .ParallelGateway("Split")
                .Task("Wait", bindingRef: "wait")
                .EndEvent("EndWaited")
                .Connect("Start", "Split")
                .Connect("Split", "Wait")
                .Connect("Wait", "EndWaited"),
            from: "Split")
            .Build();

    /// <summary>
    /// A scope with a spread of work already running when a trigger branch ends it: a task under an interrupting
    /// timer boundary (so its listener is live too), a parallel multi-instance activity, a subprocess with a task
    /// of its own, and a message event subprocess's scope listener. Completing <c>trigger</c> reaches a terminate
    /// end event. With <paramref name="activatesWorkInTheSameInstant"/> the trigger first splits off a follow-up
    /// task, so the terminate is reached in the same evaluation that activated that task's work.
    /// </summary>
    public static (BpmnProcessDefinition Parent, IReadOnlyDictionary<string, BpmnProcessDefinition> Nested) TerminateBesideLiveWork(
        bool activatesWorkInTheSameInstant = false)
    {
        var parent = ScopeWithLiveWork("terminate-beside-live-work")
            .EndEvent("Terminate", null, Terminate());

        if (activatesWorkInTheSameInstant)
            parent.ParallelGateway("Fork")
                .Task("Followup", bindingRef: "followup")
                .EndEvent("EndFollowedUp")
                .Connect("Trigger", "Fork")
                .Connect("Fork", "Followup")
                .Connect("Fork", "Terminate")
                .Connect("Followup", "EndFollowedUp");
        else
            parent.Connect("Trigger", "Terminate");

        return (parent.Build(), ScopeWithLiveWorkNestedProcesses());
    }

    /// <summary>
    /// The same scope, but the trigger splits into a follow-up task and an exclusive gateway that can never
    /// choose, so the scope faults in the same evaluation that activated the follow-up's work.
    /// </summary>
    public static (BpmnProcessDefinition Parent, IReadOnlyDictionary<string, BpmnProcessDefinition> Nested) FaultBesideLiveWork()
    {
        var parent = ScopeWithLiveWork("fault-beside-live-work")
            .ParallelGateway("Fork")
            .Task("Followup", bindingRef: "followup")
            .EndEvent("EndFollowedUp")
            .Connect("Trigger", "Fork")
            .Connect("Fork", "Followup")
            .Connect("Followup", "EndFollowedUp");

        return (WithUndecidableGateway(parent, from: "Fork").Build(), ScopeWithLiveWorkNestedProcesses());
    }

    /// <summary>
    /// A subprocess whose body terminates in its start evaluation, beside a parent branch that keeps working.
    /// The body splits three ways, in this order: an escalation throw that signals the parent, a task, and a
    /// terminate end event. So by the time the terminate is reached, the same evaluation has already raised a
    /// scope signal and activated the task's work.
    /// </summary>
    public static (BpmnProcessDefinition Parent, BpmnProcessDefinition Body) SubProcessThatEscalatesThenTerminates(string code = "overdue")
    {
        var body = new BpmnProcessBuilder("terminating-body")
            .StartEvent("BodyStart")
            .ParallelGateway("BodySplit")
            .IntermediateThrowEvent("BodyEscalate", Escalation(code))
            .EndEvent("BodyEscalated")
            .Task("Inner", bindingRef: "inner")
            .EndEvent("BodyEnd")
            .EndEvent("BodyTerminate", null, Terminate())
            .Connect("BodyStart", "BodySplit")
            .Connect("BodySplit", "BodyEscalate")
            .Connect("BodySplit", "Inner")
            .Connect("BodySplit", "BodyTerminate")
            .Connect("BodyEscalate", "BodyEscalated")
            .Connect("Inner", "BodyEnd")
            .Build();

        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("EndSub")
            .Task("ParentWork", bindingRef: "parent-work")
            .EndEvent("EndParentWork")
            .Connect("Start", "Split")
            .Connect("Split", "Sub")
            .Connect("Split", "ParentWork")
            .Connect("Sub", "EndSub")
            .Connect("ParentWork", "EndParentWork")
            .Build();

        return (parent, body);
    }

    /// <summary>
    /// A scope that terminates while a subprocess and a task are still running. The subprocess carries a
    /// non-interrupting escalation boundary for <c>forward</c> whose path throws an escalation outward, so a
    /// signal reaching a live scope either bubbles to the parent (an unmatched code) or is caught and
    /// re-thrown (<c>forward</c>). Completing <c>trigger</c> reaches the terminate end event.
    /// </summary>
    public static BpmnProcessDefinition TerminateBesideASignallingChild() =>
        new BpmnProcessBuilder("terminate-beside-signalling-child")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .SubProcess("Child", bindingRef: "child")
            .EndEvent("EndChild")
            .BoundaryEvent("Forwarded", "Child", Escalation("forward"), interrupting: false)
            .IntermediateThrowEvent("Forward", Escalation("forward-out"))
            .EndEvent("EndForwarded")
            .Task("Other", bindingRef: "other")
            .EndEvent("EndOther")
            .Task("Trigger", bindingRef: "trigger")
            .EndEvent("Terminate", null, Terminate())
            .Connect("Start", "Split")
            .Connect("Split", "Child")
            .Connect("Split", "Other")
            .Connect("Split", "Trigger")
            .Connect("Child", "EndChild")
            .ConnectSequence("Forwarded", "Forward", "EndForwarded")
            .Connect("Other", "EndOther")
            .Connect("Trigger", "Terminate")
            .Build();

    /// <summary>
    /// A risky task whose error boundary path forks into a recovery task and an exclusive gateway that can never
    /// choose. Catching the error starts the recovery's work and then faults, in the same evaluation.
    /// </summary>
    public static BpmnProcessDefinition ErrorPathThatForksIntoWorkAndFault() =>
        WithUndecidableGateway(new BpmnProcessBuilder("error-path-faults")
                .StartEvent("Start")
                .Task("Risky", bindingRef: "risky")
                .EndEvent("EndOk")
                .BoundaryEvent("Failed", "Risky", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
                .ParallelGateway("Fork")
                .Task("Recover", bindingRef: "recover")
                .EndEvent("EndRecovered")
                .ConnectSequence("Start", "Risky", "EndOk")
                .Connect("Failed", "Fork")
                .Connect("Fork", "Recover")
                .Connect("Recover", "EndRecovered"),
            from: "Fork")
            .Build();

    /// <summary>
    /// An event-based gateway race beside a running task, where the winning catch leads to a terminate end
    /// event. Winning the race tears the losing catch down for its own reason in the same evaluation that
    /// terminates.
    /// </summary>
    public static BpmnProcessDefinition RaceWonIntoTerminate() =>
        new BpmnProcessBuilder("race-into-terminate")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .EventBasedGateway("Race")
            .IntermediateCatchEvent("CatchA", Message("a"), bindingRef: "catch-a")
            .IntermediateCatchEvent("CatchB", Message("b"), bindingRef: "catch-b")
            .EndEvent("Terminate", null, Terminate())
            .EndEvent("EndB")
            .Task("Other", bindingRef: "other")
            .EndEvent("EndOther")
            .Connect("Start", "Split")
            .Connect("Split", "Race")
            .Connect("Split", "Other")
            .Connect("Race", "CatchA")
            .Connect("Race", "CatchB")
            .Connect("CatchA", "Terminate")
            .Connect("CatchB", "EndB")
            .Connect("Other", "EndOther")
            .Build();

    /// <summary>The shared shape of <see cref="TerminateBesideLiveWork"/> and <see cref="FaultBesideLiveWork"/>, up to the trigger's outbound flow.</summary>
    private static BpmnProcessBuilder ScopeWithLiveWork(string processId) =>
        new BpmnProcessBuilder(processId)
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("Long", bindingRef: "long")
            .EndEvent("EndLong")
            .BoundaryEvent("Timeout", "Long", Timer("PT1H"), interrupting: true, bindingRef: "timeout")
            .EndEvent("EndTimedOut")
            .Task(BpmnElementTypes.Task, "Review", bindingRef: "review",
                loopCharacteristics: new BpmnLoopCharacteristics(isSequential: false, cardinality: 2))
            .EndEvent("EndReviewed")
            .SubProcess("Sub", bindingRef: "sub")
            .EndEvent("EndSub")
            .Element(new BpmnElement("OnNudge", BpmnElementTypes.SubProcess,
                bindingRef: "on-nudge", triggeredByEvent: true, listenerBindingRef: "on-nudge-listener"))
            .Task("Trigger", bindingRef: "trigger")
            .Connect("Start", "Split")
            .Connect("Split", "Long")
            .Connect("Split", "Review")
            .Connect("Split", "Sub")
            .Connect("Split", "Trigger")
            .Connect("Long", "EndLong")
            .Connect("Timeout", "EndTimedOut")
            .Connect("Review", "EndReviewed")
            .Connect("Sub", "EndSub");

    /// <summary>The nested processes <see cref="ScopeWithLiveWork"/> runs: the subprocess body and the message event subprocess body.</summary>
    private static IReadOnlyDictionary<string, BpmnProcessDefinition> ScopeWithLiveWorkNestedProcesses() =>
        new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
        {
            ["sub"] = new BpmnProcessBuilder("sub-body")
                .StartEvent("BodyStart")
                .Task("Inner", bindingRef: "inner")
                .EndEvent("BodyEnd")
                .ConnectSequence("BodyStart", "Inner", "BodyEnd")
                .Build(),
            ["on-nudge"] = new BpmnProcessBuilder("on-nudge-body")
                .StartEvent("NudgeStart", null, Message("nudge"))
                .Task("HandleNudge", bindingRef: "handle-nudge")
                .EndEvent("NudgeEnd")
                .ConnectSequence("NudgeStart", "HandleNudge", "NudgeEnd")
                .Build()
        };

    /// <summary>Adds an exclusive gateway reached from <paramref name="from"/> that binds no decision, declares no default, and has only conditional flows.</summary>
    private static BpmnProcessBuilder WithUndecidableGateway(BpmnProcessBuilder builder, string from) =>
        builder
            .ExclusiveGateway("Undecidable")
            .EndEvent("EndLeft")
            .EndEvent("EndRight")
            .Connect(from, "Undecidable")
            .Connect("Undecidable", "EndLeft", condition: "Left")
            .Connect("Undecidable", "EndRight", condition: "Right");

    public static BpmnEventDefinition Terminate() => new(BpmnEventDefinitionTypes.Terminate);

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
