using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

        written["status"]!.GetValue<int>().ShouldBe((int)BpmnTokenStatus.WaitingAtJoin);

        var declared = (JsonObject)Defs["bpmnTokenStatus"]!;
        declared["type"]!.GetValue<string>().ShouldBe("integer");
        declared["enum"]!.AsArray().Select(x => x!.GetValue<int>())
            .ShouldContain((int)BpmnTokenStatus.WaitingAtJoin);
    }

    /// <summary>
    /// The format names every property in camelCase, and says so explicitly on every one of them.
    /// <para>
    /// Explicitness is the point, not just the casing. With no naming policy configured, a property that
    /// omits the attribute goes out under its CLR name, and the format silently acquires a PascalCase
    /// member. That is exactly how <c>Bpmn.Model.State</c> and <c>BpmnProcessDefinition.Extensions</c>
    /// drifted apart from the rest of the format before the schema existed to show it - 53 properties,
    /// invisible until something generated a document and looked. This test is what stops the 54th.
    /// </para>
    /// </summary>
    [Fact]
    public void Model_declares_every_serialized_name()
    {
        BpmnSchemaGenerator.Generate(out var covered);

        var offending = (
            from type in covered
            where !type.IsEnum
            from property in BpmnSchemaGenerator.SerializedProperties(type)
            let declared = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            where !IsCamelCase(declared)
            select declared is null
                ? $"{type.Name}.{property.Name} declares no [JsonPropertyName], so it is written as \"{property.Name}\""
                : $"{type.Name}.{property.Name} is written as \"{declared}\", which is not camelCase")
            .ToArray();

        offending.ShouldBeEmpty(
            "Every serialized property must carry [JsonPropertyName] with a camelCase name. Without the "
            + "attribute the property goes out under its CLR name and the payload format acquires a second "
            + "naming convention.\n  " + string.Join("\n  ", offending));
    }

    /// <summary>
    /// Both documents the format covers are addressable. The schema's own <c>$ref</c> can only point at
    /// one, so a consumer persisting execution state needs <c>x-roots</c> to find the other.
    /// </summary>
    [Fact]
    public void Both_payload_roots_are_addressable()
    {
        var roots = (JsonObject)Schema["x-roots"]!;

        roots.Count.ShouldBe(2);

        foreach (var (name, _) in roots)
            RootSchema(name).ShouldNotBeNull($"x-roots lists '{name}' but it resolves to nothing.");
    }

    /// <summary>
    /// Every property the serializer emits for a realistic document must be described by the schema.
    /// This is the check that catches a computed get-only property quietly joining the contract, and
    /// it is why <c>additionalProperties: false</c> is safe to publish.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleDocuments))]
    public void Every_property_the_serializer_emits_is_described_by_the_schema(string root, object sample)
    {
        var document = JsonSerializer.SerializeToNode(sample, sample.GetType())!;
        var undescribed = new List<string>();

        Walk(document, RootSchema(root), "$", undescribed);

        undescribed.ShouldBeEmpty(
            $"These paths are written by System.Text.Json for {root} but not described by the schema:\n  "
            + string.Join("\n  ", undescribed));
    }

    /// <summary>
    /// The mirror image: a property the schema marks required must actually be written. A required
    /// property the serializer omits would make every real document fail validation.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleDocuments))]
    public void Every_required_property_is_actually_written(string root, object sample)
    {
        var document = JsonSerializer.SerializeToNode(sample, sample.GetType())!.AsObject();

        foreach (var name in Required(RootSchema(root)))
            document.ContainsKey(name).ShouldBeTrue($"Schema requires '{name}' on {root}, but it was not written.");
    }

    /// <summary>
    /// One sample per payload root. A definition alone would leave the whole of
    /// <c>Bpmn.Model.State</c> unchecked, which is the half of the format that drifted.
    /// </summary>
    public static TheoryData<string, object> SampleDocuments() => new()
    {
        { nameof(BpmnDefinitions), SampleDefinitions() },
        { nameof(BpmnExecutionState), SampleExecutionState() }
    };

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

    /// <summary>
    /// A wire name is checked for the convention, not for being mechanically derived from the CLR name.
    /// Deliberate renames are legitimate - <c>BpmnQName.Namespace</c> is published as <c>ns</c>, since the
    /// CLR name only reads that way because <c>namespace</c> is a keyword.
    /// </summary>
    private static bool IsCamelCase(string? name) =>
        !string.IsNullOrEmpty(name) && char.IsLower(name[0]) && name.All(char.IsLetterOrDigit);

    /// <summary>The schema describing one of the payload roots, looked up through <c>x-roots</c>.</summary>
    private static JsonObject RootSchema(string root) =>
        Resolve(new JsonObject { ["$ref"] = ((JsonObject)Schema["x-roots"]!)[root]!.DeepClone() })!;

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

    /// <summary>
    /// A state document populating every collection on the root, so the walk reaches each record type
    /// rather than skipping past an empty array.
    /// </summary>
    private static BpmnExecutionState SampleExecutionState() =>
        new(
            tokens: [new BpmnToken("tok:1", "work", flowId: "flow:1", status: BpmnTokenStatus.AwaitingChild, kind: BpmnTokenKind.Listener)],
            activeWork: [new BpmnActiveWork("node:1", "work", "tok:1", "sequence-flow", iterationId: "iter:1")],
            diagnostics: [new BpmnDiagnosticEvent("diag:1", BpmnDiagnosticKind.Scheduled, "Scheduled work", elementId: "work", details: new Dictionary<string, string> { ["reason"] = "test" })],
            sequence: 7,
            terminated: true,
            pendingFault: new BpmnPendingFault("BPMN-1", "A fault"),
            races: [new BpmnEventRace("race:1", "gateway", ["tok:1"], resolved: true)],
            loops: [new BpmnLoopState("loop:1", "tok:1", "work", isSequential: true, totalCount: 2, nextIndex: 1, completedCount: 1, items: [JsonSerializer.SerializeToElement("first")])],
            compensables: [new BpmnCompensable("comp:1", "work", "undo", BpmnCompensableStatus.Registered)],
            compensationRuns: [new BpmnCompensationRun("comprun:1", "tok:1", ["comp:1"])],
            cancelling: true);

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
