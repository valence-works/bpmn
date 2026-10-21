using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// A BPMN error crossing scopes, end to end through the reference host. A scope's interpreter cannot see the scope
/// above it, so when a nested scope ends with a fault carrying an error code, the host copies that code into the
/// parent's report: <see cref="BpmnWorkFaultedRequest.ErrorCode"/> for an embedded subprocess, or
/// <see cref="BpmnWorkCompletedRequest.ErrorCode"/> with the <c>Faulted</c> outcome for a call activity (ADR 0012).
/// The parent then runs the same ladder, so its coded catchers see the child's error.
/// </summary>
public sealed class ErrorCodePassThroughTests
{
    private const string CreditDeclined = "CREDIT_DECLINED";

    private static BpmnEventDefinition Error(string? code) =>
        code is null
            ? new BpmnEventDefinition(BpmnEventDefinitionTypes.Error)
            : new BpmnEventDefinition(BpmnEventDefinitionTypes.Error,
                new Dictionary<string, string>(StringComparer.Ordinal) { [BpmnEventDefinitionProperties.Code] = code });

    /// <summary><c>Start -> Charge -> Failed</c>, an error end event throwing <paramref name="thrown"/>.</summary>
    private static BpmnProcessDefinition Thrower(string? thrown) =>
        new BpmnProcessBuilder("settle")
            .StartEvent("Start")
            .ServiceTask("Charge", bindingRef: "charge")
            .EndEvent("Failed", null, Error(thrown))
            .ConnectSequence("Start", "Charge", "Failed")
            .Build();

    /// <summary><c>Start -> Charge -> End</c> with no catcher, so a fault of <c>Charge</c> leaves the scope.</summary>
    private static BpmnProcessDefinition UncaughtCharge() =>
        new BpmnProcessBuilder("settle")
            .StartEvent("Start")
            .ServiceTask("Charge", bindingRef: "charge")
            .EndEvent("End")
            .ConnectSequence("Start", "Charge", "End")
            .Build();

    /// <summary>
    /// A parent running <c>Settle</c> (a subprocess, or a call activity when <paramref name="callActivity"/>) bound
    /// <c>settle</c>, with an error boundary per entry of <paramref name="boundaries"/>: <c>On{code}</c> (or
    /// <c>OnAny</c>) routing to work bound <c>handle-{code}</c> (or <c>handle-any</c>).
    /// </summary>
    private static BpmnProcessDefinition Parent(bool callActivity, params string?[] boundaries)
    {
        var builder = new BpmnProcessBuilder("parent").StartEvent("Start");
        builder = callActivity
            ? builder.CallActivity("Settle", "settle", bindingRef: "settle")
            : builder.SubProcess("Settle", bindingRef: "settle");
        builder = builder.EndEvent("End").ConnectSequence("Start", "Settle", "End");

        foreach (var code in boundaries)
        {
            var name = code ?? "Any";
            builder = builder
                .BoundaryEvent($"On{name}", "Settle", Error(code))
                .Task($"Handle{name}", bindingRef: $"handle-{name.ToLowerInvariant()}")
                .EndEvent($"Handled{name}")
                .ConnectSequence($"On{name}", $"Handle{name}", $"Handled{name}");
        }

        return builder.Build();
    }

