using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// Exercises message, signal and timer event-subprocess listeners through the host boundary. These listeners
/// are live while the scope runs, but they never keep completed work alive; non-interrupting fires re-arm,
/// while interrupting and terminal paths retire sibling listeners with the rest of their scope.
/// </summary>
public sealed class ExternalEventSubprocessTests
{
    [Fact]
    public void An_armed_listener_does_not_block_completion_and_is_retired_with_the_scope()
    {
        var (parent, bodies) = ScopeWithExternalHandlers(
            [Handler("MessageHandler", "message-body", "message-listener", BpmnEventDefinitionTypes.Message, "message")]);
        var host = Host(bodies);

        var instance = host.Start(parent);

        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["hold", "message-listener"]);
        var listener = instance.ResolveWork("message-listener");
        listener.IsCatchListener.ShouldBeTrue();
        listener.SchedulingCause.ShouldBe(BpmnInterpreter.ScopeListenerSchedulingCause);
        instance.State.Tokens.ShouldContain(token => token.Kind == BpmnTokenKind.Listener
            && token.AtElementId == "MessageHandler" && token.Status == BpmnTokenStatus.AwaitingChild);
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ScopeListenerArmed
            && diagnostic.ElementId == "MessageHandler");

        instance.CompleteWork("hold");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.CancelledWork.ShouldContain(work => work.ElementId == "MessageHandler"
            && work.Reason == BpmnInterpreter.EventSubprocessListenerSupersededByCompletionReason);
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ScopeListenerRetired
            && diagnostic.ElementId == "MessageHandler");
        instance.PendingWork.ShouldBeEmpty();
        host.Clock.Pending.ShouldBeEmpty();
        instance.Children.ShouldBeEmpty("an unfired external trigger must not start its body");
    }

    [Fact]
    public void An_external_listener_is_not_started_when_the_real_path_completes_synchronously()
    {
        var body = EventBody("message-body", "MessageStart", Message("message"), "message-work", interrupting: true);
        var parent = ImmediateCompletionWithHandler("MessageHandler", "message-body", "message-listener");
        var host = Host(("message-body", body));

        var instance = host.Start(parent);

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.PendingWork.ShouldBeEmpty();
        instance.State.ActiveWork.ShouldBeEmpty();
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ScopeListenerRetired
            && diagnostic.ElementId == "MessageHandler");
        instance.State.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ScopeListenerArmed);
        instance.Children.ShouldBeEmpty();
        host.Clock.Pending.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(BpmnEventDefinitionTypes.Message)]
    [InlineData(BpmnEventDefinitionTypes.Signal)]
    public void A_non_interrupting_message_or_signal_listener_runs_its_body_and_rearms(string triggerType)
    {
        var (parent, bodies) = ScopeWithExternalHandlers(
            [Handler("Handler", "handler-body", "listener", triggerType, "notice", interrupting: false)]);
        var host = Host(bodies);
        var instance = host.Start(parent);

        var firstListener = instance.ResolveWork("listener");
        instance.CompleteWork(firstListener);

        instance.IsCompleted.ShouldBeFalse("the original work and a fresh listener remain live");
        instance.CancelledWork.ShouldBeEmpty();
        instance.Children.Count.ShouldBe(1);
        instance.Children[0].PendingWork.Select(work => work.BindingRef).ShouldBe(["handler-work"]);
        var rearmed = instance.PendingWork.Single(work => work.BindingRef == "listener");
        rearmed.Handle.ShouldNotBe(firstListener.Handle);
        rearmed.IsCatchListener.ShouldBeTrue();
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ScopeListenerFired).ShouldBe(1);
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ScopeListenerArmed).ShouldBe(2);

        instance.Children[0].CompleteWork("handler-work");
        instance.CompleteWork(rearmed);

        instance.Children.Count.ShouldBe(2);
        instance.Children[0].IsCompleted.ShouldBeTrue();
        instance.Children[1].PendingWork.Select(work => work.BindingRef).ShouldBe(["handler-work"]);
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ScopeListenerFired).ShouldBe(2);

        instance.Children[1].CompleteWork("handler-work");
        instance.CompleteWork("hold");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.CancelledWork.ShouldContain(work => work.ElementId == "Handler");
    }

    [Fact]
    public void A_non_interrupting_timer_listener_rearms_after_each_one_shot_fire()
    {
        var (parent, bodies) = ScopeWithExternalHandlers(
            [Handler("TimerHandler", "timer-body", "timer-listener", BpmnEventDefinitionTypes.Timer, "PT1M", interrupting: false)]);
        var host = Host(bodies);
        var instance = host.Start(parent);

        host.Clock.Pending.Single().DueAt.ShouldBe(TimeSpan.FromMinutes(1));
        instance.ResolveWork("timer-listener").Kind.ShouldBe(InMemoryWorkKind.Timer);
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        instance.Children.ShouldHaveSingleItem().PendingWork.Select(work => work.BindingRef).ShouldBe(["handler-work"]);
        var secondTimer = host.Clock.Pending.ShouldHaveSingleItem();
        secondTimer.BindingRef.ShouldBe("timer-listener");
        secondTimer.DueAt.ShouldBe(TimeSpan.FromMinutes(2));
        instance.Children[0].CompleteWork("handler-work");

        host.Clock.Advance(TimeSpan.FromMinutes(1));

        instance.Children.Count.ShouldBe(2);
        instance.Children[1].PendingWork.Select(work => work.BindingRef).ShouldBe(["handler-work"]);
        instance.State.Diagnostics.Count(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ScopeListenerFired).ShouldBe(2);
        host.Clock.Pending.ShouldHaveSingleItem().DueAt.ShouldBe(TimeSpan.FromMinutes(3));

        instance.Children[1].CompleteWork("handler-work");
        instance.CompleteWork("hold");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        host.Clock.Pending.ShouldBeEmpty();
    }

    [Fact]
    public void An_interrupting_message_listener_drains_real_work_and_its_sibling_listener()
    {
        var handlers = new[]
        {
            Handler("MessageHandler", "message-body", "message-listener", BpmnEventDefinitionTypes.Message, "message"),
            Handler("SignalHandler", "signal-body", "signal-listener", BpmnEventDefinitionTypes.Signal, "signal")
        };
        var (parent, bodies) = ScopeWithExternalHandlers(handlers);
        var host = Host(bodies);
        var instance = host.Start(parent);

        instance.CompleteWork("message-listener");

        instance.IsCompleted.ShouldBeFalse("the interrupting handler body still has to complete");
        instance.CancelledWork.Select(work => work.ElementId).ShouldBe(["Hold", "SignalHandler"], ignoreOrder: true);
        instance.CancelledWork.ShouldContain(work => work.ElementId == "SignalHandler"
            && work.Reason == BpmnInterpreter.EventSubprocessScopeInterruptedReason);
        instance.State.Tokens.ShouldContain(token => token.AtElementId == "SignalHandler" && token.Status == BpmnTokenStatus.Canceled);
        instance.PendingWork.Select(work => work.BindingRef).ShouldBe(["message-body"]);
        instance.Children.ShouldHaveSingleItem().PendingWork.Select(work => work.BindingRef).ShouldBe(["handler-work"]);
        instance.State.Diagnostics.ShouldContain(diagnostic => diagnostic.Kind == BpmnDiagnosticKind.ScopeListenerFired
            && diagnostic.ElementId == "MessageHandler");
        instance.Children[0].CompleteWork("handler-work");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Children.Count.ShouldBe(1, "the sibling signal listener was drained before it could activate");
        host.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void An_interrupting_own_scope_escalation_drains_an_armed_external_listener()
    {
        var externalHandler = Handler("MessageHandler", "message-body", "message-listener", BpmnEventDefinitionTypes.Message, "message");
        var escalationBody = new BpmnProcessBuilder("escalation-body")
            .StartEvent("EscalationStart", null, Escalation("stop"))
            .Task("EscalationWork", bindingRef: "escalation-work")
            .EndEvent("EscalationEnd")
            .ConnectSequence("EscalationStart", "EscalationWork", "EscalationEnd")
            .Build();
        var parent = new BpmnProcessBuilder("interrupt-listener")
            .StartEvent("Start")
            .ParallelGateway("Split")
            .Task("Trigger", bindingRef: "trigger")
            .Task("Hold", bindingRef: "hold")
            .IntermediateThrowEvent("Escalate", Escalation("stop"))
            .EndEvent("TriggerEnd")
            .EndEvent("HoldEnd")
            .Element(new BpmnElement("EscalationHandler", BpmnElementTypes.SubProcess,
                bindingRef: "escalation-body", triggeredByEvent: true))
            .Element(externalHandler.Element)
            .Connect("Start", "Split")
            .Connect("Split", "Trigger")
            .Connect("Split", "Hold")
            .ConnectSequence("Trigger", "Escalate", "TriggerEnd")
            .Connect("Hold", "HoldEnd")
            .Build();
        var host = Host(("escalation-body", escalationBody), ("message-body", externalHandler.Body));
        var instance = host.Start(parent);
        instance.PendingWork.ShouldContain(work => work.BindingRef == "message-listener");

        instance.CompleteWork("trigger");

        instance.CancelledWork.ShouldContain(work => work.ElementId == "MessageHandler"
            && work.Reason == BpmnInterpreter.EventSubprocessScopeInterruptedReason);
        instance.CancelledWork.ShouldContain(work => work.ElementId == "Hold");
        instance.State.Tokens.ShouldContain(token => token.AtElementId == "MessageHandler" && token.Status == BpmnTokenStatus.Canceled);
        instance.Children.ShouldHaveSingleItem().PendingWork.Select(work => work.BindingRef).ShouldBe(["escalation-work"]);
        instance.Children[0].CompleteWork("escalation-work");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        host.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void A_real_join_deadlock_still_faults_while_an_external_listener_is_armed()
    {
        var (parent, bodies) = ScopeWithExternalHandlers(
            [Handler("MessageHandler", "message-body", "message-listener", BpmnEventDefinitionTypes.Message, "message")],
            includeDecisionJoin: true);
        var host = Host(bodies);
        var instance = host.Start(parent);

        instance.CompleteWork("decision", "Left");
        instance.CompleteWork("left");

        instance.Fault.ShouldNotBeNull().Code.ShouldBe("bpmn.join.deadlock");
        instance.IsCompleted.ShouldBeFalse();
        instance.CancelledWork.ShouldContain(work => work.ElementId == "MessageHandler");
        instance.Children.ShouldBeEmpty();
        host.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void Terminate_retires_an_armed_external_listener_without_running_its_body()
    {
        var body = EventBody("message-body", "MessageStart", Message("message"), "handler-work", interrupting: true);
        var parent = new BpmnProcessBuilder("terminate-listener")
            .StartEvent("Start")
            .Task("Go", bindingRef: "go")
            .EndEvent("Terminate", null, Terminate())
            .Element(new BpmnElement("MessageHandler", BpmnElementTypes.SubProcess,
                bindingRef: "message-body", triggeredByEvent: true, listenerBindingRef: "message-listener"))
            .ConnectSequence("Start", "Go", "Terminate")
            .Build();
        var host = Host(("message-body", body));
        var instance = host.Start(parent);

        instance.PendingWork.ShouldContain(work => work.BindingRef == "message-listener");
        instance.CompleteWork("go");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.State.Terminated.ShouldBeTrue();
        instance.CancelledWork.ShouldContain(work => work.ElementId == "MessageHandler");
        instance.Children.ShouldBeEmpty();
        host.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void Identical_listener_runs_produce_identical_token_and_diagnostic_ids()
    {
        static (string[] Tokens, string[] Diagnostics) RunOnce()
        {
            var (parent, bodies) = ScopeWithExternalHandlers(
                [Handler("Handler", "handler-body", "listener", BpmnEventDefinitionTypes.Message, "notice", interrupting: false)]);
            var instance = Host(bodies).Start(parent);
            instance.CompleteWork("listener");

            return (
                instance.State.Tokens.Select(token => $"{token.TokenId}:{token.AtElementId}:{token.Status}:{token.Kind}")
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                instance.State.Diagnostics.Select(diagnostic => $"{diagnostic.DiagnosticId}:{diagnostic.Kind}")
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }

        var first = RunOnce();
        var second = RunOnce();

        first.Tokens.ShouldNotBeEmpty();
        second.Tokens.ShouldBe(first.Tokens);
        second.Diagnostics.ShouldBe(first.Diagnostics);
    }

    private static (BpmnProcessDefinition Parent, (string BindingRef, BpmnProcessDefinition Definition)[] Bodies)
        ScopeWithExternalHandlers(IReadOnlyCollection<ExternalHandler> handlers, bool includeDecisionJoin = false)
    {
        var builder = new BpmnProcessBuilder("external-listener-scope").StartEvent("Start");
        if (includeDecisionJoin)
        {
            builder.Element(new BpmnElement("Decision", BpmnElementTypes.ExclusiveGateway, bindingRef: "decision"))
                .Task("Left", bindingRef: "left")
                .Task("Right", bindingRef: "right")
                .ParallelGateway("Join")
                .EndEvent("End")
                .Connect("Start", "Decision")
                .Connect("Decision", "Left", condition: "Left")
                .Connect("Decision", "Right", condition: "Right")
                .Connect("Left", "Join")
                .Connect("Right", "Join")
                .Connect("Join", "End");
        }
        else
        {
            builder.Task("Hold", bindingRef: "hold")
                .EndEvent("End")
                .ConnectSequence("Start", "Hold", "End");
        }

        foreach (var handler in handlers)
            builder.Element(handler.Element);

        return (builder.Build(), handlers.Select(handler => (handler.BodyBindingRef, handler.Body)).ToArray());
    }

    private static BpmnProcessDefinition ImmediateCompletionWithHandler(string elementId, string bodyRef, string listenerRef) =>
        new BpmnProcessBuilder("immediate-listener-scope")
            .StartEvent("Start")
            .EndEvent("End")
            .Element(new BpmnElement(elementId, BpmnElementTypes.SubProcess,
                bindingRef: bodyRef, triggeredByEvent: true, listenerBindingRef: listenerRef))
            .Connect("Start", "End")
            .Build();

    private static ExternalHandler Handler(
        string elementId,
        string bodyRef,
        string listenerRef,
        string triggerType,
        string triggerValue,
        bool interrupting = true)
    {
        var startId = $"{elementId}Start";
        var definition = triggerType == BpmnEventDefinitionTypes.Timer
            ? Timer(triggerValue)
            : NamedTrigger(triggerType, triggerValue);
        return new ExternalHandler(
            new BpmnElement(elementId, BpmnElementTypes.SubProcess, bindingRef: bodyRef,
                triggeredByEvent: true, listenerBindingRef: listenerRef),
            bodyRef,
            EventBody(bodyRef, startId, definition, "handler-work", interrupting));
    }

    private static BpmnProcessDefinition EventBody(
        string processId,
        string startId,
        BpmnEventDefinition definition,
        string workRef,
        bool interrupting) =>
        new BpmnProcessBuilder(processId)
            .Element(new BpmnElement(startId, BpmnElementTypes.StartEvent,
                eventDefinitions: [definition], cancelActivity: interrupting))
            .Task("Handle", bindingRef: workRef)
            .EndEvent("HandlerEnd")
            .ConnectSequence(startId, "Handle", "HandlerEnd")
            .Build();

    private static BpmnEventDefinition NamedTrigger(string type, string name) =>
        new(type, new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Name] = name });

    private static BpmnEventDefinition Message(string name) => NamedTrigger(BpmnEventDefinitionTypes.Message, name);

    private static BpmnEventDefinition Timer(string interval) =>
        new(BpmnEventDefinitionTypes.Timer,
            new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Interval] = interval });

    private static BpmnEventDefinition Escalation(string code) =>
        new(BpmnEventDefinitionTypes.Escalation,
            new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code });

    private static BpmnEventDefinition Terminate() => new(BpmnEventDefinitionTypes.Terminate);

    private static InMemoryBpmnHost Host(params (string BindingRef, BpmnProcessDefinition Definition)[] bodies) =>
        new(new InMemoryBpmnHostOptions
        {
            NestedProcesses = bodies.ToDictionary(body => body.BindingRef, body => body.Definition, StringComparer.Ordinal)
        });

    private sealed record ExternalHandler(BpmnElement Element, string BodyBindingRef, BpmnProcessDefinition Body);
}
