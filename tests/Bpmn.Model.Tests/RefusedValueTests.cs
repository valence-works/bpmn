using System.Text.Json;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Model.Tests;

public sealed class RefusedValueTests
{
    [Fact]
    public void Refusal_round_trips_with_exact_host_metadata_and_no_inline_value()
    {
        var value = BpmnValue.Refused("host.variable.denied", "Collection is withheld.");

        var json = JsonSerializer.Serialize(value);
        var restored = JsonSerializer.Deserialize<BpmnValue>(json)!;

        restored.ShouldBe(value);
        restored.Presence.ShouldBe(BpmnValuePresence.Refused);
        restored.HasValue.ShouldBeFalse();
        restored.Json.ShouldBeNull();
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("presence").GetInt32().ShouldBe(4);
        document.RootElement.GetProperty("faultCode").GetString().ShouldBe("host.variable.denied");
        document.RootElement.GetProperty("faultMessage").GetString().ShouldBe("Collection is withheld.");
    }

    [Fact]
    public void Existing_value_json_and_three_value_deconstruction_remain_unchanged()
    {
        var value = BpmnValue.FromInteger(3);
        var (presence, typeHint, json) = value;

        presence.ShouldBe(BpmnValuePresence.Present);
        typeHint.ShouldBe(BpmnValueTypes.Integer);
        json!.Value.GetInt32().ShouldBe(3);
        JsonSerializer.Serialize(value).ShouldBe("{\"presence\":2,\"typeHint\":\"integer\",\"json\":3,\"HasValue\":true}");
    }

    [Theory]
    [InlineData("{\"presence\":0,\"typeHint\":null,\"json\":null,\"HasValue\":false}", BpmnValuePresence.Absent)]
    [InlineData("{\"presence\":1,\"typeHint\":null,\"json\":null,\"HasValue\":false}", BpmnValuePresence.Null)]
    [InlineData("{\"presence\":2,\"typeHint\":\"integer\",\"json\":3,\"HasValue\":true}", BpmnValuePresence.Present)]
    [InlineData("{\"presence\":3,\"typeHint\":null,\"json\":null,\"HasValue\":false}", BpmnValuePresence.StoredExternally)]
    public void Values_from_legacy_json_keep_existing_ordinals_and_omit_refusal_metadata(
        string json,
        BpmnValuePresence expectedPresence)
    {
        var value = JsonSerializer.Deserialize<BpmnValue>(json)!;

        value.Presence.ShouldBe(expectedPresence);
        value.FaultCode.ShouldBeNull();
        value.FaultMessage.ShouldBeNull();
        JsonSerializer.Serialize(value).ShouldBe(json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Refusal_factory_requires_a_usable_code(string? code) =>
        Should.Throw<ArgumentException>(() => BpmnValue.Refused(code!, "Withheld."));

    [Fact]
    public void Refusal_factory_requires_a_message_but_preserves_an_empty_one()
    {
        Should.Throw<ArgumentNullException>(() => BpmnValue.Refused("host.denied", null!));
        BpmnValue.Refused("host.denied", "").FaultMessage.ShouldBe("");
    }
}
