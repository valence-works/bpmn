using Bpmn.Model;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>Validates event-subprocess shapes and the graph's public catcher index.</summary>
public sealed class EventSubprocessValidationTests
{
    [Fact]
    public void A_valid_escalation_event_subprocess_exposes_its_indexed_catcher_fields()
    {
        var graph = Build(Parent(EventSubprocess("Handler", "handler-body")),
            Body("handler-body", "HandlerStart", Escalation("overdue"), interrupting: false));

        var catcher = graph.EventSubprocesses.ShouldHaveSingleItem();
        catcher.ElementId.ShouldBe("Handler");
        catcher.BindingRef.ShouldBe("handler-body");
        catcher.BodyStartElementId.ShouldBe("HandlerStart");
        catcher.TriggerKind.ShouldBe(BpmnEventSubprocessTriggerKind.Escalation);
        catcher.Code.ShouldBe("overdue");
        catcher.Interrupting.ShouldBeFalse();
        catcher.ListenerBindingRef.ShouldBeNull();
        graph.EscalationEventSubprocess("overdue").ShouldBeSameAs(catcher);
    }

    [Fact]
    public void A_valid_error_event_subprocess_exposes_its_indexed_catcher_fields()
    {
        var graph = Build(Parent(EventSubprocess("Handler", "handler-body")),
            Body("handler-body", "HandlerStart", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)));

