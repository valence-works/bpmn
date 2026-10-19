using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class EventStartTests
{
    [Fact]
    public void An_external_trigger_seeds_only_its_named_event_start()
    {
        var instance = new InMemoryBpmnHost().Start(TwoStarts(), startSignal: new BpmnStartSignal("StartA"));

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["WorkA"]);
        instance.State.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Kind == BpmnDiagnosticKind.TokenEmitted && diagnostic.ElementId == "StartA");
        instance.State.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Kind == BpmnDiagnosticKind.TokenEmitted && diagnostic.ElementId == "StartB");

        instance.CompleteWork("work-a");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.PendingWork.ShouldBeEmpty();
    }

    [Fact]
    public void Direct_invocation_seeds_none_starts_and_leaves_event_starts_dormant()
    {
        var instance = new InMemoryBpmnHost().Start(TwoStarts(firstIsNone: true));

        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["WorkA"]);
        instance.CompleteWork("work-a");

        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Select(work => work.BindingRef).ShouldBe(["work-a"]);
    }

    [Theory]
    [InlineData(BpmnEventDefinitionTypes.Message)]
    [InlineData(BpmnEventDefinitionTypes.Signal)]
    [InlineData(BpmnEventDefinitionTypes.Timer)]
    public void Direct_invocation_of_only_event_starts_faults_without_dispatching_work(string eventType)
    {
        var instance = new InMemoryBpmnHost().Start(TwoStarts(firstEventType: eventType));

        instance.Fault.ShouldNotBeNull().Code.ShouldBe("bpmn.start.none-available");
        instance.PendingWork.ShouldBeEmpty();
        instance.State.Tokens.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("WorkA")]
    [InlineData("StartA")]
    public void A_trigger_must_name_an_event_defined_start(string elementId)
    {
        var instance = new InMemoryBpmnHost().Start(
            TwoStarts(firstIsNone: true), startSignal: new BpmnStartSignal(elementId));

        instance.Fault.ShouldNotBeNull().Code.ShouldBe("bpmn.start.unresolved-trigger");
        instance.PendingWork.ShouldBeEmpty();
        instance.State.Tokens.ShouldBeEmpty();
    }

    [Fact]
    public void Repeated_triggered_starts_produce_the_same_nonempty_token_id_sequence()
    {
        var definition = TwoStarts();
        var host = new InMemoryBpmnHost();
        var first = host.Start(definition, startSignal: new BpmnStartSignal("StartA"));
        var second = host.Start(definition, startSignal: new BpmnStartSignal("StartA"));

        first.ScopeInstanceId.ShouldNotBe(second.ScopeInstanceId);
        var tokenIds = first.State.Tokens.Select(token => token.TokenId).ToArray();
        tokenIds.ShouldNotBeEmpty("compare live tokens before pruning can erase the evidence");
        second.State.Tokens.Select(token => token.TokenId).ShouldBe(tokenIds);

        first.CompleteWork("work-a");
        second.CompleteWork("work-a");
        first.IsCompleted.ShouldBeTrue();
        second.IsCompleted.ShouldBeTrue();
    }

    private static BpmnProcessDefinition TwoStarts(
        bool firstIsNone = false, string firstEventType = BpmnEventDefinitionTypes.Message)
    {
        var firstDefinition = firstEventType == BpmnEventDefinitionTypes.Timer
            ? ProcessFixtures.Timer("PT1M")
            : new BpmnEventDefinition(firstEventType,
                new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = "a" });
        return new BpmnProcessBuilder("event-starts")
            .Element(new BpmnElement("StartA", BpmnElementTypes.StartEvent,
                eventDefinitions: firstIsNone ? [] : [firstDefinition]))
            .StartEvent("StartB", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Signal,
                new Dictionary<string, string> { [BpmnEventDefinitionProperties.Name] = "b" }))
            .Task("WorkA", bindingRef: "work-a")
            .Task("WorkB", bindingRef: "work-b")
            .EndEvent("End")
            .ConnectSequence("StartA", "WorkA", "End")
            .ConnectSequence("StartB", "WorkB", "End")
            .Build();
    }
}
