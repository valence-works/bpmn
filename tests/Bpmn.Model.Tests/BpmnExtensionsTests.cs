using System.Text.Json;
using Bpmn.Model;
using Shouldly;
using Xunit;

namespace Bpmn.Model.Tests;

/// <summary>
/// The retained-extension model is what makes vendor annotations survive a read-modify-write cycle, so
/// its shape is worth pinning: it must round-trip through JSON, compare by value, and report which
/// namespaces it is carrying.
/// </summary>
public sealed class BpmnExtensionsTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    [Fact]
    public void Empty_is_empty()
    {
        BpmnExtensions.Empty.IsEmpty.ShouldBeTrue();
        BpmnExtensions.Empty.RetainedNamespaces().ShouldBeEmpty();
    }

    [Fact]
    public void Collections_default_to_empty_rather_than_null()
    {
        var extensions = new BpmnExtensions();

        extensions.Documentation.ShouldNotBeNull();
        extensions.ExtensionElements.ShouldNotBeNull();
        extensions.ForeignAttributes.ShouldNotBeNull();
        extensions.ForeignChildren.ShouldNotBeNull();
    }

    [Fact]
    public void Retained_namespaces_are_collected_from_every_position()
    {
        const string camunda = "http://camunda.org/schema/1.0/bpmn";
        const string zeebe = "http://camunda.org/schema/zeebe/1.0";

        var extensions = new BpmnExtensions(
            ExtensionElements:
            [
                new BpmnExtensionElement(
                    new BpmnQName(camunda, "formData"),
                    Children:
                    [
                        new BpmnExtensionElement(new BpmnQName(zeebe, "taskDefinition"))
                    ])
            ],
            ForeignAttributes:
            [
                new BpmnForeignAttribute(new BpmnQName("urn:example:vendor", "flag"), "true")
            ]);

        var namespaces = extensions.RetainedNamespaces();

        namespaces.ShouldBe([camunda, zeebe, "urn:example:vendor"], ignoreOrder: true);
    }

    [Fact]
    public void A_nested_extension_tree_survives_json_round_trip()
    {
        var original = new BpmnExtensions(
            Documentation: [new BpmnDocumentation("Reviewed by finance.", "text/plain")],
            ExtensionElements:
            [
                new BpmnExtensionElement(
                    new BpmnQName("http://camunda.org/schema/1.0/bpmn", "formData"),
                    Attributes: [new BpmnForeignAttribute(new BpmnQName(null, "id"), "approval")],
                    Children:
                    [
                        new BpmnExtensionElement(
                            new BpmnQName("http://camunda.org/schema/1.0/bpmn", "formField"),
                            Value: "amount")
                    ])
            ],
            ForeignChildren:
            [
                new BpmnForeignChild(new BpmnExtensionElement(new BpmnQName("urn:x", "note"), Value: "keep me"), Index: 3)
            ]);

        var json = JsonSerializer.Serialize(original, SerializerOptions);
        var restored = JsonSerializer.Deserialize<BpmnExtensions>(json, SerializerOptions);

        restored.ShouldNotBeNull();
        restored!.ShouldBe(original);
    }

    [Fact]
    public void Foreign_children_remember_their_position()
    {
        // BPMN's tFlowNode is an xsd:sequence, so a retained child written back at the wrong index
        // produces schema-invalid XML that modeling tools reject. The index is the whole point.
        var child = new BpmnForeignChild(new BpmnExtensionElement(new BpmnQName("urn:x", "note")), Index: 2);

        child.Index.ShouldBe(2);
    }

    [Theory]
    [InlineData(null, "task", "task")]
    [InlineData("http://camunda.org/schema/1.0/bpmn", "formData", "{http://camunda.org/schema/1.0/bpmn}formData")]
    public void Qualified_names_render_in_the_xname_convention(string? ns, string local, string expected) =>
        new BpmnQName(ns, local).ToString().ShouldBe(expected);
}
