using System.Text.Json;
using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The escalation signal payload as a public contract: a host originating an escalation from outside the process
/// builds it with <see cref="BpmnInterpreter.EscalationPayload"/> rather than guessing its shape and casing, and
/// reads one with <see cref="BpmnInterpreter.TryReadEscalationCode"/> exactly as the interpreter matches it.
/// </summary>
public sealed class EscalationPayloadTests
{
    /// <summary>Verifies the public payload uses the expected JSON casing and omits an absent display name.</summary>
    [Fact]
    public void The_payload_carries_the_code_web_cased_and_omits_an_absent_name()
    {
        BpmnInterpreter.EscalationPayload("overdue").GetRawText().ShouldBe("""{"code":"overdue"}""");
    }

    /// <summary>Verifies surrounding whitespace is trimmed from both the matching code and the display name.</summary>
    [Fact]
    public void The_payload_carries_a_name_when_one_is_given()
    {
        BpmnInterpreter.EscalationPayload(" overdue ", " Overdue ").GetRawText().ShouldBe("""{"code":"overdue","name":"Overdue"}""");
    }

    /// <summary>Verifies a null, empty or whitespace code is rejected before a payload can be emitted.</summary>
    /// <param name="code">An invalid escalation code.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_payload_without_a_code_is_refused(string? code)
    {
        Should.Throw<ArgumentException>(() => BpmnInterpreter.EscalationPayload(code!));
    }

    /// <summary>Verifies hosts can recover the matching code from a payload created by the public helper.</summary>
    [Fact]
    public void A_built_payload_reads_back_its_code()
    {
        BpmnInterpreter.TryReadEscalationCode(BpmnInterpreter.EscalationPayload("overdue", "Overdue"), out var code).ShouldBeTrue();
        code.ShouldBe("overdue");
    }

    /// <summary>Verifies a hand-built payload's code is trimmed the way catcher codes are.</summary>
    [Fact]
    public void A_padded_code_reads_back_trimmed()
    {
        using var document = JsonDocument.Parse("""{"code":" overdue "}""");

        BpmnInterpreter.TryReadEscalationCode(document.RootElement, out var code).ShouldBeTrue();
        code.ShouldBe("overdue");
    }

    /// <summary>Verifies absent or malformed payloads return false and leave no escalation code to match.</summary>
    /// <param name="json">A payload without a readable code, or null for an absent payload.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("\"overdue\"")]
    [InlineData("{}")]
    [InlineData("""{"Code":"overdue"}""")]
    [InlineData("""{"code":42}""")]
    [InlineData("""{"code":""}""")]
    [InlineData("""{"code":"   "}""")]
    public void A_payload_without_a_readable_code_reads_as_none(string? json)
    {
        JsonElement? payload = json is null ? null : JsonDocument.Parse(json).RootElement;

        BpmnInterpreter.TryReadEscalationCode(payload, out var code).ShouldBeFalse();
        code.ShouldBeNull();
    }

    /// <summary>Verifies an actual interpreter escalation emits the same payload contract exposed to hosts.</summary>
    [Fact]
    public void The_payload_the_interpreter_raises_is_the_one_the_helper_builds()
    {
        var (parent, body) = ProcessFixtures.SubProcessThatEscalatesThenTerminates("overdue");
        var instance = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["sub"] = body }
        }).Start(parent);

        var signal = instance.Children.ShouldHaveSingleItem().Transcript.ShouldHaveSingleItem().Commands
            .ShouldHaveSingleItem().ShouldBeOfType<BpmnHostCommand.SignalEnclosingScope>();

        signal.Code.ShouldBe(BpmnInterpreter.EscalationSignalCode);
        signal.Payload.ShouldNotBeNull().GetRawText().ShouldBe(BpmnInterpreter.EscalationPayload("overdue").GetRawText());
    }
}
