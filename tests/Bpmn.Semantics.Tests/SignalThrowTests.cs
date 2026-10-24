using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// A signal throw or signal end event is a bound-work broadcast, like a message throw or end: it starts the work
/// that publishes the signal and, when that work completes, continues (a throw) or consumes its token (an end).
/// Delivering the signal to every catcher is the host's; the interpreter only starts the work.
/// </summary>
public sealed class SignalThrowTests
{
    private static readonly BpmnInterpreter Interpreter = BpmnInterpreter.CreateDefault();

    private static BpmnEventDefinition Signal(string? name) =>
        name is null
            ? new BpmnEventDefinition(BpmnEventDefinitionTypes.Signal)
            : new BpmnEventDefinition(BpmnEventDefinitionTypes.Signal,
                new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Name] = name });

    private static BpmnHostSnapshot Scope(params BpmnLiveWork[] liveWork) =>
        BpmnHostSnapshot.Root("scope-1") with { LiveWork = liveWork };

    private static BpmnLiveWork Started(BpmnEvaluation evaluation, string bindingRef) =>
        evaluation.Commands.OfType<BpmnHostCommand.StartWork>()
            .Where(start => start.BindingRef == bindingRef)
            .Select(start => new BpmnLiveWork(start.BindingRef, start.IterationScope?.IterationId, $"h-{start.TokenId}") { WorkTokenId = start.TokenId })
            .Single();

    private static BpmnEvaluation Complete(BpmnGraph graph, BpmnEvaluation previous, BpmnLiveWork work) =>
        Interpreter.OnWorkCompleted(new BpmnWorkCompletedRequest(graph, previous.State, Scope(), work.BindingRef, work.Handle, []) { WorkTokenId = work.WorkTokenId });

    private static BpmnGraph Build(BpmnProcessDefinition definition) =>
        BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.None);

    [Fact]
    public void A_signal_throw_starts_its_broadcast_and_continues_when_the_broadcast_completes()
    {
        var graph = Build(new BpmnProcessBuilder("p")
            .StartEvent("Start")
            .IntermediateThrowEvent("Announce", Signal("Order closed"), bindingRef: "announce")
            .Task("Archive", bindingRef: "archive")
            .EndEvent("End")
            .ConnectSequence("Start", "Announce", "Archive", "End")
            .Build());

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));
        var broadcast = Started(started, "announce");
        started.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["announce"]);

        var continued = Complete(graph, started, broadcast);

        continued.Commands.OfType<BpmnHostCommand.StartWork>().Select(start => start.BindingRef).ShouldBe(["archive"]);
    }

    [Fact]
    public void A_signal_end_event_starts_its_broadcast_and_ends_when_the_broadcast_completes()
    {
        var definition = new BpmnProcessBuilder("p")
            .StartEvent("Start")
            .Element(new BpmnElement("Closed", BpmnElementTypes.EndEvent, bindingRef: "closed", eventDefinitions: [Signal("Order closed")]))
            .ConnectSequence("Start", "Closed")
            .Build();
        var graph = Build(definition);

        var started = Interpreter.Start(new BpmnStartRequest(graph, null, Scope()));
        var broadcast = Started(started, "closed");
        started.Continuation.ShouldBeOfType<BpmnContinuation.Defer>("the token waits on the broadcast");

        var ended = Complete(graph, started, broadcast);

        ended.Continuation.ShouldBeOfType<BpmnContinuation.Complete>();
    }

    [Fact]
    public void A_signal_throw_that_binds_no_work_is_refused()
    {
        var definition = new BpmnProcessBuilder("p")
            .StartEvent("Start")
            .IntermediateThrowEvent("Announce", Signal("Order closed"))
            .EndEvent("End")
            .ConnectSequence("Start", "Announce", "End")
            .Build();

        var refusal = Should.Throw<BpmnExecutionException>(() => Build(definition));

        refusal.Message.ShouldContain("signal throw event 'Announce' requires bound work");
        refusal.ElementIds.ShouldBe(["Announce"]);
    }

    [Fact]
    public void A_signal_end_event_without_a_signal_name_is_refused()
    {
        var definition = new BpmnProcessBuilder("p")
            .StartEvent("Start")
            .Element(new BpmnElement("Closed", BpmnElementTypes.EndEvent, bindingRef: "closed", eventDefinitions: [Signal(null)]))
            .ConnectSequence("Start", "Closed")
            .Build();

        var refusal = Should.Throw<BpmnExecutionException>(() => Build(definition));

        refusal.Message.ShouldContain("signal end event 'Closed' must declare a non-empty signal name");
        refusal.ElementIds.ShouldBe(["Closed"]);
    }

    [Fact]
    public void Signal_throw_and_end_events_resolve_to_their_own_families()
    {
        BpmnElementFamilies.Resolve(new BpmnElement("T", BpmnElementTypes.IntermediateThrowEvent, eventDefinitions: [Signal("s")]))
            .ShouldBe(BpmnElementFamilies.IntermediateThrowEventSignal);
        BpmnElementFamilies.Resolve(new BpmnElement("E", BpmnElementTypes.EndEvent, eventDefinitions: [Signal("s")]))
            .ShouldBe(BpmnElementFamilies.EndEventSignal);
    }
}
