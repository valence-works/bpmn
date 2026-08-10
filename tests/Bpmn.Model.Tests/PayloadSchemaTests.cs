using System.Text.Json;
using System.Text.Json.Nodes;
using Bpmn.Model;
using Bpmn.Model.State;
using Bpmn.Schema.Generator;
using Shouldly;
using Xunit;

namespace Bpmn.Model.Tests;

/// <summary>
/// The guard that keeps the published JSON Schema honest.
/// <para>
/// A checked-in generated artifact is only worth having if something fails when it goes stale, so the
/// first test regenerates and compares. The rest cross-check the schema against what
/// <see cref="JsonSerializer"/> actually writes, because a schema that merely looks plausible is worse
/// than none: consumers generate types from it and trust the result.
/// </para>
/// </summary>
public sealed class PayloadSchemaTests
{
    private static readonly JsonObject Schema = LoadSchema();
    private static JsonObject Defs => (JsonObject)Schema["$defs"]!;

    [Fact]
    public void Checked_in_schema_matches_the_generator()
    {
        var generated = BpmnSchemaGenerator.Generate();
        var checkedIn = File.ReadAllText(SchemaPath());

        checkedIn.ShouldBe(
            generated,
            "The checked-in schema is stale. Regenerate it with:\n"
            + "  dotnet run --project tools/Bpmn.Schema.Generator -- .\n"
            + "and review the diff. A payload change and a schema change are the same review.");
    }

    [Fact]
    public void Schema_declares_the_current_format_version()
    {
        Schema["x-payloadFormatVersion"]!.GetValue<string>().ShouldBe(BpmnPayloadFormat.Version);
        Schema["$id"]!.GetValue<string>().ShouldBe(BpmnPayloadFormat.SchemaId);
        BpmnPayloadFormat.SchemaId.ShouldContain(BpmnPayloadFormat.Version);
    }

    /// <summary>
    /// The model declares no <c>JsonStringEnumConverter</c>, so enums go out as integers. This is the
    /// single most likely thing for a hand-written client mirror to get wrong, so it is asserted
    /// against real serializer output rather than against the model's declaration.
    /// </summary>
    [Fact]
    public void Enums_are_serialized_as_integers_exactly_as_the_schema_claims()
    {
        var token = new BpmnToken("t1", "e1", status: BpmnTokenStatus.WaitingAtJoin);
        var written = JsonSerializer.SerializeToNode(token)!.AsObject();

        // Note the PascalCase key: BpmnToken declares no [JsonPropertyName], so the CLR name goes out
        // verbatim. Asserting "status" here would pass against an assumption and fail against reality.
        written["Status"]!.GetValue<int>().ShouldBe((int)BpmnTokenStatus.WaitingAtJoin);

        var declared = (JsonObject)Defs["bpmnTokenStatus"]!;
        declared["type"]!.GetValue<string>().ShouldBe("integer");
        declared["enum"]!.AsArray().Select(x => x!.GetValue<int>())
            .ShouldContain((int)BpmnTokenStatus.WaitingAtJoin);
    }

    /// <summary>
    /// Every property the serializer emits for a realistic document must be described by the schema.
    /// This is the check that catches a computed get-only property quietly joining the contract, and
    /// it is why <c>additionalProperties: false</c> is safe to publish.
    /// </summary>
    [Fact]
    public void Every_property_the_serializer_emits_is_described_by_the_schema()
    {
        var document = JsonSerializer.SerializeToNode(SampleDefinitions())!;
        var undescribed = new List<string>();

        Walk(document, RefTarget((JsonObject)Schema), "$", undescribed);

        undescribed.ShouldBeEmpty(
            "These paths are written by System.Text.Json but not described by the schema:\n  "
            + string.Join("\n  ", undescribed));
    }

    /// <summary>
    /// The mirror image: a property the schema marks required must actually be written. A required
    /// property the serializer omits would make every real document fail validation.
    /// </summary>
    [Fact]
    public void Every_required_property_is_actually_written()
    {
        var document = JsonSerializer.SerializeToNode(SampleDefinitions())!.AsObject();
        var definition = RefTarget((JsonObject)Schema);

        foreach (var name in Required(definition))
            document.ContainsKey(name).ShouldBeTrue($"Schema requires '{name}' on the root, but it was not written.");
    }

    // ---- walking ---------------------------------------------------------------------------------

    private static void Walk(JsonNode? node, JsonObject? schema, string path, List<string> undescribed)
    {
        if (node is null || schema is null)
            return;

        switch (node)
        {
            case JsonObject obj when schema["properties"] is JsonObject properties:
                foreach (var entry in obj)
                {
                    if (properties[entry.Key] is not { } propertySchema)
                    {
                        undescribed.Add($"{path}.{entry.Key}");
                        continue;
                    }

                    Walk(entry.Value, Resolve(propertySchema), $"{path}.{entry.Key}", undescribed);
                }

                break;

            // A free-form object (additionalProperties describing values, e.g. a string dictionary).
            case JsonObject obj when schema["additionalProperties"] is JsonObject valueSchema:
                foreach (var entry in obj)
                    Walk(entry.Value, Resolve(valueSchema), $"{path}.{entry.Key}", undescribed);

                break;

            case JsonArray array when schema["items"] is { } itemSchema:
                var resolvedItems = Resolve(itemSchema);

                for (var i = 0; i < array.Count; i++)
                    Walk(array[i], resolvedItems, $"{path}[{i}]", undescribed);

                break;
        }
    }

    /// <summary>Follows a <c>$ref</c> or an <c>anyOf</c> containing one, to the object it describes.</summary>
    private static JsonObject? Resolve(JsonNode node)
    {
        if (node is not JsonObject obj)
            return null;

        if (obj["$ref"] is { } reference)
            return Defs[reference.GetValue<string>()["#/$defs/".Length..]] as JsonObject;

        if (obj["anyOf"] is JsonArray anyOf)
            return anyOf.Select(x => x is null ? null : Resolve(x)).FirstOrDefault(x => x is not null);

        return obj;
    }

    private static JsonObject RefTarget(JsonObject schema) => Resolve(schema)!;

    private static IEnumerable<string> Required(JsonObject schema) =>
        schema["required"] is JsonArray required
            ? required.Select(x => x!.GetValue<string>())
            : [];

    // ---- fixtures --------------------------------------------------------------------------------

    /// <summary>
    /// A document deliberately exercising the awkward corners: a dictionary, a nested collection, an
    /// enum, retained foreign content and diagram interchange.
    /// </summary>
    private static BpmnDefinitions SampleDefinitions()
    {
        var process = new BpmnProcessBuilder("p1")
            .Name("Sample")
            .Variable("items", "any")
            .StartEvent("start")
            .ServiceTask("work", "Do work")
            .ExclusiveGateway("choice")
            .EndEvent("done")
            .Connect("start", "work")
            .Connect("work", "choice")
            .Connect("choice", "done", isDefault: true)
            .Build();

        return new BpmnDefinitions(
            Id: "defs",
            TargetNamespace: "https://example.test",
            Processes: [process]);
    }

    private static JsonObject LoadSchema() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!;

    /// <summary>
    /// Walks up from the test binaries to the repository root. Keyed off the solution file so it does
    /// not depend on how deep the build output happens to be.
    /// </summary>
    private static string SchemaPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bpmn.slnx")))
            directory = directory.Parent;

        directory.ShouldNotBeNull("Could not locate the repository root from the test output directory.");
        return Path.Combine(directory!.FullName, "src", "Bpmn.Model", "schema", BpmnPayloadFormat.SchemaFileName);
    }
}
