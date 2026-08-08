using Bpmn.Runtime.InMemory;
using Bpmn.Semantics;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The transcript. It is the thing a failing test prints, so what it says has to be worth reading: what
/// provoked the interpreter, what it decided, what it asked the host to do, and which tokens moved.
/// </summary>
public sealed class TranscriptTests
{
    private readonly InMemoryBpmnHost _host = new();

    [Fact]
    public void Every_evaluation_is_recorded_with_its_trigger_and_continuation()
    {
        var instance = _host.Start(ProcessFixtures.Linear());
        instance.CompleteWork("work");

        instance.Transcript.Count.ShouldBe(2);

        instance.Transcript[0].Trigger.ShouldBeOfType<BpmnTranscriptTrigger.Started>();
        instance.Transcript[0].Continuation.ShouldBeOfType<BpmnContinuation.Defer>();
        instance.Transcript[0].Commands.ShouldHaveSingleItem().ShouldBeOfType<BpmnHostCommand.StartWork>();

        var completion = instance.Transcript[1];
        completion.Trigger.ShouldBeOfType<BpmnTranscriptTrigger.WorkCompleted>().Work.ElementId.ShouldBe("Work");
        completion.Continuation.ShouldBeOfType<BpmnContinuation.Complete>().Outcome.ShouldBe(BpmnInterpreter.DoneOutcomeName);
    }

    [Fact]
    public void Token_movement_is_recorded_as_minting_and_status_changes()
    {
        var instance = _host.Start(ProcessFixtures.Linear());

        var start = instance.Transcript[0];
        start.TokenMoves.ShouldContain(move => move.ElementId == "Start" && move.From is null);
        start.TokenMoves.Last().ElementId.ShouldBe("Work");

        instance.CompleteWork("work");

        instance.Transcript[1].TokenMoves.ShouldContain(move => move.ElementId == "End");
    }

    [Fact]
    public void A_fault_records_how_BPMN_disposed_of_it()
    {
        var caught = _host.Start(ProcessFixtures.RiskyActivity(withErrorBoundary: true));
        caught.FaultWork("risky", "boom");
        caught.Transcript.Last().Disposition.ShouldBeOfType<BpmnErrorDisposition.Caught>();

        var propagated = _host.Start(ProcessFixtures.RiskyActivity(withErrorBoundary: false));
        propagated.FaultWork("risky", "boom");
        propagated.Transcript.Last().Disposition.ShouldBeOfType<BpmnErrorDisposition.Propagated>();
    }

    [Fact]
    public void An_entry_prints_its_trigger_its_decision_its_commands_and_its_tokens()
    {
        var instance = _host.Start(ProcessFixtures.TimerBoundary("P7D"));
        _host.Clock.Advance(TimeSpan.FromDays(7));

        var fired = instance.Transcript.Single(entry => entry.Trigger is BpmnTranscriptTrigger.TimerFired);
        var text = fired.ToString();

        text.ShouldContain("timer fired Timeout");
        text.ShouldContain("7.00:00:00");
        text.ShouldContain("CancelWorkSubtree");
        text.ShouldContain(BpmnInterpreter.BoundaryHostInterruptedReason);
        text.ShouldContain("tokens:");
    }

    [Fact]
    public void A_whole_transcript_prints_one_entry_per_line_group_in_order()
    {
        var instance = _host.Start(ProcessFixtures.ExclusiveChoice());
        instance.CompleteWork("decide", "Approved");
        instance.CompleteWork("approve");

        var text = instance.Transcript.ToString();

        text.ShouldContain("#1");
        text.ShouldContain("#3");
        text.ShouldContain("outcomes=[Approved]");
        text.ShouldContain($"Complete({BpmnInterpreter.DoneOutcomeName})");
    }

    [Fact]
    public void Nested_scopes_share_one_ordering_so_their_transcripts_can_be_interleaved()
    {
        var (parent, body) = ProcessFixtures.NestedSubProcess();
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, Bpmn.Model.BpmnProcessDefinition>(StringComparer.Ordinal) { ["sub"] = body }
        });

        var instance = host.Start(parent);
        instance.Children.Single().CompleteWork("inner");

        var sequences = host.Instances.SelectMany(scope => scope.Transcript).Select(entry => entry.Sequence).ToArray();

        sequences.Distinct().Count().ShouldBe(sequences.Length, "a sequence number is unique across the host");
        sequences.OrderBy(sequence => sequence).ShouldBe(Enumerable.Range(1, sequences.Length));
    }

    [Fact]
    public void An_empty_transcript_says_so_rather_than_printing_nothing()
    {
        new BpmnTranscript().ToString().ShouldBe("(nothing evaluated)");
    }
}
