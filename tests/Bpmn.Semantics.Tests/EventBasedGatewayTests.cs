using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>Race resolution, loser cancellation, routing failure, and stable race identity in the reference host.</summary>
public sealed class EventBasedGatewayTests
{
    [Fact]
    public void FirstCatchWins_CancelsLosingSiblingTokenAndSubtree()
    {
        var instance = new InMemoryBpmnHost().Start(RaceDefinition("race-first", 2));
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["catch-0", "catch-1"]);
        var armed = instance.State.Races.ShouldHaveSingleItem();
        armed.Resolved.ShouldBeFalse();
        armed.MemberTokenIds.Count.ShouldBe(2);

        instance.CompleteWork("catch-0-work");

        instance.State.Races.ShouldHaveSingleItem().Resolved.ShouldBeTrue();
        instance.State.Tokens.ShouldContain(token => token.AtElementId == "catch-1" && token.Status == BpmnTokenStatus.Canceled);
        instance.CancelledWork.Select(work => work.ElementId).ShouldBe(["catch-1"]);
        instance.CancelledWork.ShouldHaveSingleItem().Reason.ShouldBe(BpmnInterpreter.EventGatewayRaceCancellationReason);
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["after"]);
        instance.IsCompleted.ShouldBeFalse("the winner routes onward so the resolved race remains inspectable");
        instance.CompleteWork("after-work");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void ThreeMemberRace_CancelsBothLosers()
    {
        var instance = new InMemoryBpmnHost().Start(RaceDefinition("race-three", 3));
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["catch-0", "catch-1", "catch-2"]);
        instance.State.Races.ShouldHaveSingleItem().MemberTokenIds.Count.ShouldBe(3);

        instance.CompleteWork("catch-0-work");

        instance.State.Races.ShouldHaveSingleItem().Resolved.ShouldBeTrue();
        instance.State.Tokens.Count(token => token.Status == BpmnTokenStatus.Canceled && token.AtElementId.StartsWith("catch-", StringComparison.Ordinal)).ShouldBe(2);
        instance.CancelledWork.Select(work => work.ElementId).ShouldBe(["catch-1", "catch-2"]);
        instance.CancelledWork.ShouldAllBe(work => work.Reason == BpmnInterpreter.EventGatewayRaceCancellationReason);
        instance.PendingWork.Select(work => work.ElementId).ShouldBe(["after"]);
        instance.CompleteWork("after-work");
        instance.IsCompleted.ShouldBeTrue(instance.Transcript.ToString());
    }

    [Fact]
    public void WinnerRoutingFault_DoesNotStageCancellations_AndSurfacesTheRoutingFault()
    {
        var definition = RaceDefinition("race-routing-fault", 2, winnerRequiresOutcome: true);
        var instance = new InMemoryBpmnHost().Start(definition);

        instance.CompleteWork("catch-0-work");

        instance.IsFaulted.ShouldBeTrue(instance.Transcript.ToString());
        instance.Fault!.Code.ShouldBe("bpmn.flow.none-taken");
        instance.Transcript.Last().Commands.OfType<BpmnHostCommand.CancelWorkSubtree>().ShouldBeEmpty();
        var hostCleanup = instance.CancelledWork.ShouldHaveSingleItem();
        hostCleanup.ElementId.ShouldBe("catch-1");
        hostCleanup.Reason.ShouldBe(InMemoryProcessInstance.ScopeFinishedReason);
    }

    [Fact]
    public void IdenticalRuns_ProduceIdenticalTokenAndRaceIds()
    {
        var host = new InMemoryBpmnHost();
        var definition = RaceDefinition("race-stable", 2);
        var first = host.Start(definition);
        var second = host.Start(definition);

        first.State.Tokens.ShouldNotBeEmpty();
        first.State.Tokens.Select(token => token.TokenId).Distinct(StringComparer.Ordinal).Count().ShouldBe(first.State.Tokens.Count);
        var race = first.State.Races.ShouldHaveSingleItem();
        race.RaceId.ShouldNotBeNullOrWhiteSpace();
        race.MemberTokenIds.Count.ShouldBe(2);
        race.MemberTokenIds.Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        first.State.Tokens.Select(token => token.TokenId).OrderBy(id => id, StringComparer.Ordinal)
            .ShouldBe(second.State.Tokens.Select(token => token.TokenId).OrderBy(id => id, StringComparer.Ordinal));
        first.State.Races.Select(race => race.RaceId).OrderBy(id => id, StringComparer.Ordinal)
            .ShouldBe(second.State.Races.Select(race => race.RaceId).OrderBy(id => id, StringComparer.Ordinal));
        first.State.Races.ShouldHaveSingleItem().MemberTokenIds.OrderBy(id => id, StringComparer.Ordinal)
            .ShouldBe(second.State.Races.ShouldHaveSingleItem().MemberTokenIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    private static BpmnProcessDefinition RaceDefinition(string processId, int memberCount, bool winnerRequiresOutcome = false)
    {
        var builder = new BpmnProcessBuilder(processId)
            .StartEvent("start")
            .EventBasedGateway("race");
        for (var index = 0; index < memberCount; index++)
            builder.IntermediateCatchEvent($"catch-{index}", new BpmnEventDefinition(index % 2 == 0
                ? BpmnEventDefinitionTypes.Message
                : BpmnEventDefinitionTypes.Signal), bindingRef: $"catch-{index}-work");

        builder.Task("after", bindingRef: "after-work");
        for (var index = 0; index < memberCount; index++)
            builder.EndEvent($"end-{index}");

        builder.Connect("start", "race");
        for (var index = 0; index < memberCount; index++)
        {
            builder.Connect("race", $"catch-{index}");
            if (index == 0)
                builder.Connect($"catch-{index}", winnerRequiresOutcome ? "end-0" : "after",
                    condition: winnerRequiresOutcome ? "NeverMatched" : null);
            else
                builder.Connect($"catch-{index}", $"end-{index}");
        }

        if (!winnerRequiresOutcome)
            builder.Connect("after", "end-0");
        return builder.Build();
    }
}
