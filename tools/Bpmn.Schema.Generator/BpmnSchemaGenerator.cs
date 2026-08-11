using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bpmn.Model;
using Bpmn.Model.State;

namespace Bpmn.Schema.Generator;

/// <summary>
/// Generates the JSON Schema for the library-owned payload format by reflecting over the model.
/// <para>
/// It describes what <see cref="JsonSerializer"/> actually writes with default options, not what the
/// C# types look like to a reader. Those differ in ways that matter, and every one of them is a place
/// a hand-written mirror drifts:
/// </para>
/// <list type="bullet">
/// <item>The model declares no <c>JsonStringEnumConverter</c>, so enums are written as <b>integers</b>.</item>
/// <item>Computed get-only properties are serialized too, so they are part of the contract.</item>
/// <item><c>JsonElement</c> is unconstrained, and <c>JsonElement?</c> is unconstrained or null.</item>
/// </list>
/// <para>
/// Generating rather than hand-writing is the point: the schema cannot disagree with the model,
/// because it is derived from it.
/// </para>
/// </summary>
public static class BpmnSchemaGenerator
{
    /// <summary>
    /// The roots a consumer serializes. Everything reachable from them is emitted into <c>$defs</c>.
    /// <para>
    /// There are two, because the format covers two documents: a definition and the execution state of a
    /// running instance. The schema's own <c>$ref</c> points at the first, since a bare
    /// <c>bpmn-payload.schema.json</c> should validate a definition; the second is addressed as
    /// <c>bpmn-payload.schema.json#/$defs/bpmnExecutionState</c>. Both are listed under <c>x-roots</c> so a
    /// code generator can find them without knowing those names.
    /// </para>
    /// </summary>
    private static readonly Type[] Roots =
    [
        typeof(BpmnDefinitions),
        typeof(BpmnExecutionState)
    ];

    /// <summary>Generates the schema document, formatted for a reviewable diff.</summary>
    public static string Generate() => Generate(out _);