        var catcher = graph.EventSubprocesses.ShouldHaveSingleItem();
        catcher.ElementId.ShouldBe("Handler");
        catcher.BindingRef.ShouldBe("handler-body");
        catcher.BodyStartElementId.ShouldBe("HandlerStart");
        catcher.TriggerKind.ShouldBe(BpmnEventSubprocessTriggerKind.Error);
        catcher.Code.ShouldBeNull();
        catcher.Interrupting.ShouldBeTrue();
        catcher.ListenerBindingRef.ShouldBeNull();
        graph.ErrorEventSubprocess().ShouldBeSameAs(catcher);
    }

    [Fact]
    public void An_event_subprocess_cannot_participate_in_sequence_flows()
    {
        var parent = Parent(EventSubprocess("Handler", "handler-body"), connectHandler: true);

        Should.Throw<BpmnExecutionException>(() => Build(parent, Body("handler-body", "HandlerStart", Escalation("E1"))))
            .Message.ShouldContain("cannot participate in sequence flows");
    }

    [Fact]
    public void An_event_subprocess_cannot_host_an_attached_boundary()
    {
        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .Task("Work", bindingRef: "work")
            .EndEvent("End")
            .Element(EventSubprocess("Handler", "handler-body"))
            .BoundaryEvent("Boundary", "Handler", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))
            .EndEvent("BoundaryEnd")
            .ConnectSequence("Start", "Work", "End")
            .Connect("Boundary", "BoundaryEnd")
            .Build();

        Should.Throw<BpmnExecutionException>(() => Build(parent, Body("handler-body", "HandlerStart", Escalation("E1"))))
            .Message.ShouldContain("cannot host attached boundary events");
    }

    [Fact]
    public void An_event_subprocess_body_requires_an_event_start()
    {
        var body = new BpmnProcessBuilder("handler-body")
            .StartEvent("HandlerStart")
            .Task("Handle", bindingRef: "handle")
            .EndEvent("End")
            .ConnectSequence("HandlerStart", "Handle", "End")
            .Build();

        Should.Throw<BpmnExecutionException>(() => Build(Parent(EventSubprocess("Handler", "handler-body")), body))
            .Message.ShouldContain("exactly one event definition of a supported trigger");
    }

    [Fact]
    public void An_event_subprocess_body_requires_exactly_one_start()
    {
        var body = new BpmnProcessBuilder("handler-body")
            .StartEvent("FirstStart", null, Escalation("E1"))
            .StartEvent("SecondStart", null, Escalation("E2"))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("End")
            .ConnectSequence("FirstStart", "Handle", "End")
            .ConnectSequence("SecondStart", "Handle")
            .Build();

        Should.Throw<BpmnExecutionException>(() => Build(Parent(EventSubprocess("Handler", "handler-body")), body))
            .Message.ShouldContain("exactly one start event");
    }

    [Fact]
    public void An_event_subprocess_body_rejects_an_unsupported_trigger()
    {
        var body = Body("handler-body", "HandlerStart", new BpmnEventDefinition(BpmnEventDefinitionTypes.Compensation));

        Should.Throw<BpmnExecutionException>(() => Build(Parent(EventSubprocess("Handler", "handler-body")), body))
            .Message.ShouldContain("unsupported trigger definition");
    }

    [Fact]
    public void An_error_event_subprocess_must_be_interrupting()
    {
        var body = Body("handler-body", "HandlerStart", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error), interrupting: false);

        Should.Throw<BpmnExecutionException>(() => Build(Parent(EventSubprocess("Handler", "handler-body")), body))
            .Message.ShouldContain("must be interrupting");
    }

    [Fact]
    public void An_event_subprocess_body_cannot_contain_a_nested_event_subprocess()
    {
        var nested = new BpmnElement("Nested", BpmnElementTypes.SubProcess, bindingRef: "nested-body", triggeredByEvent: true);
        var body = new BpmnProcessBuilder("handler-body")
            .StartEvent("HandlerStart", null, Escalation("E1"))
            .Task("Handle", bindingRef: "handle")
            .EndEvent("End")
            .Element(nested)
            .ConnectSequence("HandlerStart", "Handle", "End")
            .Build();

        Should.Throw<BpmnExecutionException>(() => Build(Parent(EventSubprocess("Handler", "handler-body")), body))
            .Message.ShouldContain("nested event subprocess");
    }

    [Fact]
    public void A_scope_rejects_two_escalation_event_subprocesses_with_the_same_code()
    {
        var parent = Parent(
            EventSubprocess("FirstHandler", "first-body"),
            EventSubprocess("SecondHandler", "second-body"));
        var first = Body("first-body", "FirstStart", Escalation("E1"));
        var second = Body("second-body", "SecondStart", Escalation("E1"));

        Should.Throw<BpmnExecutionException>(() => Build(parent, first, second))
            .Message.ShouldContain("codes in one scope must be distinct");
    }

    [Fact]
    public void A_scope_indexes_error_and_escalation_event_subprocesses_together()
    {
        var parent = Parent(
            EventSubprocess("EscalationHandler", "escalation-body"),
            EventSubprocess("ErrorHandler", "error-body"));
        var graph = Build(parent,
            Body("escalation-body", "EscalationStart", Escalation("E1")),
            Body("error-body", "ErrorStart", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)));

        graph.EventSubprocesses.Count.ShouldBe(2);
        graph.EscalationEventSubprocess("E1")?.ElementId.ShouldBe("EscalationHandler");
        graph.ErrorEventSubprocess()?.ElementId.ShouldBe("ErrorHandler");
    }

    [Fact]
    public void A_scope_rejects_two_error_event_subprocesses()
    {
        var parent = Parent(
            EventSubprocess("FirstHandler", "first-body"),
            EventSubprocess("SecondHandler", "second-body"));

        Should.Throw<BpmnExecutionException>(() => Build(parent,
            Body("first-body", "FirstStart", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)),
            Body("second-body", "SecondStart", new BpmnEventDefinition(BpmnEventDefinitionTypes.Error))))
            .Message.ShouldContain("at most one");
    }

    [Theory]
    [InlineData(BpmnEventDefinitionTypes.Message, BpmnEventSubprocessTriggerKind.Message, "notice")]
    [InlineData(BpmnEventDefinitionTypes.Signal, BpmnEventSubprocessTriggerKind.Signal, "notice")]
    [InlineData(BpmnEventDefinitionTypes.Timer, BpmnEventSubprocessTriggerKind.Timer, "PT1M")]
    public void A_valid_external_event_subprocess_exposes_its_listener_and_trigger_fields(
        string triggerType,
        BpmnEventSubprocessTriggerKind expectedKind,
        string triggerValue)
    {
        var body = Body("handler-body", "HandlerStart", ExternalTrigger(triggerType, triggerValue), interrupting: false);
        var graph = Build(Parent(EventSubprocess("Handler", "handler-body", "listener")), body);

        var catcher = graph.ExternalTriggerEventSubprocesses.ShouldHaveSingleItem();
        catcher.ElementId.ShouldBe("Handler");
        catcher.BindingRef.ShouldBe("handler-body");
        catcher.BodyStartElementId.ShouldBe("HandlerStart");
        catcher.TriggerKind.ShouldBe(expectedKind);
        catcher.Code.ShouldBeNull();
        catcher.Interrupting.ShouldBeFalse();
        catcher.ListenerBindingRef.ShouldBe("listener");
        catcher.IsExternalTrigger.ShouldBeTrue();
        graph.EventSubprocessByElementId("Handler").ShouldBeSameAs(catcher);
    }

    [Theory]
    [InlineData(BpmnEventDefinitionTypes.Message, "notice")]
    [InlineData(BpmnEventDefinitionTypes.Signal, "notice")]
    [InlineData(BpmnEventDefinitionTypes.Timer, "PT1M")]
    public void An_external_event_subprocess_requires_listener_work(string triggerType, string triggerValue)
    {
        var parent = Parent(EventSubprocess("Handler", "handler-body", listenerRef: null));
        var body = Body("handler-body", "HandlerStart", ExternalTrigger(triggerType, triggerValue));

        Should.Throw<BpmnExecutionException>(() => Build(parent, body))
            .Message.ShouldContain("requires scope-listener work");
    }

    [Fact]
    public void An_escalation_event_subprocess_cannot_declare_external_listener_work()
    {
        var parent = Parent(EventSubprocess("Handler", "handler-body", "listener"));
        var body = Body("handler-body", "HandlerStart", Escalation("E1"));

        Should.Throw<BpmnExecutionException>(() => Build(parent, body))
            .Message.ShouldContain("escalation event subprocesses are dormant");
    }

    [Fact]
    public void A_listener_binding_cannot_be_bound_by_two_elements()
    {
        var parent = new BpmnProcessBuilder("task-and-listener-owner")
            .StartEvent("Start")
            .Task("Work", bindingRef: "listener")
            .EndEvent("End")
            .Element(EventSubprocess("Handler", "handler-body", "listener"))
            .ConnectSequence("Start", "Work", "End")
            .Build();
        var body = Body("handler-body", "HandlerStart", ExternalTrigger(BpmnEventDefinitionTypes.Message, "notice"));

        Should.Throw<BpmnExecutionException>(() => Build(parent, body))
            .Message.ShouldContain("bound by more than one element");
    }

    [Fact]
    public void Two_event_subprocesses_cannot_share_a_listener_binding()
    {
        var parent = Parent(
            EventSubprocess("FirstHandler", "first-body", "listener"),
            EventSubprocess("SecondHandler", "second-body", "listener"));

        Should.Throw<BpmnExecutionException>(() => Build(parent,
            Body("first-body", "FirstStart", ExternalTrigger(BpmnEventDefinitionTypes.Message, "first")),
            Body("second-body", "SecondStart", ExternalTrigger(BpmnEventDefinitionTypes.Signal, "second"))))
            .Message.ShouldContain("bound by more than one element");
    }

    [Fact]
    public void A_listener_binding_must_name_supplied_host_work()
    {
        var parent = Parent(EventSubprocess("Handler", "handler-body", "missing-listener"));
        var body = Body("handler-body", "HandlerStart", ExternalTrigger(BpmnEventDefinitionTypes.Message, "notice"));

        Should.Throw<BpmnExecutionException>(() => Build(parent, body, omitListenerBinding: true))
            .Message.ShouldContain("scope-listener work 'missing-listener', which the host did not supply");
    }

    private static BpmnProcessDefinition Parent(params BpmnElement[] handlers) => Parent(handlers, false);

    private static BpmnProcessDefinition Parent(BpmnElement handler, bool connectHandler) => Parent([handler], connectHandler);

    private static BpmnProcessDefinition Parent(BpmnElement[] handlers, bool connectHandler)
    {
        var builder = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .Task("Work", bindingRef: "work")
            .EndEvent("End");
        foreach (var handler in handlers)
            builder.Element(handler);
        builder.ConnectSequence("Start", "Work", "End");
        if (connectHandler)
            builder.Connect("Work", handlers[0].ElementId);
        return builder.Build();
    }

    private static BpmnElement EventSubprocess(
        string elementId,
        string bodyRef,
        string? listenerRef = null) =>
        new(elementId, BpmnElementTypes.SubProcess, bindingRef: bodyRef,
            triggeredByEvent: true, listenerBindingRef: listenerRef);

    private static BpmnProcessDefinition Body(
        string processId,
        string startId,
        BpmnEventDefinition trigger,
        bool interrupting = true) =>
        new BpmnProcessBuilder(processId)
            .Element(new BpmnElement(startId, BpmnElementTypes.StartEvent,
                eventDefinitions: [trigger], cancelActivity: interrupting))
            .Task("Handle", bindingRef: $"{processId}-work")
            .EndEvent("End")
            .ConnectSequence(startId, "Handle", "End")
            .Build();

    private static BpmnGraph Build(BpmnProcessDefinition parent, params BpmnProcessDefinition[] bodies) =>
        Build(parent, bodies, omitListenerBinding: false);

    private static BpmnGraph Build(BpmnProcessDefinition parent, BpmnProcessDefinition body, bool omitListenerBinding) =>
        Build(parent, [body], omitListenerBinding);

    private static BpmnGraph Build(BpmnProcessDefinition parent, BpmnProcessDefinition[] bodies, bool omitListenerBinding)
    {
        var nested = bodies.ToDictionary(body => body.ProcessId, StringComparer.Ordinal);
        var boundWork = new List<BpmnBoundWork>();
        var suppliedRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in parent.Elements)
        {
            if (element.BindingRef is { } bindingRef && suppliedRefs.Add(bindingRef))
                boundWork.Add(new BpmnBoundWork(bindingRef, nested.GetValueOrDefault(bindingRef)));
            if (element.ListenerBindingRef is { } listenerRef && !omitListenerBinding && suppliedRefs.Add(listenerRef))
                boundWork.Add(new BpmnBoundWork(listenerRef));
        }

        return BpmnGraph.Build(parent, boundWork, BpmnHostCapabilities.Full);
    }

    private static BpmnEventDefinition Escalation(string code, bool includeCode = true) =>
        new(BpmnEventDefinitionTypes.Escalation,
            includeCode ? new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code } : null);

    private static BpmnEventDefinition ExternalTrigger(string type, string value) =>
        type == BpmnEventDefinitionTypes.Timer
            ? new BpmnEventDefinition(type, new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Interval] = value })
            : new BpmnEventDefinition(type, new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Name] = value });
}
