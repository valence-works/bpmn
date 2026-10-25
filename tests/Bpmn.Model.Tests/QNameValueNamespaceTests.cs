using System.Text.Json;
using System.Text.Json.Nodes;
using Bpmn.Model;
using Bpmn.Schema.Generator;
using Shouldly;
using Xunit;

namespace Bpmn.Model.Tests;

public sealed class QNameValueNamespaceTests
{
    [Fact]
    public void Legacy_foreign_attribute_json_and_two_value_deconstruction_remain_unchanged()
    {
        const string json = "{\"name\":{\"ns\":\"urn:vendor\",\"localName\":\"type\"},\"value\":\"v:Thing\"}";

        var attribute = JsonSerializer.Deserialize<BpmnForeignAttribute>(json)!;
        var (name, value) = attribute;

        name.ShouldBe(new BpmnQName("urn:vendor", "type"));
        value.ShouldBe("v:Thing");
        attribute.ValueNamespaces.ShouldBeNull();
        JsonSerializer.Serialize(attribute).ShouldBe(json);
    }

    [Fact]
    public void Value_namespace_provenance_round_trips_and_compares_by_value()
    {
        var original = new BpmnForeignAttribute(new BpmnQName("urn:vendor", "types"), "a:Thing b:Other")
        {
            ValueNamespaces =
            [
                new BpmnNamespaceBinding("a", "urn:alpha"),
                new BpmnNamespaceBinding("b", null)
            ]
        };

        var json = JsonSerializer.Serialize(original);
        var copy = JsonSerializer.Deserialize<BpmnForeignAttribute>(json)!;

        copy.ShouldBe(original);
        copy.GetHashCode().ShouldBe(original.GetHashCode());
        var bindings = copy.ValueNamespaces!;
        bindings.Select(binding => binding.Prefix).ShouldBe(["a", "b"]);
        bindings[0].Namespace.ShouldBe("urn:alpha");
        bindings[1].Namespace.ShouldBeNull();
    }

    [Fact]
    public void Retained_namespaces_include_resolved_value_bindings_only()
    {
        var extensions = new BpmnExtensions(ForeignAttributes:
        [
            new BpmnForeignAttribute(new BpmnQName("urn:attribute", "types"), "a:Thing b:Other")
            {
                ValueNamespaces =
                [
                    new BpmnNamespaceBinding("a", "urn:alpha"),
                    new BpmnNamespaceBinding("b", null)
                ]
            }
        ]);

        extensions.RetainedNamespaces().ShouldBe(["urn:alpha", "urn:attribute"]);
    }

    [Fact]
    public void Payload_schema_covers_value_namespace_provenance()
    {
        var json = JsonNode.Parse(BpmnSchemaGenerator.Generate(out var covered))!.AsObject();
        var definitions = json["$defs"]!.AsObject();
        var attribute = definitions["bpmnForeignAttribute"]!["properties"]!.AsObject();

        covered.ShouldContain(typeof(BpmnNamespaceBinding));
        attribute["valueNamespaces"]!["items"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/bpmnNamespaceBinding");
        definitions["bpmnExtensions"]!["properties"]!.AsObject().ShouldNotContainKey("isEmpty");
        definitions["bpmnNamespaceBinding"]!["properties"]!["ns"]!["type"]!.AsArray()
            .Select(option => option!.GetValue<string>())
            .ShouldBe(["string", "null"]);
    }
}
