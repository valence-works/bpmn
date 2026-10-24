using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bpmn.Interchange;
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
    public void Every_covered_type_declares_every_serialized_name()
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
    /// Every payload root is addressable through <c>x-roots</c>, including roots that consumers use as
    /// components inside their own envelope.
    /// </summary>
    [Fact]
    public void All_payload_roots_are_addressable()
    {
        var roots = (JsonObject)Schema["x-roots"]!;

        roots.Count.ShouldBe(3);
        Schema["oneOf"]!.AsArray().Count.ShouldBe(3);

        foreach (var (name, _) in roots)
            RootSchema(name).ShouldNotBeNull($"x-roots lists '{name}' but it resolves to nothing.");
    }

    [Fact]
    public void Every_binding_branch_requires_its_discriminator_constant()
    {
        var union = RootSchema(nameof(BpmnWorkBinding));
        var branches = union["oneOf"]!.AsArray();
        var discriminatorValues = new List<string>();

        branches.Count.ShouldBe(10);

        foreach (var branch in branches)
        {
            var described = Resolve(branch!)!;
            var kind = (JsonObject)described["properties"]!["kind"]!;
            kind["type"]!.GetValue<string>().ShouldBe("string");
            Required(described).ShouldContain("kind");
            discriminatorValues.Add(kind["const"]!.GetValue<string>());
        }

        discriminatorValues.OrderBy(value => value, StringComparer.Ordinal).ToArray().ShouldBe([
            "callProcess",
            "messagePublish",
            "messageWait",
            "nestedProcess",
            "signalPublish",
            "signalWait",
            "timerCycleWait",
            "timerDateWait",
            "timerWait",
            "unboundTask"
        ]);
    }

    [Fact]
    public void Binding_slots_are_numeric_and_match_the_schema_enum()
    {
        var serializedSlots = SampleBindings()
            .Select(binding => JsonSerializer.SerializeToNode((BpmnWorkBinding)binding)!.AsObject()["slot"]!.GetValue<int>())
            .Distinct()
            .Order()
            .ToArray();
        var declared = (JsonObject)Defs["bpmnBindingSlot"]!;

        serializedSlots.ShouldBe([0, 1]);
        declared["type"]!.GetValue<string>().ShouldBe("integer");
        declared["enum"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray().ShouldBe([0, 1]);
        declared["x-enumNames"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray()
            .ShouldBe([nameof(BpmnBindingSlot.Primary), nameof(BpmnBindingSlot.ScopeListener)]);
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
        var document = SerializeSample(root, sample);
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
        var document = SerializeSample(root, sample).AsObject();
        var schema = RootSchema(root);

        if (schema["oneOf"] is JsonArray branches)
        {
            var selectedBranch = SelectBranch(document, branches);
            selectedBranch.ShouldNotBeNull($"Serialized {root} sample did not match one schema branch.");
            schema = selectedBranch!;
        }

        foreach (var name in Required(schema))
            document.ContainsKey(name).ShouldBeTrue($"Schema requires '{name}' on {root}, but it was not written.");
    }

    /// <summary>
    /// Samples cover all three addressable roots, including every polymorphic binding branch. A
    /// definition alone would leave the whole of <c>Bpmn.Model.State</c> unchecked, which is the half
    /// of the format that drifted.
    /// </summary>
    public static TheoryData<string, object> SampleDocuments()
    {
        var bindings = SampleBindings();
        return new()
        {
            { nameof(BpmnDefinitions), SampleDefinitions() },
            { nameof(BpmnExecutionState), SampleExecutionState() },
            { nameof(BpmnWorkBinding), bindings[0] },
            { nameof(BpmnWorkBinding), bindings[1] },
            { nameof(BpmnWorkBinding), bindings[2] },
            { nameof(BpmnWorkBinding), bindings[3] },
            { nameof(BpmnWorkBinding), bindings[4] },
            { nameof(BpmnWorkBinding), bindings[5] },
            { nameof(BpmnWorkBinding), bindings[6] },
            { nameof(BpmnWorkBinding), bindings[7] },
            { nameof(BpmnWorkBinding), bindings[8] },
            { nameof(BpmnWorkBinding), bindings[9] },
            { nameof(BpmnWorkBinding), bindings[10] }
        };
    }

    // ---- walking ---------------------------------------------------------------------------------

    private static void Walk(JsonNode? node, JsonObject? schema, string path, List<string> undescribed)
    {
        if (node is null || schema is null)
            return;

        if (schema["oneOf"] is JsonArray branches)
        {
            var matchingBranch = SelectBranch(node, branches);
            if (matchingBranch is null)
            {
                undescribed.Add($"{path} does not match exactly one discriminated schema branch");
                return;
            }

            schema = matchingBranch;
        }

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

    /// <summary>
    /// Selects the unique branch whose required constant discriminator matches the serializer output.
    /// Ignoring a <c>oneOf</c> would make a walk of the abstract base accept a document without checking
    /// any of the concrete binding properties.
    /// </summary>
    private static JsonObject? SelectBranch(JsonNode node, JsonArray branches)
    {
        if (node is not JsonObject value)
            return null;

        var matches = branches
            .Select(branch => branch is null ? null : Resolve(branch))
            .Where(branch => branch is not null && HasMatchingConstant(branch, value))
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool HasMatchingConstant(JsonObject schema, JsonObject value)
    {
        if (schema["properties"] is not JsonObject properties)
            return false;

        foreach (var (name, propertySchema) in properties)
        {
            if (propertySchema is JsonObject describedProperty
                && describedProperty["const"] is { } expected
                && value.TryGetPropertyValue(name, out var actual)
                && JsonNode.DeepEquals(actual, expected))
            {
                return true;
            }
        }

        return false;
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

        // An expression condition reaches the expression shape and its retained content.
        process = process with
        {
            ExpressionLanguage = "urn:example:language",
            // A script task reaches the script shape and its retained content.
            Elements =
            [
                .. process.Elements,
                new BpmnElement("script", BpmnElementTypes.ScriptTask, bindingRef: "script")
                {
                    Script = new BpmnScript(
                        "total = items.length",
                        "text/javascript",
                        new BpmnExtensions(ForeignAttributes: [new BpmnForeignAttribute(new BpmnQName("urn:vendor", "engine"), "nashorn")]))
                }
            ],
            SequenceFlows =
            [
                .. process.SequenceFlows,
                new BpmnSequenceFlow("retry", "choice", "work")
                {
                    ConditionExpression = new BpmnConditionExpression(
                        "attempts < 3",
                        "urn:example:other-language",
                        new BpmnExtensions(ForeignAttributes:
                        [
                            new BpmnForeignAttribute(new BpmnQName("http://www.w3.org/2001/XMLSchema-instance", "type"), "bpmn:tFormalExpression")
                        ]))
                }
            ]
        };

        return new BpmnDefinitions(
            Id: "defs",
            TargetNamespace: "https://example.test",
            Processes: [process])
        {
            ExpressionLanguage = "urn:example:language"
        };
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

    private static BpmnWorkBinding[] SampleBindings()
    {
        var process = SampleDefinitions().Processes[0];

        return
        [
            new BpmnWorkBinding.TimerWait("process", "timer", "timer:primary", BpmnBindingSlot.Primary, "PT1M"),
            new BpmnWorkBinding.MessageWait("process", "message", "message:listener", BpmnBindingSlot.ScopeListener, "Started"),
            new BpmnWorkBinding.SignalWait("process", "signal", "signal:primary", BpmnBindingSlot.Primary, "Ready"),
            new BpmnWorkBinding.MessagePublish("process", "publish", "publish:primary", BpmnBindingSlot.Primary, "Completed"),
            new BpmnWorkBinding.SignalPublish("process", "broadcast", "broadcast:primary", BpmnBindingSlot.Primary, "Closed"),
            new BpmnWorkBinding.CallProcess("process", "call", "call:primary", BpmnBindingSlot.Primary, "called-process", WaitForCompletion: true),
            new BpmnWorkBinding.NestedProcess("process", "nested", "nested:primary", BpmnBindingSlot.Primary, process),
            new BpmnWorkBinding.UnboundTask("process", "task", "task:primary", BpmnBindingSlot.Primary, "serviceTask"),
            new BpmnWorkBinding.TimerDateWait("process", "deadline", "deadline:primary", BpmnBindingSlot.Primary, "2026-12-24T18:00:00Z"),
            new BpmnWorkBinding.TimerCycleWait("process", "reminder", "reminder:primary", BpmnBindingSlot.Primary, "R3/PT1H", "PT1H", null, 3, null),
            new BpmnWorkBinding.TimerCycleWait("process", "nightly", "nightly:listener", BpmnBindingSlot.ScopeListener, "0 0 2 * * ?", null, "0 0 2 * * ?", null, null)
        ];
    }

    private static JsonNode SerializeSample(string root, object sample) =>
        root == nameof(BpmnWorkBinding)
            ? JsonSerializer.SerializeToNode((BpmnWorkBinding)sample)!
            : JsonSerializer.SerializeToNode(sample, sample.GetType())!;

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
