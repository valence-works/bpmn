using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

public sealed class DecisionDefaultTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Done")]
    public void An_unmatched_decision_outcome_takes_only_the_default_flow(string outcome)
    {
        var definition = new BpmnProcessBuilder("default-decision")
            .StartEvent("Start")
            .Element(new BpmnElement("Decision", BpmnElementTypes.ExclusiveGateway, bindingRef: "decision"))
            .Task("High", bindingRef: "high")
            .Task("Fallback", bindingRef: "fallback")
            .EndEvent("End")
            .Connect("Start", "Decision")
            .Connect("Decision", "High", condition: "High")
            .Connect("Decision", "Fallback", isDefault: true)
            .Connect("High", "End")
            .Connect("Fallback", "End")
            .Build();
        var instance = new InMemoryBpmnHost().Start(definition);

        instance.PendingWork.ShouldHaveSingleItem().ElementId.ShouldBe("Decision");
        instance.CompleteWork("decision", outcome);

        instance.PendingWork.ShouldHaveSingleItem().ElementId.ShouldBe("Fallback");
        instance.CompleteWork("fallback");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Transcript.SelectMany(entry => entry.Commands).OfType<BpmnHostCommand.StartWork>()
            .Select(command => command.BindingRef).ShouldBe(["decision", "fallback"]);
    }
}
