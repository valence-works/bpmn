using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// An error end event says its scope failed. Until the interpreter throws the error, building a graph that
/// contains one is refused, because the only other reading is a plain end event that completes the scope.
/// </summary>
public sealed class ErrorEndEventTests
{
    private static BpmnProcessDefinition Throwing(BpmnEventDefinition error) =>
        new BpmnProcessBuilder("throws")
            .StartEvent("Start")
            .EndEvent("Failed", null, error)
            .ConnectSequence("Start", "Failed")
            .Build();

    [Fact]
    public void A_coded_error_end_event_is_refused_at_graph_build()
    {
        var definition = Throwing(new BpmnEventDefinition(
            BpmnEventDefinitionTypes.Error,
            new Dictionary<string, string> { [BpmnEventDefinitionProperties.Code] = "CREDIT_DECLINED" }));

        var refusal = Should.Throw<BpmnExecutionException>(() =>
            BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full));

        refusal.Message.ShouldContain("'Failed'");
        refusal.Message.ShouldContain("'CREDIT_DECLINED'");
    }

    [Fact]
    public void An_uncoded_error_end_event_is_refused_at_graph_build()
    {
        var definition = Throwing(new BpmnEventDefinition(BpmnEventDefinitionTypes.Error));

        var refusal = Should.Throw<BpmnExecutionException>(() =>
            BpmnGraph.Build(definition, BpmnBoundWork.Derive(definition), BpmnHostCapabilities.Full));

        refusal.Message.ShouldContain("'Failed'");
        refusal.Message.ShouldContain("no code");
    }
}