    private static (InMemoryProcessInstance Root, InMemoryProcessInstance Child) Run(BpmnProcessDefinition parent, BpmnProcessDefinition child)
    {
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["settle"] = child }
        });
        var root = host.Start(parent);
        return (root, root.Children.ShouldHaveSingleItem());
    }

    // --- Embedded subprocess -------------------------------------------------------------------------------

    [Fact]
    public void A_parents_coded_boundary_catches_an_error_end_event_in_its_subprocess()
    {
        var (root, child) = Run(Parent(callActivity: false, null, CreditDeclined), Thrower(CreditDeclined));

        child.CompleteWork("charge");

        child.Fault.ShouldNotBeNull(child.Transcript.ToString()).Code.ShouldBe(BpmnInterpreter.ErrorThrownFaultCode);
        child.Fault.ErrorCode.ShouldBe(CreditDeclined);
        child.Transcript[^1].Disposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);

        var parentEvaluation = root.Transcript[^1];
        parentEvaluation.Trigger.ShouldBeOfType<BpmnTranscriptTrigger.WorkFaulted>().ErrorCode.ShouldBe(CreditDeclined);
        parentEvaluation.Disposition.ShouldBe(new BpmnErrorDisposition.Caught($"On{CreditDeclined}", CreditDeclined), root.Transcript.ToString());
        root.PendingWork.Select(work => work.BindingRef).ShouldBe([$"handle-{CreditDeclined.ToLowerInvariant()}"]);

        root.CompleteWork($"handle-{CreditDeclined.ToLowerInvariant()}");
        root.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName, root.Transcript.ToString());
    }

    [Fact]
    public void An_error_no_scope_catches_reaches_the_root_with_its_code()
    {
        var (root, child) = Run(Parent(callActivity: false, "TIMEOUT"), Thrower(CreditDeclined));

        child.CompleteWork("charge");

        root.Transcript[^1].Disposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
        var fault = root.Fault.ShouldNotBeNull(root.Transcript.ToString());
        fault.Code.ShouldBe("bpmn.work.faulted");
        fault.ErrorCode.ShouldBe(CreditDeclined);
    }

    [Fact]
    public void An_uncoded_error_crosses_as_uncoded_and_reaches_only_the_parents_catch_all()
    {
        var (root, child) = Run(Parent(callActivity: false, CreditDeclined, null), Thrower(thrown: null));

        child.CompleteWork("charge");

        root.Transcript[^1].Disposition.ShouldBe(new BpmnErrorDisposition.Caught("OnAny", ErrorCode: null), root.Transcript.ToString());
    }

    [Fact]
    public void A_coded_work_fault_the_subprocess_does_not_catch_reaches_the_parents_coded_boundary()
    {
        var (root, child) = Run(Parent(callActivity: false, null, CreditDeclined), UncaughtCharge());

        child.FaultWork("charge", "card declined", CreditDeclined);

        child.Fault.ShouldNotBeNull().ErrorCode.ShouldBe(CreditDeclined);
        root.Transcript[^1].Disposition.ShouldBe(new BpmnErrorDisposition.Caught($"On{CreditDeclined}", CreditDeclined), root.Transcript.ToString());
    }

    [Fact]
    public void An_error_crosses_every_scope_between_the_thrower_and_its_catcher()
    {
        var outer = new BpmnProcessBuilder("outer")
            .StartEvent("OuterStart")
            .SubProcess("Inner", bindingRef: "inner")
            .EndEvent("OuterEnd")
            .ConnectSequence("OuterStart", "Inner", "OuterEnd")
            .Build();
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal)
            {
                ["settle"] = outer,
                ["inner"] = Thrower(CreditDeclined)
            }
        });
        var root = host.Start(Parent(callActivity: false, null, CreditDeclined));
        var inner = root.Children.ShouldHaveSingleItem().Children.ShouldHaveSingleItem();

        inner.CompleteWork("charge");

        root.Transcript[^1].Disposition.ShouldBe(new BpmnErrorDisposition.Caught($"On{CreditDeclined}", CreditDeclined), root.Transcript.ToString());
    }

    // --- Call activity -------------------------------------------------------------------------------------

    [Fact]
    public void A_called_process_error_reaches_the_callers_coded_boundary_through_the_faulted_outcome()
    {
        var (root, child) = Run(Parent(callActivity: true, null, CreditDeclined), Thrower(CreditDeclined));

        child.CompleteWork("charge");

        var parentEvaluation = root.Transcript[^1];
        var trigger = parentEvaluation.Trigger.ShouldBeOfType<BpmnTranscriptTrigger.WorkCompleted>();
        trigger.OutcomeNames.ShouldBe([BpmnInterpreter.CallActivityFaultedOutcomeName]);
        trigger.ErrorCode.ShouldBe(CreditDeclined);
        parentEvaluation.Disposition.ShouldBe(new BpmnErrorDisposition.Caught($"On{CreditDeclined}", CreditDeclined), root.Transcript.ToString());
        root.PendingWork.Select(work => work.BindingRef).ShouldBe([$"handle-{CreditDeclined.ToLowerInvariant()}"]);
    }

    [Fact]
    public void A_called_process_error_the_caller_does_not_catch_faults_the_caller_with_its_code()
    {
        var (root, child) = Run(Parent(callActivity: true), Thrower(CreditDeclined));

        child.CompleteWork("charge");

        var fault = root.Fault.ShouldNotBeNull(root.Transcript.ToString());
        fault.Code.ShouldBe(BpmnInterpreter.CallActivityFaultedFaultCode);
        fault.ErrorCode.ShouldBe(CreditDeclined);
        root.Transcript[^1].Disposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance);
    }

    // --- The reference host's own API ----------------------------------------------------------------------

    [Fact]
    public void FaultWork_names_the_error_a_coded_boundary_matches()
    {
        var definition = new BpmnProcessBuilder("charge")
            .StartEvent("Start")
            .ServiceTask("Charge", bindingRef: "charge")
            .EndEvent("End")
            .ConnectSequence("Start", "Charge", "End")
            .BoundaryEvent("Declined", "Charge", Error(CreditDeclined))
            .EndEvent("DeclinedEnd")
            .Connect("Declined", "DeclinedEnd")
            .Build();

        var named = new InMemoryBpmnHost().Start(definition);
        named.FaultWork("charge", "card declined", CreditDeclined);
        named.Transcript[^1].Disposition.ShouldBe(new BpmnErrorDisposition.Caught("Declined", CreditDeclined));
        named.Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName);

        var unnamed = new InMemoryBpmnHost().Start(definition);
        unnamed.FaultWork("charge", "card declined");
        unnamed.Transcript[^1].Disposition.ShouldBe(BpmnErrorDisposition.Propagated.Instance, "a coded boundary never catches a fault that names no error");
        unnamed.Fault.ShouldNotBeNull().ErrorCode.ShouldBeNull();
    }
}
