using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// An error end event says its scope failed. Until the interpreter throws the error, building a graph that
/// contains one is refused, because the only other reading is a plain end event that completes the scope.
/// <para>
/// A subprocess body is built only when a token reaches the subprocess, so a refusal confined to the definition
/// being built would surface mid-run, after work has already happened. The parent's build refuses an error end
/// event in any body its bound work carries, and a host that resolves deeper bodies itself checks those before
/// the root runs.
/// </para>
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

    [Fact]
    public void An_error_end_event_in_an_embedded_subprocess_body_is_refused_when_the_parent_builds()
    {
        var body = Throwing(CreditDeclined());
        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .Task("Review", bindingRef: "review")
            .SubProcess("Settle", bindingRef: "settle")
            .EndEvent("End")
            .ConnectSequence("Start", "Review", "Settle", "End")
            .Build();

        var refusal = Should.Throw<BpmnExecutionException>(() =>
            BpmnGraph.Build(parent, BpmnBoundWork.Derive(parent, Bodies(("settle", body))), BpmnHostCapabilities.Full));

        refusal.Message.ShouldContain("'Failed'");
        refusal.Message.ShouldContain("'Settle'");
        refusal.Message.ShouldContain("'CREDIT_DECLINED'");
    }

    [Fact]
    public void An_error_end_event_in_an_event_subprocess_body_is_refused_when_the_parent_builds()
    {
        var body = new BpmnProcessBuilder("on-overdue")
            .StartEvent("HandlerStart", null, new BpmnEventDefinition(BpmnEventDefinitionTypes.Escalation,
                new Dictionary<string, string> { [BpmnEventDefinitionProperties.Code] = "overdue" }))
            .EndEvent("Failed", null, CreditDeclined())
            .ConnectSequence("HandlerStart", "Failed")
            .Build();
        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .Task("Review", bindingRef: "review")
            .EndEvent("End")
            .ConnectSequence("Start", "Review", "End")
            .SubProcess("OnOverdue", bindingRef: "on-overdue", triggeredByEvent: true)
            .Build();

        var refusal = Should.Throw<BpmnExecutionException>(() =>
            BpmnGraph.Build(parent, BpmnBoundWork.Derive(parent, Bodies(("on-overdue", body))), BpmnHostCapabilities.Full));

        refusal.Message.ShouldContain("'Failed'");
        refusal.Message.ShouldContain("'OnOverdue'");
    }

    [Fact]
    public void An_error_end_event_two_subprocesses_deep_is_refused_before_the_root_runs()
    {
        // The parent's bound work carries only the outer body, so the inner one is visible to the host alone. The
        // review task waits, which is what would otherwise let the root run and refuse only once it completed.
        var inner = Throwing(CreditDeclined());
        var outer = new BpmnProcessBuilder("outer")
            .StartEvent("OuterStart")
            .SubProcess("Settle", bindingRef: "settle")
            .EndEvent("OuterEnd")
            .ConnectSequence("OuterStart", "Settle", "OuterEnd")
            .Build();
        var parent = new BpmnProcessBuilder("parent")
            .StartEvent("Start")
            .Task("Review", bindingRef: "review")
            .SubProcess("Fulfil", bindingRef: "fulfil")
            .EndEvent("End")
            .ConnectSequence("Start", "Review", "Fulfil", "End")
            .Build();
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["fulfil"] = outer, ["settle"] = inner }
        });

        var refusal = Should.Throw<BpmnExecutionException>(() => host.Start(parent));

        refusal.Message.ShouldContain("'Failed'");
        refusal.Message.ShouldContain("'Settle'");
        host.Instances.ShouldBeEmpty("nothing runs when a body the root can reach is refused");
    }

    private static BpmnEventDefinition CreditDeclined() =>
        new(BpmnEventDefinitionTypes.Error,
            new Dictionary<string, string> { [BpmnEventDefinitionProperties.Code] = "CREDIT_DECLINED" });

    private static Func<string, BpmnProcessDefinition?> Bodies(params (string BindingRef, BpmnProcessDefinition Body)[] bodies) =>
        bindingRef => bodies.FirstOrDefault(entry => StringComparer.Ordinal.Equals(entry.BindingRef, bindingRef)).Body;
}