    /// <summary>
    /// Generates the schema and reports the type closure it covers - exactly the types that land in
    /// <c>$defs</c>.
    /// <para>
    /// The closure is reported rather than recomputed by callers so that "what the format covers" has a
    /// single definition. A test asserting a property of the model over a second, hand-maintained list
    /// would drift from the schema the moment someone added a type, which is the failure this whole
    /// generator exists to prevent.
    /// </para>
    /// </summary>
    public static string Generate(out IReadOnlyList<Type> covered)
    {
        var defs = new JsonObject();
        var pending = new Queue<Type>(Roots);
        var seen = new HashSet<Type>();
        var closure = new List<Type>();

        covered = closure;

        while (pending.Count > 0)
        {
            var type = pending.Dequeue();

            if (!seen.Add(type))
                continue;

            closure.Add(type);
            defs[DefName(type)] = type.IsEnum ? DescribeEnum(type) : DescribeObject(type, pending);
        }

        // $defs is emitted in a stable order so a regenerated schema diffs cleanly against the last one.
        var orderedDefs = new JsonObject();

        foreach (var entry in defs.OrderBy(x => x.Key, StringComparer.Ordinal))
            orderedDefs[entry.Key] = entry.Value?.DeepClone();

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = BpmnPayloadFormat.SchemaId,
            ["title"] = "BPMN payload format",
            ["description"] =
                $"Version {BpmnPayloadFormat.Version} of the payload format owned by Bpmn.Model. "
                + "Generated from the model; do not edit by hand. See ADR 0005.",
            ["x-payloadFormatVersion"] = BpmnPayloadFormat.Version,
            ["x-roots"] = RootPointers(),
            ["$ref"] = Ref(typeof(BpmnDefinitions)),
            ["$defs"] = orderedDefs
        };

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static JsonObject DescribeObject(Type type, Queue<Type> pending)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in SerializedProperties(type))
        {
            var name = JsonNameOf(property);
            properties[name] = Describe(property.PropertyType, IsNullable(property), pending);

            // A property is required when the serializer always writes it and it can never be null.
            // Collections are never null in this model (an absent list is an empty list), so they are
            // required too, which is a real guarantee worth publishing rather than hiding.
            if (!IsNullable(property))
                required.Add(name);
        }

        var described = new JsonObject
        {
            ["type"] = "object",
            ["title"] = type.Name,
            ["properties"] = properties
        };

        if (required.Count > 0)
            described["required"] = required;

        // The model is a closed set of records. Refusing unknown properties turns a typo in a
        // hand-written document into a validation error rather than a silently ignored field.
        described["additionalProperties"] = false;
        return described;
    }

    /// <summary>
    /// Enums carry no string converter anywhere in the model, so they are written as integers. The
    /// names are published alongside as <c>x-enumNames</c> so a code generator can still emit a
    /// readable type without inventing the mapping.
    /// </summary>
    private static JsonObject DescribeEnum(Type type)
    {
        var values = new JsonArray();
        var names = new JsonArray();

        foreach (var value in Enum.GetValues(type))
        {
            values.Add(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
            names.Add(Enum.GetName(type, value));
        }

        return new JsonObject
        {
            ["type"] = "integer",
            ["title"] = type.Name,
            ["description"] = "Serialized as an integer: the model declares no JsonStringEnumConverter.",
            ["enum"] = values,
            ["x-enumNames"] = names
        };
    }

    private static JsonNode Describe(Type type, bool nullable, Queue<Type> pending)
    {
        var underlying = Nullable.GetUnderlyingType(type);

        if (underlying is not null)
            return Describe(underlying, nullable: true, pending);

        // JsonElement is whatever the host put there: any JSON value, including null. Deliberately
        // unconstrained, so nullability makes no difference to what it accepts.
        if (type == typeof(JsonElement))
            return new JsonObject();

        if (type == typeof(string))
            return Primitive("string", nullable);

        if (type == typeof(bool))
            return Primitive("boolean", nullable);

        if (type == typeof(int) || type == typeof(long))
            return Primitive("integer", nullable);

        if (type == typeof(double) || type == typeof(decimal) || type == typeof(float))
            return Primitive("number", nullable);

        if (type.IsEnum)
        {
            pending.Enqueue(type);
            return RefNode(type, nullable);
        }

        if (TryGetDictionaryValueType(type, out var valueType))
        {
            return new JsonObject
            {
                ["type"] = nullable ? new JsonArray("object", "null") : "object",
                ["additionalProperties"] = Describe(valueType, nullable: false, pending)
            };
        }

        if (TryGetEnumerableItemType(type, out var itemType))
        {
            return new JsonObject
            {
                ["type"] = nullable ? new JsonArray("array", "null") : "array",
                ["items"] = Describe(itemType, nullable: false, pending)
            };
        }

        pending.Enqueue(type);
        return RefNode(type, nullable);
    }

    private static JsonObject Primitive(string jsonType, bool nullable) =>
        new() { ["type"] = nullable ? new JsonArray(jsonType, "null") : jsonType };

    /// <summary>
    /// A nullable reference to a described type. JSON Schema cannot express "null or this $ref" with a
    /// bare $ref, so a nullable one becomes an anyOf.
    /// </summary>
    private static JsonNode RefNode(Type type, bool nullable) =>
        nullable
            ? new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    new JsonObject { ["$ref"] = Ref(type) },
                    new JsonObject { ["type"] = "null" })
            }
            : new JsonObject { ["$ref"] = Ref(type) };

    /// <summary>
    /// Every public instance property the serializer writes, in declaration order.
    /// <para>
    /// Get-only computed properties are included deliberately. System.Text.Json serializes them, so
    /// they are on the wire and therefore part of the contract, whether or not they were intended to be.
    /// </para>
    /// </summary>
    public static IEnumerable<PropertyInfo> SerializedProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is not null && p.GetIndexParameters().Length == 0)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    /// <summary>
    /// The name on the wire.
    /// <para>
    /// With default <see cref="JsonSerializerOptions"/> there is no naming policy, so a property with no
    /// <see cref="JsonPropertyNameAttribute"/> is written under its CLR name verbatim, PascalCase and
    /// all. Assuming camelCase here would produce a schema that quietly disagrees with the serializer,
    /// which is the exact failure this schema exists to prevent.
    /// </para>
    /// </summary>
    private static string JsonNameOf(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;

    private static readonly NullabilityInfoContext Nullability = new();

    private static bool IsNullable(PropertyInfo property)
    {
        if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
            return true;

        if (property.PropertyType.IsValueType)
            return false;

        return Nullability.Create(property).ReadState is not NullabilityState.NotNull;
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        foreach (var candidate in Interfaces(type))
        {
            if (!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();

            if (definition != typeof(IReadOnlyDictionary<,>) && definition != typeof(IDictionary<,>))
                continue;

            valueType = candidate.GetGenericArguments()[1];
            return true;
        }

        valueType = null!;
        return false;
    }

    private static bool TryGetEnumerableItemType(Type type, out Type itemType)
    {
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            foreach (var candidate in Interfaces(type))
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    itemType = candidate.GetGenericArguments()[0];
                    return true;
                }
            }
        }

        itemType = null!;
        return false;
    }

    private static IEnumerable<Type> Interfaces(Type type) =>
        type.IsInterface ? new[] { type }.Concat(type.GetInterfaces()) : type.GetInterfaces();

    /// <summary>
    /// The addressable roots, keyed by type name. A consumer generating types needs to know which
    /// <c>$defs</c> entries are whole documents rather than fragments, and the schema's single
    /// <c>$ref</c> can only say that about one of them.
    /// </summary>
    private static JsonObject RootPointers()
    {
        var roots = new JsonObject();

        foreach (var type in Roots)
            roots[type.Name] = Ref(type);

        return roots;
    }

    private static string Ref(Type type) => "#/$defs/" + DefName(type);

    private static string DefName(Type type) => JsonNamingPolicy.CamelCase.ConvertName(type.Name);
}
