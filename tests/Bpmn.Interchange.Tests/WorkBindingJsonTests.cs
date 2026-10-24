using System.Text.Json;
using System.Text.Json.Serialization;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Interchange.Tests;

/// <summary>Checks that a host can transfer the library's work bindings with default JSON settings.</summary>
public sealed class WorkBindingJsonTests
{
    private const string Document = """
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:vw="https://bpmn.valenceworks.io/schema/bpmn"
                          xmlns:acme="https://example.test/metadata"
                          id="Definitions" targetNamespace="https://example.test/orders">
          <bpmn:extensionElements>
            <acme:document><acme:owner>orders</acme:owner></acme:document>
          </bpmn:extensionElements>
          <bpmn:process id="main">
            <bpmn:startEvent id="start" />
            <bpmn:callActivity id="call" calledElement="Approval" vw:waitForCompletion="false" />
            <bpmn:subProcess id="embedded" acme:owner="operations">
              <bpmn:startEvent id="embeddedStart" />
              <bpmn:serviceTask id="embeddedWork" acme:source="dispatch">
                <bpmn:extensionElements>
                  <acme:metadata><acme:tag>retained value</acme:tag></acme:metadata>
                </bpmn:extensionElements>
              </bpmn:serviceTask>
              <bpmn:endEvent id="embeddedEnd" />
              <bpmn:sequenceFlow id="embeddedToWork" sourceRef="embeddedStart" targetRef="embeddedWork" />
              <bpmn:sequenceFlow id="embeddedToEnd" sourceRef="embeddedWork" targetRef="embeddedEnd" />
            </bpmn:subProcess>
            <bpmn:transaction id="transaction">
              <bpmn:startEvent id="transactionStart" />
              <bpmn:serviceTask id="transactionWork" />
              <bpmn:endEvent id="transactionEnd" />
              <bpmn:sequenceFlow id="transactionToWork" sourceRef="transactionStart" targetRef="transactionWork" />
              <bpmn:sequenceFlow id="transactionToEnd" sourceRef="transactionWork" targetRef="transactionEnd" />
            </bpmn:transaction>
            <bpmn:subProcess id="eventScope" triggeredByEvent="true">
              <bpmn:startEvent id="eventStart">
                <bpmn:timerEventDefinition><bpmn:timeDuration>PT1M</bpmn:timeDuration></bpmn:timerEventDefinition>
              </bpmn:startEvent>
              <bpmn:serviceTask id="eventWork" />
              <bpmn:endEvent id="eventEnd" />
              <bpmn:sequenceFlow id="eventToWork" sourceRef="eventStart" targetRef="eventWork" />
              <bpmn:sequenceFlow id="eventToEnd" sourceRef="eventWork" targetRef="eventEnd" />
            </bpmn:subProcess>
            <bpmn:endEvent id="done" />
            <bpmn:sequenceFlow id="toCall" sourceRef="start" targetRef="call" />
            <bpmn:sequenceFlow id="toEmbedded" sourceRef="call" targetRef="embedded" />
            <bpmn:sequenceFlow id="toTransaction" sourceRef="embedded" targetRef="transaction" />
            <bpmn:sequenceFlow id="toDone" sourceRef="transaction" targetRef="done" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private readonly BpmnXmlReader _reader = new();
    private readonly BpmnXmlWriter _writer = new();

    [Fact]
    public void Base_binding_and_binding_list_round_trip_all_cases_with_default_json_options()
    {
        BpmnWorkBinding[] bindings =
        [
            new BpmnWorkBinding.TimerWait("orders", "delay", "node-delay", BpmnBindingSlot.Primary, "PT15M"),
            new BpmnWorkBinding.MessageWait("orders", "approval", "node-approval", BpmnBindingSlot.ScopeListener, "Approved"),
            new BpmnWorkBinding.SignalWait("orders", "cancel", "node-cancel", BpmnBindingSlot.Primary, "Cancelled"),
            new BpmnWorkBinding.MessagePublish("orders", "notify", "node-notify", BpmnBindingSlot.Primary, "Order shipped"),
            new BpmnWorkBinding.CallProcess("orders", "lookup", "node-lookup", BpmnBindingSlot.Primary, null, WaitForCompletion: false),
            new BpmnWorkBinding.CallProcess("orders", "charge", "node-charge", BpmnBindingSlot.Primary, "ChargeCard", WaitForCompletion: true),
            new BpmnWorkBinding.NestedProcess("orders", "embedded", "node-embedded", BpmnBindingSlot.Primary, NestedDefinition()),
            new BpmnWorkBinding.UnboundTask("orders", "review", "node-review", BpmnBindingSlot.Primary, BpmnElementTypes.UserTask),
            new BpmnWorkBinding.SignalPublish("orders", "announce", "node-announce", BpmnBindingSlot.Primary, "Order closed"),
            new BpmnWorkBinding.TimerDateWait("orders", "deadline", "node-deadline", BpmnBindingSlot.Primary, "2026-12-24T18:00:00Z")
        ];

        var json = JsonSerializer.Serialize<IReadOnlyList<BpmnWorkBinding>>(bindings);
        using var document = JsonDocument.Parse(json);
        var serializedBindings = document.RootElement.EnumerateArray().ToArray();

        serializedBindings.ShouldAllBe(binding => binding.EnumerateObject().First().Name == "kind");
        serializedBindings.Select(binding => binding.GetProperty("kind").GetString()).ShouldBe(
        ["timerWait", "messageWait", "signalWait", "messagePublish", "callProcess", "callProcess", "nestedProcess", "unboundTask", "signalPublish", "timerDateWait"]);
        serializedBindings[0].GetProperty("processId").GetString().ShouldBe("orders");
        serializedBindings[0].GetProperty("elementId").GetString().ShouldBe("delay");
        serializedBindings[0].GetProperty("bindingRef").GetString().ShouldBe("node-delay");
        serializedBindings[0].GetProperty("slot").GetInt32().ShouldBe(0);
        serializedBindings[1].GetProperty("slot").GetInt32().ShouldBe(1);
        serializedBindings[0].GetProperty("isoDuration").GetString().ShouldBe("PT15M");
        serializedBindings[1].GetProperty("messageName").GetString().ShouldBe("Approved");
        serializedBindings[2].GetProperty("signalName").GetString().ShouldBe("Cancelled");
        serializedBindings[3].GetProperty("messageName").GetString().ShouldBe("Order shipped");
        serializedBindings[4].GetProperty("calledElement").ValueKind.ShouldBe(JsonValueKind.Null);
        serializedBindings[4].GetProperty("waitForCompletion").GetBoolean().ShouldBeFalse();
        serializedBindings[5].GetProperty("calledElement").GetString().ShouldBe("ChargeCard");
        serializedBindings[5].GetProperty("waitForCompletion").GetBoolean().ShouldBeTrue();
        serializedBindings[6].GetProperty("definition").GetProperty("processId").GetString().ShouldBe("embedded");
        serializedBindings[7].GetProperty("taskType").GetString().ShouldBe(BpmnElementTypes.UserTask);
        serializedBindings[8].GetProperty("signalName").GetString().ShouldBe("Order closed");
        serializedBindings[9].GetProperty("isoDate").GetString().ShouldBe("2026-12-24T18:00:00Z");
        serializedBindings[0].TryGetProperty("ProcessId", out _).ShouldBeFalse();

        var decoded = JsonSerializer.Deserialize<IReadOnlyList<BpmnWorkBinding>>(json);
        decoded.ShouldNotBeNull();
        decoded.Count.ShouldBe(bindings.Length);
        decoded[0].ShouldBeOfType<BpmnWorkBinding.TimerWait>().IsoDuration.ShouldBe("PT15M");
        decoded[1].ShouldBeOfType<BpmnWorkBinding.MessageWait>().MessageName.ShouldBe("Approved");
        decoded[2].ShouldBeOfType<BpmnWorkBinding.SignalWait>().SignalName.ShouldBe("Cancelled");
        decoded[3].ShouldBeOfType<BpmnWorkBinding.MessagePublish>().MessageName.ShouldBe("Order shipped");
        var nullableCall = decoded[4].ShouldBeOfType<BpmnWorkBinding.CallProcess>();
        nullableCall.CalledElement.ShouldBeNull();
        nullableCall.WaitForCompletion.ShouldBeFalse();
        var namedCall = decoded[5].ShouldBeOfType<BpmnWorkBinding.CallProcess>();
        namedCall.CalledElement.ShouldBe("ChargeCard");
        namedCall.WaitForCompletion.ShouldBeTrue();
        decoded[6].ShouldBeOfType<BpmnWorkBinding.NestedProcess>().Definition.Elements.Single().ElementId.ShouldBe("inner-task");
        decoded[7].ShouldBeOfType<BpmnWorkBinding.UnboundTask>().TaskType.ShouldBe(BpmnElementTypes.UserTask);
        decoded[8].ShouldBeOfType<BpmnWorkBinding.SignalPublish>().SignalName.ShouldBe("Order closed");
        decoded[9].ShouldBeOfType<BpmnWorkBinding.TimerDateWait>().IsoDate.ShouldBe("2026-12-24T18:00:00Z");

        BpmnWorkBinding singleBinding = bindings[0];
        var singleJson = JsonSerializer.Serialize(singleBinding);
        JsonSerializer.Deserialize<BpmnWorkBinding>(singleJson)
            .ShouldBeOfType<BpmnWorkBinding.TimerWait>().IsoDuration.ShouldBe("PT15M");
    }

    [Fact]
    public void Unknown_binding_kind_is_rejected()
    {
        const string json = """{"kind":"futureBinding","processId":"orders","elementId":"x","bindingRef":"node-x","slot":0}""";

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<BpmnWorkBinding>(json));
    }

    [Fact]
    public void Reader_bindings_survive_a_default_json_envelope_and_xml_round_trip()
    {
        var original = _reader.Read(Document);
        var beforeNested = NestedBindings(original);
        var envelope = new WorkEnvelope(original.Definitions, original.Bindings);
        var json = JsonSerializer.Serialize(envelope);
        var transferred = JsonSerializer.Deserialize<WorkEnvelope>(json)
            ?? throw new InvalidOperationException("The work envelope did not deserialize.");

        var written = _writer.Write(transferred.Definitions, transferred.Bindings);
        var result = _reader.Read(written);
        var afterNested = NestedBindings(result);

        result.Definitions.Extensions.ShouldBe(original.Definitions.Extensions);
        afterNested.Keys.Order().ShouldBe(beforeNested.Keys.Order());
        foreach (var elementId in beforeNested.Keys)
        {
            var before = beforeNested[elementId].Definition;
            var after = afterNested[elementId].Definition;
            after.ProcessId.ShouldBe(before.ProcessId);
            after.IsTransaction.ShouldBe(before.IsTransaction);
            after.Elements.Select(element => element.ElementId).ShouldBe(before.Elements.Select(element => element.ElementId));
            after.SequenceFlows.Select(flow => flow.FlowId).ShouldBe(before.SequenceFlows.Select(flow => flow.FlowId));
            after.Extensions.ShouldBe(before.Extensions);
        }

        var originalTaskExtensions = beforeNested["embedded"].Definition.Elements
            .Single(element => element.ElementId == "embeddedWork").Extensions;
        var transferredTaskExtensions = afterNested["embedded"].Definition.Elements
            .Single(element => element.ElementId == "embeddedWork").Extensions;
        transferredTaskExtensions.ShouldBe(originalTaskExtensions);
        transferredTaskExtensions.ForeignAttributes.Single().Value.ShouldBe("dispatch");
        transferredTaskExtensions.ExtensionElements.Single().Children.Single().Value.ShouldBe("retained value");

        var originalCall = original.Bindings.OfType<BpmnWorkBinding.CallProcess>().ShouldHaveSingleItem();
        var transferredCall = result.Bindings.OfType<BpmnWorkBinding.CallProcess>().ShouldHaveSingleItem();
        transferredCall.CalledElement.ShouldBe(originalCall.CalledElement);
        transferredCall.WaitForCompletion.ShouldBeFalse();
        result.Definitions.Processes.ShouldHaveSingleItem().Elements
            .Single(element => element.ElementId == "transaction").IsTransaction.ShouldBeTrue();
        result.Definitions.Processes[0].Elements
            .Single(element => element.ElementId == "eventScope").TriggeredByEvent.ShouldBeTrue();
        result.Bindings.OfType<BpmnWorkBinding.TimerWait>().ShouldContain(binding =>
            binding.ElementId == "eventScope" && binding.Slot == BpmnBindingSlot.ScopeListener && binding.IsoDuration == "PT1M");
    }

    private static BpmnProcessDefinition NestedDefinition() => new(
        "embedded",
        Elements: [new BpmnElement("inner-task", BpmnElementTypes.ServiceTask)]);

    private static Dictionary<string, BpmnWorkBinding.NestedProcess> NestedBindings(BpmnImportResult result) =>
        result.Bindings.OfType<BpmnWorkBinding.NestedProcess>().ToDictionary(binding => binding.ElementId);

    private sealed record WorkEnvelope(
        [property: JsonPropertyName("definitions")] BpmnDefinitions Definitions,
        [property: JsonPropertyName("bindings")] IReadOnlyList<BpmnWorkBinding> Bindings);
}
