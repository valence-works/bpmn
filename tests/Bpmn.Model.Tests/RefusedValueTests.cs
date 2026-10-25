using System.Text.Json;
using System.Text.Json.Serialization;
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
    public void Value_json_omits_computed_has_value_and_three_value_deconstruction_remains()
    {
        var value = BpmnValue.FromInteger(3);
        var (presence, typeHint, json) = value;

        presence.ShouldBe(BpmnValuePresence.Present);
        typeHint.ShouldBe(BpmnValueTypes.Integer);
        json!.Value.GetInt32().ShouldBe(3);
        JsonSerializer.Serialize(value).ShouldBe("{\"presence\":2,\"typeHint\":\"integer\",\"json\":3}");
    }

    [Theory]
    [InlineData("{\"presence\":0,\"typeHint\":null,\"json\":null,\"HasValue\":false}", BpmnValuePresence.Absent, false, false)]
    [InlineData("{\"presence\":1,\"typeHint\":null,\"json\":null,\"HasValue\":false}", BpmnValuePresence.Null, false, false)]
    [InlineData("{\"presence\":2,\"typeHint\":\"integer\",\"json\":3,\"HasValue\":true}", BpmnValuePresence.Present, true, false)]
    [InlineData("{\"presence\":3,\"typeHint\":null,\"json\":null,\"HasValue\":false}", BpmnValuePresence.StoredExternally, false, false)]
    [InlineData("{\"presence\":0,\"typeHint\":null,\"json\":null,\"HasValue\":true}", BpmnValuePresence.Absent, false, false)]
    [InlineData("{\"presence\":2,\"typeHint\":\"integer\",\"json\":3,\"HasValue\":false}", BpmnValuePresence.Present, true, false)]
    [InlineData("{\"presence\":0,\"typeHint\":null,\"json\":null,\"hasValue\":true}", BpmnValuePresence.Absent, false, true)]
    [InlineData("{\"presence\":2,\"typeHint\":\"integer\",\"json\":3,\"hasValue\":false}", BpmnValuePresence.Present, true, true)]
    public void Values_from_legacy_json_recompute_has_value_and_omit_it_from_current_writes(
        string json,
        BpmnValuePresence expectedPresence,
        bool expectedHasValue,
        bool useCamelCase)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = useCamelCase ? JsonNamingPolicy.CamelCase : null,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        var value = JsonSerializer.Deserialize<BpmnValue>(json, options)!;

        value.Presence.ShouldBe(expectedPresence);
        value.HasValue.ShouldBe(expectedHasValue);
        value.FaultCode.ShouldBeNull();
        value.FaultMessage.ShouldBeNull();
        using var current = JsonDocument.Parse(JsonSerializer.Serialize(value, options));
        current.RootElement.TryGetProperty("HasValue", out _).ShouldBeFalse();
        current.RootElement.TryGetProperty("hasValue", out _).ShouldBeFalse();
        current.RootElement.GetProperty("presence").GetInt32().ShouldBe((int)expectedPresence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_value_shapes_omit_computed_has_value_under_default_and_camel_case_options(bool useCamelCase)
    {
        var options = useCamelCase ? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } : null;
        BpmnValue[] values =
        [
            BpmnValue.Absent,
            BpmnValue.Null,
            BpmnValue.FromInteger(3),
            new BpmnValue(BpmnValuePresence.StoredExternally),
            BpmnValue.Refused("host.variable.denied", "Collection is withheld.")
        ];

        foreach (var value in values)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, options));
            var root = document.RootElement;

            root.GetProperty("presence").GetInt32().ShouldBe((int)value.Presence);
            root.TryGetProperty("HasValue", out _).ShouldBeFalse();
            root.TryGetProperty("hasValue", out _).ShouldBeFalse();
        }
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
